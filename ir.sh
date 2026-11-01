#!/bin/sh
# Runs the built ir with the prism front end and the bundled standard library.
# Used as RUBY_EXE when running ruby/spec:
#
#   RUBY_EXE=./ir.sh ./ir.sh -Imspec/lib mspec/bin/mspec-run spec/language
#
IR_ROOT="$(dirname "$(readlink -f "$0")")"

# apphost needs DOTNET_ROOT when the SDK isn't on the default search path
if [ -z "$DOTNET_ROOT" ] && [ -x /usr/local/dotnet/dotnet ]; then
  DOTNET_ROOT=/usr/local/dotnet
  export DOTNET_ROOT
fi

# Ruby 4.0's standard library comes first; the 1.9 tree is still on the path
# behind it for the libraries 4.0 gemified or implements as C extensions.
# -X:StdLib puts them on $LOAD_PATH after -I, RUBYOPT's -I and RUBYLIB, where
# MRI keeps its own library directories.
S="$IR_ROOT/Src/StdLib"
# IR_CONFIG=Release runs the optimized build instead (build it with
# `dotnet build Src/Console/Ruby.Console.csproj -c Release ...`).  It is about 1.8x
# faster across Util/bench - the Debug build is compiled with optimizations off, which
# marks the assemblies non-optimizable, so the JIT leaves the whole runtime unoptimized
# too - and it passes the same specs and the same IronRuby.Tests.  Util/run-tests.sh and
# regen-initializers.sh read IR_CONFIG as well, so one export covers a whole session.
# Debug is what an unconfigured run uses, because every worktree and agent script builds it.
# A binary release (Util/package-release.sh) ships only the Release build: when IR_CONFIG is
# not set and there is no Debug build, run that one.
# Once an R2R host has been published (Util/publish-r2r.sh) it is the default instead: startup
# is about a third faster (`ir -e 1`: 1.30 s -> 0.80 s). It is skipped while a Debug or Release
# build is newer than it, since that build has changes the host does not have.
if [ -z "$IR_CONFIG" ]; then
  IR_CONFIG=Debug
  if [ ! -x "$IR_ROOT/Src/Console/bin/Debug/${IR_TFM:-net8.0}/ir" ] &&
     [ -x "$IR_ROOT/Src/Console/bin/Release/${IR_TFM:-net8.0}/ir" ]; then
    IR_CONFIG=Release
  fi
  R2R_DLL="$IR_ROOT/Src/Console/bin/R2R/${IR_TFM:-net8.0}/ir.dll"
  if [ -x "$IR_ROOT/Src/Console/bin/R2R/${IR_TFM:-net8.0}/ir" ] &&
     [ -z "$(find "$IR_ROOT/Src/Console/bin/Debug/${IR_TFM:-net8.0}/ir.dll" \
                  "$IR_ROOT/Src/Console/bin/Release/${IR_TFM:-net8.0}/ir.dll" \
                  -maxdepth 0 -newer "$R2R_DLL" 2>/dev/null)" ]; then
    IR_CONFIG=R2R
  fi
fi

# IR_TFM picks the target framework, the same way IR_CONFIG picks the configuration.  The
# tree multi-targets net8.0;net10.0 (Directory.Build.props), so one `dotnet build` leaves
# both side by side and IR_TFM=net10.0 runs the same IL on the .NET 10 runtime.  net8.0
# stays the default: it is what every worktree, agent script and baseline was taken on.
: "${IR_TFM:=net8.0}"

IR_BIN="$IR_ROOT/Src/Console/bin/$IR_CONFIG/$IR_TFM/ir"
# Say which configuration is missing rather than let the shell's "not found" - which a
# filtered spec log swallows - be the only clue. Getting IR_CONFIG=Release past a tree
# that has only ever been built Debug is the whole reason this check is here.
if [ ! -x "$IR_BIN" ]; then
  echo "ir.sh: no $IR_CONFIG build at $IR_BIN" >&2
  if [ "$IR_CONFIG" = "R2R" ]; then
    # Not a build configuration: a ReadyToRun publish, which starts about 0.45s
    # faster than Release because IronRuby's own assemblies are already native.
    echo "ir.sh: build it with: IR_TFM=$IR_TFM Util/publish-r2r.sh" >&2
  else
    echo "ir.sh: build it with: dotnet build Src/Console/Ruby.Console.csproj -c $IR_CONFIG -p:DlrSourceDir=..." >&2
    echo "ir.sh: (that builds every framework in IronRubyTargetFrameworks, including $IR_TFM)" >&2
  fi
  exit 127
fi

# -S searches RUBYPATH before PATH; Src/StdLib/bin holds the executables the
# bundled library ships (irb), so `./ir.sh -S irb` runs IronRuby's own rather
# than whatever binstub happens to be on PATH.  An existing RUBYPATH still wins
# for anything not shipped here.
RUBYPATH="$S/bin${RUBYPATH:+:$RUBYPATH}"
export RUBYPATH

STDLIB="-X:StdLib=$S/ironruby:$S/ruby/4.0:$S/ruby/1.9.1"

# Warm start (Util/warm/README.md): IR_SERVER=start keeps a pool of IronRuby processes with the
# runtime and the prelude already loaded, and IR_SERVER=1 runs the program in one of them -
# `ir -e 1` in ~0.05 s instead of ~0.8 s. Whenever the pool cannot serve a request (none running,
# none idle, startup inputs such as RUBYOPT or -I that differ from the pool's) the client execs
# exactly the command below instead.
case "$IR_SERVER" in
  ""|0) ;;
  start|stop|restart|status)
    exec "$IR_ROOT/Util/warm/ir-server.sh" "$IR_SERVER" "$IR_BIN" -X:UsePrism "$STDLIB" ;;
  *)
    if [ -x "$IR_ROOT/Util/warm/bin/ir-client" ]; then
      exec "$IR_ROOT/Util/warm/bin/ir-client" "$IR_BIN" -X:UsePrism "$STDLIB" "$@"
    fi ;;
esac

exec "$IR_BIN" -X:UsePrism "$STDLIB" "$@"
