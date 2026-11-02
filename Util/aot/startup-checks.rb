# Behaviour the startup library (IronRuby.Startup.dll, Util/aot/README.md part D) must not change: what this
# prints has to be the same with the library and without it (IR_STARTUP_DLL=none). check-startup.sh runs it both
# ways and compares.
require "coverage"
require "tmpdir"

def frames(n = 3)
  yield
  "no exception"
rescue Exception => e
  e.backtrace.first(n).map { |f| f.sub(/^.*\//, "") }.join(" | ")
end

# Coverage started at run time (SimpleCov, test runners) measures what is required after it, precompiled or not.
Coverage.start(lines: true)
require "optparse"
require "delegate"
result = Coverage.result
%w[optparse.rb delegate.rb].each do |f|
  key = result.keys.find { |k| k.end_with?("/" + f) }
  puts "coverage #{f}: #{key ? result[key][:lines].compact.size : 'MISSING'}"
end

# Frame labels the runtime gives a method when its def runs: module functions (both copies), class << Time,
# Dir::Tmpname's module_function, a Kernel function called with a receiver.
require "fileutils"
require "time"
puts "FileUtils.mkdir_p: " + frames { FileUtils.mkdir_p("/proc/startup-checks/x") }
puts "Time.parse: " + frames(1) { Time.parse("garbage!!") }
puts "Time.iso8601: " + frames(1) { Time.iso8601("bad") }
puts "Integer: " + frames(2) { Integer("zz") }
puts "Kernel.Integer: " + frames(2) { Kernel.Integer("zz") }
puts "Dir::Tmpname.create: " + frames(4) { Dir::Tmpname.create("x", "/proc/startup-checks") { |p| Dir.mkdir(p) } }
included = Object.new.extend(FileUtils)
puts "FileUtils#mkdir_p: " + frames { included.send(:mkdir_p, "/proc/startup-checks/y") }

# A def compiled from its source (the instance copy of a module function) keeps its file's frozen_string_literal.
class String
  alias_method :__startup_checks_chomp, :chomp
  def chomp(*args)
    $startup_checks_frozen = args[0].frozen?
    __startup_checks_chomp(*args)
  end
end
included.send(:remove_trailing_slash, "a/")
puts "frozen literal (instance copy): #{$startup_checks_frozen}"
FileUtils.send(:remove_trailing_slash, "b/")
puts "frozen literal (module function): #{$startup_checks_frozen}"
class String
  alias_method :chomp, :__startup_checks_chomp
end

# Interpolated strings with literal parts, which precompiled code makes from text.
f = Fiber.new { 1 }
puts "Fiber#inspect: " + f.inspect.sub(/0x\h+/, "0x").sub(/ \S+:\d+/, " file:line")
puts "interpolation: " + "#{[:a, 1].map(&:to_s).join}:#{1 + 1}-x".inspect
