#!/usr/bin/env bash
# Real-world QA run for one schema file: load it into a throwaway server, then
#   init path:    pgproj init -> build -> publish into an empty database -> diff (both databases) -> pg_dump compare
#   written path: the file itself as a project -> build -> publish into an empty database -> diff -> pg_dump compare
# Usage: rw.sh <name> <schema file> <container> <port> [written]
# Build the CLI first (dotnet build src/PgProj.Cli). Fetch the schema files with fetch.sh. See README.md.
set -u
# Git Bash rewrites /paths given to docker into Windows paths; these are paths inside the container.
export MSYS_NO_PATHCONV=1
export PYTHONIOENCODING=utf-8
name=$1; file=$2; container=$3; port=$4; written=${5:-}
pw() { (cd "$1" && { pwd -W 2>/dev/null || pwd; }); }  # a Windows path under Git Bash, which docker, python, and the CLI need
here=$(pw "$(dirname "$0")")
repo=$(pw "$here/../../..")
bin="$repo/src/PgProj.Cli/bin/Debug/net10.0"
pgproj="$bin/pgproj"; [ -x "$pgproj" ] || pgproj="$bin/pgproj.exe"
out="$here/../work/realworld/rw-out/$name"
rm -rf "$out"; mkdir -p "$out"
cs() { echo "Host=localhost;Port=$port;Username=postgres;Password=qa;Database=$1;Timeout=60;Command Timeout=0"; }
psqlc() { docker exec -i "$container" psql -U postgres -v ON_ERROR_STOP=0 -q "$@"; }
now() { date +%s; }
src="rw_${name}_src"; dst="rw_${name}_dst"; wdb="rw_${name}_w"
for db in $src $dst $wdb; do psqlc -c "DROP DATABASE IF EXISTS $db WITH (FORCE);" >/dev/null 2>&1; psqlc -c "CREATE DATABASE $db;" >/dev/null; done

docker cp "$file" "$container:/tmp/$name.sql" >/dev/null
psqlc -d "$src" -f "/tmp/$name.sql" > "$out/load.log" 2>&1
echo "load errors: $(grep -ci 'error' "$out/load.log")"
# A script that creates its own database and \c's into it leaves $src empty; say so.
objs=$(psqlc -d "$src" -At -c "select count(*) from pg_class c join pg_namespace n on n.oid=c.relnamespace where n.nspname not in ('pg_catalog','information_schema','pg_toast') and c.relkind in ('r','v','m','p')")
echo "source objects: $objs"
[ "${objs:-0}" -gt 0 ] || { echo "ABORT: the source database is empty, so nothing below would test anything (the script may load into another database, or the file is missing)"; exit 2; }

# init path
t=$(now); "$pgproj" init --source-connection "$(cs $src)" --project "$out/init/pgproj.json" --output sql --force --report > "$out/init.log" 2>&1; echo "init: exit $? in $(( $(now) - t ))s, $(find "$out/init/sql" -name '*.sql' 2>/dev/null | wc -l) files"
t=$(now); "$pgproj" build --project "$out/init/pgproj.json" > "$out/build.log" 2>&1; echo "build: exit $? in $(( $(now) - t ))s, $(grep -c '\[Warning\]' "$out/build.log") warnings, $(grep -c '\[Error\]' "$out/build.log") errors"
t=$(now); "$pgproj" publish --project "$out/init/pgproj.json" --target-connection "$(cs $dst)" --execute --allow-destructive --allow-superuser-statements > "$out/publish.log" 2>&1; echo "publish: exit $? in $(( $(now) - t ))s"
t=$(now); "$pgproj" diff --project "$out/init/pgproj.json" --target-connection "$(cs $dst)" > "$out/diff-dst.log" 2>&1; echo "diff vs published: exit $? in $(( $(now) - t ))s, $(grep -E '^(Added|Removed|Modified):' "$out/diff-dst.log" | tr '\n' ' ')"
t=$(now); "$pgproj" diff --project "$out/init/pgproj.json" --target-connection "$(cs $src)" > "$out/diff-src.log" 2>&1; echo "diff vs source: exit $? in $(( $(now) - t ))s, $(grep -E '^(Added|Removed|Modified):' "$out/diff-src.log" | tr '\n' ' ')"
docker exec "$container" pg_dump -U postgres -s -O -x "$src" > "$out/src_raw.sql" 2>/dev/null
docker exec "$container" pg_dump -U postgres -s -O -x "$dst" > "$out/dst_raw.sql" 2>/dev/null
python "$repo/tools/diffqa/norm.py" "$out/src_raw.sql" > "$out/src.norm"; python "$repo/tools/diffqa/norm.py" "$out/dst_raw.sql" > "$out/dst.norm"
diff "$out/src.norm" "$out/dst.norm" > "$out/init.diff"; echo "init path dump differences: $(grep -c '^[<>]' "$out/init.diff") lines"

# written path
if [ -n "$written" ]; then
  mkdir -p "$out/written/sql"
  grep -v '^\\' "$file" > "$out/written/sql/schema.sql"
  echo "{ \"name\": \"$name\", \"version\": \"1.0\", \"source\": \"./sql\" }" > "$out/written/pgproj.json"
  t=$(now); "$pgproj" build --project "$out/written/pgproj.json" > "$out/w-build.log" 2>&1; echo "written build: exit $? in $(( $(now) - t ))s, $(grep -c '\[Warning\]' "$out/w-build.log") warnings, $(grep -c '\[Error\]' "$out/w-build.log") errors"
  t=$(now); "$pgproj" publish --project "$out/written/pgproj.json" --target-connection "$(cs $wdb)" --execute --allow-destructive --allow-superuser-statements > "$out/w-publish.log" 2>&1; echo "written publish: exit $? in $(( $(now) - t ))s"
  "$pgproj" diff --project "$out/written/pgproj.json" --target-connection "$(cs $wdb)" > "$out/w-diff.log" 2>&1; echo "written diff vs published: $(grep -E '^(Added|Removed|Modified):' "$out/w-diff.log" | tr '\n' ' ')"
  docker exec "$container" pg_dump -U postgres -s -O -x "$wdb" > "$out/w_raw.sql" 2>/dev/null
  python "$repo/tools/diffqa/norm.py" "$out/w_raw.sql" > "$out/w.norm"
  diff "$out/src.norm" "$out/w.norm" > "$out/written.diff"; echo "written path dump differences: $(grep -c '^[<>]' "$out/written.diff") lines"
fi
