# Differential matrix for Process.spawn and the rest of the exec family.
#
# Process.spawn's options are a cross-product - the command form (one string, a
# word list, a [file, argv0] pair), the env hash, :unsetenv_others, :chdir,
# :umask, :pgroup and the :in/:out/:err redirections, each of which takes a path,
# a mode pair, a descriptor, an IO, :close or [:child, fd] - and a fix to one
# corner of it routinely breaks another. Rendering the whole product one line at a
# time and diffing the result against CRuby is how the corners stay honest:
#
#   ruby    Util/spawn_matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/spawn_matrix.rb > /tmp/ir.txt
#   diff /tmp/mri.txt /tmp/ir.txt | grep -c '^<'
#
# Every line is "<name> | <result>", so a diff names the case that differs.

require 'tmpdir'

DIR = Dir.mktmpdir("spawn_matrix")
OUT = File.join(DIR, "out")
at_exit { Dir.glob(File.join(DIR, "*")).each { |f| File.delete(f) rescue nil }; Dir.rmdir(DIR) rescue nil }

# A child that is a plain shell script rather than a Ruby process: starting one
# CRuby per line would make the matrix take minutes, and everything the matrix
# asks about is visible to /bin/sh.
REPORTER = File.join(DIR, "report.sh")
File.write(REPORTER, <<~SH)
  #!/bin/sh
  echo "argv0=$0 args=$*"
  echo "pwd=$(pwd)"
  echo "umask=$(umask)"
  echo "FOO=${FOO-<unset>} BAR=${BAR-<unset>}"
  echo "pathcount=$(echo $PATH | tr ':' '\\n' | wc -l)"
SH
File.chmod(0o755, REPORTER)

# The temporary directory is in half the outputs, and its name changes every run,
# so it is folded away before anything is printed.
def normalize(text)
  text.to_s.gsub(DIR, "<dir>").gsub("\n", "\\n")
end

def show(name)
  result =
    begin
      value = yield
      value.nil? ? "nil" : value.to_s
    rescue Exception => e
      "#{e.class}: #{e.message}"
    end
  puts "#{name} | #{normalize(result)}"
end

# Runs a child and reports what it wrote plus how it finished, so that one line
# covers both the redirection and the exit status.
def run(name, *args)
  File.delete(OUT) if File.exist?(OUT)
  show(name) do
    options = args.last.is_a?(Hash) ? args.pop : {}
    options = options.merge(out: OUT) unless options.key?(:out) || options.key?([:out, :err])
    pid = Process.spawn(*args, options)
    Process.waitpid(pid)
    text = File.exist?(OUT) ? File.read(OUT) : ""
    "status=#{$?.exitstatus.inspect} signaled=#{$?.signaled?} out=#{text.inspect}"
  end
end

puts "--- command forms"
run("single word",            "true")
run("single with args",       "#{REPORTER} a b")
run("collapsing whitespace",  "#{REPORTER} a b  c   d")
run("shell metacharacter",    "#{REPORTER} *")
run("shell builtin",          "exit 29")
run("word list",              REPORTER, "a b  c   d")
run("argv0 pair",             [REPORTER, "argv_zero"], "x")
run("missing command",        "definitely-no-such-command-42")
run("missing path",           "./definitely-no-such-command-42")
run("directory",              "./")
run("empty string",           "")
run("null byte",              "\0")
run("not a string",           :echo)

puts "--- environment"
run("env set",                { "FOO" => "one" }, REPORTER)
run("env unset",              { "PATH" => ENV["PATH"], "FOO" => nil }, REPORTER)
run("env equals in key",      { "FOO=" => "one" }, REPORTER)
run("env null in key",        { "\0" => "one" }, REPORTER)
run("env null in value",      { "FOO" => "\0" }, REPORTER)
run("unsetenv_others",        { "PATH" => ENV["PATH"] }, REPORTER, unsetenv_others: true)
run("unsetenv_others false",  { "PATH" => ENV["PATH"] }, REPORTER, unsetenv_others: false)
run("unsetenv_others bad",    REPORTER, unsetenv_others: 1)

puts "--- chdir and umask"
run("chdir",                  REPORTER, chdir: "/")
run("chdir missing",          REPORTER, chdir: "/no-such-directory-42")
run("umask",                  REPORTER, umask: 0o146)
run("umask default",          REPORTER)

puts "--- pgroup"
show("pgroup default") do
  pid = Process.spawn("true", pgroup: nil)
  Process.waitpid(pid)
  "ok"
