using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Testing;
using Jellyfin.Server.Implementations.Item;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Item;

/// <summary>
/// Covers the get-or-create of the item values in <see cref="ItemPersistenceService.SaveItems"/>. ItemValues
/// de-duplicates on a unique (Type, Value) index, and a scan saves items in parallel, so two saves that
/// introduce the same new value race to create it and the insert of the one that loses violates the index.
/// </summary>
public sealed class ItemPersistenceItemValueRaceTests : DbTestFixture
{
    private const string Genre = "Drama";
    private const string OtherGenre = "Comedy";

    private readonly StagedRaceInterceptor _interceptor;
    private readonly ItemPersistenceService _service;
    private readonly ILibraryManager? _previousLibraryManager;
    private readonly IServerConfigurationManager? _previousConfigurationManager;

    public ItemPersistenceItemValueRaceTests()
        : this(new StagedRaceInterceptor())
    {
    }

    private ItemPersistenceItemValueRaceTests(StagedRaceInterceptor interceptor)
        : base(interceptor)
    {
        _interceptor = interceptor;

        // BaseItem resolves these through process-wide statics; restored in Dispose.
        _previousLibraryManager = BaseItem.LibraryManager;
        _previousConfigurationManager = BaseItem.ConfigurationManager;

        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(l => l.GetCollectionFolders(It.IsAny<BaseItem>())).Returns([]);
        BaseItem.LibraryManager = libraryManager.Object;

        var configurationManager = new Mock<IServerConfigurationManager>();
        configurationManager.Setup(c => c.Configuration).Returns(new ServerConfiguration());
        BaseItem.ConfigurationManager = configurationManager.Object;

        _service = CreateService();
    }

    [Fact]
    public void ItemValues_SameTypeAndValueTwice_ViolatesTheUniqueIndex()
    {
        // The premise of the race: the index really does reject the second row, so the save that loses
        // fails instead of quietly storing a duplicate, and both databases report it the same way.
        using var context = CreateDbContext();
        context.ItemValues.Add(CreateItemValue(Guid.NewGuid()));
        context.SaveChanges();

        context.ItemValues.Add(CreateItemValue(Guid.NewGuid()));

        var exception = Assert.Throws<DbUpdateException>(() => context.SaveChanges());
        Assert.Equal(DatabaseErrorKind.UniqueViolation, Database.Provider.ClassifyException(exception));
    }

    [Fact]
    public void SaveItems_TwoItemsInOneBatchShareANewValue_StoreItOnce()
    {
        _service.SaveItems([CreateBook(Guid.NewGuid()), CreateBook(Guid.NewGuid())], CancellationToken.None);

        using var context = CreateDbContext();
        var stored = Assert.Single(StoredGenres(context));
        Assert.Equal(2, context.ItemValuesMap.Count(e => e.ItemValueId.Equals(stored.ItemValueId)));
    }

    [Fact]
    public void SaveItems_ValueWasStoredByAnEarlierSave_MapsOntoTheStoredRow()
    {
        // The state a repeated save converges on: once the winner's row is stored, the save that follows
        // maps onto it rather than creating one of its own.
        _service.SaveItems([CreateBook(Guid.NewGuid())], CancellationToken.None);
        _service.SaveItems([CreateBook(Guid.NewGuid())], CancellationToken.None);

        using var context = CreateDbContext();
        var stored = Assert.Single(StoredGenres(context));
        Assert.Equal(2, context.ItemValuesMap.Count(e => e.ItemValueId.Equals(stored.ItemValueId)));
    }

    [Fact]
    public void SaveItems_ReadMissesAValueThatIsStored_MapsOntoTheStoredRow()
    {
        // The interleaving of the race, played out in one thread: the winner's row is stored, the read of
        // this save does not see it, and the insert that follows runs into the unique index. A save that
        // gives up there loses the whole item, not just its genre.
        var storedId = Guid.NewGuid();
        using (var context = CreateDbContext())
        {
            context.ItemValues.Add(CreateItemValue(storedId));
            context.SaveChanges();
        }

        var itemId = Guid.NewGuid();
        _interceptor.BlindNextReadOf("ItemValues");

        _service.SaveItems([CreateBook(itemId)], CancellationToken.None);

        Assert.Equal(1, _interceptor.BlindedReads);

        using (var context = CreateDbContext())
        {
            // The row the save generated before the race must not survive: the stored one is the only one,
            // and the item hangs off it.
            var stored = Assert.Single(StoredGenres(context));
            Assert.Equal(storedId, stored.ItemValueId);
            Assert.Equal(storedId, Assert.Single(MappedValues(context, itemId)).ItemValueId);
        }
    }

