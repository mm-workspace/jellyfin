using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.PostgreSQL;
using Jellyfin.Database.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.DatabaseProviders.PostgreSql;

[Trait("Provider", "PostgreSql")]
public class PostgreSqlInitialiseTests
{
    [Fact]
    public async Task Initialise_Session_UsesUtcAndApplicationName()
    {
        await using var context = CreateContext(ServerConnectionString());

        Assert.Equal("UTC", await ScalarAsync(context, "SHOW TimeZone"));
        Assert.StartsWith("Jellyfin/", await ScalarAsync(context, "SELECT current_setting('application_name')"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Initialise_LoopbackHostWithoutSslMode_ConnectsToServerWithoutTls()
    {
        var builder = new NpgsqlConnectionStringBuilder(ServerConnectionString());
        builder.Remove("SSL Mode");
        await using var context = CreateContext(builder.ConnectionString);

        Assert.Equal("off", await ScalarAsync(context, "SELECT CASE WHEN ssl THEN 'on' ELSE 'off' END FROM pg_stat_ssl WHERE pid = pg_backend_pid()"));
    }

    [Fact]
    public async Task Initialise_WrongPassword_ErrorDoesNotContainPassword()
    {
        const string WrongPassword = "definitely-Wrong-Password-42";
        var builder = new NpgsqlConnectionStringBuilder(ServerConnectionString()) { Password = WrongPassword };
        await using var context = CreateContext(builder.ConnectionString);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => ScalarAsync(context, "SELECT 1"));

        for (Exception? e = exception; e is not null; e = e.InnerException)
        {
            Assert.DoesNotContain(WrongPassword, e.Message, StringComparison.Ordinal);
        }
    }

    private static string ServerConnectionString()
    {
        var connectionString = TestDatabase.PostgreSqlConnectionString;
        Assert.SkipWhen(connectionString is null, $"{TestDatabase.PostgreSqlConnectionStringEnvironmentVariable} is not set.");
        return connectionString;
    }

    private static JellyfinDbContext CreateContext(string connectionString)
    {
        var provider = new PostgreSqlDatabaseProvider(null!, NullLogger<PostgreSqlDatabaseProvider>.Instance);
        var builder = new DbContextOptionsBuilder<JellyfinDbContext>();
        provider.Initialise(builder, new DatabaseConfigurationOptions
        {
            DatabaseType = "Jellyfin-PostgreSQL",
            CustomProviderOptions = new CustomDatabaseOptions { PluginName = string.Empty, PluginAssembly = string.Empty, ConnectionString = connectionString }
        });

        return new JellyfinDbContext(builder.Options, NullLogger<JellyfinDbContext>.Instance, provider, new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Test statements are constants.")]
    private static async Task<string> ScalarAsync(JellyfinDbContext context, string sql)
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken), System.Globalization.CultureInfo.InvariantCulture)!;
    }
}
