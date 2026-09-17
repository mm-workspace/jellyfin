using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Jellyfin.Server.Implementations.DatabaseImport;

/// <summary>
/// Writes a value read from SQLite or PostgreSQL in one text form, so the same row hashes the same on both databases.
/// </summary>
/// <remarks>
/// The rules follow how PostgreSQL accepts SQLite's text: timestamps are UTC and rounded to microseconds as PostgreSQL parses them,
/// ids ignore case, booleans are 0 or 1, real values are compared as 32-bit floats and negative zero equals zero.
/// </remarks>
internal static class ValueCanonicalizer
{
    /// <summary>
    /// The text for NULL. It cannot be produced by any value.
    /// </summary>
    public const string Null = "\x01NULL";

    private const long TicksPerMicrosecond = TimeSpan.TicksPerMillisecond / 1000;

    private const string SqliteDateTimeFormat = "yyyy-MM-dd HH:mm:ss.FFFFFFF";

    private static readonly string[] _sqliteSecondFormats = ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss"];

    /// <summary>
    /// Gets the canonical text of a value.
    /// </summary>
    /// <param name="column">The model column the value was read from.</param>
    /// <param name="value">The value as the database reader returned it.</param>
    /// <returns>The canonical text.</returns>
    public static string Canonicalize(ImportColumn column, object? value)
    {
        if (value is null || value is DBNull)
        {
            return Null;
        }

        var type = column.ClrType;
        if (type.IsEnum)
        {
            type = Enum.GetUnderlyingType(type);
        }

        if (type == typeof(bool))
        {
            return Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0 ? "1" : "0";
        }

        if (type == typeof(int) || type == typeof(long) || type == typeof(uint) || type == typeof(short) || type == typeof(byte))
        {
            return Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        }

        if (type == typeof(float))
        {
            return Real((float)Convert.ToDouble(value, CultureInfo.InvariantCulture));
        }

        if (type == typeof(double))
        {
            return Real(Convert.ToDouble(value, CultureInfo.InvariantCulture));
        }

        if (type == typeof(Guid))
        {
            return (value is Guid guid ? guid : Guid.Parse((string)value)).ToString("D");
        }

        if (type == typeof(DateTime))
        {
            return Timestamp(value);
        }

        if (type == typeof(string))
        {
            return (string)value;
        }

        if (type == typeof(byte[]))
        {
            return Convert.ToHexStringLower((byte[])value);
        }

        if (column.IsArray || typeof(IEnumerable<long>).IsAssignableFrom(type))
        {
            return LongList(value);
        }

        throw new NotSupportedException($"The column {column.Name} has the type {type}, which the import does not handle.");
    }

    /// <summary>
    /// Parses a timestamp as SQLite stores it and rounds it the way PostgreSQL does.
    /// </summary>
    /// <remarks>
    /// PostgreSQL reads the fraction of a second as a double and rounds its microseconds with <c>rint</c>, so a tie in the
    /// seventh digit goes the way its binary value leans, which is not always to the even digit.
    /// </remarks>
    /// <param name="value">The stored text or a <see cref="DateTime"/>.</param>
    /// <returns>The canonical text; <c>-infinity</c> for <see cref="DateTime.MinValue"/> and <c>infinity</c> for the values that round past <see cref="DateTime.MaxValue"/>.</returns>
    internal static string Timestamp(object value)
    {
        var text = value is DateTime dateTime
            ? dateTime.ToString(SqliteDateTimeFormat, CultureInfo.InvariantCulture)
            : (string)value;

        var fractionStart = text.IndexOf('.', StringComparison.Ordinal);
        var seconds = DateTime.ParseExact(
            fractionStart < 0 ? text : text[..fractionStart],
            _sqliteSecondFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        var microseconds = fractionStart < 0
            ? 0
            : (long)Math.Round(double.Parse(text.AsSpan(fractionStart), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture) * 1_000_000, MidpointRounding.ToEven);
        var ticks = seconds.Ticks + (microseconds * TicksPerMicrosecond);

        if (ticks == 0)
        {
            return "-infinity";
        }

        return ticks > DateTime.MaxValue.Ticks
            ? "infinity"
            : new DateTime(ticks, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
    }

    private static string Real(double value) => value == 0 ? "0" : value.ToString("R", CultureInfo.InvariantCulture);

    private static string Real(float value) => value == 0 ? "0" : value.ToString("R", CultureInfo.InvariantCulture);

    private static string LongList(object value)
    {
        IEnumerable<long> values = value switch
        {
            string json => JsonSerializer.Deserialize<long[]>(json) ?? [],
            IEnumerable<long> list => list,
            IEnumerable other => other.Cast<object>().Select(o => Convert.ToInt64(o, CultureInfo.InvariantCulture)),
            _ => throw new NotSupportedException($"A list of numbers was stored as {value.GetType()}.")
        };
        return string.Join(',', values.Select(v => v.ToString(CultureInfo.InvariantCulture)));
    }
}
