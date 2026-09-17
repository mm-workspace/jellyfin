using System;
using System.Linq;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Providers.PostgreSQL;
using Jellyfin.Database.Providers.PostgreSQL.ValueConverters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.DatabaseProviders.PostgreSql;

public sealed class PostgreSqlModelConventionTests : IDisposable
{
    private readonly JellyfinDbContext _context = new PostgreSqlDesignTimeJellyfinDbFactory().CreateDbContext([]);

    [Fact]
    public void Model_StringColumns_AreTextWithBinaryCollation()
    {
        var wrong = StoreProperties(typeof(string))
            .Where(p => p.GetColumnType() != "text" || p.GetCollation() != PostgreSqlDatabaseProvider.BinaryCollation)
            .Select(Describe)
            .ToArray();

        Assert.Empty(wrong);
    }

    [Fact]
    public void Model_DateTimeColumns_AreUtcTimestamps()
    {
        var wrong = StoreProperties(typeof(DateTime))
            .Concat(StoreProperties(typeof(DateTime?)))
            .Where(p => p.GetValueConverter() is not UtcDateTimeConverter || p.GetColumnType() != "timestamp with time zone")
            .Select(Describe)
            .ToArray();

        Assert.Empty(wrong);
    }

    [Fact]
    public void Model_RowVersion_IsBigintConcurrencyToken()
    {
        var rowVersion = _context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(User))!.FindProperty(nameof(User.RowVersion))!;

        Assert.Equal("bigint", rowVersion.GetColumnType());
        Assert.True(rowVersion.IsConcurrencyToken);
        Assert.Empty(StoreProperties(typeof(uint)).Where(p => p.GetColumnType() != "bigint").Select(Describe));
    }

    [Fact]
    public void Model_KeyframeTicks_IsBigintArray()
    {
        var ticks = _context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(KeyframeData))!.FindProperty(nameof(KeyframeData.KeyframeTicks))!;

        Assert.Equal("bigint[]", ticks.GetColumnType());
    }

    [Fact]
    public void CreateScript_UsesBinaryCollationAndNoVarchar()
    {
        var script = _context.Database.GenerateCreateScript();

        Assert.Contains("COLLATE \"C\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("character varying(", script, StringComparison.Ordinal);
    }

    [Fact]
    public void DesignTimeModel_IsStable()
    {
        using var other = new PostgreSqlDesignTimeJellyfinDbFactory().CreateDbContext([]);

        Assert.Equal(_context.Database.GenerateCreateScript(), other.Database.GenerateCreateScript());
    }

    public void Dispose()
    {
        _context.Dispose();
    }

    private static string Describe(IProperty property) => $"{property.DeclaringType.ShortName()}.{property.Name} ({property.GetColumnType()}, {property.GetCollation()})";

    private IProperty[] StoreProperties(Type clrType) => _context.GetService<IDesignTimeModel>().Model.GetEntityTypes()
        .SelectMany(e => e.GetProperties())
        .Where(p => p.ClrType == clrType && p.GetTableColumnMappings().Any())
        .ToArray();
}
