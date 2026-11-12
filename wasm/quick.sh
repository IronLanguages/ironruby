#!/bin/sh
# Rebuild (publish) and refresh dist without re-zipping when only C# changed. Dev helper.
# AOT=0 for the fast-to-build interpreter-only bundle (see build.sh).
set -eu
HERE=$(cd "$(dirname "$0")" && pwd)
export DOTNET_ROOT=${DOTNET_ROOT:-/usr/local/dotnet} PATH="${DOTNET_ROOT:-/usr/local/dotnet}:$PATH" MSBUILDDISABLENODEREUSE=1
# interpreter and AOT builds share obj/: never let one reuse the other's intermediates (a
# trimmed CoreLib left in obj/.../linked by an AOT build breaks the untrimmed interpreter build)
rm -rf "$HERE/IronRuby.Wasm/obj/${CONF:-Release}/net10.0" "$HERE/IronRuby.Wasm/bin/${CONF:-Release}/net10.0/publish"
# wasm/global.json pins the .NET 10 SDK: dotnet finds it from the cwd, so run from here whatever
# directory the script was started in. CheckDlrPatches is off as in build.sh: the DLR copy has the
# wasm patches on top, which the check of the regular ones takes for a missing patch.
cd "$HERE"
nice -n 10 dotnet publish "$HERE/IronRuby.Wasm/IronRuby.Wasm.csproj" -c ${CONF:-Release} -m:3 \
  -p:DlrSourceDir=${DLR_DIR:-$(dirname "$(dirname "$HERE")")/dlr-wasm}/src/core -p:CheckDlrPatches=false -p:IronRubyTargetFrameworks=net10.0 \
  -p:RunAOTCompilation=$([ "${AOT:-1}" = 0 ] && echo false || echo true) -nologo -clp:ErrorsOnly
Z=$(mktemp); [ -f "$HERE/dist/stdlib.zip" ] && cp "$HERE/dist/stdlib.zip" "$Z"
rm -rf "$HERE/dist"; mkdir -p "$HERE/dist"
[ -s "$Z" ] && cp "$Z" "$HERE/dist/stdlib.zip"; rm -f "$Z"
cp -r "$HERE/IronRuby.Wasm/bin/${CONF:-Release}/net10.0/publish/wwwroot/." "$HERE/dist/"
[ -f "$HERE/dist/stdlib.zip" ] || python3 "$HERE/make-stdlib.py" "$HERE/../Src/StdLib" "$HERE/dist/stdlib.zip"
