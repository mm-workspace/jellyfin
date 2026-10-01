# Database migrations

Jellyfin supports SQLite (the default) and PostgreSQL. Each provider has its own migrations assembly, because migrations contain provider-specific SQL. Every schema change needs a migration for **both** providers.

Run the commands from the repository root. If `dotnet ef` is missing, run `dotnet tool restore`.

## Adding a migration

1. Change the model in `Jellyfin.Database.Implementations`.
2. Add the SQLite migration:

   ```sh
   dotnet ef migrations add MIGRATION_NAME \
     --project src/Jellyfin.Database/Jellyfin.Database.Providers.Sqlite \
     --startup-project src/Jellyfin.Database/Jellyfin.Database.Providers.Sqlite \
     --output-dir Migrations
   ```

3. Add the PostgreSQL migration with the same name:

   ```sh
   dotnet ef migrations add MIGRATION_NAME \
     --project src/Jellyfin.Database/Jellyfin.Database.Providers.PostgreSQL \
     --startup-project src/Jellyfin.Database/Jellyfin.Database.Providers.PostgreSQL \
     --output-dir Migrations
   ```

4. Give the PostgreSQL migration the id of the SQLite migration: rename both generated files and change `[Migration("...")]` in the designer file to the SQLite id. Code migrations and schema migrations share one history table and run in id order, so both providers must apply the change at the same point.
5. A migration that only changes SQLite (for example a SQLite data fix) still needs a PostgreSQL migration with the same id and class name; its `Up` and `Down` stay empty.
6. Run the checks:

   ```sh
   dotnet test tests/Jellyfin.Server.Tests --filter "Category=MigrationGuard"
   dotnet test tests/Jellyfin.Server.Implementations.Tests --filter "FullyQualifiedName~EfMigrationTests"
   ```

   The guard tests fail on a missing or misnamed twin, a code migration id that collides with a schema migration, and identifiers longer than PostgreSQL allows.

## Writing SQL

- Quote identifiers in PostgreSQL SQL (`"BaseItems"`). PostgreSQL truncates names longer than 63 bytes.
- On PostgreSQL every string column is `text COLLATE "C"` (unbounded and compared byte by byte, like SQLite) and `DateTime` values are stored as UTC.
- Code migrations (`Jellyfin.Server/Migrations/Routines`) run on every provider. SQL written for SQLite must be guarded with `context.Database.IsSqlite()`; `RoutineRawSqlGuardTests` checks this.

If you get `System.UnauthorizedAccessException: Access to the path '/src/Jellyfin.Database' is denied.`, restore and run `dotnet ef` with `sudo`.
