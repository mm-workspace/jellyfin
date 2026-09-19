using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Providers.PostgreSQL.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Jellyfin.Database.Providers.PostgreSQL;

/// <summary>
/// Checks that a PostgreSQL database can be used by Jellyfin before anything reads or writes it.
/// </summary>
internal sealed class PostgreSqlStartupChecks
{
    /// <summary>
    /// The migrations of the Jellyfin.Pgsql plugin, whose databases differ from the ones Jellyfin creates.
    /// </summary>
    internal static readonly IReadOnlySet<string> PluginMigrationIds = new HashSet<string>(StringComparer.Ordinal)
    {
        "20250618214615_PgSQL_Init",
        "20250929202529_Update_10.11-RC8",
        "20260128200059_10.11.6-1",
        "20260522092303_Jellyfin10.11.11_NormalizedUsername",
        "20260524120336_Jellyfin101111_NormalizedUsernameIndex"
    };

    private const int MaximumListedObjects = 5;

    private const decimal MaximumHashMemMultiplier = 1000;

    private const string HashMemoryShortfall = "Hash tables of the PostgreSQL connection may use {HashMemory} MB (work_mem {WorkMem} x hash_mem_multiplier {HashMemMultiplier}), "
        + "less than the {RequestedHashMemory} MB of the hash-memory database option, so item filters over larger sets of ids can take minutes. ";

    private const string MovingToPostgreSqlHint = "follow the documentation for moving a server from SQLite to PostgreSQL";

    private static readonly IReadOnlySet<string> _squashedSqliteMigrationIds = new HashSet<string>(PostgreSqlBaselineSquashedIds.Ids, StringComparer.Ordinal);

    private readonly NpgsqlConnectionStringBuilder _settings;
    private readonly int? _hashMemoryMegabytes;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgreSqlStartupChecks"/> class.
    /// </summary>
    /// <param name="settings">The connection settings Jellyfin uses.</param>
    /// <param name="hashMemoryMegabytes">The memory the connections try to let a hash table use, or <c>null</c> when that is left to the server.</param>
    /// <param name="logger">The logger for warnings.</param>
    public PostgreSqlStartupChecks(NpgsqlConnectionStringBuilder settings, int? hashMemoryMegabytes, ILogger logger)
    {
        _settings = settings;
        _hashMemoryMegabytes = hashMemoryMegabytes;
        _logger = logger;
    }

    /// <summary>
    /// Gets a value indicating whether Jellyfin supports a PostgreSQL server version.
    /// </summary>
    /// <param name="version">The server version.</param>
    /// <returns><c>true</c> when the version is supported.</returns>
    internal static bool IsSupportedServerVersion(Version version) => version.Major >= PostgreSqlDatabaseProvider.MinimumServerVersion;

    /// <summary>
    /// Gets a value indicating whether an unencrypted or unverified connection deserves a warning.
    /// </summary>
    /// <param name="encrypted">Whether the connection uses TLS.</param>
    /// <param name="sslMode">The configured SSL mode.</param>
    /// <param name="host">The configured host.</param>
    /// <returns>The warning, or <c>null</c>.</returns>
    internal static string? GetTransportWarning(bool encrypted, SslMode sslMode, string? host)
    {
        // Connections that stay on the machine or the private network follow the TLS default for such hosts.
        if (PostgreSqlOptionsReader.IsLocalOrPrivateHost(host))
        {
            return null;
        }

        if (!encrypted)
        {
            return "The connection to PostgreSQL is not encrypted. Set ssl-mode to VerifyFull.";
        }

        return sslMode is SslMode.VerifyCA or SslMode.VerifyFull
            ? null
            : "The certificate of the PostgreSQL server is not verified. Set ssl-mode to VerifyFull and, for a private certificate authority, root-certificate.";
    }

