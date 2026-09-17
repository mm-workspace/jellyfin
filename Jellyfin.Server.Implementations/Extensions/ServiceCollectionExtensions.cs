using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.PostgreSQL;
using Jellyfin.Database.Providers.Sqlite;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using JellyfinDbProviderFactory = System.Func<System.IServiceProvider, Jellyfin.Database.Implementations.IJellyfinDatabaseProvider>;

namespace Jellyfin.Server.Implementations.Extensions;

/// <summary>
/// Extensions for the <see cref="IServiceCollection"/> interface.
/// </summary>
public static class ServiceCollectionExtensions
{
    private const string PostgreSqlPluginProviderKey = "Jellyfin-PgSql";

    private const string PostgreSqlPluginMessage = "The database configuration uses the PostgreSQL plugin. Jellyfin supports PostgreSQL without a plugin (database type Jellyfin-PostgreSQL), "
        + "but it cannot use a database created by the plugin. Move the server back to SQLite with a backup taken while the plugin was in use, then follow the documentation for moving a server from SQLite to PostgreSQL.";

    private static IEnumerable<Type> DatabaseProviderTypes()
    {
        yield return typeof(SqliteDatabaseProvider);
        yield return typeof(PostgreSqlDatabaseProvider);
    }

    private static IDictionary<string, Type> GetSupportedDbProviders()
    {
        var items = new Dictionary<string, Type>(StringComparer.InvariantCultureIgnoreCase);
        foreach (var providerType in DatabaseProviderTypes())
        {
            var keyAttribute = providerType.GetCustomAttribute<JellyfinDatabaseProviderKeyAttribute>();
            if (keyAttribute is null || string.IsNullOrWhiteSpace(keyAttribute.DatabaseProviderKey))
            {
                continue;
            }

            items[keyAttribute.DatabaseProviderKey] = providerType;
        }

        return items;
    }

    private static bool IsPostgreSqlPlugin(CustomDatabaseOptions customProviderOptions)
    {
        // The options come from database.xml, which does not enforce required members.
        return NamesPostgreSql(customProviderOptions.PluginName) || NamesPostgreSql(customProviderOptions.PluginAssembly);

        static bool NamesPostgreSql(string? value)
            => value is not null
                && (value.Contains("pgsql", StringComparison.OrdinalIgnoreCase) || value.Contains("postgres", StringComparison.OrdinalIgnoreCase));
    }

    private static JellyfinDbProviderFactory? LoadDatabasePlugin(CustomDatabaseOptions customProviderOptions, IApplicationPaths applicationPaths)
    {
        var plugin = Directory.EnumerateDirectories(applicationPaths.PluginsPath)
            .Where(e => Path.GetFileName(e)!.StartsWith(customProviderOptions.PluginName, StringComparison.OrdinalIgnoreCase))
            .Order()
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"The requested custom database plugin with the name '{customProviderOptions.PluginName}' could not been found in '{applicationPaths.PluginsPath}'");

        var dbProviderAssembly = Path.Combine(plugin, Path.ChangeExtension(customProviderOptions.PluginAssembly, "dll"));
        if (!File.Exists(dbProviderAssembly))
        {
            throw new InvalidOperationException($"Could not find the requested assembly at '{dbProviderAssembly}'");
        }

        // we have to load the assembly without proxy to ensure maximum performance for this.
        var assembly = Assembly.LoadFrom(dbProviderAssembly);
        var dbProviderType = assembly.GetExportedTypes().FirstOrDefault(f => f.IsAssignableTo(typeof(IJellyfinDatabaseProvider)))
            ?? throw new InvalidOperationException($"Could not find any type implementing the '{nameof(IJellyfinDatabaseProvider)}' interface.");

