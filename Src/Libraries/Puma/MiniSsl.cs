/* ****************************************************************************
 *
 * Copyright (c) IronRuby contributors.
 *
 * Puma's TLS engine - ext/puma_http11/mini_ssl.c - for IronRuby, on System.Net.Security.SslStream.
 * 3-clause BSD, as puma is.
 *
 * ***************************************************************************/

// What this is, and where it differs from the C extension:
//
//   * The C extension is OpenSSL over a pair of memory BIOs: puma reads ciphertext off the
//     socket and hands it to Engine#inject, takes plaintext from #read, gives plaintext to #write
//     and sends what #extract answers.  Here the pair of BIOs is MemoryBio, the inner stream of a
//     server SslStream: #inject fills its inbound side, #extract drains its outbound side.  The
//     handshake and the reads run as SslStream's asynchronous operations, waiting on MemoryBio for
//     input; nothing blocks on the socket, which puma alone touches.
//   * OpenSSL does its work inside SSL_read, so the plaintext (or the handshake's answer) is there
//     as soon as #inject returns.  SslStream does it in continuations on the thread pool.  So that
//     puma sees the same thing, #read, #extract and #write first wait until SslStream has used up
//     the input and is waiting for more (or has finished): it never waits for anything else, so
//     that is a matter of microseconds.
//   * #read answers all the plaintext there is, not 512 bytes of it at a time.  #extract likewise.
//   * The context: cert and key (files, or cert_pem and key_pem), key_password_command for an
//     encrypted key, ca and verify_mode none, peer or force_peer for client certificates,
//     no_tlsv1 and no_tlsv1_1.  Not used: ssl_cipher_filter and ssl_ciphersuites (SslStream takes
//     the system's cipher suites; Windows has no per-connection list), verification_flags and
//     reuse (SslStream keeps its own session cache).
//   * Engine.client, used by puma's own tests only, is not here.

using System;
using System.IO;
using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using IronRuby.Builtins;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;

namespace IronRuby.StandardLibrary.Puma {

    public static partial class PumaModule {

        [RubyModule("MiniSSL")]
        public static class MiniSsl {
            // puma's minissl.rb takes the version out of these (HAS_TLS1_3): the TLS SslStream
            // speaks is what OpenSSL 3.0 speaks.
            [RubyConstant]
            public const string OPENSSL_VERSION = "OpenSSL 3.0.0 (IronRuby, System.Net.Security.SslStream)";

            [RubyConstant]
            public const string OPENSSL_LIBRARY_VERSION = OPENSSL_VERSION;

            [RubyConstant]
            public const bool OPENSSL_NO_SSL3 = true;

            [RubyConstant]
            public const bool OPENSSL_NO_TLS1 = false;

            [RubyConstant]
            public const bool OPENSSL_NO_TLS1_1 = false;

            [RubyMethod("check", RubyMethodAttributes.PublicSingleton)]
            public static object Check(RubyModule/*!*/ self) {
                return null;
            }

            [RubyException("SSLError")]
            public class SSLError : SystemException {
                public SSLError() : this(null, null) { }
                public SSLError(string message) : this(message, null) { }
                public SSLError(string message, Exception inner) : base(message ?? "Puma::MiniSSL::SSLError", inner) { }
            }

            // puma's VERIFY_PEER and VERIFY_FAIL_IF_NO_PEER_CERT, OpenSSL's values.
            private const int VerifyPeer = 1;
            private const int VerifyFailIfNoPeerCert = 2;

            private static readonly Oid ClientAuthentication = new Oid("1.3.6.1.5.5.7.3.2");

            /// <summary>
            /// What a Puma::MiniSSL::Context says, loaded once per listener: the server
            /// certificate with its key and chain, and how to check a client's certificate.
            /// </summary>
            [RubyClass("SSLContext")]
            public sealed class SSLContext {
                internal SslStreamCertificateContext/*!*/ Certificate;
                internal X509Certificate2Collection Ca;
                internal int VerifyMode;
                internal SslProtocols Protocols;

