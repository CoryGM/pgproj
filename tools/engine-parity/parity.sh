#!/usr/bin/env bash
# Output parity of the CLI against a baseline build (G-309, G-310): the same command on the same input must print the same on standard
# output and standard error, exit with the same code, and write the same files. The baseline is built once from the commit before the
# work started (G-309):
#
#   git worktree add --detach <dir> aebdd7f && (cd <dir> && dotnet publish src/PgProj.Cli/PgProj.Cli.csproj -c Release -o <repo>/artifacts/baseline-cli)
#
# Usage (Git Bash, from anywhere):
#   tools/engine-parity/parity.sh samples <new-cli-dir>             build over the sample projects and a project with an error (build --format json, new in
#                                                                   G-309f, is shown, not compared)
#   tools/engine-parity/parity.sh wide    <new-cli-dir>             build --dump-model over a generated 1,200-column table and other wide statements
#   tools/engine-parity/parity.sh corpus  <new-cli-dir> [pg18 ...]  build --dump-model over the differential harness's projects
#                                                                   (tools/diffqa/work/<version>/<case>/{iproj,wproj}; needs a harness run)
#   PARITY_PG="Host=localhost;Port=55490;Username=postgres;Password=..." tools/engine-parity/parity.sh diff <new-cli-dir>
#                                                                   diff (and build/diff in source mode) over the samples against a throwaway server (databases parity_a, parity_b, parity_t)
#   PARITY_PG="..." PARITY_CONTAINER=<name> tools/engine-parity/parity.sh publish <new-cli-dir>
#                                                                   publish (printed, --dry-run, --output, --execute, --concurrently, --comments-only, refusals, failures, tenants, source mode)
#                                                                   against a throwaway server; each side changes databases of its own (see the mode)
#   PARITY_PG="..." PARITY_CONTAINER=<name> tools/engine-parity/parity.sh init <new-cli-dir>
#                                                                   init (plain, --report, --schemas, --exclude-*, --erd, tenants with --tenant-file, the errors) into
#                                                                   empty folders, compared file by file (G-309d)
#   [PARITY_PG="..." PARITY_CONTAINER=<name>] tools/engine-parity/parity.sh document <new-cli-dir>
#                                                                   document of both samples, a project with tag problems, the errors, and with PARITY_PG a database (G-309d)
# <new-cli-dir> is the output of `dotnet publish src/PgProj.Cli/PgProj.Cli.csproj -c Release -o <dir>` of the commit being checked.
# Environment: BASELINE (default artifacts/baseline-cli), OUT (default artifacts/engine-parity/<UTC time>), PARITY_COMBINED=1 (compare standard output and
# standard error as one stream, in the order a terminal shows them).
# Exit code: 0 when every case matched, 1 otherwise; a case that differs keeps both outputs under OUT.
set -u
export MSYS_NO_PATHCONV=1

# dotnet is a Windows program on this machine: it needs C:/... paths, not /c/....
winpath() { if command -v cygpath >/dev/null 2>&1; then cygpath -m "$1"; else echo "$1"; fi; }

ROOT="$(winpath "$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)")"
BASELINE="$(winpath "${BASELINE:-$ROOT/artifacts/baseline-cli}")"
OUT="$(winpath "${OUT:-$ROOT/artifacts/engine-parity/$(date -u +%Y%m%d-%H%M%S)}")"
mode="${1:?mode: samples, wide, corpus, diff, publish, init, or document}"
NEW="$(winpath "${2:?the directory of the CLI to check}")"
shift 2
mkdir -p "$OUT"

# psql on the throwaway server (publish mode), through its container.
PARITY_CONTAINER="${PARITY_CONTAINER:-pgproj-parity-55490}"
psql_run() { docker exec -i "$PARITY_CONTAINER" psql -U postgres -v ON_ERROR_STOP=1 -q -At "$@"; }

# pg_recreate <name> <template>: a fresh database from a template (publish mode).
pg_recreate() {
  psql_run -d postgres -c "DROP DATABASE IF EXISTS $1 WITH (FORCE)" >/dev/null
  psql_run -d postgres -c "CREATE DATABASE $1 TEMPLATE $2" >/dev/null
}

# run_one <label> <cli-dir> <side> -- <arguments, with {OUT} for the side's folder of written files>
# Publish mode also takes {NEWDB:<template>:<key>} (a connection string to a fresh database pp_<side>_<key> made from the template, with $PREP_SQL run in it
# when $PREP_KEY is <key>) and {DB:<key>} (the connection string to that database as it stands), so each side changes a database of its own.
run_one() {
  local label="$1" cli="$2" side="$3"
  shift 4
  local dir="$OUT/$label/$side"
  mkdir -p "$dir/files"
  local args=() a name
  for a in "$@"; do
    a="${a//\{OUT\}/$dir/files}"
    while [[ "$a" =~ \{NEWDB:([a-z_0-9]+):([a-z_0-9]+)\} ]]; do
      name="pp_${side}_${BASH_REMATCH[2]}"
      pg_recreate "$name" "${BASH_REMATCH[1]}"
      if [ "${PREP_KEY:-}" = "${BASH_REMATCH[2]}" ] && [ -n "${PREP_SQL:-}" ]; then psql_run -d "$name" -c "$PREP_SQL" >/dev/null; fi
      a="${a/"${BASH_REMATCH[0]}"/$PARITY_PG;Database=$name}"
    done
    while [[ "$a" =~ \{DB:([a-z_0-9]+)\} ]]; do
      name="pp_${side}_${BASH_REMATCH[1]}"
      a="${a/"${BASH_REMATCH[0]}"/$PARITY_PG;Database=$name}"
    done
    args+=("$a")
  done
  # PARITY_COMBINED=1 writes standard error into standard output, as a terminal shows them, so the order of the two is compared too (G-316).
  if [ -n "${PARITY_COMBINED:-}" ]; then
    (cd "$ROOT" && dotnet "$cli/pgproj.dll" "${args[@]}" >"$dir/stdout.txt" 2>&1; echo $? >"$dir/exit.txt"; : >"$dir/stderr.txt")
  else
    (cd "$ROOT" && dotnet "$cli/pgproj.dll" "${args[@]}" >"$dir/stdout.txt" 2>"$dir/stderr.txt"; echo $? >"$dir/exit.txt")
  fi
  # HASH_DUMPS=1 (wide mode): the dump of the wide project is about 10 GB, so it is replaced by its SHA-256 as soon as it is written, and the two sides'
  # hashes are compared. Only one dump is on disk at a time, and only until it is hashed (G-309e).
  if [ -n "${HASH_DUMPS:-}" ] && [ -f "$dir/files/model.json" ]; then
    sha256sum <"$dir/files/model.json" | cut -d' ' -f1 >"$dir/files/model.json.sha256"
    rm -f "$dir/files/model.json"
  fi
}

