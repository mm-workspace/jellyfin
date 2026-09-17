using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace Jellyfin.Server.Implementations.DatabaseImport.PostgreSql;

/// <summary>
/// Verifies a PostgreSQL database loaded by pgloader against the manifest of its source and commits it.
/// </summary>
/// <remarks>
/// pgloader reports success when rows were dropped or constraints were not recreated, so this is the only success signal of
/// the import. Everything runs in one transaction that is committed only if every check passes.
/// </remarks>
internal sealed class PostgreSqlImportFinalizer
{
    /// <summary>
    /// The key of the advisory lock held while an import step changes the database.
    /// </summary>
    public const string LockKey = "jellyfin.postgresql-import";

    private const string TimestampType = "timestamp with time zone";

    private readonly ImportModel _model;
    private readonly HashSet<string> _historyIds;
    private readonly PostgreSqlCatalogSnapshot _reference;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgreSqlImportFinalizer"/> class.
    /// </summary>
    /// <param name="model">The PostgreSQL model of this server.</param>
    /// <param name="historyIds">The migration ids the seeded database has: the PostgreSQL schema migrations and the code migrations.</param>
    /// <param name="reference">The schema captured after seeding.</param>
    public PostgreSqlImportFinalizer(ImportModel model, IEnumerable<string> historyIds, PostgreSqlCatalogSnapshot reference)
    {
        _model = model;
        _historyIds = historyIds.ToHashSet(StringComparer.Ordinal);
        _reference = reference;
    }

