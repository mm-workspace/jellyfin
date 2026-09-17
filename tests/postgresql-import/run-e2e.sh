#!/usr/bin/env bash
# Imports a synthetic SQLite library into PostgreSQL with the built server and the pinned pgloader image:
# export, preflight, seed, pgloader, finalize. Exits non-zero at the first step that does not end as expected.
#
# Environment (defaults suit the jfpg compose project of a development machine):
#   SIZE                S, S-edge or L (S), for a synthetic source
#   SOURCE_DIR          a data directory of an official image (its /config and /cache) to import instead; it is
#                       copied and upgraded with --mode MigrateSystem first
#   CONFIGURATION       build configuration of the server and tests (Debug)
#   PG_HOST, PG_PORT    PostgreSQL as the server reaches it (127.0.0.1, 55416)
#   PG_USER, PG_PASSWORD
#   PSQL                command reading SQL on stdin as a role that may create databases
#   PG_TOOLS            command prefix running pg_dump/pg_restore against the server (docker exec -i jfpg-pg16-1)
#   PGLOADER_NETWORK    Docker network pgloader joins (jfpg_default)
#   PGLOADER_PG_HOST, PGLOADER_PG_PORT   PostgreSQL as pgloader reaches it (pg16, 5432)
#   WORK                work directory (a new temporary directory)
#   KEEP                1 keeps the database and the work directory
#   PARITY              1 starts the server on SQLite before and on PostgreSQL after the import and compares API
#                       responses as PARITY_USER / PARITY_PASSWORD (admin / golden) at SERVER_URL (http://127.0.0.1:8096)
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SIZE="${SIZE:-S}"
CONFIGURATION="${CONFIGURATION:-Debug}"
PG_HOST="${PG_HOST:-127.0.0.1}"
PG_PORT="${PG_PORT:-55416}"
PG_USER="${PG_USER:-jfpg}"
PG_PASSWORD="${PG_PASSWORD:-jfpg}"
PSQL="${PSQL:-docker exec -i jfpg-pg16-1 psql -v ON_ERROR_STOP=1 -X -q -U jfpg -d postgres}"
PG_TOOLS="${PG_TOOLS:-docker exec -i jfpg-pg16-1}"
PGLOADER_NETWORK="${PGLOADER_NETWORK:-jfpg_default}"
PGLOADER_PG_HOST="${PGLOADER_PG_HOST:-pg16}"
PGLOADER_PG_PORT="${PGLOADER_PG_PORT:-5432}"
WORK="${WORK:-$(mktemp -d "${TMPDIR:-/tmp}/jellyfin-import-e2e.XXXXXX")}"
IMAGE="$(tr -d '[:space:]' < "$ROOT/tests/postgresql-import/pgloader.image")"
DATABASE="jfimport_$(date +%s)_$$"
SERVER="$ROOT/Jellyfin.Server/bin/$CONFIGURATION/net10.0/jellyfin.dll"

log() { printf '== %s\n' "$*"; }

cleanup() {
  if [ -n "${SERVER_PID:-}" ] && kill -0 "$SERVER_PID" 2>/dev/null; then
    kill "$SERVER_PID"
    wait "$SERVER_PID" 2>/dev/null || true
  fi
  if [ "${KEEP:-0}" != "1" ]; then
    echo "DROP DATABASE IF EXISTS \"$DATABASE\" WITH (FORCE); DROP DATABASE IF EXISTS \"${DATABASE}_restored\" WITH (FORCE);" | $PSQL || true
    rm -rf "$WORK"
  else
    log "kept database $DATABASE and $WORK"
  fi
}
trap cleanup EXIT

start_server() {
  local name="$1"
  dotnet "$SERVER" --datadir "$WORK" --configdir "$WORK/config" --cachedir "$WORK/cache" --logdir "$WORK/log" --nowebclient > "$WORK/server-$name.log" 2>&1 &
  SERVER_PID=$!
  local started=$SECONDS
  # The setup server answers first with camelCase JSON; the server itself answers in PascalCase.
  until curl -sf -m 2 "${SERVER_URL:-http://127.0.0.1:8096}/System/Info/Public" 2>/dev/null | grep -q '"StartupWizardCompleted":true'; do
    if ! kill -0 "$SERVER_PID" 2>/dev/null || [ $((SECONDS - started)) -gt 180 ]; then
      tail -n 40 "$WORK/server-$name.log"
      exit 1
    fi
    sleep 1
  done
  log "server on $name ready in $((SECONDS - started)) s"
}

