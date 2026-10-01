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
/// String matching and storage behave as on SQLite, which Jellyfin's queries were written against.
/// </summary>
public sealed class StringParityTests : IDisposable
{
    private const string MovieType = "MediaBrowser.Controller.Entities.Movies.Movie";

    private readonly ITestDatabase _database = TestDatabase.Create(new TestDatabaseOptions { ApplicationPaths = new Mock<IApplicationPaths>().Object });

    public StringParityTests()
    {
        using var context = _database.CreateDbContext();
        foreach (var name in new[] { "Alien", "alien", "ALIENS", "Émile", "émile", "100% Wolf", "Under_score" })
        {
            context.BaseItems.Add(new BaseItemEntity { Id = Guid.NewGuid(), Type = MovieType, Name = name, CleanName = name, OriginalTitle = name });
        }

        context.SaveChanges();
    }

    [Theory]
    [InlineData("%alien%", new[] { "ALIENS", "Alien", "alien" })]
    [InlineData("alien", new[] { "Alien", "alien" })]
    [InlineData("%ÉMILE%", new[] { "Émile" })]
    [InlineData("%émile%", new[] { "émile" })]
    [InlineData("100%", new[] { "100% Wolf" })]
    [InlineData("Under_score", new[] { "Under_score" })]
    public async Task Like_MatchesAsciiCaseInsensitively(string pattern, string[] expected)
    {
        await using var context = _database.CreateDbContext();

        var names = await Movies(context).Where(e => EF.Functions.Like(e.Name, pattern)).Select(e => e.Name!).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected.Order(StringComparer.Ordinal), names.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Like_BackslashIsNotAnEscape()
    {
        await using (var context = _database.CreateDbContext())
        {
            context.BaseItems.Add(new BaseItemEntity { Id = Guid.NewGuid(), Type = MovieType, Name = @"C:\Movies" });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _database.CreateDbContext();
        var count = await Movies(query).CountAsync(e => EF.Functions.Like(e.Name, @"C:\Movies"), TestContext.Current.CancellationToken);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Contains_IsCaseSensitive()
    {
        await using var context = _database.CreateDbContext();
        var term = "lien";

        var names = await Movies(context).Where(e => e.Name!.Contains(term)).Select(e => e.Name!).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["Alien", "alien"], names.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task StartsWith_MatchesAsciiCaseInsensitively()
    {
        await using var context = _database.CreateDbContext();
        var prefix = "Al";

        var names = await Movies(context).Where(e => e.Name!.StartsWith(prefix)).Select(e => e.Name!).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["ALIENS", "Alien", "alien"], names.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task StartsWith_WildcardsInTheValue_MatchLiterally()
    {
        await using (var context = _database.CreateDbContext())
        {
            context.BaseItems.Add(new BaseItemEntity { Id = Guid.NewGuid(), Type = MovieType, Name = "UnderXscore" });
            context.BaseItems.Add(new BaseItemEntity { Id = Guid.NewGuid(), Type = MovieType, Name = "100 Wolves" });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var query = _database.CreateDbContext();
        var underscore = "under_";
        var percent = "100%";

        Assert.Equal(["Under_score"], await Movies(query).Where(e => e.Name!.StartsWith(underscore)).Select(e => e.Name!).ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(["100% Wolf"], await Movies(query).Where(e => e.Name!.StartsWith(percent)).Select(e => e.Name!).ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EndsWith_MatchesAsciiCaseInsensitively()
    {
        await using var context = _database.CreateDbContext();
        var suffix = "EN";

        var names = await Movies(context).Where(e => e.Name!.EndsWith(suffix)).Select(e => e.Name!).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["Alien", "alien"], names.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ToLower_LowersAsciiOnly()
    {
        await using var context = _database.CreateDbContext();

#pragma warning disable CA1304, CA1311, CA1862 // The query is translated to SQL; the database lowers, not .NET.
        var names = await Movies(context).Where(e => e.Name!.ToLower() == "émile").Select(e => e.Name!).ToListAsync(TestContext.Current.CancellationToken);
#pragma warning restore CA1304, CA1311, CA1862

        Assert.Equal(["émile"], names);
    }

    [Fact]
    public async Task Save_UnpairedSurrogates_StoresReplacementCharacters()
    {
        // Lone surrogates cannot go through attribute arguments without being replaced by the compiler.
        (string Value, string Expected)[] cases =
        [
            ("lone high \uD83C end", "lone high \uFFFD end"),
            ("lone low \uDFAC end", "lone low \uFFFD end"),
            ("reversed \uDFAC\uD83C", "reversed \uFFFD\uFFFD"),
            ("trailing \uD83C", "trailing \uFFFD"),
            ("pair \uD83C\uDFAC kept", "pair \uD83C\uDFAC kept")
        ];

        foreach (var (value, expected) in cases)
        {
            Assert.Equal(expected, await SaveAndReadNameAsync(value));
        }
    }

    [Fact]
    public async Task Save_NulCharacter_DoesNotFail()
    {
        var stored = await SaveAndReadNameAsync("Bad\0Name");

        // PostgreSQL text cannot hold NUL, so it is dropped there; SQLite keeps it.
        Assert.Contains(stored, new[] { "Bad\0Name", "BadName" });
    }

    public void Dispose() => _database.Dispose();

    private async Task<string?> SaveAndReadNameAsync(string name)
    {
        var id = Guid.NewGuid();
        await using (var context = _database.CreateDbContext())
        {
            context.BaseItems.Add(new BaseItemEntity { Id = id, Type = MovieType, Name = name });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var read = _database.CreateDbContext();
        return await read.BaseItems.Where(e => e.Id.Equals(id)).Select(e => e.Name).SingleAsync(TestContext.Current.CancellationToken);
    }

    private static IQueryable<BaseItemEntity> Movies(Database.Implementations.JellyfinDbContext context)
        => context.BaseItems.AsNoTracking().Where(e => e.Type == MovieType);
}
