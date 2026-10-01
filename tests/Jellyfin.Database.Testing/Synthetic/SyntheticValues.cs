using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Database.Testing.Synthetic;

/// <summary>
/// Deterministic column values: ordinary ones and the extreme ones of each type.
/// </summary>
internal sealed class SyntheticValues
{
    /// <summary>
    /// The number of rows needed to use every extreme value of every type once.
    /// </summary>
    public const int EdgeRowCount = 12;

    private const int HugeTextLength = 1024 * 1024;

    private const int IndexedTextLength = 400;

    private static readonly DateTime _start = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly string[] _words =
    [
        "Movie",
        "Élodie",
        "Straße",
        "İstanbul",
        "日本語",
        "O'Brien",
        "\"Quoted\"",
        "Back\\slash",
        "Tab\there",
        "Line\nbreak",
        "a,b;c|d",
        "Clapper \U0001F3AC"
    ];

    private static readonly string[] _edgeText =
    [
        string.Empty,
        "\t\r\n\\\"'",
        "\\N",
        "NULL",
        "Élodie 日本語 \U0001F3AC\U0001F600",
        "e\U00000301 \U000005E9\U000005DC\U000005D5\U000005DD \U0000200B end",
        " leading and trailing ",
        "{1,2}[3]\\x00",
        "\U0001F600",
        "İıŞş ǅ ß ﬁ"
    ];

    private static readonly object[] _edgeInt = [int.MinValue, int.MaxValue, 0, -1, 1];

    private static readonly object[] _edgeLong = [long.MinValue, long.MaxValue, 0L, -1L];

    private static readonly object[] _edgeUInt = [0u, uint.MaxValue, 1u];

    private static readonly object[] _edgeDouble =
    [
        double.MaxValue, -double.MaxValue, double.Epsilon, -0.0d, 1e-300d, 0.1d, double.PositiveInfinity, double.NegativeInfinity, 1d / 3
    ];

    private static readonly object[] _edgeFloat =
    [
        float.MaxValue, -float.MaxValue, float.Epsilon, -0.0f, 0.1f, float.PositiveInfinity, float.NegativeInfinity, 1f / 3
    ];

    private static readonly object[] _edgeGuid =
    [
        Guid.Empty, Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"), Guid.Parse("00000000-0000-0000-0000-000000000002")
    ];

