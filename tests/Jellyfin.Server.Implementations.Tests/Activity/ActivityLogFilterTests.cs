using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data.Queries;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Server.Implementations.Activity;
using Jellyfin.Server.Implementations.Tests.Item;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Activity;

/// <summary>
/// The activity log filters are free text a user typed into the dashboard, so they have to match the case
/// the entry was written in and to take a wildcard in the text for part of what is being looked for.
/// </summary>
public sealed class ActivityLogFilterTests : DbTestFixture
{
    private readonly ActivityManager _activity;

    public ActivityLogFilterTests()
    {
        using (var context = CreateDbContext())
        {
            var user = new User("Rene_M", "auth-provider", "reset-provider");
            var other = new User("ReneXM", "auth-provider", "reset-provider");
            context.Users.AddRange(user, other);
            context.ActivityLogs.AddRange(
                Entry("Playback started", "AudioPlayback", "Finished 100% of the episode", user.Id),
                Entry("1000 items indexed", "LibraryScan", "Finished 1000X of the episode", other.Id));
            context.SaveChanges();
        }

        _activity = new ActivityManager(CreateDbContextFactory());
    }

    [Fact]
    public async Task Name_TakesAPercentSignForItself()
    {
        Assert.Empty(await NamesAsync(new ActivityLogQuery { Name = "100%" }).ConfigureAwait(true));
    }

    [Fact]
    public async Task Overview_TakesAPercentSignForItself()
    {
        Assert.Equal(["Playback started"], await NamesAsync(new ActivityLogQuery { Overview = "100% of" }).ConfigureAwait(true));
    }

    [Fact]
    public async Task Username_TakesAnUnderscoreForItself()
    {
        Assert.Equal(["Playback started"], await NamesAsync(new ActivityLogQuery { Username = "Rene_M" }).ConfigureAwait(true));
    }

    [Fact]
    public async Task Name_MatchesTheOtherCaseOfALetter()
    {
        Assert.Equal(["Playback started"], await NamesAsync(new ActivityLogQuery { Name = "PLAYBACK" }).ConfigureAwait(true));
    }

    [Fact]
    public async Task Type_MatchesTheOtherCaseOfALetter()
    {
        Assert.Equal(["1000 items indexed"], await NamesAsync(new ActivityLogQuery { Type = "libraryscan" }).ConfigureAwait(true));
    }

    private static ActivityLog Entry(string name, string type, string overview, Guid userId)
        => new(name, type, userId) { Overview = overview, ShortOverview = overview };

    private async Task<IReadOnlyList<string>> NamesAsync(ActivityLogQuery query)
    {
        var result = await _activity.GetPagedResultAsync(query).ConfigureAwait(false);
        return result.Items.Select(e => e.Name).ToArray();
    }
}
