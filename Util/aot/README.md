# Ahead-of-time IronRuby: Ruby -> IL in a saved DLL, and NativeAOT

This is a research prototype. Nothing in the main build or solution depends on `Util/aot`.
The short answers:

- **A. Ruby -> IL in a saved DLL: yes, it works.** `IronRuby.Aot compile` turns a Ruby
  program, and optionally IronRuby's own Ruby-level prelude (`ruby4.rb` and the files it
  requires, about 15k lines), into a .NET assembly. The assembly's methods are the compiled
  Ruby. A stock `dotnet app.dll` runs it with **zero Ruby parser calls**; a tripwire checks this.
- **B. The interpreter under NativeAOT: yes, it works, but only as an interpreter.** A 35-39 MB
  native `ir` runs the prelude and the samples. Getting there needed a DLR patch, trimming
  roots and a host change. Call-site rules can never be compiled to IL, so they are always
  interpreted.
- **C. The two combined: yes, it works.** Ruby -> IL -> ILC produces one native executable
  (`demo-native`, 51 MB). It starts with no parser and no JIT. Method dispatch still goes
  through DLR call sites whose rules are built and interpreted at run time.

All numbers below were taken on a shared 8-core box with a load average of 60-90 from other
agents. Wall times are very noisy. CPU times are a bit better but still inflated. Treat the
numbers as rough.

## A. Ruby -> IL in a saved assembly

### What was known, and what was verified

- `LambdaExpression.CompileToMethod` does not exist on .NET Core. The DLR still has the old
  save-to-disk infrastructure (`SavableScriptCode`, `ToDiskRewriter`, `LegacyScriptCode`), but
  it is compiled out (`FEATURE_LAMBDAEXPRESSION_COMPILETOMETHOD`) because it calls the missing
  API. The DLR's `ILGen` is only an emit helper. **The DLR carries no expression-tree compiler
  of its own.**
- Existing ports:
  - FastExpressionCompiler has `CompileFastToIL(ILGenerator)`. It only handles lambdas without
    closures: its nested lambdas and constants go through its own runtime closure objects.
    IronRuby's blocks are closures, so it does not fit.
  - The .NET Framework compiler in the MIT-licensed Microsoft Reference Source
    (`System.Core/Microsoft/Scripting/Compiler`) still has the MethodBuilder code path. That
    code path is what CompileToMethod used, including nested lambdas as private static
    methods closed over a `Closure` argument. **I ported that one.**

### The tree census (`IronRuby.Aot census file.rb`)

The census does two things:

- It transforms files as `ir` would. It also transforms every `def` body, which IronRuby
  otherwise compiles lazily from the AST at the first call.
- It reduces extension nodes and counts what a saved assembly cannot hold.

| | `samples/demo.rb` | `ruby4.rb` (the prelude) |
|---|---|---|
| expression nodes | 1,822 | 356,678 |
| lambdas / `def` bodies | 9 / 4 | 1,747 / 1,076 |
| dynamic call sites | 27 | 10,827 |
| live-object constants | 57 (14 kinds) | 14,373 (22 kinds) |
| calls to non-public members | 0 | 0 |

The live objects in `ruby4.rb`, and how each is re-created at load time:

| Live object | Count | Re-created as |
|---|---|---|
| `RubyContext` | 2,137 | `AotRuntime.Context` (one runtime per process) |
| `ConstantSiteCache` / `IsDefinedConstantSiteCache` | 2,117 / 20 | `new ...()` |
| `RubyEncoding` | 2,077 | its `IExpressionSerializable`, or a well-known static field |
| `RubySymbol` | 1,914 | `context.CreateSymbol(bytes, encoding)` |
| `WeakReference` | 1,911 | the `ConstantSiteCache.WeakMissingConstant` static field (found by identity) |
| `string[]` (local variable names) | 1,463 | array literal |
| `RubyMethodBody` | 1,076 | see below; this is the hard one |
| `BlockDispatcher*` | 970 | `BlockDispatcher.Create(...)` from its private fields, plus the parameter signature |
| `CallSite<T>` constants | 482 | a static `CallSite<T>` field made with `Create(binder)` |
| `BinaryOpStorage` | 94 | `new BinaryOpStorage(context)` |
| `StrongBox<RubyRegex>` / `StrongBox<MutableString>` (empty caches) | 41 / 28 | `new StrongBox<T>()` |
| `Range` (integer literal) | 39 | `new Range(int, int, bool)` |
| `byte[]` | 4 | array literal |

