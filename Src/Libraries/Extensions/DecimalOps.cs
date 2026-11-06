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

namespace IronRuby.Builtins {
    [RubyClass(Extends = typeof(decimal), Inherits = typeof(Numeric), Restrictions = ModuleRestrictions.None)]
    public static class DecimalOps {
        #region new

        [RubyConstructor]
        public static decimal Create(RubyModule/*!*/ self, double value) {
            try {
                return (decimal)value;
            } catch (OverflowException) {
                throw RubyExceptions.CreateRangeError("number too big or to small to convert into System::Decimal");
            }
        }

        #endregion

        #region ==

        [RubyMethod("==")]
        public static bool Equal(decimal self, double other) {
            // Some doubles are not convertible to decimal so we do it the other way around to avoid overflow exception:
            return Decimal.ToDouble(self) == other;
        }

        [RubyMethod("==")]
        public static bool Equal(BinaryOpStorage/*!*/ equals, decimal self, object other) {
            if (other is decimal) {
                return self == (decimal)other;
            }

            // Call == on the right operand like Float#== does
            return Protocols.IsEqual(equals, other, self);
        }

        #endregion

        /// <summary>
        /// Returns <code>self</code> truncated to an <code>Integer</code>.
        /// </summary>
        [RubyMethod("to_i"), RubyMethod("to_int")]
        public static object ToInt(decimal self) {
            decimal rounded;
            if (self >= 0) {
                rounded = Decimal.Floor(self);
            } else {
                rounded = Decimal.Ceiling(self);
            }

            return Protocols.Normalize(rounded);
        }

        [RubyMethod("to_f")]
        public static double ToDouble(decimal self) {
            return Decimal.ToDouble(self);
        }

        [RubyMethod("inspect")]
        public static MutableString/*!*/ Inspect(object/*!*/ self) {
            return MutableString.CreateMutable(RubyEncoding.Binary).Append(self.ToString()).Append(" (Decimal)");
        }

        [RubyMethod("size")]
        public static int Size(object/*!*/ self) {
            return sizeof(decimal);
        }
    }
}
