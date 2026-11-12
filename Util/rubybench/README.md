# ruby-bench under IronRuby

[ruby-bench](https://github.com/ruby/ruby-bench) is CRuby's own benchmark suite
(what used to be yjit-bench). This directory runs it against IronRuby and
against CRuby 4.0.6 side by side, so a change to IronRuby can be judged on
whether it made real Ruby code faster, and so that a benchmark that stops
working is noticed.

    Util/rubybench/run.sh                     # every micro benchmark
    Util/rubybench/run.sh fib nbody           # just these
    Util/rubybench/run.sh --group all         # micro + macro
    Util/rubybench/run.sh --verify            # compare the answer, not the time
    Util/rubybench/run.sh --nojit             # extra column: IronRuby -X:NoJIT
    IR_CONFIG=Release Util/rubybench/run.sh   # measure the optimized build

ruby-bench is not vendored. Point the runner at a checkout with `RB_DIR`
(default `/root/workspace/ruby-bench`); the runner never writes inside it,
beyond what a bundled benchmark does to its own `Gemfile.lock`. Results go to
stdout and the per-run logs to a temp directory - no CSV is written into this
repository.

`catalog.rb` is the list of benchmarks: name, group (`:micro` or `:macro`), and
what is known about running it here. `--group` selects from it, so the macro
benchmarks can be added to the catalog without touching the runner.

## How a number is produced

Each benchmark runs in a fresh process per interpreter. ruby-bench's own
harness prints one `#N: NNNms` line per iteration; the runner drops the warmup
iterations (`RB_WARMUP`, default 5) and reports the fastest of the rest
(`RB_ITRS`, default 5). Minimum rather than mean: the noise on a timed loop is
all one-sided, so the best iteration is the honest "how fast can this go", and
it is the statistic `Util/bench/run.sh` already uses.

`RATIO` is IronRuby divided by CRuby, so 1.00 is parity and 5.00 means five
times slower.

Statuses are meant literally:

| Status  | Meaning |
|---------|---------|
| OK      | ran, and produced iteration times |
| FAIL    | raised, or produced no times |
| TIMEOUT | did not finish within `RB_TIMEOUT` (default 600s) |
| N/A     | cannot run here; the note says why, in one line |

N/A is for something IronRuby genuinely cannot do (`Process.fork`), never for a
bug. A benchmark that fails because IronRuby is wrong is a FAIL to be fixed.

## Checking the answer, not just the exit code

A fast wrong answer is the worst outcome, and almost none of these benchmarks
print anything, so exiting 0 proves very little. `--verify` runs each benchmark
once under both interpreters with a replacement harness - generated into the log
directory, so ruby-bench itself is untouched - that calls the benchmark's block
exactly once and prints the value it returned. The two are then compared:

    BENCHMARK                GROUP  VERDICT   NOTE
    fib                      micro  SAME      Integer len=7 2178309
    nqueens                  micro  SAME      Integer len=2 10
    blurhash                 micro  SAME      String len=30 "LFE.@D9F01_2%L%MIVD*9Goe-;WB"

Object addresses are normalized out. Four benchmarks have no comparable value
(they return a live thread, a freshly built tree, a splay tree whose shape
embeds object ids, or an SVG document); they are listed as SKIP with the reason
in `catalog.rb`'s `UNSTABLE_VALUE`, and three of the four raise on a wrong
answer by themselves.

## RSS is missing, and that is not a failure

ruby-bench's `harness-common.rb#get_maxrss` calls `Fiddle::Function`, which
IronRuby does not have yet. `return_results` therefore raises a `NameError` at
the very end of every IronRuby run - *after* the iterations have been timed and
printed. The runner recognizes exactly that one error, keeps the timings, and
says once at the bottom of the table that the memory figures are unavailable.
Nothing in ruby-bench is patched to work around it: when `Fiddle::Function`
lands, the runs simply stop raising.

## Status

Rerun 2026-10-29, `IR_CONFIG=Release`, the runner's defaults (5 warmup, 5 timed
iterations), CRuby 4.0.6, load average about 3. All 41 microbenchmarks run and
every one with a value to compare produces CRuby's answer (`--verify`); one is
N/A (`--verify`, rerun the same day: 37 SAME, 4 SKIP, 0 DIFFER). Geomean IronRuby/CRuby 2.13, median 2.40, five faster than CRuby. Times
are milliseconds for the fastest timed iteration.

