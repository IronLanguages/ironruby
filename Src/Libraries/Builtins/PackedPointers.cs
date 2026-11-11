/* ****************************************************************************
 *
 * PackedPointers - the addresses Array#pack("p") and ("P") write.
 *
 * This source code is subject to terms and conditions of the Apache License,
 * Version 2.0. A copy of the license can be found in the License.html file at
 * the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace IronRuby.Builtins {

    /// <summary>
    /// CRuby's pack("p") writes the address of the String's own buffer, NUL-terminated, and C
    /// reads and writes the String through it: reline's Win32API passes every buffer to the
    /// console API as [str].pack("p").unpack("q*"), and reads what the call wrote from str.
    /// A MutableString has no such address - its bytes live on a heap that moves them, and C
    /// writing behind its back would leave its cached state stale - so each String pack is asked
    /// to point at gets a native block of its own, holding its bytes and a NUL, for as long as
    /// the String lives. pack writes the block's address. Fiddle recognises the address among a
    /// call's pointer arguments and copies the String into the block before the call and the
    /// block back into the String after it (Fiddle.NativeCall.ToPointerArgument), which is
    /// what the String's own buffer would have given. A String that has grown past its block
    /// since gets a new one, at a new address, as CRuby's buffer moves when it is reallocated.
    /// </summary>
    internal static class PackedPointers {
        private static readonly ConditionalWeakTable<MutableString, Block> _byString = new ConditionalWeakTable<MutableString, Block>();
        private static readonly ConcurrentDictionary<long, WeakReference<Block>> _byAddress = new ConcurrentDictionary<long, WeakReference<Block>>();
        private static readonly object _lock = new object();

        internal sealed class Block {
            internal readonly MutableString/*!*/ String;
            internal readonly IntPtr Address;
            /// <summary>Bytes the block holds, the NUL included.</summary>
            internal readonly int Capacity;
            private readonly WeakReference<Block>/*!*/ _entry;

            internal Block(MutableString/*!*/ str, int capacity) {
                String = str;
                Capacity = capacity;
                Address = Marshal.AllocHGlobal(capacity);
                _entry = new WeakReference<Block>(this);
                _byAddress[(long)Address] = _entry;
            }

            ~Block() {
                // Forget the address before the memory goes, so it cannot be found for a block
                // that a later allocation gets at the same place.
                ((ICollection<KeyValuePair<long, WeakReference<Block>>>)_byAddress).Remove(
                    new KeyValuePair<long, WeakReference<Block>>((long)Address, _entry));
                Marshal.FreeHGlobal(Address);
            }

            internal void Forget() {
                ((ICollection<KeyValuePair<long, WeakReference<Block>>>)_byAddress).Remove(
                    new KeyValuePair<long, WeakReference<Block>>((long)Address, _entry));
            }

            /// <summary>Puts the String's bytes and a NUL into the block; answers how many bytes.</summary>
            internal int Refresh() {
                byte[] bytes = String.ToByteArray();
                int count = Math.Min(bytes.Length, Capacity - 1);
                if (count > 0) {
                    Marshal.Copy(bytes, 0, Address, count);
                }
                Marshal.WriteByte(Address, count, 0);
                return count;
            }

            /// <summary>Puts what C left in the block back into the String.</summary>
            internal void CopyBack(int count) {
                if (String.IsFrozen) {
                    return;
                }
                count = Math.Min(count, String.GetByteCount());
                for (int i = 0; i < count; i++) {
                    String.SetByte(i, Marshal.ReadByte(Address, i));
                }
            }
        }

        /// <summary>The address pack writes for <paramref name="str"/>, its bytes put there.</summary>
        internal static long AddressOf(MutableString/*!*/ str) {
            int needed = str.GetByteCount() + 1;
            Block block;
            lock (_lock) {
                if (!_byString.TryGetValue(str, out block) || block.Capacity < needed) {
                    if (block != null) {
                        // the old block is freed once nothing holds it; its address is found no more
                        _byString.Remove(str);
                        block.Forget();
                    }
                    block = new Block(str, needed);
                    _byString.Add(str, block);
                }
            }
            block.Refresh();
            return (long)block.Address;
        }

        /// <summary>The block at <paramref name="address"/>, if pack wrote that address for a live String.</summary>
        internal static Block Find(long address) {
            WeakReference<Block> entry;
            Block block;
            return (address != 0 && _byAddress.TryGetValue(address, out entry) && entry.TryGetTarget(out block)) ? block : null;
        }

        /// <summary>Whether <paramref name="address"/> is where pack put <paramref name="str"/> last.</summary>
        internal static bool IsAddressOf(MutableString/*!*/ str, long address) {
            Block block;
            return _byString.TryGetValue(str, out block) && (long)block.Address == address;
        }
    }
}
