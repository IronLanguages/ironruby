#!/bin/sh
# Builds IronRuby.Startup.dll - the Ruby code `ir` runs at every start (ruby4.rb, rbconfig, rubygems,
# see startup-files.txt) compiled ahead of time to IL and ReadyToRun code - into a host's directory,
# where IronRuby's loader picks it up: requiring one of its files then runs the compiled top level
# instead of parsing, transforming and LightCompiling the source (`ir -e 1` ~17% faster).
#
# A Linux Release build runs this for the R2R host it publishes (Ruby.Console.csproj,
# IronRubyReadyToRunHost; -p:IronRubyStartupLibrary=false leaves it out), and so does
# Util/package-release.sh. By hand, after a Release build:
#
#   Util/aot/build-startup.sh                     # into Src/Console/bin/R2R/net8.0
#   IR_CONFIG=R2R ./ir.sh -e 1                    # uses it; IR_STARTUP_DLL=none turns it off
#   IR_STARTUP_DLL_TRACE=1 ...                    # says what it registered and what is still compiled from source
#
# Options: --host DIR (the ir's directory), --tfm TFM (its framework, default net8.0), --rid RID,
# --root DIR (the tree the host is in, if not this one), --dlr DIR (DlrSourceDir), --il (no
# ReadyToRun code, for an IL host), --build-references (build IronRuby for the compiler too; the
# default takes the Release build that is there).
#
# The compiler needs .NET 10 (PersistedAssemblyBuilder): without a .NET 10 SDK and runtime this
# says so and exits 3, and the host simply runs without the library. It runs the host's own
# IronRuby and DLR builds; the library records their identity (MVID) and each source file's
# hash, so a rebuilt host or an edited file is never run from stale code. An unchanged rebuild
# copies the last library from the cache in Util/aot/IronRuby.Aot/obj/startup/.
# Exit status: 0 installed, 3 skipped (no .NET 10), anything else failed.
IR_ROOT="$(dirname "$(dirname "$(dirname "$(readlink -f "$0")")")")"
# One at a time: a build of both frameworks publishes their hosts in parallel, and both use the one
# compiler. flock -o: the build servers and MSBuild nodes started below outlive this script, and must
# not hold the lock.
if [ -z "$IR_STARTUP_LOCKED" ] && command -v flock >/dev/null; then
  mkdir -p "$IR_ROOT/Util/aot/IronRuby.Aot/obj"
  IR_STARTUP_LOCKED=1 exec flock -o "$IR_ROOT/Util/aot/IronRuby.Aot/obj/.startup.lock" sh "$0" "$@"
fi
if [ -z "$DOTNET_ROOT" ] && [ -x /usr/local/dotnet/dotnet ]; then
  DOTNET_ROOT=/usr/local/dotnet
fi
[ -n "$DOTNET_ROOT" ] && export DOTNET_ROOT && PATH="$DOTNET_ROOT:$PATH"
DOTNET="${DOTNET_HOST_PATH:-dotnet}"
TFM=net8.0 HOST= RID= ROOT= R2R=--r2r BUILDREFS=false
# the DLR the tree builds with: --dlr, DLR_SOURCE_DIR, or else what the build itself takes (Directory.Build.props or .rsp)
DLR="${DLR_SOURCE_DIR:-}"
while [ $# -gt 0 ]; do
  case "$1" in
    --host) HOST="$2"; shift ;;
    --tfm) TFM="$2"; shift ;;
    --rid) RID="$2"; shift ;;
    --root) ROOT="$2"; shift ;;
    --dlr) DLR="$2"; shift ;;
    --il) R2R= ;;
    --build-references) BUILDREFS=true ;;
    *) echo "build-startup.sh: unknown option $1" >&2; exit 2 ;;
  esac
  shift
done
: "${HOST:=$IR_ROOT/Src/Console/bin/R2R/$TFM}"
HOST="$(cd "$HOST" 2>/dev/null && pwd)" || { echo "build-startup.sh: no host directory $HOST" >&2; exit 1; }

cd "$IR_ROOT" || exit 1
if [ ! -f "$HOST/IronRuby.dll" ]; then
  echo "build-startup.sh: no ir in $HOST (build -c Release first)" >&2
  exit 1
fi
# no stale library is left behind, whatever happens below
rm -f "$HOST"/IronRuby.Startup*.dll

major() { sed -n 's/^\([0-9][0-9]*\)\..*/\1/p' | sort -n | tail -1; }
sdk=$("$DOTNET" --list-sdks 2>/dev/null | major)
runtime=$("$DOTNET" --list-runtimes 2>/dev/null | sed -n 's/^Microsoft\.NETCore\.App //p' | major)
if [ "${sdk:-0}" -lt 10 ] || [ "${runtime:-0}" -lt 10 ]; then
  echo "build-startup.sh: skipped, the compiler needs the .NET 10 SDK and runtime (found SDK ${sdk:-none}, runtime ${runtime:-none}); ir runs without the startup library"
  exit 3
fi

AOT_DIR=Util/aot/IronRuby.Aot
AOT="$AOT_DIR/bin/Release/net10.0/IronRuby.Aot"
CACHE="$IR_ROOT/$AOT_DIR/obj/startup/$TFM-${RID:-host}$( [ -f "$HOST/System.Private.CoreLib.dll" ] && echo -sc )${R2R:+-r2r}"

props="${DLR:+-p:DlrSourceDir=$DLR} -p:IronRubyTargetFrameworks=$TFM -p:IronRubyAotHostTfm=$TFM -p:IronRubyR2R=false"
if [ "$BUILDREFS" = true ]; then
  "$DOTNET" build "$AOT_DIR" -c Release -nologo -v:q -nodeReuse:false $props || exit 1
else
  # only the compiler itself: the IronRuby and DLR it runs are the Release build of this tree, and a restore of
  # their projects with other properties than the build's would rewrite their assets under a running build
  "$DOTNET" restore "$AOT_DIR" --no-dependencies -nologo -v:q -nodeReuse:false $props &&
  "$DOTNET" build "$AOT_DIR" -c Release --no-restore -nologo -v:q -nodeReuse:false -p:BuildProjectReferences=false $props || exit 1
fi

OUT="$(mktemp -d)"
trap 'rm -rf "$OUT"' EXIT
if ! "$AOT" startup -o "$OUT" --tfm "$TFM" $R2R --host "$HOST" ${RID:+--rid "$RID"} ${ROOT:+--root "$ROOT"} \
     --cache "$CACHE" @Util/aot/startup-files.txt 2>"$OUT/log"; then
  cat "$OUT/log" >&2
  exit 1
fi
grep -E "^(wrote|cached|r2r|retargeted)|UNRELOCATABLE|UNSUPPORTED" "$OUT/log"
cp "$OUT"/IronRuby.Startup*.dll "$HOST/" || exit 1
echo "build-startup.sh: installed $HOST/IronRuby.Startup.dll"
