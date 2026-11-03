#!/usr/bin/env bash
# Builds IronRuby's Windows installer (ironruby.iss, Inno Setup 6) from the win-x64
# release archive that Util/package-release.sh makes:
#
#   Util/installer/build-installer.sh dist/ironruby-4.0.3-win-x64.zip
#     -> dist/ironruby-4.0.3-win-x64-setup.exe
#   ISCC=/path/to/ISCC.exe Util/installer/build-installer.sh ZIP OUTDIR
#
# The installer carries the archive's tree as it is: the archive is unpacked into a
# directory of its own and handed to Inno Setup's compiler, and the version comes from
# the archive's name. ISCC is taken from $ISCC, from PATH or from where Inno Setup 6
# installs itself. Runs in Git Bash on Windows.
set -euo pipefail

ZIP=${1:?usage: Util/installer/build-installer.sh ironruby-VERSION-win-x64.zip [OUTDIR]}
ISS=$(cd "$(dirname "$0")" && pwd)/ironruby.iss
ZIP=$(cd "$(dirname "$ZIP")" && pwd)/$(basename "$ZIP")
OUT=$(mkdir -p "${2:-$(dirname "$ZIP")}" && cd "${2:-$(dirname "$ZIP")}" && pwd)
NAME=$(basename "$ZIP" .zip)
case "$NAME" in
  ironruby-*-win-x64) ;;
  *) echo "build-installer.sh: not a win-x64 release archive: $ZIP" >&2; exit 1 ;;
esac
VERSION=${NAME#ironruby-}
VERSION=${VERSION%-win-x64}
# 4.0.3-preview1 and 4.0.2+1a2b3c4 are 4.0.3 and 4.0.2 to Windows' file version.
NUMERIC=$(echo "$VERSION" | grep -oE '^[0-9]+(\.[0-9]+){0,3}')

if [ -z "${ISCC:-}" ]; then
  for candidate in "$(command -v iscc || true)" \
      "$(cygpath -u "${LOCALAPPDATA:-C:\\}")/Programs/Inno Setup 6/ISCC.exe" \
      "/c/Program Files (x86)/Inno Setup 6/ISCC.exe" \
      "/c/Program Files/Inno Setup 6/ISCC.exe"; do
    if [ -n "$candidate" ] && [ -f "$candidate" ]; then ISCC=$candidate; break; fi
  done
fi
if [ -z "${ISCC:-}" ]; then
  echo "build-installer.sh: no Inno Setup 6 compiler (ISCC.exe); install Inno Setup 6 or set ISCC" >&2
  exit 1
fi

# Unpacked into the temporary directory, not next to the output: ISCC does not read paths
# past Windows' 260 characters, and the tree's own go 100 deep.
STAGE=$(mktemp -d)
trap 'rm -rf "$STAGE"' EXIT
if command -v unzip >/dev/null; then unzip -q "$ZIP" -d "$STAGE"
else 7z x -bd -o"$STAGE" "$ZIP" >/dev/null; fi
test -f "$STAGE/$NAME/ironruby.cmd"

# Not MSYS's path conversion: it would take ISCC's /D and /O switches for paths.
MSYS2_ARG_CONV_EXCL='*' "$ISCC" /Q \
  "/DAppVersion=$VERSION" "/DNumericVersion=$NUMERIC" \
  "/DSourceDir=$(cygpath -w "$STAGE/$NAME")" \
  "/O$(cygpath -w "$OUT")" "$(cygpath -w "$ISS")"
echo "installer: $OUT/$NAME-setup.exe"
