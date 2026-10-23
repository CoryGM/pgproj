#!/usr/bin/env bash
# Signs every .rpm in <dir> with the key in <signing dir> (G-331; packaging/linux/signing-key.sh makes it) and checks each signature, in a Fedora
# container with rpm-sign. A package that is already signed is signed again. The .deb packages are not signed themselves: apt checks the signed
# repository they come from instead (packaging/linux/build-repository.sh).
#
# Usage: packaging/linux/sign-rpms.sh <dir> <signing dir>
set -euo pipefail

dir="$(cd "${1:?usage: sign-rpms.sh <dir> <signing dir>}" && pwd)"
signing="$(cd "${2:?usage: sign-rpms.sh <dir> <signing dir>}" && pwd)"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# Docker on Windows (Git Bash) needs a Windows path for a mount; elsewhere the path is used as it is.
host() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi; }

MSYS_NO_PATHCONV=1 docker run --rm -v "$(host "$dir"):/rpms" -v "$(host "$signing"):/signing:ro" -v "$(host "$here"):/scripts:ro" fedora:latest bash -c '
    set -euo pipefail
    shopt -s nullglob
    rpms=(/rpms/*.rpm)
    [[ ${#rpms[@]} -gt 0 ]] || { echo "no .rpm in the directory" >&2; exit 1; }
    dnf install -y -q rpm-sign gnupg2 >/dev/null
    . /scripts/import-signing-key.sh
    rpmsign --define "_gpg_name $fingerprint" \
        --define "_gpg_sign_cmd_extra_args --pinentry-mode loopback --passphrase-file /signing/passphrase" \
        --addsign "${rpms[@]}"
    gpg --batch --armor --export "$fingerprint" > /tmp/key.asc
    rpm --import /tmp/key.asc
    for rpm in "${rpms[@]}"; do
        rpm --checksig "$rpm"
        rpm --checksig "$rpm" | grep -q "signatures OK" || { echo "$rpm: the signature does not check" >&2; exit 1; }
    done'
