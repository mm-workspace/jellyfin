using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Server.ServerSetupApp;
using MediaBrowser.Controller.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Migrations.Routines;

/// <summary>
/// Gives every audiobook user data row set a copy keyed by the item id.
/// </summary>
/// <remarks>
/// Untagged audiobooks no longer report their song key, so rows carried over from 10.10, which
/// only exist under that key, would not reattach when the item is deleted and added back.
/// </remarks>
[JellyfinMigration("2026-10-19T12:00:00", nameof(EnsureAudioBookIdUserDataKeys))]
[JellyfinMigrationBackup(JellyfinDb = true)]
public class EnsureAudioBookIdUserDataKeys : IAsyncMigrationRoutine
{
    private const int BatchSize = 500;

    private readonly IStartupLogger<EnsureAudioBookIdUserDataKeys> _logger;
    private readonly IDbContextFactory<JellyfinDbContext> _dbContextFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="EnsureAudioBookIdUserDataKeys"/> class.
    /// </summary>
    /// <param name="logger">The startup logger.</param>
    /// <param name="dbContextFactory">The database context factory.</param>
    public EnsureAudioBookIdUserDataKeys(
        IStartupLogger<EnsureAudioBookIdUserDataKeys> logger,
        IDbContextFactory<JellyfinDbContext> dbContextFactory)
    {
        _logger = logger;
        _dbContextFactory = dbContextFactory;
    }

    /// <inheritdoc/>
    public async Task PerformAsync(CancellationToken cancellationToken)
    {
        var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var audioBookType = typeof(AudioBook).FullName;
            var keys = await context.UserData
                .Where(e => context.BaseItems.Any(i => i.Id.Equals(e.ItemId) && i.Type == audioBookType))
                .Select(e => new { e.ItemId, e.UserId, e.CustomDataKey })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var missing = keys
                .GroupBy(e => new ItemUser(e.ItemId, e.UserId))
                .Where(g => !g.Any(e => string.Equals(e.CustomDataKey, g.Key.ItemId.ToString(), StringComparison.Ordinal)))
                .Select(g => g.Key)
                .ToList();

            if (missing.Count == 0)
            {
                _logger.LogInformation("All audiobook user data already has an item id key.");
                return;
            }

            _logger.LogInformation("Adding item id keyed user data for {Count} audiobook/user combinations.", missing.Count);

            foreach (var batch in missing.Chunk(BatchSize))
            {
                await AddIdKeyedRowsAsync(context, batch, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task AddIdKeyedRowsAsync(JellyfinDbContext context, ItemUser[] batch, CancellationToken cancellationToken)
    {
        var itemIds = batch.Select(e => e.ItemId).Distinct().ToArray();
        var wanted = batch.ToHashSet();

        var rows = await context.UserData
            .WhereOneOrMany(itemIds, e => e.ItemId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var group in rows.GroupBy(e => new ItemUser(e.ItemId, e.UserId)))
        {
            if (!wanted.Contains(group.Key))
            {
                continue;
            }

            var winner = group
                .OrderByDescending(e => e.LastPlayedDate)
                .ThenByDescending(e => e.PlayCount)
                .ThenByDescending(e => e.PlaybackPositionTicks)
                .First();

            // Copy every mapped column, so columns added after this migration was written come along too.
            var twin = (UserData)context.Entry(winner).CurrentValues.ToObject();
            twin.CustomDataKey = group.Key.ItemId.ToString();
            twin.RetentionDate = null;
            context.UserData.Add(twin);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        context.ChangeTracker.Clear();
    }

    private readonly record struct ItemUser(Guid ItemId, Guid UserId);
}