Every binder is a `RubyMetaBinder` and therefore implements `IExpressionSerializable`. All
of them describe themselves as `X.MakeShared(...)`. The compiler rewrites that to the
runtime-bound `X.Make(context, ...)`, which avoids the cross-runtime checks shared binders
put in their rules.

One call site has more than 16 arguments. Its delegate type is created in an in-memory
assembly. The compiler re-makes it in a tiny saved assembly of its own
(`<name>.Delegate0.dll`) and loads that back.

**This list decides feasibility, and it is short and tractable.** The one real design
problem is `RubyMethodBody`. Two things make it hard:

- A `def` is compiled from the AST at the first call.
- The declaring scope and module are baked into that compilation as constants.

The prototype solves it in three steps:

1. It transforms each body with the scope and module as lambda parameters, producing a
   factory `(RubyScope, RubyModule) => Delegate` that is compiled into the assembly.
2. It keeps only a stub AST at run time: a header with the name, parameters, signature,
   location and `UsesBlock`. `Method#arity`, `#parameters`, dispatch and similar features
   read from that header.
3. It adds a `RubyMethodBody` constructor that takes the factory in place of compiling the
   AST.

### What the compiler does (`IronRuby.Aot compile`)

1. **Parse and transform** with prism, exactly as `ir` does. This also transforms every
   `def` body as described above.
2. **Rewrite** (`AotCompiler.Rewriter`):
   - Every extension node is reduced.
   - Every live constant becomes a static field. The field is initialized **lazily on first
     use** (`field ?? (field = init)`), because eager initialization of the prelude's
     ~19.6k constants cost 2.3-3.4 s before any Ruby ran; lazy initialization brought that
     to 0.8 s.
   - Every `DynamicExpression` becomes `site.Target(site, args)` over a static
     `CallSite<T>` field.
3. **Compile** with the ported LambdaCompiler (`ExpressionCompiler/`, about 10.8k lines) into
   `PersistedAssemblyBuilder` methods. The assembly is saved with an entry point, a
   `runtimeconfig.json` and the IronRuby DLLs, but without prism.
4. **Run** through `AotRuntime.Main`:
   - It sets up the runtime the way `ir` does.
   - It registers the precompiled library files with the loader
     (`Loader.RegisterPrecompiledFile`). `require` still resolves the file on the load path,
     then runs the compiled code instead of parsing the file.
   - It runs the main file.
   - `RubyContext.AlternativeParser` is replaced by a counter. `IR_AOT_FORBID_PARSE=1` makes
     any parse fatal.

Porting notes:

- The Framework compiler used internal members of the expression node classes. These are
  re-expressed over the public API with C# 14 extension members (`Compat.cs`). The
  `StackSpiller` constructors became public factories.
- `Closure` and `RuntimeOps` are not public on .NET Core, so the saved IL references
  `IronRuby.Aot.Runtime.Closure`.
- **.NET 10 bug found:** `ControlFlowBuilder.CopyCodeAndFixupBranches` (System.Reflection.Metadata,
  used by `PersistedAssemblyBuilder` at save time) corrupts the IL when a short branch's
  operand is the last byte of a BlobBuilder chunk. Saving `ruby4.rb` failed with
  `ArgumentOutOfRangeException ('start')`. The workaround is to emit only long branch forms.

Changes to IronRuby itself. All are small and internal; none changes the behaviour of a
normal build.

- `InternalsVisibleTo("IronRuby.Aot")`.
- A `MethodDefinition.TransformBody` overload that takes the scope and module as
  expressions. The old overload now calls it.
- A precompiled-factory constructor on `RubyMethodBody`.
- `Loader.RegisterPrecompiledFile`.
- For part C: in `MethodDispatcher`, a check that applies only when dynamic code is
  unsupported (see below).

### Reproduce

```sh
dotnet build Util/aot/IronRuby.Aot -c Release -p:DlrSourceDir=/root/workspace/dlr/src/core
A=Util/aot/IronRuby.Aot/bin/Release/net10.0/IronRuby.Aot
$A census Util/aot/samples/demo.rb Src/StdLib/ironruby/ruby4.rb
$A selftest /tmp/st                                   # the ported compiler alone: closures, try/catch, switch
$A compile --prelude -o /tmp/demo Util/aot/samples/demo.rb
IR_AOT_TRACE=1 IR_AOT_FORBID_PARSE=1 dotnet /tmp/demo/demo.dll   # "parser calls: 0"
Util/aot/build-samples.sh /tmp/ironruby-aot            # hello/demo/bench x {noprelude, prelude, r2r}
python3 Util/aot/measure.py -n 5 'a=cmd' 'b=cmd'       # best-of-N wall and CPU
```