    /// <summary>
    /// Verifies the loaded database and commits it if it matches the source.
    /// </summary>
    /// <param name="connection">An open connection to the target database, without a transaction.</param>
    /// <param name="manifest">The manifest written by preflight.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The findings and whether the transaction was committed.</returns>
    public async Task<PostgreSqlFinalization> FinalizeAsync(NpgsqlConnection connection, ImportManifest manifest, CancellationToken cancellationToken)
    {
        var findings = new ImportFindingCollector();
        var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            await ExecuteAsync(connection, "SET LOCAL statement_timeout = 0; SET LOCAL idle_in_transaction_session_timeout = 0", cancellationToken).ConfigureAwait(false);
            if (!(bool)(await ScalarAsync(connection, $"SELECT pg_try_advisory_xact_lock(hashtext('{LockKey}'))", cancellationToken).ConfigureAwait(false))!)
            {
                findings.Add(nameof(FinalizeCheck.ImportLockHeld), ImportFindingSeverity.Error);
                return new PostgreSqlFinalization(findings.ToList(), false);
            }

            var catalog = await PostgreSqlCatalogSnapshot.CaptureAsync(connection, cancellationToken).ConfigureAwait(false);
            if (catalog.DatabaseOid != _reference.DatabaseOid || catalog.HistoryTableOid != _reference.HistoryTableOid)
            {
                findings.Add(nameof(FinalizeCheck.TargetChanged), ImportFindingSeverity.Error);
                return new PostgreSqlFinalization(findings.ToList(), false);
            }

            await CheckHistoryAsync(connection, findings, cancellationToken).ConfigureAwait(false);
            foreach (var entry in _reference.Differences(catalog))
            {
                findings.Add(nameof(FinalizeCheck.CatalogChanged), ImportFindingSeverity.Error, entry.Table, index: entry.Name);
            }

            var sources = manifest.Tables.ToDictionary(t => t.Name, StringComparer.Ordinal);
            foreach (var table in _model.Tables)
            {
                var source = sources.GetValueOrDefault(table.Name);
                var rows = (long)(await ScalarAsync(connection, $"SELECT count(*) FROM {Quote(table.Name)}", cancellationToken).ConfigureAwait(false))!;
                if (source is null || rows != source.RowCount)
                {
                    findings.Add(nameof(FinalizeCheck.RowCountMismatch), ImportFindingSeverity.Error, table.Name);
                }
            }

            // Checking content is only worth its time when the schema and counts are right.
            if (findings.HasErrors)
            {
                return new PostgreSqlFinalization(findings.ToList(), false);
            }

            foreach (var table in _model.Tables)
            {
                var source = sources[table.Name];
                await NormalizeSentinelsAsync(connection, table, source, findings, cancellationToken).ConfigureAwait(false);
                var hash = await TableContentHash.ComputeAsync(connection, table, cancellationToken).ConfigureAwait(false);
                if (hash.Value != source.ContentHash)
                {
                    findings.Add(nameof(FinalizeCheck.ContentMismatch), ImportFindingSeverity.Error, table.Name);
                }

                await ResetIdentitySequencesAsync(connection, table, findings, cancellationToken).ConfigureAwait(false);
            }

            if (findings.HasErrors)
            {
                return new PostgreSqlFinalization(findings.ToList(), false);
            }

            await ExecuteAsync(connection, "ANALYZE", cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new PostgreSqlFinalization(findings.ToList(), true);
        }
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
#pragma warning disable CA2100 // Identifiers come from the EF model.
        var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 0 };
#pragma warning restore CA2100
        await using (command.ConfigureAwait(false))
        {
            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<int> ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
#pragma warning disable CA2100 // Identifiers come from the EF model.
        var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 0 };
#pragma warning restore CA2100
        await using (command.ConfigureAwait(false))
        {
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task NormalizeSentinelsAsync(NpgsqlConnection connection, ImportTable table, ImportTableSummary source, ImportFindingCollector findings, CancellationToken cancellationToken)
    {
        // SQLite holds DateTime.MinValue and MaxValue as text; PostgreSQL parses them into finite timestamps, the largest of
        // which the server cannot read. Npgsql writes both as infinity, so they become infinity here as well.
        foreach (var column in table.Columns.Where(c => c.StoreType == TimestampType))
        {
            var expected = source.TimestampSentinels.FirstOrDefault(s => s.Column == column.Name) ?? new ImportTimestampSentinels(column.Name, 0, 0);
            var name = Quote(column.Name);
            var pastMaxValue = await ExecuteAsync(connection, $"UPDATE {Quote(table.Name)} SET {name} = 'infinity' WHERE {name} >= '10000-01-01 00:00:00+00' AND {name} <> 'infinity'", cancellationToken).ConfigureAwait(false);
            var minValue = await ExecuteAsync(connection, $"UPDATE {Quote(table.Name)} SET {name} = '-infinity' WHERE {name} = '0001-01-01 00:00:00+00'", cancellationToken).ConfigureAwait(false);
            if (pastMaxValue != expected.PastMaxValue || minValue != expected.MinValue)
            {
                findings.Add(nameof(FinalizeCheck.SentinelCountMismatch), ImportFindingSeverity.Error, table.Name, column.Name);
            }
        }
    }

    private static async Task ResetIdentitySequencesAsync(NpgsqlConnection connection, ImportTable table, ImportFindingCollector findings, CancellationToken cancellationToken)
    {
        foreach (var column in table.IdentityColumns)
        {
            // pgloader leaves the sequences at their start, so the next insert would reuse an id.
            var sequence = $"pg_get_serial_sequence('{Quote(table.Name).Replace("'", "''", StringComparison.Ordinal)}', '{column.Replace("'", "''", StringComparison.Ordinal)}')";
            var next = Convert.ToInt64(
                await ScalarAsync(connection, $"SELECT COALESCE(MAX({Quote(column)}), 0) + 1 FROM {Quote(table.Name)}", cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
            await ScalarAsync(connection, $"SELECT setval({sequence}, {next}, false)", cancellationToken).ConfigureAwait(false);
            var probe = await ScalarAsync(connection, $"SELECT nextval({sequence})", cancellationToken).ConfigureAwait(false);
            await ScalarAsync(connection, $"SELECT setval({sequence}, {next}, false)", cancellationToken).ConfigureAwait(false);
            if (probe is not long id || id != next)
            {
                findings.Add(nameof(FinalizeCheck.IdentitySequenceBehind), ImportFindingSeverity.Error, table.Name, column);
            }
        }
    }

    private async Task CheckHistoryAsync(NpgsqlConnection connection, ImportFindingCollector findings, CancellationToken cancellationToken)
    {
        var applied = new HashSet<string>(StringComparer.Ordinal);
        var command = new NpgsqlCommand("SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\"", connection);
        await using (command.ConfigureAwait(false))
        {
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    applied.Add(reader.GetString(0));
                }
            }
        }

        foreach (var id in _historyIds.Except(applied).Order(StringComparer.Ordinal))
        {
            findings.Add(nameof(FinalizeCheck.HistoryMissingMigrations), ImportFindingSeverity.Error, "__EFMigrationsHistory", primaryKey: () => id);
        }

        foreach (var id in applied.Except(_historyIds).Order(StringComparer.Ordinal))
        {
            findings.Add(nameof(FinalizeCheck.HistoryForeignMigrations), ImportFindingSeverity.Error, "__EFMigrationsHistory", primaryKey: () => id);
        }
    }
}
