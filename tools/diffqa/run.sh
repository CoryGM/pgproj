#!/bin/bash
# Differential QA for one regression file (see README.md).
#   regress/<branch>/<name>.sql -> database A (psql, top-level DROPs removed, errors ignored)
#   init path:    pgproj init from A -> publish into empty C -> pg_dump C, compared with pg_dump A
#   written path: pg_dump A as a pgproj project -> publish into empty B -> pg_dump B, compared with pg_dump A
# Usage: run.sh <name>   (settings from the environment, see config.sh)
set -u
here=$(cd "$(dirname "$0")" && pwd)
. "$here/config.sh"
name=$1
w="$DIFFQA_WORK/$DIFFQA_LABEL/$name"; rm -rf "$w"; mkdir -p "$w/wproj" "$w/iproj"
safe=$(echo "$name" | tr -c 'a-z0-9\n' '_')
a=dq_${safe}_a; b=dq_${safe}_b; c=dq_${safe}_c
cs() { echo "Host=localhost;Port=$DIFFQA_PORT;Database=$1;Username=postgres;Password=$DIFFQA_PASSWORD"; }
psqlc() { docker exec -i "$DIFFQA_CONTAINER" psql -U postgres "$@"; }
for d in $a $b $c; do psqlc -qc "drop database if exists $d with (force)" -c "create database $d" >/dev/null 2>&1; done

grep -viE '^\s*DROP\s' "$DIFFQA_REGRESS/$name.sql" > "$w/input.sql"
timeout "$DIFFQA_TIMEOUT" docker exec -i "$DIFFQA_CONTAINER" psql -U postgres -d $a -q -v ON_ERROR_STOP=0 < "$w/input.sql" > "$w/psql.out" 2>&1
docker exec "$DIFFQA_CONTAINER" pg_dump -U postgres -s -O -x $a > "$w/a_raw.sql" 2>/dev/null
python "$here/norm.py" "$w/a_raw.sql" > "$w/a.norm"

# Written path. pg_dump 18 writes psql meta-commands (\restrict), which are not SQL.
grep -v '^\\' "$w/a_raw.sql" > "$w/wproj/schema.sql"
printf '{"name":"w","version":"1","source":"./","schemas":[],"targetConnectionString":"%s"}' "$(cs $b)" > "$w/wproj/pgproj.json"
timeout "$DIFFQA_TIMEOUT" "$DIFFQA_PGPROJ" build --project "$w/wproj/pgproj.json" > "$w/w_build.out" 2>&1
timeout "$DIFFQA_TIMEOUT" "$DIFFQA_PGPROJ" publish --project "$w/wproj/pgproj.json" --execute --allow-destructive --allow-superuser-statements > "$w/w_pub.out" 2>&1; wrc=$?
docker exec "$DIFFQA_CONTAINER" pg_dump -U postgres -s -O -x $b > "$w/b_raw.sql" 2>/dev/null
python "$here/norm.py" "$w/b_raw.sql" > "$w/b.norm"

# Init path.
timeout "$DIFFQA_TIMEOUT" "$DIFFQA_PGPROJ" init --source-connection "$(cs $a)" --project "$w/iproj/pgproj.json" > "$w/i_init.out" 2>&1; irc=$?
timeout "$DIFFQA_TIMEOUT" "$DIFFQA_PGPROJ" publish --project "$w/iproj/pgproj.json" --target-connection "$(cs $c)" --execute --allow-destructive --allow-superuser-statements > "$w/i_pub.out" 2>&1; iprc=$?
docker exec "$DIFFQA_CONTAINER" pg_dump -U postgres -s -O -x $c > "$w/c_raw.sql" 2>/dev/null
python "$here/norm.py" "$w/c_raw.sql" > "$w/c.norm"

diff "$w/a.norm" "$w/b.norm" > "$w/written.diff"; diff "$w/a.norm" "$w/c.norm" > "$w/init.diff"
printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$name" "$(grep -c . "$w/a.norm")" "$wrc" "$(grep -c '^[<>]' "$w/written.diff")" "$irc" "$iprc" "$(grep -c '^[<>]' "$w/init.diff")" > "$w/result.tsv"
for d in $a $b $c; do psqlc -qc "drop database if exists $d with (force)" >/dev/null 2>&1; done
cat "$w/result.tsv"