`--r2r` runs crossgen2 (ReadyToRun) over the saved assembly and over IronRuby's assemblies.
`--eager-init` switches back to initializing all constants at load time.

### Results

All five samples produce the same output as `./ir.sh`:

- `demo.rb`: fib, a class with state, blocks, string interpolation, an exception.
- `errors.rb`: backtraces and an uncaught error, including the exit code.
- `hello.rb`, `bench.rb`, `loop.rb`.

Backtrace frame labels (`Widget#explode`, `block in ...`, `<main>`) are correct. Paths are
absolute, where `ir` prints them relative.

Output sizes:

| Build | `.dll` size |
|---|---|
| `demo.rb` alone | 21 KB |
| with the prelude (lazy initialization) | 4.3 MB |
| with the prelude, ReadyToRun | 13.3 MB |

Compiling with the prelude takes 15-21 s. It produces about 19.6k constant fields, 12,328
call sites and 1,273 `def` bodies compiled ahead of time.

**Startup, `hello.rb`, best of 5 (load average ~90):**

| | wall | CPU |
|---|---|---|
| `./ir.sh` Release net10.0 | 9.60 s | 4.02 s |
| `./ir.sh` ReadyToRun (`Util/publish-r2r.sh`) | 6.60 s | 2.92 s |
| AOT dll + prelude | 7.30 s | 3.07 s |
| AOT dll + prelude + `--r2r` | **3.75 s** | **1.50 s** |

The `noprelude` row is not a fair comparison and is left out. It ran in 1.20 s wall /
0.44 s CPU, but without `ruby4.rb` there is no `Array#sum`, so `hello.rb` failed.

Where the time goes (single `IR_AOT_TRACE` runs, so unsure):

- **Without R2R:** 4,574 methods were JIT-compiled, taking 4.0 s of JIT time.
- **With `--r2r`:** the runtime was ready in 0.29 s, and the `ruby4.rb` top level took
  1.47 s. 908 methods were still JIT-compiled (0.7 s). In another run, 376 of the 961
  JIT-compiled methods were `CallSite.Target` rule delegates, which the DLR compiles at run
  time.

**The IL is there. Startup is now bounded by run-time rule creation, not by parsing.**

**Steady state, `bench.rb`, best round of 5, in ms. UNSURE: runs varied up to 3x between
repeats under this load:**

| | fib(25) | vectors | strings |
|---|---|---|---|
| CRuby 4.0.6 | 26 | 383 | 143 |
| `ir` default (`-X:JIT`/`-X:OSR` on) | 2.3 | 804 | 588 |
| `ir -X:NoJIT -X:NoOSR` | 203 | 1,291 | 368 |
| AOT dll (3 runs) | 199-248 | 681-1,071 | 338-1,291 |

The AOT dll is roughly on par with `ir` without its method JIT. **It cannot use `-X:JIT`:**
that JIT specializes from the `def`'s AST body, which the saved assembly does not have. So
`fib` is about 100x slower than `ir`'s default.

## B. The interpreter under NativeAOT

To build and run it (the DLR patch is applied to a private copy; the shared checkout is not
touched):

```sh
Util/aot/NativeAot/make-dlr-copy.sh /tmp/dlr-aot
dotnet publish Util/aot/NativeAot/ir-native.csproj -c Release -p:DlrSourceDir=/tmp/dlr-aot/src/core
Util/aot/NativeAot/ir-native.sh -X:NoJIT -X:NoOSR --disable-gems Util/aot/samples/demo.rb
```

`ir-native.csproj` builds the same host with `PublishAot`, rooting IronRuby, the DLR and
prism. With `-p:PublishAot=false` it builds a JIT binary that takes the same no-dynamic-code
paths, which is useful for debugging. `IR_FIRST_CHANCE=N` prints the first N distinct
exceptions; that is how the walls below were found.

The publish produces 292 IL3050 warnings (`RequiresDynamicCode`) plus about 330 trimming
warnings.

**Walls, in the order they were hit:**

1. **`System.Configuration`.** `ScriptRuntimeSetup.ReadConfiguration` fails under AOT. Fix:
   the host builds its `ScriptRuntimeSetup` directly.
