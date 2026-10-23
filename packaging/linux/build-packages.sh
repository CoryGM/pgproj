#!/usr/bin/env bash
# Builds every Linux artifact of one runtime (G-81): pgproj-<version>-<rid>.tar.gz, pgproj_<version>_<arch>.deb, and
# pgproj-<version>.<arch>.rpm, all in <output dir>.
# Usage: packaging/linux/build-packages.sh <rid> <version> <output dir>   (rid: linux-x64 or linux-arm64)
# nfpm is taken from the PATH, or else run from its container image (Docker).
set -euo pipefail

rid="${1:?usage: build-packages.sh <rid> <version> <output dir>}"
version="${2:?}"
out="${3:?}"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
nfpm_image="${NFPM_IMAGE:-ghcr.io/goreleaser/nfpm:v2.47.0}"

case "$rid" in
    linux-x64)   arch=amd64 ;;
    linux-arm64) arch=arm64 ;;
    *) echo "unsupported runtime identifier: $rid" >&2; exit 1 ;;
esac

"$root/packaging/publish-unix.sh" "$rid" "$version" "$out"
out="$(cd "$out" && pwd)"

# nfpm reads the application from ./pgproj-stage (see nfpm.yaml), so it is run from a directory holding that.
work="$out/nfpm-$rid"
rm -rf "$work"
mkdir -p "$work"
cp -a "$out/pgproj-$version-$rid" "$work/pgproj-stage"
cp "$root/packaging/linux/nfpm.yaml" "$work/nfpm.yaml"

nfpm_package() {
    local packager="$1"
    if command -v nfpm >/dev/null 2>&1; then
        (cd "$work" && VERSION="$version" ARCH="$arch" nfpm package --config nfpm.yaml --packager "$packager" --target "$out")
    else
        docker run --rm -v "$work:/work" -v "$out:/out" -w /work -e VERSION="$version" -e ARCH="$arch" \
            "$nfpm_image" package --config nfpm.yaml --packager "$packager" --target /out
    fi
}

nfpm_package deb
nfpm_package rpm
rm -rf "$work"
ls -l "$out"
