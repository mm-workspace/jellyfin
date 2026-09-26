using System;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Emby.Server.Implementations.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Server.Implementations.Item;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using Xunit;
using BaseItemKind = Jellyfin.Data.Enums.BaseItemKind;

namespace Jellyfin.Server.Implementations.Tests.Item;

/// <summary>
/// Covers what happens when another writer stores the credits of an item between the reads and the writes of a
/// people update. <see cref="CompetingCreditWriter"/> writes on the update's own connection and in its
/// transaction, so at the moment the update meets those rows they are exactly the rows a second server's commit
/// would have put there; they then roll back with the attempt that fails on them.
/// <see cref="CommittedCreditWriter"/> is what leaves rows behind that outlive it, which is the state the attempt
/// after a failed one reads. Two servers writing at the same time are covered by
/// <see cref="PeopleRepositoryCrossProcessUpdateTests"/>, which needs PostgreSQL.
/// </summary>
public sealed class PeopleRepositoryConcurrentUpdateTests : DbTestFixture
{
    private const string PersonName = "Person A";
    private const string Role = "Hero";
    private const string OtherPersonName = "Person B";
    private const string OtherRole = "Villain";
    private const string MarkerRole = "Extra";
    private const string ItemName = "Movie";
    private const string OtherItemName = "Other Movie";

    // The statements of the update another writer's work is timed against.
    private const string TheCreditLookup = "FROM \"Peoples\"";
    private const string TheCreditInsert = "INSERT INTO \"Peoples\"";

    // The other writer's statements. They name the rows by the constants above; a literal statement keeps the
    // text out of both providers' parameter handling, and it is the same standard SQL on each.
    private const string MapTheCredit = """
        INSERT INTO "PeopleBaseItemMap" ("ItemId", "PeopleId", "Role", "ListOrder", "SortOrder")
        SELECT b."Id", p."Id", 'Hero', 7, NULL
        FROM "BaseItems" b, "Peoples" p
        WHERE b."Name" = 'Movie' AND p."Name" = 'Person A'
        """;

    private const string MapTheCreditAndMarkTheOtherItem = """
        INSERT INTO "PeopleBaseItemMap" ("ItemId", "PeopleId", "Role", "ListOrder", "SortOrder")
        SELECT b."Id", p."Id", 'Hero', 7, NULL
        FROM "BaseItems" b, "Peoples" p
        WHERE b."Name" = 'Movie' AND p."Name" = 'Person A';
        INSERT INTO "PeopleBaseItemMap" ("ItemId", "PeopleId", "Role", "ListOrder", "SortOrder")
        SELECT b."Id", p."Id", 'Extra', 7, NULL
        FROM "BaseItems" b, "Peoples" p
        WHERE b."Name" = 'Other Movie' AND p."Name" = 'Person A'
        """;

    private const string DropTheMappings = """
        DELETE FROM "PeopleBaseItemMap"
        WHERE "ItemId" IN (SELECT b."Id" FROM "BaseItems" b WHERE b."Name" = 'Movie')
        """;

    // The credit row of the other writer borrows its identifier from an item, which is a Guid the database made
    // earlier, so that a generated one does not have to be written into SQL both providers have to read.
    private const string CreateASecondCreditAndMapIt = """
        INSERT INTO "Peoples" ("Id", "Name", "PersonType")
        SELECT b."Id", 'Person A', 'Actor'
        FROM "BaseItems" b
        WHERE b."Name" = 'Other Movie';
        INSERT INTO "PeopleBaseItemMap" ("ItemId", "PeopleId", "Role", "ListOrder", "SortOrder")
        SELECT b."Id", p."Id", 'Hero', 7, NULL
        FROM "BaseItems" b, "Peoples" p
        WHERE b."Name" = 'Movie' AND p."Name" = 'Person A'
        """;

    private static readonly Guid _itemId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid _otherItemId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    private readonly CompetingCreditWriter _competitor;
    private readonly CommittedCreditWriter _committer;

    public PeopleRepositoryConcurrentUpdateTests()
        : this(new CompetingCreditWriter(), new CommittedCreditWriter())
    {
    }

    private PeopleRepositoryConcurrentUpdateTests(CompetingCreditWriter competitor, CommittedCreditWriter committer)
        : base(competitor, committer)
    {
        _competitor = competitor;
        _committer = committer;
        AddMovie(_itemId, ItemName);
        AddMovie(_otherItemId, OtherItemName);
    }

