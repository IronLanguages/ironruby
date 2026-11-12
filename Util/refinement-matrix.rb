# Differential matrix for refinements (Module#refine / main#using / Module#using).
# Run under CRuby and under ./ir.sh and diff the output.
#
#   ruby Util/refinement-matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/refinement-matrix.rb > /tmp/ir.txt
#   diff /tmp/mri.txt /tmp/ir.txt
#
# The point of this file is ACTIVATION SCOPE, not `refine` itself.  A
# refinement is lexically scoped: it is active from the `using` call to the end
# of the enclosing file / eval string / class body, it leaks into blocks
# created there, and it does NOT leak into methods called from there.  Every
# section below pins one edge of that rule.
#
# NOTE ON THIS FILE'S OWN SCOPE: a top-level `using` would activate for the
# whole rest of the file, so every case that needs a fresh scope runs inside an
# `eval` string (eval gives a new lexical scope in which `using` is allowed).
# The genuinely top-level `using` case is deliberately LAST.

def t(label)
  print label.ljust(52), ' => '
  begin
    puts yield.inspect
  rescue Exception => e
    puts "#{e.class}: #{e.message}"
  end
end

# ---------------------------------------------------------------- fixtures

class Target
  def base;  'Target#base';  end
  def shout; 'Target#shout'; end
end

R1 = Module.new do
  refine Target do
    def hello; 'R1#hello'; end
    def shout; "R1(#{super})"; end
  end
end

R2 = Module.new do
  refine Target do
    def other; 'R2#other'; end
  end
end

# a method defined OUTSIDE any `using` scope; calling it from inside a using
# scope must NOT see the refinement
def outside_caller(obj)
  obj.hello
end

def outside_block_runner
  yield
end

def outside_respond(obj)
  obj.respond_to?(:hello)
end

puts '--- presence ---'
t('Module#refine is a private instance method') { Module.private_method_defined?(:refine) }
t('Module#refine is not public')                { Module.method_defined?(:refine) }
t('Module#using is a private instance method')  { Module.private_method_defined?(:using) }
t('main responds to using (private singleton)') { (class << self; self; end).private_instance_methods(false).include?(:using) }
t('Kernel#using')                               { Kernel.private_method_defined?(:using) }
t('Refinement is a class')                      { defined?(Refinement) && Refinement.class.to_s }
t('Refinement.superclass')                      { Refinement.superclass.to_s rescue $!.class }
t('Refinement#refined_class')                   { Refinement.method_defined?(:refined_class) rescue $!.class }
t('Refinement#target')                          { Refinement.method_defined?(:target) rescue $!.class }
t('Module#refinements')                         { Module.method_defined?(:refinements) }
t('Module.used_modules')                        { Module.respond_to?(:used_modules) }
t('Module.used_refinements')                    { Module.respond_to?(:used_refinements) }

puts
puts '--- refine returns an anonymous refinement module ---'
inner = nil
Rx = Module.new { inner = refine(Target) { def q; end } }
t('refine result class')        { inner.class.to_s }
t('refine result name')         { inner.name.inspect }
t('refine result is a Module')  { inner.is_a?(Module) }
t('refined_class')              { inner.refined_class.to_s rescue $!.class.to_s }
t('target')                     { inner.target.to_s rescue $!.class.to_s }
t('public_instance_methods')    { inner.public_instance_methods(false).sort }
t('holder != refinement')       { Rx != inner }
t('inner.ancestors[0,3]')       { inner.ancestors.first(3).map { |m| m.name || m.class.to_s } }
t('Rx.refinements size')        { Rx.refinements.size rescue $!.class.to_s }
t('Rx.refinements == [inner]')  { (Rx.refinements == [inner]) rescue $!.class.to_s }

puts
puts '--- refine reopening: same class twice gives the same module ---'
a = b = nil
Rr = Module.new do
  a = refine(Target) { def one; 1; end }
  b = refine(Target) { def two; 2; end }
end
t('same object for both refine calls') { a.equal?(b) }
t('both methods on it')                { a.public_instance_methods(false).sort }
t('Rr.refinements size')               { Rr.refinements.size rescue $!.class.to_s }

