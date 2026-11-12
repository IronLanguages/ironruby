using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace IronRuby.Prism {
    /// <summary>
    /// One token of prism's lexer output: the token type's prism name, its byte span in
    /// the source, and the lexer state MRI's parser would have been in after scanning it
    /// (prism tracks it purely so Ripper-compatible lexers can report it).
    /// </summary>
    public struct PrismLexToken {
        public string Type;
        public int Start;
        public int Length;
        public int LexState;
    }

    /// <summary>
    /// prism's token stream (pm_serialize_lex). Only the token list at the head of the
    /// serialized blob is decoded - the trailing encoding/line-offset/diagnostic sections
    /// carry nothing a caller cannot recompute from the source itself.
    /// </summary>
    public static class PrismLex {
        [DllImport("prism")]
        private static extern void pm_serialize_lex(IntPtr buffer, IntPtr source, nuint size, IntPtr data);

        public static List<PrismLexToken>/*!*/ Lex(byte[]/*!*/ source) {
            // pm_serialize_lex below is declared on this type, so PrismNative's static
            // constructor - which installs the DllImport resolver that finds libprism at all -
            // is not guaranteed to have run yet.
            PrismNative.EnsureResolver();
            return Decode(Serialize(source, PrismParser.BuildOptionsData(null, 1, null)));
        }

        private static byte[]/*!*/ Serialize(byte[]/*!*/ source, byte[]/*!*/ optionsData) {
            return PrismNative.WithParserStack(source.Length, () => SerializeHere(source, optionsData));
        }

        private static byte[]/*!*/ SerializeHere(byte[]/*!*/ source, byte[]/*!*/ optionsData) {
            IntPtr buffer = IntPtr.Zero;
            GCHandle pinned = GCHandle.Alloc(source, GCHandleType.Pinned);
            GCHandle options = GCHandle.Alloc(optionsData, GCHandleType.Pinned);
            try {
                buffer = PrismNative.pm_buffer_new();
                pm_serialize_lex(buffer, pinned.AddrOfPinnedObject(), (nuint)source.Length, options.AddrOfPinnedObject());
                int length = checked((int)PrismNative.pm_buffer_length(buffer));
                byte[] result = new byte[length];
                Marshal.Copy(PrismNative.pm_buffer_value(buffer), result, 0, length);
                return result;
            } finally {
                if (buffer != IntPtr.Zero) PrismNative.pm_buffer_free(buffer);
                pinned.Free();
                options.Free();
            }
        }

        private static List<PrismLexToken>/*!*/ Decode(byte[]/*!*/ serialized) {
            var tokens = new List<PrismLexToken>();
            int pos = 0;
            while (true) {
                uint type = LoadVarUInt(serialized, ref pos);
                if (type == 0) {
                    break;
                }
                tokens.Add(new PrismLexToken {
                    Type = PrismMeta.TokenTypes[type],
                    Start = (int)LoadVarUInt(serialized, ref pos),
                    Length = (int)LoadVarUInt(serialized, ref pos),
                    LexState = (int)LoadVarUInt(serialized, ref pos),
                });
            }
            return tokens;
        }

        // LEB128, as everywhere else in prism's serialization.
        private static uint LoadVarUInt(byte[]/*!*/ buffer, ref int pos) {
            uint result = 0;
            int shift = 0;
            while (true) {
                byte b = buffer[pos++];
                result |= (uint)(b & 0x7f) << shift;
                if ((b & 0x80) == 0) {
                    return result;
                }
                shift += 7;
            }
        }
    }
}
