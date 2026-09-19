using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Server.Implementations.DatabaseImport;

/// <summary>
/// An order-independent hash of the rows of one table.
/// </summary>
/// <remarks>
/// Each row is hashed with SHA-256 over its canonical values; the first 128 bits of the row hashes are added up, so the
/// result does not depend on the order the rows are read in.
/// </remarks>
internal sealed class TableContentHash
{
    private const char Separator = '\x1F';

    private UInt128 _sum;

    /// <summary>
    /// Gets the number of rows added.
    /// </summary>
    public long RowCount { get; private set; }

    /// <summary>
    /// Gets the hash as text.
    /// </summary>
    public string Value => string.Create(CultureInfo.InvariantCulture, $"{RowCount}:{_sum:x32}");

    /// <summary>
    /// Hashes every row of a table.
    /// </summary>
    /// <param name="connection">An open connection to a SQLite or PostgreSQL database.</param>
    /// <param name="table">The table.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The hash of the table.</returns>
    public static async Task<TableContentHash> ComputeAsync(DbConnection connection, ImportTable table, CancellationToken cancellationToken)
    {
        var hash = new TableContentHash();
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
#pragma warning disable CA2100 // Identifiers come from the EF model.
            command.CommandText = $"SELECT {string.Join(", ", table.Columns.Select(c => SqlIdentifier.Quote(c.Name)))} FROM {SqlIdentifier.Quote(table.Name)}";
#pragma warning restore CA2100
            command.CommandTimeout = 0;
            var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                var values = new object?[table.Columns.Count];
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    for (var i = 0; i < values.Length; i++)
                    {
                        values[i] = reader.GetValue(i);
                    }

                    hash.AddRow(table.Columns, values);
                }
            }
        }

        return hash;
    }

    /// <summary>
    /// Adds a row.
    /// </summary>
    /// <param name="columns">The columns of the row, in model order.</param>
    /// <param name="values">The values as the database reader returned them.</param>
    public void AddRow(IReadOnlyList<ImportColumn> columns, IReadOnlyList<object?> values)
    {
        var canonical = new string[columns.Count];
        for (var i = 0; i < canonical.Length; i++)
        {
            canonical[i] = ValueCanonicalizer.Canonicalize(columns[i], values[i]);
        }

        AddCanonicalRow(canonical);
    }

    /// <summary>
    /// Adds a row whose values are canonical already.
    /// </summary>
    /// <param name="canonicalValues">The <see cref="ValueCanonicalizer.Canonicalize"/> text of each column, in model order.</param>
    public void AddCanonicalRow(IReadOnlyList<string> canonicalValues)
    {
        var text = new StringBuilder();
        foreach (var value in canonicalValues)
        {
            text.Append(value).Append(Separator);
        }

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()), hash);
        _sum += BinaryPrimitives.ReadUInt128LittleEndian(hash);
        RowCount++;
    }
}
