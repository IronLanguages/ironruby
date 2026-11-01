#!/usr/bin/env python3
"""Warm start vs cold start, case by case (Util/warm/README.md).

    Util/warm/check.py [-v] [-k SUBSTRING]

Runs each case twice through ir.sh - cold, and with IR_SERVER=1 - and compares stdout, stderr
and how the process ended. A case passes when both agree; it also records whether the warm run
really was warm (IR_SERVER_VERBOSE says) and fails when a case that should be warm went cold,
or the other way round. Needs a running pool started from this environment:

    IR_CONFIG=R2R IR_SERVER=start ./ir.sh
"""
import fcntl, os, pty, re, select, shutil, signal, subprocess, sys, tempfile, termios, threading, time

IR_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
IR = os.path.join(IR_ROOT, 'ir.sh')
TMP = tempfile.mkdtemp(prefix='warm-check-')
BASE_ENV = dict(os.environ)
BASE_ENV.setdefault('IR_CONFIG', 'R2R')
for k in ('IR_SERVER', 'IR_SERVER_VERBOSE'):
    BASE_ENV.pop(k, None)

verbose = '-v' in sys.argv
only = sys.argv[sys.argv.index('-k') + 1] if '-k' in sys.argv else None


def write(name, text):
    path = os.path.join(TMP, name)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, 'w') as f:
        f.write(text)
    return path


write('script.rb', 'p ARGV, $0, __FILE__, __dir__ == File.dirname(File.expand_path(__FILE__)), $PROGRAM_NAME\nputs DATA.read\n__END__\ndata section\n')
write('sub/rel.rb', 'p Dir.pwd == File.dirname(File.expand_path(__FILE__)), __FILE__, $0\n')
write('lib/warmfoo.rb', 'puts "warmfoo loaded"\nWARMFOO = 1\n')
write('shadow/rubygems.rb', 'puts "shadowing rubygems.rb loaded"\n')
write('x.rb', 'junk line\n#!ruby\np :after_x\n__END__\n')
write('s.rb', 'p $xyz, $abc\n')
write('syntax.rb', 'def foo\n')
write('stdin_prog.rb', 'puts 5\n')

