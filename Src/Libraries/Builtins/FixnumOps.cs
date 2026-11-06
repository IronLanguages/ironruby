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

using IronRuby.Runtime;

namespace IronRuby.Builtins {
    // Was the Fixnum class.  Fixnum and Bignum were unified into Integer in Ruby 2.4 and
    // removed in 3.2, so these are int-typed overloads on Integer itself now.
    public partial class Integer {
        [RubyMethod("size")]
        public static int Size(int self) {
            // the size of MRI's fixnum on a 64-bit machine: a C long
            return sizeof(long);
        }
    }
}
