# IronRuby: notes for Claude

IronRuby is Ruby on .NET: a C# interpreter/compiler on the DLR, built for .NET 10 and .NET 11,
parsing with prism (CRuby's parser) through a native `libprism`. README.md has the user-facing
story; this file is what you need to work on the code.

## The one rule: CRuby 4.0 is the spec

Where IronRuby's behaviour differs from CRuby 4.0's, IronRuby is wrong. Before changing
behaviour, run the same Ruby on CRuby 4.0 (`ruby -v` should say 4.0.x) and match its output,
messages included. Expected outputs in tests come from CRuby, not from what IronRuby happens
to print. Ruby 1.8/1.9-era behaviour that old tests assert is not a reason to keep it: update
the test (several have been, e.g. `Clone1`, `SuperInModuleEval1`).

One exception: **conversion error wording.** ruby/spec pins `can't convert X into Y` (with
`into`) for every engine but CRuby < 4.1, so IronRuby says `into` where CRuby 4.0 says `to`.
Before changing a message, `grep -rn raise_consistent_error spec/` for it. The handful of
`into`/`to` spec errors (in `define_method_spec`, `module_eval_spec`, `instance_eval_spec`,
`language/send_spec`) are known and expected.

## Layout

| Path | What |
|---|---|
| `Src/Ruby` | the runtime and compiler (`IronRuby.dll`): `Builtins/` (core types: MutableString, RubyArray, RubyIO, RubyModule/RubyClass...), `Runtime/` (RubyContext, RubyOps, RubyScope, call sites in `Calls/`, JIT in `Jit/`), `Compiler/` (AST and code generation) |
| `Src/Libraries` | the core classes' Ruby methods (`Builtins/*Ops.cs`, `[RubyMethod]` attributes) and the C#-implemented stdlib/gems (OpenSSL, Socket, Zlib, StringIO, Puma, Nokogiri over AngleSharp, Sqlite3...) |
| `Src/Libraries.Yaml` | psych |
| `Src/Prism` | the prism front end: P/Invoke loader, generated node classes, `PrismAstBridge` |
| `Src/StdLib/ironruby` | IronRuby's own Ruby code, first on the load path: `ruby4.rb` (the prelude's core half, a large file of core methods written in Ruby, loaded by `gem_prelude.rb`), `argf.rb`, and shims that replace C extensions |
| `Src/StdLib/ruby/4.0` | CRuby 4.0's standard library, vendored with some IronRuby patches; `Util/sync-stdlib.rb` moves it to a new CRuby release and merges the patches forward. Prefer an override in `ironruby/` to a new patch here |
| `Src/Console` | `ironruby`/`ir`/`ironrubyw` executables |
| `Src/IronRuby.Tests` | IronRuby's own test suite (C#; see below) |
| `Src/ClassInitGenerator` | generates the library initializers |
| `Build/dlr-patches` | patches IronRuby needs on the upstream DLR (README there) |
| `Util/` | build helpers, spec sweeps, `*-matrix.rb` probes, benchmarks, release packaging |
| `spec/`, `mspec/` | ruby/spec and mspec checkouts (not tracked) |

A method can be defined in C# (`Src/Libraries/Builtins/*Ops.cs`) **and** in the prelude
(`Src/StdLib/ironruby/ruby4.rb`); the prelude's definition wins, since it loads later. Check
both before concluding where a method lives.

## Building

The build needs two sibling checkouts next to this one, pinned in `.github/workflows/ci.yml`
(`DLR_REF`, `PRISM_REF`):

```sh
git clone https://github.com/IronLanguages/dlr ../dlr && git -C ../dlr checkout <DLR_REF>
for p in Build/dlr-patches/*.patch; do git -C ../dlr apply "$PWD/$p"; done
git clone https://github.com/ruby/prism ../prism && git -C ../prism checkout <PRISM_REF>
(cd ../prism && ruby templates/template.rb && make shared)    # libprism.so
bash Util/build-markly.sh && Util/build-librbs.sh                # two more native libs
(cd Src/Prism && ruby generate.rb)                               # C# prism nodes
dotnet build Src/Console/Ruby.Console.csproj -p:IronRubyTargetFrameworks=net10.0
```

