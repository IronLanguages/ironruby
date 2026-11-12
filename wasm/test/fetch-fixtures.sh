#!/bin/sh
# Downloads the NuGet package the browser test loads at run time (Markdig, a Markdown -> HTML
# library) into test/fixtures. The file is not checked in.
set -eu
HERE=$(cd "$(dirname "$0")" && pwd)
V=${MARKDIG_VERSION:-0.44.0}
mkdir -p "$HERE/fixtures"
[ -f "$HERE/fixtures/Markdig.dll" ] && exit 0
TMP=$(mktemp -d)
curl -sfL -o "$TMP/markdig.nupkg" "https://api.nuget.org/v3-flatcontainer/markdig/$V/markdig.$V.nupkg"
(cd "$TMP" && unzip -q markdig.nupkg 'lib/net8.0/Markdig.dll')
cp "$TMP/lib/net8.0/Markdig.dll" "$HERE/fixtures/Markdig.dll"
rm -rf "$TMP"
ls -l "$HERE/fixtures/Markdig.dll"
