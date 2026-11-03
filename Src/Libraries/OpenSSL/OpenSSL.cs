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
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using IronRuby.StandardLibrary.Sockets;
using System.Text;
using IronRuby.Builtins;
using IronRuby.Runtime;
using System.Numerics;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using Crypto = System.Security.Cryptography;

namespace IronRuby.StandardLibrary.OpenSsl {

    [RubyModule("OpenSSL", BuildConfig = "FEATURE_CRYPTOGRAPHY")]
    public static class OpenSsl {
        // TODO: constants
        // Config,HMACError,PKCS12,Random,OPENSSL_VERSION,PKCS7,BN,ConfigError,PKey,Engine,BNError,Netscape,OCSP
        // OpenSSLError,CipherError,SSL,VERSION,X509,ASN1,OPENSSL_VERSION_NUMBER,Cipher

        // The algorithms below come from System.Security.Cryptography, not from
        // libcrypto, but ruby/spec (and real code) gates features on these two
        // constants, so they report the feature level actually implemented.
        [RubyConstant]
        public const string OPENSSL_VERSION = "OpenSSL 3.0.0 (IronRuby, System.Security.Cryptography)";

        [RubyConstant]
        public const int OPENSSL_VERSION_NUMBER = 0x30000000;

        [RubyConstant]
        public const string VERSION = "3.2.0";

        /// <summary>
        /// OpenSSL::Digest wraps one of the message digests OpenSSL's EVP layer exposes.
        /// The .NET equivalent is IncrementalHash, which supports the same
        /// update/finish/reset lifecycle.
        /// </summary>
        [RubyClass("Digest")]
        public class Digest {
            // MRI's class for "this is not a digest OpenSSL knows"; code rescues it
            // by name, so it has to be this class and not its OpenSSLError parent.
            [RubyException("DigestError")]
            public class DigestError : OpenSSLError {
                public DigestError() : this(null, null) { }
                public DigestError(string message) : this(message, null) { }
                public DigestError(string message, Exception inner) : base(message ?? "DigestError", inner) { }
                public DigestError(MutableString message) : base(message.ConvertToString()) { RubyExceptionData.InitializeException(this, message); }
            }

            private string/*!*/ _name = "SHA1";
            private IncrementalDigest _hash;

            public Digest() {
                _hash = CreateIncremental("SHA1");
            }

            internal string/*!*/ AlgorithmName {
                get { return _name; }
            }

            /// <summary>
            /// OpenSSL accepts any of its own spellings of a digest name ("sha1", "SHA-1", ...)
            /// and reports back the canonical one.
            /// </summary>
            internal static string CanonicalizeName(string/*!*/ name) {
                switch (name.ToUpperInvariant().Replace("-", "")) {
                    case "MD5": return "MD5";
                    case "SHA":
                    case "SHA1": return "SHA1";
                    case "SHA224": return "SHA224";
                    case "SHA256": return "SHA256";
                    case "SHA384": return "SHA384";
                    case "SHA512": return "SHA512";
                    // SHA-3 arrived in .NET 8; it is only there when the platform's
                    // crypto backend has it (OpenSSL 3 does, Windows CNG before 25H2
                    // does not), so an unsupported build reports the name as unknown.
                    // .NET has no SHA3-224: it only bound the three sizes that pair
                    // with its signature and HMAC algorithms.
                    case "SHA3256": return Crypto.SHA3_256.IsSupported ? "SHA3-256" : null;
                    case "SHA3384": return Crypto.SHA3_256.IsSupported ? "SHA3-384" : null;
                    case "SHA3512": return Crypto.SHA3_256.IsSupported ? "SHA3-512" : null;
                    default: return null;
                }
            }

            internal static Crypto.HashAlgorithmName ToHashAlgorithmName(string/*!*/ canonicalName) {
                switch (canonicalName) {
                    case "MD5": return Crypto.HashAlgorithmName.MD5;
                    case "SHA1": return Crypto.HashAlgorithmName.SHA1;
                    case "SHA256": return Crypto.HashAlgorithmName.SHA256;
                    case "SHA384": return Crypto.HashAlgorithmName.SHA384;
                    case "SHA3-256": return Crypto.HashAlgorithmName.SHA3_256;
                    case "SHA3-384": return Crypto.HashAlgorithmName.SHA3_384;
                    case "SHA3-512": return Crypto.HashAlgorithmName.SHA3_512;
                    default: return Crypto.HashAlgorithmName.SHA512;
                }
            }

            internal static int DigestLengthOf(string/*!*/ canonicalName) {
                switch (canonicalName) {
                    case "MD5": return 16;
                    case "SHA1": return 20;
                    case "SHA224": return 28;
                    case "SHA256": return 32;
                    case "SHA384": return 48;
                    case "SHA3-256": return 32;
                    case "SHA3-384": return 48;
                    default: return 64;
                }
            }

            internal static int BlockLengthOf(string/*!*/ canonicalName) {
                switch (canonicalName) {
                    case "SHA384":
                    case "SHA512": return 128;
                    // Keccak's rate: 1600 bits of state less twice the capacity.
                    case "SHA3-256": return 136;
                    case "SHA3-384": return 104;
                    case "SHA3-512": return 72;
                    default: return 64;
                }
            }

            /// <summary>
            /// A fresh running digest for the algorithm.  .NET has no SHA-224 and no way
            /// to hand SHA-256 a different initial state, which is all SHA-224 is, so
            /// that one is computed here (Sha224.cs) instead of mapped onto .NET.
            /// </summary>
            internal static IncrementalDigest/*!*/ CreateIncremental(string/*!*/ canonicalName) {
                if (canonicalName == "SHA224") {
                    return new Sha224();
                }
                return new NetDigest(Crypto.IncrementalHash.CreateHash(ToHashAlgorithmName(canonicalName)));
            }

