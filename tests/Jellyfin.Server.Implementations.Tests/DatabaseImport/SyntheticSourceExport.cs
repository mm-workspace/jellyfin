using System;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Database.Testing.Synthetic;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.DatabaseImport;

/// <summary>
/// Writes a synthetic SQLite source for the import runs outside the test process.
/// </summary>
public class SyntheticSourceExport
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

        var path = Path.Combine(output, "data", "jellyfin.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await SyntheticLibrary.CreateSqliteDatabaseAsync(path, options, [], TestContext.Current.CancellationToken);
    }
}
