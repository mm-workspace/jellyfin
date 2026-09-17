using System;
using System.Collections.Generic;
using Jellyfin.Database.Implementations;
using MediaBrowser.Common.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Jellyfin.Database.Testing;

/// <summary>
/// Options for creating an <see cref="ITestDatabase"/>.
/// </summary>
public sealed class TestDatabaseOptions
{
    /// <summary>
    /// Gets the interceptors added to every context.
    /// </summary>
    public IReadOnlyList<IInterceptor> Interceptors { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether foreign keys are enforced, as they are in a running server.
    /// </summary>
    public bool EnforceForeignKeys { get; init; }

    /// <summary>
    /// Gets the application paths handed to the database provider.
    /// </summary>
    public IApplicationPaths? ApplicationPaths { get; init; }

    /// <summary>
    /// Gets a callback that can further configure the context options, for example to capture logging.
    /// </summary>
    public Action<DbContextOptionsBuilder<JellyfinDbContext>>? ConfigureOptions { get; init; }
}
