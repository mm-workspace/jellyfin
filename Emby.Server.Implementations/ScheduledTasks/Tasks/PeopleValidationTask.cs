using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Library.Validators;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.ScheduledTasks.Tasks;

/// <summary>
/// Class PeopleValidationTask.
/// </summary>
public class PeopleValidationTask : IScheduledTask, IConfigurableScheduledTask
{
    /// <summary>
    /// How many duplicated names one pass of the deduplication reads.
    /// </summary>
    private const int DefaultNamePartitionSize = 100;

    /// <summary>
    /// How often the rows of one duplicated name are merged before a collision with another writer is left to the
    /// caller.
    /// </summary>
    private const int MaxMergeAttempts = 3;

    private readonly ILibraryManager _libraryManager;
    private readonly ILocalizationManager _localization;
    private readonly IDbContextFactory<JellyfinDbContext> _dbContextFactory;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<PeopleValidationTask> _logger;
    private readonly ILogger<PeopleValidator> _validatorLogger;
    private readonly IItemTypeLookup _itemTypeLookup;
    private readonly IJellyfinDatabaseProvider _databaseProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="PeopleValidationTask" /> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="localization">Instance of the <see cref="ILocalizationManager"/> interface.</param>
    /// <param name="dbContextFactory">Instance of the <see cref="IDbContextFactory{TContext}"/> interface.</param>
    /// <param name="fileSystem">Instance of the <see cref="IFileSystem"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{PeopleValidationTask}"/> interface.</param>
    /// <param name="validatorLogger">Instance of the <see cref="ILogger{PeopleValidator}"/> interface.</param>
    /// <param name="itemTypeLookup">Instance of the <see cref="IItemTypeLookup"/> interface.</param>
    /// <param name="databaseProvider">The database provider, which tells this one what a failure the database reported means.</param>
    public PeopleValidationTask(
        ILibraryManager libraryManager,
        ILocalizationManager localization,
        IDbContextFactory<JellyfinDbContext> dbContextFactory,
        IFileSystem fileSystem,
        ILogger<PeopleValidationTask> logger,
        ILogger<PeopleValidator> validatorLogger,
        IItemTypeLookup itemTypeLookup,
        IJellyfinDatabaseProvider databaseProvider)
    {
        _libraryManager = libraryManager;
        _localization = localization;
        _dbContextFactory = dbContextFactory;
        _fileSystem = fileSystem;
        _logger = logger;
        _validatorLogger = validatorLogger;
        _itemTypeLookup = itemTypeLookup;
        _databaseProvider = databaseProvider;
    }

    /// <inheritdoc />
    public string Name => _localization.GetLocalizedString("TaskRefreshPeople");

    /// <inheritdoc />
    public string Description => _localization.GetLocalizedString("TaskRefreshPeopleDescription");

    /// <inheritdoc />
    public string Category => _localization.GetLocalizedString("TasksLibraryCategory");

    /// <inheritdoc />
    public string Key => "RefreshPeople";

    /// <inheritdoc />
    public bool IsHidden => false;

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public bool IsLogged => true;

    /// <summary>
    /// Gets how many duplicated names one pass of the deduplication reads.
    /// </summary>
    /// <value>The number of names one pass reads.</value>
    internal int NamePartitionSize { get; init; } = DefaultNamePartitionSize;

    /// <summary>
    /// Creates the triggers that define when the task will run.
    /// </summary>
    /// <returns>An <see cref="IEnumerable{TaskTriggerInfo}"/> containing the default trigger infos for this task.</returns>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromDays(7).Ticks
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        // People validation performs heavy database writes that contend with an active library scan.
        // Defer it until the scan has finished; the task will run again on its next trigger.
        if (_libraryManager.IsScanRunning)
        {
            _logger.LogInformation("Skipping people validation because a library scan is currently running.");
            return;
        }

        // Phase 1: Deduplicate and remove orphaned people (0-33%)
        var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            IProgress<double> subProgress = new Progress<double>((val) => progress.Report(val / 3));
            var dupQuery = context.Peoples
                    .GroupBy(e => new { e.Name, e.PersonType })
                    .Where(e => e.Count() > 1)
                    .OrderBy(e => e.Key.Name)
                    .ThenBy(e => e.Key.PersonType)
                    .Select(e => e.Select(f => f.Id).ToArray());

            var total = dupQuery.Count();

