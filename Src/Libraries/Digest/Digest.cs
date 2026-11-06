/* ****************************************************************************
 *
 * Copyright (c) Microsoft Corporation. 
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
 * copy of the license can be found in the License.html file at the root of this distribution. If 
 * you cannot locate the  Apache License, Version 2.0, please send an email to 
 * ironruby@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 *
 * ***************************************************************************/
using System;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using IronRuby.Builtins;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;

namespace IronRuby.StandardLibrary.Digest {

    [RubyModule("Digest")]
    public static class Digest {

        #region Module Methods

        [RubyMethod("hexencode", RubyMethodAttributes.PublicSingleton)]
        public static MutableString/*!*/ HexEncode(RubyModule/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ str) {
            return Class.HexEncode(str);
        }

        #endregion

        // MRI's digest.so leaves MD5, SHA1 and the SHA2 family to Digest.const_missing, which
        // requires digest/md5 and friends on first use. Here they are defined up front, and the
        // const_missing in Src/StdLib/ironruby/digest.rb only runs for the names that still live
        // in a file (RMD160, Bubble Babble) or do not exist.
        [RubyClass("MD5")]
        public class MD5 : Base {
            public MD5()
                : base(HashAlgorithmName.MD5, System.Security.Cryptography.MD5.HashData, 16, 64) {
            }
        }

        [RubyClass("SHA1")]
        public class SHA1 : Base {
            public SHA1()
                : base(HashAlgorithmName.SHA1, System.Security.Cryptography.SHA1.HashData, 20, 64) {
            }
        }

        [RubyClass("SHA256")]
        public class SHA256 : Base {
            public SHA256()
                : base(HashAlgorithmName.SHA256, System.Security.Cryptography.SHA256.HashData, 32, 64) {
            }
        }

        [RubyClass("SHA384")]
        public class SHA384 : Base {
            public SHA384()
                : base(HashAlgorithmName.SHA384, System.Security.Cryptography.SHA384.HashData, 48, 128) {
            }
        }

        [RubyClass("SHA512")]
        public class SHA512 : Base {
            public SHA512()
                : base(HashAlgorithmName.SHA512, System.Security.Cryptography.SHA512.HashData, 64, 128) {
            }
        }

        [RubyClass("Base")]
        public class Base : Class {
            // Input up to this many bytes is kept and hashed in one go by #finish, with the static
            // one-shot HashData: most digests are of a short string, and an IncrementalHash would
            // open a native digest context (and a finalizable handle) for each of them. Past it,
            // the input streams into an IncrementalHash, so #update is O(length of the update)
            // and a #digest after every update no longer rehashes everything fed in so far.
            private const int BufferLimit = 64 * 1024;

            // A subclass that brings a HashAlgorithm of its own keeps all of its input and hashes
            // it with ComputeHash, as before.
            private readonly HashAlgorithm _algorithm;
            private readonly HashAlgorithmName _algorithmName;
            private readonly Func<byte[], byte[]> _hashData;
            private readonly int _digestLength;
            private readonly int _blockLength;

            // the input not yet handed to _incremental (all of it while _incremental is null)
            private byte[]/*!*/ _buffer = Array.Empty<byte>();
            private int _count;

            private IncrementalHash _incremental;
#if !NET10_0_OR_GREATER
            // IncrementalHash.Clone is .NET 10's: before it, a copy (#dup, #clone) of a digest
            // that has gone incremental replays the input into a new one. The input is kept in
            // the chunks it came in, so nothing is copied as it arrives.
            private System.Collections.Generic.List<byte[]> _streamed;
#endif

            protected Base(HashAlgorithm/*!*/ algorithm, int blockLength) {
                Assert.NotNull(algorithm);
                _algorithm = algorithm;
                _digestLength = algorithm.HashSize / 8;
                _blockLength = blockLength;
            }

            private protected Base(HashAlgorithmName algorithmName, Func<byte[], byte[]>/*!*/ hashData, int digestLength, int blockLength) {
                Assert.NotNull(hashData);
                _algorithmName = algorithmName;
                _hashData = hashData;
                _digestLength = digestLength;
                _blockLength = blockLength;
            }

