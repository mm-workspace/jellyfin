using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Server.Implementations.DatabaseImport.Sqlite;

/// <summary>
/// Checks that a SQLite database can be loaded into the PostgreSQL schema without losing or changing a value.
/// </summary>
/// <remarks>
/// pgloader drops or truncates many of these values silently, so everything it cannot load exactly is refused here.
/// </remarks>
internal sealed partial class SqliteSourceInspector
{
    /// <summary>
    /// The largest index row a PostgreSQL B-tree accepts, in bytes.
    /// </summary>
    internal const int MaxIndexRowBytes = 2704;

    private const string HistoryTable = "__EFMigrationsHistory";

    private static readonly UTF8Encoding _strictUtf8 = new(false, true);

    private readonly ImportModel _model;
    private readonly HashSet<string> _schemaMigrationIds;
    private readonly HashSet<string> _codeMigrationIds;
    private readonly Version _serverVersion;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteSourceInspector"/> class.
    /// </summary>
    /// <param name="model">The PostgreSQL model the data is loaded into.</param>
    /// <param name="schemaMigrationIds">The ids of the SQLite schema migrations of this server.</param>
    /// <param name="codeMigrationIds">The ids of the code migrations of this server.</param>
    /// <param name="serverVersion">The version of this server.</param>
    public SqliteSourceInspector(ImportModel model, IEnumerable<string> schemaMigrationIds, IEnumerable<string> codeMigrationIds, Version serverVersion)
    {
        _model = model;
        _schemaMigrationIds = schemaMigrationIds.ToHashSet(StringComparer.Ordinal);
        _codeMigrationIds = codeMigrationIds.ToHashSet(StringComparer.Ordinal);
        _serverVersion = Normalize(serverVersion);
    }

    private enum ValueKind
    {
        Text,
        Uuid,
        Timestamp,
        IntegerArray,
        Boolean,
        Int16,
        Int32,
        Int64,
        Real,
        Blob
    }

    /// <summary>
    /// Gets the ids of the SQLite schema migrations of this server.
    /// </summary>
    /// <returns>The ids, in order.</returns>
    public static IReadOnlyList<string> GetSchemaMigrationIds()
    {
        var options = new DbContextOptionsBuilder<JellyfinDbContext>()
            .UseSqlite(o => o.MigrationsAssembly(typeof(SqliteDatabaseProvider).Assembly))
            .Options;
        using var context = new JellyfinDbContext(
            options,
            NullLogger<JellyfinDbContext>.Instance,
            new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance),
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
        return context.Database.GetMigrations().ToArray();
    }

    /// <summary>
    /// Opens a snapshot for inspection.
    /// </summary>
    /// <param name="path">The path of the snapshot.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The open, read-only connection.</returns>
    public static async Task<SqliteConnection> OpenReadOnlyAsync(string path, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    /// <summary>
    /// Inspects a database.
    /// </summary>
    /// <param name="connection">An open connection to the snapshot.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The findings and the row counts and hashes of the tables.</returns>
    public async Task<SqliteInspection> InspectAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var findings = new ImportFindingCollector();
        var schema = await ReadSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        await CheckHistoryAsync(connection, schema, findings, cancellationToken).ConfigureAwait(false);

        var summaries = new List<ImportTableSummary>();
        foreach (var table in CheckSchema(schema, findings))
        {
            await CheckForeignKeysAsync(connection, table, schema, findings, cancellationToken).ConfigureAwait(false);
            summaries.Add(await ScanTableAsync(connection, table, findings, cancellationToken).ConfigureAwait(false));
        }

        return new SqliteInspection(findings.ToList(), summaries);
    }

    private static Version Normalize(Version version)
        => new(version.Major, Math.Max(version.Minor, 0), Math.Max(version.Build, 0), Math.Max(version.Revision, 0));

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static ValueKind KindOf(ImportColumn column)
    {
        if (column.IsArray)
        {
            return ValueKind.IntegerArray;
        }

        return column.StoreType switch
        {
            "uuid" => ValueKind.Uuid,
            "timestamp with time zone" => ValueKind.Timestamp,
            "boolean" => ValueKind.Boolean,
            "smallint" => ValueKind.Int16,
            "integer" => ValueKind.Int32,
            "bigint" => ValueKind.Int64,
            "real" or "double precision" => ValueKind.Real,
            "bytea" => ValueKind.Blob,
            _ when column.StoreType.StartsWith("text", StringComparison.Ordinal) || column.StoreType.StartsWith("character", StringComparison.Ordinal) => ValueKind.Text,
            _ => throw new NotSupportedException($"The column {column.Name} has the PostgreSQL type {column.StoreType}, which the import does not handle.")
        };
    }

