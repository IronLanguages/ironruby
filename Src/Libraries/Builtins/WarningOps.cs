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
using System.Runtime.InteropServices;
using Microsoft.Scripting.Runtime;
using IronRuby.Runtime;

namespace IronRuby.Builtins {

    /// <summary>
    /// Warning's category switches.  They live here rather than in the Ruby prelude because the
    /// runtime has to read them too: the chilled string literal warning is raised from inside
    /// MutableString's mutation guard, and -W:category has to be able to set them before any Ruby
    /// code runs.
    ///
    /// Warning#warn lives here rather than in the prelude because every internal warning is routed
    /// through it (RubyContext.DispatchWarning) and the first ones are emitted while ruby4.rb is
    /// still being parsed, long before a Ruby level definition would exist.
    /// </summary>
    [RubyModule("Warning")]
    public static class WarningOps {

        [RubyMethod("[]", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("[]", RubyMethodAttributes.PublicInstance)]
        public static bool GetCategory(RubyContext/*!*/ context, object self, [NotNull]RubySymbol/*!*/ category) {
            return context.IsWarningEnabled(CheckCategory(category));
        }

        [RubyMethod("[]=", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("[]=", RubyMethodAttributes.PublicInstance)]
        public static object SetCategory(RubyContext/*!*/ context, object self, [NotNull]RubySymbol/*!*/ category, object flag) {
            context.SetWarningEnabled(CheckCategory(category), RubyOps.IsTrue(flag));
            return flag;
        }

        [RubyMethod("categories", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("categories", RubyMethodAttributes.PublicInstance)]
        public static RubyArray/*!*/ GetCategories(RubyContext/*!*/ context, object self) {
            var result = new RubyArray(RubyContext.WarningCategories.Length);
            foreach (string category in RubyContext.WarningCategories) {
                result.Add(context.CreateAsciiSymbol(category));
            }
            return result;
        }

        /// <summary>
        /// MRI's rb_warning_s_warn. Registered as an instance method only: `extend self' in the
        /// prelude makes Warning.warn work while keeping Method#owner == Warning, which
        /// spec/core/warning/warn_spec.rb checks.
        /// </summary>
        [RubyMethod("warn", RubyMethodAttributes.PublicInstance)]
        public static object Warn(RubyContext/*!*/ context, object self, object message,
            [Keywords, DefaultParameterValue(null)]Hash options) {

            MutableString str = message as MutableString;
            if (str == null) {
                throw RubyExceptions.CreateTypeError("wrong argument type {0} (expected String)",
                    context.GetClassDisplayName(message));
            }

            object category;
            if (options != null && options.TryGetValue(context.CreateAsciiSymbol("category"), out category) && category != null) {
                var symbol = category as RubySymbol;
                if (symbol == null) {
                    throw RubyExceptions.CreateTypeError("wrong argument type {0} (expected Symbol)",
                        context.GetClassDisplayName(category));
                }
                if (!context.IsWarningEnabled(CheckCategory(symbol))) {
                    return null;
                }
            }

            // Resolves $stderr dynamically and adds nothing to the message - MRI's
            // rb_write_error_str. $VERBOSE is deliberately not consulted here.
            context.WriteWarningMessage(str);
            return null;
        }

        private static string/*!*/ CheckCategory(RubySymbol/*!*/ category) {
            string name = category.ToString();
            if (Array.IndexOf(RubyContext.WarningCategories, name) < 0) {
                throw RubyExceptions.CreateArgumentError("unknown category: {0}", name);
            }
            return name;
        }
    }
}
