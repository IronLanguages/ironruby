#!/usr/bin/env python3
"""Drive int-matrix.rb under ir, restarting past any expression that kills the
process (a .NET stack overflow cannot be rescued from Ruby).

    python3 Util/int-matrix.py /tmp/m-ir.txt [interpreter]
"""
import subprocess, sys, os, re

out_path = sys.argv[1]
exe = sys.argv[2] if len(sys.argv) > 2 else './ir.sh'
here = os.path.dirname(os.path.abspath(__file__))
root = os.path.dirname(here)
script = os.path.join(here, 'int-matrix.rb')
env = dict(os.environ, DOTNET_ROOT='/usr/local/dotnet',
           PATH='/usr/local/dotnet:' + os.environ['PATH'])

lines = {}
start = 0
crashes = []
while True:
    r = subprocess.run([exe, script, str(start)], cwd=root, env=env,
                       capture_output=True, text=True, timeout=900)
    last = start - 1
    for l in r.stdout.splitlines():
        m = re.match(r'(\d+)\t(.*)', l)
        if m:
            last = int(m.group(1))
            lines[last] = m.group(2)
    if r.returncode == 0:
        break
    crashes.append(last + 1)
    start = last + 2
    if len(crashes) > 200:
        break

with open(out_path, 'w') as f:
    for i in sorted(lines):
        f.write('%d\t%s\n' % (i, lines[i]))
print('wrote %d lines; %d expressions killed the process at indexes %s'
      % (len(lines), len(crashes), crashes))
