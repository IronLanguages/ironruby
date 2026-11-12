# Differential matrix for how a callable describes itself: #parameters, #arity
# and #lambda? for procs, lambdas, methods, unbound methods, define_method
# bodies, Symbol#to_proc, Method#to_proc and curried procs.
#
#   ruby Util/callable-parameters-matrix.rb > /tmp/mri.txt 2>/dev/null
#   ./ir.sh Util/callable-parameters-matrix.rb > /tmp/ir.txt 2>/dev/null
#   diff /tmp/mri.txt /tmp/ir.txt
#
# The cross-product that matters is the parameter shape: required / optional /
# rest / post / keyword / required keyword / keyrest / **nil / block, and the
# anonymous spellings of each.  Every shape is asked the same three questions
# through five different callable wrappers, because MRI's answers differ
# between them: a proc reports positional parameters as :opt where the same
# shape in a lambda reports :req, and #arity counts keywords only when at
# least one of them is required.

# Object addresses differ between the two runtimes by construction, so they are
# blanked out of every answer.
def t(label)
  print label.ljust(58), ' => '
  begin
    puts(yield.inspect.gsub(/0x\h+/, '0xX'))
  rescue ::Exception => e
    puts "#{e.class}: #{e.message.gsub(/0x\h+/, '0xX')}"
  end
end

# Every shape below is written once as source text and instantiated five ways.
# `eval` keeps the list to one spelling per shape.
SHAPES = [
  '',
  'a',
  'a, b',
  'a=1',
  'a, b=1',
  'a=1, b=2',
  '*a',
  '*',
  'a, *b',
  'a, *b, c',
  'a, *, c',
  'a=1, *b, c',
  'a, b=1, *c, d',
  '&b',
  '&',
  'a, &b',
  'k:',
  'k: 1',
  'k:, j: 2',
  '**kw',
  '**',
  '**nil',
  'a, k:',
  'a, k: 1',
  'a, *b, k:',
  'a, *b, k:, **kw',
  'a=1, *b, c, k:, j: 2, **kw, &l',
  'a, b=1, *c, d, e:, f: 2, **g, &h',
  '*a, **kw',
  '*a, &b',
  '*, **, &',
  'a, (b, c)',
  '(a, b)',
  'a, (b, *c), d',
  '_, _',
  'a, _, _',
]

# Shapes that are only legal in a block / lambda, not in a `def`.
BLOCK_ONLY = ['a, ', 'a, b, ']

puts '===== proc { |...| }  parameters / arity / lambda?'
SHAPES.each do |s|
  t("proc {|#{s}|}") do
    pr = eval("proc { |#{s}| }")
    [pr.parameters, pr.arity, pr.lambda?]
  end
end
BLOCK_ONLY.each do |s|
  t("proc {|#{s}|}") do
    pr = eval("proc { |#{s}| }")
    [pr.parameters, pr.arity, pr.lambda?]
  end
end

puts
puts '===== lambda { |...| }  parameters / arity / lambda?'
SHAPES.each do |s|
  t("lambda {|#{s}|}") do
    pr = eval("lambda { |#{s}| }")
    [pr.parameters, pr.arity, pr.lambda?]
  end
end
BLOCK_ONLY.each do |s|
  t("lambda {|#{s}|}") do
    pr = eval("lambda { |#{s}| }")
    [pr.parameters, pr.arity, pr.lambda?]
  end
end

puts
puts '===== ->(...) {}  parameters / arity / lambda?'
SHAPES.each do |s|
  t("->(#{s}){}") do
    pr = eval("->(#{s}) { }")
    [pr.parameters, pr.arity, pr.lambda?]
  end
end

puts
puts '===== def m(...)  Method#parameters / arity, UnboundMethod#parameters / arity'
SHAPES.each do |s|
  t("def m(#{s})") do
    c = Class.new
    c.class_eval("def m(#{s}); end")
    m = c.new.method(:m)
    u = c.instance_method(:m)
    [m.parameters, m.arity, u.parameters, u.arity]
  end
end

puts
puts '===== define_method(:m) { |...| }  parameters / arity'
SHAPES.each do |s|
  t("define_method {|#{s}|}") do
    c = Class.new
    c.class_eval("define_method(:m) { |#{s}| }")
    m = c.new.method(:m)
    [m.parameters, m.arity, m.to_proc.lambda?]
  end
end

