using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace Jellyfin.Database.Testing.Synthetic;

/// <summary>
/// Writes generated rows into one table, keeping keys unique and foreign keys pointing at existing rows.
/// </summary>
internal sealed class SyntheticTableWriter
{
    private const int MaxAttempts = 20;

    private static readonly MethodInfo _getFieldValue = typeof(DbDataReader).GetMethod(nameof(DbDataReader.GetFieldValue))!;

    private readonly ITable _table;
    private readonly IColumn[] _columns;
    private readonly RelationalTypeMapping[] _mappings;
    private readonly SyntheticValues _values;
    private readonly SyntheticKeys _keys;
    private readonly bool _roundTimestamps;
    private readonly List<(int[] Ordinals, HashSet<string> Seen)> _uniqueKeys;
    private readonly (IForeignKeyConstraint Constraint, int[] Ordinals, int[] PrincipalOrdinals)[] _foreignKeys;
    private readonly List<(int[] Ordinals, List<object?[]> Rows)> _referencedKeys;
    private readonly HashSet<int> _indexed;
    private readonly HashSet<int> _constructorArguments;
    private readonly int _identity;
    private readonly int _huge;
    private long _nextIdentity = 1;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyntheticTableWriter"/> class.
    /// </summary>
    /// <param name="table">The table.</param>
    /// <param name="values">The value source.</param>
    /// <param name="keys">The key values written so far, by table.</param>
    /// <param name="roundTimestamps">Whether to round timestamps to microseconds before writing, as PostgreSQL does when it parses text.</param>
    /// <param name="indexedColumns">The columns indexed by a provider's hand-written indexes, which limit the size of their values too.</param>
    public SyntheticTableWriter(ITable table, SyntheticValues values, SyntheticKeys keys, bool roundTimestamps, IEnumerable<string> indexedColumns)
    {
        _table = table;
        _values = values;
        _keys = keys;
        _roundTimestamps = roundTimestamps;
        _columns = table.Columns.ToArray();
        _mappings = _columns.Select(c => (RelationalTypeMapping)c.PropertyMappings[0].TypeMapping).ToArray();

        _uniqueKeys = table.UniqueConstraints.Select(u => u.Columns)
            .Concat(table.Indexes.Where(i => i.IsUnique).Select(i => i.Columns))
            .Select(columns => Ordinals(columns))
            .DistinctBy(ordinals => string.Join(',', ordinals))
            .Select(ordinals => (ordinals, new HashSet<string>(StringComparer.Ordinal)))
            .ToList();

        _foreignKeys = table.ForeignKeyConstraints
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .Select(f => (f, Ordinals(f.Columns), f.PrincipalColumns.Select(c => Array.IndexOf(f.PrincipalTable.Columns.ToArray(), c)).ToArray()))
            .ToArray();

        _referencedKeys = table.ReferencingForeignKeyConstraints
            .Select(f => Ordinals(f.PrincipalColumns))
            .DistinctBy(ordinals => string.Join(',', ordinals))
            .Select(ordinals => (ordinals, keys.Register(table.Name, ordinals)))
            .ToList();

        _indexed = table.Indexes.SelectMany(i => i.Columns)
            .Concat(table.UniqueConstraints.SelectMany(u => u.Columns))
            .Select(c => c.Name)
            .Concat(indexedColumns)
            .Select(name => Array.FindIndex(_columns, c => c.Name == name))
            .ToHashSet();

        // Entity constructors reject empty text, so the server never writes it into these columns.
        var constructorProperties = table.EntityTypeMappings
            .Select(m => m.TypeBase.ConstructorBinding)
            .SelectMany(b => b?.ParameterBindings ?? [])
            .SelectMany(p => p.ConsumedProperties)
            .ToHashSet();
        _constructorArguments = Enumerable.Range(0, _columns.Length)
            .Where(i => _columns[i].PropertyMappings.Any(m => constructorProperties.Contains(m.Property)))
            .ToHashSet();
        _identity = Array.FindIndex(_columns, c => c.PropertyMappings.Any(m =>
            m.Property.ValueGenerated == ValueGenerated.OnAdd && m.Property.IsKey() && (m.Property.ClrType == typeof(int) || m.Property.ClrType == typeof(long))));
        _huge = Array.FindIndex(_columns, c => !_indexed.Contains(Array.IndexOf(_columns, c)) && c.PropertyMappings[0].Property.ClrType == typeof(string));
    }

