namespace Jellyfin.Database.Providers.PostgreSQL;

/// <summary>
/// The PostgreSQL connection settings read from the database configuration.
/// </summary>
/// <param name="ConnectionString">The connection string, including the password.</param>
/// <param name="CommandTimeout">The command timeout in seconds.</param>
/// <param name="EnableSensitiveDataLogging">Whether EF Core may log parameter values.</param>
/// <param name="Description">A description of the connection that is safe to log.</param>
internal sealed record PostgreSqlConnectionSettings(string ConnectionString, int CommandTimeout, bool EnableSensitiveDataLogging, string Description);
