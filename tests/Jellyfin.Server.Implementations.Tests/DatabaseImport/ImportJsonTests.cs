using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Server.Implementations.DatabaseImport;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.DatabaseImport;

public class ImportJsonTests
{
    [Fact]
    public async Task Manifest_RoundTrips()
    {
        var manifest = CreateManifest();

        var read = await ImportJson.ReadManifestAsync(await WriteAsync(manifest), TestContext.Current.CancellationToken);

        Assert.Equal(manifest.ServerVersion, read.ServerVersion);
        Assert.Equal(manifest.ModelFingerprint, read.ModelFingerprint);
        Assert.Equal(manifest.SnapshotSha256, read.SnapshotSha256);
        Assert.Equal(manifest.SourceFiles, read.SourceFiles);
        Assert.Equal(manifest.Tables.Select(t => (t.Name, t.RowCount, t.ContentHash)), read.Tables.Select(t => (t.Name, t.RowCount, t.ContentHash)));
        Assert.Equal(manifest.Tables[0].TimestampSentinels, read.Tables[0].TimestampSentinels);
        var warning = Assert.Single(read.Warnings);
        Assert.Equal(manifest.Warnings[0] with { PrimaryKeySamples = [] }, warning with { PrimaryKeySamples = [] });
        Assert.Equal(manifest.Warnings[0].PrimaryKeySamples, warning.PrimaryKeySamples);
    }

    [Fact]
    public async Task ReadManifest_OtherFormatVersion_Throws()
    {
        var stream = await WriteAsync(CreateManifest() with { FormatVersion = ImportManifest.CurrentFormatVersion + 1 });

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => ImportJson.ReadManifestAsync(stream, TestContext.Current.CancellationToken));
        Assert.Contains("preflight again", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"formatVersion\":1,\"serverVersion\":\"10.12.0.0\"}")]
    [InlineData("[1,2]")]
    public async Task ReadManifest_Invalid_Throws(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        await Assert.ThrowsAsync<InvalidDataException>(() => ImportJson.ReadManifestAsync(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadManifest_UnknownProperty_Throws()
    {
        var json = Encoding.UTF8.GetString((await WriteAsync(CreateManifest())).ToArray());
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json.Replace("{", "{\"extra\":1,", StringComparison.Ordinal)));

        await Assert.ThrowsAsync<InvalidDataException>(() => ImportJson.ReadManifestAsync(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Finding_KeepsAtMostTenPrimaryKeys()
    {
        var finding = ImportFinding.Create("Orphan", ImportFindingSeverity.Error, 25, "UserData", primaryKeySamples: Enumerable.Range(0, 25).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        Assert.Equal(ImportFinding.MaxPrimaryKeySamples, finding.PrimaryKeySamples.Count);
        Assert.Equal(25, finding.Count);
    }

    [Fact]
    public void Finding_HasNoPlaceForRowValues()
    {
        // Reports are shared when asking for help, so they only say where a problem is.
        Assert.Equal(
            ["Check", "Column", "Count", "Index", "PrimaryKeySamples", "Severity", "Table"],
            typeof(ImportFinding).GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Report_WritesFindingsWithoutDerivedProperties()
    {
        var report = new ImportReport(
            ImportReport.CurrentFormatVersion,
            ImportStep.Preflight,
            "10.12.0.0",
            new DateTime(2026, 9, 17, 10, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 17, 10, 1, 0, DateTimeKind.Utc),
            [ImportFinding.Create("Orphan", ImportFindingSeverity.Error, 1, "UserData", "ItemId", "FK_UserData_BaseItems_ItemId", ["4"])]);
        using var stream = new MemoryStream();

        await ImportJson.WriteAsync(stream, report, TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(stream.ToArray());
        Assert.False(report.Succeeded);
        Assert.False(document.RootElement.TryGetProperty("succeeded", out _));
        Assert.Equal("Preflight", document.RootElement.GetProperty("step").GetString());
        Assert.Equal("Error", document.RootElement.GetProperty("findings")[0].GetProperty("severity").GetString());
    }

    private static ImportManifest CreateManifest()
        => new(
            ImportManifest.CurrentFormatVersion,
            "10.12.0.0",
            "fingerprint",
            "0123456789abcdef",
            [new ImportSourceFile("jellyfin.db", 4096, new DateTime(2026, 9, 17, 9, 0, 0, DateTimeKind.Utc)), new ImportSourceFile("jellyfin.db-wal", null, null)],
            [new ImportTableSummary("ActivityLogs", 2, "2:00000000000000000000000000000001", [new ImportTimestampSentinels("DateCreated", 1, 0)])],
            [ImportFinding.Create("UnknownTable", ImportFindingSeverity.Warning, 1, "PluginTable")]);

    private static async Task<MemoryStream> WriteAsync(ImportManifest manifest)
    {
        var stream = new MemoryStream();
        await ImportJson.WriteAsync(stream, manifest, TestContext.Current.CancellationToken);
        stream.Position = 0;
        return stream;
    }
}
