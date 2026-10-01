using System;
using System.Collections.Generic;
using System.Data.Common;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Testing;
using Jellyfin.Server.Implementations.Users;
using MediaBrowser.Common.Configuration;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Users;

/// <summary>
/// Covers what happens when another writer stores the preferences a call is about to insert. The lookups these writes
/// start from are reads, so the write lock that queues writes inside one server does not cover them, and two callers
/// here can miss each other's rows just as a second server on the same database can. The other writer stores its rows
/// between what the call it competes with reads and what it writes, which is where a second server's commit falls.
/// </summary>
public sealed class DisplayPreferencesManagerConcurrentWriteTests : IDisposable
{
    private const string Client = "client";
    private const string CustomPreferencesTable = "CustomItemDisplayPreferences";

    private static readonly Guid _userId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid _itemId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly CompetingWriter _otherWriter = new();
    private readonly StatementRewriter _statements = new();
    private readonly ITestDatabase _database;

    public DisplayPreferencesManagerConcurrentWriteTests()
    {
        _database = TestDatabase.Create(new TestDatabaseOptions
        {
            ApplicationPaths = Mock.Of<IApplicationPaths>(),
            Interceptors = [_otherWriter, _statements]
        });

        using var context = _database.CreateDbContext();
        context.Users.Add(new User("user", "auth-provider", "reset-provider") { Id = _userId });
        context.SaveChanges();
    }

    public void Dispose() => _database.Dispose();

    [Fact]
    public void GetDisplayPreferences_WhenAnotherWriterStoredThePreferencesFirst_ReturnsTheStoredOnes()
    {
        var manager = CreateManager();
        var otherManager = CreateManager();
        _otherWriter.Arm(() => otherManager.GetDisplayPreferences(_userId, _itemId, Client));

        var preferences = manager.GetDisplayPreferences(_userId, _itemId, Client);

        Assert.True(_otherWriter.Wrote, "The other writer never got in, so the insert was not made to meet a stored row.");

        // The caller sets its preferences on what it was handed, so that has to be the stored row.
        preferences.ShowSidebar = true;
        manager.UpdateDisplayPreferences(preferences);

        using var context = _database.CreateDbContext();
        var stored = Assert.Single(context.DisplayPreferences);
        Assert.Equal(stored.Id, preferences.Id);
        Assert.True(stored.ShowSidebar);
    }

    [Fact]
    public void SetCustomItemDisplayPreferences_WhenAnotherWriterStoredOneOfTheKeysFirst_StoresThePreferencesItWasGiven()
    {
        var manager = CreateManager();

        // The rows of the other writer, stored, and this call's delete made to miss them: that is what it looks
        // like to the insert when the other writer stores them after that delete has looked for them.
        SetPreferences(CreateManager(), new Dictionary<string, string?> { ["first"] = "theirs", ["only-theirs"] = "theirs" });
        _statements.SkipTheNextDeleteOf(CustomPreferencesTable);

        SetPreferences(manager, new Dictionary<string, string?> { ["first"] = "mine", ["second"] = "mine" });

        Assert.True(_statements.SkippedADelete, "No delete was recognised, so the insert was not made to meet stored rows.");
        Assert.Equal(
            new Dictionary<string, string?> { ["first"] = "mine", ["second"] = "mine" },
            manager.ListCustomItemDisplayPreferences(_userId, _itemId, Client));
    }

    [Fact]
    public void SetCustomItemDisplayPreferences_WhenTheWriteFails_KeepsThePreferencesItWasReplacing()
    {
        var manager = CreateManager();
        SetPreferences(manager, new Dictionary<string, string?> { ["first"] = "1" });

        _statements.FailTheNextInsertOf(CustomPreferencesTable);

        Assert.ThrowsAny<Exception>(() => SetPreferences(manager, new Dictionary<string, string?> { ["second"] = "2" }));

        // Replacing the preferences is one unit of work: a write that does not land leaves the stored ones alone.
        Assert.Equal(
            new Dictionary<string, string?> { ["first"] = "1" },
            manager.ListCustomItemDisplayPreferences(_userId, _itemId, Client));
    }

    private static void SetPreferences(DisplayPreferencesManager manager, Dictionary<string, string?> preferences)
        => manager.SetCustomItemDisplayPreferences(_userId, _itemId, Client, preferences);

    private DisplayPreferencesManager CreateManager()
        => new(_database.CreateDbContextFactory(), _database.Provider);

    /// <summary>
    /// Stands in for the two halves of the window another writer's commit falls into: a delete that removes nothing
    /// because the rows it looked for were stored after it ran, and a write that never reaches the database.
    /// </summary>
    private sealed class StatementRewriter : DbCommandInterceptor
    {
        private string _skipTheDeleteOf = string.Empty;
        private string _failTheInsertOf = string.Empty;

        /// <summary>
        /// Gets a value indicating whether a delete was recognised and skipped.
        /// </summary>
        public bool SkippedADelete { get; private set; }

        /// <summary>
        /// Keeps the next delete of the given table from running, which leaves the rows it was to remove where an
        /// insert of the same keys meets them.
        /// </summary>
        /// <param name="table">The table whose delete is skipped.</param>
        public void SkipTheNextDeleteOf(string table) => _skipTheDeleteOf = table;

        /// <summary>
        /// Fails the next insert into the given table with a failure no database reported, so nothing reads it as
        /// another writer having got there first.
        /// </summary>
        /// <param name="table">The table whose insert fails.</param>
        public void FailTheNextInsertOf(string table) => _failTheInsertOf = table;

        /// <inheritdoc />
        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            FailTheInsert(command);

            if (_skipTheDeleteOf.Length > 0 && Names(command, "DELETE FROM", _skipTheDeleteOf))
            {
                _skipTheDeleteOf = string.Empty;
                SkippedADelete = true;
                return InterceptionResult<int>.SuppressWithResult(0);
            }

            return result;
        }

        /// <inheritdoc />
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            FailTheInsert(command);
            return result;
        }

        private static bool Names(DbCommand command, string statement, string table)
            => command.CommandText.StartsWith(statement, StringComparison.Ordinal)
                && command.CommandText.Contains($"\"{table}\"", StringComparison.Ordinal);

        private void FailTheInsert(DbCommand command)
        {
            if (_failTheInsertOf.Length > 0 && Names(command, "INSERT INTO", _failTheInsertOf))
            {
                _failTheInsertOf = string.Empty;
                throw new InvalidOperationException("The write did not reach the database.");
            }
        }
    }
}
