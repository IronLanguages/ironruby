# What Thread#backtrace answers for a thread that is not the caller.
#
#   ./ir.sh Util/thread-backtrace-matrix.rb
#
# MRI lets any thread read any other thread's stack.  IronRuby built backtraces
# by walking the CLR stack of whoever asked, which answers only for the calling
# thread, so Thread#backtrace was hard-wired to nil for everybody else:
#
#   sleeping thread      : nil
#   thread blocked on a lock : nil
#   thread in a nested call  : nil
#
# Anything that watches another thread's progress through its stack therefore
# never makes progress.  Util/kernel-require-wedge.rb is one such spec, and
# `Thread.pass until t.backtrace ...` is a shape that appears throughout
# rubyspec, so a nil here does not fail an example, it hangs the file.
#
# Expected after the fix: each case prints a real array whose top frame is
# inside the method named in the case.  The frame list is a snapshot of a
# running thread, so the exact depth can vary by a frame; what must not vary is
# that it is an array rather than nil.
#
# --- what is still missing, and what completing it would cost ----------------
#
# The list being read is the interpreter's frame chain, so it holds a frame for
# every Ruby method that is still being interpreted and nothing for one that has
# been compiled.  The DLR compiles a lambda on its 33rd run
# (LightCompiler.DefaultCompilationThreshold is 32), and the measurement is
# exactly that sharp - warm a three-deep call chain N times, then read the
# thread's backtrace from outside:
#
#   warmup   0 .. 32  ->  5 frames, all three methods named, real line numbers
#   warmup  33        ->  2 frames, the three methods gone
#
# So a method a program calls more than 32 times is invisible to another
# thread's backtrace.  Closing that means a frame record pushed and popped on
# every Ruby method call, because a compiled method leaves nothing else behind:
# .NET Core cannot walk another thread's CLR stack at all.
#
# Measured, so the trade is on the record rather than guessed at.  A Ruby method
# call in this tree costs about 410 ns (Util-scale loop, 3M calls, loop overhead
# subtracted).  The push/pop is a ThreadLocal storage lookup plus two reference
# writes on an object that is already allocated per call (RubyMethodScope), so
# it adds single-digit nanoseconds - call it 1.5-2.5% of every Ruby method call
# in every program, paid whether or not anyone ever asks for a backtrace.  And
# it would buy less than it sounds: a pushed record knows the method's *defining*
# line, not the line currently executing, because the executing line of compiled
# code is an IL offset only a stack walk can recover.  The compiled frames would
# come back as "file:definition-line:in `name'".
#
# That is why it stops here: the free half is accurate, and the half that costs
# every call in every program would be approximate.

$stdout.sync = true

def show(label, thread)
  bt = thread.backtrace
  puts "#{label}: #{bt.inspect}"
end

# 1. A thread parked in sleep, several Ruby frames deep.
q = Queue.new
def deep3(q); q << :ready; sleep 30; end
def deep2(q); deep3(q); end
def deep1(q); deep2(q); end
t = Thread.new { deep1(q) }
q.pop
sleep 0.2
show "nested + sleeping  ", t
locs = t.backtrace_locations
puts "  locations         : #{(locs && locs.map(&:to_s)).inspect}"
t.kill

# 2. A thread blocked trying to take a lock somebody else holds.
m = Mutex.new
m.lock
q2 = Queue.new
t2 = Thread.new { q2 << :ready; m.lock }
q2.pop
sleep 0.2
show "blocked on a mutex ", t2
m.unlock

# 3. The main thread, read from a child.
q3 = Queue.new
main = Thread.current
t3 = Thread.new { q3 << main.backtrace }
puts "main, seen by child: #{q3.pop.inspect}"
t3.join

# 4. A thread that has finished has no stack, in MRI too.
t4 = Thread.new { 1 }
t4.join
show "finished thread    ", t4

# 5. Reading your own is Kernel#caller and always worked.
show "self               ", Thread.current
