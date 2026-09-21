using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Library;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Testing;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using Xunit;
using AudioBook = MediaBrowser.Controller.Entities.AudioBook;

namespace Jellyfin.Server.Implementations.Tests.Library;

public sealed class UserDataManagerConcurrentSaveTests : IDisposable
{
    private readonly StaleExistenceCheck _staleExistenceCheck = new();
    private readonly ITestDatabase _database;
    private readonly UserDataManager _userDataManager;
    private readonly User _user;

    public UserDataManagerConcurrentSaveTests()
    {
        _database = TestDatabase.Create(new TestDatabaseOptions { Interceptors = [_staleExistenceCheck] });
        _userDataManager = CreateUserDataManager(_database);
        _user = CreateUser(_database);
    }

    public void Dispose()
    {
        _database.Dispose();
    }

    [Fact]
    public void SaveUserData_WhenTheRowWasStoredAfterTheExistenceCheck_UpdatesItInsteadOfFailing()
    {
        var item = CreateAudioBook();
        var keys = item.GetUserDataKeys();
        using (var context = _database.CreateDbContext())
        {
            context.BaseItems.Add(new BaseItemEntity { Id = item.Id, Type = typeof(AudioBook).FullName! });

            // The row the other writer stores while this save is between its check and its insert.
            context.UserData.Add(new UserData
            {
                ItemId = item.Id,
                Item = null,
                UserId = _user.Id,
                User = null,
                CustomDataKey = keys[0],
                PlaybackPositionTicks = 111,
                PlayCount = 5
            });
            context.SaveChanges();
        }

        _staleExistenceCheck.Arm();
        _userDataManager.SaveUserData(_user, item, new UserItemData { Key = keys[0], PlaybackPositionTicks = 999, PlayCount = 2 }, UserDataSaveReason.PlaybackProgress, CancellationToken.None);

        Assert.True(_staleExistenceCheck.Applied, "No existence check was recognised, so the save was never made to miss the stored row.");

        using var check = _database.CreateDbContext();
        var rows = ReadUserData(check, item.Id);
        Assert.Equal(keys.Count, rows.Count);
        var updated = rows.Single(e => string.Equals(e.CustomDataKey, keys[0], StringComparison.Ordinal));
        Assert.Equal(999, updated.PlaybackPositionTicks);
        Assert.Equal(2, updated.PlayCount);
    }

    [Fact]
    [Trait("Provider", "PostgreSql")]
    public async Task SaveUserData_FromTwoWritersAtOnce_StoresOneRowPerKey()
    {
        var connectionString = TestDatabase.PostgreSqlConnectionString;
        Assert.SkipWhen(connectionString is null, $"{TestDatabase.PostgreSqlConnectionStringEnvironmentVariable} is not set.");

        var rendezvous = new SaveRendezvous();
        using var database = new PostgreSqlTestDatabase(connectionString, new TestDatabaseOptions { Interceptors = [rendezvous] });
        var userDataManager = CreateUserDataManager(database);
        var user = CreateUser(database);

        var item = CreateAudioBook();
        var keys = item.GetUserDataKeys();
        SeedItem(database, item);

        void Save(long positionTicks)
            => userDataManager.SaveUserData(user, item, new UserItemData { Key = keys[0], PlaybackPositionTicks = positionTicks }, UserDataSaveReason.PlaybackProgress, CancellationToken.None);

        rendezvous.Arm();
        await Task.WhenAll(
            Task.Run(() => Save(111), TestContext.Current.CancellationToken),
            Task.Run(() => Save(222), TestContext.Current.CancellationToken));

        using var check = database.CreateDbContext();
        var rows = ReadUserData(check, item.Id);
        Assert.Equal(keys.Count, rows.Count);
        Assert.All(rows, row => Assert.Contains(row.PlaybackPositionTicks, new long[] { 111, 222 }));
    }

    private static UserDataManager CreateUserDataManager(ITestDatabase database)
    {
        var config = new Mock<IServerConfigurationManager>();
        config.SetupGet(c => c.Configuration).Returns(new ServerConfiguration());
        return new UserDataManager(config.Object, database.CreateDbContextFactory(), database.Provider);
    }

    private static User CreateUser(ITestDatabase database)
    {
        var user = new User("user", "auth-provider", "reset-provider")
        {
            Id = Guid.NewGuid()
        };

        using var context = database.CreateDbContext();
        context.Users.Add(user);
        context.SaveChanges();
        return user;
    }

    private static void SeedItem(ITestDatabase database, BaseItem item)
    {
        using var context = database.CreateDbContext();
        context.BaseItems.Add(new BaseItemEntity { Id = item.Id, Type = item.GetType().FullName! });
        context.SaveChanges();
    }

    private static AudioBook CreateAudioBook()
    {
        // GetUserDataKeys(): ["Author-Series-0001Book Title", "<item id>"]
        return new AudioBook
        {
            Id = Guid.NewGuid(),
            Name = "Book Title",
            Album = "Series",
            AlbumArtists = new[] { "Author" },
            IndexNumber = 1
        };
    }

    private static List<UserData> ReadUserData(JellyfinDbContext context, Guid itemId)
    {
        return context.UserData.AsNoTracking().Where(e => e.ItemId.Equals(itemId)).ToList();
    }

    /// <summary>
    /// Makes the existence checks a save runs before its first insert find nothing, which is what a row another
    /// writer stores between such a check and that insert looks like to the save.
    /// </summary>
    private sealed class StaleExistenceCheck : DbCommandInterceptor
    {
        private bool _armed;
        private bool _inserted;

        /// <summary>
        /// Gets a value indicating whether an existence check was recognised and made to find nothing.
        /// </summary>
        public bool Applied { get; private set; }

        public void Arm() => _armed = true;

        /// <inheritdoc />
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Rewrite(command);
            return result;
        }

        /// <inheritdoc />
        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Rewrite(command);
            return result;
        }

        private void Rewrite(DbCommand command)
        {
            if (!_armed || !command.CommandText.Contains("\"UserData\"", StringComparison.Ordinal))
            {
                return;
            }

            if (command.CommandText.Contains("INSERT INTO", StringComparison.Ordinal))
            {
                // From here on the save is past the checks this stands in for, and its next attempt reads the truth.
                _inserted = true;
                return;
            }

            if (_inserted || !command.CommandText.StartsWith("SELECT EXISTS", StringComparison.Ordinal))
            {
                return;
            }

            command.Parameters.Clear();
            command.CommandText = "SELECT EXISTS (SELECT 1 FROM \"UserData\" WHERE 1 = 0)";
            Applied = true;
        }
    }

    /// <summary>
    /// Makes each of two saves wait, once both have read what is stored and before either writes, for up to a
    /// second until the other has read too. Unless the second cannot read before the first commits, both find
    /// the rows they are about to write missing.
    /// </summary>
    private sealed class SaveRendezvous : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _bothRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _armed;
        private int _reads;

        public void Arm() => _armed = true;

        /// <inheritdoc />
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            if (!_armed)
            {
                return result;
            }

            if (Interlocked.Increment(ref _reads) == 2)
            {
                _bothRead.SetResult();
            }

            Task.WhenAny(_bothRead.Task, Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken)).Wait(TestContext.Current.CancellationToken);
            return result;
        }
    }
}
