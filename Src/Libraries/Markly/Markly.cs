/* ****************************************************************************
 *
 * IronRuby's implementation of the markly gem's C extension (markly/markly.so).
 *
 * markly 0.15.2 - the CommonMark/GFM parser Rails apps use for user-written
 * Markdown - is Ruby over one C extension, and that extension is two things:
 * GitHub's cmark-gfm (~15,000 lines of C that know nothing about Ruby) and
 * markly.c, 1,255 lines that bind it to Ruby.  This file is markly.c, in C#,
 * method for method: Markly.extensions, Markly::Error, Markly::Parser (new,
 * enable, parse) and every Markly::Node method markly.c defines, with the same
 * argument checks, the same error messages and the same string encodings
 * (render output, url, title, fence_info and type_string are ASCII-8BIT, as
 * rb_str_new2 makes them; string_content is UTF-8).
 *
 * cmark-gfm itself is not ported.  It runs as native code - libmarkly_cmark,
 * built by Util/build-markly.sh from the markly gem's own ext/markly sources
 * (pinned by SHA-256) - so a document renders to the same bytes it does on
 * CRuby, by construction rather than by care.  A handful of things markly.c does
 * by reaching into cmark's structs are in native/markly_shim.c, compiled into
 * the same library.
 *
 * Only the extension is provided: Src/StdLib/ironruby/markly/markly.rb loads
 * this assembly, and the installed gem's own lib/markly.rb, node.rb, flags.rb
 * and renderers load on top of it, whatever version the lockfile names.
 *
 * Memory.  markly.c lets CRuby's GC decide when a parsed tree is freed: a
 * wrapper's dmark marks its parent's wrapper, and a parentless node is freed with
 * its last wrapper.  Here every native tree - the subtree under one parentless
 * node - is owned by a Tree object whose finalizer frees it, every Node wrapper
 * holds its Tree, and the Tree holds every wrapper made for one of its nodes
 * (which is also what makes `node.first_child.equal?(node.first_child)` hold, as
 * the user_data cache does in markly.c).  So a tree lives exactly as long as
 * any wrapper of any of its nodes, and native memory is never freed under a
 * live wrapper.  The operations that move nodes between trees - insert_before,
 * insert_after, prepend_child, append_child, replace, delete - re-home the
 * wrappers of the subtree they moved.
 *
 * This source code is subject to terms and conditions of the Apache License,
 * Version 2.0. A copy of the license can be found in the License.html file at
 * the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IronRuby.Builtins;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;

namespace IronRuby.StandardLibrary.Markly {

    /// <summary>cmark-gfm's exported API and markly_shim.c, from libmarkly_cmark.</summary>
    internal static class CmarkNative {
        private const string Lib = "markly_cmark";

        // The node types markly.c names (cmark-gfm.h).
        internal const int NODE_DOCUMENT = 0x8001;
        internal const int NODE_BLOCK_QUOTE = 0x8002;
        internal const int NODE_LIST = 0x8003;
        internal const int NODE_ITEM = 0x8004;
        internal const int NODE_CODE_BLOCK = 0x8005;
        internal const int NODE_HTML_BLOCK = 0x8006;
        internal const int NODE_CUSTOM_BLOCK = 0x8007;
        internal const int NODE_PARAGRAPH = 0x8008;
        internal const int NODE_HEADING = 0x8009;
        internal const int NODE_THEMATIC_BREAK = 0x800a;
        internal const int NODE_FOOTNOTE_DEFINITION = 0x800b;
        internal const int NODE_TEXT = 0xc001;
        internal const int NODE_SOFTBREAK = 0xc002;
        internal const int NODE_LINEBREAK = 0xc003;
        internal const int NODE_CODE = 0xc004;
        internal const int NODE_HTML_INLINE = 0xc005;
        internal const int NODE_CUSTOM_INLINE = 0xc006;
        internal const int NODE_EMPH = 0xc007;
        internal const int NODE_STRONG = 0xc008;
        internal const int NODE_LINK = 0xc009;
        internal const int NODE_IMAGE = 0xc00a;
        internal const int NODE_FOOTNOTE_REFERENCE = 0xc00b;

        internal const int BULLET_LIST = 1;
        internal const int ORDERED_LIST = 2;

        /// <summary>The markly_shim_version this file was written against.</summary>
        internal const int ShimVersion = 1;

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int markly_shim_version();
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern void markly_shim_init();
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr markly_shim_extension_type_string(IntPtr node);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr markly_shim_escape_href(byte[] src, int size);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr markly_shim_escape_html(byte[] src, int size);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern void markly_shim_free(IntPtr ptr);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr markly_shim_extension_names();
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr markly_shim_extension_list(IntPtr[] exts, int count);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern UIntPtr markly_shim_strlen(IntPtr str);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern void markly_shim_free_extension_list(IntPtr list);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr cmark_get_default_mem_allocator();
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr cmark_parser_new_with_mem(int options, IntPtr mem);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern void cmark_parser_free(IntPtr parser);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern void cmark_parser_feed(IntPtr parser, byte[] buffer, UIntPtr len);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr cmark_parser_finish(IntPtr parser);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int cmark_parser_attach_syntax_extension(IntPtr parser, IntPtr extension);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr cmark_find_syntax_extension(byte[] name);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr cmark_node_new(int type);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern void cmark_node_free(IntPtr node);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr cmark_node_next(IntPtr node);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr cmark_node_previous(IntPtr node);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr cmark_node_parent(IntPtr node);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr cmark_node_first_child(IntPtr node);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr cmark_node_last_child(IntPtr node);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr cmark_node_parent_footnote_def(IntPtr node);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int cmark_node_get_type(IntPtr node);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr cmark_node_get_type_string(IntPtr node);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr cmark_node_get_literal(IntPtr node);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int cmark_node_set_literal(IntPtr node, byte[] content);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int cmark_node_get_heading_level(IntPtr node);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int cmark_node_set_heading_level(IntPtr node, int level);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int cmark_node_get_list_type(IntPtr node);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int cmark_node_set_list_type(IntPtr node, int type);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int cmark_node_get_list_start(IntPtr node);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int cmark_node_set_list_start(IntPtr node, int start);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int cmark_node_get_list_tight(IntPtr node);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int cmark_node_set_list_tight(IntPtr node, int tight);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr cmark_node_get_fence_info(IntPtr node);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int cmark_node_set_fence_info(IntPtr node, byte[] info);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr cmark_node_get_url(IntPtr node);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int cmark_node_set_url(IntPtr node, byte[] url);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr cmark_node_get_title(IntPtr node);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int cmark_node_set_title(IntPtr node, byte[] title);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int cmark_node_get_start_line(IntPtr node);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int cmark_node_get_start_column(IntPtr node);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int cmark_node_get_end_line(IntPtr node);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int cmark_node_get_end_column(IntPtr node);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern void cmark_node_unlink(IntPtr node);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int cmark_node_insert_before(IntPtr node, IntPtr sibling);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int cmark_node_insert_after(IntPtr node, IntPtr sibling);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int cmark_node_replace(IntPtr oldnode, IntPtr newnode);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int cmark_node_prepend_child(IntPtr node, IntPtr child);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int cmark_node_append_child(IntPtr node, IntPtr child);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr cmark_render_html(IntPtr root, int options, IntPtr extensions);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr cmark_render_commonmark(IntPtr root, int options, int width);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr cmark_render_plaintext(IntPtr root, int options, int width);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern ushort cmark_gfm_extensions_get_table_columns(IntPtr node);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr cmark_gfm_extensions_get_table_alignments(IntPtr node);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern byte cmark_gfm_extensions_get_tasklist_item_checked(IntPtr node);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] internal static extern int cmark_gfm_extensions_set_tasklist_item_checked(IntPtr node, byte isChecked);
    }

    [RubyModule("Markly")]
    public static class MarklyOps {

        #region Native library

        private static readonly object _initLock = new object();
        private static volatile bool _initialized;

        /// <summary>
        /// Init_markly's cmark_gfm_core_extensions_ensure_registered, and the point where a
        /// missing or stale libmarkly_cmark turns into a LoadError (markly/markly.rb calls
        /// Markly.extensions while it is being required, so it is that require that fails).
        /// </summary>
        internal static void EnsureInitialized() {
            if (_initialized) {
                return;
            }
            lock (_initLock) {
                if (_initialized) {
                    return;
                }
                int version;
                try {
                    version = CmarkNative.markly_shim_version();
                } catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException || e is BadImageFormatException) {
                    throw RubyExceptions.CreateLoadError(
                        "cannot load such file -- markly/markly (IronRuby runs markly's cmark-gfm as a native library, " +
                        "libmarkly_cmark, and it was not found: build it with Util/build-markly.sh; " + e.Message + ")");
                }
                if (version != CmarkNative.ShimVersion) {
                    throw RubyExceptions.CreateLoadError(String.Format(
                        "cannot load such file -- markly/markly (libmarkly_cmark is shim version {0}, this IronRuby expects {1}: " +
                        "rebuild it with Util/build-markly.sh)", version, CmarkNative.ShimVersion));
                }
                CmarkNative.markly_shim_init();
                _initialized = true;
            }
        }

        #endregion

        #region Strings

        /// <summary>rb_str_new2: the bytes up to the first NUL, as an ASCII-8BIT string.</summary>
        internal static MutableString/*!*/ CStringToBinary(IntPtr ptr) {
            return MutableString.CreateBinary(CStringBytes(ptr), RubyEncoding.Binary);
        }

        internal static byte[]/*!*/ CStringBytes(IntPtr ptr) {
            var result = new byte[checked((int)(ulong)CmarkNative.markly_shim_strlen(ptr))];
            Marshal.Copy(ptr, result, 0, result.Length);
            return result;
        }

        /// <summary>A string's bytes followed by a NUL, so the array is never empty (and never passed as NULL).</summary>
        internal static byte[]/*!*/ Terminated(byte[]/*!*/ bytes) {
            var result = new byte[bytes.Length + 1];
            Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
            return result;
        }

        /// <summary>A buffer cmark allocated, as an ASCII-8BIT string; the buffer is freed.</summary>
        internal static MutableString/*!*/ TakeBinary(IntPtr ptr) {
            try {
                return CStringToBinary(ptr);
            } finally {
                CmarkNative.markly_shim_free(ptr);
            }
        }

        /// <summary>Check_Type(value, T_STRING): a String, and no implicit conversion.</summary>
        internal static MutableString/*!*/ CheckString(RubyContext/*!*/ context, object value) {
            var str = value as MutableString;
            if (str == null) {
                throw WrongArgumentType(context, value, "String");
            }
            return str;
        }

        /// <summary>StringValueCStr: the string's bytes, NUL-terminated, refusing an embedded NUL.</summary>
        internal static byte[]/*!*/ CString(MutableString/*!*/ str) {
            byte[] bytes = str.ToByteArray();
            if (Array.IndexOf(bytes, (byte)0) >= 0) {
                throw RubyExceptions.CreateArgumentError("string contains null byte");
            }
            return Terminated(bytes);
        }

        internal static Exception/*!*/ WrongArgumentType(RubyContext/*!*/ context, object value, string/*!*/ expected) {
            return RubyExceptions.CreateTypeError(String.Format("wrong argument type {0} (expected {1})",
                value == null ? "nil" : value is bool ? ((bool)value ? "true" : "false") : context.GetClassDisplayName(value), expected));
        }

        /// <summary>Check_Type(value, T_FIXNUM) followed by FIX2INT / NUM2INT.</summary>
        internal static int CheckInt(RubyContext/*!*/ context, object value) {
            if (value is int) {
                return (int)value;
            }
            BigInteger big;
            if (value is long) {
                big = (long)value;
            } else if (value is BigInteger) {
                big = (BigInteger)value;
            } else {
                throw WrongArgumentType(context, value, "Integer");
            }
            throw RubyExceptions.CreateRangeError(String.Format("integer {0} too {1} to convert to 'int'",
                big, big.Sign < 0 ? "small" : "big"));
        }

        /// <summary>Check_Type(value, T_SYMBOL): the symbol's name.</summary>
        internal static string/*!*/ CheckSymbol(RubyContext/*!*/ context, object value) {
            var sym = value as RubySymbol;
            if (sym == null) {
                throw WrongArgumentType(context, value, "Symbol");
            }
            return sym.ToString();
        }

        internal static IntPtr FindExtension(string/*!*/ name) {
            return CmarkNative.cmark_find_syntax_extension(Terminated(System.Text.Encoding.UTF8.GetBytes(name)));
        }

        #endregion

        /// <summary>Markly.extensions: the names of the registered syntax extensions.</summary>
        [RubyMethod("extensions", RubyMethodAttributes.PublicSingleton)]
        public static RubyArray/*!*/ Extensions(RubyModule/*!*/ self) {
            EnsureInitialized();
            var result = new RubyArray();
            IntPtr names = CmarkNative.markly_shim_extension_names();
            try {
                IntPtr p = names;
                while (true) {
                    byte[] name = CStringBytes(p);
                    if (name.Length == 0) {
                        break;
                    }
                    result.Add(MutableString.CreateBinary(name, RubyEncoding.Binary));
                    p += name.Length + 1;
                }
            } finally {
                CmarkNative.markly_shim_free(names);
            }
            return result;
        }

        #region Error

        [RubyException("Error")]
        public class MarklyError : SystemException {
            public MarklyError() : this(null, null) { }
            public MarklyError(string message) : this(message, null) { }
            public MarklyError(string message, Exception inner) : base(message ?? "Markly::Error", inner) { }
        }

        #endregion

        #region Parser

        [RubyClass("Parser")]
        public sealed class Parser : RubyObject {
            private IntPtr _parser;

            public Parser(RubyClass/*!*/ rubyClass) : base(rubyClass) {
            }

            protected override RubyObject/*!*/ CreateInstance() {
                return new Parser(ImmediateClass.NominalClass);
            }

            ~Parser() {
                IntPtr parser = _parser;
                _parser = IntPtr.Zero;
                if (parser != IntPtr.Zero) {
                    CmarkNative.cmark_parser_free(parser);
                }
            }

            private IntPtr Handle {
                get {
                    if (_parser == IntPtr.Zero) {
                        // markly.c would hand cmark a NULL parser here and crash.
                        throw RubyExceptions.CreateTypeError("uninitialized Markly::Parser");
                    }
                    return _parser;
                }
            }

            [RubyConstructor]
            public static Parser/*!*/ Create(RubyClass/*!*/ self, object flags) {
                return Initialize(self.Context, new Parser(self), flags);
            }

            [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
            public static Parser/*!*/ Initialize(RubyContext/*!*/ context, Parser/*!*/ self, object flags) {
                int options = CheckInt(context, flags);
                EnsureInitialized();
                IntPtr parser = CmarkNative.cmark_parser_new_with_mem(options, CmarkNative.cmark_get_default_mem_allocator());
                IntPtr old = self._parser;
                self._parser = parser;
                if (old != IntPtr.Zero) {
                    CmarkNative.cmark_parser_free(old);
                }
                return self;
            }

            [RubyMethod("enable")]
            public static object Enable(RubyContext/*!*/ context, Parser/*!*/ self, object extension) {
                string name = CheckSymbol(context, extension);
                IntPtr parser = self.Handle;
                IntPtr ext = FindExtension(name);
                if (ext == IntPtr.Zero) {
                    throw RubyExceptions.CreateArgumentError(String.Format("extension {0} not found", name));
                }
                CmarkNative.cmark_parser_attach_syntax_extension(parser, ext);
                GC.KeepAlive(self);
                return null;
            }

            [RubyMethod("parse")]
            public static object Parse(RubyContext/*!*/ context, Parser/*!*/ self, [DefaultProtocol]MutableString text) {
                if (text == null) {
                    throw RubyExceptions.CreateTypeError("no implicit conversion of nil into String");
                }
                IntPtr parser = self.Handle;
                byte[] bytes = text.ToByteArray();
                CmarkNative.cmark_parser_feed(parser, Terminated(bytes), (UIntPtr)bytes.Length);
                IntPtr root = CmarkNative.cmark_parser_finish(parser);
                GC.KeepAlive(self);
                if (root == IntPtr.Zero) {
                    return null;
                }
                return Tree.Adopt(context, root);
            }
        }

        #endregion

        #region Tree

        /// <summary>
        /// One native tree: the subtree under a parentless node, which this object frees
        /// when nothing refers to it any more, and the wrappers made for its nodes.
        /// </summary>
        internal sealed class Tree {
            /// <summary>Guards every Tree's wrapper table and the moves between trees.</summary>
            internal static readonly object Lock = new object();

            /// <summary>The parentless node this tree frees; zero once it became part of another tree.</summary>
            private IntPtr _root;
            internal readonly Dictionary<IntPtr, Node>/*!*/ Wrappers = new Dictionary<IntPtr, Node>();
            internal readonly RubyClass/*!*/ NodeClass;

            private Tree(RubyClass/*!*/ nodeClass, IntPtr root) {
                NodeClass = nodeClass;
                _root = root;
            }

            ~Tree() {
                IntPtr root = _root;
                _root = IntPtr.Zero;
                if (root != IntPtr.Zero) {
                    CmarkNative.cmark_node_free(root);
                }
            }

            /// <summary>A wrapper for a freshly made parentless node, which the new tree owns.</summary>
            internal static Node/*!*/ Adopt(RubyContext/*!*/ context, IntPtr root) {
                var tree = new Tree(context.GetClass(typeof(Node)), root);
                return tree.Wrap(root);
            }

            /// <summary>rb_Markly_Node_wrap: the one wrapper of a node of this tree.</summary>
            internal Node/*!*/ Wrap(IntPtr node) {
                lock (Lock) {
                    Node result;
                    if (!Wrappers.TryGetValue(node, out result)) {
                        result = new Node(NodeClass, node, this);
                        Wrappers.Add(node, result);
                    }
                    return result;
                }
            }

            /// <summary>
            /// The subtree under <paramref name="node"/> (whose wrapper is <paramref name="moved"/>)
            /// now hangs in <paramref name="target"/>'s native tree; it used to be in
            /// <paramref name="moved"/>'s.  Hand its wrappers over, and if it was a whole tree,
            /// make sure that tree no longer frees it.  Call with Lock held.
            /// </summary>
            internal static void Rehome(Node/*!*/ moved, Tree/*!*/ target) {
                Tree source = moved.Tree;
                if (source == target) {
                    return;
                }
                if (source._root == moved.Pointer) {
                    // The whole of source went: every wrapper it has is in the moved subtree.
                    source._root = IntPtr.Zero;
                    GC.SuppressFinalize(source);
                    foreach (var entry in source.Wrappers) {
                        entry.Value.Tree = target;
                        target.Wrappers[entry.Key] = entry.Value;
                    }
                    source.Wrappers.Clear();
                } else {
                    MoveSubtree(moved.Pointer, source, target);
                }
            }

            /// <summary>
            /// <paramref name="detached"/> has just been unlinked from its tree and is
            /// parentless now: give it a tree of its own.  Call with Lock held.
            /// </summary>
            internal static void Detach(Node/*!*/ detached) {
                Tree source = detached.Tree;
                if (source._root == detached.Pointer) {
                    return;
                }
                var tree = new Tree(source.NodeClass, detached.Pointer);
                MoveSubtree(detached.Pointer, source, tree);
            }

            private static void MoveSubtree(IntPtr top, Tree/*!*/ source, Tree/*!*/ target) {
                if (source.Wrappers.Count == 0) {
                    return;
                }
                IntPtr current = top;
                while (true) {
                    Node wrapper;
                    if (source.Wrappers.TryGetValue(current, out wrapper)) {
                        source.Wrappers.Remove(current);
                        wrapper.Tree = target;
                        target.Wrappers[current] = wrapper;
                        if (source.Wrappers.Count == 0) {
                            return;
                        }
                    }
                    IntPtr child = CmarkNative.cmark_node_first_child(current);
                    if (child != IntPtr.Zero) {
                        current = child;
                        continue;
                    }
                    while (current != top && CmarkNative.cmark_node_next(current) == IntPtr.Zero) {
                        current = CmarkNative.cmark_node_parent(current);
                    }
                    if (current == top) {
                        return;
                    }
                    current = CmarkNative.cmark_node_next(current);
                }
            }
        }

        #endregion

        #region Node

        [RubyClass("Node")]
        public sealed class Node : RubyObject {
            internal readonly IntPtr Pointer;
            internal Tree/*!*/ Tree;

            // Not public: Markly::Node has no allocator (rb_undef_alloc_func), so
            // Node.allocate and #clone raise as they do on CRuby.  Node.new is the
            // singleton method below.
            internal Node(RubyClass/*!*/ rubyClass, IntPtr pointer, Tree/*!*/ tree) : base(rubyClass) {
                Pointer = pointer;
                Tree = tree;
            }

            protected override RubyObject/*!*/ CreateInstance() {
                throw RubyExceptions.CreateAllocatorUndefinedError(ImmediateClass.NominalClass);
            }

            private Node WrapRelative(IntPtr node) {
                return node == IntPtr.Zero ? null : Tree.Wrap(node);
            }

            private static Node/*!*/ CheckNode(RubyContext/*!*/ context, object value) {
                var node = value as Node;
                if (node == null) {
                    throw WrongArgumentType(context, value, "Markly::Node");
                }
                return node;
            }

            private static readonly Dictionary<string, int>/*!*/ _typesByName = new Dictionary<string, int> {
                { "document", CmarkNative.NODE_DOCUMENT },
                { "custom_block", CmarkNative.NODE_CUSTOM_BLOCK },
                { "custom_inline", CmarkNative.NODE_CUSTOM_INLINE },
                { "blockquote", CmarkNative.NODE_BLOCK_QUOTE },
                { "list", CmarkNative.NODE_LIST },
                { "list_item", CmarkNative.NODE_ITEM },
                { "code_block", CmarkNative.NODE_CODE_BLOCK },
                { "html", CmarkNative.NODE_HTML_BLOCK },
                { "paragraph", CmarkNative.NODE_PARAGRAPH },
                { "header", CmarkNative.NODE_HEADING },
                { "hrule", CmarkNative.NODE_THEMATIC_BREAK },
                { "text", CmarkNative.NODE_TEXT },
                { "softbreak", CmarkNative.NODE_SOFTBREAK },
                { "linebreak", CmarkNative.NODE_LINEBREAK },
                { "code", CmarkNative.NODE_CODE },
                { "inline_html", CmarkNative.NODE_HTML_INLINE },
                { "emph", CmarkNative.NODE_EMPH },
                { "strong", CmarkNative.NODE_STRONG },
                { "link", CmarkNative.NODE_LINK },
                { "image", CmarkNative.NODE_IMAGE },
                { "footnote_reference", CmarkNative.NODE_FOOTNOTE_REFERENCE },
                { "footnote_definition", CmarkNative.NODE_FOOTNOTE_DEFINITION },
            };

            private static readonly Dictionary<int, string>/*!*/ _namesByType = new Dictionary<int, string>();

            static Node() {
                foreach (var entry in _typesByName) {
                    _namesByType.Add(entry.Value, entry.Key);
                }
            }

            #region Creation and identity

            /// <summary>rb_node_new.</summary>
            [RubyMethod("new", RubyMethodAttributes.PublicSingleton)]
            public static Node/*!*/ New(RubyContext/*!*/ context, RubyClass/*!*/ self, object type) {
                string name = CheckSymbol(context, type);
                int nodeType;
                if (!_typesByName.TryGetValue(name, out nodeType)) {
                    throw new MarklyError("invalid node of type 0");
                }
                EnsureInitialized();
                IntPtr node = CmarkNative.cmark_node_new(nodeType);
                if (node == IntPtr.Zero) {
                    throw new MarklyError(String.Format("could not create node of type {0}", nodeType));
                }
                return Tree.Adopt(context, node);
            }

            #endregion

            #region Tree structure

            [RubyMethod("first_child")]
            public static Node FirstChild(Node/*!*/ self) {
                var result = self.WrapRelative(CmarkNative.cmark_node_first_child(self.Pointer));
                GC.KeepAlive(self);
                return result;
            }

            [RubyMethod("last_child")]
            public static Node LastChild(Node/*!*/ self) {
                var result = self.WrapRelative(CmarkNative.cmark_node_last_child(self.Pointer));
                GC.KeepAlive(self);
                return result;
            }

            [RubyMethod("next")]
            public static Node Next(Node/*!*/ self) {
                var result = self.WrapRelative(CmarkNative.cmark_node_next(self.Pointer));
                GC.KeepAlive(self);
                return result;
            }

            [RubyMethod("previous")]
            public static Node Previous(Node/*!*/ self) {
                var result = self.WrapRelative(CmarkNative.cmark_node_previous(self.Pointer));
                GC.KeepAlive(self);
                return result;
            }

            [RubyMethod("parent")]
            public static Node Parent(Node/*!*/ self) {
                var result = self.WrapRelative(CmarkNative.cmark_node_parent(self.Pointer));
                GC.KeepAlive(self);
                return result;
            }

            [RubyMethod("parent_footnote_def")]
            public static Node ParentFootnoteDef(Node/*!*/ self) {
                var result = self.WrapRelative(CmarkNative.cmark_node_parent_footnote_def(self.Pointer));
                GC.KeepAlive(self);
                return result;
            }

            /// <summary>
            /// The insertions: <paramref name="other"/>'s subtree leaves wherever it was (its own
            /// tree, or a place in another) and joins self's tree.
            /// </summary>
            private static bool Insert(Node/*!*/ self, Node/*!*/ other, Func<IntPtr, IntPtr, int>/*!*/ operation) {
                lock (Tree.Lock) {
                    if (operation(self.Pointer, other.Pointer) == 0) {
                        return false;
                    }
                    Tree.Rehome(other, self.Tree);
                }
                GC.KeepAlive(self);
                GC.KeepAlive(other);
                return true;
            }

            [RubyMethod("insert_before")]
            public static bool InsertBefore(RubyContext/*!*/ context, Node/*!*/ self, object sibling) {
                if (!Insert(self, CheckNode(context, sibling), CmarkNative.cmark_node_insert_before)) {
                    throw new MarklyError("could not insert before");
                }
                return true;
            }

            [RubyMethod("insert_after")]
            public static bool InsertAfter(RubyContext/*!*/ context, Node/*!*/ self, object sibling) {
                if (!Insert(self, CheckNode(context, sibling), CmarkNative.cmark_node_insert_after)) {
                    throw new MarklyError("could not insert after");
                }
                return true;
            }

            [RubyMethod("prepend_child")]
            public static bool PrependChild(RubyContext/*!*/ context, Node/*!*/ self, object child) {
                if (!Insert(self, CheckNode(context, child), CmarkNative.cmark_node_prepend_child)) {
                    throw new MarklyError("could not prepend child");
                }
                return true;
            }

            [RubyMethod("append_child")]
            public static bool AppendChild(RubyContext/*!*/ context, Node/*!*/ self, object child) {
                if (!Insert(self, CheckNode(context, child), CmarkNative.cmark_node_append_child)) {
                    throw new MarklyError("could not append child");
                }
                return true;
            }

            /// <summary>
            /// rb_node_replace.  cmark_node_replace is insert_after(self, other) followed by
            /// unlinking self, so other joins self's tree and self gets a tree of its own.
            /// </summary>
            [RubyMethod("replace")]
            public static object Replace(RubyContext/*!*/ context, Node/*!*/ self, object other) {
                Node replacement = CheckNode(context, other);
                lock (Tree.Lock) {
                    Tree tree = self.Tree;
                    if (CmarkNative.cmark_node_replace(self.Pointer, replacement.Pointer) == 0) {
                        throw new MarklyError("could not replace node");
                    }
                    Tree.Rehome(replacement, tree);
                    Tree.Detach(self);
                }
                GC.KeepAlive(self);
                GC.KeepAlive(replacement);
                return other;
            }

            /// <summary>rb_node_unlink.</summary>
            [RubyMethod("delete")]
            public static object Delete(Node/*!*/ self) {
                lock (Tree.Lock) {
                    CmarkNative.cmark_node_unlink(self.Pointer);
                    Tree.Detach(self);
                }
                GC.KeepAlive(self);
                return null;
            }

            #endregion

            #region Type and position

            /// <summary>
            /// #type's symbol for each of cmark's own node types, per context: the renderers and
            /// walkers ask for it on every node.  An extension's node is not cached - its name can
            /// depend on the node (a table row is :table_header or :table_row).
            /// </summary>
            private static readonly ConditionalWeakTable<RubyContext, Dictionary<int, RubySymbol>>/*!*/ _typeSymbols =
                new ConditionalWeakTable<RubyContext, Dictionary<int, RubySymbol>>();

            private static Dictionary<int, RubySymbol>/*!*/ MakeTypeSymbols(RubyContext/*!*/ context) {
                var result = new Dictionary<int, RubySymbol>();
                foreach (var entry in _namesByType) {
                    result.Add(entry.Key, context.CreateAsciiSymbol(entry.Value));
                }
                return result;
            }

            [RubyMethod("type")]
            public static RubySymbol/*!*/ GetNodeType(RubyContext/*!*/ context, Node/*!*/ self) {
                int type = CmarkNative.cmark_node_get_type(self.Pointer);
                RubySymbol result;
                if (_typeSymbols.GetValue(context, MakeTypeSymbols).TryGetValue(type, out result)) {
                    GC.KeepAlive(self);
                    return result;
                }
                IntPtr ext = CmarkNative.markly_shim_extension_type_string(self.Pointer);
                if (ext == IntPtr.Zero) {
                    GC.KeepAlive(self);
                    throw new MarklyError(String.Format("invalid node type {0}", type));
                }
                string name = System.Text.Encoding.UTF8.GetString(CStringBytes(ext));
                GC.KeepAlive(self);
                return context.CreateAsciiSymbol(name);
            }

            [RubyMethod("type_string")]
            public static MutableString/*!*/ GetTypeString(Node/*!*/ self) {
                var result = CStringToBinary(CmarkNative.cmark_node_get_type_string(self.Pointer));
                GC.KeepAlive(self);
                return result;
            }

            [RubyMethod("source_position")]
            public static Hash/*!*/ GetSourcePosition(RubyContext/*!*/ context, Node/*!*/ self) {
                int startLine = CmarkNative.cmark_node_get_start_line(self.Pointer);
                int startColumn = CmarkNative.cmark_node_get_start_column(self.Pointer);
                int endLine = CmarkNative.cmark_node_get_end_line(self.Pointer);
                int endColumn = CmarkNative.cmark_node_get_end_column(self.Pointer);
                GC.KeepAlive(self);

                var result = new Hash(context);
                result[context.CreateAsciiSymbol("start_line")] = ScriptingRuntimeHelpers.Int32ToObject(startLine);
                result[context.CreateAsciiSymbol("start_column")] = ScriptingRuntimeHelpers.Int32ToObject(startColumn);
                result[context.CreateAsciiSymbol("end_line")] = ScriptingRuntimeHelpers.Int32ToObject(endLine);
                result[context.CreateAsciiSymbol("end_column")] = ScriptingRuntimeHelpers.Int32ToObject(endColumn);
                return result;
            }

            #endregion

            #region Content

            [RubyMethod("string_content")]
            public static MutableString GetStringContent(Node/*!*/ self) {
                IntPtr text = CmarkNative.cmark_node_get_literal(self.Pointer);
                MutableString result = text == IntPtr.Zero ? null : MutableString.CreateBinary(CStringBytes(text), RubyEncoding.UTF8);
                GC.KeepAlive(self);
                return result;
            }

            private static void SetCString(RubyContext/*!*/ context, Node/*!*/ self, object value, Func<IntPtr, byte[], int>/*!*/ setter, string/*!*/ error) {
                byte[] bytes = CString(CheckString(context, value));
                int ok = setter(self.Pointer, bytes);
                GC.KeepAlive(self);
                if (ok == 0) {
                    throw new MarklyError(error);
                }
            }

            [RubyMethod("string_content=")]
            public static object SetStringContent(RubyContext/*!*/ context, Node/*!*/ self, object value) {
                SetCString(context, self, value, CmarkNative.cmark_node_set_literal, "could not set string content");
                return null;
            }

            [RubyMethod("url")]
            public static MutableString/*!*/ GetUrl(Node/*!*/ self) {
                IntPtr text = CmarkNative.cmark_node_get_url(self.Pointer);
                if (text == IntPtr.Zero) {
                    throw new MarklyError("could not get url");
                }
                var result = CStringToBinary(text);
                GC.KeepAlive(self);
                return result;
            }

            [RubyMethod("url=")]
            public static object SetUrl(RubyContext/*!*/ context, Node/*!*/ self, object value) {
                SetCString(context, self, value, CmarkNative.cmark_node_set_url, "could not set url");
                return null;
            }

            [RubyMethod("title")]
            public static MutableString/*!*/ GetTitle(Node/*!*/ self) {
                IntPtr text = CmarkNative.cmark_node_get_title(self.Pointer);
                if (text == IntPtr.Zero) {
                    throw new MarklyError("could not get title");
                }
                var result = CStringToBinary(text);
                GC.KeepAlive(self);
                return result;
            }

            [RubyMethod("title=")]
            public static object SetTitle(RubyContext/*!*/ context, Node/*!*/ self, object value) {
                SetCString(context, self, value, CmarkNative.cmark_node_set_title, "could not set title");
                return null;
            }

            [RubyMethod("fence_info")]
            public static MutableString/*!*/ GetFenceInfo(Node/*!*/ self) {
                IntPtr text = CmarkNative.cmark_node_get_fence_info(self.Pointer);
                if (text == IntPtr.Zero) {
                    throw new MarklyError("could not get fence_info");
                }
                var result = CStringToBinary(text);
                GC.KeepAlive(self);
                return result;
            }

            [RubyMethod("fence_info=")]
            public static object SetFenceInfo(RubyContext/*!*/ context, Node/*!*/ self, object value) {
                SetCString(context, self, value, CmarkNative.cmark_node_set_fence_info, "could not set fence_info");
                return null;
            }

            [RubyMethod("header_level")]
            public static int GetHeaderLevel(Node/*!*/ self) {
                int level = CmarkNative.cmark_node_get_heading_level(self.Pointer);
                GC.KeepAlive(self);
                if (level == 0) {
                    throw new MarklyError("could not get header_level");
                }
                return level;
            }

            [RubyMethod("header_level=")]
            public static object SetHeaderLevel(RubyContext/*!*/ context, Node/*!*/ self, object level) {
                int value = CheckInt(context, level);
                int ok = CmarkNative.cmark_node_set_heading_level(self.Pointer, value);
                GC.KeepAlive(self);
                if (ok == 0) {
                    throw new MarklyError("could not set header_level");
                }
                return null;
            }

            [RubyMethod("list_type")]
            public static RubySymbol/*!*/ GetListType(RubyContext/*!*/ context, Node/*!*/ self) {
                int type = CmarkNative.cmark_node_get_list_type(self.Pointer);
                GC.KeepAlive(self);
                if (type == CmarkNative.BULLET_LIST) {
                    return context.CreateAsciiSymbol("bullet_list");
                } else if (type == CmarkNative.ORDERED_LIST) {
                    return context.CreateAsciiSymbol("ordered_list");
                }
                throw new MarklyError("could not get list_type");
            }

            [RubyMethod("list_type=")]
            public static object SetListType(RubyContext/*!*/ context, Node/*!*/ self, object listType) {
                string name = CheckSymbol(context, listType);
                int type;
                if (name == "bullet_list") {
                    type = CmarkNative.BULLET_LIST;
                } else if (name == "ordered_list") {
                    type = CmarkNative.ORDERED_LIST;
                } else {
                    throw new MarklyError("invalid list_type");
                }
                int ok = CmarkNative.cmark_node_set_list_type(self.Pointer, type);
                GC.KeepAlive(self);
                if (ok == 0) {
                    throw new MarklyError("could not set list_type");
                }
                return null;
            }

            [RubyMethod("list_start")]
            public static int GetListStart(Node/*!*/ self) {
                try {
                    int listType = CmarkNative.cmark_node_get_list_type(self.Pointer);
                    if (CmarkNative.cmark_node_get_type(self.Pointer) != CmarkNative.NODE_LIST || listType != CmarkNative.ORDERED_LIST) {
                        throw new MarklyError(String.Format("can't get list_start for non-ordered list {0}", listType));
                    }
                    return CmarkNative.cmark_node_get_list_start(self.Pointer);
                } finally {
                    GC.KeepAlive(self);
                }
            }

            [RubyMethod("list_start=")]
            public static object SetListStart(RubyContext/*!*/ context, Node/*!*/ self, object start) {
                int value = CheckInt(context, start);
                int ok = CmarkNative.cmark_node_set_list_start(self.Pointer, value);
                GC.KeepAlive(self);
                if (ok == 0) {
                    throw new MarklyError("could not set list_start");
                }
                return null;
            }

            [RubyMethod("list_tight")]
            public static bool GetListTight(Node/*!*/ self) {
                try {
                    if (CmarkNative.cmark_node_get_type(self.Pointer) != CmarkNative.NODE_LIST) {
                        throw new MarklyError("can't get list_tight for non-list");
                    }
                    return CmarkNative.cmark_node_get_list_tight(self.Pointer) != 0;
                } finally {
                    GC.KeepAlive(self);
                }
            }

            [RubyMethod("list_tight=")]
            public static object SetListTight(Node/*!*/ self, object tight) {
                int ok = CmarkNative.cmark_node_set_list_tight(self.Pointer, Protocols.IsTrue(tight) ? 1 : 0);
                GC.KeepAlive(self);
                if (ok == 0) {
                    throw new MarklyError("could not set list_tight");
                }
                return null;
            }

            [RubyMethod("table_alignments")]
            public static RubyArray/*!*/ GetTableAlignments(RubyContext/*!*/ context, Node/*!*/ self) {
                try {
                    ushort columns = CmarkNative.cmark_gfm_extensions_get_table_columns(self.Pointer);
                    IntPtr alignments = CmarkNative.cmark_gfm_extensions_get_table_alignments(self.Pointer);
                    if (columns == 0 || alignments == IntPtr.Zero) {
                        throw new MarklyError("could not get column_count or alignments");
                    }
                    var result = new RubyArray(columns);
                    for (int i = 0; i < columns; i++) {
                        switch (Marshal.ReadByte(alignments, i)) {
                            case (byte)'l': result.Add(context.CreateAsciiSymbol("left")); break;
                            case (byte)'c': result.Add(context.CreateAsciiSymbol("center")); break;
                            case (byte)'r': result.Add(context.CreateAsciiSymbol("right")); break;
                            default: result.Add(null); break;
                        }
                    }
                    return result;
                } finally {
                    GC.KeepAlive(self);
                }
            }

            [RubyMethod("tasklist_item_checked?")]
            public static bool GetTasklistItemChecked(Node/*!*/ self) {
                bool result = CmarkNative.cmark_gfm_extensions_get_tasklist_item_checked(self.Pointer) == 1;
                GC.KeepAlive(self);
                return result;
            }

            [RubyMethod("tasklist_item_checked=")]
            public static bool SetTasklistItemChecked(Node/*!*/ self, object itemChecked) {
                bool state = Protocols.IsTrue(itemChecked);
                int ok = CmarkNative.cmark_gfm_extensions_set_tasklist_item_checked(self.Pointer, state ? (byte)1 : (byte)0);
                GC.KeepAlive(self);
                if (ok == 0) {
                    throw new MarklyError("could not set tasklist_item_checked");
                }
                return state;
            }

            #endregion

            #region Rendering

            private static IntPtr[]/*!*/ ResolveExtensions(RubyContext/*!*/ context, RubyArray/*!*/ extensions) {
                var result = new IntPtr[extensions.Count];
                for (int i = 0; i < result.Length; i++) {
                    var sym = extensions[i] as RubySymbol;
                    if (sym == null) {
                        throw RubyExceptions.CreateTypeError(String.Format("extension names should be Symbols; got a {0}",
                            context.GetClassName(extensions[i])));
                    }
                    string name = sym.ToString();
                    IntPtr ext = FindExtension(name);
                    if (ext == IntPtr.Zero) {
                        throw RubyExceptions.CreateArgumentError(String.Format("extension {0} not found\n", name));
                    }
                    result[i] = ext;
                }
                return result;
            }

            [RubyMethod("_render_html")]
            public static MutableString/*!*/ RenderHtml(RubyContext/*!*/ context, Node/*!*/ self, object options, object extensions) {
                if (!(options is int) && !(options is long) && !(options is BigInteger)) {
                    throw WrongArgumentType(context, options, "Integer");
                }
                var list = extensions as RubyArray;
                if (list == null) {
                    throw WrongArgumentType(context, extensions, "Array");
                }
                int flags = CheckInt(context, options);
                IntPtr[] exts = ResolveExtensions(context, list);

                IntPtr llist = CmarkNative.markly_shim_extension_list(exts, exts.Length);
                try {
                    IntPtr html = CmarkNative.cmark_render_html(self.Pointer, flags, llist);
                    GC.KeepAlive(self);
                    return TakeBinary(html);
                } finally {
                    CmarkNative.markly_shim_free_extension_list(llist);
                }
            }

            /// <summary>rb_scan_args(argc, argv, "11", &amp;options, &amp;width), width defaulting to 120.</summary>
            private static void ScanRenderArgs(RubyContext/*!*/ context, object[]/*!*/ args, out int options, out int width) {
                if (args.Length < 1 || args.Length > 2) {
                    throw RubyExceptions.CreateArgumentError(String.Format("wrong number of arguments (given {0}, expected 1..2)", args.Length));
                }
                width = 120;
                if (args.Length == 2 && args[1] != null) {
                    width = CheckInt(context, args[1]);
                }
                options = CheckInt(context, args[0]);
            }

            [RubyMethod("_render_commonmark")]
            public static MutableString/*!*/ RenderCommonMark(RubyContext/*!*/ context, Node/*!*/ self, params object[]/*!*/ args) {
                int options, width;
                ScanRenderArgs(context, args, out options, out width);
                IntPtr text = CmarkNative.cmark_render_commonmark(self.Pointer, options, width);
                GC.KeepAlive(self);
                return TakeBinary(text);
            }

            [RubyMethod("_render_plaintext")]
            public static MutableString/*!*/ RenderPlaintext(RubyContext/*!*/ context, Node/*!*/ self, params object[]/*!*/ args) {
                int options, width;
                ScanRenderArgs(context, args, out options, out width);
                IntPtr text = CmarkNative.cmark_render_plaintext(self.Pointer, options, width);
                GC.KeepAlive(self);
                return TakeBinary(text);
            }

            [RubyMethod("html_escape_href")]
            public static MutableString/*!*/ HtmlEscapeHref(RubyContext/*!*/ context, Node/*!*/ self, object text) {
                byte[] bytes = CheckString(context, text).ToByteArray();
                return TakeBinary(CmarkNative.markly_shim_escape_href(Terminated(bytes), bytes.Length));
            }

            [RubyMethod("html_escape_html")]
            public static MutableString/*!*/ HtmlEscapeHtml(RubyContext/*!*/ context, Node/*!*/ self, object text) {
                byte[] bytes = CheckString(context, text).ToByteArray();
                return TakeBinary(CmarkNative.markly_shim_escape_html(Terminated(bytes), bytes.Length));
            }

            #endregion
        }

        #endregion
    }
}
