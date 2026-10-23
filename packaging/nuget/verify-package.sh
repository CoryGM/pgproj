#!/usr/bin/env bash
# Verifies a packed PgProj.Engine package as a user would meet it (G-319), before it is published:
#   1. its contents: the seven assemblies, libpg_query for the five supported runtimes and no other, the build check, the readme and notices;
#   2. a program that references it builds for no runtime in particular and runs (the native library found under runtimes/<rid>/native);
#   3. the same program published for this machine's runtime runs (the library beside it);
#   4. a build for a runtime the package does not support (osx-x64) fails with PGPROJ001.
# Usage: packaging/nuget/verify-package.sh <folder with the .nupkg> <version>
set -euo pipefail
export MSYS_NO_PATHCONV=1

packages="$(cd "${1:?usage: verify-package.sh <folder with the .nupkg> <version>}" && pwd)"
version="${2:?usage: verify-package.sh <folder with the .nupkg> <version>}"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
nupkg="$packages/PgProj.Engine.$version.nupkg"
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
expected = {f"lib/net10.0/PgProj.{name}.dll" for name in ["Engine", "Core", "Parser", "Semantics", "Dependencies", "Diff", "Deploy"]} | {
    "lib/net10.0/PgProj.Engine.xml",
    "runtimes/win-x64/native/libpg_query.dll", "runtimes/win-arm64/native/libpg_query.dll",
    "runtimes/linux-x64/native/libpg_query.so", "runtimes/linux-arm64/native/libpg_query.so",
    "runtimes/osx-arm64/native/libpg_query.dylib",
    "buildTransitive/PgProj.Engine.targets", "README.md", "NOTICE", "THIRD-PARTY-NOTICES.txt",
}
missing = sorted(expected - names)
other_runtimes = sorted(name for name in names if name.startswith("runtimes/") and name not in expected)
other_libs = sorted(name for name in names if name.startswith("lib/") and name not in expected)
for problem, found in [("missing", missing), ("a runtime it does not support", other_runtimes), ("an unexpected assembly", other_libs)]:
    for name in found:
        print(f"{problem}: {name}")
if missing or other_runtimes or other_libs:
    sys.exit(1)
print(f"{len(expected)} expected files, nothing else under lib/ or runtimes/")
PY

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
cp -R "$here/consumer/." "$work/"
# Restored into a folder of its own, so a package cached by an earlier run of the same version is never used.
cat > "$work/NuGet.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="packed" value="$(winpath "$packages")" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <config>
    <add key="globalPackagesFolder" value="$(winpath "$work/packages")" />
  </config>
</configuration>
EOF
project="$(winpath "$work/Consumer.csproj")"

echo "== 2. build for no runtime in particular, and run"
dotnet run --project "$project" -c Release -p:PgProjEngineVersion="$version"

echo "== 3. publish for this machine's runtime, and run"
rid="$(dotnet --info | sed -n 's/^ *RID: *//p' | head -n 1)"
echo "runtime: $rid"
dotnet publish "$project" -c Release -r "$rid" --self-contained false -p:PgProjEngineVersion="$version" -o "$(winpath "$work/published")" -nologo
exe="$work/published/Consumer"
[[ -f "$exe.exe" ]] && exe="$exe.exe"
"$exe"

echo "== 4. a runtime the package does not support fails the build"
if output="$(dotnet build "$project" -c Release -r osx-x64 -p:PgProjEngineVersion="$version" -nologo 2>&1)"; then
    echo "$output"
    echo "a build for osx-x64 succeeded; it should fail with PGPROJ001" >&2
    exit 1
fi
if ! grep -q "PGPROJ001" <<<"$output"; then
    echo "$output"
    echo "a build for osx-x64 failed, but not with PGPROJ001" >&2
    exit 1
fi
echo "osx-x64: refused with PGPROJ001"

echo "package verified: PgProj.Engine $version"
