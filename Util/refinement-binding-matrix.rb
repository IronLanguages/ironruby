# Differential matrix for refinements on the paths that bind a call once and keep it: call-site
# rules (precompiled dispatchers, rules shared across receiver classes, the respond_to?/send
# intrinsics), the "#{x}" and &x conversions, the method JIT and OSR loops. Run under CRuby and
# under ./ir.sh - with the JIT and OSR on and off - and diff:
#
#   ruby Util/refinement-binding-matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/refinement-binding-matrix.rb > /tmp/ir.txt
#   ./ir.sh -X:NoJIT -X:NoOSR Util/refinement-binding-matrix.rb > /tmp/ir-nojit.txt
#   diff /tmp/mri.txt /tmp/ir.txt && diff /tmp/mri.txt /tmp/ir-nojit.txt
#
# IronRuby only looks at the scope of a call whose name some refinement of the receiver's class
# (or of one of its ancestors) has, and that set only grows: every probe below is made hot first
# and then probed again after the refinements around it change - a method arriving in an active
# refinement, being redefined or removed there, another file's `using' - so that a call bound
# the fast way before the change has to have been re-bound.

def t(label)
  print label.ljust(56), ' => '
  begin
    v = yield
    puts v.inspect
  rescue Exception => e
    puts e.class
  end
end

HOT = 120

# Section 9's method: defined before anything in this file is refined, first called in section 1.
def before_any(s)
  s.r9
rescue NoMethodError
  :plain
end

def warm
  i = 0
  while i < HOT
    yield i
    i += 1
  end
end

# ---------------------------------------------------------------- 1. methods arriving late

puts '--- 1. a method arriving in an active refinement after its call was bound ---'
module LateM
  R = refine(String) { def late_unused; end }
end
module LateUser
  using LateM
  def self.shout(s)
    s.shout
  rescue NoMethodError
    :no_method
  end
  def self.up(s) = s.upcase
end
warm { LateUser.shout('x'); LateUser.up('x') }
$before_any_first = before_any('x')
t('before the refinement has it') { LateUser.shout('x') }
LateM::R.send(:define_method, :shout) { :refined }
t('defined outside the refine block') { LateUser.shout('x') }
warm { LateUser.shout('x') }
LateM::R.send(:define_method, :shout) { :redefined }
t('redefined') { LateUser.shout('x') }
LateM::R.send(:remove_method, :shout)
t('removed') { LateUser.shout('x') }
LateM::R.send(:define_method, :upcase) { :refined_upcase }
t('a name the class has, refined late') { LateUser.up('x') }
t('and outside the using') { 'x'.upcase }

# ---------------------------------------------------------------- 2. which calls a refinement reaches

puts
puts '--- 2. which calls a refinement reaches ---'
class Base; def foo = :base_foo; def bar = :base_bar; end
class Sub < Base; end
class Other; def foo = :other_foo; end
module FooRef
  refine(Base) { def foo = :refined_foo }
end
module Mixed
  refine(Comparable) { def cmp_name = :refined_cmp }
end
def plain_foo(o) = o.foo
module FooUser
  using FooRef
  using Mixed
  def self.foo(o) = o.foo
  def self.bar(o) = o.bar
  def self.cmp(o) = o.cmp_name
end
objs = [Base.new, Sub.new, Other.new]
# one site seeing receivers of three classes: the shared-rule path, and Sub sharing Base's method
warm { |i| o = objs[i % 3]; FooUser.foo(o); FooUser.bar(o) if o.is_a?(Base); plain_foo(o) }
t('a refined name on the refined class') { FooUser.foo(Base.new) }
t('on a subclass') { FooUser.foo(Sub.new) }
t('on an unrelated class') { FooUser.foo(Other.new) }
t('a name the refinement does not have') { FooUser.bar(Sub.new) }
t('the same name where the refinement is not used') { objs.map { |o| plain_foo(o) } }
t('a refinement of a module') { [FooUser.cmp(1), FooUser.cmp('a'), FooUser.cmp(1.5)] }
t('outside the using') { 1.cmp_name rescue :none }

# ---------------------------------------------------------------- 3. respond_to?, send, method

puts
puts '--- 3. respond_to?, send, public_send, method ---'
module Reflect
  refine(Base) { def hidden = :refined_hidden }
end
def rt_plain(o) = o.respond_to?(:hidden)
module ReflectUser
  using Reflect
  def self.rt(o) = o.respond_to?(:hidden)
  def self.snd(o) = o.send(:hidden)
  def self.psnd(o) = o.public_send(:hidden)
  def self.meth(o) = o.method(:hidden).call
