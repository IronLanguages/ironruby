using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using IronRuby.Prism.Ast;

namespace IronRuby.Prism {
    public struct PrismError {
        public string Message;
        public PmLocation Location;
        public byte Level; // 0 fatal, 1 argument, 2 load
    }

    public struct PrismWarning {
        public string Message;
        public PmLocation Location;
        public byte Level; // 0 default, 1 verbose
    }

    public sealed class PrismParseResult {
        public PmNode Root;
        public string EncodingName;
        public int StartLine;
        public List<PrismError> Errors;
        public List<PrismWarning> Warnings;
        public PmLocation? DataLocation; // __END__

        // Byte offset of the first byte after the __END__ line terminator, or -1 if
        // there is no __END__. Computed by PrismParser while the UTF-8 byte array is
        // still around: prism's locations are byte offsets, not char indices.
        public int DataOffset = -1;
    }

    /// <summary>
    /// Deserializer for prism's binary AST format (docs/serialization.md). The
    /// per-node loading switch lives in PrismLoader.Generated.cs, produced by
    /// generate.rb from prism's config.yml — the same scheme JRuby uses.
    /// prism writes the tree in post-order, children before their parent, so
    /// the loader builds it bottom-up on an explicit stack (LoadNodes) rather
    /// than by recursion: no tree is too deep to load.
    /// </summary>
    public sealed partial class PrismLoader {
        private readonly byte[]/*!*/ _buffer;
        private int _pos;
        private uint _cpoolBase;
        private string[] _constants;

        // Lazy method bodies: a DefNode's body is skimmed rather than built (prism puts the byte
        // lengths of a DefNode's children ahead of them for this) and loaded from the buffer when it
        // is asked for.
        private readonly bool _lazyDefBodies;

        // Nodes built and not yet popped by their parent's record (LoadNodes).
        private PmNode[] _stack = new PmNode[64];
        private int _stackSize;

        // The DefNode bodies waiting to be skipped (LoadNodes): where each starts and how long it is.
        // The innermost DefNode's body comes first in the buffer, and its marker last, so the next
        // body to reach is always the top one.
        private int[] _lazyStarts = new int[8];
        private int[] _lazyLengths = new int[8];
        private int _lazyCount;

        // The Contents* bits of each subtree skimmed and not yet popped by its parent (SkimNodes).
        private uint[] _skimStack = new uint[64];
        private int _skimSize;

        // An absent node? child on the skim stack.
        private const uint SkimAbsent = 0x80000000u;

        // What the body being skimmed holds, as Contents* bits.
        private uint _contents;

        /// <summary>The method uses its block: a yield, or a super that passes the block on.</summary>
        public const uint ContentsUsesBlock = 1;

        /// <summary>
        /// Something the AST bridge reports while it maps it - a warning or an error that belongs to
        /// parse time - so a body holding it is not put off: `defined?' (void-context warning), a
        /// regexp literal that may draw a nested-quantifier warning, `class << x' (yield in it is an
        /// error), class and module.
        /// </summary>
        public const uint ContentsEager = 2;

        private PrismLoader(byte[]/*!*/ buffer, bool lazyDefBodies) {
            _buffer = buffer;
            _lazyDefBodies = lazyDefBodies;
        }

        public static PrismParseResult/*!*/ LoadParse(byte[]/*!*/ serialized) {
            return LoadParse(serialized, false);
        }

        /// <summary>
        /// With <paramref name="lazyDefBodies"/> every DefNode comes back with a null Body and its
        /// DeferredBodyLoader set: <see cref="LoadDeferredBody"/> builds the body when it is needed.
        /// </summary>
        public static PrismParseResult/*!*/ LoadParse(byte[]/*!*/ serialized, bool lazyDefBodies) {
            var loader = new PrismLoader(serialized, lazyDefBodies);
            return loader.Load();
        }

        /// <summary>
        /// Builds the body a lazy load left in the buffer. Safe to call from any thread.
        /// </summary>
        public PmNode LoadDeferredBody(DefNode/*!*/ node) {
            lock (this) {
                int saved = _pos;
                _pos = node.DeferredBodyOffset;
                try {
                    return LoadNodes(node.DeferredBodyOffset + node.DeferredBodyLength);
                } finally {
                    _pos = saved;
                    // only a malformed buffer leaves anything behind; don't let it poison the next load
                    _stackSize = 0;
                    _lazyCount = 0;
                    _skimSize = 0;
                }
            }
        }

