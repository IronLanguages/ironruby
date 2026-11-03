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
using Microsoft.Scripting.Utils;

namespace IronRuby.Builtins {
    [Serializable]
    public class RubySymbol : IComparable, IComparable<RubySymbol>, IEquatable<RubySymbol>, IEquatable<MutableString>, IRubyObjectState {
        internal static int MinId = 1;
        private readonly int _id;
        private readonly int _runtimeId;
        private readonly MutableString/*!*/ _string;

        // The string is frozen, so its hash never changes; computing it walks (and for some
        // content representations copies) the characters, which every Hash lookup with a Symbol
        // key - keyword arguments above all - would otherwise pay again.
        private readonly int _hashCode;

        // Symbol#name answers the same frozen String every time it is called, so it is made once.
        private MutableString _frozenName;

        // Symbol#to_proc answers the same Proc each time, as MRI's does.
        private Proc _methodInvoker;

        public Proc MethodInvoker {
            get { return _methodInvoker; }
            set { _methodInvoker = value; }
        }

        /// <summary>The String Symbol#name answers - one per symbol, frozen.</summary>
        public MutableString/*!*/ FrozenName {
            get { return _frozenName ?? (_frozenName = MutableString.Create(_string).Freeze()); }
        }

        internal RubySymbol(MutableString/*!*/ str, int id, int runtimeId) {
            Assert.NotNull(str);
            Debug.Assert(str.IsFrozen);
            Debug.Assert(str.GetType() == typeof(MutableString));

            _string = str;
            _id = id;
            _runtimeId = runtimeId;
            _hashCode = str.GetHashCode();
        }

        #region Equals, GetHashCode, CompareTo
        
        public override int GetHashCode() {
            // TODO: the same value, different encoding => different hash? different symbols?
            return _hashCode;
        }

        public bool Equals(RubySymbol other) {
            return ReferenceEquals(this, other);
        }

        public bool Equals(MutableString other) {
            return other != null && _string.Equals(other);
        }

        public int CompareTo(RubySymbol/*!*/ other) {
            return _string.CompareTo(other._string);
        }

        public int CompareTo(MutableString/*!*/ other) {
            return _string.CompareTo(other);
        }

        public override bool Equals(object other) {
            var sym = other as RubySymbol;
            if (sym != null) {
                return Equals(sym);
            }
            var ms = other as MutableString;
            if (ms != null) {
                return Equals(ms);
            }
            return false;
        }

        int IComparable.CompareTo(object other) {
            var sym = other as RubySymbol;
            if (sym != null) {
                return CompareTo(sym);
            }
            var ms = other as MutableString;
            if (ms != null) {
                return CompareTo(ms);
            }
            return -1;
        }

        #endregion

        #region Id, Encoding, Length, ToString

        public int RuntimeId {
            get { return _runtimeId; }
        }

        public int Id {
            get { return _id; }
        }

        public RubyEncoding/*!*/ Encoding {
            get { return _string.Encoding; }
        }

        /// <summary>
        /// Returns a frozen string.
        /// </summary>
        public MutableString/*!*/ String {
            get { return _string; }
        }

        public override string/*!*/ ToString() {
            return _string.ToString();
        }

        public bool IsEmpty {
            get { return _string.IsEmpty; }
        }

        public int GetByteCount() {
            return _string.GetByteCount();
        }

        public int GetCharCount() {
            return _string.GetCharCount();
        }

        public static explicit operator string(RubySymbol/*!*/ self) {
            return self._string.ToString();
        }

        #endregion

        #region IRubyObjectState Members

        public bool IsFrozen {
            get { return false; }
        }

        public bool IsTainted {
            get { return false; }
            set { /* nop */ }
        }

        public bool IsUntrusted {
            get { return false; }
            set { /* nop */ }
        }

        public void Freeze() {
            // nop
        }

        #endregion

        #region String Operations

        public bool EndsWith(char value) {
            return _string.GetLastChar() == value;
        }

        public MutableString/*!*/ GetSlice(int start) {
            return _string.GetSlice(start);
        }

        #endregion
    }
}
