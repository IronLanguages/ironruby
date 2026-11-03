#!/bin/sh
# Regenerates Build/startup.mibc, the profile crossgen2 uses for the release's composite
# ReadyToRun image (package-release.sh), from a trace of the published R2R host starting up.
#
#   Util/make-startup-mibc.sh            # needs an R2R host (Util/publish-r2r.sh)
#
# The profile tells crossgen2 which methods startup runs, among them the generic
# instantiations it cannot see statically (FuncCallInstruction<...> and friends, made with
# MakeGenericType, and framework generics over value types such as Dictionary<int, ...>):
# with the framework in the same composite version bubble it precompiles them, so they are
# not JIT'd at startup. A profile older than the code only loses some of that; methods it
# names that no longer exist are ignored.
#
# Tools: dotnet-trace (`dotnet tool install -g dotnet-trace`) and dotnet-pgo, which is not on
# nuget.org but on the .NET team's public dotnet8 feed:
#   dotnet tool install -g dotnet-pgo --version 8.0.0-rc.2.23479.6 \
#     --add-source https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet8/nuget/v3/index.json
# or DOTNET_PGO=<path to dotnet-pgo.dll> (tools/net8.0/any/ in that package).
set -e
IR_ROOT="$(dirname "$(dirname "$(readlink -f "$0")")")"
export DOTNET_ROOT="${DOTNET_ROOT:-/usr/local/dotnet}"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
# JIT, loader and R2R entry-point events: what dotnet-pgo needs to see every method startup runs
PROVIDERS='Microsoft-Windows-DotNETRuntime:0x1F000080018:5'
IR_CONFIG=R2R IR_SERVER=0 dotnet-trace collect --providers "$PROVIDERS" -o "$TMP/startup.nettrace" -- \
  "$IR_ROOT/ir.sh" -e 'require "set"; require "json"; JSON.parse(JSON.generate({"a" => [1, 2.5, nil]})); Set[1, 2].to_a' > /dev/null
if [ -n "$DOTNET_PGO" ]; then
  dotnet "$DOTNET_PGO" create-mibc -t "$TMP/startup.nettrace" -o "$IR_ROOT/Build/startup.mibc"
else
  dotnet-pgo create-mibc -t "$TMP/startup.nettrace" -o "$IR_ROOT/Build/startup.mibc"
fi
echo "wrote $IR_ROOT/Build/startup.mibc"
