using System;
using System.Reflection;
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

    // Holds a Ruby thread in CLR code until it is released. There is no safe point in here, and the
    // thread is never in a wait an interrupt could break off, so whatever Thread#raise / #kill park for
    // it meanwhile is all still parked when it comes back to Ruby code.
    public sealed class SafePointHold {
        private volatile bool _held, _released;
        public bool Held { get { return _held; } }
        public void Release() { _released = true; }

        public void Hold() {
            _held = true;
            while (!_released) {
                // not Thread.Sleep or SpinWait, which may sleep: an interrupt would break that off
                Thread.Yield();
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

        /// <summary>
        /// A Thread#raise that comes in between two calls of a `do ... rescue ... end` block meets the
        /// block's rescue. Integer#times loops in C#, so the safe point that reaches such a loop is the
        /// block's entry - which used to come before the rescued statements, and the exception left
        /// the loop. MRI checks only at the block's `leave` and the loop's back edge, a few instructions;
        /// here it was every raise that found the body without a safe point of its own, as this one has
        /// none. The pattern is ruby/spec core/mutex/lock_spec's "does not raise deadlock if a fiber's
        /// attempt to lock was interrupted".
        /// </summary>
        public void Thread_RaiseBetweenBlockCallsMeetsRescue1() {
            TestOutput(@"
rescued = 0
ready = stop = false
t = Thread.new do
  Thread.current.report_on_exception = false
  1_000_000_000.times do
    ready = true
    break if stop
  rescue RuntimeError
    rescued += 1
  end
  :finished
end
Thread.pass until ready
5.times do |i|
  t.raise 'between two calls of the block'
  # a lost raise fails the test (rescued stays short) instead of waiting out the loop
  deadline = Time.now + 10
  Thread.pass until rescued > i || !t.alive? || Time.now > deadline
end
stop = true
p [(t.value rescue $!.message), rescued]
", @"
[:finished, 5]
");
        }

        /// <summary>
        /// A Thread#kill that comes in while a Thread#raise is still parked for the thread is not lost.
        /// The kill used to be dropped then, on the assumption that the raise ends the thread; when a
        /// Thread.handle_interrupt mask held the raise back, or the thread rescued it, the thread ran
        /// on. Here the mask holds the raise back, the kill is not masked and ends the thread inside
        /// the mask: in A after the thread has gone on to other work in the mask, in B straight away.
        /// The other order is C: a Thread#raise that comes in behind a parked Thread#kill must not
        /// replace it either (MRI takes the kill first and drops what is queued behind it); it did,
        /// and the masked raise then left the thread running for ever. C repeats, as a thread may take
        /// the kill before the raise arrives.
        /// </summary>
        public void Thread_KillBehindRaise1() {
            TestOutput(@"
# A: raise, then kill, while the thread is in the mask; the kill ends it there, before the rescue
ready = go = false
t = Thread.new do
  Thread.current.report_on_exception = false
  begin
    Thread.handle_interrupt(RuntimeError => :never) do
      ready = true
      Thread.pass until go
    end
  rescue RuntimeError
  end
  loop { sleep 0.01 }
end
Thread.pass until ready
t.raise 'first'
t.kill
go = true
a = t.join(5) ? :killed : :still_running
t.kill.join(1) rescue nil

# B: a kill behind a raise that a handle_interrupt mask holds back ends the thread inside the mask
ready = false
ensured = false
t = Thread.new do
  Thread.current.report_on_exception = false
  begin
    Thread.handle_interrupt(RuntimeError => :never) do
      ready = true
      loop { sleep 0.01 }
    end
  ensure
    ensured = true
  end
end
Thread.pass until ready
t.raise 'held back'
t.kill
b = t.join(5) ? :killed : :still_running

# C: kill, then raise; the raise must not replace the kill
c = 20.times.map do
  ready = false
  t = Thread.new do
    Thread.current.report_on_exception = false
    Thread.handle_interrupt(RuntimeError => :never) do
      ready = true
      loop { sleep 0.01 }
    end
  end
  Thread.pass until ready
  t.kill
  t.raise 'behind the kill'
  t.join(5) ? :killed : (t.kill; :still_running)
end.uniq
p [a, b, ensured, c]
", @"
[:killed, :killed, true, [:killed]]
");
        }

        /// <summary>
        /// Thread#raise and then Thread#kill reach a thread that is held in CLR code, so both are parked
        /// when it gets back to Ruby code. The raise is not masked: it is delivered and the thread
        /// rescues it. The kill waiting behind it then has to be parked in its place and end the thread
        /// at its next safe point (as MRI does), not be dropped with the raise it waited behind.
        /// </summary>
        public void Thread_KillBehindRescuedRaise1() {
            Context.DefineGlobalVariable("hold", new SafePointHold());
            TestOutput(@"
t = Thread.new do
  Thread.current.report_on_exception = false
  begin
    $hold.hold
    loop { Thread.pass }
  rescue RuntimeError => $rescued
  end
  loop { sleep 0.01 }
end
Thread.pass until $hold.held
t.raise 'first'
t.kill
$hold.release
r = t.join(5) ? :killed : (t.kill; :still_running)
p [r, $rescued&.message, t.status]
", @"
[:killed, ""first"", false]
");
        }

        /// <summary>
        /// A Thread#raise that is withdrawn (RetractAsyncException: a fiber thread whose fiber is done
        /// drops a raise that has nowhere to go) leaves a Thread#kill that waited behind it parked in
        /// its place; the kill is not withdrawn with it. The thread is never started: this is about what
        /// is parked for it.
        /// </summary>
        public void Thread_KillBehindRetractedRaise1() {
            var thread = new Thread(() => { });
            var raise = new InvalidOperationException("withdrawn");
            try {
                RubyUtils.ParkAsyncException(thread, raise);
                RubyUtils.ExitThread(thread);
                Assert(RubyUtils.PeekPendingAsyncException(thread) == raise);

                Assert(RubyUtils.RetractAsyncException(thread, raise));
                Assert(RubyUtils.IsRubyThreadExit(RubyUtils.PeekPendingAsyncException(thread)));

                // what is parked now is the kill, which the raise's retraction does not take
                Assert(!RubyUtils.RetractAsyncException(thread, raise));
                Assert(RubyUtils.IsRubyThreadExit(RubyUtils.PeekPendingAsyncException(thread)));
            } finally {
                RubyUtils.DropPendingAsyncExceptions(thread);
            }
            Assert(!RubyUtils.HasPendingAsyncException(thread));
        }

        /// <summary>
        /// A fiber waiting for control (TranslateFiberWaitInterrupt) does not raise a parked Thread#raise:
        /// it leaves it parked for when control arrives. A Thread#kill that came in behind the raise
        /// ends the thread there all the same, and the raise goes with it.
        /// </summary>
        public void Thread_KillBehindRaiseInFiberWait1() {
            int step = 0;
            Exception alone = null, behind = null;
            bool parkedAfterAlone = false, parkedAfterKill = true;
            var worker = new Thread(() => {
                while (Volatile.Read(ref step) != 1) Thread.Yield();
                try {
                    RubyUtils.TranslateFiberWaitInterrupt();
                } catch (Exception e) {
                    alone = e;
                }
                parkedAfterAlone = RubyUtils.HasPendingAsyncException(Thread.CurrentThread);
                Volatile.Write(ref step, 2);

                while (Volatile.Read(ref step) != 3) Thread.Yield();
                try {
                    RubyUtils.TranslateFiberWaitInterrupt();
                } catch (Exception e) {
                    behind = e;
                }
                parkedAfterKill = RubyUtils.HasPendingAsyncException(Thread.CurrentThread);
            });
            worker.IsBackground = true;
            worker.Start();
            try {
                RubyUtils.ParkAsyncException(worker, new InvalidOperationException("raise"));
                Volatile.Write(ref step, 1);
                while (Volatile.Read(ref step) != 2) Thread.Yield();

                RubyUtils.ExitThread(worker);
                Volatile.Write(ref step, 3);
                Assert(worker.Join(5000));
            } finally {
                RubyUtils.DropPendingAsyncExceptions(worker);
            }

            Assert(alone == null);
            Assert(parkedAfterAlone);
            Assert(behind != null && RubyUtils.IsRubyThreadExit(behind));
            Assert(!parkedAfterKill);
        }

        /// <summary>
        /// Thread_KillBehindRaiseInFiberWait1 through the wait itself: a thread blocked in the mailbox
        /// take of a fiber waiting for control, with a Thread#raise parked for it, is woken by a
        /// Thread#kill behind the raise and leaves the take with the kill. Were the kill not promoted
        /// there, the take would go on waiting for a message that never comes.
        /// </summary>
        public void Thread_KillBehindRaiseInFiberMailbox1() {
            var mailbox = new ThreadOps.FiberMailbox();
            // internal to the libraries: set for a fiber's own mailbox (FiberCore)
            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            typeof(ThreadOps.FiberMailbox).GetField("HandsOverControl", flags).SetValue(mailbox, true);
            var blocked = typeof(ThreadOps.FiberMailbox).GetField("_blocked", flags);

            Exception thrown = null;
            var worker = new Thread(() => {
                try {
                    mailbox.Take();
                } catch (Exception e) {
                    thrown = e;
                }
            });
            worker.IsBackground = true;
            worker.Start();
            try {
                // in the Monitor wait, where Thread#kill's interrupt reaches it, not in the spin before it
                while (!(bool)blocked.GetValue(mailbox) || (worker.ThreadState & ThreadState.WaitSleepJoin) == 0) {
                    Thread.Yield();
                }
                RubyUtils.ParkAsyncException(worker, new InvalidOperationException("raise"));
                RubyUtils.ExitThread(worker);

                bool ended = worker.Join(5000);
                if (!ended) {
                    mailbox.Post(null);
                    worker.Join(5000);
                }
                Assert(ended);
            } finally {
                RubyUtils.DropPendingAsyncExceptions(worker);
            }
            Assert(thrown != null && RubyUtils.IsRubyThreadExit(thrown));
        }

        /// <summary>
        /// A Thread#kill that comes while a fiber's resumer is still on its way into the mailbox wait
        /// - spinning before it blocks - is not lost. Its interrupt went only to a thread already in
        /// WaitSleepJoin, so the resumer blocked with the kill parked and the thread never ended:
        /// ruby/spec's kill_spec "kills the entire thread when a fiber is active" hung now and then,
        /// since Thread#status says "sleep" as soon as the fiber sleeps. Here the kill is parked
        /// while the worker runs, before it takes. A raise parked like that, which a fiber waiting
        /// for control keeps (FiberCore.Switch raises it), must not keep the take from blocking.
        /// </summary>
        public void Thread_KillBeforeFiberMailboxWait1() {
            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var handsOverControl = typeof(ThreadOps.FiberMailbox).GetField("HandsOverControl", flags);
            var blocked = typeof(ThreadOps.FiberMailbox).GetField("_blocked", flags);

            foreach (bool kill in new[] { true, false }) {
                var mailbox = new ThreadOps.FiberMailbox();
                handsOverControl.SetValue(mailbox, true);
                var raise = new InvalidOperationException("raise");

                Exception thrown = null;
                object message = null;
                bool ready = false;
                bool go = false;
                var worker = new Thread(() => {
                    try {
                        Volatile.Write(ref ready, true);
                        // running, not in a wait: the nudge finds nothing to interrupt
                        while (!Volatile.Read(ref go)) {
                        }
                        message = mailbox.Take();
                    } catch (Exception e) {
                        thrown = e;
                    }
                });
                worker.IsBackground = true;
                worker.Start();
                try {
                    while (!Volatile.Read(ref ready)) {
                        Thread.Yield();
                    }
                    if (kill) {
                        RubyUtils.ExitThread(worker);
                    } else {
                        RubyUtils.RaiseAsyncException(worker, raise);
                    }
                    Volatile.Write(ref go, true);

                    if (!kill) {
                        // the raise stays parked and the take blocks, waiting for its message (a take that
                        // throws ends the worker instead, and the asserts below report it)
                        while (worker.IsAlive && (!(bool)blocked.GetValue(mailbox) || (worker.ThreadState & ThreadState.WaitSleepJoin) == 0)) {
                            Thread.Yield();
                        }
                        mailbox.Post("control");
                    }

                    bool ended = worker.Join(5000);
                    if (!ended) {
                        mailbox.Post(null);
                        worker.Join(5000);
                    }
                    Assert(ended);
                    if (kill) {
                        Assert(thrown != null && RubyUtils.IsRubyThreadExit(thrown));
                    } else {
                        Assert(thrown == null && (string)message == "control");
                        Assert(ReferenceEquals(RubyUtils.PeekPendingAsyncException(worker), raise));
                    }
                } finally {
                    RubyUtils.DropPendingAsyncExceptions(worker);
                }
            }
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
