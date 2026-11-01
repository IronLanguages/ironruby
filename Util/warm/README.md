# Warm start (prototype)

`ir -e 1` spends about 0.9 s before the program's first line runs: creating the runtime, loading
the 14k-line prelude, RbConfig and RubyGems. Warm start does that ahead of time. A broker keeps
K IronRuby processes that have already started up waiting on a Unix socket. A small C client
hands one of them the invocation, and that process runs it as if it had just been exec'ed.

```
IR_CONFIG=R2R IR_SERVER=start ./ir.sh        # start a pool for this build (IR_SERVER_POOL=K, default 2)
IR_SERVER=1 ./ir.sh -e 'puts 1'              # warm when the pool can serve it, cold otherwise
export IR_SERVER=1                           # every ir.sh from this shell, and the ones its programs run
IR_SERVER=status ./ir.sh                     # also: stop, restart
```

Without a running pool, or whenever a request cannot be served warm, the client execs exactly
the command `ir.sh` would have run, so `IR_SERVER=1` is always safe to set. Start the pool from
the environment its programs will run in. A request whose startup inputs (below) differ from the
pool's goes cold.

| Variable | Meaning |
|---|---|
| `IR_SERVER` | `1` runs warm if possible; `start`, `stop`, `restart` and `status` manage the pool |
| `IR_SERVER_POOL` | number of idle workers to keep (default 2); each one costs about 70 MB PSS (120 MB RSS) |
| `IR_SERVER_SOCKET` | socket path; the default is `$XDG_RUNTIME_DIR` or `/tmp`, `ironruby-warm-<uid>-<hash of binary>.sock` |
| `IR_SERVER_LOG` | broker log (default `<socket>.log`); workers log why they declined a request |
| `IR_SERVER_VERBOSE=1` | the client says on stderr whether it went warm or cold, and why |
| `IR_SERVER_EXIT=early` | exit when the program has finished rather than when its process is gone: about 9 ms faster, but the worker may still hold its files and locks for those 9 ms |
| `IR_WARM_DEBUG=1` | (set when starting the pool) workers log each step with timestamps |

## How it works

- `ir-broker.c` starts K workers. Each is the same command line `ir.sh` would exec, with
  `IR_WARM_WORKER_FD` naming its end of a socketpair. It keeps K workers idle or warming,
  refills after each assignment, and reports each worker's real wait status back to its
  client. It lives in its own session, so the workers have no controlling terminal.
- A worker (`Src/Ruby/Hosting/WarmStart.cs`) creates the runtime, loads the prelude into the
  main script's top-level scope, compiles a dummy request, says ready and waits.
- `ir-client.c` (static, about 0.3 ms of its own) sends argv, environ, cwd, umask, rlimits, CPU
  affinity, nice value, the signals it ignores and the name of its controlling terminal, plus
  fds 0, 1 and 2 by SCM_RIGHTS. It then forwards signals, and exits the way the worker ended.
- The worker swaps in the request's environment and directory, and reparses the command line
  exactly as a cold start would. Unless every input that startup has already consumed matches
  the pool's, it declines, and the client execs the cold `ir`. Otherwise it commits: dup2 onto
  0, 1 and 2, applies umask, rlimits, affinity, nice and ignored signals, resets .NET's and the
  DLR's cached console streams, sets ARGV, `$0` and the per-program options, and runs the
  program. It exits afterwards and is never reused.

## Measured (R2R host; 8-core shared box, bench-lock core; best of 11, interleaved)

| | cold (find3, d96bab0e) | warm |
|---|---|---|
| `-e 1`, wall | 0.896 s | 0.015 s |
| `-e 'puts 1'`, first output | 0.869 s | 0.010 s |
| `Util/warm/print.rb`, first output / wall | 0.923 / 0.942 s | 0.011 / 0.034 s |
| `require "set"; require "json"`, wall | 1.045 s | 0.202 s |
| `Util/bench/bench.rb --list`, first output / wall | 1.001 / 1.023 s | 0.087 / 0.116 s |
| peak RSS (of the worker) | 116-123 MB | 114-123 MB |

Of the 15 ms for a warm `-e 1`, the worker spends about 6 ms from request to exit (2 ms
parsing, 1 ms committing, 3 ms compiling, running and shutting down). Process exit and reaping
take about 7 ms, which `IR_SERVER_EXIT=early` skips; `ir.sh` takes about 1 ms and the client
0.3 ms. Each request still costs one warm-up (about 1 CPU-second), paid ahead of time on
whatever cores the broker runs on. So warm start helps latency, not throughput: 20 back-to-back
`-e 1` take 6.6 s with K=4 (23.6 s cold), and 17 of them waited for a refill.

## Semantics: what differs from a cold start, and how it is handled