# (name, argv, options): options keys
#   stdin (bytes), env (dict to add; None value removes), cwd, umask, rlimit (list), ignore (signals),
#   expect ('warm' or 'cold'; default warm), kill (signal sent once 'ready' is printed),
#   stdout_file (stdout to a file), merge (2>&1)
CASES = [
    ('puts', ['-e', 'puts 1']),
    ('argv', ['-e', 'p ARGV, $0, __FILE__, $PROGRAM_NAME, __dir__', 'a', 'b c', '']),
    ('script file', [os.path.join(TMP, 'script.rb'), 'x', 'y']),
    ('relative script', ['rel.rb'], {'cwd': os.path.join(TMP, 'sub')}),
    ('relative script ../', ['sub/rel.rb'], {'cwd': TMP}),
    ('-C', ['-C', os.path.join(TMP, 'sub'), '-e', 'p Dir.pwd']),
    ('cwd', ['-e', 'p Dir.pwd'], {'cwd': TMP}),
    ('env', ['-e', 'p ENV["WARM_TEST"], ENV.key?("WARM_GONE"), ENV["EMPTY"]'],
     {'env': {'WARM_TEST': 'v=1 2', 'WARM_GONE': None, 'EMPTY': ''}}),
    ('env keys', ['-e', 'puts ENV.keys.sort.grep_v(/^IR_SERVER/)']),
    ('subprocess env', ['-e', 'system("sh", "-c", "echo $WARM_TEST; echo ${WARM_GONE-unset}")'],
     {'env': {'WARM_TEST': 'child sees it', 'WARM_GONE': None}}),
    ('umask', ['-e', 'printf "%o\\n", File.umask; File.write("u.txt", ""); printf "%o\\n", File.stat("u.txt").mode & 0777'],
     {'umask': 0o027, 'cwd': TMP}),
    ('exit 3', ['-e', 'exit 3']),
    ('exit false', ['-e', 'exit false']),
    ('exit!', ['-e', 'at_exit { puts "no" }; exit! 4']),
    ('abort', ['-e', 'abort "bye"']),
    ('raise', ['-e', 'def f = raise("boom")\nf']),
    ('raise custom', ['-e', 'class E < StandardError; def message = "custom"; end; raise E']),
    ('SystemExit in thread', ['-e', 'Thread.new { exit 9 }.join; puts "unreached"']),
    ('at_exit', ['-e', 'at_exit { puts "ae1" }; at_exit { puts "ae2"; exit 7 }; puts "main"']),
    ('at_exit after raise', ['-e', 'at_exit { $stderr.puts "handler sees #{$!.inspect}" }; raise "y"']),
    ('throw', ['-e', 'throw :x']),
    ('syntax error -e', ['-e', 'def']),
    ('syntax error file', [os.path.join(TMP, 'syntax.rb')]),
    ('missing file', [os.path.join(TMP, 'nope.rb')]),
    ('self TERM', ['-e', 'Process.kill(:TERM, $$); sleep 2; puts "unreached"']),
    ('self KILL', ['-e', '$stdout.sync = true; puts 1; Process.kill(:KILL, Process.pid); sleep 2']),
    ('interrupt raise', ['-e', 'raise Interrupt']),
    ('stdin read', ['-e', 'p $stdin.read'], {'stdin': b'abc\ndef'}),
    ('stdin gets', ['-e', 'p gets, gets, gets'], {'stdin': b'1\n2\n'}),
    ('stdin devnull', ['-e', 'p $stdin.read, $stdin.eof?'], {'stdin': None}),
    ('ARGF', ['-e', 'p ARGF.read'], {'stdin': b'argf\n'}),
    ('ARGF files', ['-e', 'p ARGF.read, ARGV', os.path.join(TMP, 's.rb'), os.path.join(TMP, 's.rb')]),
    ('-n', ['-n', '-e', 'print $_.upcase'], {'stdin': b'a\nb\n'}),
    ('-p -l', ['-p', '-l', '-e', '$_ = $_.reverse'], {'stdin': b'ab\ncd\n'}),
    ('-a', ['-a', '-n', '-e', 'p $F'], {'stdin': b'a b\nc  d\n'}),
    ('-a -F', ['-a', '-F:', '-n', '-e', 'p $F'], {'stdin': b'a:b\nc:d\n', 'expect': 'cold'}),
    ('program on stdin', [], {'stdin': b'puts 6 * 7\n'}),
    ('program on stdin -', ['-', 'arg'], {'stdin': b'p ARGV, $0\n'}),
    ('-x', ['-x', os.path.join(TMP, 'x.rb')]),
    ('-s', ['-s', os.path.join(TMP, 's.rb'), '-xyz', '-abc=val']),
    ('-c', ['-c', os.path.join(TMP, 'script.rb')]),
    ('-v', ['-v'], {'expect': 'cold'}),
    ('--version', ['--version'], {'expect': 'cold'}),
    ('-v -e', ['-v', '-e', 'p $VERBOSE']),
    ('-w', ['-w', '-e', 'p $VERBOSE; x = 1']),
    ('-W0', ['-W0', '-e', 'warn "hidden"; p $VERBOSE']),
    ('-W:category', ['-W:no-deprecated', '-W:performance', '-e', 'p Warning[:deprecated], Warning[:performance], Warning[:experimental]'], {'expect': 'cold'}),
    ('-0', ['-0', '-n', '-e', 'p $_'], {'stdin': b'a\0b\0'}),
    ('-i', ['-i.bak', '-p', '-e', '$_.upcase!', 'inplace.txt'], {'cwd': TMP, 'setup': ('inplace.txt', 'abc\n'), 'after': 'inplace.txt', 'expect': 'cold'}),
    ('backtrace-limit', ['--backtrace-limit=1', '-e', 'def a = b; def b = c; def c = raise("x"); a']),
    ('-d', ['-d', '-e', 'p $DEBUG'], {'expect': 'cold'}),
    ('--disable-gems', ['--disable-gems', '-e', 'p defined?(Gem)'], {'expect': 'cold'}),
    ('-E', ['-E', 'ASCII-8BIT:UTF-8', '-e', 'p Encoding.default_external, Encoding.default_internal'], {'expect': 'cold'}),
    ('frozen literal', ['--enable=frozen-string-literal', '-e', 'p "x".frozen?'], {'expect': 'cold'}),
    ('-h', ['-h'], {'expect': 'cold'}),
    ('invalid option', ['--no-such-option']),
    ('-I -r', ['-I', os.path.join(TMP, 'lib'), '-r', 'warmfoo', '-e', 'p WARMFOO, $LOADED_FEATURES.grep(/warmfoo/).size, $LOAD_PATH.first == File.expand_path(ARGV[0])', os.path.join(TMP, 'lib')]),
    ('-I relative', ['-Ilib', '-e', 'require "warmfoo"; p $LOAD_PATH.first'], {'cwd': TMP}),
    ('-I shadows prelude file', ['-I', os.path.join(TMP, 'shadow'), '-e', 'p 1'], {'expect': 'cold'}),
    ('-r set -r json', ['-rset', '-rjson', '-e', 'p Set[1], JSON.generate([1])']),
    ('require set json', ['-e', 'require "set"; require "json"; p Set[1], JSON.generate([1])']),
    ('$LOAD_PATH', ['-e', 'puts $LOAD_PATH']),
    ('$LOADED_FEATURES', ['-e', 'puts $LOADED_FEATURES']),
    ('gem paths', ['-e', 'p Gem.path, Gem.dir, Gem.user_dir, Gem.default_dir']),
    ('encodings', ['-e', 'p Encoding.default_external, Encoding.default_internal, __ENCODING__, Encoding.find("locale")']),
    ('LANG=C', ['-e', 'p Encoding.default_external'], {'env': {'LANG': 'C', 'LC_ALL': 'C'}, 'expect': 'cold'}),
    ('RUBYOPT', ['-e', 'p $VERBOSE'], {'env': {'RUBYOPT': '-w'}, 'expect': 'cold'}),
    ('RUBYLIB', ['-e', 'require "warmfoo"'], {'env': {'RUBYLIB': os.path.join(TMP, 'lib')}, 'expect': 'cold'}),
    ('GEM_HOME', ['-e', 'p Gem.dir'], {'env': {'GEM_HOME': os.path.join(TMP, 'gems')}, 'expect': 'cold'}),
    ('TZ', ['-e', 'p Time.at(0).utc_offset'], {'env': {'TZ': 'Asia/Tokyo'}, 'expect': 'cold'}),
    ('HOME', ['-e', 'p Dir.home'], {'env': {'HOME': TMP}, 'expect': 'cold'}),
    ('ruby description', ['-e', 'p RUBY_DESCRIPTION, RUBY_ENGINE, RUBY_VERSION']),
    ('argv0', ['-e', 'p Process.argv0, $0']),
    ('ppid', ['-e', 'p Process.ppid == ENV["HARNESS_PID"].to_i'], {'env': {'HARNESS_PID': str(os.getpid())}}),
    ('sync tty', ['-e', 'p $stdout.sync, $stderr.sync, $stdin.tty?, $stdout.tty?, $stderr.tty?']),
    ('interleave', ['-e', '$stdout.print "a"; $stderr.print "b"; $stdout.print "c"; $stderr.print "d"; $stdout.flush; $stderr.puts'], {'merge': True}),
    ('stdout file', ['-e', 'puts "to a file"; $stderr.puts "err"'], {'stdout_file': True}),
    ('big output', ['-e', '200000.times { |i| puts i }']),
    ('system/backtick/spawn', ['-e', 'system("echo hi"); p $?.exitstatus; puts `echo there`; pid = spawn("sh", "-c", "exit 5"); Process.wait(pid); p $?.exitstatus; p system("exit 2"), $?.exitstatus']),
    ('child stdin', ['-e', 'system("cat")'], {'stdin': b'piped to the child\n'}),
    ('exec', ['-e', 'puts "before"; $stdout.flush; exec "sh", "-c", "echo from exec; exit 6"']),
    ('fork', ['-e', 'begin; pid = fork { puts "child" }; Process.wait(pid); puts "parent"; rescue NotImplementedError => e; p e.class; end']),
    ('IO.popen', ['-e', 'IO.popen(["sh", "-c", "echo popen; echo err >&2"]) { |io| p io.read }']),
    ('threads', ['-e', 't = Thread.new { sleep 0.05; puts "t" }; t.join; Thread.new { sleep }; puts "main done"']),
    ('rlimit nofile', ['-e', 'p Process.getrlimit(:NOFILE)'], {'rlimit': [('NOFILE', 512, 1024)]}),
    ('rlimit core', ['-e', 'p Process.getrlimit(:CORE)'], {'rlimit': [('CORE', 0, 0)]}),
    ('rlimit raise hard', ['-e', 'p Process.getrlimit(:NOFILE)'], {'rlimit': [('NOFILE', 1024, 1 << 20)]}),
    ('ignored INT', ['-e', 'p trap(:INT, "DEFAULT"), trap(:HUP, "DEFAULT")'], {'ignore': [signal.SIGINT]}),
    ('trap INT', ['-e', 'trap(:INT) { puts "got INT"; exit 3 }; $stdout.sync = true; puts "ready"; sleep 5; puts "unreached"'], {'kill': signal.SIGINT}),
    ('default INT', ['-e', '$stdout.sync = true; puts "ready"; sleep 5'], {'kill': signal.SIGINT}),
    ('rescue Interrupt', ['-e', '$stdout.sync = true; begin; puts "ready"; sleep 5; rescue Interrupt => e; p e.class; end'], {'kill': signal.SIGINT}),
    ('default TERM', ['-e', '$stdout.sync = true; at_exit { puts "at_exit ran" }; puts "ready"; sleep 5'], {'kill': signal.SIGTERM}),
    ('trap USR1', ['-e', 'trap(:USR1) { puts "usr1" }; $stdout.sync = true; puts "ready"; sleep 0.5; puts "done"'], {'kill': signal.SIGUSR1}),
    ('broken pipe', ['-e', 'loop { puts :ok }'], {'head': True}),
    ('Process.times', ['-e', 'p Process.times.utime > 0.3']),
    ('rlimit stack', ['-e', 'p Process.getrlimit(:STACK)'], {'rlimit': [('STACK', 4 << 20, -1)], 'expect': 'cold'}),
    ('rlimit as', ['-e', 'p 1'], {'rlimit': [('AS', 1 << 40, -1)], 'expect': 'cold'}),
    ('trap INT twice', ['-e', 'trap(:INT) { puts "INT on main: #{Thread.current == Thread.main}" }; Thread.new { sleep 0.2; Process.kill(:INT, $$) }; sleep 1; puts "end"']),
    ('INT from thread', ['-e', 'Thread.new { sleep 0.2; Process.kill(:INT, $$) }; sleep 1']),
    ('pid', ['-e', 'p Process.pid == ENV["HARNESS_CHILD"].to_i'], {'pidcheck': True, 'gap': 'Process.pid is the worker, not the pid the parent started'}),
    ('pgrp', ['-e', 'p Process.getpgrp == ENV["HARNESS_CHILD"].to_i'], {'pidcheck': True, 'gap': 'the worker runs in its own process group and session'}),
]


