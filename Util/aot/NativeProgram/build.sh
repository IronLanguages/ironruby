#!/bin/sh
# Part C: Ruby -> IL (IronRuby.Aot) -> native executable (NativeAOT), prelude included.
#
#   Util/aot/NativeProgram/build.sh Util/aot/samples/demo.rb [workdir]
#   <workdir>/demo-native/demo-native            # the native program
#
# The IL is produced against the shared DLR; ILC then compiles it together with IronRuby built
# against a private DLR copy carrying Build/dlr-patches/aot (interpreter-only call-site rules).
set -e
HERE="$(dirname "$(readlink -f "$0")")"
AOT_DIR="$(dirname "$HERE")"
RB="$(readlink -f "${1:?usage: build.sh file.rb [workdir]}")"
WORK="${2:-/tmp/ironruby-aot-native}"
NAME="$(basename "$RB" .rb)"
export DOTNET_ROOT=/usr/local/dotnet
DOTNET=/usr/local/dotnet/dotnet
mkdir -p "$WORK"

# 1. the IL (the Ruby program and the core prelude, compiled)
"$DOTNET" build "$AOT_DIR/IronRuby.Aot" -c Release -p:DlrSourceDir=/root/workspace/dlr/src/core -p:IronRubyAotHostTfm=net10.0 2>&1 | grep -E " error |rror\(s\)" | sort -u
"$AOT_DIR/IronRuby.Aot/bin/Release/net10.0/IronRuby.Aot" compile --prelude -o "$WORK/$NAME-il" "$RB"

# 2. a DLR copy with the AOT patches
[ -d "$WORK/dlr/src/core" ] || "$AOT_DIR/NativeAot/make-dlr-copy.sh" "$WORK/dlr"

# 3. NativeAOT over the IL and IronRuby
"$DOTNET" publish "$HERE/native-program.csproj" -c Release \
  -p:DlrSourceDir="$WORK/dlr/src/core" \
  -p:RubyProgramName="$NAME" \
  -p:RubyProgramDll="$WORK/$NAME-il/$NAME.dll" \
  -p:RubyProgramDelegatesDll="$WORK/$NAME-il/$NAME.Delegate0.dll" \
  -o "$WORK/$NAME-native" > "$WORK/$NAME-native.log" 2>&1 || { grep -E " error " "$WORK/$NAME-native.log" | head; exit 1; }
ls -la "$WORK/$NAME-native/$NAME-native"
