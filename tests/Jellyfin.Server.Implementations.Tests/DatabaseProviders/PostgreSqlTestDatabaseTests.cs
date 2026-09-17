using System;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.DatabaseProviders;

[Trait("Provider", "PostgreSql")]
public class PostgreSqlTestDatabaseTests
{
    [Fact]
    public async Task Create_Database_HasSchemaAndIsDroppedOnDispose()
    {
        var serverConnectionString = TestDatabase.PostgreSqlConnectionString;
        Assert.SkipWhen(serverConnectionString is null, $"{TestDatabase.PostgreSqlConnectionStringEnvironmentVariable} is not set.");

        string databaseName;
        await using (var database = new PostgreSqlTestDatabase(serverConnectionString, new TestDatabaseOptions()))
        {
            databaseName = database.DatabaseName;
            await using (var context = database.CreateDbContext())
            {
                context.ActivityLogs.Add(new ActivityLog("name", "type", Guid.NewGuid()));
                await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            await using (var context = database.CreateDbContext())
            {
                Assert.Equal(1, await context.ActivityLogs.CountAsync(TestContext.Current.CancellationToken));
            }
        }

        await using var connection = new NpgsqlConnection(serverConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_database WHERE datname = @name", connection);
        command.Parameters.AddWithValue("name", databaseName);
        Assert.Equal(0L, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }
}