def run(argv, opts, warm):
    env = dict(BASE_ENV)
    for k, v in (opts.get('env') or {}).items():
        if v is None:
            env.pop(k, None)
        else:
            env[k] = v
    if warm:
        env['IR_SERVER'] = '1'
        env['IR_SERVER_VERBOSE'] = '1'

    def pre():
        if 'umask' in opts:
            os.umask(opts['umask'])
        import resource
        for name, soft, hard in opts.get('rlimit', []):
            resource.setrlimit(getattr(resource, 'RLIMIT_' + name), (soft, hard))
        for s in opts.get('ignore', []):
            signal.signal(s, signal.SIG_IGN)

    if 'setup' in opts:
        write(*opts['setup'])
    stdin_data = opts.get('stdin', b'')
    stdin = subprocess.DEVNULL if stdin_data is None else subprocess.PIPE
    out_file = None
    if opts.get('stdout_file'):
        out_file = open(os.path.join(TMP, 'out-%d.txt' % warm), 'w+b')
    stdout = out_file if out_file else subprocess.PIPE
    stderr = subprocess.STDOUT if opts.get('merge') else subprocess.PIPE
    cmd = [IR] + argv
    if opts.get('head'):
        cmd = ['sh', '-c', '"$0" "$@" | head -1', IR] + argv
    if opts.get('pidcheck'):
        # the program compares against the pid ir.sh runs as: sh -c 'exec ...' keeps it
        cmd = ['sh', '-c', 'HARNESS_CHILD=$$ exec "$0" "$@"', IR] + argv
    p = subprocess.Popen(cmd, stdin=stdin, stdout=stdout, stderr=stderr, env=env,
                         cwd=opts.get('cwd', IR_ROOT), preexec_fn=pre)
    if 'kill' in opts:
        # wait for "ready", then signal what the parent started
        line = p.stdout.readline()
        time.sleep(0.1)
        os.kill(p.pid, opts['kill'])
        out, err = p.communicate(timeout=60)
        out = line + out
    else:
        out, err = p.communicate(stdin_data if stdin_data else None, timeout=120)
    if out_file:
        out_file.seek(0)
        out = out_file.read()
        out_file.close()
    err = err or b''
    if 'after' in opts:
        with open(os.path.join(TMP, opts['after']), 'rb') as f:
            out = (out or b'') + b'[file] ' + f.read()
    mode = 'n/a'
    lines = []
    for l in err.decode('utf-8', 'replace').splitlines(True):
        if l.startswith('ir-client: warm worker'):
            mode = 'warm'
        elif l.startswith('ir-client: cold start:'):
            mode = 'cold (' + l.split(':', 2)[2].strip() + ')'
        else:
            lines.append(l)
    if warm and mode == 'n/a':
        mode = 'warm?'
    if not warm:
        mode = 'cold'
    # the interleave case merges stderr into stdout: strip the client's lines there too
    o = out.decode('utf-8', 'replace')
    if opts.get('merge'):
        kept = []
        for l in o.splitlines(True):
            if l.startswith('ir-client: warm worker'):
                mode = 'warm'
            elif l.startswith('ir-client: cold start:'):
                mode = 'cold (' + l.split(':', 2)[2].strip() + ')'
            else:
                kept.append(l)
        o = ''.join(kept)
    return p.returncode, o, ''.join(lines), mode


