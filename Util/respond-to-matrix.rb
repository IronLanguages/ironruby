# Differential matrix for #respond_to? and the method_defined? family, built to catch a stale
# method-lookup cache: every query is asked once to warm whatever caches exist, then the method
# tables are changed (def, undef, remove, alias, include, prepend, extend, singleton def,
# visibility, define_method, attr_*, refinements, Kernel/Object/BasicObject additions) and every
# query is asked again. Run under CRuby and under ./ir.sh and diff the output - it must be identical.
#
#   ruby Util/respond-to-matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/respond-to-matrix.rb > /tmp/ir.txt
#   diff /tmp/mri.txt /tmp/ir.txt
#
# The last section runs threads that define and undefine methods while others query, with a
# handshake after each change, so a query that must see the change and answers from a stale cache
# is counted; it prints only the counts, which must be zero.

$step = 0

def t(label)
  $step += 1
  print "#{$step}. ", label.ljust(52), ' => '
  begin
    puts yield.inspect
  rescue Exception => e
    puts "#{e.class}: #{e.message}"
  end
end

# Every question about one name on one receiver, asked twice in a row so the second answer can
# come from a cache the first one filled.
# The method_defined? questions go to the object's class, or to the class passed in (a singleton
# class, for the singleton sections) - asking for obj.singleton_class would create one and move
# the object off its plain class.
def q(label, obj, name, cls = obj.class)
  2.times do
    t("#{label} #{name}") do
      [
        (obj.respond_to?(name) rescue $!.class),
        (obj.respond_to?(name, true) rescue $!.class),
        (obj.respond_to?(name.to_s) rescue $!.class),
        cls.method_defined?(name),
        cls.public_method_defined?(name),
        cls.private_method_defined?(name),
        cls.protected_method_defined?(name),
      ]
    end
  end
end

def mq(label, mod, name)
  2.times do
    t("#{label} #{name}") do
      [mod.method_defined?(name), mod.public_method_defined?(name),
       mod.private_method_defined?(name), mod.protected_method_defined?(name)]
    end
  end
end

puts '--- def / undef / remove on the receiver class ---'
class A; end
class B < A; end
a = A.new
b = B.new
q('a', a, :foo)
q('b', b, :foo)
class A; def foo; :a; end; end
q('a after def A#foo', a, :foo)
q('b after def A#foo', b, :foo)
class B; undef_method :foo; end
q('a after B undef', a, :foo)
q('b after B undef', b, :foo)
class B; def foo; :b; end; end
q('b after B redef', b, :foo)
class B; remove_method :foo; end
q('b after B remove', b, :foo)
class A; remove_method :foo; end
q('a after A remove', a, :foo)
q('b after A remove', b, :foo)
class A; def foo; end; undef_method :foo; end
q('a after A def+undef', a, :foo)
class A; define_method(:foo) { 1 }; end
q('a after define_method over undef', a, :foo)
q('b after define_method over undef', b, :foo)

puts
puts '--- visibility ---'
class A; def vis; end; end
q('a public', a, :vis)
class A; private :vis; end
q('a private', a, :vis)
q('b private', b, :vis)
class A; protected :vis; end
q('a protected', a, :vis)
class B; public :vis; end
q('b re-exported public', b, :vis)
q('a still protected', a, :vis)
class B; private :vis; end
q('b re-exported private', b, :vis)
class A; public :vis; end
q('a public again', a, :vis)
q('b forwarder still private', b, :vis)
class B; remove_method :vis; end
q('b forwarder removed', b, :vis)
class A; private def pdef; end; end
q('a private def', a, :pdef)
class A; public; def pub2; end; private; def priv2; end; public; end
q('a pub2', a, :pub2)
q('a priv2', a, :priv2)
class A; public :priv2; end
q('a priv2 made public', a, :priv2)

puts
puts '--- alias ---'
q('a al', a, :al)
class A; alias al foo; end
q('a alias al', a, :al)
class A; alias_method :al2, :vis; end
q('a alias_method al2', a, :al2)
class A; private :al2; end
q('a al2 private', a, :al2)
class A; undef_method :al; end
q('a al undef', a, :al)

puts
puts '--- attr_* ---'
q('a x', a, :x)
q('a x=', a, :x=)
class A; attr_reader :x; end
q('a attr_reader x', a, :x)
q('a x= still missing', a, :x=)
class A; attr_writer :x; end
q('a attr_writer x=', a, :x=)
class A; attr_accessor :y; private :y=; end
q('a y', a, :y)
q('a y= private', a, :y=)

