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
using System.Text;
using System.Runtime.CompilerServices;
using Microsoft.Scripting;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;

namespace IronRuby.Builtins {

    [RubyClass("UnboundMethod")]
    public class UnboundMethod : IDuplicable {
        private readonly string/*!*/ _name;
        private readonly RubyMemberInfo/*!*/ _info;
        private readonly RubyModule/*!*/ _targetConstraint;

        // unbound from a Method that stands in for a name only #method_missing answers
        private readonly bool _isMethodMissing;

        internal RubyMemberInfo/*!*/ Info {
            get { return _info; }
        }

        internal string/*!*/ Name {
            get { return _name; }
        }

        internal RubyModule/*!*/ TargetConstraint {
            get { return _targetConstraint; }
        }

        internal bool IsMethodMissing {
            get { return _isMethodMissing; }
        }

        internal UnboundMethod(RubyModule/*!*/ targetConstraint, string/*!*/ name, RubyMemberInfo/*!*/ info) {
            Assert.NotNull(targetConstraint, name, info);

            _name = name;
            _info = info;
            _targetConstraint = targetConstraint;
        }

        internal UnboundMethod(RubyModule/*!*/ targetConstraint, string/*!*/ name, RubyMemberInfo/*!*/ info, bool isMethodMissing)
            : this(targetConstraint, name, info) {
            _isMethodMissing = isMethodMissing;
        }

        object IDuplicable.Duplicate(RubyContext/*!*/ context, bool copySingletonMembers) {
            var result = new UnboundMethod(_targetConstraint, _name, _info, _isMethodMissing);
            context.CopyInstanceData(this, result, copySingletonMembers);
            return result;
        }

        #region Public Instance Methods

        [RubyMethod("==")]
        [RubyMethod("eql?")]
        public static bool Equal(UnboundMethod/*!*/ self, [NotNull]UnboundMethod/*!*/ other) {
            return self.Info.IsEquivalentTo(other.Info)
                && (!self._isMethodMissing || other._isMethodMissing && self._name == other._name);
        }

        // both names need both overloads, or the two are not the same method and the specs that
        // check `eql?' is an alias of `==' by comparing the two UnboundMethods fail
        [RubyMethod("==")]
        [RubyMethod("eql?")]
        public static bool Equal(UnboundMethod/*!*/ self, object other) {
            return false;
        }

        [RubyMethod("hash")]
        public static int GetHash(UnboundMethod/*!*/ self) {
            return self.Info.GetEquivalenceHashCode();
        }

        [RubyMethod("arity")]
        public static int GetArity(UnboundMethod/*!*/ self) {
            return self._isMethodMissing ? -1 : self.Info.GetArity();
        }

        /// <summary>
        /// The receiver has to be a kind of the module the method is *defined* in, not of the one
        /// it was extracted from: Child.instance_method(:inherited_one) binds to any Parent, which
        /// constraining on Child would refuse. Since Ruby 3.0 (Feature #15608) a method whose owner
        /// is a module rather than a class binds to anything at all.
        /// </summary>
        [RubyMethod("bind")]
        public static RubyMethod/*!*/ Bind(UnboundMethod/*!*/ self, object target) {
            RubyContext context = self._targetConstraint.Context;
            RubyModule constraint = self._info.DeclaringModule ?? self._targetConstraint;

            if (constraint.IsClass && !context.IsKindOf(target, constraint)) {
                throw RubyExceptions.CreateTypeError(
                    "bind argument must be an instance of {0}", constraint.GetNonNullName(context)
                );
            }
            
            return self._isMethodMissing
                ? RubyMethod.CreateMethodMissing(target, self._info, self._name)
                : new RubyMethod(target, self._info, self._name);
        }

