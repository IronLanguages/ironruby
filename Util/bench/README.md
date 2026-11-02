# Benchmarks and the experimental compilers

`run.sh` times `bench.rb`'s ~50 micro/medium benchmarks against CRuby and prints the ratio.

    Util/bench/run.sh                 # Debug build (what ir.sh runs by default)
    IR_CONFIG=Release Util/bench/run.sh
    IR=/path/to/other/ir.sh Util/bench/run.sh

Measure in **Release**: the optimized build is ~1.76x faster across the whole suite
(geomean 8.26x -> 4.69x of CRuby 4.0.6), because `Optimize=false` leaves the whole
runtime unoptimized for the .NET JIT. `ir.sh` honours `IR_CONFIG=Release`.

## -X:JIT - method JIT

Type-specializes a hot method body: unboxed int/long/double locals and CLR arithmetic, no
RubyMethodScope, direct self-calls for recursion. Guards on one global method-table
version (covers redefinition, override, singleton, prepend, include, alias, and a method
coming or going in a refinement); the operators and self-calls a body binds are resolved
under the refinements active where it was defined, so a refinement elsewhere costs it
nothing; overflow out of Int64 and division by zero deopt to the generic body, so a body
that can deopt and has a side effect is never specialized.

An Integer is carried as a CLR `int` or `long` following the runtime's own rule - Int32 if
the value fits in one, otherwise Int64 - and every value that leaves the specialized region
goes back through that funnel, so a value the JIT produced is `eql?`, hashes and Marshals
exactly like the same value produced anywhere else. `int op int` that overflows produces a
long rather than deopting (two Int32s never overflow a 64-bit operation, so that case needs
no check at all); only the step out of Int64 deopts, because a BigInteger result would make
the static type of every arithmetic result `object`, which is no specialization at all.

`fib` 8.7x faster, `ackermann` 8.6x - both at or past CRuby; the call family ~2x.

## -X:OSR - on-stack replacement

Replaces a running `while`/`until` loop with a type-specialized copy of itself, entered
over the same locals tuple (so there is no state to materialize). This is what reaches
loops in a block that is only ever entered once - the shape no invocation-counting JIT
can see.

`while_loop` 0.78x, `cmp_branch` 0.38x, `float_arith` 0.26x, `mandelbrot` 0.44x of CRuby,
and `int_arith` 0.57x once the lattice learned Int64 (see below).

A type-guard failure rebuilds the copy over the types the locals hold now, so a loop whose
accumulator grows out of Int32 is re-specialized rather than abandoned.

Note: outlining a loop *without* specializing it is worth nothing - IronRuby's compiled
tier is no faster than its interpreter here, because the cost is the dynamic call site on
every operator, not interpretation.

## Both are on by default

The full sweep (core library language command_line security) gives identical results with
them on and off, and IronRuby's C# test suite is unchanged, so they are the default. Turn
them off to compare, or if you suspect one of them:

    ./ir.sh -X:NoJIT -X:NoOSR script.rb

A loop whose locals change representation under it - an accumulator crossing 2^31 - deopts
once, and the site then builds a second copy over the types the locals hold *now* rather
than handing the loop back for good. That is what makes `int_arith` move: 3.61x of CRuby
before, 0.57x after.

## Known limits

- `times`/`each`/`upto` loops are blocks, not `while` loops, so -X:OSR does not see them.
- A *method* that deopts is not re-specialized the way a loop is: its entry stays installed
  and every later call whose argument types no longer match falls through to the generic
  body. Only -X:OSR rebuilds.
- `raise_rescue` (~150x) and `fiber_switch` (~237x) are unrelated to both: exception
  backtrace construction, and a real CLR thread per Fiber.

## Fiber switches and parked-thread GC cost

`fiber.rb` measures resume/yield round trips, create-and-complete time, and forced
full collections while fibers remain suspended. Run each GC population in its own
process. The benchmark resumes all parked fibers afterward, including on error.

```sh
IR_CONFIG=Release IR_TFM=net10.0 ./ir.sh Util/bench/fiber.rb switch 100000
IR_CONFIG=Release IR_TFM=net10.0 ./ir.sh Util/bench/fiber.rb gc 0
IR_CONFIG=Release IR_TFM=net10.0 ./ir.sh Util/bench/fiber.rb gc 1000
IR_CONFIG=Release IR_TFM=net10.0 ./ir.sh Util/bench/fiber.rb gc 10000
```