puts
puts '===== Method#to_proc keeps the method signature'
SHAPES.each do |s|
  t("method(:m).to_proc |#{s}|") do
    c = Class.new
    c.class_eval("def m(#{s}); end")
    pr = c.new.method(:m).to_proc
    [pr.parameters, pr.arity, pr.lambda?]
  end
end

puts
puts '===== Proc#parameters(lambda: ...)'
[true, false, nil, 0, 'x'].each do |arg|
  t("proc{|a,b=1,*c,d:,&e|}.parameters(lambda: #{arg.inspect})") do
    proc { |a, b = 1, *c, d:, &e| }.parameters(lambda: arg)
  end
  t("->(a,b=1,*c,d:,&e){}.parameters(lambda: #{arg.inspect})") do
    ->(a, b = 1, *c, d:, &e) {}.parameters(lambda: arg)
  end
end
t('proc{}.parameters(:positional)') { proc {}.parameters(:positional) }
t('proc{}.parameters(1)') { proc {}.parameters(1) }
t('proc{}.parameters(bogus: 1)') { proc {}.parameters(bogus: 1) }

puts
puts '===== Symbol#to_proc'
t(':upcase.to_proc.parameters') { :upcase.to_proc.parameters }
t(':upcase.to_proc.arity') { :upcase.to_proc.arity }
t(':upcase.to_proc.lambda?') { :upcase.to_proc.lambda? }
t(':upcase.to_proc.source_location') { :upcase.to_proc.source_location }
t(':upcase.to_proc.call("a")') { :upcase.to_proc.call('a') }
t(':+.to_proc.call(1, 2)') { :+.to_proc.call(1, 2) }
t(':+.to_proc.call(1)') { :+.to_proc.call(1) }
t(':upcase.to_proc.inspect') { :upcase.to_proc.inspect.sub(/0x\h+/, '0xX') }
class PrivHolder
  private def secret; 'secret'; end
end
t(':secret.to_proc on private method') { :secret.to_proc.call(PrivHolder.new) }
t('[1,2].map(&:secret) private') { [PrivHolder.new].map(&:secret) }
t(':upcase.to_proc.frozen?') { :upcase.to_proc.frozen? }
t(':upcase.to_proc == :upcase.to_proc') { :upcase.to_proc == :upcase.to_proc }

puts
puts '===== Proc#curry'
add3 = ->(a, b, c) { a + b + c }
padd = proc { |a, b, c| a.to_i + b.to_i + c.to_i }
t('lambda.curry.parameters') { add3.curry.parameters }
t('lambda.curry.arity') { add3.curry.arity }
t('lambda.curry.lambda?') { add3.curry.lambda? }
t('lambda.curry[1][2][3]') { add3.curry[1][2][3] }
t('lambda.curry[1, 2][3]') { add3.curry[1, 2][3] }
t('lambda.curry[1].lambda?') { add3.curry[1].lambda? }
t('lambda.curry.source_location') { add3.curry.source_location }
t('lambda.curry.binding') { add3.curry.binding }
t('lambda.curry(3)[1][2][3]') { add3.curry(3)[1][2][3] }
t('lambda.curry(2)') { add3.curry(2) }
t('lambda.curry(4)') { add3.curry(4) }
t('proc.curry[1][2][3]') { padd.curry[1][2][3] }
t('proc.curry.lambda?') { padd.curry.lambda? }
t('proc.curry[1].lambda?') { padd.curry[1].lambda? }
t('proc.curry(2)[1][2]') { padd.curry(2)[1][2] }
t('proc.curry(5)[1][2][3][4][5]') { padd.curry(5)[1][2][3][4][5] }
t('variadic lambda curry') { ->(a, *b) { [a, b] }.curry[1] }
t('variadic lambda curry(3)') { ->(a, *b) { [a, b] }.curry(3)[1][2][3] }
t('curry on 0-arity lambda') { -> { 42 }.curry.call }
t('curry(-1)') { add3.curry(-1) }
t('curry("2")') { add3.curry('2') }

puts
puts '===== Method#curry, Method#>>, Method#<<'
class CompHolder
  def add(a, b); a + b; end
  def dbl(x); x * 2; end
  def none; 7; end
