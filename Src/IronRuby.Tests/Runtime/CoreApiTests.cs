/* ****************************************************************************
 *
 * Copyright (c) IronRuby contributors.
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A
 * copy of the license can be found in the License.html file at the root of this distribution. If
 * you cannot locate the  Apache License, Version 2.0, please send an email to
 * ironruby@microsoft.com. By using this source code in any fashion, you are agreeing to be bound
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 * ***************************************************************************/

using System;
using Microsoft.Scripting.Hosting;

namespace IronRuby.Tests {
    /// <summary>
    /// Core API that CRuby 4.0 has: IO#timeout, Thread#set_trace_func/#add_trace_func, GC.stat_heap
    /// and friends, Enumerator::Producer, Process::Waiter, Set::CoreSet, Ruby::Box as a disabled one
    /// looks, the private copies of module functions, and lazily loaded did_you_mean. The expected
    /// output is CRuby 4.0's.
    /// </summary>
    public partial class Tests {
        /// <summary>
        /// IO#timeout on the in-process pipe the runtime has without the prelude: a read or a write that would
        /// wait longer raises IO::TimeoutError (defined here, as the prelude does).
        /// </summary>
        public void Io_Timeout1() {
            TestOutput(@"
class IO; class TimeoutError < IOError; end; end
r, w = IO.pipe
p [r.timeout, (r.timeout = 0.05), r.timeout]
[-> { r.read(1) }, -> { r.gets }, -> { r.getc }].each { |l| p((l.call rescue [$!.class, $!.message])) }
w.timeout = 0.05
p((loop { w.write(""x"" * 4096) } rescue $!.class))
p r.read(3)
r.timeout = nil
p r.timeout
", @"
[nil, 0.05, 0.05]
[IO::TimeoutError, ""Blocking operation timed out!""]
[IO::TimeoutError, ""Blocking operation timed out!""]
[IO::TimeoutError, ""Blocking operation timed out!""]
IO::TimeoutError
""xxx""
nil
");
        }

        /// <summary>
        /// IO#timeout on IO.pipe (pipe(2) with the prelude): every blocking read and write, but not the
        /// non-blocking ones; an interval may be a Rational, as IO.select's may.
        /// </summary>
        public void Io_Timeout2() {
            TestOutput(@"
require 'ruby4'
r, w = IO.pipe
p [r.timeout, (r.timeout = 0.05), r.timeout]
[-> { r.read(1) }, -> { r.gets }, -> { r.getc }, -> { r.each_line.first }].each { |l| p((l.call rescue [$!.class, $!.message])) }
p((r.read_nonblock(1) rescue $!.class))
p r.dup.timeout
w.timeout = 0.05
p((loop { w.write(""x"" * 4096) } rescue $!.class))
p((loop { w.syswrite(""x"") } rescue $!.class))
r2, w2 = IO.pipe
r2.timeout = 2
th = Thread.new { sleep 0.05; w2.write(""z"") }
p r2.read(1)
th.join
w2.close
p r2.read
begin; r2.timeout = -1; rescue => e; p e; end
begin; r2.timeout = ""1""; rescue => e; p e; end
r2.timeout = 1r / 50
p r2.timeout
r2.timeout = nil
p r2.timeout
p IO.select([r2], nil, nil, 1r / 100).nil?
require ""tempfile""
Tempfile.create(""tmo"") { |f| f.write(""abcdef""); f.rewind; f.timeout = 0.01; p f.read(4) }
p IO::TimeoutError.ancestors.take(3)
", @"
[nil, 0.05, 0.05]
[IO::TimeoutError, ""Blocking operation timed out!""]
[IO::TimeoutError, ""Blocking operation timed out!""]
[IO::TimeoutError, ""Blocking operation timed out!""]
[IO::TimeoutError, ""Blocking operation timed out!""]
IO::EAGAINWaitReadable
0.05
IO::TimeoutError
IO::TimeoutError
""z""
""""
#<ArgumentError: time interval must not be negative>
#<TypeError: can't convert String into time interval>
(1/50)
nil
false
""abcd""
[IO::TimeoutError, IOError, StandardError]
");
        }

        /// <summary>
        /// IO#timeout is each IO's own even when two IOs share one stream, as a Ractor's $stdin
        /// shares the main Ractor's (RubyIO.CreateSharedView): a timeout on one does not bound
        /// the reads of the other.
        /// </summary>
        public void Io_Timeout4() {
            var r = Engine.Execute<IronRuby.Builtins.RubyIO>("$tmo4_r, $tmo4_w = IO.pipe; $tmo4_r");
            Context.DefineGlobalVariable("tmo4_v", r.CreateSharedView());
            TestOutput(@"
require 'ruby4'
r, w, v = $tmo4_r, $tmo4_w, $tmo4_v
r.timeout = 0.05
p v.timeout
th = Thread.new { sleep 0.2; w << ""a"" }
p v.read(1)
th.join
p((r.read(1) rescue $!.class))
r.timeout = nil
v.timeout = 0.05
th = Thread.new { sleep 0.2; w << ""b"" }
p r.read(1)
th.join
p((v.read(1) rescue $!.class))
", @"
nil
""a""
IO::TimeoutError
""b""
IO::TimeoutError
");
        }

        /// <summary>
        /// IO#timeout when the IO's own buffer already holds part of what a read wants: the wait
        /// for the rest looks at the pipe(2) underneath, not at the buffer, so it times out rather
        /// than blocking in the read for ever. IO#read(n) waits for all n bytes, as in MRI.
        /// </summary>
        public void Io_Timeout3() {
            TestOutput(@"
require 'ruby4'
r, w = IO.pipe
r.timeout = 0.05
w << ""abc\npartial""
p r.gets
p((r.gets rescue $!.class))
r, w = IO.pipe
r.timeout = 0.05
w << ""ab""
p r.getc
p((r.read(5) rescue $!.class))
r, w = IO.pipe
r.timeout = 0.05
w << ""x\ny""
a = []
p((r.each_line { |l| a << l } rescue [$!.class, a]))
r, w = IO.pipe
r.timeout = 2
w << ""ab""
th = Thread.new { sleep 0.05; w << ""cde\n"" }
p r.read(5)
p r.gets
th.join
w.close
p r.read(5)
", @"
""abc\n""
IO::TimeoutError
""a""
IO::TimeoutError
[IO::TimeoutError, [""x\n""]]
""abcde""
""\n""
nil
");
        }

        /// <summary>
        /// Kernel#set_trace_func works without -trace (it is a TracePoint underneath, as in MRI), and
        /// Thread#set_trace_func / #add_trace_func hook one thread, the latest added hook first.
        /// </summary>
        public void Thread_SetTraceFunc1() {
            TestOutput(@"
require 'ruby4'
BASE = __LINE__ - 1
class TraceProbe
  def bar(x)
    x + 1
  end
end
events = []
pr = proc { |e, f, l, id, b, k| events << [e, l - BASE, id, b.class, k] if %w[call line return class end].include?(e) }
p set_trace_func(pr).equal?(pr)
TraceProbe.new.bar(1)
class TraceProbe; end
set_trace_func(nil)
events.each { |e| p e }
t = Thread.current
seen = []
t.set_trace_func(proc { |e, *| seen << Thread.current if e == ""call"" })
Thread.new { TraceProbe.new.bar(2) }.join
TraceProbe.new.bar(3)
t.set_trace_func(nil)
p [seen.size, seen.uniq == [t]]
order = []
t.add_trace_func(proc { |e, *| order << :a if e == ""call"" })
added = t.add_trace_func(proc { |e, *| order << :b if e == ""call"" })
TraceProbe.new.bar(4)
t.set_trace_func(nil)
TraceProbe.new.bar(5)
p [order, added.class]
[-> { set_trace_func(1) }, -> { t.set_trace_func(:x) }, -> { t.add_trace_func(nil) }].each { |l| p((l.call rescue $!)) }
p [set_trace_func(nil), t.set_trace_func(nil)]
locals = nil
set_trace_func(proc { |e, f, l, id, b, k| (locals = [b.local_variables, b.receiver.class]; set_trace_func(nil)) if e == ""call"" })
TraceProbe.new.bar(6)
p locals
", @"
true
[""line"", 10, nil, Binding, nil]
[""call"", 3, :bar, Binding, TraceProbe]
[""line"", 4, :bar, Binding, TraceProbe]
[""return"", 5, :bar, Binding, TraceProbe]
[""line"", 11, nil, Binding, nil]
[""class"", 11, nil, Binding, nil]
[""end"", 11, nil, Binding, nil]
[""line"", 12, nil, Binding, nil]
[1, true]
[[:b, :a], Proc]
#<TypeError: trace_func needs to be Proc>
#<TypeError: trace_func needs to be Proc>
#<TypeError: trace_func needs to be Proc>
[nil, nil]
[[:x], TraceProbe]
");
        }

        /// <summary>
        /// Constants and methods of CRuby 4.0's core that were missing: Enumerator::Producer, Process::Waiter,
        /// Set::CoreSet, String.new/Array.new on the classes, private copies of module functions, Kernel#pp
        /// private, Ruby::Box (disabled), ObjectSpace.count_objects, GC.stat_heap and the compaction API.
        /// The last line is an instance of a class backed by a generated CLR type whose #== calls #equal?,
        /// which used to recurse until the stack overflowed (Set#== with a Set subclass did).
        /// </summary>
        public void Core_Ruby4Surface1() {
            TestOutput(@"
require 'ruby4'
p Enumerator::Producer.ancestors.take(2), Enumerator::Producer.instance_methods(false)
e = Enumerator.produce([1, 1]) { |a, b| [b, a + b] }
p e.inspect.sub(/0x\h+/, ""XX""), e.size, e.take(5).map(&:first)
p Enumerator.produce(1) { raise StopIteration if _1 > 2; _1 + 1 }.to_a
p((Enumerator::Producer.new.each {} rescue $!))
w = Process.detach(2**30 - 3)
p w.class, Process::Waiter.ancestors.take(2), Process::Waiter.instance_methods(false), w.pid, w.value
p((Process::Waiter.new rescue $!.class))
class CoreProbe < Set::CoreSet; end
p Set::CoreSet.ancestors.take(3), CoreProbe[1, 2], Set[1] == CoreProbe[1]
p String.singleton_methods(false).sort, Array.singleton_methods(false).sort
p Process.private_instance_methods(false).include?(:pid), Process.private_instance_methods(false).include?(:exit)
p Signal.private_instance_methods(false).sort, ObjectSpace.private_instance_methods(false).sort, Marshal.private_instance_methods(false)
o = Object.new.extend(Process, Marshal)
p o.send(:pid) == Process.pid, Marshal.load(o.send(:dump, [1]))
p((Kernel.pp(1) rescue $!.class))
p Ruby::Box.ancestors.take(2), Ruby::Box.enabled?, Ruby::Box.current, Ruby::Box.constants.sort, method(:puts).box
p((Ruby::Box.new rescue $!))
p ObjectSpace.count_objects.keys.take(5), ObjectSpace.count_objects.values.all?(Integer)
h = {}
p ObjectSpace.count_objects(h).equal?(h), ((ObjectSpace.count_objects(1)) rescue $!)
p GC.stat_heap.keys, GC.stat_heap(0).keys, GC.stat_heap(0, :slot_size), GC.stat_heap(4, :slot_size)
[-> { GC.stat_heap(5) }, -> { GC.stat_heap(:x) }, -> { GC.stat_heap(0, :nope) }, -> { GC.stat_heap(0, 1) }, -> { GC.stat_heap(nil, :slot_size) }].each { |l| p((l.call rescue $!)) }
p GC.latest_compact_info, GC.verify_internal_consistency, GC.compact.keys, GC.verify_compaction_references(expand_heap: true).class
p [defined?(UnicodeNormalize), GC.respond_to?(:garbage_collect)]
class EqProbe
  include Enumerable
  def each; end
  def ==(other) = equal?(other)
end
p EqProbe.new == EqProbe.new
", @"
[Enumerator::Producer, Object]
[:each]
""#<Enumerator: #<Enumerator::Producer:XX>:each>""
Infinity
[1, 1, 2, 3, 5]
[1, 2, 3]
#<ArgumentError: uninitialized producer>
Process::Waiter
[Process::Waiter, Thread]
[:pid]
1073741821
nil
NoMethodError
[Set::CoreSet, Set, Enumerable]
CoreProbe[1, 2]
true
[:new, :try_convert]
[:[], :new, :try_convert]
true
false
[:list, :signame, :trap]
[:_id2ref, :count_objects, :define_finalizer, :each_object, :garbage_collect, :undefine_finalizer]
[:dump]
true
[1]
NoMethodError
[Ruby::Box, Module]
false
nil
[:Entry, :Loader]
nil
#<RuntimeError: Ruby Box is disabled. Set RUBY_BOX=1 environment variable to use Ruby::Box.>
[:TOTAL, :FREE, :T_OBJECT, :T_CLASS, :T_MODULE]
true
true
#<TypeError: non-hash given>
[0, 1, 2, 3, 4]
[:slot_size, :heap_live_slots, :heap_free_slots, :heap_final_slots, :heap_eden_pages, :heap_eden_slots, :total_allocated_pages, :force_major_gc_count, :force_incremental_marking_finish_count, :total_allocated_objects, :total_freed_objects]
40
640
#<ArgumentError: size pool index out of range>
#<TypeError: heap_name must be nil or an Integer>
#<ArgumentError: unknown key: nope>
#<TypeError: non-hash or symbol given>
#<TypeError: non-hash given>
{considered: {}, moved: {}, moved_up: {}, moved_down: {}}
nil
[:considered, :moved, :moved_up, :moved_down]
Hash
[""constant"", false]
false
");
        }

        /// <summary>
        /// did_you_mean is loaded on first use: the first #detailed_message of a NameError loads it,
        /// and an uncaught error is reported through #detailed_message, so the suggestion is there too.
        /// </summary>
        public void DidYouMean_Lazy1() {
            TestOutput(@"
require 'ruby4'
require 'ironruby/did_you_mean'
p $LOADED_FEATURES.any? { |f| f.end_with?('/did_you_mean.rb') && !f.include?('ironruby/') }
begin; [].lenght; rescue NameError => e; p e.detailed_message(highlight: false); end
p DidYouMean::Correctable.class
begin; Strin; rescue NameError => e; p e.detailed_message(highlight: false); end
p ArgumentError.new('x').detailed_message
", @"
false
""undefined method 'lenght' for an instance of Array (NoMethodError)\nDid you mean?  length""
Module
""uninitialized constant Strin (NameError)\nDid you mean?  String\n               STDIN""
""x (ArgumentError)""
");

            Engine.Execute("require 'ruby4'; require 'ironruby/did_you_mean'");
            Engine.Execute("class DetailedProbe < StandardError; def detailed_message(**) = 'custom report'; end");
            var operations = Engine.GetService<ExceptionOperations>();
            try {
                Engine.Execute("[].lenght");
                Assert(false);
            } catch (Exception e) {
                string report = operations.FormatException(e).Replace("\r\n", "\n");
                Assert(report.Contains("undefined method 'lenght' for an instance of Array (NoMethodError)\nDid you mean?  length"));
            }
            try {
                Engine.Execute("raise DetailedProbe");
                Assert(false);
            } catch (Exception e) {
                Assert(operations.FormatException(e).Contains(": custom report"));
            }
        }

        /// <summary>
        /// The first messages, asked for on several threads at once, all have the suggestion: a thread
        /// that comes while another one is loading did_you_mean waits for it. The first thread used to
        /// mark them loaded before they were, and the others went on without.
        /// </summary>
        public void DidYouMean_LazyThreads1() {
            TestOutput(@"
require 'ruby4'
require 'ironruby/did_you_mean'
q = Queue.new
4.times.map {
  Thread.new { begin; [].lenght; rescue NameError => e; q << e.detailed_message(highlight: false).include?('Did you mean?'); end }
}.each(&:join)
a = []; a << q.pop until q.empty?
p a
", @"
[true, true, true, true]
");
        }

        /// <summary>
        /// The program's own require of did_you_mean, before it is loaded on first use: its `module
        /// DidYouMean` fires the autoload that stands for it, which then has did_you_mean already loading.
        /// That warned (under -w, as these tests run) of a circular require and of an autoload that did not
        /// define its constant; MRI says nothing.
        /// </summary>
        public void DidYouMean_LazyRequire1() {
            TestOutput(@"
require 'ruby4'
require 'ironruby/error_highlight'
require 'ironruby/did_you_mean'
$warned = []
$stderr = Object.new.tap { |o| def o.write(*a) = $warned.concat(a).sum(&:size); }
require 'did_you_mean'
$stderr = STDERR
p $warned
p NameError.ancestors.take_while { |m| m != NameError }.map(&:name)
begin; [].lenght; rescue NameError => e; p e.detailed_message(highlight: false).include?('Did you mean?  length'); end
", @"
[]
[""DidYouMean::Correctable"", ""ErrorHighlight::CoreExt"", ""IronRuby::ErrorExtensions::Loader""]
true
");
        }
    }
}
