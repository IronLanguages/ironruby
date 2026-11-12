#!/usr/bin/env python3
"""Dump every Ruby-visible registration in a generated initializer file, one per
line, so two generator runs can be diffed for silently lost classes/methods.

    python3 reg-names.py Src/Libraries/Initializers.Generated.cs > /tmp/before.txt
"""
import sys, re

src = open(sys.argv[1], errors='replace').read()
out = set()

# class/module definitions
for m in re.finditer(r'Define(?:Global)?(Class|Module)\("([^"]+)"', src):
    out.add('%s %s' % (m.group(1).lower(), m.group(2)))
for m in re.finditer(r'Extend(Class|Module)\(typeof\(([^)]+)\)', src):
    out.add('extend%s %s' % (m.group(1).lower(), m.group(2)))

# methods / constants / aliases, attributed to the enclosing Load<X>_<Y> trait
cur = '?'
for line in src.splitlines():
    m = re.match(r'\s*private static void (Load\w+)\(', line)
    if m:
        cur = m.group(1)
        continue
    for m in re.finditer(r'DefineLibraryMethod\(module, "([^"]+)"', line):
        out.add('%s#%s' % (cur, m.group(1)))
    for m in re.finditer(r'DefineRuleGenerator\(module, "([^"]+)"', line):
        out.add('%s#%s' % (cur, m.group(1)))
    for m in re.finditer(r'SetBuiltinConstant\((?:module|\w+), "([^"]+)"', line):
        out.add('%s::%s' % (cur, m.group(1)))
    for m in re.finditer(r'module\.(HideMethod|UndefineMethodNoEvent|UndefineMethod)\("([^"]+)"', line):
        out.add('%s!%s %s' % (cur, m.group(1), m.group(2)))

for x in sorted(out):
    print(x)
