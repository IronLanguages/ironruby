/* ****************************************************************************
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A
 * copy of the license can be found in the License.html file at the root of this distribution. If
 * you cannot locate the  Apache License, Version 2.0, please send an email to
 * ironruby@microsoft.com. By using this source code in any fashion, you are agreeing to be bound
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 * ***************************************************************************/

using System;
using System.Reflection;
using IronRuby.Builtins;
using IronRuby.Prism;
using IronRuby.Prism.Ast;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;

namespace IronRuby.StandardLibrary.Ripper {

    /// <summary>
    /// The two primitives Ripper is built on, both of them prism's: its token stream and
    /// its syntax tree. Everything Ripper-shaped - MRI's event names, its s-expressions
    /// and its lexer state objects - is Ruby, in Src/StdLib/ironruby/ripper.rb. This is
    /// the same arrangement CRuby 4.0 uses, where Ripper is Prism::Translation::Ripper.
    ///
    /// The tree is handed over generically (a Hash per node, keyed by prism's own field
    /// names, values raw byte offsets) rather than as typed objects, so that adding
    /// Ripper coverage for another node never needs a C# change.
    /// </summary>
    [RubyClass("Ripper")]
    public static class RipperOps {

        /// <summary>
        /// prism's tokens as [type, start, length, lex_state]; offsets are byte offsets
        /// into the source, which is what Ripper reports columns in.
        /// </summary>
        [RubyMethod("__lex__", RubyMethodAttributes.PublicSingleton)]
        public static RubyArray/*!*/ Lex(RubyClass/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ source) {
            var result = new RubyArray();
            foreach (var token in PrismLex.Lex(source.ToByteArray())) {
                var entry = new RubyArray(4);
                entry.Add(MutableString.CreateAscii(token.Type));
                entry.Add(token.Start);
                entry.Add(token.Length);
                entry.Add(token.LexState);
                result.Add(entry);
            }
            return result;
        }

        /// <summary>
        /// prism's tree, or nil when the source does not parse. Each node is a Hash with
        /// "type", "start", "length", "flags" and one entry per prism field.
        /// </summary>
        [RubyMethod("__parse__", RubyMethodAttributes.PublicSingleton)]
        public static Hash Parse(RubyClass/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ source) {
            PrismParseResult result = PrismParser.ParseBytes(source.ToByteArray());
            if (result.Errors != null && result.Errors.Count > 0) {
                return null;
            }
            return (Hash)ToRuby(self.Context, result.Root);
        }

        private static object ToRuby(RubyContext/*!*/ context, PmNode node) {
            if (node == null) {
                return null;
            }

            string name = node.GetType().Name;
            var hash = new Hash(context);
            hash[MutableString.CreateAscii("type")] = MutableString.CreateAscii(name);
            hash[MutableString.CreateAscii("start")] = node.StartOffset;
            hash[MutableString.CreateAscii("length")] = node.Length;
            hash[MutableString.CreateAscii("flags")] = (int)node.Flags;

            string[][] fields;
            if (PrismMeta.NodeFields.TryGetValue(name, out fields)) {
                Type type = node.GetType();
                foreach (string[] field in fields) {
                    FieldInfo info = type.GetField(field[1]);
                    hash[MutableString.CreateAscii(field[0])] = ToRuby(context, info.GetValue(node));
                }
            }
            return hash;
        }

        private static object ToRuby(RubyContext/*!*/ context, object value) {
            if (value == null) {
                return null;
            }

            var node = value as PmNode;
            if (node != null) {
                return ToRuby(context, node);
            }

            // A location? field arrives boxed as a PmLocation, or as null when unset.
            if (value is PmLocation) {
                return Location((PmLocation)value);
            }

            var nodes = value as PmNode[];
            if (nodes != null) {
                var list = new RubyArray(nodes.Length);
                foreach (PmNode child in nodes) {
                    list.Add(ToRuby(context, child));
                }
                return list;
            }

            var strings = value as string[];
            if (strings != null) {
                var list = new RubyArray(strings.Length);
                foreach (string item in strings) {
                    list.Add(item != null ? MutableString.CreateMutable(item, RubyEncoding.UTF8) : null);
                }
                return list;
            }

            var bytes = value as byte[];
            if (bytes != null) {
                return MutableString.CreateBinary(bytes);
            }

            var str = value as string;
            if (str != null) {
                return MutableString.CreateMutable(str, RubyEncoding.UTF8);
            }

            if (value is byte) {
                return (int)(byte)value;
            }

            if (value is uint) {
                return unchecked((int)(uint)value);
            }

            return value;
        }

        private static RubyArray/*!*/ Location(PmLocation location) {
            var pair = new RubyArray(2);
            pair.Add(location.Start);
            pair.Add(location.Length);
            return pair;
        }
    }
}
