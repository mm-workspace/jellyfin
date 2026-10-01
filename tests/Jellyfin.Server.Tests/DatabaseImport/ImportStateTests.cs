using System;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Server.DatabaseImport;
using Xunit;

namespace Jellyfin.Server.Tests.DatabaseImport;

public sealed class ImportStateTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "jf-import-state-" + Guid.NewGuid().ToString("N"));

    public ImportStateTests()
    {
        Directory.CreateDirectory(_dataPath);
    }

    [Fact]
    public async Task Write_ThenRead_ReturnsTheState()
    {
        var state = new ImportState(ImportState.CurrentFormatVersion, ImportStage.Seeded, "/import", "/data/jellyfin.db", "abc", "10.12.0.0", 42, new DateTime(2026, 9, 17, 0, 0, 0, DateTimeKind.Utc));

        await state.WriteAsync(_dataPath, TestContext.Current.CancellationToken);

        Assert.Equal(state, await ImportState.ReadAsync(_dataPath, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(ImportState.GetPath(_dataPath) + ".tmp"));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(ImportState.GetPath(_dataPath)));
        }
    }

    [Fact]
    public async Task Read_NoFile_ReturnsNull()
    {
        Assert.Null(await ImportState.ReadAsync(_dataPath, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{\"formatVersion\":2,\"step\":\"Seeded\",\"importDirectory\":\"/i\",\"sqliteDatabasePath\":\"/d\",\"snapshotSha256\":\"a\",\"serverVersion\":\"1\",\"databaseOid\":null,\"updatedUtc\":\"2026-09-17T00:00:00Z\"}")]
    [InlineData("{\"formatVersion\":1,\"step\":\"Seeded\"}")]
    public async Task Read_DamagedFile_IsRefusedNotIgnored(string content)
    {
        await File.WriteAllTextAsync(ImportState.GetPath(_dataPath), content, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() => ImportState.ReadAsync(_dataPath, TestContext.Current.CancellationToken));
    }

    public void Dispose()
    {
        Directory.Delete(_dataPath, true);
    }
}
