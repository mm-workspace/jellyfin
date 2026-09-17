using System;
using System.Text;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Jellyfin.Database.Providers.PostgreSQL.ValueConverters;

/// <summary>
/// Makes text storable in PostgreSQL the way SQLite stores it.
/// </summary>
/// <remarks>
/// NUL characters are removed, because PostgreSQL text cannot hold them. A surrogate without its pair becomes
/// U+FFFD, which is what SQLite stores for it; the PostgreSQL client refuses to send it at all.
/// </remarks>
public class DatabaseTextConverter : ValueConverter<string, string>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseTextConverter"/> class.
    /// </summary>
    public DatabaseTextConverter()
        : base(v => MakeStorable(v), v => v)
    {
    }

    /// <summary>
    /// Removes NUL characters and replaces unpaired surrogates.
    /// </summary>
    /// <param name="value">The text.</param>
    /// <returns>The text as it is stored.</returns>
    internal static string MakeStorable(string value)
    {
        var span = value.AsSpan();
        if (span.IndexOf('\0') < 0 && span.IndexOfAnyInRange('\uD800', '\uDFFF') < 0)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\0')
            {
                continue;
            }

            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                builder.Append(c).Append(value[++i]);
            }
            else if (char.IsSurrogate(c))
            {
                builder.Append('\uFFFD');
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