end
ch = CompHolder.new
t('method.curry[1][2]') { ch.method(:add).curry[1][2] }
t('method.curry(2)[1][2]') { ch.method(:add).curry(2)[1][2] }
t('method.curry(1)') { ch.method(:add).curry(1) }
t('method.curry(3)') { ch.method(:add).curry(3) }
t('(m >> m2).call') { (ch.method(:dbl) >> ch.method(:dbl)).call(3) }
t('(m >> m2).lambda?') { (ch.method(:dbl) >> ch.method(:dbl)).lambda? }
t('(m << m2).call') { (ch.method(:dbl) << ch.method(:dbl)).call(3) }
t('(m >> proc).call') { (ch.method(:dbl) >> proc { |x| x + 1 }).call(3) }
t('(m >> 1)') { ch.method(:dbl) >> 1 }
t('(m << 1)') { ch.method(:dbl) << 1 }
t('(m >> :sym)') { (ch.method(:dbl) >> :to_s).call(3) }
t('(lambda >> lambda).lambda?') { (->(x) { x } >> ->(x) { x }).lambda? }
t('(proc >> proc).lambda?') { (proc { |x| x } >> proc { |x| x }).lambda? }
t('(proc >> lambda).lambda?') { (proc { |x| x } >> ->(x) { x }).lambda? }
t('(lambda << proc).lambda?') { (->(x) { x } << proc { |x| x }).lambda? }
t('(proc << lambda).lambda?') { (proc { |x| x } << ->(x) { x }).lambda? }
t('(m >> m).parameters') { (ch.method(:dbl) >> ch.method(:dbl)).parameters }
t('(m >> m).arity') { (ch.method(:dbl) >> ch.method(:dbl)).arity }

puts
puts '===== Method identity: owner / receiver / name / original_name / unbind'
module OwnerMod
  def modm; end
end
class OwnerBase
  def basem; end
  def privm; end
  private :privm
  def self.singm; end
end
class OwnerSub < OwnerBase
  include OwnerMod
  alias_method :aliased, :basem
  define_method(:defined_m) { |x| x }
  alias_method :aliased_twice, :aliased
  public :privm
end
ob = OwnerSub.new
%w[basem modm aliased aliased_twice defined_m privm].each do |n|
  t("OwnerSub##{n} owner/name/original_name") do
    m = ob.method(n)
    [m.owner, m.name, m.original_name, m.receiver.class]
  end
  t("OwnerSub##{n} unbind.owner/name") do
    u = ob.method(n).unbind
    [u.owner, u.name]
  end
end
t('OwnerBase.method(:singm).owner') { OwnerBase.method(:singm).owner }
t('OwnerBase.method(:singm).receiver') { OwnerBase.method(:singm).receiver }
t('singleton method owner') do
  o = Object.new
  def o.s; end
  o.method(:s).owner.inspect.sub(/0x\h+/, '0xX')
end
t('method(:puts).owner') { method(:puts).owner }
t('method(:puts).source_location') { method(:puts).source_location }
t('1.method(:+).owner') { 1.method(:+).owner }
t('1.method(:+).source_location') { 1.method(:+).source_location }
t('1.method(:+).unbind.owner') { 1.method(:+).unbind.owner }
t('Integer.instance_method(:+).owner') { Integer.instance_method(:+).owner }

puts
puts '===== UnboundMethod#bind / #bind_call'
t('bind_call basic') { OwnerSub.instance_method(:defined_m).bind_call(ob, 5) }
t('bind_call wrong receiver') { OwnerSub.instance_method(:defined_m).bind_call(Object.new, 5) }
t('bind wrong receiver') { OwnerSub.instance_method(:defined_m).bind(Object.new) }
t('bind_call Kernel#class on BasicObject') do
  Kernel.instance_method(:class).bind_call(BasicObject.new)
end
t('bind_call with block') do
  c = Class.new { def y; yield 3; end }
  c.instance_method(:y).bind_call(c.new) { |x| x * 3 }
end
t('unbound == unbound') { OwnerSub.instance_method(:basem) == OwnerSub.instance_method(:basem) }
t('unbound.to_s') { OwnerSub.instance_method(:basem).to_s.sub(/0x\h+/, '0xX') }
t('unbound.inspect == to_s') do
  u = OwnerSub.instance_method(:basem)
  u.inspect == u.to_s
end
t('unbound.bind(ob).class') { OwnerSub.instance_method(:basem).bind(ob).class }
t('Method#to_s') { ob.method(:basem).to_s.sub(/0x\h+/, '0xX') }
t('Method#inspect == to_s') do
  m = ob.method(:basem)
  m.inspect == m.to_s
