# DLR patches

IronRuby builds against the DLR sources from a sibling `dlr` checkout
(`IronLanguages/dlr`). A few changes this tree needs are not upstream yet, so CI
checks out the upstream revision pinned as `DLR_REF` in `.github/workflows/ci.yml` and
`release.yml` and applies the patches in this directory, in name
order, with `git apply`:

```sh
for p in ironruby/Build/dlr-patches/*.patch; do git -C dlr apply "$p"; done
```

To follow upstream, move `DLR_REF` in both workflows together and check the patches still
apply. Delete a patch once it lands upstream. If one stops applying, CI fails loudly at
the apply step instead of with a confusing CS1061 deep inside IronRuby.

| Patch | Why IronRuby needs it |
|-------|-----------------------|
| `0001-ThreadLocal-cross-thread-read.patch` | `Microsoft.Scripting.Utils.ThreadLocal<T>.TryGetValue(Thread)`, used by `RubyExceptionData` so `Thread#backtrace` can read another thread's interpreted frames. |
| `0002-LightDynamicExpression-materialize-args.patch` | `Reduce()` runs more than once for a node (light interpreter, then background compilation), but the constructor stored the caller's `ReadOnlyCollectionBuilder`, which `ToReadOnlyCollection()` empties on first use. |
| `0003-DlrConfiguration-base-exception.patch` | A language whose constructor throws during startup reported only `InnerException?.Message`, hiding the type and the stack - the only information there is at that point. |
| `0004-InterpretedFrame-handle-exception-frame.patch` | `InterpretedFrame.IsInterpretedFrame` also recognises `Interpreter.HandleException`, where a handler's instructions run: without it an exception raised inside a `rescue` clause had a backtrace missing every frame from the rescuing frame outwards (empty at the top level). |
| `0005-TypeInferer-invokable-delegate-parameters.patch` | Generic type inference let a Ruby block passed for a delegate whose parameters use a type parameter (`Where<T>(IEnumerable<T>, Func<T, bool>)`) decide that parameter: it guessed `Object` and won over `int` from the `List<int>`, which then failed to convert. A block takes whatever its delegate passes it, so such an input now abstains and the other arguments decide; its guess is used only when nothing else mentions the parameter. |
| `0006-SelectBestConversionFor-ambiguous.patch` | `OverloadResolver.GetPreferredParameter` returns `Ambiguous` when the language's `SelectBestConversionFor` says so, instead of going on to order the two parameter types. IronRuby says it for an argument neither overload can convert (no `to_int`, no `to_f`): ordering the types picked the narrowest numeric one, so `System::Math.abs(nil)` called `Abs(SByte)` and failed "no implicit conversion of nil into System::SByte". Ambiguous, the call reports what all of them would convert to ("into Integer or Float"). The base `SelectBestConversionFor` never returns `Ambiguous`, so nothing else changes. |
| `0101-stack-guard.patch` | The interpreter's `LightCompiler` and the compiled path's `FlowControlRewriter` recurse once or more per level of an expression tree, and Ruby code nests deeper than a thread's stack goes: prism accepts 10000 levels, and a call chain (`a.b.c...`) has no limit. A new `Microsoft.Scripting.Utils.StackGuard` lets them carry on on a new thread with a fresh 64 MB stack when `RuntimeHelpers.TryEnsureSufficientExecutionStack()` says the stack is nearly gone, as `System.Linq.Expressions`' own compilers do. Named to apply after the `0100-` patches, which touch `LightCompiler.cs` too. |
| `0101-startup.patch` | Startup costs the DLR adds to every process: no COM type scan of every loaded assembly (nothing reads its cache); no culture-sensitive casing or comparison in option parsing and `OpenFileStream`, which made ICU open a collator; the namespace tree reads an assembly's public type names from its metadata instead of loading every exported type (a listed type that does not load, such as a forwarder to an assembly that is not there, is skipped when the namespace is enumerated, as it was before); the call-instruction shapes the interpreter builds with `MakeGenericType` are rooted so ReadyToRun precompiles them; user-defined conversion operator lookups are cached; `BackgroundCompilation.HoldBack()` lets a host hold back the interpreter's background compilations (IronRuby does while the prelude loads); `DeclaredMemberNames.MayDeclareMethod` answers from metadata whether a type declares a method name; no CoreLib collection over a DLR struct on the startup path (their code would be JIT-compiled). |
| `0102-jit-on-fresh-stack.patch` | The CLR's JIT also recurses over the trees of the IL it compiles, and needs far more stack per level than the light compiler: compiling a lambda nested ~1500 Ruby levels deep (an array literal, say) overflowed a 1 MB stack - a Windows main thread's, or the thread `DeepNesting1` runs on - in `DynamicMethod.CreateDelegate`, which no managed check can catch. The light compiler now records how deep the tree went, and `LightDelegateCreator.Compile`, through which both synchronous and background compiles go, compiles a lambda deeper than 400 levels on a fresh stack (`StackGuard`, from `0101`). Hardly anything is that deep: in IronRuby.Tests, 3 of 4609 compiled lambdas, all from `DeepNesting1`. |
| `0103-reflection-call-rethrow.patch` | The interpreter calls a delegate with more parameters than it has call instructions for - a Ruby method with ten or so parameters or more - through `MethodInfo.Invoke`, and rethrew what the call raised with `throw inner`, which starts the exception's stack trace over: the backtrace lost every frame between the `raise` and the caller's call site, and the frames below the rescuing one (`<main>`). It now rethrows through `ExceptionDispatchInfo`, which keeps them; IronRuby hides the reflection frames themselves. |
| `0104-SharedIO-output-set-by-host.patch` | `SharedIO.IsOutputSetByHost` / `IsErrorOutputSetByHost`: whether a host set the stream with `SetOutput`. On Windows .NET opens a standard output whose pipe reader has gone as `Stream.Null`, and IronRuby then writes to the handle itself so that the write reports EPIPE; a host that passes `Stream.Null` to discard the output is the same object, and has to stay a discard. |
| `0105-rethrow-outside-catch.patch` | On CoreCLR a catch handler runs on top of the frames the exception came out of, popped only when it completes. The interpreter (`Interpreter.Run`, for a frame with only a finally to run) and `TryStatementBuilder`'s filter and fault lowering rethrew from inside the catch, so an exception passing n such frames needed stack for all of them at once, and a deep recursion's `SystemStackError` ended the process with a CLR stack overflow. Both now capture the exception, leave the catch and rethrow it from there (`ExceptionDispatchInfo`, which keeps the trace). A lowered filter's finally now runs before an outer frame's filter sees the exception - the order of Ruby's `ensure` and `rescue`. |
| `0106-BigInteger-ToString-radix.patch` | `MathUtils.ToString(BigInteger, radix)` divided the whole number by the radix once per digit: quadratic, with a BigInteger division per digit (a million-bit number: ~108 s). A power-of-two radix now reads the bits, and any other radix splits the number by powers of the radix down to pieces of 8 words, divided in place by the largest power of the radix that fits in a uint; on .NET 5 to 8, whose `BigInteger.DivRem` is quadratic, the large splits use Barrett reduction with a Newton reciprocal. A million bits now take ~0.3 s (net10.0) / ~0.6 s (net8.0); the output is unchanged. `Integer#to_s` has not called it since #86, but the DLR's other callers and IronRuby.Tests do. |
| `0107-interpreter-pass-exceptions-through.patch` | `Interpreter.Run` caught every exception leaving an interpreted frame, ran the frame's finally blocks and threw it again: one more CLR exception dispatch per interpreted frame passed, so a raise through 20 interpreted Ruby methods cost ~470 us on .NET 8 (~60 us through the same methods compiled). It now catches in a filter only when it must - a handler of the frame takes the exception, a finally block of the frame contains a jump or sits inside a handler of the frame (either can end the exception), or the stack is nearly gone (0105) - and otherwise lets the exception through, running the frame's finally blocks from a finally of its own. |
| `0108-ThreadLocal-no-dead-thread-roots.patch` | `ThreadLocal<T>`'s entries (the interpreter's current frame among them) held the Thread that owns them, and an entry stays in the array after its thread has finished, until the array next grows. Every Ruby thread that had run interpreted code was kept alive that way, and with it the CLR's native record of the dead thread and its `ManagedThreadId`: thousands of dead threads piled up in the runtime's thread list, which the finalizer thread walks to remove each one and every GC suspension walks too (`Thread.new { }.join` in a loop spent most of its process CPU in `ThreadStore::RemoveThread`). An entry now carries a per-thread token object instead (weakly mapped from the Thread for `TryGetValue`, patch 0001), so a finished thread is collected and its id reused; a new thread that gets the id replaces the stale entry on first use. |