            private static string/*!*/ ResolveName(RubyContext/*!*/ context, object algorithm) {
                var str = algorithm as MutableString;
                if (str != null) {
                    string canonical = CanonicalizeName(str.ConvertToString());
                    if (canonical == null) {
                        throw new DigestError(MutableString.CreateMutable(
                            "Unsupported digest algorithm (" + str.ConvertToString() + ").", RubyEncoding.UTF8
                        ));
                    }
                    return canonical;
                }

                var digest = algorithm as Digest;
                if (digest != null) {
                    // the state of the argument is deliberately not copied, matching OpenSSL
                    return digest._name;
                }

                throw RubyExceptions.CreateTypeConversionError(context.GetClassDisplayName(algorithm), "String");
            }

            [RubyConstructor]
            public static Digest/*!*/ CreateDigest(RubyContext/*!*/ context, RubyClass/*!*/ self, object algorithm,
                [DefaultProtocol, Optional]MutableString data) {
                return Initialize(context, new Digest(), algorithm, data);
            }

            // Reinitialization. Not called when a factory/non-default ctor is called.
            [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
            public static Digest/*!*/ Initialize(RubyContext/*!*/ context, Digest/*!*/ self, object algorithm,
                [DefaultProtocol, Optional]MutableString data) {
                self._name = ResolveName(context, algorithm);
                self._hash = CreateIncremental(self._name);

                if (data != null) {
                    Update(self, data);
                }
                return self;
            }

            [RubyMethod("reset")]
            public static Digest/*!*/ Reset(Digest/*!*/ self) {
                // IncrementalHash resets itself when the current hash is retrieved
                self._hash.Reset();
                return self;
            }

            [RubyMethod("update")]
            [RubyMethod("<<")]
            public static Digest/*!*/ Update(Digest/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ data) {
                self._hash.Append(data.ConvertToBytes());
                return self;
            }

            [RubyMethod("name")]
            public static MutableString/*!*/ Name(Digest/*!*/ self) {
                return MutableString.CreateAscii(self._name);
            }

            [RubyMethod("digest_length")]
            [RubyMethod("digest_size")]
            public static int DigestLength(Digest/*!*/ self) {
                return DigestLengthOf(self._name);
            }

            [RubyMethod("block_length")]
            public static int BlockLength(Digest/*!*/ self) {
                return BlockLengthOf(self._name);
            }

            /// <summary>
            /// Finishes the running digest without losing it: IncrementalHash can only
            /// finish-and-reset, so the accumulated bytes are replayed afterwards.
            /// </summary>
            internal static byte[]/*!*/ Finish(Digest/*!*/ self) {
                return self._hash.Peek();
            }

            [RubyMethod("digest")]
            public static MutableString/*!*/ GetDigest(Digest/*!*/ self) {
                return MutableString.CreateBinary(Finish(self));
            }

            [RubyMethod("digest")]
            public static MutableString/*!*/ GetDigest(RubyContext/*!*/ context, Digest/*!*/ self, [NotNull]MutableString/*!*/ data) {
                Reset(self);
                Update(self, data);
                var result = MutableString.CreateBinary(Finish(self));
                Reset(self);
                return result;
            }

            [RubyMethod("hexdigest")]
            public static MutableString/*!*/ HexDigest(Digest/*!*/ self) {
                return MutableString.CreateAscii(ToHex(Finish(self)));
            }

            [RubyMethod("hexdigest")]
            public static MutableString/*!*/ HexDigest(RubyContext/*!*/ context, Digest/*!*/ self, [NotNull]MutableString/*!*/ data) {
                return MutableString.CreateAscii(ToHex(GetDigest(context, self, data).ConvertToBytes()));
            }

            [RubyMethod("base64digest")]
            public static MutableString/*!*/ Base64Digest(Digest/*!*/ self) {
                return MutableString.CreateAscii(Convert.ToBase64String(Finish(self)));
            }

            [RubyMethod("base64digest")]
            public static MutableString/*!*/ Base64Digest(RubyContext/*!*/ context, Digest/*!*/ self, [NotNull]MutableString/*!*/ data) {
                return MutableString.CreateAscii(Convert.ToBase64String(GetDigest(context, self, data).ConvertToBytes()));
            }

            [RubyMethod("==")]
            public static bool Equal(RubyContext/*!*/ context, Digest/*!*/ self, [NotNull]Digest/*!*/ other) {
                return self._name == other._name && ToHex(Finish(self)) == ToHex(Finish(other));
            }

            internal static byte[]/*!*/ ComputeHash(string/*!*/ canonicalName, byte[]/*!*/ data) {
                using (var hash = CreateIncremental(canonicalName)) {
                    hash.Append(data);
                    return hash.Peek();
                }
            }

            internal static string/*!*/ ToHex(byte[]/*!*/ bytes) {
                var sb = new StringBuilder(bytes.Length * 2);
                for (int i = 0; i < bytes.Length; i++) {
                    sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
                }
                return sb.ToString();
            }

            #region singleton digest/hexdigest/base64digest

            [RubyMethod("digest", RubyMethodAttributes.PublicSingleton)]
            public static MutableString/*!*/ Digest_(RubyContext/*!*/ context, RubyClass/*!*/ self,
                object algorithm, [DefaultProtocol, NotNull]MutableString/*!*/ data) {

                return MutableString.CreateBinary(ComputeHash(ResolveName(context, algorithm), data.ConvertToBytes()));
            }

            [RubyMethod("hexdigest", RubyMethodAttributes.PublicSingleton)]
            public static MutableString/*!*/ HexDigest_(RubyContext/*!*/ context, RubyClass/*!*/ self,
                object algorithm, [DefaultProtocol, NotNull]MutableString/*!*/ data) {

                return MutableString.CreateAscii(ToHex(ComputeHash(ResolveName(context, algorithm), data.ConvertToBytes())));
            }

            [RubyMethod("base64digest", RubyMethodAttributes.PublicSingleton)]
            public static MutableString/*!*/ Base64Digest_(RubyContext/*!*/ context, RubyClass/*!*/ self,
                object algorithm, [DefaultProtocol, NotNull]MutableString/*!*/ data) {

                return MutableString.CreateAscii(Convert.ToBase64String(ComputeHash(ResolveName(context, algorithm), data.ConvertToBytes())));
            }

            #endregion
        }

