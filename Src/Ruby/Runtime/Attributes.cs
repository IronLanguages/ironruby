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

using System;
using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Scripting.Utils;
using IronRuby.Builtins;

namespace IronRuby.Runtime {

    public abstract class RubyAttribute : Attribute {
    }

    /// <summary>
    /// Applied to assemblies containing Ruby library methods.
    /// Specifies an initializer for the library, which is a type that publishes RubyModules and RubyClasses defined in the assembly.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class RubyLibraryAttribute : Attribute {
        private Type/*!*/ _initializer;

        public Type/*!*/ Initializer {
            get { return _initializer; }
        }

        public RubyLibraryAttribute(Type/*!*/ initializer) {
            ContractUtils.RequiresNotNull(initializer, "initializer");
            _initializer = initializer;
        }
    }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Class | AttributeTargets.Interface | AttributeTargets.Method, AllowMultiple = true)]
    public sealed class RubyConstantAttribute : RubyAttribute {
        private readonly string _name;

        public string Name { get { return _name; } }

        public RubyConstantAttribute() {
        }

        public RubyConstantAttribute(string/*!*/ name) {
            ContractUtils.RequiresNotNull(name, "name");
            _name = name;
        }
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public sealed class RubyMethodAttribute : RubyAttribute {
        private readonly string/*!*/ _name;
        private readonly RubyMethodAttributes _methodAttributes;

        public string/*!*/ Name {
            get { return _name; }
        }

        public RubyMethodAttributes MethodAttributes {
            get { return _methodAttributes; }
        }
        
        public RubyMethodAttribute(string/*!*/ name)
            : this(name, RubyMethodAttributes.Default) {
        }

        public RubyMethodAttribute(string/*!*/ name, RubyMethodAttributes methodAttributes)
            : base() {
            ContractUtils.RequiresNotNull(name, "name");

            _name = name;
            _methodAttributes = methodAttributes;
        }
    }

    /// <summary>
    /// Marks a library method that only raises NotImplementedError on this platform, like MRI's
    /// rb_f_notimplement: #respond_to? answers false for it although the method is defined.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class RubyNotImplementedAttribute : Attribute {
        /// <summary>
        /// Not implemented on Windows only, where MRI defines it as the stub too (IO#ioctl); the
        /// same build runs on Unix, where the method works.
        /// </summary>
        public bool OnWindowsOnly { get; set; }

        internal bool AppliesHere {
            get { return !OnWindowsOnly || System.IO.Path.DirectorySeparatorChar == '\\'; }
        }
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class RubyConstructorAttribute : RubyAttribute {
        public RubyConstructorAttribute() {
        }
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface, AllowMultiple = false)]
    public class RubyModuleAttribute : RubyAttribute {
        private readonly string _name;
        private ModuleRestrictions? _restrictions; 

        /// <summary>
        /// If an extension type doesn't specify Ruby name its name is inferred from the CLR 
        /// non-generic qualified name of the type being extended. No constant is added to the Object class.
        /// 
        /// Otherwise, the qualified name is the qualified name of the outer Ruby type within which the type is nested
        /// or the simple name of the type only if it is not a nested type (non-Ruby types and namespaces are ignored).
        /// The constant defined for such module/class is set on the declaring Ruby type or on Object if it is not nested in a Ruby type.
        /// 
        /// Examples:
        /// 1)
        /// namespace Ruby.Extensions {
        ///     [RubyClass(Extends = System.Collections.Generic.IDictionary{K,V}]
        ///     public static class IDictionaryOps {}
        /// }
        /// 
        /// Ruby name is "System::Collections::Generic::IDictionary"
        /// 
        /// 2)
        /// namespace Ruby.Builtins {
        ///     [RubyClass("Exception", Extends = typeof(Exception))]
        ///     public static class ExceptionOps {}
        /// }
        /// 
        /// Ruby name is "Exception". Constant "Exception" is defined on Object.
        /// 
        /// 3) 
        /// namespace Ruby.MyLibrary {
        ///     public class MyType {
        ///         [RubyModule]
        ///         public class MyClass {
        ///             [RubyClass("Foo")]
        ///             public class Bar {
        ///                [RubyClass]
        ///                public class Baz {
        ///                }
        ///                
        ///                [RubyClass("Goo", Extends = typeof(int))]
        ///                public class Gaz {
        ///                }
        ///                
        ///                [RubyClass(Extends = typeof(int))]
        ///                public class IntOps {
        ///                  
        ///                    [RubyClass]
        ///                    public class C {
        ///                    }
        ///                }
        ///             }
        ///         }
        ///     }
        /// }
        /// 
        /// names are (full CLR name -> Ruby name):
        /// Ruby.MyLibrary.MyType.MyClass            -> "MyClass" 
        /// Ruby.MyLibrary.MyType.MyClass.Bar        -> "MyClass::Foo" 
        /// Ruby.MyLibrary.MyType.MyClass.Bar.Baz    -> "MyClass::Foo::Baz" 
        /// Ruby.MyLibrary.MyType.MyClass.Bar.Gaz    -> "MyClass::Foo::Goo" 
        /// Ruby.MyLibrary.MyType.MyClass.Bar.IntOps -> "System::Int32"
        /// Ruby.MyLibrary.MyType.MyClass.Bar.IntOps -> "System::Int32::C"
        /// </summary>
        public string Name {
            get { return _name; }
        }
        
        /// <summary>
        /// The CLR type extended by this RubyModule.
        /// </summary>
        /// <remarks>
        /// A Ruby class/module defined by this attribute can either <c>extend</c> an existing CLR type, specified by this property, 
        /// or be <c>self-contained</c>, in which case the value of <see cref="Extends"/> property is <c>null</c>. 
        /// A self-contained class/module has <see cref="ModuleRestriction.NoUnderlyingType"/> set by default.
        /// </remarks>
        public Type Extends { get; set; }

        public Type DefineIn { get; set; }

        public ModuleRestrictions Restrictions {
            get { return _restrictions ?? ModuleRestrictions.None; }
            set { _restrictions = value; }
        }

        public ModuleRestrictions GetRestrictions(bool builtin) {
            return _restrictions ?? (
                (builtin ? ModuleRestrictions.Builtin : ModuleRestrictions.None) | 
                (Extends == null ? ModuleRestrictions.NoUnderlyingType : ModuleRestrictions.None)
            );
        }

        public RubyModuleAttribute() {
        }

        public RubyModuleAttribute(string/*!*/ name) {
            ContractUtils.RequiresNotEmpty(name, "name");
            _name = name;
        }
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public sealed class RubySingletonAttribute : RubyModuleAttribute {
        public RubySingletonAttribute() : base() {
        }
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class RubyClassAttribute : RubyModuleAttribute {
        private Type/*!*/ _inherits;

        public Type/*!*/ Inherits {
            get { return _inherits; }
            set { ContractUtils.RequiresNotNull(value, "value"); _inherits = value; }
        }

        public RubyClassAttribute() {
        }

        public RubyClassAttribute(string/*!*/ name) 
            : base(name) {
        }
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public sealed class RubyExceptionAttribute : RubyClassAttribute {
        public RubyExceptionAttribute(string/*!*/ name) 
            : base(name) {
        }
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public sealed class IncludesAttribute : Attribute {
        private readonly Type/*!*/[]/*!*/ _types;
        private bool _copy; 

        public Type/*!*/[]/*!*/ Types {
            get { return _types; }
        }

        public bool Copy {
            get { return _copy; }
            set { _copy = value; }
        }

        public IncludesAttribute() {
            _types = ReflectionUtils.EmptyTypes;
        }

        public IncludesAttribute(params Type[]/*!*/ types) {
            ContractUtils.RequiresNotNullItems(types, "types");
            _types = types;
        }
    }

    /// <summary>
    /// Hides CLR method when called using give name. 
    /// Doesn't apply on calls using a different (mangled/unmangled) name.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface, AllowMultiple = true)]
    public sealed class HideMethodAttribute : Attribute {
        private readonly string/*!*/ _name;
        private bool _isStatic;

        public string/*!*/ Name { get { return _name; } }
        public bool IsStatic { get { return _isStatic; } set { _isStatic = value; } }
        
        public HideMethodAttribute(string/*!*/ name) {
            ContractUtils.RequiresNotNull(name, "name");
            _name = name;
        }
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface, AllowMultiple = true)]
    public sealed class UndefineMethodAttribute : Attribute {
        private readonly string/*!*/ _name;
        private bool _isStatic;

        public string/*!*/ Name { get { return _name; } }
        public bool IsStatic { get { return _isStatic; } set { _isStatic = value; } }

        public UndefineMethodAttribute(string/*!*/ name) {
            ContractUtils.RequiresNotNull(name, "name");
            _name = name;
        }
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface, AllowMultiple = true)]
    public sealed class AliasMethodAttribute : Attribute {
        private readonly string/*!*/ _oldName, _newName;
        private bool _isStatic;

        public string/*!*/ NewName { get { return _newName; } }
        public string/*!*/ OldName { get { return _oldName; } }
        public bool IsStatic { get { return _isStatic; } set { _isStatic = value; } }

        public AliasMethodAttribute(string/*!*/ newName, string/*!*/ oldName) {
            ContractUtils.RequiresNotNull(newName, "newName");
            ContractUtils.RequiresNotNull(oldName, "oldName");
            _newName = newName;
            _oldName = oldName;
        }
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class RubyStackTraceHiddenAttribute : Attribute {
    }

    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false)]
    public class DefaultProtocolAttribute : Attribute {
    }

    /// <summary>
    /// Marks the parameter of a library method that receives its keyword arguments (as a Hash). It is not one of the
    /// positional parameters MRI counts: a call whose positional arguments - not counting a trailing keyword Hash - are
    /// more or fewer than the method takes raises "wrong number of arguments" with the count MRI gives
    /// (Integer() is "given 0, expected 1..2", not "expected 1..3").
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false)]
    public sealed class KeywordsAttribute : Attribute {
    }

    /// <summary>
    /// A library method's way past the keyword Hash for a call that passes literal keywords (`Integer(s, exception: false)'):
    /// the call's positional arguments and keyword values, in the call's order, without the Hash and the overload resolution
    /// of the ordinary call. Answers <see cref="IronRuby.Runtime.Jit.JitKeywordCall.NoDirect"/> for whatever it does not answer
    /// itself - in particular anything that would raise - and the call is then made the ordinary way, which raises as it
    /// always did, from where it always did.
    /// </summary>
    public delegate object LibraryKeywordEntry(object self, object p0, object p1, object k0, object k1, object k2);

    /// <summary>
    /// Marks a library method overload that has a <see cref="LibraryKeywordEntry"/>: <see cref="Factory"/> names a public
    /// static method of the same class, LibraryKeywordEntry Factory(RubyContext context, int positionalCount, string[] keywords),
    /// which makes the entry for a call with that many positional arguments and those keywords, or answers null for a call it
    /// does not take. The JIT's keyword call sites (JitKeywordCall) use it once the method name resolves to this method.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class LibraryKeywordEntryAttribute : Attribute {
        public string/*!*/ Factory { get; }

        public LibraryKeywordEntryAttribute(string/*!*/ factory) {
            Factory = factory;
        }
    }

    /// <summary>
    /// Marks a library method (an overload) that sees the keyword arguments of its call as keyword arguments: a trailing
    /// Hash the call passed as keywords reaches it still marked (Hash.IsKeywordArguments), so that it can tell `f(a: 1)'
    /// from `f({a: 1})' or pass them on as keywords - Kernel#send, Proc#call, Struct#initialize (MRI's rb_keyword_given_p
    /// and RB_PASS_CALLED_KEYWORDS). An overload with a <see cref="KeywordsAttribute"/> parameter does too.
    /// Every other library method gets the hash unmarked, as a Ruby method without keyword parameters does: for it the
    /// hash is a positional argument, which it may keep (Array#push), and a kept hash that said it was keyword arguments
    /// would turn into the keywords of whatever call it is passed to later.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor, AllowMultiple = false)]
    public sealed class ReceivesKeywordsAttribute : Attribute {
    }

    /// <summary>
    /// Marks a hidden string parameter of a library method that receives the name of the method being called - the
    /// name the method was defined with, which for an alias is the aliased method's (MRI's rb_frame_this_func, which
    /// is what <c>[1].collect</c> without a block names its Enumerator by). A method registered under several names
    /// ([RubyMethod("map")], [RubyMethod("collect")]) learns which one was called. Opt-in: methods without it pay nothing.
    /// The parameter goes after RubyCallSiteStorage and RubyContext/RubyScope parameters and before the BlockParam.
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false)]
    public sealed class CalledNameAttribute : Attribute {
        internal static bool IsDefinedOn(ParameterInfo/*!*/ parameter) {
            return parameter.ParameterType == typeof(string) && parameter.IsDefined(typeof(CalledNameAttribute), false);
        }
    }

    /// <summary>
    /// Marks a library method that does nothing but call another of the same type with the same parameters - named by
    /// <see cref="ClrName"/> - under another Ruby name. MRI defines `collect' and `map' as one C function under two names,
    /// and its backtrace names the frame by the name called; a backtrace here names a library frame by its CLR method, so
    /// each name that can show in one has a method of its own. To Ruby the two are still one method (UnboundMethod#==).
    /// BasicObject#equal? also uses it: MRI defines it and BasicObject#== with one C function, but here #== of a CLR-backed
    /// object reaches the CLR Equals (a String subclass compares contents), which #equal? must not.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class SameRubyMethodAttribute : Attribute {
        private readonly string/*!*/ _clrName;

        public string/*!*/ ClrName { get { return _clrName; } }

        public SameRubyMethodAttribute(string/*!*/ clrName) {
            ContractUtils.RequiresNotNull(clrName, "clrName");
            _clrName = clrName;
        }

        private static readonly ConcurrentDictionary<MethodBase, MethodBase>/*!*/ _targets = new ConcurrentDictionary<MethodBase, MethodBase>();

        /// <summary>The method <paramref name="method"/> calls if it is marked so, or the method itself.</summary>
        internal static MethodBase/*!*/ GetTarget(MethodBase/*!*/ method) {
            return _targets.GetOrAdd(method, static m => {
                var attr = (SameRubyMethodAttribute)Attribute.GetCustomAttribute(m, typeof(SameRubyMethodAttribute), false);
                if (attr == null || m.DeclaringType == null) {
                    return m;
                }
                var parameters = m.GetParameters();
                var types = new Type[parameters.Length];
                for (int i = 0; i < types.Length; i++) {
                    types[i] = parameters[i].ParameterType;
                }
                return (MethodBase)m.DeclaringType.GetMethod(attr.ClrName, BindingFlags.Public | BindingFlags.Static, null, types, null) ?? m;
            });
        }
    }
}

