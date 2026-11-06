# Generates the CLR subclasses a NativeAOT build needs for Ruby subclasses of built-in classes.
#
#   IR_CONFIG=R2R ./ir.sh Util/aot/gen-subclasses.rb
#
# A Ruby class that derives from a class backed by a CLR type (StandardError is
# System.SystemException, KeyError derives IndexError, ...) gets a CLR subclass of that type which
# implements IRubyObject. RubyTypeDispenser emits it with Reflection.Emit at run time; without dynamic
# code (NativeAOT) that throws PlatformNotSupportedException. This script writes the same types as C#
# source, one per CLR base type of a library class, and RubyTypeDispenser uses them when it cannot
# emit. They implement what RubyTypeBuilder emits except the virtual-method overrides (a Ruby method
# does not override a CLR virtual for CLR callers), ISerializable and ICustomTypeDescriptor.
#
# Output: Src/Ruby/Compiler/Generation/AotSubclasses.Generated.cs (types of IronRuby and the BCL)
#         Src/Libraries/AotSubclasses.Generated.cs (types of IronRuby.Libraries)
root = File.expand_path("../..", __dir__)
BF = System::Reflection::BindingFlags
ruby_asm = System::Type.get_type("IronRuby.Runtime.RubyContext, IronRuby").assembly
lib_asm = System::Type.get_type("IronRuby.Builtins.EOFError, IronRuby.Libraries").assembly
class_attr = System::Type.get_type("IronRuby.Runtime.RubyClassAttribute, IronRuby")
irubyobject = System::Type.get_type("IronRuby.Runtime.IRubyObject, IronRuby")
ruby_class = System::Type.get_type("IronRuby.Builtins.RubyClass, IronRuby")
ruby_context = System::Type.get_type("IronRuby.Runtime.RubyContext, IronRuby")
exception = System::Exception.to_clr_type
# RubyTypeDispenser's hand-written subclasses
existing = %w[System.Object IronRuby.Builtins.MutableString IronRuby.Builtins.Proc IronRuby.Builtins.RubyRegex
  IronRuby.Builtins.Range IronRuby.Builtins.Hash IronRuby.Builtins.RubyArray IronRuby.Builtins.MatchData IronRuby.Builtins.RubyIO]

