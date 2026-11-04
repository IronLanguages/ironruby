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
#if FEATURE_CRYPTOGRAPHY
using System;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using IronRuby.Builtins;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using Microsoft.Scripting.Generation;

namespace IronRuby.StandardLibrary.Digest {

    [RubyModule("Digest", BuildConfig = "FEATURE_CRYPTOGRAPHY")]
    public static class Digest {

        #region Module Methods

        [RubyMethod("const_missing", RubyMethodAttributes.PublicSingleton)]
        public static object ConstantMissing(RubyModule/*!*/ self, [DefaultProtocol, NotNull]string/*!*/ name) {
            // TODO:
            throw new NotImplementedException();
        }

        [RubyMethod("hexencode", RubyMethodAttributes.PublicSingleton)]
        public static MutableString/*!*/ HexEncode(RubyModule/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ str) {
            return Class.HexEncode(str);
        }

        #endregion

        // TODO: MRI doesn't define MD5 constant here, it implements const_missing
        [RubyClass("MD5")]
        public class MD5 : Base {
            public MD5()
                : base(System.Security.Cryptography.MD5.HashData, 16, 64) {
            }
        }

        [RubyClass("SHA1")]
        public class SHA1 : Base {
            public SHA1()
                : base(System.Security.Cryptography.SHA1.HashData, 20, 64) {
            }
        }

        [RubyClass("SHA256")]
        public class SHA256 : Base {
            public SHA256()
                : base(System.Security.Cryptography.SHA256.HashData, 32, 64) {
            }
        }

        [RubyClass("SHA384")]
        public class SHA384 : Base {
            public SHA384()
                : base(System.Security.Cryptography.SHA384.HashData, 48, 128) {
            }
        }

        [RubyClass("SHA512")]
        public class SHA512 : Base {
            public SHA512()
                : base(System.Security.Cryptography.SHA512.HashData, 64, 128) {
            }
        }

        [RubyClass("Base")]
        public class Base : Class {
            // The whole input is kept and hashed in one go by #finish, so the built-in digests
            // need no HashAlgorithm of their own: the static one-shot HashData does the work.
            // Creating a HashAlgorithm opens a native digest context (and a finalizable handle)
            // for every Digest object - most of the cost of Digest::SHA256.hexdigest(str) - and
            // ComputeHash reinitializes it afterwards. A subclass that brings an algorithm of its
            // own still uses it.
            private readonly HashAlgorithm _algorithm;
            private readonly Func<byte[], byte[]> _hashData;
            private readonly int _digestLength;
            private readonly int _blockLength;
            private MutableString/*!*/ _buffer;

            protected Base(HashAlgorithm/*!*/ algorithm, int blockLength) {
                Assert.NotNull(algorithm);
                _algorithm = algorithm;
                _digestLength = algorithm.HashSize / 8;
                _blockLength = blockLength;
                _buffer = MutableString.CreateBinary();
            }

            private protected Base(Func<byte[], byte[]>/*!*/ hashData, int digestLength, int blockLength) {
                Assert.NotNull(hashData);
                _hashData = hashData;
                _digestLength = digestLength;
                _blockLength = blockLength;
                _buffer = MutableString.CreateBinary();
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

            [RubyMethod("<<")]
            [RubyMethod("update")]
            public static Base/*!*/ Update(RubyContext/*!*/ context, Base/*!*/ self, MutableString str) {
                self._buffer.Append(str);
                return self;
            }

            /// <summary>
            /// The accumulated input lives in a private CLR field, which object copying
            /// does not carry across -- so without this, the clone that Digest::Instance#digest
            /// makes to snapshot the state started out empty and every no-argument
            /// digest/hexdigest returned the digest of "".
            /// </summary>
            [RubyMethod("initialize_copy", RubyMethodAttributes.PrivateInstance)]
            public static Base/*!*/ InitializeCopy(Base/*!*/ self, [NotNull]Base/*!*/ other) {
                self._buffer = other._buffer.Clone();
                return self;
            }

            [RubyMethod("finish", RubyMethodAttributes.PrivateInstance)]
            public static MutableString/*!*/ Finish(RubyContext/*!*/ context, Base/*!*/ self) {
                byte[] input = self._buffer.ConvertToBytes();
                byte[] hash = (self._hashData != null) ? self._hashData(input) : self._algorithm.ComputeHash(input);
                return MutableString.CreateBinary(hash);
            }

            [RubyMethod("reset")]
            public static Base/*!*/ Reset(RubyContext/*!*/ context, Base/*!*/ self) {
                self._buffer = MutableString.CreateBinary();
                if (self._algorithm != null) {
                    self._algorithm.Initialize();
                }
                return self;
            }
        }

