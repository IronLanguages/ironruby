/* ****************************************************************************
 *
 * Copyright (c) Microsoft Corporation.
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A
 * copy of the license can be found in the License.html file at the root of this distribution. If
 * you cannot locate the  Apache License, Version 2.0, please send an email to
 * ironruby@microsoft.com. By using this source code in any fashion, you are agreeing to be bound
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 *
 * ***************************************************************************/

namespace IronRuby.Tests {
    // Ruby 4.0's Ractor (Src/Libraries/Builtins/RactorOps.cs). The expected output is CRuby 4.0's.
    public partial class Tests {
        // Each test turns the experimental warning off for its runtime and back on at the end.
        private const string RactorPrologue = @"
Warning[:experimental] = false
def t; yield; rescue Exception => e; puts ""#{e.class}: #{e.message}""; end
";

        public void Ractor_Basics1() {
            TestOutput(RactorPrologue + @"
p Ractor.new { 1 + 2 }.value
p Ractor.new(1, [2]) { |a, b| [a, b] }.value
r = Ractor.new(name: 'worker') { self }
p r.value.equal?(r), r.name, r.name.frozen?
sleep 0.01 until r.inspect.include?('terminated')
puts r.inspect.sub(/ \S+:\d+ /, ' LOC ').sub(/#\d+/, '#N')
p Ractor.current, Ractor.current == Ractor.main, Ractor.main?
p Ractor.new { [Ractor.main?, Ractor.current.class, Thread.new { Ractor.current }.value == Ractor.current] }.value
p Ractor.new { Thread.current.group.object_id }.value != Thread.current.group.object_id
t { Ractor.new }
t { Ractor.new(name: 1) { } }
t { Ractor.new(foo: 1) { } }
t { Ractor.allocate }
p Ractor.new({ name: 'positional' }) { |h| h }.value
r = Ractor.new { 7 }
p r.join.equal?(r), r.value, r.value
sleep 0.05 until Ractor.count == 1
rs = Array.new(3) { Ractor.new { Ractor.receive } }
p Ractor.count
rs.each { |x| x << 1 }.each(&:join)
p Ractor.count
Warning[:experimental] = true
", @"
3
[1, [2]]
true
""worker""
true
#<Ractor:#N worker LOC terminated>
#<Ractor:#1 running>
true
true
[false, Ractor, true]
true
ArgumentError: must be called with a block
TypeError: no implicit conversion of Integer into String
ArgumentError: unknown keyword: :foo
TypeError: allocator undefined for Ractor
{name: ""positional""}
true
7
7
4
1
");
        }

        /// <summary>
        /// The block of Ractor.new may not use the locals of the scopes around it; that it does is
        /// known when it is compiled (BlockDispatcher.OuterVariableNames). Run in eval too, where
        /// the outer locals are the dynamic ones of the eval's scope.
        /// </summary>
        public void Ractor_Isolation1() {
            TestOutputWithEval(RactorPrologue + @"
a = 1
b = 2
t { Ractor.new { a } }
t { Ractor.new { a = 2 } }
t { Ractor.new { [a, b] } }
t { Ractor.new { [1].map { [1].map { b } } } }
t { Ractor.new { -> { a } } }
t { Ractor.new(&proc { a }) }
p Ractor.new { x = 1; [2].each { |y| x += y }; x }.value
p Ractor.new { def ractor_m; a = 5; [1].map { a }; end; ractor_m }.value
p Ractor.new { class RactorIsolationC; c = 3; [1].map { c }; end }.value
p Ractor.new { [1].map { _1 + 1 } + [1].map { it + 2 } }.value
p Ractor.new { [1].each { |;a| a = 3 }; :block_local }.value
p Ractor.new(5) { |a| a + 1 }.value
p Ractor.new { z = 1; eval('z + 1') + z }.value
Warning[:experimental] = true
", @"
ArgumentError: can not isolate a Proc because it accesses outer variables (a).
ArgumentError: can not isolate a Proc because it accesses outer variables (a).
ArgumentError: can not isolate a Proc because it accesses outer variables (a, b).
ArgumentError: can not isolate a Proc because it accesses outer variables (b).
ArgumentError: can not isolate a Proc because it accesses outer variables (a).
ArgumentError: can not isolate a Proc because it accesses outer variables (a).
3
[5]
[3]
[2, 3]
:block_local
6
3
");
        }

        public void Ractor_Messages1() {
            TestOutput(RactorPrologue + @"
echo = -> obj, move = false { Ractor.new { Ractor.receive }.send(obj, move: move).value }
s = +'str'
c = echo.(s)
p c, c.equal?(s), c.frozen?
f = 'lit'.freeze
p echo.(f).equal?(f)
a = [+'x', { k: +'v' }]; a << a
c = echo.(a)
p c[0].equal?(a[0]), c[2].equal?(c), c[1][:k], c[1][:k].equal?(a[1][:k])
fa = [+'q'].freeze
c = echo.(fa)
p c.frozen?, c.equal?(fa), c[0].frozen?
o = Object.new; o.instance_variable_set(:@x, [1])
c = echo.(o)
p c.instance_variable_get(:@x), c.instance_variable_get(:@x).equal?(o.instance_variable_get(:@x))
S = Struct.new(:m)
p echo.(S.new(+'member'))
p echo.(1..2), echo.(Ractor.current).equal?(Ractor.current)
t { echo.(proc { }) }
t { echo.(Thread.current) }
t { echo.([1].each) }
t { echo.(Hash.new { }) }
moved = [+'a', [+'b']]
inner = moved[1]
p echo.(moved, true)
t { moved.size }
t { inner.first }
t { moved.inspect }
mo = Object.new
echo.(mo, true)
t { mo.class }
t { mo == mo }
t { mo.__id__ }
t { echo.(Thread.current, true) }
fs = 'frozen'.freeze
p echo.(fs, true).equal?(fs), fs
Warning[:experimental] = true
", @"
""str""
false
false
true
false
true
""v""
false
true
false
false
[1]
false
#<struct S m=""member"">
1..2
true
TypeError: allocator undefined for Proc
TypeError: allocator undefined for Thread
Ractor::Error: can not copy Enumerator object.
TypeError: allocator undefined for Proc
[""a"", [""b""]]
Ractor::MovedError: can not send any methods to a moved object
Ractor::MovedError: can not send any methods to a moved object
Ractor::MovedError: can not send any methods to a moved object
Ractor::MovedError: can not send any methods to a moved object
Ractor::MovedError: can not send any methods to a moved object
Ractor::MovedError: can not send any methods to a moved object
Ractor::Error: can not move Thread object.
true
""frozen""
");
        }

        public void Ractor_Ports1() {
            TestOutput(RactorPrologue + @"
port = Ractor::Port.new
puts port.inspect.sub(/id:\d+/, 'id:N')
r = Ractor.new(port) { |pt| pt << 10; pt.send(20); :done }
p port.receive, port.receive, r.value
workers = Array.new(3) { |i| Ractor.new(port) { |pt| while (m = Ractor.receive) != :stop; pt << m * 2; end; :ok } }
6.times { |j| workers[j % 3] << j }
p Array.new(6) { port.receive }.sort
workers.each { |w| w << :stop }
p workers.map(&:value)
p port.closed?, port.close.equal?(port), port.closed?
t { port.receive }
t { port << 1 }
t { Ractor.new(Ractor::Port.new) { |q| Thread.current.report_on_exception = false; q.receive }.value }
t { Ractor.new(Ractor::Port.new) { |q| Thread.current.report_on_exception = false; q.close }.value }
t { r << 1 }
pt = Ractor.new { Ractor::Port.new }.value
p pt.closed?
q = Ractor::Port.new
q << 1 << 2
q.close
p q.receive, q.receive
t { q.receive }
a = Ractor.new { sleep 0.1; :a }
b = Ractor.new { :b }
rs = [a, b]
p Array.new(2) { r, v = Ractor.select(*rs); rs.delete(r); v }.sort
p1 = Ractor::Port.new; p2 = Ractor::Port.new
Ractor.new(p2) { |x| x << :via_p2 }
from, v = Ractor.select(p1, p2)
p from == p2, v
t { Ractor.select }
t { Ractor.select(1) }
r = Ractor.new { Ractor.receive }
mp = Ractor::Port.new
p r.monitor(mp)
r << 1
p mp.receive, r.monitor(mp), mp.receive
Ractor.new(Ractor.current) { |m| m << :hello_main }
p Ractor.receive
Warning[:experimental] = true
", @"
#<Ractor::Port to:#1 id:N>
10
20
:done
[0, 2, 4, 6, 8, 10]
[:ok, :ok, :ok]
false
true
true
Ractor::ClosedError: The port was already closed
Ractor::ClosedError: The port was already closed
Ractor::RemoteError: thrown by remote Ractor.
Ractor::RemoteError: thrown by remote Ractor.
Ractor::ClosedError: The port was already closed
true
1
2
Ractor::ClosedError: The port was already closed
[:a, :b]
true
:via_p2
ArgumentError: specify at least one Ractor::Port or Ractor
ArgumentError: should be Ractor::Port or Ractor
true
:exited
false
:exited
:hello_main
");
        }

        public void Ractor_Errors1() {
            TestOutput(RactorPrologue + @"
r = Ractor.new { Thread.current.report_on_exception = false; raise ArgumentError, 'bad' }
begin
  r.join
rescue Ractor::RemoteError => e
  p e.message, e.cause.class, e.cause.message, e.ractor.equal?(r)
end
t { r.value }
slow = Ractor.new { sleep 0.1; 1 }
p Ractor.new(slow) { |x| x.value }.value
t { slow.value }
p Ractor::Error.superclass, Ractor::IsolationError.superclass, Ractor::RemoteError.superclass,
  Ractor::MovedError.superclass, Ractor::ClosedError.superclass, Ractor::UnsafeError.superclass,
  Ractor::MovedObject.superclass
p Ractor.constants.sort
r = Ractor.new { Thread.new { exit }.join }
begin; r.value; rescue Ractor::RemoteError => e; p e.cause.class; end
r = Ractor.new(Ractor.current) { |m| Thread.new { begin; sleep; ensure; m << :killed; end }; sleep 0.1; :done }
p r.value, Ractor.receive
Warning[:experimental] = true
", @"
""thrown by remote Ractor.""
ArgumentError
""bad""
true
Ractor::RemoteError: thrown by remote Ractor.
1
Ractor::Error: Only the successor ractor can take a value
RuntimeError
Ractor::Error
Ractor::Error
Ractor::Error
StopIteration
Ractor::Error
BasicObject
[:ClosedError, :Error, :IsolationError, :MovedError, :MovedObject, :Port, :RemoteError, :UnsafeError]
SystemExit
:done
:killed
");
        }

        public void Ractor_Shareable1() {
            AssertOutput(() => CompilerTest(RactorPrologue + @"
p [1, :a, nil, 1.5, 2**100, 1..2, 'a'.freeze, Object, Ractor.current, Ractor::Port.new, Encoding::UTF_8, ENV].all? { |o| Ractor.shareable?(o) }
p [+'a', [1], ['a'.freeze, +'b'].freeze, Regexp.new('a'), proc { }, Struct.new(:a).new(+'x').freeze].any? { |o| Ractor.shareable?(o) }
x = Ractor.make_shareable([['a'], { b: +'c' }, +'d'])
p x.frozen?, x[0].frozen?, x[0][0].frozen?, x[1][:b].frozen?, Ractor.shareable?(x)
o = Object.new; o.instance_variable_set(:@a, +'z')
p Ractor.make_shareable(o).equal?(o), o.instance_variable_get(:@a).frozen?
c = [+'q']
d = Ractor.make_shareable(c, copy: true)
p c.frozen?, d.frozen?, d.equal?(c), d[0].frozen?
t { Ractor.make_shareable(Thread.current) }
t { Ractor.make_shareable([1].each) }
t { Ractor.make_shareable(proc { 1 }) }
t { Ractor.make_shareable(1.method(:+).to_proc) }
t { Ractor.make_shareable((+'').method(:to_s)) }
class RactorBadFreeze; def freeze; self; end; end
t { Ractor.make_shareable(RactorBadFreeze.new) }
pr = nil.instance_eval { -> { 1 } }
p Ractor.shareable?(pr), Ractor.make_shareable(pr).equal?(pr), Ractor.shareable?(pr), pr.frozen?
p Ractor.shareable?(Ractor.make_shareable(:upcase.to_proc))
v = [1]
w = 1
u = 1
t { Ractor.make_shareable(nil.instance_eval { proc { v } }) }
t { Ractor.make_shareable(nil.instance_eval { proc { w += 1 } }) }
p Ractor.make_shareable(nil.instance_eval { proc { u } }).call
t { Ractor.shareable_proc { v } }
p Ractor.shareable_proc(self: 1) { self + u }.call, Ractor.shareable_lambda { |q| q }.lambda?
p Ractor.shareable?(Ractor.shareable_proc { 1 })
t { Ractor.shareable_proc(self: +'s') { } }
Warning[:experimental] = true
"), @"
true
false
true
true
true
true
true
true
true
false
true
false
true
Ractor::Error: can not make shareable object for #<Thread:*>
Ractor::Error: can not make shareable object for *
Ractor::IsolationError: Proc's self is not shareable: #<Proc:*>
RuntimeError: not supported yet
Ractor::Error: can not make shareable object for #<Method: String#to_s*> because it refers unshareable objects
Ractor::Error: #freeze does not freeze object correctly
false
true
true
true
true
Ractor::IsolationError: cannot make a shareable Proc because it can refer unshareable object [1] from variable 'v'
ArgumentError: can not make a Proc shareable because it accesses outer variables (w).
1
Ractor::IsolationError: cannot make a shareable Proc because it can refer unshareable object [1] from variable 'v'
2
true
true
Ractor::IsolationError: self should be shareable: s
", OutputFlags.Match);
        }

        public void Ractor_Locals1() {
            TestOutput(RactorPrologue + @"
p Ractor[:rl_x], (Ractor[:rl_x] = 3), Ractor[:rl_x], Ractor.current[:rl_x], Ractor['rl_x']
p Ractor.store_if_absent(:rl_y) { 5 }, Ractor.store_if_absent(:rl_y) { 6 }
p Ractor.new { [Ractor[:rl_x], (Ractor.current[:rl_x] = 4), Ractor[:rl_x]] }.value, Ractor[:rl_x]
r = Ractor.new { Ractor.receive }
t { r[:rl_x] }
t { r[:rl_x] = 1 }
t { Ractor[1] }
r << 1
Warning[:experimental] = true
", @"
nil
3
3
3
3
5
5
[nil, 4, 4]
3
RuntimeError: Cannot get ractor local storage for non-current ractor
RuntimeError: Cannot set ractor local storage for non-current ractor
TypeError: 1 is not a symbol nor a string
");
        }

        // Each runs its Ractor's block and prints its value, or the error that ended the Ractor.
        private const string RactorIsolationPrologue = RactorPrologue + @"
Thread.report_on_exception = false
def rt(*args, &b) = t { r = Ractor.new(*args, &b); begin; p r.value; rescue Ractor::RemoteError => e; puts ""#{e.cause.class}: #{e.cause.message.sub(/\A\(eval at [^)]*\):\d+: /, '')}""; end }
";

        /// <summary>
        /// A non-main Ractor may use only the globals CRuby makes Ractor-local ($stdin, $stdout,
        /// $stderr, $VERBOSE, $DEBUG and aliases, which it has its own of) or that are per thread or
        /// scope; any other global, global_variables and alias raise Ractor::IsolationError.
        /// </summary>
        public void Ractor_GlobalVariables1() {
            TestOutput(RactorIsolationPrologue + @"
$g = 1
rt { $g }
rt { $g = 2 }
rt { defined?($g) }
rt { $0 }
rt { $LOAD_PATH }
rt { global_variables }
rt { eval('alias $ga $gb') }
rt { [$~, $_, $!, $/, $-0, $$ == Process.pid, $?.class] }
rt { /(b)/ =~ 'abc'; [$~[0], $1, $`, $', $&, $+] }
p $g
$VERBOSE = true
$DEBUG = false
rt { [$VERBOSE, $-v, $-w, $-W, $DEBUG, $-d] }
rt { $VERBOSE = nil; $DEBUG = 1; [$VERBOSE, $-W, $DEBUG, $-d] }
p [$VERBOSE, $DEBUG]
$VERBOSE = false
rt { [$stdin, $stdout, $stderr].map { |io| [io.class, io.fileno] } }
rt { [$stdout.equal?(STDOUT), $stderr.equal?(STDERR), $stdin.equal?(STDIN)] }
p Ractor.new { [$stdout.object_id, $stdout.object_id] }.value.uniq.size
p Ractor.new { $stdout.object_id }.value != $stdout.object_id
p $stdout.fileno
rt { $> = 1 }
Thread.report_on_exception = true
Warning[:experimental] = true
", @"
Ractor::IsolationError: can not access global variable $g from non-main Ractor
Ractor::IsolationError: can not access global variable $g from non-main Ractor
Ractor::IsolationError: can not access global variable $g from non-main Ractor
Ractor::IsolationError: can not access global variable $0 from non-main Ractor
Ractor::IsolationError: can not access global variable $LOAD_PATH from non-main Ractor
Ractor::IsolationError: can not access global variables from non-main Ractors
Ractor::IsolationError: can not access global variables from non-main Ractors
[nil, nil, nil, ""\n"", ""\n"", true, NilClass]
[""b"", ""b"", ""a"", ""c"", ""b"", ""b""]
1
[true, true, true, 2, false, false]
[nil, 0, 1, 1]
[true, false]
[[IO, 0], [IO, 1], [IO, 2]]
Ractor::IsolationError: can not access non-shareable objects in constant Object::STDOUT by non-main Ractor.
1
true
1
TypeError: $> must have write method, Integer given
");
        }

        /// <summary>
        /// Class variables, class-level instance variables and constants in a non-main Ractor
        /// (RactorIsolation): class variables are out of reach, a class or module only hands out
        /// shareable values and takes no writes, and a constant must hold a shareable value -
        /// whether read through a constant site's cache, Module#const_get or a method.
        /// </summary>
        public void Ractor_ClassState1() {
            TestOutput(RactorIsolationPrologue + @"
class RC
  @@cv = 1
  @str = 'str'.dup
  @int = 7
  @frozen = 'f'.freeze
  CONST = 'mutable'.dup
  FROZEN = ['a', 'b'].freeze
  DEEP = Ractor.make_shareable(['x', {k: 'v'}])
  NUM = 42
  class << self
    attr_accessor :str
    def cv = @@cv
    def int = @int
    def frozen = @frozen
    def const = CONST
  end
end
module RM; X = 'x'.dup; def self.x = X; end
TOP = 'top'.dup
class Object; private def top = TOP; end
rt { RC.cv }
rt { class RC; @@cv = 2; end }
rt { RC.class_variable_get(:@@cv) }
rt { RC.class_variable_set(:@@cv, 3) }
rt { RC.class_variable_defined?(:@@cv) }
rt { RC.class_variables }
rt { RC.str }
rt { RC.int }
rt { RC.frozen }
rt { RC.str = 1 }
rt { RC.instance_variable_get(:@str) }
rt { RC.instance_variable_get(:@int) }
rt { RC.instance_variable_set(:@int, 1) }
rt { RC.instance_variables.size }
rt { class RC; @nothing; end }
rt { Ractor.current.instance_variable_get(:@x) }
rt { Ractor.current.instance_variable_set(:@x, 1) }
rt { o = Object.new; o.instance_variable_set(:@a, 1); o.instance_variable_get(:@a) }
rt { RC::CONST }
rt { RC::FROZEN }
rt { RC::DEEP }
rt { RC::NUM }
rt { RC.const_get(:CONST) }
rt { RC.const_get(:NUM) }
rt { RC::NEW1 = 'new'.dup }
rt { RC::NEW2 = 'new'.freeze; RC::NEW2 }
rt { RC.const_set(:NEW3, [1]) }
rt { class RactorDefinedClass; end; RactorDefinedClass.name }
rt { defined?(RC::CONST) }
p RC.cv, RC.str, RC::CONST, top
Thread.report_on_exception = true
Warning[:experimental] = true
", @"
Ractor::IsolationError: can not access class variables from non-main Ractors (@@cv from RC)
Ractor::IsolationError: can not access class variables from non-main Ractors (@@cv from RC)
Ractor::IsolationError: can not access class variables from non-main Ractors (@@cv from RC)
Ractor::IsolationError: can not access class variables from non-main Ractors (@@cv from RC)
Ractor::IsolationError: can not access class variables from non-main Ractors (@@cv from RC)
[:@@cv]
Ractor::IsolationError: can not get unshareable values from instance variables of classes/modules from non-main Ractors (@str from RC)
7
""f""
Ractor::IsolationError: can not set instance variables of classes/modules by non-main Ractors
Ractor::IsolationError: can not get unshareable values from instance variables of classes/modules from non-main Ractors (@str from RC)
7
Ractor::IsolationError: can not set instance variables of classes/modules by non-main Ractors
3
nil
Ractor::IsolationError: can not access instance variables of shareable objects from non-main Ractors
Ractor::IsolationError: can not access instance variables of shareable objects from non-main Ractors
1
Ractor::IsolationError: can not access non-shareable objects in constant RC::CONST by non-main Ractor.
Ractor::IsolationError: can not access non-shareable objects in constant RC::FROZEN by non-main Ractor.
[""x"", {k: ""v""}]
42
Ractor::IsolationError: can not access non-shareable objects in constant RC::CONST by non-main Ractor.
42
Ractor::IsolationError: can not set constants with non-shareable objects by non-main Ractors
""new""
Ractor::IsolationError: can not set constants with non-shareable objects by non-main Ractors
""RactorDefinedClass""
""constant""
1
""str""
""mutable""
""top""
");
        }

        /// <summary>
        /// The block of Ractor.new and a shareable proc are isolated: eval cannot reach the locals
        /// around them (SyntaxError), Proc#binding refuses, and a shareable proc may not read an outer
        /// variable its scope assigns more than once. A method define_method made of a block that is
        /// not shareable runs only in the Ractor that defined it.
        /// </summary>
        public void Ractor_IsolatedProcs1() {
            TestOutput(RactorIsolationPrologue + @"
a = 42
t { Ractor.shareable_proc { eval('a') }.call }
p Ractor.shareable_proc { eval('1 + 1') }.call
p Ractor.shareable_proc { b = 3; eval('b') }.call
p Ractor.shareable_proc { [1].map { c = 2; eval('c') } }.call
p Ractor.shareable_proc { binding }.call.class
t { Ractor.shareable_proc { a }.binding }
p Ractor.shareable_proc { local_variables }.call
pr = nil.instance_exec { proc { eval('a') } }
p pr.call
Ractor.make_shareable(pr)
t { pr.call }
t { pr.binding }
outer = 1
rt { eval('outer') }
d = 1
t { Ractor.shareable_proc { d } }
d = 2
e1, e2 = 1, 2
p Ractor.shareable_proc { e1 }.call
f = 1 if true
p Ractor.shareable_proc { f }.call
g = 1
g += 1 if outer == 2
t { Ractor.shareable_proc { g } }
t { Ractor.make_shareable(nil.instance_exec { proc { g } }) }
->(z = 1) { p Ractor.shareable_proc { z }.call }.call
->(z) { p Ractor.shareable_proc { z }.call }.call(1)
->(z) { z += 1; t { Ractor.shareable_proc { z } } }.call(1)
[1].each { |i| i = 2; t { Ractor.shareable_proc { i } } }
def opt(o = 1) = Ractor.shareable_proc { o }.call
p opt
str = 'foo'.dup
Object.send(:define_method, :buggy) { |i| str << i.to_s }
rt { buggy(10) }
p buggy(1)
rt { self.class.send(:define_method, :mine) { 7 }; mine }
Thread.report_on_exception = true
Warning[:experimental] = true
", @"
SyntaxError: can not access variable 'a' from isolated Proc
2
3
[2]
Binding
ArgumentError: Can't create Binding from isolated Proc
[]
42
SyntaxError: can not access variable 'a' from isolated Proc
ArgumentError: Can't create Binding from isolated Proc
SyntaxError: can not access variable 'outer' from isolated Proc
Ractor::IsolationError: cannot make a shareable Proc because the outer variable 'd' may be reassigned.
1
1
Ractor::IsolationError: cannot make a shareable Proc because the outer variable 'g' may be reassigned.
Ractor::IsolationError: cannot make a shareable Proc because the outer variable 'g' may be reassigned.
1
1
Ractor::IsolationError: cannot make a shareable Proc because the outer variable 'z' may be reassigned.
Ractor::IsolationError: cannot make a shareable Proc because the outer variable 'i' may be reassigned.
1
RuntimeError: defined with an un-shareable Proc in a different Ractor
""foo1""
7
", 3);
        }

        /// <summary>
        /// A non-main Ractor's require is the main Ractor's (Ractor._require): the file runs free of
        /// the isolation checks. trap in a non-main Ractor isolates its block.
        /// </summary>
        public void Ractor_RequireInMain1() {
            TestOutput(RactorIsolationPrologue + @"
path = File.join(ENV['TMPDIR'] || '/tmp', ""ir_ractor_req_#{Process.pid}.rb"")
File.open(path, ""w"") { |io| io.write(""RACTOR_REQ = 'loaded'.dup\n$ractor_req = Ractor.main?\n"") }
rt(path) { |f| require f }
p RACTOR_REQ, $ractor_req
rt(path) { |f| require f }
rt(path) { |f| Ractor._require(f) }
rt { Ractor._require('ir_no_such_feature_x') }
File.delete(path)
Object.send(:define_method, :twice, &Ractor.shareable_proc { |i| i * 2 })
rt { twice(4) }
rt { trap(:USR2) { :ok } && :trapped }
rt { s = 1; trap(:USR2) { s }; :trapped }
trap(:USR2, 'DEFAULT')
Thread.report_on_exception = true
Warning[:experimental] = true
", @"
true
""loaded""
true
false
false
LoadError: cannot load such file -- ir_no_such_feature_x
8
:trapped
ArgumentError: can not isolate a Proc because it accesses outer variables (s).
");
        }
    }
}
