# Differential matrix for Module#prepend.
# Run under CRuby and under ./ir.sh and diff the output.
#
#   ruby Util/prepend-matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/prepend-matrix.rb > /tmp/ir.txt
#   diff /tmp/mri.txt /tmp/ir.txt

def t(label)
  print label.ljust(46), ' => '
  begin
    puts yield.inspect
  rescue Exception => e
    puts "#{e.class}: #{e.message}"
  end
end

def names(mods)
  mods.map { |m| m.to_s }
end

puts '--- respond_to ---'
t('Module#prepend defined')          { Module.private_method_defined?(:prepend) || Module.method_defined?(:prepend) }
t('Module#prepend_features defined') { Module.private_method_defined?(:prepend_features) }
t('Module#prepended defined')        { Module.private_method_defined?(:prepended) }
t('Module#prepended_modules')        { Module.method_defined?(:prepended_modules) }

puts
puts '--- basic ancestors ---'
module P1; end
class C1; prepend P1; end
t('C1.ancestors[0,2]')        { names(C1.ancestors[0, 2]) }
t('C1.include?(P1)')          { C1.include?(P1) }
t('C1.prepended_modules')     { names(C1.prepended_modules) rescue 'n/a' }
t('C1.included_modules[0]')   { C1.included_modules[0].to_s }
t('P1 < C1')                  { P1 < C1 }
t('C1 < P1')                  { C1 < P1 }

puts
puts '--- method wins + super ---'
module P2
  def who; "P2(#{defined?(super) ? super : 'no-super'})"; end
end
class C2
  prepend P2
  def who; 'C2'; end
end
t('C2.new.who')                    { C2.new.who }
t('C2.instance_method(:who).owner') { C2.instance_method(:who).owner.to_s }
t('C2.new.method(:who).owner')     { C2.new.method(:who).owner.to_s }
t('C2.method_defined?(:who)')      { C2.method_defined?(:who) }
t('C2.instance_methods(false)')    { C2.instance_methods(false).sort.inspect }
t('C2 ancestors')                  { names(C2.ancestors[0, 2]) }

puts
puts '--- prepend before def ---'
module P3
  def who; "P3->#{super}"; end
end
class C3
  def who; 'C3'; end
  prepend P3
end
t('C3.new.who')  { C3.new.who }

puts
puts '--- define method after prepend (cache invalidation) ---'
module P4; end
class C4
  prepend P4
  def who; 'C4'; end
end
o4 = C4.new
t('before P4#who: o4.who') { o4.who }
module P4
  def who; "P4->#{super}"; end
end
t('after  P4#who: o4.who') { o4.who }

puts
puts '--- prepend after instances exist ---'
class C5
  def who; 'C5'; end
end
o5 = C5.new
t('pre-prepend o5.who') { o5.who }
module P5
  def who; "P5->#{super}"; end
end
class C5; prepend P5; end
t('post-prepend o5.who')  { o5.who }
t('post-prepend C5.new.who') { C5.new.who }

puts
puts '--- interaction with include ---'
module I6; def who; "I6->#{defined?(super) ? super : 'x'}"; end; end
module P6; def who; "P6->#{super}"; end; end
class C6
  include I6
  prepend P6
  def who; "C6->#{super}"; end
end
t('C6.new.who')  { C6.new.who }
t('C6.ancestors[0,3]') { names(C6.ancestors[0, 3]) }

puts
puts '--- multiple prepends, order ---'
module Pa; end
module Pb; end
class C7
  prepend Pa
  prepend Pb
end
t('C7.ancestors[0,3]') { names(C7.ancestors[0, 3]) }
class C8
  prepend Pa, Pb
end
t('C8 prepend Pa,Pb ancestors[0,3]') { names(C8.ancestors[0, 3]) }

puts
puts '--- re-prepend is a no-op ---'
module P9; end
module Pz; end
class C9
  prepend P9
  prepend Pz
  prepend P9
end
t('C9.ancestors[0,3]') { names(C9.ancestors[0, 3]) }

puts
puts '--- nested: prepending a module that prepends ---'
module Na; end
module Nb; prepend Na; end
class C10; prepend Nb; end
t('Nb.ancestors')      { names(Nb.ancestors) }
t('C10.ancestors[0,3]') { names(C10.ancestors[0, 3]) }

