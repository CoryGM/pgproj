#!/usr/bin/env bash
# Runs the real-world QA sweep: every schema through rw.sh (init path, kept script, written path) on throwaway PostgreSQL containers,
# then a second publish, `diff --exit-code` and `document` on each init project, GitLab, and the Synapse upgrade. See README.md. Run it once
# at a time: runs share tools/diffqa/work/realworld/rw-out and would delete each other's output.
# Needs Docker, the .NET SDK, Python 3, Git Bash on Windows, and the files from fetch.sh.
# No server publishes a host port: they and pgproj (a self-contained linux-x64 build, run in a container) share the Docker network $RW_NETWORK
# (default rwqa), which is removed at the end with the containers. Set KEEP=1 to leave them.
set -u
export MSYS_NO_PATHCONV=1
pw() { (cd "$1" && { pwd -W 2>/dev/null || pwd; }); }  # a Windows path under Git Bash, which docker, python, and dotnet need
here=$(pw "$(dirname "$0")")
data=${RW_DATA:-$here/../work/realworld/rw-data}
repo=$(pw "$here/../../..")
mkdir -p "$here/../work/realworld/rw-tools"
work=$(pw "$here/../work/realworld")
export RW_NETWORK=${RW_NETWORK:-rwqa}
net=$RW_NETWORK
image=${RW_PGPROJ_IMAGE:-mcr.microsoft.com/dotnet/runtime-deps:10.0}
bash "$here/fetch.sh" || { echo "fetch failed" >&2; exit 1; }

# The Linux build the sweep runs, from this checkout.
rm -rf "$work/rw-tools/pgproj-linux"
dotnet publish "$repo/src/PgProj.Cli" -c Release -r linux-x64 --self-contained -o "$work/rw-tools/pgproj-linux" > "$work/rw-tools/publish.log" 2>&1 \
  || { echo "the Linux build failed: $work/rw-tools/publish.log" >&2; exit 1; }
pgproj() { docker run --rm --network "$net" -v "$work:/work" -w /work "$image" /work/rw-tools/pgproj-linux/pgproj "$@"; }

rm -rf "$work/rw-out"
docker rm -f rwqa90 rwqa91 rwqa92 >/dev/null 2>&1
docker network rm "$net" >/dev/null 2>&1
docker network create "$net" >/dev/null
echo "pgproj: $(pgproj --version 2>&1 | head -1)"
docker run -d --name rwqa90 --network "$net" -e POSTGRES_PASSWORD=qa pgvector/pgvector:pg18 >/dev/null
docker run -d --name rwqa91 --network "$net" -e POSTGRES_PASSWORD=qa postgis/postgis:17-3.5 >/dev/null
for c in rwqa90 rwqa91; do until docker exec $c pg_isready -U postgres >/dev/null 2>&1; do sleep 2; done; done; sleep 5
cs() { echo "Host=$2;Port=5432;Username=postgres;Password=qa;Database=$1;Timeout=60;Command Timeout=0"; }
# name file container (the variants load; see fetch.sh)
for spec in "northwind northwind.sql rwqa90" "chinook chinook_fixed.sql rwqa90" "temporal temporal.sql rwqa90" \
            "pagila pagila.sql rwqa90" "synapse synapse.sql rwqa90" "supabase supabase2.sql rwqa90" \
            "plausible plausible.sql rwqa90" "musicbrainz musicbrainz2.sql rwqa90" "adventureworks adventureworks.sql rwqa90" \
            "osm osm.sql rwqa91"; do
  set -- $spec
  echo "=== $1"; bash "$here/rw.sh" "$1" "$data/$2" "$3" written 2>&1
  pj="/work/rw-out/$1/init/pgproj.json"; c=$(cs rw_$1_dst $3)
  echo "second publish statements: $(pgproj publish --project "$pj" --target-connection "$c" --dry-run --allow-destructive --allow-superuser-statements 2>&1 | grep -cE '^(ALTER|CREATE|DROP|COMMENT|GRANT|REVOKE) ')"
  pgproj diff --project "$pj" --target-connection "$c" --exit-code >/dev/null 2>&1; echo "diff --exit-code: $?"
  echo "document errors: $(pgproj document --project "$pj" --output "/work/rw-out/$1/docs" --format markdown,json 2>&1 | grep -ci '\[error\]')"
done
# GitLab is refused by the default lock table (G-280, about 12,000 locks against 6,400); it needs a server with more.
docker run -d --name rwqa92 --network "$net" -e POSTGRES_PASSWORD=qa pgvector/pgvector:pg18 -c max_locks_per_transaction=256 >/dev/null
until docker exec rwqa92 pg_isready -U postgres >/dev/null 2>&1; do sleep 2; done; sleep 5
echo "=== gitlab (max_locks_per_transaction=256; the init path and kept script)"; bash "$here/rw.sh" gitlab "$data/gitlab.sql" rwqa92 2>&1
# The Synapse upgrade: a v54 database brought to v72 with --concurrently and --lock-timeout.
echo "=== synapse upgrade v54 -> v72"
docker exec rwqa90 psql -U postgres -q -c "DROP DATABASE IF EXISTS syn_up WITH (FORCE)" -c "CREATE DATABASE syn_up" >/dev/null 2>&1
docker cp "$data/synapse54.sql" rwqa90:/tmp/s54.sql >/dev/null; docker exec rwqa90 psql -U postgres -q -d syn_up -f /tmp/s54.sql >/dev/null 2>&1
pj="/work/rw-out/synapse/init/pgproj.json"; c=$(cs syn_up rwqa90)
pgproj publish --project "$pj" --target-connection "$c" --execute --concurrently --lock-timeout 5s --allow-destructive 2>&1 | tail -2
pgproj diff --project "$pj" --target-connection "$c" --exit-code 2>&1 | grep -E '^(Added|Removed|Modified):' | tr '\n' ' '; echo "exit ${PIPESTATUS[0]}"
[ "${KEEP:-}" = 1 ] || { docker rm -f rwqa90 rwqa91 rwqa92 >/dev/null 2>&1; docker network rm "$net" >/dev/null 2>&1; }
echo ALLDONE
