#!/usr/bin/env bash
# Builds a binary release of IronRuby for one platform:
#
#   Util/package-release.sh linux-x64            # -> dist/ironruby-<version>-linux-x64.tar.gz
#   Util/package-release.sh win-x64 out          # -> out/ironruby-<version>-win-x64.zip
#   IR_VERSION=4.0.0-preview1 Util/package-release.sh osx-arm64
#
# The archive keeps the source tree's layout - the launchers at the top, the build in
# Src/Console/bin/Release/<tfm>, the standard library in Src/StdLib - so ir.sh/ir.cmd,
# igem, irb and irubyc work from the unpacked directory exactly as they do in a
# checkout (ir.sh picks the Release build when there is no Debug one).
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
# ../prism/build holding the libprism for this platform. The RID must be this
# machine's: the archive carries this machine's libprism.
set -euo pipefail

RID=${1:?usage: Util/package-release.sh RID [OUTDIR]}
IR_ROOT=$(cd "$(dirname "$0")/.." && pwd)
OUT=$(mkdir -p "${2:-dist}" && cd "${2:-dist}" && pwd)
: "${IR_TFM:=net8.0}"
VERSION=${IR_VERSION:-$(sed -n 's/.*DisplayVersion = "\(.*\)";.*/\1/p' "$IR_ROOT/Src/Ruby/CurrentVersion.cs")}
NAME="ironruby-$VERSION-$RID"
STAGE="$OUT/$NAME"

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
dotnet publish "$IR_ROOT/Src/Console/Ruby.Console.csproj" \
  -c Release -r "$RID" --self-contained true -f "$IR_TFM" \
  -p:IronRubyTargetFrameworks="$IR_TFM" -p:PublishReadyToRun=true \
  $composite \
  -o "$STAGE/Src/Console/bin/Release/$IR_TFM"
rm -rf "$r2r_obj"
rm -f "$STAGE/Src/Console/bin/Release/$IR_TFM"/*.pdb

case "$RID" in
  win-*) launchers=(ir.cmd irb.cmd igem.cmd iirb.cmd irubyc.cmd) ;;
  *)     launchers=(ir.sh irb.sh igem.sh iirb.sh irubyc.sh) ;;
esac
for f in "${launchers[@]}" README.md; do
  cp "$IR_ROOT/$f" "$STAGE/"
done
cp -R "$IR_ROOT/Src/StdLib" "$STAGE/Src/"
rm -f "$STAGE/Src/StdLib/StdLib.rbproj"
cp -R "$IR_ROOT/Src/Public" "$STAGE/License"
# Samples/Notepad is a Windows Forms application, so only the Windows archive has it.
case "$RID" in
  win-*) cp -R "$IR_ROOT/Samples" "$STAGE/" ;;
esac

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
# without breaking anything else.
case "$RID" in
  win-*) (cd "$STAGE" && cmd //c ir.cmd -e 'puts RUBY_DESCRIPTION' &&
          cmd //c ir.cmd "$(cygpath -w "$IR_ROOT/Util/windows-desktop-check.rb")") ;;
  *)     "$STAGE/ir.sh" -e 'puts RUBY_DESCRIPTION' ;;
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
