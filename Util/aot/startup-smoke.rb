# What package-release.sh runs from a release's tree with its startup library (IronRuby.Startup.dll) and without it
# (IR_STARTUP_DLL=none): the two must print the same, or the archive goes without the library. Every line is
# something the code the library holds - the core prelude, rbconfig, rubygems - set up, or something a common
# program does right after; nothing prints a path, a time or an address, which differ between two runs.
puts "engine: #{RUBY_ENGINE} #{RUBY_VERSION}"
# names the prelude defines and makes private (the osx-arm64 library of 58099b4e had mangled one of them)
puts "prelude: #{Kernel.private_method_defined?(:__ir_message_type_name__)}"
puts "symbols: #{%i[each_slot __id__ respond_to_missing?].map(&:inspect).join(" ")}"
begin
  Integer("zz")
rescue ArgumentError => e
  puts "rescue: #{e.class}: #{e.message}"
end
begin
  [].fetch(3)
rescue IndexError => e
  puts "rescue: #{e.class}: #{e.message}"
end
require "rbconfig"
puts "rbconfig: #{RbConfig::CONFIG["ruby_install_name"]} #{RbConfig::CONFIG["MAJOR"]}.#{RbConfig::CONFIG["MINOR"]}"
puts "rubygems: #{Gem::VERSION} #{Gem::Version.new("1.2.3") < Gem::Version.new("1.10")}"
puts "gem json: #{!Gem::Specification.find_all_by_name("json").empty?}"
require "json"
puts "json: #{JSON.generate({ "a" => [1, 2.5, nil, true] })} #{JSON.parse('[1, {"b": "c"}]').inspect}"
require "set"
puts "set: #{Set[3, 1, 2].sort.inspect}"
require "pp"
puts "pp: #{{ a: 1 }.pretty_inspect.chomp}"
require "irb"
puts "irb: #{defined?(IRB.start).inspect}"
puts "frozen: #{"lit".frozen?} #{:sym.to_s.frozen?}"
puts "format: #{format("%05.2f|%-3s|%x", 3.14159, "a", 255)}"
# Methods of the library whose code runs only when called, not at startup: a method compiled wrong fails here, at its
# first call (a Fiber's block and IO.pipe once threw InvalidProgramException, and the fiber then hung).
puts "fiber: #{Fiber.new { |x| Fiber.yield(x + 1); :end }.then { |f| [f.resume(1), f.resume, f.alive?] }.inspect}"
# (what a pipe raises is printed, not raised, so that the two runs still compare; release.yml fails the job when either
# line is not the pipe's or the command's output - macOS's libc has no pipe2, and both raised EntryPointNotFoundException
# there until the fallback to pipe and fcntl)
begin
  r, w = IO.pipe
  w.write("piped")
  w.close
  puts "pipe: #{r.read}"
  r.close
rescue Exception => e
  puts "pipe: #{e.class}"
end
begin
  puts "popen: #{IO.popen("echo popen", &:read).strip} #{$?.success?}"
rescue Exception => e
  puts "popen: #{e.class}"
end
require "tempfile"
puts "tempfile: #{Tempfile.create("smoke") { |f| f.write("x"); f.flush; File.size(f.path) }}"
point = Data.define(:x, :y)
puts "data: #{point.new(x: 1, y: 2).inspect} #{point.new(1, 2).hash == point.new(1, 2).hash}"
puts "set inspect: #{Set[1, 2].inspect}"
puts "hash eql: #{{ a: 1 }.eql?({ a: 1 })}"
require "stringio"
puts "stringio: #{StringIO.open("s\nt") { |io| io.readlines.size }}"
puts "select!: #{[1, 2, 3].select! { |v| v > 1 }.inspect}"
begin
  [1].each { raise IndexError, "in block" }
rescue IndexError => e
  puts "ensure path: #{e.message}"
ensure
  puts "ensure ran"
end
puts "done"
