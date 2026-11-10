#!/usr/bin/env bash
# Real-world QA run for one schema file: load it into a throwaway server, then
#   init path:    pgproj init -> build -> publish into an empty database -> diff (both databases) -> pg_dump compare
#                 (without owners and privileges, and with them)
#   kept script:  the script publish --output writes for an empty database, run with psql -v ON_ERROR_STOP=1 -f -> diff -> pg_dump compare
#   written path: the file itself as a project -> build -> publish into an empty database -> diff -> pg_dump compare
# Usage: rw.sh <name> <schema file> <container> [written]
#
# The server publishes no host port: pgproj (a self-contained linux-x64 build) runs in a container of its own on the same Docker network,
# $RW_NETWORK (default rwqa), and reaches the server by its container name. runall.sh makes the network, the servers, and the build;
# to run one schema by hand, make them the same way (see README.md). Fetch the schema files with fetch.sh.
set -u
# Git Bash rewrites /paths given to docker into Windows paths; these are paths inside the containers.
export MSYS_NO_PATHCONV=1
export PYTHONIOENCODING=utf-8
name=$1; file=$2; container=$3; written=${4:-}
pw() { (cd "$1" && { pwd -W 2>/dev/null || pwd; }); }  # a Windows path under Git Bash, which docker and python need
here=$(pw "$(dirname "$0")")
repo=$(pw "$here/../../..")
mkdir -p "$here/../work/realworld"
work=$(pw "$here/../work/realworld")   # mounted at /work in the pgproj container
net=${RW_NETWORK:-rwqa}
image=${RW_PGPROJ_IMAGE:-mcr.microsoft.com/dotnet/runtime-deps:10.0}
[ -x "$work/rw-tools/pgproj-linux/pgproj" ] || [ -f "$work/rw-tools/pgproj-linux/pgproj" ] || { echo "ABORT: no Linux build in $work/rw-tools/pgproj-linux (runall.sh makes it)"; exit 2; }
pgproj() { docker run --rm --network "$net" -v "$work:/work" -w /work "$image" /work/rw-tools/pgproj-linux/pgproj "$@"; }
out="$work/rw-out/$name"     # on the host
cout="/work/rw-out/$name"    # the same folder in the pgproj container
rm -rf "$out"; mkdir -p "$out"
cs() { echo "Host=$container;Port=5432;Username=postgres;Password=qa;Database=$1;Timeout=60;Command Timeout=0"; }
psqlc() { docker exec -i "$container" psql -U postgres -v ON_ERROR_STOP=0 -q "$@"; }
dump() { docker exec "$container" pg_dump -U postgres -s "$@"; }
norm() { python "$repo/tools/diffqa/norm.py" "$1"; }
now() { date +%s; }
src="rw_${name}_src"; dst="rw_${name}_dst"; kdb="rw_${name}_k"; wdb="rw_${name}_w"
for db in $src $dst $kdb $wdb; do psqlc -c "DROP DATABASE IF EXISTS $db WITH (FORCE);" >/dev/null 2>&1; psqlc -c "CREATE DATABASE $db;" >/dev/null; done

docker cp "$file" "$container:/tmp/$name.sql" >/dev/null
psqlc -d "$src" -f "/tmp/$name.sql" > "$out/load.log" 2>&1
echo "load errors: $(grep -ci 'error' "$out/load.log")"
# A script that creates its own database and \c's into it leaves $src empty; say so.
objs=$(psqlc -d "$src" -At -c "select count(*) from pg_class c join pg_namespace n on n.oid=c.relnamespace where n.nspname not in ('pg_catalog','information_schema','pg_toast') and c.relkind in ('r','v','m','p','f')")
echo "source objects: $objs"
[ "${objs:-0}" -gt 0 ] || { echo "ABORT: the source database is empty, so nothing below would test anything (the script may load into another database, or the file is missing)"; exit 2; }
dump -O -x "$src" > "$out/src_raw.sql" 2>/dev/null; norm "$out/src_raw.sql" > "$out/src.norm"
dump "$src" > "$out/src_acl_raw.sql" 2>/dev/null; norm "$out/src_acl_raw.sql" > "$out/src_acl.norm"
# compare <database> <label>: the dump without owners and privileges, then with them, against the source's.
compare() {
  dump -O -x "$1" > "$out/$2_raw.sql" 2>/dev/null; norm "$out/$2_raw.sql" > "$out/$2.norm"
  diff "$out/src.norm" "$out/$2.norm" > "$out/$2.diff"
  dump "$1" > "$out/$2_acl_raw.sql" 2>/dev/null; norm "$out/$2_acl_raw.sql" > "$out/$2_acl.norm"
  diff "$out/src_acl.norm" "$out/$2_acl.norm" > "$out/$2_acl.diff"
  echo "$2 dump differences: $(grep -c '^[<>]' "$out/$2.diff") lines; with owners and privileges: $(grep -c '^[<>]' "$out/$2_acl.diff") lines"
}