    private static readonly object[] _edgeDateTime =
    [
        DateTime.MinValue,
        DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc),
        new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(5),
        new DateTime(2024, 5, 1, 12, 34, 56, DateTimeKind.Utc).AddTicks(5),
        new DateTime(2024, 5, 1, 12, 34, 56, DateTimeKind.Utc).AddTicks(1255),
        new DateTime(2024, 5, 1, 12, 34, 56, DateTimeKind.Utc).AddTicks(9_999_995),
        new DateTime(1969, 12, 31, 23, 59, 59, DateTimeKind.Utc).AddTicks(9_999_999),
        new DateTime(2024, 2, 29, 0, 0, 0, DateTimeKind.Utc),
        new DateTime(9999, 12, 31, 23, 59, 59, DateTimeKind.Utc).AddTicks(9_999_994),
        new DateTime(9999, 12, 31, 23, 59, 59, DateTimeKind.Utc).AddTicks(9_999_995)
    ];

    private readonly Random _random;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyntheticValues"/> class.
    /// </summary>
    /// <param name="seed">The seed.</param>
    public SyntheticValues(int seed)
    {
        _random = new Random(seed);
    }

    /// <summary>
    /// Returns a random number in [0, <paramref name="maxValue"/>).
    /// </summary>
    /// <param name="maxValue">The exclusive upper bound.</param>
    /// <returns>The number.</returns>
    public int Next(int maxValue) => _random.Next(maxValue);

    /// <summary>
    /// Creates an ordinary value.
    /// </summary>
    /// <param name="clrType">The model type of the column.</param>
    /// <param name="maxLength">The maximum length of text.</param>
    /// <returns>The value.</returns>
    public object Ordinary(Type clrType, int? maxLength)
    {
        var type = Nullable.GetUnderlyingType(clrType) ?? clrType;
        if (type.IsEnum)
        {
            var values = Enum.GetValues(type);
            return values.GetValue(_random.Next(values.Length))!;
        }

        if (type == typeof(string))
        {
            return Truncate($"{_words[_random.Next(_words.Length)]} {_random.Next(100_000)}", maxLength);
        }

        if (type == typeof(Guid))
        {
            Span<byte> bytes = stackalloc byte[16];
            _random.NextBytes(bytes);
            return new Guid(bytes);
        }

        if (type == typeof(DateTime))
        {
            // Full tick precision, as DateTime.UtcNow produces.
            return _start.AddTicks(_random.NextInt64(TimeSpan.TicksPerDay * 365 * 6));
        }

        if (type == typeof(bool))
        {
            return _random.Next(2) == 1;
        }

        if (type == typeof(int))
        {
            return _random.Next(1_000);
        }

        if (type == typeof(long))
        {
            return _random.NextInt64(1_000_000_000_000);
        }

        if (type == typeof(uint))
        {
            return (uint)_random.Next();
        }

        if (type == typeof(double))
        {
            return _random.NextDouble() * 10;
        }

        if (type == typeof(float))
        {
            return (float)(_random.NextDouble() * 10);
        }

        if (type == typeof(byte[]))
        {
            var bytes = new byte[_random.Next(33)];
            _random.NextBytes(bytes);
            return bytes;
        }

        if (type.IsAssignableFrom(typeof(List<long>)))
        {
            var ticks = new List<long>();
            var tick = 0L;
            for (var i = _random.Next(20); i > 0; i--)
            {
                tick += _random.NextInt64(1, 100_000_000);
                ticks.Add(tick);
            }

            return ticks;
        }

        throw new NotSupportedException($"The synthetic library does not generate values of type {type}.");
    }

    /// <summary>
    /// Gets an extreme value.
    /// </summary>
    /// <param name="clrType">The model type of the column.</param>
    /// <param name="maxLength">The maximum length of text.</param>
    /// <param name="row">The number of the edge row; each row takes the next extreme value.</param>
    /// <param name="indexed">Whether the column is part of an index, which limits the size of text.</param>
    /// <param name="huge">Whether the column may hold the largest text and lists of the table.</param>
    /// <returns>The value.</returns>
    public object Edge(Type clrType, int? maxLength, int row, bool indexed, bool huge)
    {
        var type = Nullable.GetUnderlyingType(clrType) ?? clrType;
        if (type.IsEnum)
        {
            var defined = Enum.GetValues(type).Cast<object>().OrderBy(v => Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            return row % 2 == 0 ? defined[0] : defined[^1];
        }

        if (type == typeof(string))
        {
            return row switch
            {
                0 => maxLength is { } max ? MaxLengthText(max) : Text(huge ? HugeTextLength : indexed ? IndexedTextLength : 4000),
                1 => Truncate(Text(indexed ? IndexedTextLength : 4000), maxLength),
                _ => Truncate(_edgeText[(row - 2) % _edgeText.Length], maxLength)
            };
        }

        if (type == typeof(byte[]))
        {
            return (row % 4) switch
            {
                0 => Array.Empty<byte>(),
                1 => new byte[] { 0 },
                2 => new byte[] { 0, 0, 0, 255 },
                _ => Bytes(huge ? 65_536 : 1_024)
            };
        }

        if (type.IsAssignableFrom(typeof(List<long>)))
        {
            return (row % 3) switch
            {
                0 => new List<long>(),
                1 => new List<long> { long.MinValue, 0, long.MaxValue },
                _ => Enumerable.Range(0, huge ? 100_000 : 100).Select(i => i * 40_000_000L).ToList()
            };
        }

        object[] values = type switch
        {
            _ when type == typeof(int) => _edgeInt,
            _ when type == typeof(long) => _edgeLong,
            _ when type == typeof(uint) => _edgeUInt,
            _ when type == typeof(double) => _edgeDouble,
            _ when type == typeof(float) => _edgeFloat,
            _ when type == typeof(Guid) => _edgeGuid,
            _ when type == typeof(DateTime) => _edgeDateTime,
            _ when type == typeof(bool) => [false, true],
            _ => throw new NotSupportedException($"The synthetic library does not generate values of type {type}.")
        };

        return values[row % values.Length];
    }

    private static string Truncate(string value, int? maxLength)
    {
        if (maxLength is not { } max || value.Length <= max)
        {
            return value;
        }

        return char.IsHighSurrogate(value[max - 1]) ? value[..(max - 1)] : value[..max];
    }

    private static string MaxLengthText(int maxLength)
    {
        // Astral characters take two UTF-16 units, so the text is exactly as long as the column allows.
        var pairs = string.Concat(Enumerable.Repeat("\U0001F3AC", maxLength / 2));
        return maxLength % 2 == 0 ? pairs : pairs + "a";
    }

    private string Text(int length)
    {
        // Random letters, so PostgreSQL cannot compress an indexed value below its index row limit.
        return string.Create(length, _random, static (span, random) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = (char)('a' + random.Next(26));
            }
        });
    }

    private byte[] Bytes(int length)
    {
        var bytes = new byte[length];
        _random.NextBytes(bytes);
        bytes[0] = 0;
        return bytes;
    }
}
