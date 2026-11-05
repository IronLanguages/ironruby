/* ****************************************************************************
 *
 * IronRuby's implementation of the msgpack gem's C extension.
 *
 * msgpack-ruby 1.8.5 is two halves: a C extension (ext/msgpack) that defines
 * MessagePack::Buffer, Packer, Unpacker, Factory, ExtensionValue and the error classes,
 * and Ruby on top of it (lib/msgpack: Factory#register_type, Timestamp, core_ext, ...).
 * The Ruby half is vendored unchanged under Src/StdLib/ironruby/msgpack; this is the C
 * half, ported function by function from packer.c, unpacker.c, factory_class.c and the
 * ext registries so that the bytes written, the objects read back and the errors raised
 * are the same ones.  The buffer is in MessagePackBuffer.cs.
 *
 * This source code is subject to terms and conditions of the Apache License,
 * Version 2.0. A copy of the license can be found in the License.html file at
 * the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using IronRuby.Builtins;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting.Runtime;

namespace IronRuby.StandardLibrary.MessagePack {

    #region dynamic calls

    /// <summary>
    /// rb_funcall and friends: calls into Ruby from here, through call sites cached per
    /// runtime.  Visibility is ignored, as rb_funcall ignores it.
    /// </summary>
    internal sealed class Sites {
        private readonly RubyContext/*!*/ _context;
        private readonly ConcurrentDictionary<string, CallSite<Func<CallSite, object, object>>> _sites0 =
            new ConcurrentDictionary<string, CallSite<Func<CallSite, object, object>>>();
        private readonly ConcurrentDictionary<string, CallSite<Func<CallSite, object, object, object>>> _sites1 =
            new ConcurrentDictionary<string, CallSite<Func<CallSite, object, object, object>>>();
        private readonly ConcurrentDictionary<string, CallSite<Func<CallSite, object, object, object, object>>> _sites2 =
            new ConcurrentDictionary<string, CallSite<Func<CallSite, object, object, object, object>>>();

        private Sites(RubyContext/*!*/ context) {
            _context = context;
        }

        private static readonly object _key = new object();

        private static Sites/*!*/ Get(RubyContext/*!*/ context) {
            return (Sites)context.GetOrCreateLibraryData(_key, () => new Sites(context));
        }

        internal static object Call(RubyContext/*!*/ context, object target, string/*!*/ name) {
            var self = Get(context);
            var site = self._sites0.GetOrAdd(name, n => CallSite<Func<CallSite, object, object>>.Create(
                RubyCallAction.Make(self._context, n, RubyCallSignature.WithImplicitSelf(0))));
            return site.Target(site, target);
        }

        internal static object Call(RubyContext/*!*/ context, object target, string/*!*/ name, object arg1) {
            var self = Get(context);
            var site = self._sites1.GetOrAdd(name, n => CallSite<Func<CallSite, object, object, object>>.Create(
                RubyCallAction.Make(self._context, n, RubyCallSignature.WithImplicitSelf(1))));
            return site.Target(site, target, arg1);
        }

        internal static object Call(RubyContext/*!*/ context, object target, string/*!*/ name, object arg1, object arg2) {
            var self = Get(context);
            var site = self._sites2.GetOrAdd(name, n => CallSite<Func<CallSite, object, object, object, object>>.Create(
                RubyCallAction.Make(self._context, n, RubyCallSignature.WithImplicitSelf(2))));
            return site.Target(site, target, arg1, arg2);
        }

        /// <summary>rb_respond_to.</summary>
        internal static bool RespondTo(RubyContext/*!*/ context, object target, string/*!*/ name) {
            return Protocols.IsTrue(Call(context, target, "respond_to?", context.EncodeIdentifier(name)));
        }

        /// <summary>rb_hash_aref(options, ID2SYM(rb_intern(name))), with nil for a missing key.</summary>
        internal static object Option(RubyContext/*!*/ context, Hash/*!*/ options, string/*!*/ name) {
            object value;
            return options.TryGetValue(context.CreateAsciiSymbol(name), out value) ? value : null;
        }

        internal static bool OptionTrue(RubyContext/*!*/ context, Hash options, string/*!*/ name) {
            return options != null && Protocols.IsTrue(Option(context, options, name));
        }

        /// <summary>The name Check_Type and rb_obj_classname use in their messages.</summary>
        internal static string/*!*/ TypeName(RubyContext/*!*/ context, object obj) {
            if (obj == null) {
                return "nil";
            }
            if (obj is bool) {
                return (bool)obj ? "true" : "false";
            }
            return context.GetClassName(obj);
        }

        internal static Exception/*!*/ WrongType(RubyContext/*!*/ context, object obj, string/*!*/ expected) {
            return RubyExceptions.CreateTypeError("wrong argument type {0} (expected {1})", TypeName(context, obj), expected);
        }

        /// <summary>StringValue: a String, or what #to_str makes of one.</summary>
        internal static MutableString/*!*/ StringValue(RubyContext/*!*/ context, object obj) {
            var str = obj as MutableString;
            if (str != null) {
                return str;
            }
            if (obj != null && !(obj is bool) && RespondTo(context, obj, "to_str")) {
                str = Call(context, obj, "to_str") as MutableString;
                if (str != null) {
                    return str;
                }
            }
            throw RubyExceptions.CreateTypeError("no implicit conversion of {0} into String", TypeName(context, obj));
        }

        /// <summary>NUM2INT.</summary>
        internal static int ToInt(RubyContext/*!*/ context, object obj) {
            if (obj is int) {
                return (int)obj;
            }
            if (obj is BigInteger) {
                var big = (BigInteger)obj;
                if (big >= Int32.MinValue && big <= Int32.MaxValue) {
                    return (int)big;
                }
                throw RubyExceptions.CreateRangeError("integer {0} too big to convert to 'int'", big);
            }
            if (obj is long) {
                return ToInt(context, (BigInteger)(long)obj);
            }
            if (obj is double) {
                double d = (double)obj;
                if (Double.IsNaN(d) || d < Int32.MinValue || d >= (double)Int32.MaxValue + 1) {
                    throw RubyExceptions.CreateRangeError("float {0} out of range of integer", d);
                }
                return (int)d;
            }
            if (obj == null) {
                throw RubyExceptions.CreateTypeError("no implicit conversion from nil to integer");
            }
            if (obj is bool) {
                throw RubyExceptions.CreateTypeError("no implicit conversion of {0} into Integer", TypeName(context, obj));
            }
            if (RespondTo(context, obj, "to_int")) {
                return ToInt(context, Call(context, obj, "to_int"));
            }
            throw RubyExceptions.CreateTypeError("no implicit conversion of {0} into Integer", TypeName(context, obj));
        }

        /// <summary>NUM2UINT: negative values down to INT_MIN wrap around, as they do in C.</summary>
        internal static uint ToUInt(RubyContext/*!*/ context, object obj) {
            if (obj is BigInteger) {
                var big = (BigInteger)obj;
                if (big >= 0 && big <= UInt32.MaxValue) {
                    return (uint)big;
                }
            }
            return unchecked((uint)ToInt(context, obj));
        }

        /// <summary>NUM2SIZET, for the buffer options.</summary>
        internal static int ToSize(RubyContext/*!*/ context, object obj) {
            if (obj is BigInteger && (BigInteger)obj > Int32.MaxValue) {
                return Int32.MaxValue;
            }
            int value = ToInt(context, obj);
            if (value < 0) {
                throw RubyExceptions.CreateRangeError("integer {0} too small to convert to 'unsigned long'", value);
            }
            return value;
        }

        /// <summary>rb_num2dbl.</summary>
        internal static double ToDouble(RubyContext/*!*/ context, object obj) {
            if (obj is double) {
                return (double)obj;
            }
            if (obj is int) {
                return (int)obj;
            }
            if (obj is BigInteger) {
                return (double)(BigInteger)obj;
            }
            if (obj is long) {
                return (long)obj;
            }
            if (obj == null) {
                throw RubyExceptions.CreateTypeError("can't convert nil into Float");
            }
            if (obj is bool) {
                throw RubyExceptions.CreateTypeError("can't convert {0} into Float", TypeName(context, obj));
            }
            if (obj is MutableString) {
                throw RubyExceptions.CreateTypeError("no implicit conversion to float from string");
            }
            object f = Call(context, obj, "to_f");
            if (f is double) {
                return (double)f;
            }
            throw RubyExceptions.CreateTypeError("can't convert {0} into Float", TypeName(context, obj));
        }

        internal static Exception/*!*/ SignedCharRange(object value) {
            return RubyExceptions.CreateRangeError(String.Format("integer {0} too big to convert to `signed char'", value));
        }

        internal static int ExtType(RubyContext/*!*/ context, object type) {
            int extType = ToInt(context, type);
            if (extType < -128 || extType > 127) {
                throw SignedCharRange(extType);
            }
            return extType;
        }
    }

    #endregion

    #region ext registries

    /// <summary>One entry of an ext type registry: [type, proc, flags] on the packer side.</summary>
    internal sealed class PackerEntry {
        internal readonly int Type;
        internal readonly object Proc;
        internal readonly int Flags;

        internal PackerEntry(int type, object proc, int flags) {
            Type = type;
            Proc = proc;
            Flags = flags;
        }
    }

    /// <summary>
    /// msgpack_packer_ext_registry_t: module => entry, in registration order (the order the
    /// ancestor search walks), plus the cache of classes already resolved through an ancestor.
    /// </summary>
    internal sealed class PackerRegistry {
        internal readonly List<KeyValuePair<RubyModule, PackerEntry>>/*!*/ Order = new List<KeyValuePair<RubyModule, PackerEntry>>();
        internal readonly Dictionary<RubyModule, PackerEntry>/*!*/ Map = new Dictionary<RubyModule, PackerEntry>();
        internal Dictionary<RubyModule, PackerEntry> Cache;
        internal bool Frozen;

        internal bool IsEmpty { get { return Order.Count == 0; } }

        internal PackerRegistry/*!*/ Dup() {
            var result = new PackerRegistry();
            result.Order.AddRange(Order);
            foreach (var pair in Map) {
                result.Map[pair.Key] = pair.Value;
            }
            if (Cache != null) {
                result.Cache = new Dictionary<RubyModule, PackerEntry>(Cache);
            }
            return result;
        }

        /// <summary>msgpack_packer_ext_registry_borrow: a frozen registry is shared, cache included.</summary>
        internal PackerRegistry/*!*/ Borrow() {
            return Frozen ? this : Dup();
        }

        internal void Put(RubyModule/*!*/ module, int type, int flags, object proc) {
            if (Cache == null) {
                Cache = new Dictionary<RubyModule, PackerEntry>();
            } else {
                Cache.Clear();
            }
            var entry = new PackerEntry(type, proc, flags);
            if (Map.ContainsKey(module)) {
                int index = Order.FindIndex(p => ReferenceEquals(p.Key, module));
                Order[index] = new KeyValuePair<RubyModule, PackerEntry>(module, entry);
            } else {
                Order.Add(new KeyValuePair<RubyModule, PackerEntry>(module, entry));
            }
            Map[module] = entry;
        }

        private PackerEntry Fetch(RubyModule/*!*/ lookupClass) {
            PackerEntry entry;
            if (Map.TryGetValue(lookupClass, out entry)) {
                return entry;
            }
            if (Cache != null && Cache.TryGetValue(lookupClass, out entry)) {
                return entry;
            }
            return null;
        }

        /// <summary>msgpack_packer_ext_registry_lookup.</summary>
        internal PackerEntry Lookup(RubyContext/*!*/ context, object instance) {
            if (IsEmpty) {
                return null;
            }

            RubyClass lookupClass = context.GetImmediateClassOf(instance);
            PackerEntry entry = Fetch(lookupClass);
            if (entry != null) {
                return entry;
            }

            RubyClass realClass = context.GetClassOf(instance);
            if (!ReferenceEquals(lookupClass, realClass)) {
                entry = Fetch(realClass);
                if (entry != null) {
                    return entry;
                }
            }

            foreach (var pair in Order) {
                if (lookupClass.HasAncestor(pair.Key)) {
                    if (Cache == null) {
                        Cache = new Dictionary<RubyModule, PackerEntry>();
                    }
                    Cache[lookupClass] = pair.Value;
                    return pair.Value;
                }
            }
            return null;
        }

        internal Hash/*!*/ ToHash(RubyContext/*!*/ context) {
            var result = new Hash(context);
            foreach (var pair in Order) {
                result[pair.Key] = MessagePackOps.NewArray(ScriptingRuntimeHelpers.Int32ToObject(pair.Value.Type), pair.Value.Proc,
                    ScriptingRuntimeHelpers.Int32ToObject(pair.Value.Flags));
            }
            return result;
        }
    }

    /// <summary>One entry of the unpacker's registry: [module, proc, flags].</summary>
    internal sealed class UnpackerEntry {
        internal readonly object Module;
        internal readonly object Proc;
        internal readonly int Flags;

        internal UnpackerEntry(object module, object proc, int flags) {
            Module = module;
            Proc = proc;
            Flags = flags;
        }
    }

    /// <summary>msgpack_unpacker_ext_registry_t: 256 slots, copied on write once shared.</summary>
    internal sealed class UnpackerRegistry {
        private UnpackerEntry[]/*!*/ _slots;
        private bool _shared;

        internal UnpackerRegistry() {
            _slots = new UnpackerEntry[256];
        }

        private UnpackerRegistry(UnpackerEntry[]/*!*/ slots) {
            _slots = slots;
            _shared = true;
        }

        internal UnpackerRegistry/*!*/ Borrow() {
            _shared = true;
            return new UnpackerRegistry(_slots);
        }

        internal void Put(object module, int type, int flags, object proc) {
            if (_shared) {
                _slots = (UnpackerEntry[])_slots.Clone();
                _shared = false;
            }
            _slots[type + 128] = new UnpackerEntry(module, proc, flags);
        }

        internal UnpackerEntry Lookup(int type) {
            return _slots[type + 128];
        }

        internal Hash/*!*/ ToHash(RubyContext/*!*/ context) {
            var result = new Hash(context);
            for (int i = 0; i < 256; i++) {
                var entry = _slots[i];
                if (entry != null) {
                    result[ScriptingRuntimeHelpers.Int32ToObject(i - 128)] =
                        MessagePackOps.NewArray(entry.Module, entry.Proc, ScriptingRuntimeHelpers.Int32ToObject(entry.Flags));
                }
            }
            return result;
        }
    }

    #endregion

    [RubyModule("MessagePack")]
    public static class MessagePackOps {

        internal const int ExtRecursive = 1;   // MSGPACK_EXT_RECURSIVE

        #region errors

        [RubyException("UnpackError")]
        public class UnpackError : SystemException {
            public UnpackError() : this(null, null) { }
            public UnpackError(string message) : this(message, null) { }
            public UnpackError(string message, Exception inner) : base(message ?? "MessagePack::UnpackError", inner) { }
        }

        [RubyException("MalformedFormatError")]
        public class MalformedFormatError : UnpackError {
            public MalformedFormatError() : this(null, null) { }
            public MalformedFormatError(string message) : this(message, null) { }
            public MalformedFormatError(string message, Exception inner) : base(message ?? "MessagePack::MalformedFormatError", inner) { }
        }

        [RubyException("StackError")]
        public class StackError : UnpackError {
            public StackError() : this(null, null) { }
            public StackError(string message) : this(message, null) { }
            public StackError(string message, Exception inner) : base(message ?? "MessagePack::StackError", inner) { }
        }

        [RubyException("UnexpectedTypeError")]
        public class UnexpectedTypeError : UnpackError {
            public UnexpectedTypeError() : this(null, null) { }
            public UnexpectedTypeError(string message) : this(message, null) { }
            public UnexpectedTypeError(string message, Exception inner) : base(message ?? "MessagePack::UnexpectedTypeError", inner) { }
        }

        [RubyException("UnknownExtTypeError")]
        public class UnknownExtTypeError : UnpackError {
            public UnknownExtTypeError() : this(null, null) { }
            public UnknownExtTypeError(string message) : this(message, null) { }
            public UnknownExtTypeError(string message, Exception inner) : base(message ?? "MessagePack::UnknownExtTypeError", inner) { }
        }

        #endregion

        #region helpers

        /// <summary>MessagePack::ExtensionValue, which the Ruby half defines as a Struct.</summary>
        internal static RubyStruct/*!*/ NewExtensionValue(RubyContext/*!*/ context, int type, MutableString/*!*/ payload) {
            var result = RubyStruct.Create(GetClass(context, "ExtensionValue"));
            result[0] = ScriptingRuntimeHelpers.Int32ToObject(type);
            result[1] = payload;
            return result;
        }

        /// <summary>A class under MessagePack, looked up by name.</summary>
        internal static RubyClass/*!*/ GetClass(RubyContext/*!*/ context, string/*!*/ name) {
            object mp, cls;
            if (context.ObjectClass.TryGetConstant(null, "MessagePack", out mp) && mp is RubyModule &&
                ((RubyModule)mp).TryGetConstant(null, name, out cls) && cls is RubyClass) {
                return (RubyClass)cls;
            }
            throw RubyExceptions.CreateNameError("uninitialized constant MessagePack::" + name);
        }

        internal static RubyArray/*!*/ NewArray(params object[]/*!*/ items) {
            var result = new RubyArray(items.Length);
            foreach (var item in items) {
                result.Add(item);
            }
            return result;
        }

        internal static object CallProc(object proc, object arg) {
            var p = proc as Proc;
            if (p == null) {
                throw RubyExceptions.CreateTypeError("wrong argument type {0} (expected Proc)", proc == null ? "nil" : proc.GetType().Name);
            }
            return p.Call(null, arg);
        }

        internal static object CallProc(object proc, object arg1, object arg2) {
            var p = proc as Proc;
            if (p == null) {
                throw RubyExceptions.CreateTypeError("wrong argument type {0} (expected Proc)", proc == null ? "nil" : proc.GetType().Name);
            }
            return p.Call(null, arg1, arg2);
        }

        #endregion

        #region Buffer

        /// <summary>
        /// MessagePack::Buffer.  A buffer made by Buffer.new owns its bytes; the one a Packer or
        /// Unpacker hands out from #buffer is a view of its owner's, which follows the owner
        /// even when a recursive extension packer swaps a fresh buffer in underneath.
        /// </summary>
        [RubyClass("Buffer")]
        public sealed class BufferObject : RubyObject, IMemorySized {
            private BufferCore _own;
            internal object Owner;   // PackerObject or UnpackerObject for a view

            public BufferObject(RubyClass/*!*/ rubyClass) : base(rubyClass) {
                _own = new BufferCore(rubyClass.Context);
            }

            protected override RubyObject/*!*/ CreateInstance() {
                return new BufferObject(ImmediateClass.NominalClass);
            }

            internal static BufferObject/*!*/ Wrap(RubyContext/*!*/ context, object owner) {
                var result = new BufferObject(GetClass(context, "Buffer"));
                result._own = null;
                result.Owner = owner;
                return result;
            }

            // Buffer_memsize; a view has no dsize of its own
            long IMemorySized.ExtraMemorySize {
                get { return _own != null ? 160 + _own.MemorySize : 0; }
            }

            internal BufferCore/*!*/ Core {
                get {
                    var packer = Owner as PackerObject;
                    if (packer != null) {
                        return packer.Core;
                    }
                    var unpacker = Owner as UnpackerObject;
                    if (unpacker != null) {
                        return unpacker.Core;
                    }
                    return _own;
                }
            }

            /// <summary>
            /// Kernel#inspect answers #to_s for a class the libraries define, and this one has a
            /// #to_s that is the buffer's bytes; upstream's inspects as #&lt;MessagePack::...:0x...&gt;.
            /// </summary>
            [RubyMethod("inspect")]
            public static MutableString/*!*/ Inspect(UnaryOpStorage/*!*/ inspectStorage, ConversionStorage<MutableString>/*!*/ tosConversion,
                BufferObject/*!*/ self) {
                return RubyUtils.InspectObject(inspectStorage, tosConversion, self);
            }

            [RubyConstructor]
            public static BufferObject/*!*/ Create(RubyClass/*!*/ self, params object[]/*!*/ args) {
                var result = new BufferObject(self);
                Initialize(self.Context, result, args);
                return result;
            }

            [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
            public static object Initialize(RubyContext/*!*/ context, BufferObject/*!*/ self, params object[]/*!*/ args) {
                object io = null;
                Hash options = null;

                if (args.Length == 0 || (args.Length == 1 && args[0] == null)) {
                    // nothing
                } else if (args.Length == 1) {
                    if (args[0] is Hash) {
                        options = (Hash)args[0];
                    } else {
                        io = args[0];
                    }
                } else if (args.Length == 2) {
                    io = args[0];
                    options = args[1] as Hash;
                    if (options == null) {
                        // sic: upstream names the class of the io here
                        throw RubyExceptions.CreateArgumentError("expected Hash but found {0}.", context.GetClassName(io));
                    }
                } else {
                    throw RubyExceptions.CreateArgumentError("wrong number of arguments ({0} for 0..1)", args.Length);
                }

                self.Core.SetOptions(io, options);
                return self;
            }

            [RubyMethod("clear")]
            public static object Clear(BufferObject/*!*/ self) {
                self.Core.Clear();
                return null;
            }

            [RubyMethod("size")]
            public static object Size(BufferObject/*!*/ self) {
                return Protocols.Normalize(self.Core.AllReadableSize);
            }

            [RubyMethod("empty?")]
            public static bool IsEmpty(BufferObject/*!*/ self) {
                return self.Core.TopReadableSize == 0;
            }

            [RubyMethod("write")]
            public static object Write(RubyContext/*!*/ context, BufferObject/*!*/ self, object str) {
                return ScriptingRuntimeHelpers.Int32ToObject(self.Core.AppendString(Sites.StringValue(context, str)));
            }

            [RubyMethod("<<")]
            public static BufferObject/*!*/ Append(RubyContext/*!*/ context, BufferObject/*!*/ self, object str) {
                self.Core.AppendString(Sites.StringValue(context, str));
                return self;
            }

            /// <summary>read_until_eof: reads (or skips, when out is null) up to max bytes, 0 meaning all.</summary>
            private static long ReadUntilEof(BufferCore/*!*/ b, MutableString @out, long max) {
                if (b.HasIo) {
                    long size = 0;
                    try {
                        while (true) {
                            long rl;
                            if (max == 0) {
                                rl = (@out == null) ? b.Skip(b.IoBufferSize) : b.ReadToString(@out, b.IoBufferSize);
                                if (rl == 0) {
                                    break;
                                }
                                size += rl;
                            } else {
                                rl = (@out == null) ? b.Skip(max) : b.ReadToString(@out, max);
                                if (rl == 0) {
                                    break;
                                }
                                size += rl;
                                if (max <= rl) {
                                    break;
                                }
                                max -= rl;
                            }
                        }
                    } catch (EOFError) {
                        // ignored, as upstream's rb_rescue2 does
                    }
                    return size;
                }

                if (max == 0) {
                    max = Int64.MaxValue;
                }
                return (@out == null) ? b.SkipNonblock(max) : b.ReadToStringNonblock(@out, max);
            }

            private static MutableString/*!*/ MakeEmptyString(MutableString @out) {
                if (@out == null) {
                    return MutableString.CreateBinary();
                }
                @out.Clear();
                return @out;
            }

            private static MutableString/*!*/ ReadAllOf(BufferCore/*!*/ b, MutableString @out) {
                if (@out == null && !b.HasIo) {
                    MutableString str = b.AllAsString();
                    b.Clear();
                    return str;
                }
                @out = MakeEmptyString(@out);
                ReadUntilEof(b, @out, 0);
                return @out;
            }

            private static long ToCount(RubyContext/*!*/ context, object n) {
                if (n is BigInteger) {
                    return (long)(BigInteger)n;
                }
                return Sites.ToInt(context, n);
            }

            private static MutableString CheckOut(RubyContext/*!*/ context, object @out) {
                if (@out == null) {
                    return null;
                }
                var str = @out as MutableString;
                if (str == null && !(@out is bool) && Sites.RespondTo(context, @out, "to_str")) {
                    str = Sites.Call(context, @out, "to_str") as MutableString;
                }
                if (str == null) {
                    throw RubyExceptions.CreateTypeError("instance of String needed");
                }
                return str;
            }

            [RubyMethod("skip")]
            public static object Skip(RubyContext/*!*/ context, BufferObject/*!*/ self, object n) {
                long count = ToCount(context, n);
                if (count == 0) {
                    return ScriptingRuntimeHelpers.Int32ToObject(0);
                }
                return Protocols.Normalize(ReadUntilEof(self.Core, null, count));
            }

            [RubyMethod("skip_all")]
            public static BufferObject/*!*/ SkipAll(RubyContext/*!*/ context, BufferObject/*!*/ self, object n) {
                long count = ToCount(context, n);
                if (count == 0) {
                    return self;
                }
                BufferCore b = self.Core;
                if (!b.EnsureReadable(count)) {
                    throw new EOFError("end of buffer reached");
                }
                b.SkipNonblock(count);
                return self;
            }

            [RubyMethod("read_all")]
            public static MutableString/*!*/ ReadAll(RubyContext/*!*/ context, BufferObject/*!*/ self, params object[]/*!*/ args) {
                if (args.Length > 2) {
                    throw RubyExceptions.CreateArgumentError("wrong number of arguments ({0} for 0..2)", args.Length);
                }
                BufferCore b = self.Core;
                MutableString @out = args.Length == 2 ? CheckOut(context, args[1]) : null;
                if (args.Length == 0) {
                    return ReadAllOf(b, @out);
                }
                long n = ToCount(context, args[0]);
                if (n == 0) {
                    return MakeEmptyString(@out);
                }
                if (!b.EnsureReadable(n)) {
                    throw new EOFError("end of buffer reached");
                }
                @out = MakeEmptyString(@out);
                b.ReadToStringNonblock(@out, n);
                return @out;
            }

            [RubyMethod("read")]
            public static MutableString Read(RubyContext/*!*/ context, BufferObject/*!*/ self, params object[]/*!*/ args) {
                if (args.Length > 2) {
                    throw RubyExceptions.CreateArgumentError("wrong number of arguments ({0} for 0..2)", args.Length);
                }
                BufferCore b = self.Core;
                MutableString @out = args.Length == 2 ? CheckOut(context, args[1]) : null;
                if (args.Length == 0) {
                    return ReadAllOf(b, @out);
                }
                long n = ToCount(context, args[0]);
                if (n == 0) {
                    return MakeEmptyString(@out);
                }

                if (!b.HasIo && @out == null && b.AllReadableSize <= n) {
                    MutableString str = b.AllAsString();
                    b.Clear();
                    return str.GetByteCount() == 0 ? null : str;
                }

                @out = MakeEmptyString(@out);
                ReadUntilEof(b, @out, n);
                return @out.GetByteCount() == 0 ? null : @out;
            }

            [RubyMethod("to_str")]
            [RubyMethod("to_s")]
            public static MutableString/*!*/ ToStr(BufferObject/*!*/ self) {
                return self.Core.AllAsString();
            }

            [RubyMethod("to_a")]
            public static RubyArray/*!*/ ToA(BufferObject/*!*/ self) {
                return self.Core.AllAsStringArray();
            }

            [RubyMethod("flush")]
            public static BufferObject/*!*/ Flush(BufferObject/*!*/ self) {
                self.Core.Flush();
                return self;
            }

            [RubyMethod("io")]
            public static object Io(BufferObject/*!*/ self) {
                return self.Core.Io;
            }

            [RubyMethod("close")]
            public static object Close(RubyContext/*!*/ context, BufferObject/*!*/ self) {
                var io = self.Core.Io;
                if (io != null) {
                    return Sites.Call(context, io, "close");
                }
                return null;
            }

            [RubyMethod("write_to")]
            public static object WriteTo(BufferObject/*!*/ self, object io) {
                return Protocols.Normalize(self.Core.FlushToIo(io, "write", true));
            }
        }

        #endregion

        #region Packer

        [RubyClass("Packer")]
        public sealed class PackerObject : RubyObject, IMemorySized {
            internal BufferCore/*!*/ Core;
            internal bool CompatibilityMode;
            internal bool HasBigintExtType;
            internal bool HasSymbolExtType;
            internal PackerRegistry/*!*/ Registry = new PackerRegistry();
            internal BufferObject BufferRef;

            public PackerObject(RubyClass/*!*/ rubyClass) : base(rubyClass) {
                Core = new BufferCore(rubyClass.Context);
            }

            protected override RubyObject/*!*/ CreateInstance() {
                return new PackerObject(ImmediateClass.NominalClass);
            }

            internal RubyContext/*!*/ Context { get { return ImmediateClass.Context; } }

            long IMemorySized.ExtraMemorySize {
                get { return 240 + Core.MemorySize; }
            }

            internal static PackerObject/*!*/ CreateDefault(RubyContext/*!*/ context, object[]/*!*/ args) {
                var result = new PackerObject(GetClass(context, "Packer"));
                Initialize(context, result, args);
                return result;
            }

            /// <summary>
            /// Kernel#inspect answers #to_s for a class the libraries define, and this one has a
            /// #to_s that is the buffer's bytes; upstream's inspects as #&lt;MessagePack::...:0x...&gt;.
            /// </summary>
            [RubyMethod("inspect")]
            public static MutableString/*!*/ Inspect(UnaryOpStorage/*!*/ inspectStorage, ConversionStorage<MutableString>/*!*/ tosConversion,
                PackerObject/*!*/ self) {
                return RubyUtils.InspectObject(inspectStorage, tosConversion, self);
            }

            [RubyConstructor]
            public static PackerObject/*!*/ Create(RubyClass/*!*/ self, params object[]/*!*/ args) {
                var result = new PackerObject(self);
                Initialize(self.Context, result, args);
                return result;
            }

            [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
            public static object Initialize(RubyContext/*!*/ context, PackerObject/*!*/ self, params object[]/*!*/ args) {
                if (args.Length > 2) {
                    throw RubyExceptions.CreateArgumentError("wrong number of arguments ({0} for 0..2)", args.Length);
                }
                object io = args.Length >= 1 ? args[0] : null;
                object options = args.Length == 2 ? args[1] : null;

                if (options == null && io is Hash) {
                    options = io;
                    io = null;
                }
                if (options != null && !(options is Hash)) {
                    throw Sites.WrongType(context, options, "Hash");
                }

                self.Registry = new PackerRegistry();
                self.BufferRef = BufferObject.Wrap(context, self);
                self.Core.SetOptions(io, (Hash)options);
                if (options != null) {
                    self.CompatibilityMode = Sites.OptionTrue(context, (Hash)options, "compatibility_mode");
                }
                return self;
            }

            [RubyMethod("compatibility_mode?")]
            public static bool IsCompatibilityMode(PackerObject/*!*/ self) {
                return self.CompatibilityMode;
            }

            [RubyMethod("buffer")]
            public static BufferObject/*!*/ GetBuffer(RubyContext/*!*/ context, PackerObject/*!*/ self) {
                if (self.BufferRef == null) {
                    self.BufferRef = BufferObject.Wrap(context, self);
                }
                return self.BufferRef;
            }

            [RubyMethod("write")]
            [RubyMethod("pack")]
            public static PackerObject/*!*/ Write(PackerObject/*!*/ self, object value) {
                self.WriteValue(value);
                return self;
            }

            [RubyMethod("write_nil")]
            public static PackerObject/*!*/ WriteNil(PackerObject/*!*/ self) {
                self.PutByte(0xc0);
                return self;
            }

            [RubyMethod("write_true")]
            public static PackerObject/*!*/ WriteTrue(PackerObject/*!*/ self) {
                self.PutByte(0xc3);
                return self;
            }

            [RubyMethod("write_false")]
            public static PackerObject/*!*/ WriteFalse(PackerObject/*!*/ self) {
                self.PutByte(0xc2);
                return self;
            }

            [RubyMethod("write_float")]
            public static PackerObject/*!*/ WriteFloat(RubyContext/*!*/ context, PackerObject/*!*/ self, object value) {
                self.WriteDouble(Sites.ToDouble(context, value));
                return self;
            }

            [RubyMethod("write_string")]
            public static PackerObject/*!*/ WriteString(RubyContext/*!*/ context, PackerObject/*!*/ self, object value) {
                var str = value as MutableString;
                if (str == null) {
                    throw Sites.WrongType(context, value, "String");
                }
                self.WriteStringValue(str);
                return self;
            }

            [RubyMethod("write_bin")]
            public static PackerObject/*!*/ WriteBin(RubyContext/*!*/ context, PackerObject/*!*/ self, object value) {
                var str = value as MutableString;
                if (str == null) {
                    throw Sites.WrongType(context, value, "String");
                }
                str = (MutableString)Sites.Call(context, str, "encode", RubyEncoding.Binary);
                self.WriteStringValue(str);
                return self;
            }

            [RubyMethod("write_array")]
            public static PackerObject/*!*/ WriteArray(RubyContext/*!*/ context, PackerObject/*!*/ self, object value) {
                var list = value as RubyArray;
                if (list == null) {
                    throw Sites.WrongType(context, value, "Array");
                }
                self.WriteArrayValue(list);
                return self;
            }

            [RubyMethod("write_hash")]
            public static PackerObject/*!*/ WriteHash(RubyContext/*!*/ context, PackerObject/*!*/ self, object value) {
                var hash = value as Hash;
                if (hash == null) {
                    throw Sites.WrongType(context, value, "Hash");
                }
                self.WriteHashValue(hash);
                return self;
            }

            [RubyMethod("write_symbol")]
            public static PackerObject/*!*/ WriteSymbol(RubyContext/*!*/ context, PackerObject/*!*/ self, object value) {
                var symbol = value as RubySymbol;
                if (symbol == null) {
                    throw Sites.WrongType(context, value, "Symbol");
                }
                self.WriteSymbolValue(symbol);
                return self;
            }

            [RubyMethod("write_int")]
            public static PackerObject/*!*/ WriteInt(RubyContext/*!*/ context, PackerObject/*!*/ self, object value) {
                if (value is int) {
                    self.WriteLong((int)value);
                } else if (value is long) {
                    self.WriteBignum((long)value);
                } else if (value is BigInteger) {
                    self.WriteBignum((BigInteger)value);
                } else {
                    throw Sites.WrongType(context, value, "Integer");
                }
                return self;
            }

            [RubyMethod("write_extension")]
            public static PackerObject/*!*/ WriteExtension(RubyContext/*!*/ context, PackerObject/*!*/ self, object value) {
                var ext = value as RubyStruct;
                if (ext == null) {
                    throw Sites.WrongType(context, value, "Struct");
                }
                object rbType = ext[0];
                if (!(rbType is int)) {
                    throw RubyExceptions.CreateRangeError(String.Format("integer {0} too big to convert to `signed char'",
                        context.Inspect(rbType).ToString()));
                }
                int type = (int)rbType;
                if (type < -128 || type > 127) {
                    throw Sites.SignedCharRange(type);
                }
                self.WriteExt(type, Sites.StringValue(context, ext[1]));
                return self;
            }

            [RubyMethod("write_array_header")]
            public static PackerObject/*!*/ WriteArrayHeader(RubyContext/*!*/ context, PackerObject/*!*/ self, object n) {
                self.WriteArrayHeaderValue(Sites.ToUInt(context, n));
                return self;
            }

            [RubyMethod("write_map_header")]
            public static PackerObject/*!*/ WriteMapHeader(RubyContext/*!*/ context, PackerObject/*!*/ self, object n) {
                self.WriteMapHeaderValue(Sites.ToUInt(context, n));
                return self;
            }

            [RubyMethod("write_bin_header")]
            public static PackerObject/*!*/ WriteBinHeader(RubyContext/*!*/ context, PackerObject/*!*/ self, object n) {
                self.WriteBinHeaderValue(Sites.ToUInt(context, n));
                return self;
            }

            [RubyMethod("write_float32")]
            public static PackerObject/*!*/ WriteFloat32(RubyContext/*!*/ context, PackerObject/*!*/ self, object value) {
                if (!(value is int || value is double || value is BigInteger || value is long) &&
                    !context.IsKindOf(value, context.GetClass(typeof(Numeric)))) {
                    throw RubyExceptions.CreateArgumentError("Expected numeric");
                }
                self.WriteSingle((float)Sites.ToDouble(context, value));
                return self;
            }

            [RubyMethod("write_ext")]
            public static PackerObject/*!*/ WriteExtValue(RubyContext/*!*/ context, PackerObject/*!*/ self, object type, object data) {
                int extType = Sites.ExtType(context, type);
                self.WriteExt(extType, Sites.StringValue(context, data));
                return self;
            }

            [RubyMethod("flush")]
            public static PackerObject/*!*/ Flush(PackerObject/*!*/ self) {
                self.Core.Flush();
                return self;
            }

            [RubyMethod("reset")]
            [RubyMethod("clear")]
            public static object Reset(PackerObject/*!*/ self) {
                self.Core.Clear();
                return null;
            }

            [RubyMethod("size")]
            public static object Size(PackerObject/*!*/ self) {
                return Protocols.Normalize(self.Core.AllReadableSize);
            }

            [RubyMethod("empty?")]
            public static bool IsEmpty(PackerObject/*!*/ self) {
                return self.Core.TopReadableSize == 0;
            }

            [RubyMethod("to_str")]
            [RubyMethod("to_s")]
            public static MutableString/*!*/ ToStr(PackerObject/*!*/ self) {
                return self.Core.AllAsString();
            }

            [RubyMethod("to_a")]
            public static RubyArray/*!*/ ToA(PackerObject/*!*/ self) {
                return self.Core.AllAsStringArray();
            }

            [RubyMethod("write_to")]
            public static object WriteTo(PackerObject/*!*/ self, object io) {
                return Protocols.Normalize(self.Core.FlushToIo(io, "write", true));
            }

            [RubyMethod("registered_types_internal", RubyMethodAttributes.PrivateInstance)]
            public static Hash/*!*/ RegisteredTypesInternal(RubyContext/*!*/ context, PackerObject/*!*/ self) {
                return self.Registry.ToHash(context);
            }

            [RubyMethod("register_type_internal")]
            public static object RegisterTypeInternal(RubyContext/*!*/ context, PackerObject/*!*/ self, object type, object module, object proc) {
                if (self.IsFrozen) {
                    throw new FrozenError("can't modify frozen MessagePack::Packer");
                }
                int extType = Sites.ExtType(context, type);
                var mod = module as RubyModule;
                if (mod == null) {
                    throw Sites.WrongType(context, module, "Module");
                }
                if (self.Registry.Frozen) {
                    self.Registry = self.Registry.Dup();
                }
                self.Registry.Put(mod, extType, 0, proc);
                if (ReferenceEquals(mod, context.GetClass(typeof(RubySymbol)))) {
                    self.HasSymbolExtType = true;
                }
                return null;
            }

            [RubyMethod("full_pack")]
            public static MutableString FullPack(PackerObject/*!*/ self) {
                MutableString result;
                if (self.Core.HasIo) {
                    self.Core.Flush();
                    result = null;
                } else {
                    result = self.Core.AllAsString();
                }
                self.Core.Clear();
                return result;
            }

            #region writing

            private void PutByte(byte b) {
                int offset;
                byte[] mem = Core.Reserve(1, out offset);
                mem[offset] = b;
            }

            /// <summary>A head byte followed by a big-endian value of 1, 2, 4 or 8 bytes.</summary>
            private void PutHeader(byte head, int size, ulong value) {
                int offset;
                byte[] mem = Core.Reserve(size + 1, out offset);
                mem[offset] = head;
                var span = new Span<byte>(mem, offset + 1, size);
                switch (size) {
                    case 1: span[0] = (byte)value; break;
                    case 2: BinaryPrimitives.WriteUInt16BigEndian(span, (ushort)value); break;
                    case 4: BinaryPrimitives.WriteUInt32BigEndian(span, (uint)value); break;
                    default: BinaryPrimitives.WriteUInt64BigEndian(span, value); break;
                }
            }

            /// <summary>_msgpack_packer_write_long_long64.</summary>
            internal void WriteLong(long v) {
                if (v < -0x20L) {
                    if (v < -0x8000L) {
                        if (v < -0x80000000L) {
                            PutHeader(0xd3, 8, unchecked((ulong)v));
                        } else {
                            PutHeader(0xd2, 4, unchecked((uint)(int)v));
                        }
                    } else if (v < -0x80L) {
                        PutHeader(0xd1, 2, unchecked((ushort)(short)v));
                    } else {
                        PutHeader(0xd0, 1, unchecked((byte)(sbyte)v));
                    }
                } else if (v <= 0x7fL) {
                    PutByte(unchecked((byte)(sbyte)v));
                } else if (v <= 0xffffL) {
                    if (v <= 0xffL) {
                        PutHeader(0xcc, 1, (ulong)v);
                    } else {
                        PutHeader(0xcd, 2, (ulong)v);
                    }
                } else if (v <= 0xffffffffL) {
                    PutHeader(0xce, 4, (ulong)v);
                } else {
                    PutHeader(0xcf, 8, (ulong)v);
                }
            }

            /// <summary>msgpack_packer_write_u64.</summary>
            private void WriteU64(ulong v) {
                if (v <= 0xffUL) {
                    if (v <= 0x7fUL) {
                        PutByte((byte)v);
                    } else {
                        PutHeader(0xcc, 1, v);
                    }
                } else if (v <= 0xffffUL) {
                    PutHeader(0xcd, 2, v);
                } else if (v <= 0xffffffffUL) {
                    PutHeader(0xce, 4, v);
                } else {
                    PutHeader(0xcf, 8, v);
                }
            }

            private static readonly BigInteger _fixnumMin = -(BigInteger.One << 62);
            private static readonly BigInteger _fixnumMax = (BigInteger.One << 62) - 1;
            private static readonly BigInteger _longMin = long.MinValue;

            internal void WriteBignum(BigInteger v) {
                // Within CRuby's Fixnum range the value takes msgpack_packer_write_long's path;
                // for encodings that is the same as the Bignum one below, as long as it fits.
                if (v >= _fixnumMin && v <= _fixnumMax) {
                    WriteLong((long)v);
                    return;
                }

                if (v.Sign >= 0) {
                    // rb_absint_size(v) > 8
                    if (v > UInt64.MaxValue && HasBigintExtType) {
                        if (TryWriteWithExtTypeLookup(v)) {
                            return;
                        }
                    }
                    if (v > UInt64.MaxValue) {
                        throw RubyExceptions.CreateRangeError("bignum too big to convert into 'unsigned long long'");
                    }
                    WriteU64((ulong)v);
                } else {
                    // rb_absint_size(v) plus the sign bit > 8
                    if (v <= _longMin && HasBigintExtType) {
                        if (TryWriteWithExtTypeLookup(v)) {
                            return;
                        }
                    }
                    if (v < _longMin) {
                        throw RubyExceptions.CreateRangeError("bignum too big to convert into 'long long'");
                    }
                    WriteLong((long)v);
                }
            }

            internal void WriteSingle(float v) {
                PutHeader(0xca, 4, BitConverter.SingleToUInt32Bits(v));
            }

            internal void WriteDouble(double v) {
                PutHeader(0xcb, 8, BitConverter.DoubleToUInt64Bits(v));
            }

            private void WriteRawHeader(uint n) {
                if (n < 32) {
                    PutByte((byte)(0xa0 | n));
                } else if (n < 256 && !CompatibilityMode) {
                    PutHeader(0xd9, 1, n);
                } else if (n < 65536) {
                    PutHeader(0xda, 2, n);
                } else {
                    PutHeader(0xdb, 4, n);
                }
            }

            internal void WriteBinHeaderValue(uint n) {
                if (n < 256) {
                    PutHeader(0xc4, 1, n);
                } else if (n < 65536) {
                    PutHeader(0xc5, 2, n);
                } else {
                    PutHeader(0xc6, 4, n);
                }
            }

            internal void WriteArrayHeaderValue(uint n) {
                if (n < 16) {
                    PutByte((byte)(0x90 | n));
                } else if (n < 65536) {
                    PutHeader(0xdc, 2, n);
                } else {
                    PutHeader(0xdd, 4, n);
                }
            }

            internal void WriteMapHeaderValue(uint n) {
                if (n < 16) {
                    PutByte((byte)(0x80 | n));
                } else if (n < 65536) {
                    PutHeader(0xde, 2, n);
                } else {
                    PutHeader(0xdf, 4, n);
                }
            }

            /// <summary>msgpack_packer_write_ext.</summary>
            internal void WriteExt(int extType, MutableString/*!*/ payload) {
                int len = payload.GetByteCount();
                byte type = unchecked((byte)(sbyte)extType);
                switch (len) {
                    case 1: Core.EnsureWritable(2); Core.WriteByte(0xd4); Core.WriteByte(type); break;
                    case 2: Core.EnsureWritable(2); Core.WriteByte(0xd5); Core.WriteByte(type); break;
                    case 4: Core.EnsureWritable(2); Core.WriteByte(0xd6); Core.WriteByte(type); break;
                    case 8: Core.EnsureWritable(2); Core.WriteByte(0xd7); Core.WriteByte(type); break;
                    case 16: Core.EnsureWritable(2); Core.WriteByte(0xd8); Core.WriteByte(type); break;
                    default:
                        if (len < 256) {
                            Core.EnsureWritable(3);
                            Core.WriteByte(0xc7);
                            Core.WriteByte((byte)len);
                            Core.WriteByte(type);
                        } else if (len < 65536) {
                            PutHeader(0xc8, 2, (ulong)len);
                            Core.EnsureWritable(1);
                            Core.WriteByte(type);
                        } else {
                            PutHeader(0xc9, 4, (ulong)len);
                            Core.EnsureWritable(1);
                            Core.WriteByte(type);
                        }
                        break;
                }
                Core.AppendString(payload);
            }

            /// <summary>msgpack_packer_write_string_value.</summary>
            internal void WriteStringValue(MutableString/*!*/ v) {
                if (CompatibilityMode) {
                    byte[] raw = v.ToByteArray();
                    WriteRawHeader((uint)raw.Length);
                    Core.AppendString(v, raw);
                    return;
                }

                RubyEncoding encoding = v.Encoding;
                if (encoding == RubyEncoding.Binary) {
                    byte[] bin = v.ToByteArray();
                    WriteBinHeaderValue((uint)bin.Length);
                    Core.AppendString(v, bin);
                    return;
                }

                if (!(encoding == RubyEncoding.UTF8 || encoding == RubyEncoding.Ascii || v.IsAscii())) {
                    // transcode other strings to UTF-8
                    v = (MutableString)Sites.Call(Context, v, "encode", RubyEncoding.UTF8);
                }
                byte[] bytes = v.ToByteArray();
                WriteRawHeader((uint)bytes.Length);
                Core.AppendString(v, bytes);
            }

            internal void WriteSymbolValue(RubySymbol/*!*/ v) {
                if (HasSymbolExtType) {
                    WriteOtherValue(v);
                } else {
                    WriteStringValue(v.String);
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveOptimization)]
            internal void WriteArrayValue(RubyArray/*!*/ v) {
                long len = v.Count;
                WriteArrayHeaderValue((uint)len);
                for (int i = 0; i < len && i < v.Count; i++) {
                    WriteValue(v[i]);
                }
                // upstream reads rb_ary_entry past a shrunken end as nil
                for (int i = v.Count; i < len; i++) {
                    WriteValue(null);
                }
            }

            internal void WriteHashValue(Hash/*!*/ v) {
                WriteMapHeaderValue((uint)v.Count);
                foreach (var pair in v) {
                    WriteValue(CustomStringDictionary.ObjToNull(pair.Key));
                    WriteValue(pair.Value);
                }
            }

            /// <summary>msgpack_packer_try_write_with_ext_type_lookup.</summary>
            internal bool TryWriteWithExtTypeLookup(object v) {
                PackerEntry entry = Registry.Lookup(Context, v);
                if (entry == null || entry.Proc == null) {
                    return false;
                }

                if ((entry.Flags & ExtRecursive) != 0) {
                    BufferCore parent = Core;
                    Core = new BufferCore(Context);
                    MutableString payload;
                    try {
                        CallProc(entry.Proc, v, this);
                        payload = Core.AllAsString();
                    } finally {
                        Core = parent;
                    }
                    WriteExt(entry.Type, payload);
                } else {
                    object payload = CallProc(entry.Proc, v);
                    WriteExt(entry.Type, Sites.StringValue(Context, payload));
                }
                return true;
            }

            internal void WriteOtherValue(object v) {
                if (!TryWriteWithExtTypeLookup(v)) {
                    Sites.Call(Context, v, "to_msgpack", this);
                }
            }

            /// <summary>msgpack_packer_write_value.</summary>
            internal void WriteValue(object v) {
                if (v == null) {
                    PutByte(0xc0);
                    return;
                }
                if (v is bool) {
                    PutByte((bool)v ? (byte)0xc3 : (byte)0xc2);
                    return;
                }
                if (v is int) {
                    WriteLong((int)v);
                    return;
                }

                var symbol = v as RubySymbol;
                if (symbol != null) {
                    WriteSymbolValue(symbol);
                    return;
                }

                var str = v as MutableString;
                if (str != null) {
                    if (IsExactly(v, typeof(MutableString)) || !TryWriteWithExtTypeLookup(v)) {
                        WriteStringValue(str);
                    }
                    return;
                }

                var array = v as RubyArray;
                if (array != null) {
                    if (IsExactly(v, typeof(RubyArray)) || !TryWriteWithExtTypeLookup(v)) {
                        WriteArrayValue(array);
                    }
                    return;
                }

                var hash = v as Hash;
                if (hash != null) {
                    if (IsExactly(v, typeof(Hash)) || !TryWriteWithExtTypeLookup(v)) {
                        WriteHashValue(hash);
                    }
                    return;
                }

                if (v is BigInteger) {
                    WriteBignum((BigInteger)v);
                    return;
                }
                if (v is long) {
                    WriteBignum((long)v);
                    return;
                }
                if (v is double) {
                    WriteDouble((double)v);
                    return;
                }

                WriteOtherValue(v);
            }

            private RubyClass _stringClass, _arrayClass, _hashClass;

            /// <summary>rb_class_of(v) == rb_cString and friends: no subclass, no singleton class.</summary>
            private bool IsExactly(object v, Type type) {
                RubyClass cls;
                if (type == typeof(MutableString)) {
                    cls = _stringClass ?? (_stringClass = Context.GetClass(type));
                } else if (type == typeof(RubyArray)) {
                    cls = _arrayClass ?? (_arrayClass = Context.GetClass(type));
                } else {
                    cls = _hashClass ?? (_hashClass = Context.GetClass(type));
                }
                return ReferenceEquals(Context.GetImmediateClassOf(v), cls);
            }

            #endregion
        }

        #endregion

        #region Unpacker

        [RubyClass("Unpacker")]
        public sealed class UnpackerObject : RubyObject, IMemorySized {
            private const int HeadByteRequired = 0xc1;
            private const int StackCapacity = 128;       // MSGPACK_UNPACKER_STACK_CAPACITY
            private const int RawTypeString = 256;
            private const int RawTypeBinary = 257;

            private const int PrimitiveContainerStart = 1;
            private const int PrimitiveObjectComplete = 0;
            private const int PrimitiveEof = -1;
            private const int PrimitiveInvalidByte = -2;
            private const int PrimitiveStackTooDeep = -3;
            private const int PrimitiveUnexpectedType = -4;
            private const int PrimitiveUnexpectedExtType = -5;
            private const int PrimitiveRecursiveRaised = -6;

            private const int StackArray = 0;
            private const int StackMapKey = 1;
            private const int StackMapValue = 2;
            private const int StackRecursive = 3;

            private sealed class StackEntry {
                internal long Count;
                internal int Type;
                internal object Object;
                internal object Key;
            }

            internal BufferCore/*!*/ Core;
            private StackEntry[] _stack;
            private int _depth;
            private object _lastObject;
            private MutableString _readingRaw;
            private long _readingRawRemaining;
            private int _readingRawType;
            private int _headByte = HeadByteRequired;
            private Exception _raised;

            internal BufferObject BufferRef;
            internal UnpackerRegistry Registry;
            internal int SymbolExtType;
            internal bool OptimizedSymbolExtType;
            internal bool SymbolizeKeys;
            internal bool FreezeOption;
            internal bool AllowUnknownExt;
            internal bool UseKeyCache;

            public UnpackerObject(RubyClass/*!*/ rubyClass) : base(rubyClass) {
                Core = new BufferCore(rubyClass.Context);
            }

            protected override RubyObject/*!*/ CreateInstance() {
                return new UnpackerObject(ImmediateClass.NominalClass);
            }

            internal RubyContext/*!*/ Context { get { return ImmediateClass.Context; } }

            long IMemorySized.ExtraMemorySize {
                get {
                    long size = 760 + Core.MemorySize;
                    if (Registry != null) {
                        size += 2056;
                    }
                    if (_stack != null) {
                        size += (_depth + 1) * 32;
                    }
                    return size;
                }
            }

            internal static UnpackerObject/*!*/ CreateDefault(RubyContext/*!*/ context, object[]/*!*/ args) {
                var result = new UnpackerObject(GetClass(context, "Unpacker"));
                Initialize(context, result, args);
                return result;
            }

            /// <summary>
            /// Kernel#inspect answers #to_s for a class the libraries define, and this one has a
            /// #to_s that is the buffer's bytes; upstream's inspects as #&lt;MessagePack::...:0x...&gt;.
            /// </summary>
            [RubyMethod("inspect")]
            public static MutableString/*!*/ Inspect(UnaryOpStorage/*!*/ inspectStorage, ConversionStorage<MutableString>/*!*/ tosConversion,
                UnpackerObject/*!*/ self) {
                return RubyUtils.InspectObject(inspectStorage, tosConversion, self);
            }

            [RubyConstructor]
            public static UnpackerObject/*!*/ Create(RubyClass/*!*/ self, params object[]/*!*/ args) {
                var result = new UnpackerObject(self);
                Initialize(self.Context, result, args);
                return result;
            }

            [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
            public static object Initialize(RubyContext/*!*/ context, UnpackerObject/*!*/ self, params object[]/*!*/ args) {
                object io = null;
                Hash options = null;

                if (args.Length == 0 || (args.Length == 1 && args[0] == null)) {
                    // nothing
                } else if (args.Length == 1) {
                    if (args[0] is Hash) {
                        options = (Hash)args[0];
                    } else {
                        io = args[0];
                    }
                } else if (args.Length == 2) {
                    io = args[0];
                    if (args[1] != null && !(args[1] is Hash)) {
                        throw RubyExceptions.CreateArgumentError("expected Hash but found {0}.", context.GetClassName(args[1]));
                    }
                    options = (Hash)args[1];
                } else {
                    throw RubyExceptions.CreateArgumentError("wrong number of arguments ({0} for 0..2)", args.Length);
                }

                self.BufferRef = null;
                self.Core.SetOptions(io, options);

                if (options != null) {
                    self.UseKeyCache = Sites.OptionTrue(context, options, "key_cache");
                    self.SymbolizeKeys = Sites.OptionTrue(context, options, "symbolize_keys");
                    self.FreezeOption = Sites.OptionTrue(context, options, "freeze");
                    self.AllowUnknownExt = Sites.OptionTrue(context, options, "allow_unknown_ext");
                }
                return self;
            }

            [RubyMethod("symbolize_keys?")]
            public static bool IsSymbolizeKeys(UnpackerObject/*!*/ self) {
                return self.SymbolizeKeys;
            }

            [RubyMethod("freeze?")]
            public static bool IsFreeze(UnpackerObject/*!*/ self) {
                return self.FreezeOption;
            }

            [RubyMethod("allow_unknown_ext?")]
            public static bool IsAllowUnknownExt(UnpackerObject/*!*/ self) {
                return self.AllowUnknownExt;
            }

            [RubyMethod("buffer")]
            public static BufferObject/*!*/ GetBuffer(RubyContext/*!*/ context, UnpackerObject/*!*/ self) {
                if (self.BufferRef == null) {
                    self.BufferRef = BufferObject.Wrap(context, self);
                }
                return self.BufferRef;
            }

            private Exception/*!*/ Error(int r) {
                _depth = 0;
                switch (r) {
                    case PrimitiveEof: return new EOFError("end of buffer reached");
                    case PrimitiveInvalidByte: return new MalformedFormatError("invalid byte");
                    case PrimitiveStackTooDeep: return new StackError("stack level too deep");
                    case PrimitiveUnexpectedType: return new UnexpectedTypeError("unexpected type");
                    case PrimitiveUnexpectedExtType: return new UnknownExtTypeError("unexpected extension type");
                    case PrimitiveRecursiveRaised:
                        Exception e = _raised;
                        _raised = null;
                        return e;
                    default: return new UnpackError(String.Format("logically unknown error {0}", r));
                }
            }

            [RubyMethod("read")]
            [RubyMethod("unpack")]
            public static object Read(UnpackerObject/*!*/ self) {
                int r = self.ReadObject(0);
                if (r < 0) {
                    throw self.Error(r);
                }
                return self._lastObject;
            }

            [RubyMethod("skip")]
            public static object Skip(UnpackerObject/*!*/ self) {
                int r = self.SkipObject(0);
                if (r < 0) {
                    throw self.Error(r);
                }
                return null;
            }

            [RubyMethod("skip_nil")]
            public static bool SkipNil(UnpackerObject/*!*/ self) {
                int b = self.GetHeadByte();
                if (b < 0) {
                    throw self.Error(b);
                }
                if (b == 0xc0) {
                    self._headByte = HeadByteRequired;
                    return true;
                }
                return false;
            }

            [RubyMethod("read_array_header")]
            public static object ReadArrayHeader(UnpackerObject/*!*/ self) {
                uint size;
                int r = self.ReadContainerHeader(0x90, 0xdc, 0xdd, out size);
                if (r < 0) {
                    throw self.Error(r);
                }
                return Protocols.Normalize(size);
            }

            [RubyMethod("read_map_header")]
            public static object ReadMapHeader(UnpackerObject/*!*/ self) {
                uint size;
                int r = self.ReadContainerHeader(0x80, 0xde, 0xdf, out size);
                if (r < 0) {
                    throw self.Error(r);
                }
                return Protocols.Normalize(size);
            }

            [RubyMethod("feed")]
            [RubyMethod("feed_reference")]
            public static UnpackerObject/*!*/ Feed(RubyContext/*!*/ context, UnpackerObject/*!*/ self, object data) {
                self.Core.AppendStringReference(Sites.StringValue(context, data));
                return self;
            }

            private object EachImpl(BlockParam/*!*/ block) {
                while (true) {
                    int r = ReadObject(0);
                    if (r < 0) {
                        if (r == PrimitiveEof) {
                            return null;
                        }
                        throw Error(r);
                    }
                    object result;
                    if (block.Yield(_lastObject, out result)) {
                        return result;
                    }
                }
            }

            [RubyMethod("each")]
            public static object Each(BlockParam block, UnpackerObject/*!*/ self) {
                if (block == null) {
                    return new IronRuby.Builtins.Enumerator(self, "each");
                }
                if (self.Core.HasIo) {
                    try {
                        return self.EachImpl(block);
                    } catch (EOFError) {
                        return null;
                    }
                }
                return self.EachImpl(block);
            }

            [RubyMethod("feed_each")]
            public static object FeedEach(RubyContext/*!*/ context, BlockParam block, UnpackerObject/*!*/ self, object data) {
                if (block == null) {
                    return new IronRuby.Builtins.Enumerator(self, "feed_each", data);
                }
                Feed(context, self, data);
                return Each(block, self);
            }

            [RubyMethod("reset")]
            public static object Reset(UnpackerObject/*!*/ self) {
                self.Core.Clear();
                self._headByte = HeadByteRequired;
                self._depth = 0;
                self._lastObject = null;
                self._readingRaw = null;
                self._readingRawRemaining = 0;
                return null;
            }

            [RubyMethod("registered_types_internal", RubyMethodAttributes.PrivateInstance)]
            public static Hash/*!*/ RegisteredTypesInternal(RubyContext/*!*/ context, UnpackerObject/*!*/ self) {
                return self.Registry != null ? self.Registry.ToHash(context) : new Hash(context);
            }

            [RubyMethod("register_type_internal", RubyMethodAttributes.PrivateInstance)]
            public static object RegisterTypeInternal(RubyContext/*!*/ context, UnpackerObject/*!*/ self, object type, object module, object proc) {
                if (self.IsFrozen) {
                    throw new FrozenError("can't modify frozen MessagePack::Unpacker");
                }
                int extType = Sites.ExtType(context, type);
                if (self.Registry == null) {
                    self.Registry = new UnpackerRegistry();
                }
                self.Registry.Put(module, extType, 0, proc);
                return null;
            }

            [RubyMethod("full_unpack")]
            public static object FullUnpack(UnpackerObject/*!*/ self) {
                int r = self.ReadObject(0);
                if (r < 0) {
                    throw self.Error(r);
                }
                int extra = self.Core.TopReadableSize;
                if (extra > 0) {
                    throw new MalformedFormatError(String.Format("{0} extra bytes after the deserialized object", extra));
                }
                return self._lastObject;
            }

            #region reading

            private int ReadHeadByte() {
                int r = Core.ReadByte();
                if (r == -1) {
                    return PrimitiveEof;
                }
                return _headByte = r;
            }

            private int GetHeadByte() {
                int b = _headByte;
                if (b == HeadByteRequired) {
                    b = ReadHeadByte();
                }
                return b;
            }

            private int ObjectComplete(object obj) {
                if (FreezeOption) {
                    Context.FreezeObject(obj);
                }
                _lastObject = obj;
                _headByte = HeadByteRequired;
                return PrimitiveObjectComplete;
            }

            private int ObjectCompleteFrozen(object obj) {
                _lastObject = obj;
                _headByte = HeadByteRequired;
                return PrimitiveObjectComplete;
            }

            private object CallProtected(object proc, object arg, out bool raised) {
                try {
                    raised = false;
                    return CallProc(proc, arg);
                } catch (Exception e) {
                    raised = true;
                    // thrown again once the unpacker unwinds, which starts its CLR trace over:
                    RubyExceptionData.GetInstance(e).CaptureExceptionTrace(Context);
                    _raised = e;
                    return null;
                }
            }

            private int ObjectCompleteExt(int extType, MutableString str) {
                if (OptimizedSymbolExtType && extType == SymbolExtType) {
                    if (str == null) {
                        return ObjectCompleteFrozen(Context.CreateSymbol(MutableString.CreateEmpty(RubyEncoding.UTF8), false));
                    }
                    return ObjectCompleteFrozen(Context.CreateSymbol(str));
                }

                UnpackerEntry entry = Registry != null ? Registry.Lookup(extType) : null;
                if (entry != null && entry.Proc != null) {
                    bool raised;
                    object obj = CallProtected(entry.Proc, str ?? MutableString.CreateBinary(), out raised);
                    if (raised) {
                        _lastObject = _raised;
                        return PrimitiveRecursiveRaised;
                    }
                    return ObjectComplete(obj);
                }

                if (AllowUnknownExt) {
                    return ObjectComplete(NewExtensionValue(Context, extType, str ?? MutableString.CreateBinary()));
                }

                return PrimitiveUnexpectedExtType;
            }

            private StackEntry/*!*/ Top {
                get { return _stack[_depth - 1]; }
            }

            private int StackPush(int type, long count, object obj) {
                _headByte = HeadByteRequired;
                if (StackCapacity - _depth <= 0) {
                    return PrimitiveStackTooDeep;
                }
                var next = _stack[_depth];
                if (next == null) {
                    _stack[_depth] = next = new StackEntry();
                }
                next.Count = count;
                next.Type = type;
                next.Object = obj;
                next.Key = null;
                _depth++;
                return PrimitiveContainerStart;
            }

            private bool IsReadingMapKey {
                get { return _depth > 0 && Top.Type == StackMapKey; }
            }

            private int ReadRawBodyCont() {
                long length = _readingRawRemaining;

                if (_readingRaw == null) {
                    long buffered = Core.AllReadableSize;
                    _readingRaw = MutableString.CreateBinary((int)Math.Min(length > buffered ? buffered : length, Int32.MaxValue));
                }

                do {
                    long n = Core.ReadToString(_readingRaw, length);
                    if (n == 0) {
                        return PrimitiveEof;
                    }
                    _readingRawRemaining = length = length - n;
                } while (length > 0);

                int ret;
                MutableString raw = _readingRaw;
                if (_readingRawType == RawTypeString) {
                    raw.ForceEncoding(RubyEncoding.UTF8);
                    ret = ObjectComplete(raw);
                } else if (_readingRawType == RawTypeBinary) {
                    ret = ObjectComplete(raw);
                } else {
                    ret = ObjectCompleteExt(_readingRawType, raw);
                }
                _readingRaw = null;
                return ret;
            }

            private MutableString/*!*/ ReadTopAsString(int length, bool willBeFrozen, bool utf8) {
                MutableString result = Core.ReadTopAsString(length, utf8 ? RubyEncoding.UTF8 : RubyEncoding.Binary);
                if (willBeFrozen) {
                    result = RubyOps.InternFrozenString(result);
                }
                return result;
            }

            private int ReadRawBodyBegin(int rawType) {
                if (!(rawType == RawTypeString || rawType == RawTypeBinary)) {
                    UnpackerEntry entry = Registry != null ? Registry.Lookup(rawType) : null;
                    if (entry != null && entry.Proc != null && (entry.Flags & ExtRecursive) != 0) {
                        _lastObject = null;
                        _headByte = HeadByteRequired;
                        _readingRawRemaining = 0;

                        if (StackPush(StackRecursive, 1, null) < 0) {
                            return PrimitiveStackTooDeep;
                        }
                        int barrierDepth = _depth;
                        bool raised;
                        object obj = CallProtected(entry.Proc, this, out raised);
                        _depth = barrierDepth - 1;

                        if (raised) {
                            _lastObject = _raised;
                            return PrimitiveRecursiveRaised;
                        }
                        return ObjectComplete(obj);
                    }
                }

                // try the optimized read
                long length = _readingRawRemaining;
                if (length <= Core.TopReadableSize) {
                    int len = (int)length;
                    int ret;
                    if (OptimizedSymbolExtType && SymbolExtType == rawType) {
                        MutableString str = ReadTopAsString(len, true, rawType != RawTypeBinary);
                        ret = ObjectCompleteFrozen(Context.CreateSymbol(str));
                    } else if (IsReadingMapKey && rawType == RawTypeString) {
                        // hash keys: a frozen string straight away, since Hash#[]= would copy a mutable one
                        if (SymbolizeKeys) {
                            MutableString str = ReadTopAsString(len, true, true);
                            ret = ObjectCompleteFrozen(Context.CreateSymbol(str));
                        } else {
                            ret = ObjectComplete(ReadTopAsString(len, true, true));
                        }
                    } else {
                        if (rawType == RawTypeString || rawType == RawTypeBinary) {
                            ret = ObjectComplete(ReadTopAsString(len, FreezeOption, rawType == RawTypeString));
                        } else {
                            ret = ObjectCompleteExt(rawType, ReadTopAsString(len, false, false));
                        }
                    }
                    _readingRawRemaining = 0;
                    return ret;
                }

                _readingRawType = rawType;
                return ReadRawBodyCont();
            }

            private readonly byte[]/*!*/ _cb = new byte[8];

            private int ReadRaw(long count, int rawType) {
                _readingRawRemaining = count;
                return ReadRawBodyBegin(rawType);
            }

            private int ReadPrimitive() {
                if (_readingRawRemaining > 0) {
                    return ReadRawBodyCont();
                }

                int b = GetHeadByte();
                if (b < 0) {
                    return b;
                }

                byte[] cb = _cb;

                if (b <= 0x7f) {                       // positive fixnum
                    return ObjectCompleteFrozen(ScriptingRuntimeHelpers.Int32ToObject(b));
                }
                if (b >= 0xe0) {                       // negative fixnum
                    return ObjectCompleteFrozen(ScriptingRuntimeHelpers.Int32ToObject((sbyte)b));
                }
                if (b >= 0xa0 && b <= 0xbf) {          // fixstr
                    return ReadRaw(b & 0x1f, RawTypeString);
                }
                if (b >= 0x90 && b <= 0x9f) {          // fixarray
                    int count = b & 0x0f;
                    if (count == 0) {
                        return ObjectComplete(new RubyArray());
                    }
                    return StackPush(StackArray, count, new RubyArray(count));
                }
                if (b >= 0x80 && b <= 0x8f) {          // fixmap
                    int count = b & 0x0f;
                    if (count == 0) {
                        return ObjectComplete(new Hash(Context));
                    }
                    return StackPush(StackMapKey, count * 2, new Hash(Context));
                }

                switch (b) {
                    case 0xc0: return ObjectCompleteFrozen(null);
                    case 0xc2: return ObjectCompleteFrozen(ScriptingRuntimeHelpers.False);
                    case 0xc3: return ObjectCompleteFrozen(ScriptingRuntimeHelpers.True);

                    case 0xc7: {  // ext 8
                            if (!Core.ReadAll(cb, 2)) return PrimitiveEof;
                            int length = cb[0];
                            int extType = (sbyte)cb[1];
                            if (length == 0) {
                                return ObjectCompleteExt(extType, null);
                            }
                            return ReadRaw(length, extType);
                        }
                    case 0xc8: {  // ext 16
                            if (!Core.ReadAll(cb, 3)) return PrimitiveEof;
                            int length = BinaryPrimitives.ReadUInt16BigEndian(cb);
                            int extType = (sbyte)cb[2];
                            if (length == 0) {
                                return ObjectCompleteExt(extType, null);
                            }
                            return ReadRaw(length, extType);
                        }
                    case 0xc9: {  // ext 32
                            if (!Core.ReadAll(cb, 5)) return PrimitiveEof;
                            uint length = BinaryPrimitives.ReadUInt32BigEndian(cb);
                            int extType = (sbyte)cb[4];
                            if (length == 0) {
                                return ObjectCompleteExt(extType, null);
                            }
                            return ReadRaw(length, extType);
                        }
                    case 0xca: {  // float
                            if (!Core.ReadAll(cb, 4)) return PrimitiveEof;
                            float f = BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32BigEndian(cb));
                            return ObjectComplete((double)f);
                        }
                    case 0xcb: {  // double
                            if (!Core.ReadAll(cb, 8)) return PrimitiveEof;
                            double d = BitConverter.UInt64BitsToDouble(BinaryPrimitives.ReadUInt64BigEndian(cb));
                            return ObjectComplete(d);
                        }
                    case 0xcc:
                        if (!Core.ReadAll(cb, 1)) return PrimitiveEof;
                        return ObjectCompleteFrozen(ScriptingRuntimeHelpers.Int32ToObject(cb[0]));
                    case 0xcd:
                        if (!Core.ReadAll(cb, 2)) return PrimitiveEof;
                        return ObjectCompleteFrozen(ScriptingRuntimeHelpers.Int32ToObject(BinaryPrimitives.ReadUInt16BigEndian(cb)));
                    case 0xce:
                        if (!Core.ReadAll(cb, 4)) return PrimitiveEof;
                        return ObjectComplete(Protocols.Normalize(BinaryPrimitives.ReadUInt32BigEndian(cb)));
                    case 0xcf:
                        if (!Core.ReadAll(cb, 8)) return PrimitiveEof;
                        return ObjectComplete(Protocols.Normalize(BinaryPrimitives.ReadUInt64BigEndian(cb)));
                    case 0xd0:
                        if (!Core.ReadAll(cb, 1)) return PrimitiveEof;
                        return ObjectCompleteFrozen(ScriptingRuntimeHelpers.Int32ToObject((sbyte)cb[0]));
                    case 0xd1:
                        if (!Core.ReadAll(cb, 2)) return PrimitiveEof;
                        return ObjectCompleteFrozen(ScriptingRuntimeHelpers.Int32ToObject(BinaryPrimitives.ReadInt16BigEndian(cb)));
                    case 0xd2:
                        if (!Core.ReadAll(cb, 4)) return PrimitiveEof;
                        return ObjectComplete(ScriptingRuntimeHelpers.Int32ToObject(BinaryPrimitives.ReadInt32BigEndian(cb)));
                    case 0xd3:
                        if (!Core.ReadAll(cb, 8)) return PrimitiveEof;
                        return ObjectComplete(Protocols.Normalize(BinaryPrimitives.ReadInt64BigEndian(cb)));

                    case 0xd4:  // fixext 1
                    case 0xd5:  // fixext 2
                    case 0xd6:  // fixext 4
                    case 0xd7:  // fixext 8
                    case 0xd8: {  // fixext 16
                            if (!Core.ReadAll(cb, 1)) return PrimitiveEof;
                            int extType = (sbyte)cb[0];
                            return ReadRaw(1 << (b - 0xd4), extType);
                        }

                    case 0xd9:  // str 8
                        if (!Core.ReadAll(cb, 1)) return PrimitiveEof;
                        return ReadRaw(cb[0], RawTypeString);
                    case 0xda:  // str 16
                        if (!Core.ReadAll(cb, 2)) return PrimitiveEof;
                        return ReadRaw(BinaryPrimitives.ReadUInt16BigEndian(cb), RawTypeString);
                    case 0xdb:  // str 32
                        if (!Core.ReadAll(cb, 4)) return PrimitiveEof;
                        return ReadRaw(BinaryPrimitives.ReadUInt32BigEndian(cb), RawTypeString);

                    case 0xc4:  // bin 8
                        if (!Core.ReadAll(cb, 1)) return PrimitiveEof;
                        return ReadRaw(cb[0], RawTypeBinary);
                    case 0xc5:  // bin 16
                        if (!Core.ReadAll(cb, 2)) return PrimitiveEof;
                        return ReadRaw(BinaryPrimitives.ReadUInt16BigEndian(cb), RawTypeBinary);
                    case 0xc6:  // bin 32
                        if (!Core.ReadAll(cb, 4)) return PrimitiveEof;
                        return ReadRaw(BinaryPrimitives.ReadUInt32BigEndian(cb), RawTypeBinary);

                    case 0xdc: {  // array 16
                            if (!Core.ReadAll(cb, 2)) return PrimitiveEof;
                            int count = BinaryPrimitives.ReadUInt16BigEndian(cb);
                            if (count == 0) {
                                return ObjectComplete(new RubyArray());
                            }
                            return StackPush(StackArray, count, new RubyArray(Math.Min(count, Int16.MaxValue)));
                        }
                    case 0xdd: {  // array 32
                            if (!Core.ReadAll(cb, 4)) return PrimitiveEof;
                            uint count = BinaryPrimitives.ReadUInt32BigEndian(cb);
                            if (count == 0) {
                                return ObjectComplete(new RubyArray());
                            }
                            return StackPush(StackArray, count, new RubyArray((int)Math.Min(count, Int16.MaxValue)));
                        }
                    case 0xde: {  // map 16
                            if (!Core.ReadAll(cb, 2)) return PrimitiveEof;
                            int count = BinaryPrimitives.ReadUInt16BigEndian(cb);
                            if (count == 0) {
                                return ObjectComplete(new Hash(Context));
                            }
                            return StackPush(StackMapKey, (long)count * 2, new Hash(Context));
                        }
                    case 0xdf: {  // map 32
                            if (!Core.ReadAll(cb, 4)) return PrimitiveEof;
                            uint count = BinaryPrimitives.ReadUInt32BigEndian(cb);
                            if (count == 0) {
                                return ObjectComplete(new Hash(Context));
                            }
                            return StackPush(StackMapKey, (long)count * 2, new Hash(Context));
                        }

                    default:
                        return PrimitiveInvalidByte;
                }
            }

            private int ReadContainerHeader(int fixBase, int head16, int head32, out uint size) {
                size = 0;
                int b = GetHeadByte();
                if (b < 0) {
                    return b;
                }

                if (fixBase <= b && b <= fixBase + 0x0f) {
                    size = (uint)(b & 0x0f);
                } else if (b == head16) {
                    if (!Core.ReadAll(_cb, 2)) return PrimitiveEof;
                    size = BinaryPrimitives.ReadUInt16BigEndian(_cb);
                } else if (b == head32) {
                    if (!Core.ReadAll(_cb, 4)) return PrimitiveEof;
                    size = BinaryPrimitives.ReadUInt32BigEndian(_cb);
                } else {
                    return PrimitiveUnexpectedType;
                }

                _headByte = HeadByteRequired;
                return 0;
            }

            /// <summary>msgpack_unpacker_read.  One call reads a whole document, so it is compiled optimized up front.</summary>
            [MethodImpl(MethodImplOptions.AggressiveOptimization)]
            internal int ReadObject(int targetStackDepth) {
                bool stackAllocated = _stack == null;
                if (stackAllocated) {
                    _stack = new StackEntry[StackCapacity];
                    _depth = 0;
                }

                while (true) {
                    int r = ReadPrimitive();
                    if (r < 0) {
                        if (r != PrimitiveEof) {
                            // the stack is kept on EOF, as parsing may be resumed
                            FreeStack(stackAllocated);
                        }
                        return r;
                    }
                    if (r == PrimitiveContainerStart) {
                        continue;
                    }

                    if (_depth == 0) {
                        FreeStack(stackAllocated);
                        return PrimitiveObjectComplete;
                    }

                    while (true) {
                        StackEntry top = Top;
                        switch (top.Type) {
                            case StackArray:
                                ((RubyArray)top.Object).Add(_lastObject);
                                break;
                            case StackMapKey:
                                top.Key = _lastObject;
                                top.Type = StackMapValue;
                                break;
                            case StackMapValue:
                                object key = top.Key;
                                if (SymbolizeKeys && key is MutableString) {
                                    key = Context.CreateSymbol((MutableString)key);
                                }
                                RubyUtils.SetHashElement(Context, (Hash)top.Object, key, _lastObject);
                                top.Type = StackMapKey;
                                break;
                            case StackRecursive:
                                FreeStack(stackAllocated);
                                return PrimitiveObjectComplete;
                        }

                        long count = --top.Count;
                        if (count != 0) {
                            break;
                        }

                        object completed = top.Object;
                        top.Object = null;
                        top.Key = null;
                        ObjectComplete(completed);
                        if (--_depth <= targetStackDepth) {
                            FreeStack(stackAllocated);
                            return PrimitiveObjectComplete;
                        }
                    }
                }
            }

            /// <summary>msgpack_unpacker_skip.</summary>
            internal int SkipObject(int targetStackDepth) {
                bool stackAllocated = _stack == null;
                if (stackAllocated) {
                    _stack = new StackEntry[StackCapacity];
                    _depth = 0;
                }

                while (true) {
                    int r = ReadPrimitive();
                    if (r < 0) {
                        FreeStack(stackAllocated);
                        return r;
                    }
                    if (r == PrimitiveContainerStart) {
                        continue;
                    }

                    if (_depth == 0) {
                        FreeStack(stackAllocated);
                        return PrimitiveObjectComplete;
                    }

                    while (true) {
                        StackEntry top = Top;
                        long count = --top.Count;
                        if (count != 0) {
                            break;
                        }
                        top.Object = null;
                        top.Key = null;
                        ObjectComplete(null);
                        if (--_depth <= targetStackDepth) {
                            FreeStack(stackAllocated);
                            return PrimitiveObjectComplete;
                        }
                    }
                }
            }

            private void FreeStack(bool stackAllocated) {
                if (stackAllocated) {
                    _stack = null;
                    _depth = 0;
                }
            }

            #endregion
        }

        #endregion

        #region Factory

        [RubyClass("Factory")]
        public sealed class FactoryObject : RubyObject, IMemorySized {
            internal PackerRegistry/*!*/ Pkrg = new PackerRegistry();
            internal UnpackerRegistry Ukrg;
            internal bool HasBigintExtType;
            internal bool HasSymbolExtType;
            internal bool OptimizedSymbolExtType;
            internal int SymbolExtType;

            public FactoryObject(RubyClass/*!*/ rubyClass) : base(rubyClass) {
            }

            long IMemorySized.ExtraMemorySize {
                get { return 40 + (Ukrg != null ? 2056 : 0); }
            }

            protected override RubyObject/*!*/ CreateInstance() {
                return new FactoryObject(ImmediateClass.NominalClass);
            }

            [RubyConstructor]
            public static FactoryObject/*!*/ Create(RubyClass/*!*/ self, params object[]/*!*/ args) {
                var result = new FactoryObject(self);
                Initialize(result, args);
                return result;
            }

            [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
            public static object Initialize(FactoryObject/*!*/ self, params object[]/*!*/ args) {
                self.Pkrg = new PackerRegistry();
                self.HasSymbolExtType = false;
                if (args.Length != 0) {
                    throw RubyExceptions.CreateArgumentError("wrong number of arguments ({0} for 0)", args.Length);
                }
                return null;
            }

            [RubyMethod("dup")]
            public static FactoryObject/*!*/ Dup(FactoryObject/*!*/ self) {
                var clone = new FactoryObject(self.ImmediateClass.NominalClass);
                clone.HasBigintExtType = self.HasBigintExtType;
                clone.HasSymbolExtType = self.HasSymbolExtType;
                clone.OptimizedSymbolExtType = self.OptimizedSymbolExtType;
                clone.SymbolExtType = self.SymbolExtType;
                clone.Ukrg = self.Ukrg != null ? self.Ukrg.Borrow() : null;
                clone.Pkrg = self.Pkrg.Dup();
                return clone;
            }

            [RubyMethod("freeze")]
            public static FactoryObject/*!*/ Freeze(FactoryObject/*!*/ self) {
                if (!self.IsFrozen) {
                    if (!self.Pkrg.IsEmpty) {
                        self.Pkrg.Frozen = true;
                        if (self.Pkrg.Cache == null) {
                            self.Pkrg.Cache = new Dictionary<RubyModule, PackerEntry>();
                        }
                    }
                    self.Freeze();
                }
                return self;
            }

            [RubyMethod("packer")]
            public static PackerObject/*!*/ Packer(RubyContext/*!*/ context, FactoryObject/*!*/ self, params object[]/*!*/ args) {
                PackerObject packer = PackerObject.CreateDefault(context, args);
                packer.Registry = self.Pkrg.Borrow();
                packer.HasBigintExtType = self.HasBigintExtType;
                packer.HasSymbolExtType = self.HasSymbolExtType;
                return packer;
            }

            [RubyMethod("unpacker")]
            public static UnpackerObject/*!*/ Unpacker(RubyContext/*!*/ context, FactoryObject/*!*/ self, params object[]/*!*/ args) {
                UnpackerObject unpacker = UnpackerObject.CreateDefault(context, args);
                if (self.Ukrg != null) {
                    unpacker.Registry = self.Ukrg.Borrow();
                }
                unpacker.OptimizedSymbolExtType = self.OptimizedSymbolExtType;
                unpacker.SymbolExtType = self.SymbolExtType;
                return unpacker;
            }

            [RubyMethod("registered_types_internal", RubyMethodAttributes.PrivateInstance)]
            public static RubyArray/*!*/ RegisteredTypesInternal(RubyContext/*!*/ context, FactoryObject/*!*/ self) {
                return NewArray(self.Pkrg.ToHash(context), self.Ukrg != null ? self.Ukrg.ToHash(context) : new Hash(context));
            }

            [RubyMethod("register_type_internal", RubyMethodAttributes.PrivateInstance)]
            public static object RegisterTypeInternal(RubyContext/*!*/ context, FactoryObject/*!*/ self, object type, object module, object options) {
                if (!(type is int)) {
                    throw Sites.WrongType(context, type, "Integer");
                }
                var mod = module as RubyModule;
                if (mod == null) {
                    throw RubyExceptions.CreateArgumentError("expected Module/Class but found {0}.", context.GetClassName(module));
                }

                int flags = 0;
                object packerProc = null;
                object unpackerProc = null;
                Hash opts = null;
                if (options != null) {
                    opts = options as Hash;
                    if (opts == null) {
                        throw Sites.WrongType(context, options, "Hash");
                    }
                    packerProc = Sites.Option(context, opts, "packer");
                    unpackerProc = Sites.Option(context, opts, "unpacker");
                }

                if (self.IsFrozen) {
                    throw new FrozenError("can't modify frozen MessagePack::Factory");
                }

                int extType = (int)type;
                if (extType < -128 || extType > 127) {
                    throw Sites.SignedCharRange(extType);
                }

                if (ReferenceEquals(mod, context.GetClass(typeof(RubySymbol)))) {
                    self.SymbolExtType = extType;
                    self.HasSymbolExtType = options == null || Protocols.IsTrue(packerProc);
                    self.OptimizedSymbolExtType = opts != null && Sites.OptionTrue(context, opts, "optimized_symbols_parsing");
                }

                if (ReferenceEquals(mod, context.IntegerClass)) {
                    self.HasBigintExtType = false;
                }

                if (opts != null) {
                    if (Sites.OptionTrue(context, opts, "oversized_integer_extension")) {
                        if (ReferenceEquals(mod, context.IntegerClass)) {
                            self.HasBigintExtType = true;
                        } else {
                            throw RubyExceptions.CreateArgumentError("oversized_integer_extension: true is only for Integer class");
                        }
                    }
                    if (Sites.OptionTrue(context, opts, "recursive")) {
                        flags |= ExtRecursive;
                    }
                }

                if (self.Pkrg.Frozen) {
                    self.Pkrg = self.Pkrg.Dup();
                }
                self.Pkrg.Put(mod, extType, flags, packerProc);
                if (self.Ukrg == null) {
                    self.Ukrg = new UnpackerRegistry();
                }
                self.Ukrg.Put(mod, extType, flags, unpackerProc);
                return null;
            }
        }

        #endregion
    }
}
