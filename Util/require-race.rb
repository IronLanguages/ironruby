# What a require of a file another thread is still loading used to answer.
#
#   ./ir.sh Util/require-race.rb
#
# Eight threads require the same file at once.  The file sleeps before it
# defines its constant, so the winner is inside it for a while, and the other
# seven arrive while it is still running.
#
# require answers true to the thread that loaded the file and false to everybody
# else, and in MRI false means "it is loaded, you may use it" - the latecomers
# blocked on a per-file lock until the loader finished.  This runtime kept the
# in-progress files in a set and answered false as soon as it saw one, without
# waiting, so false meant "somebody is on it" and the caller carried on using
# constants that did not exist yet:
#
#   [[true, :sees_it], [false, :MISSING], [false, :MISSING], [false, :MISSING],
#    [false, :MISSING], [false, :MISSING], [false, :MISSING], [false, :MISSING]]
#
# Seven of eight threads were told the file was available and it was not.  The
# same set was a Stack<string> shared by every thread and pushed and popped
# without a lock, which is its own problem.
#
# Expected now, and what this prints:
#
#   [[true, :sees_it], [false, :sees_it], ... ]  x8
#
# A circular require - the same thread re-entering a file it is already loading
# - still answers false immediately, which is the case the set was written for
# and the only case where returning before the file is finished is right.

require 'tmpdir'

Dir.mktmpdir do |dir|
  slow = File.join(dir, "slow.rb")
  File.write(slow, "sleep 0.4\nSLOW = :loaded\n")

  ts = 8.times.map do
    Thread.new { [require(slow), defined?(SLOW) ? :sees_it : :MISSING] }
  end
  p ts.map(&:value)

  # Circular require, on one thread: a.rb requires b.rb requires a.rb.
  a = File.join(dir, "a.rb")
  b = File.join(dir, "b.rb")
  File.write(a, "$order << :a_start\nrequire #{b.inspect}\nA = 1\n$order << :a_end\n")
  File.write(b, "$order << :b_start\nrequire #{a.inspect}\nB = 1\n$order << :b_end\n")
  $order = []
  require a
  p [$order, defined?(A), defined?(B)]
end
