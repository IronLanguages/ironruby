# TruffleRuby-style benchmark and compatibility suite

The suites TruffleRuby uses to check its own speed and compatibility, run
against IronRuby and CRuby 4.0 side by side, so the same run can be repeated
after any change and compared with the last one.

| Suite | What it is | Runner |
|-------|------------|--------|
| `truffle` | TruffleRuby's `bench/` tree: classic (shootout, deltablue, richards, aobench...), micro (array, hash, string, dispatch, fiber, thread, file...), optcarrot, rubykon, asciidoctor, chunky_png, psd.rb, image-demo, liquid, savina, sinatra - 136 files, 146 benchmarks | here |
| `awfy` | [Are We Fast Yet](https://github.com/smarr/are-we-fast-yet), the cross-language suite in TruffleRuby's papers. Every benchmark checks its own result | here |
| `mri` | CRuby's own `test/ruby/test_*.rb`, the tests TruffleRuby keeps a copy of as `test/mri` | here |
| ruby-bench | CRuby's suite, formerly **yjit-bench**: micro via [`Util/rubybench/run.sh`](../rubybench/README.md); the gem-driven macro benchmarks (optcarrot, rubykon, liquid, hexapdf, graphql...) via `Util/rubybench/pure-gems-run.sh <copy of ruby-bench>` | Util/rubybench |
| ruby/spec | the other half of TruffleRuby's compatibility testing | `Util/parallel-sweep.sh` |

    Util/benchsuite/run.sh --fetch                 # clone the pinned sources, once
    Util/benchsuite/run.sh                         # time truffle + awfy (~35 min)
    Util/benchsuite/run.sh --verify -j 4           # compare answers instead (~15 min)
    Util/benchsuite/run.sh --suite mri -j 4        # CRuby test/ruby
    Util/benchsuite/run.sh --all                   # all three, then ruby-bench micro
    Util/benchsuite/run.sh classic/ optcarrot awfy/Richards    # substring filters
    Util/benchsuite/run.sh --suite awfy --baseline /tmp/benchsuite-20261029-0200
    Util/benchsuite/run.sh --ir-arg -X:NoJIT       # any IronRuby flag
    Util/benchsuite/run.sh --list

`run.sh` defaults to `IR_CONFIG=Release`: a Debug build is ~1.8x slower across
the board and makes every ratio meaningless. It refuses to start if that build
is missing. The driver, `suite.rb`, runs under the host CRuby and starts each
benchmark as a child process of each interpreter.

Nothing is vendored. `--fetch` puts TruffleRuby's `bench/` (sparse) and AWFY at
pinned commits under `BS_SRC` (default `/root/workspace/bench-src`); the CRuby
tests come from the CRuby 4.0.6 checkout at `BS_CRUBY_SRC` (default
`/root/workspace/ruby40`). The sources are never patched. What the suite adds is
loaded with `-r` into both interpreters alike:

- `prelude.rb` - `Primitive.blackhole(x)` (TruffleRuby's "keep this value
  alive") as the identity function, and `$stdout.sync`.
- `verify-prelude.rb` - for `--verify` only, see below.
- `mri-prelude.rb` - for `mri` only: switches off test/unit's leak checker,
  whose fd-leak report walks `ObjectSpace.each_object(IO)`, which IronRuby
  can't do - one leaked fd used to abort the rest of the file.

Every run writes `results-<suite>.tsv`, `summary.txt` and one log per process
to `--out` (default `/tmp/benchsuite-<timestamp>`), never into the repository.
Give an older output directory to `--baseline` and the table gets a `PREV`
column with that run's ratio.

## How a number is produced

**truffle**: benchmark-interface's own `--simple` backend (what `jt benchmark`
uses) runs the block in rounds of about one second for `--time` seconds
(default 5) and prints iterations per second each round. The figure reported
is the median of the second half of those rounds (the steady state, after
warmup), turned into time per iteration so that `RATIO = IronRuby / CRuby`:
1.00 is parity, 3.00 is three times slower, 0.50 twice as fast.

**awfy**: AWFY's `harness.rb` with a large outer-iteration count, stopped after
`2 x --time` seconds; the figure is the median of the second half of the
iterations it printed. Inner iterations are AWFY's `rebench.conf` values / 10,
except CD, Mandelbrot and NBody, which keep a count they know the answer for.

The geomean in the summary is over the benchmarks that ran on both. Interleave
IronRuby and CRuby (the runner does, per benchmark) and look at the load average
printed in the header and summary: on a shared box the absolute numbers move
by tens of percent between runs, so only compare runs taken at similar load.

## Checking the answer: `--verify`

A fast wrong answer is the worst result, and these benchmarks print nothing,
so `--verify` loads `verify-prelude.rb`, which replaces `benchmark` with a
version that runs each block exactly once and prints the value it returned -
class, inspect length, CRC32 of the inspect string with object addresses
normalized, and its first 60 characters. Kernel#rand is seeded first
(IronRuby's MT19937 gives CRuby's numbers for the same seed). A benchmark's own
`.verify { |result| ... }` block, which aobench has, runs as well. AWFY needs
none of this: every iteration raises 'Benchmark failed with incorrect result'
on a wrong answer.

| Verdict | Meaning |
|---------|---------|
| SAME | the same value on both |
| DIFF | different values - a bug until shown otherwise |
| KNOWN | different, for the reason given in `catalog.rb`'s `TRUFFLE_KNOWN_DIFF` (it goes back to SAME by itself once fixed) |
| SKIP | the value legitimately changes run to run (`TRUFFLE_UNSTABLE`) |
| FAIL | IronRuby raised or produced nothing; CRuby did not |
| CRUBY-FAIL / BOTH-FAIL | CRuby failed / both did |
| N/A | not a benchmark of Ruby outside TruffleRuby (polyglot, `TruffleRuby.*`, or a file that defines nothing unless `RUBY_ENGINE == 'truffleruby'`), or broken on CRuby 4.0 itself |

## The `mri` suite

One process per test file and interpreter, `-v --seed=1`, with
`tool/lib` on the load path. test/unit -v names each test before it starts it,
so when a file hangs (per-attempt `--mri-timeout`, default 180s) or kills the
process (IronRuby's deep recursion still dies with "Stack overflow."), the test
that was running is excluded with `-n '!/.../'` and the file is run again,
up to ten times. Reported per file: `IR PASS` and `CR PASS`, the number of
tests that passed on each, and the hung/crashed and failing test names;
`mri-failing.txt` in the output directory lists every IronRuby failure by
name, for diffing two runs.

Status: PASS (no failures), RAN (finished, with failures), TIMEOUT / CRASH (it
never reached its summary even after excluding the tests that hung).

## Status

`catalog.rb` records what is known about individual benchmarks. Latest run:
2026-10-29, IronRuby 4.0.1 Release on .NET 8 vs CRuby 4.0.6 (no YJIT), 8 CPUs,
load average 1-2. Timing took 35 minutes, `mri` 28 minutes at `-j 4`.

**Answers (`--verify`).** Every benchmark that runs computes CRuby's value:
truffle 130 SAME, 2 KNOWN, 4 SKIP, 10 N/A; awfy 13 SAME, 1 N/A. The two KNOWN
are inspect strings, not computations: after normalizing those, rubykon and
savina-apsp produce CRuby's exact output.

**Speed (IronRuby / CRuby, steady state).**

| | geomean | median | faster than CRuby |
|-|--------:|-------:|------------------:|
| truffle (136 benchmarks) | 3.6 | 2.7 | 6 |
| awfy (13) | 2.33 | 2.37 | 0 |
| ruby-bench micro (41) | 2.13 | 2.40 | 5 |
| ruby-bench macro (34 of 36 run) | 6.2 | 4.8 | 0 |

By group: classic 2.0 (mandelbrot 0.17, pidigits 0.80), psd.rb 2.1, micro/string
2.2, chunky_png 3.1, liquid 3.0, rubykon 3.0, optcarrot 4.75, asciidoctor 6.9
(convert 18x, quote-match 15x: regexp-heavy). AWFY is 1.4x (Mandelbrot) to
3.45x (DeltaBlue). The outliers that dominate the geomean:

| Benchmark | ratio | Cause |
|-----------|------:|-------|
| micro/exception/*-stack-100 | ~1320 | raise through 100 frames: a Ruby raise is a .NET throw, whose cost grows with the frames it unwinds |
| micro/exception/*-small-stack | ~137 | the same, shallow stack |
| micro/fiber/chain, create | 233, 86 | each Fiber runs on its own thread, switched by a Queue handoff (`ironruby/ruby4.rb`) |
| core-array-rotate-inplace-* | 20-156 | Array#rotate! is Ruby in `ironruby/ruby4.rb`: `replace(rotate(n))`, a copy per call |
| core-string-index-1000000 | 80 | String#index with a large start offset |
| micro/array/set-span-no-move | 48 | `a[i, n] = same-length array` |
| micro/random/random, monitor/synchronize | 12, 10 | |

ruby-bench (rerun later the same day, after the C-extension work - see
`Util/rubybench/README.md`, which also compares what TruffleRuby and JRuby do
for each C-extension gem): optcarrot 4.0x, rubykon 2.9x, liquid 4.4-5.1x,
fluentd 1.9x, shipit 5.0x, lobsters 20x, railsbench 23x, ruby-lsp 48x, rubocop
77x, protoboeuf 243x. Only psych-load and sequel do not run: their Gemfiles
demand psych 4 and sqlite3 1.x, older majors than IronRuby provides.

**CRuby `test/ruby` (`mri`).** IronRuby passes 4444 of the 10220 tests CRuby
passes (43.5%): 25 files PASS, 100 RAN, 11 CRASH, 6 N/A (they need CRuby's C
test extensions). The crashes are mostly a single missing piece that stops a
whole file before its first test:

- no `continuation` library: test_hash (297 tests), test_enum, test_fiber,
  test_settracefunc, test_beginendblock, test_continuation
- `RbConfig::CONFIG["EXECUTABLE_EXTS"]` is nil: all of test_rubyoptions
- `Set::CoreSet` missing: test_set; `RubyVM` missing: test_iseq (expected)
- test_syntax:219 (`no implicit conversion of nil into Array`), test_thread_queue
  (`remove_method :marshal_dump` on a Queue subclass)

Hangs and process kills that the runner excluded so the rest of each file ran:
TestArray#test_join2 and TestException#test_stackoverflow (deep recursion still
ends in "Stack overflow."), TestArray#test_cycle, TestRange#test_count /
test_comparison_when_recursive / test_cyclic_range_inspect,
TestModule#test_prepend_after_include / test_include_with_prepend,
TestThread#test_thread_join_main_thread / test_thread_join_current,
TestRegexp#test_cache_opcodes_initialize, TestMarshal#test_marshal_dump_recursion,
TestSuper#test_double_include2.

Found through `--verify`, also cosmetic: a recursive object inspects as `...`
rather than `#<Foo:0x... ...>`; Thread::Queue, SizedQueue, Mutex and
ConditionVariable#inspect print the CLR class name; object addresses in
`#inspect` have 7 hex digits where CRuby has 16.
