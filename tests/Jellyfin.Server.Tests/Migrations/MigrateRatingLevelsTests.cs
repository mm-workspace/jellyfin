using System;
using System.Linq;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Testing;
using Jellyfin.Server.Migrations.Routines;
using Jellyfin.Server.ServerSetupApp;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Tests.Migrations;

public sealed class MigrateRatingLevelsTests : IDisposable
{
    private readonly ITestDatabase _database = TestDatabase.Create(new TestDatabaseOptions { ApplicationPaths = new Mock<IApplicationPaths>().Object });

    [Fact]
    public void Perform_SetsRatingValuesFromTheRatingText()
    {
        using (var context = _database.CreateDbContext())
        {
            context.BaseItems.AddRange(
                Item("PG-13"),
                Item("PG-13"),
                Item("R"),
                Item("Unrated"),
                Item(string.Empty),
                Item(null));
            context.SaveChanges();
        }

        var localization = new Mock<ILocalizationManager>();
        localization.Setup(l => l.GetRatingScore("PG-13", It.IsAny<string?>())).Returns(new ParentalRatingScore(13, null));
        localization.Setup(l => l.GetRatingScore("R", It.IsAny<string?>())).Returns(new ParentalRatingScore(17, 1));

        new MigrateRatingLevels(
            _database.CreateDbContextFactory(),
            new StartupLogger<MigrateRatingLevels>(NullLogger<MigrateRatingLevels>.Instance),
            localization.Object).Perform();

        using (var context = _database.CreateDbContext())
        {
            var items = context.BaseItems.AsNoTracking().ToList();
            Assert.All(items.Where(i => i.OfficialRating == "PG-13"), i => Assert.Equal((13, (int?)null), (i.InheritedParentalRatingValue!.Value, i.InheritedParentalRatingSubValue)));
            Assert.Equal((17, (int?)1), items.Where(i => i.OfficialRating == "R").Select(i => (i.InheritedParentalRatingValue!.Value, i.InheritedParentalRatingSubValue)).Single());
            Assert.All(items.Where(i => i.OfficialRating is not ("PG-13" or "R")), i => Assert.Equal(((int?)null, (int?)null), (i.InheritedParentalRatingValue, i.InheritedParentalRatingSubValue)));
        }
    }

    public void Dispose() => _database.Dispose();

    private static BaseItemEntity Item(string? officialRating) => new()
    {
        Id = Guid.NewGuid(),
        Type = "MediaBrowser.Controller.Entities.Movies.Movie",
        OfficialRating = officialRating,
        InheritedParentalRatingValue = 99,
        InheritedParentalRatingSubValue = 99
    };
}
