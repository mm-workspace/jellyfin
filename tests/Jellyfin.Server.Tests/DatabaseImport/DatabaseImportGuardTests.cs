using System;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Server.Configuration;
using Jellyfin.Server.DatabaseImport;
using Xunit;

namespace Jellyfin.Server.Tests.DatabaseImport;

public sealed class DatabaseImportGuardTests : IDisposable
{
    private static readonly DatabaseConfigurationOptions _sqlite = new() { DatabaseType = "Jellyfin-SQLite" };
    private static readonly DatabaseConfigurationOptions _postgreSql = new() { DatabaseType = "Jellyfin-PostgreSQL" };

    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "jf-import-guard-" + Guid.NewGuid().ToString("N"));

    public DatabaseImportGuardTests()
    {
        Directory.CreateDirectory(_dataPath);
    }

    [Fact]
    public async Task NoImport_StartsOnEitherDatabase()
    {
        await File.WriteAllTextAsync(Path.Combine(_dataPath, "jellyfin.db"), string.Empty, TestContext.Current.CancellationToken);

        await DatabaseImportGuard.EnsureNoImportInProgressAsync(_dataPath, _sqlite, TestContext.Current.CancellationToken);
        await DatabaseImportGuard.EnsureNoImportInProgressAsync(_dataPath, _postgreSql, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(nameof(ImportStage.Preflighted), "PostgreSqlImportSeed")]
    [InlineData(nameof(ImportStage.Seeded), "PostgreSqlImportFinalize")]
    [InlineData(nameof(ImportStage.Committed), "PostgreSqlImportFinalize")]
    public async Task ImportInProgress_RefusesEveryStartWithTheNextStep(string stage, string nextStep)
    {
        await new ImportState(ImportState.CurrentFormatVersion, Enum.Parse<ImportStage>(stage), "/import", Path.Combine(_dataPath, "jellyfin.db"), "abc", "10.12.0.0", null, DateTime.UtcNow)
            .WriteAsync(_dataPath, TestContext.Current.CancellationToken);

        foreach (var configuration in new[] { _sqlite, _postgreSql })
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseImportGuard.EnsureNoImportInProgressAsync(_dataPath, configuration, TestContext.Current.CancellationToken));
            Assert.Contains(nextStep, ex.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(DatabaseImportGuard.ImportedSuffix)]
    [InlineData(DatabaseImportGuard.ImportedSuffix + ".2")]
    public async Task ImportedSqliteDatabase_RefusesSqliteButNotPostgreSql(string suffix)
    {
        var importedPath = Path.Combine(_dataPath, "jellyfin.db" + suffix);
        await File.WriteAllTextAsync(importedPath, string.Empty, TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseImportGuard.EnsureNoImportInProgressAsync(_dataPath, _sqlite, TestContext.Current.CancellationToken));
        Assert.Contains($"imported into PostgreSQL and set aside as '{importedPath}'", ex.Message, StringComparison.Ordinal);
        await DatabaseImportGuard.EnsureNoImportInProgressAsync(_dataPath, _postgreSql, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".2", "")]
    [InlineData(".2", "-wal")]
    [InlineData(".3", "", ".2-shm")]
    public async Task GetFreeImportedPath_SkipsTheNamesEarlierImportsUse(string expected, params string[] existing)
    {
        var sqlitePath = Path.Combine(_dataPath, "jellyfin.db");
        foreach (var name in existing)
        {
            await File.WriteAllTextAsync(sqlitePath + DatabaseImportGuard.ImportedSuffix + name, string.Empty, TestContext.Current.CancellationToken);
        }

        Assert.Equal(sqlitePath + DatabaseImportGuard.ImportedSuffix + expected, DatabaseImportGuard.GetFreeImportedPath(sqlitePath));
    }

    [Theory]
    [InlineData(StartupMode.PostgreSqlImportPreflight, true)]
    [InlineData(StartupMode.PostgreSqlImportSeed, true)]
    [InlineData(StartupMode.PostgreSqlImportFinalize, true)]
    [InlineData(StartupMode.PostgreSqlImportAbort, true)]
    [InlineData(StartupMode.MediaServer, false)]
    [InlineData(StartupMode.MigrateSystem, false)]
    [InlineData(StartupMode.SeedSystem, false)]
    [InlineData(null, false)]
    public void IsImportMode_OnlyTheImportSteps(StartupMode? mode, bool expected)
    {
        Assert.Equal(expected, PostgreSqlImportCommand.IsImportMode(mode));
    }

    [Fact]
    public void StartupOptions_ParseTheImportDirectory()
    {
        var result = CommandLine.Parser.Default.ParseArguments<StartupOptions>(["--mode", "PostgreSqlImportSeed", "--pg-import-dir", "/import"]);

        var options = Assert.IsType<CommandLine.Parsed<StartupOptions>>(result).Value;
        Assert.Equal((StartupMode.PostgreSqlImportSeed, "/import"), (options.StartupMode, options.PostgreSqlImportDirectory));
    }

    public void Dispose()
    {
        Directory.Delete(_dataPath, true);
    }
}