def cs_name(t)
  return cs_name(t.get_element_type) + "[]" if t.is_array
  name = t.full_name.to_s.sub(/`\d+.*/, "").gsub("+", ".")
  if t.is_generic_type
    name += "<" + t.get_generic_arguments.map { |a| cs_name(a) }.join(", ") + ">"
  end
  "global::" + name
end

def literal(v)
  case v
  when nil then "null"
  when true then "true"
  when false then "false"
  when Integer then v.to_s
  when System::String, String then v.to_s.inspect
  else raise "default value #{v.inspect}"
  end
end

bases = []
[ruby_asm, lib_asm].each do |asm|
  asm.get_types.each do |t|
    attrs = t.get_custom_attributes(class_attr, false)
    next if attrs.length == 0
    attr = attrs[0]
    base = attr.extends || t
    next if base.is_sealed || base.is_abstract || base.is_value_type || base.is_interface || base.contains_generic_parameters
    next if irubyobject.is_assignable_from(base) || existing.include?(base.full_name.to_s)
    next unless base.is_public || base.is_nested_public
    bases << base unless bases.include?(base)
  end
end

files = { ruby_asm => [], lib_asm => [] }
bases.sort_by { |b| b.full_name.to_s }.each do |base|
  home = base.assembly == lib_asm ? lib_asm : ruby_asm
  is_exception = exception.is_assignable_from(base)
  msg_ctor = base.get_constructor(System::Array[System::Type].new([System::String.to_clr_type]))
  ctors = []
  base.get_constructors(BF.Public | BF.NonPublic | BF.Instance).each do |c|
    next unless c.is_public || c.is_family || c.is_family_or_assembly
    ps = c.get_parameters.to_a
    next if ps.length == 2 && ps[0].parameter_type.name == "SerializationInfo"
    next if c.get_custom_attributes(System::ObsoleteAttribute.to_clr_type, false).length > 0
    class_idx = ps.index { |p| p.parameter_type == ruby_class }
    ctx_idx = ps.index { |p| p.parameter_type == ruby_context }
    adjust = class_idx ? 0 : (ctx_idx ? 1 : 2)
    params = []
    params << "global::IronRuby.Builtins.RubyClass/*!*/ rubyClass" if adjust == 2
    args = []
    ps.each_with_index do |p, i|
      if i == class_idx
        params << "global::IronRuby.Builtins.RubyClass/*!*/ rubyClass"; args << "rubyClass"
      elsif i == ctx_idx && adjust == 1
        params << "global::IronRuby.Builtins.RubyClass/*!*/ rubyClass"; args << "global::IronRuby.Runtime.RubyOps.GetContextFromModule(rubyClass)"
      else
        decl = "#{cs_name(p.parameter_type)} p#{i}"
        decl = "params " + decl if p.is_defined(System::ParamArrayAttribute.to_clr_type, false)
        decl += " = " + literal(p.default_value) if p.has_default_value
        params << decl; args << "p#{i}"
      end
    end
    if adjust == 2 && ps.empty? && is_exception && msg_ctor && !msg_ctor.is_private
      args = ["global::IronRuby.Runtime.RubyOps.GetDefaultExceptionMessage(rubyClass)"]
    end
    sig = params.map { |s| s.sub(/ p\d+.*| rubyClass.*/, "") }
    old = ctors.index { |o| o[:sig] == sig }
    entry = { sig: sig, adjust: adjust, params: params, args: args }
    if old
      ctors[old] = entry if ctors[old][:adjust] > adjust
    else
      ctors << entry
    end
  end
  next if ctors.empty?
  name = base.full_name.to_s.gsub(/[.+`]/, "_") + "_Subclass"
  to_string = base.get_method("ToString", System::Type.EmptyTypes).declaring_type == System::Object.to_clr_type ?
    "global::IronRuby.Runtime.RubyOps.ObjectToString(this)" : "base.ToString()"
  s = +""
  s << "    [global::System.Diagnostics.DebuggerTypeProxy(typeof(global::IronRuby.Builtins.RubyObject.DebugView))]\n" if false
  s << "    public sealed class #{name} : #{cs_name(base)}, IRubyObject, IRubyDynamicMetaObjectProvider, IRubyType {\n"
  s << "        private RubyInstanceData _instanceData;\n"
  s << "        private RubyClass/*!*/ _immediateClass;\n\n"
  ctors.each do |c|
    s << "        public #{name}(#{c[:params].join(', ')})\n"
    s << "            : base(#{c[:args].join(', ')}) {\n"
    s << "            _immediateClass = rubyClass;\n"
    s << "        }\n\n"
  end
  s << <<-CS
        public RubyClass/*!*/ ImmediateClass { get { return _immediateClass; } set { _immediateClass = value; } }
        public RubyInstanceData TryGetInstanceData() { return _instanceData; }
        public RubyInstanceData/*!*/ GetInstanceData() { return RubyOps.GetInstanceData(ref _instanceData); }
        public bool IsFrozen { get { return RubyOps.IsObjectFrozen(_instanceData); } }
        public void Freeze() { RubyOps.FreezeObject(ref _instanceData); }
        public int BaseGetHashCode() { return base.GetHashCode(); }
        public bool BaseEquals(object other) { return base.Equals(other); }
        public string/*!*/ BaseToString() { return #{to_string}; }
        DynamicMetaObject/*!*/ IDynamicMetaObjectProvider.GetMetaObject(Expression/*!*/ parameter) { return RubyOps.GetMetaObject(this, parameter); }
    }

  CS
  files[home] << [base, name, s]
end

header = <<-CS
// Generated by Util/aot/gen-subclasses.rb: do not edit. Regenerate after adding a library class
// backed by a new CLR type.
//
// RubyTypeDispenser emits a subclass like these with Reflection.Emit for each Ruby subclass of a
// class backed by a CLR type; without dynamic code (NativeAOT) it uses these instead.
using System.Dynamic;
using System.Linq.Expressions;
using IronRuby.Builtins;
using IronRuby.Compiler.Generation;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;

CS
{ ruby_asm => ["Src/Ruby/Compiler/Generation/AotSubclasses.Generated.cs", "IronRuby.Compiler.Generation", "AotSubclasses", "internal"],
  lib_asm => ["Src/Libraries/AotSubclasses.Generated.cs", "IronRuby.Builtins", "LibraryAotSubclasses", "internal"] }.each do |asm, (path, ns, cls, vis)|
  out = +header
  out << "namespace #{ns} {\n"
  out << "    #{vis} static class #{cls} {\n"
  if asm == lib_asm
    out << "        [global::System.Runtime.CompilerServices.ModuleInitializer]\n"
    out << "        internal static void Initialize() {\n"
    out << "            if (global::System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) {\n"
    out << "                return;\n"
    out << "            }\n"
  else
    out << "        internal static void Register(global::System.Action<global::System.Type, global::System.Type> add) {\n"
  end
  files[asm].each do |base, name, _|
    reg = asm == lib_asm ? "global::IronRuby.Compiler.Generation.PrebuiltSubclasses.Register" : "add"
    out << "            #{reg}(typeof(#{cs_name(base)}), typeof(#{name}));\n"
  end
  out << "        }\n"
  out << "    }\n\n"
  files[asm].each { |_, _, s| out << s }
  out << "}\n"
  File.open(File.join(root, path), "wb") { |f| f.write(out.gsub("\n", "\r\n")) }
  puts "#{path}: #{files[asm].length} types"
end
