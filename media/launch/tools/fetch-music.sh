#!/usr/bin/env bash
# Fetches the launch video's music: Edvard Grieg, "In the Hall of the Mountain King"
# (Peer Gynt Suite No. 1, Op. 46, IV), played by the Czech National Symphony Orchestra
# for Musopen, from classicals.de. The recording is in the public domain, marked
# CC PDM 1.0 (https://www.classicals.de/grieg-peer-gynt), so it may be used in the video
# without conditions; the release notes credit it all the same.
#
#   media/launch/tools/fetch-music.sh            # -> media/launch/assets/music/mountain-king.mp3
#   media/launch/tools/fetch-music.sh --profile  # and print its loudness every half second
#
# The repository does not carry the recording: the render fetches it, and the checksum
# pins the exact file, so a different download cannot slip into a release unnoticed.
# --profile is how the cuts in index.html were placed (data-media-start) without
# listening: it prints the momentary loudness (EBU R128, LUFS) over time.
set -euo pipefail

URL='https://www.quantumdigitalmedia.de/Classicals-Music/Music/MO%20Collection/Classicals.de%20-%20Grieg%20-%20Peer%20Gynt%20Suite%20No.%201%2C%20Op.%2046%20-%20IV.%20In%20the%20Hall%20Of%20The%20Mountain%20King.zip'
SHA256='78ef3c248c376997d0175ef534ab3243677e4323f4a8ff3cb07f698a3441cd7a'  # of the zip, 2024-10-27

here=$(cd "$(dirname "$0")/.." && pwd)
out="$here/assets/music/mountain-king.mp3"
mkdir -p "$(dirname "$out")"
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

curl -fsSL --retry 3 -o "$tmp/music.zip" "$URL"
actual=$(sha256sum "$tmp/music.zip" | cut -d' ' -f1)
if [ -z "$SHA256" ]; then
  echo "fetch-music.sh: no checksum pinned yet; this download is sha256 $actual" >&2
elif [ "$actual" != "$SHA256" ]; then
  echo "fetch-music.sh: the download is sha256 $actual, not the pinned $SHA256" >&2
  exit 1
fi

unzip -l "$tmp/music.zip"
# The zip also holds a macOS resource fork (__MACOSX/._<name>.mp3), which is not audio.
mp3=$(unzip -Z1 "$tmp/music.zip" | grep -v '^__MACOSX/' | grep -i '\.mp3$' | head -1)
if [ -z "$mp3" ]; then
  echo "fetch-music.sh: no MP3 in the download" >&2
  exit 1
fi
unzip -p "$tmp/music.zip" "$mp3" > "$out"
echo "fetch-music.sh: $mp3 -> $out ($(wc -c < "$out") bytes)"

if [ "${1:-}" = --profile ]; then
  ffprobe -v error -show_entries format=duration -of default=noprint_wrappers=1 "$out"
  # ebur128 reports every 100 ms; keep every fifth report.
  ffmpeg -nostats -hide_banner -i "$out" -af ebur128=metadata=1,ametadata=print:key=lavfi.r128.M -f null - 2>&1 |
    awk '/pts_time/ { split($0, a, "pts_time:"); t = a[2] + 0 }
         /lavfi.r128.M=/ { split($0, b, "="); if (n++ % 5 == 0) printf "%7.1f s  %6.1f LUFS\n", t, b[2] }'
fi
