# Why `mspec-run spec/core/kernel` used to never finish.
#
#   ./ir.sh Util/kernel-require-wedge.rb
#
# The directory wedged on one example, spec/core/kernel/require_spec.rb ->
# spec/core/kernel/shared/require.rb:667, "blocks a second thread from
# returning while the 1st is still requiring".  It was a deadlock, not
# slowness: a busy-wait with no timeout cannot be waited out, so raising
# mspec's cap did not help and never would.
#
# This script is that example with the two busy-waits bounded and instrumented,
# so it reports which thread is stuck and on which term instead of hanging.
# Against the tree as it was it printed:
#
#   events            : [:con_pre, :set_thread_local,
#                        [:t1_gave_up, {other_backtrace: "nil", other_stop?: true}],
#                        :con_post]
#
# and it now prints
#
#   events            : [:con_pre, :set_thread_local, :con_post]
#   t2 saw t1's thread-local : true
#   t1_res=true t2_res=false
#
# The thread that spun forever was t1, inside the fixture
# spec/fixtures/code/concurrent.rb, on this line:
#
#     Thread.pass until t.backtrace && t.backtrace.any? { |c| c.include? 'require' } && t.stop?
#
# Two separate defects kept that condition permanently false, and both had to go.
#
#   * Thread#backtrace answered nil for any thread other than the caller.  The
#     reason given was that .NET Core has no Thread.Suspend and no
#     StackTrace(Thread), so another thread's stack cannot be walked - true, and
#     beside the point, because the interpreter already keeps each thread's Ruby
#     frames as a linked list in a thread-local and that list can simply be read.
#     See Util/thread-backtrace-matrix.rb.
#
#   * t2 never blocked.  A require of a file another thread had started but not
#     finished returned false immediately, so by the time t1 looked, t2 was not
#     inside require at all - it had finished.  That is also a correctness bug in
#     its own right: false means "already loaded" to the caller, which then uses
#     constants the other thread has not defined yet.  require now waits, as MRI
#     does, and only a circular require on the *same* thread short-circuits.
#
# spec/core/kernel therefore runs as a directory again: 2204 examples, 211
# failures, 209 errors.  It no longer has to be swept per file.
#
require 'thread'
$stdout.sync = true

FIX = "/tmp/kn/concurrent_probe.rb"
File.write(FIX, <<'FIXTURE')
$probe << :con_pre
Thread.current[:in_concurrent_rb] = true
$probe << :set_thread_local

if t = Thread.current[:wait_for]
  spins = 0
  until (t.backtrace && t.backtrace.any? { |c| c.include?('require') } && t.stop?)
    spins += 1
    if spins > 300_000
      $probe << [:t1_gave_up, :other_backtrace => t.backtrace.inspect, :other_stop? => t.stop?]
      break
    end
    Thread.pass
  end
end
$probe << :con_post
FIXTURE

$probe = Queue.new
fin = false
t1_res = t2_res = nil
t2 = nil

t1 = Thread.new do
  Thread.pass until t2
  Thread.current[:wait_for] = t2
  t1_res = require(FIX)
  n = 0
  until fin
    break if (n += 1) > 300_000
    Thread.pass
  end
end

t2_saw_local = false
t2 = Thread.new do
  n = 0
  until t1[:in_concurrent_rb]
    if (n += 1) > 300_000
      $probe << :t2_never_saw_thread_local
      break
    end
    Thread.pass
  end
  t2_saw_local = !!t1[:in_concurrent_rb]
  begin
    t2_res = require(FIX)
  ensure
    fin = true
  end
end

t1.join(60) or puts "t1 DID NOT FINISH"
t2.join(60) or puts "t2 DID NOT FINISH"

events = []
events << $probe.pop until $probe.empty?
puts "events            : #{events.inspect}"
puts "t2 saw t1's thread-local : #{t2_saw_local}"
puts "t1_res=#{t1_res.inspect} t2_res=#{t2_res.inspect}"
