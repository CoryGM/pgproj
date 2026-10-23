#!/usr/bin/env bash
# Writes <file>.asc, an armored detached signature of <file> by the key in <signing dir> (G-331; packaging/linux/signing-key.sh makes it), and
# checks it. release.yml signs SHA256SUMS.txt with it, which covers every file of a release.
#
# Usage: packaging/linux/sign-file.sh <file> <signing dir>
set -euo pipefail

file="${1:?usage: sign-file.sh <file> <signing dir>}"
signing="$(cd "${2:?usage: sign-file.sh <file> <signing dir>}" && pwd)"
dir="$(cd "$(dirname "$file")" && pwd)"
name="$(basename "$file")"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# Docker on Windows (Git Bash) needs a Windows path for a mount; elsewhere the path is used as it is.
host() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi; }

MSYS_NO_PATHCONV=1 docker run --rm -v "$(host "$dir"):/files" -v "$(host "$signing"):/signing:ro" -v "$(host "$here"):/scripts:ro" \
    -e NAME="$name" debian:12 bash -c '
    set -euo pipefail
    apt-get update -qq && apt-get install -y -qq gnupg >/dev/null
    . /scripts/import-signing-key.sh
    "${gpg_sign[@]}" --armor --detach-sign -o "/files/$NAME.asc" "/files/$NAME"
    gpg --batch --verify "/files/$NAME.asc" "/files/$NAME"'
