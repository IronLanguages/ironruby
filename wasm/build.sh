#!/bin/sh
# Builds IronRuby for the browser into wasm/dist (static files; serve with any web server).
#
#   wasm/build.sh                 # Mono AOT of the hot assemblies (default; boot ~1.5 s, ~8 min build)
#   AOT=0 wasm/build.sh           # IL only, Mono's interpreter (40 s build; boot ~5 s, code ~3x slower)
#
# DLR_DIR: a DLR checkout with Build/dlr-patches/*.patch applied (default ../dlr-wasm beside
# the repository; created from ../dlr, at DLR_REF if set, when missing).
set -eu
HERE=$(cd "$(dirname "$0")" && pwd)
IR=$(dirname "$HERE")
export DOTNET_ROOT=${DOTNET_ROOT:-/usr/local/dotnet}
export PATH="$DOTNET_ROOT:$PATH"
export MSBUILDDISABLENODEREUSE=1
DLR_DIR=${DLR_DIR:-$(dirname "$IR")/dlr-wasm}
DLR_SRC=$(dirname "$IR")/dlr
# What the copy was made from: the DLR commit and the patches. A copy this script made from
# something else (DLR_REF moved, ../dlr advanced, a patch added or changed) is made again; one it did
# not make (no stamp: a checkout of your own in DLR_DIR) is left as it is.
dlr_rev=$(git -C "$DLR_SRC" rev-parse --verify -q "${DLR_REF:-HEAD}^{commit}" 2>/dev/null || echo unknown)
stamp="$dlr_rev $(cat "$IR"/Build/dlr-patches/*.patch "$IR"/Build/dlr-patches/wasm/*.patch | cksum)"
if [ -f "$DLR_DIR/.ironruby-wasm-copy" ] && [ "$(cat "$DLR_DIR/.ironruby-wasm-copy")" != "$stamp" ]; then
  echo "$DLR_DIR was made from another DLR commit or other patches: making it again"
  rm -rf "$DLR_DIR"
fi
if [ ! -d "$DLR_DIR/src/core" ]; then
  # a copy of ../dlr (at DLR_REF when given: ../dlr may be ahead of this branch's patches) with
  # Build/dlr-patches checked before the wasm patches go on top - they touch some of the same
  # files, which makes the build's own check (CheckDlrPatches, off below) fail on the copy.
  git clone -q --no-hardlinks "$DLR_SRC" "$DLR_DIR"
  [ -n "${DLR_REF:-}" ] && git -C "$DLR_DIR" checkout -q "$DLR_REF"
  for p in "$IR"/Build/dlr-patches/*.patch; do
    git -C "$DLR_DIR" apply --check "$p" 2>/dev/null && git -C "$DLR_DIR" apply "$p" || true
  done
  "$IR/Util/check-dlr-patches.sh" "$DLR_DIR/src/core"
  echo "$stamp" > "$DLR_DIR/.ironruby-wasm-copy"
fi
for p in "$IR"/Build/dlr-patches/wasm/*.patch; do
  [ -f "$p" ] || continue
  # already there (it reverses cleanly) or applied now; a patch that does neither no longer fits this
  # DLR, and the build without it would be a runtime that starts threads the browser does not have
  if git -C "$DLR_DIR" apply --reverse --check "$p" 2>/dev/null; then
    continue
  fi
  git -C "$DLR_DIR" apply "$p" || { echo "build.sh: $p does not apply to $DLR_DIR" >&2; exit 1; }
  echo "applied $p"
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