- In a cloud session `.claude/hooks/session-start.sh` prepares the DLR, prism and the two
  native libraries and exports `DOTNET_ROOT` and `DLR_SOURCE_DIR`. It runs neither
  `ruby generate.rb` (the committed `Src/Prism/Generated` matches the pinned prism) nor
  `dotnet build`, which is still yours to run before `./ir.sh` has anything to start.
- Moving prism: change `PRISM_REF` in **both** `ci.yml` and `release.yml`, check `../prism` out at
  the new ref and rebuild `libprism` (the hook only does that at the next session start), then
  run `ruby generate.rb` and commit `Src/Prism/Generated`. `release.yml` regenerates and fails on
  any difference from the committed files. `Src/Prism/README.md` has the details.
- A DLR checkout somewhere else: `-p:DlrSourceDir=/path/to/dlr/src/core/`. Several worktrees
  sharing one DLR checkout share its `obj/`, so concurrent builds can fail with bogus CS0246.
  Give each its own DLR worktree if you build in parallel.
- `Util/check-dlr-patches.sh <dlr>/src/core` says whether a DLR checkout has every patch; a
  new patch in `Build/dlr-patches` must be applied to existing checkouts by hand. Off Windows the
  build runs it and fails on a DLR that lacks one (`-p:CheckDlrPatches=false` skips it): a build
  against a stale DLR used to succeed and differ from CI only in a test or two (issue 132).
- `IronRubyTargetFrameworks=net11.0` (or `net10.0`) builds one framework; the default builds
  both (it needs the .NET 11 SDK). CI tests both.
- Apphosts (`ir`, `IronRuby.Tests`) need `DOTNET_ROOT` when .NET is not installed system-wide.
- `make`, `make test`, `make spec` are shortcuts for the CI commands (see the Makefile).

## Running

```sh
./ir.sh -e 'p RUBY_DESCRIPTION'
./ir.sh script.rb
./iirb.sh                                 # irb
```

On Windows use `ir.cmd` and `iirb.cmd` (from cmd.exe, or `cmd //c ir.cmd ...` from Git Bash,
as CI does): they pass `-X:StdLib` `;`-separated, while the shell launchers' `:`-separated list
breaks at drive letters.

`ir.sh` runs `./ironruby`, which runs the in-place build under
`Src/Console/bin/$IR_CONFIG/$IR_TFM/`. `IR_TFM` defaults to **net10.0**: after building only
net11.0, set `IR_TFM=net11.0` or you run a stale build (or none). `IR_CONFIG` defaults to
Debug. The stdlib is read from `Src/StdLib` at startup: editing a `.rb` there needs no
rebuild, a `.cs` change does.

## Tests

**IronRuby.Tests** (`Src/IronRuby.Tests`), about 1,240 tests, run on every CI job:

```sh
dotnet build Src/IronRuby.Tests/IronRuby.Tests.csproj -p:IronRubyTargetFrameworks=net10.0
cd Src/IronRuby.Tests/bin/Debug/net10.0
./IronRuby.Tests /verbose > results.txt 2>&1    # all; ends with PASSED
./IronRuby.Tests Name1 Name2                     # some
grep '^> FAILED' results.txt
```

- A test is a `public void Name1()` method in a `partial class Tests` file under `Runtime/`,
  **registered by name in the list in `RubyTests.cs`**. An unregistered test never runs.
- Most are `TestOutput(@"ruby code", @"expected output")`. Inside the verbatim strings `"` is
  `""`. Output and expectation are trimmed and `\r` is removed before comparing.
- Tests parse with prism, as `ir` does, and run with warnings on (`-w`): an unexpected
  warning ("assigned but unused variable") fails the test. Run the snippet with `ruby -w`.