    [Fact]
    public void SaveItems_ConflictIsNotOneOfItsItemValues_FailsWithoutRepeatingTheSave()
    {
        var itemId = Guid.NewGuid();
        _service.SaveItems([CreateBook(itemId)], CancellationToken.None);
        _interceptor.Reset();

        // The item and its genre are both stored, and this read is the one deciding whether the item is
        // inserted or updated: what conflicts now is the item itself, which no repeat can resolve.
        _interceptor.BlindNextReadOf("BaseItems");

        var exception = Assert.Throws<DbUpdateException>(
            () => _service.SaveItems([CreateBook(itemId)], CancellationToken.None));

        Assert.Equal(DatabaseErrorKind.UniqueViolation, Database.Provider.ClassifyException(exception));
        Assert.Equal(1, _interceptor.ReadsInATransactionOf("ItemValues"));

        // It introduced no value of its own, so there was nothing to check and the check never asked.
        Assert.Equal(0, _interceptor.ReadsOutsideATransactionOf("ItemValues"));
    }

    [Fact]
    public void SaveItems_ConflictWhileItsNewValuesAreStillFree_FailsWithoutRepeatingTheSave()
    {
        var itemId = Guid.NewGuid();
        _service.SaveItems([CreateBook(itemId)], CancellationToken.None);
        _interceptor.Reset();

        // The same conflict as above, except that this save does introduce a value of its own, so the check
        // after the failure has something to ask about. Nobody took that value, which is what tells the two
        // apart: the conflict is the item, and a repeat would only run into it again.
        _interceptor.BlindNextReadOf("BaseItems");

        var exception = Assert.Throws<DbUpdateException>(
            () => _service.SaveItems([CreateBook(itemId, OtherGenre)], CancellationToken.None));

        Assert.Equal(DatabaseErrorKind.UniqueViolation, Database.Provider.ClassifyException(exception));
        Assert.Equal(1, _interceptor.ReadsInATransactionOf("ItemValues"));
        Assert.Equal(1, _interceptor.ReadsOutsideATransactionOf("ItemValues"));

        using var context = CreateDbContext();
        Assert.Empty(context.ItemValues.Where(e => e.Type == ItemValueType.Genre && e.Value == OtherGenre));
    }

    [Fact]
    [Trait("Provider", "PostgreSql")]
    public void SaveItems_AnotherConnectionStoresTheValueFirst_MapsOntoItsRow()
    {
        Assert.SkipWhen(
            TestDatabase.SelectedProvider != TestDatabase.PostgreSql,
            "SQLite cannot host this interleaving: its writers wait for each other, so a writer that does get in between the read and the insert leaves the reading transaction with a stale snapshot instead of a duplicate row.");

        // The race itself, over two connections: a writer this server does not wait for - a second server on
        // the same database, or one that went ahead when the wait for the write permit timed out - saves an
        // item with the same new genre after this save has read the values and commits before it inserts.
        var competitorId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        _interceptor.AfterNextReadOf(
            "ItemValues",
            () => CreateService().SaveItems([CreateBook(competitorId)], CancellationToken.None));

        _service.SaveItems([CreateBook(itemId)], CancellationToken.None);

        Assert.Equal(1, _interceptor.StagedRaces);

        using var context = CreateDbContext();
        var stored = Assert.Single(StoredGenres(context));

        // The row is the competitor's, and the save that lost dropped the one it had generated for itself.
        Assert.Equal(stored.ItemValueId, Assert.Single(MappedValues(context, competitorId)).ItemValueId);
        Assert.Equal(stored.ItemValueId, Assert.Single(MappedValues(context, itemId)).ItemValueId);
    }

