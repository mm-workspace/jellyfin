using System;
using System.Linq;
using System.Reflection;
using System.Text;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Providers.PostgreSQL;
using Jellyfin.Database.Providers.PostgreSQL.Migrations;
using Jellyfin.Database.Providers.Sqlite.Migrations;
using Jellyfin.Server.Migrations;
using Jellyfin.Server.Migrations.Stages;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Jellyfin.Server.Tests.Migrations;

/// <summary>
/// Keeps the PostgreSQL migrations in step with the SQLite ones. Code migrations and schema migrations share one history and run in id
/// order, so a PostgreSQL migration must carry the id of the SQLite migration it mirrors.
/// </summary>
[Trait("Category", "MigrationGuard")]
public sealed class PostgreSqlMigrationGuardTests : IDisposable
{
    private const string BaselineId = "20200101000000_PostgreSqlBaseline";

    private readonly JellyfinDbContext _sqlite = new SqliteDesignTimeJellyfinDbFactory().CreateDbContext([]);
    private readonly JellyfinDbContext _postgreSql = new PostgreSqlDesignTimeJellyfinDbFactory().CreateDbContext([]);

    private System.Collections.Generic.IReadOnlyDictionary<string, TypeInfo> SqliteMigrations => _sqlite.GetService<IMigrationsAssembly>().Migrations;

    private System.Collections.Generic.IReadOnlyDictionary<string, TypeInfo> PostgreSqlMigrations => _postgreSql.GetService<IMigrationsAssembly>().Migrations;

    [Fact]
    public void MigrationIds_PostgreSqlMirrorsEverySqliteMigrationAfterTheBaseline()
    {
        var sqliteIds = SqliteMigrations.Keys.Except(PostgreSqlBaselineSquashedIds.Ids, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var postgreSqlIds = PostgreSqlMigrations.Keys.Where(id => id != BaselineId).Order(StringComparer.Ordinal).ToArray();

        Assert.True(
            sqliteIds.SequenceEqual(postgreSqlIds),
            $"Every SQLite migration added after the PostgreSQL baseline needs a PostgreSQL migration with the same id. Missing: [{string.Join(", ", sqliteIds.Except(postgreSqlIds))}]. Unexpected: [{string.Join(", ", postgreSqlIds.Except(sqliteIds))}].");
    }

    [Fact]
    public void Baseline_SquashesOnlyExistingSqliteMigrations()
    {
        Assert.Empty(PostgreSqlBaselineSquashedIds.Ids.Except(SqliteMigrations.Keys, StringComparer.Ordinal));
    }

    [Fact]
    public void Baseline_SortsBeforeEveryOtherMigration()
    {
        Assert.Contains(BaselineId, PostgreSqlMigrations.Keys);
        var others = SqliteMigrations.Keys.Concat(CodeMigrationIds()).Concat(PostgreSqlMigrations.Keys.Where(id => id != BaselineId));
        Assert.All(others, id => Assert.True(string.CompareOrdinal(BaselineId, id) < 0, $"{id} sorts before the PostgreSQL baseline."));
    }

    [Fact]
    public void MigrationClassNames_MatchTheirSqliteTwins()
    {
        foreach (var (id, type) in PostgreSqlMigrations.Where(e => e.Key != BaselineId))
        {
            Assert.Equal(SqliteMigrations[id].Name, type.Name);
        }
    }

    [Fact]
    public void CodeMigrationIds_NeverCollideWithSchemaMigrationIds()
    {
        var schemaIds = SqliteMigrations.Keys.Concat(PostgreSqlMigrations.Keys).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain(CodeMigrationIds(), schemaIds.Contains);
    }

    [Fact]
    public void Baseline_CannotBeReverted()
    {
        var migration = (Migration)Activator.CreateInstance(PostgreSqlMigrations[BaselineId])!;
        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        var down = typeof(Migration).GetMethod("Down", BindingFlags.Instance | BindingFlags.NonPublic)!;

        var exception = Assert.Throws<TargetInvocationException>(() => down.Invoke(migration, [builder]));
        Assert.IsType<NotSupportedException>(exception.InnerException);
    }

    [Fact]
    public void Identifiers_FitPostgreSqlNameLimit()
    {
        var model = _postgreSql.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        var names = model.Tables.SelectMany(t => t.Indexes.Select(i => i.Name)
            .Concat(t.ForeignKeyConstraints.Select(f => f.Name))
            .Concat(t.UniqueConstraints.Select(u => u.Name))
            .Append(t.Name)
            .Concat(t.Columns.Select(c => c.Name)));

        Assert.DoesNotContain(names, n => Encoding.UTF8.GetByteCount(n) > 63);
    }

    public void Dispose()
    {
        _sqlite.Dispose();
        _postgreSql.Dispose();
    }

    private static string[] CodeMigrationIds()
    {
#pragma warning disable CS0618 // Type or member is obsolete
        return typeof(JellyfinMigrationService).Assembly.GetTypes()
            .Where(e => typeof(IMigrationRoutine).IsAssignableFrom(e) || typeof(IAsyncMigrationRoutine).IsAssignableFrom(e))
#pragma warning restore CS0618 // Type or member is obsolete
            .Select(e => (Type: e, Metadata: e.GetCustomAttribute<JellyfinMigrationAttribute>()))
            .Where(e => e.Metadata is not null)
            .Select(e => new CodeMigration(e.Type, e.Metadata!, null).BuildCodeMigrationId())
            .ToArray();
    }
}
