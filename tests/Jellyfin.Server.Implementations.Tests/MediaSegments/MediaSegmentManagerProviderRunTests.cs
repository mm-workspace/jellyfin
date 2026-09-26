using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Database.Testing;
using Jellyfin.Server.Implementations.MediaSegments;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.MediaSegments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.MediaSegments;

/// <summary>
/// Covers running the segment providers of an item: what the segments they return are compared against, and how often
/// the stored segments are read while a provider, which is plugin code of unknown duration, runs between the two.
/// </summary>
public sealed class MediaSegmentManagerProviderRunTests : IDisposable
{
    private const string ProviderName = "Provider";
    private const string TheSegmentLookup = "FROM \"MediaSegments\"";

    private static readonly Guid _itemId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly StatementCounter _statements = new();
    private readonly ITestDatabase _database;

    public MediaSegmentManagerProviderRunTests()
    {
        _database = TestDatabase.Create(new TestDatabaseOptions
        {
            ApplicationPaths = Mock.Of<IApplicationPaths>(),
            Interceptors = [_statements]
        });
    }

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task RunSegmentPluginProviders_WhileAProviderRuns_ReadsTheStoredSegmentsOnce()
    {
        var provider = new FakeSegmentProvider([Segment(200, 300)]);
        var manager = CreateManager(provider);
        await StoreSegment(manager, provider, Segment(0, 100));

        _statements.Reset();
        await manager.RunSegmentPluginProviders(CreateItem(), new LibraryOptions(), false, TestContext.Current.CancellationToken);

        Assert.Equal(1, _statements.Count(TheSegmentLookup));
        Assert.Equal([(200L, 300L)], await ReadStoredSegments(manager));
    }

    [Fact]
    public async Task RunSegmentPluginProviders_WhenTheProviderReturnsWhatIsStored_KeepsTheStoredSegments()
    {
        var provider = new FakeSegmentProvider([Segment(0, 100)]);
        var manager = CreateManager(provider);
        await StoreSegment(manager, provider, Segment(0, 100));
        var storedId = StoredSegmentId();

        await manager.RunSegmentPluginProviders(CreateItem(), new LibraryOptions(), false, TestContext.Current.CancellationToken);

        // The rows themselves are kept, rather than being deleted and written again with what they already said.
        Assert.Equal(storedId, StoredSegmentId());
    }

    [Fact]
    public async Task RunSegmentPluginProviders_WhenTheProviderChangesWhatItWasGiven_StoresWhatItReturned()
    {
        // A provider that hands back the segments it was given, having moved them, is the case the comparison has to
        // read as a change: it is told what is stored by a list of this side's, not by the copies the provider held.
        var provider = new FakeSegmentProvider([], request =>
        {
            foreach (var segment in request.ExistingSegments)
            {
                segment.StartTicks += 50;
                segment.EndTicks += 50;
            }

            return request.ExistingSegments;
        });

        var manager = CreateManager(provider);
        await StoreSegment(manager, provider, Segment(0, 100));

        await manager.RunSegmentPluginProviders(CreateItem(), new LibraryOptions(), false, TestContext.Current.CancellationToken);

        Assert.Equal([(50L, 150L)], await ReadStoredSegments(manager));
    }

    [Fact]
    public async Task RunSegmentPluginProviders_WithOverwrite_ReplacesTheStoredSegmentsWithoutReadingThem()
    {
        var provider = new FakeSegmentProvider([Segment(200, 300)]);
        var manager = CreateManager(provider);
        await StoreSegment(manager, provider, Segment(0, 100));

        _statements.Reset();
        await manager.RunSegmentPluginProviders(CreateItem(), new LibraryOptions(), true, TestContext.Current.CancellationToken);

        Assert.Equal(0, _statements.Count(TheSegmentLookup));
        Assert.Equal([(200L, 300L)], await ReadStoredSegments(manager));
    }

    private static MediaSegmentDto Segment(long startTicks, long endTicks) => new()
    {
        ItemId = _itemId,
        Type = MediaSegmentType.Intro,
        StartTicks = startTicks,
        EndTicks = endTicks
    };

    private static BaseItem CreateItem() => new Movie { Id = _itemId, Name = "Movie", Path = "/movies/movie.mkv" };

    private static async Task StoreSegment(MediaSegmentManager manager, IMediaSegmentProvider provider, MediaSegmentDto segment)
    {
        var providerId = manager.GetSupportedProviders(CreateItem()).Single(e => e.Name == provider.Name).Id;
        await manager.CreateSegmentAsync(segment, providerId);
    }

    private static async Task<(long StartTicks, long EndTicks)[]> ReadStoredSegments(MediaSegmentManager manager)
    {
        var segments = await manager.GetSegmentsAsync(CreateItem(), null, new LibraryOptions());
        return segments.Select(e => (e.StartTicks, e.EndTicks)).ToArray();
    }

    private Guid StoredSegmentId()
    {
        using var context = _database.CreateDbContext();
        return Assert.Single(context.MediaSegments.AsNoTracking().ToArray()).Id;
    }

    private MediaSegmentManager CreateManager(IMediaSegmentProvider provider)
        => new(NullLogger<MediaSegmentManager>.Instance, _database.CreateDbContextFactory(), [provider]);

    /// <summary>
    /// A segment provider standing in for plugin code: it answers with what the test gave it, and may change the
    /// copies of the stored segments it was handed on the way.
    /// </summary>
    private sealed class FakeSegmentProvider : IMediaSegmentProvider
    {
        private readonly IReadOnlyList<MediaSegmentDto> _segments;
        private readonly Func<MediaSegmentGenerationRequest, IReadOnlyList<MediaSegmentDto>>? _answer;

        public FakeSegmentProvider(IReadOnlyList<MediaSegmentDto> segments, Func<MediaSegmentGenerationRequest, IReadOnlyList<MediaSegmentDto>>? answer = null)
        {
            _segments = segments;
            _answer = answer;
        }

        /// <inheritdoc />
        public string Name => ProviderName;

        /// <inheritdoc />
        public Task<IReadOnlyList<MediaSegmentDto>> GetMediaSegments(MediaSegmentGenerationRequest request, CancellationToken cancellationToken)
            => Task.FromResult(_answer is null ? _segments : _answer(request));

        /// <inheritdoc />
        public ValueTask<bool> Supports(BaseItem item) => ValueTask.FromResult(true);

        /// <inheritdoc />
        public Task CleanupExtractedData(Guid itemId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// Counts the statements that read from the database, so a read that happens twice can be told from one that
    /// happens once. Writes are not counted, because only the statements that fetch rows are in question here.
    /// </summary>
    private sealed class StatementCounter : DbCommandInterceptor
    {
        private readonly List<string> _statements = [];

        /// <summary>
        /// Forgets the reads counted so far.
        /// </summary>
        public void Reset()
        {
            lock (_statements)
            {
                _statements.Clear();
            }
        }

        /// <summary>
        /// Counts the reads whose text contains the given text.
        /// </summary>
        /// <param name="text">The text identifying the statement.</param>
        /// <returns>How often it went to the database.</returns>
        public int Count(string text)
        {
            lock (_statements)
            {
                return _statements.Count(statement => statement.Contains(text, StringComparison.Ordinal));
            }
        }

        /// <inheritdoc />
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }

        /// <inheritdoc />
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Record(command);
            return result;
        }

        private void Record(DbCommand command)
        {
            lock (_statements)
            {
                _statements.Add(command.CommandText);
            }
        }
    }
}