            /// <summary>
            /// The hash function's internal block size in bytes. .NET's HashAlgorithm
            /// exposes the output size (HashSize) but not this, so each subclass passes
            /// it in: 64 for MD5/SHA-1/SHA-256, 128 for the 64-bit SHA-2 variants.
            /// </summary>
            [RubyMethod("block_length")]
            public static int BlockLength(Base/*!*/ self) {
                return self._blockLength;
            }

            [RubyMethod("digest_length")]
            public static int DigestLength(Base/*!*/ self) {
                return self._digestLength;
            }

            // The input is bytes: a UTF-8 string and a binary one can be fed to the same digest,
            // which raised Encoding::CompatibilityError while the input was kept in a String.
            [RubyMethod("<<")]
            [RubyMethod("update")]
            public static Base/*!*/ Update(RubyContext/*!*/ context, Base/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ str) {
                self.Append(str.ToByteArray());
                return self;
            }

            private void Append(byte[]/*!*/ data) {
                if (_incremental != null) {
                    _incremental.AppendData(data);
#if !NET10_0_OR_GREATER
                    _streamed.Add(data);
#endif
                    return;
                }

                if (_hashData != null && _count + data.Length > BufferLimit) {
                    _incremental = IncrementalHash.CreateHash(_algorithmName);
                    _incremental.AppendData(_buffer, 0, _count);
                    _incremental.AppendData(data);
#if !NET10_0_OR_GREATER
                    _streamed = new System.Collections.Generic.List<byte[]>();
                    _streamed.Add(_buffer.AsSpan(0, _count).ToArray());
                    _streamed.Add(data);
#endif
                    _buffer = Array.Empty<byte>();
                    _count = 0;
                    return;
                }

                if (_count + data.Length > _buffer.Length) {
                    Array.Resize(ref _buffer, Math.Max(_count + data.Length, Math.Max(64, _buffer.Length * 2)));
                }
                Buffer.BlockCopy(data, 0, _buffer, _count, data.Length);
                _count += data.Length;
            }

            /// <summary>
            /// The digest of everything fed in so far; the state is left as it is.
            /// </summary>
            internal byte[]/*!*/ GetCurrentHash() {
                if (_incremental != null) {
                    return _incremental.GetCurrentHash();
                }
                byte[] input = (_count == _buffer.Length) ? _buffer : _buffer.AsSpan(0, _count).ToArray();
                return (_hashData != null) ? _hashData(input) : _algorithm.ComputeHash(input);
            }

            /// <summary>
            /// The accumulated input lives in private CLR fields, which object copying
            /// does not carry across -- so without this, the clone that Digest::Instance#digest
            /// makes to snapshot the state started out empty and every no-argument
            /// digest/hexdigest returned the digest of "".
            /// </summary>
            [RubyMethod("initialize_copy", RubyMethodAttributes.PrivateInstance)]
            public static Base/*!*/ InitializeCopy(Base/*!*/ self, [NotNull]Base/*!*/ other) {
                self.Clear();
                self._buffer = other._buffer.AsSpan(0, other._count).ToArray();
                self._count = other._count;
                if (other._incremental != null) {
#if NET10_0_OR_GREATER
                    self._incremental = other._incremental.Clone();
#else
                    self._incremental = IncrementalHash.CreateHash(other._algorithmName);
                    self._streamed = new System.Collections.Generic.List<byte[]>(other._streamed);
                    foreach (var chunk in other._streamed) {
                        self._incremental.AppendData(chunk);
                    }
#endif
                }
                return self;
            }

            [RubyMethod("finish", RubyMethodAttributes.PrivateInstance)]
            public static MutableString/*!*/ Finish(RubyContext/*!*/ context, Base/*!*/ self) {
                return MutableString.CreateBinary(self.GetCurrentHash());
            }

            [RubyMethod("reset")]
            public static Base/*!*/ Reset(RubyContext/*!*/ context, Base/*!*/ self) {
                self.Clear();
                if (self._algorithm != null) {
                    self._algorithm.Initialize();
                }
                return self;
            }

