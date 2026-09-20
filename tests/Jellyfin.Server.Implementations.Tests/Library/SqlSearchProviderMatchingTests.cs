using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Data;
using Emby.Server.Implementations.Library.Search;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Server.Implementations.Tests.Item;
using MediaBrowser.Controller.Library;
using Moq;
using Xunit;
using BaseItemKind = Jellyfin.Data.Enums.BaseItemKind;

namespace Jellyfin.Server.Implementations.Tests.Library;

/// <summary>
/// What a user typed into the search box is looked for in OriginalTitle with a LIKE. It has to be found
/// whatever case the title was written in, and a wildcard in it is part of the title being looked for.
/// </summary>
public sealed class SqlSearchProviderMatchingTests : DbTestFixture
{
    private readonly SqlSearchProvider _provider;
    private readonly Dictionary<Guid, string> _names = [];

    public SqlSearchProviderMatchingTests()
    {
        var itemTypeLookup = new ItemTypeLookup();
        var movieTypeName = itemTypeLookup.BaseItemKindNames[BaseItemKind.Movie]!;

        using (var context = CreateDbContext())
        {
            context.BaseItems.AddRange(
                CreateMovie(movieTypeName, "Underscore", "Wolf_1"),
                CreateMovie(movieTypeName, "Wildcard", "WolfX1"));
            context.SaveChanges();
        }

        _provider = new SqlSearchProvider(
            CreateDbContextFactory(),
            itemTypeLookup,
            Mock.Of<ILibraryManager>(),
            Mock.Of<IUserManager>(),
            CreateBaseItemRepository(itemTypeLookup));
    }

    [Fact]
    public async Task SearchAsync_UnderscoreInTheTerm_MatchesTheTitleHoldingIt()
    {
        Assert.Equal(["Underscore"], await NamesAsync("wolf_1").ConfigureAwait(true));
    }

    [Fact]
    public async Task SearchAsync_TermInTheOtherCase_MatchesTheOriginalTitle()
    {
        Assert.Equal(["Underscore"], await NamesAsync("WOLF_1").ConfigureAwait(true));
    }

    private BaseItemEntity CreateMovie(string typeName, string name, string originalTitle)
    {
        var id = Guid.NewGuid();
        _names[id] = name;
        return new BaseItemEntity
        {
            Id = id,
            Type = typeName,
            Name = name,
            CleanName = name.ToLowerInvariant(),
            SortName = name,
            OriginalTitle = originalTitle,
            MediaType = "Video",
            IsMovie = true,
            IsFolder = false,
            IsVirtualItem = false,
            PresentationUniqueKey = id.ToString("N")
        };
    }

    private async Task<IReadOnlyList<string>> NamesAsync(string searchTerm)
    {
        var results = await _provider.SearchAsync(
            new SearchProviderQuery { SearchTerm = searchTerm, Limit = 10 },
            CancellationToken.None).ConfigureAwait(false);

        return results.Select(r => _names[r.ItemId]).Order(StringComparer.Ordinal).ToArray();
    }
}