end
b = Base.new
warm { rt_plain(b); ReflectUser.rt(b) }
t('respond_to? where the refinement is used') { ReflectUser.rt(b) }
t('respond_to? where it is not') { rt_plain(b) }
t('send') { ReflectUser.snd(b) }
t('public_send') { ReflectUser.psnd(b) }
t('method') { ReflectUser.meth(b) }
t('send where it is not used') { b.send(:hidden) }

# ---------------------------------------------------------------- 4. conversions

puts
puts '--- 4. "#{x}" and &x ---'
class Pt; def to_s = 'plain'; end
module ToS
  refine(Pt) { def to_s = 'refined' }
end
module Dbl
  refine(Integer) { def double = self * 2 }
end
module ToProc
  refine(Pt) { def to_proc = proc { |x| x * 10 } }
end
def show_plain(o) = "#{o}"
module ConvUser
  using ToS
  using Dbl
  using ToProc
  def self.show(o) = "#{o}"
  def self.dbl(a) = a.map(&:double)
  def self.size(a) = a.map(&:size)
  def self.by_pt(a) = a.map(&Pt.new)
end
warm { ConvUser.show(Pt.new); show_plain(Pt.new); ConvUser.dbl([1]); ConvUser.size(['ab']) }
t('interpolation where to_s is refined') { ConvUser.show(Pt.new) }
t('interpolation where it is not') { show_plain(Pt.new) }
t('&:refined_name') { ConvUser.dbl([1, 2]) }
t('&:other_name') { ConvUser.size(['ab', 'c']) }
t('&obj with a refined to_proc') { ConvUser.by_pt([1, 2]) }
t('&:refined_name where it is not used') { [1].map(&:double) }

# ---------------------------------------------------------------- 5. the method JIT

puts
puts '--- 5. hot methods: operators and self-calls ---'
module PlusSucc
  refine(Integer) { def +(o) = super(o).succ }
end
def jit_plain_add(a, b) = a + b
module JitUser
  using PlusSucc
  def self.add(a, b) = a + b
  def self.mul(a, b) = a * b
  def self.fact(n) = n <= 1 ? 1 : n * fact(n.pred)
end
warm { |i| jit_plain_add(i, 1); JitUser.add(i, 1); JitUser.mul(i, 2); JitUser.fact(5) }
t('refined + where it is used') { JitUser.add(40, 1) }
t('built-in * there') { JitUser.mul(21, 2) }
t('a self-call there') { JitUser.fact(5) }
t('+ where it is not used') { jit_plain_add(40, 1) }

# ---------------------------------------------------------------- 6. OSR loops

puts
puts '--- 6. hot loops ---'
def osr_plain
  i = 0
  s = 0
  while i < 100_000
    s = s + 10
    i += 1
  end
  [i, s]
end
module OsrUser
  using PlusSucc
  def self.loop_refined
    i = 0
    s = 0
    while i < 100_000
      s = s + 10
      i += 1
    end
    [i, s]
  end
  def self.loop_times
    i = 0
    s = 1
    while i < 100_000
      s = (s * 3) % 1_000_003
      i = i.succ
    end
    [i, s]
  end
end
t('a loop where + is not refined') { osr_plain }
t('a loop where + is refined') { OsrUser.loop_refined }
t('a loop there that uses no refined operator') { OsrUser.loop_times }
t('the first loop again') { osr_plain }

# ---------------------------------------------------------------- 7. another file's using

puts
puts '--- 7. a `using` elsewhere ---'
require 'tmpdir'
other = File.join(Dir.tmpdir, "refinement_matrix_other_#{$$}.rb")
File.write(other, <<~RUBY)
  module OtherRef
    refine(String) { def other_name = :other }
  end
  using OtherRef
  $other_result = 'x'.other_name
RUBY
LateM::R.send(:define_method, :shout) { :back }
warm { LateUser.shout('x') }
t('a refined call before') { LateUser.shout('x') }
load other
File.delete(other)
t('the other file saw its refinement') { $other_result }
t('the refined call after') { LateUser.shout('x') }
t('the other refinement is not active here') { 'x'.other_name rescue :none }

# ---------------------------------------------------------------- 8. the same def under a using

puts
puts '--- 8. the same `def` run again where a refinement is active ---'
module R8
  refine(String) { def r8 = :refined }
end
definer = proc do
  def m8(s)
    s.r8
  rescue NoMethodError
    :plain
  end
end
definer.call
warm { m8('x') }
t('defined where nothing is used') { m8('x') }
using R8
t('still, after a using') { m8('x') }
definer.call
t('defined again after the using') { m8('x') }

# ---------------------------------------------------------------- 9. a def that ran before anything was refined

puts
puts '--- 9. a method defined before any refinement existed ---'
module R9
  refine(String) { def r9 = :refined }
end
using R9
t('first called once refinements existed') { $before_any_first }
t('called after a using in its scope') { before_any('x') }
t('a call written after the using') { 'x'.r9 }
