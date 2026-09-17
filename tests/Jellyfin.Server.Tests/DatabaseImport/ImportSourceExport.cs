using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Emby.Server.Implementations.Serialization;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Testing.Synthetic;
using Jellyfin.Server.DatabaseImport;
using Jellyfin.Server.Migrations;
using MediaBrowser.Model.Configuration;
using Xunit;

namespace Jellyfin.Server.Tests.DatabaseImport;

/// <summary>
/// Writes the directories of a set-up server on a synthetic SQLite library, for import runs with the real server and pgloader.
/// </summary>
public class ImportSourceExport
{
    private const string OutputVariable = "JELLYFIN_SYNTHETIC_OUT";

    private const string SizeVariable = "JELLYFIN_SYNTHETIC_SIZE";

    [Fact]
    public async Task Export()
    {
        var output = Environment.GetEnvironmentVariable(OutputVariable);
        var size = Environment.GetEnvironmentVariable(SizeVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(output) || string.IsNullOrEmpty(size), $"{OutputVariable} and {SizeVariable} are not set.");

        var options = size switch
        {
            "S" => SyntheticLibraryOptions.Small,
            "S-edge" => SyntheticLibraryOptions.SmallEdge,
            "L" => SyntheticLibraryOptions.Large,
            _ => throw new ArgumentException($"{SizeVariable} must be S, S-edge or L.")
        };

        // The history of a server of this build that ran all of its migrations.
        var version = typeof(PostgreSqlImportCommand).Assembly.GetName().Version!.ToString();
        var data = Path.Combine(output, "data");
        var config = Path.Combine(output, "config");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(config);
        await SyntheticLibrary.CreateSqliteDatabaseAsync(
            Path.Combine(data, "jellyfin.db"),
            options,
            JellyfinMigrationService.GetCodeMigrationIds().Select(id => new System.Collections.Generic.KeyValuePair<string, string>(id, version)),
            TestContext.Current.CancellationToken);

        var serializer = new MyXmlSerializer();
        serializer.SerializeToFile(new ServerConfiguration { IsStartupWizardCompleted = true }, Path.Combine(config, "system.xml"));
        serializer.SerializeToFile(
            new DatabaseConfigurationOptions { DatabaseType = PostgreSqlImportCommand.SqliteDatabaseType, LockingBehavior = DatabaseLockingBehaviorTypes.NoLock },
            Path.Combine(config, "database.xml"));
    }
}