puts
puts '--- module including a module that has prepends ---'
module Ia; def who; 'Ia'; end; end
module Ib; prepend Ia; def who; "Ib->#{super}"; end; end
class C11; include Ib; end
t('C11.ancestors[0,3]') { names(C11.ancestors[0, 3]) }
t('C11.new.who')        { C11.new.who }

puts
puts '--- prepend to a module, then include it ---'
module Pm; def who; "Pm->#{defined?(super) ? super : 'x'}"; end; end
module Mm; def who; 'Mm'; end; end
class C12; include Mm; end
t('C12.new.who (before prepend)') { C12.new.who }
module Mm; prepend Pm; end
t('Mm.ancestors')                 { names(Mm.ancestors) }
t('C12.ancestors[0,3]')           { names(C12.ancestors[0, 3]) }
t('C12.new.who (after prepend)')  { C12.new.who }

puts
puts '--- callbacks ---'
$log = []
module Cb
  def self.prepended(base); $log << "prepended:#{base}"; end
  def self.included(base);  $log << "included:#{base}"; end
end
class C13; prepend Cb; end
class C14; include Cb; end
t('callback log') { $log }

puts
puts '--- prepend_features ---'
$pf = []
module Pf
  def self.prepend_features(base); $pf << "prepend_features:#{base}"; super; end
end
class C15; prepend Pf; end
t('prepend_features log')   { $pf }
t('C15.ancestors[0,2]')     { names(C15.ancestors[0, 2]) }

$pf2 = []
module Pf2
  def self.prepend_features(base); $pf2 << "pf2:#{base}"; end  # no super => not inserted
end
class C16; prepend Pf2; end
t('pf2 log')                { $pf2 }
t('C16.ancestors[0,2]')     { names(C16.ancestors[0, 2]) }

puts
puts '--- errors ---'
t('prepend a Class')       { Class.new.prepend(String) }
t('prepend nil')           { Class.new.prepend(nil) }
t('prepend self')          { m = Module.new; m.prepend(m) }
t('prepend no args')       { Class.new.prepend }
t('prepend returns self')  { c = Class.new; m = Module.new; (c.prepend(m) == c) }
t('prepend is public?')    { Module.public_method_defined?(:prepend) }
t('prepend_features vis')  { Module.private_method_defined?(:prepend_features) }

puts
puts '--- singleton / extend ---'
module Ps; def who; "Ps->#{super}"; end; end
class C17
  def self.who; 'C17.who'; end
  class << self; prepend Ps; end
end
t('C17.who')  { C17.who }

o = Object.new
def o.who; 'sing'; end
module Po; def who; "Po->#{super}"; end; end
o.singleton_class.prepend(Po)
t('obj singleton prepend') { o.who }

puts
puts '--- prepend to Object / builtins ---'
module Ph; def to_s; "Ph->#{super}"; end; end
class Hp; end
Hp.prepend(Ph)
t('Hp.new.to_s =~ Ph')  { !!(Hp.new.to_s =~ /\APh->/) }

puts
puts '--- prepended module methods visible on subclass ---'
module Pq; def who; "Pq->#{super}"; end; end
class Base18; def who; 'Base18'; end; end
class Sub18 < Base18; end
Base18.prepend(Pq)
t('Sub18.new.who')      { Sub18.new.who }
t('Sub18.ancestors[0,3]') { names(Sub18.ancestors[0, 3]) }

puts
puts '--- ancestors of prepended module itself ---'
t('P1.ancestors')  { names(P1.ancestors) }

puts
puts '--- is_a? / kind_of? ---'
t('C1.new.is_a?(P1)')     { C1.new.is_a?(P1) }
t('C1.new.kind_of?(P1)')  { C1.new.kind_of?(P1) }

puts
puts '--- alias/super through prepend ---'
module Pr
  def size; super * 2; end
end
class Ar18 < Array; prepend Pr; end
t('Ar18[1,2,3].size') { Ar18.new([1,2,3]).size rescue Ar18[1,2,3].size }

puts
puts '--- prepended_modules details ---'
module Pd1; end
module Pd2; end
class C19
  prepend Pd1
  prepend Pd2
  include Pd1
end
t('C19.prepended_modules')  { names(C19.prepended_modules) rescue 'n/a' }
t('C19.ancestors[0,3]')     { names(C19.ancestors[0, 3]) }
t('Module.new.prepended_modules') { Module.new.prepended_modules.inspect rescue 'n/a' }

puts
puts 'DONE'