    /// <summary>
    /// Registers the rows already in the table, such as the rows added by migrations.
    /// </summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="transaction">The transaction.</param>
    /// <param name="sql">The SQL helper of the provider.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the read.</returns>
    public async Task ReadExistingRowsAsync(DbConnection connection, DbTransaction transaction, ISqlGenerationHelper sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
#pragma warning disable CA2100 // Identifiers come from the EF model.
        command.CommandText = $"SELECT {string.Join(", ", _columns.Select(c => sql.DelimitIdentifier(c.Name)))} FROM {sql.DelimitIdentifier(_table.Name)}";
#pragma warning restore CA2100
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = new object?[_columns.Length];
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = await ReadAsync(reader, i, cancellationToken).ConfigureAwait(false);
            }

            Reserve(row);
            Register(row);
        }
    }

    /// <summary>
    /// Writes rows.
    /// </summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="transaction">The transaction.</param>
    /// <param name="sql">The SQL helper of the provider.</param>
    /// <param name="ordinaryRows">The number of ordinary rows to try to write.</param>
    /// <param name="edgeRows">The number of rows with extreme values to try to write.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of rows written. Rows whose keys stay taken after several attempts are left out.</returns>
    public async Task<int> WriteAsync(DbConnection connection, DbTransaction transaction, ISqlGenerationHelper sql, int ordinaryRows, int edgeRows, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var names = _columns.Select((_, i) => "p" + i.ToString(CultureInfo.InvariantCulture)).ToArray();
        var parameters = new DbParameter[_columns.Length];
        for (var i = 0; i < _columns.Length; i++)
        {
            parameters[i] = _mappings[i].CreateParameter(command, sql.GenerateParameterName(names[i]), null, _columns[i].IsNullable);
            command.Parameters.Add(parameters[i]);
        }

#pragma warning disable CA2100 // Identifiers come from the EF model.
        command.CommandText = $"INSERT INTO {sql.DelimitIdentifier(_table.Name)} ({string.Join(", ", _columns.Select(c => sql.DelimitIdentifier(c.Name)))}) "
            + $"VALUES ({string.Join(", ", names.Select(sql.GenerateParameterNamePlaceholder))})";
#pragma warning restore CA2100

        var written = 0;
        for (var i = 0; i < ordinaryRows + edgeRows; i++)
        {
            for (var attempt = 0; attempt < MaxAttempts; attempt++)
            {
                var row = CreateRow(i < ordinaryRows ? null : i - ordinaryRows, attempt);
                if (row is null || !Reserve(row))
                {
                    continue;
                }

                for (var c = 0; c < row.Length; c++)
                {
                    var value = row[c];
                    if (_roundTimestamps && value is DateTime dateTime)
                    {
                        value = RoundToMicroseconds(dateTime);
                    }

                    parameters[c].Value = (value is null ? null : _mappings[c].Converter?.ConvertToProvider(value) ?? value) ?? DBNull.Value;
                }

                try
                {
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (DbException ex)
                {
                    throw new InvalidOperationException($"Writing {(i < ordinaryRows ? "an ordinary" : "an edge")} row {i} into {_table.Name} failed.", ex);
                }

                Register(row);
                written++;
                break;
            }
        }

        return written;
    }

    private static DateTime RoundToMicroseconds(DateTime value)
    {
        // What PostgreSQL stores for the text SQLite holds; Npgsql would truncate the ticks instead.
        var text = value.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);
        var fractionStart = text.IndexOf('.', StringComparison.Ordinal);
        if (fractionStart < 0)
        {
            return value;
        }

        var microseconds = (long)Math.Round(double.Parse(text.AsSpan(fractionStart), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture) * 1_000_000, MidpointRounding.ToEven);
        var ticks = value.Ticks - (value.Ticks % TimeSpan.TicksPerSecond) + (microseconds * 10);
        return ticks > DateTime.MaxValue.Ticks ? DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc) : new DateTime(ticks, DateTimeKind.Utc);
    }

    private static string KeyText(object? value) => value switch
    {
        DateTime dateTime => (dateTime.Ticks / 10).ToString(CultureInfo.InvariantCulture),
        byte[] bytes => Convert.ToHexString(bytes),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value?.ToString() ?? string.Empty
    };

    private int[] Ordinals(IEnumerable<IColumn> columns) => columns.Select(c => Array.IndexOf(_columns, c)).ToArray();

    private object?[]? CreateRow(int? edgeRow, int attempt)
    {
        var row = new object?[_columns.Length];
        var assigned = new bool[_columns.Length];

        foreach (var (constraint, ordinals, principalOrdinals) in _foreignKeys)
        {
            var nullable = ordinals.All(o => _columns[o].IsNullable);
            var principals = _keys.Get(constraint.PrincipalTable.Name, principalOrdinals);
            if (principals.Count == 0 || (nullable && _values.Next(5) == 0))
            {
                if (!nullable)
                {
                    return null;
                }
            }
            else
            {
                var principal = principals[_values.Next(principals.Count)];
                for (var i = 0; i < ordinals.Length; i++)
                {
                    row[ordinals[i]] = principal[i];
                }
            }

            foreach (var ordinal in ordinals)
            {
                assigned[ordinal] = true;
            }
        }

        for (var c = 0; c < _columns.Length; c++)
        {
            if (assigned[c])
            {
                continue;
            }

            var column = _columns[c];
            var property = column.PropertyMappings[0].Property;
            if (c == _identity)
            {
                row[c] = property.ClrType == typeof(int) ? (object)(int)_nextIdentity : _nextIdentity;
            }
            else if (edgeRow is { } edge && attempt == 0)
            {
                row[c] = _values.Edge(property.ClrType, column.MaxLength, edge, _indexed.Contains(c), c == _huge);
                if (row[c] is "" && _constructorArguments.Contains(c))
                {
                    row[c] = "-";
                }
            }
            else if (column.IsNullable && _values.Next(5) == 0)
            {
                row[c] = null;
            }
            else
            {
                row[c] = _values.Ordinary(property.ClrType, column.MaxLength);
            }
        }

        return row;
    }

    private bool Reserve(object?[] row)
    {
        var texts = new string?[_uniqueKeys.Count];
        for (var k = 0; k < _uniqueKeys.Count; k++)
        {
            var (ordinals, seen) = _uniqueKeys[k];
            if (ordinals.Any(o => row[o] is null))
            {
                continue;
            }

            texts[k] = string.Join('\x1F', ordinals.Select(o => KeyText(row[o])));
            if (seen.Contains(texts[k]!))
            {
                return false;
            }
        }

        for (var k = 0; k < _uniqueKeys.Count; k++)
        {
            if (texts[k] is { } text)
            {
                _uniqueKeys[k].Seen.Add(text);
            }
        }

        return true;
    }

    private void Register(object?[] row)
    {
        if (_identity >= 0)
        {
            _nextIdentity = Math.Max(_nextIdentity, Convert.ToInt64(row[_identity], CultureInfo.InvariantCulture) + 1);
        }

        foreach (var (ordinals, rows) in _referencedKeys)
        {
            if (ordinals.All(o => row[o] is not null))
            {
                rows.Add(ordinals.Select(o => row[o]).ToArray());
            }
        }
    }

    private async Task<object?> ReadAsync(DbDataReader reader, int ordinal, CancellationToken cancellationToken)
    {
        if (await reader.IsDBNullAsync(ordinal, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var mapping = _mappings[ordinal];
        var providerType = mapping.Converter?.ProviderClrType ?? mapping.ClrType;
        providerType = Nullable.GetUnderlyingType(providerType) ?? providerType;
        var value = _getFieldValue.MakeGenericMethod(providerType).Invoke(reader, [ordinal])!;
        return mapping.Converter?.ConvertFromProvider(value) ?? value;
    }
}
