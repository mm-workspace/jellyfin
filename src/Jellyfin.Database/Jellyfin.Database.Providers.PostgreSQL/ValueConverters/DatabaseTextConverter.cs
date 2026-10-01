using Jellyfin.Extensions;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Jellyfin.Database.Providers.PostgreSQL.ValueConverters;

/// <summary>
/// Makes text storable in PostgreSQL the way SQLite stores it.
/// </summary>
/// <remarks>
/// NUL characters are removed, because PostgreSQL text cannot hold them. A surrogate without its pair becomes
/// U+FFFD, which is what SQLite stores for it; the PostgreSQL client refuses to send it at all. Callers that
/// write through the repositories already apply the same rule, so that both providers keep the same text; this
/// covers every other string that reaches the model.
/// </remarks>
public class DatabaseTextConverter : ValueConverter<string, string>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseTextConverter"/> class.
    /// </summary>
    public DatabaseTextConverter()
        : base(v => v.SanitizeForDatabase(), v => v)
    {
    }
}