        [RubyClass("HMAC")]
        public class HMAC {

            internal static byte[]/*!*/ Compute(Digest/*!*/ digest, MutableString/*!*/ key, MutableString/*!*/ data) {
                byte[] keyBytes = key.ConvertToBytes();
                using (var hmac = Crypto.IncrementalHash.CreateHMAC(Digest.ToHashAlgorithmName(digest.AlgorithmName), keyBytes)) {
                    hmac.AppendData(data.ConvertToBytes());
                    return hmac.GetHashAndReset();
                }
            }

            [RubyMethod("hexdigest", RubyMethodAttributes.PublicSingleton)]
            public static MutableString/*!*/ HexDigest(RubyClass/*!*/ self,
                [NotNull]Digest/*!*/ digest,
                [DefaultProtocol, NotNull]MutableString/*!*/ key,
                [DefaultProtocol, NotNull]MutableString/*!*/ data) {

                return MutableString.CreateAscii(Digest.ToHex(Compute(digest, key, data)));
            }

            [RubyMethod("digest", RubyMethodAttributes.PublicSingleton)]
            public static MutableString/*!*/ Digest_(RubyClass/*!*/ self,
                [NotNull]Digest/*!*/ digest,
                [DefaultProtocol, NotNull]MutableString/*!*/ key,
                [DefaultProtocol, NotNull]MutableString/*!*/ data) {

                return MutableString.CreateBinary(Compute(digest, key, data));
            }

            // HMAC.new(key, digest) -> hmac
            // update(string) -> self
            // digest -> aString
            // hexdigest -> aString
            // reset -> self
        }

        /// <summary>
        /// OpenSSL::KDF. The keyword handling and argument coercion live in the Ruby
        /// half (Src/StdLib/ironruby/openssl.rb); these are the raw primitives.
        /// </summary>
        [RubyModule("KDF")]
        public static class KDF {

            [RubyMethod("__pbkdf2_hmac__", RubyMethodAttributes.PublicSingleton)]
            public static MutableString/*!*/ Pbkdf2Hmac(RubyModule/*!*/ self,
                [DefaultProtocol, NotNull]MutableString/*!*/ pass,
                [DefaultProtocol, NotNull]MutableString/*!*/ salt,
                [DefaultProtocol]int iterations,
                [DefaultProtocol]int length,
                [DefaultProtocol, NotNull]MutableString/*!*/ hash) {

                // MRI allocates the result string before anything else is checked
                if (length < 0) {
                    throw RubyExceptions.CreateArgumentError("negative string size (or size too big)");
                }

                string canonical = Digest.CanonicalizeName(hash.ConvertToString());
                if (canonical == null) {
                    throw new OpenSSLError(MutableString.CreateMutable(
                        "Unsupported digest algorithm (" + hash.ConvertToString() + ").", RubyEncoding.UTF8
                    ));
                }
                if (length == 0) {
                    return MutableString.CreateBinary(new byte[0]);
                }
                if (iterations <= 0) {
                    throw new KDFError(MutableString.CreateAscii("PKCS5_PBKDF2_HMAC: invalid iteration count"));
                }

                byte[] derived = Crypto.Rfc2898DeriveBytes.Pbkdf2(
                    pass.ConvertToBytes(), salt.ConvertToBytes(), iterations, Digest.ToHashAlgorithmName(canonical), length
                );
                return MutableString.CreateBinary(derived);
            }

            [RubyMethod("__scrypt__", RubyMethodAttributes.PublicSingleton)]
            public static MutableString/*!*/ Scrypt(RubyModule/*!*/ self,
                [DefaultProtocol, NotNull]MutableString/*!*/ pass,
                [DefaultProtocol, NotNull]MutableString/*!*/ salt,
                [DefaultProtocol]int N,
                [DefaultProtocol]int r,
                [DefaultProtocol]int p,
                [DefaultProtocol]int length) {

                // MRI allocates the result string before anything else is checked
                if (length < 0) {
                    throw RubyExceptions.CreateArgumentError("negative string size (or size too big)");
                }
                if (length == 0) {
                    return MutableString.CreateBinary(new byte[0]);
                }
                if (N < 2 || (N & (N - 1)) != 0) {
                    throw new KDFError(MutableString.CreateAscii("EVP_PBE_scrypt: Invalid N parameter"));
                }
                if (r < 1 || p < 1) {
                    throw new KDFError(MutableString.CreateAscii("EVP_PBE_scrypt: Invalid parameters"));
                }
                return MutableString.CreateBinary(
                    ScryptImpl.DeriveKey(pass.ConvertToBytes(), salt.ConvertToBytes(), N, r, p, length)
                );
            }
        }

        [RubyException("KDFError")]
        public class KDFError : OpenSSLError {
            public KDFError() : this(null, null) { }
            public KDFError(string message) : this(message, null) { }
            public KDFError(string message, Exception inner) : base(message ?? "KDFError", inner) { }
            public KDFError(MutableString message) : base(message.ConvertToString()) { RubyExceptionData.InitializeException(this, message); }
        }

        #region secure_compare

        [RubyMethod("fixed_length_secure_compare", RubyMethodAttributes.PublicSingleton)]
        public static bool FixedLengthSecureCompare(RubyModule/*!*/ self,
            [DefaultProtocol, NotNull]MutableString/*!*/ a, [DefaultProtocol, NotNull]MutableString/*!*/ b) {

            byte[] x = a.ConvertToBytes();
            byte[] y = b.ConvertToBytes();
            if (x.Length != y.Length) {
                throw RubyExceptions.CreateArgumentError("inputs must be of equal length");
            }
            return Crypto.CryptographicOperations.FixedTimeEquals(x, y);
        }

        #endregion

        [RubyModule("Random")]
        public static class RandomModule {

            // This is a no-op method since our random number generator uses the .NET crypto random number generator
            // that gets its seed values from the OS

            [RubyMethod("seed", RubyMethodAttributes.PublicSingleton)]
            public static MutableString/*!*/ Seed(RubyModule/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ seed) {
                return seed;
            }

