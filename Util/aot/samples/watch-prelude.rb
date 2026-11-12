# Diagnostic for the NativeAOT build: load the core prelude by hand (run with IR_NO_PRELUDE=1)
# while a watchdog thread prints where the main thread is.
$stderr.syswrite "script started\n"
Thread.new do
  while true
    sleep 15
    $stderr.syswrite "--- main thread at:\n"
    $stderr.syswrite Thread.main.backtrace[0, 12].join("\n") + "\n"
  end
end
$stderr.syswrite "watchdog started\n"
t = Time.now
require 'ruby4.rb'
puts "prelude loaded in #{Time.now - t} s"