        /// <summary>
        /// The method `super' would reach from inside this one, resolved from the module holding
        /// this body onwards through the ancestry of the module the method was extracted from.
        /// </summary>
        [RubyMethod("super_method")]
        public static UnboundMethod GetSuperMethod(RubyContext/*!*/ context, UnboundMethod/*!*/ self) {
            RubyModule owner = self._info.DeclaringModule;
            if (owner == null || self._isMethodMissing) {
                return null;
            }

            // an alias resolves super under the name the body was written with
            string name = self._info.OriginalName ?? self._name;

            MethodResolutionResult result;
            using (context.ClassHierarchyLocker()) {
                result = self._targetConstraint.ResolveSuperMethodNoLock(name, owner);
            }

            return result.Found ? new UnboundMethod(self._targetConstraint, name, result.Info) : null;
        }

        /// <summary>
        /// Binds and calls in one step. mspec's own pretty_inspect uses this, so its absence
        /// turned unrelated failures into "undefined method `bind_call'" noise.
        /// </summary>
        [RubyMethod("bind_call")]
        public static object BindCall(RubyScope/*!*/ scope, BlockParam block, UnboundMethod/*!*/ self, object target,
            params object[]/*!*/ args) {

            var bound = Bind(self, target);
            var site = scope.RubyContext.GetOrCreateSendSite<Func<CallSite, RubyScope, object, Proc, RubyArray, object>>(
                "call", new RubyCallSignature(1, RubyCallFlags.HasScope | RubyCallFlags.HasSplattedArgument | RubyCallFlags.HasBlock)
            );
            return site.Target(site, scope, bound, block != null ? block.Proc : null, RubyOps.MakeArrayN(args));
        }

        [RubyMethod("name")]
        public static RubySymbol/*!*/ GetName(RubyContext/*!*/ context, UnboundMethod/*!*/ self) {
            // EncodeIdentifier rather than StringifyIdentifier: both return a Symbol, but the latter
            // hardcodes UTF-8 where this needs the context's identifier encoding.
            return context.EncodeIdentifier(self._name);
        }

        // The module the method is defined in. With Module#prepend this is the prepended module rather than
        // the class the method was looked up on.
        [RubyMethod("owner")]
        public static RubyModule/*!*/ GetOwner(UnboundMethod/*!*/ self) {
            return self._info.AliasOwner ?? self._info.DeclaringModule ?? self._targetConstraint;
        }

        /// <summary>
        /// The name the body was written with, which differs from #name once `alias' or
        /// define_method has given the same body a second name.
        /// </summary>
        [RubyMethod("original_name")]
        public static RubySymbol/*!*/ GetOriginalName(RubyContext/*!*/ context, UnboundMethod/*!*/ self) {
            return context.EncodeIdentifier(self._info.OriginalName ?? self._name);
        }

        [RubyMethod("to_s"), RubyMethod("inspect")]
        public static MutableString/*!*/ ToS(RubyContext/*!*/ context, UnboundMethod/*!*/ self) {
            return ToS(context, self.Name, self._info, null, "UnboundMethod", self._isMethodMissing);
        }

        /// <summary>
        /// MRI's description is
        ///
        ///   #&lt;Method: Origin(Owner)#name(original_name)(parameters) file:line&gt;
        ///
        /// of which this used to print only the first half.  The origin is the module the method
        /// was looked up on and is dropped when it is the owner itself; an UnboundMethod has no
        /// receiver to have looked it up on, so it prints the owner alone.  A method that lives in
        /// a singleton class is spelled "object.name" instead, and a singleton class that a method
        /// merely passed through on its way to an ancestor is replaced by the object's real class,
        /// unless the object is a class or a module - which is why String.method(:include) still
        /// says #&lt;Class:String&gt;.
        /// </summary>
        internal static MutableString/*!*/ ToS(RubyContext/*!*/ context, string/*!*/ methodName, RubyMemberInfo/*!*/ info,
            RubyModule targetModule, string/*!*/ classDisplayName) {
            return ToS(context, methodName, info, targetModule, classDisplayName, false);
        }

