# ruby-bench: the pure-Ruby gem macro benchmarks

Status of the macro benchmarks in [ruby-bench](https://github.com/ruby/ruby-bench)
that exercise a pure-Ruby gem, under IronRuby.  Reproduce with
`Util/rubybench/pure-gems-run.sh <your ruby-bench copy>`; the numbers below
(rerun 2026-10-29) were taken with `WARMUP_ITRS=1 MIN_BENCH_ITRS=3`, IronRuby in Release on .NET 8, and
CRuby 4.0.6 as the oracle for both timing and output.  Per-iteration times, as
the harness reports them ("Average of last 3, non-warmup iters").

| Benchmark | Status | IronRuby | CRuby 4.0.6 | Ratio |
|---|---|---:|---:|---:|
| erubi | OK | 953 ms | 251 ms | 3.8x |
| etanni | OK | 1405 ms | 487 ms | 2.9x |
| chunky-png | OK | 2081 ms | 966 ms | 2.2x |
| liquid-render | OK | 1047 ms | 218 ms | 4.8x |
| liquid-compile | OK | 386 ms | 76 ms | 5.1x |
| liquid-il | OK | 1994 ms | 455 ms | 4.4x |
| liquid-c | N/A | | | needs the liquid-c C extension (liquid_c.so); no CRuby ABI here, so it cannot be built or loaded |
| psych-load | N/A | | 2554 ms | its Gemfile asks for `psych ~> 4.0.0`; IronRuby provides psych 5.3.1 (psych 4's other releases are C), and Bundler.setup now says so. It used to run IronRuby's 5.3.1 silently |
| addressable-parse | OK | 882 ms | 368 ms | 2.4x |
| addressable-equality | OK | 2905 ms | 1118 ms | 2.6x |
| addressable-getters | OK | 708 ms | 217 ms | 3.3x |
| addressable-join | OK | 1337 ms | 531 ms | 2.5x |
| addressable-merge | OK | 768 ms | 255 ms | 3.0x |
| addressable-new | OK | 464 ms | 99 ms | 4.7x |
| addressable-normalize | OK | 1712 ms | 627 ms | 2.7x |
| addressable-setters | OK | 739 ms | 196 ms | 3.8x |
| addressable-to-s | OK | 697 ms | 247 ms | 2.8x |
| tinygql | OK | 2741 ms | 803 ms | 3.4x |
| protoboeuf | OK | 42228 ms | 174 ms | 242.7x |
| protoboeuf-encode | OK | 3647 ms | 315 ms | 11.6x |
| json_parse_float | N/A | | | ractor-only benchmark (`ractor_only: true`): it needs harness-ractor, which needs Ractor. Under the default harness it fails the same way on CRuby (`undefined method 'zero?' for nil`) |
| json_parse_string | N/A | | | same - ractor-only |
| hexapdf | OK | 26965 ms | 2926 ms | 9.2x |
| rubykon | OK | 3586 ms | 1250 ms | 2.9x |
| rubyboy | OK | 43235 ms | 5910 ms | 7.3x |
| optcarrot | OK | 29311 ms | 7294 ms | 4.0x |
| graphql | OK | 669 ms | 86 ms | 7.8x |
| graphql-native | N/A | | | needs graphql-c_parser's graphql/graphql_c_parser_ext.so |

23 of them run now. Each had to be fixed to get there - before the first round the
harness killed all of them (see bug 1) and half could not even `bundle install`.
psych-load ran in that round but is N/A since 2026-10-29 (its row says why), so
its entry under output equivalence below describes the earlier run.

## Output equivalence

A benchmark that runs but computes the wrong thing is worse than one that fails,
so every benchmark whose result is a byte string was compared against CRuby
rather than just timed.  All of these agree exactly:

| Benchmark | What was compared |
|---|---|
| chunky-png | SHA-256 of all nine `to_blob` encodings, the RGBA and RGB streams, and a decode round-trip - identical, zlib levels included |
| protoboeuf, protoboeuf-encode | the 11 decoded messages re-encoded with `to_proto`: 4648275 bytes, same SHA-256 |
| optcarrot | the PPU pixel buffer after 20, 60 and 200 frames (`output_pixels.pack("C*")`), same sum and SHA-256 |
| rubyboy | the PPU frame buffer after 50, 200 and 500 steps (`@buffer.pack("V*")`), same SHA-256 |
| liquid-render | the rendered output of all compiled theme tests: 165689 bytes, identical |
| psych-load | the loaded structure of each of the three YAML fixtures (found a bug, see below) |
| graphql | `GraphQL.parse(negotiate.gql).to_query_string`: 81246 bytes, same SHA-256 (found a bug, see below) |
| tinygql | the full token stream of negotiate.gql: 5262 tokens, same SHA-256 |
| hexapdf | the benchmark checks the PDF size itself (569797 bytes) - it now passes (found a bug, see below) |
| erubi | the benchmark checks the generated source size and the rendered size itself - both pass |

## Bugs found and fixed

Seven IronRuby bugs, all committed on this branch.

1. **Fiddle::Function did not exist** - `require "fiddle"` succeeded and defined
   only `Fiddle::Handle`, so anything reaching for `Fiddle::Function` died with a
   NameError rather than a LoadError it could rescue.  ruby-bench's harness calls
   `getrusage(2)` through it to report maxrss, so *every* macro benchmark raised
   after its last iteration and never wrote its result.  Now implemented on a
   `calli` stub emitted per native signature (the technique the 32-bit-only
   Win32API library already used), with `Fiddle::Pointer`,
   `Fiddle.malloc/realloc/free` and the size_t / const-string / bool typedefs on
   top.  `Fiddle::Closure` (native -> Ruby callbacks) is still missing.

2. **A Gemfile that pins a C-extension gem could not be installed** - three
   separate causes, all fixed in `rubygems/defaults/ironruby.rb`:
   `Gem::Installer#build_extensions` still ran `extconf.rb` and aborted the
   bundle; a host CRuby's installed copy of a library IronRuby provides itself
   shadowed the default gem (worst case: `BUNDLED WITH 4.0.12` made
   `Bundler::SelfManager` switch to an upstream Bundler without IronRuby's
   carve-outs, which then dropped every extension gem from its index); and such a
   gem could still put its lib directory on `$LOAD_PATH` through Bundler, where
   bigdecimal's one-line `lib/bigdecimal.rb` requires `bigdecimal.so`.  Also
   `Bundler::SelfManager` no longer switches away from the bundled Bundler at
   all.  This is what unblocked liquid-render, liquid-compile, liquid-il,
   psych-load and the json benchmarks' `bundle install`.

3. **A keyword-arguments hash stayed "keywords" forever** - `f(a: 1)` builds a
   Hash marked as this call's keyword arguments, and the mark was never removed.
   A method that declares no keyword parameters gets that hash as an ordinary
   positional argument and may keep it, and then any later call that passed it on
   to a method that *does* take keywords failed with `wrong number of arguments
   (given 0, expected 1)`:

   ```ruby
   def set_filter(f, parms = nil) = parms
   h = set_filter(:x, Columns: 5, Predictor: 12)
   def wrap(obj, type: nil) = [obj, type]
   wrap(h, type: 1)     # ArgumentError; CRuby returns [{Columns: 5, ...}, 1]
   ```

   That is HexaPDF's `Stream#set_filter`, and it stopped hexapdf writing a PDF.

4. **`Object#type` still existed** - the 1.8 spelling of `#class`, removed in
   1.9.  Defining it is not harmless: `obj.respond_to?(:type)` is a live test in
   real code.  HexaPDF's `TextLayouter#fit` uses it to tell already-segmented
   text items from raw ones, so with a `Kernel#type` in the way it skipped
   segmentation and laid out nothing - a 618 byte PDF instead of 569797, with no
   error anywhere.

5. **A folded YAML scalar could not carry a chomping or indentation indicator** -
   the scanner opened a block scalar only when the character after `>` was
   whitespace, so `>-`, `>+` and `>2` scanned as plain scalars and the header
   text ended up in the value (`"​>- one two"`).  Three strings in
   psych-load's `discourse_client.en.yaml` loaded wrong.

6. **`JSON.generate` took no options argument** - the documented signature is
   `generate(obj, opts)`.  graphql's printer ends every value in
   `JSON.generate(value, quirks_mode: true)`, so `to_query_string` could not run
   at all.  indent / space / space_before / object_nl / array_nl / max_nesting
   are honoured now, anything else ignored, as the json gem does.

7. **Bundler switched away from the Bundler IronRuby ships** - counted with 2
   above, but a separate commit: `Bundler::SelfManager#find_restart_version` now
   returns early here, so a `BUNDLED WITH` line naming another version cannot
   replace the patched copy in Src/StdLib with an upstream release.

## Known slow paths (not fixed here)

`StringScanner` is O(n) per scan operation, so scanning a large string is
quadratic: `StringScanner#skip` with a Regexp goes through
`StringScanner.RestString()`, which copies the remaining bytes into a fresh
binary MutableString and then decodes all of them to a .NET string for the match.
`_fixedAnchor` mode is no better - `CharIndexOf` slices and counts characters from
the start of the string on every call.

Lexing ruby-bench's 81 KB `negotiate.gql` with tinygql's StringScanner-based
lexer - about 15000 scan operations - takes **1.29 s** on IronRuby against
**0.003 s** on CRuby, a factor of 430.  That is essentially all of tinygql's 121x
and graphql's 241x, the two worst ratios in the table, and everything in the
standard library that scans with StringScanner (csv, erb, rdoc, prism's
`scan_byte` polyfill) pays the same.  Fixing it means matching against the whole
string at an offset - .NET's `Regex.Match(input, beginning, length)` has exactly
StringScanner's non-fixed-anchor anchoring semantics, where `\A` and `^` anchor
at the scan pointer - plus a byte-index/char-index fast path for ASCII-only and
single-byte strings so `CharIndexOf` stops being O(n) too.  Left alone here
because it is a change in a hot shared path rather than a correctness fix.

protoboeuf's 251x is a different shape: its generated decoder is a byte-at-a-time
varint loop (`getbyte`, `<<`, `&`, `|`) with no library call to blame, so it is a
straight read on how fast IronRuby runs small-integer arithmetic in a tight Ruby
loop.  protoboeuf-*encode*, which does the same kind of work but builds strings,
is 20x - the gap between the two is worth a look on its own.