`Util/check-dlr-patches.sh DLR_DIR` checks that a DLR checkout has all of these applied:
a local one is patched once and falls behind when a new patch lands here.
Off Windows, building `IronRuby.dll` runs it and fails on a DLR without all of them
(`-p:CheckDlrPatches=false` skips that), and `Util/package-release.sh` refuses to build a
release from one.

`aot/` holds patches for the ahead-of-time/NativeAOT prototype (`Util/aot`). CI does not apply
them (the loop above takes this directory's `*.patch` only); `Util/aot/NativeAot/make-dlr-copy.sh`
applies them to a private copy of the DLR.

`wasm/` holds the patches the browser build (`wasm/build.sh`) applies on top of those to its own
copy of the DLR (`../dlr-wasm`): `0001-browser-posix-file-system.patch` (browser-wasm reports
`PlatformID.Other`, but its emscripten file system is POSIX: `/` paths, case-sensitive) and
`0002-compile-synchronously-without-threads.patch` (no background thread to compile hot
interpreted code on, so it is compiled synchronously when it crosses the threshold) and
`0003-stack-guard-without-threads.patch` (`StackGuard.RunOnFreshStack` from `0101-stack-guard`
cannot start a thread there, so the walk carries on on the one stack there is) and
`0004-virtual-calls-by-reflection.patch` (an open-instance delegate over a virtual or interface method,
such as `Func<ICollection, int>`, crashes Mono's AOT build in an assertion when invoked, so
the interpreter calls virtual methods through reflection there). `build.sh` checks
`../dlr` for the patches above and recreates `../dlr-wasm` from it when `../dlr` has moved on.