        internal static MutableString/*!*/ ToS(RubyContext/*!*/ context, string/*!*/ methodName, RubyMemberInfo/*!*/ info,
            RubyModule targetModule, string/*!*/ classDisplayName, bool isMissingMethod) {

            // the module that holds the body, even for an alias: MRI's description of an alias
            // names the module the original was written in and puts the original name in
            // parentheses after the new one
            RubyModule declaringModule = info.DeclaringModule ?? targetModule;

            MutableString result = MutableString.CreateMutable(context.GetIdentifierEncoding());

            result.Append("#<");
            result.Append(classDisplayName);
            result.Append(": ");

            RubyClass declaringSingleton = declaringModule as RubyClass;
            if (targetModule != null && declaringSingleton != null && declaringSingleton.IsSingletonClass) {
                var attached = declaringSingleton.SingletonClassOf;
                var attachedModule = attached as RubyModule;
                // a class method looked up on a subclass: Sub(Base).cm
                var targetSingleton = targetModule as RubyClass;
                var targetAttached = (targetSingleton != null && targetSingleton.IsSingletonClass) ? targetSingleton.SingletonClassOf as RubyModule : null;
                if (attachedModule != null && targetAttached != null && !ReferenceEquals(targetAttached, attachedModule)) {
                    result.Append(targetAttached.GetDisplayName(context, false));
                    result.Append('(');
                    result.Append(attachedModule.GetDisplayName(context, false));
                    result.Append(')');
                } else {
                    result.Append(attachedModule != null
                        ? attachedModule.GetDisplayName(context, false)
                        : context.Inspect(attached));
                }
                result.Append('.');
            } else {
                RubyModule origin = targetModule ?? declaringModule;
                var originSingleton = origin as RubyClass;
                if (targetModule != null && originSingleton != null && originSingleton.IsSingletonClass && !(originSingleton.SingletonClassOf is RubyModule)) {
                    origin = context.GetClassOf(originSingleton.SingletonClassOf);
                }

                result.Append(origin.GetDisplayName(context, false));
                if (!ReferenceEquals(origin, declaringModule)) {
                    result.Append('(');
                    result.Append(declaringModule.GetDisplayName(context, false));
                    result.Append(')');
                }
                result.Append('#');
            }

            result.Append(methodName);

            if (!isMissingMethod && info.OriginalName != null && info.OriginalName != methodName) {
                result.Append('(');
                result.Append(info.OriginalName);
                result.Append(')');
            }

            var signature = isMissingMethod ? null : info.GetParameterSignature();
            if (isMissingMethod) {
                result.Append("(*)");
            } else if (signature != null) {
                result.Append(signature.ToParameterListString());
            } else {
                // a library method or an attribute accessor: MRI prints the parameter list
                // from #parameters, "_" standing for a required parameter with no name
                result.Append(FormatParameterArray(info.GetRubyParameterArray()));
            }

            var location = isMissingMethod ? null : GetSourceLocation(info);
            if (location != null) {
                result.Append(' ');
                result.Append(location[0] as MutableString);
                result.Append(':');
                result.Append(location[1].ToString());
            }

            result.Append('>');
            return result; 
        }

        private static string/*!*/ FormatParameterArray(RubyArray/*!*/ parameters) {
            var result = new StringBuilder("(");
            foreach (object item in parameters) {
                var parameter = item as RubyArray;
                if (parameter == null || parameter.Count == 0) {
                    continue;
                }
                string kind = parameter[0].ToString();
                string name = parameter.Count > 1 ? parameter[1].ToString() : null;
                string text;
                switch (kind) {
                    case "req": text = name ?? "_"; break;
                    case "opt": text = (name ?? "_") + "=..."; break;
                    case "rest": text = "*" + (name == "*" ? null : name); break;
                    case "keyreq": text = (name ?? "_") + ":"; break;
                    case "key": text = (name ?? "_") + ": ..."; break;
                    case "keyrest": text = "**" + (name == "**" ? null : name); break;
                    case "nokey": text = "**nil"; break;
                    case "block": text = "&" + (name == "&" ? null : name); break;
                    default: continue;
                }
                if (result.Length > 1) {
                    result.Append(", ");
                }
                result.Append(text);
            }
            result.Append(')');
            return result.ToString();
        }

