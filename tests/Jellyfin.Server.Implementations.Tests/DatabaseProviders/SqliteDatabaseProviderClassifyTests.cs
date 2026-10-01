using System;
using System.IO;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.DatabaseProviders;

/// <summary>
/// Classification of the errors SQLite really raises, rather than of exceptions the test built itself.
/// </summary>
[Trait("Provider", "Sqlite")]
public sealed class SqliteDatabaseProviderClassifyTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), "jellyfin-classify-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly IJellyfinDatabaseProvider _provider = new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance);

    public SqliteDatabaseProviderClassifyTests()
    {
        using var context = CreateContext();
        context.Database.EnsureCreated();
    }

    [Fact]
    public void ClassifyException_DatabaseHeldByAnotherConnection_IsTransient()
    {
        using var holder = new SqliteConnection(ConnectionString());
        holder.Open();

        // BEGIN IMMEDIATE: this connection holds the database write lock until it is rolled back.
        using var holding = holder.BeginTransaction();

        // A short timeout turns the wait for the write lock into an error instead of a wait for the test's lifetime.
        using var blocked = new SqliteConnection(ConnectionString("Default Timeout=1"));
        blocked.Open();

        var exception = Assert.Throws<SqliteException>(() => blocked.BeginTransaction());

        Assert.Equal(5, exception.SqliteErrorCode);
        Assert.Equal(DatabaseErrorKind.Transient, _provider.ClassifyException(exception));
    }

    [Fact]
    public void ClassifyException_DuplicatePrimaryKey_IsUniqueViolation()
    {
        var itemValueId = Guid.NewGuid();
        using (var context = CreateContext())
        {
            context.ItemValues.Add(NewItemValue(itemValueId, "First"));
            context.SaveChanges();
        }

        using var duplicating = CreateContext();
        duplicating.ItemValues.Add(NewItemValue(itemValueId, "Second"));

        var exception = Assert.Throws<DbUpdateException>(() => duplicating.SaveChanges());

        Assert.Equal(1555, Assert.IsType<SqliteException>(exception.InnerException).SqliteExtendedErrorCode);
        Assert.Equal(DatabaseErrorKind.UniqueViolation, _provider.ClassifyException(exception));
    }

    [Fact]
    public void ClassifyException_DuplicateUniqueIndex_IsUniqueViolation()
    {
        using (var context = CreateContext())
        {
            context.ItemValues.Add(NewItemValue(Guid.NewGuid(), "Shared"));
            context.SaveChanges();
        }

        using var duplicating = CreateContext();
        duplicating.ItemValues.Add(NewItemValue(Guid.NewGuid(), "Shared"));

        var exception = Assert.Throws<DbUpdateException>(() => duplicating.SaveChanges());

        Assert.Equal(2067, Assert.IsType<SqliteException>(exception.InnerException).SqliteExtendedErrorCode);
        Assert.Equal(DatabaseErrorKind.UniqueViolation, _provider.ClassifyException(exception));
    }

    [Fact]
    public void ClassifyException_ErrorThatIsNeither_IsNone()
    {
        using var context = CreateContext();

        var exception = Assert.Throws<SqliteException>(() => context.Database.ExecuteSqlRaw("SELECT * FROM \"NoSuchTable\""));

        Assert.Equal(1, exception.SqliteErrorCode);
        Assert.Equal(DatabaseErrorKind.None, _provider.ClassifyException(exception));
    }

    [Fact]
    public void ClassifyException_ExceptionFromSomewhereElse_IsNone()
    {
        Assert.Equal(DatabaseErrorKind.None, _provider.ClassifyException(new InvalidOperationException("not from the database")));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_databasePath);
    }

    private static ItemValue NewItemValue(Guid itemValueId, string value) => new()
    {
        ItemValueId = itemValueId,
        Type = ItemValueType.Genre,
        Value = value,
        CleanValue = value.ToLowerInvariant()
    };

    private string ConnectionString(string connectionOptions = "") => $"Data Source={_databasePath};Pooling=false;{connectionOptions}";

    private JellyfinDbContext CreateContext() => new(
        new DbContextOptionsBuilder<JellyfinDbContext>().UseSqlite(ConnectionString()).Options,
        NullLogger<JellyfinDbContext>.Instance,
        _provider,
        new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
}