        private PrismParseResult/*!*/ Load() {
            if (_buffer.Length < 8 || _buffer[0] != 'P' || _buffer[1] != 'R' || _buffer[2] != 'I' || _buffer[3] != 'S' || _buffer[4] != 'M') {
                throw new InvalidOperationException("Invalid prism serialization (bad magic)");
            }
            if (_buffer[5] != SerializationMajor || _buffer[6] != SerializationMinor) {
                throw new InvalidOperationException(
                    $"prism serialization version mismatch: libprism {_buffer[5]}.{_buffer[6]}.{_buffer[7]}, generated loader {SerializationMajor}.{SerializationMinor}.{SerializationPatch} — re-run generate.rb");
            }
            _pos = 8;
            if (LoadByte() != 0) {
                throw new InvalidOperationException("prism serialization must include location fields");
            }

            var result = new PrismParseResult();
            result.EncodingName = LoadString();
            result.StartLine = LoadVarSInt();
            _pos += 8; // checksum of the source (pm_parser_checksum), which nothing here needs

            uint newlineCount = LoadVarUInt();
            for (uint i = 0; i < newlineCount; i++) LoadVarUInt();

            uint commentCount = LoadVarUInt();
            for (uint i = 0; i < commentCount; i++) { LoadByte(); LoadLocation(); }

            // Comment attachments: node id, comment count, then each comment's index. The
            // options never ask for attach_comments, so this is empty, but the count is there.
            uint attachedNodeCount = LoadVarUInt();
            for (uint i = 0; i < attachedNodeCount; i++) {
                LoadVarUInt();
                uint attachedCount = LoadVarUInt();
                for (uint j = 0; j < attachedCount; j++) LoadVarUInt();
            }

            uint magicCommentCount = LoadVarUInt();
            for (uint i = 0; i < magicCommentCount; i++) { LoadLocation(); LoadLocation(); }

            result.DataLocation = LoadOptionalLocation();

            result.Errors = new List<PrismError>();
            uint errorCount = LoadVarUInt();
            for (uint i = 0; i < errorCount; i++) {
                LoadVarUInt(); // error type
                var error = new PrismError { Message = LoadString(), Location = LoadLocation(), Level = LoadByte() };
                result.Errors.Add(error);
            }

            result.Warnings = new List<PrismWarning>();
            uint warningCount = LoadVarUInt();
            for (uint i = 0; i < warningCount; i++) {
                LoadVarUInt(); // warning type
                var warning = new PrismWarning { Message = LoadString(), Location = LoadLocation(), Level = LoadByte() };
                result.Warnings.Add(warning);
            }

            LoadByte(); // continuable
            _cpoolBase = LoadRawUInt32();
            uint cpoolSize = LoadVarUInt();
            _constants = new string[cpoolSize];

            // The nodes run up to the constant pool, the root last.
            result.Root = LoadNodes((int)_cpoolBase);
            return result;
        }

        // ---- primitive readers ----

        private byte LoadByte() {
            return _buffer[_pos++];
        }

        private uint LoadVarUInt() {
            uint result = 0;
            int shift = 0;
            while (true) {
                byte b = _buffer[_pos++];
                result |= (uint)(b & 0x7f) << shift;
                if ((b & 0x80) == 0) return result;
                shift += 7;
            }
        }

        private int LoadVarSInt() {
            uint encoded = LoadVarUInt();
            return (int)(encoded >> 1) ^ -(int)(encoded & 1);
        }

        private uint LoadRawUInt32() {
            uint value = BitConverter.ToUInt32(_buffer, _pos);
            _pos += 4;
            return value;
        }

        /// <summary>
        /// prism's string fields hold raw bytes. Decoding them as UTF-16 here would
        /// destroy byte sequences that are not valid UTF-8, which Ruby string literals
        /// are perfectly entitled to contain ("\xE3\xB0").
        /// </summary>
        private byte[]/*!*/ LoadStringBytes() {
            int length = (int)LoadVarUInt();
            var result = new byte[length];
            Buffer.BlockCopy(_buffer, _pos, result, 0, length);
            _pos += length;
            return result;
        }

        private string/*!*/ LoadString() {
            int length = (int)LoadVarUInt();
            string result = Encoding.UTF8.GetString(_buffer, _pos, length);
            _pos += length;
            return result;
        }

        private PmLocation LoadLocation() {
            return new PmLocation((int)LoadVarUInt(), (int)LoadVarUInt());
        }

        private PmLocation? LoadOptionalLocation() {
            if (LoadByte() == 0) return null;
            return LoadLocation();
        }

        // ---- the node stack (see LoadNodes) ----

        private void Push(PmNode node) {
            if (_stackSize == _stack.Length) Array.Resize(ref _stack, _stack.Length * 2);
            _stack[_stackSize++] = node;
        }

        private PmNode Pop() {
            if (_stackSize == 0) throw new InvalidOperationException("Invalid prism serialization (node stack underflow)");
            PmNode node = _stack[--_stackSize];
            _stack[_stackSize] = null;
            return node;
        }

