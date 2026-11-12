#!/bin/sh
# Publish a ReadyToRun (AOT-precompiled) build of the console host.
#
#   Util/publish-r2r.sh            # from the Release sources
#   IR_CONFIG=R2R ./ir.sh -e ''    # run it
#
# Why: almost half of IronRuby's remaining startup time is the .NET JIT
# compiling IronRuby's own interpreter, binder and parser paths - the same
# methods, from scratch, in every process.  crossgen2 compiles them ahead of
# time into the assemblies, so a fresh process starts from native code and only
# re-JITs what tiering decides is hot.  Measured on an idle box, `./ir.sh -e ''`,
# best of 4:
#
#   Release, dotnet build      1.37 s     Release --disable-gems      1.15 s
#   Release, this script       0.93 s     Release --disable-gems      0.74 s
#
# It is a separate output tree rather than the default because ReadyToRun only
# runs in the `dotnet publish` pipeline, needs a RuntimeIdentifier, and takes
# noticeably longer to build than `dotnet build` - not something every worktree
# and every agent script should pay on each rebuild.  The IL is the same, so the
# published host passes the same specs as the Release build it came from.
#
# The output lands in Src/Console/bin/R2R/$IR_TFM, which is exactly where
# IR_CONFIG=R2R makes ir.sh look; no change to ir.sh is needed.
IR_ROOT="$(dirname "$(dirname "$(readlink -f "$0")")")"
export DOTNET_ROOT=/usr/local/dotnet
export PATH=/usr/local/dotnet:$PATH
: "${DLR_SOURCE_DIR:=/root/workspace/dlr/src/core}"
: "${RID:=linux-x64}"
# Which framework to precompile for; crossgen2 has to pick one.  net10.0 is the default
# everything else uses, IR_TFM=net11.0 publishes the .NET 11 one next to it.
: "${IR_TFM:=net10.0}"

cd "$IR_ROOT" || exit 1
dotnet publish Src/Console/Ruby.Console.csproj \
  -c Release -r "$RID" --self-contained false -f "$IR_TFM" \
  -p:PublishReadyToRun=true \
  -p:DlrSourceDir="$DLR_SOURCE_DIR" \
  -o "$IR_ROOT/Src/Console/bin/R2R/$IR_TFM" "$@" || exit 1

echo "published: $IR_ROOT/Src/Console/bin/R2R/$IR_TFM/ir  (run it with IR_CONFIG=R2R ./ir.sh)"
