using System;
using System.Threading;
using IronRuby.Builtins;
using IronRuby.Runtime;

namespace IronRuby.Tests {
    // A CLR boundary can catch an unwind signal or replace it from finally. The
    // runtime must keep reclaiming until it reaches the fiber worker boundary.
    public static class FiberReclaimInterop {
        private static int _cleanups;
        public static int Cleanups { get { return Volatile.Read(ref _cleanups); } }
        public static void Reset() { _cleanups = 0; }

        public static void Run(Action body, Action after, bool replaceSignal) {
            try {
                try {
                    body();
                } catch (Exception) {
                }
                after();
            } finally {
                Interlocked.Increment(ref _cleanups);
                if (replaceSignal) {
                    throw new InvalidOperationException("CLR cleanup replaced the reclaim signal");
                }
            }
        }
    }

    public partial class Tests {
        [Options(NoRuntime = true)]
        public void Block_ReturnTraceFrameCleanup1() {
            foreach (bool compiled in new[] { false, true }) {
                var engine = Ruby.CreateEngine(setup => {
                    setup.Options["EnableTracing"] = true;
                    setup.Options["NoAdaptiveCompilation"] = compiled;
                });
                var result = engine.Execute<RubyArray>(@"
class ReturnTraceFailure
  define_method(:run) do
    $captured_block = binding
    [1].each { return :value }
  end
end
set_trace_func proc { |event, file, line, id, frame, klass|
  if event == 'return' && id == :run
    set_trace_func nil
    raise 'return trace failed'
  end
}
begin
  ReturnTraceFailure.new.run
rescue => error
  failure = error.message
ensure
  set_trace_func nil
end
[$captured_block, failure]
");
                AssertEquals(result[1].ToString(), "return trace failed");
                var scope = (RubyBlockScope)((Binding)result[0]).FrameScope;
                Assert(scope.InterpretedFrame == null);
                AssertEquals(scope.LambdaReturnState, (byte)2);
                engine.Runtime.Shutdown();
            }
        }

        public void Fiber_ReclamationClrBoundary1() {
            FiberReclaimInterop.Reset();
            Context.ObjectClass.SetConstant("FiberReclaimInterop", Context.GetClass(typeof(FiberReclaimInterop)));
            TestOutput(@"
require 'ruby4'
require 'weakref'
$reclaim_continued = $reclaim_rescued = $reclaim_ensured = 0

def make_clr_reclaim_fiber(replace)
  Fiber.new do
    begin
      FiberReclaimInterop.run(-> { Fiber.yield :ready }, -> { $reclaim_continued += 1 }, replace)
    rescue Exception
      $reclaim_rescued += 1
    ensure
      $reclaim_ensured += 1
    end
  end
end

def discard_clr_reclaim_fiber(replace)
  fiber = make_clr_reclaim_fiber(replace)
  raise 'not ready' unless fiber.resume == :ready
  [WeakRef.new(fiber), fiber.__state__.__core__]
end

cases = [discard_clr_reclaim_fiber(false), discard_clr_reclaim_fiber(true)]
deadline = Process.clock_gettime(Process::CLOCK_MONOTONIC) + 10
loop do
  GC.start
  break if cases.all? { |weak, core| !weak.weakref_alive? && core.status == 'terminated' }
  raise 'CLR boundary retained discarded fiber' if Process.clock_gettime(Process::CLOCK_MONOTONIC) > deadline
  sleep 0.01
end
p [$reclaim_continued, $reclaim_rescued, $reclaim_ensured, FiberReclaimInterop.cleanups]
", "[0, 0, 0, 2]");
        }

        /// <summary>
        /// Keywords given to Fiber#resume / #transfer reach the fiber's block as keywords and come
        /// back from Fiber.yield as a plain Hash; Fiber.schedule hands them on to the scheduler.
        /// The FiberError messages for resuming a fiber that is resumed or was left by #transfer,
        /// and transferring to a yielding one, and the storage rules, are MRI 4.0's.
        /// </summary>
        public void Fiber_Keywords1() {
            TestOutput(@"
require 'ruby4'
Warning[:experimental] = false
p Fiber.new { |**k| k }.resume(x: 1)
p Fiber.new { |*a, **k| [a, k] }.resume(1, x: 1)
p Fiber.new { |*a| [a, Hash.ruby2_keywords_hash?(a[0])] }.resume(x: 1)
p Fiber.new { |a, b: 2| [a, b] }.resume({b: 5})
p Fiber.new { |**k| k }.transfer(y: 2)
f = Fiber.new { |**kw| r = Fiber.yield(**kw, y: 2); [r, Hash.ruby2_keywords_hash?(r)] }
r = f.resume(x: 1)
p [r, Hash.ruby2_keywords_hash?(r)], f.resume(z: 3)
f = Fiber.new { begin; Fiber.yield; rescue => e; [e.class, e.message, e.cause] end }
f.resume
p f.raise(RuntimeError, 'boom', cause: nil)

class Sched
  def block(*); end
  def unblock(*); end
  def kernel_sleep(*); end
  def io_wait(*); end
  def fiber_interrupt(*); end
  def fiber(*args, **kw, &blk)
    p [:fiber, args, kw]
    Fiber.new(blocking: false, **kw, &blk).tap(&:resume)
  end
end
Thread.new do
  Fiber.set_scheduler(Sched.new)
  Fiber.schedule(storage: {a: 1}) { p Fiber[:a] }
end.join

def err; yield; rescue => e; puts ""#{e.class}: #{e.message}""; end
f = Fiber.new { Fiber.yield 1 }
f.resume
err { f.transfer }
f2 = nil
f1 = Fiber.new { f2.resume }
f2 = Fiber.new { f1.resume }
err { f1.resume }
root = Fiber.current
g = Fiber.new { root.transfer; :done }
g.transfer
err { g.resume }
err { Fiber[1] }
err { Fiber.new(storage: {'a' => 1}) { } }
err { Fiber.new(storage: {a: 1}.freeze) { } }
p Fiber.new(storage: nil) { Fiber.current.storage }.resume
o = Object.new; def o.to_str; 'q'; end
Fiber[o] = 3
p Fiber[:q]
", @"
{x: 1}
[[1], {x: 1}]
[[{x: 1}], false]
[{b: 5}, 2]
{y: 2}
[{x: 1, y: 2}, false]
[{z: 3}, false]
[RuntimeError, ""boom"", nil]
[:fiber, [], {storage: {a: 1}}]
1
FiberError: attempt to transfer to a yielding fiber
FiberError: attempt to resume a resumed fiber (double resume)
FiberError: attempt to resume a transferring fiber
TypeError: 1 is not a symbol nor a string
TypeError: wrong argument type String (expected Symbol)
FrozenError: storage must not be frozen
nil
3
");
        }

        /// <summary>
        /// ObjectSpace::WeakMap#each and friends without a block are MRI's LocalJumpError (an empty map
        /// yields nothing and answers itself); WeakKeyMap has no include?/member?.
        /// IO#write_nonblock converts a non-String with #to_s and still never blocks.
        /// </summary>
        public void WeakMap_NoBlock1() {
            TestOutput(@"
require 'ruby4'
wm = ObjectSpace::WeakMap.new
p wm.each.equal?(wm)
# the value is held weakly too: without a reference of its own a GC empties the map
k, v = 'a', 'b'
wm[k] = v
[:each, :each_pair, :each_key, :each_value].each { |m| p((wm.send(m) rescue $!)) }
p ObjectSpace::WeakKeyMap.instance_methods(false).sort

r, w = IO.pipe
o = Object.new
def o.to_s; 'hello'; end
p w.write_nonblock(o), w.write_nonblock(:sym), r.read_nonblock(100)
big = 'x' * 1_000_000
begin
  loop { w.write_nonblock(big) }
rescue IO::WaitWritable => e
  p e.class
end
p w.write_nonblock(12345, exception: false)
p((w.write_nonblock(:a) rescue $!.class))
", @"
true
#<LocalJumpError: no block given>
#<LocalJumpError: no block given>
#<LocalJumpError: no block given>
#<LocalJumpError: no block given>
[:[], :[]=, :clear, :delete, :getkey, :inspect, :key?]
5
3
""hellosym""
IO::EAGAINWaitWritable
:wait_writable
IO::EAGAINWaitWritable
");
        }
    }
}