    [Fact]
    public void UpdatePeople_CreditWrittenByAnotherWriterAfterTheMappingsWereRead_MapsTheCreditOnce()
    {
        var repository = CreateRepository(Database.Provider);

        // The credit exists because another item names the same person; only the mapping is new.
        repository.UpdatePeople(_otherItemId, [CreatePerson()]);
        _competitor.Arm(1, TheCreditLookup, MapTheCredit);

        repository.UpdatePeople(_itemId, [CreatePerson()]);

        Assert.Equal(1, _competitor.Writes);
        using var context = CreateDbContext();
        Assert.Single(context.Peoples);
        var map = Assert.Single(context.PeopleBaseItemMap.Where(e => e.ItemId.Equals(_itemId)));
        Assert.Equal(Role, map.Role);
        Assert.Equal(0, map.ListOrder);
    }

    [Fact]
    public void UpdatePeople_CreditAnotherWriterCommitted_IsMappedOnceByTheAttemptThatFollows()
    {
        var repository = CreateRepository(Database.Provider);

        repository.UpdatePeople(_otherItemId, [CreatePerson()]);

        // The other writer's mapping collides with the first attempt and is in the database by the time the
        // second one reads, which is the state a second server that has committed leaves behind.
        _competitor.Arm(1, TheCreditLookup, MapTheCredit);
        _committer.ArmBeforeTransaction(2, MapTheCreditAndMarkTheOtherItem);

        repository.UpdatePeople(_itemId, [CreatePerson()]);

        Assert.Equal(1, _competitor.Writes);
        using var context = CreateDbContext();
        Assert.Single(context.Peoples);

        // The other writer's mapping was moved to where this update wants the credit, not left beside a second
        // row for the same credit.
        var map = Assert.Single(context.PeopleBaseItemMap.Where(e => e.ItemId.Equals(_itemId)));
        Assert.Equal(Role, map.Role);
        Assert.Equal(0, map.ListOrder);

        // The row the other writer wrote on an item this update does not touch is still there, so what the
        // second attempt read was committed and not rolled back with the attempt that failed on it.
        Assert.Single(context.PeopleBaseItemMap.Where(e => e.ItemId.Equals(_otherItemId) && e.Role == MarkerRole));
    }

    [Fact]
    public void UpdatePeople_MappingsRemovedByAnotherWriterAfterTheyWereRead_ReplacesTheCreditsOnce()
    {
        var repository = CreateRepository(Database.Provider);

        repository.UpdatePeople(_itemId, [CreatePerson()]);

        // The other writer gives the item a different cast, so the mapping this update read and means to drop
        // is already gone when its delete looks for it. No database reports that; EF counts the rows itself.
        _competitor.Arm(1, TheCreditLookup, DropTheMappings);

        repository.UpdatePeople(_itemId, [CreateOtherPerson()]);

        Assert.Equal(1, _competitor.Writes);
        using var context = CreateDbContext();
        var credit = Assert.Single(context.Peoples);
        Assert.Equal(OtherPersonName, credit.Name);
        var map = Assert.Single(context.PeopleBaseItemMap);
        Assert.Equal(OtherRole, map.Role);
    }

    [Fact]
    public void UpdatePeople_WriterThatCollidesOnEveryAttempt_ReportsTheFailure()
    {
        var repository = CreateRepository(Database.Provider);

        repository.UpdatePeople(_otherItemId, [CreatePerson()]);
        _competitor.Arm(int.MaxValue, TheCreditLookup, MapTheCredit);

        Assert.Throws<DbUpdateException>(() => repository.UpdatePeople(_itemId, [CreatePerson()]));

        // Bounded: the update is not repeated for as long as another writer keeps winning.
        Assert.Equal(PeopleRepository.MaxCreditWriteAttempts, _competitor.Writes);
    }

    [Fact]
    public void UpdatePeople_FailureTheProviderDoesNotRecognise_IsNotRetried()
    {
        // What is written again is decided by the provider's reading of the failure, not by the update
        // repeating itself whenever a write fails.
        var repository = CreateRepository(Mock.Of<IJellyfinDatabaseProvider>());

        repository.UpdatePeople(_otherItemId, [CreatePerson()]);
        _competitor.Arm(1, TheCreditLookup, MapTheCredit);

        Assert.Throws<DbUpdateException>(() => repository.UpdatePeople(_itemId, [CreatePerson()]));
        Assert.Equal(1, _competitor.Writes);
    }

