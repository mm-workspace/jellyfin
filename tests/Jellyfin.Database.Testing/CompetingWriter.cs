using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Jellyfin.Database.Testing;

/// <summary>
/// Lets another writer store its rows in the window between what a call reads and what it writes, which is the window
/// a second server on the same database writes into and the one the write lock of a single server does not cover. It
/// gets in just before the next save of any context of the database it was added to, at which point that save has read
/// everything it is going to read and has opened no transaction yet. It is disarmed before the other writer is let in,
/// so the writes of that writer itself pass through untouched.
/// </summary>
public sealed class CompetingWriter : SaveChangesInterceptor
{
    private Action? _write;
    private Func<Task>? _writeAsync;

    /// <summary>
    /// Gets a value indicating whether the other writer was let in.
    /// </summary>
    public bool Wrote { get; private set; }

    /// <summary>
    /// Lets <paramref name="write"/> in before the next save.
    /// </summary>
    /// <param name="write">What the other writer stores.</param>
    public void Arm(Action write) => _write = write;

    /// <summary>
    /// Lets <paramref name="write"/> in before the next save.
    /// </summary>
    /// <param name="write">What the other writer stores.</param>
    public void ArmAsync(Func<Task> write) => _writeAsync = write;

    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        var write = _write;
        if (write is not null)
        {
            _write = null;
            Wrote = true;
            write();
        }

        return result;
    }

    /// <inheritdoc />
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        var write = _writeAsync;
        if (write is not null)
        {
            _writeAsync = null;
            Wrote = true;
            await write().ConfigureAwait(false);
        }

        return result;
    }
}
