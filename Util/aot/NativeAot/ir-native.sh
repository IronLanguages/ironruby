#!/bin/sh
# Runs the NativeAOT build of ir the way ./ir.sh runs the JIT one (prism, bundled stdlib).
#   dotnet publish Util/aot/NativeAot/ir-native.csproj -c Release -p:DlrSourceDir=...
#   Util/aot/NativeAot/ir-native.sh -e 'puts 1 + 2'
HERE="$(dirname "$(readlink -f "$0")")"
IR_ROOT="$(dirname "$(dirname "$(dirname "$HERE")")")"
S="$IR_ROOT/Src/StdLib"
# IR_NATIVE_BIN runs another build of the same host, e.g. the JIT one that takes the
# interpreter-only paths (dotnet build ir-native.csproj -p:PublishAot=false -o DIR).
: "${IR_NATIVE_BIN:=$HERE/bin/Release/net10.0/linux-x64/publish/ir}"
export DOTNET_ROOT="${DOTNET_ROOT:-/usr/local/dotnet}"
exec "$IR_NATIVE_BIN" \
  "-X:StdLib=$S/ironruby:$S/ruby/4.0" "$@"
