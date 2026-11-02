require 'listen'
require 'fileutils'
require 'tmpdir'

# The polling backend, which needs no OS event API - the ffi-backed inotify
# adapter is a separate question (see POPULAR.md).
dir = File.join(Dir.tmpdir, "listen-exercise-#{Process.pid}")
FileUtils.rm_rf(dir)
FileUtils.mkdir_p(dir)

seen = Queue.new
listener = Listen.to(dir, force_polling: true, latency: 0.05, wait_for_delay: 0.05) do |mod, add, rem|
  seen << [mod.map { |f| File.basename(f) }, add.map { |f| File.basename(f) }, rem.map { |f| File.basename(f) }]
end
puts listener.class
listener.start
puts listener.processing?

# Listen builds its first snapshot of the directory on a thread of its own after #start, so a
# file written at once may already be in it, and then no "added" event comes - on any Ruby,
# just more often on a slower or busier one. Writing it again with other content makes a
# "modified" event in that case, so the answer does not depend on who won the race.
file = File.join(dir, 'a.txt')
File.write(file, 'one')
deadline = Time.now + 10
events = []
rewritten_at = Time.now
while Time.now < deadline
  begin
    events << seen.pop(true)
  rescue ThreadError
    sleep 0.1
    if Time.now - rewritten_at > 0.5
      File.write(file, "one #{Time.now.to_f}")
      rewritten_at = Time.now
    end
  end
  break if events.flatten.include?('a.txt')
end
puts events.flatten.include?('a.txt')

listener.pause
listener.start
puts listener.processing?
listener.stop
puts listener.stopped?

puts Listen::VERSION.split('.').first
puts Listen::Adapter::Polling.usable?
puts Listen::Adapter.select(force_polling: true).name.split('::').last
FileUtils.rm_rf(dir)