            private void Clear() {
                _buffer = Array.Empty<byte>();
                _count = 0;
                if (_incremental != null) {
                    _incremental.Dispose();
                    _incremental = null;
                }
#if !NET10_0_OR_GREATER
                _streamed = null;
#endif
            }

            /// <summary>
            /// True when <paramref name="obj"/> is a built-in digest whose #finish and
            /// #initialize_copy are these: Digest::Instance#digest can then read the current digest
            /// straight off it rather than finishing a copy, which copied all of the input every
            /// time (and, once streaming, would replay it).
            /// </summary>
            internal static bool CanSnapshot(RubyContext/*!*/ context, object obj) {
                var digest = obj as Base;
                if (digest == null) {
                    return false;
                }
                RubyClass cls = context.GetImmediateClassOf(obj);
                return IsBaseMethod(cls, "finish") && IsBaseMethod(cls, "initialize_copy");
            }

            private static bool IsBaseMethod(RubyClass/*!*/ cls, string/*!*/ name) {
                var resolved = cls.ResolveMethod(name, IronRuby.Runtime.Calls.VisibilityContext.AllVisible);
                var info = resolved.Found ? resolved.Info as IronRuby.Runtime.Calls.RubyLibraryMethodInfo : null;
                if (info == null) {
                    return false;
                }
                foreach (var member in info.GetMembers()) {
                    if (member.DeclaringType != typeof(Base)) {
                        return false;
                    }
                }
                return true;
            }
        }

        [RubyClass("Class"), Includes(typeof(Instance))]
        public class Class {

            [RubyMethod("digest", RubyMethodAttributes.PublicSingleton)]
            public static object Digest(
                CallSiteStorage<Func<CallSite, RubyClass, object>>/*!*/ allocateStorage,
                CallSiteStorage<Func<CallSite, object, MutableString, object>>/*!*/ digestStorage,
                RubyClass/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ str) {

                // "new", not "allocate": MRI's Digest::Class.digest constructs the
                // object properly, and subclasses written in Ruby (Digest::SHA2 wraps
                // a SHA256/384/512 instance) do their real setup in #initialize.
                var allocateSite = allocateStorage.GetCallSite("new", 0);
                object obj = allocateSite.Target(allocateSite, self);

                // Whatever #digest answers, as MRI's rb_digest_class_s_digest returns it unchecked:
                // a subclass whose #finish is not a String still has its result handed back.
                var site = digestStorage.GetCallSite("digest", 1);
                return site.Target(site, obj, str);
            }

            [RubyMethod("digest", RubyMethodAttributes.PublicSingleton)]
            public static MutableString Digest(RubyClass/*!*/ self) {
                throw RubyExceptions.CreateArgumentError("no data given");
            }

            [RubyMethod("hexdigest", RubyMethodAttributes.PublicSingleton)]
            public static MutableString/*!*/ HexDigest(CallSiteStorage<Func<CallSite, object, MutableString, object>>/*!*/ storage,
                ConversionStorage<MutableString>/*!*/ stringCast,
                RubyClass/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ str) {

                var site = storage.GetCallSite("digest", 1);
                // MRI's hexencode_str_new runs StringValue on the digest, so a #digest that answers
                // nil or a Symbol is a TypeError ("no implicit conversion of ... into String"), not
                // a cast failure.
                return HexEncode(Protocols.CastToString(stringCast, site.Target(site, self, str)));
            }

            [RubyMethod("hexdigest", RubyMethodAttributes.PublicSingleton)]
            public static MutableString HexDigest(RubyClass/*!*/ self) {
                throw RubyExceptions.CreateArgumentError("no data given");
            }

            #region Helpers

            internal static MutableString/*!*/ Bytes2Hex(byte[]/*!*/ bytes) {
                const string digits = "0123456789abcdef";
                var chars = new char[bytes.Length * 2];
                for (int i = 0; i < bytes.Length; i++) {
                    chars[2 * i] = digits[bytes[i] >> 4];
                    chars[2 * i + 1] = digits[bytes[i] & 0xf];
                }
                return MutableString.CreateAscii(new String(chars));
            }

