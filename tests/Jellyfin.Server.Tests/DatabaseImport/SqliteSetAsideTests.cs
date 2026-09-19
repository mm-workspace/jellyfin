using System;
using System.IO;
using System.Threading.Tasks;
using Emby.Server.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Server.Configuration;
using Jellyfin.Server.DatabaseImport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Server.Tests.DatabaseImport;

/// <summary>
/// Sets the SQLite files aside once an import is committed, which finalize does without PostgreSQL.
/// </summary>
public sealed class SqliteSetAsideTests : IDisposable
{
    private static readonly Version _serverVersion = new(10, 12, 0, 0);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "jf-import-set-aside-" + Guid.NewGuid().ToString("N"));
    private readonly ServerApplicationPaths _paths;

    public SqliteSetAsideTests()
    {
        _paths = new ServerApplicationPaths(
            Path.Combine(_root, "data"),
            Path.Combine(_root, "log"),
            Path.Combine(_root, "config"),
            Path.Combine(_root, "cache"),
            Path.Combine(_root, "web"));
        Directory.CreateDirectory(_paths.DataPath);
    }

    private string SqlitePath => Path.Combine(_paths.DataPath, "jellyfin.db");

    private string ImportedPath => SqlitePath + DatabaseImportGuard.ImportedSuffix;

    [Fact]
    public async Task Finalize_AfterAnEarlierImport_KeepsItsCopyAndPicksAnotherName()
    {
        // The database was imported before, copied back to go on with SQLite, and is imported again.
        await WriteAsync(ImportedPath, "earlier import");
        await WriteCommittedImportAsync();

        Assert.Equal(ImportExitCode.Success, await FinalizeAsync());

        Assert.Equal("earlier import", await File.ReadAllTextAsync(ImportedPath, TestContext.Current.CancellationToken));
        Assert.Equal("database", await File.ReadAllTextAsync(ImportedPath + ".2", TestContext.Current.CancellationToken));
        Assert.Equal("log", await File.ReadAllTextAsync(ImportedPath + ".2-wal", TestContext.Current.CancellationToken));
        Assert.False(File.Exists(SqlitePath));
        Assert.Null(await ImportState.ReadAsync(_paths.DataPath, TestContext.Current.CancellationToken));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseImportGuard.EnsureNoImportInProgressAsync(
            _paths.DataPath,
            new DatabaseConfigurationOptions { DatabaseType = PostgreSqlImportCommand.SqliteDatabaseType },
            TestContext.Current.CancellationToken));
        Assert.Contains($"set aside as '{ImportedPath}', '{ImportedPath}.2'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Finalize_StoppedWhileRenaming_MovesTheOtherFilesNextToTheFirstOnTheNextRun()
    {
        await WriteAsync(ImportedPath, "earlier import");
        await WriteCommittedImportAsync();

        // A directory in the way stops the log from moving once the database file has moved.
        Directory.CreateDirectory(ImportedPath + ".2-wal");
        Assert.Equal(ImportExitCode.Error, await FinalizeAsync());
        Assert.Equal("database", await File.ReadAllTextAsync(ImportedPath + ".2", TestContext.Current.CancellationToken));
        Assert.Equal(ImportStage.Committed, (await ImportState.ReadAsync(_paths.DataPath, TestContext.Current.CancellationToken))!.Step);

        Directory.Delete(ImportedPath + ".2-wal");
        Assert.Equal(ImportExitCode.Success, await FinalizeAsync());

        Assert.Equal("log", await File.ReadAllTextAsync(ImportedPath + ".2-wal", TestContext.Current.CancellationToken));
        Assert.False(File.Exists(SqlitePath + "-wal"));
        Assert.Equal("earlier import", await File.ReadAllTextAsync(ImportedPath, TestContext.Current.CancellationToken));
        Assert.Null(await ImportState.ReadAsync(_paths.DataPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Finalize_DatabasePutBackAfterItMoved_MovesItToTheNextFreeName()
    {
        // An earlier run moved the database file to the name it kept and stopped; then a copy of it was put back.
        await WriteAsync(ImportedPath, "moved");
        await WriteCommittedImportAsync(ImportedPath);

        Assert.Equal(ImportExitCode.Success, await FinalizeAsync());

        Assert.Equal("moved", await File.ReadAllTextAsync(ImportedPath, TestContext.Current.CancellationToken));
        Assert.Equal("database", await File.ReadAllTextAsync(ImportedPath + ".2", TestContext.Current.CancellationToken));
        Assert.Equal("log", await File.ReadAllTextAsync(ImportedPath + ".2-wal", TestContext.Current.CancellationToken));
        Assert.False(File.Exists(SqlitePath));
        Assert.False(File.Exists(SqlitePath + "-wal"));
        Assert.Null(await ImportState.ReadAsync(_paths.DataPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Finalize_FileInTheWayOfTheLog_MovesTheLogToTheNextFreeName()
    {
        // An earlier run moved the database file to the name it kept and stopped; then another log was put under that name.
        await WriteCommittedImportAsync(ImportedPath);
        File.Move(SqlitePath, ImportedPath);
        await WriteAsync(ImportedPath + "-wal", "other log");

        Assert.Equal(ImportExitCode.Success, await FinalizeAsync());

        Assert.Equal("database", await File.ReadAllTextAsync(ImportedPath, TestContext.Current.CancellationToken));
        Assert.Equal("other log", await File.ReadAllTextAsync(ImportedPath + "-wal", TestContext.Current.CancellationToken));
        Assert.Equal("log", await File.ReadAllTextAsync(ImportedPath + ".2-wal", TestContext.Current.CancellationToken));
        Assert.False(File.Exists(SqlitePath + "-wal"));
        Assert.Null(await ImportState.ReadAsync(_paths.DataPath, TestContext.Current.CancellationToken));
    }

    public void Dispose()
    {
        Directory.Delete(_root, true);
    }

    private static Task WriteAsync(string path, string content) => File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);

    private async Task WriteCommittedImportAsync(string? importedPath = null)
    {
        await WriteAsync(SqlitePath, "database");
        await WriteAsync(SqlitePath + "-wal", "log");
        await new ImportState(ImportState.CurrentFormatVersion, ImportStage.Committed, Path.Combine(_paths.DataPath, "postgresql-import"), SqlitePath, "abc", _serverVersion.ToString(), 1, DateTime.UtcNow, importedPath)
            .WriteAsync(_paths.DataPath, TestContext.Current.CancellationToken);
    }

    private Task<int> FinalizeAsync()
        => new PostgreSqlImportCommand(_paths, new ConfigurationBuilder().Build(), NullLoggerFactory.Instance, _serverVersion, TimeProvider.System)
            .RunAsync(StartupMode.PostgreSqlImportFinalize, null, TestContext.Current.CancellationToken);
}