2. **UTF-7 disabled.** A `RuntimeHostConfigurationOption` does not override the SDK's baked-in
   feature switch. Fix: `<EnableUnsafeUTF7Encoding>true</EnableUnsafeUTF7Encoding>`.
3. **`LightLambda.MakeRun5<...>` has no native code.** `MakeGenericMethod` over value types
   fails under AOT. This is the IL3050 wall that sits on every call site. It is fixed by
   `Build/dlr-patches/aot/0001-interpreter-only-without-dynamic-code.patch`. When
   `RuntimeFeature.IsDynamicCodeSupported` is false, the patch changes three things:
   - `LightLambda` falls back to an `object[]` thunk for delegate types that have no native
     instantiation. System.Linq.Expressions interprets the thunk.
   - Lambda and loop compilation thresholds are set to infinity. Compiling there only hands
     the code to System.Linq.Expressions' own interpreter, which is slower.
   - `CallInstruction` uses reflection invoke for signatures that contain value types,
     instead of trying the generic `FuncCallInstruction<...>` and catching the failure.

   In short, **call-site rules (the binders' output) are always interpreted.** This answers
   the question in the brief: there is a fallback, and it is the DLR's own interpreter.
4. **CLR interop hits the trimmer.** `ruby4.rb` calls `System::Type.get_type`, `System::Math`,
   `System::GC` and similar APIs through reflection, and IronRuby's binders look up
   conversion operators and members by name. Fix: `NativeAot/roots.xml` lists the needed
   types one by one.

   Rooting `System.Private.CoreLib` whole does not link (`undefined reference to
   RhIsGCBridgeActive`). So **any Ruby program that uses other .NET types through interop
   needs them rooted by hand.** This is inherent to trimming.
5. **Prelude load took minutes, then segfaulted.**
   - Every missing generic instantiation surfaced as a caught exception
     (`MissingTemplateException`, "missing native code").
   - Under that load, backtrace handling looped
     (`Exception#set_backtrace` -> `Location.__parse__` -> `MatchData#[]`).
   - `hello.rb` took 562 s wall and then segfaulted. The segfault was not investigated
     further; it went away with the wall 3 and 4 fixes.

**Results with all fixes (single runs, UNSURE):**

- The native binary is 35-39 MB. `demo.rb` and `errors.rb` produce the right output, and so
  does `hello.rb` with the full prelude.
- Backtraces contain extra `:0:in 'Throw'` / `'Run'` frames. This is cosmetic.
- `loop.rb`, in ms:

  | | while loop, 1M iterations | 200k calls |
  |---|---|---|
  | native | 1,430 | 482 |
  | `ir -X:NoJIT -X:NoOSR` | 311 | 380 |
  | `ir` default | 155 | 665 |
  | CRuby | 67 | 42 |

  Before the threshold patch the loop took 3.1-6.0 s.
- **The native `ir`'s startup time was not measured.**
- The JIT build without dynamic code ran `hello.rb` with `--disable-gems` in 3.25 s wall /
  2.77 s CPU. That was measured before the `CallInstruction` fix.

## C. Ruby -> IL -> one native executable

```sh
Util/aot/NativeProgram/build.sh Util/aot/samples/demo.rb /tmp/w   # -> /tmp/w/demo-native/demo-native
IR_AOT_TRACE=1 IR_AOT_FORBID_PARSE=1 /tmp/w/demo-native/demo-native
```

How the build works:

1. `IronRuby.Aot compile --prelude` produces the IL.
2. ILC compiles that assembly (rooted, reached through `RubyProgram.Main`) together with
   IronRuby, built against the patched DLR copy.

The result for `demo.rb` is 51 MB. It gives the right output with **0 parser calls and 0
JIT-compiled methods**:

| Step | Time (one run, UNSURE) |
|---|---|
| runtime ready | 37 ms |
| `ruby4.rb` top level | 663-867 ms |
| total | about 1.0 s |

**What stays dynamic.** Every Ruby method call, operator and conversion still goes through
a `CallSite<T>`. Its rules are built by IronRuby's binders at run time and, without a JIT,
interpreted. Pre-binding the monomorphic common case is plausible, but it is not built. It
would mean emitting a guarded direct call in the saved IL: check the receiver's class
version, then call the compiled `def` factory's delegate.
`RubyModule.GlobalMethodVersion` (see `-X:JIT`) already gives a sound one-integer guard.

**The value-type dispatcher limitation.** IronRuby's precompiled rule dispatchers
(`RubyObjectMethodDispatcher*<T1..Tn>`) are built with `MakeGenericType` over the call
site's argument types. Under NativeAOT the instantiations over value types (e.g. `<int>` in
`fib(n - 1)`) do not exist. Commit 469a95a4 makes `MethodDispatcher.CreateDispatcher` return
null in that case when dynamic code is unsupported. The binder then builds an ordinary
(interpreted) rule. Calls with `int` arguments therefore lose the fast dispatcher. Normal
builds are unaffected, because the check is behind `!IsDynamicCodeSupported`.

