using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Server.Implementations.DatabaseImport.Sqlite;

/// <summary>
/// The keys of one unique index as PostgreSQL compares them, added row by row to find the rows whose key an earlier row has.
/// </summary>
/// <remarks>
/// A table can have millions of rows, so the keys themselves are not kept: only a 64-bit hash of each key and the rowid of
/// the first row that has it. A repeated hash is confirmed by reading the key of that row again, and a key that shares its
/// hash with a different key is then kept whole, so a hash collision is never reported as a duplicate. The rows of a table
/// without a rowid cannot be read again, so all of its keys are kept whole.
/// </remarks>
internal sealed class ConvertedKeySet
{
    private readonly Dictionary<ulong, long> _firstRows = [];
    private readonly HashSet<string> _wholeKeys = new(StringComparer.Ordinal);
    private readonly Func<long, CancellationToken, Task<string>>? _readKey;
    private readonly Func<string, ulong> _hash = Hash;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConvertedKeySet"/> class that keeps every key whole.
    /// </summary>
    public ConvertedKeySet()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConvertedKeySet"/> class that keeps the hashes of the keys.
    /// </summary>
    /// <param name="readKey">Reads the key of the row with a rowid again.</param>
    /// <param name="hash">Hashes a key, usually <see cref="Hash"/>.</param>
    public ConvertedKeySet(Func<long, CancellationToken, Task<string>> readKey, Func<string, ulong> hash)
    {
        _readKey = readKey;
        _hash = hash;
    }

    /// <summary>
    /// Hashes a key into 64 bits: the first 8 bytes of its SHA-256.
    /// </summary>
    /// <param name="key">The canonical key.</param>
    /// <returns>The hash.</returns>
    public static ulong Hash(string key)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(MemoryMarshal.AsBytes(key.AsSpan()), hash);
        return BinaryPrimitives.ReadUInt64LittleEndian(hash);
    }

    /// <summary>
    /// Adds the key of a row.
    /// </summary>
    /// <param name="key">The canonical key.</param>
    /// <param name="rowId">The rowid of the row; unused when every key is kept whole.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><c>true</c> if no row added before has the key; <c>false</c> if one has.</returns>
    public async Task<bool> AddAsync(string key, long rowId, CancellationToken cancellationToken)
    {
        if (_readKey is null)
        {
            return _wholeKeys.Add(key);
        }

        var hash = _hash(key);
        if (_firstRows.TryAdd(hash, rowId))
        {
            return true;
        }

        var firstKey = await _readKey(_firstRows[hash], cancellationToken).ConfigureAwait(false);
        return !string.Equals(firstKey, key, StringComparison.Ordinal) && _wholeKeys.Add(key);
    }
}
