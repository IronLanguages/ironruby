# Pipes and child processes, as release.yml runs them from each Unix archive: the code under them is P/Invoke into
# libc, and what one libc lacks or passes differently only shows on that platform. macOS's libc has no pipe2, so
# IO.pipe, IO.popen and every backquote once raised EntryPointNotFoundException there, and on Apple arm64 a variadic
# argument (fcntl's third, open's mode) goes on the stack, where an ordinary P/Invoke does not put it. Each line is
# printed, and the script exits 1 if any differs from what CRuby prints for it.
require "rbconfig"
require "tmpdir"

$failed = false

def check(name, expected)
  actual = begin
    yield
  rescue Exception => e
    "#{e.class}: #{e.message}"
  end
  ok = actual == expected
  $failed = true unless ok
  puts "#{ok ? "ok  " : "FAIL"} #{name}: #{actual.inspect}#{ok ? "" : " (expected #{expected.inspect})"}"
end

check("IO.pipe", "piped") do
  r, w = IO.pipe
  w.write("piped")
  w.close
  r.read.tap { r.close }
end

# FD_CLOEXEC is set on both ends with F_SETFD where there is no pipe2 to set it atomically
check("IO.pipe close_on_exec?", [true, true]) do
  IO.pipe { |r, w| [r.close_on_exec?, w.close_on_exec?] }
end

check("close_on_exec = false", [false, true]) do
  IO.pipe do |r, w|
    r.close_on_exec = false
    a = r.close_on_exec?
    r.close_on_exec = true
    [a, r.close_on_exec?]
  end
end

check("IO.popen", ["popen\n", true]) { [IO.popen("echo popen", &:read), $?.success?] }
check("IO.popen r+", "duplex") do
  IO.popen(["cat"], "r+") { |io| io.write("duplex"); io.close_write; io.read }
end
check("backquote", ["back\n", 0]) { [`echo back`, $?.exitstatus] }
check("system", [true, false, 3]) do
  [system("true"), system("false"), (system("exit 3"); $?.exitstatus)]
end

# a descriptor the child is given (fd => fd) is open in it, and one it is not given is not
# (bash, not sh: dash takes only one-digit descriptors in a redirection)
check("spawn fd => fd", "via fd\n") do
  r, w = IO.pipe
  pid = spawn("bash", "-c", "echo via fd >&#{w.fileno}", w => w)
  w.close
  Process.wait(pid)
  r.read.tap { r.close }
end
check("spawn leaves our pipe out", ["", false]) do
  r, w = IO.pipe
  pid = spawn("bash", "-c", "echo leaked >&#{w.fileno}", err: File::NULL)
  w.close
  Process.wait(pid)
  [r.read.tap { r.close }, $?.success?]
end

# a file a redirection creates gets the mode it was asked for, less the umask
check("spawn out: [path, w, 0600]", ["spawned\n", "600"]) do
  Dir.mktmpdir do |dir|
    path = File.join(dir, "out")
    Process.wait(spawn("echo spawned", out: [path, "w", 0o600]))
    [File.read(path), format("%o", File.stat(path).mode & 0o777)]
  end
end
# exec does its own redirections, with open(2) in this process before execve
check("exec out: [path, w, 0600]", ["execed\n", "600"]) do
  Dir.mktmpdir do |dir|
    path = File.join(dir, "out")
    system(RbConfig.ruby, "-e", "exec('echo', 'execed', out: [#{path.dump}, 'w', 0600])")
    [File.read(path), format("%o", File.stat(path).mode & 0o777)]
  end
end

check("spawn pgroup", true) do
  pid = spawn("sleep 0.1", pgroup: true)
  pgid = Process.getpgid(pid)
  Process.wait(pid)
  pgid == pid
end

require "io/nonblock"
check("nonblock", [true, false, true]) do
  IO.pipe do |r, _w|
    a = r.nonblock?
    r.nonblock = false
    b = r.nonblock?
    r.nonblock = true
    [a, b, r.nonblock?]
  end
end

exit(1) if $failed
puts "done"