            [RubyMethod("pseudo_bytes", RubyMethodAttributes.PublicSingleton)]
            [RubyMethod("random_bytes", RubyMethodAttributes.PublicSingleton)]
            public static MutableString/*!*/ RandomBytes(RubyModule/*!*/ self, [DefaultProtocol]int length) {
                if (length < 0) {
                    throw RubyExceptions.CreateArgumentError("negative string size");
                }

                if (length == 0) {
                    return MutableString.CreateEmpty();
                }

                byte[] data = new byte[length];
                Crypto.RandomNumberGenerator.Fill(data);

                return MutableString.CreateBinary(data);
            }

            // add(str, entropy) -> self
            // load_random_file(filename) -> true
        }

        [RubyClass("BN")]
        public class BN {

            // new => aBN
            // new(bn) => aBN
            // new(string) => aBN
            // new(string, 0 | 2 | 10 | 16) => aBN

            [RubyMethod("rand", RubyMethodAttributes.PublicSingleton)]
            public static BigInteger/*!*/ Rand(RubyClass/*!*/ self, [DefaultProtocol]int bits, [DefaultProtocol, Optional]int someFlag, [Optional]bool otherFlag) { // TODO: figure out someFlag and otherFlag
                byte[] data = new byte[bits >> 3];
                Crypto.RandomNumberGenerator.Fill(data);

                uint[] transformed = new uint[data.Length >> 2];
                int j = 0;
                for (int i = 0; i < transformed.Length; ++i) {
                    transformed[i] = data[j] + (uint)(data[j + 1] << 8) + (uint)(data[j + 2] << 16) + (uint)(data[j + 3] << 24);
                    j += 4;
                }

                return BigIntegerCompat.Create(1, transformed);
            }
        }

        [RubyModule("X509")]
        public static class X509 {

            [RubyClass("CertificateError", Extends = typeof(CryptographicException), Inherits = typeof(ExternalException))]
            public class CryptographicExceptionOps {
                [RubyConstructor]
                public static CryptographicException/*!*/ Create(RubyClass/*!*/ self, [DefaultProtocol, DefaultParameterValue(null)]MutableString message) {
                    CryptographicException result = new CryptographicException(RubyExceptions.MakeMessage(ref message, "Not enought data."));
                    RubyExceptionData.InitializeException(result, message);
                    return result;
                }
            }

            // TODO: Constants

            // OpenSSL::X509::Certificate, ExtensionFactory and Store are written in Ruby
            // (Src/StdLib/ironruby/openssl.rb): building and verifying a certificate is
            // System.Security.Cryptography.X509Certificates work, and driving CertificateRequest
            // and X509Chain from Ruby keeps the whole of X509 in one place.

            [RubyClass("Name")]
            public class Name {
                // new => name
                // new(string) => name
                // new(dn) => name
                // new(dn, template) => name
                // add_entry(oid, value [, type]) => self
                // to_s => string
                // to_s(integer) => string
                // to_a => [[name, data, type], ...]
                // hash => integer
                // to_der => string
                // parse(string) => name
            }
        }

        // OpenSSL::PKey is written in Ruby (Src/StdLib/ironruby/openssl/pkey.rb): the
        // keys are System.Security.Cryptography's RSA, DSA and ECDsa, and the classes
        // there define #initialize, which a C# stub of the same name would take over.

        // MRI's OpenSSL exceptions are plain StandardErrors: the message is exactly what was given,
        // not the "<base> - <message>" form of SystemCallError.
        [RubyException("OpenSSLError")]
        public class OpenSSLError : SystemException {
            public OpenSSLError() : this(null, null) { }
            public OpenSSLError(string message) : this(message, null) { }
            public OpenSSLError(string message, Exception inner) : base(message ?? "OpenSSLError", inner) { }
            public OpenSSLError(MutableString message) : base(message.ConvertToString()) { RubyExceptionData.InitializeException(this, message); }
        }

        [RubyModule("SSL")]
        public static class SSL {
#if FEATURE_SYNC_SOCKETS
        /// <summary>
        /// One TLS connection.  Not an MRI class: OpenSSL::SSL::SSLSocket is the
        /// Ruby class in openssl/ssl.rb and this is what it does its reading and
        /// writing through.
        ///
        /// Every read, write and handshake is one of SslStream's asynchronous operations,
        /// at most one of each kind in flight.  The *_nonblock methods start one and answer
        /// :wait_readable or :wait_writable while it runs; the blocking ones start one, or
        /// take over the one in flight, and wait for it, at most read_timeout or
        /// write_timeout.  The transport is the TCP socket's readiness hook
        /// (IReadinessHook): IO.select and NIO::Selector ask it, not the socket, whether a
        /// read would return, since an operation in flight may already have taken the
        /// socket's bytes, and SslStream may hold bytes the socket never shows.  Asking it
        /// whether a read would return starts that read.
        /// </summary>
        [RubyClass("Transport__")]
        public class Transport : IReadinessHook {
            private RubyContext _context;
            private Socket _socket;
            private SslStream _ssl;
            private bool _verify;
            private string _hostname;
            private int _readTimeout = Timeout.Infinite;
            private int _writeTimeout = Timeout.Infinite;

            // Guards what follows; never held while waiting.
            private readonly object _sync = new object();
            private Task _pendingConnect;
            private Task<int> _pendingRead;
            private byte[] _pendingReadBuffer;
            private Task _pendingWrite;
            // What a finished read brought that nobody has taken yet; _eof once it brought nothing.
            private byte[] _readahead;
            private int _readaheadOffset, _readaheadCount;
            private bool _eof;
            private bool _closed;
            private readonly List<ReadinessWaiter>/*!*/ _waiters = new List<ReadinessWaiter>();

            // A TLS record carries at most 16 KB; a non-blocking write hands over one at a time,
            // and goes on with the next only while the last one went out at once, so that what it
            // reports as written is about what a kernel buffer holds.
            private const int ChunkSize = 16 * 1024;

