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
using System.Numerics;
using System.Runtime.CompilerServices;
using IronRuby.Builtins;
using Microsoft.Scripting.Utils;
using IronRuby.Runtime.Calls;

namespace IronRuby.Runtime {
    // Even though Ruby types overload Equals & GetHashCode, we can't use them
    // because monkeypatching allows for implementing "hash" and "eql?" on any type
    // (including instances of arbitrary .NET types via singleton methods)
    // TODO: optimize this by caching hash values?
    public class EqualityComparer : IEqualityComparer<object> {
        private readonly CallSite<Func<CallSite, object, object>>/*!*/ _hashSite;
        private readonly CallSite<Func<CallSite, object, object, object>>/*!*/ _eqlSite;

        // friend: RubyContext
        internal EqualityComparer(RubyContext/*!*/ context)
            : this(
                CallSite<Func<CallSite, object, object>>.Create(RubyCallAction.Make(context, "hash", RubyCallSignature.WithImplicitSelf(0))),
                CallSite<Func<CallSite, object, object, object>>.Create(RubyCallAction.Make(context, "eql?", RubyCallSignature.WithImplicitSelf(1)))
            ) {
        }

        public EqualityComparer(UnaryOpStorage/*!*/ hashStorage, BinaryOpStorage/*!*/ eqlStorage) 
            : this(hashStorage.GetCallSite("hash"), eqlStorage.GetCallSite("eql?")) {
        }

        public EqualityComparer(CallSite<Func<CallSite, object, object>>/*!*/ hashSite, CallSite<Func<CallSite, object, object, object>>/*!*/ eqlSite) {
            ContractUtils.RequiresNotNull(hashSite, "hashSite");
            ContractUtils.RequiresNotNull(eqlSite, "eqlSite");
            _hashSite = hashSite;
            _eqlSite = eqlSite;
        }

        bool IEqualityComparer<object>.Equals(object x, object y) {
            if (x == y) {
                return true;
            }

            // An Integer key compares by value, whichever of the two immediate CLR types carries
            // it. The narrowing funnels make an Int32 and an Int64 of the same value impossible,
            // but a long straight out of a CLR method has not been through one.
            if (x is int) {
                return y is int ? (int)x == (int)y : y is long && (int)x == (long)y;
            }

            if (x is long) {
                return y is long ? (long)x == (long)y : y is int && (long)x == (int)y;
            }

            // Dictionary<,> calls this as Equals(storedKey, probeKey); Ruby dispatches eql? on the
            // key being looked up, so the arguments are swapped here.
            return RubyOps.IsTrue(_eqlSite.Target(_eqlSite, y, x));
        }

        int IEqualityComparer<object>.GetHashCode(object obj) {
            if (obj is int) {
                return RubyUtils.GetFixnumHashCode((int)obj);
            }

            // MRI's any_hash hashes these core values itself and never dispatches #hash, so a
            // redefined Integer#hash or String#hash does not affect Hash lookups. The values are
            // the ones the built-in #hash methods answer. A String subclass still dispatches.
            if (obj == null) {
                return RubyUtils.NilObjectId;
            }
            if (obj is double) {
                return RubyUtils.GetFloatHashCode((double)obj);
            }
            if (obj is long) {
                return RubyUtils.GetIntegerHashCode((long)obj);
            }
            if (obj is BigInteger) {
                return RubyUtils.GetIntegerHashCode((BigInteger)obj);
            }
            if (obj is bool || obj is RubySymbol || obj.GetType() == typeof(MutableString)) {
                return obj.GetHashCode();
            }

            return Protocols.ToHashCode(_hashSite.Target(_hashSite, obj));
        }
    }
}
