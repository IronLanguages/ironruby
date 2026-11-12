#!/bin/sh
# A private copy of the DLR with Build/dlr-patches/aot/*.patch applied, for the NativeAOT
# prototype. The shared checkout (/root/workspace/dlr) is left alone.
#
#   Util/aot/NativeAot/make-dlr-copy.sh /tmp/dlr-aot
#   dotnet publish Util/aot/NativeAot/ir-native.csproj -c Release -p:DlrSourceDir=/tmp/dlr-aot/src/core
set -e
HERE="$(dirname "$(readlink -f "$0")")"
IR_ROOT="$(dirname "$(dirname "$(dirname "$HERE")")")"
DLR="${DLR:-/root/workspace/dlr}"
OUT="${1:?usage: make-dlr-copy.sh <dir>}"
mkdir -p "$OUT"
git -C "$DLR" archive HEAD | tar -x -C "$OUT"
for p in "$IR_ROOT"/Build/dlr-patches/aot/*.patch; do
  (cd "$OUT" && patch -p1 < "$p")
done
echo "patched DLR copy: $OUT/src/core"
