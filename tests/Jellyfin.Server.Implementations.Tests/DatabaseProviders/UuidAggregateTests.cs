using System;
using System.Collections.Generic;
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
/// Aggregates over ids pick the same id on every database, in the order of the canonical text form.
/// </summary>
public sealed class UuidAggregateTests : IDisposable
{
    private const string MovieType = "MediaBrowser.Controller.Entities.Movies.Movie";

    private readonly ITestDatabase _database = TestDatabase.Create(new TestDatabaseOptions { ApplicationPaths = new Mock<IApplicationPaths>().Object });
    private readonly List<BaseItemEntity> _items = [];

    public UuidAggregateTests()
    {
        var random = new Random(7);
        var bytes = new byte[16];
        for (var i = 0; i < 300; i++)
        {
            random.NextBytes(bytes);
            _items.Add(new BaseItemEntity
            {
                Id = new Guid(bytes),
                Type = MovieType,
                Name = "Movie " + i,
                PresentationUniqueKey = i % 10 == 9 ? null : "group-" + (i % 10),
                PrimaryVersionId = i % 3 == 0 ? Guid.Empty : null
            });
        }

        using var context = _database.CreateDbContext();
        context.BaseItems.AddRange(_items);
        context.SaveChanges();
    }

    [Fact]
    public async Task MinAndMax_PerGroup_MatchCanonicalTextOrder()
    {
        await using var context = _database.CreateDbContext();

        var actual = await context.BaseItems
            .Where(e => e.Type == MovieType)
            .GroupBy(e => e.PresentationUniqueKey)
            .Select(g => new { g.Key, Min = g.Min(e => e.Id), Max = g.Max(e => e.Id) })
            .ToListAsync(TestContext.Current.CancellationToken);

        var expected = _items.GroupBy(e => e.PresentationUniqueKey).ToDictionary(g => g.Key ?? string.Empty, g => (Min: CanonicalMin(g.Select(e => e.Id)), Max: CanonicalMax(g.Select(e => e.Id))));
        Assert.Equal(expected.Count, actual.Count);
        Assert.All(actual, group => Assert.Equal(expected[group.Key ?? string.Empty], (group.Min, group.Max)));
    }

    [Fact]
    public async Task Min_WithPredicateAndFallback_PicksTheSameIdAsInMemory()
    {
        await using var context = _database.CreateDbContext();

        var actual = await context.BaseItems
            .Where(e => e.Type == MovieType)
            .GroupBy(e => e.PresentationUniqueKey)
            .Select(g => g.Where(e => !e.PrimaryVersionId.HasValue).Min(e => (Guid?)e.Id) ?? g.Min(e => (Guid?)e.Id))
            .ToListAsync(TestContext.Current.CancellationToken);

        var expected = _items.GroupBy(e => e.PresentationUniqueKey)
            .Select(g => (Guid?)(g.Any(e => !e.PrimaryVersionId.HasValue) ? CanonicalMin(g.Where(e => !e.PrimaryVersionId.HasValue).Select(e => e.Id)) : CanonicalMin(g.Select(e => e.Id))));
        Assert.Equal(expected.Order(), actual.Order());
    }

    [Fact]
    public async Task Min_UsedAsSubquery_SelectsItems()
    {
        await using var context = _database.CreateDbContext();
        var representatives = context.BaseItems.Where(e => e.Type == MovieType).GroupBy(e => e.PresentationUniqueKey).Select(g => g.Min(e => e.Id));

        var count = await context.BaseItems.CountAsync(e => representatives.Contains(e.Id), TestContext.Current.CancellationToken);

        Assert.Equal(10, count);
    }

    public void Dispose() => _database.Dispose();

    private static Guid CanonicalMin(IEnumerable<Guid> ids) => ids.Aggregate(CanonicalMin2);

    private static Guid CanonicalMin2(Guid left, Guid right) => string.CompareOrdinal(left.ToString(), right.ToString()) <= 0 ? left : right;

    private static Guid CanonicalMax(IEnumerable<Guid> ids) => ids.Aggregate((left, right) => string.CompareOrdinal(left.ToString(), right.ToString()) >= 0 ? left : right);
}
