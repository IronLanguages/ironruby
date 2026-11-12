#!/usr/bin/env python3
"""Packs the part of Src/StdLib the browser host needs into stdlib.zip (unpacked by
Interop.Boot into /ironruby/stdlib). Leaves out what cannot work without processes, sockets
or RubyGems, and the big tools (rdoc, irb, bundler)."""
import os, sys, zipfile

src = sys.argv[1]   # Src/StdLib
out = sys.argv[2]   # .../stdlib.zip

EXCLUDE = {
    'ruby/4.0': ['rubygems', 'rubygems.rb', 'prism', 'prism.rb', 'bundler', 'bundler.rb', 'rdoc',
                 'rdoc.rb', 'irb', 'irb.rb', 'reline', 'reline.rb', 'syntax_suggest',
                 'syntax_suggest.rb', 'mkmf.rb', 'bundled_gems.rb', 'gem_prelude.rb', 'un.rb'],
    'ironruby': ['puma', 'puma.rb', 'nio', 'nio.rb', 'nio4r.rb', 'nio4r_ext.rb', 'websocket',
                 'ffi', 'ffi.rb', 'fiddle', 'fiddle.rb', 'PresentationCore.rb',
                 'PresentationFramework.rb', 'rack'],
}

# no sockets, processes, rake/irb/bundler in the browser
BUNDLED_GEMS = {'abbrev', 'base64', 'benchmark', 'csv', 'getoptlong', 'logger', 'matrix', 'minitest',
                'mutex_m', 'observer', 'ostruct', 'power_assert', 'prime', 'pstore', 'rexml', 'test-unit'}

n = 0
with zipfile.ZipFile(out, 'w', zipfile.ZIP_STORED) as z:
    for top, excl in EXCLUDE.items():
        root = os.path.join(src, top)
        for name in sorted(os.listdir(root)):
            if name in excl:
                continue
            path = os.path.join(root, name)
            paths = [path] if os.path.isfile(path) else [
                os.path.join(d, f) for d, _, fs in os.walk(path) for f in fs]
            for p in sorted(paths):
                if p.endswith(('.dll', '.so', '.exe', '.pdb')):
                    continue
                arc = 'stdlib/' + os.path.relpath(p, src).replace(os.sep, '/')
                z.write(p, arc)
                n += 1
    # Pure-Ruby bundled gems (ostruct, prime, csv, matrix, ...). Without RubyGems they are found
    # through one flat directory on $LOAD_PATH (Interop.Boot adds stdlib/bundled-gems).
    gems = os.path.join(src, 'ruby', 'gems', '4.0.0', 'gems')
    for gem in sorted(os.listdir(gems)):
        if gem.rsplit('-', 1)[0] not in BUNDLED_GEMS:
            continue
        lib = os.path.join(gems, gem, 'lib')
        for d, _, fs in os.walk(lib):
            for f in sorted(fs):
                if f.endswith('.rb'):
                    p = os.path.join(d, f)
                    z.write(p, 'stdlib/bundled-gems/' + os.path.relpath(p, lib).replace(os.sep, '/'))
                    n += 1
print(f'{out}: {n} files, {os.path.getsize(out)//1024} KB')
