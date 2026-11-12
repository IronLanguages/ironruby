#!/bin/sh
# Builds the prototype compiler and compiles the samples in the configurations the README
# measures. Output: $OUT/<sample>-<variant>/<sample>.dll, run with `dotnet <that dll>`.
#
#   Util/aot/build-samples.sh [outdir]     (default: /tmp/ironruby-aot)
#
# Variants: noprelude  - just the program; IronRuby's Ruby-level core (ruby4.rb) is not loaded
#           prelude    - the program plus ruby4.rb and what it requires, all compiled in
#           r2r        - prelude, then crossgen2 (ReadyToRun) over it and IronRuby's assemblies
AOT_DIR="$(dirname "$(readlink -f "$0")")"
IR_ROOT="$(dirname "$(dirname "$AOT_DIR")")"
OUT="${1:-/tmp/ironruby-aot}"
export DOTNET_ROOT=/usr/local/dotnet
: "${DLR_SOURCE_DIR:=/root/workspace/dlr/src/core}"

/usr/local/dotnet/dotnet build "$AOT_DIR/IronRuby.Aot" -c Release -p:DlrSourceDir="$DLR_SOURCE_DIR" -p:IronRubyAotHostTfm=net10.0 2>&1 | grep -E " error |rror\(s\)" | sort -u
AOT="$AOT_DIR/IronRuby.Aot/bin/Release/net10.0/IronRuby.Aot"
[ -x "$AOT" ] || { echo "build failed" >&2; exit 1; }

for sample in ${SAMPLES:-hello demo bench}; do
  "$AOT" compile -o "$OUT/$sample-noprelude" "$AOT_DIR/samples/$sample.rb" 2>&1 | grep wrote
  "$AOT" compile --prelude -o "$OUT/$sample-prelude" "$AOT_DIR/samples/$sample.rb" 2>&1 | grep wrote
  "$AOT" compile --prelude --r2r -o "$OUT/$sample-r2r" "$AOT_DIR/samples/$sample.rb" 2>&1 | grep "wrote\|r2r $sample"
done
