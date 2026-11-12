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

    [RubyClass("Object", Extends = typeof(object), Inherits = typeof(BasicObject), Restrictions = ModuleRestrictions.NoNameMapping | ModuleRestrictions.NotPublished)]
    [Includes(typeof(Kernel))]
    public static class ObjectOps {
        // RubyConstructor implemented by RubyObject ctors

        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance | RubyMethodAttributes.Empty)]
        public static object Reinitialize(object self) {
            // takes no arguments: Object.new(1), and a `super` passing arguments up to here, are
            // "wrong number of arguments (given 1, expected 0)" in MRI; answers nil, as MRI's does
            return null;
        }

        [RubyConstant]
        public readonly static bool TRUE = true;

        [RubyConstant]
        public readonly static bool FALSE = false;

        [RubyConstant]
        public readonly static object NIL = null;
    }
}
