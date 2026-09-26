using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Server.Implementations.DatabaseImport.Sqlite;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.DatabaseImport.Sqlite;

public class ConvertedKeySetTests
{
    private readonly Dictionary<long, string> _rows = [];
    private readonly List<long> _reads = [];

    [Fact]
    public async Task AddAsync_DifferentKeys_ReadsNoRow()
    {
        var keys = new ConvertedKeySet(ReadKeyAsync, ConvertedKeySet.Hash);

        Assert.True(await AddAsync(keys, 1, "a"));
        Assert.True(await AddAsync(keys, 2, "A"));
        Assert.True(await AddAsync(keys, 3, "b"));
        Assert.Empty(_reads);
    }

    [Fact]
    public async Task AddAsync_RepeatedKey_IsConfirmedWithTheFirstRow()
    {
        var keys = new ConvertedKeySet(ReadKeyAsync, ConvertedKeySet.Hash);

        Assert.True(await AddAsync(keys, 1, "a"));
        Assert.True(await AddAsync(keys, 2, "b"));
        Assert.False(await AddAsync(keys, 3, "a"));
        Assert.False(await AddAsync(keys, 4, "a"));
        Assert.Equal([1L, 1L], _reads);
    }

    [Fact]
    public async Task AddAsync_DifferentKeysWithTheSameHash_AreNotDuplicates()
    {
        var keys = new ConvertedKeySet(ReadKeyAsync, _ => 42);

        Assert.True(await AddAsync(keys, 1, "a"));
        Assert.True(await AddAsync(keys, 2, "b"));
        Assert.True(await AddAsync(keys, 3, "c"));
        Assert.False(await AddAsync(keys, 4, "b"));
        Assert.False(await AddAsync(keys, 5, "a"));
        Assert.False(await AddAsync(keys, 6, "c"));
        Assert.Equal([1L, 1L, 1L, 1L, 1L], _reads);
    }

    [Fact]
    public async Task AddAsync_KeysKeptWhole_ReadsNoRow()
    {
        var keys = new ConvertedKeySet();

        Assert.True(await AddAsync(keys, 1, "a"));
        Assert.True(await AddAsync(keys, 2, "b"));
        Assert.False(await AddAsync(keys, 3, "a"));
        Assert.Empty(_reads);
    }

    private Task<bool> AddAsync(ConvertedKeySet keys, long rowId, string key)
    {
        _rows.Add(rowId, key);
        return keys.AddAsync(key, rowId, TestContext.Current.CancellationToken);
    }

    private Task<string> ReadKeyAsync(long rowId, CancellationToken cancellationToken)
    {
        _reads.Add(rowId);
        return Task.FromResult(_rows[rowId]);
    }
}