    private static bool IsStoredAsText(ValueKind kind) => kind is ValueKind.Text or ValueKind.Uuid or ValueKind.Timestamp or ValueKind.IntegerArray;

    private static bool IsAllowedStorageClass(ValueKind kind, string storageClass) => kind switch
    {
        _ when IsStoredAsText(kind) => storageClass == "text",
        ValueKind.Real => storageClass is "real" or "integer",
        ValueKind.Blob => storageClass == "blob",
        _ => storageClass == "integer"
    };

    private static bool IsLongArray(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Array
                && document.RootElement.EnumerateArray().All(e => e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out _));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static int IndexBytes(ValueKind kind, int storedBytes) => kind switch
    {
        ValueKind.Uuid => 16,
        ValueKind.Timestamp or ValueKind.Int64 or ValueKind.Real => 8,
        ValueKind.Int32 => 4,
        ValueKind.Int16 => 2,
        ValueKind.Boolean => 1,
        _ => storedBytes
    };

    [GeneratedRegex(@"\d{2}:\d{2}(:\d{2}(\.\d+)?)? ?(Z|[+-]\d{2}(:?\d{2})?)$", RegexOptions.CultureInvariant)]
    private static partial Regex TimeZoneSuffix();

    private static async Task<Dictionary<string, HashSet<string>>> ReadSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var schema = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = "SELECT m.name, p.name FROM sqlite_master AS m JOIN pragma_table_info(m.name) AS p WHERE m.type = 'table' ORDER BY m.name, p.cid";
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var table = reader.GetString(0);
                    if (!schema.TryGetValue(table, out var columns))
                    {
                        columns = new HashSet<string>(StringComparer.Ordinal);
                        schema[table] = columns;
                    }

