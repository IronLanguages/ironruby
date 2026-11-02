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
using System.Reflection;
using Microsoft.Scripting.Actions;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using IronRuby.Builtins;

namespace IronRuby.Runtime.Calls {
    internal sealed class RubyCustomMethodInfo : RubyMemberInfo {
        private readonly RuleGenerator/*!*/ _ruleGenerator;
        private readonly int? _arity;

        public RubyCustomMethodInfo(RuleGenerator/*!*/ ruleGenerator, RubyMemberFlags flags, RubyModule/*!*/ declaringModule)
            : this(ruleGenerator, flags, declaringModule, null) {
        }

        /// <summary>
        /// A rule generator with a fixed number of arguments - a Struct member's reader (0) or
        /// writer (1) - reports it as its arity.
        /// </summary>
        public RubyCustomMethodInfo(RuleGenerator/*!*/ ruleGenerator, RubyMemberFlags flags, RubyModule/*!*/ declaringModule, int? arity)
            : base(flags, declaringModule) {

            Assert.NotNull(ruleGenerator, declaringModule);
            _ruleGenerator = ruleGenerator;
            _arity = arity;
        }

        internal override void BuildCallNoFlow(MetaObjectBuilder/*!*/ metaBuilder, CallArguments/*!*/ args, string/*!*/ name) {
            _ruleGenerator(metaBuilder, args, name);
        }

        /// <summary>
        /// Like a library method registered under several names, one rule generator registered
        /// under several - Method#call, #[] and #=== - gets an info each, and Ruby considers the
        /// names to be the same method.
        /// </summary>
        public override bool IsEquivalentTo(RubyMemberInfo/*!*/ other) {
            if (ReferenceEquals(this, other)) {
                return true;
            }
            var info = other as RubyCustomMethodInfo;
            return info != null && Equals(_ruleGenerator, info._ruleGenerator);
        }

        public override int GetEquivalenceHashCode() {
            return _ruleGenerator.GetHashCode();
        }

        protected internal override RubyMemberInfo/*!*/ Copy(RubyMemberFlags flags, RubyModule/*!*/ module) {
            return new RubyCustomMethodInfo(_ruleGenerator, flags, module, _arity);
        }

        public override MemberInfo/*!*/[]/*!*/ GetMembers() {
            return new MemberInfo[] { _ruleGenerator.GetMethodInfo() };
        }

        /// <summary>
        /// A rule generator binds whatever arguments arrive - Class#new, Kernel#send, IO.open -
        /// so its arity is -1, as MRI's is for those; Class#allocate alone takes nothing.
        /// </summary>
        public override int GetArity() {
            if (_arity.HasValue) {
                return _arity.Value;
            }
            return _ruleGenerator.GetMethodInfo().Name == "InstanceAllocator" ? 0 : -1;
        }

        public override RubyMemberInfo TrySelectOverload(Type/*!*/[]/*!*/ parameterTypes) {
            return this;
        }
    }
}
