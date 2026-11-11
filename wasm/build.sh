#!/bin/sh
# Builds IronRuby for the browser into wasm/dist (static files; serve with any web server).
#
#   wasm/build.sh                 # Mono AOT of the hot assemblies (default; boot ~1.5 s, ~8 min build)
#   AOT=0 wasm/build.sh           # IL only, Mono's interpreter (40 s build; boot ~5 s, code ~3x slower)
#
# DLR_DIR: a DLR checkout with Build/dlr-patches/*.patch applied (default ../dlr-wasm beside
# the repository; created from ../dlr when missing).
set -eu
HERE=$(cd "$(dirname "$0")" && pwd)
IR=$(dirname "$HERE")
export DOTNET_ROOT=${DOTNET_ROOT:-/usr/local/dotnet}
export PATH="$DOTNET_ROOT:$PATH"
export MSBUILDDISABLENODEREUSE=1
DLR_DIR_SET=${DLR_DIR:-}
DLR_DIR=${DLR_DIR:-$(dirname "$IR")/dlr-wasm}
DLR_SRC=$(dirname "$IR")/dlr
# dlr-wasm is a copy of ../dlr plus the wasm patches: start it over when ../dlr has moved on
# (new Build/dlr-patches committed there), and check ../dlr, since the wasm patches touch some
# of the same files and make the build's own check (CheckDlrPatches) fail on the copy
if [ -z "${DLR_DIR_SET:-}" ] && [ -d "$DLR_DIR/src/core" ] && [ -d "$DLR_SRC/.git" ] &&
   [ "$(git -C "$DLR_DIR" rev-parse HEAD)" != "$(git -C "$DLR_SRC" rev-parse HEAD)" ]; then
  echo "$DLR_SRC moved on: recreating $DLR_DIR"
  rm -rf "$DLR_DIR"
fi
[ -d "$DLR_SRC/.git" ] && "$IR/Util/check-dlr-patches.sh" "$DLR_SRC/src/core"
if [ ! -d "$DLR_DIR/src/core" ]; then
  git clone -q --no-hardlinks "$(dirname "$IR")/dlr" "$DLR_DIR"
  for p in "$IR"/Build/dlr-patches/*.patch; do
    git -C "$DLR_DIR" apply --check "$p" 2>/dev/null && git -C "$DLR_DIR" apply "$p" || echo "skip (applied?) $p"
  done
fi
for p in "$IR"/Build/dlr-patches/wasm/*.patch; do
  [ -f "$p" ] || continue
  git -C "$DLR_DIR" apply --check "$p" 2>/dev/null && git -C "$DLR_DIR" apply "$p" && echo "applied $p" || true
done

"$HERE/native/build-prism.sh"

CONF=${CONF:-Release}
# interpreter and AOT builds share obj/: never let one reuse the other's intermediates (a
# trimmed CoreLib left in obj/.../linked by an AOT build breaks the untrimmed interpreter build)
rm -rf "$HERE/IronRuby.Wasm/obj/$CONF/net10.0" "$HERE/IronRuby.Wasm/bin/$CONF/net10.0/publish"
# wasm/global.json pins the .NET 10 SDK (its wasm-tools workload); dotnet finds it from the cwd
cd "$HERE"
nice -n 10 dotnet publish "$HERE/IronRuby.Wasm/IronRuby.Wasm.csproj" -c "$CONF" -m:3 \
  -p:DlrSourceDir="$DLR_DIR/src/core" -p:CheckDlrPatches=false -p:IronRubyTargetFrameworks=net10.0 \
  -p:RunAOTCompilation=$([ "${AOT:-1}" = 0 ] && echo false || echo true) -nologo -clp:ErrorsOnly

DIST="$HERE/dist"
rm -rf "$DIST"; mkdir -p "$DIST"
cp -r "$HERE/IronRuby.Wasm/bin/$CONF/net10.0/publish/wwwroot/." "$DIST/"
python3 "$HERE/make-stdlib.py" "$IR/Src/StdLib" "$DIST/stdlib.zip"
echo "dist: $DIST"
du -sh "$DIST"
