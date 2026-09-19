using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Server.Implementations.DatabaseImport;
using Jellyfin.Server.Implementations.DatabaseImport.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.DatabaseImport.Sqlite;

public class SqliteSourceInspectorTests : IClassFixture<SqliteSourceFixture>
{
    private static readonly ImportModel _model = ImportModel.ForPostgreSql();

    private readonly SqliteSourceFixture _fixture;

    public SqliteSourceInspectorTests(SqliteSourceFixture fixture)
    {
        _fixture = fixture;
    }

    public static TheoryData<string, string, string, string, string?> PathologicalSources => new()
    {
        { "UPDATE BaseItems SET Name = 'a' || char(0) || 'b' WHERE " + Last("BaseItems"), "NUL in text", nameof(PreflightCheck.NulInText), "BaseItems", "Name" },
        { "UPDATE BaseItems SET Name = CAST(X'61C328' AS TEXT) WHERE " + Last("BaseItems"), "invalid UTF-8", nameof(PreflightCheck.InvalidUtf8), "BaseItems", "Name" },
        { "UPDATE BaseItems SET IsFolder = 'yes' WHERE " + Last("BaseItems"), "text in an integer column", nameof(PreflightCheck.StorageClassMismatch), "BaseItems", "IsFolder" },
        { "UPDATE BaseItems SET ExtraType = 1.5 WHERE " + Last("BaseItems"), "real in an integer column", nameof(PreflightCheck.StorageClassMismatch), "BaseItems", "ExtraType" },
        { "UPDATE Users SET Username = X'4142' WHERE " + Last("Users"), "blob in a text column", nameof(PreflightCheck.StorageClassMismatch), "Users", "Username" },
        { "UPDATE BaseItems SET IsFolder = 2 WHERE " + Last("BaseItems"), "boolean 2", nameof(PreflightCheck.InvalidBoolean), "BaseItems", "IsFolder" },
        { "UPDATE BaseItems SET ExtraType = 3000000000 WHERE " + Last("BaseItems"), "32-bit overflow", nameof(PreflightCheck.IntegerOutOfRange), "BaseItems", "ExtraType" },
        { "UPDATE UserData SET UserId = replace(UserId, '-', '') WHERE " + Last("UserData"), "GUID without hyphens", nameof(PreflightCheck.InvalidUuid), "UserData", "UserId" },
        { "UPDATE UserData SET ItemId = '11111111-1111-1111-1111-111111111111' WHERE " + Last("UserData"), "orphan", nameof(PreflightCheck.Orphans), "UserData", null },
        { "UPDATE Peoples SET Name = hex(randomblob(1500)) WHERE " + Last("Peoples"), "oversized index row", nameof(PreflightCheck.IndexRowTooLarge), "Peoples", null },
        { "UPDATE BaseItems SET DateCreated = 'yesterday' WHERE " + Last("BaseItems"), "unparseable date", nameof(PreflightCheck.InvalidTimestamp), "BaseItems", "DateCreated" },
        { "UPDATE BaseItems SET DateCreated = '2024-01-01' WHERE " + Last("BaseItems"), "date without time", nameof(PreflightCheck.InvalidTimestamp), "BaseItems", "DateCreated" },
        { "UPDATE BaseItems SET DateCreated = '2024-01-01 10:00:00+02:00' WHERE " + Last("BaseItems"), "date with offset", nameof(PreflightCheck.TimestampWithOffset), "BaseItems", "DateCreated" },
        { "UPDATE KeyframeData SET KeyframeTicks = '[1,2,' WHERE " + Last("KeyframeData"), "malformed keyframes", nameof(PreflightCheck.InvalidKeyframeTicks), "KeyframeData", "KeyframeTicks" },
        { "UPDATE KeyframeData SET KeyframeTicks = 'null' WHERE " + Last("KeyframeData"), "null keyframes", nameof(PreflightCheck.InvalidKeyframeTicks), "KeyframeData", "KeyframeTicks" },
        { "INSERT INTO TrickplayInfos (ItemId, Width, Height, TileWidth, TileHeight, ThumbnailCount, Interval, Bandwidth) SELECT lower(ItemId), Width, Height, TileWidth, TileHeight, ThumbnailCount, Interval, Bandwidth FROM TrickplayInfos WHERE ItemId <> lower(ItemId) LIMIT 1", "id differing in case", nameof(PreflightCheck.DuplicateKeyAfterConversion), "TrickplayInfos", null },
        { "DROP TABLE HomeSection", "missing table", nameof(PreflightCheck.MissingTable), "HomeSection", null },
        { "ALTER TABLE BaseItems ADD COLUMN PluginColumn TEXT", "unknown column", nameof(PreflightCheck.UnknownColumn), "BaseItems", "PluginColumn" },
        { "ALTER TABLE ActivityLogs DROP COLUMN ShortOverview", "missing column", nameof(PreflightCheck.MissingColumn), "ActivityLogs", "ShortOverview" },
        { "DELETE FROM __EFMigrationsHistory WHERE MigrationId = (SELECT max(MigrationId) FROM __EFMigrationsHistory WHERE MigrationId <> '" + SqliteSourceFixture.CodeMigrationId + "')", "pending schema migration", nameof(PreflightCheck.PendingMigrations), "__EFMigrationsHistory", null },
        { "DELETE FROM __EFMigrationsHistory WHERE MigrationId = '" + SqliteSourceFixture.CodeMigrationId + "'", "pending code migration", nameof(PreflightCheck.PendingMigrations), "__EFMigrationsHistory", null },
        { "INSERT INTO __EFMigrationsHistory VALUES ('29990101000000_FromANewerServer', '10.0.11')", "migration of a newer server", nameof(PreflightCheck.NewerMigrations), "__EFMigrationsHistory", null },
        { "UPDATE __EFMigrationsHistory SET ProductVersion = '10.13.0' WHERE MigrationId = '" + SqliteSourceFixture.CodeMigrationId + "'", "code migration run by a newer server", nameof(PreflightCheck.NewerMigrations), "__EFMigrationsHistory", null },
        { "DROP TABLE __EFMigrationsHistory", "no history", nameof(PreflightCheck.MissingHistory), "__EFMigrationsHistory", null },
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Inspect_SyntheticSource_Succeeds(bool edgeValues)
    {
        var path = _fixture.Copy(edgeValues ? _fixture.SmallEdge : _fixture.Small);

        var inspection = await InspectAsync(path);

        Assert.True(inspection.Succeeded, string.Join(", ", inspection.Findings.Select(f => $"{f.Check} {f.Table}.{f.Column} {f.Index}")));
        Assert.Empty(inspection.Findings);
        Assert.Equal(_model.Tables.Select(t => t.Name), inspection.Tables.Select(t => t.Name));
        await using var connection = await SqliteSourceInspector.OpenReadOnlyAsync(path + ".snapshot", TestContext.Current.CancellationToken);
        foreach (var (table, summary) in _model.Tables.Zip(inspection.Tables))
        {
            // The streaming pass hashes the rows exactly as reading the table directly does.
            var direct = await TableContentHash.ComputeAsync(connection, table, TestContext.Current.CancellationToken);
            Assert.Equal(direct.RowCount, summary.RowCount);
            Assert.Equal(direct.Value, summary.ContentHash);
        }

        Assert.Equal(edgeValues, inspection.Tables.Any(t => t.TimestampSentinels.Count > 0));
    }

    [Theory]
    [MemberData(nameof(PathologicalSources))]
    public async Task Inspect_PathologicalSource_IsRefused(string mutation, string description, string check, string table, string? column)
    {
        var path = _fixture.Copy(_fixture.Small);
        Execute(path, mutation);

        var inspection = await InspectAsync(path);

        Assert.False(inspection.Succeeded, description);
        var findings = inspection.Findings.Where(f => f.Check == check && f.Table == table && (column is null || f.Column == column)).ToList();
        Assert.NotEmpty(findings);
        Assert.All(findings, f => Assert.Equal((ImportFindingSeverity.Error, 1L), (f.Severity, f.Count)));
    }

    [Fact]
    public async Task Inspect_PluginTableStatisticsAndRetiredMigration_OnlyWarns()
    {
        var path = _fixture.Copy(_fixture.Small);
        Execute(path, "CREATE TABLE PluginThing (Id INTEGER PRIMARY KEY); ANALYZE; INSERT INTO __EFMigrationsHistory VALUES ('20000101000000_Retired', '10.9.0')");

        var inspection = await InspectAsync(path);

        Assert.True(inspection.Succeeded);
        Assert.Equal(
            [(nameof(PreflightCheck.RetiredMigrations), "__EFMigrationsHistory"), (nameof(PreflightCheck.UnknownTable), "PluginThing")],
            inspection.Findings.Select(f => (f.Check, f.Table!)));
        Assert.All(inspection.Findings, f => Assert.Equal(ImportFindingSeverity.Warning, f.Severity));
    }

    [Fact]
    public async Task Inspect_ManyOrphans_CountsAllAndSamplesTen()
    {
        var path = _fixture.Copy(_fixture.Small);
        Execute(path, "UPDATE UserData SET ItemId = '11111111-1111-1111-1111-111111111111' WHERE rowid IN (SELECT rowid FROM UserData ORDER BY rowid LIMIT 25)");

        var inspection = await InspectAsync(path);

        var finding = Assert.Single(inspection.Findings);
        Assert.Equal(("Orphans", "FK_UserData_BaseItems_ItemId", 25L), (finding.Check, finding.Index!, finding.Count));
        Assert.Equal(ImportFinding.MaxPrimaryKeySamples, finding.PrimaryKeySamples.Count);
        Assert.All(finding.PrimaryKeySamples, key => Assert.StartsWith("11111111-1111-1111-1111-111111111111|", key, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Inspect_AllKeysSharingAHash_ComparesTheKeysAndFindsNothing()
    {
        var path = _fixture.Copy(_fixture.Small);
        var hashed = 0;
        ulong SameHash(string key)
        {
            hashed++;
            return 42;
        }

        // Each converted key after the first of its index shares a hash with an earlier row, which is read again to compare the keys.
        var inspection = await InspectAsync(path, SameHash);

        Assert.Empty(inspection.Findings);
        Assert.NotEqual(0, hashed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Inspect_ManyIdsDifferingInCase_CountsAllAndSamplesTen(bool keysShareAHash)
    {
        var path = _fixture.Copy(_fixture.Small);
        var copies = AddIdsDifferingInCase(path);

        var inspection = await InspectAsync(path, keysShareAHash ? _ => 42 : null);

        // The copies are read after the rows they duplicate, so they are the rows reported, in the order they were added.
        var finding = Assert.Single(inspection.Findings);
        Assert.Equal((nameof(PreflightCheck.DuplicateKeyAfterConversion), "TrickplayInfos", "PK_TrickplayInfos", 25L), (finding.Check, finding.Table!, finding.Index!, finding.Count));
        Assert.Equal(copies.Take(ImportFinding.MaxPrimaryKeySamples), finding.PrimaryKeySamples);
    }

    [Fact]
    public async Task Inspect_TableWithoutRowId_CountsAllIdsDifferingInCase()
    {
        var path = _fixture.Copy(_fixture.Small);
        var copies = AddIdsDifferingInCase(path);
        var create = Assert.Single(Query(path, "SELECT sql FROM sqlite_master WHERE name = 'TrickplayInfos'"));
        Execute(
            path,
            create.Replace("\"TrickplayInfos\"", "\"TrickplayInfosWithoutRowId\"", StringComparison.Ordinal) + " WITHOUT ROWID; "
            + "INSERT INTO TrickplayInfosWithoutRowId SELECT * FROM TrickplayInfos; "
            + "DROP TABLE TrickplayInfos; "
            + "ALTER TABLE TrickplayInfosWithoutRowId RENAME TO TrickplayInfos");

        var inspection = await InspectAsync(path);

        // The table is read in key order, which puts every id right before its lower-case copy.
        var finding = Assert.Single(inspection.Findings);
        Assert.Equal((nameof(PreflightCheck.DuplicateKeyAfterConversion), "TrickplayInfos", "PK_TrickplayInfos", 25L), (finding.Check, finding.Table!, finding.Index!, finding.Count));
        Assert.Equal(ImportFinding.MaxPrimaryKeySamples, finding.PrimaryKeySamples.Count);
        Assert.Subset(copies.ToHashSet(), finding.PrimaryKeySamples.ToHashSet());
    }

    [Theory]
    [InlineData("RowId")]
    [InlineData("rowid", "_ROWID_", "Oid")]
    public async Task Inspect_ColumnsHidingTheRowId_CountAllIdsDifferingInCase(params string[] names)
    {
        var path = _fixture.Copy(_fixture.Small);
        var copies = AddIdsDifferingInCase(path);

        // The same value in every row: reading a row again by one of these names would read the first row.
        Execute(path, string.Concat(names.Select(n => $"ALTER TABLE TrickplayInfos ADD COLUMN {n} INTEGER; UPDATE TrickplayInfos SET {n} = 1; ")));

        var inspection = await InspectAsync(path);

        Assert.Equal(
            names.Order(StringComparer.Ordinal).Select(n => (nameof(PreflightCheck.UnknownColumn), n)),
            inspection.Findings.Where(f => f.Check != nameof(PreflightCheck.DuplicateKeyAfterConversion)).Select(f => (f.Check, f.Column!)));
        var finding = Assert.Single(inspection.Findings, f => f.Check == nameof(PreflightCheck.DuplicateKeyAfterConversion));
        Assert.Equal(("TrickplayInfos", "PK_TrickplayInfos", 25L), (finding.Table!, finding.Index!, finding.Count));
        Assert.Equal(copies.Take(ImportFinding.MaxPrimaryKeySamples), finding.PrimaryKeySamples);
    }

    private static string Last(string table) => $"rowid = (SELECT max(rowid) FROM {table})";

    // Copies 25 TrickplayInfos rows with the item id in lower case, and returns the primary keys of the copies in rowid order.
    private static List<string> AddIdsDifferingInCase(string path)
    {
        Execute(
            path,
            "INSERT INTO TrickplayInfos (ItemId, Width, Height, TileWidth, TileHeight, ThumbnailCount, Interval, Bandwidth) "
            + "SELECT lower(ItemId), Width, Height, TileWidth, TileHeight, ThumbnailCount, Interval, Bandwidth FROM TrickplayInfos WHERE ItemId <> lower(ItemId) ORDER BY rowid LIMIT 25");
        var copies = Query(path, "SELECT ItemId || '|' || Width FROM TrickplayInfos WHERE ItemId = lower(ItemId) ORDER BY rowid");
        Assert.Equal(25, copies.Count);
        return copies;
    }

    private static void Execute(string path, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False;Foreign Keys=False");
        connection.Open();
        using var command = connection.CreateCommand();
#pragma warning disable CA2100 // The statements are constants of this class.
        command.CommandText = sql;
#pragma warning restore CA2100
        command.ExecuteNonQuery();
    }

    private static List<string> Query(string path, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
#pragma warning disable CA2100 // The statements are constants of this class.
        command.CommandText = sql;
#pragma warning restore CA2100
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static async Task<SqliteInspection> InspectAsync(string path, Func<string, ulong>? keyHash = null)
    {
        var snapshot = await SqliteSnapshotWriter.WriteAsync(path, path + ".snapshot", TestContext.Current.CancellationToken);
        var inspector = new SqliteSourceInspector(
            _model,
            SqliteSourceInspector.GetSchemaMigrationIds(),
            [SqliteSourceFixture.CodeMigrationId],
            Version.Parse(SqliteSourceFixture.ServerVersion),
            keyHash ?? ConvertedKeySet.Hash);
        await using var connection = await SqliteSourceInspector.OpenReadOnlyAsync(snapshot.Path, TestContext.Current.CancellationToken);
        return await inspector.InspectAsync(connection, TestContext.Current.CancellationToken);
    }
}