            // How much a non-blocking write hands over at most. net/protocol slices off what was
            // written and passes the rest back, a copy of the rest every time: 16 KB a call made
            // a 64 MB body cost gigabytes of copying before the kernel buffers were even full.
            private const int NonBlockingWriteLimit = 1024 * 1024;

            // The context's own CAs (ca_file, ca_path, cert_store): the self-signed ones
            // are the roots, the others may complete the chain.  Null means the system
            // store's; _systemTrust says whether the system's count as well.
            private X509Certificate2Collection _roots;
            private X509Certificate2Collection _intermediates;
            private bool _systemTrust = true;
            private Proc _verifyCallback;
            private Exception _callbackError;

            // Why the connection cannot be used any more, once a read, a write or the
            // handshake timed out: SslStream is in an undefined state after that.
            private string _broken;

            [RubyConstructor]
            public static Transport/*!*/ Create(RubyClass/*!*/ self, [NotNull]object/*!*/ io) {
                var socket = io as RubyBasicSocket;
                if (socket == null) {
                    throw RubyExceptions.CreateTypeError("SSLSocket needs a socket to run on");
                }
                var result = new Transport();
                result._context = self.Context;
                result._socket = socket.Socket;
                // The Ruby socket layer leaves Socket.Blocking false and does its own
                // polling; SslStream drives the socket itself and needs it blocking.
                result._socket.Blocking = true;
                IoReadiness.SetHook(socket, result);
                return result;
            }

            /// <summary>
            /// The CA certificates to check the server's chain against, as X509Certificate2s
            /// (OpenSSL::X509::Certificate#__clr).  As in OpenSSL::X509::Store#verify, the
            /// self-signed ones are trusted and the others only help build the chain.
            /// system says whether the system's trust store counts as well, which it does
            /// for a cert_store that had set_default_paths called on it.
            /// </summary>
            [RubyMethod("trust")]
            public static void Trust(Transport/*!*/ self, [NotNull]IList/*!*/ certificates, bool system) {
                var roots = new X509Certificate2Collection();
                var intermediates = new X509Certificate2Collection();
                foreach (object item in certificates) {
                    var certificate = item as X509Certificate2;
                    if (certificate == null) {
                        throw RubyExceptions.CreateTypeError("trust anchors must be X509Certificate2s");
                    }
                    intermediates.Add(certificate);
                    if (certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData)) {
                        roots.Add(certificate);
                    }
                }
                self._roots = roots;
                self._intermediates = intermediates;
                self._systemTrust = system;
            }

            /// <summary>
            /// Called once with the chain and the SslPolicyErrors as an Integer when the
            /// server's certificate has been checked; what it returns decides.
            /// OpenSSL::SSL::SSLSocket runs SSLContext#verify_callback from it.
            /// </summary>
            [RubyMethod("verify_callback=")]
            public static void SetVerifyCallback(Transport/*!*/ self, Proc callback) {
                self._verifyCallback = callback;
            }

            // What a server certificate has to be good for: TLS server authentication.
            private static readonly Oid ServerAuthentication = new Oid("1.3.6.1.5.5.7.3.1");

            private bool Validate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors errors) {
                if (!_verify) {
                    return true;
                }
                // #connect gave up on this handshake (Thread#raise, Timeout), or the socket was
                // closed under it (an open_timeout); nothing waits for an answer.
                if (_broken != null || _closed) {
                    return false;
                }

                // No SNI name means net/http connected to an IP address: it checks the
                // name itself afterwards (SSLSocket#post_connection_check), as on MRI.
                if (_hostname.Length == 0) {
                    errors &= ~SslPolicyErrors.RemoteCertificateNameMismatch;
                }

                X509Chain custom = null;
                X509Certificate2 leaf = null;
                try {
                    if (_roots != null && certificate != null) {
                        custom = new X509Chain();
                        var policy = custom.ChainPolicy;
                        policy.RevocationMode = X509RevocationMode.NoCheck;
                        policy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                        policy.ApplicationPolicy.Add(ServerAuthentication);
                        policy.CustomTrustStore.AddRange(_roots);
                        policy.ExtraStore.AddRange(_intermediates);
                        // The intermediates the server sent.
                        if (chain != null) {
                            policy.ExtraStore.AddRange(chain.ChainPolicy.ExtraStore);
                        }
                        leaf = new X509Certificate2(certificate);
                        bool trusted = custom.Build(leaf);
                        if (_systemTrust && (errors & SslPolicyErrors.RemoteCertificateChainErrors) == 0) {
                            trusted = true;
                        } else {
                            chain = custom;
                        }
                        errors = trusted
                            ? errors & ~SslPolicyErrors.RemoteCertificateChainErrors
                            : errors | SslPolicyErrors.RemoteCertificateChainErrors;
                    }

                    if (_verifyCallback == null) {
                        return errors == SslPolicyErrors.None;
                    }

                    // SslStream runs this on whichever thread finished reading the server's
                    // certificate.  SSLSocket's callback handles the exceptions of
                    // SSLContext#verify_callback itself; anything else is carried back to #connect.
                    try {
                        return _verifyCallback.Call(null, chain, (int)errors) is bool accepted && accepted;
                    } catch (Exception error) {
                        _callbackError = error;
                        return false;
                    }
                } finally {
                    custom?.Dispose();
                    leaf?.Dispose();
                }
            }

