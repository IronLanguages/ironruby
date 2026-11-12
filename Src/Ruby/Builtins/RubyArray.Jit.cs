/* ****************************************************************************
 *
 * The element reads and writes the JIT's Array#[], #fetch and #[]= intrinsics use
 * (Src/Ruby/Runtime/Jit/JitIntrinsics.cs).
 *
 * ***************************************************************************/

namespace IronRuby.Builtins {
    public partial class RubyArray {
        /// <summary>
        /// Array#[] with an Integer index: counted from the end when negative, nil when out of range
        /// either way - what IListOps.GetElement answers, without going through IList.
        /// </summary>
        internal object JitElementOrNil(int index) {
            int count = _count;
            if (index < 0) {
                index += count;
                if (index < 0) {
                    return null;
                }
            } else if (index >= count) {
                return null;
            }
            return _content[_start + index];
        }

        /// <summary>
        /// Array#fetch with an Integer index: the element, counted from the end when the index is
        /// negative, or false when the index is out of range either way (IListOps.Fetch).
        /// </summary>
        internal bool JitTryGetElement(int index, out object value) {
            int count = _count;
            if (index < 0) {
                index += count;
            }
            if ((uint)index < (uint)count) {
                value = _content[_start + index];
                return true;
            }
            value = null;
            return false;
        }

        /// <summary>
        /// Array#[]= with an Integer index inside an array that is not frozen: stores the value and
        /// hands back the element it replaced and where. False, with nothing changed, in every other
        /// case - those are the ones in which Array#[]= grows the array or raises.
        /// </summary>
        internal bool JitReplaceElement(int index, object value, out int position, out object old) {
            if ((_flags & IsFrozenFlag) == 0) {
                if (index < 0) {
                    index += _count;
                }
                if ((uint)index < (uint)_count) {
                    position = index;
                    old = _content[_start + index];
                    _content[_start + index] = value;
                    return true;
                }
            }
            position = 0;
            old = null;
            return false;
        }

        /// <summary>Puts back an element JitReplaceElement replaced (JitUndoLog).</summary>
        internal void JitRestoreElement(int position, object old) {
            if ((uint)position < (uint)_count) {
                _content[_start + position] = old;
            }
        }
    }
}
