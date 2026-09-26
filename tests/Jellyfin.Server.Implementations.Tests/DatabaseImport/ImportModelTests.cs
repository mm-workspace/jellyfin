using System;
using System.Linq;
using Jellyfin.Server.Implementations.DatabaseImport;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.DatabaseImport;

public class ImportModelTests
{
    private static readonly ImportModel _postgreSql = ImportModel.ForPostgreSql();
    private static readonly ImportModel _sqlite = ImportModel.ForSqlite();

    [Fact]
    public void Fingerprint_IsTheSameForBothProviders()
    {
        Assert.Equal(_sqlite.Fingerprint, _postgreSql.Fingerprint);
        Assert.Equal(_sqlite.Tables.Select(t => t.Name), _postgreSql.Tables.Select(t => t.Name));
    }

    [Fact]
    public void Tables_LeaveOutTheMigrationHistory()
    {
        Assert.DoesNotContain(_postgreSql.Tables, t => t.Name == "__EFMigrationsHistory");
        Assert.Contains(_postgreSql.Tables, t => t.Name == "BaseItems");
    }

    [Fact]
    public void NonCopyableColumns_AreOnlyKeyframeTicks()
    {
        var column = Assert.Single(_postgreSql.NonCopyableColumns);
        Assert.Equal("KeyframeTicks", column.Name);
        Assert.Empty(_sqlite.NonCopyableColumns);
    }

    [Fact]
    public void PostgreSqlStoreTypes_FollowTheProviderConventions()
    {
        var baseItems = _postgreSql.GetTable("BaseItems");
        Assert.Equal("uuid", baseItems.Columns.Single(c => c.Name == "Id").StoreType);
        Assert.Equal("text", baseItems.Columns.Single(c => c.Name == "Name").StoreType);
        Assert.Equal("boolean", baseItems.Columns.Single(c => c.Name == "IsFolder").StoreType);
        Assert.Equal("timestamp with time zone", baseItems.Columns.Single(c => c.Name == "DateCreated").StoreType);
        Assert.Equal("bigint[]", _postgreSql.GetTable("KeyframeData").Columns.Single(c => c.Name == "KeyframeTicks").StoreType);
    }

    [Fact]
    public void IdentityColumns_AreIntegerKeysOfTheSameTablesOnBothProviders()
    {
        var postgreSql = _postgreSql.Tables.Where(t => t.IdentityColumns.Count > 0).Select(t => $"{t.Name}.{string.Join(",", t.IdentityColumns)}").ToArray();
        var sqlite = _sqlite.Tables.Where(t => t.IdentityColumns.Count > 0).Select(t => $"{t.Name}.{string.Join(",", t.IdentityColumns)}").ToArray();

        Assert.Equal(sqlite, postgreSql);
        Assert.Contains("ActivityLogs.Id", postgreSql);
        Assert.All(_postgreSql.Tables.SelectMany(t => t.IdentityColumns.Select(c => t.Columns.Single(col => col.Name == c))), c => Assert.Contains(c.ClrType, new[] { typeof(int), typeof(long) }));
    }

    [Fact]
    public void ForeignKeys_PointAtTablesOfTheModel()
    {
        var names = _postgreSql.Tables.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(_postgreSql.Tables.SelectMany(t => t.ForeignKeys));
        Assert.All(_postgreSql.Tables.SelectMany(t => t.ForeignKeys), f => Assert.Contains(f.PrincipalTable, names));
    }

    [Fact]
    public void Shape_MatchesTheSchema()
    {
        Assert.Equal(31, _postgreSql.Tables.Count);
        Assert.Equal(12, _postgreSql.Tables.Sum(t => t.IdentityColumns.Count));
        Assert.Equal(_sqlite.Tables.Sum(t => t.ForeignKeys.Count), _postgreSql.Tables.Sum(t => t.ForeignKeys.Count));
    }

    [Fact]
    public void Indexes_IncludeKeysAndThePostgreSqlExpressionIndexes()
    {
        var peoples = _postgreSql.GetTable("Peoples").Indexes;
        var baseItems = _postgreSql.GetTable("BaseItems").Indexes;

        Assert.Contains(peoples, i => i.Name == "IX_Peoples_NameLower" && i.Expression == "lower(\"Name\")" && i.Columns.SequenceEqual(["Name"]));
        Assert.Contains(baseItems, i => i.Name == "PK_BaseItems" && i.IsUnique && i.Columns.SequenceEqual(["Id"]));
        Assert.Contains(baseItems, i => i.Name == "IX_BaseItems_VersionGroup" && i.Expression is not null);
        Assert.DoesNotContain(_sqlite.Tables.SelectMany(t => t.Indexes), i => i.Expression is not null);
        Assert.All(_postgreSql.Tables, t => Assert.Contains(t.Indexes, i => i.IsUnique && i.Columns.SequenceEqual(t.PrimaryKey)));
    }
}
