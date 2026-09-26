using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Library;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Sqlite;
using Jellyfin.Server.Implementations.Item;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Item;

/// <summary>
/// Reattaching reads the detached rows and then rewrites them, which is only safe because the transaction it
/// runs in keeps other writers out from BEGIN. Two real connections are needed to see that, so these run
/// against a database on disk rather than the shared in-memory one. What they pin is the ordering, not which
/// of the two mechanisms produced it: on SQLite the in-process write permit and the database write lock
/// Microsoft.Data.Sqlite takes with BEGIN IMMEDIATE both keep the second writer out.
/// </summary>
[Trait("Provider", "Sqlite")]
public sealed class ItemPersistenceReattachConcurrencyTests : IDisposable
{
    private static readonly Guid _userId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTime _watched = new(2023, 8, 14, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime _started = new(2021, 12, 31, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), "jellyfin-reattach-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly SerializedWriteLockBehavior _behavior = new(NullLogger<SerializedWriteLockBehavior>.Instance);
    private readonly ILibraryManager? _previousLibraryManager;
    private readonly IServerConfigurationManager? _previousConfigurationManager;
    private readonly IRecordingsManager? _previousRecordingsManager;
    private readonly User _user = new("user", "auth-provider", "reset-provider") { Id = _userId };

    public ItemPersistenceReattachConcurrencyTests()
    {
        // BaseItem resolves these through process-wide statics; restored in Dispose.
        _previousLibraryManager = BaseItem.LibraryManager;
        _previousConfigurationManager = BaseItem.ConfigurationManager;
        _previousRecordingsManager = Video.RecordingsManager;

        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(l => l.GetCollectionFolders(It.IsAny<BaseItem>())).Returns([]);
        BaseItem.LibraryManager = libraryManager.Object;

        var configurationManager = new Mock<IServerConfigurationManager>();
        configurationManager.Setup(c => c.Configuration).Returns(new ServerConfiguration());
        BaseItem.ConfigurationManager = configurationManager.Object;

        // Video.SourceType consults this before it can produce user data keys.
        Video.RecordingsManager = new Mock<IRecordingsManager>().Object;

        // The schema is created without the behaviour under test, so its state is untouched when a test starts.
        using var schemaContext = CreateContext(new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
        schemaContext.Database.EnsureCreated();
    }

    [Fact]
    public async Task ReattachUserData_WhileAPlaybackReportWaits_ReconcilesBeforeTheReportLands()
    {
        var movie = CreateMovie();
        var keys = movie.GetUserDataKeys();
        Seed(movie);

        // The keys of an earlier incarnation of the item, detached by its deletion, next to the rows the
        // item already holds under the same keys.
        AddRows(BaseItemRepository.PlaceholderId, [keys[0], keys[1]], _watched, playCount: 9, positionTicks: 0, played: true);
        AddRows(movie.Id, keys, _started, playCount: 1, positionTicks: 490);

        var reportBeginning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? report = null;

        // Runs once the reattach has read the detached rows, inside the transaction that keeps writers out.
        var pause = new ReadPause(
            "UserData",
            async cancellationToken =>
            {
                report = Task.Run(() => SavePlaybackReport(movie, reportBeginning), cancellationToken);

                // Waiting until the report is about to begin its own transaction is what makes the
                // interleaving deterministic rather than timed: the reattach goes on to delete and re-insert
                // with a writer already queued behind it, which is the window the race needs.
                await reportBeginning.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            });

        var service = new ItemPersistenceService(
            new ContextFactory(() => CreateContext(_behavior, pause)),
            new Mock<IServerApplicationHost>().Object,
            new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance),
            NullLogger<ItemPersistenceService>.Instance);

        await service.ReattachUserDataAsync(movie, TestContext.Current.CancellationToken);
        await report!.WaitAsync(TestContext.Current.CancellationToken);

        using var context = CreateContext(_behavior);
        var rows = context.UserData.Where(e => e.ItemId.Equals(movie.Id)).ToList();
        Assert.Equal(keys.Order(StringComparer.Ordinal), rows.Select(e => e.CustomDataKey).Order(StringComparer.Ordinal));

        // The report landing on every key is what carries the ordering: had it committed between the
        // reattach's read and its insert, the insert would have collided or written the stale read back.
        Assert.All(rows, row => Assert.Equal(500, row.PlaybackPositionTicks));
        Assert.Empty(context.UserData.Where(e => e.ItemId.Equals(BaseItemRepository.PlaceholderId)));
    }

    public void Dispose()
    {
        BaseItem.LibraryManager = _previousLibraryManager!;
        BaseItem.ConfigurationManager = _previousConfigurationManager!;
        Video.RecordingsManager = _previousRecordingsManager!;
        _behavior.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(_databasePath);
    }

    private static Movie CreateMovie()
    {
        // GetUserDataKeys(): ["497698", "tt3480822", "<item id>"]
        return new Movie
        {
            Id = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddd06"),
            Name = "Black Widow",
            ProviderIds = new Dictionary<string, string>
            {
                ["Tmdb"] = "497698",
                ["Imdb"] = "tt3480822"
            }
        };
    }

    /// <summary>
    /// Reports a playback position for the item, as a client does while the library scan runs. Signals once its
    /// transaction is starting, which is where it queues for the write permit.
    /// </summary>
    private void SavePlaybackReport(BaseItem item, TaskCompletionSource beginning)
    {
        var config = new Mock<IServerConfigurationManager>();
        config.SetupGet(c => c.Configuration).Returns(new ServerConfiguration());

        var recorder = new TransactionStartRecorder(() => beginning.TrySetResult());
        var userDataManager = new UserDataManager(
            config.Object,
            new ContextFactory(() => CreateContext(_behavior, recorder)),
            new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance));

        userDataManager.SaveUserData(
            _user,
            item,
            new UserItemData { Key = item.GetUserDataKeys()[0], PlaybackPositionTicks = 500 },
            UserDataSaveReason.PlaybackProgress,
            CancellationToken.None);
    }

