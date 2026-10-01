using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Jellyfin.Server.Implementations.FullSystemBackup;

/// <summary>
/// Writes the rows a backup holds for one table, in batches and after the rows they reference.
/// </summary>
/// <remarks>
/// <para>
/// Rows are saved and then forgotten a batch at a time, so that restoring a large library does not hold the whole
/// database in one change tracker. Every batch is written inside the restore transaction, so a failure in a later
/// batch still rolls the earlier ones back.
/// </para>
/// <para>
/// Entity Framework orders the rows of a single batch by their foreign keys, so only a row whose principal ends up
/// in a later batch has to wait, and it waits on the key it needs rather than being looked at again and again.
/// Rows that reference each other, or a row the archive does not hold, are written without that reference and are
/// given it back once every row of the table exists.
/// </para>
/// </remarks>
internal sealed class RestoreTableWriter
{
    private readonly JellyfinDbContext _dbContext;

    /// <summary>
    /// The nullable properties through which a row of this table points at another row of the same table.
    /// </summary>
    private readonly List<(PropertyInfo Property, string Name)> _selfReferences = [];

    /// <summary>
    /// The single property those references point at, or <c>null</c> when the table has none.
    /// </summary>
    private readonly PropertyInfo? _referencedKey;

    private readonly HashSet<object> _writtenKeys = [];

    /// <summary>
    /// The rows that cannot go in yet, by the key of the row each of them is waiting for.
    /// </summary>
    private readonly Dictionary<object, List<object>> _waiting = [];

    private readonly Queue<object> _writable = new();
    private readonly List<(object Entity, IReadOnlyList<(string Name, object Value)> Values)> _withoutReference = [];

    private readonly int _batchSize;
    private int _staged;
    private long _rows;

    /// <summary>
    /// Initializes a new instance of the <see cref="RestoreTableWriter"/> class.
    /// </summary>
    /// <param name="dbContext">The context the restore transaction runs on.</param>
    /// <param name="entityType">The entity type mapped to the table.</param>
    /// <param name="batchSize">The number of rows to write before saving them.</param>
    public RestoreTableWriter(JellyfinDbContext dbContext, IEntityType entityType, int batchSize)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        _dbContext = dbContext;
        _batchSize = batchSize;

        var selfReferencing = entityType.GetForeignKeys().Where(e => e.PrincipalEntityType.Equals(entityType)).ToArray();
        foreach (var foreignKey in selfReferencing)
        {
            var dependent = foreignKey.Properties.Count == 1 ? foreignKey.Properties[0] : null;
            var principal = foreignKey.PrincipalKey.Properties.Count == 1 ? foreignKey.PrincipalKey.Properties[0] : null;
            if (dependent?.PropertyInfo is not { } dependentProperty
                || principal?.PropertyInfo is not { } principalProperty
                || !dependent.IsNullable
                || (_referencedKey is not null && _referencedKey != principalProperty))
            {
                _selfReferences.Clear();
                _referencedKey = null;
                break;
            }

            _referencedKey = principalProperty;
            _selfReferences.Add((dependentProperty, dependent.Name));
        }

        if (selfReferencing.Length > 0 && _selfReferences.Count == 0)
        {
            // A reference this cannot read or clear leaves the rows unorderable here, so the table goes in as one
            // batch and Entity Framework orders it, as it did before restores were batched at all.
            _batchSize = int.MaxValue;
        }
    }

    /// <summary>
    /// Adds one row of the backup, writing a batch whenever enough rows have been added.
    /// </summary>
    /// <param name="entity">The row.</param>
    /// <param name="cancellationToken">The token to cancel the operation.</param>
    /// <returns>A task representing the operation.</returns>
    public async Task AddAsync(object entity, CancellationToken cancellationToken)
    {
        _rows++;
        if (FindMissingReference(entity) is { } missing)
        {
            Wait(missing, entity);
            return;
        }

        await WriteAsync(entity, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the rows that were still waiting for another row of the same table.
    /// </summary>
    /// <param name="cancellationToken">The token to cancel the operation.</param>
    /// <returns>The number of rows the backup held for this table.</returns>
    public async Task<long> CompleteAsync(CancellationToken cancellationToken)
    {
        // What is still waiting points at a row that points back at it, or at a row the archive does not hold. Such
        // a row goes in without that reference, which frees whatever waited for it, and is given the reference back
        // below once every row of the table exists.
        while (_waiting.Count > 0)
        {
            var (key, waiting) = _waiting.First();
            var entity = waiting[^1];
            waiting.RemoveAt(waiting.Count - 1);
            if (waiting.Count == 0)
            {
                _waiting.Remove(key);
            }

            var values = new List<(string Name, object Value)>();
            foreach (var (property, name) in _selfReferences)
            {
                if (property.GetValue(entity) is { } value && !_writtenKeys.Contains(value))
                {
                    values.Add((name, value));
                    property.SetValue(entity, null);
                }
            }

            _withoutReference.Add((entity, values));
            await WriteAsync(entity, cancellationToken).ConfigureAwait(false);
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);

        foreach (var (entity, values) in _withoutReference)
        {
            var entry = _dbContext.Attach(entity);
            foreach (var (name, value) in values)
            {
                entry.Property(name).CurrentValue = value;
            }

            if (++_staged >= _batchSize)
            {
                await SaveAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        _withoutReference.Clear();
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        return _rows;
    }

    /// <summary>
    /// The key of the first row this row points at that has not been written yet.
    /// </summary>
    /// <param name="entity">The row.</param>
    /// <returns>The key, or <c>null</c> when the row can be written now.</returns>
    private object? FindMissingReference(object entity)
    {
        foreach (var (property, _) in _selfReferences)
        {
            if (property.GetValue(entity) is { } value && !_writtenKeys.Contains(value))
            {
                return value;
            }
        }

        return null;
    }

    private void Wait(object key, object entity)
    {
        if (!_waiting.TryGetValue(key, out var waiting))
        {
            _waiting[key] = waiting = [];
        }

        waiting.Add(entity);
    }

    /// <summary>
    /// Writes a row, and then every row that was only waiting for it.
    /// </summary>
    /// <param name="entity">The row.</param>
    /// <param name="cancellationToken">The token to cancel the operation.</param>
    /// <returns>A task representing the operation.</returns>
    private async Task WriteAsync(object entity, CancellationToken cancellationToken)
    {
        _writable.Enqueue(entity);
        while (_writable.TryDequeue(out var next))
        {
            await StageAsync(next, cancellationToken).ConfigureAwait(false);
            if (_referencedKey?.GetValue(next) is not { } key || !_waiting.Remove(key, out var waiting))
            {
                continue;
            }

            foreach (var row in waiting)
            {
                if (FindMissingReference(row) is { } missing)
                {
                    Wait(missing, row);
                }
                else
                {
                    _writable.Enqueue(row);
                }
            }
        }
    }

    private async Task StageAsync(object entity, CancellationToken cancellationToken)
    {
        if (_referencedKey?.GetValue(entity) is { } key)
        {
            _writtenKeys.Add(key);
        }

        _dbContext.Add(entity);
        if (++_staged >= _batchSize)
        {
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (_staged == 0)
        {
            return;
        }

        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _dbContext.ChangeTracker.Clear();
        _staged = 0;
    }
}
