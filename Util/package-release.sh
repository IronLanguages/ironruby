#!/usr/bin/env bash
# Builds a binary release of IronRuby for one platform:
#
#   Util/package-release.sh linux-x64            # -> dist/ironruby-<version>-linux-x64.tar.gz
#   Util/package-release.sh win-x64 out          # -> out/ironruby-<version>-win-x64.zip
#   IR_VERSION=4.0.0-preview1 Util/package-release.sh osx-arm64
#   IR_TFM=net11.0 Util/package-release.sh linux-x64   # -> dist/ironruby-<version>-net11-linux-x64.tar.gz
#
# IR_TFM picks the target framework, and so the .NET runtime the archive carries. The
# default, net10.0 (LTS), is the main download and keeps the plain name; any other
# framework is named in the archive's (and its directory's) name, net11.0 as -net11-.
# The archive keeps the source tree's layout - the launchers at the top, the build in
# Src/Console/bin/Release/<tfm>, the standard library in Src/StdLib - so ir.sh/ir.cmd,
# igem, iirb and irubyc work from the unpacked directory exactly as they do in a
# checkout (ir.sh picks the Release build when there is no Debug one). There is no irb
# or gem launcher: on a PATH those names are CRuby's.
#
# The build is self-contained, so it runs on a machine with no .NET installed, and
# ReadyToRun, so IronRuby's own assemblies start as native code (see publish-r2r.sh).
# Being self-contained, it can be one composite ReadyToRun image of the framework and
# IronRuby together, compiled with the startup profile Build/startup.mibc
# (Util/make-startup-mibc.sh): the generic instantiations startup makes over framework
# types are then precompiled too, so `ir -e 1` JITs ~140 methods instead of ~570 and
# starts ~9% faster (0.94 -> 0.85 s), for ~10 MB more RSS and 107 MB instead of 95 MB
# on disk. IR_COMPOSITE=0 builds the per-assembly images instead.
# irubyc still needs the .NET SDK, as it does from a checkout.
#
# It needs what a normal build needs: ../dlr with Build/dlr-patches applied, and
# ../prism/build holding the libprism for this platform, and the librbs that
# Util/build-librbs.sh builds (without it `require "rbs"` raises LoadError). The RID
# must be this machine's: the archive carries this machine's libprism and librbs.
set -euo pipefail

RID=${1:?usage: Util/package-release.sh RID [OUTDIR]}
IR_ROOT=$(cd "$(dirname "$0")/.." && pwd)
OUT=$(mkdir -p "${2:-dist}" && cd "${2:-dist}" && pwd)
DEFAULT_TFM=net10.0
: "${IR_TFM:=$DEFAULT_TFM}"
VERSION=${IR_VERSION:-$(sed -n 's/.*DisplayVersion = "\(.*\)";.*/\1/p' "$IR_ROOT/Src/Ruby/CurrentVersion.cs")}
# Not passed on to what this runs: the checks below must find the build the way the unpacked
# archive's own launchers do, with no IR_TFM set.
export -n IR_TFM
if [ "$IR_TFM" = "$DEFAULT_TFM" ]; then
  NAME="ironruby-$VERSION-$RID"
else
  NAME="ironruby-$VERSION-${IR_TFM%.0}-$RID"
fi
STAGE="$OUT/$NAME"

# A release is CI's build: not one from a ../dlr that has fallen behind Build/dlr-patches.
"$IR_ROOT/Util/check-dlr-patches.sh" "$IR_ROOT/../dlr/src/core"

rm -rf "$STAGE"
mkdir -p "$STAGE/Src"

composite=
if [ "${IR_COMPOSITE:-1}" != 0 ]; then
  composite="-p:PublishReadyToRunComposite=true -p:IronRubyStartupProfile=true"
fi
# The SDK keeps its ReadyToRun output in obj/.../R2R and reuses whatever is there and newer
# than the IL. A composite build leaves component stubs there, which a per-assembly publish
# (this script with IR_COMPOSITE=0, or the R2R host every Linux Release build makes) would
# take as compiled and ship without the composite image: so start and end with it empty.
r2r_obj="$IR_ROOT/Src/Console/obj/Release/$IR_TFM/$RID/R2R"
rm -rf "$r2r_obj"
# also when the publish fails part way (set -e): stale composite stubs would disable R2R in the next publish
trap 'rm -rf "$r2r_obj"' EXIT
publish() {
  dotnet publish "$IR_ROOT/Src/Console/Ruby.Console.csproj" \
    -c Release -r "$RID" --self-contained true -f "$IR_TFM" \
    -p:IronRubyTargetFrameworks="$IR_TFM" -p:PublishReadyToRun=true \
    $composite \
    -o "$STAGE/Src/Console/bin/Release/$IR_TFM"
}
# Once more if it fails: on the macOS runners the parallel build sometimes copies IronRuby.Prism's
# apphost (or its runtimeconfig.json) before it is written (MSB3030 / "being used by another
# process"); 4.0.5's osx-arm64 net10.0 archive was lost to it. A real error fails the second time too.
if ! publish; then
  echo "package-release: publish failed, retrying once" >&2
  rm -rf "$r2r_obj"
  publish
