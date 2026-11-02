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
    public partial class Tests {
        /// <summary>
        /// A method that arrives in an active refinement after a call of its name has been bound - defined,
        /// redefined or removed outside the refine block - is what the call makes next.
        /// </summary>
        public void Refinements1() {
            TestOutput(@"
module M
  R = refine(String) { def a; :a; end }
end
using M
def call_shout(s)
  s.shout
rescue NoMethodError
  :no_method
end
p call_shout('x')
M::R.send(:define_method, :shout) { :refined }
p call_shout('x')
M::R.send(:define_method, :shout) { :redefined }
p call_shout('x')
M::R.send(:remove_method, :shout)
p call_shout('x')
", @"
:no_method
:refined
:redefined
:no_method
");
        }

        /// <summary>
        /// An operator added to an active refinement after the method using it has been called many times.
        /// </summary>
        public void Refinements2() {
            TestOutput(@"
module Later
  R = refine(Integer) { def zzz; 0; end }
end
module Calc
  using Later
  def self.mul(a, b)
    a * b
  end
end
200.times { Calc.mul(3, 4) }
p Calc.mul(3, 4)
Later::R.send(:define_method, :*) { |o| :refined_mul }
p Calc.mul(3, 4)
p 3 * 4
", @"
12
:refined_mul
12
");
        }

        /// <summary>
        /// Whether a call can be refined depends on its name and the receiver's class: a refinement of
        /// String#shout leaves String#upcase and every Integer call alone, a refinement of a module reaches
        /// the classes that include it, and a CLR class can be refined.
        /// </summary>
        public void Refinements3() {
            TestOutput(@"
module Shout
  refine(String) { def shout; upcase + '!'; end }
end
module CmpName
  refine(Comparable) { def cmp_name; :refined_cmp; end }
end
module SbShout
  refine(System::Text::StringBuilder) { def shout; :sb; end }
end
module User
  using Shout
  using CmpName
  using SbShout
  def self.go(s, i)
    [s.shout, s.upcase, i + 1, i.cmp_name, s.cmp_name, System::Text::StringBuilder.new.shout]
  end
end
3.times { User.go('a', 1) }
p User.go('a', 1)
p [('a'.shout rescue :none), (1.cmp_name rescue :none)]
", @"
[""A!"", ""A"", 2, :refined_cmp, :refined_cmp, :sb]
[:none, :none]
");
        }

        /// <summary>
        /// Kernel#respond_to? sees the refinements active where it is called; the folded respond_to?(:sym)
        /// of a Ruby object must not answer for a scope that sees another refinement.
        /// </summary>
        public void Refinements4() {
            TestOutput(@"
class Foo; end
module RT
  refine(Foo) { def bar; 1; end }
end
def rt1(o) = o.respond_to?(:bar)
module UsesRT
  using RT
  def self.rt2(o) = o.respond_to?(:bar)
end
f = Foo.new
3.times { rt1(f); UsesRT.rt2(f) }
p [rt1(f), UsesRT.rt2(f), f.respond_to?(:bar)]
", @"
[false, true, false]
");
        }

        /// <summary>
        /// ""#{x}"" and &amp;:sym honour the refinements active where they are written, and only there.
        /// </summary>
        public void Refinements5() {
            TestOutput(@"
class Pt; def to_s; 'plain'; end; end
module TS
  refine(Pt) { def to_s; 'refined'; end }
end
module SY
  refine(Integer) { def double; self * 2; end }
end
module Uses
  using TS
  using SY
  def self.show(o) = ""#{o}""
  def self.dbl(a) = a.map(&:double)
  def self.lens(a) = a.map(&:size)
end
p [""#{Pt.new}"", Uses.show(Pt.new), Uses.dbl([1, 2]), Uses.lens(['ab']), ([1].map(&:double) rescue :no_double)]
", @"
[""plain"", ""refined"", [2, 4], [2], :no_double]
");
        }

        /// <summary>
        /// A method defined where nothing is `using'd sees no refinement however hot it runs; the same `def'
        /// run again where one is active does.
        /// </summary>
        public void Refinements6() {
            TestOutput(@"
module R6
  refine(String) { def r6; :refined; end }
end
definer = proc do
  def m6(s)
    s.r6
  rescue NoMethodError
    :plain
  end
end
definer.call
100.times { m6('x') }
p m6('x')
using R6
p m6('x')
definer.call
p m6('x')
", @"
:plain
:plain
:refined
");
        }

        /// <summary>
        /// A method defined before anything was refined, and first called after, does not see a `using' that comes
        /// later in the scope it was defined in.
        /// </summary>
        public void Refinements7() {
            TestOutput(@"
def before(s)
  s.r7
rescue NoMethodError
  :plain
end
module R7
  refine(String) { def r7; :refined; end }
end
p before('x')
using R7
p before('x')
def after(s) = s.r7
p after('x')
", @"
:plain
:plain
:refined
");
        }

        /// <summary>
        /// An OSR loop entered under other refinements than its compiled copy's gets a copy for those, however
        /// often that happens: it does not use up the rebuilds kept for locals whose types widen, and retire.
        /// </summary>
        public void Refinements8() {
            int specialized = IronRuby.Runtime.Osr.OsrLoopSite.Specialized;
            int retired = IronRuby.Runtime.Osr.OsrLoopSite.Retired;
            TestOutput(@"
module R8a; refine(String) { def r8a; 1; end }; end
module R8b; refine(String) { def r8b; 2; end }; end
module R8c; refine(String) { def r8c; 3; end }; end
module R8d; refine(String) { def r8d; 4; end }; end
module R8e; refine(String) { def r8e; 5; end }; end
work = lambda { i = 0; while i < 100_000; i += 1; end; i }
p work.call
using R8a
p work.call
using R8b
p work.call
using R8c
p work.call
using R8d
p work.call
using R8e
p work.call
", @"
100000
100000
100000
100000
100000
100000
");
            int retiredHere = IronRuby.Runtime.Osr.OsrLoopSite.Retired - retired;
            int specializedHere = IronRuby.Runtime.Osr.OsrLoopSite.Specialized - specialized;
            Assert(retiredHere == 0 && specializedHere == 6,
                System.String.Format("OSR sites retired: {0} (expected 0), copies specialized: {1} (expected 6)", retiredHere, specializedHere));
        }

        /// <summary>
        /// A refined call in a method body, or in a block in it, tests the body's refinement record (RubyCallAction.Resolve).
        /// The same `def' run again where other refinements are active changes the record, and the calls bound against
        /// the old one bind again - in the calls of the method that start afterwards and in one that is still running,
        /// whether or not its scope has evaluated a string. (Before the record was tested, a running call that had
        /// evaluated a string kept the old refinements, while every other one saw the new ones.)
        /// </summary>
        public void Refinements9() {
            TestOutput(@"
module R1; refine(String) { def r = :r1 }; end
module R2; refine(String) { def r = :r2 }; end
module R3; refine(String) { def r = :r3 }; end
class C; end
using R1
definer = proc do
  C.class_eval do
    def m(s) = [s.r, [1].map { s.r }.first]
    def n(s, again) = [s.r, again.call, s.r, [1].map { s.r }.first]
    def e(s, again) = [eval('s.r'), again.call, s.r, eval('s.r')]
  end
end
definer.call
p (1..100).map { [C.new.m('x'), C.new.n('x', -> { :none }), C.new.e('x', -> { :none })] }.uniq
using R2
p C.new.m('x')
p C.new.e('x', -> { definer.call; :again })
p (1..100).map { [C.new.m('x'), C.new.n('x', -> { :none }), C.new.e('x', -> { :none })] }.uniq
using R3
p C.new.n('x', -> { definer.call; :again })
p (1..100).map { [C.new.m('x'), C.new.n('x', -> { :none }), C.new.e('x', -> { :none })] }.uniq
", @"
[[[:r1, :r1], [:r1, :none, :r1, :r1], [:r1, :none, :r1, :r1]]]
[:r1, :r1]
[:r1, :again, :r2, :r2]
[[[:r2, :r2], [:r2, :none, :r2, :r2], [:r2, :none, :r2, :r2]]]
[:r2, :again, :r3, :r3]
[[[:r3, :r3], [:r3, :none, :r3, :r3], [:r3, :none, :r3, :r3]]]
");
        }

        /// <summary>
        /// A block written in a method body, and one inside it, that Module#refine runs see the refinements of the module
        /// being refined, though their calls were bound against the method's record before: a rule that reads the record
        /// for a block passes only while no refine block runs. (CRuby 4.0 answers :outer_q and :outer_n on the last line,
        /// from the inline caches the earlier calls filled; with no earlier call it answers :inner_q and :inner_n.)
        /// </summary>
        public void Refinements10() {
            TestOutput(@"
module Outer
  refine(String) { def o = :outer; def q = :outer_q; def n = :outer_n }
end
using Outer
class FakeRefiner
  def refine(_) = yield
end
def mk(target, out) = target.send(:refine, Integer) { out << ['x'.o, 'x'.q, [1].map { 'x'.n }.first] }
holder = Module.new { refine(String) { def q = :inner_q; def n = :inner_n } }
out = []
fake = FakeRefiner.new
100.times { mk(fake, out) }
p out.uniq
mk(holder, out)
p out.last
", @"
[[:outer, :outer_q, :outer_n]]
[:outer, :inner_q, :inner_n]
");
        }

        /// <summary>
        /// A refined call on a Ruby object, made in a method or in a block written in one, gets a precompiled dispatcher that
        /// tests the method's record (MethodDispatcher.CreateRefinedRubyObjectDispatcher): with no arguments, with two and
        /// with a block. It binds again when the `def' runs again under other refinements, and when Module#refine runs the
        /// block. (CRuby 4.0 prints :r1 on the third line, its block keeping the refinements it was made under - see
        /// Refinements6 - and :r2 on the fifth, from the inline caches the earlier calls filled.)
        /// </summary>
        public void Refinements11() {
            TestOutput(@"
class P; end
module R1
  refine(P) { def r = :r1; def r2(a, b) = [:r1, a, b]; def rb = [:r1, yield] }
end
module R2
  refine(P) { def r = :r2; def r2(a, b) = [:r2, a, b]; def rb = [:r2, yield] }
end
using R1
definer = proc do
  def m(o) = [o.r, o.r2(1, 2), o.rb { 3 }, [1].map { [o.r, o.r2(4, 5), o.rb { 6 }] }.first]
end
definer.call
o = P.new
p (1..100).map { m(o) }.uniq
using R2
p m(o)
definer.call
p (1..100).map { m(o) }.uniq
class FakeRefiner
  def refine(_) = yield
end
def mk(target, out, o) = target.send(:refine, Integer) { out << [o.r, o.r2(1, 2), o.rb { 3 }] }
holder = Module.new { refine(P) { def r = :inner; def r2(a, b) = [:inner, a, b]; def rb = [:inner, yield] } }
out = []
fake = FakeRefiner.new
100.times { mk(fake, out, o) }
p out.uniq
mk(holder, out, o)
p out.last
p (1..100).map { mk(fake, out, o); out.last }.uniq
", @"
[[:r1, [:r1, 1, 2], [:r1, 3], [:r1, [:r1, 4, 5], [:r1, 6]]]]
[:r1, [:r1, 1, 2], [:r1, 3], [:r1, [:r1, 4, 5], [:r1, 6]]]
[[:r2, [:r2, 1, 2], [:r2, 3], [:r2, [:r2, 4, 5], [:r2, 6]]]]
[[:r2, [:r2, 1, 2], [:r2, 3]]]
[:inner, [:inner, 1, 2], [:inner, 3]]
[[:r2, [:r2, 1, 2], [:r2, 3]]]
");
        }
    }
}