        // The top count nodes, in the order they were pushed: a node[] field's elements.
        private PmNode/*!*/[]/*!*/ PopList(int count) {
            if (count > _stackSize) throw new InvalidOperationException("Invalid prism serialization (node stack underflow)");
            var nodes = new PmNode[count];
            _stackSize -= count;
            Array.Copy(_stack, _stackSize, nodes, 0, count);
            Array.Clear(_stack, _stackSize, count);
            return nodes;
        }

        private void PushLazy(int start, int length) {
            if (_lazyCount == _lazyStarts.Length) {
                Array.Resize(ref _lazyStarts, _lazyCount * 2);
                Array.Resize(ref _lazyLengths, _lazyCount * 2);
            }
            _lazyStarts[_lazyCount] = start;
            _lazyLengths[_lazyCount] = length;
            _lazyCount++;
        }

        /// <summary>
        /// Stands in on the node stack for a DefNode body that was skimmed rather than built, until
        /// the DefNode's record takes it off.
        /// </summary>
        private sealed class DeferredBody : PmNode {
            public int Offset;
            public int BodyLength;
            public uint Contents;
        }

        private string/*!*/ LoadConstant() {
            return GetConstant(LoadVarUInt());
        }

        private string LoadOptionalConstant() {
            uint index = LoadVarUInt();
            return index == 0 ? null : GetConstant(index);
        }

        private string/*!*/[]/*!*/ LoadConstantList() {
            var constants = new string[LoadVarUInt()];
            for (int i = 0; i < constants.Length; i++) constants[i] = LoadConstant();
            return constants;
        }

        private string/*!*/ GetConstant(uint oneBasedIndex) {
            int index = (int)oneBasedIndex - 1;
            string result = _constants[index];
            if (result == null) {
                int entry = (int)_cpoolBase + index * 8;
                int start = (int)BitConverter.ToUInt32(_buffer, entry);
                int length = (int)BitConverter.ToUInt32(_buffer, entry + 4);
                result = _constants[index] = Encoding.UTF8.GetString(_buffer, start, length);
            }
            return result;
        }

        // ---- skimming (see SkimNodes) ----

        private void PushSkim(uint contents) {
            if (_skimSize == _skimStack.Length) Array.Resize(ref _skimStack, _skimStack.Length * 2);
            _skimStack[_skimSize++] = contents;
        }

        private uint PeekSkim() {
            if (_skimSize == 0) throw new InvalidOperationException("Invalid prism serialization (node stack underflow)");
            return _skimStack[_skimSize - 1];
        }

        // What one child holds, absent or not.
        private uint PopSkim() {
            if (_skimSize == 0) throw new InvalidOperationException("Invalid prism serialization (node stack underflow)");
            return _skimStack[--_skimSize] & ~SkimAbsent;
        }

        // What the top count children hold between them.
        private uint PopSkim(int count) {
            if (count > _skimSize) throw new InvalidOperationException("Invalid prism serialization (node stack underflow)");
            uint contents = 0;
            for (int i = 0; i < count; i++) contents |= _skimStack[--_skimSize];
            return contents & ~SkimAbsent;
        }

        private void SkipString() {
            int length = (int)LoadVarUInt();
            _pos += length;
        }

        private void SkipVarUIntList() {
            uint count = LoadVarUInt();
            for (uint i = 0; i < count; i++) LoadVarUInt();
        }

        /// <summary>
        /// A regexp literal's source. RegexpTransformer only warns about a quantifier applied directly
        /// to another, which takes two quantifier characters in a row; extended mode lets whitespace or a
        /// comment sit between them, and non-ASCII bytes may be whitespace, so any of those counts too.
        /// </summary>
        private void SkipRegexpSource() {
            int length = (int)LoadVarUInt();
            int end = _pos + length;
            for (int i = _pos; i < end; i++) {
                byte c = _buffer[i];
                if (c == (byte)'#' || c == (byte)' ' || (c >= 9 && c <= 13) || c >= 0x80) {
                    _contents |= ContentsEager;
                    break;
                }
                if ((c == (byte)'?' || c == (byte)'*' || c == (byte)'+' || c == (byte)'}') && i + 1 < end) {
                    byte d = _buffer[i + 1];
                    if (d == (byte)'?' || d == (byte)'*' || d == (byte)'+' || d == (byte)'{') {
                        _contents |= ContentsEager;
                        break;
                    }
                }
            }
            _pos = end;
        }

        private BigInteger LoadInteger() {
            bool negative = LoadByte() != 0;
            uint words = LoadVarUInt();
            BigInteger value = BigInteger.Zero;
            for (int i = 0; i < words; i++) {
                value |= new BigInteger(LoadVarUInt()) << (i * 32);
            }
            return negative ? -value : value;
        }

        private double LoadDouble() {
            double value = BitConverter.ToDouble(_buffer, _pos);
            _pos += 8;
            return value;
        }
    }
}
