#!/usr/bin/env bash
# Publishes pgproj self-contained for one runtime identifier and packs it as pgproj-<version>-<rid>.tar.gz (G-81).
# Usage: packaging/publish-unix.sh <rid> <version> <output dir>
# Leaves the unpacked application in <output dir>/pgproj-<version>-<rid>/ (the .deb and .rpm are made from it).
set -euo pipefail

rid="${1:?usage: publish-unix.sh <rid> <version> <output dir>}"
version="${2:?usage: publish-unix.sh <rid> <version> <output dir>}"
out="${3:?usage: publish-unix.sh <rid> <version> <output dir>}"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
name="pgproj-$version-$rid"

rm -rf "$out/$name"
mkdir -p "$out"
dotnet publish "$root/src/PgProj.Cli/PgProj.Cli.csproj" \
    --configuration Release --runtime "$rid" --self-contained true \
    -p:DebugType=none -p:DebugSymbols=false -p:MinVerVersion="$version" \
    --output "$out/$name"

for doc in README.md pgproj.json.md documentation.md CHANGELOG.md LICENSE NOTICE THIRD-PARTY-NOTICES.txt; do
    [[ -f "$root/$doc" ]] && cp "$root/$doc" "$out/$name/"
done
# The README's notes, at the same relative path as in the repository so its links work (the .deb and .rpm take the whole folder).
mkdir -p "$out/$name/docs"
cp -R "$root/docs/notes" "$out/$name/docs/"
cp "$root/docs/renaming-columns.md" "$out/$name/docs/"
chmod +x "$out/$name/pgproj"

# Apple silicon refuses to run an unsigned executable, so on a Mac the launcher and the parser library get an ad-hoc signature.
# That is not an Apple Developer ID signature and is not notarized (no Apple Developer account, G-81): a download from a browser
# is quarantined and Gatekeeper blocks it until `xattr -dr com.apple.quarantine <folder>` is run (README.md, Installing).
if [[ "$rid" == osx-* && "$(uname -s)" == "Darwin" ]]; then
    codesign --force --sign - "$out/$name/runtimes/$rid/native/libpg_query.dylib"
    codesign --force --sign - "$out/$name/pgproj"
fi

tar -czf "$out/$name.tar.gz" -C "$out" "$name"
echo "$out/$name.tar.gz"