```
BENCHMARK                GROUP     IR(ms) CRUBY(ms)   RATIO STATUS   NOTE
30k_ifelse               micro       5205      1284    4.05 OK
30k_methods              micro       4011       844    4.75 OK
30k_variables            micro       5777       910    6.35 OK
attr_accessor            micro        339       171    1.98 OK
block_methods            micro      13436      3255    4.13 OK
cfunc_itself             micro         74        83    0.89 OK
fib                      micro         35       216    0.16 OK
gcbench                  micro       7646      2642    2.89 OK
getivar                  micro        257        94    2.73 OK
getivar-module           micro        594       239    2.49 OK
keyword_args             micro       2454       235   10.44 OK
loops-times              micro       2169      1113    1.95 OK
matmul                   micro        753       471    1.60 OK
nqueens                  micro        315       200    1.57 OK
object-new               micro         47        77    0.61 OK
object-new-initialize    micro        388       151    2.57 OK
object-new-no-escape     micro        579       283    2.05 OK
respond_to               micro        316       251    1.26 OK
ruby-xor                 micro        111       116    0.96 OK
send_bmethod             micro        924       237    3.90 OK
send_cfunc_block         micro       1150       330    3.48 OK
send_rubyfunc_block      micro        731       156    4.69 OK
send_rubyfunc_inline     micro       2147       630    3.41 OK
setivar                  micro        128        72    1.78 OK
setivar_object           micro        122        98    1.24 OK
setivar_young            micro        113        99    1.14 OK
splay                    micro        693       108    6.42 OK
str_concat               micro        142        66    2.15 OK
string_malloc_pressure   micro         60       493    0.12 OK
structaref               micro        284       134    2.12 OK
structaset               micro        177       122    1.45 OK
struct-new-no-escape     micro        989       178    5.56 OK
sudoku                   micro        680       434    1.57 OK
throw                    micro         65        24    2.71 OK
binarytrees              micro       1202       441    2.73 OK
fannkuchredux            micro        704       375    1.88 OK
nbody                    micro        286       119    2.40 OK
gvl_release_acquire      micro        190       140    1.36 OK
ruby-json                micro       1265       306    4.13 OK
blurhash                 micro       1073       329    3.26 OK
lee                      micro       4529      1264    3.58 OK       needs the victor gem; first run installs it
knucleotide              micro          -         -       - N/A      uses Process.fork for parallelism; .NET cannot fork
```

Faster than CRuby: `fib` (0.16) and `cfunc_itself` (0.89), which the method JIT
compiles into plain .NET calls; `string_malloc_pressure` (0.12), because
`String.new(capacity:)` is a hint IronRuby need not honour with a real
allocation; `object-new` (0.61), a plain allocation loop; and `ruby-xor`
(0.96), at parity.

What the outliers say:

* `keyword_args` (10x) is the one call-shape benchmark still well out of line;
  what is left is the per-call binding of the keyword hash. `respond_to` is at
  parity since the resolution cache (it was 11x).
* `struct-new-no-escape` (5.6x) and `30k_variables` (6.4x, dominated by
  compiling 300 methods of 100 locals) are the next ones.
* Instance-variable access is 1.1x-2.7x now (`getivar`, `setivar*`; 6x before).

The macro benchmarks are in [pure-gems.md](pure-gems.md) (34 of 36 run;
geomean 6.2x, median 4.8x, counting the web ones) and [web-gems.md](web-gems.md).
The two that do not run, psych-load and sequel, are not missing code: their
Gemfiles demand psych 4 and sqlite3 1.x, older majors than the ones IronRuby
provides, and Bundler.setup says so.

`--nojit` is worth running when a change touches the compiler: a benchmark
where `-X:NoJIT` wins is a benchmark the method JIT is costing.

## C extensions: what IronRuby, TruffleRuby and JRuby do