- Each test gets a fresh runtime **without the prelude**. Code that needs `ruby4.rb`'s methods
  (or the IO::WaitReadable family, `then`, `Kernel#puts`'s Ruby half...) starts with
  `require 'ruby4'`.
- Platform-only tests return early: `if (Environment.OSVersion.Platform != PlatformID.Win32NT) { return; }`.
  Platform differences in expected output are written as C# conditionals in the string.
- On Windows in Git Bash, run it with `MSYS2_ARG_CONV_EXCL='*'`, or `/verbose` becomes a path
  and the driver runs zero tests and still prints PASSED.

**ruby/spec**, with `spec/` and `mspec/` checked out at `SPEC_REF`/`MSPEC_REF` from ci.yml:

```sh
DOTNET_ROOT=... IR_TFM=net10.0 ./ir.sh -Imspec/lib mspec/bin/mspec-run spec/core/string/sub_spec.rb
Util/parallel-sweep.sh out [baseline_out]    # core, library, language, command_line, security
```

- CI runs only `spec/language`, the nightly sweep (below) all of it. For a change with a wide reach, sweep the branch and a master
  build and compare (`parallel-sweep.sh new master-out` lists new and fixed failures).
  A job the summary reports without an mspec summary line (it crashed or hung) leaves its
  examples out of the counts: resolve those in both sweeps before trusting the comparison.
- Keep both trees at **short paths**: UNIX-socket specs fail when the socket path gets too long,
  and the difference then looks like a regression or a fix.
- Timing-sensitive examples flip between runs under the sweep's parallel load (e.g.
  `Timeout.timeout ... if message is nil`). Rerun the file alone before believing a "new" one.
- Do not rebuild while a sweep runs: it measures the in-place build.

**Nightly sweep** (`.github/workflows/spec-sweep.yml`): all five suites on net10.0 every night
(by hand: `gh workflow run spec-sweep.yml -f tfms=both`), with ci.yml's pins, through
`Util/ci-sweep.sh` (parallel-sweep, then crashed or hung jobs file by file, then new failures
rerun alone). It fails on a failing example not in `Util/spec-known-failures.txt`, on a file
with no mspec summary line, and on a missing shard; a listed example that passes is a warning.

- `Util/spec-known-failures.txt` is one `<suite> <example>` per line, the name as mspec prints
  it; `flaky <suite> <example>` may pass or fail. Every entry sits under a comment saying why.
- A fix that makes a listed example pass removes its line in the same commit. The job summary
  prints new and fixed examples in the file's format, ready to paste.
- The same check on a sweep of your own: `Util/ci-sweep.sh out && ruby Util/sweep-check.rb out`.

## Generated code

`*.Generated.cs` files are generated; do not edit them by hand except to get a build going again.

| File | Regenerate with | When |
|---|---|---|
| `Src/Libraries/Initializers.Generated.cs` | `./regen-initializers.sh` (or the steps in `Src/ClassInitGenerator/README.md`) | any `[RubyMethod]`, `[RubyClass]`, `[RubyModule]`, `[RubyConstructor]`, `[RubyConstant]` change, **including parameter attributes** (`[NotNull]`, `[DefaultProtocol]`): the binder reads them from this file, so a C#-only rebuild silently keeps the old behaviour, and a new constant is not there at all |
| `Src/Libraries.Yaml/Initializer.Generated.cs` | ClassInitGenerator README | psych's `[RubyMethod]`s |
| `Src/Ruby/Compiler/ReflectionCache.Generated.cs` | `ClassInitGenerator /refcache` (README) | a method marked `[Emitted]` (called from generated code via `Methods.X`) is added or changed |
| `Src/Prism/Generated/*` | `cd Src/Prism && ruby generate.rb` | the pinned prism changes (CI regenerates them) |
| `Src/Libraries/Builtins/InternalMethods.Generated.cs` | `Util/gen-internal-methods.rb` | |
| `Src/Ruby/Builtins/UnicodeProperties.Generated.cs` | `Util/gen-unicode-properties.rb` | |
| `*/AotSubclasses.Generated.cs` | `Util/aot/gen-subclasses.rb` | a library class is added |
| `Src/Libraries/Rbs/RbsNodes.Generated.cs` | `Src/Libraries/Rbs/generate.rb` | |

A new library namespace (`IronRuby.StandardLibrary.Foo`) has to be added to `LIBS` in
`regen-initializers.sh` and to the list in `Src/ClassInitGenerator/README.md`; without it the
generator skips the namespace and nothing in it is registered.

If changing a `[RubyMethod]`'s signature breaks the build because the generated file still
names the old one, either run `regen-initializers.sh` (it builds from a skeleton) or fix the
one generated line by hand, build, and then regenerate properly. Check that the regenerated
diff touches only what you changed.

