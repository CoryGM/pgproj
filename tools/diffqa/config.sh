# Settings for the differential QA harness; each can be overridden from the environment.
here=${here:-$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)}
repo=$(cd "$here/../.." && pwd)
: "${DIFFQA_VERSION:=18}"                                  # PostgreSQL major version under test
: "${DIFFQA_CONTAINER:=diffqa$DIFFQA_VERSION}"             # docker container running postgres:$DIFFQA_VERSION-alpine
: "${DIFFQA_PORT:=$((55480 + DIFFQA_VERSION))}"            # its published port (55498 for 18); never 55432
: "${DIFFQA_PASSWORD:=qa}"
: "${DIFFQA_BRANCH:=REL_${DIFFQA_VERSION}_STABLE}"         # postgres/postgres branch the regression SQL comes from
: "${DIFFQA_REGRESS:=$here/regress/$DIFFQA_BRANCH}"
: "${DIFFQA_WORK:=$here/work}"
: "${DIFFQA_LABEL:=pg$DIFFQA_VERSION}"
: "${DIFFQA_TIMEOUT:=600}"
if [ -z "${DIFFQA_PGPROJ:-}" ]; then
  DIFFQA_PGPROJ="$repo/src/PgProj.Cli/bin/Debug/net10.0/pgproj"
  [ -x "$DIFFQA_PGPROJ.exe" ] && DIFFQA_PGPROJ="$DIFFQA_PGPROJ.exe"
fi
export DIFFQA_VERSION DIFFQA_CONTAINER DIFFQA_PORT DIFFQA_PASSWORD DIFFQA_BRANCH DIFFQA_REGRESS DIFFQA_WORK DIFFQA_LABEL DIFFQA_TIMEOUT DIFFQA_PGPROJ
