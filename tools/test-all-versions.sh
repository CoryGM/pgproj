#!/usr/bin/env bash
# Runs the test suite against every supported PostgreSQL version (G-269, the user's decision of 2026-10-13): the unit tests once, then the Docker-backed
# integration suite once per version, in parallel, at most three at a time, each with a timeout, and a summary line per version at the end.
#
#   tools/test-all-versions.sh                 # build, then 14 15 16 17 18, three at a time
#   tools/test-all-versions.sh 15 18           # only the versions named
#   MAX_PARALLEL=2 TIMEOUT_MINUTES=45 tools/test-all-versions.sh
#   NO_BUILD=1 tools/test-all-versions.sh      # skip the build (the test DLLs are shared, so never rebuild while a run is going)
#
# Logs are written to artifacts/test-runs/<UTC time>/pg<version>.log. The exit code is 0 only when every run passed.
set -uo pipefail

repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
cd "$repo"
versions=("$@")
[ ${#versions[@]} -eq 0 ] && versions=(14 15 16 17 18)
max_parallel=${MAX_PARALLEL:-3}
timeout_minutes=${TIMEOUT_MINUTES:-40}
logs="artifacts/test-runs/$(date -u +%Y-%m-%d-%H-%M-%S)"
mkdir -p "$logs"

if [ -z "${NO_BUILD:-}" ]; then
  echo "Building..."
  dotnet build --nologo -v quiet > "$logs/build.log" 2>&1 || { echo "Build failed; see $logs/build.log"; exit 1; }
fi

# The unit tests do not depend on the server, so they run once.
unit_failed=0
for project in src/*.Tests; do
  [ "$(basename "$project")" = "PgProj.Integration.Tests" ] && continue
  dotnet test "$project" --no-build --nologo >> "$logs/unit.log" 2>&1 || unit_failed=1
done
echo "Unit tests: $(grep -cE '^Passed!' "$logs/unit.log") project(s) passed, $(grep -cE '^Failed!' "$logs/unit.log") failed (log: $logs/unit.log)"

run_version() {
  local version=$1
  local log="$logs/pg$version.log"
  PGPROJ_TEST_POSTGRES_IMAGE="postgres:$version-alpine" timeout "${timeout_minutes}m" \
    dotnet test src/PgProj.Integration.Tests --no-build --nologo > "$log" 2>&1
  echo $? > "$logs/pg$version.exit"
}

echo "Integration tests: ${versions[*]}, at most $max_parallel at a time, $timeout_minutes minutes each..."
for version in "${versions[@]}"; do
  while [ "$(jobs -rp | wc -l)" -ge "$max_parallel" ]; do
    wait -n
  done
  echo "  started PostgreSQL $version"
  run_version "$version" &
done
wait

status=$unit_failed
echo
for version in "${versions[@]}"; do
  code=$(cat "$logs/pg$version.exit" 2>/dev/null || echo 1)
  summary=$(grep -E '^(Passed|Failed)!' "$logs/pg$version.log" | sed -E 's/ - PgProj.*//; s/,? *Duration:.*//')
  if [ "$code" = 124 ]; then
    summary="TIMED OUT after $timeout_minutes minutes"
  fi
  [ "$code" = 0 ] || status=1
  printf 'PostgreSQL %-3s %s  (%s)\n' "$version" "${summary:-no result}" "$logs/pg$version.log"
done
exit $status
