#!/usr/bin/env python3
# ttfo.py N cmdA... -- cmdB... : interleaved time to first output byte (stdout is a pipe) and
# total wall time, best and median per command. Companion of /tmp/startup-ab.py, which only
# measures wall time with stdout on /dev/null.
import os, statistics, subprocess, sys, time

args = sys.argv[1:]
n = int(args[0])
cmds, cur = [], []
for a in args[1:]:
    if a == '--':
        cmds.append(cur)
        cur = []
    else:
        cur.append(a)
cmds.append(cur)

env = dict(os.environ, DOTNET_ROOT='/usr/local/dotnet')
res = {i: ([], []) for i in range(len(cmds))}
for _ in range(n):
    for i, c in enumerate(cmds):
        r, w = os.pipe()
        t0 = time.monotonic()
        p = subprocess.Popen(c, stdout=w, stderr=subprocess.DEVNULL, env=env)
        os.close(w)
        first = None
        while True:
            chunk = os.read(r, 65536)
            if first is None and chunk:
                first = time.monotonic() - t0
            if not chunk:
                break
        os.close(r)
        p.wait()
        total = time.monotonic() - t0
        res[i][0].append(first if first is not None else float('nan'))
        res[i][1].append(total)

for i, c in enumerate(cmds):
    f, t = res[i]
    print(f"first-output best {min(f):.3f}s median {statistics.median(f):.3f}s | "
          f"wall best {min(t):.3f}s median {statistics.median(t):.3f}s :: {' '.join(c)[-60:]}")