            /// <summary>
            /// The client handshake.  hostname is the SNI name and the name the
            /// certificate is checked against; verify false accepts any certificate,
            /// which is what OpenSSL::SSL::VERIFY_NONE means.  timeout is in
            /// milliseconds, -1 for none; a handshake that takes longer raises
            /// IO::TimeoutError.
            /// </summary>
            [RubyMethod("connect")]
            public static Transport/*!*/ Connect(Transport/*!*/ self,
                [DefaultProtocol]MutableString hostname, bool verify, [DefaultProtocol, DefaultParameterValue(-1)]int timeout) {

                // The token bounds the handshake and cancels the read it is waiting in.
                using (var cancel = timeout < 0 ? new CancellationTokenSource() : new CancellationTokenSource(timeout)) {
                    Task task = self.StartConnect(hostname, verify, cancel.Token);
                    try {
                        task.Wait();
                    } catch (AggregateException) {
                        // FinishConnect reports it
                    } catch (ThreadInterruptedException) {
                        // Thread#raise or Thread#kill (Timeout.timeout among them) woke this
                        // thread up.  The handshake goes on without it: stop it, make sure a
                        // late certificate check does not call into Ruby, and let the
                        // exception that was raised into this thread through.
                        self._broken = "the TLS handshake was interrupted";
                        cancel.Cancel();
#if FEATURE_THREAD && FEATURE_EXCEPTION_STATE
                        RubyUtils.TranslateThreadInterrupt();
#endif
                        throw;
                    }
                    if (cancel.IsCancellationRequested && !task.IsCompletedSuccessfully) {
                        // An OperationCanceledException, or an IOException from the cancelled read.
                        lock (self._sync) {
                            self._pendingConnect = null;
                        }
                        throw self.TimedOut("TLS handshake timed out after " + timeout + " ms");
                    }
                }
                self.FinishConnect();
                return self;
            }

            /// <summary>
            /// The handshake without waiting for it: the transport once it is done, else
            /// :wait_readable, and SSLSocket#connect_nonblock asks again after IO.select.
            /// </summary>
            [RubyMethod("connect_nonblock")]
            public static object/*!*/ ConnectNonBlocking(Transport/*!*/ self, [DefaultProtocol]MutableString hostname, bool verify) {
                Task task = self.StartConnect(hostname, verify, CancellationToken.None);
                if (!task.IsCompleted) {
                    return self._context.CreateAsciiSymbol("wait_readable");
                }
                self.FinishConnect();
                return self;
            }

            // The handshake in flight, started if it is not.
            private Task/*!*/ StartConnect(MutableString hostname, bool verify, CancellationToken cancel) {
                lock (_sync) {
                    CheckUsable();
                    if (_pendingConnect != null) {
                        return _pendingConnect;
                    }
                    if (_ssl != null) {
                        return Task.CompletedTask;
                    }
                    _verify = verify;
                    _hostname = hostname == null ? "" : hostname.ConvertToString();
                    _callbackError = null;
                    try {
                        _ssl = new SslStream(new NetworkStream(_socket, false), false,
                            new RemoteCertificateValidationCallback(Validate));
                        var options = new SslClientAuthenticationOptions { TargetHost = _hostname };
                        _pendingConnect = _ssl.AuthenticateAsClientAsync(options, cancel);
                    } catch (Exception error) {
                        throw new SSLError(Unwrap(error).Message);
                    }
                    NotifyOnCompletion(_pendingConnect);
                    return _pendingConnect;
                }
            }

            private void FinishConnect() {
                Task task;
                lock (_sync) {
                    task = _pendingConnect;
                    _pendingConnect = null;
                }
                if (task == null) {
                    return;
                }
                try {
                    task.GetAwaiter().GetResult();
                } catch (Exception error) {
                    // The stream never authenticated: a second #connect must not find it and
                    // call the connection made.
                    lock (_sync) {
                        _broken = "the TLS handshake failed";
                    }
                    if (_callbackError != null) {
                        throw _callbackError;
                    }
                    throw new SSLError(Unwrap(error).Message);
                }
            }

            /// <summary>Milliseconds a blocking read may wait, -1 for ever.</summary>
            [RubyMethod("read_timeout=")]
            public static void SetReadTimeout(Transport/*!*/ self, [DefaultProtocol]int milliseconds) {
                self._readTimeout = milliseconds < 0 ? Timeout.Infinite : Math.Max(milliseconds, 1);
            }

            /// <summary>Milliseconds a blocking write may wait, -1 for ever.</summary>
            [RubyMethod("write_timeout=")]
            public static void SetWriteTimeout(Transport/*!*/ self, [DefaultProtocol]int milliseconds) {
                self._writeTimeout = milliseconds < 0 ? Timeout.Infinite : Math.Max(milliseconds, 1);
            }

            // Waits for an operation in flight, as a blocking call does; false when the time
            // ran out first.  A Thread#raise or Thread#kill gets through, and the operation
            // stays in flight for whoever asks next.
            private static bool WaitFor(Task/*!*/ task, int milliseconds) {
                try {
                    return task.Wait(milliseconds);
                } catch (AggregateException) {
                    // done, with an error the caller reports
                    return true;
                } catch (ThreadInterruptedException) {
#if FEATURE_THREAD && FEATURE_EXCEPTION_STATE
                    RubyUtils.TranslateThreadInterrupt();
#endif
                    throw;
                }
            }

