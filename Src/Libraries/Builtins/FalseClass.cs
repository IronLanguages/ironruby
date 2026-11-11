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
using IronRuby.Runtime;

namespace IronRuby.Builtins {

    [RubyClass("FalseClass")]
    public static class FalseClass : Object {
        #region Public Instance Methods

        /// <summary>One frozen string, answered again every time - as in MRI.</summary>
        private static readonly MutableString/*!*/ _FalseString = MutableString.CreateAscii("false").Freeze();

        [RubyMethodAttribute("to_s")]
        [RubyMethodAttribute("inspect")]
        public static MutableString/*!*/ ToString(bool self) {
            Debug.Assert(self == false);
            return _FalseString;
        }

        // MRI defines #=== on nil, true and false themselves; it is Kernel#=== (rb_equal) all the same.
        [RubyMethodAttribute("===")]
        public static bool CaseEquals(BinaryOpStorage/*!*/ equals, bool self, object other) {
            return Protocols.IsEqual(equals, self, other);
        }

        [RubyMethodAttribute("&")]
        public static bool And(bool self, object obj) {
            Debug.Assert(self == false);
            return false;
        }

        // false ^ x and false | x are the one method in MRI - both answer whether x is true.
        [RubyMethodAttribute("|")]
        [RubyMethodAttribute("^")]
        public static bool Or(bool self, object obj) {
            Debug.Assert(self == false);
            return obj != null;
        }

        [RubyMethodAttribute("|")]
        [RubyMethodAttribute("^")]
        public static bool Or(bool self, bool obj) {
            Debug.Assert(self == false);
            return obj;
        }

        #endregion
    }
}