def pty_run(argv, warm, keys=b'', delay=1.5):
    """Runs ir in a new session on a pseudo-terminal, as an interactive shell would: the slave
    is the controlling terminal and the child is its foreground process group."""
    env = dict(BASE_ENV)
    if warm:
        env['IR_SERVER'] = '1'
        env['IR_SERVER_VERBOSE'] = '1'
    master, slave = pty.openpty()
    fcntl.ioctl(slave, termios.TIOCSWINSZ, b'\x18\x00\x50\x00\x00\x00\x00\x00')  # 24x80

    def pre():
        os.setsid()
        fcntl.ioctl(0, termios.TIOCSCTTY, 0)

    p = subprocess.Popen([IR] + argv, stdin=slave, stdout=slave, stderr=slave, env=env, cwd=IR_ROOT,
                         preexec_fn=pre)
    os.close(slave)
    out = b''
    deadline = time.time() + 30
    sent = not keys
    start = time.time()
    while time.time() < deadline:
        if not sent and time.time() - start > delay:
            os.write(master, keys)
            sent = True
        r, _, _ = select.select([master], [], [], 0.05)
        if r:
            try:
                chunk = os.read(master, 4096)
            except OSError:
                break
            if not chunk:
                break
            out += chunk
        elif p.poll() is not None and sent:
            # drain
            try:
                while select.select([master], [], [], 0.1)[0]:
                    chunk = os.read(master, 4096)
                    if not chunk:
                        break
                    out += chunk
            except OSError:
                pass
            break
    p.wait(timeout=30)
    os.close(master)
    text = out.decode('utf-8', 'replace').replace('\r\n', '\n')
    mode = 'cold'
    kept = []
    for l in text.splitlines(True):
        m = re.search(r'ir-client: (warm worker|cold start: )(.*)\n?', l)
        if m:
            mode = 'warm' if m.group(1) == 'warm worker' else 'cold (' + m.group(2).strip() + ')'
            l = l[:m.start()]
        kept.append(l)
    return p.returncode, ''.join(kept), '', mode if warm else 'cold'


