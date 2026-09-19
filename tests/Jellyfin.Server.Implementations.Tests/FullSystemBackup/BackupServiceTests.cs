using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Sqlite;
using Jellyfin.Database.Testing;
using Jellyfin.Server.Implementations.FullSystemBackup;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.SystemBackupService;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using BaseItemKind = Jellyfin.Data.Enums.BaseItemKind;

namespace Jellyfin.Server.Implementations.Tests.FullSystemBackup;

/// <summary>
/// Tests for <see cref="BackupService"/>, in particular that a single row of corrupt
/// <see cref="KeyframeData"/> (e.g. malformed <c>KeyframeTicks</c> JSON) does not abort
/// an otherwise healthy backup. See https://github.com/jellyfin/jellyfin/issues/17216.
/// </summary>
public sealed class BackupServiceTests : IDisposable
{
    // The ids alternate between the two display preferences, so a section restored to the wrong one shows.
    private static readonly (int PreferencesId, string Client, int Id, int Order, HomeSectionType Type)[] _seededHomeSections =
    [
        (31, "web", 71, 0, HomeSectionType.LatestMedia),
        (32, "tv", 73, 0, HomeSectionType.NextUp),
        (31, "web", 75, 1, HomeSectionType.Resume),
    ];

    private readonly ITestDatabase _database;
    private readonly string _testRoot;
    private readonly string _backupPath;
    private readonly string _configurationDirectoryPath;

    public BackupServiceTests()
    {
        _database = TestDatabase.Create();

        // Use the test assembly's own output directory instead of Path.GetTempPath(). On GitHub-hosted
        // windows-latest runners, the system temp directory lives on the constrained C: drive, which can have
        // less than the 5GiB BackupService requires free, causing spurious failures. AppContext.BaseDirectory
        // is under the repo checkout (the much larger D: drive on Windows runners) on all platforms.
        _testRoot = Path.Combine(AppContext.BaseDirectory, "jellyfin-backup-service-tests-" + Guid.NewGuid().ToString("N"));
        _backupPath = Path.Combine(_testRoot, "Backup");
        _configurationDirectoryPath = Path.Combine(_testRoot, "Config");
        Directory.CreateDirectory(_backupPath);
        Directory.CreateDirectory(_configurationDirectoryPath);
    }

