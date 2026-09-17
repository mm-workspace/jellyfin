using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Extensions.Json;

namespace Jellyfin.Server.Implementations.DatabaseImport;

/// <summary>
/// Reads and writes the import manifest and report files.
/// </summary>
internal static class ImportJson
{
    private static readonly JsonSerializerOptions _options = new(JsonDefaults.Options)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    /// <summary>
    /// Writes an import file.
    /// </summary>
    /// <typeparam name="T">The type of the file content.</typeparam>
    /// <param name="stream">The stream to write to.</param>
    /// <param name="value">The file content.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the write.</returns>
    public static Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
        => JsonSerializer.SerializeAsync(stream, value, _options, cancellationToken);

    /// <summary>
    /// Reads an import file.
    /// </summary>
    /// <typeparam name="T">The type of the file content.</typeparam>
    /// <param name="stream">The stream to read from.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The file content.</returns>
    /// <exception cref="InvalidDataException">The file is not valid.</exception>
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(stream, _options, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException($"The {typeof(T).Name} file is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The {typeof(T).Name} file is not valid.", ex);
        }
    }

    /// <summary>
    /// Reads a manifest written by this server version's format.
    /// </summary>
    /// <param name="stream">The stream to read from.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The manifest.</returns>
    /// <exception cref="InvalidDataException">The manifest is not valid or has another format version.</exception>
    public static async Task<ImportManifest> ReadManifestAsync(Stream stream, CancellationToken cancellationToken)
    {
        ImportManifest? manifest;
        try
        {
            manifest = await JsonSerializer.DeserializeAsync<ImportManifest>(stream, _options, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The import manifest is not valid.", ex);
        }

        if (manifest is null
            || manifest.ServerVersion is null
            || manifest.ModelFingerprint is null
            || manifest.SnapshotSha256 is null
            || manifest.SourceFiles is null
            || manifest.Tables is null
            || manifest.Warnings is null)
        {
            throw new InvalidDataException("The import manifest is not valid.");
        }

        if (manifest.FormatVersion != ImportManifest.CurrentFormatVersion)
        {
            throw new InvalidDataException(
                $"The import manifest has format version {manifest.FormatVersion}, but this server reads version {ImportManifest.CurrentFormatVersion}. Run the preflight again with this server.");
        }

        return manifest;
    }
}
