using System;
using System.Linq;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Sqlite;
using Jellyfin.Database.Providers.Sqlite.ValueConverters;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.DatabaseProviders;

public sealed class DateTimeKindCoverageTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    [Fact]
    public void SqliteModel_EveryDateTimeProperty_UsesTheKindConverter()
    {
        using var context = new JellyfinDbContext(
            new DbContextOptionsBuilder<JellyfinDbContext>().UseSqlite(_connection).Options,
            NullLogger<JellyfinDbContext>.Instance,
            new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance),
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));

        var missing = context.Model.GetEntityTypes()
            .SelectMany(e => e.GetProperties().Select(p => (Entity: e, Property: p)))
            .Where(e => e.Property.ClrType == typeof(DateTime) || e.Property.ClrType == typeof(DateTime?))
            .Where(e => e.Property.GetValueConverter() is not DateTimeKindValueConverter)
            .Select(e => $"{e.Entity.ShortName()}.{e.Property.Name}")
            .ToArray();

        Assert.Empty(missing);
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}