# init path
t=$(now); pgproj init --source-connection "$(cs $src)" --project "$cout/init/pgproj.json" --output sql --force --report > "$out/init.log" 2>&1; echo "init: exit $? in $(( $(now) - t ))s, $(find "$out/init/sql" -name '*.sql' 2>/dev/null | wc -l) files"
t=$(now); pgproj build --project "$cout/init/pgproj.json" > "$out/build.log" 2>&1; echo "build: exit $? in $(( $(now) - t ))s, $(grep -c '\[Warning\]' "$out/build.log") warnings, $(grep -c '\[Error\]' "$out/build.log") errors"
t=$(now); pgproj publish --project "$cout/init/pgproj.json" --target-connection "$(cs $dst)" --execute --allow-destructive --allow-superuser-statements > "$out/publish.log" 2>&1; echo "publish: exit $? in $(( $(now) - t ))s"
t=$(now); pgproj diff --project "$cout/init/pgproj.json" --target-connection "$(cs $dst)" > "$out/diff-dst.log" 2>&1; echo "diff vs published: exit $? in $(( $(now) - t ))s, $(grep -E '^(Added|Removed|Modified):' "$out/diff-dst.log" | tr '\n' ' ')"
t=$(now); pgproj diff --project "$cout/init/pgproj.json" --target-connection "$(cs $src)" > "$out/diff-src.log" 2>&1; echo "diff vs source: exit $? in $(( $(now) - t ))s, $(grep -E '^(Added|Removed|Modified):' "$out/diff-src.log" | tr '\n' ' ')"
compare "$dst" init

# kept script: what publish --output writes for an empty database, run the way a user runs it (G-165: it carries BEGIN and COMMIT)
pgproj publish --project "$cout/init/pgproj.json" --target-connection "$(cs $kdb)" --output "$cout/kept.sql" --allow-destructive --allow-superuser-statements > "$out/kept-plan.log" 2>&1; echo "kept script: written exit $?, $(grep -c '' "$out/kept.sql" 2>/dev/null) lines, BEGIN $(grep -c '^BEGIN;' "$out/kept.sql" 2>/dev/null), COMMIT $(grep -c '^COMMIT;' "$out/kept.sql" 2>/dev/null)"
docker cp "$out/kept.sql" "$container:/tmp/${name}_kept.sql" >/dev/null 2>&1
t=$(now); docker exec -i "$container" psql -U postgres -v ON_ERROR_STOP=1 -q -d "$kdb" -f "/tmp/${name}_kept.sql" > "$out/kept-apply.log" 2>&1; echo "kept script: psql -f exit $? in $(( $(now) - t ))s"
pgproj diff --project "$cout/init/pgproj.json" --target-connection "$(cs $kdb)" > "$out/kept-diff.log" 2>&1; echo "kept script: diff $(grep -E '^(Added|Removed|Modified):' "$out/kept-diff.log" | tr '\n' ' ')"
compare "$kdb" kept

# written path
if [ -n "$written" ]; then
  mkdir -p "$out/written/sql"
  grep -v '^\\' "$file" > "$out/written/sql/schema.sql"
  echo "{ \"name\": \"$name\", \"version\": \"1.0\", \"source\": \"./sql\" }" > "$out/written/pgproj.json"
  t=$(now); pgproj build --project "$cout/written/pgproj.json" > "$out/w-build.log" 2>&1; echo "written build: exit $? in $(( $(now) - t ))s, $(grep -c '\[Warning\]' "$out/w-build.log") warnings, $(grep -c '\[Error\]' "$out/w-build.log") errors"
  t=$(now); pgproj publish --project "$cout/written/pgproj.json" --target-connection "$(cs $wdb)" --execute --allow-destructive --allow-superuser-statements > "$out/w-publish.log" 2>&1; echo "written publish: exit $? in $(( $(now) - t ))s"
  pgproj diff --project "$cout/written/pgproj.json" --target-connection "$(cs $wdb)" > "$out/w-diff.log" 2>&1; echo "written diff vs published: $(grep -E '^(Added|Removed|Modified):' "$out/w-diff.log" | tr '\n' ' ')"
  compare "$wdb" written
fi