## D. The startup library (`IronRuby.Startup.dll`)

Part A's compiler, aimed at `ir`'s own startup: `IronRuby.Aot startup` compiles the Ruby files
every `ir` process loads (`startup-files.txt`: ruby4.rb, rbconfig, gem_prelude, rubygems and the
rest of `ir -e 1`, plus `rubygems/path_support.rb` and a few commonly required libraries such as
json, set, pp, optparse, fileutils, time) into a library that sits next to IronRuby.dll in the
R2R host's directory. Requiring one of its files then runs the compiled top level instead of
parsing, bridging, transforming and LightCompiling the source.

```sh
dotnet build Src/Console/Ruby.Console.csproj -c Release -p:DlrSourceDir=... # publishes the R2R host
DLR_SOURCE_DIR=... Util/aot/build-startup.sh   # builds IronRuby.Aot (net10.0) and installs the library
IR_CONFIG=R2R ./ir.sh -e 1                     # uses it; IR_STARTUP_DLL=none turns it off
IR_STARTUP_DLL_TRACE=1 IR_CONFIG=R2R ./ir.sh -e 1   # what it registered, what is still parsed
```

How it stays correct:

- The library records the MVIDs of the host's IronRuby, Microsoft.Dynamic and
  Microsoft.Scripting, and is ignored as a whole if they differ (rerun build-startup.sh after
  each host build). It records a hash of each file's source, checked when the file is first
  required; an edited file is compiled from source.
- It is only used by the first runtime of a process (its static fields hold that runtime's
  objects), and not under `-d`, coverage, profiling, tracing or frozen-string-literal options,
  nor for `load(file, true)`.
- There is no PDB: the compiler records each sequence point's IL offset and the library carries
  the line table (`RubyStartupIndex.Lines()`), which the backtrace builder reads. Frame labels
  are worked out from the qualified lexical nesting (`Gem::Requirement.parse`). Backtraces
  through rubygems code match the interpreted ones.
- `const_set`/`autoload` called from precompiled top-level code record no caller location
  (as for C-defined constants in MRI): a CLR stack walk per call cost 50 ms over the
  prelude's `const_set` loops.
- Each file's code is a class of its own (`RubyStartup<i>`), loaded when the file first runs.

What it does not change: call sites. Every dynamic site of the saved code is a static
`CallSite<T>` made with the runtime's binder, and binds on first execution exactly as in
interpreted code (1,948 binds and 729 rule builds for `ir -e 1`, against 1,922 and 667).
Binding is now the largest part of the prelude: about 240 ms inclusive, of which 75-105 ms is
LightCompiling the 729 rules. Rules are not one-shot: only 156 run once, 215 two to five
times, 234 six to 31 times and 122 often enough to be compiled to IL.

Measured on d96bab0e + this, R2R host, bench-lock, best/median of 21 (`-e 1`) or 15:

| | base | library |
|---|---|---|
| `ir -e 1` | 0.848 / 0.906 s, 116 MB | 0.752 / 0.795 s, 122 MB |
| `-e 'require "set"; require "json"'` | 1.073 / 1.127 s, 121 MB | 0.884 / 0.982 s, 124 MB |
| `Util/bench/bench.rb --list` | 1.033 / 1.268 s, 123 MB | 0.918 / 1.044 s, 126 MB |

The library is 30 MB (ReadyToRun) for 46 files. spec/language passes with it (80 files, 2934
examples, 5563 expectations, 0 failures, 0 errors). Steady state over Util/bench is unchanged
except `enum_next_gen` (10-20 % slower) and `fiber_switch` (about 5 %), whose hot paths run
through precompiled prelude methods; `-X:JIT` cannot specialize a precompiled method (it has
no AST body), though the methods on those paths take blocks and are not eligible anyway.

Known differences: a `require` LoadError's backtrace lacks one `Kernel#require` frame (the
library-method frame is dropped by the backtrace builder; the IL and R2R builds already
disagree on that frame without the library).

