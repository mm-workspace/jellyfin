using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Database.Testing.Synthetic;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.DatabaseImport.Sqlite;

/// <summary>
/// Creates the synthetic SQLite sources once for a test class; tests work on copies.
/// </summary>
public sealed class SqliteSourceFixture : IAsyncLifetime
{
    public const string CodeMigrationId = "20990101000000_SyntheticCodeMigration";

    public const string ServerVersion = "10.12.0.0";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "jf-preflight-" + Guid.NewGuid().ToString("N"));

    public string Small => Path.Combine(_directory, "small.db");

    public string SmallEdge => Path.Combine(_directory, "small-edge.db");

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        KeyValuePair<string, string>[] history = [new(CodeMigrationId, ServerVersion)];
        await SyntheticLibrary.CreateSqliteDatabaseAsync(Small, SyntheticLibraryOptions.Small, history);
        await SyntheticLibrary.CreateSqliteDatabaseAsync(SmallEdge, SyntheticLibraryOptions.SmallEdge, history);
    }

    /// <summary>
    /// Copies a source into a new directory.
    /// </summary>
    /// <param name="source">The source to copy.</param>
    /// <returns>The path of the copy.</returns>
    public string Copy(string source)
    {
        var directory = Path.Combine(_directory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "jellyfin.db");
        File.Copy(source, path);
        return path;
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, true);
        return ValueTask.CompletedTask;
    }
}