## Code and commits

- **Match the surrounding code**, including its comments: they are full sentences that say why,
  and often name the MRI function or the issue (`#133`) the behaviour comes from.
- **Line endings:** many `.cs` files are CRLF, some have a BOM, others are LF. Keep each file's
  own. Scripted edits (sed, Python) are the usual way to get this wrong: open with
  `newline=''` / read bytes, and check `git diff --stat` for whole-file churn.
- **Threads run in parallel:** there is no GVL. Code ported from MRI's C, or written as if one
  thread ran at a time, races.
- `[ThreadStatic]` on an *instance* field is silently ignored by the CLR.
- **Several runtimes can share a process** (hosting, `IronRuby.create_engine`), so state in
  static fields leaks between them.
- **Commits:** a subject line saying what changed, then a body saying why: what was wrong,
  what CRuby does, what the change does and how it was checked. `Fixes #N` closes an issue on
  merge. One commit per logical change; normal commits on shared branches (no rebase or
  force-push of a PR someone else is looking at).
- **This repository is public.** Never put the names of clients, their products or private
  machines in files, commit messages, PRs or issues.

## Pull requests

- CI (`.github/workflows/ci.yml`) builds and tests Ubuntu and Windows, plus ruby/spec's language
  suite. The Windows jobs run the full IronRuby.Tests suite too. A PR or push tests **net11.0
  only**; net10.0 (the default download is built on it) runs on a PR from a `release/*` branch, on a
  run by hand (`gh workflow run ci.yml --ref <branch> -f tfms=net10.0`, or `both`), and as the
  gate a `v*` tag's release.yml must pass before it publishes. A net10.0-only regression can
  therefore sit on master until the next release PR; run the workflow by hand when in doubt.
- Cutting a release: a "Release X.Y.Z" PR from `release/vX.Y.Z` (version files, README, the
  installer scripts' examples, and `Docs/releases/vX.Y.Z.md`), whose CI runs net10.0 and net11.0;
  merge it, then push an annotated tag `vX.Y.Z` on the merge commit. release.yml runs ci.yml's
  test set on both frameworks (`tfms: both`) and packages every RID on both: the net10.0
  archives keep the plain names (`ironruby-X.Y.Z-<rid>.*`) and the Windows installer is made
  from that zip; the net11.0 ones are `ironruby-X.Y.Z-net11-<rid>.*`
  (`IR_TFM=net11.0 Util/package-release.sh <rid>`). It publishes only if the tests and the
  Linux and Windows packages succeed (macOS is experimental), with every file in SHA256SUMS
  and attested.
- A PR whose branch conflicts with master gets **no** `pull_request` CI run at all, silently.
  Merge master into the branch, or dispatch the workflow on the branch (`workflow_dispatch`).
- CodeRabbit reviews every PR. Read the review **bodies** too: findings on lines outside the
  diff only appear there, in collapsed sections. Check each finding against CRuby before
  applying its suggested fix. After many commits it pauses itself; `@coderabbitai review` asks
  for one more.
- `.claude/skills/review` is a project skill for reviewing a change the same way, before
  opening or merging a PR.
- PRs are merged with merge commits, after review points are addressed.

## Windows

- `IO.pipe`/`popen` pipes are `WindowsPipeStream` (`Src/Ruby/Builtins`): it gives non-blocking
  reads and writes, `IO#timeout`, and cancellation on close. There is no `poll(2)`; readiness
  comes from `PeekNamedPipe`/`NtQueryInformationFile` (`IoReadiness`).
- CRuby on Windows lacks some functions (`getrlimit`, `maxgroups`, `initgroups`...): they raise
  NotImplementedError and `respond_to?` says false, and IronRuby matches that.
  NotImplementedError is a ScriptError, which a bare `rescue` does not catch.
- Errno texts are the C runtime's (`Arg list too long`), sockets report WSA errors (an ICMP
  port unreachable is ECONNRESET), and a refused connect is retried for about two seconds.
- TLS tests need the user's key store: in an SSH session logged in with a **key**, DPAPI is
  unavailable and every PFX import fails with "Access denied". Log in with a password.
- Threads have 1 MB stacks, so deep recursion that works on Linux can overflow there.