            var namesLeftBehind = 0;
            var namesRead = 0;
            int nameCounter;
            var buffer = ArrayPool<Guid[]>.Shared.Rent(NamePartitionSize)!;
            try
            {
                do
                {
                    nameCounter = 0;

                    // Every pass reads the duplicated names from the start again and relies on the passes before it
                    // having collapsed the ones they read, so a name they could not collapse would be read for as
                    // long as the task runs. Paging past those is what keeps a pass moving on: the names still
                    // duplicated that sort before the first one never read are exactly the ones left behind.
                    // Another writer can make that count off by a name, either way; what a pass skips that way is
                    // read by the next run of the task.
                    await foreach (var name in dupQuery
                        .Skip(namesLeftBehind)
                        .Take(NamePartitionSize)
                        .AsAsyncEnumerable()
                        .WithCancellation(cancellationToken)
                        .ConfigureAwait(false))
                    {
                        buffer[nameCounter++] = name;
                    }

                    for (int i = 0; i < nameCounter; i++)
                    {
                        var credits = buffer[i];
                        var removed = await MergeDuplicatesAsync(context, credits, cancellationToken).ConfigureAwait(false);
                        if (removed < credits.Length - 1)
                        {
                            namesLeftBehind++;
                        }

                        subProgress.Report(100f / total * (namesRead + i));
                    }

                    namesRead += nameCounter;
                } while (nameCounter == NamePartitionSize && !cancellationToken.IsCancellationRequested);
            }
            finally
            {
                ArrayPool<Guid[]>.Shared.Return(buffer);
            }

            if (namesLeftBehind > 0)
            {
                _logger.LogInformation("Leaving {Count} duplicated names to the next run; a row of each was left in place.", namesLeftBehind);
            }

            var peopleToDelete = await context.Peoples
                .Where(p => !context.PeopleBaseItemMap.Any(m => m.PeopleId.Equals(p.Id)))
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
            _logger.LogInformation("Removed {Count} orphaned people.", peopleToDelete);