    private void Seed(BaseItem item)
    {
        using var context = CreateContext(_behavior);
        context.Users.Add(_user);
        context.BaseItems.Add(new BaseItemEntity { Id = item.Id, Type = item.GetType().FullName! });
        context.SaveChanges();
    }

    private void AddRows(Guid itemId, IReadOnlyList<string> keys, DateTime lastPlayed, int playCount, long positionTicks, bool played = false)
    {
        using var context = CreateContext(_behavior);
        foreach (var key in keys)
        {
            context.UserData.Add(new UserData
            {
                ItemId = itemId,
                Item = null,
                UserId = _userId,
                User = null,
                CustomDataKey = key,
                LastPlayedDate = lastPlayed,
                PlayCount = playCount,
                PlaybackPositionTicks = positionTicks,
                Played = played
            });
        }

        context.SaveChanges();
    }

    private JellyfinDbContext CreateContext(IEntityFrameworkCoreLockingBehavior behavior, params IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<JellyfinDbContext>().UseSqlite($"Data Source={_databasePath};Pooling=false");

        // Added first, so they run before the behaviour's interceptors.
        builder.AddInterceptors(interceptors);
        behavior.Initialise(builder);
        return new JellyfinDbContext(builder.Options, NullLogger<JellyfinDbContext>.Instance, new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance), behavior);
    }

    private sealed class ContextFactory : IDbContextFactory<JellyfinDbContext>
    {
        private readonly Func<JellyfinDbContext> _createDbContext;

        public ContextFactory(Func<JellyfinDbContext> createDbContext)
        {
            _createDbContext = createDbContext;
        }

        public JellyfinDbContext CreateDbContext() => _createDbContext();
    }

    /// <summary>
    /// Runs code once, after the first read touching a table, while the reading transaction still holds the permit.
    /// </summary>
    private sealed class ReadPause : DbCommandInterceptor
    {
        private readonly string _table;
        private readonly Func<CancellationToken, Task> _read;
        private int _ran;

        public ReadPause(string table, Func<CancellationToken, Task> read)
        {
            _table = table;
            _read = read;
        }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(_table, StringComparison.Ordinal) && Interlocked.Exchange(ref _ran, 1) == 0)
            {
                await _read(cancellationToken).ConfigureAwait(false);
            }

            return result;
        }
    }

    /// <summary>
    /// Signals before a transaction asks for the write permit, which the behaviour's own interceptor does next.
    /// </summary>
    private sealed class TransactionStartRecorder : DbTransactionInterceptor
    {
        private readonly Action _starting;

        public TransactionStartRecorder(Action starting)
        {
            _starting = starting;
        }

        public override InterceptionResult<DbTransaction> TransactionStarting(DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result)
        {
            _starting();
            return base.TransactionStarting(connection, eventData, result);
        }
    }
}