        [RubyClass("Class"), Includes(typeof(Instance))]
        public class Class {

            [RubyMethod("digest", RubyMethodAttributes.PublicSingleton)]
            public static MutableString Digest(
                CallSiteStorage<Func<CallSite, RubyClass, object>>/*!*/ allocateStorage,
                CallSiteStorage<Func<CallSite, object, MutableString, object>>/*!*/ digestStorage,
                RubyClass/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ str) {

                // "new", not "allocate": MRI's Digest::Class.digest constructs the
                // object properly, and subclasses written in Ruby (Digest::SHA2 wraps
                // a SHA256/384/512 instance) do their real setup in #initialize.
                var allocateSite = allocateStorage.GetCallSite("new", 0);
                object obj = allocateSite.Target(allocateSite, self);

                // TODO: check obj
                var site = digestStorage.GetCallSite("digest", 1);
                return (MutableString)site.Target(site, obj, str);
            }

            [RubyMethod("digest", RubyMethodAttributes.PublicSingleton)]
            public static MutableString Digest(RubyClass/*!*/ self) {
                throw RubyExceptions.CreateArgumentError("no data given");
            }

            [RubyMethod("hexdigest", RubyMethodAttributes.PublicSingleton)]
            public static MutableString/*!*/ HexDigest(CallSiteStorage<Func<CallSite, object, MutableString, object>>/*!*/ storage, 
                RubyClass/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ str) {

                var site = storage.GetCallSite("digest", 1);
                MutableString result = (MutableString)site.Target(site, self, str);
                // TODO: check result != null
                return HexEncode(result);
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
            public static MutableString Digest(
                CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ initializeCopyStorage,
                CallSiteStorage<Func<CallSite, RubyClass, object>>/*!*/ allocateStorage,
                CallSiteStorage<Func<CallSite, object, object>>/*!*/ finishStorage,
                object self) {

                object clone;
                if (!RubyUtils.TryDuplicateObject(initializeCopyStorage, allocateStorage, self, true, out clone)) {
                    throw RubyExceptions.CreateArgumentError("unable to copy object");
                }

                var finish = finishStorage.GetCallSite("finish", 0);
                // TODO: cast?
                return (MutableString)finish.Target(finish, clone);
            }

            [RubyMethod("digest")]
            public static MutableString Digest(
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

                // TODO: cast?
                return (MutableString)value;
            }

            [RubyMethod("digest!")]
            public static MutableString DigestNew(
                CallSiteStorage<Func<CallSite, object, object>>/*!*/ finishStorage,
                CallSiteStorage<Func<CallSite, object, object>>/*!*/ resetStorage,
                object self) {

                var finish = finishStorage.GetCallSite("finish", 0);
                object value = finish.Target(finish, self);

                var reset = resetStorage.GetCallSite("reset", 0);
                reset.Target(reset, self);

                // TODO: cast?
                return (MutableString)value;
            }

            [RubyMethod("hexdigest")]
            public static MutableString HexDigest(
                CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ initializeCopyStorage,
                CallSiteStorage<Func<CallSite, RubyClass, object>>/*!*/ allocateStorage,
                CallSiteStorage<Func<CallSite, object, object>>/*!*/ finishStorage,
                object self) {

                return Class.HexEncode(Digest(initializeCopyStorage, allocateStorage, finishStorage, self));
            }

            [RubyMethod("hexdigest")]
            public static MutableString HexDigest(
                CallSiteStorage<Func<CallSite, object, MutableString, object>>/*!*/ updateStorage,
                CallSiteStorage<Func<CallSite, object, object>>/*!*/ finishStorage,
                CallSiteStorage<Func<CallSite, object, object>>/*!*/ resetStorage,
                object self, [DefaultProtocol, NotNull]MutableString/*!*/ str) {
                return Class.HexEncode(Digest(updateStorage, finishStorage, resetStorage, self, str));
            }

            [RubyMethod("hexdigest!")]
            public static MutableString HexDigestNew(
                CallSiteStorage<Func<CallSite, object, object>>/*!*/ finishStorage,
                CallSiteStorage<Func<CallSite, object, object>>/*!*/ resetStorage,
                object self) {
                return Class.HexEncode(DigestNew(finishStorage, resetStorage, self));
            }
        }
    }
}
#endif