                [RubyConstructor]
                public static SSLContext/*!*/ Create(CallSiteStorage<Func<CallSite, object, object>>/*!*/ keyPasswordStorage,
                    RubyClass/*!*/ self, [NotNull]object/*!*/ miniContext) {

                    var context = self.Context;
                    string key = FileText(context, Get(context, miniContext, "@key"), "key");
                    if (key == null) {
                        key = StringOrNull(Get(context, miniContext, "@key_pem"));
                    }
                    string cert = FileText(context, Get(context, miniContext, "@cert"), "cert");
                    if (cert == null) {
                        cert = StringOrNull(Get(context, miniContext, "@cert_pem"));
                    }
                    if (key == null) {
                        throw new SSLError("no SSL key given (key= or key_pem=)");
                    }
                    if (cert == null) {
                        throw new SSLError("no SSL certificate given (cert= or cert_pem=)");
                    }

                    string password = null;
                    if (Get(context, miniContext, "@key_password_command") != null) {
                        var site = keyPasswordStorage.GetCallSite("key_password", 0);
                        password = StringOrNull(site.Target(site, miniContext));
                    }

                    var result = new SSLContext();
                    result.Certificate = LoadCertificate(cert, key, password);

                    string ca = FileText(context, Get(context, miniContext, "@ca"), "ca");
                    if (ca != null) {
                        result.Ca = new X509Certificate2Collection();
                        try {
                            result.Ca.ImportFromPem(ca);
                        } catch (CryptographicException e) {
                            throw new SSLError("cannot load the SSL ca: " + e.Message, e);
                        }
                    }

                    object verifyMode = Get(context, miniContext, "@verify_mode");
                    result.VerifyMode = verifyMode is int ? (int)verifyMode : 0;

#pragma warning disable SYSLIB0039 // TLS 1.0 and 1.1: what no_tlsv1 and no_tlsv1_1 are about
                    if (RubyOps.IsTrue(Get(context, miniContext, "@no_tlsv1_1"))) {
                        result.Protocols = SslProtocols.Tls12 | SslProtocols.Tls13;
                    } else if (RubyOps.IsTrue(Get(context, miniContext, "@no_tlsv1"))) {
                        result.Protocols = SslProtocols.Tls11 | SslProtocols.Tls12 | SslProtocols.Tls13;
                    } else {
                        result.Protocols = SslProtocols.None;
                    }
#pragma warning restore SYSLIB0039
                    return result;
                }

                private static object Get(RubyContext/*!*/ context, object/*!*/ obj, string/*!*/ name) {
                    object value;
                    return context.TryGetInstanceVariable(obj, name, out value) ? value : null;
                }

                private static string StringOrNull(object value) {
                    var str = value as MutableString;
                    return str != null ? str.ToString() : null;
                }

                private static string FileText(RubyContext/*!*/ context, object path, string/*!*/ what) {
                    string name = StringOrNull(path);
                    if (name == null) {
                        return null;
                    }
                    try {
                        using (var stream = context.DomainManager.Platform.OpenInputFileStream(name))
                        using (var reader = new StreamReader(stream)) {
                            return reader.ReadToEnd();
                        }
                    } catch (IOException e) {
                        throw new SSLError(String.Format("cannot read the SSL {0} file {1}: {2}", what, name, e.Message), e);
                    } catch (UnauthorizedAccessException e) {
                        throw new SSLError(String.Format("cannot read the SSL {0} file {1}: {2}", what, name, e.Message), e);
                    }
                }