# mask_tree <label> <side> (init and document modes, G-309d): what a run writes into folders below its own names files and says when it ran. The time in a report's or
# a diagram's name, the times inside the files (the report's start, end, and elapsed rows, a document's generated time), and the side's folder in a written path are
# meant to differ; they are masked in every file written and in what was printed.
mask_tree() {
  local dir="$OUT/$1/$2" f
  while IFS= read -r -d '' f; do
    local base="${f##*/}" masked
    masked="$(echo "$base" | sed -E 's/^(init-report|erd)-[0-9]{4}(-[0-9]{2}){5}/\1-<t>/')"
    [ "$masked" != "$base" ] && mv "$f" "${f%/*}/$masked"
  done < <(find "$dir/files" -type f -print0)
  while IFS= read -r -d '' f; do
    sed -i -E -e 's#\\\\#/#g' -e 's#\\#/#g' -e "s#$dir/files#<out>#g" \
      -e 's/[0-9]{4}-[0-9]{2}-[0-9]{2}[ T][0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]+)?Z?( UTC)?/<time>/g' \
      -e 's/\| Elapsed \| [0-9.]+ s \|/| Elapsed | <n> s |/' -e 's/(init-report|erd)-[0-9]{4}(-[0-9]{2}){5}/\1-<t>/g' "$f"
  done < <(find "$dir/files" -type f -print0)
  for f in stdout.txt stderr.txt; do
    sed -i -E 's/(init-report|erd)-[0-9]{4}(-[0-9]{2}){5}/\1-<t>/g' "$dir/$f"
  done
}

