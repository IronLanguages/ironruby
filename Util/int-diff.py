#!/usr/bin/env python3
"""Three-way diff of int-matrix runs: CRuby, IronRuby before, IronRuby after.

    python3 int-diff.py /tmp/m-cruby.txt /tmp/m-base.txt /tmp/m-ir.txt

Prints REGRESSION for expressions the change made worse, FIXED for ones it made
match CRuby, CHANGED for ones that moved but matched neither before nor after,
and CRASH for expressions that now kill the process.
"""
import sys


def load(p):
    d = {}
    for l in open(p, errors='replace'):
        l = l.rstrip('\n')
        if '\t' not in l:
            continue
        i, rest = l.split('\t', 1)
        if not i.isdigit():
            continue
        d[int(i)] = rest
    return d


cruby, before, after = (load(p) for p in sys.argv[1:4])
buckets = {'CRASH': [], 'REGRESSION': [], 'FIXED': [], 'CHANGED': []}
for i in sorted(cruby):
    b, a, c = before.get(i), after.get(i), cruby[i]
    if b is None:
        continue
    if a is None:
        buckets['CRASH'].append((i, b, '<process died>'))
        continue
    if a == b:
        continue
    if b == c:
        buckets['REGRESSION'].append((i, b, a))
    elif a == c:
        buckets['FIXED'].append((i, b, a))
    else:
        buckets['CHANGED'].append((i, b, a))

for k in ('CRASH', 'REGRESSION', 'CHANGED', 'FIXED'):
    print('=== %s: %d' % (k, len(buckets[k])))
    for i, b, a in buckets[k]:
        print('  %s' % cruby[i].split(' => ')[0])
        print('      cruby : %s' % cruby[i].split(' => ', 1)[-1])
        print('      before: %s' % b.split(' => ', 1)[-1])
        print('      after : %s' % a.split(' => ', 1)[-1])
