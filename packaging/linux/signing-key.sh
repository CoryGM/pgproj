#!/usr/bin/env bash
# Prepares the key that signs the Linux packages and repositories (G-331) in <signing dir>: key.asc (the armored secret key) and passphrase (its
# passphrase, empty for a key without one). The other scripts take that directory and import the key into each container they sign in.
#
# With LINUX_SIGNING_KEY (and LINUX_SIGNING_PASSPHRASE) in the environment, the release key is written there. Without them a throwaway key is made,
# valid for a day, so a dry run or a fork exercises every signing step; nothing it signs is published (release.yml publishes only with the real key).
# Prints "release" or "throwaway", then the key's fingerprint.
#
# Usage: packaging/linux/signing-key.sh <signing dir>
set -euo pipefail

dir="${1:?usage: signing-key.sh <signing dir>}"
mkdir -p "$dir"
dir="$(cd "$dir" && pwd)"
umask 077
# Docker on Windows (Git Bash) needs a Windows path for a mount; elsewhere the path is used as it is.
host() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi; }

if [[ -n "${LINUX_SIGNING_KEY:-}" ]]; then
    printf '%s\n' "$LINUX_SIGNING_KEY" > "$dir/key.asc"
    printf '%s' "${LINUX_SIGNING_PASSPHRASE:-}" > "$dir/passphrase"
    kind=release
else
    : > "$dir/passphrase"
    MSYS_NO_PATHCONV=1 docker run --rm -v "$(host "$dir"):/signing" debian:12 bash -c '
        set -euo pipefail
        apt-get update -qq && apt-get install -y -qq gnupg >/dev/null
        gpg --batch --quiet --gen-key <<EOF
%no-protection
Key-Type: RSA
Key-Length: 3072
Key-Usage: sign
Name-Real: pgproj throwaway test key
Expire-Date: 1d
%commit
EOF
        gpg --batch --armor --export-secret-keys > /signing/key.asc'
    kind=throwaway
fi

fingerprint="$(MSYS_NO_PATHCONV=1 docker run --rm -v "$(host "$dir"):/signing:ro" debian:12 bash -c '
    apt-get update -qq && apt-get install -y -qq gnupg >/dev/null
    gpg --batch --quiet --import /signing/key.asc 2>/dev/null
    gpg --batch --with-colons --list-secret-keys | awk -F: "/^fpr/ { print \$10; exit }"')"
[[ -n "$fingerprint" ]] || { echo "no secret key could be read from the signing key" >&2; exit 1; }
echo "$kind"
echo "$fingerprint"
