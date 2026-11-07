/* ****************************************************************************
 *
 * The storage operations the JIT's Hash intrinsics use (Src/Ruby/Runtime/Jit/JitIntrinsics.cs).
 *
 * The ordinary lookups go through the comparer, which may dispatch #hash and #eql? into Ruby
 * code. Specialized code calls no Ruby code at all, so these answer only where the comparer would
 * not have to ask Ruby either - or where what it would ask is a built-in method whose answer is
 * known here: Integer#eql?, Symbol#eql? and String#eql?, which the JIT checks are the library
 * methods before it emits a call to any of these (and the entry's method-version guard keeps
 * them so). Everywhere else they say "undecided", and the specialized code deopts.
 *
 * ***************************************************************************/

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using IronRuby.Runtime;

namespace IronRuby.Builtins {
    public partial class Hash {
        /// <summary>What the lookups below answer for a key they cannot decide without calling Ruby.</summary>
        internal const int JitUndecided = -2;

        // the kinds of probe key the default comparer hashes without dispatching
        private const int ProbeIdentity = 0, ProbeFixnum = 1, ProbeSymbol = 2, ProbeString = 3;

        /// <summary>
        /// The probe kind and hash code of a key, as the comparer would compute them, or false if the
        /// comparer would dispatch #hash for it (or is not one of the two Ruby comparers).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private bool JitProbe(object/*!*/ key, out int kind, out int hashCode) {
            var comparer = _comparer;
            if (comparer == IdentityComparer.Instance) {
                kind = ProbeIdentity;
                hashCode = comparer.GetHashCode(key);
                return true;
            }
            if (comparer.GetType() == typeof(IronRuby.Runtime.EqualityComparer)) {
                if (key is int) {
                    kind = ProbeFixnum;
                    hashCode = RubyUtils.GetFixnumHashCode((int)key);
                    return true;
                }
                if (key is RubySymbol) {
                    kind = ProbeSymbol;
                    hashCode = key.GetHashCode();
                    return true;
                }
                if (key.GetType() == typeof(MutableString)) {
                    kind = ProbeString;
                    hashCode = key.GetHashCode();
                    return true;
                }
            }
            kind = 0;
            hashCode = 0;
            return false;
        }

        /// <summary>
        /// The index of the entry holding <paramref name="key"/>, -1 if there is none, or JitUndecided.
        /// <paramref name="hashCode"/> is the key's hash code whenever the answer is not JitUndecided.
        /// </summary>
        internal int JitFind(object/*!*/ key, out int hashCode) {
            int kind;
            if (!JitProbe(key, out kind, out hashCode)) {
                return JitUndecided;
            }
            return JitFind(key, kind, hashCode);
        }

        /// <summary>JitFind of an Integer key, without boxing it.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal int JitFindFixnum(int key, out int hashCode) {
            var comparer = _comparer;
            if (comparer == IdentityComparer.Instance) {
                // IdentityComparer compares an int by value, and hashes it as it hashes any Fixnum
                hashCode = IdentityComparer.FixnumHashCode(key);
            } else if (comparer.GetType() == typeof(IronRuby.Runtime.EqualityComparer)) {
                hashCode = RubyUtils.GetFixnumHashCode(key);
            } else {
                hashCode = 0;
                return JitUndecided;
            }
            Entry[] entries = _entries;
            if (entries == null) {
                return -1;
            }
            bool identity = comparer == IdentityComparer.Instance;
            int[] buckets = _buckets ?? LinkPendingBuckets();
            int i = buckets[(int)FastMod((uint)hashCode, (uint)buckets.Length, _fastModMultiplier)] - 1;
            int collisions = 0;
            while ((uint)i < (uint)entries.Length) {
                ref Entry e = ref entries[i];
                if (e.HashCode == hashCode) {
                    object stored = e.Key;
                    if (stored is int) {
                        if ((int)stored == key) {
                            return i;
                        }
                    } else if (!identity) {
                        if (stored is long) {
                            if ((long)stored == key) {
                                return i;
                            }
                        } else if (stored is System.Numerics.BigInteger) {
                            // Integer#eql? with a Bignum: not worth deciding here
                            return JitUndecided;
                        }
                        // else Integer#eql?(stored) is false
                    }
                }
                i = e.Next;
                if (++collisions > entries.Length) {
                    break;
                }
            }
            return -1;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private int JitFind(object/*!*/ key, int kind, int hashCode) {
            Entry[] entries = _entries;
            if (entries == null) {
                return -1;
            }
            IEqualityComparer<object> comparer = _comparer;
            int[] buckets = _buckets ?? LinkPendingBuckets();
            int i = buckets[(int)FastMod((uint)hashCode, (uint)buckets.Length, _fastModMultiplier)] - 1;
            int collisions = 0;
            while ((uint)i < (uint)entries.Length) {
                ref Entry e = ref entries[i];
                if (e.HashCode == hashCode) {
                    object stored = e.Key;
                    if (kind == ProbeIdentity) {
                        // IdentityComparer never calls Ruby
                        if (comparer.Equals(stored, key)) {
                            return i;
                        }
                    } else if (ReferenceEquals(stored, key)) {
                        return i;
                    } else if (stored is int || stored is long) {
                        // the comparer compares an Integer key itself
                        if (kind == ProbeFixnum && (stored is int ? (int)stored == (int)key : (long)stored == (int)key)) {
                            return i;
                        }
                    } else {
                        // the comparer asks key.eql?(stored)
                        switch (kind) {
                            case ProbeFixnum:
                                if (stored is System.Numerics.BigInteger) {
                                    return JitUndecided;
                                }
                                break;
                            case ProbeSymbol:
                                // Symbol#eql?: identity, which has already failed
                                break;
                            case ProbeString:
                                var str = stored as MutableString;
                                if (str != null) {
                                    if (((MutableString)key).Equals(str)) {
                                        return i;
                                    }
                                } else if (stored is string) {
                                    return JitUndecided;
                                }
                                break;
                        }
                    }
                }
                i = e.Next;
                if (++collisions > entries.Length) {
                    break;
                }
            }
            return -1;
        }