end

puts
puts '===== Binding'
def binding_maker(arg1, arg2 = 2)
  local = 3
  binding
end
b = binding_maker(1)
t('b.local_variables.sort') { b.local_variables.sort }
t('b.local_variable_get(:arg1)') { b.local_variable_get(:arg1) }
t('b.local_variable_get("local")') { b.local_variable_get('local') }
t('b.local_variable_get(:nope)') { b.local_variable_get(:nope) }
t('b.local_variable_defined?(:local)') { b.local_variable_defined?(:local) }
t('b.local_variable_defined?(:nope)') { b.local_variable_defined?(:nope) }
t('b.local_variable_set(:fresh, 9)') { b.local_variable_set(:fresh, 9) }
t('b.local_variable_get(:fresh)') { b.local_variable_get(:fresh) }
t('b.local_variables.include?(:fresh)') { b.local_variables.include?(:fresh) }
t('b.local_variable_get(1)') { b.local_variable_get(1) }
t('b.local_variable_set(:$g, 1)') { b.local_variable_set(:$g, 1) }
t('b.receiver.class') { b.receiver.class }
t('b.source_location') { b.source_location&.first&.class }
t('b.source_location[1].class') { b.source_location&.last&.class }
t('b.eval("arg1 + local")') { b.eval('arg1 + local') }
t('b.eval("self").class') { b.eval('self').class }
t('b.dup.class') { b.dup.class }
t('b.clone.class') { b.clone.class }
t('b.dup.local_variable_get(:local)') { b.dup.local_variable_get(:local) }
t('b.frozen?') { b.frozen? }
t('Binding.new') { Binding.new }
t('proc binding sees block locals') do
  x = 41
  pr = proc { x }
  pr.binding.local_variable_get(:x)
end
t('lambda binding set writes through') do
  y = 1
  l = -> { y }
  l.binding.local_variable_set(:y, 2)
  y
end

puts
puts '===== Proc misc'
t('proc{}.inspect == to_s') do
  pr = proc {}
  pr.inspect == pr.to_s
end
t('proc{}.to_s shape') { proc {}.to_s.sub(/0x\h+/, '0xX').sub(/:\d+/, ':L') }
t('lambda{}.to_s shape') { lambda {}.to_s.sub(/0x\h+/, '0xX').sub(/:\d+/, ':L') }
t(':x.to_proc.to_s shape') { :x.to_proc.to_s.sub(/0x\h+/, '0xX').sub(/:\d+/, ':L') }
t('method(:puts).to_proc.to_s shape') do
  method(:puts).to_proc.to_s.sub(/0x\h+/, '0xX').sub(/:\d+/, ':L')
end
t('method(:puts).to_proc.lambda?') { method(:puts).to_proc.lambda? }
t('UnboundMethod#to_proc') { Integer.instance_method(:to_s).to_proc.lambda? rescue $!.class }
t('proc{}.source_location.last.class') { proc {}.source_location.last.class }
t('proc{}.source_location.first.class') { proc {}.source_location.first.class }
t('method(:puts).to_proc.source_location') { method(:puts).to_proc.source_location }
t('proc{}.dup.class') { proc {}.dup.class }
t('proc{}.clone.class') { proc {}.clone.class }
t('proc ivar survives dup') do
  pr = proc {}
  pr.instance_variable_set(:@a, 1)
  pr.dup.instance_variable_get(:@a)
end
t('proc ivar survives clone') do
  pr = proc {}
  pr.instance_variable_set(:@a, 1)
  pr.clone.instance_variable_get(:@a)
end
t('frozen proc clone stays frozen') { proc {}.freeze.clone.frozen? }
t('frozen proc dup not frozen') { proc {}.freeze.dup.frozen? }
t('Proc.new without block') { Proc.new }
t('proc{|a|}.call(1,2)') { proc { |a| a }.call(1, 2) }
t('lambda{|a|}.call(1,2)') { lambda { |a| a }.call(1, 2) }
t('proc{|a,b|}.call([1,2])') { proc { |a, b| [a, b] }.call([1, 2]) }
t('proc{}.ruby2_keywords.class') { proc {}.ruby2_keywords.class }
t('proc{}.=== 1') { proc { |x| x == 1 }.===(1) }