fi
rm -rf "$r2r_obj"
rm -f "$STAGE/Src/Console/bin/Release/$IR_TFM"/*.pdb

case "$RID" in
  win-*) launchers=(ironruby.cmd ir.cmd igem.cmd iirb.cmd irubyc.cmd) ;;
  *)     launchers=(ironruby ir.sh igem.sh iirb.sh irubyc.sh) ;;
esac
for f in "${launchers[@]}" README.md; do
  cp "$IR_ROOT/$f" "$STAGE/"
done
cp -R "$IR_ROOT/Src/StdLib" "$STAGE/Src/"

cp -R "$IR_ROOT/Src/Public" "$STAGE/License"
# Samples/Notepad is a Windows Forms application, so only the Windows archive has it.
case "$RID" in
  win-*) cp -R "$IR_ROOT/Samples" "$STAGE/" ;;
esac

# The launchers run Src/Console/bin/<config>/net10.0 unless IR_TFM says otherwise. An archive
# of another framework has only that framework's build, so its launchers default to it.
if [ "$IR_TFM" != "$DEFAULT_TFM" ]; then
  for f in ironruby ironruby.cmd Samples/Notepad/notepad.cmd; do
    [ -f "$STAGE/$f" ] || continue
    sed -i.bak -e "s/IR_TFM:-$DEFAULT_TFM}/IR_TFM:-$IR_TFM}/g" -e "s/IR_TFM:=$DEFAULT_TFM}/IR_TFM:=$IR_TFM}/" \
      -e "s/\"IR_TFM=$DEFAULT_TFM\"/\"IR_TFM=$IR_TFM\"/" "$STAGE/$f"
    rm -f "$STAGE/$f.bak"
    if grep -q "IR_TFM[:=-]*$DEFAULT_TFM" "$STAGE/$f" || ! grep -q "IR_TFM[:=-]*$IR_TFM" "$STAGE/$f"; then
      echo "package-release.sh: could not make $IR_TFM the default framework of $f" >&2
      exit 1
    fi
  done
fi

# The startup library (Util/aot/build-startup.sh, IronRuby.Startup.dll): the Ruby code every start runs - the
# prelude, rubygems and the rest - compiled ahead of time against exactly this build and the archive's own copy
# of the standard library (`ir -e 1` ~17% faster). Building it needs the SDK and runtime of $IR_TFM, running it
# does not. It ships only if the archive runs Util/aot/startup-smoke.rb the same with it as without it, using
# it, and starts faster with it: without the SDK, or if it fails to build or those checks, the archive goes without it, which costs only the
# speedup, never a broken download. IR_STARTUP_LIBRARY=0 leaves it out. Made after the launchers are final,
# because it is checked through them, as a user runs the unpacked archive.
bin="$STAGE/Src/Console/bin/Release/$IR_TFM"
stage_ir() {
  case "$RID" in
    win-*) (cd "$STAGE" && cmd //c ir.cmd "$@") ;;
    *)     "$STAGE/ir.sh" "$@" ;;
  esac
}
# `with_timeout SECONDS command...`: the command, killed when it runs longer (exit 124). A library that hangs a
# program (a fiber's block it could not run hung forever) must fail the check, not hold the job for its 6 hours; and
# macOS has no timeout(1). On Windows the kill takes the process tree: cmd and the ir under it.
with_timeout() {
  local seconds=$1 pid watchdog status=0
  shift
  "$@" &
  pid=$!
  ( sleep "$seconds"
    case "$RID" in
      win-*) taskkill //F //T //PID "$(cat /proc/$pid/winpid 2>/dev/null)" > /dev/null 2>&1 ;;
    esac
    kill -9 $pid 2> /dev/null ) > /dev/null 2>&1 &
  watchdog=$!
  wait $pid || status=$?
  if kill $watchdog 2> /dev/null; then
    wait $watchdog 2> /dev/null || true
  elif [ $status != 0 ]; then
    status=124
  fi
  return $status
}
# a line in the log, and on GitHub Actions in the job's summary
startup_note() {
  echo "package-release.sh: $*"
  if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then echo "- \`$NAME\`: $*" >> "$GITHUB_STEP_SUMMARY"; fi
}
if [ "${IR_STARTUP_LIBRARY:-1}" != 0 ]; then
  status=0
  # --build-references: the compiler runs a Release build of IronRuby and the DLR (its IL must call exactly the
  # host's, which the library checks by MVID at load time), and the RID-specific publish above leaves IronRuby.Prism,
  # an Exe, only in bin/Release/$IR_TFM/$RID: without it the compiler failed to build in every release up to 4.0.5
  # (CS0006: Metadata file '.../Src/Prism/bin/Release/<tfm>/IronRuby.Prism.dll' could not be found).
  sh "$IR_ROOT/Util/aot/build-startup.sh" --host "$bin" --tfm "$IR_TFM" --rid "$RID" --root "$STAGE" \
    --build-references ${DLR_SOURCE_DIR:+--dlr "$DLR_SOURCE_DIR"} || status=$?
  case $status in
    0) ;;
    3) startup_note "no startup library: building it needs the SDK and runtime of $IR_TFM" ;;
    *) startup_note "no startup library: it failed to build (exit $status)" ;;
  esac
fi
if [ -f "$bin/IronRuby.Startup.dll" ]; then
  check=$(mktemp -d)
  smoke="$IR_ROOT/Util/aot/startup-smoke.rb"
  case "$RID" in win-*) smoke=$(cygpath -w "$smoke") ;; esac
  # Without the library first: if that fails, the tree is broken, which is not the library's to hide.
  IR_STARTUP_DLL=none with_timeout 300 stage_ir "$smoke" > "$check/without"
  status=0
  IR_STARTUP_DLL_TRACE=1 with_timeout 300 stage_ir "$smoke" > "$check/with" 2> "$check/trace" || status=$?
  if [ $status = 124 ]; then
    why="startup-smoke.rb did not finish in 5 minutes with it"
  elif [ $status != 0 ]; then
    why="startup-smoke.rb failed with it (exit $status)"
  elif ! grep -q "IronRuby.Startup: loaded" "$check/trace"; then
    why="the archive's ir did not load it"
  elif grep -q "^IronRuby.Startup: [A-Za-z.]*Exception" "$check/trace"; then
    why="the archive's ir failed to register it"
  elif grep -q "IronRuby.Startup: .*not used" "$check/trace"; then
    why="the archive's ir did not use it for all of its files"
  elif ! diff "$check/without" "$check/with" > "$check/diff"; then
    why="startup-smoke.rb printed something else with it"
  else
    why=
  fi
  if [ -n "$why" ]; then
    rm -f "$bin"/IronRuby.Startup*.dll
    startup_note "no startup library: $why"
    grep -v "^IronRuby.Startup: \(parsing\|compiling\|compiles\|the JIT parses\)" "$check/trace" | head -8 >&2 || true
    head -20 "$check/diff" 2>/dev/null >&2 || true
    if [ -n "${GITHUB_ACTIONS:-}" ]; then echo "::warning::$NAME ships without the startup library: $why"; fi
  elif ! command -v perl > /dev/null; then
    startup_note "startup library used (no perl here to time it)"
  else
    # `ir -e 1` with and without it: the median of 5 runs each, interleaved, after one of each to warm the caches
    now() { perl -MTime::HiRes=time -e 'printf "%.0f\n", time * 1000'; }
    with_timeout 120 stage_ir -e 1 >/dev/null; IR_STARTUP_DLL=none with_timeout 120 stage_ir -e 1 >/dev/null
    : > "$check/on"; : > "$check/off"
    for i in 1 2 3 4 5; do
      t=$(now); with_timeout 120 stage_ir -e 1 >/dev/null; echo $(( $(now) - t )) >> "$check/on"
      t=$(now); IR_STARTUP_DLL=none with_timeout 120 stage_ir -e 1 >/dev/null; echo $(( $(now) - t )) >> "$check/off"
    done
    on=$(sort -n "$check/on" | sed -n 3p) off=$(sort -n "$check/off" | sed -n 3p)
    best_on=$(sort -n "$check/on" | sed -n 1p) best_off=$(sort -n "$check/off" | sed -n 1p)
    times="\`ir -e 1\` $on ms with it, $off ms without (median of 5; best $best_on / $best_off ms)"
    # A library that does not make the start faster is only a risk. Judged by the best run of each, which a busy
    # machine disturbs least: on a box at load 30 the medians once had it the other way round.
    if [ "$best_on" -lt "$best_off" ]; then
      startup_note "startup library used; $times"
    else
      rm -f "$bin"/IronRuby.Startup*.dll
      startup_note "no startup library: it did not make the start faster, $times"
      if [ -n "${GITHUB_ACTIONS:-}" ]; then echo "::warning::$NAME ships without the startup library: it did not make the start faster"; fi
    fi
  fi
  rm -rf "$check"
fi

# The archive carries Microsoft's runtime - .NET, and for Windows also the Windows
# Desktop runtime with Windows Forms and WPF - so it carries their license and
# third-party notices too, from the runtime packs the publish took the files from.
# ir.runtimeconfig.json names each framework and version it bundled.
packs=${NUGET_PACKAGES:-$HOME/.nuget/packages}
grep -oE '"(name|version)": *"[^"]*"' "$STAGE/Src/Console/bin/Release/$IR_TFM/ir.runtimeconfig.json" |
  sed -E 's/.*"([^"]*)"$/\1/' | paste - - |
  while read -r framework version; do
    pack="$packs/$(echo "$framework.Runtime.$RID" | tr '[:upper:]' '[:lower:]')/$version"
    mkdir -p "$STAGE/License/$framework"
    found=
    for f in "$pack"/*; do
      case "$(basename "$f" | tr '[:upper:]' '[:lower:]')" in
        license*|third-party-notices*) cp "$f" "$STAGE/License/$framework/"; found=1 ;;
      esac
    done
    if [ -z "$found" ]; then
      echo "package-release.sh: no license file in $pack" >&2
      exit 1
    fi
  done

# Linux: SQLitePCLRaw's libe_sqlite3.so crashes once another SQLite is in the global
# symbol scope (SQLitePCL.raw#682), so the archive carries one rebuilt -Bsymbolic
# (Util/build-e_sqlite3.sh). SQLite must report the same version, source id and compile
# options from both, and the rebuilt one must survive the system's libsqlite3 preloaded,
# which is how the issue reproduces it.
case "$RID" in
  linux-*)
    bin="$STAGE/Src/Console/bin/Release/$IR_TFM"
    check="$IR_ROOT/Util/sqlite-options.rb"
    report=$(mktemp -d)
    system_sqlite=$(ldconfig -p 2>/dev/null | awk '/libsqlite3\.so\.0 /{print $NF; exit}')
    "$STAGE/ir.sh" "$check" > "$report/shipped.txt"
    if [ -n "$system_sqlite" ]; then
      if LD_PRELOAD="$system_sqlite" "$STAGE/ir.sh" "$check" > /dev/null 2>&1; then
        echo "package-release.sh: the shipped libe_sqlite3.so survives $system_sqlite preloaded"
      else
        echo "package-release.sh: the shipped libe_sqlite3.so fails with $system_sqlite preloaded"
      fi
    fi
    bash "$IR_ROOT/Util/build-e_sqlite3.sh" "$bin"
    "$STAGE/ir.sh" "$check" > "$report/rebuilt.txt"
    diff "$report/shipped.txt" "$report/rebuilt.txt"
    if [ -n "$system_sqlite" ]; then
      # Not only no crash: it must still be the bundled SQLite answering, not the system's.
      LD_PRELOAD="$system_sqlite" "$STAGE/ir.sh" "$check" > "$report/preloaded.txt"
      diff "$report/rebuilt.txt" "$report/preloaded.txt"
      echo "package-release.sh: the rebuilt libe_sqlite3.so survives $system_sqlite preloaded," \
        "and answers as $(head -1 "$report/rebuilt.txt")"
    else
      echo "package-release.sh: no system libsqlite3.so.0 here to preload against the rebuilt one" >&2
    fi
    rm -rf "$report"
    ;;
esac

# A quick check that the unpacked tree starts, before it is archived - and on Windows
# that Windows Forms and WPF load from it, which a missing framework reference breaks
# without breaking anything else. iirb has to start too: it loads reline, which on Windows
# looks up Win32 functions through Fiddle, and 4.0.2's died there.
# One command per line: set -e does not stop at a failure on the left of && - nor at a
# case whose last && list failed that way - so a chain would let a broken tree through.
case "$RID" in
  win-*)
    test -f "$STAGE/Src/Console/bin/Release/$IR_TFM/ironrubyw.exe"
    (cd "$STAGE"
     cmd //c ironruby.cmd -e 'puts RUBY_DESCRIPTION'
     cmd //c ir.cmd -e 'puts RUBY_ENGINE'
     printf 'puts 6 * 7\n' | cmd //c iirb.cmd | grep -x 42
     cmd //c ir.cmd "$(cygpath -w "$IR_ROOT/Util/windows-desktop-check.rb")")
    ;;
  *)
    "$STAGE/ironruby" -e 'puts RUBY_DESCRIPTION'
    "$STAGE/ir.sh" -e 'puts RUBY_ENGINE'
    printf 'puts 6 * 7\n' | "$STAGE/iirb.sh" | grep -x 42
    ;;
esac

cd "$OUT"
case "$RID" in
  win-*)
    rm -f "$NAME.zip"
    if command -v 7z >/dev/null; then 7z a -tzip -bd "$NAME.zip" "$NAME" >/dev/null
    else zip -qr "$NAME.zip" "$NAME"; fi
    ARCHIVE="$NAME.zip" ;;
  *)
    tar -czf "$NAME.tar.gz" "$NAME"
    ARCHIVE="$NAME.tar.gz" ;;
esac
echo "packaged: $OUT/$ARCHIVE"
