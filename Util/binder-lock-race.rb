# Why several threads binding cold call sites at once kill the process.
#
#   ./ir.sh Util/binder-lock-race.rb [rounds]
#
# Every call site IronRuby has not bound yet goes through RubyCallAction.Resolve,
# which takes the class hierarchy lock and then asserts, inside
# MetaObjectBuilder.AddTargetTypeTest, that it is holding it.  Run that on one
# thread and it is fine.  Run it on eight threads and the assert fires:
#
#   Process terminated. Assertion failed.
#   Code can only be executed while holding class hierarchy lock.
#      at IronRuby.Runtime.RubyContext.RequiresClassHierarchyLock()
#      at IronRuby.Runtime.Calls.MetaObjectBuilder.AddTargetTypeTest(...)
#
# A failed Debug.Assert on .NET Core is not an exception - it is FailFast.  The
# whole process dies with exit 134, so a single unlucky interleaving takes down
# an entire mspec run rather than failing one example.
#
# This script makes that interleaving likely on purpose: each round builds a
# fresh class per thread with freshly named methods, so no call site in it has
# ever been bound before and every thread is inside the binder at the same time.
# It prints a line per round and exits 0 if it survives; if the defect is
# present it dies part way through with the assert above.
#
# It is a race, so it is not certain: before the fix 200 rounds aborted in 3 of
# 4 runs, 40 rounds in 1 of 6.  After the fix it prints "survived N rounds" and
# exits 0 every time.

rounds  = (ARGV[0] || 200).to_i
threads = 8
$stdout.sync = true

rounds.times do |r|
  gate = Queue.new
  ts = (0...threads).map do |t|
    Thread.new do
      # A class nobody has seen, with method names nobody has seen.  Building it
      # here rather than in the parent keeps the class-creation traffic on the
      # lock concurrent too.
      klass = Class.new do
        20.times do |i|
          define_method("m_#{r}_#{t}_#{i}") { |x| x + i }
        end
      end
      obj = klass.new
      gate.pop            # release all threads into the binder together
      sum = 0
      20.times { |i| sum += obj.send("m_#{r}_#{t}_#{i}", i) }
      # Cold sites on builtin receivers as well, so the contention is not only
      # on classes this thread itself created.
      sum += "#{r}#{t}".size + [r, t].size + {r => t}.size
      sum
    end
  end
  threads.times { gate << :go }
  ts.each(&:join)
  puts "round #{r} ok" if r % 10 == 0
end

puts "survived #{rounds} rounds"