        return (services) => (IJellyfinDatabaseProvider)ActivatorUtilities.CreateInstance(services, dbProviderType);
    }

    /// <summary>
    /// Reads the database configuration. SQLite is only used as the default when there is no database configuration file yet.
    /// </summary>
    /// <param name="configurationManager">The server configuration manager.</param>
    /// <param name="configuration">The startup configuration.</param>
    /// <returns>The database configuration.</returns>
    /// <exception cref="InvalidOperationException">The database configuration file exists but does not name a database type.</exception>
    internal static DatabaseConfigurationOptions ResolveDatabaseConfiguration(IServerConfigurationManager configurationManager, IConfiguration configuration)
    {
        var efCoreConfiguration = configurationManager.GetConfiguration<DatabaseConfigurationOptions>("database");
        if (efCoreConfiguration?.DatabaseType is not null)
        {
            return efCoreConfiguration;
        }

        var cmdMigrationArgument = configuration.GetValue<string>("migration-provider");
        if (!string.IsNullOrWhiteSpace(cmdMigrationArgument))
        {
            return new DatabaseConfigurationOptions()
            {
                DatabaseType = cmdMigrationArgument,
            };
        }

        // A file that failed to load must not be replaced with the default, or the server would start on a different, empty database.
        var configurationFile = Path.Combine(configurationManager.ApplicationPaths.ConfigurationDirectoryPath, "database.xml");
        if (File.Exists(configurationFile))
        {
            throw new InvalidOperationException(
                $"The database configuration file '{configurationFile}' could not be read or does not set DatabaseType. Fix the file or restore it from a backup; Jellyfin does not replace it.");
        }

        // when nothing is setup via new Database configuration, fallback to SQLite with default settings.
        efCoreConfiguration = new DatabaseConfigurationOptions()
        {
            DatabaseType = "Jellyfin-SQLite",
            LockingBehavior = DatabaseLockingBehaviorTypes.NoLock
        };
        configurationManager.SaveConfiguration("database", efCoreConfiguration);
        return efCoreConfiguration;
    }

    /// <summary>
    /// Adds the <see cref="IDbContextFactory{TContext}"/> interface to the service collection with second level caching enabled.
    /// </summary>
    /// <param name="serviceCollection">An instance of the <see cref="IServiceCollection"/> interface.</param>
    /// <param name="configurationManager">The server configuration manager.</param>
    /// <param name="configuration">The startup Configuration.</param>
    /// <returns>The updated service collection.</returns>
    public static IServiceCollection AddJellyfinDbContext(
        this IServiceCollection serviceCollection,
        IServerConfigurationManager configurationManager,
        IConfiguration configuration)
    {
        var efCoreConfiguration = ResolveDatabaseConfiguration(configurationManager, configuration);
        JellyfinDbProviderFactory? providerFactory = null;

        Type? providerType = null;
        if (efCoreConfiguration.DatabaseType.Equals("PLUGIN_PROVIDER", StringComparison.OrdinalIgnoreCase))
        {
            if (efCoreConfiguration.CustomProviderOptions is null)
            {
                throw new InvalidOperationException("The custom database provider must declare the custom provider options to work");
            }

            if (IsPostgreSqlPlugin(efCoreConfiguration.CustomProviderOptions))
            {
                throw new InvalidOperationException(PostgreSqlPluginMessage);
            }

            providerFactory = LoadDatabasePlugin(efCoreConfiguration.CustomProviderOptions, configurationManager.ApplicationPaths);
        }
        else
        {
            if (efCoreConfiguration.DatabaseType.Equals(PostgreSqlPluginProviderKey, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(PostgreSqlPluginMessage);
            }

            var providers = GetSupportedDbProviders();
            if (!providers.TryGetValue(efCoreConfiguration.DatabaseType.ToUpperInvariant(), out providerType))
            {
                throw new InvalidOperationException($"Jellyfin cannot find the database provider of type '{efCoreConfiguration.DatabaseType}'. Supported types are {string.Join(", ", providers.Keys)}");
            }

            var builtInProviderType = providerType;
            providerFactory = (services) => (IJellyfinDatabaseProvider)ActivatorUtilities.CreateInstance(services, builtInProviderType);
        }

        serviceCollection.AddSingleton<IJellyfinDatabaseProvider>(providerFactory!);

        var configuredLockingBehavior = efCoreConfiguration.LockingBehavior;
        if (providerType == typeof(PostgreSqlDatabaseProvider))
        {
            var effectiveLockingBehavior = PostgreSqlDatabaseProvider.GetEffectiveLockingBehavior(configuredLockingBehavior);
            if (effectiveLockingBehavior != configuredLockingBehavior)
            {
                efCoreConfiguration = new DatabaseConfigurationOptions
                {
                    DatabaseType = efCoreConfiguration.DatabaseType,
                    CustomProviderOptions = efCoreConfiguration.CustomProviderOptions,
                    LockingBehavior = effectiveLockingBehavior
                };
            }
        }

        switch (efCoreConfiguration.LockingBehavior)
        {
            case DatabaseLockingBehaviorTypes.NoLock:
                serviceCollection.AddSingleton<IEntityFrameworkCoreLockingBehavior, NoLockBehavior>();
                break;
            case DatabaseLockingBehaviorTypes.Pessimistic:
                serviceCollection.AddSingleton<IEntityFrameworkCoreLockingBehavior, PessimisticLockBehavior>();
                break;
            case DatabaseLockingBehaviorTypes.Optimistic:
                serviceCollection.AddSingleton<IEntityFrameworkCoreLockingBehavior, OptimisticLockBehavior>();
                break;
            case DatabaseLockingBehaviorTypes.SerializedWrites:
                serviceCollection.AddSingleton<IEntityFrameworkCoreLockingBehavior, SerializedWriteLockBehavior>();
                break;
        }

        serviceCollection.AddPooledDbContextFactory<JellyfinDbContext>((serviceProvider, opt) =>
        {
            if (efCoreConfiguration.LockingBehavior != configuredLockingBehavior)
            {
                serviceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(ServiceCollectionExtensions)).LogInformation(
                    "The effective database locking behavior is {LockingBehavior}; the configured {ConfiguredLockingBehavior} is not used with this database provider.",
                    efCoreConfiguration.LockingBehavior,
                    configuredLockingBehavior);
            }

            var provider = serviceProvider.GetRequiredService<IJellyfinDatabaseProvider>();
            provider.Initialise(opt, efCoreConfiguration);
            var lockingBehavior = serviceProvider.GetRequiredService<IEntityFrameworkCoreLockingBehavior>();
            lockingBehavior.Initialise(opt);
        });

        return serviceCollection;
    }
}
