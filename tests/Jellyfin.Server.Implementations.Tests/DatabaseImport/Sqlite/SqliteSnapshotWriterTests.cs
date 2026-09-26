using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Jellyfin.Server.Implementations.DatabaseImport.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.DatabaseImport.Sqlite;

public class SqliteSnapshotWriterTests : IClassFixture<SqliteSourceFixture>
{
    private readonly SqliteSourceFixture _fixture;

    public SqliteSnapshotWriterTests(SqliteSourceFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Write_IncludesChangesOnlyInTheWriteAheadLog()
    {
        var path = _fixture.Copy(_fixture.Small);
        await using (var writer = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await writer.OpenAsync(TestContext.Current.CancellationToken);
            Execute(writer, "PRAGMA wal_autocheckpoint = 0; INSERT INTO ActivityLogs (Name, Type, UserId, DateCreated, LogSeverity, RowVersion) VALUES ('only in the log', 'Test', '00000000-0000-0000-0000-000000000000', '2024-01-01 00:00:00', 2, 0)");
            Assert.True(new FileInfo(path + "-wal").Length > 0);

            // The writer connection stays open, as it would for a server that was killed.
            var snapshot = await SqliteSnapshotWriter.WriteAsync(path, path + ".snapshot", TestContext.Current.CancellationToken);

            Assert.True(snapshot.SourceFiles.Single(f => f.Name == "jellyfin.db-wal").Length > 0);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(snapshot.Path, TestContext.Current.CancellationToken))), snapshot.Sha256);
        }

        await using var connection = new SqliteConnection($"Data Source={path}.snapshot;Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1L, Scalar(connection, "SELECT count(*) FROM ActivityLogs WHERE Name = 'only in the log'"));
        Assert.Equal("delete", Scalar(connection, "PRAGMA journal_mode"));
        Assert.False(File.Exists(path + ".snapshot-wal"));
    }

    [Fact]
    public async Task Write_SnapshotExists_ThrowsAndKeepsIt()
    {
        var path = _fixture.Copy(_fixture.Small);
        await File.WriteAllTextAsync(path + ".snapshot", "existing", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<IOException>(() => SqliteSnapshotWriter.WriteAsync(path, path + ".snapshot", TestContext.Current.CancellationToken));

        Assert.Equal("existing", await File.ReadAllTextAsync(path + ".snapshot", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Write_SourceMissing_ThrowsWithoutCreatingFiles()
    {
        var path = Path.Combine(Path.GetDirectoryName(_fixture.Copy(_fixture.Small))!, "missing.db");

        await Assert.ThrowsAsync<FileNotFoundException>(() => SqliteSnapshotWriter.WriteAsync(path, path + ".snapshot", TestContext.Current.CancellationToken));

        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".snapshot"));
    }

    [Fact]
    public async Task Write_DamagedSource_ThrowsAndRemovesTheCopy()
    {
        var path = _fixture.Copy(_fixture.Small);
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            // Overwrite pages in the middle of the file, keeping the header readable.
            stream.Position = stream.Length / 2;
            await stream.WriteAsync(Enumerable.Repeat((byte)0xAB, 64 * 1024).ToArray(), TestContext.Current.CancellationToken);
        }

        await Assert.ThrowsAnyAsync<Exception>(() => SqliteSnapshotWriter.WriteAsync(path, path + ".snapshot", TestContext.Current.CancellationToken));

        Assert.False(File.Exists(path + ".snapshot"));
    }

    [Fact]
    public void ReadSourceFiles_ListsMissingFilesWithoutSize()
    {
        var path = _fixture.Copy(_fixture.Small);

        var files = SqliteSnapshotWriter.ReadSourceFiles(path);

        Assert.Equal(["jellyfin.db", "jellyfin.db-wal", "jellyfin.db-shm"], files.Select(f => f.Name));
        Assert.NotNull(files[0].Length);
        Assert.All(files.Skip(1), f => Assert.Equal((null, null), (f.Length, f.LastWriteTimeUtc)));
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
#pragma warning disable CA2100 // The statements are constants of this class.
        command.CommandText = sql;
#pragma warning restore CA2100
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
#pragma warning disable CA2100 // The statements are constants of this class.
        command.CommandText = sql;
#pragma warning restore CA2100
        return command.ExecuteScalar();
    }
}
