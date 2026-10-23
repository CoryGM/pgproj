#!/usr/bin/env bash
# Builds the signed apt and dnf repositories of pgproj (G-331) in <site dir>, which GitHub Pages serves (release.yml):
#
#   pgproj.asc                          the public key the repositories and the .rpm packages are signed with
#   pgproj.repo, pgproj-testing.repo    dnf/yum repository files
#   apt/pool/main/p/pgproj/*.deb        one pool for both channels
#   apt/dists/<channel>/                Release, InRelease, Release.gpg, and main/binary-{amd64,arm64}/Packages(.gz)
#   rpm/<channel>/                      the .rpm packages (signed), repodata/, and repodata/repomd.xml.asc
#   index.html                          how to install from them
#
# The channels are stable (releases) and testing (the newest release candidate, or the newest release when no candidate is newer;
# packaging/linux/collect-releases.sh chooses them). <stable dir> and <testing dir> hold each channel's .deb and .rpm packages; either may be empty
# (stable is, until the first release), and its indexes are then empty, so a source list or repository file that names it still works. The key is the
# one in <signing dir> (packaging/linux/signing-key.sh). PGPROJ_REPOSITORY_URL is the address the site is served at, written into the repository files
# and the page (default https://corygm.github.io/pgproj). Debian's tools build the apt side and Fedora's the dnf side, each in a container, so it runs
# the same on a runner and on a developer's machine.
#
# Usage: packaging/linux/build-repository.sh <site dir> <signing dir> <stable dir> <testing dir>
set -euo pipefail

usage="usage: build-repository.sh <site dir> <signing dir> <stable dir> <testing dir>"
site="${1:?$usage}"
signing="$(cd "${2:?$usage}" && pwd)"
stable="$(cd "${3:?$usage}" && pwd)"
testing="$(cd "${4:?$usage}" && pwd)"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
url="${PGPROJ_REPOSITORY_URL:-https://corygm.github.io/pgproj}"
# Docker on Windows (Git Bash) needs a Windows path for a mount; elsewhere the path is used as it is.
host() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi; }

if ! compgen -G "$stable/*.deb" >/dev/null && ! compgen -G "$testing/*.deb" >/dev/null; then
    echo "neither channel has a package" >&2
    exit 1
fi

rm -rf "$site"
mkdir -p "$site"
site="$(cd "$site" && pwd)"
mounts=(-v "$(host "$site"):/site" -v "$(host "$signing"):/signing:ro" -v "$(host "$here"):/scripts:ro"
        -v "$(host "$stable"):/in/stable:ro" -v "$(host "$testing"):/in/testing:ro")

# The apt repository: one pool, a Packages index per channel and architecture, and a signed Release per channel.
MSYS_NO_PATHCONV=1 docker run --rm "${mounts[@]}" debian:12 bash -c '
    set -euo pipefail
    shopt -s nullglob
    apt-get update -qq && apt-get install -y -qq apt-utils gnupg >/dev/null
    . /scripts/import-signing-key.sh
    gpg --batch --armor --export "$fingerprint" > /site/pgproj.asc
    mkdir -p /site/apt/pool/main/p/pgproj
    cd /site/apt
    for channel in stable testing; do
        debs=(/in/$channel/*.deb)
        [[ ${#debs[@]} -eq 0 ]] || cp -n "${debs[@]}" pool/main/p/pgproj/
    done
    apt-ftparchive packages pool > /tmp/all.Packages
    for channel in stable testing; do
        names=""
        for deb in /in/$channel/*.deb; do names+="$(basename "$deb") "; done
        for arch in amd64 arm64; do
            dir="dists/$channel/main/binary-$arch"
            mkdir -p "$dir"
            awk -v names="$names" -v arch="$arch" -f /scripts/filter-packages.awk /tmp/all.Packages > "$dir/Packages"
            gzip -9 -k -f "$dir/Packages"
        done
        apt-ftparchive \
            -o APT::FTPArchive::Release::Origin=pgproj -o APT::FTPArchive::Release::Label=pgproj \
            -o APT::FTPArchive::Release::Suite="$channel" -o APT::FTPArchive::Release::Codename="$channel" \
            -o APT::FTPArchive::Release::Architectures="amd64 arm64" -o APT::FTPArchive::Release::Components=main \
            -o APT::FTPArchive::Release::Description="pgproj ($channel)" \
            release "dists/$channel" > /tmp/Release
        mv /tmp/Release "dists/$channel/Release"
        "${gpg_sign[@]}" --clearsign -o "dists/$channel/InRelease" "dists/$channel/Release"
        "${gpg_sign[@]}" --armor --detach-sign -o "dists/$channel/Release.gpg" "dists/$channel/Release"
        echo "apt $channel: $(grep -c "^Package:" dists/$channel/main/binary-amd64/Packages || true) amd64, $(grep -c "^Package:" dists/$channel/main/binary-arm64/Packages || true) arm64"
    done'

# The dnf repositories: the packages signed, their metadata, and the metadata signed.
MSYS_NO_PATHCONV=1 docker run --rm "${mounts[@]}" fedora:latest bash -c '
    set -euo pipefail
    shopt -s nullglob
    dnf install -y -q rpm-sign createrepo_c gnupg2 >/dev/null
    . /scripts/import-signing-key.sh
    for channel in stable testing; do
        dir="/site/rpm/$channel"
        mkdir -p "$dir"
        rpms=(/in/$channel/*.rpm)
        if [[ ${#rpms[@]} -gt 0 ]]; then
            cp "${rpms[@]}" "$dir/"
            rpmsign --define "_gpg_name $fingerprint" \
                --define "_gpg_sign_cmd_extra_args --pinentry-mode loopback --passphrase-file /signing/passphrase" \
                --addsign "$dir"/*.rpm >/dev/null 2>&1
        fi
        createrepo_c --quiet --general-compress-type=gz "$dir"   # gzip, not zstd: the dnf of RHEL 8 cannot read zstd
        "${gpg_sign[@]}" --armor --detach-sign -o "$dir/repodata/repomd.xml.asc" "$dir/repodata/repomd.xml"
        echo "rpm $channel: ${#rpms[@]} packages"
    done'

repo_file() {
    local channel="$1" id="$2" name="$3"
    cat <<EOF
[$id]
name=$name
baseurl=$url/rpm/$channel
enabled=1
gpgcheck=1
repo_gpgcheck=1
gpgkey=$url/pgproj.asc
EOF
}
repo_file stable pgproj "pgproj" > "$site/pgproj.repo"
repo_file testing pgproj-testing "pgproj (testing: release candidates)" > "$site/pgproj-testing.repo"
touch "$site/.nojekyll"
sed "s|@URL@|$url|g" "$here/repository-index.html" > "$site/index.html"
echo "site built in $site ($(du -sh "$site" | cut -f1))"
