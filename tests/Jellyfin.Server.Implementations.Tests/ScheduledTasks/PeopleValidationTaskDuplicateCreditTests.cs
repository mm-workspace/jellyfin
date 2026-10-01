using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Data;
using Emby.Server.Implementations.Library.Validators;
using Emby.Server.Implementations.ScheduledTasks.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Testing;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using BaseItemKind = Jellyfin.Data.Enums.BaseItemKind;

namespace Jellyfin.Server.Implementations.Tests.ScheduledTasks;

/// <summary>
/// Covers how the people validation task collapses credits that name the same person. Two writers that credit a
/// person neither of them has stored yet each store one of their own, so the rows this task merges are the rows
/// that race leaves behind; when both of them are mapped to one item for one role, the mapping the merge moves is
/// the mapping already there, under the key (ItemId, PeopleId, Role).
/// </summary>
public sealed class PeopleValidationTaskDuplicateCreditTests : IDisposable
{
    private const string PersonName = "Person A";
    private const string PersonType = "Actor";
    private const string Role = "Hero";
    private const string OtherRole = "Villain";

    private static readonly Guid _itemId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid _otherItemId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    private readonly StatementTransactions _statements = new();
    private readonly ITestDatabase _database;

    public PeopleValidationTaskDuplicateCreditTests()
    {
        _database = TestDatabase.Create(new TestDatabaseOptions
        {
            Interceptors = [_statements],
            ApplicationPaths = Mock.Of<IApplicationPaths>()
        });

        AddMovie(_itemId, "Movie");
        AddMovie(_otherItemId, "Other Movie");
    }

    public void Dispose()
    {
        _database.Dispose();
    }

    [Fact]
    public async Task ExecuteAsync_OneItemCreditedToBothRowsForTheSameRole_KeepsOneCreditAndOneMapping()
    {
        var kept = AddCredit();
        var duplicate = AddCredit();
        Map(_itemId, kept, Role);
        Map(_itemId, duplicate, Role);

        await RunTask();

        using var context = _database.CreateDbContext();
        var credit = Assert.Single(context.Peoples);
        var map = Assert.Single(context.PeopleBaseItemMap);
        Assert.Equal(credit.Id, map.PeopleId);
        Assert.Equal(Role, map.Role);
    }

    [Fact]
    public async Task ExecuteAsync_ThreeRowsCreditedForTheSameRoleOnOneItem_KeepsOneCreditAndOneMapping()
    {
        // Every duplicate collides, whether with the credit that is kept or with a duplicate merged before it.
        Map(_itemId, AddCredit(), Role);
        Map(_itemId, AddCredit(), Role);
        Map(_itemId, AddCredit(), Role);

        await RunTask();

        using var context = _database.CreateDbContext();
        var credit = Assert.Single(context.Peoples);
        var map = Assert.Single(context.PeopleBaseItemMap);
        Assert.Equal(credit.Id, map.PeopleId);
    }

    [Fact]
    public async Task ExecuteAsync_DuplicateCreditedForAnotherRoleAndAnotherItem_KeepsEveryMapping()
    {
        // Two credits of one name hold mappings that are no duplicates of each other: a second role on the same
        // item and a role on another item. Both belong to the credit that is kept.
        var kept = AddCredit();
        var duplicate = AddCredit();
        Map(_itemId, kept, Role);
        Map(_itemId, duplicate, OtherRole);
        Map(_otherItemId, duplicate, Role);

        await RunTask();

        using var context = _database.CreateDbContext();
        var credit = Assert.Single(context.Peoples);
        Assert.Equal(3, context.PeopleBaseItemMap.Count());
        Assert.All(context.PeopleBaseItemMap, map => Assert.Equal(credit.Id, map.PeopleId));
        Assert.Single(context.PeopleBaseItemMap.Where(e => e.ItemId.Equals(_itemId) && e.Role == Role));
        Assert.Single(context.PeopleBaseItemMap.Where(e => e.ItemId.Equals(_itemId) && e.Role == OtherRole));
        Assert.Single(context.PeopleBaseItemMap.Where(e => e.ItemId.Equals(_otherItemId) && e.Role == Role));
    }

    [Fact]
    public async Task ExecuteAsync_MoreDuplicatedNamesThanOnePartition_MergesEveryOne()
    {
        // The pass reads the first partition of duplicated names again and again until none is left, so a name it
        // cannot collapse would be read for ever. Every one here collapses, including the colliding mappings.
        const int Names = 101;
        for (var name = 0; name < Names; name++)
        {
            var kept = AddCredit(PersonName + name);
            var duplicate = AddCredit(PersonName + name);
            Map(_itemId, kept, Role);
            Map(_itemId, duplicate, Role);
            Map(_otherItemId, duplicate, Role);
        }

        await RunTask();

        using var context = _database.CreateDbContext();
        Assert.Equal(Names, context.Peoples.Count());
        Assert.Equal(Names * 2, context.PeopleBaseItemMap.Count());
    }

