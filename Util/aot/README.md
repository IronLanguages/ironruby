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
dotnet build Src/Console/Ruby.Console.csproj -c Release -p:DlrSourceDir=...  # the R2R host, with its library
IR_CONFIG=R2R ./ir.sh -e 1                          # uses it; IR_STARTUP_DLL=none turns it off
IR_STARTUP_DLL_TRACE=1 IR_CONFIG=R2R ./ir.sh -e 1   # what it registered, what is still parsed
DLR_SOURCE_DIR=... Util/aot/build-startup.sh        # by hand, into Src/Console/bin/R2R/net8.0
```

Where it is built:

- **The Linux Release build.** Ruby.Console.csproj's `IronRubyReadyToRunHost` target publishes
  the R2R host to `bin/R2R/<tfm>.new`, runs `build-startup.sh --host` on that directory, and only
  then swaps it in, so a host and its library always go in together.
  `-p:IronRubyStartupLibrary=false` leaves the library out (and `-p:IronRubyR2R=false` skips the
  whole R2R step). The compiler, IronRuby.Aot, is a net10.0 program because it needs
  `PersistedAssemblyBuilder`. Without the .NET 10 SDK and runtime the script says so and exits 3.
  If it fails, the build only warns. Either way the host runs without the library, which costs only
  the speedup.
- **How the compiler builds it.** It runs the host's own IronRuby and DLR builds
  (`IronRubyAotHostTfm`, net8.0), so the saved IL calls exactly those assemblies. It retargets the
  library's framework references to the host's major version. It crossgen2's the library with the
  crossgen2 of that version, against the host's own framework. A rebuild that changed nothing the
  library is made from copies the last one from `Util/aot/IronRuby.Aot/obj/startup/`, in about 1 s.
  Otherwise the library adds about 20 s to the build: 8 s to compile and 10 s for crossgen2.
- **`Util/package-release.sh`.** It builds the library against the archive's self-contained
  composite host and the archive's own copy of the standard library. Before archiving, it checks
  that the unpacked tree really uses the library (`IR_STARTUP_DLL_TRACE`), and leaves it out
  otherwise. `IR_STARTUP_LIBRARY=0` leaves it out on purpose. Every path under the tree that the
  code holds (`__FILE__`, a method's or block's file) is compiled as a call of
  `PrecompiledCode.Rooted`, and backtrace frames are rebased. So the library works wherever the
  archive is unpacked.
- **Never** for the NativeAOT host, which has no assembly files and cannot load code, or for the
  IL Debug and Release builds, which have no library next to them.

How it stays correct:

- The library records the MVIDs of the host's IronRuby, Microsoft.Dynamic and
  Microsoft.Scripting, and is ignored as a whole if they differ. It also records a hash of each
  file's source, checked when the file is first required; an edited file is compiled from source.
- It is only used by the first runtime of a process (its static fields hold that runtime's
  objects). It is not used under `-d`, coverage, profiling, tracing or frozen-string-literal
  options, nor for `load(file, true)`.
- There is no PDB. The compiler records each sequence point's IL offset, and the library carries
  the line table (`RubyStartupIndex.Lines()`), which the backtrace builder reads. Frame labels
  are worked out from the qualified lexical nesting (`Gem::Requirement.parse`). Backtraces
  through rubygems code match the interpreted ones. That includes a `require` LoadError's: a
  compiled catch site is now skipped as a frame, not as the first frame reported, which had
  dropped the caller of gem_compat.rb's `Kernel#require` block. The same happened without the
  library once that block was compiled, after 200 calls.
- `const_set` and `autoload` record their caller's location (`const_source_location`, "previous
  definition was here"). Compiled code would need a CLR stack walk for that, about 0.2 ms each.
  Instead, the compiler puts `PrecompiledCode.NoteCallLocation(scope, file, line)` before each
  such call (19 in the startup files), and both methods take the caller's scope, which only the
  noted call can match. Not covered: constants that ruby4.rb itself makes with `const_set` or
  `autoload` (`Errno::*`, the `Set` autoload) have no location, as C-defined ones in MRI. Without
  the library they were reported at gem_prelude.rb:9, the line that requires ruby4.rb.
- Each file's code is a class of its own (`RubyStartup<i>`), loaded when the file first runs.

Call sites. Every dynamic site of the saved code is a static `CallSite<T>` made with the runtime's
binder, and it binds on first execution as interpreted code does. The sites have the interpreter's
untyped shape: every argument and the result is an object, as in
`DynamicInstructionN.CreateUntypedInstruction`. So the library's sites share delegate types,
`CallSite<T>` instantiations and binder rule caches with interpreted code. Sites typed after their
arguments made the phases of `ir -e 1` take 574 ms instead of 480 ms: each typed shape had a
delegate type, a `CallSite<T>` and rule instantiations of its own to JIT, plus rules the binders
cached apart from the interpreted code's. `ir -e 1` makes 950 outermost binds and 651 rule
builds with the library, against 935 and 634 without it.

The method JIT (`-X:JIT`, on by default) specializes a hot method from its AST, and a precompiled
method has only a header. For each `def`, the compiler asks `JitStub.IsCandidate` whether the JIT
may specialize it; 109 of the 2,379 in the startup files pass. For those it keeps the def's
source text, 4.9k characters in all, and `RubyMethodBody` parses it at the method's first call
(6 parses for `ir -e 1`). So `Object#itself` and the `Fiber` accessors are specialized as without
the library. Without this, `call_kw_omitted` was 1.38x slower and `fiber_new` 1.27x.

