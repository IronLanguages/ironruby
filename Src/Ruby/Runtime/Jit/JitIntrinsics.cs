/* ****************************************************************************
 *
 * -X:JIT / -X:OSR: the runtime half of the library intrinsics specialized code calls instead of
 * a dynamic call - case/when tests, Array#[], #fetch, #[]= and Hash#[], #[]=, #key?, #fetch.
 *
 * Every helper here answers exactly what the library method would, or deopts. None of them calls
 * Ruby code: where the library method would - a Hash's default proc, #hash or #eql? on a key the
 * comparer cannot decide by itself, a receiver of any class but the exact one (a subclass may
 * override the method) - the helper throws the deopt instead, and the generic code makes the
 * call. JitCompiler checks, when it compiles, that every library method a helper stands in for
 * is the library one on the classes involved, and the entry's global method version guard keeps
 * that true for as long as the specialization is installed.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using IronRuby.Builtins;
using Microsoft.Scripting.Runtime;

namespace IronRuby.Runtime.Jit {

    /// <summary>
    /// The Hash and Array writes specialized code has made since the entry it is running under started: a
    /// deopt hands the work back to the generic code, which re-runs it from the top (of the method,
    /// or of the loop iteration), so the writes are taken back first, newest first, and every hash
    /// is as it was when that run began. Nothing can have seen the values in between: specialized
    /// code calls no Ruby code. One per thread; an entry remembers where it started (Mark), lets go
    /// of what is above it on its way out (Release) and rolls back down to there on a deopt, so a
    /// nested entry (a trap handler run at a safe point, say) leaves the log as it found it. An
    /// outlined loop commits at the top of each iteration, which only moves the count back: the
    /// entries' references are cleared once, by Release.
    /// </summary>
    public sealed class JitUndoLog {
        private struct Entry {
            internal object Target;   // a Hash or a RubyArray
            internal object Key;      // a Hash's key
            internal object Old;
            internal int Index;       // the replaced entry or element, or -1: the key was appended
        }

        /// <summary>
        /// Writes one run may make before the next one deopts instead (taking the run back), so the
        /// log stays bounded whatever the body does.
        /// </summary>
        private const int MaxEntries = 1 << 16;

        [ThreadStatic]
        private static JitUndoLog _current;

        private Entry[] _entries = new Entry[8];
        private int _count;
        // how far up entries may still hold references (Commit leaves them)
        private int _highWater;

        public static JitUndoLog/*!*/ Current {
            get { return _current ?? (_current = new JitUndoLog()); }
        }

        /// <summary>Where a run's writes start: what Commit and Rollback go back to.</summary>
        public int Mark {
            get { return _count; }
        }

        internal bool IsFull {
            get { return _count >= MaxEntries; }
        }

        private ref Entry Add() {
            if (_count == _entries.Length) {
                Array.Resize(ref _entries, _entries.Length * 2);
            }
            return ref _entries[_count++];
        }

        internal void Replaced(Hash/*!*/ hash, object/*!*/ key, int index, object old) {
            ref Entry e = ref Add();
            e.Target = hash;
            e.Key = key;
            e.Old = old;
            e.Index = index;
        }

        internal void Replaced(RubyArray/*!*/ array, int index, object old) {
            ref Entry e = ref Add();
            e.Target = array;
            e.Old = old;
            e.Index = index;
        }

        internal void Appended(Hash/*!*/ hash, object/*!*/ key) {
            ref Entry e = ref Add();
            e.Target = hash;
            e.Key = key;
            e.Old = null;
            e.Index = -1;
        }

        /// <summary>The writes since <paramref name="mark"/> stand (and more may follow).</summary>
        public void Commit(int mark) {
            int count = _count;
            if (count > mark) {
                if (count > _highWater) { _highWater = count; }
                _count = mark;
            }
        }

        /// <summary>The writes since <paramref name="mark"/> stand, and the entry that made them is done.</summary>
        public void Release(int mark) {
            int top = Math.Max(_count, _highWater);
            if (top > mark) {
                Array.Clear(_entries, mark, top - mark);
            }
            _count = mark;
            if (_highWater > mark) { _highWater = mark; }
        }

        /// <summary>Takes back the writes since <paramref name="mark"/>, newest first.</summary>
        public void Rollback(int mark) {
            for (int i = _count - 1; i >= mark; i--) {
                ref Entry e = ref _entries[i];
                var array = e.Target as RubyArray;
                if (array != null) {
                    array.JitRestoreElement(e.Index, e.Old);
                } else if (e.Index < 0) {
                    ((Hash)e.Target).JitUndoAppend(e.Key);
                } else {
                    ((Hash)e.Target).JitUndoReplace(e.Key, e.Index, e.Old);
                }
                e = default(Entry);
            }
            if (_count > mark) {
                _count = mark;
            }
        }
    }

    public static partial class JitRuntime {

        // ---- case/when (JitCompiler.EmitCase) --------------------------------------------
        // `case v when C' on a value the specialization holds as an object: the CLR type of the
        // value says which of a few core classes it is an instance of, once per case, and for those
        // the answer of `C === v' was worked out when the code was compiled (a bit per kind in a
        // mask). The kinds below KindString cannot have a singleton class of their own (nil's,
        // true's and false's is their class), so for them the answer holds for a module C as well;
        // for the others only for a class C, because a singleton class can include modules but its
        // superclass chain is the class's. Anything else asks the value's class (KindOf).

        internal const int KindOther = 0, KindNil = 1, KindTrue = 2, KindFalse = 3, KindFixnum = 4, KindLong = 5,
            KindBignum = 6, KindFloat = 7, KindSymbol = 8, KindString = 9, KindArray = 10, KindHash = 11, KindRange = 12,
            KindCount = 13;

        /// <summary>The first kind whose instances can have singleton classes.</summary>
        internal const int FirstSingletonKind = KindString;

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static int CaseKind(object value) {
            if (value == null) { return KindNil; }
            Type t = value.GetType();
            if (t == typeof(int)) { return KindFixnum; }
            if (t == typeof(MutableString)) { return KindString; }
            if (t == typeof(RubySymbol)) { return KindSymbol; }
            if (t == typeof(double)) { return KindFloat; }
            if (t == typeof(bool)) { return (bool)value ? KindTrue : KindFalse; }
            if (t == typeof(long)) { return KindLong; }
            if (t == typeof(RubyArray)) { return KindArray; }
            if (t == typeof(Hash)) { return KindHash; }
            if (t == typeof(BigInteger)) { return KindBignum; }
            if (t == typeof(IronRuby.Builtins.Range)) { return KindRange; }
            return KindOther;
        }

        /// <summary>A value of each kind, for working out what its class answers (JitCompiler).</summary>
        internal static object KindSample(RubyContext/*!*/ context, int kind) {
            switch (kind) {
                case KindNil: return null;
                case KindTrue: return ScriptingRuntimeHelpers.True;
                case KindFalse: return ScriptingRuntimeHelpers.False;
                case KindFixnum: return ScriptingRuntimeHelpers.Int32ToObject(0);
                case KindLong: return (object)(1L << 40);
                case KindBignum: return (object)(BigInteger.One << 80);
                case KindFloat: return (object)0.0;
                case KindSymbol: return context.CreateAsciiSymbol("a");
                case KindString: return MutableString.CreateEmpty();
                case KindArray: return new RubyArray();
                case KindHash: return new Hash(context);
                case KindRange: return new IronRuby.Builtins.Range(0, 1, false);
                default: throw new ArgumentOutOfRangeException("kind");
            }
        }

        /// <summary>`M === v' (Module#===) for a value of no kind in particular: one per when, caching the last class seen.</summary>
        public sealed class KindOfSite {
            internal readonly RubyModule/*!*/ Module;

            private sealed class Last {
                internal readonly RubyClass/*!*/ Class;
                internal readonly bool Result;
                internal Last(RubyClass/*!*/ cls, bool result) { Class = cls; Result = result; }
            }
            private volatile Last _last;

            internal KindOfSite(RubyModule/*!*/ module) {
                Module = module;
            }

            // The answer for a class cannot change while the code using this site is installed: an
            // include or a prepend changes the global method version, which retires the code.
            internal bool Test(RubyClass/*!*/ cls) {
                var last = _last;
                if (last != null && last.Class == cls) { return last.Result; }
                bool result = cls.HasAncestor(Module);
                _last = new Last(cls, result);
                return result;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static bool KindOf(RubyContext/*!*/ context, KindOfSite/*!*/ site, object value) {
            return site.Test(ClassOf(context, value));
        }

        /// <summary>
        /// `L === v', L an Integer literal, v an object that is not an Int32 (the caller tested that):
        /// Integer#=== asks `v == L', which a Float answers numerically when Float#== is the library
        /// one (<paramref name="floatEquals"/>), nil, true, false and a Symbol answer false when
        /// theirs are (<paramref name="plainFalse"/>), and a String does when String#== is and an
        /// Integer does not respond to to_str (<paramref name="stringFalse"/>). Anything else deopts.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static bool CaseFixnum(object value, int literal, bool floatEquals, bool plainFalse, bool stringFalse) {
            if (value is long) { return (long)value == literal; }
            if (value is double) {
                if (floatEquals) { return (double)value == literal; }
            } else if (plainFalse && (value == null || value is bool || value is RubySymbol)) {
                return false;
            } else if (stringFalse && value != null && value.GetType() == typeof(MutableString)) {
                return false;
            }
            throw Deopt();
        }

        /// <summary>
        /// `:s === v' is identity (JitCompiler.EmitSymbolTest), except for a ClrName subject, which
        /// Symbol#=== compares by name: such a subject deopts, once per case.
        /// </summary>
        public static void CaseSubjectNotClrName(object value) {
            if (value is ClrName) { throw Deopt(); }
        }

        /// <summary>
        /// `"s" === v': String#=== compares a String's contents; anything else is false unless it
        /// responds to to_str, which nil, true, false, an Integer, a Float and a Symbol do not when
        /// <paramref name="plainFalse"/> (checked when compiled). Anything else deopts.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static bool CaseString(object value, MutableString/*!*/ literal, bool plainFalse) {
            var str = value as MutableString;
            if (str != null) { return literal.Equals(str); }
            if (plainFalse && (value == null || value is bool || value is int || value is long || value is double || value is RubySymbol)) {
                return false;
            }
            throw Deopt();
        }

        /// <summary>
        /// `b..e === v' / `b...e === v' with Integer bounds, v an object: Range#=== compares an
        /// Integer, and a Float when Float#&lt;=&gt; is the library one; anything else deopts.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static bool CaseIntRange(object value, int begin, int end, bool exclusive, bool floats) {
            if (value is int) {
                int v = (int)value;
                return begin <= v && (exclusive ? v < end : v <= end);
            }
            if (value is long) {
                long v = (long)value;
                return begin <= v && (exclusive ? v < end : v <= end);
            }
            if (floats && value is double) {
                double v = (double)value;
                return begin <= v && (exclusive ? v < end : v <= end);
            }
            throw Deopt();
        }

        // ---- Array#[], Hash#[], #key?, #fetch, #size -------------------------------------
        // The receiver is an object the specialization knows nothing about; the helper checks its
        // CLR type, which is exactly what makes it an instance of Array or Hash itself rather than
        // of a subclass. A singleton method on one Array or Hash is ruled out when compiling
        // (JitCompiler.HasPlainInstances): `arrays' and `hashes' say for which of the two the
        // method is the library one there, and a receiver of the other deopts.

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static object IndexInt(object recv, int index, bool arrays, bool hashes) {
            if (recv != null) {
                Type t = recv.GetType();
                if (t == typeof(RubyArray) && arrays) {
                    return ((RubyArray)recv).JitElementOrNil(index);
                }
                if (t == typeof(Hash) && hashes) {
                    var hash = (Hash)recv;
                    int hashCode;
                    int i = hash.JitFindFixnum(index, out hashCode);
                    if (i >= 0) { return hash.JitValueAt(i); }
                    if (i == Hash.JitUndecided) { throw Deopt(); }
                    return Missing(hash);
                }
            }
            throw Deopt();
        }

        public static object IndexLong(object recv, long index, bool arrays, bool hashes) {
            if (index >= Int32.MinValue && index <= Int32.MaxValue) {
                return IndexInt(recv, (int)index, arrays, hashes);
            }
            // a key or index outside an Int32 is an Int64 the generic code knows what to do with
            throw Deopt();
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static object IndexObj(object recv, object index, bool arrays, bool hashes) {
            if (index is int) {
                return IndexInt(recv, (int)index, arrays, hashes);
            }
            if (hashes && recv != null && recv.GetType() == typeof(Hash) && index != null) {
                var hash = (Hash)recv;
                int hashCode;
                int i = hash.JitFind(index, out hashCode);
                if (i >= 0) { return hash.JitValueAt(i); }
                if (i == Hash.JitUndecided) { throw Deopt(); }
                return Missing(hash);
            }
            throw Deopt();
        }

        /// <summary>What Hash#[] answers for a key that is not there: Hash#default, without calling a default proc.</summary>
        private static object Missing(Hash/*!*/ hash) {
            if (hash.DefaultProc != null) { throw Deopt(); }
            return hash.DefaultValue;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static bool HashKeyInt(object recv, int key) {
            if (recv != null && recv.GetType() == typeof(Hash)) {
                int hashCode;
                int i = ((Hash)recv).JitFindFixnum(key, out hashCode);
                if (i != Hash.JitUndecided) { return i >= 0; }
            }
            throw Deopt();
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static bool HashKeyObj(object recv, object key) {
            if (key is int) {
                return HashKeyInt(recv, (int)key);
            }
            if (recv != null && recv.GetType() == typeof(Hash) && key != null) {
                int hashCode;
                int i = ((Hash)recv).JitFind(key, out hashCode);
                if (i != Hash.JitUndecided) { return i >= 0; }
            }
            throw Deopt();
        }

        /// <summary>Array#fetch(index), Hash#fetch(key): a miss raises, which the generic code does.</summary>
        public static object FetchObj(object recv, object key, bool arrays, bool hashes) {
            object value;
            if (TryFetch(recv, key, arrays, hashes, out value)) { return value; }
            throw Deopt();
        }

        /// <summary>Array#fetch(index, default), Hash#fetch(key, default).</summary>
        public static object FetchObjDefault(object recv, object key, object defaultValue, bool arrays, bool hashes) {
            object value;
            return TryFetch(recv, key, arrays, hashes, out value) ? value : defaultValue;
        }

        /// <summary>FetchObj of an Integer index or key, without boxing it.</summary>
        public static object FetchInt(object recv, int key, bool arrays, bool hashes) {
            object value;
            if (TryFetchInt(recv, key, arrays, hashes, out value)) { return value; }
            throw Deopt();
        }

        /// <summary>FetchObjDefault of an Integer index or key, without boxing it.</summary>
        public static object FetchIntDefault(object recv, int key, object defaultValue, bool arrays, bool hashes) {
            object value;
            return TryFetchInt(recv, key, arrays, hashes, out value) ? value : defaultValue;
        }

        /// <summary>
        /// Whether the element or key is there, and if so its value. An Array index is counted from the
        /// end when negative, as Array#fetch does; one that is not an Int32 the generic code converts
        /// with to_int. A key the hash cannot find without calling Ruby deopts.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static bool TryFetch(object recv, object key, bool arrays, bool hashes, out object value) {
            if (key is int) {
                return TryFetchInt(recv, (int)key, arrays, hashes, out value);
            }
            if (hashes && recv != null && recv.GetType() == typeof(Hash) && key != null) {
                var hash = (Hash)recv;
                int hashCode;
                int i = hash.JitFind(key, out hashCode);
                if (i >= 0) {
                    value = hash.JitValueAt(i);
                    return true;
                }
                if (i != Hash.JitUndecided) {
                    value = null;
                    return false;
                }
            }
            throw Deopt();
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static bool TryFetchInt(object recv, int key, bool arrays, bool hashes, out object value) {
            if (recv != null) {
                Type t = recv.GetType();
                if (t == typeof(RubyArray) && arrays) {
                    return ((RubyArray)recv).JitTryGetElement(key, out value);
                }
                if (t == typeof(Hash) && hashes) {
                    var hash = (Hash)recv;
                    int hashCode;
                    int i = hash.JitFindFixnum(key, out hashCode);
                    if (i >= 0) {
                        value = hash.JitValueAt(i);
                        return true;
                    }
                    if (i != Hash.JitUndecided) {
                        value = null;
                        return false;
                    }
                }
            }
            throw Deopt();
        }

        /// <summary>Array#size / #length, Hash#size / #length.</summary>
        public static int SizeOf(object recv, bool arrays, bool hashes) {
            if (recv != null) {
                Type t = recv.GetType();
                if (t == typeof(RubyArray) && arrays) { return ((RubyArray)recv).Count; }
                if (t == typeof(Hash) && hashes) { return ((Hash)recv).Count; }
            }
            throw Deopt();
        }

        /// <summary>Array#empty?, Hash#empty?.</summary>
        public static bool IsEmpty(object recv, bool arrays, bool hashes) {
            return SizeOf(recv, arrays, hashes) == 0;
        }

        // ---- Array#[]=, Hash#[]= -------------------------------------------------------------

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void IndexStoreInt(object recv, int index, object value, bool arrays, bool hashes, JitUndoLog/*!*/ log) {
            if (recv != null) {
                Type t = recv.GetType();
                if (t == typeof(RubyArray) && arrays) {
                    var array = (RubyArray)recv;
                    int position;
                    object old;
                    // an index out of range grows the array or raises, which the generic code does
                    if (log.IsFull || !array.JitReplaceElement(index, value, out position, out old)) { throw Deopt(); }
                    log.Replaced(array, position, old);
                    return;
                }
                if (t == typeof(Hash) && hashes) {
                    HashStoreInt(recv, index, value, log);
                    return;
                }
            }
            throw Deopt();
        }

        public static void IndexStoreLong(object recv, long index, object value, bool arrays, bool hashes, JitUndoLog/*!*/ log) {
            if (index >= Int32.MinValue && index <= Int32.MaxValue) {
                IndexStoreInt(recv, (int)index, value, arrays, hashes, log);
                return;
            }
            throw Deopt();
        }

        public static void IndexStoreObj(RubyContext/*!*/ context, object recv, object index, object value, bool arrays, bool hashes,
            JitUndoLog/*!*/ log) {
            if (index is int) {
                IndexStoreInt(recv, (int)index, value, arrays, hashes, log);
                return;
            }
            if (!hashes) { throw Deopt(); }
            HashStoreObj(context, recv, index, value, log);
        }

        // ---- Hash#[]= ------------------------------------------------------------------------

        /// <summary>
        /// `h[k] = v' on an exact Hash that is not frozen, for a key the hash can find without calling
        /// Ruby: the write, logged in the thread's JitUndoLog so that a deopt later in the run can take
        /// it back. A String key the hash does not hold yet is stored as a frozen copy, as
        /// RubyUtils.SetHashElement does.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void HashStoreObj(RubyContext/*!*/ context, object recv, object key, object value, JitUndoLog/*!*/ log) {
            if (key is int) {
                HashStoreInt(recv, (int)key, value, log);
                return;
            }
            var hash = recv as Hash;
            if (hash == null || recv.GetType() != typeof(Hash) || hash.IsFrozen || key == null) { throw Deopt(); }
            int hashCode;
            int i = hash.JitFind(key, out hashCode);
            if (i == Hash.JitUndecided || log.IsFull) { throw Deopt(); }
            if (i >= 0) {
                log.Replaced(hash, hash.JitKeyAt(i), i, hash.JitReplaceAt(i, value));
                return;
            }
            var str = key as MutableString;
            if (str != null && !str.IsFrozen && !hash.ComparesByIdentity) {
                key = str.Duplicate(context, false, str.Clone()).Freeze();
                hashCode = key.GetHashCode();
            }
            hash.JitAppend(key, hashCode, value);
            log.Appended(hash, key);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void HashStoreInt(object recv, int key, object value, JitUndoLog/*!*/ log) {
            var hash = recv as Hash;
            if (hash == null || recv.GetType() != typeof(Hash) || hash.IsFrozen) { throw Deopt(); }
            int hashCode;
            int i = hash.JitFindFixnum(key, out hashCode);
            if (i == Hash.JitUndecided || log.IsFull) { throw Deopt(); }
            if (i >= 0) {
                log.Replaced(hash, hash.JitKeyAt(i), i, hash.JitReplaceAt(i, value));
                return;
            }
            object boxed = ScriptingRuntimeHelpers.Int32ToObject(key);
            hash.JitAppend(boxed, hashCode, value);
            log.Appended(hash, boxed);
        }



        // ---- the undo log, as the entries use it -------------------------------------------

        public static int UndoMark() {
            return JitUndoLog.Current.Mark;
        }

        public static void UndoRelease(int mark) {
            JitUndoLog.Current.Release(mark);
        }

        public static void UndoRollback(int mark) {
            JitUndoLog.Current.Rollback(mark);
        }

        // ---- numeric speculation on an element -----------------------------------------------

        /// <summary>An element read used as an Integer operand (`h[k] += 1'): an Integer, or a deopt.</summary>
        public static long SpeculateLong(object value) {
            if (value is int) { return (int)value; }
            if (value is long) { return (long)value; }
            throw Deopt();
        }

        /// <summary>An element read used as a Float operand: an Integer or a Float, as a double, or a deopt.</summary>
        public static double SpeculateDouble(object value) {
            if (value is double) { return (double)value; }
            if (value is int) { return (int)value; }
            if (value is long) { return (long)value; }
            throw Deopt();
        }

        // ---- constants -------------------------------------------------------------------

        /// <summary>The guard of code that folded constants: nothing has changed any constant since.</summary>
        public static bool ConstantsOk(RubyContext/*!*/ context, int snapshot) {
            return context.ConstantAccessVersion == snapshot;
        }
    }
}
