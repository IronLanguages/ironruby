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

using System.Diagnostics;
using System.Runtime.InteropServices;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;

namespace IronRuby.Builtins {

    [RubyClass("Class", Extends = typeof(RubyClass), Inherits = typeof(RubyModule), Restrictions = ModuleRestrictions.Builtin | ModuleRestrictions.NoUnderlyingType)]
    [UndefineMethod("extend_object")]
    [UndefineMethod("append_features")]
    [UndefineMethod("prepend_features")]
    [UndefineMethod("module_function")]
    public sealed class ClassOps {
        #region initialize, initialize_copy, allocate, new, superclass, inherited

        // factory defined in on RubyClass

        // Reinitialization. Not called when a factory/non-default ctor is called.
        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static void Reinitialize(BlockParam body, RubyClass/*!*/ self, [Optional]RubyClass superClass) {
            // Class cannot be subclassed, so this can only be called directly on an already initialized class:
            throw RubyExceptions.CreateTypeError("already initialized class");
        }

        [RubyMethod("initialize_copy", RubyMethodAttributes.PrivateInstance)]
        public static void InitializeCopy(RubyClass/*!*/ self, [NotNull]RubyClass/*!*/ other) {
            self.InitializeClassCopy(other);
        }
        
        [RubyMethod("allocate")]
        public static RuleGenerator/*!*/ Allocate() {
            return new RuleGenerator(RuleGenerators.InstanceAllocator);
        }

        // Module.allocate is undefined (see ModuleOps), and Class's singleton class inherits from
        // Module's: MRI defines allocate once more on Class's singleton class so Class.allocate
        // still works (object.c: rb_define_method(rb_singleton_class(rb_cClass), "allocate", ...)).
        [RubyMethod("allocate", RubyMethodAttributes.PublicSingleton)]
        public static RuleGenerator/*!*/ AllocateClass() {
            return new RuleGenerator(RuleGenerators.InstanceAllocator);
        }

        [RubyMethod("new")]
        public static RuleGenerator/*!*/ New() {
            return new RuleGenerator(RuleGenerators.InstanceConstructor);
        }

        [RubyMethod("superclass")]
        public static RubyClass GetSuperclass(RubyClass/*!*/ self) {
            // A singleton class inherits along the same chain as the object it belongs to: the
            // singleton class of a class has the singleton class of its superclass above it, and
            // the singleton class of a plain object has the object's class. That is exactly what
            // the class hierarchy already records, so there is nothing special to do here.
            if (self.IsUninitializedAllocation) {
                throw RubyExceptions.CreateTypeError("uninitialized class");
            }
            return self.SuperClass;
        }

        [RubyMethod("inherited", RubyMethodAttributes.PrivateInstance | RubyMethodAttributes.Empty)]
        public static void Inherited(object/*!*/ self, object subclass) {
            // nop
        }

        /// <summary>
        /// Ruby 3.1's Class#subclasses: the classes that name this one as their direct
        /// superclass, excluding singleton classes. The runtime already keeps a weak list
        /// of every class whose method cache depends on this module (that is how method
        /// redefinition is propagated); the direct subclasses are the entries whose
        /// superclass is this class.
        /// </summary>
        [RubyMethod("subclasses")]
        public static RubyArray/*!*/ GetSubclasses(RubyClass/*!*/ self) {
            var result = new RubyArray();
            foreach (var cls in self.GetDirectSubclasses()) {
                result.Add(cls);
            }
            return result;
        }

        /// <summary>Ruby 3.2's Class#attached_object.</summary>
        [RubyMethod("attached_object")]
        public static object GetAttachedObject(RubyClass/*!*/ self) {
            if (!self.IsSingletonClass || self.IsDummySingletonClass) {
                throw RubyExceptions.CreateTypeError("'{0}' is not a singleton class", self.GetNonNullName(self.Context));
            }
            return self.SingletonClassOf;
        }

        #endregion

        #region IronRuby: clr_new, clr_ctor

        [RubyMethod("clr_new")]
        public static RuleGenerator/*!*/ ClrNew() {
            return (metaBuilder, args, name) => ((RubyClass)args.Target).BuildClrObjectConstruction(metaBuilder, args, name);
        }

        [RubyMethod("clr_ctor")]
        [RubyMethod("clr_constructor")]
        public static RubyMethod/*!*/ GetClrConstructor(RubyClass/*!*/ self) {
            RubyMemberInfo info;

            if (self.TypeTracker == null) {
                throw RubyExceptions.CreateNotClrTypeError(self);
            }

            if (!self.TryGetClrConstructor(out info)) {
                throw RubyOps.MakeConstructorUndefinedError(self);
            }

            return new RubyMethod(self, info, ".ctor");
        }

        #endregion
    }
}