puts
puts '--- include / prepend / extend ---'
module M; def m1; end; end
q('a m1', a, :m1)
class A; include M; end
q('a after include M', a, :m1)
q('b after include M', b, :m1)
module M; def m2; end; end
q('a after def M#m2', a, :m2)
module M; private :m2; end
q('a after M private m2', a, :m2)
module M; remove_method :m2; end
q('a after M remove m2', a, :m2)
mq('M', M, :m1)
mq('M', M, :m2)
module N; def n1; end; end
module M; include N; end
q('a after M include N', a, :n1)
mq('M', M, :n1)
module N; private :n1; end
mq('M after N private n1', M, :n1)
q('a after N private n1', a, :n1)
module P; def pp1; end; def foo; super; end; end
q('b pp1', b, :pp1)
class B; prepend P; end
q('b after prepend P', b, :pp1)
q('b foo after prepend P', b, :foo)
module P; def pp2; end; end
q('b after def P#pp2', b, :pp2)
module P; undef_method :pp1; end
q('b after P undef pp1', b, :pp1)
module E; def e1; end; end
c = A.new
q('c e1', c, :e1)
q('a e1', a, :e1)
c.extend(E)
q('c after extend E', c, :e1, c.singleton_class)
q('a not extended', a, :e1)
module E; def e2; end; end
q('c after def E#e2', c, :e2, c.singleton_class)

puts
puts '--- singleton methods ---'
d = A.new
q('d s1', d, :s1, d.singleton_class)
def d.s1; end
q('d after def d.s1', d, :s1, d.singleton_class)
q('a no s1', a, :s1)
class << d; private :s1; end
q('d s1 private', d, :s1, d.singleton_class)
class << d; undef_method :foo; end
q('d singleton undef foo', d, :foo, d.singleton_class)
q('a foo unaffected', a, :foo)
class << d; remove_method :s1; end
q('d s1 removed', d, :s1, d.singleton_class)
d.define_singleton_method(:s2) { }
q('d define_singleton_method s2', d, :s2, d.singleton_class)
q('A class method cm', A, :cm, A.singleton_class)
def A.cm; end
q('A after def A.cm', A, :cm, A.singleton_class)
q('B inherits cm', B, :cm, B.singleton_class)
class << B; undef_method :cm; end
q('B undef cm', B, :cm, B.singleton_class)

puts
puts '--- Kernel / Object / BasicObject after warm-up ---'
q('a k1', a, :k1)
q('1 k1', 1, :k1)
q('"" k1', '', :k1)
module Kernel; def k1; end; end
q('a after Kernel#k1', a, :k1)
q('1 after Kernel#k1', 1, :k1)
q('"" after Kernel#k1', '', :k1)
q('a o1', a, :o1)
class Object; def o1; end; end
q('a after Object#o1', a, :o1)
q('nil after Object#o1', nil, :o1)
class Object; private :o1; end
q('a after Object private o1', a, :o1)
q('a bo1', a, :bo1)
class BasicObject; def bo1; end; end
q('a after BasicObject#bo1', a, :bo1)
module Kernel; undef_method :k1; end
q('a after Kernel undef k1', a, :k1)
module Kernel; remove_method :k1 rescue nil; end
q('a after Kernel remove k1', a, :k1)
module KX; def kx; end; end
q('a kx', a, :kx)
module Kernel; include KX; end
q('a after Kernel include KX', a, :kx)
q('A to_str', a, :to_str)
q('A to_ary', a, :to_ary)
q('A to_hash', a, :to_hash)
class A; def to_str; 'a'; end; end
q('A after def to_str', a, :to_str)
t('"x" + a after to_str') { 'x' + a }
t('Array(a)') { Array(a).map(&:class) }
class A; def to_ary; [1]; end; end
q('A after def to_ary', a, :to_ary)
t('Array(a) after to_ary') { Array(a) }
t('[[a]].flatten') { [[a]].flatten.map(&:class) }
class A; undef_method :to_ary; end
t('Array(a) after undef to_ary') { Array(a).map(&:class) }
q('A after undef to_ary', a, :to_ary)

puts
puts '--- respond_to_missing? ---'
class RM
  def respond_to_missing?(name, include_all)
    ($rm_calls ||= []) << [name, include_all]
    name.to_s.start_with?('dyn_') || (include_all && name == :secret)
  end
