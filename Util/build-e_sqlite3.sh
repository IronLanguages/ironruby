#!/usr/bin/env bash
# Rebuilds the libe_sqlite3.so that SQLitePCLRaw ships for linux-x64, linked -Bsymbolic:
#
#   Util/build-e_sqlite3.sh <dir holding libe_sqlite3.so>
#
# SQLitePCLRaw's libe_sqlite3.so exports SQLite's whole API and is linked without
# -Bsymbolic, so its calls to its own exported functions go through the PLT. Once another
# SQLite is in the global symbol scope - a plugin loaded RTLD_GLOBAL, libvips's
# vips-openslide for one, or LD_PRELOAD - those calls bind to that copy: it initializes
# itself, the bundled copy's allocator stays empty, and the first sqlite3_open_v2
# segfaults (https://github.com/ericsink/SQLitePCL.raw/issues/682).
#
# This builds the same SQLite (3.53.3, the one SQLitePCLRaw.lib.e_sqlite3 2.1.13 carries)
# with the same compile options, the way SQLitePCLRaw builds it (ericsink/cb, bld/cb.cs:
# gcc -m64 -msse4.2 -maes -shared -fPIC -O), plus -Wl,-Bsymbolic. package-release.sh
# checks that SQLite reports the same version, source id and compile options before and
# after the swap. Windows and macOS do not bind symbols this way and keep the original.
set -euo pipefail

BIN=${1:?usage: Util/build-e_sqlite3.sh <dir holding libe_sqlite3.so>}
VERSION=3530300  # 3.53.3
URL="https://sqlite.org/2026/sqlite-amalgamation-$VERSION.zip"
SHA256='646421e12aac110282ef8cc68f1a62d4bb15fc7b8f09da0b53e29ee690500431'  # of the zip
SOURCE_ID='2026-06-26 20:14:12 d4c0e51e4aeb96955b99185ab9cde75c339e2c29c3f3f12428d364a10d782c62'

# SQLitePCLRaw's set (bld/cb.cs, add_basic_sqlite3_defines and add_linux_sqlite3_defines),
# and what PRAGMA compile_options shows the 2.1.13 build has beyond it.
DEFINES=(
  -DSQLITE_DEFAULT_FOREIGN_KEYS=1
  -DSQLITE_DQS=0
  -DSQLITE_ENABLE_COLUMN_METADATA
  -DSQLITE_ENABLE_FTS3_PARENTHESIS
  -DSQLITE_ENABLE_FTS4
  -DSQLITE_ENABLE_FTS5
  -DSQLITE_ENABLE_GEOPOLY
  -DSQLITE_ENABLE_JSON1
  -DSQLITE_ENABLE_MATH_FUNCTIONS
  -DSQLITE_ENABLE_PREUPDATE_HOOK
  -DSQLITE_ENABLE_RTREE
  -DSQLITE_ENABLE_SESSION
  -DSQLITE_ENABLE_SNAPSHOT
  -DSQLITE_LIKE_DOESNT_MATCH_BLOBS
  -DSQLITE_OS_UNIX
  -DNDEBUG
)

[ -f "$BIN/libe_sqlite3.so" ] || { echo "build-e_sqlite3.sh: no libe_sqlite3.so in $BIN" >&2; exit 1; }
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

curl -fsSL --retry 3 -o "$tmp/sqlite.zip" "$URL"
actual=$(sha256sum "$tmp/sqlite.zip" | cut -d' ' -f1)
if [ -z "$SHA256" ]; then
  echo "build-e_sqlite3.sh: no checksum pinned yet; this download is sha256 $actual" >&2
elif [ "$actual" != "$SHA256" ]; then
  echo "build-e_sqlite3.sh: the download is sha256 $actual, not the pinned $SHA256" >&2
  exit 1
fi
unzip -q -j "$tmp/sqlite.zip" "sqlite-amalgamation-$VERSION/sqlite3.c" -d "$tmp"
if ! grep -qF "\"$SOURCE_ID\"" "$tmp/sqlite3.c"; then
  echo "build-e_sqlite3.sh: sqlite3.c is not SQLite check-in $SOURCE_ID" >&2
  exit 1
fi

gcc -m64 -msse4.2 -maes -shared -fPIC -O "${DEFINES[@]}" \
  -o "$tmp/libe_sqlite3.so" "$tmp/sqlite3.c" -lm -Wl,-Bsymbolic
readelf -d "$tmp/libe_sqlite3.so" | grep -q SYMBOLIC
cp "$tmp/libe_sqlite3.so" "$BIN/libe_sqlite3.so"
echo "build-e_sqlite3.sh: $BIN/libe_sqlite3.so is SQLite 3.53.3, linked -Bsymbolic"