# compare <label> -- <arguments>: runs the baseline and the new CLI and reports whether everything they did matches.
compare() {
  local label="$1"
  shift 2
  run_one "$label" "$BASELINE" base -- "$@"
  run_one "$label" "$NEW" new -- "$@"
  # The side's folder is part of a written path in the output, and a diagram says when it was generated: both are meant to differ.
  local side f
  for side in base new; do
    for f in stdout.txt stderr.txt; do
      # The CLI prints Windows paths with backslashes: both sides are written with slashes first, so the folder can be found.
      # --verbose prints how long a step took.
      sed -i -e 's#\\#/#g' -e "s#$OUT/$label/$side/files#<out>#g" -E -e 's/[0-9]+ ms/<n> ms/g' -e 's/pp_(base|new)_/pp_/g' "$OUT/$label/$side/$f"
    done
    for f in "$OUT/$label/$side/files"/*.mmd "$OUT/$label/$side/files"/*.md "$OUT/$label/$side/files"/*.html; do
      [ -f "$f" ] && sed -i -E 's/[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2} UTC/<time>/g' "$f"
    done
    [ -n "${MASK_TREE:-}" ] && mask_tree "$label" "$side"
  done
  if diff -rq "$OUT/$label/base" "$OUT/$label/new" >/dev/null 2>&1; then
    echo "ok   $label"
    rm -rf "${OUT:?}/$label"
  else
    echo "DIFF $label"
    return 1
  fi
}

# show <label> -- <arguments>: runs the new CLI alone and keeps what it printed under OUT/<label>/new, for a shape that is meant to differ (the JSON of diff).
show() {
  local label="$1"
  shift 2
  run_one "$label" "$NEW" new -- "$@"
  echo "show $label ($OUT/$label/new)"
}

failures=0

case "$mode" in
  samples)
    ms="$ROOT/samples/MultiSchema/pgproj.json"
    pt="$ROOT/samples/SchemaPerTenant/pgproj.json"
    # A project with errors, so the diagnostics and the exit code 1 are compared too.
    broken="$OUT/inputs/broken"
    mkdir -p "$broken/sql"
    printf '{"name":"broken","version":"1","source":"./sql"}\n' >"$broken/pgproj.json"
    printf 'CREATE TABLE b (a int, ;\nCREATE TABLE c (x int REFERENCES nowhere (id));\nINSERT INTO c VALUES (1);\n' >"$broken/sql/broken.sql"
    for p in "multischema:$ms" "tenant:$pt" "broken:$broken/pgproj.json"; do
      n="${p%%:*}"
      f="${p#*:}"
      compare "$n-build" -- build --project "$f" || failures=$((failures + 1))
      compare "$n-build-quiet" -- build --project "$f" --quiet || failures=$((failures + 1))
      compare "$n-build-verbose" -- build --project "$f" --verbose || failures=$((failures + 1))
      compare "$n-build-dump-erd" -- build --project "$f" --dump-model "{OUT}/model.json" --erd "{OUT}/erd" --erd-html || failures=$((failures + 1))
      # build --format json is new (G-309f), so the baseline has no output to compare it with: it is shown, to be read.
      show "$n-build-json" -- build --project "$f" --format json
    done
    # A project file that is not there, one that is not valid JSON, and one that lacks a name: the messages and the exit code 1.
    mkdir -p "$OUT/inputs/invalid" "$OUT/inputs/badjson"
    printf '{"version":"1","source":"./sql"}\n' >"$OUT/inputs/invalid/pgproj.json"
    printf '{ not json\n' >"$OUT/inputs/badjson/pgproj.json"
    compare "notfound-build" -- build --project "$OUT/inputs/none/pgproj.json" || failures=$((failures + 1))
    compare "invalid-build" -- build --project "$OUT/inputs/invalid/pgproj.json" || failures=$((failures + 1))
    compare "badjson-build" -- build --project "$OUT/inputs/badjson/pgproj.json" || failures=$((failures + 1))
    show "notfound-build-json" -- build --project "$OUT/inputs/none/pgproj.json" --format json
    show "invalid-build-json" -- build --project "$OUT/inputs/invalid/pgproj.json" --format json
    ;;
  wide)
    # One table of 1,200 columns of five kinds (default, identity, rename annotation, generated, check) and other wide statements.
    wide="$OUT/inputs/wide"
    mkdir -p "$wide/sql"
    printf '{"name":"wide","version":"1","source":"./sql"}\n' >"$wide/pgproj.json"
    {
      echo "CREATE TABLE public.wide ("
      for ((i = 0; i < 1200; i++)); do
        case $((i % 5)) in
          0) echo "  c$i integer DEFAULT $i NOT NULL," ;;
          1) echo "  c$i bigint GENERATED BY DEFAULT AS IDENTITY (START WITH $i)," ;;
          2) printf '  -- @renamed-from: old%d\n  c%d character varying(20) DEFAULT '"'"'x'"'"',\n' "$i" "$i" ;;
          3) echo "  c$i integer GENERATED ALWAYS AS (c0 + $i) STORED," ;;
          *) echo "  c$i numeric(10,2) CHECK (c$i > 0)," ;;
        esac
      done
      for ((i = 4; i < 1200; i += 5)); do echo "  CONSTRAINT chk_$i CHECK (c$i > 0 AND c$i < $((i + 10))),"; done
      echo "  CHECK (c0 > 0)"
      echo ");"
      printf 'CREATE VIEW public.wide_v AS SELECT '
      for ((i = 0; i < 300; i++)); do printf 'a.c%d + %d AS x%d, ' "$i" "$i" "$i"; done
      echo 'a.c0 AS last FROM public.wide a;'
      printf 'CREATE TYPE public.wide_t AS ('
      for ((i = 0; i < 300; i++)); do printf 'f%d integer, ' "$i"; done
      echo 'last integer);'
      printf 'CREATE INDEX wide_i ON public.wide ('
      for ((i = 0; i < 200; i++)); do printf 'lower(c%d::text), ' "$((i * 5))"; done
      echo 'c0);'
      printf 'CREATE FUNCTION public.wide_f() RETURNS int LANGUAGE sql'
      for ((i = 0; i < 200; i++)); do
        case $((i % 4)) in
          0) printf " SET custom.s%d = 'v%d'" "$i" "$i" ;;
          1) printf ' SET custom.s%d TO -%d' "$i" "$i" ;;
          2) printf ' SET custom.s%d FROM CURRENT' "$i" ;;
          *) printf ' SET custom.s%d = a, b' "$i" ;;
        esac
      done
      echo ' AS $$ SELECT 1 $$;'
      printf 'CREATE TABLE public.wide_like (a int'
      for ((i = 0; i < 200; i++)); do
        if ((i % 2 == 0)); then printf ', LIKE public.wide INCLUDING DEFAULTS'; else printf ',  like   public.wide'; fi
      done
      echo ');'
    } >"$wide/sql/wide.sql"
    HASH_DUMPS=1 compare "wide-build" -- build --project "$wide/pgproj.json" --dump-model "{OUT}/model.json" || failures=$((failures + 1))
    ;;
  corpus)
    versions=("$@")
    [ ${#versions[@]} -gt 0 ] || versions=(pg18)
    inputs="$OUT/inputs"
    mkdir -p "$inputs"
    cases=()
    for v in "${versions[@]}"; do
      for d in "$ROOT/tools/diffqa/work/$v"/*/; do
        c="$(basename "$d")"
        # The projects' own pgproj.json files name a connection the loader no longer accepts: each is replaced by a minimal one.
        if [ -d "$d/iproj/sql" ]; then
          mkdir -p "$inputs/$v-$c-i"
          printf '{"name":"p","version":"1","source":"%s"}\n' "${d}iproj/sql" >"$inputs/$v-$c-i/pgproj.json"
          cases+=("$v-$c-i")
        fi
        if [ -d "${d}wproj" ]; then
          mkdir -p "$inputs/$v-$c-w"
          printf '{"name":"p","version":"1","source":"%s"}\n' "${d}wproj" >"$inputs/$v-$c-w/pgproj.json"
          cases+=("$v-$c-w")
        fi
      done
    done
    echo "${#cases[@]} projects"
    for c in "${cases[@]}"; do
      compare "$c" -- build --project "$inputs/$c/pgproj.json" --dump-model "{OUT}/model.json" || failures=$((failures + 1))
    done
    ;;
  diff)
    # diff against a throwaway server: PARITY_PG is a connection string without a database (Host=localhost;Port=5549x;Username=postgres;Password=...),
    # and the databases parity_a, parity_b, and parity_t exist on it. The samples are published with the baseline first.
    : "${PARITY_PG:?PARITY_PG: a connection string to a throwaway server, without a database}"
    ms="$ROOT/samples/MultiSchema/pgproj.json"
    pt="$ROOT/samples/SchemaPerTenant/pgproj.json"
    a="$PARITY_PG;Database=parity_a"
    b="$PARITY_PG;Database=parity_b"
    t="$PARITY_PG;Database=parity_t"
    dotnet "$BASELINE/pgproj.dll" publish --project "$ms" --target-connection "$a" --execute --allow-destructive >/dev/null 2>&1
    dotnet "$BASELINE/pgproj.dll" publish --project "$pt" --target-connection "$t" --execute --allow-destructive >/dev/null 2>&1
    # One change in each: a table in the multi-schema project, and a table in the tenant template.
    changed="$OUT/inputs/ms-changed"
    mkdir -p "$changed"
    cp -r "$ROOT/samples/MultiSchema/." "$changed/"
    printf 'CREATE TABLE sales.parity_added (id integer PRIMARY KEY, note text);\n' >"$changed/Schema/parity_added.sql"
    tchanged="$OUT/inputs/tenant-changed"
    mkdir -p "$tchanged"
    cp -r "$ROOT/samples/SchemaPerTenant/." "$tchanged/"
    printf 'CREATE TABLE parity_added (id integer PRIMARY KEY);\n' >"$tchanged/sql/Tenant/parity_added.sql"
    broken="$OUT/inputs/broken"
    mkdir -p "$broken/sql"
    printf '{"name":"broken","version":"1","source":"./sql"}\n' >"$broken/pgproj.json"
    printf 'CREATE TABLE b (a int, ;\n' >"$broken/sql/broken.sql"
    for p in "ms|$ms|$a" "mschanged|$changed/pgproj.json|$a" "tenant|$pt|$t" "tenantchanged|$tchanged/pgproj.json|$t"; do
      IFS="|" read -r n f c <<<"$p"
      compare "$n-diff" -- diff --project "$f" --target-connection "$c" || failures=$((failures + 1))
      compare "$n-diff-verbose" -- diff --project "$f" --target-connection "$c" --verbose || failures=$((failures + 1))
      compare "$n-diff-quiet" -- diff --project "$f" --target-connection "$c" --quiet || failures=$((failures + 1))
      compare "$n-diff-exit" -- diff --project "$f" --target-connection "$c" --exit-code || failures=$((failures + 1))
      compare "$n-diff-erd" -- diff --project "$f" --target-connection "$c" --erd "{OUT}/erd" || failures=$((failures + 1))
      show "$n-diff-json" -- diff --project "$f" --target-connection "$c" --format json
    done
    compare "tenant-only" -- diff --project "$tchanged/pgproj.json" --target-connection "$t" --tenants acme || failures=$((failures + 1))
    compare "tenant-unknown" -- diff --project "$tchanged/pgproj.json" --target-connection "$t" --tenants nobody || failures=$((failures + 1))
    compare "tenants-without-tenants" -- diff --project "$ms" --target-connection "$a" --tenants acme || failures=$((failures + 1))
    # Source mode: the database parity_a as the desired state, parity_b as the target; with no project file, and with the sample's.
    compare "source-nofile" -- diff --project "$OUT/inputs/none/pgproj.json" --source-connection "$a" --target-connection "$b" || failures=$((failures + 1))
    compare "source-verbose" -- diff --project "$OUT/inputs/none/pgproj.json" --source-connection "$a" --target-connection "$b" --verbose || failures=$((failures + 1))
    compare "source-exit" -- diff --project "$OUT/inputs/none/pgproj.json" --source-connection "$a" --target-connection "$b" --exit-code --exclude-owners || failures=$((failures + 1))
    compare "source-sample" -- diff --project "$ms" --source-connection "$a" --target-connection "$b" || failures=$((failures + 1))
    compare "source-same" -- diff --project "$OUT/inputs/none/pgproj.json" --source-connection "$a" --target-connection "$a" || failures=$((failures + 1))
    show "source-json" -- diff --project "$OUT/inputs/none/pgproj.json" --source-connection "$a" --target-connection "$b" --format json
    compare "source-tenants" -- diff --project "$pt" --source-connection "$a" --target-connection "$b" || failures=$((failures + 1))
    compare "build-source" -- build --project "$OUT/inputs/none/pgproj.json" --source-connection "$a" || failures=$((failures + 1))
    compare "build-source-verbose" -- build --project "$OUT/inputs/none/pgproj.json" --source-connection "$a" --verbose --dump-model "{OUT}/model.json" || failures=$((failures + 1))
    # Errors: a project with errors, a missing project, no target, a malformed target, an unreachable one.
    compare "broken-diff" -- diff --project "$broken/pgproj.json" --target-connection "$a" || failures=$((failures + 1))
    compare "notfound-diff" -- diff --project "$OUT/inputs/none/pgproj.json" --target-connection "$a" || failures=$((failures + 1))
    compare "notarget-diff" -- diff --project "$ms" || failures=$((failures + 1))
    compare "badtarget-diff" -- diff --project "$ms" --target-connection "Host=localhost;Port=notaport" || failures=$((failures + 1))
    compare "unreachable-diff" -- diff --project "$ms" --target-connection "Host=127.0.0.1;Port=1;Database=x;Timeout=3" || failures=$((failures + 1))
    ;;
  publish)
    # publish against a throwaway server (G-309c): PARITY_PG is a connection string without a database, PARITY_CONTAINER the container's name (for psql). Each side
    # changes databases of its own (pp_base_*, pp_new_*), made from templates: pp_empty, pp_ms and pp_pt (the samples, published by the baseline), pp_small (a small
    # project with rows). Every case passes --target-connection: the samples' own connection strings are never used.
    : "${PARITY_PG:?PARITY_PG: a connection string to a throwaway server, without a database}"
    # The shell must not supply a connection of its own to a case that gives none.
    unset PGPROJ_TARGET_CONNECTION PGPROJ_SOURCE_CONNECTION
    ms="$ROOT/samples/MultiSchema/pgproj.json"
    pt="$ROOT/samples/SchemaPerTenant/pgproj.json"
    for d in pp_empty pp_ms pp_pt pp_small; do psql_run -d postgres -c "DROP DATABASE IF EXISTS $d WITH (FORCE)" >/dev/null; psql_run -d postgres -c "CREATE DATABASE $d" >/dev/null; done
    dotnet "$BASELINE/pgproj.dll" publish --project "$ms" --target-connection "$PARITY_PG;Database=pp_ms" --execute --allow-destructive >/dev/null 2>&1
    dotnet "$BASELINE/pgproj.dll" publish --project "$pt" --target-connection "$PARITY_PG;Database=pp_pt" --execute --allow-destructive >/dev/null 2>&1

    # A small project, and variations of it.
    inputs="$OUT/inputs"
    mkproj() { mkdir -p "$inputs/$1/sql"; printf '{"name":"%s","version":"1","source":"./sql"%s}\n' "$1" "${3:-}" >"$inputs/$1/pgproj.json"; printf '%s\n' "$2" >"$inputs/$1/sql/s.sql"; }
    base_sql='CREATE SCHEMA s; CREATE TABLE s.t (id int PRIMARY KEY, v int); CREATE TABLE s.u (id int);'
    mkproj small "$base_sql"
    dotnet "$BASELINE/pgproj.dll" publish --project "$inputs/small/pgproj.json" --target-connection "$PARITY_PG;Database=pp_small" --execute >/dev/null 2>&1
    psql_run -d pp_small -c "INSERT INTO s.t VALUES (1, 1), (2, 2); INSERT INTO s.u VALUES (1);" >/dev/null
    mkproj sm_index 'CREATE SCHEMA s; CREATE TABLE s.t (id int PRIMARY KEY, v int); CREATE TABLE s.u (id int); CREATE INDEX t_v_idx ON s.t (v); ALTER TABLE s.u ADD CONSTRAINT u_fk FOREIGN KEY (id) REFERENCES s.t (id);'
    mkproj sm_notnull 'CREATE SCHEMA s; CREATE TABLE s.t (id int PRIMARY KEY, v int NOT NULL); CREATE TABLE s.u (id int);'
    mkproj sm_comments "CREATE SCHEMA s; CREATE TABLE s.t (id int PRIMARY KEY, v int, note text); COMMENT ON TABLE s.t IS 'orders'; CREATE TABLE s.u (id int); CREATE TABLE s.w (id int); COMMENT ON TABLE s.w IS 'new';"
    mkproj sm_drop 'CREATE SCHEMA s; CREATE TABLE s.t (id int PRIMARY KEY, v int);'
    mkproj sm_fail 'CREATE SCHEMA s; CREATE TABLE s.t (id int PRIMARY KEY, v int, must_have text NOT NULL); CREATE TABLE s.u (id int);'
    mkproj sm_role 'CREATE SCHEMA s; CREATE TABLE s.t (id int PRIMARY KEY, v int); CREATE TABLE s.u (id int); GRANT SELECT ON s.t TO pgproj_parity_missing_role;'
    mkproj sm_event 'CREATE SCHEMA s; CREATE TABLE s.t (id int PRIMARY KEY, v int); CREATE TABLE s.u (id int); CREATE FUNCTION s.on_ddl() RETURNS event_trigger LANGUAGE plpgsql AS $$ BEGIN END $$; CREATE EVENT TRIGGER pp_event ON ddl_command_start EXECUTE FUNCTION s.on_ddl();'
    mkproj sm_script 'CREATE SCHEMA s; CREATE TABLE s.t (id int PRIMARY KEY, v int, extra int); CREATE TABLE s.u (id int);' ',"predeploy":"./pre.sql","postdeploy":"./post.sql"'
    printf 'SELECT 1;\n' >"$inputs/sm_script/pre.sql"
    printf 'SELECT 2;\n' >"$inputs/sm_script/post.sql"
    mkproj nameless 'CREATE TABLE x (a int);'
    printf '{"version":"1","source":"./sql"}\n' >"$inputs/nameless/pgproj.json"
    broken="$inputs/broken"
    mkdir -p "$broken/sql"
    printf '{"name":"broken","version":"1","source":"./sql"}\n' >"$broken/pgproj.json"
    printf 'CREATE TABLE b (a int, ;\n' >"$broken/sql/broken.sql"

    # The samples against an empty database: printed, dry run, written, quiet, verbose, with a lock timeout, then run, and run again.
    for p in "ms|$ms" "tenant|$pt"; do
      IFS="|" read -r n f <<<"$p"
      compare "$n-publish" -- publish --project "$f" --target-connection "{NEWDB:pp_empty:$n}" || failures=$((failures + 1))
      compare "$n-publish-dryrun" -- publish --project "$f" --target-connection "{NEWDB:pp_empty:$n}" --dry-run --execute || failures=$((failures + 1))
      compare "$n-publish-output" -- publish --project "$f" --target-connection "{NEWDB:pp_empty:$n}" --output "{OUT}/deploy.sql" || failures=$((failures + 1))
      compare "$n-publish-output-quiet" -- publish --project "$f" --target-connection "{NEWDB:pp_empty:$n}" --output "{OUT}/deploy.sql" --quiet || failures=$((failures + 1))
      compare "$n-publish-verbose" -- publish --project "$f" --target-connection "{NEWDB:pp_empty:$n}" --verbose || failures=$((failures + 1))
      compare "$n-publish-timeout" -- publish --project "$f" --target-connection "{NEWDB:pp_empty:$n}" --lock-timeout 5s --output "{OUT}/deploy.sql" || failures=$((failures + 1))
      compare "$n-publish-erd" -- publish --project "$f" --target-connection "{NEWDB:pp_empty:$n}" --dry-run --erd "{OUT}/erd" || failures=$((failures + 1))
      compare "$n-execute" -- publish --project "$f" --target-connection "{NEWDB:pp_empty:$n}" --execute --allow-destructive || failures=$((failures + 1))
      compare "$n-execute-again" -- publish --project "$f" --target-connection "{DB:$n}" --execute || failures=$((failures + 1))
      compare "$n-execute-verbose-output" -- publish --project "$f" --target-connection "{NEWDB:pp_empty:${n}2}" --execute --verbose --output "{OUT}/deploy.sql" || failures=$((failures + 1))
      compare "$n-execute-diff" -- diff --project "$f" --target-connection "{DB:${n}2}" --exit-code || failures=$((failures + 1))
    done

    # A change to a published sample: a table in each.
    changed="$inputs/ms-changed"
    mkdir -p "$changed"
    cp -r "$ROOT/samples/MultiSchema/." "$changed/"
    printf 'CREATE TABLE sales.parity_added (id integer PRIMARY KEY, note text);\n' >"$changed/Schema/parity_added.sql"
    tchanged="$inputs/tenant-changed"
    mkdir -p "$tchanged"
    cp -r "$ROOT/samples/SchemaPerTenant/." "$tchanged/"
    printf 'CREATE TABLE parity_added (id integer PRIMARY KEY);\n' >"$tchanged/sql/Tenant/parity_added.sql"
    for p in "mschanged|$changed/pgproj.json|pp_ms" "tenantchanged|$tchanged/pgproj.json|pp_pt"; do
      IFS="|" read -r n f tpl <<<"$p"
      compare "$n-publish" -- publish --project "$f" --target-connection "{NEWDB:$tpl:$n}" || failures=$((failures + 1))
      compare "$n-publish-output" -- publish --project "$f" --target-connection "{NEWDB:$tpl:$n}" --output "{OUT}/deploy.sql" --dry-run || failures=$((failures + 1))
      compare "$n-publish-verbose" -- publish --project "$f" --target-connection "{NEWDB:$tpl:$n}" --verbose || failures=$((failures + 1))
      compare "$n-execute" -- publish --project "$f" --target-connection "{NEWDB:$tpl:$n}" --execute || failures=$((failures + 1))
      compare "$n-execute-comments" -- publish --project "$f" --target-connection "{NEWDB:$tpl:$n}" --execute --comments-only || failures=$((failures + 1))
      compare "$n-publish-comments" -- publish --project "$f" --target-connection "{NEWDB:$tpl:$n}" --comments-only --output "{OUT}/c.sql" || failures=$((failures + 1))
    done
    # A tenant template with a dropped table (refused, then allowed), one shop only, an unknown shop, and a shop-only dry run.
    tdrop="$inputs/tenant-drop"
    mkdir -p "$tdrop"
    cp -r "$ROOT/samples/SchemaPerTenant/." "$tdrop/"
    rm -f "$tdrop/sql/Tenant/Table/customer_groups.sql"
    compare "tenant-drop-refused" -- publish --project "$tdrop/pgproj.json" --target-connection "{NEWDB:pp_pt:tdrop}" --execute || failures=$((failures + 1))
    compare "tenant-drop-allowed" -- publish --project "$tdrop/pgproj.json" --target-connection "{NEWDB:pp_pt:tdrop}" --execute --allow-destructive || failures=$((failures + 1))
    compare "tenant-drop-plan" -- publish --project "$tdrop/pgproj.json" --target-connection "{NEWDB:pp_pt:tdrop}" || failures=$((failures + 1))
    compare "tenant-only" -- publish --project "$tchanged/pgproj.json" --target-connection "{NEWDB:pp_pt:tonly}" --execute --tenants acme || failures=$((failures + 1))
    compare "tenant-only-plan" -- publish --project "$tchanged/pgproj.json" --target-connection "{NEWDB:pp_pt:tonly}" --tenants acme,globex || failures=$((failures + 1))
    compare "tenant-unknown" -- publish --project "$tchanged/pgproj.json" --target-connection "{NEWDB:pp_pt:tonly}" --tenants nobody || failures=$((failures + 1))
    compare "tenant-verbose" -- publish --project "$tchanged/pgproj.json" --target-connection "{NEWDB:pp_pt:tonly}" --verbose --execute || failures=$((failures + 1))
    # A project with tenants against an empty database: the tenants are new (their schemas are created).
    compare "tenant-new-plan" -- publish --project "$pt" --target-connection "{NEWDB:pp_empty:tnew}" || failures=$((failures + 1))
    compare "tenant-new-execute" -- publish --project "$pt" --target-connection "{NEWDB:pp_empty:tnew}" --execute --allow-destructive || failures=$((failures + 1))

    # The small project: each kind of warning, refusal, and failure.
    for c in sm_index sm_notnull sm_comments sm_drop sm_fail sm_role sm_event sm_script; do
      compare "$c-plan" -- publish --project "$inputs/$c/pgproj.json" --target-connection "{NEWDB:pp_small:$c}" || failures=$((failures + 1))
      compare "$c-plan-verbose" -- publish --project "$inputs/$c/pgproj.json" --target-connection "{NEWDB:pp_small:$c}" --verbose --output "{OUT}/d.sql" || failures=$((failures + 1))
      compare "$c-execute" -- publish --project "$inputs/$c/pgproj.json" --target-connection "{NEWDB:pp_small:$c}" --execute || failures=$((failures + 1))
      compare "$c-execute-allowed" -- publish --project "$inputs/$c/pgproj.json" --target-connection "{NEWDB:pp_small:$c}" --execute --allow-destructive --allow-superuser-statements || failures=$((failures + 1))
    done
    compare "sm_index-concurrently-plan" -- publish --project "$inputs/sm_index/pgproj.json" --target-connection "{NEWDB:pp_small:cc}" --concurrently || failures=$((failures + 1))
    compare "sm_index-concurrently-output" -- publish --project "$inputs/sm_index/pgproj.json" --target-connection "{NEWDB:pp_small:cc}" --concurrently --output "{OUT}/d.sql" --quiet || failures=$((failures + 1))
    compare "sm_index-concurrently-execute" -- publish --project "$inputs/sm_index/pgproj.json" --target-connection "{NEWDB:pp_small:cc}" --concurrently --execute || failures=$((failures + 1))
    compare "sm_index-concurrently-verbose" -- publish --project "$inputs/sm_index/pgproj.json" --target-connection "{NEWDB:pp_small:cc2}" --concurrently --execute --lock-timeout 10s --verbose --output "{OUT}/d.sql" || failures=$((failures + 1))
    compare "sm_comments-comments-execute" -- publish --project "$inputs/sm_comments/pgproj.json" --target-connection "{NEWDB:pp_small:cm}" --comments-only --execute || failures=$((failures + 1))
    compare "sm_comments-comments-plan" -- publish --project "$inputs/sm_comments/pgproj.json" --target-connection "{NEWDB:pp_small:cm}" --comments-only || failures=$((failures + 1))
    compare "sm_notnull-timeout-execute" -- publish --project "$inputs/sm_notnull/pgproj.json" --target-connection "{NEWDB:pp_small:to}" --execute --lock-timeout 5s || failures=$((failures + 1))
    # A failing statement in the middle of a script: what the error says.
    compare "sm_fail-failed" -- publish --project "$inputs/sm_fail/pgproj.json" --target-connection "{NEWDB:pp_small:ff}" --execute || failures=$((failures + 1))
    # A lock wait that times out: another session holds the table.
    # Source mode: a database as the desired state.
    compare "source-plan" -- publish --source-connection "{NEWDB:pp_ms:srca}" --target-connection "{NEWDB:pp_empty:srcb}" --project "$inputs/none/pgproj.json" || failures=$((failures + 1))
    compare "source-execute" -- publish --source-connection "{NEWDB:pp_ms:srca}" --target-connection "{NEWDB:pp_empty:srcb}" --project "$inputs/none/pgproj.json" --execute --exclude-owners || failures=$((failures + 1))
    compare "source-verbose" -- publish --source-connection "{NEWDB:pp_small:srca}" --target-connection "{NEWDB:pp_empty:srcb}" --project "$inputs/none/pgproj.json" --verbose --dry-run || failures=$((failures + 1))
    compare "source-same" -- publish --source-connection "{DB:srca}" --target-connection "{DB:srca}" --project "$inputs/none/pgproj.json" || failures=$((failures + 1))
    compare "source-tenants" -- publish --source-connection "{DB:srca}" --target-connection "{DB:srcb}" --project "$pt" || failures=$((failures + 1))

    # Errors: options that cannot be combined, a bad lock timeout, tenants on a project without, a missing project, a project with errors or without a name, no target,
    # a malformed target, an unreachable one.
    compare "err-conc-comments" -- publish --project "$inputs/small/pgproj.json" --target-connection "{DB:err}" --concurrently --comments-only || failures=$((failures + 1))
    compare "err-timeout" -- publish --project "$inputs/small/pgproj.json" --target-connection "{DB:err}" --lock-timeout soon || failures=$((failures + 1))
    compare "err-timeout-missing-project" -- publish --project "$inputs/none/pgproj.json" --lock-timeout soon --verbose || failures=$((failures + 1))
    compare "err-tenants" -- publish --project "$inputs/small/pgproj.json" --target-connection "{NEWDB:pp_small:e1}" --tenants acme || failures=$((failures + 1))
    compare "err-notfound" -- publish --project "$inputs/none/pgproj.json" --target-connection "{DB:err}" || failures=$((failures + 1))
    compare "err-broken" -- publish --project "$broken/pgproj.json" --target-connection "{DB:err}" --erd "{OUT}/erd" || failures=$((failures + 1))
    compare "err-nameless" -- publish --project "$inputs/nameless/pgproj.json" --target-connection "{DB:err}" || failures=$((failures + 1))
    compare "err-notarget" -- publish --project "$inputs/small/pgproj.json" || failures=$((failures + 1))
    compare "err-badtarget" -- publish --project "$inputs/small/pgproj.json" --target-connection "Host=localhost;Port=notaport" || failures=$((failures + 1))
    compare "err-unreachable" -- publish --project "$inputs/small/pgproj.json" --target-connection "Host=127.0.0.1;Port=1;Database=x;Timeout=3" --execute || failures=$((failures + 1))
    ;;
  init)
    # init against a throwaway server (G-309d): PARITY_PG is a connection string without a database, PARITY_CONTAINER the container's name. init only reads, so both
    # sides read the same databases: pp_init_ms and pp_init_pt (the samples, published by the baseline). Each side writes into folders of its own, compared file by file.
    : "${PARITY_PG:?PARITY_PG: a connection string to a throwaway server, without a database}"
    unset PGPROJ_TARGET_CONNECTION PGPROJ_SOURCE_CONNECTION
    MASK_TREE=1
    ms="$ROOT/samples/MultiSchema/pgproj.json"
    pt="$ROOT/samples/SchemaPerTenant/pgproj.json"
    for d in pp_init_ms pp_init_pt pp_init_empty; do psql_run -d postgres -c "DROP DATABASE IF EXISTS $d WITH (FORCE)" >/dev/null; psql_run -d postgres -c "CREATE DATABASE $d" >/dev/null; done
    dotnet "$BASELINE/pgproj.dll" publish --project "$ms" --target-connection "$PARITY_PG;Database=pp_init_ms" --execute --allow-destructive >/dev/null 2>&1
    dotnet "$BASELINE/pgproj.dll" publish --project "$pt" --target-connection "$PARITY_PG;Database=pp_init_pt" --execute --allow-destructive >/dev/null 2>&1
    # A base type, an INVALID index, and mixed internal trigger states, for init's warnings.
    psql_run -d pp_init_ms -c "CREATE TABLE sales.pp_mixed (id int PRIMARY KEY, parent int REFERENCES sales.pp_mixed (id)); ALTER TABLE sales.pp_mixed DISABLE TRIGGER ALL; ALTER TABLE sales.pp_mixed ENABLE TRIGGER USER;" >/dev/null 2>&1
    msdb="$PARITY_PG;Database=pp_init_ms"
    ptdb="$PARITY_PG;Database=pp_init_pt"
    existing="$OUT/inputs/existing"
    mkdir -p "$existing/sql"
    printf '{}\n' >"$existing/pgproj.json"
    printf 'x\n' >"$existing/sql/x.sql"

    compare "ms-init" -- init --project "{OUT}/p/pgproj.json" --source-connection "$msdb" || failures=$((failures + 1))
    compare "ms-init-verbose" -- init --project "{OUT}/p/pgproj.json" --source-connection "$msdb" --verbose || failures=$((failures + 1))
    compare "ms-init-quiet" -- init --project "{OUT}/p/pgproj.json" --source-connection "$msdb" --quiet || failures=$((failures + 1))
    compare "ms-init-report" -- init --project "{OUT}/p/pgproj.json" --source-connection "$msdb" --report || failures=$((failures + 1))
    compare "ms-init-schemas" -- init --project "{OUT}/p/pgproj.json" --source-connection "$msdb" --schemas sales,catalog --report --name shop || failures=$((failures + 1))
    compare "ms-init-exclude" -- init --project "{OUT}/p/pgproj.json" --output ../schema --source-connection "$msdb" --exclude-schemas reporting --exclude-grants --exclude-owners --exclude-role-memberships --report || failures=$((failures + 1))
    compare "ms-init-erd" -- init --project "{OUT}/p/pgproj.json" --source-connection "$msdb" --report --erd --erd-html --erd-schemas sales || failures=$((failures + 1))
    compare "ms-init-erd-path" -- init --project "{OUT}/p/pgproj.json" --source-connection "$msdb" --report --erd "{OUT}/diagram" --verbose || failures=$((failures + 1))
    compare "empty-init" -- init --project "{OUT}/p/pgproj.json" --source-connection "$PARITY_PG;Database=pp_init_empty" --report || failures=$((failures + 1))
    compare "pt-init-tenants" -- init --project "{OUT}/p/pgproj.json" --source-connection "$ptdb" --tenant-prefix shop || failures=$((failures + 1))
    compare "pt-init-tenant-file" -- init --project "{OUT}/p/pgproj.json" --source-connection "$ptdb" --tenant-prefix SHOP --tenant-file --report --verbose || failures=$((failures + 1))
    compare "pt-init-tenant-from" -- init --project "{OUT}/p/pgproj.json" --source-connection "$ptdb" --tenant-prefix shop --tenant-from globex --tenant-query "SELECT 'acme'" --report --erd || failures=$((failures + 1))
    compare "pt-init-shared" -- init --project "{OUT}/p/pgproj.json" --source-connection "$ptdb" --schemas platform --report || failures=$((failures + 1))
    # Errors: a project or folder already there, schemas the database lacks, a tenant prefix with no tenant, a tenant schema named in --schemas, a diagram schema not
    # scaffolded, no connection, a malformed one, an unreachable one.
    compare "err-exists" -- init --project "$existing/pgproj.json" --source-connection "$msdb" || failures=$((failures + 1))
    compare "err-folder" -- init --project "{OUT}/p/pgproj.json" --output "$existing/sql" --source-connection "$msdb" || failures=$((failures + 1))
    compare "err-schemas" -- init --project "{OUT}/p/pgproj.json" --source-connection "$msdb" --schemas nope,sales || failures=$((failures + 1))
    compare "err-exclude-all" -- init --project "{OUT}/p/pgproj.json" --source-connection "$PARITY_PG;Database=pp_init_empty" --exclude-schemas public || failures=$((failures + 1))
    compare "err-no-tenant" -- init --project "{OUT}/p/pgproj.json" --source-connection "$msdb" --tenant-prefix shop || failures=$((failures + 1))
    compare "err-tenant-from" -- init --project "{OUT}/p/pgproj.json" --source-connection "$ptdb" --tenant-prefix shop --tenant-from nobody || failures=$((failures + 1))
    compare "err-tenant-schemas" -- init --project "{OUT}/p/pgproj.json" --source-connection "$ptdb" --tenant-prefix shop --schemas shop_acme || failures=$((failures + 1))
    compare "err-erd-schemas" -- init --project "{OUT}/p/pgproj.json" --source-connection "$msdb" --erd --erd-schemas nope || failures=$((failures + 1))
    compare "err-noconnection" -- init --project "{OUT}/p/pgproj.json" || failures=$((failures + 1))
    compare "err-badconnection" -- init --project "{OUT}/p/pgproj.json" --source-connection "Host=localhost;Port=notaport" || failures=$((failures + 1))
    compare "err-unreachable" -- init --project "{OUT}/p/pgproj.json" --source-connection "Host=127.0.0.1;Port=1;Database=x;Timeout=3" || failures=$((failures + 1))
    ;;
  document)
    # document (G-309d): the samples' projects without a server, and with PARITY_PG a database (the multi-schema sample, published by the baseline).
    unset PGPROJ_TARGET_CONNECTION PGPROJ_SOURCE_CONNECTION
    MASK_TREE=1
    ms="$ROOT/samples/MultiSchema/pgproj.json"
    pt="$ROOT/samples/SchemaPerTenant/pgproj.json"
    inputs="$OUT/inputs"
    mkdir -p "$inputs/tags/sql" "$inputs/broken/sql"
    printf '{"name":"tags","version":"1","source":"./sql"}\n' >"$inputs/tags/pgproj.json"
    printf "CREATE TABLE app.t (id int PRIMARY KEY, a int, CONSTRAINT t_ck CHECK (id > 0));\nCOMMENT ON COLUMN app.t.a IS '@retention-months: soon';\nCOMMENT ON CONSTRAINT t_ck ON app.t IS '@pii: true';\n" >"$inputs/tags/sql/t.sql"
    printf '{"name":"broken","version":"1","source":"./sql"}\n' >"$inputs/broken/pgproj.json"
    printf 'CREATE TABLE b (a int, ;\n' >"$inputs/broken/sql/broken.sql"
    for p in "ms|$ms" "tenant|$pt"; do
      IFS="|" read -r n f <<<"$p"
      compare "$n-document" -- document --project "$f" --output "{OUT}/docs" || failures=$((failures + 1))
      compare "$n-document-all" -- document --project "$f" --output "{OUT}/docs" --format markdown,json --format HTML || failures=$((failures + 1))
      compare "$n-document-verbose" -- document --project "$f" --output "{OUT}/docs" --verbose --format md || failures=$((failures + 1))
      compare "$n-document-quiet" -- document --project "$f" --output "{OUT}/docs" --quiet || failures=$((failures + 1))
    done
    compare "ms-document-schemas" -- document --project "$ms" --output "{OUT}/docs" --schemas sales,catalog --format markdown || failures=$((failures + 1))
    compare "ms-document-exclude" -- document --project "$ms" --output "{OUT}/docs" --exclude-schemas sales --format json || failures=$((failures + 1))
    compare "tags-document" -- document --project "$inputs/tags/pgproj.json" --output "{OUT}/docs" --format json || failures=$((failures + 1))
    compare "tags-document-nofail" -- document --project "$inputs/tags/pgproj.json" --output "{OUT}/docs" --no-fail-on-doc-errors --verbose || failures=$((failures + 1))
    compare "err-broken" -- document --project "$inputs/broken/pgproj.json" --output "{OUT}/docs" || failures=$((failures + 1))
    compare "err-notfound" -- document --project "$inputs/none/pgproj.json" --output "{OUT}/docs" || failures=$((failures + 1))
    compare "err-schemas" -- document --project "$ms" --output "{OUT}/docs" --schemas nope || failures=$((failures + 1))
    compare "err-exclude-schemas" -- document --project "$ms" --output "{OUT}/docs" --exclude-schemas nope,other || failures=$((failures + 1))
    compare "err-badconnection" -- document --source-connection "Host=localhost;Port=notaport" --output "{OUT}/docs" || failures=$((failures + 1))
    compare "err-unreachable" -- document --source-connection "Host=127.0.0.1;Port=1;Database=x;Timeout=3" --output "{OUT}/docs" || failures=$((failures + 1))
    if [ -n "${PARITY_PG:-}" ]; then
      psql_run -d postgres -c "DROP DATABASE IF EXISTS pp_doc_ms WITH (FORCE)" >/dev/null
      psql_run -d postgres -c "CREATE DATABASE pp_doc_ms" >/dev/null
      dotnet "$BASELINE/pgproj.dll" publish --project "$ms" --target-connection "$PARITY_PG;Database=pp_doc_ms" --execute --allow-destructive >/dev/null 2>&1
      db="$PARITY_PG;Database=pp_doc_ms"
      compare "db-document" -- document --source-connection "$db" --output "{OUT}/docs" || failures=$((failures + 1))
      compare "db-document-all" -- document --source-connection "$db" --output "{OUT}/docs" --format html,markdown,json --verbose || failures=$((failures + 1))
      compare "db-document-schemas" -- document --source-connection "$db" --output "{OUT}/docs" --schemas sales --quiet || failures=$((failures + 1))
      compare "db-document-badschema" -- document --source-connection "$db" --output "{OUT}/docs" --schemas nope || failures=$((failures + 1))
    else
      echo "(no PARITY_PG: the database cases were not run)"
    fi
    ;;
  *)
    echo "unknown mode $mode"
    exit 2
    ;;
esac

echo "---"
if [ "$failures" -eq 0 ]; then
  echo "parity: every case matched ($OUT)"
else
  echo "parity: $failures case(s) differ (see $OUT)"
fi
[ "$failures" -eq 0 ]
