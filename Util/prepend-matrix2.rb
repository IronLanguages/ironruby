# Second differential matrix for Module#prepend: the interactions that the first
# one does not cover (callbacks, removal, extend, define_method, deep chains).
#
#   ruby Util/prepend-matrix2.rb > /tmp/mri2.txt
#   ./ir.sh Util/prepend-matrix2.rb > /tmp/ir2.txt
#   diff /tmp/mri2.txt /tmp/ir2.txt

def t(label)
  print label.ljust(50), ' => '
  begin
    puts yield.inspect
  rescue Exception => e
    puts "#{e.class}: #{e.message}"
  end
end

def names(mods); mods.map { |m| m.to_s }; end

puts '--- method_added / method_defined through prepend ---'
$added = []
module Pa1
  def self.method_added(n); $added << "Pa1:#{n}"; end
  def who; "Pa1->#{super}"; end
end
class Ca1
  def self.method_added(n); $added << "Ca1:#{n}"; end
  prepend Pa1
  def who; 'Ca1'; end
end
t('method_added log')            { $added }
t('Ca1.method_defined?(:who)')   { Ca1.method_defined?(:who) }
t('Ca1.new.who')                 { Ca1.new.who }

puts
puts '--- define_method inside a prepended module ---'
module Pd
  define_method(:who) { "Pd->#{super()}" }
end
class Cd
  prepend Pd
  def who; 'Cd'; end
end
t('Cd.new.who') { Cd.new.who }

puts
puts '--- remove_method in the prepended module ---'
module Pr1
  def who; "Pr1->#{super}"; end
end
class Cr1
  prepend Pr1
  def who; 'Cr1'; end
end
o = Cr1.new
t('before remove') { o.who }
module Pr1
  remove_method :who
end
t('after remove')  { o.who }

puts
puts '--- undef_method in the prepended module ---'
module Pu
  def who; 'Pu'; end
end
class Cu
  prepend Pu
  def who; 'Cu'; end
end
ou = Cu.new
t('before undef') { ou.who }
module Pu
  undef_method :who
end
t('after undef')  { ou.who }

puts
puts '--- respond_to? / methods ---'
module Ps1; def zap; end; end
class Cs1; prepend Ps1; end
t('Cs1.new.respond_to?(:zap)')      { Cs1.new.respond_to?(:zap) }
t('Cs1.new.methods.include?(:zap)') { Cs1.new.methods.include?(:zap) }
t('Cs1.instance_methods.grep(/zap/)') { Cs1.instance_methods.grep(/zap/).inspect }

puts
puts '--- Object#extend with a module that has prepends ---'
module Pe1; def who; "Pe1->#{super}"; end; end
module Me1; prepend Pe1; def who; 'Me1'; end; end
oe = Object.new
oe.extend(Me1)
t('extended obj who')             { oe.who }
t('singleton ancestors[0,3]')     { names(oe.singleton_class.ancestors[0, 3]) }

puts
puts '--- deep super chain ---'
module D1; def who; "D1->#{super}"; end; end
module D2; def who; "D2->#{super}"; end; end
module D3; def who; "D3->#{super}"; end; end
class Cdeep
  include D3
  prepend D1
  prepend D2
  def who; "Cdeep->#{super}"; end
end
t('Cdeep.new.who')      { Cdeep.new.who }
t('Cdeep.ancestors[0,5]') { names(Cdeep.ancestors[0, 5]) }

puts
puts '--- prepend to a superclass, super from subclass method ---'
module Psup; def who; "Psup->#{super}"; end; end
class BaseS; def who; 'BaseS'; end; end
class SubS < BaseS; def who; "SubS->#{super}"; end; end
BaseS.prepend(Psup)
t('SubS.new.who')        { SubS.new.who }
t('SubS.ancestors[0,4]') { names(SubS.ancestors[0, 4]) }

