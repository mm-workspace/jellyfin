using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Testing;
using Jellyfin.Server.Implementations.Users;
using MediaBrowser.Common.Configuration;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Users;

public sealed class DisplayPreferencesManagerTests : IDisposable
{
    // Lone surrogates cannot go through attribute arguments: metadata strings are UTF-8, so the compiler
    // writes U+FFFD in their place and a theory case would assert nothing.
    private const string Unstorable = "Bad\0Text\uD83C";
    private const string Sanitized = "BadText�";

    private static readonly Guid _userId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid _itemId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly ITestDatabase _database = TestDatabase.Create(new TestDatabaseOptions { ApplicationPaths = new Mock<IApplicationPaths>().Object });

    [Fact]
    public void SetCustomItemDisplayPreferences_UnstorableText_StoresTheSanitizedText()
    {
        var manager = new DisplayPreferencesManager(_database.CreateDbContextFactory());

        manager.SetCustomItemDisplayPreferences(_userId, _itemId, "client", new Dictionary<string, string?> { [Unstorable] = Unstorable });

        using var context = _database.CreateDbContext();
        var preference = Assert.Single(context.CustomItemDisplayPreferences);
        Assert.Equal(Sanitized, preference.Key);
        Assert.Equal(Sanitized, preference.Value);
    }

    [Fact]
    public void SetCustomItemDisplayPreferences_ReplacesThePreviousPreferences()
    {
        var manager = new DisplayPreferencesManager(_database.CreateDbContextFactory());

        manager.SetCustomItemDisplayPreferences(_userId, _itemId, "client", new Dictionary<string, string?> { ["first"] = "1" });
        manager.SetCustomItemDisplayPreferences(_userId, _itemId, "client", new Dictionary<string, string?> { ["second"] = "2" });

        using var context = _database.CreateDbContext();
        Assert.Equal(["second"], context.CustomItemDisplayPreferences.Select(e => e.Key).ToArray());
    }

    public void Dispose() => _database.Dispose();
}
