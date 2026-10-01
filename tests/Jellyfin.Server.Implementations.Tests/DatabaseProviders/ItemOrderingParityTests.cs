using System;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.DatabaseProviders;

/// <summary>
/// The same library, seeded on SQLite and on PostgreSQL, comes back in the same order for every ordering the item
/// repository can build.
/// </summary>
/// <remarks>
/// Both databases are read in the same test, so no expected order is written down: SQLite is the definition. The
/// seeded library leaves a value out of every column an ordering reads, and its sort names and clean names are unique,
/// which are the two keys an ordering can end in, so each ordering puts the rows in exactly one order.
/// </remarks>
[Trait("Provider", "PostgreSql")]
public sealed class ItemOrderingParityTests : IClassFixture<SeededItemLibraries>
{
    private readonly SeededItemLibraries _libraries;

    public ItemOrderingParityTests(SeededItemLibraries libraries)
    {
        _libraries = libraries;
    }

    public static TheoryData<ItemSortBy, SortOrder> SortKeys
    {
        get
        {
            var data = new TheoryData<ItemSortBy, SortOrder>();

            // Random is the one ordering that is not meant to repeat itself.
            foreach (var sortBy in Enum.GetValues<ItemSortBy>().Where(s => s != ItemSortBy.Random))
            {
                data.Add(sortBy, SortOrder.Ascending);
                data.Add(sortBy, SortOrder.Descending);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(SortKeys))]
    public void GetItemIdsList_Ordering_MatchesSqlite(ItemSortBy sortBy, SortOrder sortOrder)
    {
        var expected = _libraries.SqliteIds(sortBy, sortOrder, startIndex: null, limit: null);
        Assert.NotEmpty(expected);

        Assert.Equal(expected, _libraries.PostgreSqlIds(sortBy, sortOrder, startIndex: null, limit: null));
    }

    [Theory]
    [MemberData(nameof(SortKeys))]
    public void GetItemIdsList_PageOfAnOrdering_MatchesSqlite(ItemSortBy sortBy, SortOrder sortOrder)
    {
        // A page is skipped rows away from the first one, so an order that differs returns different rows.
        const int StartIndex = 4;
        const int Limit = 5;
        var expected = _libraries.SqliteIds(sortBy, sortOrder, StartIndex, Limit);
        Assert.Equal(Limit, expected.Count);

        Assert.Equal(expected, _libraries.PostgreSqlIds(sortBy, sortOrder, StartIndex, Limit));
    }
}
