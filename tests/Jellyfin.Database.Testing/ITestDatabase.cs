using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Database.Testing;

/// <summary>
/// A database created for a test, with the schema of the current model.
/// </summary>
public interface ITestDatabase : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Gets the key of the database provider, as used in the database configuration.
    /// </summary>
    string ProviderKey { get; }

    /// <summary>
    /// Gets the database provider the contexts are created with.
    /// </summary>
    IJellyfinDatabaseProvider Provider { get; }

    /// <summary>
    /// Gets the options the contexts are created with.
    /// </summary>
    DbContextOptions<JellyfinDbContext> Options { get; }

    /// <summary>
    /// Creates a new context on the test database.
    /// </summary>
    /// <returns>The context.</returns>
    JellyfinDbContext CreateDbContext();

    /// <summary>
    /// Creates a factory handing out new contexts on the test database.
    /// </summary>
    /// <returns>The factory.</returns>
    IDbContextFactory<JellyfinDbContext> CreateDbContextFactory();

    /// <summary>
    /// Removes all data by recreating the database.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the operation.</returns>
    Task ResetAsync(CancellationToken cancellationToken = default);
}
