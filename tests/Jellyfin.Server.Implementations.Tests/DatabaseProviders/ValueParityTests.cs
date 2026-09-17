using System;
using System.Linq;
using System.Threading.Tasks;
using Emby.Server.Implementations.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Testing;
using Jellyfin.Server.Implementations.Item;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using BaseItemKind = Jellyfin.Data.Enums.BaseItemKind;
using ItemSortBy = Jellyfin.Data.Enums.ItemSortBy;
using SortOrder = Jellyfin.Database.Implementations.Enums.SortOrder;

namespace Jellyfin.Server.Implementations.Tests.DatabaseProviders;

/// <summary>
/// Values and orderings come back as they do on SQLite.
/// </summary>
public sealed class ValueParityTests : IDisposable
{
    private const string MovieType = "MediaBrowser.Controller.Entities.Movies.Movie";

    private readonly ITestDatabase _database = TestDatabase.Create(new TestDatabaseOptions { ApplicationPaths = new Mock<IApplicationPaths>().Object });

    [Theory]
    [InlineData(SortOrder.Ascending)]
    [InlineData(SortOrder.Descending)]
    public void GetItemList_PremiereDateOrder_UsesProductionYearWhenDateIsMissing(SortOrder sortOrder)
    {
        var dated = Movie("dated", premiereDate: new DateTime(1999, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        var yearOnly = Movie("year only", productionYear: 1999);
        var earlier = Movie("earlier", productionYear: 1980);
        var neither = Movie("neither");
        using (var context = _database.CreateDbContext())
        {
            context.BaseItems.AddRange(dated, yearOnly, earlier, neither);
            context.SaveChanges();
        }

        var names = CreateRepository()
            .GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.Movie], OrderBy = [(ItemSortBy.PremiereDate, sortOrder)] })
            .Select(i => i.Name)
            .ToList();

        string[] ascending = ["neither", "earlier", "year only", "dated"];
        Assert.Equal(sortOrder == SortOrder.Ascending ? ascending : ascending.Reverse(), names);
    }

    [Theory]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public async Task Save_InfiniteRating_ReadsBackUnchanged(float rating)
    {
        // NaN is not covered: SQLite refuses to store it.
        var movie = Movie("rated");
        movie.CommunityRating = rating;
        await using (var context = _database.CreateDbContext())
        {
            context.BaseItems.Add(movie);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var read = _database.CreateDbContext();
        var stored = await read.BaseItems.Where(e => e.Id.Equals(movie.Id)).Select(e => e.CommunityRating).SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(rating, stored);
    }

    public void Dispose() => _database.Dispose();

    private static BaseItemEntity Movie(string name, DateTime? premiereDate = null, int? productionYear = null)
    {
        var id = Guid.NewGuid();
        return new BaseItemEntity { Id = id, Type = MovieType, Name = name, SortName = name, PresentationUniqueKey = id.ToString("N"), PremiereDate = premiereDate, ProductionYear = productionYear, MediaType = "Video" };
    }

    private BaseItemRepository CreateRepository()
    {
        var configuration = new Mock<IServerConfigurationManager>();
        configuration.Setup(c => c.Configuration).Returns(new ServerConfiguration());
        return new BaseItemRepository(_database.CreateDbContextFactory(), new Mock<IServerApplicationHost>().Object, new ItemTypeLookup(), configuration.Object, NullLogger<BaseItemRepository>.Instance);
    }
}
