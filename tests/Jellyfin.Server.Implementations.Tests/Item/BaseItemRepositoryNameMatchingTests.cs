using System;
using System.Collections.Generic;
using System.Linq;
using Emby.Server.Implementations.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Server.Implementations.Item;
using MediaBrowser.Controller.Entities;
using Xunit;
using BaseItemKind = Jellyfin.Data.Enums.BaseItemKind;

namespace Jellyfin.Server.Implementations.Tests.Item;

/// <summary>
/// A search term and a name filter are text a user typed. Both are matched against CleanName, which holds
/// the lowercased form of the name, and against OriginalTitle, which keeps the case it was written in.
/// </summary>
public sealed class BaseItemRepositoryNameMatchingTests : DbTestFixture
{
    private readonly BaseItemRepository _repository;
    private readonly string _movieTypeName;

    public BaseItemRepositoryNameMatchingTests()
    {
        var itemTypeLookup = new ItemTypeLookup();
        _movieTypeName = itemTypeLookup.BaseItemKindNames[BaseItemKind.Movie];
        _repository = CreateBaseItemRepository(itemTypeLookup);

        using var context = CreateDbContext();
        context.BaseItems.AddRange(
            CreateMovie("The Matrix", null),
            CreateMovie("Underscore", "Wolf_1"),
            CreateMovie("Wildcard", "WolfX1"),
            CreateMovie("Backslash", @"Road\Trip"),
            CreateMovie("Plain", "RoadTrip"));
        context.SaveChanges();
    }

    [Fact]
    public void SearchTerm_TakesAnUnderscoreForItself()
    {
        Assert.Equal(["Underscore"], Names(new InternalItemsQuery { SearchTerm = "wolf_1" }));
    }

    [Fact]
    public void SearchTerm_MatchesAnOriginalTitleInTheOtherCase()
    {
        Assert.Equal(["Underscore"], Names(new InternalItemsQuery { SearchTerm = "WOLF_1" }));
    }

    [Fact]
    public void NameContains_MatchesACleanNameInTheOtherCase()
    {
        Assert.Equal(["The Matrix"], Names(new InternalItemsQuery { NameContains = "Matrix" }));
    }

    [Fact]
    public void NameContains_TakesABackslashForItself()
    {
        Assert.Equal(["Backslash"], Names(new InternalItemsQuery { NameContains = @"Road\Trip" }));
    }

    private BaseItemEntity CreateMovie(string name, string? originalTitle)
        => new()
        {
            Id = Guid.NewGuid(),
            Type = _movieTypeName,
            Name = name,
            CleanName = name.ToLowerInvariant(),
            SortName = name,
            OriginalTitle = originalTitle,
            MediaType = "Video",
            IsMovie = true,
            IsFolder = false,
            IsVirtualItem = false,
            PresentationUniqueKey = Guid.NewGuid().ToString("N")
        };

    private IReadOnlyList<string> Names(InternalItemsQuery filter)
    {
        filter.IncludeItemTypes = [BaseItemKind.Movie];
        return _repository.GetItemList(filter).Select(i => i.Name).Order(StringComparer.Ordinal).ToArray();
    }
}
