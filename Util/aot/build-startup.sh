#!/bin/sh
# Builds IronRuby.Startup.dll - the Ruby code `ir` runs at every start (ruby4.rb, rbconfig, rubygems,
# see startup-files.txt) compiled ahead of time to IL and ReadyToRun code - into the R2R host's
# directory, where IronRuby's loader picks it up: requiring one of its files then runs the compiled
# top level instead of parsing, transforming and LightCompiling the source.
#
#   Util/aot/build-startup.sh            # after a Release build (which publishes the R2R host)
#   IR_CONFIG=R2R ./ir.sh -e 1           # uses it; IR_STARTUP_DLL=none turns it off
#   IR_STARTUP_DLL_TRACE=1 ...           # says what it registered and what is still compiled from source
#
# The compiler needs .NET 10 (PersistedAssemblyBuilder), so this builds IronRuby for net10.0 as well;
# the library it writes is retargeted to the host's framework. It records the identity (MVID) of the
# host's IronRuby and DLR assemblies, and of each source file's contents: a host
# rebuilt since, or an edited file, is not run from stale code (the file is compiled from source; the
# library as a whole is ignored when the assemblies differ). So rerun this after each host rebuild.
IR_ROOT="$(dirname "$(dirname "$(dirname "$(readlink -f "$0")")")")"
export DOTNET_ROOT="${DOTNET_ROOT:-/usr/local/dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
: "${DLR_SOURCE_DIR:=/root/workspace/dlr/src/core}"
: "${IR_TFM:=net8.0}"
HOST="${IR_HOST_DIR:-$IR_ROOT/Src/Console/bin/R2R/$IR_TFM}"

cd "$IR_ROOT" || exit 1
if [ ! -f "$HOST/IronRuby.dll" ]; then
  echo "build-startup.sh: no R2R host in $HOST (build -c Release first, or Util/publish-r2r.sh)" >&2
  exit 1
fi

dotnet build Util/aot/IronRuby.Aot -c Release -m:3 -nologo -v:q \
  -p:DlrSourceDir="$DLR_SOURCE_DIR" -p:IronRubyTargetFrameworks=net10.0 -p:IronRubyR2R=false || exit 1

OUT="$(mktemp -d)"
Util/aot/IronRuby.Aot/bin/Release/net10.0/IronRuby.Aot startup -o "$OUT" --tfm "$IR_TFM" --r2r --host "$HOST" \
  @Util/aot/startup-files.txt 2>"$OUT/log" || { cat "$OUT/log" >&2; rm -rf "$OUT"; exit 1; }
grep -E "^(wrote|r2r|retargeted)" "$OUT/log"
cp "$OUT"/IronRuby.Startup*.dll "$HOST/" || exit 1
rm -rf "$OUT"
echo "installed: $HOST/IronRuby.Startup.dll"
