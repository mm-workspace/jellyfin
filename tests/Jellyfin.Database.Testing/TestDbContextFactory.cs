using System;
using Jellyfin.Database.Implementations;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Database.Testing;

/// <summary>
/// A context factory handing out contexts created by a callback.
/// </summary>
internal sealed class TestDbContextFactory : IDbContextFactory<JellyfinDbContext>
{
    private readonly Func<JellyfinDbContext> _createDbContext;

    public TestDbContextFactory(Func<JellyfinDbContext> createDbContext)
    {
        _createDbContext = createDbContext;
    }

    public JellyfinDbContext CreateDbContext() => _createDbContext();
}