PTY_CASES = [
    ('tty', ['-e', 'p $stdin.tty?, $stdout.tty?, $stderr.tty?, $stdout.sync']),
    ('tty winsize', ['-e', 'require "io/console"; p $stdout.winsize'], b''),
    ('IO.console', ['-e', 'require "io/console"; p IO.console.class'], b'', 'gap: no controlling terminal: /dev/tty, IO.console'),
    ('/dev/tty', ['-e', 'File.open("/dev/tty", "w") { |f| f.puts "via tty" }; p :ok'], b'', 'gap: no controlling terminal: /dev/tty, IO.console'),
    ('tty gets', ['-e', 'p gets'], b'typed\n'),
    ('tty EOF', ['-e', 'p $stdin.read'], b'abc\n\x04'),
    ('Ctrl-C', ['-e', '$stdout.sync = true; trap(:INT) { puts "INT"; exit 2 }; puts "ready"; sleep 5'], b'\x03'),
    ('Ctrl-C default', ['-e', '$stdout.sync = true; puts "ready"; sleep 5'], b'\x03'),
    ('Ctrl-\\', ['-e', '$stdout.sync = true; trap(:QUIT) { puts "QUIT"; exit 4 }; puts "ready"; sleep 5'], b'\x1c'),
    ('REPL', [], b'puts 6 * 7\n\x04', None, 'cold'),
]


