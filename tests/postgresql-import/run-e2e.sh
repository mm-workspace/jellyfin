#!/usr/bin/env bash
# Imports a synthetic SQLite library into PostgreSQL with the built server and the pinned pgloader image:
# export, preflight, seed, pgloader, finalize. Exits non-zero at the first step that does not end as expected.
#
# Environment (defaults suit the jfpg compose project of a development machine):
#   SIZE                S, S-edge or L (S)
#   CONFIGURATION       build configuration of the server and tests (Debug)
#   PG_HOST, PG_PORT    PostgreSQL as the server reaches it (127.0.0.1, 55416)
#   PG_USER, PG_PASSWORD
#   PSQL                command reading SQL on stdin as a role that may create databases
#   PGLOADER_NETWORK    Docker network pgloader joins (jfpg_default)
#   PGLOADER_PG_HOST, PGLOADER_PG_PORT   PostgreSQL as pgloader reaches it (pg16, 5432)
#   WORK                work directory (a new temporary directory)
#   KEEP                1 keeps the database and the work directory
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SIZE="${SIZE:-S}"
CONFIGURATION="${CONFIGURATION:-Debug}"
PG_HOST="${PG_HOST:-127.0.0.1}"
PG_PORT="${PG_PORT:-55416}"
PG_USER="${PG_USER:-jfpg}"
PG_PASSWORD="${PG_PASSWORD:-jfpg}"
PSQL="${PSQL:-docker exec -i jfpg-pg16-1 psql -v ON_ERROR_STOP=1 -X -q -U jfpg -d postgres}"
PGLOADER_NETWORK="${PGLOADER_NETWORK:-jfpg_default}"
PGLOADER_PG_HOST="${PGLOADER_PG_HOST:-pg16}"
PGLOADER_PG_PORT="${PGLOADER_PG_PORT:-5432}"
WORK="${WORK:-$(mktemp -d "${TMPDIR:-/tmp}/jellyfin-import-e2e.XXXXXX")}"
IMAGE="$(tr -d '[:space:]' < "$ROOT/tests/postgresql-import/pgloader.image")"
DATABASE="jfimport_$(date +%s)_$$"
SERVER="$ROOT/Jellyfin.Server/bin/$CONFIGURATION/net10.0/jellyfin.dll"

log() { printf '== %s\n' "$*"; }

cleanup() {
  if [ "${KEEP:-0}" != "1" ]; then
    echo "DROP DATABASE IF EXISTS \"$DATABASE\" WITH (FORCE);" | $PSQL || true
    rm -rf "$WORK"
  else
    log "kept database $DATABASE and $WORK"
  fi
}
trap cleanup EXIT

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
log "work directory $WORK, source $SIZE, image $IMAGE"
dotnet build "$ROOT/Jellyfin.Server" -c "$CONFIGURATION" --nologo -v q
dotnet build "$ROOT/tests/Jellyfin.Server.Tests" -c "$CONFIGURATION" --nologo -v q

log "export"
JELLYFIN_TEST_DB= JELLYFIN_TEST_PG= JELLYFIN_SYNTHETIC_OUT="$WORK" JELLYFIN_SYNTHETIC_SIZE="$SIZE" \
  dotnet test "$ROOT/tests/Jellyfin.Server.Tests" -c "$CONFIGURATION" --no-build --filter "FullyQualifiedName~ImportSourceExport" > "$WORK/export.log"

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
log "import of $SIZE completed"
