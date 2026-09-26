using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Testing;
using Jellyfin.Database.Testing.Import;
using Jellyfin.Server.Implementations.DatabaseImport;
using Jellyfin.Server.Implementations.DatabaseImport.PostgreSql;
using Jellyfin.Server.Implementations.DatabaseImport.Sqlite;
using Jellyfin.Server.Implementations.Tests.DatabaseImport.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.DatabaseImport.PostgreSql;

[Trait("Provider", "PostgreSql")]
public sealed class PostgreSqlImportFinalizerTests : IClassFixture<SqliteSourceFixture>, IDisposable
{
    private static readonly ImportModel _model = ImportModel.ForPostgreSql();

    private readonly SqliteSourceFixture _sources;
    private readonly PostgreSqlTestDatabase? _database;

    public PostgreSqlImportFinalizerTests(SqliteSourceFixture sources)
    {
        _sources = sources;
        if (TestDatabase.PostgreSqlConnectionString is { } connectionString)
        {
            _database = new PostgreSqlTestDatabase(connectionString, new TestDatabaseOptions());
        }
    }

    public static TheoryData<string, string, string?> Tampering => new()
    {
        { "DELETE FROM \"UserData\" WHERE ctid = (SELECT min(ctid) FROM \"UserData\")", nameof(FinalizeCheck.RowCountMismatch), "UserData" },
        { "UPDATE \"BaseItems\" SET \"Name\" = left(\"Name\", 1) WHERE \"Id\" = (SELECT \"Id\" FROM \"BaseItems\" WHERE length(\"Name\") > 1 ORDER BY \"Id\" LIMIT 1)", nameof(FinalizeCheck.ContentMismatch), "BaseItems" },
        { "UPDATE \"BaseItems\" SET \"DateCreated\" = \"DateCreated\" + interval '1 hour' WHERE \"Id\" = (SELECT \"Id\" FROM \"BaseItems\" WHERE \"DateCreated\" IS NOT NULL ORDER BY \"Id\" LIMIT 1)", nameof(FinalizeCheck.ContentMismatch), "BaseItems" },
        { "ALTER TABLE \"UserData\" DROP CONSTRAINT \"FK_UserData_BaseItems_ItemId\"", nameof(FinalizeCheck.CatalogChanged), "UserData" },
        { "ALTER TABLE \"UserData\" DROP CONSTRAINT \"FK_UserData_BaseItems_ItemId\"; ALTER TABLE \"UserData\" ADD CONSTRAINT \"FK_UserData_BaseItems_ItemId\" FOREIGN KEY (\"ItemId\") REFERENCES \"BaseItems\" (\"Id\") ON DELETE CASCADE NOT VALID", nameof(FinalizeCheck.CatalogChanged), "UserData" },
        { "DROP INDEX \"IX_Peoples_NameLower\"", nameof(FinalizeCheck.CatalogChanged), "Peoples" },
        { "ALTER TABLE \"TrickplayInfos\" DROP CONSTRAINT \"PK_TrickplayInfos\"", nameof(FinalizeCheck.CatalogChanged), "TrickplayInfos" },
        { "ALTER TABLE \"BaseItems\" ADD COLUMN \"PluginColumn\" text", nameof(FinalizeCheck.CatalogChanged), "BaseItems" },
        { "CREATE FUNCTION plugin_trigger() RETURNS trigger LANGUAGE plpgsql AS 'BEGIN RETURN NEW; END'; CREATE TRIGGER plugin BEFORE INSERT ON \"Users\" FOR EACH ROW EXECUTE FUNCTION plugin_trigger()", nameof(FinalizeCheck.CatalogChanged), "Users" },
        { "INSERT INTO \"__EFMigrationsHistory\" VALUES ('20250101000000_Jellyfin_PgSQL_Init', '9.0.0')", nameof(FinalizeCheck.HistoryForeignMigrations), "__EFMigrationsHistory" },
        { $"DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '{SqliteSourceFixture.CodeMigrationId}'", nameof(FinalizeCheck.HistoryMissingMigrations), "__EFMigrationsHistory" },
    };

    private PostgreSqlTestDatabase Database
    {
        get
        {
            Assert.SkipWhen(_database is null, $"{TestDatabase.PostgreSqlConnectionStringEnvironmentVariable} is not set.");
            return _database;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Finalize_CleanLoad_CommitsADatabaseTheServerCanUse(bool edgeValues)
    {
        var (manifest, finalizer, connection) = await PrepareAsync(edgeValues);
        await using (connection)
        {
            var result = await finalizer.FinalizeAsync(connection, manifest, TestContext.Current.CancellationToken);

            Assert.Empty(result.Findings);
            Assert.True(result.Committed);
            Assert.Equal(edgeValues, manifest.Tables.Any(t => t.TimestampSentinels.Count > 0));
        }

        await using var context = Database.CreateDbContext();
        Assert.Equal(manifest.Tables.Single(t => t.Name == "BaseItems").RowCount, (await context.BaseItems.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken)).Count);
        Assert.Equal(manifest.Tables.Single(t => t.Name == "Users").RowCount, (await context.Users.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken)).Count);

        // The identity sequences are past the loaded ids.
        var maxId = await context.ActivityLogs.MaxAsync(a => a.Id, TestContext.Current.CancellationToken);
        var log = new ActivityLog("After the import", "Test", Guid.Empty);
        context.ActivityLogs.Add(log);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(maxId + 1, log.Id);
    }