end
run("pgroup negative",        REPORTER, pgroup: -1)
run("pgroup symbol",          REPORTER, pgroup: :true)
show("pgroup true") do
  read, write = IO.pipe
  pid = Process.spawn("ps -o pgid= -p $$", pgroup: true, out: write)
  write.close
  group = read.read.to_s.strip
  read.close
  Process.waitpid(pid)
  group == Process.getpgid(Process.pid).to_s ? "same as parent" : "own group"
end

puts "--- redirection"
run("out path",               "#{REPORTER} x", out: OUT)
run("out path and mode",      "#{REPORTER} x", out: [OUT, "w"])
run("out append",             "#{REPORTER} x", out: [OUT, "a"])
run("err to path",            "sh -c 'echo e >&2'", err: OUT, out: "/dev/null")
run("err to child out",       "sh -c 'echo o; echo e >&2'", out: OUT, err: [:child, :out])
run("out and err together",   "sh -c 'echo o; echo e >&2'", [:out, :err] => OUT)
run("err closed",             "sh -c 'echo o; echo e >&2'", out: OUT, err: :close)
run("in from path",           "cat", in: REPORTER, out: OUT)
show("out to an IO") do
  File.open(OUT, "w") { |f| Process.waitpid(Process.spawn("echo io", out: f)) }
  File.read(OUT).inspect
end
show("out to a descriptor") do
  File.open(OUT, "w") { |f| Process.waitpid(Process.spawn("echo fd", out: f.fileno)) }
  File.read(OUT).inspect
end
show("descriptor to itself") do
  File.open(OUT, "w") do |f|
    Process.waitpid(Process.spawn("sh -c 'echo self >&#{f.fileno}'", f.fileno => f.fileno))
  end
  File.read(OUT).inspect
end

puts "--- options validation"
run("string option key",      REPORTER, "chdir" => "/")
run("unknown option key",     REPORTER, nonesuch: 1)
show("no arguments")          { Process.spawn }
show("env only")              { Process.spawn({}) }
show("env and options only")  { Process.spawn({}, {}) }

puts "--- system"
show("system true")           { system("true") }
show("system false")          { system("false") }
show("system missing")        { system("definitely-no-such-command-42") }
show("system status")         { system("exit 7"); $?.exitstatus }
show("system argv")           { system(REPORTER, "a", out: File::NULL) }
show("system env")            { system({ "FOO" => "one" }, REPORTER, out: File::NULL) }
show("system exception")      { system("definitely-no-such-command-42", exception: true) }

puts "--- backquotes"
show("backquote")             { `echo hi`.inspect }
show("backquote status")      { `sh -c 'exit 9'`; $?.exitstatus }
show("backquote stderr")      { `sh -c 'echo e >&2' 2>/dev/null`.inspect }
show("backquote missing")     { `definitely-no-such-command-42`.inspect }

puts "--- status"
show("exited")                { system("exit 3"); [$?.exited?, $?.exitstatus, $?.signaled?, $?.termsig, $?.success?, $?.stopped?, $?.coredump?].inspect }
show("signaled")              { system("kill -KILL $$"); [$?.exited?, $?.exitstatus, $?.signaled?, $?.termsig, $?.success?, $?.stopped?].inspect }
show("to_i exited")           { system("exit 3"); $?.to_i }
show("to_i signaled")         { system("kill -KILL $$"); $?.to_i }
show("== integer")            { system("exit 3"); $? == $?.to_i }
show("to_s")                  { system("exit 3"); $?.to_s.sub(/pid \d+/, "pid N") }
show("inspect")               { system("kill -TERM $$"); $?.inspect.sub(/pid \d+/, "pid N") }

puts "--- waiting"
show("wait returns pid")      { pid = Process.spawn("true"); Process.wait == pid }
show("wait2")                 { pid = Process.spawn("exit 4"); r = Process.wait2(pid); [r[0] == pid, r[1].exitstatus].inspect }
show("waitall")               { 2.times { Process.spawn("true") }; Process.waitall.size }
show("wait no children")      { Process.wait }
show("waitpid unknown")       { Process.waitpid(999_999) }
show("Status.wait no child")  { Process::Status.wait.pid }
show("popen read")            { IO.popen("echo popen") { |io| io.read }.inspect }
show("popen status")          { IO.popen("sh -c 'exit 5'") { |io| io.read }; $?.exitstatus }
show("popen pid")             { io = IO.popen("true"); ok = io.pid.is_a?(Integer); io.close; ok }
show("popen write")           { IO.popen("cat > #{OUT}", "w") { |io| io.write("piped") }; File.read(OUT) }