Loops. ReadyToRun code is replaced by optimized code only after 30 calls, and never while it
runs. A loop that runs long in a method called once, such as `Kernel#loop` or a fiber thread's
`while` block, therefore ran to the end as ReadyToRun code. That code calls every helper the JIT
would have inlined. This was the 10-20 % on `enum_next_gen` and 5 % on `fiber_switch`. The
compiler now marks each method or block whose own code has a loop (70 in the startup files)
`AggressiveOptimization`: crossgen2 leaves it out, and the JIT compiles it optimized at its first
call. File and class bodies, which run once, keep their ReadyToRun code. `IR_AOT_LOOP_JIT=0` at
compile time turns this off.

Measured on 97fe9183 + this (R2R host, bench-lock, interleaved; best / median, peak RSS):

| | base | library off (`IR_STARTUP_DLL=none`) | library |
|---|---|---|---|
| `ir -e 1` | 0.675 / 0.784 s, 106 MB | 0.692 / 0.789 s, 106 MB | 0.576 / 0.672 s, 112 MB |
| `-e 'require "set"; require "json"'` | 0.782 / 0.943 s, 110 MB | 0.823 / 0.939 s, 109 MB | 0.693 / 0.792 s, 124 MB |
| `Util/bench/bench.rb --list` | 0.848 / 0.900 s, 111 MB | 0.849 / 0.885 s, 111 MB | 0.717 / 0.760 s, 113 MB |

That is N=21 on a loaded box (load average 11-13). On a quieter box, N=11, `ir -e 1` was 0.676 /
0.722 s for the base and 0.557 / 0.599 s with the library. The release archive (package-release.sh,
self-contained composite host, unpacked elsewhere, N=11) goes from 0.668 / 0.707 s to 0.574 /
0.594 s, and `require "set"; require "json"` from 0.854 / 0.879 s to 0.695 / 0.715 s.

Peak RSS grows by 6 MB, or by 14 MB once json is loaded. That is a transient peak: 3 s after
`require "json"`, the process holds 100 MB RSS with the library and 105 MB without it (39 against
43 MB anonymous).

The library is 32 MB (ReadyToRun) for 46 files. spec/language passes with it (80 files, 2934
examples, 5563 expectations, 0 failures, 0 errors), as do spec/core/module, kernel, exception,
enumerator and fiber, with the same counts as without it. Warm start workers (Util/warm) are a
process's first runtime, so they load the library too, and `check.py` still gives 0 failures and
its 2 known gaps. The NativeAOT host never looks for the library, and it passes spec/language
unchanged. Steady state is unchanged:

- Util/bench, all 67 rows, best of 5 at scale 1, R2R base against R2R with the library: geomean
  1.003.
- Rows above 1.08 were re-measured, best of 8 at scale 3. `enum_next_gen` is 0.97 and
  `enum_next_array` 0.95. `case_when` (1.07) and `new_kwargs` (1.07) are slower than the base, but
  they are 1.01 and 0.98 library off -> on, so the library is not the cause.
- One row is slower with the library: `mandelbrot`, by 6-8 % (about 5 ms of 80 ms). It shows only
  as the first run of bench.rb's top-level block, and still does after a 0.5 s sleep, so it is not
  background tier-up. Run elsewhere, the same loop is not slower:
  - in a method, its first call is faster with the library (71 against 88 ms) and later calls
    are the same;
  - in a top-level proc alone, its first run is faster too (73 against 96 ms median).
- ruby-bench (R2R, best timed iteration): `fib`, `nbody` and `erubi` are within noise, and their
  whole-process wall time is 6-16 % lower.

Where the time goes with the library (`IR_PHASE_TIMES=1`, one run each, bench-lock), without
-> with:

- Total phase time falls from 654 to 526 ms, and the prelude ends at 490 ms instead of 630 ms
  after it starts.
- Parsing, bridging, transforming and LightCompiling the startup files and their def bodies
  (about 270 ms) are gone. What is left of them is 9 small parses: `-e`, the 6 JIT sources, and
  two evals in rubygems.
- Binding falls from 229 to 203 ms inclusive.
- Allocation falls from 51.7 to 33.4 MB.
- What grows is "everything else", from 223 to 291 ms. Part of that is work that moved: the
  precompiled code creates its binders and call sites when it first runs them, where the
  interpreted path did that during transform. The rest is native runtime time under the
  precompiled frames: ReadyToRun fixups, type and static-base loads, and 14 ms to load the 81
  classes.
- The JIT compiles 332 methods instead of 308 (47 ms instead of 39 ms), the loop methods among
  them.

Also measured, and not worth pursuing:

- Crossgen'ing the host's IronRuby, DLR and Prism assemblies and the library as one version
  bubble (`--inputbubble`, one image each) changes nothing: `ir -e 1` best/median 0.705/0.823 s
  without the bubble, 0.715/0.810 s with it, and RSS grows by 3 MB.
- The prism parse cache (`IR_PRISM_CACHE`, ff2937ce "Prototype: cache prism's serialized
  output", left on branch sp/su-prelude) saves at most the native parse, 24-27 ms over the 32
  startup files without the library. That was not measurable in wall time at N=11, and with the
  library there is almost nothing left to parse.
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
