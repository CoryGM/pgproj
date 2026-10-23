#!/usr/bin/env bash
# Prints the CHANGELOG.md section of one version, for the GitHub release notes (G-81).
# Usage: packaging/release-notes.sh <version>   (no leading v). A pre-release (0.1.0-rc.1) with no section of its own prints the
# [Unreleased] section, which is what it is a candidate of. Prints nothing when there is neither.
set -euo pipefail

version="${1:?usage: release-notes.sh <version>}"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

section() {
    awk -v version="$1" '
        /^## \[/ { if (printing) exit; printing = (index($0, "## [" version "]") == 1); next }
        printing { print }
    ' "$root/CHANGELOG.md"
}

notes="$(section "$version")"
if [[ -z "$notes" && "$version" == *-* ]]; then
    notes="$(section Unreleased)"
fi
if [[ -n "$notes" ]]; then
    printf '%s
' "$notes"
fi
