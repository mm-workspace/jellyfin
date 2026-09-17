using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Providers.PostgreSQL;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Jellyfin.Database.Testing.Import;

/// <summary>
/// Loads a SQLite snapshot into a seeded PostgreSQL database the way pgloader's data-only load does, without pgloader.
/// </summary>
/// <remarks>
/// Values go through COPY as the text SQLite returns, with the session time zone at UTC; foreign keys are not checked while
/// loading and identity sequences are left alone. Turning off foreign key triggers needs a superuser.
/// </remarks>
public static class DataOnlyLoader
{
    /// <summary>
    /// Checks whether the role of a connection may turn off foreign key triggers.
    /// </summary>
    /// <param name="target">The open connection.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether the role is a superuser.</returns>
    public static async Task<bool> CanLoadAsync(NpgsqlConnection target, CancellationToken cancellationToken)
    {
        await using var role = new NpgsqlCommand("SELECT rolsuper FROM pg_roles WHERE rolname = current_user", target);
        return (bool)(await role.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>
    /// Replaces the rows of every model table with the rows of a SQLite snapshot.
    /// </summary>
    /// <param name="snapshotPath">The path of the SQLite snapshot.</param>
    /// <param name="target">An open connection to the seeded database.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the load.</returns>
    public static async Task LoadAsync(string snapshotPath, NpgsqlConnection target, CancellationToken cancellationToken)
    {
        IRelationalModel model;
        using (var context = new PostgreSqlDesignTimeJellyfinDbFactory().CreateDbContext([]))
        {
            model = context.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        }

        var tables = model.Tables.Where(t => t.Name != HistoryRepository.DefaultTableName).OrderBy(t => t.Name, StringComparer.Ordinal).ToArray();
        await ExecuteAsync(target, $"SET TIME ZONE 'UTC'; SET session_replication_role = replica; TRUNCATE {string.Join(", ", tables.Select(t => Quote(t.Name)))}", cancellationToken).ConfigureAwait(false);
        await using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = snapshotPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        foreach (var table in tables)
        {
            var tableColumns = table.Columns.ToArray();
            var columns = string.Join(", ", tableColumns.Select(c => Quote(c.Name)));
            await using var read = source.CreateCommand();
#pragma warning disable CA2100 // Identifiers come from the EF model.
            read.CommandText = $"SELECT {columns} FROM {Quote(table.Name)}";
#pragma warning restore CA2100
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using var copy = await target.BeginTextImportAsync($"COPY {Quote(table.Name)} ({columns}) FROM STDIN", cancellationToken).ConfigureAwait(false);
            var line = new StringBuilder();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                line.Clear();
                for (var i = 0; i < tableColumns.Length; i++)
                {
                    if (i > 0)
                    {
                        line.Append('\t');
                    }

                    AppendCopyText(line, tableColumns[i], reader.GetValue(i));
                }

                await copy.WriteAsync(line.Append('\n'), cancellationToken).ConfigureAwait(false);
            }
        }

        await ExecuteAsync(target, "SET session_replication_role = DEFAULT; RESET TIME ZONE", cancellationToken).ConfigureAwait(false);
    }

    private static void AppendCopyText(StringBuilder line, IColumn column, object value)
    {
        switch (value)
        {
            case DBNull:
                line.Append("\\N");
                break;
            case byte[] bytes:
                line.Append("\\\\x").Append(Convert.ToHexStringLower(bytes));
                break;
            case double number:
                line.Append(number.ToString("R", CultureInfo.InvariantCulture));
                break;
            case long number:
                line.Append(number.ToString(CultureInfo.InvariantCulture));
                break;
            case string text:
                // The load file turns the JSON array of keyframe ticks into an array literal the same way.
                var copyText = column.StoreType.EndsWith("[]", StringComparison.Ordinal) ? "{" + text.Trim('[', ']') + "}" : text;
                foreach (var c in copyText)
                {
                    line.Append(c switch
                    {
                        '\\' => "\\\\",
                        '\t' => "\\t",
                        '\n' => "\\n",
                        '\r' => "\\r",
                        _ => c.ToString()
                    });
                }

                break;
            default:
                throw new NotSupportedException($"SQLite returned {value.GetType()} for {column.Name}.");
        }
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
#pragma warning disable CA2100 // Identifiers come from the EF model.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