puts
puts '--- errors ---'
t('refine with no argument')  { Module.new { refine {} }; :ok }
t('refine with a String')     { Module.new { refine('foo') {} }; :ok }
t('refine with no block')     { Module.new { refine Target }; :ok }
t('using in a method body')   { Class.new { def g; using R1; end }.new.g }
t('main.using inside a def')  { eval('def zz_top; using R1; end; zz_top') }
t('using with a non-module')  { eval('using 42') }
t('using with a plain module'){ eval('using Comparable') }

puts
puts '--- not active without using ---'
t('Target#hello before using') { Target.new.hello }
t('respond_to?(:hello)')       { Target.new.respond_to?(:hello) }
t('Target.method_defined?')    { Target.method_defined?(:hello) }
t('Target.instance_methods')   { Target.instance_methods(false).sort }
t('Target.ancestors[0,2]')     { Target.ancestors.first(2).map(&:to_s) }

puts
puts '=== ACTIVATION SCOPE ==='

puts
puts '--- 1. visible after `using` in the same scope ---'
t('call after using') { eval(<<~RUBY) }
  using R1
  Target.new.hello
RUBY
t('call BEFORE using in the same scope') { eval(<<~RUBY) }
  r = (Target.new.hello rescue "\#{$!.class}")
  using R1
  r
RUBY

puts
puts '--- 2. NOT visible in a method defined elsewhere, called from here ---'
t('method defined outside, called inside') { eval(<<~RUBY) }
  using R1
  outside_caller(Target.new)
RUBY
t('method defined inside the using scope') { eval(<<~RUBY) }
  using R1
  def inside_caller(o); o.hello; end
  inside_caller(Target.new)
RUBY
t('send from inside the using scope') { eval(<<~RUBY) }
  using R1
  Target.new.send(:hello)
RUBY
t('public_send from inside') { eval(<<~RUBY) }
  using R1
  Target.new.public_send(:hello)
RUBY
t('respond_to? from inside') { eval(<<~RUBY) }
  using R1
  Target.new.respond_to?(:hello)
RUBY
t('method(:hello) from inside') { eval(<<~RUBY) }
  using R1
  Target.new.method(:hello).call
RUBY
t('Target.method_defined? from inside') { eval(<<~RUBY) }
  using R1
  Target.method_defined?(:hello)
RUBY
t('Target.ancestors from inside') { eval(<<~RUBY) }
  using R1
  Target.ancestors.first(2).map(&:to_s)
RUBY

puts
puts '--- 3. visible in a block created here and called elsewhere ---'
t('block passed out of the using scope') { eval(<<~RUBY) }
  using R1
  outside_block_runner { Target.new.hello }
RUBY
t('proc created here, called outside') { eval(<<~RUBY).call }
  using R1
  proc { Target.new.hello }
RUBY
t('lambda created here, called outside') { eval(<<~RUBY).call }
  using R1
  -> { Target.new.hello }
RUBY
t('nested block two deep') { eval(<<~RUBY) }
  using R1
  outside_block_runner { outside_block_runner { Target.new.hello } }
RUBY
t('define_method body created here') { eval(<<~RUBY) }
  using R1
  k = Class.new(Target) { }
  k.send(:define_method, :dm) { hello }
  k.new.dm
RUBY

puts
puts '--- 4. super inside a refinement ---'
t('super reaches the original')      { eval("using R1; Target.new.shout") }
t('unrefined shout still available') { Target.new.shout }

puts
puts '--- 5. multiple refinements of the same class ---'
t('two `using` calls, both active') { eval(<<~RUBY) }
  using R1
  using R2
  [Target.new.hello, Target.new.other]
RUBY
t('only one of the two active') { eval(<<~RUBY) }
  using R2
  [(Target.new.hello rescue "\#{$!.class}"), Target.new.other]
RUBY
Rlast = Module.new { refine(Target) { def hello; 'Rlast#hello'; end } }
t('later using wins for the same method') { eval(<<~RUBY) }
  using R1
  using Rlast
  Target.new.hello
RUBY
t('earlier using is shadowed, not lost') { eval(<<~RUBY) }
  using Rlast
  using R1
  Target.new.hello
RUBY

puts
puts '--- 6. refinement module that includes another ---'
Rinc = Module.new { include R1 }
t('using a module that includes a refiner') { eval("using Rinc; Target.new.hello") }
t('Rinc.refinements (own only)')            { Rinc.refinements.size rescue $!.class.to_s }