    [Theory]
    [MemberData(nameof(Tampering))]
    public async Task Finalize_TamperedLoad_RollsBack(string tampering, string check, string? table)
    {
        var (manifest, finalizer, connection) = await PrepareAsync(false);
        await using (connection)
        {
            await ExecuteAsync(connection, tampering);

            var result = await finalizer.FinalizeAsync(connection, manifest, TestContext.Current.CancellationToken);

            Assert.False(result.Committed);
            Assert.Contains(result.Findings, f => f.Check == check && f.Table == table && f.Severity == ImportFindingSeverity.Error);
        }
    }

    [Fact]
    public async Task Finalize_ExtraValueBeyondTheServerRange_RollsBack()
    {
        var (manifest, finalizer, connection) = await PrepareAsync(true);
        await using (connection)
        {
            await ExecuteAsync(connection, "UPDATE \"BaseItems\" SET \"DateModified\" = '10000-06-01 00:00:00+00' WHERE \"Id\" = (SELECT \"Id\" FROM \"BaseItems\" WHERE \"DateModified\" < '9000-01-01' ORDER BY \"Id\" LIMIT 1)");

            var result = await finalizer.FinalizeAsync(connection, manifest, TestContext.Current.CancellationToken);

            Assert.False(result.Committed);
            Assert.Contains(result.Findings, f => f.Check == nameof(FinalizeCheck.SentinelCountMismatch) && f.Table == "BaseItems" && f.Column == "DateModified");
        }

        // Rolled back: the values that round past the server's range are still finite.
        await using var check = new NpgsqlConnection(Database.ConnectionString);
        await check.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM \"ActivityLogs\" WHERE \"DateCreated\" = 'infinity'", check);
        Assert.Equal(0L, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Finalize_OtherDatabase_IsRefused()
    {
        var (manifest, finalizer, connection) = await PrepareAsync(false, reference => reference with { DatabaseOid = reference.DatabaseOid + 1 });
        await using (connection)
        {
            var result = await finalizer.FinalizeAsync(connection, manifest, TestContext.Current.CancellationToken);

            Assert.False(result.Committed);
            Assert.Equal(nameof(FinalizeCheck.TargetChanged), Assert.Single(result.Findings).Check);
        }
    }

    [Fact]
    public async Task Finalize_ImportLockHeldBySomeoneElse_IsRefused()
    {
        var (manifest, finalizer, connection) = await PrepareAsync(false);
        await using (connection)
        {
            await using var other = new NpgsqlConnection(Database.ConnectionString);
            await other.OpenAsync(TestContext.Current.CancellationToken);
            await ExecuteAsync(other, $"SELECT pg_advisory_lock(hashtext('{PostgreSqlImportFinalizer.LockKey}'))");

            var result = await finalizer.FinalizeAsync(connection, manifest, TestContext.Current.CancellationToken);

            Assert.False(result.Committed);
            Assert.Equal(nameof(FinalizeCheck.ImportLockHeld), Assert.Single(result.Findings).Check);
        }
    }

    public void Dispose()
    {
        _database?.Dispose();
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
#pragma warning disable CA2100 // The statements are constants of this class.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private async Task<(ImportManifest Manifest, PostgreSqlImportFinalizer Finalizer, NpgsqlConnection Connection)> PrepareAsync(
        bool edgeValues,
        Func<PostgreSqlCatalogSnapshot, PostgreSqlCatalogSnapshot>? changeReference = null)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = Database;

        // Preflight.
        var path = _sources.Copy(edgeValues ? _sources.SmallEdge : _sources.Small);
        var snapshot = await SqliteSnapshotWriter.WriteAsync(path, path + ".snapshot", cancellationToken);
        var inspector = new SqliteSourceInspector(_model, SqliteSourceInspector.GetSchemaMigrationIds(), [SqliteSourceFixture.CodeMigrationId], Version.Parse(SqliteSourceFixture.ServerVersion));
        SqliteInspection inspection;
        await using (var source = await SqliteSourceInspector.OpenReadOnlyAsync(snapshot.Path, cancellationToken))
        {
            inspection = await inspector.InspectAsync(source, cancellationToken);
        }

        Assert.True(inspection.Succeeded);
        var manifest = new ImportManifest(ImportManifest.CurrentFormatVersion, SqliteSourceFixture.ServerVersion, _model.Fingerprint, snapshot.Sha256, snapshot.SourceFiles, inspection.Tables, inspection.Findings);

        // Seed: the harness applied the schema migrations; the code migrations are recorded as the seed step does.
        string[] historyIds;
        await using (var context = database.CreateDbContext())
        {
            historyIds = [.. context.Database.GetMigrations(), SqliteSourceFixture.CodeMigrationId];
        }

        var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await ExecuteAsync(connection, $"INSERT INTO \"__EFMigrationsHistory\" VALUES ('{SqliteSourceFixture.CodeMigrationId}', '{SqliteSourceFixture.ServerVersion}')");
        var reference = await PostgreSqlCatalogSnapshot.CaptureAsync(connection, cancellationToken);

        // Load.
        Assert.SkipUnless(await DataOnlyLoader.CanLoadAsync(connection, cancellationToken), "Loading without foreign key checks needs a superuser.");
        await DataOnlyLoader.LoadAsync(snapshot.Path, connection, cancellationToken);
        return (manifest, new PostgreSqlImportFinalizer(_model, historyIds, changeReference?.Invoke(reference) ?? reference), connection);
    }
}