    /// <summary>
    /// Runs the checks on an open connection.
    /// </summary>
    /// <param name="connection">The open connection to the configured database.</param>
    /// <param name="context">The context that opened the connection, if any. Checks that need the model are skipped without it.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    /// <exception cref="InvalidOperationException">Jellyfin cannot use the database.</exception>
    public async Task RunAsync(NpgsqlConnection connection, DbContext? context, CancellationToken cancellationToken)
    {
        if (!IsSupportedServerVersion(connection.PostgreSqlVersion))
        {
            throw new InvalidOperationException(
                $"Jellyfin requires PostgreSQL {PostgreSqlDatabaseProvider.MinimumServerVersion} or later, but the server runs PostgreSQL {connection.ServerVersion}.");
        }

        var facts = await ReadFactsAsync(connection, cancellationToken).ConfigureAwait(false);

        if (!facts.Encoding.Equals("UTF8", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The PostgreSQL database {facts.Database} uses the {facts.Encoding} encoding, but Jellyfin requires UTF8. Create the database with ENCODING 'UTF8' TEMPLATE template0.");
        }

        if (facts.CurrentSchema is null)
        {
            throw new InvalidOperationException(
                $"The search_path of the PostgreSQL role {facts.Role} does not name an existing schema in the database {facts.Database}, so Jellyfin has no schema to use.");
        }

        var historySchemas = await ReadStringsAsync(
            connection,
            "SELECT n.nspname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE c.relname = @name AND c.relkind IN ('r', 'p') ORDER BY n.nspname",
            HistoryRepository.DefaultTableName,
            cancellationToken).ConfigureAwait(false);
        var hasHistoryTable = historySchemas.Contains(facts.CurrentSchema, StringComparer.Ordinal);
        if (!hasHistoryTable && historySchemas.Count > 0)
        {
            throw new InvalidOperationException(
                $"The Jellyfin migration history of the PostgreSQL database {facts.Database} is in the schema {string.Join(", ", historySchemas)}, but the search_path resolves to the schema {facts.CurrentSchema}. "
                + "Set the search_path of the role to the schema Jellyfin was installed in.");
        }

        var migrationIds = hasHistoryTable
            ? await ReadStringsAsync(connection, "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\"", null, cancellationToken).ConfigureAwait(false)
            : [];

        if (migrationIds.FirstOrDefault(PluginMigrationIds.Contains) is { } pluginMigrationId)
        {
            throw new InvalidOperationException(
                $"The PostgreSQL database {facts.Database} was created by the PostgreSQL plugin (migration {pluginMigrationId}), and Jellyfin cannot use it. "
                + $"Move the server back to SQLite with a backup taken while the plugin was in use, then {MovingToPostgreSqlHint}.");
        }

        if (migrationIds.FirstOrDefault(_squashedSqliteMigrationIds.Contains) is { } sqliteMigrationId)
        {
            throw new InvalidOperationException(
                $"The migration history of the PostgreSQL database {facts.Database} contains the SQLite migration {sqliteMigrationId}, so the database was not set up by Jellyfin. "
                + $"Create an empty database and {MovingToPostgreSqlHint}.");
        }

        if (context is not null)
        {
            var modelTables = PostgreSqlModelCatalog.Create(context.GetService<IDesignTimeModel>().Model).Tables.Values
                .Select(t => t.Name)
                .Append(HistoryRepository.DefaultTableName)
                .ToHashSet(StringComparer.Ordinal);

            if (migrationIds.Count == 0)
            {
                await EnsureNoForeignObjectsAsync(connection, facts, modelTables, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await WarnAboutTableOwnershipAsync(connection, facts, modelTables, cancellationToken).ConfigureAwait(false);
            }
        }

        await LogWarningsAsync(connection, facts, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "PostgreSQL {Version}, TLS {Tls}, JIT {Jit}, maximum pool size {MaxPoolSize}, collation {Collation}, database size {DatabaseSize} MB",
            connection.ServerVersion,
            facts.Encrypted ? "on" : "off",
            facts.Jit,
            _settings.MaxPoolSize,
            facts.Collation,
            facts.DatabaseSize / 1024 / 1024);
    }

    private static async Task<DatabaseFacts> ReadFactsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string Sql = """
            SELECT current_database(),
                   current_user,
                   pg_encoding_to_char(d.encoding),
                   d.datcollate::text,
                   current_schema(),
                   r.rolsuper,
                   pg_database_size(d.oid),
                   pg_backend_pid(),
                   COALESCE((SELECT s.ssl FROM pg_stat_ssl s WHERE s.pid = pg_backend_pid()), false),
                   current_setting('jit'),
                   current_setting('max_connections')::int
                     - current_setting('superuser_reserved_connections')::int
                     - COALESCE(current_setting('reserved_connections', true)::int, 0)
                     - (SELECT count(*) FROM pg_stat_activity WHERE backend_type = 'client backend')::int,
                   current_setting('work_mem'),
                   (SELECT s.setting::bigint FROM pg_settings s WHERE s.name = 'work_mem'),
                   current_setting('hash_mem_multiplier')::numeric,
                   quote_ident(current_user)
            FROM pg_database d, pg_roles r
            WHERE d.datname = current_database() AND r.rolname = current_user
            """;

        var command = new NpgsqlCommand(Sql, connection);
        await using (command.ConfigureAwait(false))
        {
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                return new DatabaseFacts(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(3),
                    await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(4),
                    reader.GetBoolean(5),
                    reader.GetInt64(6),
                    reader.GetInt32(7),
                    reader.GetBoolean(8),
                    reader.GetString(9),
                    reader.GetInt32(10),
                    reader.GetString(11),
                    reader.GetInt64(12),
                    reader.GetDecimal(13),
                    reader.GetString(14));
            }
        }
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Only constant statements are passed; values go through parameters.")]
    private static async Task<List<string>> ReadStringsAsync(NpgsqlConnection connection, string sql, string? name, CancellationToken cancellationToken)
    {
        var command = new NpgsqlCommand(sql, connection);
        await using (command.ConfigureAwait(false))
        {
            if (name is not null)
            {
                command.Parameters.AddWithValue("name", name);
            }

            var values = new List<string>();
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    values.Add(reader.GetString(0));
                }
            }

            return values;
        }
    }

    private static async Task EnsureNoForeignObjectsAsync(NpgsqlConnection connection, DatabaseFacts facts, HashSet<string> modelTables, CancellationToken cancellationToken)
    {
        // Objects created by the system for other objects, such as row types, array types, indexes and identity sequences,
        // and objects belonging to extensions do not count.
        const string Sql = """
            WITH schema AS (SELECT oid FROM pg_namespace WHERE nspname = current_schema()),
            objects AS (
                SELECT CASE c.relkind WHEN 'S' THEN 'sequence' WHEN 'v' THEN 'view' WHEN 'm' THEN 'materialized view'
                                      WHEN 'f' THEN 'foreign table' WHEN 'c' THEN 'type' ELSE 'table' END AS kind,
                       c.relname::text AS name, c.oid, 'pg_class'::regclass AS classid
                FROM pg_class c, schema WHERE c.relnamespace = schema.oid AND c.relkind IN ('r', 'p', 'v', 'm', 'f', 'S', 'c')
                UNION ALL
                SELECT 'function', p.proname::text, p.oid, 'pg_proc'::regclass
                FROM pg_proc p, schema WHERE p.pronamespace = schema.oid
                UNION ALL
                SELECT 'type', t.typname::text, t.oid, 'pg_type'::regclass
                FROM pg_type t, schema WHERE t.typnamespace = schema.oid AND t.typrelid = 0)
            SELECT o.kind, o.name FROM objects o
            WHERE NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.classid = o.classid AND d.objid = o.oid AND d.deptype IN ('e', 'x', 'i', 'a'))
            ORDER BY o.kind COLLATE "C", o.name COLLATE "C"
            """;

        var foreignObjects = new List<string>();
        var command = new NpgsqlCommand(Sql, connection);
        await using (command.ConfigureAwait(false))
        {
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var kind = reader.GetString(0);
                    var name = reader.GetString(1);
                    if (kind == "table" && modelTables.Contains(name))
                    {
                        continue;
                    }

                    foreignObjects.Add(string.Create(CultureInfo.InvariantCulture, $"{kind} \"{name}\""));
                }
            }
        }

        if (foreignObjects.Count == 0)
        {
            return;
        }

        var listed = string.Join(", ", foreignObjects.Take(MaximumListedObjects));
        var more = foreignObjects.Count > MaximumListedObjects
            ? string.Create(CultureInfo.InvariantCulture, $" and {foreignObjects.Count - MaximumListedObjects} more")
            : string.Empty;
        throw new InvalidOperationException(
            $"The PostgreSQL database {facts.Database} has no Jellyfin migration history, but its schema {facts.CurrentSchema} already contains objects that are not part of Jellyfin: {listed}{more}. "
            + "Give Jellyfin an empty database or a schema of its own.");
    }

    private async Task WarnAboutTableOwnershipAsync(NpgsqlConnection connection, DatabaseFacts facts, HashSet<string> modelTables, CancellationToken cancellationToken)
    {
        var notOwned = (await ReadStringsAsync(
                connection,
                "SELECT c.relname FROM pg_class c WHERE c.relnamespace = (SELECT oid FROM pg_namespace WHERE nspname = current_schema()) AND c.relkind IN ('r', 'p') AND NOT pg_has_role(c.relowner, 'USAGE') ORDER BY c.relname",
                null,
                cancellationToken).ConfigureAwait(false))
            .Where(modelTables.Contains)
            .ToList();

        if (notOwned.Count > 0)
        {
            _logger.LogWarning(
                "{Count} Jellyfin tables are not owned by the PostgreSQL role {Role}, for example {Tables}. Upgrades that change these tables will fail.",
                notOwned.Count,
                facts.Role,
                string.Join(", ", notOwned.Take(MaximumListedObjects)));
        }
    }

    private async Task LogWarningsAsync(NpgsqlConnection connection, DatabaseFacts facts, CancellationToken cancellationToken)
    {
        if (facts.Jit.Equals("on", StringComparison.Ordinal))
        {
            _logger.LogWarning("JIT compilation is on for the PostgreSQL connection. Large item queries can take seconds to compile; remove the jit database option or run ALTER ROLE {Role} SET jit = off.", facts.QuotedRole);
        }

        WarnAboutHashMemory(facts);

        if (facts.Superuser)
        {
            _logger.LogWarning("The PostgreSQL role {Role} is a superuser. Run Jellyfin with a role that only owns the Jellyfin database.", facts.Role);
        }

        // The pool may still open all its connections but the one already in use.
        if (facts.FreeConnections + 1 < _settings.MaxPoolSize + 5)
        {
            _logger.LogWarning(
                "The PostgreSQL server has {FreeConnections} free connections, but Jellyfin may open up to {MaxPoolSize}. Lower max-pool-size or raise max_connections.",
                facts.FreeConnections,
                _settings.MaxPoolSize);
        }

        if (GetTransportWarning(facts.Encrypted, _settings.SslMode, _settings.Host) is { } transportWarning)
        {
            _logger.LogWarning("{TransportWarning}", transportWarning);
        }

        var otherServers = await ReadStringsAsync(
            connection,
            """
            SELECT DISTINCT host(a.client_addr) FROM pg_stat_activity a
            WHERE a.datname = current_database() AND a.pid <> pg_backend_pid() AND a.usename = current_user
              AND (a.application_name LIKE 'Jellyfin/%' OR (current_setting('application_name') <> '' AND a.application_name = current_setting('application_name')))
              AND a.client_addr IS NOT NULL AND a.client_addr IS DISTINCT FROM inet_client_addr()
            ORDER BY 1
            """,
            null,
            cancellationToken).ConfigureAwait(false);
        if (otherServers.Count > 0)
        {
            _logger.LogWarning(
                "Other Jellyfin servers seem to use the PostgreSQL database {Database} from {Addresses}. Only one Jellyfin server may use a database.",
                facts.Database,
                string.Join(", ", otherServers));
        }

        var command = new NpgsqlCommand("SELECT pg_backend_pid()", connection);
        await using (command.ConfigureAwait(false))
        {
            var backend = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (backend is int pid && pid != facts.BackendPid)
            {
                _logger.LogWarning(
                    "The connection to PostgreSQL seems to go through a pooler in transaction mode. Jellyfin needs a direct connection or a pooler in session mode to move data into the database.");
            }
        }
    }

    private void WarnAboutHashMemory(DatabaseFacts facts)
    {
        var hashMemoryKilobytes = facts.WorkMemKilobytes * facts.HashMemMultiplier;
        if (_hashMemoryMegabytes is not { } hashMemoryMegabytes || hashMemoryKilobytes >= hashMemoryMegabytes * 1024m)
        {
            return;
        }

        var hashMemory = decimal.ToInt64(hashMemoryKilobytes / 1024);
        var hashMemMultiplier = facts.HashMemMultiplier.ToString(CultureInfo.InvariantCulture);

        // Opening the connection raised the multiplier as far as this work_mem needs, up to the maximum,
        // so a multiplier below the maximum means the session lost what was set when it was opened.
        if (facts.HashMemMultiplier < MaximumHashMemMultiplier)
        {
            var wantedHashMemMultiplier = Math.Min(MaximumHashMemMultiplier, Math.Ceiling(hashMemoryMegabytes * 1024m * 1000 / facts.WorkMemKilobytes) / 1000);
            _logger.LogWarning(
                HashMemoryShortfall + "The connection no longer has the hash_mem_multiplier it set when it was opened, as happens when the connection string sets No Reset On Close to false or a pooler in transaction mode is in between. "
                + "Remove No Reset On Close, connect directly or through a pooler in session mode, or run ALTER ROLE {Role} SET hash_mem_multiplier = {WantedHashMemMultiplier}.",
                hashMemory,
                facts.WorkMem,
                hashMemMultiplier,
                hashMemoryMegabytes,
                facts.QuotedRole,
                wantedHashMemMultiplier.ToString(CultureInfo.InvariantCulture));
            return;
        }

        // Beyond 1000 x work_mem only a larger work_mem gives hash tables more memory.
        _logger.LogWarning(
            HashMemoryShortfall + "hash_mem_multiplier cannot exceed 1000, so raise work_mem for the role or the database, e.g. ALTER ROLE {Role} SET work_mem = '{MinimumWorkMem}MB', or lower hash-memory.",
            hashMemory,
            facts.WorkMem,
            hashMemMultiplier,
            hashMemoryMegabytes,
            facts.QuotedRole,
            (hashMemoryMegabytes + 999) / 1000);
    }

    private sealed record DatabaseFacts(
        string Database,
        string Role,
        string Encoding,
        string? Collation,
        string? CurrentSchema,
        bool Superuser,
        long DatabaseSize,
        int BackendPid,
        bool Encrypted,
        string Jit,
        int FreeConnections,
        string WorkMem,
        long WorkMemKilobytes,
        decimal HashMemMultiplier,
        string QuotedRole);
}
