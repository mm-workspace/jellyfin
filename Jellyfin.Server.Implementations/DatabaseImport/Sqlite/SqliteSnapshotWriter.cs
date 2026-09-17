using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Server.Implementations.DatabaseImport.Sqlite;

/// <summary>
/// Copies a live SQLite database into a single file that pgloader can read.
/// </summary>
internal static class SqliteSnapshotWriter
{
    /// <summary>
    /// The suffixes of the files that make up a SQLite database in WAL mode.
    /// </summary>
    public static readonly string[] DatabaseFileSuffixes = [string.Empty, "-wal", "-shm"];

    /// <summary>
    /// Copies the database, including changes still in its write-ahead log, and checks the copy.
    /// </summary>
    /// <param name="livePath">The path of the live database. The server must not be running.</param>
    /// <param name="snapshotPath">The path of the copy, which must not exist.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The snapshot.</returns>
    /// <exception cref="IOException">The copy exists already.</exception>
    /// <exception cref="InvalidDataException">The copy fails SQLite's integrity check.</exception>
    public static async Task<SqliteSnapshot> WriteAsync(string livePath, string snapshotPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(livePath))
        {
            throw new FileNotFoundException("The SQLite database does not exist.", livePath);
        }

        if (File.Exists(snapshotPath))
        {
            throw new IOException($"The snapshot '{snapshotPath}' exists already.");
        }

        try
        {
            // Not read-only: a database in WAL mode cannot be opened read-only once its -shm file is gone.
            var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = livePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            await using (source.ConfigureAwait(false))
            {
                var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = snapshotPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
                await using (destination.ConfigureAwait(false))
                {
                    await source.OpenAsync(cancellationToken).ConfigureAwait(false);
                    await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
                    source.BackupDatabase(destination);

                    await ExecuteAsync(destination, "PRAGMA journal_mode=DELETE", cancellationToken).ConfigureAwait(false);
                    var check = await ExecuteAsync(destination, "PRAGMA quick_check", cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(check, "ok", StringComparison.Ordinal))
                    {
                        throw new InvalidDataException($"The copy of the SQLite database is damaged: {check}");
                    }
                }
            }
        }
        catch
        {
            File.Delete(snapshotPath);
            throw;
        }

        return new SqliteSnapshot(snapshotPath, await HashAsync(snapshotPath, cancellationToken).ConfigureAwait(false), ReadSourceFiles(livePath));
    }

    /// <summary>
    /// Gets the size and modification time of the files of a live database.
    /// </summary>
    /// <param name="livePath">The path of the database.</param>
    /// <returns>The files, including the ones that do not exist.</returns>
    public static ImportSourceFile[] ReadSourceFiles(string livePath)
    {
        return Array.ConvertAll(DatabaseFileSuffixes, suffix =>
        {
            var file = new FileInfo(livePath + suffix);
            return file.Exists
                ? new ImportSourceFile(file.Name, file.Length, file.LastWriteTimeUtc)
                : new ImportSourceFile(file.Name, null, null);
        });
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        var stream = File.OpenRead(path);
        await using (stream.ConfigureAwait(false))
        {
            return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }
    }

    private static async Task<string?> ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
#pragma warning disable CA2100 // The statements are constants of this class.
            command.CommandText = sql;
#pragma warning restore CA2100
            return (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