end
r = RM.new
$rm_calls = []
q('r dyn_x', r, :dyn_x)
q('r plain', r, :plain)
q('r secret', r, :secret)
t('respond_to_missing? calls') { $rm_calls }
$rm_calls = []
class RM; def respond_to_missing?(name, include_all) = name == :plain; end
q('r after rtm redef dyn_x', r, :dyn_x)
q('r after rtm redef plain', r, :plain)
t('respond_to_missing? calls after redef') { $rm_calls }
class RM; remove_method :respond_to_missing?; end
q('r after rtm remove', r, :plain)
class RM; def respond_to_missing?(n, i) = true; end
q('r after rtm def true', r, :anything)
class RM; def anything; end; end
q('r defined anything', r, :anything)
class RM; private :anything; end
q('r anything private (rtm true)', r, :anything)
class RM; undef_method :respond_to_missing?; end
q('r after rtm undef', r, :zzz)
$rtm_state = false
class RM2; def respond_to_missing?(n, i) = $rtm_state; end
r2 = RM2.new
t('rtm answer not cached (false)') { r2.respond_to?(:q) }
$rtm_state = true
t('rtm answer not cached (true)') { r2.respond_to?(:q) }
$rtm_state = false
t('rtm answer not cached (false again)') { r2.respond_to?(:q) }
module Kernel; private def respond_to_missing?(n, i) = n == :kernel_rtm; end
t('Kernel rtm redefined, a kernel_rtm') { a.respond_to?(:kernel_rtm) }
t('Kernel rtm redefined, 1 kernel_rtm') { 1.respond_to?(:kernel_rtm) }
module Kernel; private def respond_to_missing?(n, i) = false; end
t('Kernel rtm reset, a kernel_rtm') { a.respond_to?(:kernel_rtm) }

puts
puts '--- method_missing does not count ---'
class MM; def method_missing(n, *a) = n == :ghost ? :boo : super; end
mm = MM.new
q('mm ghost', mm, :ghost)
t('mm.ghost') { mm.ghost }

puts
puts '--- user-defined respond_to? ---'
class UR; def respond_to?(n, i = false) = n == :fake || super; end
ur = UR.new
q('ur fake', ur, :fake)
q('ur to_s', ur, :to_s)
class UR; remove_method :respond_to?; end
q('ur fake after remove', ur, :fake)
class UR2; def respond_to?(n, i = false) = true; end
t('UR2 anything') { UR2.new.respond_to?(:anything) }
t('UR2 method_defined? anything') { UR2.method_defined?(:anything) }

puts
puts '--- BasicObject ---'
class BO < BasicObject; def bm; end; end
bo = BO.new
t('BO method_defined? bm') { BO.method_defined?(:bm) }
t('BO method_defined? respond_to?') { BO.method_defined?(:respond_to?) }
t('bo.respond_to?') { bo.respond_to?(:bm) rescue $!.class }
class BO; define_method(:respond_to?, ::Kernel.instance_method(:respond_to?)); end
t('bo borrowed respond_to? bm') { bo.respond_to?(:bm) }
t('bo borrowed respond_to? zz') { bo.respond_to?(:zz) }
class BO; def zz; end; end
t('bo borrowed respond_to? zz after def') { bo.respond_to?(:zz) }

puts
puts '--- refinements ---'
class RC; def plain; end; end
module RR
  refine RC do
    def refined_only; end
    private def refined_private; end
  end
end
rc = RC.new
q('rc refined_only before using', rc, :refined_only)
using RR
t('rc refined_only with using') { [rc.respond_to?(:refined_only), rc.respond_to?(:refined_only, true)] }
t('rc refined_private with using') { [rc.respond_to?(:refined_private), rc.respond_to?(:refined_private, true)] }
t('RC.method_defined? refined_only') { RC.method_defined?(:refined_only) }
t('rc plain with using') { rc.respond_to?(:plain) }
t('rc refined_only via send') { rc.send(:respond_to?, :refined_only) }
t('rc refined_only via method') { rc.method(:respond_to?).call(:refined_only) }