                    columns.Add(reader.GetString(1));
                }
            }
        }

        return schema;
    }

    private async Task CheckHistoryAsync(SqliteConnection connection, Dictionary<string, HashSet<string>> schema, ImportFindingCollector findings, CancellationToken cancellationToken)
    {
        var check = nameof(PreflightCheck.MissingHistory);
        if (!schema.ContainsKey(HistoryTable))
        {
            findings.Add(check, ImportFindingSeverity.Error, HistoryTable);
            return;
        }

        var rows = new List<(string Id, string ProductVersion)>();
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = "SELECT \"MigrationId\", \"ProductVersion\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\"";
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    rows.Add((reader.GetString(0), reader.GetString(1)));
                }
            }
        }

        var known = _schemaMigrationIds.Union(_codeMigrationIds).ToHashSet(StringComparer.Ordinal);
        var applied = rows.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in known.Except(applied).Order(StringComparer.Ordinal))
        {
            findings.Add(nameof(PreflightCheck.PendingMigrations), ImportFindingSeverity.Error, HistoryTable, primaryKey: () => id);
        }

        var newestKnown = known.Max(StringComparer.Ordinal);
        foreach (var (id, productVersion) in rows)
        {
            if (!known.Contains(id))
            {
                // Ids start with a timestamp: an unknown id newer than every id of this server comes from a newer server.
                var newer = newestKnown is null || string.CompareOrdinal(id, newestKnown) > 0;
                findings.Add(
                    newer ? nameof(PreflightCheck.NewerMigrations) : nameof(PreflightCheck.RetiredMigrations),
                    newer ? ImportFindingSeverity.Error : ImportFindingSeverity.Warning,
                    HistoryTable,
                    primaryKey: () => id);
            }
            else if (_codeMigrationIds.Contains(id) && Version.TryParse(productVersion, out var version) && Normalize(version) > _serverVersion)
            {
                findings.Add(nameof(PreflightCheck.NewerMigrations), ImportFindingSeverity.Error, HistoryTable, primaryKey: () => id);
            }
        }
    }

    private List<ImportTable> CheckSchema(Dictionary<string, HashSet<string>> schema, ImportFindingCollector findings)
    {
        var complete = new List<ImportTable>();
        foreach (var table in _model.Tables)
        {
            if (!schema.TryGetValue(table.Name, out var columns))
            {
                findings.Add(nameof(PreflightCheck.MissingTable), ImportFindingSeverity.Error, table.Name);
                continue;
            }

            var missing = table.Columns.Where(c => !columns.Contains(c.Name)).ToList();
            foreach (var column in missing)
            {
                findings.Add(nameof(PreflightCheck.MissingColumn), ImportFindingSeverity.Error, table.Name, column.Name);
            }

            foreach (var column in columns.Where(c => table.Columns.All(m => m.Name != c)).Order(StringComparer.Ordinal))
            {
                findings.Add(nameof(PreflightCheck.UnknownColumn), ImportFindingSeverity.Error, table.Name, column);
            }

            if (missing.Count == 0)
            {
                complete.Add(table);
            }
        }

        foreach (var table in schema.Keys.Order(StringComparer.Ordinal))
        {
            if (!table.StartsWith("sqlite_", StringComparison.Ordinal)
                && !table.StartsWith("__EFMigrations", StringComparison.Ordinal)
                && _model.Tables.All(t => t.Name != table))
            {
                findings.Add(nameof(PreflightCheck.UnknownTable), ImportFindingSeverity.Warning, table);
            }
        }

        return complete;
    }

    private async Task CheckForeignKeysAsync(SqliteConnection connection, ImportTable table, Dictionary<string, HashSet<string>> schema, ImportFindingCollector findings, CancellationToken cancellationToken)
    {
        foreach (var foreignKey in table.ForeignKeys)
        {
            if (!schema.TryGetValue(foreignKey.PrincipalTable, out var principalColumns) || !foreignKey.PrincipalColumns.All(principalColumns.Contains))
            {
                continue;
            }

            var keyColumns = table.PrimaryKey.Count > 0 ? table.PrimaryKey : ["rowid"];
            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
#pragma warning disable CA2100 // Identifiers come from the EF model.
                command.CommandText = $"SELECT {string.Join(", ", keyColumns.Select(c => "c." + Quote(c)))} FROM {Quote(table.Name)} AS c "
                    + $"WHERE {string.Join(" AND ", foreignKey.Columns.Select(c => $"c.{Quote(c)} IS NOT NULL"))} "
                    + $"AND NOT EXISTS (SELECT 1 FROM {Quote(foreignKey.PrincipalTable)} AS p WHERE "
                    + string.Join(" AND ", foreignKey.Columns.Select((c, i) => $"p.{Quote(foreignKey.PrincipalColumns[i])} = c.{Quote(c)}"))
                    + ")";
#pragma warning restore CA2100
                var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        var key = string.Join('|', Enumerable.Range(0, keyColumns.Count).Select(i => Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)));
                        findings.Add(nameof(PreflightCheck.Orphans), ImportFindingSeverity.Error, table.Name, index: foreignKey.Name, primaryKey: () => key);
                    }
                }
            }
        }
    }

    private async Task<ImportTableSummary> ScanTableAsync(SqliteConnection connection, ImportTable table, ImportFindingCollector findings, CancellationToken cancellationToken)
    {
        var columns = table.Columns;
        var kinds = columns.Select(KindOf).ToArray();
        var ordinals = columns.Select((c, i) => (c.Name, i)).ToDictionary(e => e.Name, e => e.i, StringComparer.Ordinal);
        var primaryKey = table.PrimaryKey.Select(c => ordinals[c]).ToArray();
        var indexes = table.Indexes.Select(i => (Index: i, Columns: i.Columns.Select(c => ordinals[c]).ToArray())).ToArray();

        // Keys that SQLite compares as text but PostgreSQL compares as values.
        var convertedKeys = indexes
            .Where(i => i.Index.IsUnique && i.Index.Expression is null && i.Columns.Any(c => kinds[c] is ValueKind.Uuid or ValueKind.Timestamp))
            .Select(i => (i.Index.Name, i.Columns, Seen: new HashSet<string>(StringComparer.Ordinal)))
            .ToArray();
        var minValues = new long[columns.Count];
        var pastMaxValues = new long[columns.Count];
        var hash = new TableContentHash();
        var rows = 0L;

        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
#pragma warning disable CA2100 // Identifiers come from the EF model.
            command.CommandText = "SELECT "
                + string.Join(", ", columns.Select((c, i) => (IsStoredAsText(kinds[i]) ? $"CAST({Quote(c.Name)} AS BLOB)" : Quote(c.Name)) + $", typeof({Quote(c.Name)})"))
                + $" FROM {Quote(table.Name)}";
#pragma warning restore CA2100
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                var values = new object?[columns.Count];
                var canonical = new string[columns.Count];
                var sizes = new int[columns.Count];
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    rows++;
                    string Key() => string.Join('|', primaryKey.Select(o => values[o] is { } value ? ValueCanonicalizer.Canonicalize(columns[o], value) : ReadRaw(reader, o * 2)));
                    var valid = true;
                    Array.Clear(values);
                    for (var i = 0; i < columns.Count; i++)
                    {
                        var column = columns[i];
                        var storageClass = reader.GetString((i * 2) + 1);
                        if (storageClass == "null")
                        {
                            if (!column.IsNullable)
                            {
                                findings.Add(nameof(PreflightCheck.NullInRequiredColumn), ImportFindingSeverity.Error, table.Name, column.Name, primaryKey: Key);
                                valid = false;
                            }

                            canonical[i] = ValueCanonicalizer.Null;
                            sizes[i] = 0;
                            continue;
                        }

                        if (!IsAllowedStorageClass(kinds[i], storageClass))
                        {
                            findings.Add(nameof(PreflightCheck.StorageClassMismatch), ImportFindingSeverity.Error, table.Name, column.Name, primaryKey: Key);
                            valid = false;
                            continue;
                        }

                        var check = ReadValue(reader, i, kinds[i], out values[i], out sizes[i]);
                        if (check is null && kinds[i] == ValueKind.Timestamp)
                        {
                            try
                            {
                                canonical[i] = ValueCanonicalizer.Timestamp(values[i]!);
                                minValues[i] += canonical[i] == "-infinity" ? 1 : 0;
                                pastMaxValues[i] += canonical[i] == "infinity" ? 1 : 0;
                            }
                            catch (FormatException)
                            {
                                check = TimeZoneSuffix().IsMatch((string)values[i]!) ? PreflightCheck.TimestampWithOffset : PreflightCheck.InvalidTimestamp;
                            }
                        }
                        else if (check is null)
                        {
                            canonical[i] = ValueCanonicalizer.Canonicalize(column, values[i]);
                        }

                        if (check is { } failed)
                        {
                            values[i] = null;
                            findings.Add(failed.ToString(), ImportFindingSeverity.Error, table.Name, column.Name, primaryKey: Key);
                            valid = false;
                        }
                    }

                    if (!valid)
                    {
                        continue;
                    }

                    hash.AddCanonicalRow(canonical);
                    foreach (var (name, keyColumns, seen) in convertedKeys)
                    {
                        if (keyColumns.All(c => values[c] is not null) && !seen.Add(string.Join('\x1F', keyColumns.Select(c => canonical[c]))))
                        {
                            findings.Add(nameof(PreflightCheck.DuplicateKeyAfterConversion), ImportFindingSeverity.Error, table.Name, index: name, primaryKey: Key);
                        }
                    }

                    foreach (var (index, indexColumns) in indexes)
                    {
                        if (indexColumns.Sum(c => values[c] is null ? 0 : IndexBytes(kinds[c], sizes[c])) > MaxIndexRowBytes)
                        {
                            findings.Add(nameof(PreflightCheck.IndexRowTooLarge), ImportFindingSeverity.Error, table.Name, index: index.Name, primaryKey: Key);
                        }
                    }
                }
            }
        }

        var sentinels = columns
            .Select((c, i) => new ImportTimestampSentinels(c.Name, minValues[i], pastMaxValues[i]))
            .Where(s => s.MinValue > 0 || s.PastMaxValue > 0)
            .ToArray();
        return new ImportTableSummary(table.Name, rows, hash.Value, sentinels);
    }

    private static PreflightCheck? ReadValue(SqliteDataReader reader, int column, ValueKind kind, out object? value, out int size)
    {
        var ordinal = column * 2;
        value = null;
        size = 0;
        switch (kind)
        {
            case ValueKind.Boolean:
                {
                    var number = reader.GetInt64(ordinal);
                    value = number;
                    return number is 0 or 1 ? null : PreflightCheck.InvalidBoolean;
                }

            case ValueKind.Int16 or ValueKind.Int32:
                {
                    var number = reader.GetInt64(ordinal);
                    value = number;
                    var (min, max) = kind == ValueKind.Int16 ? (short.MinValue, short.MaxValue) : (int.MinValue, int.MaxValue);
                    return number >= min && number <= max ? null : PreflightCheck.IntegerOutOfRange;
                }

            case ValueKind.Int64:
                value = reader.GetInt64(ordinal);
                return null;

            case ValueKind.Real:
                value = reader.GetDouble(ordinal);
                return null;

            case ValueKind.Blob:
                {
                    var bytes = reader.GetFieldValue<byte[]>(ordinal);
                    value = bytes;
                    size = bytes.Length;
                    return null;
                }
        }

        var raw = reader.GetFieldValue<byte[]>(ordinal);
        size = raw.Length;
        string text;
        try
        {
            text = _strictUtf8.GetString(raw);
        }
        catch (DecoderFallbackException)
        {
            return PreflightCheck.InvalidUtf8;
        }

        value = text;
        if (text.Contains('\0', StringComparison.Ordinal))
        {
            return PreflightCheck.NulInText;
        }

        return kind switch
        {
            ValueKind.Uuid when !Guid.TryParseExact(text, "D", out _) => PreflightCheck.InvalidUuid,
            ValueKind.IntegerArray when !IsLongArray(text) => PreflightCheck.InvalidKeyframeTicks,
            _ => null
        };
    }

    private static string ReadRaw(SqliteDataReader reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
        return value switch
        {
            DBNull => "NULL",
            byte[] bytes => Encoding.UTF8.GetString(bytes).Replace("\0", "\\0", StringComparison.Ordinal),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
        };
    }
}