    public void Dispose()
    {
        _database.Dispose();

        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, true);
        }
    }

    [Fact]
    [Trait("Provider", "Sqlite")]
    public async Task CreateBackupAsync_WithCorruptKeyframeDataRow_SkipsRowAndCompletesBackup()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var validItemId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var corruptItemId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        await using (var ctx = CreateDbContext())
        {
            // A healthy item + keyframe row, written the normal way.
            ctx.BaseItems.Add(CreateMovieEntity(validItemId, "Good Movie"));
            ctx.BaseItems.Add(CreateMovieEntity(corruptItemId, "Corrupt Movie"));
            await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(true);

            ctx.KeyframeData.Add(new KeyframeData
            {
                ItemId = validItemId,
                TotalDuration = 60_000,
                KeyframeTicks = [0, 1000, 2000]
            });
            await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(true);

            // Simulate a corrupted database row: truncated JSON array for KeyframeTicks,
            // written directly via SQL to bypass EF's normal (well-formed) write path.
            await ctx.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO KeyframeData (ItemId, TotalDuration, KeyframeTicks) VALUES ({corruptItemId.ToString()}, {5000L}, {"[1,2,3"})",
                cancellationToken).ConfigureAwait(true);
        }

        var backupService = CreateBackupService();

        var manifest = await backupService.CreateBackupAsync(new BackupOptionsDto()).ConfigureAwait(true);

        Assert.True(File.Exists(manifest.Path));

        await using var archive = await ZipFile.OpenReadAsync(manifest.Path, cancellationToken).ConfigureAwait(true);
        await using (var manifestStream = await archive.GetEntry("manifest.json")!.OpenAsync(cancellationToken))
        {
            using var manifestDocument = await JsonDocument.ParseAsync(manifestStream, cancellationToken: cancellationToken);
            var declaredTables = manifestDocument.RootElement.GetProperty("DatabaseTables").EnumerateArray().Select(e => e.GetString()).Order().ToArray();
            var archivedTables = archive.Entries.Where(e => e.FullName.StartsWith("Database/", StringComparison.Ordinal)).Select(e => Path.GetFileNameWithoutExtension(e.Name)).Order().ToArray();
            Assert.Equal(archivedTables, declaredTables);
        }

        var keyframeEntry = archive.GetEntry("Database/KeyframeData.json");
        Assert.NotNull(keyframeEntry);

        await using var entryStream = await keyframeEntry!.OpenAsync(cancellationToken).ConfigureAwait(true);
        using var document = await JsonDocument.ParseAsync(entryStream, cancellationToken: cancellationToken).ConfigureAwait(true);

        var rows = document.RootElement.EnumerateArray().ToList();

        // The corrupt row must be skipped, but the valid row must still make it into the backup.
        var singleRow = Assert.Single(rows);
        Assert.Equal(validItemId, singleRow.GetProperty("ItemId").GetGuid());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("duplicate")]
    [InlineData("constraint")]
    public async Task RestoreBackupAsync_InvalidDatabase_PreservesExistingDataAndHistory(string failure)
    {
        var archivePath = await CreateRestoreArchiveAsync();
        await using (var archive = await ZipFile.OpenAsync(archivePath, ZipArchiveMode.Update, TestContext.Current.CancellationToken))
        {
            var entry = archive.GetEntry("Database/BaseItems.json")!;
            JsonArray? items = null;
            if (failure is "duplicate" or "constraint")
            {
                await using var stream = await entry.OpenAsync(TestContext.Current.CancellationToken);
                items = (await JsonNode.ParseAsync(stream, cancellationToken: TestContext.Current.CancellationToken))!.AsArray();
            }

            entry.Delete();
            if (failure != "missing")
            {
                await using var writer = new StreamWriter(await archive.CreateEntry("Database/BaseItems.json").OpenAsync(TestContext.Current.CancellationToken));
                if (failure == "malformed")
                {
                    await writer.WriteAsync("[{".AsMemory(), TestContext.Current.CancellationToken);
                }
                else
                {
                    if (failure == "duplicate")
                    {
                        items!.Add(items[0]!.DeepClone());
                    }
                    else
                    {
                        items![0]!["ParentId"] = Guid.NewGuid();
                    }

                    await writer.WriteAsync(items.ToJsonString().AsMemory(), TestContext.Current.CancellationToken);
                }
            }
        }

        var exception = await Record.ExceptionAsync(() => CreateBackupService().RestoreBackupAsync(archivePath));

        if (failure == "constraint")
        {
            AssertForeignKeyViolation(exception, "BaseItems");
        }
        else
        {
            Assert.IsType(failure == "malformed" ? typeof(JsonException) : typeof(InvalidOperationException), exception);
        }

        await AssertExistingDatabaseAsync();
        if (failure != "constraint")
        {
            Assert.Equal("existing config", await File.ReadAllTextAsync(Path.Combine(_configurationDirectoryPath, "system.xml"), TestContext.Current.CancellationToken));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RestoreBackupAsync_ValidDatabase_ReplacesRowsAndHistoryAndKeepsForeignKeysEnabled(bool legacyManifest, bool olderTables)
    {
        var archivePath = await CreateRestoreArchiveAsync();

        if (legacyManifest)
        {
            await UseLegacyManifestAsync(archivePath, olderTables);
        }

        await CreateBackupService().RestoreBackupAsync(archivePath);

        using var context = CreateDbContext();
        Assert.Equal(new[] { "Archived Child", "Archived Movie" }, context.BaseItems.Where(e => e.Type != "PLACEHOLDER").OrderBy(e => e.Name).Select(e => e.Name).ToArray());
        var link = Assert.Single(context.LinkedChildren);
        Assert.Equal("Archived Movie", context.BaseItems.Single(e => e.Id.Equals(link.ParentId)).Name);
        Assert.Equal("Archived Child", context.BaseItems.Single(e => e.Id.Equals(link.ChildId)).Name);
        Assert.Equal("backup", Assert.Single(await context.GetService<IHistoryRepository>().GetAppliedMigrationsAsync(TestContext.Current.CancellationToken)).MigrationId);
        Assert.Equal("archived config", await File.ReadAllTextAsync(Path.Combine(_configurationDirectoryPath, "system.xml"), TestContext.Current.CancellationToken));
        await AssertForeignKeysEnabledAsync(context);
    }

    [Fact]
    public async Task RestoreBackupAsync_LegacyManifestMissingTable_RejectsBeforeReplacingData()
    {
        var archivePath = await CreateRestoreArchiveAsync();
        await UseLegacyManifestAsync(archivePath, false);
        await using (var archive = await ZipFile.OpenAsync(archivePath, ZipArchiveMode.Update, TestContext.Current.CancellationToken))
        {
            archive.GetEntry("Database/BaseItems.json")!.Delete();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateBackupService().RestoreBackupAsync(archivePath));

        await AssertExistingDatabaseAsync();
        Assert.Equal("existing config", await File.ReadAllTextAsync(Path.Combine(_configurationDirectoryPath, "system.xml"), TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("Provider", "Sqlite")]
    public async Task PurgeDatabase_QuotedTableName_RemovesRows()
    {
        await using var context = CreateDbContext();
        await context.Database.ExecuteSqlRawAsync(
            """"
            CREATE TABLE "Restore ""items""" (Id INTEGER);
            INSERT INTO "Restore ""items""" VALUES (1);
            """",
            TestContext.Current.CancellationToken);

        var provider = new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance);
        await provider.PurgeDatabase(context, ["Restore \"items\""]);

        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM \"Restore \"\"items\"\"\";";
        Assert.Equal(0L, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    private static async Task UseLegacyManifestAsync(string archivePath, bool olderTables)
    {
        await using var archive = await ZipFile.OpenAsync(archivePath, ZipArchiveMode.Update, TestContext.Current.CancellationToken);
        var entry = archive.GetEntry("manifest.json")!;
        JsonObject manifest;
        await using (var stream = await entry.OpenAsync(TestContext.Current.CancellationToken))
        {
            manifest = (await JsonNode.ParseAsync(stream, cancellationToken: TestContext.Current.CancellationToken))!.AsObject();
        }

        if (olderTables)
        {
            archive.GetEntry("Database/MediaSegments.json")!.Delete();
        }

        manifest["DatabaseTables"] = new JsonArray(archive.Entries
            .Where(e => e.FullName.StartsWith("Database/", StringComparison.Ordinal))
            .Select(e => (JsonNode)JsonValue.Create(e.Name == "HistoryRow.json" ? "HistoryRow" : "DbSet`1")!)
            .ToArray());
        entry.Delete();
        await using var output = await archive.CreateEntry("manifest.json").OpenAsync(TestContext.Current.CancellationToken);
        await JsonSerializer.SerializeAsync(output, manifest, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RestoreBackupAsync_PreservesGeneratedIdsAndPrivateForeignKeys()
    {
        var token = TestContext.Current.CancellationToken;
        var user = new User("restore-user", "test", "test");
        await using (var context = CreateDbContext())
        {
            var activity = new ActivityLog("archived", "restore-test", user.Id);
            var image = new ImageInfo("archived-image");
            context.AddRange(user, activity, image);
            context.Entry(activity).Property(row => row.Id).CurrentValue = 91;
            context.Entry(image).Property(row => row.Id).CurrentValue = 92;
            context.Entry(image).Property(row => row.UserId).CurrentValue = user.Id;
            await context.SaveChangesAsync(token);
            await context.GetService<IHistoryRepository>().CreateIfNotExistsAsync(token);
        }

        var service = CreateBackupService();
        var archive = await service.CreateBackupAsync(new BackupOptionsDto());
        await service.RestoreBackupAsync(archive.Path);

        await using var restored = CreateDbContext();
        Assert.Equal(91, (await restored.ActivityLogs.SingleAsync(token)).Id);
        var restoredImage = await restored.ImageInfos.SingleAsync(token);
        Assert.Equal(92, restoredImage.Id);
        Assert.Equal(user.Id, restoredImage.UserId);
        var next = new ActivityLog("generated", "restore-test", user.Id);
        restored.ActivityLogs.Add(next);
        await restored.SaveChangesAsync(token);
        Assert.True(next.Id > 91);
    }

    [Fact]
    public async Task RestoreBackupAsync_CompletionFails_RollsBackSavedRowsAndHistory()
    {
        var archivePath = await CreateRestoreArchiveAsync();
        var failure = new InvalidOperationException("completion failed");
        var provider = new Mock<IJellyfinDatabaseProvider>();
        provider.Setup(value => value.BeginDatabaseRestoreAsync(It.IsAny<JellyfinDbContext>(), It.IsAny<CancellationToken>()))
            .Returns<JellyfinDbContext, CancellationToken>(_database.Provider.BeginDatabaseRestoreAsync);
        provider.Setup(value => value.EndDatabaseRestoreAsync(It.IsAny<JellyfinDbContext>(), It.IsAny<CancellationToken>()))
            .Returns<JellyfinDbContext, CancellationToken>(_database.Provider.EndDatabaseRestoreAsync);
        provider.Setup(value => value.PurgeDatabase(It.IsAny<JellyfinDbContext>(), It.IsAny<System.Collections.Generic.IEnumerable<string>>()))
            .Returns<JellyfinDbContext, System.Collections.Generic.IEnumerable<string>>(_database.Provider.PurgeDatabase);
        provider.Setup(value => value.CompleteDatabaseRestoreAsync(It.IsAny<JellyfinDbContext>(), It.IsAny<CancellationToken>()))
            .Returns<JellyfinDbContext, CancellationToken>(async (context, token) =>
            {
                Assert.NotNull(context.Database.CurrentTransaction);
                if (context.Database.IsSqlite())
                {
                    // Enforced foreign keys would make the purge delete and cascade row by row.
                    await using var command = context.Database.GetDbConnection().CreateCommand();
                    command.CommandText = "PRAGMA foreign_keys;";
                    Assert.Equal(0L, await command.ExecuteScalarAsync(token));
                }

                Assert.Equal(new[] { "Archived Child", "Archived Movie" }, await context.BaseItems.Where(row => row.Type != "PLACEHOLDER").OrderBy(row => row.Name).Select(row => row.Name).ToArrayAsync(token));
                Assert.Equal("backup", Assert.Single(await context.GetService<IHistoryRepository>().GetAppliedMigrationsAsync(token)).MigrationId);
                throw failure;
            });

        var error = await Record.ExceptionAsync(() => CreateBackupService(provider.Object).RestoreBackupAsync(archivePath));
        Assert.Same(failure, error);
        await AssertExistingDatabaseAsync();
    }

    [Fact]
    public async Task RestoreBackupAsync_HomeSections_KeepTheirIdsAndDisplayPreferences()
    {
        var token = TestContext.Current.CancellationToken;
        await using (var context = CreateDbContext())
        {
            await SeedHomeSectionsAsync(context);
        }

        var service = CreateBackupService();
        var archive = await service.CreateBackupAsync(new BackupOptionsDto());
        await using (var zip = await ZipFile.OpenReadAsync(archive.Path, token))
        {
            Assert.NotNull(zip.GetEntry("Database/HomeSection.json"));
        }

        await using (var context = CreateDbContext())
        {
            await context.HomeSections.ExecuteDeleteAsync(token);
            context.HomeSections.Add(new HomeSection { DisplayPreferencesId = 32, Order = 3, Type = HomeSectionType.LiveTv });
            await context.SaveChangesAsync(token);
        }

        await service.RestoreBackupAsync(archive.Path);

        Assert.Equal(_seededHomeSections, await ReadHomeSectionsAsync());
        await using var restored = CreateDbContext();
        var next = new HomeSection { DisplayPreferencesId = 31, Order = 2, Type = HomeSectionType.None };
        restored.HomeSections.Add(next);
        await restored.SaveChangesAsync(token);
        Assert.True(next.Id > 75);
    }

    [Fact]
    public async Task RestoreBackupAsync_ArchiveWithoutHomeSections_RestoresTheOtherTables()
    {
        var token = TestContext.Current.CancellationToken;
        await using (var context = CreateDbContext())
        {
            await SeedHomeSectionsAsync(context);
        }

        var service = CreateBackupService();
        var archive = await service.CreateBackupAsync(new BackupOptionsDto());

        // Archives written before home sections were backed up have neither the entry nor the manifest table.
        await using (var zip = await ZipFile.OpenAsync(archive.Path, ZipArchiveMode.Update, token))
        {
            zip.GetEntry("Database/HomeSection.json")!.Delete();
            var manifestEntry = zip.GetEntry("manifest.json")!;
            JsonObject manifest;
            await using (var stream = await manifestEntry.OpenAsync(token))
            {
                manifest = (await JsonNode.ParseAsync(stream, cancellationToken: token))!.AsObject();
            }

            manifest["DatabaseTables"] = new JsonArray(manifest["DatabaseTables"]!.AsArray()
                .Where(table => table!.GetValue<string>() != "HomeSection")
                .Select(table => table!.DeepClone())
                .ToArray());
            manifestEntry.Delete();
            await using var output = await zip.CreateEntry("manifest.json").OpenAsync(token);
            await JsonSerializer.SerializeAsync(output, manifest, cancellationToken: token);
        }

        await using (var context = CreateDbContext())
        {
            await context.DisplayPreferences.Where(row => row.Client == "tv").ExecuteDeleteAsync(token);
        }

        await service.RestoreBackupAsync(archive.Path);

        await using var restored = CreateDbContext();
        Assert.Equal(
            new[] { (31, "web"), (32, "tv") },
            (await restored.DisplayPreferences.OrderBy(row => row.Id).ToListAsync(token)).Select(row => (row.Id, row.Client)));
        Assert.Empty(await restored.HomeSections.ToListAsync(token));
        await AssertForeignKeysEnabledAsync(restored);
    }

    [Fact]
    public async Task RestoreBackupAsync_ForeignKeyViolation_KeepsDatabaseUnchanged()
    {
        var token = TestContext.Current.CancellationToken;
        await using (var context = CreateDbContext())
        {
            await SeedHomeSectionsAsync(context);
        }

        var service = CreateBackupService();
        var archive = await service.CreateBackupAsync(new BackupOptionsDto());
        await SetFirstHomeSectionDisplayPreferencesAsync(archive.Path, 999);

        // Differ from the archive, so that a partly applied restore shows.
        await using (var context = CreateDbContext())
        {
            context.HomeSections.Add(new HomeSection { DisplayPreferencesId = 32, Order = 1, Type = HomeSectionType.LiveTv });
            context.BaseItems.Add(CreateMovieEntity(Guid.NewGuid(), "Existing Movie"));
            await context.SaveChangesAsync(token);
        }

        var rowCounts = await CountRowsAsync();
        var homeSections = await ReadHomeSectionsAsync();

        var exception = await Record.ExceptionAsync(() => service.RestoreBackupAsync(archive.Path));

        AssertForeignKeyViolation(exception, "HomeSection");
        Assert.Equal(rowCounts, await CountRowsAsync());
        Assert.Equal(homeSections, await ReadHomeSectionsAsync());
        await using var unchanged = CreateDbContext();
        Assert.Equal("Existing Movie", (await unchanged.BaseItems.SingleAsync(row => row.Type == "Movie", token)).Name);
        await AssertForeignKeysEnabledAsync(unchanged);
    }

    [Fact]
    [Trait("Provider", "Sqlite")]
    public async Task RestoreBackupAsync_PooledSqliteConnections_KeepForeignKeysEnabled()
    {
        var token = TestContext.Current.CancellationToken;
        var provider = new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance);
        var builder = new DbContextOptionsBuilder<JellyfinDbContext>();
        provider.Initialise(builder, new DatabaseConfigurationOptions
        {
            DatabaseType = "Jellyfin-SQLite",
            CustomProviderOptions = new CustomDatabaseOptions
            {
                PluginName = string.Empty,
                PluginAssembly = string.Empty,
                ConnectionString = string.Empty,
                Options = { new CustomDatabaseOption { Key = "path", Value = Path.Combine(_testRoot, "jellyfin.db") } }
            }
        });
        JellyfinDbContext CreatePooledDbContext() => new(
            builder.Options,
            NullLogger<JellyfinDbContext>.Instance,
            provider,
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));

        try
        {
            await using (var context = CreatePooledDbContext())
            {
                await context.Database.EnsureCreatedAsync(token);
                await SeedHomeSectionsAsync(context);
            }

            var service = CreateBackupService(provider, CreatePooledDbContext);
            var archive = await service.CreateBackupAsync(new BackupOptionsDto());
            await service.RestoreBackupAsync(archive.Path);
            await AssertPooledForeignKeysEnabledAsync(CreatePooledDbContext);

            await SetFirstHomeSectionDisplayPreferencesAsync(archive.Path, 999);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreBackupAsync(archive.Path));
            await AssertPooledForeignKeysEnabledAsync(CreatePooledDbContext);
        }
        finally
        {
            await using var context = CreatePooledDbContext();
            SqliteConnection.ClearPool((SqliteConnection)context.Database.GetDbConnection());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PurgeDatabase_Table_AlsoEmptiesTablesReferencingIt(bool duringRestore)
    {
        var token = TestContext.Current.CancellationToken;
        var provider = _database.Provider;
        await using var context = CreateDbContext();
        await SeedHomeSectionsAsync(context);

        await context.Database.OpenConnectionAsync(token);
        try
        {
            if (duringRestore)
            {
                await provider.BeginDatabaseRestoreAsync(context, token);
            }

            await using var transaction = await context.Database.BeginTransactionAsync(token);
            await provider.PurgeDatabase(context, [context.Model.FindEntityType(typeof(DisplayPreferences))!.GetSchemaQualifiedTableName()!]);
            await transaction.CommitAsync(token);
        }
        finally
        {
            if (duringRestore)
            {
                await provider.EndDatabaseRestoreAsync(context, token);
            }

            await context.Database.CloseConnectionAsync();
        }

        context.ChangeTracker.Clear();
        Assert.Equal(0, await context.DisplayPreferences.CountAsync(token));
        Assert.Equal(0, await context.HomeSections.CountAsync(token));
        Assert.Equal(1, await context.Users.CountAsync(token));
        await AssertForeignKeysEnabledAsync(context);
    }

    private async Task<string> CreateRestoreArchiveAsync()
    {
        using var context = CreateDbContext();
        var archived = CreateMovieEntity(Guid.NewGuid(), "Archived Movie");
        var archivedChild = CreateMovieEntity(Guid.NewGuid(), "Archived Child");
        context.BaseItems.AddRange(archived, archivedChild);
        context.LinkedChildren.Add(new LinkedChildEntity { ParentId = archived.Id, ChildId = archivedChild.Id, ChildType = LinkedChildType.Manual });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        var history = context.GetService<IHistoryRepository>();
        await history.CreateIfNotExistsAsync(TestContext.Current.CancellationToken);

        // Only the rows written here, whether or not the test database was created through migrations.
        foreach (var applied in await history.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken))
        {
            await context.Database.ExecuteSqlRawAsync(history.GetDeleteScript(applied.MigrationId), TestContext.Current.CancellationToken);
        }

        await context.Database.ExecuteSqlRawAsync(history.GetInsertScript(new HistoryRow("backup", "10.0.0")), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_configurationDirectoryPath, "system.xml"), "archived config", TestContext.Current.CancellationToken);
        var archive = await CreateBackupService().CreateBackupAsync(new BackupOptionsDto());
        context.ChangeTracker.Clear();
        await context.LinkedChildren.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        await context.BaseItems.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        var existing = CreateMovieEntity(Guid.NewGuid(), "Existing Movie");
        var existingChild = CreateMovieEntity(Guid.NewGuid(), "Existing Child");
        context.BaseItems.AddRange(existing, existingChild);
        context.LinkedChildren.Add(new LinkedChildEntity { ParentId = existing.Id, ChildId = existingChild.Id, ChildType = LinkedChildType.Manual });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await context.Database.ExecuteSqlRawAsync(history.GetDeleteScript("backup"), TestContext.Current.CancellationToken);
        await context.Database.ExecuteSqlRawAsync(history.GetInsertScript(new HistoryRow("existing", "10.0.0")), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_configurationDirectoryPath, "system.xml"), "existing config", TestContext.Current.CancellationToken);
        return archive.Path;
    }

    private async Task AssertExistingDatabaseAsync()
    {
        using var context = CreateDbContext();
        Assert.Equal(new[] { "Existing Child", "Existing Movie" }, context.BaseItems.OrderBy(e => e.Name).Select(e => e.Name).ToArray());
        Assert.Single(context.LinkedChildren);
        Assert.Equal("existing", Assert.Single(await context.GetService<IHistoryRepository>().GetAppliedMigrationsAsync(TestContext.Current.CancellationToken)).MigrationId);
        await AssertForeignKeysEnabledAsync(context);
    }

    private static async Task AssertForeignKeysEnabledAsync(JellyfinDbContext context)
    {
        // SQLite can switch foreign keys off per connection; PostgreSQL always enforces them.
        if (context.Database.IsSqlite())
        {
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = "PRAGMA foreign_keys;";
            Assert.Equal(1L, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            command.CommandText = "PRAGMA defer_foreign_keys;";
            Assert.Equal(0L, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        }

        context.BaseItems.Add(new BaseItemEntity { Id = Guid.NewGuid(), Type = "Movie", ParentId = Guid.NewGuid() });
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    private static async Task AssertPooledForeignKeysEnabledAsync(Func<JellyfinDbContext> createDbContext)
    {
        // Hold several connections at once, so the one the restore gave back to the pool is among them.
        var contexts = Enumerable.Range(0, 4).Select(_ => createDbContext()).ToArray();
        try
        {
            foreach (var context in contexts)
            {
                await context.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
                await using var command = context.Database.GetDbConnection().CreateCommand();
                command.CommandText = "PRAGMA foreign_keys;";
                Assert.Equal(1L, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            }
        }
        finally
        {
            foreach (var context in contexts)
            {
                await context.DisposeAsync();
            }
        }
    }

    private void AssertForeignKeyViolation(Exception? exception, string table)
    {
        Assert.NotNull(exception);

        // SQLite checks the restored rows just before the restore commits, PostgreSQL while they are written.
        using var context = CreateDbContext();
        Assert.True(
            context.Database.IsSqlite() ? exception is InvalidOperationException : exception is DbUpdateException or DbException,
            exception.ToString());
        Assert.Contains(table, (exception.InnerException ?? exception).Message, StringComparison.Ordinal);
    }

    private static async Task SeedHomeSectionsAsync(JellyfinDbContext context)
    {
        var user = new User("home-user", "test", "test");
        var web = new DisplayPreferences(user.Id, Guid.Empty, "web");
        var tv = new DisplayPreferences(user.Id, Guid.Empty, "tv");
        context.AddRange(user, web, tv);
        context.Entry(web).Property(row => row.Id).CurrentValue = 31;
        context.Entry(tv).Property(row => row.Id).CurrentValue = 32;
        foreach (var (preferencesId, _, id, order, type) in _seededHomeSections)
        {
            var section = new HomeSection { DisplayPreferencesId = preferencesId, Order = order, Type = type };
            context.HomeSections.Add(section);
            context.Entry(section).Property(row => row.Id).CurrentValue = id;
        }

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await context.GetService<IHistoryRepository>().CreateIfNotExistsAsync(TestContext.Current.CancellationToken);
    }

    private async Task<(int PreferencesId, string Client, int Id, int Order, HomeSectionType Type)[]> ReadHomeSectionsAsync()
    {
        await using var context = CreateDbContext();
        var preferences = await context.DisplayPreferences.AsNoTracking().Include(row => row.HomeSections).ToListAsync(TestContext.Current.CancellationToken);
        return preferences
            .SelectMany(row => row.HomeSections.Select(section => (PreferencesId: row.Id, row.Client, section.Id, section.Order, section.Type)))
            .OrderBy(section => section.Id)
            .ToArray();
    }

    private static async Task SetFirstHomeSectionDisplayPreferencesAsync(string archivePath, int displayPreferencesId)
    {
        await using var archive = await ZipFile.OpenAsync(archivePath, ZipArchiveMode.Update, TestContext.Current.CancellationToken);
        var entry = archive.GetEntry("Database/HomeSection.json")!;
        JsonArray rows;
        await using (var stream = await entry.OpenAsync(TestContext.Current.CancellationToken))
        {
            rows = (await JsonNode.ParseAsync(stream, cancellationToken: TestContext.Current.CancellationToken))!.AsArray();
        }

        rows[0]!["DisplayPreferencesId"] = displayPreferencesId;
        entry.Delete();
        await using var output = await archive.CreateEntry("Database/HomeSection.json").OpenAsync(TestContext.Current.CancellationToken);
        await JsonSerializer.SerializeAsync(output, rows, cancellationToken: TestContext.Current.CancellationToken);
    }

    private async Task<SortedDictionary<string, long>> CountRowsAsync()
    {
        var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        await using var context = CreateDbContext();
        var sql = context.GetService<ISqlGenerationHelper>();
        await context.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        foreach (var table in context.GetService<IDesignTimeModel>().Model.GetRelationalModel().Tables)
        {
            await using var command = context.Database.GetDbConnection().CreateCommand();
#pragma warning disable CA2100 // Identifiers come from the EF model.
            command.CommandText = "SELECT COUNT(*) FROM " + sql.DelimitIdentifier(table.Name, table.Schema);
#pragma warning restore CA2100
            counts[table.SchemaQualifiedName] = Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken), CultureInfo.InvariantCulture);
        }

        return counts;
    }

    [Fact]
    public async Task CreateBackupAsync_DatabaseConfiguration_IsLeftOut()
    {
        await File.WriteAllTextAsync(Path.Combine(_configurationDirectoryPath, "system.xml"), "<ServerConfiguration />", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_configurationDirectoryPath, "database.xml"), "<DatabaseConfigurationOptions />", TestContext.Current.CancellationToken);

        var manifest = await CreateBackupService().CreateBackupAsync(new BackupOptionsDto());

        await using var archive = await ZipFile.OpenReadAsync(manifest.Path, TestContext.Current.CancellationToken);
        Assert.NotNull(archive.GetEntry("Config/system.xml"));
        Assert.Null(archive.GetEntry("Config/database.xml"));
    }

    [Fact]
    public async Task RestoreBackupAsync_ArchiveWithDatabaseConfiguration_KeepsTheLocalFile()
    {
        var databaseConfigurationPath = Path.Combine(_configurationDirectoryPath, "database.xml");
        var manifest = await CreateBackupService().CreateBackupAsync(new BackupOptionsDto { Database = false });
        await using (var archive = await ZipFile.OpenAsync(manifest.Path, ZipArchiveMode.Update, TestContext.Current.CancellationToken))
        {
            // Archives written before the database configuration was left out still contain it.
            await using var writer = new StreamWriter(await archive.CreateEntry("Config/database.xml").OpenAsync(TestContext.Current.CancellationToken));
            await writer.WriteAsync("<DatabaseConfigurationOptions><DatabaseType>from-archive</DatabaseType></DatabaseConfigurationOptions>".AsMemory(), TestContext.Current.CancellationToken);
        }

        await File.WriteAllTextAsync(databaseConfigurationPath, "local", TestContext.Current.CancellationToken);

        await CreateBackupService().RestoreBackupAsync(manifest.Path);

        Assert.Equal("local", await File.ReadAllTextAsync(databaseConfigurationPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateBackupAsync_OptimizationFails_StillCreatesTheBackup()
    {
        var provider = new Mock<IJellyfinDatabaseProvider>();
        provider.Setup(p => p.RunScheduledOptimisation(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("optimization failed"));

        var manifest = await CreateBackupService(databaseProvider: provider.Object).CreateBackupAsync(new BackupOptionsDto());

        Assert.True(File.Exists(manifest.Path));
    }

    [Fact(Timeout = 60_000)]
    public async Task CreateBackupAsync_TableCannotBeRead_FailsNamingTheTable()
    {
        using var database = TestDatabase.Create(new TestDatabaseOptions { Interceptors = [new FailingReadInterceptor("ActivityLogs")] });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateBackupService(createDbContext: database.CreateDbContext).CreateBackupAsync(new BackupOptionsDto()));

        Assert.Contains("ActivityLogs", exception.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_backupPath));
    }

    [Fact]
    public async Task CreateBackupAsync_Manifest_RecordsProviderAndRowCounts()
    {
        await using (var context = CreateDbContext())
        {
            context.BaseItems.AddRange(CreateMovieEntity(Guid.NewGuid(), "One"), CreateMovieEntity(Guid.NewGuid(), "Two"));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        int baseItemCount;
        await using (var context = CreateDbContext())
        {
            baseItemCount = await context.BaseItems.CountAsync(TestContext.Current.CancellationToken);
        }

        var manifest = await CreateBackupService(databaseProvider: _database.Provider).CreateBackupAsync(new BackupOptionsDto());

        await using var archive = await ZipFile.OpenReadAsync(manifest.Path, TestContext.Current.CancellationToken);
        await using var manifestStream = await archive.GetEntry("manifest.json")!.OpenAsync(TestContext.Current.CancellationToken);
        using var document = await JsonDocument.ParseAsync(manifestStream, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(_database.ProviderKey, document.RootElement.GetProperty("DatabaseProvider").GetString());
        Assert.Equal(baseItemCount, document.RootElement.GetProperty("TableRowCounts").GetProperty("BaseItems").GetInt64());
    }

    private BackupService CreateBackupService(IJellyfinDatabaseProvider? databaseProvider = null, Func<JellyfinDbContext>? createDbContext = null)
    {
        createDbContext ??= CreateDbContext;
        var factory = new Mock<IDbContextFactory<JellyfinDbContext>>();
        factory.Setup(f => f.CreateDbContext()).Returns(createDbContext);
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(createDbContext);

        var applicationHost = new Mock<IServerApplicationHost>();
        applicationHost.Setup(a => a.ApplicationVersion).Returns(new Version(10, 11, 0));

        var applicationPaths = new Mock<IServerApplicationPaths>();
        applicationPaths.Setup(a => a.BackupPath).Returns(_backupPath);
        applicationPaths.Setup(a => a.CachePath).Returns(Path.Combine(_testRoot, "Cache"));
        applicationPaths.Setup(a => a.ProgramDataPath).Returns(_testRoot);
        applicationPaths.Setup(a => a.ConfigurationDirectoryPath).Returns(_configurationDirectoryPath);
        applicationPaths.Setup(a => a.DataPath).Returns(Path.Combine(_testRoot, "Data"));
        applicationPaths.Setup(a => a.RootFolderPath).Returns(Path.Combine(_testRoot, "Root"));
        applicationPaths.Setup(a => a.InternalMetadataPath).Returns(Path.Combine(_testRoot, "Metadata"));
        applicationPaths.Setup(a => a.DefaultInternalMetadataPath).Returns(Path.Combine(_testRoot, "MetadataDefault"));

        var applicationLifetime = new Mock<IHostApplicationLifetime>();

        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(l => l.IsScanRunning).Returns(false);

        return new BackupService(
            NullLogger<BackupService>.Instance,
            factory.Object,
            applicationHost.Object,
            applicationPaths.Object,
            databaseProvider ?? _database.Provider,
            applicationLifetime.Object,
            libraryManager.Object);
    }

    private static BaseItemEntity CreateMovieEntity(Guid id, string name)
    {
        return new BaseItemEntity
        {
            Id = id,
            Type = "Movie",
            Name = name,
            PresentationUniqueKey = id.ToString("N"),
            MediaType = "Video",
            IsMovie = true,
            IsFolder = false,
            IsVirtualItem = false
        };
    }

    private JellyfinDbContext CreateDbContext() => _database.CreateDbContext();

    private sealed class FailingReadInterceptor(string table) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
            => command.CommandText.Contains($"FROM \"{table}\"", StringComparison.Ordinal)
                ? throw new InvalidOperationException("The table cannot be read.")
                : ValueTask.FromResult(result);
    }
}