            private void NotifyOnCompletion(Task/*!*/ task) {
                if (!task.IsCompleted) {
                    task.ContinueWith(_ => NotifyWaiters(), CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            }

            private void NotifyWaiters() {
                ReadinessWaiter[] waiters;
                lock (_waiters) {
                    waiters = _waiters.ToArray();
                }
                foreach (var waiter in waiters) {
                    waiter.Signal();
                }
            }

            void IReadinessHook.AddWaiter(ReadinessWaiter/*!*/ waiter) {
                lock (_waiters) {
                    _waiters.Add(waiter);
                }
            }

            void IReadinessHook.RemoveWaiter(ReadinessWaiter/*!*/ waiter) {
                lock (_waiters) {
                    _waiters.Remove(waiter);
                }
            }

            /// <summary>
            /// What IO.select and NIO::Selector are told about the TCP socket under this
            /// transport.  Before the handshake and after #close the socket answers for
            /// itself; a broken connection is ready, so that the next call says so.
            /// </summary>
            bool? IReadinessHook.IsReady(int kind) {
                lock (_sync) {
                    if (_ssl == null || _closed) {
                        return null;
                    }
                    if (_broken != null) {
                        return true;
                    }
                    if (_pendingConnect != null) {
                        return _pendingConnect.IsCompleted;
                    }
                    if (kind == IoReadiness.Read) {
                        if (_readaheadCount > 0 || _eof) {
                            return true;
                        }
                        if (_pendingRead == null) {
                            try {
                                StartRead(ChunkSize);
                            } catch (Exception) {
                                // the read reports it
                                return true;
                            }
                        }
                        return _pendingRead.IsCompleted;
                    }
                    return _pendingWrite == null ? (bool?)null : _pendingWrite.IsCompleted;
                }
            }

            /// <summary>
            /// Whether the server's certificate is valid for name, a host name or an IP
            /// address: SSLSocket#post_connection_check.  As MRI's
            /// verify_certificate_identity, a certificate without a subjectAltName is
            /// matched on its common name, for an IP address too.
            /// </summary>
            [RubyMethod("matches_hostname?")]
            public static bool MatchesHostname(Transport/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ name) {
                var certificate = self._ssl?.RemoteCertificate;
                string host = name.ConvertToString();
                if (certificate == null || host.Length == 0) {
                    return false;
                }
                using (var certificate2 = new X509Certificate2(certificate)) {
                    IPAddress address, commonNameAddress;
                    if (certificate2.Extensions["2.5.29.17"] == null && IPAddress.TryParse(host, out address)) {
                        string commonName = certificate2.GetNameInfo(X509NameType.SimpleName, false);
                        return IPAddress.TryParse(commonName, out commonNameAddress) && commonNameAddress.Equals(address);
                    }
                    try {
                        return certificate2.MatchesHostname(host, true, true);
                    } catch (ArgumentException) {
                        // not a host name at all
                        return false;
                    }
                }
            }

            // An SslStream failure arrives wrapped in an AuthenticationException whose
            // inner exception says what actually went wrong.
            private static Exception/*!*/ Unwrap(Exception/*!*/ error) {
                return (error is AuthenticationException || error is IOException) && error.InnerException != null
                    ? error.InnerException : error;
            }

            // Marks the connection unusable and builds the IO::TimeoutError to raise.
            private Exception/*!*/ TimedOut(string/*!*/ message) {
                _broken = message;
                object ioClass;
                if (_context.ObjectClass.TryGetConstant(null, "IO", out ioClass) && ioClass is RubyModule) {
                    var error = _context.CreateLibraryException((RubyModule)ioClass, "TimeoutError", message);
                    if (error != null) {
                        return error;
                    }
                }
                return new SSLError(message);
            }

            private void CheckUsable() {
                if (_broken != null) {
                    throw new SSLError("SSL connection is unusable: " + _broken);
                }
            }

            // The stream, once the handshake is over; call with _sync held.
            private SslStream/*!*/ Stream {
                get {
                    CheckUsable();
                    if (_closed) {
                        throw RubyExceptions.CreateIOError("closed stream");
                    }
                    if (_ssl == null || _pendingConnect != null) {
                        throw new SSLError("SSL session is not started yet");
                    }
                    return _ssl;
                }
            }

            // Errors of SslStream's operations, as Ruby sees them.
            private Exception/*!*/ Translate(Exception/*!*/ error) {
                if (error is ObjectDisposedException || _closed) {
                    return RubyExceptions.CreateIOError("closed stream");
                }
                return new SSLError(Unwrap(error).Message);
            }

            // Starts a read into a buffer of its own; call with _sync held and none in flight.
            private void StartRead(int length) {
                var stream = Stream;
                _pendingReadBuffer = new byte[Math.Max(Math.Min(length, 1 << 20), ChunkSize)];
                _pendingRead = stream.ReadAsync(_pendingReadBuffer, 0, _pendingReadBuffer.Length);
                NotifyOnCompletion(_pendingRead);
            }

            // Moves what the finished read brought to the read-ahead; call with _sync held.
            private void FinishRead() {
                var task = _pendingRead;
                _pendingRead = null;
                int count;
                try {
                    count = task.GetAwaiter().GetResult();
                } catch (Exception error) {
                    throw Translate(error);
                }
                if (count == 0) {
                    _eof = true;
                } else {
                    _readahead = _pendingReadBuffer;
                    _readaheadOffset = 0;
                    _readaheadCount = count;
                }
                _pendingReadBuffer = null;
            }

            // Up to length bytes of the read-ahead, or null if it is empty; call with _sync held.
            private MutableString TakeReadahead(int length) {
                if (_readaheadCount == 0) {
                    return null;
                }
                int count = Math.Min(length, _readaheadCount);
                var result = new byte[count];
                Buffer.BlockCopy(_readahead, _readaheadOffset, result, 0, count);
                _readaheadOffset += count;
                _readaheadCount -= count;
                if (_readaheadCount == 0) {
                    _readahead = null;
                }
                return MutableString.CreateBinary(result);
            }

            /// <summary>Up to length bytes, or nil at end of stream; waits at most read_timeout.</summary>
            [RubyMethod("read")]
            public static MutableString Read(Transport/*!*/ self, [DefaultProtocol]int length) {
                Task<int> task;
                lock (self._sync) {
                    _ = self.Stream;
                    var data = self.TakeReadahead(length);
                    if (data != null || self._eof) {
                        return data;
                    }
                    if (self._pendingRead == null) {
                        self.StartRead(length);
                    }
                    task = self._pendingRead;
                }
                if (!WaitFor(task, self._readTimeout)) {
                    throw self.TimedOut("read timed out after " + self._readTimeout + " ms");
                }
                lock (self._sync) {
                    if (self._pendingRead == task) {
                        self.FinishRead();
                    }
                    return self.TakeReadahead(length);
                }
            }

            /// <summary>
            /// Up to length bytes if a read has brought them, nil at end of stream, else
            /// :wait_readable, with a read left in flight that IO.select sees finish.
            /// </summary>
            [RubyMethod("read_nonblock")]
            public static object ReadNonBlocking(Transport/*!*/ self, [DefaultProtocol]int length) {
                lock (self._sync) {
                    _ = self.Stream;
                    var data = self.TakeReadahead(length);
                    if (data != null || self._eof) {
                        return data;
                    }
                    if (self._pendingRead == null) {
                        self.StartRead(length);
                    }
                    if (!self._pendingRead.IsCompleted) {
                        return self._context.CreateAsciiSymbol("wait_readable");
                    }
                    self.FinishRead();
                    return self.TakeReadahead(length);
                }
            }

            // Takes over a write in flight before another; call without _sync held.
            private void FinishWrite(Task/*!*/ task) {
                lock (_sync) {
                    if (_pendingWrite == task) {
                        _pendingWrite = null;
                    }
                }
                try {
                    task.GetAwaiter().GetResult();
                } catch (Exception error) {
                    throw Translate(error);
                }
            }

            /// <summary>All of data; waits at most write_timeout.</summary>
            [RubyMethod("write")]
            public static int Write(Transport/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ data) {
                byte[] bytes = data.ConvertToBytes();
                Task pending, task;
                lock (self._sync) {
                    self.CheckUsable();
                    pending = self._pendingWrite;
                }
                if (pending != null) {
                    if (!WaitFor(pending, self._writeTimeout)) {
                        throw self.TimedOut("write timed out after " + self._writeTimeout + " ms");
                    }
                    self.FinishWrite(pending);
                }
                lock (self._sync) {
                    var stream = self.Stream;
                    try {
                        task = stream.WriteAsync(bytes, 0, bytes.Length);
                    } catch (Exception error) {
                        throw self.Translate(error);
                    }
                    self._pendingWrite = task;
                    self.NotifyOnCompletion(task);
                }
                if (!WaitFor(task, self._writeTimeout)) {
                    throw self.TimedOut("write timed out after " + self._writeTimeout + " ms");
                }
                self.FinishWrite(task);
                return bytes.Length;
            }

            /// <summary>
            /// How much of data was handed over - 16 KB records for as long as they go out at once,
            /// up to 1 MB; the last may still be on its way, as bytes in a kernel buffer are - or
            /// :wait_writable while the last one is.
            /// </summary>
            [RubyMethod("write_nonblock")]
            public static object WriteNonBlocking(Transport/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ data) {
                Task pending;
                lock (self._sync) {
                    self.CheckUsable();
                    pending = self._pendingWrite;
                    if (pending != null && !pending.IsCompleted) {
                        return self._context.CreateAsciiSymbol("wait_writable");
                    }
                }
                if (pending != null) {
                    self.FinishWrite(pending);
                }
                // only what may go out: converting all of a large body on every call is a copy of it
                byte[] bytes = data.GetBinarySlice(0, Math.Min(data.GetByteCount(), NonBlockingWriteLimit));
                int written = 0;
                while (written < bytes.Length) {
                    int count = Math.Min(bytes.Length - written, ChunkSize);
                    Task task;
                    lock (self._sync) {
                        var stream = self.Stream;
                        try {
                            task = stream.WriteAsync(bytes, written, count);
                        } catch (Exception error) {
                            throw self.Translate(error);
                        }
                        if (!task.IsCompleted) {
                            self._pendingWrite = task;
                            self.NotifyOnCompletion(task);
                            return written + count;
                        }
                    }
                    self.FinishWrite(task);
                    written += count;
                }
                return written;
            }

            [RubyMethod("close")]
            public static void Close(Transport/*!*/ self) {
                SslStream ssl;
                lock (self._sync) {
                    ssl = self._ssl;
                    self._closed = true;
                }
                if (ssl != null) {
                    try {
                        ssl.Dispose();
                    } catch (Exception) {
                        // closing a connection the peer already dropped is not an error
                    }
                }
                self.NotifyWaiters();
            }

            /// <summary>The peer's certificate as DER, or nil before the handshake.</summary>
            [RubyMethod("peer_cert")]
            public static MutableString PeerCertificate(Transport/*!*/ self) {
                if (self._ssl == null || self._ssl.RemoteCertificate == null) {
                    return null;
                }
                return MutableString.CreateBinary(self._ssl.RemoteCertificate.Export(X509ContentType.Cert));
            }

            /// <summary>"TLSv1.3", the spelling OpenSSL::SSL::SSLSocket#ssl_version uses.</summary>
            [RubyMethod("protocol")]
            public static MutableString/*!*/ Protocol(Transport/*!*/ self) {
                if (self._ssl == null) {
                    return null;
                }
                switch (self._ssl.SslProtocol) {
#pragma warning disable SYSLIB0039 // TLS 1.0 and 1.1 are obsolete, but a connection can still have negotiated one
                    case SslProtocols.Tls: return MutableString.CreateAscii("TLSv1");
                    case SslProtocols.Tls11: return MutableString.CreateAscii("TLSv1.1");
#pragma warning restore SYSLIB0039
                    case SslProtocols.Tls12: return MutableString.CreateAscii("TLSv1.2");
                    case SslProtocols.Tls13: return MutableString.CreateAscii("TLSv1.3");
                    default: return MutableString.CreateAscii(self._ssl.SslProtocol.ToString());
                }
            }

            /// <summary>The negotiated cipher suite, e.g. "TLS_AES_256_GCM_SHA384".</summary>
            [RubyMethod("cipher")]
            public static MutableString Cipher(Transport/*!*/ self) {
                if (self._ssl == null) {
                    return null;
                }
                return MutableString.CreateAscii(self._ssl.NegotiatedCipherSuite.ToString());
            }

            /// <summary>Bytes already decrypted and waiting to be read.</summary>
            [RubyMethod("pending")]
            public static int Pending(Transport/*!*/ self) {
                lock (self._sync) {
                    // A failed read is left for the next read to report.
                    if (self._pendingRead != null && self._pendingRead.IsCompletedSuccessfully) {
                        self.FinishRead();
                    }
                    return self._readaheadCount;
                }
            }
        }
#endif

            [RubyException("SSLError")]
            public class SSLError : OpenSSLError {
                public SSLError() : this(null, null) { }
                public SSLError(string message) : this(message, null) { }
                public SSLError(string message, Exception inner) : base(message ?? "SSLError", inner) { }
                public SSLError(MutableString message) : base(message.ConvertToString()) { RubyExceptionData.InitializeException(this, message); }
            }
        }
    }
}
#endif