        internal object JitValueAt(int index) {
            return _entries[index].Value;
        }

        internal object JitKeyAt(int index) {
            return _entries[index].Key;
        }

        /// <summary>Hash#[]= of a key that is there already (at <paramref name="index"/>): the value changes, the entry stays.</summary>
        internal object JitReplaceAt(int index, object value) {
            ref Entry e = ref _entries[index];
            object old = e.Value;
            e.Value = value;
            _stamp++;
            return old;
        }

        /// <summary>
        /// Hash#[]= of a key JitFind has just said is not there, with the hash code it computed: the
        /// tail of Insert, without the probe.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal void JitAppend(object/*!*/ key, int hashCode, object value) {
            if (_buckets == null) {
                if (_entries == null) {
                    Initialize(0);
                } else {
                    LinkPendingBuckets();
                }
            }
            if (_count == _entries.Length) {
                MakeRoom();
            }
            int bucket = BucketIndex(hashCode);
            int index = _count++;
            ref int b = ref _buckets[bucket];
            ref Entry e = ref _entries[index];
            e.HashCode = hashCode;
            e.Next = b - 1;
            e.Key = key;
            e.Value = value;
            b = index + 1;
            _liveCount++;
            _version++;
            _stamp++;
        }

        /// <summary>
        /// Takes back the newest JitAppend (JitUndoLog): its entry is the last one there is, because the
        /// log undoes newest first and a compaction keeps the order of the entries.
        /// </summary>
        internal void JitUndoAppend(object/*!*/ key) {
            int i = _count - 1;
            if (i >= _start && i >= 0 && ReferenceEquals(_entries[i].Key, key)) {
                Entry[] entries = _entries;
                ref Entry found = ref entries[i];
                int bucket = BucketIndex(found.HashCode);
                int j = _buckets[bucket] - 1;
                int last = -1;
                while (j != i && (uint)j < (uint)entries.Length) {
                    last = j;
                    j = entries[j].Next;
                }
                if (j == i) {
                    if (last < 0) {
                        _buckets[bucket] = found.Next + 1;
                    } else {
                        entries[last].Next = found.Next;
                    }
                    found.HashCode = 0;
                    found.Next = TombstoneNext;
                    found.Key = null;
                    found.Value = null;
                    _liveCount--;
                    _version++;
                    _stamp++;
                    int n = i;
                    while (n > _start && entries[n - 1].Next == TombstoneNext) {
                        n--;
                    }
                    _count = n;
                    if (_liveCount == 0) {
                        _count = 0;
                        _start = 0;
                    } else if (_start > _count) {
                        _start = _count;
                    }
                    return;
                }
            }
            // not where it should be: take it out the ordinary way
            Remove(key);
        }

        /// <summary>Puts back a value JitReplaceAt replaced (JitUndoLog).</summary>
        internal void JitUndoReplace(object/*!*/ key, int index, object old) {
            if ((uint)index < (uint)_count && ReferenceEquals(_entries[index].Key, key) && _entries[index].Next != TombstoneNext) {
                _entries[index].Value = old;
                _stamp++;
                return;
            }
            int hashCode;
            int i = JitFind(key, out hashCode);
            if (i >= 0) {
                _entries[i].Value = old;
                _stamp++;
            } else {
                this[key] = old;
            }
        }
    }
}