    [Fact]
    public void UpdatePeople_CreditAnotherWriterCreatedAfterTheLookup_IsMergedOnlyByTheNextRefresh()
    {
        var repository = CreateRepository(Database.Provider);

        // Neither writer has stored this person yet, so each stores one of its own. The rows have different
        // identifiers and so do the mappings that name them, and nothing in the schema says two credits of one
        // name are one person, so there is no failure for either database to report and nothing to write again.
        _competitor.Arm(1, TheCreditInsert, CreateASecondCreditAndMapIt);

        repository.UpdatePeople(_itemId, [CreatePerson()]);

        Assert.Equal(1, _competitor.Writes);
        using (var context = CreateDbContext())
        {
            Assert.Equal(2, context.Peoples.Count());
            Assert.Equal(2, context.PeopleBaseItemMap.Count(e => e.ItemId.Equals(_itemId)));
        }

        // The item carries the person twice until it is refreshed again, which writes the credits it is given
        // over both rows and sweeps the one left without a mapping.
        repository.UpdatePeople(_itemId, [CreatePerson()]);

        using (var refreshed = CreateDbContext())
        {
            Assert.Single(refreshed.Peoples);
            Assert.Single(refreshed.PeopleBaseItemMap);
        }
    }

    private static PersonInfo CreatePerson() => new PersonInfo
    {
        Name = PersonName,
        Type = PersonKind.Actor,
        Role = Role
    };

    private static PersonInfo CreateOtherPerson() => new PersonInfo
    {
        Name = OtherPersonName,
        Type = PersonKind.Actor,
        Role = OtherRole
    };

    private PeopleRepository CreateRepository(IJellyfinDatabaseProvider databaseProvider) => new(
        CreateDbContextFactory(),
        new ItemTypeLookup(),
        Mock.Of<IItemQueryHelpers>(),
        databaseProvider);

    private void AddMovie(Guid id, string name)
    {
        using var context = CreateDbContext();
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
    /// Runs another writer's statement just before a statement of the update it is armed for, on the update's own
    /// connection and in its transaction, so the rows are neither a second connection the in-memory database does
    /// not have nor a write the database serializes. They roll back with the attempt that fails on them.
    /// </summary>
    private sealed class CompetingCreditWriter : DbCommandInterceptor
    {
        private string _before = string.Empty;
        private string _statement = string.Empty;
        private int _remaining;

        public int Writes { get; private set; }

        /// <summary>
        /// Runs <paramref name="statement"/> before each of the next <paramref name="writes"/> statements of the
        /// update whose text contains <paramref name="before"/>.
        /// </summary>
        /// <param name="writes">How often the other writer gets in.</param>
        /// <param name="before">The text identifying the statement it gets in before.</param>
        /// <param name="statement">What it writes.</param>
        public void Arm(int writes, string before, string statement)
        {
            _remaining = writes;
            _before = before;
            _statement = statement;
            Writes = 0;
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            WriteBefore(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            WriteBefore(command);
            return base.NonQueryExecuting(command, eventData, result);
        }

        [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Test statements are constants.")]
        private void WriteBefore(DbCommand command)
        {
            if (_remaining <= 0
                || command.Transaction is null
                || !command.CommandText.Contains(_before, StringComparison.Ordinal))
            {
                return;
            }

            _remaining--;
            Writes++;

            using var competing = command.Connection!.CreateCommand();
            competing.Transaction = command.Transaction;
            competing.CommandText = _statement;
            competing.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Runs another writer's statement on the update's connection while none of its transactions is open, so the
    /// rows are committed and every attempt that reads afterwards finds them. A second connection would be closer
    /// to the real thing, but the in-memory database of the default harness has only the one.
    /// </summary>
    private sealed class CommittedCreditWriter : DbTransactionInterceptor
    {
        private string _statement = string.Empty;
        private int _writeBefore;
        private int _transactions;

        /// <summary>
        /// Runs <paramref name="statement"/> just before the transaction with the given number among those
        /// started from now on, counting from one.
        /// </summary>
        /// <param name="ordinal">Which transaction the rows go in before.</param>
        /// <param name="statement">What the other writer commits.</param>
        public void ArmBeforeTransaction(int ordinal, string statement)
        {
            _writeBefore = ordinal;
            _statement = statement;
            _transactions = 0;
        }

        [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Test statements are constants.")]
        public override InterceptionResult<DbTransaction> TransactionStarting(DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result)
        {
            if (_writeBefore > 0 && ++_transactions == _writeBefore)
            {
                using var competing = connection.CreateCommand();
                competing.CommandText = _statement;
                competing.ExecuteNonQuery();
            }

            return base.TransactionStarting(connection, eventData, result);
        }
    }
}