    [Fact]
    public async Task ExecuteAsync_MergingADuplicate_MovesItsMappingsAndRemovesItInOneTransaction()
    {
        // What keeps another writer in this process out of the window between the move and the removal is the
        // write permit, which an explicit transaction holds from before BEGIN until it ends. Two statements of
        // their own each hold it for themselves and leave that window open in between.
        var kept = AddCredit();
        var duplicate = AddCredit();
        Map(_itemId, kept, Role);
        Map(_otherItemId, duplicate, Role);

        await RunTask();

        var statements = _statements.Executed;
        var move = Assert.Single(
            statements.Select((statement, index) => (Statement: statement, Index: index)),
            e => Names(e.Statement, "UPDATE", "\"PeopleBaseItemMap\""));

        // The credit is removed by the statement that follows the move, not by the sweep for credits nothing maps
        // to that ends the pass.
        var removal = statements.Skip(move.Index).First(e => Names(e, "DELETE", "\"Peoples\""));

        Assert.NotNull(move.Statement.Transaction);
        Assert.Same(move.Statement.Transaction, removal.Transaction);
    }

    [Fact]
    public async Task ExecuteAsync_MergingANameStoredInThreeRows_UsesOneTransactionForAllOfThem()
    {
        // Every row of one name is merged in the transaction of that name, so a name of many rows takes and hands
        // back the write permit once instead of once per row.
        var kept = AddCredit();
        Map(_itemId, kept, Role);
        Map(_otherItemId, AddCredit(), Role);
        Map(_otherItemId, AddCredit(), OtherRole);

        await RunTask();

        var statements = _statements.Executed;
        var moves = statements.Where(e => Names(e, "UPDATE", "\"PeopleBaseItemMap\"")).ToArray();
        Assert.Equal(2, moves.Length);
        Assert.NotNull(moves[0].Transaction);
        Assert.Same(moves[0].Transaction, moves[1].Transaction);

        var removal = statements.SkipWhile(e => e != moves[1]).First(e => Names(e, "DELETE", "\"Peoples\""));
        Assert.Same(moves[0].Transaction, removal.Transaction);
    }

    private static bool Names(Statement statement, params string[] words)
        => words.All(word => statement.CommandText.Contains(word, StringComparison.Ordinal));

    private async Task RunTask()
    {
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(e => e.GetPeopleNames(It.IsAny<InternalPeopleQuery>())).Returns([]);
        libraryManager.Setup(e => e.GetItemIds(It.IsAny<InternalItemsQuery>())).Returns([]);

        var task = new PeopleValidationTask(
            libraryManager.Object,
            Mock.Of<ILocalizationManager>(),
            _database.CreateDbContextFactory(),
            Mock.Of<IFileSystem>(),
            NullLogger<PeopleValidationTask>.Instance,
            NullLogger<PeopleValidator>.Instance,
            new ItemTypeLookup(),
            _database.Provider);

        await task.ExecuteAsync(new Progress<double>(), TestContext.Current.CancellationToken);
    }

    private Guid AddCredit(string name = PersonName)
    {
        var id = Guid.NewGuid();
        using var context = _database.CreateDbContext();
        context.Peoples.Add(new People
        {
            Id = id,
            Name = name,
            PersonType = PersonType
        });
        context.SaveChanges();
        return id;
    }

    private void Map(Guid itemId, Guid creditId, string role)
    {
        using var context = _database.CreateDbContext();
        context.PeopleBaseItemMap.Add(new PeopleBaseItemMap
        {
            Item = null!,
            ItemId = itemId,
            People = null!,
            PeopleId = creditId,
            ListOrder = 0,
            SortOrder = 0,
            Role = role
        });
        context.SaveChanges();
    }

    private void AddMovie(Guid id, string name)
    {
        using var context = _database.CreateDbContext();
        context.BaseItems.Add(new BaseItemEntity
        {
            Id = id,
            Type = new ItemTypeLookup().BaseItemKindNames[BaseItemKind.Movie],
            Name = name,
            MediaType = "Video",
            IsMovie = true,
            IsFolder = false,
            IsVirtualItem = false
        });
        context.SaveChanges();
    }

    /// <summary>
    /// A statement that was executed, with the transaction it ran in, if any.
    /// </summary>
    /// <param name="CommandText">The statement.</param>
    /// <param name="Transaction">The transaction it ran in, or <c>null</c> if it ran on its own.</param>
    private sealed record Statement(string CommandText, DbTransaction? Transaction);

    /// <summary>
    /// Records every statement that writes, in order, with the transaction it ran in.
    /// </summary>
    private sealed class StatementTransactions : DbCommandInterceptor
    {
        private readonly List<Statement> _executed = [];

        public IReadOnlyList<Statement> Executed
        {
            get
            {
                lock (_executed)
                {
                    return _executed.ToArray();
                }
            }
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Executing(command);
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Executing(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Executing(DbCommand command)
        {
            lock (_executed)
            {
                _executed.Add(new Statement(command.CommandText, command.Transaction));
            }
        }
    }
}