            subProgress.Report(100);
        }

        // Phase 2: Validate people (33-66%). Runs after orphaned PeopleBaseItemMap entries are
        // cleaned up above, so dead people are removed in a single pass instead of requiring a second run.
        IProgress<double> validateProgress = new Progress<double>((val) => progress.Report((val / 3) + 33));
        await new PeopleValidator(_libraryManager, _validatorLogger)
            .Run(validateProgress, cancellationToken)
            .ConfigureAwait(false);

        // Phase 3: Refresh images for people missing them (66-100%)
        IProgress<double> refreshProgress = new Progress<double>((val) => progress.Report((val / 3) + 66));
        await RefreshPeopleImagesAsync(refreshProgress, cancellationToken).ConfigureAwait(false);

        progress.Report(100);
    }

    /// <summary>
    /// Moves the mappings of the rows one name is duplicated in onto the row kept for it, then removes them.
    /// </summary>
    /// <remarks>
    /// The statements are one transaction, which is what keeps every writer in this process out of the window
    /// between a move and the removal that follows it: writes there queue up one statement at a time, and a
    /// transaction holds that turn from before it begins until it ends. One transaction covers the whole name
    /// rather than each of its rows, so a name of many rows hands that turn over once.
    /// </remarks>
    /// <param name="context">The database context.</param>
    /// <param name="credits">The credits one name and type is stored in, the first of which keeps the name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>How many of the duplicated credits were removed.</returns>
    private async Task<int> MergeDuplicatesAsync(JellyfinDbContext context, Guid[] credits, CancellationToken cancellationToken)
    {
        var keptCredit = credits[0];
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await using (transaction.ConfigureAwait(false))
                {
                    var removed = 0;

                    // One duplicate at a time, so the mappings the merge before it moved onto the kept credit
                    // are the rows the next one is compared against.
                    foreach (var duplicate in credits[1..])
                    {
                        removed += await MergeCreditAsync(context, duplicate, keptCredit, cancellationToken).ConfigureAwait(false);
                    }

                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return removed;
                }
            }
            catch (Exception exception) when (attempt < MaxMergeAttempts
                && _databaseProvider.ClassifyException(exception) is DatabaseErrorKind.UniqueViolation)
            {
                // Another writer credited the kept credit for a role a duplicate holds, after the statement that
                // removes the mappings which would collide with it had already read: the move then met a mapping
                // that is there. Such a writer does not queue with this transaction, being a second server on this
                // database or a writer that went ahead after waiting out the write permit.
                //
                // Repeating the merge is what settles it. The violation is raised before the commit, so the
                // transaction rolls back and the attempt that failed stored nothing, and the next attempt's first
                // statement removes the mapping that collided, after which the move has nothing to meet. Only a
                // unique violation is repeated: a transient failure says the write may yet have gone through.
            }
        }
    }

    /// <summary>
    /// Moves the mappings of one credit onto the credit kept for that name and type, then removes it. Runs inside
    /// the transaction of the name it belongs to.
    /// </summary>
    /// <param name="context">The database context.</param>
    /// <param name="duplicate">The credit whose mappings are moved away.</param>
    /// <param name="keptCredit">The credit that keeps the name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>1 if the duplicated credit was removed, 0 if it was left in place.</returns>
    private static async Task<int> MergeCreditAsync(JellyfinDbContext context, Guid duplicate, Guid keptCredit, CancellationToken cancellationToken)
    {
        // A mapping of the duplicate and one of the kept credit that name the same item and role are one row
        // under the key (ItemId, PeopleId, Role), so moving the duplicate's onto the kept credit would collide
        // with the row that is already there. The kept credit carries that mapping either way.
        await context.PeopleBaseItemMap
            .Where(map => map.PeopleId.Equals(duplicate)
                && context.PeopleBaseItemMap.Any(kept =>
                    kept.PeopleId.Equals(keptCredit) && kept.ItemId.Equals(map.ItemId) && kept.Role == map.Role))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        await context.PeopleBaseItemMap
            .Where(map => map.PeopleId.Equals(duplicate))
            .ExecuteUpdateAsync(e => e.SetProperty(f => f.PeopleId, keptCredit), cancellationToken)
            .ConfigureAwait(false);

        // Removing a credit takes every mapping that names it with it, so remove it only while nothing maps to
        // it. A second server on this database, or a writer that went ahead after waiting out the write permit,
        // can map an item to it after the move above; its mapping is what the removal would silently delete.
        //
        // That keeps a mapping which is committed by the time this statement reads. One committed while the
        // statement waits is still lost: the condition was evaluated on the snapshot taken before the wait, and
        // the cascade that follows deletes by the row's current state. Holding that off needs a lock on the
        // duplicated credit conflicting with the inserter's foreign key check, which no provider-neutral query
        // takes.
        return await context.Peoples
            .Where(credit => credit.Id.Equals(duplicate)
                && !context.PeopleBaseItemMap.Any(map => map.PeopleId.Equals(credit.Id)))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RefreshPeopleImagesAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var thirtyDaysAgo = DateTime.UtcNow.AddDays(-30);
        var personTypeName = _itemTypeLookup.BaseItemKindNames[BaseItemKind.Person];

        List<Guid> peopleIds;

        var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            // Read the candidates in one go rather than paging them. A refresh stamps the person and takes
            // it out of this set, so a growing offset over a shrinking set walks past people it never visits.
            peopleIds = await context.BaseItems
                .AsNoTracking()
                .Where(b => b.Type == personTypeName)
                .Where(b => b.DateLastRefreshed == null || b.DateLastRefreshed < thirtyDaysAgo)
                .Where(b =>
                    !b.Images!.Any(i => i.ImageType == ImageInfoImageType.Primary) ||
                    string.IsNullOrEmpty(b.Overview))
                .OrderBy(b => b.Id)
                .Select(b => b.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        _logger.LogDebug("Found {Count} people needing image/overview refresh", peopleIds.Count);

        if (peopleIds.Count == 0)
        {
            progress.Report(100);
            return;
        }

        var numComplete = 0;
        var numRefreshed = 0;

        foreach (var personId in peopleIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await RefreshPersonAsync(personId, cancellationToken).ConfigureAwait(false))
            {
                numRefreshed++;
            }

            numComplete++;
            progress.Report(100.0 * numComplete / peopleIds.Count);
        }

        _logger.LogInformation("Refreshed metadata for {Count} people missing images or overview", numRefreshed);
    }

    private async Task<bool> RefreshPersonAsync(Guid personId, CancellationToken cancellationToken)
    {
        try
        {
            if (_libraryManager.GetItemById(personId) is not Person item)
            {
                return false;
            }

            var hasImage = item.HasImage(MediaBrowser.Model.Entities.ImageType.Primary);
            var hasOverview = !string.IsNullOrEmpty(item.Overview);

            var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
            {
                ImageRefreshMode = hasImage ? MetadataRefreshMode.ValidationOnly : MetadataRefreshMode.FullRefresh,
                MetadataRefreshMode = hasOverview ? MetadataRefreshMode.ValidationOnly : MetadataRefreshMode.FullRefresh
            };

            await item.RefreshMetadata(options, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing images for person {PersonId}", personId);
            return false;
        }
    }
}
