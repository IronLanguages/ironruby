#!/usr/bin/env bash
# Builds libmarkly_cmark, the native half of IronRuby's markly support:
#
#   Util/build-markly.sh [target ...]       targets: linux-x64 win-x64 osx-x64 osx-arm64
#                                           (default: the machine this runs on)
#
# The markly gem (a CommonMark/GFM parser) is Ruby over one C extension, and that
# extension is GitHub's cmark-gfm with a 1,255-line Ruby binding (markly.c) on top.
# IronRuby does the binding in C# (Src/Libraries/Markly) and runs cmark-gfm itself
# as native code, so a document renders to the same bytes it does on CRuby: this is
# the same C, from the same gem.
#
# The sources are the gem's own ext/markly, taken from the markly-$VERSION.gem on
# rubygems.org and checked against a pinned SHA-256 - every *.c there except
# markly.c (the CRuby binding), plus Src/Libraries/Markly/native/markly_shim.c.
# Set MARKLY_GEM to a local copy of the .gem to skip the download; it is checked
# against the same SHA-256.
#
# Output goes to Src/Libraries/Markly/native/build (git-ignored), where
# Src/Libraries/IronRuby.Libraries.csproj picks it up as Content, the way
# Src/Prism/IronRuby.Prism.csproj ships libprism:
#   linux-x64   libmarkly_cmark.so     gcc (or $CC), -Wl,-Bsymbolic
#   win-x64     markly_cmark.dll       x86_64-w64-mingw32-gcc (or $WIN_CC); MinGW cross
#                                      compiler on Linux, or the MSYS2 gcc that
#                                      ruby/setup-ruby puts on PATH on Windows
#   osx-*       libmarkly_cmark.dylib  clang (or $CC) on macOS
# Without the library `require "markly"` raises a LoadError that names this script.
set -euo pipefail

VERSION=0.15.2
URL="https://rubygems.org/downloads/markly-$VERSION.gem"
SHA256='65dae965d4dd4ecd997fba43b93acc0fe7dadfec6f07a748640c7a9299a8551e'  # of the .gem

ROOT=$(cd "$(dirname "$0")/.." && pwd)
SHIM="$ROOT/Src/Libraries/Markly/native/markly_shim.c"
OUT="$ROOT/Src/Libraries/Markly/native/build"

# The flags markly's extconf.rb compiles with (-O3 -std=c99), position independent.
CFLAGS=(-O3 -std=c99 -Wall -Wno-unknown-pragmas -DNDEBUG)

sha256() {
  if command -v sha256sum >/dev/null; then sha256sum "$1" | cut -d' ' -f1
  else shasum -a 256 "$1" | cut -d' ' -f1; fi
}

targets=("$@")
if [ ${#targets[@]} -eq 0 ]; then
  case "$(uname -s)-$(uname -m)" in
    Linux-x86_64) targets=(linux-x64) ;;
    Darwin-x86_64) targets=(osx-x64) ;;
    Darwin-arm64) targets=(osx-arm64) ;;
    MINGW*-x86_64|MSYS*-x86_64|CYGWIN*-x86_64) targets=(win-x64) ;;
    *) echo "build-markly.sh: no default target for $(uname -s)-$(uname -m); name one" >&2; exit 1 ;;
  esac
fi

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

gem="$tmp/markly-$VERSION.gem"
if [ -n "${MARKLY_GEM:-}" ]; then
  cp "$MARKLY_GEM" "$gem"
else
  curl -fsSL --retry 3 -o "$gem" "$URL"
fi
actual=$(sha256 "$gem")
if [ "$actual" != "$SHA256" ]; then
  echo "build-markly.sh: markly-$VERSION.gem is sha256 $actual, not the pinned $SHA256" >&2
  exit 1
fi

# A .gem is a tar holding data.tar.gz.
mkdir -p "$tmp/gem" "$tmp/src"
tar -xf "$gem" -C "$tmp/gem" data.tar.gz
tar -xzf "$tmp/gem/data.tar.gz" -C "$tmp/src" ext/markly
src="$tmp/src/ext/markly"

sources=()
for f in "$src"/*.c; do
  [ "$(basename "$f")" = markly.c ] && continue
  sources+=("$f")
done
sources+=("$SHIM")

mkdir -p "$OUT"
for target in "${targets[@]}"; do
  case "$target" in
    linux-x64)
      lib=libmarkly_cmark.so
      "${CC:-gcc}" "${CFLAGS[@]}" -fPIC -shared -I"$src" -o "$tmp/$lib" "${sources[@]}" \
        -Wl,-Bsymbolic -Wl,-soname,$lib
      ;;
    win-x64)
      lib=markly_cmark.dll
      # cmark-gfm marks its API with visibility("default"), which means nothing to a
      # PE linker; export everything, as the ELF build does.  libgcc is linked in so
      # the DLL needs nothing beyond the C runtime that ships with Windows.
      cc="${WIN_CC:-}"
      if [ -z "$cc" ]; then
        if command -v x86_64-w64-mingw32-gcc >/dev/null; then cc=x86_64-w64-mingw32-gcc; else cc=gcc; fi
      fi
      "$cc" "${CFLAGS[@]}" -Wno-attributes -shared -I"$src" -o "$tmp/$lib" "${sources[@]}" \
        -static-libgcc -Wl,--export-all-symbols
      ;;
    osx-x64|osx-arm64)
      lib=libmarkly_cmark.dylib
      arch=x86_64; [ "$target" = osx-arm64 ] && arch=arm64
      "${CC:-clang}" "${CFLAGS[@]}" -arch "$arch" -dynamiclib -I"$src" -o "$tmp/$lib" "${sources[@]}" \
        -install_name "@rpath/$lib"
      ;;
    *)
      echo "build-markly.sh: unknown target $target" >&2
      exit 1
      ;;
  esac
  cp "$tmp/$lib" "$OUT/$lib"
  echo "build-markly.sh: $OUT/$lib ($target) is cmark-gfm from markly $VERSION, sha256 $(sha256 "$OUT/$lib")"
done