Most of the macro benchmarks that used to be N/A here were N/A for one C
extension somewhere in their bundle. The other two alternative Rubies solve that
in opposite ways:

* **TruffleRuby** compiles a gem's C extension as it is, against its own
  implementation of CRuby's C API, so markly, rbs, strptime, yajl-ruby and
  cool.io simply build there. It patches very little: `lib/truffle/truffle/patches`
  holds build fixes for json, oci8, pg and tk only, and `lib/patches` adjusts a
  few gems' Ruby (thread_safe among them). stackprof ships its own TruffleRuby
  backend inside the gem.
* **JRuby** cannot load C at all. It relies on gems publishing a `-java`
  platform build with a Java extension, or on a pure-Ruby fallback in the gem.
  Of the gems below, rubygems.org has `-java` builds of bcrypt, msgpack, puma,
  nio4r, nokogiri, websocket-driver, http_parser.rb, jaro_winkler and (since
  4.2.1.pre) rbs, whose Java build runs rbs's C parser compiled to WebAssembly.
  markly, strptime, yajl-ruby, cool.io, io-event, zstd-ruby, sqlite3,
  liquid-c and graphql-c_parser have none; JRuby apps use the JDBC adapter
  instead of sqlite3.

IronRuby cannot load C either, so it takes JRuby's route, with IronRuby
providing what the `-java` build would: the gem's own Ruby is used as
installed, and only the file its `lib/` requires for the extension
(`markly/markly`, `rbs_extension`, `cool.io_ext`, ...) is IronRuby's. A `.rb` on
the load path wins over a `.so`, so a CRuby-built copy of the gem next to it
does not get in the way. Where the C is a self-contained library of its own, it
is built from the gem's pinned sources and called through P/Invoke, as libprism
already was, so the output is the same bytes by construction.

| Gem | Needed by | TruffleRuby | JRuby | IronRuby |
|-----|-----------|-------------|-------|----------|
| bcrypt | lobsters | builds the C | `-java` (jBCrypt) | C# port (`Src/Libraries/BCrypt`) |
| markly 0.15 | lobsters | builds the C | none | the gem's cmark-gfm as `libmarkly_cmark` (`Util/build-markly.sh`) + markly.c in C# |
| jaro_winkler | lobsters (rubocop 0.81) | builds the C | `-java`, and the gem's pure-Ruby fallback | the gem's pure-Ruby fallback |
| rbs 4.0.0.dev.5 | ruby-lsp | builds the C | `-java` from 4.2.1.pre (C parser as WASM) | the gem's parser as `librbs` (`Util/build-librbs.sh`) + the glue in C# |
| strptime | fluentd | builds the C | none | C# port |
| yajl-ruby | fluentd | builds the C | none | C# port of yajl and yajl_ext |
| zstd-ruby | fluentd | builds the C | none | C# port over ZstdSharp (managed libzstd 1.5.7) |
| cool.io | fluentd | builds the C | none | C# event loop over poll(2) / inotify |
| io-event | fluentd (async) | builds the C | none | the gem's pure-Ruby selector |
| msgpack, puma, nio4r, sqlite3, nokogiri | shipit, lobsters | build the C | `-java` / JDBC | C# (earlier work) |
| pty (stdlib) | shipit | built in | built in | C#, posix_spawn with a new session |
| liquid-c, graphql-c_parser | liquid-c, graphql-native | build the C | none | not done: these benchmarks measure the C extension itself |

## Environment

| Variable | Meaning |
|----------|---------|
| `RB_DIR` | ruby-bench checkout (default `/root/workspace/ruby-bench`) |
| `IR_CONFIG` | `Debug` (default) or `Release` - measure in Release |
| `IR` / `RUBY` | the two interpreters (default `./ir.sh` and `ruby`) |
| `RB_TIMEOUT` | seconds per run (default 600) |
| `RB_WARMUP` / `RB_ITRS` | warmup and timed iterations (default 5 and 5) |
| `RB_MIN_TIME` | minimum seconds of timed work per run (default 0) |
| `RB_LOG_DIR` | where the per-run logs go |
| `GEM_HOME` | gem home for the benchmarks that bundle (default `<repo>/.gems`) |
