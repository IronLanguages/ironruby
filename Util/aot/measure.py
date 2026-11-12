#!/usr/bin/env python3
"""Run commands interleaved N times; report the best wall-clock and best CPU (user+sys) time.

    Util/aot/measure.py -n 5 'label=cmd ...' 'label2=cmd ...'

The best of N, not the mean: on a shared box the noise is all on the slow side. CPU time is
reported next to wall time because it is much less sensitive to other load (it still counts
the runtime's background JIT threads).
"""
import os, resource, subprocess, sys, time

def run(cmd):
    before = resource.getrusage(resource.RUSAGE_CHILDREN)
    t = time.monotonic()
    p = subprocess.run(cmd, shell=True, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
    wall = time.monotonic() - t
    after = resource.getrusage(resource.RUSAGE_CHILDREN)
    cpu = (after.ru_utime - before.ru_utime) + (after.ru_stime - before.ru_stime)
    if p.returncode != 0:
        sys.stderr.write("FAILED (%d): %s\n%s\n" % (p.returncode, cmd, p.stderr.decode()[-2000:]))
    return wall, cpu

def main():
    args = sys.argv[1:]
    n = 5
    if args[:1] == ["-n"]:
        n = int(args[1]); args = args[2:]
    cmds = [a.split("=", 1) for a in args]
    results = {label: [] for label, _ in cmds}
    for _ in range(n):
        for label, cmd in cmds:
            results[label].append(run(cmd))
    width = max(len(l) for l, _ in cmds)
    for label, _ in cmds:
        r = results[label]
        print("%-*s  wall %6.2f s   cpu %6.2f s   (best of %d)" % (width, label, min(w for w, _ in r), min(c for _, c in r), n))

if __name__ == "__main__":
    main()
