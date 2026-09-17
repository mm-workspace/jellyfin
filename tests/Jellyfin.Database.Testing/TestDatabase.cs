using System;

namespace Jellyfin.Database.Testing;

/// <summary>
/// Creates the database tests run against.
/// </summary>
public static class TestDatabase
{
    /// <summary>
    /// The environment variable selecting the database provider the tests run against.
    /// </summary>
    public const string ProviderEnvironmentVariable = "JELLYFIN_TEST_DB";

    /// <summary>
    /// The value of <see cref="ProviderEnvironmentVariable"/> selecting an in-memory SQLite database. This is the default.
    /// </summary>
    public const string Sqlite = "sqlite";

    /// <summary>
    /// Gets the name of the database provider selected through <see cref="ProviderEnvironmentVariable"/>.
    /// </summary>
    public static string SelectedProvider
    {
        get
        {
            var value = Environment.GetEnvironmentVariable(ProviderEnvironmentVariable);
            return string.IsNullOrWhiteSpace(value) ? Sqlite : value.Trim().ToLowerInvariant();
        }
    }

    /// <summary>
    /// Creates a database for a test using the selected database provider.
    /// </summary>
    /// <param name="options">The options, or <c>null</c> for the defaults.</param>
    /// <returns>The database.</returns>
    public static ITestDatabase Create(TestDatabaseOptions? options = null)
    {
        options ??= new TestDatabaseOptions();
        return SelectedProvider switch
        {
            Sqlite => new SqliteInMemoryTestDatabase(options),
            var other => throw new InvalidOperationException(
                $"The {ProviderEnvironmentVariable} environment variable selects the unsupported database provider '{other}'. Supported values: {Sqlite}.")
        };
    }
}
