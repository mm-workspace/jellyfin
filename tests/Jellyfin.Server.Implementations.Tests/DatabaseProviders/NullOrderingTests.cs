using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Testing;
using MediaBrowser.Common.Configuration;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.DatabaseProviders;

/// <summary>
/// NULL sorts below every other value on every database, as it does on SQLite.
/// </summary>
public sealed class NullOrderingTests : IDisposable
{
    private const string MovieType = "MediaBrowser.Controller.Entities.Movies.Movie";
    private const string FolderType = "MediaBrowser.Controller.Entities.Folder";

    private static readonly Guid _first = Guid.Parse("10000000-0000-0000-0000-000000000000");
    private static readonly Guid _second = Guid.Parse("20000000-0000-0000-0000-000000000000");
    private static readonly Guid _third = Guid.Parse("30000000-0000-0000-0000-000000000000");
    private static readonly Guid _folder = Guid.Parse("40000000-0000-0000-0000-000000000000");

    private readonly ITestDatabase _database = TestDatabase.Create(new TestDatabaseOptions { ApplicationPaths = new Mock<IApplicationPaths>().Object });

    // The second movie has no user data and no parent, so every value read through either is NULL for it.
    public NullOrderingTests()
    {
        using var context = _database.CreateDbContext();
        var user = new User("test", "auth-provider", "reset-provider");
        context.Users.Add(user);
        context.BaseItems.AddRange(
            new BaseItemEntity { Id = _folder, Type = FolderType, Name = "folder", IsFolder = true },
            new BaseItemEntity { Id = _first, Type = MovieType, Name = "a", SortName = "b", ProductionYear = 2000, ParentId = _folder },
            new BaseItemEntity { Id = _second, Type = MovieType, Name = "b", SortName = null, ProductionYear = null },
            new BaseItemEntity { Id = _third, Type = MovieType, Name = "c", SortName = "a", ProductionYear = 1990, ParentId = _first });
        context.UserData.AddRange(
            new UserData { ItemId = _first, UserId = user.Id, CustomDataKey = "first", PlayCount = 2, Played = true, Item = null!, User = null! },
            new UserData { ItemId = _third, UserId = user.Id, CustomDataKey = "third", PlayCount = 0, Played = false, Item = null!, User = null! });
        context.SaveChanges();
    }

    [Fact]
    public async Task OrderBy_NullableValue_PutsNullFirst()
    {
        await using var context = _database.CreateDbContext();

        var ids = await Movies(context).OrderBy(e => e.ProductionYear).Select(e => e.Id).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal([_second, _third, _first], ids);
    }

    [Fact]
    public async Task OrderByDescending_NullableValue_PutsNullLast()
    {
        await using var context = _database.CreateDbContext();

        var ids = await Movies(context).OrderByDescending(e => e.ProductionYear).Select(e => e.Id).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal([_first, _third, _second], ids);
    }

    [Fact]
    public async Task ThenBy_NullableString_PutsNullFirst()
    {
        await using var context = _database.CreateDbContext();

        var ids = await Movies(context).OrderBy(e => e.Type).ThenBy(e => e.SortName).Select(e => e.Id).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal([_second, _third, _first], ids);
    }

    [Fact]
    public async Task OrderByDescending_BoxedKey_PutsNullLast()
    {
        await using var context = _database.CreateDbContext();

        // OrderMapper hands out keys as object.
        var ids = await Movies(context).OrderByDescending(e => (object?)e.SortName).Select(e => e.Id).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal([_first, _third, _second], ids);
    }

    [Fact]
    public async Task OrderByDescending_EmptySubquery_PutsNullLast()
    {
        await using var context = _database.CreateDbContext();

        var ids = await Movies(context)
            .OrderByDescending(e => context.BaseItems.Where(o => o.Id.Equals(e.Id) && o.ProductionYear > 1995).Max(o => o.ProductionYear))
            .ThenBy(e => e.Name)
            .Select(e => e.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal([_first, _second, _third], ids);
    }

    [Fact]
    public async Task OrderByDescending_MemberOfMissingRow_PutsNullLast()
    {
        await using var context = _database.CreateDbContext();

        // Boxed like OrderMapper's play count: a non-nullable member, but NULL for an item without user data.
        var ids = await Movies(context).OrderByDescending(e => (object)e.UserData!.FirstOrDefault()!.PlayCount).Select(e => e.Id).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal([_first, _third, _second], ids);
    }

    [Fact]
    public async Task OrderBy_ValueTypeMemberOfMissingRow_PutsNullFirst()
    {
        await using var context = _database.CreateDbContext();

        var ids = await Movies(context).OrderBy(e => e.UserData!.FirstOrDefault()!.PlayCount).Select(e => e.Id).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal([_second, _third, _first], ids);
    }

    [Fact]
    public async Task OrderBy_NegatedMemberOfMissingRow_PutsNullFirst()
    {
        await using var context = _database.CreateDbContext();

        var ids = await Movies(context).OrderBy(e => !e.UserData!.FirstOrDefault()!.Played).Select(e => e.Id).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal([_second, _first, _third], ids);
    }

    [Fact]
    public async Task OrderByDescending_MaxOfEmptySetOfValues_PutsNullLast()
    {
        await using var context = _database.CreateDbContext();

        var ids = await Movies(context).OrderByDescending(e => e.UserData!.Max(u => u.PlayCount)).Select(e => e.Id).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal([_first, _third, _second], ids);
    }

    [Fact]
    public async Task OrderBy_MemberOfMissingOptionalNavigation_PutsNullFirst()
    {
        await using var context = _database.CreateDbContext();

        var ids = await Movies(context).OrderBy(e => e.DirectParent!.IsFolder).Select(e => e.Id).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal([_second, _third, _first], ids);
    }

    [Fact]
    public void OrderBy_KeyThatCannotBeNull_IsNotRewritten()
    {
        using var context = _database.CreateDbContext();

        var sql = Movies(context).OrderBy(e => e.Type).ThenBy(e => e.Id).ToQueryString();

        Assert.DoesNotContain("CASE", sql, StringComparison.Ordinal);
    }

    public void Dispose() => _database.Dispose();

    private static IQueryable<BaseItemEntity> Movies(Database.Implementations.JellyfinDbContext context)
        => context.BaseItems.AsNoTracking().Where(e => e.Type == MovieType);
}