        [RubyMethod("of")]
        public static UnboundMethod/*!*/ BingGenericParameters(RubyContext/*!*/ context, UnboundMethod/*!*/ self, [NotNullItems]params object/*!*/[]/*!*/ typeArgs) {
            return new UnboundMethod(self.TargetConstraint, self.Name, MethodOps.BindGenericParameters(context, self.Info, self.Name, typeArgs));
        }

        [RubyMethod("overloads")]
        public static RubyMethod/*!*/ SelectOverload_old(RubyContext/*!*/ context, RubyMethod/*!*/ self, [NotNullItems]params object/*!*/[]/*!*/ parameterTypes) {
            throw RubyExceptions.CreateNameError("UnboundMethod#overloads is an obsolete name, use UnboundMethod#overload.");
        }
        
        [RubyMethod("overload")]
        public static UnboundMethod/*!*/ SelectOverload(RubyContext/*!*/ context, UnboundMethod/*!*/ self, [NotNullItems]params object/*!*/[]/*!*/ parameterTypes) {
            return new UnboundMethod(self.TargetConstraint, self.Name, MethodOps.SelectOverload(context, self.Info, self.Name, parameterTypes));
        }

        [RubyMethod("clr_members")]
        public static RubyArray/*!*/ GetClrMembers(UnboundMethod/*!*/ self) {
            return new RubyArray(self.Info.GetMembers());
        }

        [RubyMethod("source_location")]
        public static RubyArray GetSourceLocation(UnboundMethod/*!*/ self) {
            return self._isMethodMissing ? null : GetSourceLocation(self.Info);
        }

        [RubyMethod("parameters")]
        public static RubyArray/*!*/ GetParameters(RubyContext/*!*/ context, UnboundMethod/*!*/ self) {
            if (self._isMethodMissing) {
                return new RubyArray(1) { new RubyArray(1) { context.CreateAsciiSymbol("rest") } };
            }
            return self.Info.GetRubyParameterArray();
        }

        // "Kernel#tap", "Dir.glob": how InternalMethods.SourceFiles names a method.
        private static string/*!*/ InternalMethodKey(RubyMethodInfo/*!*/ info) {
            RubyModule owner = info.DeclaringModule;
            var singleton = owner.IsSingletonClass ? ((RubyClass)owner).SingletonClassOf as RubyModule : null;
            return (singleton != null)
                ? singleton.Name + "." + info.DefinitionName
                : owner.Name + "#" + info.DefinitionName;
        }

        internal static RubyArray GetSourceLocation(RubyMemberInfo/*!*/ info) {
            RubyMethodInfo rubyInfo = info as RubyMethodInfo;
            if (rubyInfo != null) {
                string file = rubyInfo.Document.FileName;
                // A core method IronRuby writes in Ruby is located where MRI's is when MRI writes it
                // in Ruby too (<internal:kernel> for Kernel#tap); where MRI's is C, it has no
                // location at all, as a C method has none - method_source and pry rely on that.
                if (RubyStackTraceBuilder.IsInternalFile(file)) {
                    string mriFile;
                    if (!InternalMethods.SourceFiles.TryGetValue(InternalMethodKey(rubyInfo), out mriFile)) {
                        return null;
                    }
                    file = mriFile;
                }
                return new RubyArray(2) {
                    rubyInfo.DeclaringModule.Context.EncodePath(file),
                    rubyInfo.SourceSpan.Start.Line
                };
            }

            // a method define_method made out of a block is located where the block was written
            var lambdaInfo = info as RubyLambdaMethodInfo;
            if (lambdaInfo != null && lambdaInfo.Lambda.Dispatcher.SourcePath != null) {
                return new RubyArray(2) {
                    lambdaInfo.Context.EncodePath(lambdaInfo.Lambda.Dispatcher.SourcePath),
                    lambdaInfo.Lambda.Dispatcher.SourceLine
                };
            }

            return null;
        }

        #endregion
    }
}