puts
puts '--- prepend into a module later included ---'
module Pl1; def who; "Pl1->#{super}"; end; end
module Ml1; def who; 'Ml1'; end; end
Ml1.prepend(Pl1)
class Cl1; include Ml1; end
t('Cl1.new.who')         { Cl1.new.who }
t('Cl1.ancestors[0,3]')  { names(Cl1.ancestors[0, 3]) }

puts
puts '--- prepend into a module already included (propagation) ---'
module Pl2; def who; "Pl2->#{super}"; end; end
module Ml2; def who; 'Ml2'; end; end
class Cl2; include Ml2; end
c2 = Cl2.new
t('before propagation') { c2.who }
Ml2.prepend(Pl2)
t('after propagation')  { c2.who }
t('Cl2.ancestors[0,3]') { names(Cl2.ancestors[0, 3]) }

puts
puts '--- two levels of include, prepend at the bottom ---'
module Pl3; def who; "Pl3->#{super}"; end; end
module Ml3; def who; 'Ml3'; end; end
module Ml3b; include Ml3; end
class Cl3; include Ml3b; end
t('before')             { Cl3.new.who }
Ml3.prepend(Pl3)
t('after')              { Cl3.new.who }
t('Cl3.ancestors[0,4]') { names(Cl3.ancestors[0, 4]) }

puts
puts '--- method_missing / super to method_missing ---'
module Pmm
  def bogus; "Pmm"; end
end
class Cmm
  prepend Pmm
  def method_missing(n, *a); "mm:#{n}"; end
  def respond_to_missing?(n, p = false); true; end
end
t('Cmm.new.bogus')  { Cmm.new.bogus }
t('Cmm.new.other')  { Cmm.new.other }

puts
puts '--- prepend with initialize ---'
module Pi
  def initialize(*a); super; @tag = :pi; end
end
class Ci
  prepend Pi
  attr_reader :tag, :x
  def initialize(x); @x = x; end
end
ci = Ci.new(5)
t('Ci#x')   { ci.x }
t('Ci#tag') { ci.tag }

puts
puts '--- prepend to a module used with module_function ---'
module Pmf; def who; "Pmf->#{super}"; end; end
module Mmf
  def who; 'Mmf'; end
  module_function :who
end
t('Mmf.who') { Mmf.who }

puts
puts '--- constants resolve through prepends ---'
module Pc1; K = :from_Pc1; end
class Cc1; prepend Pc1; end
t('Cc1::K')            { Cc1::K }
t('Cc1.const_defined?(:K)') { Cc1.const_defined?(:K) }

puts
puts '--- instance_variable / class methods unaffected ---'
module Pv; def self.helper; :helper; end; def who; "Pv->#{super}"; end; end
class Cv; prepend Pv; def who; 'Cv'; end; end
t('Cv.respond_to?(:helper)') { Cv.respond_to?(:helper) }
t('Pv.helper')               { Pv.helper }

puts
puts '--- Comparable-style prepend over a builtin ---'
module Pstr; def upcase; "<#{super}>"; end; end
class MyStr < String; prepend Pstr; end
t('MyStr.new("ab").upcase') { MyStr.new('ab').upcase }

puts
puts '--- super with explicit args and blocks ---'
module Pb1
  def each(&b); super(&b); end
  def take2(a, b); super(a * 2, b * 2); end
end
class Cb1
  prepend Pb1
  def take2(a, b); [a, b]; end
  def each; yield 1; yield 2; end
end
t('Cb1.new.take2(1,2)') { Cb1.new.take2(1, 2) }
t('Cb1 each')           { r = []; Cb1.new.each { |v| r << v }; r }

puts
puts '--- ancestors after prepending twice in different orders ---'
module Q1; end
module Q2; end
class Cq
  prepend Q1
  include Q2
  prepend Q2
end
t('Cq.ancestors[0,4]') { names(Cq.ancestors[0, 4]) }

puts
puts 'DONE'