stop_server() {
  kill "$SERVER_PID"
  wait "$SERVER_PID" || true
  SERVER_PID=
  # Error lines without time and thread, so the two runs can be compared.
  grep -E '\[(ERR|FTL)\]' "$WORK/server-$1.log" | sed -E 's/^\[[^]]*\] \[(ERR|FTL)\] \[[0-9]+\] /\1 /' | sort -u > "$WORK/errors-$1.txt" || true
}

history_rows() {
  echo "SELECT count(*) FROM \"__EFMigrationsHistory\";" | $PSQL_TARGET -At
}

run_step() {
  local mode="$1" expected="$2"
  local started=$SECONDS
  set +e
  dotnet "$SERVER" --datadir "$WORK" --configdir "$WORK/config" --cachedir "$WORK/cache" --logdir "$WORK/log" --nowebclient --mode "$mode" > "$WORK/$mode.log" 2>&1
  local code=$?
  set -e
  log "$mode exited $code in $((SECONDS - started)) s"
  if [ "$code" != "$expected" ]; then
    tail -n 40 "$WORK/$mode.log"
    exit 1
  fi
}

mkdir -p "$WORK"
log "work directory $WORK, source ${SOURCE_DIR:-$SIZE}, image $IMAGE"
dotnet build "$ROOT/Jellyfin.Server" -c "$CONFIGURATION" --nologo -v q
dotnet build "$ROOT/tests/Jellyfin.Server.Tests" -c "$CONFIGURATION" --nologo -v q

if [ -n "${SOURCE_DIR:-}" ]; then
  log "copy $SOURCE_DIR"
  cp -R "$SOURCE_DIR/config/." "$WORK/"
  mkdir -p "$WORK/cache" && cp -R "$SOURCE_DIR/cache/." "$WORK/cache/"
  run_step MigrateSystem 0
else
  log "export"
  JELLYFIN_TEST_DB= JELLYFIN_TEST_PG= JELLYFIN_SYNTHETIC_OUT="$WORK" JELLYFIN_SYNTHETIC_SIZE="$SIZE" \
    dotnet test "$ROOT/tests/Jellyfin.Server.Tests" -c "$CONFIGURATION" --no-build --filter "FullyQualifiedName~ImportSourceExport" > "$WORK/export.log"
fi

if [ "${PARITY:-0}" = "1" ]; then
  start_server sqlite
  python3 "$ROOT/tests/postgresql-import/api-snapshot.py" record "${SERVER_URL:-http://127.0.0.1:8096}" "$WORK/api-sqlite" "${PARITY_USER:-admin}" "${PARITY_PASSWORD:-golden}"
  stop_server sqlite
fi

run_step PostgreSqlImportPreflight 0

log "create $DATABASE"
echo "CREATE DATABASE \"$DATABASE\" TEMPLATE template0 ENCODING 'UTF8';" | $PSQL
cat > "$WORK/config/database.xml" <<XML
<?xml version="1.0" encoding="utf-8"?>
<DatabaseConfigurationOptions xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <DatabaseType>Jellyfin-PostgreSQL</DatabaseType>
  <LockingBehavior>NoLock</LockingBehavior>
  <CustomProviderOptions>
    <PluginName />
    <PluginAssembly />
    <ConnectionString>Host=$PG_HOST;Port=$PG_PORT;Username=$PG_USER;Password=$PG_PASSWORD;Database=$DATABASE;SSL Mode=Disable</ConnectionString>
  </CustomProviderOptions>
</DatabaseConfigurationOptions>
XML

run_step PostgreSqlImportSeed 0

