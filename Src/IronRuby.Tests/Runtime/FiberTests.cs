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
        /// Thread.handle_interrupt holds a Thread#raise back in every runtime of the process. The
        /// exception's class used to be looked up in the first runtime created, whatever runtime the
        /// mask was for; in any other one no class matched, and every mask let everything through.
        /// </summary>
        [Options(NoRuntime = true)]
        public void Thread_HandleInterruptOtherRuntime1() {
            var first = Ruby.CreateEngine();
            var second = Ruby.CreateEngine();
            try {
                first.Execute("Thread.handle_interrupt(RuntimeError => :never) { }");
                object reached = second.Execute(@"
ready = reached = false
t = Thread.new do
  begin
    Thread.handle_interrupt(RuntimeError => :never) do
      ready = true
      sleep 0.3
      reached = true
    end
  rescue RuntimeError
  end
end
Thread.pass until ready
t.raise 'held back'
t.join
reached
");
                AssertEquals(reached, true);
            } finally {
                first.Runtime.Shutdown();
                second.Runtime.Shutdown();
            }
        }

        /// <summary>
        /// Thread#raise reaches the fiber the thread is running, wherever its fibers are in a switch.
        /// Every fiber is a CLR thread of its own; the interrupt used to be routed to the fiber that
        /// had last woken up, so in the window around a resume (or the end of a fiber) it went to the
        /// resumer instead - which raised it from Fiber#resume while the fiber ran on, or took it while
        /// the finished fiber's result sat unread in its mailbox, so that the next resume returned at
        /// once. Either way two fibers of one thread ran side by side, and the next one's
        /// Mutex#synchronize failed with "deadlock; recursive locking" (ruby/spec core/mutex/lock_spec,
        /// "does not raise deadlock if a fiber's attempt to lock was interrupted").
        /// </summary>
        public void Fiber_ThreadRaiseReachesRunningFiber1() {
            TestOutput(@"
require 'ruby4'

# the plain case: the fiber is running, so the fiber gets it
q = Queue.new
t = Thread.new do
  Fiber.new do
    begin
      q << :ready
      loop { Thread.pass }
    rescue RuntimeError => e
      e.message
    end
  end.resume
end
q.pop
t.raise 'to the fiber'
p t.value

# the race: the interrupts land all over the fibers' switches. Fiber#resume must not come
# back - by returning or by raising - while the fiber it resumed is still running.
errors = []
80.times do
  lock = Mutex.new
  t2 = nil
  t1 = Thread.new { loop { sleep 0.002; t2.raise if t2 } }
  t2 = Thread.new do
    Thread.current.report_on_exception = false
    10.times do
      state = {}
      fiber = Fiber.new do
        state[:started] = true
        begin
          loop { lock.synchronize {} }
        rescue RuntimeError
        ensure
          state[:done] = true
        end
      end
      begin
        fiber.resume
      ensure
        errors << 'resume came back while its fiber ran on' if state[:started] && !state[:done]
      end
      Fiber.new do
        begin
          lock.synchronize {}
        rescue ThreadError => e
          errors << e.message
        end
      end.resume
    rescue RuntimeError
      retry
    end
  end
  begin
    errors << 'hung' unless t2.join(20)
  rescue RuntimeError
    # one that reaches t2 itself between two fibers, outside the rescue - as it can in MRI
  rescue Exception => e
    errors << ""#{e.class}: #{e.message}""
  ensure
    t1.kill
    t1.join
    t2.kill
  end
end
p errors.uniq
", @"
""to the fiber""
[]
");
        }
    }
}
