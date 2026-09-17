using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.PostgreSQL;
using Jellyfin.Database.Providers.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Server.Implementations.DatabaseImport;

/// <summary>
/// The tables and columns that are moved from a SQLite database to PostgreSQL, taken from the model of one provider.
/// </summary>
internal sealed class ImportModel
{
    private const string MigrationsHistoryTable = "__EFMigrationsHistory";

    private ImportModel(IRelationalModel model)
    {
        Tables = model.Tables
            .Where(t => !t.Name.Equals(MigrationsHistoryTable, StringComparison.Ordinal))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .Select(ImportTable.Create)
            .ToArray();
        Fingerprint = ComputeFingerprint(Tables);
    }

    /// <summary>
    /// Gets the tables, ordered by name.
    /// </summary>
    public IReadOnlyList<ImportTable> Tables { get; }

    /// <summary>
    /// Gets a hash of the table and column names, their CLR types and nullability, which is the same for every provider of the same model.
    /// </summary>
    public string Fingerprint { get; }

    /// <summary>
    /// Gets the columns whose PostgreSQL values cannot be loaded from SQLite text by COPY and are copied by Jellyfin itself.
    /// </summary>
    public IReadOnlyList<ImportColumn> NonCopyableColumns => Tables.SelectMany(t => t.Columns).Where(c => c.IsArray).ToArray();

    /// <summary>
    /// Builds the model as PostgreSQL stores it, without connecting to a server.
    /// </summary>
    /// <returns>The model.</returns>
    public static ImportModel ForPostgreSql()
    {
        var options = new DbContextOptionsBuilder<JellyfinDbContext>().UseNpgsql(o => o.SetPostgresVersion(PostgreSqlDatabaseProvider.MinimumServerVersion, 0)).Options;
        return Create(options, new PostgreSqlDatabaseProvider(null!, NullLogger<PostgreSqlDatabaseProvider>.Instance));
    }

    /// <summary>
    /// Builds the model as SQLite stores it, without opening a database.
    /// </summary>
    /// <returns>The model.</returns>
    public static ImportModel ForSqlite()
    {
        var options = new DbContextOptionsBuilder<JellyfinDbContext>().UseSqlite().Options;
        return Create(options, new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance));
    }

    /// <summary>
    /// Gets a table by name.
    /// </summary>
    /// <param name="name">The table name.</param>
    /// <returns>The table.</returns>
    public ImportTable GetTable(string name) => Tables.Single(t => t.Name.Equals(name, StringComparison.Ordinal));

    private static ImportModel Create(DbContextOptions<JellyfinDbContext> options, IJellyfinDatabaseProvider provider)
    {
        using var context = new JellyfinDbContext(options, NullLogger<JellyfinDbContext>.Instance, provider, new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
        return new ImportModel(context.GetService<IDesignTimeModel>().Model.GetRelationalModel());
    }

    private static string ComputeFingerprint(IEnumerable<ImportTable> tables)
    {
        var text = new StringBuilder();
        foreach (var table in tables)
        {
            foreach (var column in table.Columns)
            {
                text.Append(CultureInfo.InvariantCulture, $"{table.Name}|{column.Name}|{column.ClrType.FullName}|{column.IsNullable}\n");
            }
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