    protected override void Dispose(bool disposing)
    {
        BaseItem.LibraryManager = _previousLibraryManager!;
        BaseItem.ConfigurationManager = _previousConfigurationManager!;
        base.Dispose(disposing);
    }

    private static ItemValue CreateItemValue(Guid itemValueId) => new()
    {
        ItemValueId = itemValueId,
        Type = ItemValueType.Genre,
        Value = Genre,
        CleanValue = Genre.ToLowerInvariant()
    };

    private static Book CreateBook(Guid id, string genre = Genre) => new()
    {
        Id = id,
        Name = "Book",
        Genres = [genre]
    };

    private static ItemValue[] StoredGenres(JellyfinDbContext context)
        => context.ItemValues.Where(e => e.Type == ItemValueType.Genre && e.Value == Genre).ToArray();

    private static ItemValueMap[] MappedValues(JellyfinDbContext context, Guid itemId)
        => context.ItemValuesMap.Where(e => e.ItemId.Equals(itemId)).ToArray();

    private ItemPersistenceService CreateService() => new(
        CreateDbContextFactory(),
        Mock.Of<IServerApplicationHost>(),
        Database.Provider,
        NullLogger<ItemPersistenceService>.Instance);

    /// <summary>
    /// Stages the race on the reads of one table, without a second thread and without a wait: it can make
    /// one read come back empty, so that the save decides on an answer that is already out of date, and it
    /// can let another writer in once a read has been made.
    /// </summary>
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Only the query the code under test built is rewritten, by negating its own filter.")]
    private sealed class StagedRaceInterceptor : DbCommandInterceptor
    {
        private readonly List<(string Sql, bool InATransaction)> _reads = [];
        private string? _blindedTable;
        private string? _racedTable;
        private Action? _racer;

        public int BlindedReads { get; private set; }

        public int StagedRaces { get; private set; }

        public void BlindNextReadOf(string table) => _blindedTable = table;

        public void AfterNextReadOf(string table, Action racer)
        {
            _racedTable = table;
            _racer = racer;
        }

        /// <summary>
        /// Counts the reads of one table an attempt made, which run in the transaction of that attempt: one
        /// per attempt, so this is how many attempts a save took.
        /// </summary>
        /// <param name="table">The table read from.</param>
        /// <returns>The number of such reads.</returns>
        public int ReadsInATransactionOf(string table)
            => _reads.Count(read => read.InATransaction && ReadsFrom(read.Sql, table));

        /// <summary>
        /// Counts the reads of one table made outside any transaction, which is how the check after a failed
        /// attempt reads: it opens a context of its own once the attempt has unwound.
        /// </summary>
        /// <param name="table">The table read from.</param>
        /// <returns>The number of such reads.</returns>
        public int ReadsOutsideATransactionOf(string table)
            => _reads.Count(read => !read.InATransaction && ReadsFrom(read.Sql, table));

        public void Reset() => _reads.Clear();

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            _reads.Add((command.CommandText, command.Transaction is not null));

            if (_blindedTable is not null && ReadsFrom(command.CommandText, _blindedTable))
            {
                _blindedTable = null;
                command.CommandText = WithoutRows(command.CommandText);
                BlindedReads++;
            }

            return result;
        }

        public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
        {
            if (_racedTable is not null && ReadsFrom(command.CommandText, _racedTable))
            {
                var racer = _racer!;
                _racedTable = null;
                _racer = null;
                StagedRaces++;

                // The competitor reads and writes on its own connection, so nothing of it comes back here.
                racer();
            }

            return result;
        }

        private static bool ReadsFrom(string sql, string table)
            => sql.Contains($"FROM \"{table}\" AS", StringComparison.Ordinal);

        private static string WithoutRows(string sql)
        {
            // The query the save built, minus its rows, so that a read which stops looking like a filtered
            // read of one table fails the test instead of quietly passing it.
            const string Where = "WHERE ";
            var index = sql.IndexOf(Where, StringComparison.Ordinal);
            Assert.True(index >= 0, $"The read to be blinded has no filter to negate: {sql}");
            return sql.Insert(index + Where.Length, "1 = 0 AND ");
        }
    }
}