puts
puts '--- 7. using inside a class body ---'
t('class body using, method in that body') { eval(<<~RUBY) }
  Class.new do
    using R1
    def g; Target.new.hello; end
  end.new.g
RUBY
t('class body using does not leak out') { eval(<<~RUBY) }
  Class.new { using R1 }
  Target.new.hello
RUBY
t('class body using does not leak to a later reopen') { eval(<<~RUBY) }
  k = Class.new { using R1; def a; Target.new.hello; end }
  k.class_eval { def b; Target.new.hello; end }
  [(k.new.a rescue "\#{$!.class}"), (k.new.b rescue "\#{$!.class}")]
RUBY
t('subclass of a using-ing class') { eval(<<~RUBY) }
  base = Class.new { using R1; def a; Target.new.hello; end }
  sub  = Class.new(base) { def b; Target.new.hello; end }
  [(sub.new.a rescue "\#{$!.class}"), (sub.new.b rescue "\#{$!.class}")]
RUBY
t('module_eval with a block does not accept using') { eval(<<~RUBY) }
  Module.new.module_eval { using R1 }
  :ok
RUBY

puts
puts '--- 8. Module.used_modules / used_refinements ---'
t('used_modules at top of file') { Module.used_modules.map { |m| m.equal?(R1) ? 'R1' : m.to_s } }
t('used_modules after using')    { eval("using R1; Module.used_modules.map { |m| m.equal?(R1) ? 'R1' : m.to_s }") rescue $!.class.to_s }
t('used_modules two usings')     { eval("using R1; using R2; Module.used_modules.size") rescue $!.class.to_s }
t('used_refinements after using'){ eval("using R1; Module.used_refinements.map { |r| r.refined_class.to_s }") rescue $!.class.to_s }
t('used_refinements empty')      { eval("Module.used_refinements") rescue $!.class.to_s }

puts
puts '--- 9. refinements and Comparable/operators ---'
Rop = Module.new do
  refine Target do
    def +(o); 'R#plus'; end
    def to_s; 'R#to_s'; end
    def <=>(o); 0; end
  end
end
t('refined binary operator')   { eval("using Rop; Target.new + 1") }
t('refined to_s via interp')   { eval('using Rop; "#{Target.new}"') }
t('refined to_s via String()') { eval('using Rop; String(Target.new)') rescue $!.class.to_s }
t('refined to_s outside')      { Target.new.to_s.sub(/0x\h+/, '0xX') }

puts
puts '--- 10. refining a builtin ---'
Rstr = Module.new do
  refine String do
    def shout; upcase + '!'; end
    def length; 4242; end
  end
end
t('refined String#shout')            { eval("using Rstr; 'ab'.shout") }
t('refined String#length overrides') { eval("using Rstr; 'ab'.length") }
t('String#length outside')           { 'ab'.length }
t('refined method via map(&:)')      { eval("using Rstr; %w[a b].map(&:shout)") }
t('refined method inside a block')   { eval("using Rstr; %w[a b].map { |s| s.shout }") }

Rint = Module.new { refine(Integer) { def double; self * 2; end } }
t('refined Integer#double')      { eval('using Rint; 3.double') }
t('refined Integer outside')     { 3.double }

puts
puts '--- 11. refinement is per-file, not global ---'
t('after every eval above, still unrefined') { Target.new.hello }
t('String still unrefined')                  { 'ab'.shout }

puts
puts '--- 12. main.using at TOP LEVEL (must come last: it leaks downward) ---'
t('before the top-level using') { Target.new.hello }
using R1
t('after the top-level using')                 { Target.new.hello }
t('super from the top-level using')            { Target.new.shout }
t('method defined before still unrefined')     { outside_caller(Target.new) }
t('block from top level run elsewhere')        { outside_block_runner { Target.new.hello } }
t('a def AFTER the top-level using')           { def after_using(o); o.hello; end; after_using(Target.new) }
t('used_modules now')                          { Module.used_modules.map { |m| m.equal?(R1) ? 'R1' : m.to_s } }
t('Target.ancestors unchanged')                { Target.ancestors.first(2).map(&:to_s) }
t('Target.method_defined? unchanged')          { Target.method_defined?(:hello) }
