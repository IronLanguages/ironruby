using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace IronRuby.Aot {
    /// <summary>
    /// Puts the exception clauses of every method of a saved assembly in the order the runtime requires: a clause nested
    /// in another one's try block, filter or handler comes before it (ECMA-335 II.19). PersistedAssemblyBuilder (.NET 10
    /// and 11) writes a try block nested in a handler after the clause of that handler, and the JIT then rejects the whole
    /// method (InvalidProgramException at its first call; ILVerify: StackUnderflow at the inner filter). Ruby's ensure
    /// compiles to such shapes - since #204 every ensure clause that runs Ruby code has a try with a filter (the one that
    /// sets $!'s cause) inside the finally handler - and 43 methods of the startup library, IO.pipe, Fiber's block,
    /// Tempfile.create among them, could not be compiled.
    ///
    /// The clauses keep their contents; only their order in the table changes, in place, so no offset moves.
    /// </summary>
    internal static class ExceptionClauses {
        private sealed class Clause {
            public byte[] Bytes;
            public int TryStart, TryEnd, HandlerStart, HandlerEnd, FilterStart; // FilterStart -1 unless a filter
        }

        /// <summary>Reorders the clauses of every method in the file; returns how many methods it changed.</summary>
        public static int Reorder(string path) {
            byte[] image = File.ReadAllBytes(path);
            int changed = 0;
            using (var pe = new PEReader(new MemoryStream(image))) {
                var md = pe.GetMetadataReader();
                foreach (var handle in md.MethodDefinitions) {
                    int rva = md.GetMethodDefinition(handle).RelativeVirtualAddress;
                    if (rva == 0) continue;
                    int offset = FileOffset(pe.PEHeaders, rva);
                    if (offset >= 0 && ReorderBody(image, offset)) changed++;
                }
            }
            if (changed > 0) File.WriteAllBytes(path, image);
            return changed;
        }

        private static int FileOffset(PEHeaders headers, int rva) {
            foreach (var s in headers.SectionHeaders) {
                if (rva >= s.VirtualAddress && rva < s.VirtualAddress + Math.Max(s.VirtualSize, s.SizeOfRawData)) {
                    return rva - s.VirtualAddress + s.PointerToRawData;
                }
            }
            return -1;
        }

        private static bool ReorderBody(byte[] image, int body) {
            // a tiny header (low bits 10) has no sections; a fat one (11) has them if MoreSects (0x8) is set
            if ((image[body] & 3) != 3) return false;
            ushort flags = BitConverter.ToUInt16(image, body);
            if ((flags & 0x8) == 0) return false;
            int headerSize = (flags >> 12) * 4;
            int codeSize = BitConverter.ToInt32(image, body + 4);
            int section = (body + headerSize + codeSize + 3) & ~3;
            bool changed = false;
            while (true) {
                byte kind = image[section];
                bool fat = (kind & 0x40) != 0;
                int size = fat ? (BitConverter.ToInt32(image, section) >> 8) & 0xFFFFFF : image[section + 1];
                if ((kind & 0x3F) == 0x01) { // CorILMethod_Sect_EHTable
                    changed |= ReorderTable(image, section + 4, (size - 4) / (fat ? 24 : 12), fat);
                }
                if ((kind & 0x80) == 0) break; // CorILMethod_Sect_MoreSects
                section = (section + size + 3) & ~3;
            }
            return changed;
        }

        private static bool ReorderTable(byte[] image, int start, int count, bool fat) {
            if (count < 2) return false;
            int entry = fat ? 24 : 12;
            var clauses = new List<Clause>(count);
            for (int i = 0; i < count; i++) {
                int at = start + i * entry;
                var c = new Clause { Bytes = new byte[entry] };
                Array.Copy(image, at, c.Bytes, 0, entry);
                int kind, tryOffset, tryLength, handlerOffset, handlerLength, extra;
                if (fat) {
                    kind = BitConverter.ToInt32(image, at);
                    tryOffset = BitConverter.ToInt32(image, at + 4);
                    tryLength = BitConverter.ToInt32(image, at + 8);
                    handlerOffset = BitConverter.ToInt32(image, at + 12);
                    handlerLength = BitConverter.ToInt32(image, at + 16);
                    extra = BitConverter.ToInt32(image, at + 20);
                } else {
                    kind = BitConverter.ToUInt16(image, at);
                    tryOffset = BitConverter.ToUInt16(image, at + 2);
                    tryLength = image[at + 4];
                    handlerOffset = BitConverter.ToUInt16(image, at + 5);
                    handlerLength = image[at + 7];
                    extra = BitConverter.ToInt32(image, at + 8);
                }
                c.TryStart = tryOffset; c.TryEnd = tryOffset + tryLength;
                c.HandlerStart = handlerOffset; c.HandlerEnd = handlerOffset + handlerLength;
                c.FilterStart = (kind & 1) != 0 ? extra : -1; // COR_ILEXCEPTION_CLAUSE_FILTER
                clauses.Add(c);
            }

            // a stable order in which no clause comes after one it is nested in: repeatedly the first clause left that has
            // no other clause left nested in it
            var ordered = new List<Clause>(count);
            var left = new List<Clause>(clauses);
            while (left.Count > 0) {
                int pick = 0;
                for (int i = 0; i < left.Count; i++) {
                    bool enclosing = false;
                    for (int j = 0; j < left.Count && !enclosing; j++) {
                        enclosing = j != i && IsNestedIn(left[j], left[i]);
                    }
                    if (!enclosing) { pick = i; break; }
                }
                ordered.Add(left[pick]);
                left.RemoveAt(pick);
            }

            bool changed = false;
            for (int i = 0; i < count; i++) {
                if (!ReferenceEquals(ordered[i], clauses[i])) changed = true;
                Array.Copy(ordered[i].Bytes, 0, image, start + i * entry, entry);
            }
            return changed;
        }

        /// <summary>
        /// Whether <paramref name="inner"/> has to come before <paramref name="outer"/>: its try block lies within the
        /// other's try block (and is not the same one: the clauses of one try block keep their order), filter or handler.
        /// </summary>
        private static bool IsNestedIn(Clause inner, Clause outer) {
            bool sameTry = inner.TryStart == outer.TryStart && inner.TryEnd == outer.TryEnd;
            if (!sameTry && Within(inner, outer.TryStart, outer.TryEnd)) return true;
            if (Within(inner, outer.HandlerStart, outer.HandlerEnd)) return true;
            return outer.FilterStart >= 0 && Within(inner, outer.FilterStart, outer.HandlerStart);
        }

        private static bool Within(Clause c, int start, int end) => c.TryStart >= start && c.TryEnd <= end;
    }
}
