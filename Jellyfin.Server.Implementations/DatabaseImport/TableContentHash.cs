using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

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
    /// Adds a row.
    /// </summary>
    /// <param name="columns">The columns of the row, in model order.</param>
    /// <param name="values">The values as the database reader returned them.</param>
    public void AddRow(IReadOnlyList<ImportColumn> columns, IReadOnlyList<object?> values)
    {
        var text = new StringBuilder();
        for (var i = 0; i < columns.Count; i++)
        {
            text.Append(ValueCanonicalizer.Canonicalize(columns[i], values[i])).Append(Separator);
        }

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()), hash);
        _sum += BinaryPrimitives.ReadUInt128LittleEndian(hash);
        RowCount++;
    }
}
