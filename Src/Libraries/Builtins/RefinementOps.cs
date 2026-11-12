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
using System.Collections.Generic;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;

namespace IronRuby.Builtins {

    /// <summary>
    /// Refinement, the class of the anonymous module Module#refine returns.
    ///
    /// It is a Module subclass, but the parts of Module that splice a module into somebody's ancestors
    /// are removed: a refinement is only ever reached through the lexical activation in
    /// RubyCallAction.Resolve, never through the MRO, so include/prepend/extend on one would be a lie.
    /// RubyContext.RefinementClass undefines append_features, prepend_features and extend_object on it
    /// for the same reason.
    /// </summary>
    [RubyClass("Refinement", Inherits = typeof(RubyModule))]
    public static class RefinementOps {

        [RubyMethod("target")]
        public static RubyModule GetTarget(RubyModule/*!*/ self) {
            return self.RefinedModule;
        }

        /// <summary>
        /// A refinement has no name, so MRI describes it by what it refines and who refines it:
        /// #&lt;refinement:String@#&lt;Module:0x...&gt;&gt;.
        /// </summary>
        [RubyMethod("to_s"), RubyMethod("inspect")]
        public static MutableString/*!*/ ToS(RubyContext/*!*/ context, RubyModule/*!*/ self) {
            if (!self.IsRefinement) {
                return RubyUtils.ObjectToMutableString(context, self);
            }

            var result = MutableString.CreateMutable(context.GetIdentifierEncoding());
            result.Append("#<refinement:");
            result.Append(self.RefinedModule.GetDisplayName(context, false));
            result.Append('@');
            result.Append(self.RefinementHolder != null
                ? context.Inspect(self.RefinementHolder)
                : MutableString.CreateAscii("nil"));
            result.Append('>');
            return result;
        }

        [RubyMethod("include")]
        public static RubyModule Include(RubyModule/*!*/ self, params object[]/*!*/ modules) {
            throw RubyExceptions.CreateTypeError("Refinement#include has been removed");
        }

        [RubyMethod("prepend")]
        public static RubyModule Prepend(RubyModule/*!*/ self, params object[]/*!*/ modules) {
            throw RubyExceptions.CreateTypeError("Refinement#prepend has been removed");
        }

        /// <summary>
        /// Copies a module's instance methods into the refinement.  Unlike include it does not create an
        /// ancestor relationship - the methods become the refinement's own, which is the only shape a
        /// refinement can take.
        /// </summary>
        [RubyMethod("import_methods", RubyMethodAttributes.PrivateInstance)]
        public static RubyModule/*!*/ ImportMethods(RubyModule/*!*/ self, params object[]/*!*/ modules) {
            foreach (object obj in modules) {
                RubyModule module = obj as RubyModule;
                if (module == null || module.IsClass) {
                    throw RubyExceptions.CreateTypeError("wrong argument type {0} (expected Module)",
                        self.Context.GetClassDisplayName(obj)
                    );
                }
            }

            foreach (object obj in modules) {
                RubyModule module = (RubyModule)obj;

                // Only the module's own methods are imported; CRuby warns rather than following ancestors.
                if (module.GetMixins().Length > 0 || module.GetPrepends().Length > 0) {
                    self.Context.ReportWarning(String.Format(
                        "{0} has ancestors, but Refinement#import_methods doesn't import their methods",
                        self.Context.Inspect(module).ToString()
                    ));
                }

                var members = new List<KeyValuePair<string, RubyMemberInfo>>();
                using (self.Context.ClassHierarchyLocker()) {
                    // every method of the module, private and protected ones as well
                    module.ForEachMember(false, RubyMethodAttributes.VisibilityMask, (name, owner, member) => {
                        members.Add(new KeyValuePair<string, RubyMemberInfo>(name, member));
                    });
                }

                foreach (var entry in members) {
                    // A refinement's methods have to be re-compiled against the refinement, so a method with
                    // no Ruby body (a library or CLR member) cannot be imported at all.
                    if (!(entry.Value is RubyMethodInfo)) {
                        throw RubyExceptions.CreateArgumentError(
                            "Can't import method which is not defined with Ruby code: {0}#{1}",
                            module.GetDisplayName(self.Context, false).ToString(), entry.Key
                        );
                    }
                }

                foreach (var entry in members) {
                    self.ImportMethod(entry.Key, entry.Value);
                }
            }
            return self;
        }
    }
}
