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
using System.Diagnostics;
using System.Runtime.CompilerServices;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;

namespace IronRuby.Builtins {

    [RubyClass("NilClass", Extends = typeof(DynamicNull))]
    public static class NilClassOps {
        #region CLR overrides

        [RubyMethod("GetType")]
        public static Type GetClrType(object self) {
            return typeof(DynamicNull);
        }

        [RubyMethod("ToString")]
        public static string ToClrString(object self) {
            return "nil";
        }

        [RubyMethod("GetHashCode")]
        public static int GetClrHashCode(object self) {
            return 0;
        }

        #endregion

        #region Public Instance Methods

        // MRI defines #=== on nil, true and false themselves; it is Kernel#=== (rb_equal) all the same.
        [RubyMethodAttribute("===")]
        public static bool CaseEquals(BinaryOpStorage/*!*/ equals, object self, object other) {
            return Protocols.IsEqual(equals, self, other);
        }

        [RubyMethodAttribute("&")]
        public static bool And(object self, object obj) {
            Debug.Assert(self == null);
            return false;
        }

        // nil ^ x and nil | x are the one method in MRI - both answer whether x is true -
        // and two names for one method is what the specs compare.
        [RubyMethodAttribute("|")]
        [RubyMethodAttribute("^")]
        public static bool Or(object self, object obj) {
            Debug.Assert(self == null);
            return obj != null;
        }

        [RubyMethodAttribute("|")]
        [RubyMethodAttribute("^")]
        public static bool Or(object self, bool obj) {
            Debug.Assert(self == null);
            return obj;
        }

        [RubyMethodAttribute("nil?")]
        public static bool IsNil(object self) {
            Debug.Assert(self == null);
            return true;
        }

        [RubyMethodAttribute("to_a")]
        public static RubyArray/*!*/ ToArray(object self) {
            Debug.Assert(self == null);
            return new RubyArray();
        }

        [RubyMethodAttribute("to_f")]
        public static double ToDouble(object self) {
            Debug.Assert(self == null);
            return 0.0;
        }

        [RubyMethodAttribute("to_i")]
        public static int ToInteger(object self) {
            Debug.Assert(self == null);
            return 0;
        }

        [RubyMethodAttribute("inspect")]
        public static MutableString Inspect(object self) {
            return MutableString.CreateAscii("nil");
        }

        /// <summary>
        /// One frozen string, answered again every time: MRI's nil.to_s is a single object, and
        /// a spec checks that two calls hand back the very same one.
        /// </summary>
        // US-ASCII, as MRI's nil.to_s is
        private static readonly MutableString/*!*/ _EmptyString = MutableString.Create(String.Empty, RubyEncoding.Ascii).Freeze();

        [RubyMethodAttribute("to_s")]
        public static MutableString/*!*/ ToString(object self) {
            Debug.Assert(self == null);
            return _EmptyString;
        }

        [SpecialName]
        public static bool op_Implicit(DynamicNull self) {
            Debug.Assert(self == null);
            return false;
        }

        #endregion
    }
}
