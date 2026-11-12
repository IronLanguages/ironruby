#!/bin/sh
# Builds prism (the CRuby parser, C99, no dependencies) as an emscripten static archive that
# the browser-wasm host links in through NativeFileReference.  The archive is named prism.a so
# the module name matches IronRuby.Prism's [DllImport("prism")].
set -eu
DN=${DOTNET_ROOT:-/usr/local/dotnet}
HERE=$(cd "$(dirname "$0")" && pwd)
PRISM=${PRISM_DIR:-$HERE/../../../prism}
EMSDK=${EMSDK_DIR:-$(ls -d $DN/packs/Microsoft.NET.Runtime.Emscripten.*.Sdk.*/*/tools | sort | tail -1)}
export DOTNET_EMSCRIPTEN_LLVM_ROOT="$EMSDK/bin" DOTNET_EMSCRIPTEN_BINARYEN_ROOT="$EMSDK"
export DOTNET_EMSCRIPTEN_NODE_JS=$(ls -d $DN/packs/Microsoft.NET.Runtime.Emscripten.*.Node.*/*/tools/bin/node | sort | tail -1)
export EM_FROZEN_CACHE=1
EMCC="$EMSDK/emscripten/emcc"
EMAR="$EMSDK/emscripten/emar"
export EM_CACHE=${EM_CACHE:-$(ls -d $DN/packs/Microsoft.NET.Runtime.Emscripten.*.Cache.*/*/tools/emscripten/cache 2>/dev/null | sort | tail -1)}
OUT="$HERE/build"
rm -rf "$OUT"; mkdir -p "$OUT/obj"
pids=
for c in "$PRISM"/src/*.c "$PRISM"/src/*/*.c; do
  [ -f "$c" ] || continue
  o="$OUT/obj/$(basename "$c" .c).o"
  "$EMCC" -O2 -std=c99 -I"$PRISM/include" -DPRISM_EXPORT_SYMBOLS -c "$c" -o "$o" &
  pids="$pids $!"
done
# each job by its id: a bare `wait` answers 0 whatever the compilers said, and the archive was then
# made from the objects that did compile
failed=0
for pid in $pids; do wait "$pid" || failed=1; done
[ "$failed" = 0 ] || { echo "build-prism.sh: a prism source failed to compile" >&2; exit 1; }
"$EMAR" rcs "$OUT/prism.a" "$OUT"/obj/*.o
ls -l "$OUT/prism.a"
