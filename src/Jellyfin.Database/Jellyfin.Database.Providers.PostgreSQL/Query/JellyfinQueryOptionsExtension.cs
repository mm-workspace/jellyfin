using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Database.Providers.PostgreSQL.Query;

/// <summary>
/// Adds the query translations Jellyfin needs on PostgreSQL to the context's services.
/// </summary>
internal sealed class JellyfinQueryOptionsExtension : IDbContextOptionsExtension
{
    private DbContextOptionsExtensionInfo? _info;

    /// <inheritdoc />
    public DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    /// <inheritdoc />
    public void ApplyServices(IServiceCollection services)
    {
        new EntityFrameworkRelationalServicesBuilder(services)
            .TryAdd<IAggregateMethodCallTranslatorPlugin, JellyfinAggregateTranslatorPlugin>();
    }

    /// <inheritdoc />
    public void Validate(IDbContextOptions options)
    {
    }

    private sealed class JellyfinAggregateTranslatorPlugin(ISqlExpressionFactory sqlExpressionFactory, IRelationalTypeMappingSource typeMappingSource) : IAggregateMethodCallTranslatorPlugin
    {
        public IEnumerable<IAggregateMethodCallTranslator> Translators { get; } = [new UuidMinMaxTranslator(sqlExpressionFactory, typeMappingSource)];
    }

    private sealed class ExtensionInfo(IDbContextOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => false;

        public override string LogFragment => "JellyfinQueryTranslations ";

        public override int GetServiceProviderHashCode() => 0;

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) => other is ExtensionInfo;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
        {
            debugInfo["Jellyfin:QueryTranslations"] = "1";
        }
    }
}