                // The first certificate of the PEM is the server's, the others its chain.
                private static SslStreamCertificateContext/*!*/ LoadCertificate(string/*!*/ certPem, string/*!*/ keyPem, string password) {
                    X509Certificate2 leaf;
                    var chain = new X509Certificate2Collection();
                    try {
                        leaf = password != null
                            ? X509Certificate2.CreateFromEncryptedPem(certPem, keyPem, password)
                            : X509Certificate2.CreateFromPem(certPem, keyPem);
                        chain.ImportFromPem(certPem);
                    } catch (CryptographicException e) {
                        throw new SSLError("cannot load the SSL certificate and key: " + e.Message, e);
                    } catch (ArgumentException e) {
                        throw new SSLError("cannot load the SSL certificate and key: " + e.Message, e);
                    }
                    if (chain.Count > 0) {
                        chain.RemoveAt(0);
                    }

                    // SChannel cannot serve with an ephemeral key: round-trip the certificate through PFX.
                    if (OperatingSystem.IsWindows()) {
                        // the ephemeral one is disposed once the PFX copy has replaced it
                        using (X509Certificate2 ephemeral = leaf) {
#if NET9_0_OR_GREATER
                            leaf = X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
#else
                            leaf = new X509Certificate2(ephemeral.Export(X509ContentType.Pfx));
#endif
                        }
                    }
                    return SslStreamCertificateContext.Create(leaf, chain, offline: true);
                }
            }

            /// <summary>
            /// One TLS connection, server side: an SslStream over a MemoryBio.
            /// </summary>
            [RubyClass("Engine")]
            public sealed class Engine {
                // Guards everything below and MemoryBio's buffers; never held while SslStream runs.
                private readonly object _sync = new object();
                private readonly ByteQueue/*!*/ _inbound = new ByteQueue();
                private readonly ByteQueue/*!*/ _outbound = new ByteQueue();
                private readonly ByteQueue/*!*/ _plain = new ByteQueue();
                // SslStream's read of the inbound side, while there is nothing to give it.
                private TaskCompletionSource<int> _bioRead;
                private Memory<byte> _bioReadBuffer;
                private bool _inboundClosed;

                private SslStream _ssl;
                private bool _verifyPeer;
                private bool _handshaken;
                private bool _eof;          // the client sent close_notify
                private bool _done;         // the handshake or a read failed, or ended
                private Exception _error;

                // How long #read, #extract and #write wait for SslStream to take up its input.
                // It only computes, so this is a safety net, not a timeout anyone should meet.
                private const int IdleTimeout = 30 * 1000;

                [RubyMethod("server", RubyMethodAttributes.PublicSingleton)]
                public static Engine/*!*/ Server(RubyClass/*!*/ self, [NotNull]SSLContext/*!*/ context) {
                    var engine = new Engine();
                    engine._verifyPeer = (context.VerifyMode & VerifyPeer) != 0;
                    engine._ssl = new SslStream(new MemoryBio(engine), false);

                    var options = new SslServerAuthenticationOptions {
                        ServerCertificateContext = context.Certificate,
                        EnabledSslProtocols = context.Protocols,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                        ClientCertificateRequired = engine._verifyPeer,
                        RemoteCertificateValidationCallback = (sender, certificate, chain, errors) =>
                            !engine._verifyPeer || ValidateClient(context, certificate, chain, errors),
                    };
                    // Runs up to its first read of MemoryBio, and on from there in the background.
                    _ = engine.Run(options);
                    return engine;
                }

                // OpenSSL's check of a client certificate: against the ca, as a TLS client.
                private static bool ValidateClient(SSLContext/*!*/ context, X509Certificate certificate, X509Chain chain, SslPolicyErrors errors) {
                    if (certificate == null) {
                        return (context.VerifyMode & VerifyFailIfNoPeerCert) == 0;
                    }
                    if (context.Ca == null) {
                        return (errors & ~SslPolicyErrors.RemoteCertificateNameMismatch) == SslPolicyErrors.None;
                    }
                    using (var custom = new X509Chain())
                    using (var leaf = new X509Certificate2(certificate)) {
                        var policy = custom.ChainPolicy;
                        policy.RevocationMode = X509RevocationMode.NoCheck;
                        policy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                        policy.ApplicationPolicy.Add(ClientAuthentication);
                        policy.CustomTrustStore.AddRange(context.Ca);
                        policy.ExtraStore.AddRange(context.Ca);
                        // The intermediates the client sent.
                        if (chain != null) {
                            policy.ExtraStore.AddRange(chain.ChainPolicy.ExtraStore);
                        }
                        return custom.Build(leaf);
                    }
                }