Where the time goes with the library (`IR_PHASE_TIMES=1`, 5 alternating runs each, bench-lock):
the prelude takes 530-560 ms instead of 650-690 ms. Parsing, bridging, transforming and
LightCompiling the startup files (about 270 ms) are gone, and JIT time falls from 200-220 ms to
130-140 ms. But the rest of the run grows from 230-270 ms to 310-330 ms. Part of that is work that
moved: the precompiled code creates its binders and call sites when it first runs them, where the
interpreted path did that during transform. The other part is native runtime time under the
precompiled frames, about 40 ms in the sample profile (ReadyToRun fixups, type and static-base
loads). Binding (1,950 binds) is unchanged at 210-235 ms inclusive.

Also measured, and not worth pursuing:

- Crossgen'ing the host's IronRuby, DLR and Prism assemblies and the library as one version
  bubble (`--inputbubble`, one image each) changes nothing: `ir -e 1` best/median 0.705/0.823 s
  without the bubble, 0.715/0.810 s with it, and RSS grows by 3 MB.
- The prism parse cache (`IR_PRISM_CACHE`, commit "Prototype: cache prism's serialized output")
  saves at most the native parse, 24-27 ms over the 32 startup files without the library. That
  was not measurable in wall time at N=11, and with the library there is almost nothing left to
  parse.
- Lazy def bodies (c7957991) are already on at d96bab0e. Without the library, turning them off
  (`IR_EAGER_DEF_BODIES=1`) costs 50-85 ms. With the library, def bodies are precompiled IL, so
  the question does not arise.

## Limits and next steps (rough effort)

- **Next: ReadyToRun follow-up.** `--r2r` exists and is measured above (3.75 s / 1.50 s CPU
  vs 6.60 s / 2.92 s for R2R `ir`).
  - I had started an `--inputbubble` crossgen2 build (all assemblies in one version bubble),
    to cut the remaining ~900 JIT-compiled methods. It ran once without being measured.
  - Worth doing with care on an idle box, together with a proper startup comparison against
    the native binaries. About 1 day.
- **Method JIT for AOT bodies.** Serialize enough of the `def` AST, or run the method JIT's
  specialization at compile time and emit the specialized body with its guard. 1-2 weeks.
  This would close the 100x `fib` gap.
- **Pre-bound call sites** (the monomorphic guard + direct call described in C). About 1-2
  weeks for Ruby-to-Ruby calls. It removes most run-time rule creation, which is the startup
  and NativeAOT bottleneck.
- **Correctness breadth.** Only 5 samples plus the prelude were run.
  - Not run: specs, `eval`/`binding` in compiled code, `define_method`, refinements,
    coverage, TracePoint (compiled with `SavePath` set, which turns off OSR and coverage).
  - Frame labels come from lexical nesting and may be wrong for methods defined through
    `class_eval` and similar.
  - Lazy constant initialization races benignly: two threads may create two `RubyMethodBody`
    objects for one `def` at first use.
  - `RuntimeVariablesExpression` and `Quote` are not supported by the port.
  - Standard-library files that are not compiled in are still parsed at run time; the
    tripwire counts them.

  Several weeks to run the spec suite through the AOT path.
- **NativeAOT interop** needs a per-program list of rooted .NET types. A compile-time scan of
  `::System::X` constants in the Ruby would get most of it. About 2-3 days.
- **Upstreaming.**
  - The DLR patch is small and safe: it only acts when dynamic code is unsupported.
  - The `PersistedAssemblyBuilder` short-branch bug is worth reporting to dotnet/runtime.

`Build/dlr-patches/aot/` is deliberately a subdirectory, so CI's `Build/dlr-patches/*.patch`
glob does not apply it to normal builds.

## Layout

| Path | Contents |
|---|---|
| `IronRuby.Aot/` | the compiler, the census, `Runtime/AotRuntime.cs` (load-time support) and `ExpressionCompiler/` (the ported CompileToMethod) |
| `NativeAot/` | the native `ir` host: csproj, `roots.xml`, `make-dlr-copy.sh`, `ir-native.sh` |
| `NativeProgram/` | the part C build |
| `samples/` | the Ruby samples |
| `build-samples.sh`, `measure.py` | reproduction and measurement scripts |
| `build-startup.sh`, `startup-files.txt`, `IronRuby.Aot/StartupCommand.cs` | part D, the startup library; its load-time side is `Src/Ruby/Runtime/PrecompiledCode.cs` |