Use the framework you built. The switch benchmark warms both paths before timing.
The GC mode takes 20 samples after three warmup collections; the optional third
argument changes that sample count. These are wall-clock durations of `GC.start`,
including its Ruby call overhead, rather than runtime GC pause-event measurements.
They do not measure young-generation collection costs or scheduler throughput.

Initial switch-only measurements on Linux x64, Release, .NET 10.0.14, October 2026,
before adding handle reclamation. Handle tracking adds switch bookkeeping and
per-fiber registrations; these initial figures do not measure the final reclaimer:

| Operation | Original Ruby switch | C# FiberCore switch |
| --- | ---: | ---: |
| Resume/yield round trip | 3.95 us | 3.75 us |
| Create and complete | 14.24 us | 13.65 us |

Values are medians of five alternating fresh-process runs, 100,000 operations
per run. The original Ruby prelude ran against the same assemblies using the
retained mailbox primitives. This isolates the switch change, but does not replace
a comparison on other runtimes and machines.

| Parked fibers | Original upper-middle collection sample |
| ---: | ---: |
| 0 | 9.33 ms |
| 1,000 | 29.81 ms |
| 10,000 | 170.80 ms |

These historical GC figures use the upper-middle value of 20 samples, as the
initial benchmark did. The corrected benchmark averages the middle pair for even
sample counts and uses the middle value for odd counts.

The C# core combines suspension, posting, owner restoration, and the resumed state
update. It replaces tagged Ruby message arrays with typed messages and packs yield
arguments in C#. Ruby still validates transitions, starts workers, and handles
exception and kill semantics. Messages still allocate and live suspended fibers
still occupy CLR threads.

## Abandoned fiber reclamation

The Ruby `Fiber` is now a handle over private execution state. Worker frames and
mailboxes retain that state; the C# core keeps a weak reference to the handle.
The current handle is strong while user code runs. Resume, raise, and kill waits
also pin the resumer's handle until control returns, so collection cannot break
an active resume chain. Yield and transfer waits remain collectible. Collection
of a discarded handle wakes the parked worker with a private control-flow
exception. Ruby `rescue` cannot catch it and
Ruby `ensure` bodies, TracePoint hooks, signal handlers, and Ruby finalizer
callbacks do not execute during reclamation. CLR cleanup finally blocks still
execute. If CLR code catches the signal, the next Ruby entry throws it again;
exceptions replacing it during CLR cleanup also bypass Ruby rescue. The worker
then returns to the bounded idle pool or exits.

Captured scopes release completed interpreter frames so factory return values
cannot accidentally keep their handles alive through stale temporary operands.
Native Enumerator iteration uses a detached source carrying the same target or
generator. Custom `each` receivers retain their original identity and can still
retain their fiber through their suspended frames.

Program-owned references are still roots. A handle stored in a captured local,
fiber-local storage, or an object reachable from its own stack can keep that stack
alive, including an unused local retained by a captured scope. Such fibers need
explicit completion or `kill`, or those references must be cleared. This change
does not provide CLR stack collection, virtual threads, or smaller live fibers.

Run the regression matrix with:

```sh
IR_CONFIG=Release IR_TFM=net10.0 ./ir.sh Util/fiber-reclaim-matrix.rb
IR_CONFIG=Release IR_TFM=net10.0 ./ir.sh -X:NoAdaptiveCompilation Util/fiber-reclaim-matrix.rb
```

The matrix checks handle collection, dropped generator Enumerators, factory
scope retention, nested ensure/rescue suppression, worker reuse, reachable and
running handle identity, ordinary kill/raise and tracing, copied handles, custom
Enumerator receiver identity, captured locals, and repeated waves of discarded
workers. It also checks nested resume/raise/kill waits, released resumer pins,
and collectible transfer waits, and waits for an idle reclaimed worker to retire.
Each collection check has a deadline and checks worker termination rather than
assuming that one GC is enough.

`Fiber_ReclamationClrBoundary1` in the C# test suite exercises a CLR catch that
swallows the signal and a CLR finally that replaces it with another exception.
Both must finish native cleanup without entering another Ruby callback, rescue,
or ensure body. This check passes with adaptive and fully compiled execution.
