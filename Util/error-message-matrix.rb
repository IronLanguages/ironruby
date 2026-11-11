# Differential matrix for the wording of error messages: Ruby 3.4 quotes a name 'like this'
# rather than `like this', and a few messages say more than the name.
#
#   ruby    Util/error-message-matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/error-message-matrix.rb > /tmp/ir.txt
#   diff /tmp/mri.txt /tmp/ir.txt
#
# Object addresses are printed as 0x: CRuby's are machine addresses, IronRuby's are object ids.

def t(label)
  print label.ljust(40), ' => '
  begin
    puts yield.inspect.gsub(/0x\h+/, '0x')
  rescue Exception => e
    puts "#{e.class}: #{e.message.gsub(/0x\h+/, '0x')}"
  end
end

class Target; end
module Mixin; end

# ---------------------------------------------------------------- method missing

t("1.foo") { 1.foo }
t("nil.foo") { nil.foo }
t("true.foo") { true.foo }
t("Target.new.hello") { Target.new.hello }
t("Target.hello") { Target.hello }
t("Mixin.hello") { Mixin.hello }
t("foo_bar (vcall)") { foo_bar }
t("self.foo_bar") { self.foo_bar }
t("send(:hello)") { Target.new.send(:hello) }
t("public_send(:hello)") { Target.new.public_send(:hello) }
t("BasicObject.new.inspect") { BasicObject.new.inspect }
t(":sym.unknown") { :sym.unknown }
t("anonymous class instance") { Class.new.new.foo }
t("object with a singleton class") { o = Object.new; o.singleton_class; o.foo }
t("private method") { Class.new { private def x = 1 }.new.x }
t("protected method") { Class.new { protected def x = 1 }.new.x }
t("private Kernel method") { 1.public_send(:puts) }
t("NoMethodError.new") { raise NoMethodError.new("x", :y) }

# ---------------------------------------------------------------- super

class SuperTarget
  def m(a) = super
  def self.cm = super
end
def top_level_super = super
t("super, instance") { SuperTarget.new.m(1) }
t("super, class method") { SuperTarget.cm }
t("super, main") { top_level_super }
t("super, singleton") { o = Object.new; def o.s = super; o.s }
t("super, nil") { class NilClass; def nil_super = super; end; nil.nil_super }
t("super, Integer") { class Integer; def int_super = super; end; 1.int_super }
t("super, anonymous module method") { Module.new { def self.r = super }.r }
t("super, name and receiver") do
  SuperTarget.new.m(1)
rescue NoMethodError => e
  [e.name, e.receiver.class]
end

# ---------------------------------------------------------------- reflection

t("method") { Target.new.method(:hello) }
t("public_method, private") { 1.public_method(:puts) }
t("public_method, missing") { 1.public_method(:nope) }
t("instance_method") { Target.instance_method(:hello) }
t("instance_method, module") { Comparable.instance_method(:nope) }
t("instance_method, object's singleton") { Object.new.singleton_class.instance_method(:nope) }
t("public_instance_method, private") { Class.new { private def x = 1 }.public_instance_method(:x) }
t("public_instance_method, protected") { Class.new { protected def x = 1 }.public_instance_method(:x) }
t("public_instance_method, module") { Kernel.public_instance_method(:puts) }
t("public_instance_method, missing") { Target.public_instance_method(:nope) }
t("undef_method") { Class.new { undef_method :nope } }
t("undef_method, defined") { Class.new { undef_method :to_s } }
t("undef_method, singleton class") { Target.singleton_class.send(:undef_method, :nope) }
t("undef") { Target.class_eval "undef nope" }
t("alias_method") { Class.new { alias_method :a, :nope } }
t("alias_method, module") { Module.new { alias_method :a, :nope } }
t("alias") { Target.class_eval "alias a nope" }
t("remove_method") { Class.new { remove_method :nope } }
t("remove_method, named") { Target.send(:remove_method, :to_s) }
t("singleton_method") { Object.new.singleton_method(:nope) }
t("singleton_method, String") { "abc".singleton_method(:nope) }
t("singleton_method, class") { Target.singleton_method(:nope) }
t("singleton_method, module") { Comparable.singleton_method(:nope) }
t("singleton_method, nil") { nil.singleton_method(:nope) }
t("singleton_method, main") { singleton_method(:nope) }
t("ruby2_keywords") { Target.send(:ruby2_keywords, :nope) }
t("ruby2_keywords, module") { Comparable.send(:ruby2_keywords, :nope) }
t("ruby2_keywords, singleton class") { Target.singleton_class.send(:ruby2_keywords, :nope) }
t("attr_accessor") { Class.new { attr_accessor :"foo?" } }
t("attr_reader") { Class.new { attr_reader "1a" } }
t("attached_object") { String.attached_object }
t("attached_object, anonymous") { Class.new.attached_object }

# ---------------------------------------------------------------- variables and constants

t("class_variable_get") { Target.class_variable_get(:x) }
t("class_variable_get, missing") { Target.class_variable_get(:@@x) }
t("class_variable_set") { Target.class_variable_set("x", 1) }
t("class_variable_defined?") { Target.class_variable_defined?(:@x) }
t("remove_class_variable, bad name") { Target.send(:remove_class_variable, :x) }
t("remove_class_variable, missing") { Target.send(:remove_class_variable, :@@x) }
t("@@x") { @@x }
t("instance_variable_get") { 1.instance_variable_get("bad") }
t("const_get") { Object.const_get(:Nope) }
t("Nope") { Nope }
t("Target::Nope") { Target::Nope }

# ---------------------------------------------------------------- conversions

t("String#* bignum") { "a" * (2**64) }
t("String#[] bignum") { "abc"[2**64, 1] }
t("round(2**31)") { 1.round(2**31) }
t("round(-2**31 - 1)") { 1.round(-2**31 - 1) }
t("round(2**63 - 1)") { 1.round(2**63 - 1) }
t("round(2**63)") { 1.round(2**63) }
t("Float#floor(2**64)") { 1.5.floor(2**64) }
t("Float#ceil(-2**40)") { 1.5.ceil(-2**40) }
t("[].fetch") { [].fetch(5) }
t("{}.fetch") { {}.fetch(:k) }
t("Integer()") { Integer("zz") }

# ---------------------------------------------------------------- libraries

t("Signal.trap") { Signal.trap(:FOO) {} }
t("Process.kill") { Process.kill(:FOO, $$) }
class NoLoad; end
t("Marshal _load") { Marshal.load("\x04\bu:\vNoLoad\x06x") }
class NoMarshalLoad; def marshal_dump = 1; end
t("Marshal marshal_load") { Marshal.load(Marshal.dump(NoMarshalLoad.new)) }
require 'socket'
t("IPSocket.getaddress(2**64)") { IPSocket.getaddress(2**64) }
t("Socket.getaddrinfo(2**64)") { Socket.getaddrinfo(2**64, nil) }
t("BasicSocket#shutdown(5)") { s = Socket.new(:INET, :STREAM); begin; s.shutdown(5); ensure; s.close; end }

# A library's class or module whose name is already taken by something else. These leave
# the library unloaded, so they come last.
t("Zlib, a class") { eval "class Zlib; end"; require 'zlib' }
t("StringIO, an Integer") { Object.const_set(:StringIO, 1); require 'stringio' }