puts
puts '--- strings, arity, bad names, inherit ---'
t('respond_to? string') { a.respond_to?('foo') }
t('respond_to? 3 args') { a.respond_to?(:foo, true, 1) rescue $!.class }
t('respond_to? 0 args') { a.respond_to? rescue $!.class }
t('respond_to? integer') { a.respond_to?(1) rescue $!.class }
t('respond_to? nil include_all') { a.respond_to?(:vis, nil) }
t('respond_to? "yes" include_all') { a.respond_to?(:pdef, 'yes') }
t('method_defined? integer') { A.method_defined?(1) rescue $!.class }
t('method_defined? inherit false') { B.method_defined?(:foo, false) rescue $!.class }
t('method_defined? inherit true') { B.method_defined?(:foo, true) rescue $!.class }
t('A inherit false foo') { [A.method_defined?(:foo, false), A.public_method_defined?(:foo, false)] }
t('B inherit false (prepended P#foo)') { [B.method_defined?(:foo, false), B.method_defined?(:pp2, false), B.method_defined?(:pp2)] }
t('A inherit false m1 (from M)') { [A.method_defined?(:m1, false), A.method_defined?(:m1)] }
t('A inherit false priv2/pdef') { [A.public_method_defined?(:priv2, false), A.private_method_defined?(:pdef, false), A.private_method_defined?(:pdef, nil)] }
class B; public :pdef; end
t('B inherit false forwarder') { [B.public_method_defined?(:pdef, false), B.private_method_defined?(:pdef, false), A.private_method_defined?(:pdef, false)] }
t('M inherit false') { [M.method_defined?(:m1, false), M.method_defined?(:n1, false), M.private_method_defined?(:n1, false), N.private_method_defined?(:n1, false)] }

puts
puts '--- many names on one class ---'
class Many; end
mo = Many.new
names = (1..600).map { |i| :"gen_#{i}" }
t('600 misses') { names.count { |n| mo.respond_to?(n) } }
class Many; define_method(:gen_1) {}; define_method(:gen_300) {}; define_method(:gen_600) {}; end
t('600 after 3 defs') { names.count { |n| mo.respond_to?(n) } }
t('method_defined? after 3 defs') { names.count { |n| Many.method_defined?(n) } }

puts
puts '--- threads ---'
class TC; end
tobj = TC.new
ROUNDS = 300
QUERIERS = 3
stale = 0
errors = 0
lock = Mutex.new
go = Array.new(QUERIERS) { Queue.new }
ack = Queue.new
queriers = QUERIERS.times.map do |i|
  Thread.new do
    loop do
      expected = go[i].pop
      break if expected == :stop
      # the change happened-before this pop, so the answer must reflect it
      begin
        got = [tobj.respond_to?(:th), TC.method_defined?(:th), TC.public_method_defined?(:th)]
        want = expected == :private ? [false, false, false] : [expected, expected, expected]
        lock.synchronize { stale += 1 } unless got == want
      rescue Exception
        lock.synchronize { errors += 1 }
      end
      ack << true
    end
  end
end
# background churn on other names and classes, racing everything above
churn_stop = false
churners = 2.times.map do |k|
  Thread.new do
    n = 0
    until churn_stop
      TC.class_eval { define_method(:"churn_#{k}") {} }
      tobj.respond_to?(:"churn_#{k}")
      TC.class_eval { remove_method(:"churn_#{k}") }
      tobj.respond_to?(:"churn_#{k}")
      n += 1
    end
  end
end
ROUNDS.times do |r|
  case r % 4
  when 0 then TC.class_eval { def th; end }; state = true
  when 1 then TC.class_eval { private :th }; state = :private
  when 2 then TC.class_eval { public :th }; state = true
  when 3 then TC.class_eval { undef_method :th }; state = false
  end
  QUERIERS.times { |i| go[i] << state }
  QUERIERS.times { ack.pop }
end
QUERIERS.times { |i| go[i] << :stop }
queriers.each(&:join)
churn_stop = true
churners.each(&:join)
t('threads: stale answers') { stale }
t('threads: errors') { errors }
t('threads: final respond_to? th') { tobj.respond_to?(:th) }
t('threads: churn methods gone') { [tobj.respond_to?(:churn_0), tobj.respond_to?(:churn_1)] }

# free-running: queries racing definitions with no handshake must still only ever answer
# true or false, and settle on the truth once the definer is done.
class TD; end
td = TD.new
odd = 0
qs = 3.times.map do
  Thread.new do
    20000.times do
      v = td.respond_to?(:flip)
      odd += 1 unless v == true || v == false
    end
  end
end
definer = Thread.new do
  500.times do
    TD.class_eval { def flip; end }
    TD.class_eval { remove_method :flip }
  end
  TD.class_eval { def flip; end }
end
(qs + [definer]).each(&:join)
t('free-running: non-boolean answers') { odd }
t('free-running: settled') { [td.respond_to?(:flip), TD.method_defined?(:flip)] }
