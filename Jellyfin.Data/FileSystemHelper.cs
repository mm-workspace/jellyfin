using System;

namespace Jellyfin.Data;

/// <summary>
/// A helper class for working with file systems.
/// </summary>
public static class FileSystemHelper
{
    private static readonly char[] _invalidPathCharacters =
    {
        '\"', '<', '>', '|', '\0',
        (char)1, (char)2, (char)3, (char)4, (char)5, (char)6, (char)7, (char)8, (char)9, (char)10,
        (char)11, (char)12, (char)13, (char)14, (char)15, (char)16, (char)17, (char)18, (char)19, (char)20,
        (char)21, (char)22, (char)23, (char)24, (char)25, (char)26, (char)27, (char)28, (char)29, (char)30,
        (char)31, ':', '*', '?', '\\', '/'
    };

    /// <summary>
    /// Takes a filename and removes invalid characters.
    /// </summary>
    /// <param name="filename">The filename.</param>
    /// <returns>System.String.</returns>
    /// <exception cref="ArgumentNullException">The filename is null.</exception>
    public static string GetValidFilename(string filename)
    {
        var first = filename.IndexOfAny(_invalidPathCharacters);
        if (first == -1)
        {
            // Fast path for clean strings
            return filename;
        }

        return string.Create(
            filename.Length,
            (filename, _invalidPathCharacters, first),
            (chars, state) =>
            {
                state.filename.AsSpan().CopyTo(chars);

                chars[state.first++] = ' ';

                var len = chars.Length;
                foreach (var c in state._invalidPathCharacters)
                {
                    for (int i = state.first; i < len; i++)
                    {
                        if (chars[i] == c)
                        {
                            chars[i] = ' ';
                        }
                    }
                }
            });
    }
}
