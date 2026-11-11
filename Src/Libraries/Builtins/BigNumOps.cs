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
using System.Numerics;

namespace IronRuby.Builtins {

    /// <summary>
    /// Bignum objects hold integers outside the range of Fixnum. Bignum objects are created automatically when integer calculations would otherwise overflow a Fixnum. When a calculation involving Bignum objects returns a result that will fit in a Fixnum, the result is automatically converted. 
    /// For the purposes of the bitwise operations and [], a Bignum is treated as if it were an infinite-length bitstring with 2s complement representation. 
    /// While Fixnum values are immediate, Bignum objects are notassignment and parameter passing work with references to objects, not the objects themselves. 
    /// </summary>
    // Was the Bignum class.  Fixnum and Bignum were unified into Integer in Ruby 2.4 and
    // removed in 3.2, so this is a BigInteger-typed overload on Integer itself now.  The four
    // HideMethod entries went with the class: they hid System.Numerics.BigInteger's own CLR
    // comparison operators so that Comparable answered <, <=, > and >=.  Integer's underlying
    // type is System.Int32, whose operators were never exposed, and ClrInteger now carries
    // real BigInteger overloads of all four.
    public partial class Integer {
        /// <summary>
        /// Returns the number of bytes in the machine representation of self. 
        /// </summary>
        /// <example>
        ///    (256**10 - 1).size   #=> 12
        ///    (256**20 - 1).size   #=> 20
        ///    (256**40 - 1).size   #=> 40
        /// </example>
        [RubyMethod("size")]
        public static int Size(BigInteger/*!*/ self) {
            // The bytes the magnitude takes up. Below 2**62 MRI has a fixnum, which is a C long.
            int bytes = (int)((BigInteger.Abs(self).GetBitLength() + 7) / 8);
            return Math.Max(bytes, sizeof(long));
        }
    }
}
