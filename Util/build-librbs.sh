#!/usr/bin/env bash
# Builds librbs, the native half of IronRuby's `rbs_extension` (the rbs gem's C extension):
#
#   Util/build-librbs.sh [OUTDIR]     # default: Src/Libraries/Rbs/native/build
#
# The rbs gem is Ruby over one C extension, and the C is two things: a parser (src/ and
# include/) that does not touch the Ruby C API at all, and ext/rbs_extension, the glue that
# turns its AST into Ruby objects.  librbs is the first, compiled from the released gem
# itself - downloaded from rubygems.org and checked against the SHA-256 pinned below - plus
# Src/Libraries/Rbs/native/rbs_ironruby.c, which replaces the glue with a byte stream that
# Src/Libraries/Rbs reads back into Ruby objects.  The parser is never modified, so a
# signature parses here exactly as it does under CRuby.
#
# The result is librbs.so (Linux), librbs.dylib (macOS) or librbs.dll (Windows, built by the
# MinGW gcc of MSYS2 or the RubyInstaller DevKit, from Git Bash or an MSYS2 shell: the same
# command; set CC to use another compiler).  IronRuby.Libraries.csproj ships whichever of
# them is in Src/Libraries/Rbs/native/build next to the IronRuby assemblies, as it does
# libprism.
#
# Moving to another rbs release: change VERSION and SHA256, run
#   ruby Src/Libraries/Rbs/generate.rb <gem>/config.yml
# and rebuild.  rbs_extension.rb only loads under the rbs version librbs was built from.
set -euo pipefail

VERSION=4.0.0.dev.5
SHA256='273938e5a9c7f06b041be583588f2aa1976149bda2ce213e33e129c51534e174'  # of rbs-$VERSION.gem
URL="https://rubygems.org/downloads/rbs-$VERSION.gem"

IR_ROOT=$(cd "$(dirname "$0")/.." && pwd)
SHIM="$IR_ROOT/Src/Libraries/Rbs/native"
OUT=${1:-$SHIM/build}
CC=${CC:-gcc}

case "$(uname -s)" in
  MINGW*|MSYS*|CYGWIN*) LIB=librbs.dll; PIC= ;;
  Darwin)               LIB=librbs.dylib; PIC=-fPIC ;;
  *)                    LIB=librbs.so; PIC=-fPIC ;;
esac

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

if [ -n "${RBS_GEM:-}" ]; then
  cp "$RBS_GEM" "$tmp/rbs.gem"        # an already downloaded copy; still checked below
else
  curl -fsSL --retry 3 -o "$tmp/rbs.gem" "$URL"
fi
actual=$( (sha256sum "$tmp/rbs.gem" 2>/dev/null || shasum -a 256 "$tmp/rbs.gem") | cut -d' ' -f1)
if [ "$actual" != "$SHA256" ]; then
  echo "build-librbs.sh: rbs-$VERSION.gem is sha256 $actual, not the pinned $SHA256" >&2
  exit 1
fi

# A .gem is a tar holding data.tar.gz, which holds the gem's files.
tar -xf "$tmp/rbs.gem" -C "$tmp" data.tar.gz
mkdir "$tmp/rbs"
tar -xzf "$tmp/data.tar.gz" -C "$tmp/rbs" src include config.yml

# The generated bridge must be the one this rbs's config.yml produces.  It is only
# checked when there is a Ruby to run the generator with.
if command -v ruby >/dev/null 2>&1; then
  mkdir -p "$tmp/gen/native"
  cp "$SHIM/../generate.rb" "$tmp/gen/"
  ruby "$tmp/gen/generate.rb" "$tmp/rbs/config.yml" >/dev/null
  # A Windows checkout (core.autocrlf) has the committed files with CRLF endings.
  if ! tr -d '\r' < "$SHIM/rbs_ironruby_nodes.inc" | cmp -s "$tmp/gen/native/rbs_ironruby_nodes.inc" - ||
     ! tr -d '\r' < "$SHIM/../RbsNodes.Generated.cs" | cmp -s "$tmp/gen/RbsNodes.Generated.cs" -; then
    echo "build-librbs.sh: the committed bridge is not what rbs $VERSION's config.yml generates;" \
         "run ruby Src/Libraries/Rbs/generate.rb on it" >&2
    exit 1
  fi
fi

# ext/rbs_extension/extconf.rb's flags, minus the warnings: C99 with GNU extensions, and
# -DNDEBUG, which compiles out the RBS_ASSERT checks as the gem's own build does.
sources=("$tmp"/rbs/src/*.c "$tmp"/rbs/src/util/*.c "$SHIM/rbs_ironruby.c")
mkdir -p "$OUT"
# shellcheck disable=SC2086
"$CC" -shared $PIC -O2 -std=gnu99 -DNDEBUG -fvisibility=hidden -DRBS_IR_RBS_VERSION="\"$VERSION\"" \
  -I"$tmp/rbs/include" -I"$SHIM" -o "$tmp/$LIB" "${sources[@]}"
cp "$tmp/$LIB" "$OUT/$LIB"
echo "build-librbs.sh: $OUT/$LIB is rbs $VERSION's parser"