                // The handshake, then reads until the client closes: started here, it runs on as
                // MemoryBio gets input.
                private async Task Run(SslServerAuthenticationOptions/*!*/ options) {
                    try {
                        await _ssl.AuthenticateAsServerAsync(options, CancellationToken.None).ConfigureAwait(false);
                        lock (_sync) {
                            _handshaken = true;
                        }
                        var buffer = new byte[16 * 1024];
                        while (true) {
                            int count = await _ssl.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                            lock (_sync) {
                                if (count == 0) {
                                    _eof = true;
                                    break;
                                }
                                _plain.Append(buffer, 0, count);
                            }
                        }
                    } catch (Exception e) {
                        lock (_sync) {
                            _error = e;
                        }
                    } finally {
                        bool dispose;
                        lock (_sync) {
                            _done = true;
                            dispose = _inboundClosed;
                            Monitor.PulseAll(_sync);
                        }
                        if (dispose) {
                            _ssl.Dispose();
                        }
                    }
                }

                // Waits until SslStream has taken up all of the input and waits for more, or is done.
                // Called holding _sync.
                private void WaitIdle() {
                    int start = Environment.TickCount;
                    while (!_done && _bioRead == null) {
                        int left = IdleTimeout - unchecked(Environment.TickCount - start);
                        if (left <= 0 || !Monitor.Wait(_sync, left)) {
                            throw new SSLError("the TLS engine did not take up its input");
                        }
                    }
                }

                private SSLError/*!*/ Failure() {
                    var authentication = _error as AuthenticationException;
                    string message = (_error.InnerException != null && authentication != null)
                        ? _error.Message + " (" + _error.InnerException.Message + ")"
                        : _error.Message;
                    return new SSLError("SSL error: " + message, _error);
                }

                /// <summary>
                /// inject(data) -> Integer: hands ciphertext read off the socket to the engine.
                /// </summary>
                [RubyMethod("inject")]
                public static int Inject(Engine/*!*/ self, [NotNull]MutableString/*!*/ data) {
                    byte[] bytes = data.ToByteArray();
                    TaskCompletionSource<int> read = null;
                    int count = 0;
                    lock (self._sync) {
                        if (self._inboundClosed) {
                            return 0;
                        }
                        self._inbound.Append(bytes, 0, bytes.Length);
                        if (self._bioRead != null && self._inbound.Count > 0) {
                            read = self._bioRead;
                            count = self._inbound.Take(self._bioReadBuffer.Span);
                            self._bioRead = null;
                            self._bioReadBuffer = default(Memory<byte>);
                        }
                    }
                    if (read != null) {
                        read.SetResult(count);
                    }
                    return bytes.Length;
                }

                /// <summary>
                /// read -> String or nil: the plaintext there is, nil while there is none yet.
                /// Raises EOFError once the client has closed the TLS session, Puma::MiniSSL::SSLError
                /// when the handshake or the connection failed.
                /// </summary>
                [RubyMethod("read")]
                public static MutableString Read(Engine/*!*/ self) {
                    lock (self._sync) {
                        self.WaitIdle();
                        if (self._plain.Count > 0) {
                            return MutableString.CreateBinary(self._plain.TakeAll());
                        }
                        if (self._eof) {
                            throw new EOFError("end of file reached");
                        }
                        if (self._error != null && !self._inboundClosed) {
                            throw self.Failure();
                        }
                        return null;
                    }
                }

