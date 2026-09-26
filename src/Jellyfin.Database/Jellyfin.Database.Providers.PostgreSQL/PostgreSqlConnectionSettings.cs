namespace Jellyfin.Database.Providers.PostgreSQL;

/// <summary>
/// The PostgreSQL connection settings read from the database configuration.
/// </summary>
/// <param name="ConnectionString">The connection string. It holds the password only when the configuration put one there.</param>
/// <param name="PasswordFile">The full path of the file the password is read from whenever a connection opens, or <c>null</c>.</param>
/// <param name="CommandTimeout">The command timeout in seconds.</param>
/// <param name="EnableSensitiveDataLogging">Whether EF Core may log parameter values.</param>
/// <param name="Description">A description of the connection that is safe to log.</param>
/// <param name="DisableJit">Whether each connection turns off JIT compilation.</param>
/// <param name="HashMemoryMegabytes">The memory each connection tries to let a hash table use, or <c>null</c> to keep the server's limit.</param>
internal sealed record PostgreSqlConnectionSettings(string ConnectionString, string? PasswordFile, int CommandTimeout, bool EnableSensitiveDataLogging, string Description, bool DisableJit, int? HashMemoryMegabytes);
