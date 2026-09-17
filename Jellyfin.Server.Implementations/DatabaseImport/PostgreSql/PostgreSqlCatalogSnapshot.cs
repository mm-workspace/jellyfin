using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace Jellyfin.Server.Implementations.DatabaseImport.PostgreSql;

/// <summary>
/// The schema of the import target after seeding, which the loaded database must still have.
/// </summary>
/// <param name="DatabaseOid">The oid of the database.</param>
/// <param name="HistoryTableOid">The oid of the migration history table.</param>
/// <param name="Entries">The schema objects of the current schema, ordered.</param>
internal sealed record PostgreSqlCatalogSnapshot(long DatabaseOid, long HistoryTableOid, IReadOnlyList<PostgreSqlCatalogEntry> Entries)
{
    private const string EntriesQuery = """
        SELECT 'table', c.relname, c.relname, c.relkind::text
        FROM pg_class c WHERE c.relnamespace = current_schema()::regnamespace AND c.relkind IN ('r', 'p')
        UNION ALL
        SELECT 'column', c.relname, a.attname,
               format_type(a.atttypid, a.atttypmod) || ' notnull=' || a.attnotnull || ' identity=' || a.attidentity::text
               || ' generated=' || a.attgenerated::text || ' collation=' || coalesce(co.collname, '') || ' default=' || coalesce(pg_get_expr(d.adbin, d.adrelid), '')
        FROM pg_attribute a
        JOIN pg_class c ON c.oid = a.attrelid
        LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
        LEFT JOIN pg_collation co ON co.oid = a.attcollation AND a.attcollation <> 0
        WHERE c.relnamespace = current_schema()::regnamespace AND c.relkind IN ('r', 'p') AND a.attnum > 0 AND NOT a.attisdropped
        UNION ALL
        SELECT 'constraint', c.relname, con.conname, pg_get_constraintdef(con.oid) || ' validated=' || con.convalidated
        FROM pg_constraint con JOIN pg_class c ON c.oid = con.conrelid
        WHERE con.connamespace = current_schema()::regnamespace
        UNION ALL
        SELECT 'index', c.relname, i.relname, pg_get_indexdef(x.indexrelid) || ' valid=' || x.indisvalid
        FROM pg_index x JOIN pg_class i ON i.oid = x.indexrelid JOIN pg_class c ON c.oid = x.indrelid
        WHERE c.relnamespace = current_schema()::regnamespace
        UNION ALL
        SELECT 'trigger', c.relname, t.tgname, pg_get_triggerdef(t.oid) || ' enabled=' || t.tgenabled::text
        FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid
        WHERE c.relnamespace = current_schema()::regnamespace AND NOT t.tgisinternal
        ORDER BY 1, 2, 3
        """;

    /// <summary>
    /// Reads the schema of the database the connection is open on.
    /// </summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The snapshot.</returns>
    public static async Task<PostgreSqlCatalogSnapshot> CaptureAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        long databaseOid;
        long historyTableOid;
        var identity = new NpgsqlCommand("SELECT oid::bigint, coalesce(to_regclass(quote_ident(current_schema()) || '.\"__EFMigrationsHistory\"')::oid::bigint, 0) FROM pg_database WHERE datname = current_database()", connection);
        await using (identity.ConfigureAwait(false))
        {
            var reader = await identity.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                databaseOid = reader.GetInt64(0);
                historyTableOid = reader.GetInt64(1);
            }
        }

        var entries = new List<PostgreSqlCatalogEntry>();
        var command = new NpgsqlCommand(EntriesQuery, connection);
        await using (command.ConfigureAwait(false))
        {
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    entries.Add(new PostgreSqlCatalogEntry(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
                }
            }
        }

        return new PostgreSqlCatalogSnapshot(databaseOid, historyTableOid, entries);
    }

    /// <summary>
    /// Lists the objects that differ from another snapshot of the same database.
    /// </summary>
    /// <param name="actual">The snapshot to compare with this one.</param>
    /// <returns>The entries of this snapshot missing or changed in <paramref name="actual"/>, and the entries only <paramref name="actual"/> has.</returns>
    public IReadOnlyList<PostgreSqlCatalogEntry> Differences(PostgreSqlCatalogSnapshot actual)
    {
        var expected = Entries.ToHashSet();
        var found = actual.Entries.ToHashSet();
        return Entries.Where(e => !found.Contains(e))
            .Concat(actual.Entries.Where(e => !expected.Contains(e)))
            .OrderBy(e => e.Kind, StringComparer.Ordinal)
            .ThenBy(e => e.Table, StringComparer.Ordinal)
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .ToArray();
    }
}