                /// <summary>
                /// write(data) -> Integer: encrypts all of the plaintext; #extract has the ciphertext.
                /// </summary>
                [RubyMethod("write")]
                public static int Write(Engine/*!*/ self, [NotNull]MutableString/*!*/ data) {
                    byte[] bytes = data.ToByteArray();
                    lock (self._sync) {
                        self.WaitIdle();
                        if (self._error != null) {
                            throw self.Failure();
                        }
                        if (!self._handshaken) {
                            throw new SSLError("SSL error: the handshake has not finished");
                        }
                    }
                    try {
                        // MemoryBio's writes complete at once, so this does not block.
                        self._ssl.Write(bytes, 0, bytes.Length);
                    } catch (IOException e) {
                        throw new SSLError("SSL error: " + e.Message, e);
                    } catch (ObjectDisposedException e) {
                        throw new SSLError("SSL error: " + e.Message, e);
                    }
                    return bytes.Length;
                }

                /// <summary>
                /// extract -> String or nil: the ciphertext to send, nil when there is none.
                /// </summary>
                [RubyMethod("extract")]
                public static MutableString Extract(Engine/*!*/ self) {
                    lock (self._sync) {
                        self.WaitIdle();
                        return self._outbound.Count > 0 ? MutableString.CreateBinary(self._outbound.TakeAll()) : null;
                    }
                }

                /// <summary>
                /// shutdown -> true or false: false when there is a close_notify for #extract to hand out.
                /// </summary>
                [RubyMethod("shutdown")]
                public static bool Shutdown(Engine/*!*/ self) {
                    bool notify;
                    lock (self._sync) {
                        self.WaitIdle();
                        notify = self._handshaken && self._error == null && !self._inboundClosed;
                    }
                    if (notify) {
                        try {
                            self._ssl.ShutdownAsync().GetAwaiter().GetResult();
                        } catch (IOException) {
                        } catch (InvalidOperationException) {
                        }
                    }

                    TaskCompletionSource<int> read;
                    bool disposeNow;
                    lock (self._sync) {
                        // Ends the read in flight, and so the engine's loop.
                        self._inboundClosed = true;
                        read = self._bioRead;
                        self._bioRead = null;
                        self._bioReadBuffer = default(Memory<byte>);
                        disposeNow = self._done;
                        notify = self._outbound.Count > 0;
                    }
                    if (read != null) {
                        read.SetResult(0);
                    }
                    if (disposeNow) {
                        self._ssl.Dispose();
                    }
                    return !notify;
                }

                [RubyMethod("init?")]
                public static bool InHandshake(Engine/*!*/ self) {
                    lock (self._sync) {
                        return !self._handshaken;
                    }
                }

                /// <summary>
                /// peercert -> String or nil: the client's certificate, DER; nil with verify_mode none.
                /// </summary>
                [RubyMethod("peercert")]
                public static MutableString GetPeerCertificate(Engine/*!*/ self) {
                    lock (self._sync) {
                        if (!self._verifyPeer || !self._handshaken) {
                            return null;
                        }
                    }
                    var certificate = self._ssl.RemoteCertificate;
                    return certificate != null ? MutableString.CreateBinary(certificate.GetRawCertData()) : null;
                }

                /// <summary>
                /// ssl_vers_st -> [version, state]: SSL_get_version and SSL_state_string, which puma
                /// looks at to tell a plain HTTP request on a TLS port from a TLS 1.3 handshake.
                /// </summary>
                [RubyMethod("ssl_vers_st")]
                public static RubyArray/*!*/ GetVersionState(Engine/*!*/ self) {
                    string version = "TLSv1.3", state;
                    lock (self._sync) {
                        if (self._handshaken) {
#pragma warning disable SYSLIB0039
                            switch (self._ssl.SslProtocol) {
                                case SslProtocols.Tls: version = "TLSv1"; break;
                                case SslProtocols.Tls11: version = "TLSv1.1"; break;
                                case SslProtocols.Tls12: version = "TLSv1.2"; break;
                            }
#pragma warning restore SYSLIB0039
                        }
                        state = self._error != null ? "SSLERR" : self._handshaken ? "SSLOK " : "before";
                    }
                    return new RubyArray { MutableString.CreateMutable(version, RubyEncoding.Binary), MutableString.CreateMutable(state, RubyEncoding.Binary) };
                }

