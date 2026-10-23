#!/usr/bin/env bash
# Verifies a packed pgproj .NET tool (G-323) as a user would meet it, before it is published:
#   1. its contents: the tool, libpg_query for the five supported runtimes and no other, the readme and notices;
#   2. dotnet tool install from that package alone (no other source), then packaging/smoke-test.sh on the installed command: its version, and a build.
# Usage: packaging/nuget/verify-tool.sh <folder with the .nupkg> <version>
set -euo pipefail
export MSYS_NO_PATHCONV=1

packages="$(cd "${1:?usage: verify-tool.sh <folder with the .nupkg> <version>}" && pwd)"
version="${2:?usage: verify-tool.sh <folder with the .nupkg> <version>}"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
nupkg="$packages/pgproj.$version.nupkg"
[[ -f "$nupkg" ]] || { echo "no $nupkg" >&2; exit 1; }

# dotnet on Windows needs C:/... paths, not /c/....
winpath() { if command -v cygpath >/dev/null 2>&1; then cygpath -m "$1"; else echo "$1"; fi; }
# The first Python that runs (on Windows, python3 can be the Store's stub).
python=""
for candidate in python3 python; do
    if command -v "$candidate" >/dev/null 2>&1 && "$candidate" -c "" >/dev/null 2>&1; then python="$candidate"; break; fi
done
[[ -n "$python" ]] || { echo "Python 3 is needed to read the package" >&2; exit 1; }

echo "== 1. contents of $(basename "$nupkg")"
"$python" - "$(winpath "$nupkg")" <<'PY'
import sys, zipfile
names = set(zipfile.ZipFile(sys.argv[1]).namelist())
tool = "tools/net10.0/any/"
expected = {tool + name for name in ["pgproj.dll", "pgproj.runtimeconfig.json", "DotnetToolSettings.xml", "PgProj.Engine.dll"]} | {
    tool + "runtimes/win-x64/native/libpg_query.dll", tool + "runtimes/win-arm64/native/libpg_query.dll",
    tool + "runtimes/linux-x64/native/libpg_query.so", tool + "runtimes/linux-arm64/native/libpg_query.so",
    tool + "runtimes/osx-arm64/native/libpg_query.dylib",
    "README.md", "NOTICE", "THIRD-PARTY-NOTICES.txt",
}
missing = sorted(expected - names)
other_runtimes = sorted(name for name in names if name.startswith(tool + "runtimes/") and name not in expected)
for problem, found in [("missing", missing), ("a runtime it does not support", other_runtimes)]:
    for name in found:
        print(f"{problem}: {name}")
if missing or other_runtimes:
    sys.exit(1)
print(f"{len(expected)} expected files, no other runtime")
PY

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
# Only the packed folder as a source, so the tool cannot come from nuget.org or a cache of an earlier run.
cat > "$work/NuGet.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="packed" value="$(winpath "$packages")" />
  </packageSources>
</configuration>
EOF

echo "== 2. dotnet tool install, and run it"
dotnet tool install pgproj --version "$version" --tool-path "$(winpath "$work/tools")" --configfile "$(winpath "$work/NuGet.config")"
exe="$work/tools/pgproj"
[[ -f "$exe.exe" ]] && exe="$exe.exe"
# With path conversion back on: on Windows the smoke test hands the executable a /c/... path to its project.
env -u MSYS_NO_PATHCONV "$here/../smoke-test.sh" "$exe" "$version"

echo "tool verified: pgproj $version"
