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
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting.Runtime;

namespace IronRuby.Builtins {
    [RubyClass("Binding", Extends = typeof(Binding))]
    [UndefineMethod("new", IsStatic = true)]
    [UndefineMethod("LocalScope")]
    public static class BindingOps {

        [RubyMethod("source_location")]
        public static RubyArray GetSourceLocation(RubyContext/*!*/ context, Binding/*!*/ self) {
            if (self.SourcePath == null) {
                return null;
            }
            return new RubyArray(2) { context.EncodePath(self.SourcePath), self.SourceLine };
        }

        /// <summary>
        /// `it` and `_1`.. are parameters the block body conjured up by mentioning them. MRI keeps
        /// them out of #local_variables and answers for them here instead, and only for the scope
        /// the binding was taken in: one in an enclosing or a nested block does not count.
        /// </summary>
        [RubyMethod("implicit_parameters")]
        public static RubyArray/*!*/ GetImplicitParameters(RubyContext/*!*/ context, Binding/*!*/ self) {
            var names = self.FrameScope.OwnImplicitParameterNames;
            var result = new RubyArray(names != null ? names.Length : 0);
            if (names != null) {
                foreach (var name in names) {
                    result.Add(context.EncodeIdentifier(name));
                }
            }
            return result;
        }

        [RubyMethod("implicit_parameter_defined?")]
        public static bool IsImplicitParameterDefined(ConversionStorage<MutableString>/*!*/ toStr, Binding/*!*/ self, object name) {
            return IsImplicitParameter(self, CheckImplicitParameterName(toStr, name));
        }

        [RubyMethod("implicit_parameter_get")]
        public static object GetImplicitParameter(ConversionStorage<MutableString>/*!*/ toStr, Binding/*!*/ self, object name) {
            var context = toStr.Context;
            string parameterName = CheckImplicitParameterName(toStr, name);
            if (!IsImplicitParameter(self, parameterName)) {
                throw RubyExceptions.CreateNameError(
                    String.Format("implicit parameter '{0}' is not defined for {1}", parameterName, context.Inspect(self))
                );
            }
            return self.FrameScope.GetImplicitParameterValue(parameterName);
        }

        private static bool IsImplicitParameter(Binding/*!*/ self, string/*!*/ name) {
            var names = self.FrameScope.OwnImplicitParameterNames;
            return names != null && Array.IndexOf(names, name) >= 0;
        }

        /// <summary>
        /// Only `it` and `_1`..`_9` are implicit parameter names at all; anything else is a NameError
        /// before the scope is even consulted, and anything that is not a String or a Symbol is a
        /// TypeError.
        /// </summary>
        private static string/*!*/ CheckImplicitParameterName(ConversionStorage<MutableString>/*!*/ toStr, object name) {
            string text = ToIdentifier(toStr, name);
            if (text != "it" && !(text.Length == 2 && text[0] == '_' && text[1] >= '1' && text[1] <= '9')) {
                throw RubyExceptions.CreateNameError(
                    String.Format("'{0}' is not an implicit parameter", text)
                );
            }
            return text;
        }

        /// <summary>
        /// A local variable name may be given as a Symbol, a String or anything with #to_str.
        /// </summary>
        [RubyMethod("__ir_variable_name__", RubyMethodAttributes.PrivateInstance)]
        public static MutableString/*!*/ VariableName(ConversionStorage<MutableString>/*!*/ toStr, Binding/*!*/ self, object name) {
            return MutableString.Create(ToIdentifier(toStr, name), toStr.Context.GetIdentifierEncoding());
        }

        /// <summary>
        /// The value of a local the binding can see, or nil. For a local no source text can
        /// name - a keyword argument called `alias:` or `if:` is a local named alias or if,
        /// which Binding#local_variable_get is the only way to read (fluentd's ConfigureProxy
        /// reads its `alias:` that way). Whether it exists is #local_variables' answer.
        /// </summary>
        [RubyMethod("__ir_resolve_local__", RubyMethodAttributes.PrivateInstance)]
        public static object ResolveLocal(ConversionStorage<MutableString>/*!*/ toStr, Binding/*!*/ self, object name) {
            return self.LocalScope.ResolveLocalVariable(ToIdentifier(toStr, name));
        }

        internal static string/*!*/ ToIdentifier(ConversionStorage<MutableString>/*!*/ toStr, object name) {
            var symbol = name as RubySymbol;
            if (symbol != null) {
                return symbol.ToString();
            }
            var str = name as MutableString;
            if (str != null) {
                return str.ToString();
            }
            // anything with #to_str is a name too
            var converted = Protocols.TryCastToString(toStr, name);
            if (converted != null) {
                return converted.ToString();
            }
            throw RubyExceptions.CreateTypeError("{0} is not a symbol nor a string",
                toStr.Context.Inspect(name).ToString());
        }

        [RubyMethod("to_s"), RubyMethod("inspect")]
        public static MutableString/*!*/ ToS(RubyContext/*!*/ context, Binding/*!*/ self) {
            return RubyUtils.ObjectToMutableStringPrefix(context, self).Append('>');
        }
    }
}
