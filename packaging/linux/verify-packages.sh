#!/usr/bin/env bash
# Installs each Linux package of one runtime in a clean container of that architecture and runs the smoke test (G-81), which also connects
# to a PostgreSQL started for the run, so a system library missing for a connection (Kerberos, G-324) shows.
# Usage: packaging/linux/verify-packages.sh <dist dir> <version> <rid>   (rid: linux-x64 or linux-arm64)
# Needs Docker; linux-arm64 on an x64 machine needs QEMU (docker/setup-qemu-action, or tonistiigi/binfmt).
set -euo pipefail

dist="$(cd "${1:?usage: verify-packages.sh <dist dir> <version> <rid>}" && pwd)"
version="${2:?}"
rid="${3:?}"
packaging="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

case "$rid" in
    linux-x64)   platform=linux/amd64; deb_arch=amd64; rpm_arch=x86_64 ;;
    linux-arm64) platform=linux/arm64; deb_arch=arm64; rpm_arch=aarch64 ;;
    *) echo "unsupported runtime identifier: $rid" >&2; exit 1 ;;
esac

# A database for the smoke test to connect to, on a network of its own; removed when the script ends.
network="pgproj-verify-$$"
database="pgproj-verify-db-$$"
cleanup() {
    docker rm -f "$database" >/dev/null 2>&1 || true
    docker network rm "$network" >/dev/null 2>&1 || true
}
trap cleanup EXIT
docker network create "$network" >/dev/null
docker run -d --name "$database" --network "$network" -e POSTGRES_PASSWORD=postgres postgres:18 >/dev/null
for _ in $(seq 1 30); do
    docker exec "$database" pg_isready -U postgres >/dev/null 2>&1 && break
    sleep 1
done
connection="Host=$database;Port=5432;Database=postgres;Username=postgres;Password=postgres"

run() {
    MSYS_NO_PATHCONV=1 docker run --rm --platform "$platform" --network "$network" -e PGPROJ_SMOKE_CONNECTION="$connection" \
        -v "$dist:/dist:ro" -v "$packaging:/packaging:ro" "$@"
}

echo "== $rid: the archive, on Ubuntu 24.04 with ICU and OpenSSL only (no Kerberos: pgproj must connect without it, quietly)"
run ubuntu:24.04 bash -c "
    apt-get update -qq && apt-get install -y -qq libicu74 libssl3t64 >/dev/null &&
    tar -xzf /dist/pgproj-$version-$rid.tar.gz -C /tmp &&
    /packaging/smoke-test.sh /tmp/pgproj-$version-$rid/pgproj $version"

echo "== $rid: the .deb, on Debian 12 (its dependencies must resolve from the distribution)"
run debian:12 bash -c "
    apt-get update -qq && apt-get install -y -qq /dist/pgproj_*_$deb_arch.deb >/dev/null &&
    /packaging/smoke-test.sh pgproj $version"

echo "== $rid: the .rpm, on Fedora"
run fedora:latest bash -c "
    dnf install -y -q /dist/pgproj-*.$rpm_arch.rpm &&
    /packaging/smoke-test.sh pgproj $version"