def show(label, r):
    status, out, err, mode = r
    print('    %s: status %s, mode %s' % (label, status, mode))
    print('      out: %r' % out[:400])
    print('      err: %r' % err[:400])


def main():
    failures = gaps = 0
    for case in CASES:
        name, argv = case[0], case[1]
        opts = case[2] if len(case) > 2 else {}
        if only and only not in name:
            continue
        cold = run(argv, opts, False)
        warm = run(argv, opts, True)
        expect = opts.get('expect', 'warm')
        same = cold[:3] == warm[:3]
        mode_ok = warm[3].startswith(expect)
        if opts.get('gap'):
            tag = 'GAP ' if not same else 'ok  '
            if not same:
                gaps += 1
        else:
            tag = 'ok  ' if same and mode_ok else 'FAIL'
            if tag == 'FAIL':
                failures += 1
        print('%s %-28s %s%s' % (tag, name, warm[3], ('  [%s]' % opts['gap']) if opts.get('gap') and not same else ''))
        if (tag != 'ok  ' or verbose):
            show('cold', cold)
            show('warm', warm)
        sys.stdout.flush()

    for case in PTY_CASES:
        name, argv = case[0], case[1]
        keys = case[2] if len(case) > 2 else b''
        gap = case[3] if len(case) > 3 else None
        expect = case[4] if len(case) > 4 else 'warm'
        if only and only not in name:
            continue
        cold = pty_run(argv, False, keys)
        warm = pty_run(argv, True, keys)
        same = cold[:3] == warm[:3]
        if gap:
            tag = 'ok  ' if same else 'GAP '
            if not same:
                gaps += 1
        else:
            tag = 'ok  ' if same and warm[3].startswith(expect) else 'FAIL'
            if tag == 'FAIL':
                failures += 1
        print('%s pty %-24s %s%s' % (tag, name, warm[3], ('  [%s]' % gap) if gap and not same else ''))
        if tag != 'ok  ' or verbose:
            show('cold', cold)
            show('warm', warm)
        sys.stdout.flush()

    shutil.rmtree(TMP, ignore_errors=True)
    print('%d failures, %d known gaps' % (failures, gaps))
    return 1 if failures else 0


if __name__ == '__main__':
    sys.exit(main())