`Util/warm/check.py` runs about 110 cases both ways and compares stdout, stderr and the wait
status. It uses pipes, files and a pseudo-terminal, and checks whether each warm run was
really warm. Current result: 0 failures, 2 known gaps.

| Topic | Handling |
|---|---|
| exit status | the broker reports the real wait status; the client exits with the same code or dies of the same signal (no core) |
| `exec` from the script | the worker is replaced; the broker still reports how the new program ended |
| `at_exit`, exceptions, `exit!`, `abort` | they run in the worker exactly as in a cold process (same output and status) |
| signals | the client forwards INT TERM HUP QUIT USR1 USR2 WINCH ALRM CONT TSTP TTIN TTOU and the rest; Ctrl-C on a terminal reaches the client's process group, and the client forwards it; Ctrl-Z stops both; SIGKILL of the client kills the worker (it watches the connection); signals ignored at exec (nohup) are ignored in the worker too; the signal mask a parent blocked at exec is not carried over |
| job control | **gap**: the worker is not in the terminal's session, so a warm program in a background job is not stopped by SIGTTIN/SIGTTOU when it reads (or, with `stty tostop`, writes) the terminal; SIGSTOP sent to the client (or the program stopping itself) does not show the shell a stopped job |
| SIGINT double dispatch | fixed in `Signal.trap` (a cold-start bug too: a `trap(:INT)` block ran twice, once off the main thread); the worker keeps the prelude's Ctrl-C handler, as a cold start does |
| `Process.ppid` | reports the client's parent, not the broker |
| `Process.pid`, `getpgrp`, `getsid` | **gap**: they are the worker's own; signals sent to either pid reach the program, but `$$` is not the pid the parent saw, and `kill 0` targets the worker's own process group |
| stdin/stdout/stderr | the client's own descriptors; the client closes its copies of 0 and 1, so EOF and SIGPIPE behave as in a cold start; tty detection and `$stdout.sync` match |
| more inherited descriptors (`3<file`, a make jobserver) | goes cold |
| terminal | `isatty` and `winsize` work. `IO.console` opens the client's terminal by name. **Gap**: `File.open("/dev/tty")` fails with ENXIO, because the worker has no controlling terminal. The REPL goes cold, because .NET sampled the terminal state of fd 0 during the warm-up |
| environment | replaced in .NET and in libc's `environ`, so `system`, spawn and backticks see it; a difference in a variable that startup reads (RUBYOPT, RUBYLIB, GEM*, RUBYGEMS_*, BUNDLE*, HOME, LANG/LC_* for `Encoding.default_external`, TZ, DOTNET_*, LD_*, MALLOC_*, IR_*) goes cold |
| cwd, umask | applied before the command line is parsed, so relative paths resolve the same way |
| rlimits | applied (RLIMIT_NOFILE soft = hard, as CoreCLR does at startup); a different stack, address-space or data limit, or a hard limit raised above the pool's without privileges, goes cold; the warm-up's CPU time counts toward RLIMIT_CPU and `Process.times`, as a cold boot's does |
| `-I` | prepended to `$LOAD_PATH`; goes cold if such a directory holds a file the warm-up loaded (a cold start would have loaded that one) |
| `-r` | the request's libraries are required after the prelude, as in a cold start |
| `-n -p -l -a -0 -x -s -c`, `-w -W0 -W1 -W2 -v`, `--backtrace-limit` | taken from the request |
| `-F -i -d -E -K -U`, `-W:<category>`, `--enable`/`--disable`, `-X:...` | go cold: the prelude's loading depends on them |
| `fork`, `spawn`, `system`, `IO.popen` | same as a cold start (children inherit the request's environment, cwd and fds) |
| a rebuilt binary, or an edited prelude or library | the worker stamps the 48 files it loaded and the whole build; on a change it declines, exits, and the broker starts a fresh worker |
| gems installed after the pool started | found: RubyGems loads no specifications during startup |
| non-UTF-8 argv, environment or cwd | goes cold |
| concurrent invocations | one worker per invocation. A client finds an idle worker, or waits for one that is warming (at most one client per warming worker), or goes cold |
| security | socket mode 0600, peer credentials checked on both sides (same uid only) |

Also tested: the whole of `spec/language` run warm (`IR_SERVER=1 RUBY_EXE=./ir.sh ./ir.sh
-Imspec/lib mspec/bin/mspec-run spec/language` with a pool of 4) passes, with the runner and
88 of its 89 ruby subprocesses warm, in 32-52 s (box load) against 103 s cold.

## Files

- `ir-broker.c`, `ir-client.c`: built by `build.sh` into `bin/` (also on demand by `ir-server.sh`)
- `ir-server.sh`: start, stop, restart and status, called by `ir.sh`
- `check.py`: warm and cold, case by case
- `ttfo.py`: time to first output, next to `/tmp/startup-ab.py`'s wall time
- `print.rb`: a small script that prints, for `ttfo.py`
