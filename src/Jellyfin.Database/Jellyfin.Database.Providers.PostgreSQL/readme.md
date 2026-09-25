# PostgreSQL database provider

Requires PostgreSQL 16 or later, a UTF8 database, and a role that owns that database (or at least its schema). Jellyfin must be the only application using the database or schema.

## Configuration

`database.xml` in the configuration directory:

```xml
<DatabaseConfigurationOptions>
  <DatabaseType>Jellyfin-PostgreSQL</DatabaseType>
  <LockingBehavior>SerializedWrites</LockingBehavior>
  <CustomProviderOptions>
    <ConnectionString>Host=db.example.com;Database=jellyfin;Username=jellyfin</ConnectionString>
    <Options>
      <CustomDatabaseOption><Key>password-file</Key><Value>/run/secrets/jellyfin-db</Value></CustomDatabaseOption>
    </Options>
  </CustomProviderOptions>
</DatabaseConfigurationOptions>
```

Any Npgsql connection string keyword can be used. The options below override the connection string; for keywords the connection string does not set, the defaults below apply.

| Option | Default | Notes |
|---|---|---|
| `host`, `port`, `database`, `username` | from the connection string | |
| `password-file` | | Path, relative to the configuration directory if not absolute. One trailing newline is removed. The file is read whenever a connection opens, so the password never enters a connection string and a rotated password takes effect without a restart. It replaces a password in the connection string. A warning is logged if other operating system accounts can read the file or if the password is in the connection string. |
| `ssl-mode` | `Prefer` for sockets, localhost, private addresses and single-label host names; `Require` otherwise | |
| `root-certificate` | | |
| `max-pool-size` / `min-pool-size` | 20 / 0 | |
| `max-auto-prepare` | `0` | How many statements a connection keeps prepared on the server, 0 to 1000; `0`, the default, prepares nothing. Jellyfin's item queries are several kilobytes of SQL that the server otherwise parses and plans again on every execution, and for the queries that read a single item that costs far more than running them: on a 50 000 item library, reading one item takes the server 0.72 ms unprepared and 0.13 ms prepared. The saving comes from reusing the query plan, which is also the risk: a reused plan was made without the parameter values, and a query that matches a name against a pattern — what a search does — is planned badly without the pattern, which measures 17 ms against 26 ms on the same library. Set it for a library that is read far more than it is searched. Every connection keeps its own set, so the server holds up to `max-pool-size` x this many prepared statements, and a connection that keeps them needs to be direct or go through a pooler in session mode. |
| `connection-idle-lifetime` | 300 s | |
| `keepalive` | 30 s | |
| `connect-timeout` / `command-timeout` | 15 s / 60 s | |
| `application-name` | `Jellyfin/<version>` | |
| `include-error-detail` | `false` | Error details can contain row values. |
| `pooling` | `true` | Multiplexing is always turned off. |
| `jit` | `off` | `off` turns JIT compilation off on every connection (Jellyfin's item queries take seconds to compile). `server` keeps the server setting. |
| `hash-memory` | `32` | Megabytes a hash table should be allowed to use, 1 to 65536, or `server`. Item filters probe sets of ids, such as a user's played items; PostgreSQL hashes such a set only when it expects it to fit into `work_mem` x `hash_mem_multiplier`, and otherwise scans it once per item, which turns a 100 ms query into minutes on a large library or with a small `work_mem`. Every connection therefore raises `hash_mem_multiplier` as far as its `work_mem` needs to reach this amount, and never lowers it. PostgreSQL accepts at most 1000 for `hash_mem_multiplier`, so a hash table gets at most 1000 x `work_mem`, e.g. 4000 MB with PostgreSQL's default `work_mem` of 4 MB. For more, raise `work_mem` for the role or the database to at least a thousandth of this amount, e.g. `ALTER ROLE jellyfin SET work_mem = '9MB'` for 8192, and restart Jellyfin; this also lets sorts use more memory. Jellyfin logs a warning on the first connection when hash tables get less than this amount. The memory is only used by sets that large. `server` keeps the server setting. |
| `EnableSensitiveDataLogging` | `false` | |

The session time zone is always UTC.

`LockingBehavior` must be `SerializedWrites` (`NoLock` is treated as `SerializedWrites`). `Optimistic` and `Pessimistic` are refused.

## Startup checks

On the first connection Jellyfin refuses to start when the server is older than PostgreSQL 16, the database is not UTF8, `search_path` names no existing schema, the migration history is in a different schema, the history belongs to the Jellyfin.Pgsql plugin or to a SQLite database, or a database without history already contains other tables, views, sequences, functions or types. It warns about superuser roles, tables owned by another role, too few free connections, unencrypted or unverified connections to remote hosts, other Jellyfin servers using the database, poolers in transaction mode, JIT compilation, and hash tables limited to less memory than `hash-memory` asks for.

The configuration of the Jellyfin.Pgsql plugin (`PLUGIN_PROVIDER` or `Jellyfin-PgSql`) is refused; a database created by the plugin cannot be used.

## Behaviour that matches SQLite

Jellyfin's queries were written against SQLite. The provider makes PostgreSQL behave the same way:

- NULL sorts below every other value.
- `LIKE`, `StartsWith` and `EndsWith` ignore the case of ASCII letters; `Contains` is case-sensitive.
- `Min` and `Max` over ids use the order of their text form.
- NUL characters are removed from text (PostgreSQL cannot store them) and unpaired surrogates become U+FFFD.
- Group representative subqueries are written so PostgreSQL hashes them instead of joining against them.

## Migrations

`Migrations/20200101000000_PostgreSqlBaseline` creates the schema of all SQLite migrations listed in `PostgreSqlBaselineSquashedIds`. Objects the model cannot declare (expression indexes) are added in `PostgreSqlBaselineSql`. Until the first release that ships the provider, the baseline is regenerated instead of adding migrations:

```sh
src/Jellyfin.Database/Jellyfin.Database.Providers.PostgreSQL/tools/regenerate-baseline.sh
```

The script refuses to run once other PostgreSQL migrations exist. After that, follow the twin rule in `../readme.md`.

## Tests

Point the tests at any PostgreSQL 16+ server with a role that may create databases:

```sh
export JELLYFIN_TEST_DB=postgres
export JELLYFIN_TEST_PG="Host=127.0.0.1;Username=jellyfin;Password=...;Database=postgres"
dotnet test tests/Jellyfin.Server.Implementations.Tests --filter "Provider!=Sqlite"
dotnet test tests/Jellyfin.Server.Tests --filter "Provider!=Sqlite"
dotnet test tests/Jellyfin.Server.Integration.Tests
```

Each test class creates and drops its own database. The devcontainer in `.devcontainer/postgresql` sets both variables.
