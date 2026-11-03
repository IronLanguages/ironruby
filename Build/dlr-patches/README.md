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
| `0101-stack-guard.patch` | The interpreter's `LightCompiler` and the compiled path's `FlowControlRewriter` recurse once or more per level of an expression tree, and Ruby code nests deeper than a thread's stack goes: prism accepts 10000 levels, and a call chain (`a.b.c...`) has no limit. A new `Microsoft.Scripting.Utils.StackGuard` lets them carry on on a new thread with a fresh 64 MB stack when `RuntimeHelpers.TryEnsureSufficientExecutionStack()` says the stack is nearly gone, as `System.Linq.Expressions`' own compilers do. Named to apply after the `0100-` patches, which touch `LightCompiler.cs` too. |
| `0101-startup.patch` | Startup costs the DLR adds to every process: no COM type scan of every loaded assembly (nothing reads its cache); no culture-sensitive casing or comparison in option parsing and `OpenFileStream`, which made ICU open a collator; the namespace tree reads an assembly's public type names from its metadata instead of loading every exported type (a listed type that does not load, such as a forwarder to an assembly that is not there, is skipped when the namespace is enumerated, as it was before); the call-instruction shapes the interpreter builds with `MakeGenericType` are rooted so ReadyToRun precompiles them; user-defined conversion operator lookups are cached; `BackgroundCompilation.HoldBack()` lets a host hold back the interpreter's background compilations (IronRuby does while the prelude loads); `DeclaredMemberNames.MayDeclareMethod` answers from metadata whether a type declares a method name; no CoreLib collection over a DLR struct on the startup path (their code would be JIT-compiled). |
| `0102-jit-on-fresh-stack.patch` | The CLR's JIT also recurses over the trees of the IL it compiles, and needs far more stack per level than the light compiler: compiling a lambda nested ~1500 Ruby levels deep (an array literal, say) overflowed a 1 MB stack - a Windows main thread's, or the thread `DeepNesting1` runs on - in `DynamicMethod.CreateDelegate`, which no managed check can catch. The light compiler now records how deep the tree went, and `LightDelegateCreator.Compile`, through which both synchronous and background compiles go, compiles a lambda deeper than 400 levels on a fresh stack (`StackGuard`, from `0101`). Hardly anything is that deep: in IronRuby.Tests, 3 of 4609 compiled lambdas, all from `DeepNesting1`. |

`Util/check-dlr-patches.sh DLR_DIR` checks that a DLR checkout has all of these applied:
a local one is patched once and falls behind when a new patch lands here.
`Util/run-tests.sh` warns when the DLR it builds against does not, and
`Util/package-release.sh` refuses to build a release from it.

`aot/` holds patches for the ahead-of-time/NativeAOT prototype (`Util/aot`). CI does not apply
them (the loop above takes this directory's `*.patch` only); `Util/aot/NativeAot/make-dlr-copy.sh`
applies them to a private copy of the DLR.
