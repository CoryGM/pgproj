#!/usr/bin/env bash
# Smoke test of an installed or unpacked pgproj (G-81): it starts, reports a version, and parses SQL through the native
# libpg_query library. With PGPROJ_SMOKE_CONNECTION set to a PostgreSQL connection string, it also compares the smoke project with
# that database, and fails if the command fails or prints "Cannot load library" (G-324: a missing system library, such as Kerberos).
# Usage: packaging/smoke-test.sh <path to the pgproj executable> [expected version]
set -euo pipefail

pgproj="${1:?usage: smoke-test.sh <pgproj executable> [expected version]}"
expected="${2:-}"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

version="$("$pgproj" --version)"
echo "pgproj --version: $version"
if [[ -n "$expected" && "$version" != "$expected"* ]]; then
    echo "expected a version starting with $expected" >&2
    exit 1
fi

output="$("$pgproj" build --project "$here/smoke/pgproj.json")"
echo "$output"
if grep -qiE 'error|unable to load' <<<"$output"; then
    echo "build reported a problem" >&2
    exit 1
fi

if [[ -n "${PGPROJ_SMOKE_CONNECTION:-}" ]]; then
    if ! output="$("$pgproj" diff --project "$here/smoke/pgproj.json" --target-connection "$PGPROJ_SMOKE_CONNECTION" --quiet 2>&1)"; then
        echo "$output"
        echo "diff against the database failed" >&2
        exit 1
    fi
    echo "$output"
    if grep -qi 'cannot load library' <<<"$output"; then
        echo "connecting to the database printed a library that could not be loaded" >&2
        exit 1
    fi
    echo "diff against the database: no missing library"
fi
echo "smoke test passed"
