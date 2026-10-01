using System;
using System.Linq;
using Jellyfin.Database.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Jellyfin.Server.Implementations.Extensions;

/// <summary>
/// Recognizes database contexts configured by <see cref="ServiceCollectionExtensions.AddJellyfinDbContext"/>.
/// </summary>
public static class JellyfinDbContextRegistration
{
    /// <summary>
    /// Gets the interceptor that marks the options configured by Jellyfin. It does not intercept anything.
    /// </summary>
    internal static IInterceptor Marker { get; } = new RegistrationMarker();

    /// <summary>
    /// Throws when a database context factory hands out contexts that Jellyfin did not configure, for example because a plugin replaced the registration.
    /// </summary>
    /// <param name="factory">The database context factory from the service provider.</param>
    /// <exception cref="InvalidOperationException">The contexts were not configured by Jellyfin.</exception>
    public static void EnsureConfiguredByJellyfin(IDbContextFactory<JellyfinDbContext> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        using var context = factory.CreateDbContext();
        var interceptors = context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.Interceptors;
        if (interceptors is not null && interceptors.Contains(Marker))
        {
            return;
        }

        var factoryType = factory.GetType();
        throw new InvalidOperationException(
            $"The database context was not configured by Jellyfin (factory {factoryType.FullName} from {factoryType.Assembly.GetName().Name}, database provider {context.Database.ProviderName}). "
            + "A plugin has most likely replaced Jellyfin's database registration. Remove that plugin to start Jellyfin.");
    }

    private sealed class RegistrationMarker : IInterceptor
    {
    }
}
