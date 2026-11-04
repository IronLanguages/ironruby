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
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using IronRuby.Runtime;

namespace IronRuby.Builtins {

    /// <summary>
    /// TODO: all operations should check frozen state!
    /// 
    /// Dictionary inherits from Object, mixes in Enumerable.
    /// Ruby hash is an IDictionary{object, object}, but it adds default value/proc.
    /// 
    /// The storage is an insertion-ordered dictionary of its own (Hash.Storage.cs) rather than
    /// <see cref="Dictionary{TKey,TValue}"/>, which reuses the slot of a removed entry and so
    /// loses the enumeration order Ruby guarantees. The interfaces Dictionary used to supply -
    /// IDictionary{object, object} above all, which is what <c>IDictionaryOps</c> is mixed in
    /// through and what every [DefaultProtocol] Hash parameter in the libraries converts to -
    /// are implemented directly.
    /// </summary>
    public partial class Hash : IDictionary<object, object>, IDictionary, IReadOnlyDictionary<object, object>,
        IRubyObjectState, IDuplicable {

        // The default value can be a Proc that we should *return*, and that is different
        // from the default value being a Proc that we should *call*, hence two variables
        private Proc _defaultProc;
        private object _defaultValue;

        private uint _flags;
        private const uint IsFrozenFlag = 1;
        private const uint IsTaintedFlag = 2;
        private const uint IsUntrustedFlag = 4;
        private const uint IsKeywordArgumentsFlag = 8;
        private const uint IsRuby2KeywordsHashFlag = 16;
        private const uint IsOmittedKeywordsFlag = 32;

        private bool _comparesByIdentity;

        // MRI's RHASH_ITER_LEV: how many Hash#each-style iterations are running over this hash.
        // Entries may be deleted and values replaced from inside an iteration, but adding a key
        // is a RuntimeError, because the table could be rebuilt under the iterator.
        private int _iterationLevel;

        /// <summary>
        /// True if Hash#compare_by_identity has been called on this hash.
        /// </summary>
        public bool ComparesByIdentity {
            get { return _comparesByIdentity; }
        }

        /// <summary>
        /// Switches the hash over to identity comparison and rehashes the existing entries.
        /// </summary>
        public Hash/*!*/ CompareByIdentity() {
            return SetComparesByIdentity(true, null);
        }

        /// <summary>
        /// Turns identity comparison on or off. <paramref name="defaultComparer"/> is only needed when
        /// turning it off (Hash#replace with a non-identity argument is the only caller that does).
        /// </summary>
        public Hash/*!*/ SetComparesByIdentity(bool value, IEqualityComparer<object> defaultComparer) {
            RequireNotFrozen();
            if (_comparesByIdentity == value) {
                return this;
            }
            if (IsIterating) {
                throw RubyExceptions.CreateRuntimeError("compare_by_identity during iteration");
            }
            IEqualityComparer<object> comparer = value ? IdentityComparer.Instance : defaultComparer;
            if (comparer == null) {
                throw new NotSupportedException("Hash#compare_by_identity is not available: no comparer to switch back to");
            }

            // The entries have to be re-hashed under the new comparer, and in insertion order so
            // that the hash keeps the order it had.
            var entries = new KeyValuePair<object, object>[Count];
            CopyTo(entries, 0);
            Clear();
            _comparer = comparer;
            _comparesByIdentity = value;
            foreach (var entry in entries) {
                this[entry.Key] = entry.Value;
            }
            return this;
        }

        /// <summary>
        /// #equal? semantics without ever dispatching to Ruby. Immediate values still compare by
        /// value, matching CRuby, where they have no separate identity: every Integer that is a
        /// Fixnum there (62 bits) and every Float that is a flonum there, however IronRuby boxes
        /// them. An Integer or Float outside those ranges is a heap object in CRuby and so has an
        /// identity of its own; here too it compares by reference.
        /// </summary>
        internal sealed class IdentityComparer : IEqualityComparer<object> {
            internal static readonly IdentityComparer/*!*/ Instance = new IdentityComparer();

            private IdentityComparer() {
            }

            private static readonly System.Numerics.BigInteger FixnumMax = System.Numerics.BigInteger.Pow(2, 62) - 1;
            private static readonly System.Numerics.BigInteger FixnumMin = -System.Numerics.BigInteger.Pow(2, 62);

            private static bool TryGetImmediateInteger(object obj, out System.Numerics.BigInteger value) {
                if (obj is int) {
                    value = (int)obj;
                    return true;
                }
                if (obj is long) {
                    value = (long)obj;
                } else if (obj is System.Numerics.BigInteger) {
                    value = (System.Numerics.BigInteger)obj;
                } else {
                    value = default(System.Numerics.BigInteger);
                    return false;
                }
                return value >= FixnumMin && value <= FixnumMax;
            }

            // CRuby's flonum: a double whose exponent lies in a middle range (and +0.0); NaN,
            // the infinities, -0.0 and very large or small magnitudes live on the heap.
            internal static bool IsFlonum(double d) {
                long bits = BitConverter.DoubleToInt64Bits(d);
                if (bits == 0) {
                    return true;
                }
                if (bits == 0x3000000000000000L) {
                    return false;
                }
                int top = (int)((bits >> 60) & 0x7);
                return top == 3 || top == 4;
            }

            bool IEqualityComparer<object>.Equals(object x, object y) {
                if (ReferenceEquals(x, y)) {
                    return true;
                }
                System.Numerics.BigInteger a, b;
                if (TryGetImmediateInteger(x, out a)) {
                    return TryGetImmediateInteger(y, out b) && a == b;
                }
                if (x is bool) {
                    return y is bool && (bool)x == (bool)y;
                }
                if (x is double) {
                    double dx = (double)x;
                    return y is double && IsFlonum(dx) && BitConverter.DoubleToInt64Bits(dx) == BitConverter.DoubleToInt64Bits((double)y);
                }
                return false;
            }

            /// <summary>The hash code of an Int32 key, which has to be that of any other representation of the same Fixnum.</summary>
            internal static int FixnumHashCode(int value) {
                return ((System.Numerics.BigInteger)value).GetHashCode();
            }

            int IEqualityComparer<object>.GetHashCode(object obj) {
                if (obj is int) {
                    return FixnumHashCode((int)obj);
                }
                System.Numerics.BigInteger value;
                if (TryGetImmediateInteger(obj, out value)) {
                    return value.GetHashCode();
                }
                if (obj is bool) {
                    return (bool)obj ? 1 : 0;
                }
                if (obj is double && IsFlonum((double)obj)) {
                    return ((double)obj).GetHashCode();
                }
                return RuntimeHelpers.GetHashCode(obj);
            }
        }

        public Proc DefaultProc { 
            get { return _defaultProc; } 
            set {
                Mutate();
                _defaultProc = value;
            }
        }

        public object DefaultValue { 
            get { return _defaultValue; } 
            set {
                Mutate();
                _defaultValue = value;
            }
        }

        #region Construction

        // Records the new hash for ObjectSpace; off unless the objspace library asked for it.
        // Every public constructor chains through the (comparer, capacity) one, which calls this.
        private void Created() {
            if (ObjectTracking.Enabled) {
                ObjectTracking.Track(this);
            }
        }

        public Hash(RubyContext/*!*/ context)
            : this(context.EqualityComparer, 0) {
        }

        public Hash(IEqualityComparer<object>/*!*/ comparer)
            : this(comparer, 0) {
        }

        public Hash(EqualityComparer/*!*/ comparer, Proc defaultProc, object defaultValue)
            : this(comparer, 0) {
            _defaultValue = defaultValue;
            _defaultProc = defaultProc;
        }

        public Hash(IEqualityComparer<object>/*!*/ comparer, int capacity) {
            ContractUtils.RequiresNotNull(comparer, "comparer");
            _comparer = comparer;
            if (capacity > 0) {
                Initialize(capacity);
            }
            Created();
        }

        public Hash(IDictionary<object, object>/*!*/ dictionary)
            : this(dictionary, dictionary is Hash ? ((Hash)dictionary).Comparer : EqualityComparer<object>.Default) {
        }

        public Hash(IDictionary<object, object>/*!*/ dictionary, IEqualityComparer<object>/*!*/ comparer)
            : this(comparer, dictionary.Count) {
            // in insertion order, whatever the source enumerates in
            foreach (var entry in dictionary) {
                this[entry.Key] = entry.Value;
            }
        }

        public Hash(Hash/*!*/ hash)
            : this(hash.Comparer, hash.Count) {
            _defaultProc = hash._defaultProc;
            _defaultValue = hash.DefaultValue;
            _comparesByIdentity = hash._comparesByIdentity;
            foreach (var entry in hash) {
                this[entry.Key] = entry.Value;
            }
        }

        /// <summary>
        /// Creates a blank instance of a RubyArray or its subclass given the Ruby class object.
        /// </summary>
        public static Hash/*!*/ CreateInstance(RubyClass/*!*/ rubyClass) {
            return (rubyClass.GetUnderlyingSystemType() == typeof(Hash)) ? new Hash(rubyClass.Context) : new Hash.Subclass(rubyClass);
        }

        /// <summary>
        /// Creates an empty instance.
        /// Doesn't copy instance data.
        /// Preserves the class of the Hash.
        /// </summary>
        protected virtual Hash/*!*/ CreateInstance() {
            return new Hash(Comparer) { _comparesByIdentity = _comparesByIdentity };
        }

        object IDuplicable.Duplicate(RubyContext/*!*/ context, bool copySingletonMembers) {
            var result = CreateInstance();
            context.CopyInstanceData(this, result, copySingletonMembers);
            return result;
        }

        #endregion

        #region Flags

        public void RequireNotFrozen() {
            if ((_flags & IsFrozenFlag) != 0) {
                // MRI: "can't modify frozen Hash: {a: 1}", with the hash as FrozenError#receiver.
                throw RubyExceptions.CreateObjectFrozenErrorFor(this, "Hash");
            }
        }

        private void Mutate() {
            RequireNotFrozen();
        }

        public bool IsIterating {
            get { return _iterationLevel > 0; }
        }

        /// <summary>Marks the start of an iteration; pair with <see cref="EndIteration"/> in a finally.</summary>
        public void BeginIteration() {
            _iterationLevel++;
        }

        public void EndIteration() {
            _iterationLevel--;
        }

        public bool IsTainted {
            get {
                return (_flags & IsTaintedFlag) != 0;
            }
            set {
                Mutate();
                _flags = (_flags & ~IsTaintedFlag) | (value ? IsTaintedFlag : 0);
            }
        }

        public bool IsUntrusted {
            get {
                return (_flags & IsUntrustedFlag) != 0;
            }
            set {
                Mutate();
                _flags = (_flags & ~IsUntrustedFlag) | (value ? IsUntrustedFlag : 0);
            }
        }

        public bool IsFrozen {
            get {
                return (_flags & IsFrozenFlag) != 0;
            }
        }

        /// <summary>
        /// True for a hash that a call site built out of keyword-argument syntax - `f(a: 1)`,
        /// `f("a" =&gt; 1, b: 2)` or `f(**h)`. Keyword arguments have no slot of their own in this
        /// calling convention: they travel as a trailing positional Hash, and this is what tells
        /// the callee whether the caller wrote keywords or passed a Hash (Ruby 3 separates the
        /// two). Such a hash is always freshly made by the call site, and the flag is not copied
        /// by #dup, so it never shows up on a hash user code holds on to.
        /// </summary>
        public bool IsKeywordArguments {
            get { return (_flags & IsKeywordArgumentsFlag) != 0; }
            set { _flags = (_flags & ~IsKeywordArgumentsFlag) | (value ? IsKeywordArgumentsFlag : 0); }
        }

        /// <summary>
        /// True for the hash a ruby2_keywords method's rest parameter is holding: it arrived as
        /// keyword arguments, and splatting the array it sits in has to send it on as keyword
        /// arguments again. Unlike <see cref="IsKeywordArguments"/>, which is a property of one
        /// call in progress, this one belongs to the object and outlives the call - which is why
        /// Hash.ruby2_keywords_hash? can report it and why the hash is copied before it is set.
        /// </summary>
        public bool IsRuby2KeywordsHash {
            get { return (_flags & IsRuby2KeywordsHashFlag) != 0; }
            set { _flags = (_flags & ~IsRuby2KeywordsHashFlag) | (value ? IsRuby2KeywordsHashFlag : 0); }
        }

        /// <summary>
        /// True for the empty hash a method or block made for itself because its caller passed no
        /// keywords: the default of its hidden keyword parameter (RubyOps.MakeOmittedKeywordArguments).
        /// Nothing else ever holds it, so a **rest parameter takes it over instead of copying it
        /// (RubyOps.MakeKeywordRest), which clears this. No copy carries it.
        /// </summary>
        internal bool IsOmittedKeywords {
            get { return (_flags & IsOmittedKeywordsFlag) != 0; }
            set { _flags = (_flags & ~IsOmittedKeywordsFlag) | (value ? IsOmittedKeywordsFlag : 0); }
        }

        void IRubyObjectState.Freeze() {
            Freeze();
        }

        public Hash/*!*/ Freeze() {
            _flags |= IsFrozenFlag;
            return this;
        }

        #endregion
    }
}
