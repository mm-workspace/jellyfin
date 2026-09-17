using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
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
    public async Task Initialise_PooledConnections_KeepJitOff()
    {
        var builder = new NpgsqlConnectionStringBuilder(ServerConnectionString()) { MaxPoolSize = 1 };
        var (provider, options) = CreateOptions(builder.ConnectionString);
        for (var i = 0; i < 3; i++)
        {
            await using var context = CreateContext(provider, options);
            Assert.Equal("off", await ScalarAsync(context, "SHOW jit"));
        }
    }

    [Fact]
    public async Task Initialise_JitServer_LeavesTheServerSetting()
    {
        var builder = new NpgsqlConnectionStringBuilder(ServerConnectionString());
        await using var admin = new NpgsqlConnection(builder.ConnectionString);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        await using var serverSetting = new NpgsqlCommand("SELECT boot_val FROM pg_settings WHERE name = 'jit'", admin);
        var expected = (string)(await serverSetting.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;

        var (provider, options) = CreateOptions(builder.ConnectionString, ("jit", "server"));
        await using var context = CreateContext(provider, options);
        Assert.Equal(expected, await ScalarAsync(context, "SHOW jit"));
    }

    [Fact]
    public async Task Initialise_PasswordFile_IsReadWhenAConnectionOpens()
    {
        var builder = new NpgsqlConnectionStringBuilder(ServerConnectionString());
        var password = builder.Password;
        Assert.SkipWhen(string.IsNullOrEmpty(password), "The test connection string has no password.");
        builder.Password = null;
        var path = Path.Combine(Path.GetTempPath(), "jellyfin-pg-password-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(path, password + "\n", TestContext.Current.CancellationToken);
        try
        {
            var (provider, options) = CreateOptions(builder.ConnectionString, ("password-file", path));
            await using var context = CreateContext(provider, options);

            Assert.Equal("1", await ScalarAsync(context, "SELECT 1"));
            Assert.Null(new NpgsqlConnectionStringBuilder(context.Database.GetDbConnection().ConnectionString).Password);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Initialise_PasswordFileWithWrongPassword_ErrorDoesNotContainPassword()
    {
        const string WrongPassword = "definitely-Wrong-Password-43";
        var builder = new NpgsqlConnectionStringBuilder(ServerConnectionString()) { Password = WrongPassword };
        var accepted = await ServerAcceptsAsync(builder.ConnectionString);
        Assert.False(accepted && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true", "The CI PostgreSQL server must use password authentication.");
        Assert.SkipWhen(accepted, "The PostgreSQL server accepts any password (trust authentication).");
        builder.Password = null;
        var path = Path.Combine(Path.GetTempPath(), "jellyfin-pg-password-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(path, WrongPassword, TestContext.Current.CancellationToken);
        try
        {
            var (provider, options) = CreateOptions(builder.ConnectionString, ("password-file", path));
            await using var context = CreateContext(provider, options);

            var exception = await Assert.ThrowsAnyAsync<Exception>(() => ScalarAsync(context, "SELECT 1"));

            Assert.Contains(Chain(exception), e => e is PostgresException { SqlState: PostgresErrorCodes.InvalidPassword });
            Assert.All(Chain(exception), e => Assert.DoesNotContain(WrongPassword, e.Message, StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Initialise_WrongPassword_ErrorDoesNotContainPassword()
    {
        const string WrongPassword = "definitely-Wrong-Password-42";
        var builder = new NpgsqlConnectionStringBuilder(ServerConnectionString()) { Password = WrongPassword };

        // A server with trust authentication accepts any password, so there is no error to check. CI must not do that.
        var accepted = await ServerAcceptsAsync(builder.ConnectionString);
        Assert.False(accepted && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true", "The CI PostgreSQL server must use password authentication.");
        Assert.SkipWhen(accepted, "The PostgreSQL server accepts any password (trust authentication).");
        await using var context = CreateContext(builder.ConnectionString);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => ScalarAsync(context, "SELECT 1"));

        Assert.Contains(Chain(exception), e => e is PostgresException { SqlState: PostgresErrorCodes.InvalidPassword });
        Assert.All(Chain(exception), e => Assert.DoesNotContain(WrongPassword, e.Message, StringComparison.Ordinal));
    }

    private static async Task<bool> ServerAcceptsAsync(string connectionString)
    {
        // Plain Npgsql, so a provider that dropped the password fails the test instead of skipping it.
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        try
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            return true;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.InvalidPassword)
        {
            return false;
        }
    }

    private static IEnumerable<Exception> Chain(Exception exception)
    {
        for (Exception? e = exception; e is not null; e = e.InnerException)
        {
            yield return e;
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
        var (provider, options) = CreateOptions(connectionString);
        return CreateContext(provider, options);
    }

    private static JellyfinDbContext CreateContext(PostgreSqlDatabaseProvider provider, DbContextOptions<JellyfinDbContext> options)
        => new(options, NullLogger<JellyfinDbContext>.Instance, provider, new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));

    private static (PostgreSqlDatabaseProvider Provider, DbContextOptions<JellyfinDbContext> Options) CreateOptions(string connectionString, params (string Key, string Value)[] providerOptions)
    {
        var provider = new PostgreSqlDatabaseProvider(null!, NullLogger<PostgreSqlDatabaseProvider>.Instance);
        var builder = new DbContextOptionsBuilder<JellyfinDbContext>();
        var customOptions = new CustomDatabaseOptions { PluginName = string.Empty, PluginAssembly = string.Empty, ConnectionString = connectionString };
        foreach (var (key, value) in providerOptions)
        {
            customOptions.Options.Add(new CustomDatabaseOption { Key = key, Value = value });
        }

        provider.Initialise(builder, new DatabaseConfigurationOptions { DatabaseType = "Jellyfin-PostgreSQL", CustomProviderOptions = customOptions });
        return (provider, builder.Options);
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
