#!/usr/bin/env bash
# Runs the real-world QA sweep: every schema through rw.sh (init path and written path) on throwaway PostgreSQL containers,
# then a second publish, `diff --exit-code` and `document` on each init project. See README.md. Run it once at a time:
# runs share tools/diffqa/work/realworld/rw-out and would delete each other's output.
# Needs Docker, a built CLI (dotnet build src/PgProj.Cli), Git Bash on Windows, and the files from fetch.sh.
# Ports 55490-55491 (never 55432, the user's own server). Set KEEP=1 to leave the containers running.
set -u
export MSYS_NO_PATHCONV=1
pw() { (cd "$1" && { pwd -W 2>/dev/null || pwd; }); }  # a Windows path under Git Bash, which docker, python, and the CLI need
here=$(pw "$(dirname "$0")")
data=${RW_DATA:-$here/../work/realworld/rw-data}
repo=$(pw "$here/../../..")
bin="$repo/src/PgProj.Cli/bin/Debug/net10.0"; pgproj="$bin/pgproj"; [ -x "$pgproj" ] || pgproj="$bin/pgproj.exe"
bash "$here/fetch.sh" || { echo "fetch failed" >&2; exit 1; }
rm -rf "$here/../work/realworld/rw-out"
docker rm -f rwqa90 rwqa91 >/dev/null 2>&1
docker run -d --name rwqa90 -e POSTGRES_PASSWORD=qa -p 55490:5432 pgvector/pgvector:pg18 >/dev/null
docker run -d --name rwqa91 -e POSTGRES_PASSWORD=qa -p 55491:5432 postgis/postgis:17-3.5 >/dev/null
for c in rwqa90 rwqa91; do until docker exec $c pg_isready -U postgres >/dev/null 2>&1; do sleep 2; done; done; sleep 5
cs() { echo "Host=localhost;Port=$2;Username=postgres;Password=qa;Database=$1;Timeout=60;Command Timeout=0"; }
# name file container port (the variants load; see fetch.sh)
for spec in "northwind northwind.sql rwqa90 55490" "chinook chinook_fixed.sql rwqa90 55490" "temporal temporal.sql rwqa90 55490" \
            "pagila pagila.sql rwqa90 55490" "synapse synapse.sql rwqa90 55490" "supabase supabase2.sql rwqa90 55490" \
            "plausible plausible.sql rwqa90 55490" "musicbrainz musicbrainz2.sql rwqa90 55490" "adventureworks adventureworks.sql rwqa90 55490" \
            "osm osm.sql rwqa91 55491"; do
  set -- $spec
  echo "=== $1"; bash "$here/rw.sh" "$1" "$data/$2" "$3" "$4" written 2>&1
  pj="$here/../work/realworld/rw-out/$1/init/pgproj.json"; c=$(cs rw_$1_dst $4)
  echo "second publish statements: $("$pgproj" publish --project "$pj" --target-connection "$c" --dry-run --allow-destructive --allow-superuser-statements 2>&1 | grep -cE '^(ALTER|CREATE|DROP|COMMENT|GRANT|REVOKE) ')"
  "$pgproj" diff --project "$pj" --target-connection "$c" --exit-code >/dev/null 2>&1; echo "diff --exit-code: $?"
  echo "document errors: $("$pgproj" document --project "$pj" --output "$here/../work/realworld/rw-out/$1/docs" --format markdown,json 2>&1 | grep -ci '\[error\]')"
done
# GitLab is refused by the default lock table (G-280, about 12,000 locks against 6,400); it needs a server with more.
docker rm -f rwqa92 >/dev/null 2>&1
docker run -d --name rwqa92 -e POSTGRES_PASSWORD=qa -p 55492:5432 pgvector/pgvector:pg18 -c max_locks_per_transaction=256 >/dev/null
until docker exec rwqa92 pg_isready -U postgres >/dev/null 2>&1; do sleep 2; done; sleep 5
echo "=== gitlab (max_locks_per_transaction=256; the init path only)"; bash "$here/rw.sh" gitlab "$data/gitlab.sql" rwqa92 55492 2>&1
# The Synapse upgrade: a v54 database brought to v72 with --concurrently and --lock-timeout.
echo "=== synapse upgrade v54 -> v72"
docker exec rwqa90 psql -U postgres -q -c "DROP DATABASE IF EXISTS syn_up WITH (FORCE)" -c "CREATE DATABASE syn_up" >/dev/null 2>&1
docker cp "$data/synapse54.sql" rwqa90:/tmp/s54.sql >/dev/null; docker exec rwqa90 psql -U postgres -q -d syn_up -f /tmp/s54.sql >/dev/null 2>&1
pj="$here/../work/realworld/rw-out/synapse/init/pgproj.json"; c=$(cs syn_up 55490)
"$pgproj" publish --project "$pj" --target-connection "$c" --execute --concurrently --lock-timeout 5s --allow-destructive 2>&1 | tail -2
"$pgproj" diff --project "$pj" --target-connection "$c" --exit-code 2>&1 | grep -E '^(Added|Removed|Modified):' | tr '\n' ' '; echo "exit ${PIPESTATUS[0]}"
[ "${KEEP:-}" = 1 ] || docker rm -f rwqa90 rwqa91 rwqa92 >/dev/null 2>&1
echo ALLDONE
