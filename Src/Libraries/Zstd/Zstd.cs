/* ****************************************************************************
 *
 * IronRuby's implementation of the zstd-ruby gem's C extension.
 *
 * zstd-ruby 1.5.7.0 is Ruby over one C extension, zstd-ruby/zstdruby, which binds
 * the libzstd 1.5.7 it bundles: Zstd.compress / decompress (levels and
 * dictionaries), the deprecated *_using_dict pair, skippable frames, Zstd::CDict and
 * Zstd::DDict, and the streaming classes Zstd::StreamingCompress and
 * Zstd::StreamingDecompress that the gem's StreamWriter / StreamReader wrap.
 *
 * The codec is ZstdSharp (the ZstdSharp.Port NuGet package, MIT), a line-by-line C#
 * port of the same libzstd 1.5.7, called through its ZSTD_* functions exactly where
 * ext/zstdruby calls libzstd's - so levels, frame parameters, dictionaries, streaming
 * block boundaries and error names come from the same code.  This file is a port of
 * ext/zstdruby/{main,zstdruby,skippable_frame,streaming_compress,streaming_decompress}.c
 * and common.h, keeping what is observable: argument handling and the deprecation
 * warnings, the messages of every RuntimeError, the "not compressed by zstd" check,
 * decompress's buffered fallback for frames without a content size (which ignores a
 * dictionary given to decompress_using_dict, as upstream's does), and
 * StreamingCompress#write's return value and its discarded output.
 *
 * The gem's own Ruby (lib/zstd-ruby.rb, StreamWriter, StreamReader, Zstd::VERSION) is
 * not vendored: an installed zstd-ruby gem loads on top of this.
 *
 * This source code is subject to terms and conditions of the Apache License,
 * Version 2.0. A copy of the license can be found in the License.html file at
 * the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using IronRuby.Builtins;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting.Runtime;
using ZstdSharp.Unsafe;
using Z = ZstdSharp.Unsafe.Methods;

namespace IronRuby.StandardLibrary.Zstd {

    internal static class Native {
        internal const int ClevelDefault = 3;                       // ZSTD_CLEVEL_DEFAULT
        internal const ulong ContentSizeUnknown = unchecked(0UL - 1);
        internal const ulong ContentSizeError = unchecked(0UL - 2);
        internal const int SkippableHeaderSize = 8;                 // ZSTD_SKIPPABLEHEADERSIZE

        internal static bool IsError(nuint code) {
            return Z.ZSTD_isError(code);
        }

        internal static string/*!*/ ErrorName(nuint code) {
            return Z.ZSTD_getErrorName(code);
        }

        internal static MutableString/*!*/ Binary(byte[]/*!*/ bytes, int count) {
            if (count == bytes.Length) {
                return MutableString.CreateBinary(bytes, RubyEncoding.Binary);
            }
            var result = new byte[count];
            Buffer.BlockCopy(bytes, 0, result, 0, count);
            return MutableString.CreateBinary(result, RubyEncoding.Binary);
        }
    }

    internal static class Args {
        internal static string/*!*/ TypeName(RubyContext/*!*/ context, object obj) {
            if (obj == null) {
                return "nil";
            }
            if (obj is bool) {
                return (bool)obj ? "true" : "false";
            }
            return context.GetClassName(obj);
        }

        private sealed class Sites {
            internal readonly ConcurrentDictionary<string, CallSite<Func<CallSite, object, object>>> Sites0 =
                new ConcurrentDictionary<string, CallSite<Func<CallSite, object, object>>>();
            internal readonly ConcurrentDictionary<string, CallSite<Func<CallSite, object, object, object>>> Sites1 =
                new ConcurrentDictionary<string, CallSite<Func<CallSite, object, object, object>>>();
        }

        private static readonly object _sitesKey = new object();

        private static Sites/*!*/ GetSites(RubyContext/*!*/ context) {
            return (Sites)context.GetOrCreateLibraryData(_sitesKey, () => new Sites());
        }

        /// <summary>rb_funcall with no arguments.</summary>
        internal static object Call(RubyContext/*!*/ context, object target, string/*!*/ name) {
            var site = GetSites(context).Sites0.GetOrAdd(name, n => CallSite<Func<CallSite, object, object>>.Create(
                RubyCallAction.Make(context, n, RubyCallSignature.WithImplicitSelf(0))));
            return site.Target(site, target);
        }

        /// <summary>rb_funcall with one argument.</summary>
        internal static object Call(RubyContext/*!*/ context, object target, string/*!*/ name, object arg) {
            var site = GetSites(context).Sites1.GetOrAdd(name, n => CallSite<Func<CallSite, object, object, object>>.Create(
                RubyCallAction.Make(context, n, RubyCallSignature.WithImplicitSelf(1))));
            return site.Target(site, target, arg);
        }

        private static bool RespondTo(RubyContext/*!*/ context, object target, string/*!*/ name) {
            return Protocols.IsTrue(Call(context, target, "respond_to?", context.EncodeIdentifier(name)));
        }

        /// <summary>StringValue</summary>
        internal static MutableString/*!*/ StringValue(RubyContext/*!*/ context, object obj) {
            var str = obj as MutableString;
            if (str != null) {
                return str;
            }
            if (obj != null && !(obj is bool) && RespondTo(context, obj, "to_str")) {
                object converted = Call(context, obj, "to_str");
                str = converted as MutableString;
                if (str != null) {
                    return str;
                }
                throw RubyExceptions.CreateTypeError("can't convert {0} to String ({0}#to_str gives {1})",
                    TypeName(context, obj), TypeName(context, converted));
            }
            throw RubyExceptions.CreateTypeError("no implicit conversion of {0} into String", TypeName(context, obj));
        }

        internal static string/*!*/ Inspect(RubyContext/*!*/ context, object obj) {
            return Call(context, obj, "inspect").ToString();
        }

        /// <summary>NUM2INT</summary>
        internal static int ToInt(RubyContext/*!*/ context, object obj) {
            if (obj is int) {
                return (int)obj;
            }
            if (obj == null) {
                throw RubyExceptions.CreateTypeError("no implicit conversion from nil to integer");
            }
            if (obj is double) {
                double d = (double)obj;
                if (Double.IsNaN(d) || Double.IsInfinity(d) || d >= 2147483648.0 || d < -2147483648.0) {
                    throw RubyExceptions.CreateRangeError(String.Format("float {0} out of range of integer", Inspect(context, obj)));
                }
                return (int)d;
            }
            if (obj is long || obj is BigInteger) {
                BigInteger big = obj is long ? new BigInteger((long)obj) : (BigInteger)obj;
                if (big > Int32.MaxValue || big < Int32.MinValue) {
                    throw RubyExceptions.CreateRangeError(String.Format("integer {0} too {1} to convert to 'int'",
                        big.ToString(), big.Sign > 0 ? "big" : "small"));
                }
                return (int)big;
            }
            if (!(obj is bool) && RespondTo(context, obj, "to_int")) {
                object converted = Call(context, obj, "to_int");
                if (converted is int || converted is long || converted is BigInteger) {
                    return ToInt(context, converted);
                }
                throw RubyExceptions.CreateTypeError("can't convert {0} to Integer ({0}#to_int gives {1})",
                    TypeName(context, obj), TypeName(context, converted));
            }
            throw RubyExceptions.CreateTypeError("no implicit conversion of {0} into Integer", TypeName(context, obj));
        }

        /// <summary>
        /// rb_scan_args with a trailing ':' : splits keyword arguments (a trailing Hash flagged
        /// IsKeywordArguments) from the positional ones and checks the positional count.
        /// </summary>
        internal static Hash Split(object[]/*!*/ args, int required, int optional, out object[]/*!*/ positional) {
            Hash kwargs = null;
            int n = args.Length;
            if (n > 0) {
                var last = args[n - 1] as Hash;
                if (last != null && last.IsKeywordArguments) {
                    kwargs = last;
                    n--;
                }
            }
            if (n < required || n > required + optional) {
                throw RubyExceptions.CreateArgumentError(optional == 0
                    ? String.Format("wrong number of arguments (given {0}, expected {1})", n, required)
                    : String.Format("wrong number of arguments (given {0}, expected {1}..{2})", n, required, required + optional));
            }
            positional = new object[required + optional];
            Array.Copy(args, positional, n);
            return kwargs;
        }

        /// <summary>
        /// rb_get_kwargs(kwargs, keys, 0, keys.Length, values): the values of the keys given
        /// (Missing for the others); any other key is an ArgumentError.
        /// </summary>
        internal static readonly object Missing = new object();

        internal static object[]/*!*/ GetKwargs(RubyContext/*!*/ context, Hash kwargs, params string[]/*!*/ keys) {
            var values = new object[keys.Length];
            for (int i = 0; i < values.Length; i++) {
                values[i] = Missing;
            }
            if (kwargs == null || kwargs.Count == 0) {
                return values;
            }
            var unknown = new List<object>();
            foreach (var entry in kwargs) {
                var sym = entry.Key as RubySymbol;
                int index = sym == null ? -1 : Array.IndexOf(keys, sym.ToString());
                if (index < 0) {
                    unknown.Add(entry.Key);
                } else {
                    values[index] = entry.Value;
                }
            }
            if (unknown.Count > 0) {
                var names = new List<string>();
                foreach (var key in unknown) {
                    names.Add(Inspect(context, key));
                }
                throw RubyExceptions.CreateArgumentError("unknown keyword{0}: {1}", unknown.Count > 1 ? "s" : "", String.Join(", ", names));
            }
            return values;
        }

        internal static bool Given(object value) {
            return value != Missing && value != null;
        }
    }

    /// <summary>The C's ZSTD_CCtx* / ZSTD_DCtx* / dictionaries, freed when collected.</summary>
    internal unsafe sealed class CompressContext : IDisposable {
        internal ZSTD_CCtx_s* Ptr;
        internal object KeepAlive;   // a CDict the context refers to

        internal CompressContext() {
            Ptr = Z.ZSTD_createCCtx();
            if (Ptr == null) {
                throw RubyExceptions.CreateRuntimeError("ZSTD_createCCtx error");
            }
        }

        public void Dispose() {
            if (Ptr != null) {
                Z.ZSTD_freeCCtx(Ptr);
                Ptr = null;
            }
            GC.SuppressFinalize(this);
        }

        ~CompressContext() {
            if (Ptr != null) {
                Z.ZSTD_freeCCtx(Ptr);
                Ptr = null;
            }
        }
    }

    internal unsafe sealed class DecompressContext : IDisposable {
        internal ZSTD_DCtx_s* Ptr;
        internal object KeepAlive;   // a DDict the context refers to

        internal DecompressContext() {
            Ptr = Z.ZSTD_createDCtx();
            if (Ptr == null) {
                throw RubyExceptions.CreateRuntimeError("ZSTD_createDCtx failed");
            }
        }

        public void Dispose() {
            if (Ptr != null) {
                Z.ZSTD_freeDCtx(Ptr);
                Ptr = null;
            }
            GC.SuppressFinalize(this);
        }

        ~DecompressContext() {
            if (Ptr != null) {
                Z.ZSTD_freeDCtx(Ptr);
                Ptr = null;
            }
        }
    }

    [RubyModule("Zstd")]
    public static unsafe class ZstdOps {

        #region common.h

        /// <summary>convert_compression_level</summary>
        private static int ConvertLevel(RubyContext/*!*/ context, object value) {
            return value == null ? Native.ClevelDefault : Args.ToInt(context, value);
        }

        /// <summary>set_compress_params</summary>
        internal static void SetCompressParams(RubyContext/*!*/ context, CompressContext/*!*/ ctx, object levelFromArgs, Hash kwargs) {
            object[] kw = Args.GetKwargs(context, kwargs, "level", "dict");
            int level = Native.ClevelDefault;
            if (Args.Given(kw[0])) {
                level = ConvertLevel(context, kw[0]);
            } else if (levelFromArgs != null) {
                context.ReportWarning("`level` in args is deprecated; use keyword args `level:` instead.");
                level = ConvertLevel(context, levelFromArgs);
            }
            Z.ZSTD_CCtx_setParameter(ctx.Ptr, ZSTD_cParameter.ZSTD_c_compressionLevel, level);

            if (Args.Given(kw[1])) {
                object dict = kw[1];
                var cdict = dict as CDict;
                if (cdict != null && context.GetClassOf(dict) == CDict.GetClass(context)) {
                    nuint ret = Z.ZSTD_CCtx_refCDict(ctx.Ptr, cdict.Ptr);
                    if (Native.IsError(ret)) {
                        throw RubyExceptions.CreateRuntimeError("ZSTD_CCtx_refCDict failed");
                    }
                    ctx.KeepAlive = cdict;
                } else if (dict is MutableString) {
                    byte[] bytes = ((MutableString)dict).ToByteArray();
                    fixed (byte* p = bytes) {
                        nuint ret = Z.ZSTD_CCtx_loadDictionary(ctx.Ptr, p, (nuint)bytes.Length);
                        if (Native.IsError(ret)) {
                            throw RubyExceptions.CreateRuntimeError("ZSTD_CCtx_loadDictionary failed");
                        }
                    }
                } else {
                    throw RubyExceptions.CreateArgumentError("`dict:` must be a Zstd::CDict or a String");
                }
            }
        }

        /// <summary>set_decompress_params</summary>
        internal static void SetDecompressParams(RubyContext/*!*/ context, DecompressContext/*!*/ dctx, Hash kwargs) {
            object[] kw = Args.GetKwargs(context, kwargs, "dict");
            if (Args.Given(kw[0])) {
                object dict = kw[0];
                var ddict = dict as DDict;
                if (ddict != null && context.GetClassOf(dict) == DDict.GetClass(context)) {
                    nuint ret = Z.ZSTD_DCtx_refDDict(dctx.Ptr, ddict.Ptr);
                    if (Native.IsError(ret)) {
                        throw RubyExceptions.CreateRuntimeError("ZSTD_DCtx_refDDict failed");
                    }
                    dctx.KeepAlive = ddict;
                } else if (dict is MutableString) {
                    byte[] bytes = ((MutableString)dict).ToByteArray();
                    fixed (byte* p = bytes) {
                        nuint ret = Z.ZSTD_DCtx_loadDictionary(dctx.Ptr, p, (nuint)bytes.Length);
                        if (Native.IsError(ret)) {
                            // sic: upstream names the CCtx function here
                            throw RubyExceptions.CreateRuntimeError("ZSTD_CCtx_loadDictionary failed");
                        }
                    }
                } else {
                    throw RubyExceptions.CreateArgumentError("`dict:` must be a Zstd::DDict or a String");
                }
            }
        }

        #endregion

        #region main.c, zstdruby.c

        [RubyMethod("zstd_version", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("zstd_version", RubyMethodAttributes.PublicSingleton)]
        public static int ZstdVersion(object self) {
            return (int)Z.ZSTD_versionNumber();
        }

        [RubyMethod("compress", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("compress", RubyMethodAttributes.PublicSingleton)]
        public static MutableString/*!*/ Compress(RubyContext/*!*/ context, object self, params object[]/*!*/ args) {
            object[] pos;
            Hash kwargs = Args.Split(args, 1, 1, out pos);

            using (var ctx = new CompressContext()) {
                SetCompressParams(context, ctx, pos[1], kwargs);

                byte[] input = Args.StringValue(context, pos[0]).ToByteArray();
                nuint bound = Z.ZSTD_compressBound((nuint)input.Length);
                var output = new byte[(int)bound];
                nuint ret;
                fixed (byte* src = input)
                fixed (byte* dst = output) {
                    ret = Z.ZSTD_compress2(ctx.Ptr, dst, bound, src, (nuint)input.Length);
                }
                if (Native.IsError(ret)) {
                    throw RubyExceptions.CreateRuntimeError("compress error error code: " + Native.ErrorName(ret));
                }
                return Native.Binary(output, (int)ret);
            }
        }

        [RubyMethod("compress_using_dict", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("compress_using_dict", RubyMethodAttributes.PublicSingleton)]
        public static MutableString/*!*/ CompressUsingDict(RubyContext/*!*/ context, object self, params object[]/*!*/ args) {
            context.ReportWarning("Zstd.compress_using_dict is deprecated; use Zstd.compress with `dict:` instead.");
            if (args.Length < 2 || args.Length > 3) {
                throw RubyExceptions.CreateArgumentError("wrong number of arguments (given {0}, expected 2..3)", args.Length);
            }
            int level = ConvertLevel(context, args.Length > 2 ? args[2] : null);
            byte[] input = Args.StringValue(context, args[0]).ToByteArray();
            nuint bound = Z.ZSTD_compressBound((nuint)input.Length);
            byte[] dict = Args.StringValue(context, args[1]).ToByteArray();

            ZSTD_CDict_s* cdict;
            fixed (byte* d = dict) {
                cdict = Z.ZSTD_createCDict(d, (nuint)dict.Length, level);
            }
            if (cdict == null) {
                throw RubyExceptions.CreateRuntimeError("ZSTD_createCDict failed");
            }
            try {
                using (var ctx = new CompressContext()) {
                    var output = new byte[(int)bound];
                    nuint size;
                    fixed (byte* src = input)
                    fixed (byte* dst = output) {
                        size = Z.ZSTD_compress_usingCDict(ctx.Ptr, dst, bound, src, (nuint)input.Length, cdict);
                    }
                    if (Native.IsError(size)) {
                        throw RubyExceptions.CreateRuntimeError("compress failed: " + Native.ErrorName(size));
                    }
                    return Native.Binary(output, (int)size);
                }
            } finally {
                Z.ZSTD_freeCDict(cdict);
            }
        }

        /// <summary>decompress_buffered</summary>
        private static MutableString/*!*/ DecompressBuffered(DecompressContext/*!*/ dctx, byte[]/*!*/ input) {
            var result = new MemoryStream();
            int outSize = (int)Z.ZSTD_DStreamOutSize();
            var buffer = new byte[outSize];
            fixed (byte* src = input)
            fixed (byte* dst = buffer) {
                ZSTD_inBuffer_s inBuf = new ZSTD_inBuffer_s { src = src, size = (nuint)input.Length, pos = 0 };
                while (inBuf.pos < inBuf.size) {
                    ZSTD_outBuffer_s outBuf = new ZSTD_outBuffer_s { dst = dst, size = (nuint)outSize, pos = 0 };
                    nuint ret = Z.ZSTD_decompressStream(dctx.Ptr, &outBuf, &inBuf);
                    if (Native.IsError(ret)) {
                        throw RubyExceptions.CreateRuntimeError("ZSTD_decompressStream failed: " + Native.ErrorName(ret));
                    }
                    result.Write(buffer, 0, (int)outBuf.pos);
                }
            }
            return MutableString.CreateBinary(result.ToArray(), RubyEncoding.Binary);
        }

        private static byte[]/*!*/ NewOutput(ulong size) {
            if (size > (ulong)Array.MaxLength) {
                throw new OutOfMemoryException("failed to allocate memory");
            }
            return new byte[(int)size];
        }

        [RubyMethod("decompress", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("decompress", RubyMethodAttributes.PublicSingleton)]
        public static MutableString/*!*/ Decompress(RubyContext/*!*/ context, object self, params object[]/*!*/ args) {
            object[] pos;
            Hash kwargs = Args.Split(args, 1, 0, out pos);
            byte[] input = Args.StringValue(context, pos[0]).ToByteArray();

            using (var dctx = new DecompressContext()) {
                SetDecompressParams(context, dctx, kwargs);

                ulong size;
                fixed (byte* src = input) {
                    size = Z.ZSTD_getFrameContentSize(src, (nuint)input.Length);
                }
                if (size == Native.ContentSizeError) {
                    throw RubyExceptions.CreateRuntimeError("not compressed by zstd: " + Native.ErrorName(unchecked((nuint)size)));
                }
                if (size == Native.ContentSizeUnknown) {
                    return DecompressBuffered(dctx, input);
                }

                byte[] output = NewOutput(size);
                nuint ret;
                fixed (byte* src = input)
                fixed (byte* dst = output) {
                    ret = Z.ZSTD_decompressDCtx(dctx.Ptr, dst, (nuint)size, src, (nuint)input.Length);
                }
                if (Native.IsError(ret)) {
                    throw RubyExceptions.CreateRuntimeError("decompress error: " + Native.ErrorName(ret));
                }
                // sic: the whole buffer, sized from the first frame's header
                return MutableString.CreateBinary(output, RubyEncoding.Binary);
            }
        }

        [RubyMethod("decompress_using_dict", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("decompress_using_dict", RubyMethodAttributes.PublicSingleton)]
        public static MutableString/*!*/ DecompressUsingDict(RubyContext/*!*/ context, object self, params object[]/*!*/ args) {
            context.ReportWarning("Zstd.decompress_using_dict is deprecated; use Zstd.decompress with `dict:` instead.");
            if (args.Length != 2) {
                throw RubyExceptions.CreateArgumentError("wrong number of arguments (given {0}, expected 2)", args.Length);
            }
            byte[] input = Args.StringValue(context, args[0]).ToByteArray();
            byte[] dict = Args.StringValue(context, args[1]).ToByteArray();

            ZSTD_DDict_s* ddict;
            fixed (byte* d = dict) {
                ddict = Z.ZSTD_createDDict(d, (nuint)dict.Length);
            }
            if (ddict == null) {
                throw RubyExceptions.CreateRuntimeError("ZSTD_createDDict failed");
            }
            try {
                uint expected = Z.ZSTD_getDictID_fromDDict(ddict);
                uint actual;
                ulong size;
                fixed (byte* src = input) {
                    actual = Z.ZSTD_getDictID_fromFrame(src, (nuint)input.Length);
                }
                if (expected != actual) {
                    throw RubyExceptions.CreateRuntimeError("DictID mismatch");
                }

                using (var dctx = new DecompressContext()) {
                    fixed (byte* src = input) {
                        size = Z.ZSTD_getFrameContentSize(src, (nuint)input.Length);
                    }
                    if (size == Native.ContentSizeError) {
                        throw RubyExceptions.CreateRuntimeError("not compressed by zstd: " + Native.ErrorName(unchecked((nuint)size)));
                    }
                    if (size == Native.ContentSizeUnknown) {
                        // sic: without the dictionary
                        return DecompressBuffered(dctx, input);
                    }
                    byte[] output = NewOutput(size);
                    nuint ret;
                    fixed (byte* src = input)
                    fixed (byte* dst = output) {
                        ret = Z.ZSTD_decompress_usingDDict(dctx.Ptr, dst, (nuint)size, src, (nuint)input.Length, ddict);
                    }
                    if (Native.IsError(ret)) {
                        throw RubyExceptions.CreateRuntimeError("decompress error: " + Native.ErrorName(ret));
                    }
                    return MutableString.CreateBinary(output, RubyEncoding.Binary);
                }
            } finally {
                Z.ZSTD_freeDDict(ddict);
            }
        }

        #endregion

        #region skippable_frame.c

        [RubyMethod("write_skippable_frame", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("write_skippable_frame", RubyMethodAttributes.PublicSingleton)]
        public static MutableString/*!*/ WriteSkippableFrame(RubyContext/*!*/ context, object self, params object[]/*!*/ args) {
            object[] pos;
            Hash kwargs = Args.Split(args, 2, 0, out pos);
            object[] kw = Args.GetKwargs(context, kwargs, "magic_variant");
            uint magicVariant = kw[0] != Args.Missing ? unchecked((uint)Args.ToInt(context, kw[0])) : 0;

            byte[] input = Args.StringValue(context, pos[0]).ToByteArray();
            byte[] skip = Args.StringValue(context, pos[1]).ToByteArray();

            // The C copies the input into a buffer of this size and has the frame written over
            // it; only the frame is kept.
            int dstSize = input.Length + Native.SkippableHeaderSize + skip.Length;
            var output = new byte[dstSize];
            Buffer.BlockCopy(input, 0, output, 0, input.Length);
            nuint size;
            fixed (byte* dst = output)
            fixed (byte* s = skip) {
                size = Z.ZSTD_writeSkippableFrame(dst, (nuint)dstSize, s, (nuint)skip.Length, magicVariant);
            }
            if (Native.IsError(size)) {
                throw RubyExceptions.CreateRuntimeError("write skippable frame failed: " + Native.ErrorName(size));
            }
            return Native.Binary(output, (int)size);
        }

        [RubyMethod("read_skippable_frame", RubyMethodAttributes.PrivateInstance)]
        [RubyMethod("read_skippable_frame", RubyMethodAttributes.PublicSingleton)]
        public static MutableString ReadSkippableFrame(RubyContext/*!*/ context, object self, object inputValue) {
            byte[] input = Args.StringValue(context, inputValue).ToByteArray();
            fixed (byte* src = input) {
                if (Z.ZSTD_isSkippableFrame(src, (nuint)input.Length) == 0) {
                    return null;
                }
                const int skipLen = 129 * 1024;
                var output = new byte[skipLen];
                uint readMagic;
                nuint size;
                fixed (byte* dst = output) {
                    size = Z.ZSTD_readSkippableFrame(dst, skipLen, &readMagic, src, (nuint)input.Length);
                }
                if (Native.IsError(size)) {
                    throw RubyExceptions.CreateRuntimeError("read skippable frame failed: " + Native.ErrorName(size));
                }
                return Native.Binary(output, (int)size);
            }
        }

        #endregion

        #region CDict, DDict

        [RubyClass("CDict", Inherits = typeof(object))]
        public sealed class CDict : RubyObject {
            internal ZSTD_CDict_s* Ptr;

            public CDict(RubyClass/*!*/ rubyClass) : base(rubyClass) {
            }

            protected override RubyObject/*!*/ CreateInstance() {
                return new CDict(ImmediateClass.NominalClass);
            }

            ~CDict() {
                if (Ptr != null) {
                    Z.ZSTD_freeCDict(Ptr);
                    Ptr = null;
                }
            }

            internal static RubyClass/*!*/ GetClass(RubyContext/*!*/ context) {
                return context.GetClass(typeof(CDict));
            }

            [RubyConstructor]
            public static CDict/*!*/ Create(RubyClass/*!*/ self, params object[]/*!*/ args) {
                var result = new CDict(self);
                Initialize(self.Context, result, args);
                return result;
            }

            [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
            public static object Initialize(RubyContext/*!*/ context, CDict/*!*/ self, params object[]/*!*/ args) {
                if (args.Length < 1 || args.Length > 2) {
                    throw RubyExceptions.CreateArgumentError("wrong number of arguments (given {0}, expected 1..2)", args.Length);
                }
                int level = ConvertLevel(context, args.Length > 1 ? args[1] : null);
                byte[] dict = Args.StringValue(context, args[0]).ToByteArray();
                ZSTD_CDict_s* cdict;
                fixed (byte* d = dict) {
                    cdict = Z.ZSTD_createCDict(d, (nuint)dict.Length, level);
                }
                if (cdict == null) {
                    throw RubyExceptions.CreateRuntimeError("ZSTD_createCDict failed");
                }
                // DATA_PTR(self) = cdict: a second #initialize leaks the first, as the C does
                self.Ptr = cdict;
                return self;
            }

            [RubyMethod("initialize_copy")]
            public static object InitializeCopy(CDict/*!*/ self, object other) {
                throw RubyExceptions.CreateRuntimeError("CDict cannot be duplicated");
            }
        }

        [RubyClass("DDict", Inherits = typeof(object))]
        public sealed class DDict : RubyObject {
            internal ZSTD_DDict_s* Ptr;

            public DDict(RubyClass/*!*/ rubyClass) : base(rubyClass) {
            }

            protected override RubyObject/*!*/ CreateInstance() {
                return new DDict(ImmediateClass.NominalClass);
            }

            ~DDict() {
                if (Ptr != null) {
                    Z.ZSTD_freeDDict(Ptr);
                    Ptr = null;
                }
            }

            internal static RubyClass/*!*/ GetClass(RubyContext/*!*/ context) {
                return context.GetClass(typeof(DDict));
            }

            [RubyConstructor]
            public static DDict/*!*/ Create(RubyClass/*!*/ self, object dict) {
                var result = new DDict(self);
                Initialize(self.Context, result, dict);
                return result;
            }

            [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
            public static object Initialize(RubyContext/*!*/ context, DDict/*!*/ self, object dictValue) {
                byte[] dict = Args.StringValue(context, dictValue).ToByteArray();
                ZSTD_DDict_s* ddict;
                fixed (byte* d = dict) {
                    ddict = Z.ZSTD_createDDict(d, (nuint)dict.Length);
                }
                if (ddict == null) {
                    throw RubyExceptions.CreateRuntimeError("ZSTD_createDDict failed");
                }
                self.Ptr = ddict;
                return self;
            }

            [RubyMethod("initialize_copy")]
            public static object InitializeCopy(DDict/*!*/ self, object other) {
                throw RubyExceptions.CreateRuntimeError("CDict cannot be duplicated");
            }
        }

        #endregion

        #region streaming_compress.c

        [RubyClass("StreamingCompress", Inherits = typeof(object))]
        public sealed class StreamingCompress : RubyObject {
            [RubyConstant("CONTINUE")]
            public const int Continue = (int)ZSTD_EndDirective.ZSTD_e_continue;
            [RubyConstant("FLUSH")]
            public const int Flush = (int)ZSTD_EndDirective.ZSTD_e_flush;
            [RubyConstant("END")]
            public const int End = (int)ZSTD_EndDirective.ZSTD_e_end;

            private CompressContext _ctx;
            private byte[] _buf;

            public StreamingCompress(RubyClass/*!*/ rubyClass) : base(rubyClass) {
            }

            protected override RubyObject/*!*/ CreateInstance() {
                return new StreamingCompress(ImmediateClass.NominalClass);
            }

            private CompressContext/*!*/ Context {
                get {
                    if (_ctx == null) {
                        throw RubyExceptions.CreateTypeError("uninitialized Zstd::StreamingCompress");
                    }
                    return _ctx;
                }
            }

            [RubyConstructor]
            public static StreamingCompress/*!*/ Create(RubyClass/*!*/ self, params object[]/*!*/ args) {
                var result = new StreamingCompress(self);
                Initialize(self.Context, result, args);
                return result;
            }

            [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
            public static object Initialize(RubyContext/*!*/ context, StreamingCompress/*!*/ self, params object[]/*!*/ args) {
                object[] pos;
                Hash kwargs = Args.Split(args, 0, 1, out pos);
                int outSize = (int)Z.ZSTD_CStreamOutSize();
                var ctx = new CompressContext();
                try {
                    SetCompressParams(context, ctx, pos[0], kwargs);
                } catch {
                    ctx.Dispose();
                    throw;
                }
                self._ctx = ctx;
                self._buf = new byte[outSize];
                return self;
            }

            [RubyMethod("compress")]
            public static MutableString/*!*/ Compress(RubyContext/*!*/ context, StreamingCompress/*!*/ self, object src) {
                byte[] input = Args.StringValue(context, src).ToByteArray();
                var ctx = self.Context;
                var result = new MemoryStream();
                byte[] buf = self._buf;
                fixed (byte* s = input)
                fixed (byte* d = buf) {
                    ZSTD_inBuffer_s inBuf = new ZSTD_inBuffer_s { src = s, size = (nuint)input.Length, pos = 0 };
                    while (inBuf.pos < inBuf.size) {
                        ZSTD_outBuffer_s outBuf = new ZSTD_outBuffer_s { dst = d, size = (nuint)buf.Length, pos = 0 };
                        nuint ret = Z.ZSTD_compressStream2(ctx.Ptr, &outBuf, &inBuf, ZSTD_EndDirective.ZSTD_e_continue);
                        if (Native.IsError(ret)) {
                            throw RubyExceptions.CreateRuntimeError("compress error error code: " + Native.ErrorName(ret));
                        }
                        Append(result, buf, (int)outBuf.pos);
                    }
                }
                // ctx.Ptr was the last use of ctx; without this the finalizer could free the
                // ZSTD_CCtx while ZSTD_compressStream2 is still using it.
                GC.KeepAlive(ctx);
                return MutableString.CreateBinary(result.ToArray(), RubyEncoding.Binary);
            }

            /// <summary>
            /// write(*strings): feeds the strings to the compressor and - as upstream does -
            /// drops whatever compressed output that produces, and answers each string's length
            /// once per compressor call it took.
            /// </summary>
            [RubyMethod("write")]
            public static object Write(RubyContext/*!*/ context, StreamingCompress/*!*/ self, params object[]/*!*/ args) {
                long total = 0;
                var ctx = self.Context;
                byte[] buf = self._buf;
                foreach (object arg in args) {
                    byte[] input = Args.StringValue(context, arg).ToByteArray();
                    fixed (byte* s = input)
                    fixed (byte* d = buf) {
                        ZSTD_inBuffer_s inBuf = new ZSTD_inBuffer_s { src = s, size = (nuint)input.Length, pos = 0 };
                        while (inBuf.pos < inBuf.size) {
                            ZSTD_outBuffer_s outBuf = new ZSTD_outBuffer_s { dst = d, size = (nuint)buf.Length, pos = 0 };
                            nuint ret = Z.ZSTD_compressStream2(ctx.Ptr, &outBuf, &inBuf, ZSTD_EndDirective.ZSTD_e_continue);
                            if (Native.IsError(ret)) {
                                throw RubyExceptions.CreateRuntimeError("compress error error code: " + Native.ErrorName(ret));
                            }
                            total += input.Length;
                        }
                    }
                }
                GC.KeepAlive(ctx);
                return Protocols.Normalize(total);
            }

            /// <summary>no_compress</summary>
            private MutableString/*!*/ NoCompress(ZSTD_EndDirective endOp) {
                var ctx = Context;
                var result = new MemoryStream();
                byte[] buf = _buf;
                fixed (byte* d = buf) {
                    ZSTD_inBuffer_s inBuf = new ZSTD_inBuffer_s { src = null, size = 0, pos = 0 };
                    nuint ret;
                    do {
                        ZSTD_outBuffer_s outBuf = new ZSTD_outBuffer_s { dst = d, size = (nuint)buf.Length, pos = 0 };
                        ret = Z.ZSTD_compressStream2(ctx.Ptr, &outBuf, &inBuf, endOp);
                        if (Native.IsError(ret)) {
                            throw RubyExceptions.CreateRuntimeError("flush error error code: " + Native.ErrorName(ret));
                        }
                        Append(result, buf, (int)outBuf.pos);
                    } while (ret > 0);
                }
                GC.KeepAlive(ctx);
                return MutableString.CreateBinary(result.ToArray(), RubyEncoding.Binary);
            }

            [RubyMethod("flush")]
            public static MutableString/*!*/ Flush_(StreamingCompress/*!*/ self) {
                return self.NoCompress(ZSTD_EndDirective.ZSTD_e_flush);
            }

            [RubyMethod("finish")]
            public static MutableString/*!*/ Finish(StreamingCompress/*!*/ self) {
                return self.NoCompress(ZSTD_EndDirective.ZSTD_e_end);
            }
        }

        private static void Append(MemoryStream/*!*/ result, byte[]/*!*/ buf, int count) {
            result.Write(buf, 0, count);
        }

        #endregion

        #region streaming_decompress.c

        [RubyClass("StreamingDecompress", Inherits = typeof(object))]
        public sealed class StreamingDecompress : RubyObject {
            private DecompressContext _dctx;
            private byte[] _buf;

            public StreamingDecompress(RubyClass/*!*/ rubyClass) : base(rubyClass) {
            }

            protected override RubyObject/*!*/ CreateInstance() {
                return new StreamingDecompress(ImmediateClass.NominalClass);
            }

            [RubyConstructor]
            public static StreamingDecompress/*!*/ Create(RubyClass/*!*/ self, params object[]/*!*/ args) {
                var result = new StreamingDecompress(self);
                Initialize(self.Context, result, args);
                return result;
            }

            [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
            public static object Initialize(RubyContext/*!*/ context, StreamingDecompress/*!*/ self, params object[]/*!*/ args) {
                object[] pos;
                Hash kwargs = Args.Split(args, 0, 0, out pos);
                int outSize = (int)Z.ZSTD_DStreamOutSize();
                var dctx = new DecompressContext();
                try {
                    SetDecompressParams(context, dctx, kwargs);
                } catch {
                    dctx.Dispose();
                    throw;
                }
                self._dctx = dctx;
                self._buf = new byte[outSize];
                return self;
            }

            [RubyMethod("decompress")]
            public static MutableString/*!*/ Decompress(RubyContext/*!*/ context, StreamingDecompress/*!*/ self, object src) {
                byte[] input = Args.StringValue(context, src).ToByteArray();
                if (self._dctx == null) {
                    throw RubyExceptions.CreateTypeError("uninitialized Zstd::StreamingDecompress");
                }
                var result = new MemoryStream();
                byte[] buf = self._buf;
                fixed (byte* s = input)
                fixed (byte* d = buf) {
                    ZSTD_inBuffer_s inBuf = new ZSTD_inBuffer_s { src = s, size = (nuint)input.Length, pos = 0 };
                    while (inBuf.pos < inBuf.size) {
                        ZSTD_outBuffer_s outBuf = new ZSTD_outBuffer_s { dst = d, size = (nuint)buf.Length, pos = 0 };
                        nuint ret = Z.ZSTD_decompressStream(self._dctx.Ptr, &outBuf, &inBuf);
                        if (Native.IsError(ret)) {
                            throw RubyExceptions.CreateRuntimeError("decompress error error code: " + Native.ErrorName(ret));
                        }
                        Append(result, buf, (int)outBuf.pos);
                    }
                }
                GC.KeepAlive(self._dctx);
                return MutableString.CreateBinary(result.ToArray(), RubyEncoding.Binary);
            }
        }

        #endregion
    }
}