            internal static MutableString/*!*/ HexEncode(MutableString/*!*/ str) {
                return Bytes2Hex(str.ConvertToBytes());
            }

            #endregion
        }

        [RubyModule("Instance")]
        public class Instance {

            [RubyMethod("digest")]
            public static object Digest(
                CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ initializeCopyStorage,
                CallSiteStorage<Func<CallSite, RubyClass, object>>/*!*/ allocateStorage,
                CallSiteStorage<Func<CallSite, object, object>>/*!*/ finishStorage,
                object self) {

                if (Base.CanSnapshot(finishStorage.Context, self)) {
                    return MutableString.CreateBinary(((Base)self).GetCurrentHash());
                }

                object clone;
                if (!RubyUtils.TryDuplicateObject(initializeCopyStorage, allocateStorage, self, true, out clone)) {
                    throw RubyExceptions.CreateArgumentError("unable to copy object");
                }

                var finish = finishStorage.GetCallSite("finish", 0);
                // #finish's result as it is: MRI's rb_digest_instance_digest does not check it.
                return finish.Target(finish, clone);
            }

            [RubyMethod("digest")]
            public static object Digest(
                CallSiteStorage<Func<CallSite, object, MutableString, object>>/*!*/ updateStorage,
                CallSiteStorage<Func<CallSite, object, object>>/*!*/ finishStorage,
                CallSiteStorage<Func<CallSite, object, object>>/*!*/ resetStorage,
                object self, [DefaultProtocol, NotNull]MutableString/*!*/ str) {

                // MRI resets before updating as well as after: digest(str) is the digest
                // of str alone, not of whatever had already been fed in plus str.
                var reset = resetStorage.GetCallSite("reset", 0);
                reset.Target(reset, self);

                var update = updateStorage.GetCallSite("update", 1);
                update.Target(update, self, str);

                var finish = finishStorage.GetCallSite("finish", 0);
                object value = finish.Target(finish, self);

                reset.Target(reset, self);

                return value;
            }

            [RubyMethod("digest!")]
            public static object DigestNew(
                CallSiteStorage<Func<CallSite, object, object>>/*!*/ finishStorage,
                CallSiteStorage<Func<CallSite, object, object>>/*!*/ resetStorage,
                object self) {

                var finish = finishStorage.GetCallSite("finish", 0);
                object value = finish.Target(finish, self);

                var reset = resetStorage.GetCallSite("reset", 0);
                reset.Target(reset, self);

                return value;
            }

            [RubyMethod("hexdigest")]
            public static MutableString HexDigest(
                ConversionStorage<MutableString>/*!*/ stringCast,
                CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ initializeCopyStorage,
                CallSiteStorage<Func<CallSite, RubyClass, object>>/*!*/ allocateStorage,
                CallSiteStorage<Func<CallSite, object, object>>/*!*/ finishStorage,
                object self) {

                return Class.HexEncode(Protocols.CastToString(stringCast, Digest(initializeCopyStorage, allocateStorage, finishStorage, self)));
            }

            [RubyMethod("hexdigest")]
            public static MutableString HexDigest(
                ConversionStorage<MutableString>/*!*/ stringCast,
                CallSiteStorage<Func<CallSite, object, MutableString, object>>/*!*/ updateStorage,
                CallSiteStorage<Func<CallSite, object, object>>/*!*/ finishStorage,
                CallSiteStorage<Func<CallSite, object, object>>/*!*/ resetStorage,
                object self, [DefaultProtocol, NotNull]MutableString/*!*/ str) {
                return Class.HexEncode(Protocols.CastToString(stringCast, Digest(updateStorage, finishStorage, resetStorage, self, str)));
            }

            [RubyMethod("hexdigest!")]
            public static MutableString HexDigestNew(
                ConversionStorage<MutableString>/*!*/ stringCast,
                CallSiteStorage<Func<CallSite, object, object>>/*!*/ finishStorage,
                CallSiteStorage<Func<CallSite, object, object>>/*!*/ resetStorage,
                object self) {
                return Class.HexEncode(Protocols.CastToString(stringCast, DigestNew(finishStorage, resetStorage, self)));
            }
        }
    }
}
