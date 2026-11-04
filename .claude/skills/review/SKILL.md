---
name: review
description: Review an IronRuby change (the current branch, a PR number, or a commit range) for real bugs before it merges — the job CodeRabbit did on this repo. Use when asked to review, check, or second-guess a diff or PR, or before opening or merging one. Finds correctness, concurrency, CRuby-compatibility, native-interop, security and release-script defects; verifies each one before reporting it.
---

# Reviewing an IronRuby change

The goal is the handful of findings that would have hurt someone, each one checked, not a
long list. On this repo CodeRabbit's useful findings (95 across PRs 17–95) were almost all
of the kinds listed under "What to look for"; its false alarms came from not checking the
code it cited (a "does not compile" against the wrong `ContractUtils`, a memory blow-up the
author's measurement disproved, a JIT difference in a program Ruby cannot parse). So: look
where the bugs are, then prove each one.

## 1. Get the change

- Current branch: `git fetch origin master` and `git diff origin/master...HEAD`, plus
  `git log --oneline origin/master..HEAD` for what each commit claims.
- A PR: the GitHub MCP tools — `pull_request_read` with `get` (head and base SHAs),
  `get_diff`, `get_files`, and `get_review_comments` for what was already said. Check out
  the head locally (`git fetch origin pull/N/head:prN`) to read and run the code.
- Read every changed function whole, not just the hunk, and its callers when the change
  alters a contract (a return value, a lock, who mutates what). A hunk is where to start,
  not what to judge.

## 2. What to look for

Go through these for every hunk that touches the area. They are ordered by how much damage
they did here.

**Threads run in parallel.** IronRuby has no GVL, so code ported from MRI's C, or written
as if one thread ran at a time, races.
- Is a shared collection enumerated, or a pointer or owner read, outside the lock that guards
  its writers? Was the live list handed out of the lock instead of a copy? (#50, #47 Markly)
- Is a check-then-act split across a lock release? Can a concurrent `signal`, `kill` or `close`
  remove the thing being waited on before the cleanup runs? (#42 ConditionVariable)
- Can a waiter be lost or left blocked forever? Is a weak handle collected while something
  still waits on it? (#65 Fiber)
- `[ThreadStatic]` and static caches: bounded? Do they keep a `RubyContext` alive? (#95)

**Blocking and interruptibility.** A thread parked in a P/Invoke cannot be raised into,
killed or closed underneath. Does a new wait use poll slices with
`RubyUtils.CheckAsyncException`, as `DescriptorStream.WaitFor` does? Does a retry loop on
EAGAIN or EINTR wait, or spin? Does a timeout still hold on an `O_NONBLOCK` descriptor? (#47)

**Native memory and interop.**
- `GC.KeepAlive` after the last native use of a managed owner.
- Finalizers that free what a wrapper still points to.
- A descriptor or handle leaked on an exception path.
- Recursion over user-controlled input with no depth limit or stack guard (a stack overflow
  kills the process, no rescue). (#47 Zstd, Yajl, PTY; #34 BEGIN walk)

**CRuby compatibility is the spec.** Where IronRuby differs from CRuby 4.0, IronRuby is wrong.
- **Fast paths:** a C# shortcut for a method must stand down when the method or an operator
  it relies on is redefined, aliased, made private or refined. Look for a
  `RubyModule.GlobalMethodVersion` guard and a `context.HasRefinements` check.
  `Integer#clamp` ignored a redefined `<=>` (#77). A Ruby helper that calls `**` or `<=>`
  through dynamic dispatch runs a user's wrapper twice (#72).
- **Error classes and messages:** errors must match MRI's, including the operand order in
  "comparison of X with Y failed" and `ArgumentError` vs `TypeError` vs `FrozenError`.
- **Arity, `method(:x).owner`, `respond_to?`** for anything moved between Ruby and C#.
  Overloads change arity.
- **Mutation:** every mutation of a `MutableString` or `RubyArray` goes through the type's own
  `Mutate`, so frozen strings are refused, copy-on-write content is copied first, and caches
  are invalidated. Writing `_content` or setting flags by hand skips all three (#90 `SetLength`).

**Security.**
- Executing or loading a file found relative to the current directory: `ironruby` ran `./irb` (#40).
- A `PATH` or `RUBYPATH` order change.
- TLS state after a rejected certificate: a second `connect` must not report success (#28).
- Pinned third-party sources with known CVEs.
- Release automation that publishes on a manual dispatch (#35).

**Platform.**
- Windows: a relative `chdir` must not be applied twice through two APIs (#34).
- Registry and DNS settings that a rewrite stopped reading (#28).
- The BSD/macOS errno values that differ from Linux.
- Linux-only struct layouts (termios offsets) used without an OS check.

**Shell and CI scripts.**
- `test … && …` as the last command defeats `set -e`. An archive built with a file missing
  still "succeeds" (#35).
- Temporary directories inside a git checkout.
- Hard-coded `DOTNET_ROOT`.
- A check that passes when the thing it checks never ran: a cold run counted as warm (#76).

**Generated files.**
- A new or changed `[RubyMethod]` needs `Src/Libraries/Initializers.Generated.cs` regenerated
  with `./regen-initializers.sh`.
- A new `[Emitted]` `RubyOps` method needs its `ReflectionCache.Generated.cs` entry.
- Prism changes must match `Src/Prism/Generated`.
- A forgotten regeneration builds but registers nothing.

## 3. Prove each finding

A finding you have not checked is a question, not a finding.

1. **Trace a concrete path:** the input or interleaving, the line that goes wrong, and what the
   user sees. If you cannot name one, drop it, or report it as a question.
2. **Check the cited API at the pinned revision.** The DLR is `../dlr` at `DLR_REF` in
   `.github/workflows/ci.yml`; prism is `PRISM_REF`. Do not reason from memory about
   overloads or signatures.
3. **Reproduce when it is cheap.**
   - Build with `dotnet build Src/Console/Ruby.Console.csproj -c Release
     -p:IronRubyTargetFrameworks=net8.0`. A `[RubyMethod]` change first needs
     `./regen-initializers.sh`. A file under `Src/StdLib` needs no build.
   - Write a few lines of Ruby and run them with `IR_CONFIG=Release ./ir.sh x.rb` and
     `ruby x.rb`. A difference from CRuby is the bug report. The reference is CRuby 4.0; if the
     `ruby` at hand is older, say which version you compared against.
   - For terminal behaviour, drive the program through a pty. See the snippet below.
   - For races, a stress loop that reproduces the bug settles it; one that does not reproduce
     it settles nothing.
   - Code over a native library has to be built before it can run. Markly needs
     `Util/build-markly.sh` plus `gem install markly -v 0.15.2`; rbs needs
     `Util/build-librbs.sh`. If you cannot build it, mark the finding "reasoned only".
4. **Check the relevant ruby/spec:**
   `RUBY_EXE=$PWD/ir.sh IR_CONFIG=Release ./ir.sh -Imspec/lib mspec/bin/mspec-run spec/core/<class>`
   (a directory: spec files are per method and not every method has one). Run it against
   both the base build and the head build, and diff the lists of failing examples. A failure
   list that changed is a finding. A failure that is already on the base is not this change's.
5. **Hold each commit to its message.** A commit that claims to fix something and fixes only
   part of it gets a Note naming the part it misses, even when that part was already broken
   on the base.
6. **Weigh the fix against the risk.** A rare race that crashes the process is worth a lock.
   A theoretical gap that no caller reaches is worth a sentence.

A pty driver for terminal behaviour (`python3 -I drive.py ./ir.sh` or `... ruby`):

```python
import os, pty, sys, time, select
script = 'require "io/console"; p STDIN.getch(min: 0, time: 1)'   # the program under test
pid, fd = pty.fork()
if pid == 0:
    os.execvp(sys.argv[1], [sys.argv[1], "-e", script])
# os.write(fd, b"x") here types input
out, end = b"", time.time() + 10
while time.time() < end:
    if select.select([fd], [], [], 0.2)[0]:
        try:
            data = os.read(fd, 4096)
        except OSError:
            break
        if not data:
            break
        out += data
print(out.decode(errors="replace"))
try:
    os.kill(pid, 9)
except ProcessLookupError:
    pass
os.waitpid(pid, 0)
```

`./ir.sh` runs `ir` as a child process. To measure the CPU of the program itself, read
`/proc/<pid>/task/<pid>/children` for the child's pid.

## 4. Report

Rank the findings, most severe first:

- **Blocker:** wrong results, a crash, a hang, data loss, a security hole, or a regression from
  the base that a user would hit.
- **Should fix:** real, but an edge case, a misuse path, or tooling.
- **Note:** latent or theoretical (say why no current path reaches it), or a test gap.

For each finding give:
- `file:line`
- one sentence stating the defect
- the concrete failure: the input or interleaving and what goes wrong
- how you verified it: reproduced, read at the pinned revision, or reasoned only
- the smallest fix

Leave out style, naming and docstring remarks unless asked. Say plainly when nothing
survived verification: an empty review is a valid result.

**Where to post:** report in the conversation by default. Post on a PR, as a review with
inline comments through the GitHub MCP review tools, only when the user asks. Never approve
or merge on the strength of a review.

**When asked to fix findings:** fix the Blockers and Should-fixes in separate commits. Name
the finding and how it was reproduced in each commit message. Re-run the reproduction and the
specs from step 3 before pushing.
