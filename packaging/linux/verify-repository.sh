#!/usr/bin/env bash
# Installs pgproj from the repositories in <site dir> (packaging/linux/build-repository.sh), the way a user does, in clean containers of one
# architecture, and runs the smoke test (G-331): apt on Debian 12 and Ubuntu 24.04, and dnf on Fedora, each checking the signatures (apt the
# signed Release; dnf the signed metadata and packages). The site is served over HTTP from another container for the run.
#
# Usage: packaging/linux/verify-repository.sh <site dir> <channel> <version> <rid>   (channel: stable or testing; rid: linux-x64 or linux-arm64)
# Needs Docker; linux-arm64 on an x64 machine needs QEMU (docker/setup-qemu-action, or tonistiigi/binfmt).
set -euo pipefail

site="$(cd "${1:?usage: verify-repository.sh <site dir> <channel> <version> <rid>}" && pwd)"
channel="${2:?}"
version="${3:?}"
rid="${4:?}"
packaging="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# Docker on Windows (Git Bash) needs a Windows path for a mount; elsewhere the path is used as it is.
host() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi; }

case "$rid" in
    linux-x64)   platform=linux/amd64 ;;
    linux-arm64) platform=linux/arm64 ;;
    *) echo "unsupported runtime identifier: $rid" >&2; exit 1 ;;
esac

network="pgproj-repository-$$"
server="pgproj-repository-site-$$"
cleanup() {
    docker rm -f "$server" >/dev/null 2>&1 || true
    docker network rm "$network" >/dev/null 2>&1 || true
}
trap cleanup EXIT
docker network create "$network" >/dev/null
MSYS_NO_PATHCONV=1 docker run -d --name "$server" --network "$network" -v "$(host "$site"):/site:ro" -w /site python:3-alpine \
    python -m http.server 8000 >/dev/null
url="http://$server:8000"
for _ in $(seq 1 30); do
    docker exec "$server" wget -q -O /dev/null http://localhost:8000/pgproj.asc 2>/dev/null && break
    sleep 1
done

run() {
    MSYS_NO_PATHCONV=1 docker run --rm --platform "$platform" --network "$network" -v "$(host "$packaging"):/packaging:ro" "$@"
}

for image in debian:12 ubuntu:24.04; do
    echo "== $rid: apt install from the $channel channel, on $image"
    run "$image" bash -c "
        set -euo pipefail
        apt-get update -qq && apt-get install -y -qq curl ca-certificates >/dev/null
        install -d -m 0755 /etc/apt/keyrings
        curl -fsSL $url/pgproj.asc -o /etc/apt/keyrings/pgproj.asc
        echo 'deb [signed-by=/etc/apt/keyrings/pgproj.asc] $url/apt $channel main' > /etc/apt/sources.list.d/pgproj.list
        apt-get update -qq
        apt-get install -y -qq pgproj >/dev/null
        /packaging/smoke-test.sh pgproj $version"
done

echo "== $rid: dnf install from the $channel channel, on Fedora"
repo_file="pgproj.repo"
[[ "$channel" == testing ]] && repo_file="pgproj-testing.repo"
run fedora:latest bash -c "
    set -euo pipefail
    # The site's repository file names the address it is published at; here it is served from the test container instead.
    curl -fsSL $url/$repo_file | sed 's|^baseurl=.*/rpm/|baseurl=$url/rpm/|; s|^gpgkey=.*|gpgkey=$url/pgproj.asc|' > /etc/yum.repos.d/pgproj.repo
    grep -q '^gpgcheck=1' /etc/yum.repos.d/pgproj.repo && grep -q '^repo_gpgcheck=1' /etc/yum.repos.d/pgproj.repo
    dnf install -y -q pgproj
    /packaging/smoke-test.sh pgproj $version"
