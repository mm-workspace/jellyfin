using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Server.Implementations.DatabaseImport;

namespace Jellyfin.Server.DatabaseImport;

/// <summary>
/// The progress of a PostgreSQL import, kept in the data directory so every start of the server sees it.
/// </summary>
/// <param name="FormatVersion">The version of this format.</param>
/// <param name="Step">The last completed step.</param>
/// <param name="ImportDirectory">The directory holding the snapshot, manifest, load file and reports.</param>
/// <param name="SqliteDatabasePath">The path of the live SQLite database.</param>
/// <param name="SnapshotSha256">The SHA-256 of the snapshot.</param>
/// <param name="ServerVersion">The version of the server that ran the steps.</param>
/// <param name="DatabaseOid">The oid of the seeded PostgreSQL database.</param>
/// <param name="UpdatedUtc">When the state was written.</param>
internal sealed record ImportState(
    int FormatVersion,
    ImportStage Step,
    string ImportDirectory,
    string SqliteDatabasePath,
    string SnapshotSha256,
    string ServerVersion,
    long? DatabaseOid,
    DateTime UpdatedUtc)
{
    /// <summary>
    /// The format version this server reads and writes.
    /// </summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>
    /// The name of the state file in the data directory.
    /// </summary>
    public const string FileName = "postgresql-import.json";

    /// <summary>
    /// Gets the path of the state file.
    /// </summary>
    /// <param name="dataPath">The data directory.</param>
    /// <returns>The path.</returns>
    public static string GetPath(string dataPath) => Path.Combine(dataPath, FileName);

    /// <summary>
    /// Reads the state.
    /// </summary>
    /// <param name="dataPath">The data directory.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The state, or <c>null</c> if no import is in progress.</returns>
    /// <exception cref="InvalidDataException">The state file exists but cannot be read; it is never ignored.</exception>
    public static async Task<ImportState?> ReadAsync(string dataPath, CancellationToken cancellationToken)
    {
        var path = GetPath(dataPath);
        if (!File.Exists(path))
        {
            return null;
        }

        var stream = File.OpenRead(path);
        await using (stream.ConfigureAwait(false))
        {
            ImportState state;
            try
            {
                state = await ImportJson.ReadAsync<ImportState>(stream, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException ex)
            {
                throw new InvalidDataException($"The PostgreSQL import state '{path}' is damaged. Restore it, or remove it after checking which database the server should use.", ex);
            }

            if (state.FormatVersion != CurrentFormatVersion || state.ImportDirectory is null || state.SqliteDatabasePath is null || state.SnapshotSha256 is null || state.ServerVersion is null)
            {
                throw new InvalidDataException($"The PostgreSQL import state '{path}' was written by another server version or is incomplete.");
            }

            return state;
        }
    }

    /// <summary>
    /// Writes the state, replacing the previous one only once the new one is complete.
    /// </summary>
    /// <param name="dataPath">The data directory.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the write.</returns>
    public async Task WriteAsync(string dataPath, CancellationToken cancellationToken)
    {
        var path = GetPath(dataPath);
        var temporaryPath = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            // The state names the database files; only the server's user needs to read it.
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        var stream = new FileStream(temporaryPath, options);
        await using (stream.ConfigureAwait(false))
        {
            await ImportJson.WriteAsync(stream, this, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849 // Only the synchronous overload writes through to the disk.
            stream.Flush(true);
#pragma warning restore CA1849
        }

        File.Move(temporaryPath, path, true);
    }

    /// <summary>
    /// Removes the state.
    /// </summary>
    /// <param name="dataPath">The data directory.</param>
    public static void Delete(string dataPath) => File.Delete(GetPath(dataPath));
}
