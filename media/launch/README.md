# IronRuby 4.0 launch video

The source of the launch video. The rendered film lives on the
[v4.0.0 release](https://github.com/Largo/ironruby/releases/tag/v4.0.0) as
`ironruby-4-launch.mp4`, so the repository carries no full renders. What the README and
the release notes show is small: `teaser.webp` plays inline in the README (GitHub does
not play an MP4 there unless it was uploaded through its web editor), and `poster.jpg` is
the title frame.

| File | What it is |
| --- | --- |
| `index.html` | The [HyperFrames](https://hyperframes.heygen.com) composition: 1920x1080, 55 s, nine scenes on one paused GSAP timeline |
| `assets/fonts/` | Inter and JetBrains Mono (SIL Open Font License, the licences beside them), so a render needs no network for fonts |
| `assets/images/notepad.png` | [`Samples/Notepad`](../../Samples/Notepad) editing its own source, captured on Windows 11 at 200 % |
| `poster.jpg` | The title frame with a play button |
| `teaser.webp` | 23 s of the film at 960x540 and 10 fps: the title, the irb session, .NET and the Notepad, the end card. Frames seeked from `index.html` in Chrome, encoded with ImageMagick (`magick -delay 10 -loop 0 f*.png -resize 960x540 -quality 72 teaser.webp`), 1.3 MB; silent, as images are |
| `tools/fetch-music.sh` | Fetches the music into `assets/music/` (not in git), checked against a pinned SHA-256 |

## Music

Edvard Grieg, *In the Hall of the Mountain King* (Peer Gynt Suite No. 1, Op. 46, IV),
played by the Czech National Symphony Orchestra for [Musopen](https://musopen.org), from
[classicals.de](https://www.classicals.de/grieg-peer-gynt). The recording is in the public
domain (Creative Commons Public Domain Mark 1.0), so it needs no licence; it is credited
here and in the release notes all the same.

The repository does not carry it: `tools/fetch-music.sh` downloads it before a render.
The quiet opening plays under the 2011 scene; at the title the film cuts into the full
orchestra (`data-media-start` on `#music-main`), placed so that the piece's last chord
falls on the end card. `tools/fetch-music.sh --profile` prints the recording's loudness
every half second, which is how the cut was placed.

Every figure on screen comes from the project README (ruby/spec counts, benchmark ratios,
versions), and every line of code runs on IronRuby 4.0. Change the README, change the film.

## Render it

Node 22 and FFmpeg; HyperFrames fetches its own pinned Chrome. The HyperFrames version is
pinned in `package.json`.

```sh
cd media/launch
tools/fetch-music.sh   # the music, into assets/music/
npm run lint
npm run check      # also audits layout and contrast in the browser
npm run render     # renders/ironruby-4-launch.mp4, 60 fps
npm run preview    # the HyperFrames studio, to scrub and edit
```

`.github/workflows/release.yml` does the same on Ubuntu, but only when it is run by hand -
neither a `v*` tag nor a pull request renders the film. With `render_video` ticked it keeps
the MP4 as a workflow artifact; with `video_for: v4.0.0` it renders only the film and puts
it on that release, along with its line in `SHA256SUMS` (replacing the one there, or adding
both to a release published without it).