log "pgloader"
mkdir -p "$WORK/data/postgresql-import/pgloader"
started=$SECONDS
set +e
docker run --rm --platform linux/amd64 --network "$PGLOADER_NETWORK" --name "jfpg-pgloader-$(date +%s)-$$" \
  -e PGHOST="$PGLOADER_PG_HOST" -e PGPORT="$PGLOADER_PG_PORT" -e PGUSER="$PG_USER" -e PGPASSWORD="$PG_PASSWORD" -e PGDATABASE="$DATABASE" -e PGSSLMODE=disable \
  -v "$WORK/data/postgresql-import:/import" "$IMAGE" \
  pgloader --root-dir /import/pgloader --logfile /import/pgloader/pgloader.log --summary /import/pgloader/summary.txt /import/jellyfin.load \
  > "$WORK/pgloader.log" 2>&1
code=$?
set -e
# pgloader's exit code does not tell whether the data arrived; finalize does.
log "pgloader exited $code in $((SECONDS - started)) s"
grep -E "ERROR|FATAL|Total import time" "$WORK/pgloader.log" | cut -c1-300 | head -20 || true

run_step PostgreSqlImportFinalize 0
cat "$WORK/data/postgresql-import/finalize-report.txt"
test -f "$WORK/data/jellyfin.db.imported-to-postgresql"
test ! -f "$WORK/data/postgresql-import.json"
if [ "${PARITY:-0}" = "1" ]; then
  PSQL_TARGET="${PSQL/-d postgres/-d $DATABASE}"
  rows_before="$(history_rows)"
  start_server postgresql
  python3 "$ROOT/tests/postgresql-import/api-snapshot.py" record "${SERVER_URL:-http://127.0.0.1:8096}" "$WORK/api-postgresql" "${PARITY_USER:-admin}" "${PARITY_PASSWORD:-golden}"
  stop_server postgresql

  log "pg_dump and pg_restore into ${DATABASE}_restored"
  $PG_TOOLS pg_dump -Fc -U "$PG_USER" -d "$DATABASE" > "$WORK/import.dump"
  echo "CREATE DATABASE \"${DATABASE}_restored\" TEMPLATE template0 ENCODING 'UTF8';" | $PSQL
  $PG_TOOLS pg_restore --exit-on-error --no-owner -U "$PG_USER" -d "${DATABASE}_restored" < "$WORK/import.dump"
  sed -i.bak "s/Database=$DATABASE;/Database=${DATABASE}_restored;/" "$WORK/config/database.xml"
  start_server restored
  python3 "$ROOT/tests/postgresql-import/api-snapshot.py" record "${SERVER_URL:-http://127.0.0.1:8096}" "$WORK/api-restored" "${PARITY_USER:-admin}" "${PARITY_PASSWORD:-golden}"
  python3 "$ROOT/tests/postgresql-import/api-snapshot.py" probe "${SERVER_URL:-http://127.0.0.1:8096}" "${PARITY_USER:-admin}" "${PARITY_PASSWORD:-golden}"
  stop_server restored
  mv "$WORK/config/database.xml.bak" "$WORK/config/database.xml"
  python3 "$ROOT/tests/postgresql-import/api-snapshot.py" compare "$WORK/api-postgresql" "$WORK/api-restored"
  if comm -13 "$WORK/errors-sqlite.txt" "$WORK/errors-restored.txt" | grep .; then
    log "the server logged errors on the restored database or during the probes that it did not log on SQLite"
    exit 1
  fi

  rows_after="$(history_rows)"
  log "migration history rows: $rows_before before the start on PostgreSQL, $rows_after after"
  [ "$rows_before" = "$rows_after" ]
  python3 "$ROOT/tests/postgresql-import/api-snapshot.py" compare "$WORK/api-sqlite" "$WORK/api-postgresql"
  # Errors the source already had on SQLite are not caused by the import.
  if comm -13 "$WORK/errors-sqlite.txt" "$WORK/errors-postgresql.txt" | grep .; then
    log "the server logged errors on PostgreSQL that it did not log on SQLite"
    exit 1
  fi
fi

log "import of ${SOURCE_DIR:-$SIZE} completed"