                /// <summary>
                /// The engine's side of the socket: what SslStream reads is what #inject was given,
                /// what it writes waits for #extract.
                /// </summary>
                private sealed class MemoryBio : Stream {
                    private readonly Engine/*!*/ _engine;

                    public MemoryBio(Engine/*!*/ engine) {
                        _engine = engine;
                    }

                    public override bool CanRead { get { return true; } }
                    public override bool CanWrite { get { return true; } }
                    public override bool CanSeek { get { return false; } }
                    public override long Length { get { throw new NotSupportedException(); } }
                    public override long Position {
                        get { throw new NotSupportedException(); }
                        set { throw new NotSupportedException(); }
                    }
                    public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
                    public override void SetLength(long value) { throw new NotSupportedException(); }
                    public override void Flush() { }

                    public override int Read(byte[]/*!*/ buffer, int offset, int count) {
                        return ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();
                    }

                    public override Task<int> ReadAsync(byte[]/*!*/ buffer, int offset, int count, CancellationToken cancellationToken) {
                        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
                    }

                    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default(CancellationToken)) {
                        var engine = _engine;
                        lock (engine._sync) {
                            if (engine._inbound.Count > 0) {
                                return new ValueTask<int>(engine._inbound.Take(buffer.Span));
                            }
                            if (engine._inboundClosed || buffer.Length == 0) {
                                return new ValueTask<int>(0);
                            }
                            // Continuations run elsewhere: not inside #inject, holding the lock of whoever called it.
                            var read = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                            engine._bioRead = read;
                            engine._bioReadBuffer = buffer;
                            Monitor.PulseAll(engine._sync);
                            return new ValueTask<int>(read.Task);
                        }
                    }

                    public override void Write(byte[]/*!*/ buffer, int offset, int count) {
                        lock (_engine._sync) {
                            _engine._outbound.Append(buffer, offset, count);
                        }
                    }

                    public override Task WriteAsync(byte[]/*!*/ buffer, int offset, int count, CancellationToken cancellationToken) {
                        Write(buffer, offset, count);
                        return Task.CompletedTask;
                    }

                    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default(CancellationToken)) {
                        lock (_engine._sync) {
                            _engine._outbound.Append(buffer.Span);
                        }
                        return default(ValueTask);
                    }

                    public override Task FlushAsync(CancellationToken cancellationToken) {
                        return Task.CompletedTask;
                    }
                }
            }

            /// <summary>A FIFO of bytes.</summary>
            private sealed class ByteQueue {
                private byte[]/*!*/ _data = new byte[0];
                private int _start, _count;

                public int Count { get { return _count; } }

                public void Append(byte[]/*!*/ bytes, int offset, int count) {
                    Append(new ReadOnlySpan<byte>(bytes, offset, count));
                }

                public void Append(ReadOnlySpan<byte> bytes) {
                    if (_start + _count + bytes.Length > _data.Length) {
                        if (_count + bytes.Length <= _data.Length) {
                            Buffer.BlockCopy(_data, _start, _data, 0, _count);
                        } else {
                            var data = new byte[Math.Max(_data.Length * 2, _count + bytes.Length)];
                            Buffer.BlockCopy(_data, _start, data, 0, _count);
                            _data = data;
                        }
                        _start = 0;
                    }
                    bytes.CopyTo(new Span<byte>(_data, _start + _count, bytes.Length));
                    _count += bytes.Length;
                }

                public int Take(Span<byte> destination) {
                    int count = Math.Min(_count, destination.Length);
                    new ReadOnlySpan<byte>(_data, _start, count).CopyTo(destination);
                    _start += count;
                    _count -= count;
                    if (_count == 0) {
                        _start = 0;
                    }
                    return count;
                }

                public byte[]/*!*/ TakeAll() {
                    var result = new byte[_count];
                    Take(result);
                    return result;
                }
            }
        }
    }
}
