using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Server.Implementations.DatabaseImport;
using Jellyfin.Server.Implementations.DatabaseImport.Sqlite;
using Npgsql;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.DatabaseImport.PostgreSql;

/// <summary>
/// Loads a SQLite snapshot into a seeded PostgreSQL database the way pgloader's data-only load does, without pgloader.
/// </summary>
/// <remarks>
/// Values go through COPY as the text SQLite returns, with the session time zone at UTC; foreign keys are not checked while
/// loading and identity sequences are left alone. Turning off foreign key triggers needs a superuser.
/// </remarks>
internal static class TestDataOnlyLoader
{
    public static async Task LoadAsync(string snapshotPath, NpgsqlConnection target, ImportModel model, CancellationToken cancellationToken)
    {
        await using (var role = new NpgsqlCommand("SELECT rolsuper FROM pg_roles WHERE rolname = current_user", target))
        {
            Assert.SkipUnless((bool)(await role.ExecuteScalarAsync(cancellationToken))!, "Loading without foreign key checks needs a superuser.");
        }

        await ExecuteAsync(target, $"SET TIME ZONE 'UTC'; SET session_replication_role = replica; TRUNCATE {string.Join(", ", model.Tables.Select(t => Quote(t.Name)))}", cancellationToken);
        await using var source = await SqliteSourceInspector.OpenReadOnlyAsync(snapshotPath, cancellationToken);
        foreach (var table in model.Tables)
        {
            var columns = string.Join(", ", table.Columns.Select(c => Quote(c.Name)));
            await using var read = source.CreateCommand();
#pragma warning disable CA2100 // Identifiers come from the EF model.
            read.CommandText = $"SELECT {columns} FROM {Quote(table.Name)}";
#pragma warning restore CA2100
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            await using var copy = await target.BeginTextImportAsync($"COPY {Quote(table.Name)} ({columns}) FROM STDIN", cancellationToken);
            var line = new StringBuilder();
            while (await reader.ReadAsync(cancellationToken))
            {
                line.Clear();
                for (var i = 0; i < table.Columns.Count; i++)
                {
                    if (i > 0)
                    {
                        line.Append('\t');
                    }

                    AppendCopyText(line, table.Columns[i], reader.GetValue(i));
                }

                await copy.WriteAsync(line.Append('\n'), cancellationToken);
            }
        }

        await ExecuteAsync(target, "SET session_replication_role = DEFAULT; RESET TIME ZONE", cancellationToken);
    }

    private static void AppendCopyText(StringBuilder line, ImportColumn column, object value)
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
                var copyText = column.IsArray ? "{" + text.Trim('[', ']') + "}" : text;
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
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
