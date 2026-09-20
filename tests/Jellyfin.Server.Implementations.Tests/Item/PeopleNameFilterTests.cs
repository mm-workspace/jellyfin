using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Emby.Server.Implementations.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Server.Implementations.Item;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Persistence;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Item;

/// <summary>
/// Credits keep the case a metadata provider wrote them in, so the name filters behind the person list and
/// its letter picker have to match without regard to it, and to match a wildcard a user typed literally.
/// </summary>
public sealed class PeopleNameFilterTests : DbTestFixture
{
    private readonly PeopleRepository _people;

    public PeopleNameFilterTests()
    {
        using (var context = CreateDbContext())
        {
            foreach (var name in new[] { "alpha centauri", "Brad Pitt", "bob dylan", "Zoe Saldana", "b_x", "bux", "Istanbul Story" })
            {
                context.Peoples.Add(new People { Id = Guid.NewGuid(), Name = name, PersonType = "Actor" });
            }

            context.SaveChanges();
        }

        _people = new PeopleRepository(CreateDbContextFactory(), new ItemTypeLookup(), Mock.Of<IItemQueryHelpers>());
    }

    [Fact]
    public void NameRange_TakesTheLetterOfANameWhateverItsCase()
    {
        var names = Names(new InternalPeopleQuery { NameStartsWithOrGreater = "b", NameLessThan = "c" });

        AssertNames(["Brad Pitt", "bob dylan", "b_x", "bux"], names);
    }

    [Fact]
    public void NameLessThan_LeavesOutALaterNameWrittenInCapitals()
    {
        var names = Names(new InternalPeopleQuery { NameLessThan = "c" });

        Assert.DoesNotContain("Zoe Saldana", names);
        Assert.Contains("alpha centauri", names);
    }

    [Fact]
    public void NameContains_MatchesWhateverTheCaseOfTheCurrentCulture()
    {
        // Turkish maps a dotless lowercase i onto the capital I, so a filter folded in the current culture
        // stops matching the very names the database folds the invariant way.
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
        try
        {
            AssertNames(["Istanbul Story"], Names(new InternalPeopleQuery { NameContains = "istanbul" }));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void NameContains_MatchesTheOtherCaseOfALetter()
    {
        AssertNames(["Brad Pitt"], Names(new InternalPeopleQuery { NameContains = "BRAD" }));
    }

    [Fact]
    public void NameStartsWith_MatchesAnUnderscoreTypedByAUser()
    {
        // The provider turns StartsWith into an escaped LIKE, so the underscore stands for itself.
        AssertNames(["b_x"], Names(new InternalPeopleQuery { NameStartsWith = "b_" }));
    }

    [Fact]
    public void NameStartsWith_MatchesTheOtherCaseOfALetter()
    {
        Assert.Contains("Brad Pitt", Names(new InternalPeopleQuery { NameStartsWith = "brad" }));
    }

    private static void AssertNames(IEnumerable<string> expected, IEnumerable<string> actual)
        => Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));

    private IReadOnlyList<string> Names(InternalPeopleQuery filter) => _people.GetPeopleNames(filter);
}
