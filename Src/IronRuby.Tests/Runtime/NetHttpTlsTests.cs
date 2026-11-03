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
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;

namespace IronRuby.Tests {

    public partial class Tests {
        // What every script below starts with: net/http (and openssl), which load without a
        // warning, and the server's ports and CA files.
        private static string NetHttpPrelude(TlsTestServer/*!*/ server, bool openssl) {
            return @"
require 'ruby4'
require 'net/http'
" + (openssl ? "require 'openssl'\n" : "") + @"
PORTS = " + server.RubyPorts + @"
CA = " + server.RubyCertificatePaths + @"

def http(**opts)
  h = Net::HTTP.new('127.0.0.1', PORTS[opts.delete(:at) || :self])
  h.use_ssl = true
  h.max_retries = 0
  opts.each { |name, value| h.__send__(""#{name}="", value) }
  h
end
";
        }

        /// <summary>
        /// Net::HTTP over TLS against a server in this process (TlsTestServer below), so that no
        /// CRuby is needed: ca_file and cert_store decide what is trusted, and the read, write and
        /// open (handshake) timeouts fire against a server that stops answering. IronRuby's
        /// SSLSocket is a blocking SslStream; before Docs/net-http-over-tls.md stage 1 none of
        /// those timeouts fired and only the system trust store counted. net/http needs prism.
        /// </summary>
        public void NetHttp_Tls1() {
            using (var server = new TlsTestServer()) {
                TestOutput(NetHttpPrelude(server, true) + @"
# ca_file: keep-alive requests both ways
http(ca_file: CA[:self]).start do |h|
  p h.get('/data?size=100000').body.bytesize
  body = 'y' * 300_000
  p h.post('/echo', body).body == body
end

# cert_store; a leaf issued by a CA in ca_file
store = OpenSSL::X509::Store.new
store.add_file(CA[:self])
p http(cert_store: store).start { |h| h.get('/data?size=3').body }
p http(at: :issued, ca_file: CA[:ca]).start { |h| h.get('/data?size=2').body }

# the system store knows neither; a certificate for client authentication is no server's
[[:self, nil], [:client_auth, CA[:ca]]].each do |at, ca|
  begin
    http(at: at, ca_file: ca).start { |h| h.get('/data?size=3') }
  rescue OpenSSL::SSL::SSLError
    p [at, :refused]
  end
end

# read_timeout: the server never answers
t = Time.now
begin
  http(ca_file: CA[:self], read_timeout: 1).start { |h| h.get('/stall') }
rescue Net::ReadTimeout
  p [:read_timeout, Time.now - t < 5]
end

# write_timeout: the server never reads the body
t = Time.now
begin
  http(ca_file: CA[:self], write_timeout: 1).start { |h| h.post('/sink', 'z' * 64_000_000) }
rescue Net::WriteTimeout
  p [:write_timeout, Time.now - t < 10]
end

# open_timeout: the server accepts the connection but never answers the handshake
t = Time.now
begin
  http(at: :stall, ca_file: CA[:self], open_timeout: 1).start { |h| h.get('/') }
rescue Net::OpenTimeout
  p [:open_timeout, Time.now - t < 5]
end

# Timeout.timeout around the same handshake: its own exception, not an SSLError
t = Time.now
begin
  Timeout.timeout(1) { http(at: :stall, ca_file: CA[:self]).start { |h| h.get('/') } }
rescue Exception => e
  p [e.class, Time.now - t < 5]
end

# a refused handshake stays refused: a second connect does not call the socket connected
ctx = OpenSSL::SSL::SSLContext.new
ctx.set_params({})
s = OpenSSL::SSL::SSLSocket.new(TCPSocket.new('127.0.0.1', PORTS[:self]), ctx)
2.times do
  begin
    s.connect
    p :connected
  rescue OpenSSL::SSL::SSLError
    p :refused
  end
end
s.close

# the socket itself: the name check, and no use after a timeout
ctx = OpenSSL::SSL::SSLContext.new
ctx.set_params(ca_file: CA[:self])
s = OpenSSL::SSL::SSLSocket.new(TCPSocket.new('127.0.0.1', PORTS[:self]), ctx)
s.sync_close = true
s.connect
p s.post_connection_check('127.0.0.1')
['example.com', ''].each do |name|
  begin
    s.post_connection_check(name)
  rescue OpenSSL::SSL::SSLError
    p [:name_mismatch, name]
  end
end
s.write ""GET /stall HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n""
s.__read_timeout = 0.5
begin
  s.sysread(10)
rescue IO::TimeoutError
  p :io_timeout
end
begin
  s.sysread(10)
rescue OpenSSL::SSL::SSLError => e
  p e.message.include?('unusable')
end
s.close

# a certificate with no subjectAltName is checked on its common name, an IP address too
ctx = OpenSSL::SSL::SSLContext.new
ctx.set_params(ca_file: CA[:common_name])
s = OpenSSL::SSL::SSLSocket.new(TCPSocket.new('127.0.0.1', PORTS[:common_name]), ctx)
s.connect
p s.post_connection_check('10.0.0.5')
begin
  s.post_connection_check('10.0.0.6')
rescue OpenSSL::SSL::SSLError
  p :cn_mismatch
end
s.close
", @"
100000
true
""xxx""
""xx""
[:self, :refused]
[:client_auth, :refused]
[:read_timeout, true]
[:write_timeout, true]
[:open_timeout, true]
[Timeout::Error, true]
:refused
:refused
true
[:name_mismatch, ""example.com""]
[:name_mismatch, """"]
:io_timeout
true
true
:cn_mismatch
");
            }
        }

        /// <summary>
        /// SSLContext#verify_callback over TLS, as CRuby's ossl_verify_cb_call has it: one call per
        /// certificate and error, root first; only true accepts (and clears the error, so
        /// verify_result is V_OK); the first rejection ends the check; an exception in the callback
        /// is a rejection with a warning. The error codes are OpenSSL's: 18 for a lone untrusted
        /// self-signed certificate, 10 expired, 9 not yet valid, 26 the wrong purpose.
        /// </summary>
        public void NetHttp_TlsVerifyCallback1() {
            using (var server = new TlsTestServer()) {
                TestOutput(NetHttpPrelude(server, true) + @"
require 'stringio'

def verify(at, ca = nil, &callback)
  seen = []
  cb = lambda { |ok, ctx| seen << [ok, ctx.error_depth, ctx.error]; callback.call(ok, ctx) }
  result = begin
    http(at: at, ca_file: ca, verify_callback: cb).start { |h| h.get('/data?size=1').body }
  rescue OpenSSL::SSL::SSLError
    :refused
  end
  [result, seen]
end

# the callback overrides the system store, and sees the certificate's subject
subjects = []
result = verify(:self) { |ok, ctx| subjects << ctx.current_cert.subject.to_s; true }
p [result, subjects]
# a chain, root first; true for both
p verify(:issued, CA[:ca]) { |ok, ctx| ok }
# every error is reported: accepting 18 does not accept the validity period as well
[:expired, :future].each do |at|
  result, seen = verify(at) { |ok, ctx| ok || ctx.error == 18 }
  p [result, seen.map(&:last)]
end
result, seen = verify(:client_auth, CA[:ca]) { |ok, ctx| ok }
p [result, seen.map(&:last)]
# only true accepts; the first rejection ends it
p verify(:issued, CA[:ca]) { |ok, ctx| 1 }
p verify(:issued, CA[:ca]) { |ok, ctx| false }
# an exception is a rejection, with a warning
err, $stderr = $stderr, StringIO.new
p verify(:issued, CA[:ca]) { |ok, ctx| raise 'boom' }
warning, $stderr = $stderr.string, err
p warning.include?('exception in verify_callback is ignored')

# accepting an error clears it
ctx = OpenSSL::SSL::SSLContext.new
ctx.set_params(verify_callback: lambda { |ok, c| true })
s = OpenSSL::SSL::SSLSocket.new(TCPSocket.new('127.0.0.1', PORTS[:self]), ctx)
s.connect
p s.verify_result
s.close
", @"
[[""x"", [[false, 0, 18]]], [""/CN=127.0.0.1""]]
[""x"", [[true, 1, 0], [true, 0, 0]]]
[:refused, [18, 10]]
[:refused, [18, 9]]
[:refused, [0, 26]]
[:refused, [[true, 1, 0]]]
[:refused, [[true, 1, 0]]]
[:refused, [[true, 1, 0]]]
true
0
");
            }
        }

        /// <summary>
        /// Non-blocking TLS, the way an event loop drives it: connect_nonblock, read_nonblock and
        /// write_nonblock answer :wait_readable / :wait_writable while SslStream's operation runs,
        /// and IO.select on #to_io and NIO::Selector see it finish. Bytes SslStream has already
        /// decrypted make the socket readable although the TCP socket under it is empty.
        /// </summary>
        public void Ssl_NonBlocking1() {
            using (var server = new TlsTestServer()) {
                TestOutput(@"
require 'ruby4'
require 'socket'
require 'openssl'
# nio.rb asks RubyGems what platform this is; the test runtime has no RubyGems
module Gem
  def self.win_platform?
    File::ALT_SEPARATOR == '\\'
  end
end unless defined?(Gem)
require 'nio'
PORTS = " + server.RubyPorts + @"
CA = " + server.RubyCertificatePaths + @"

def tls(at = :self)
  ctx = OpenSSL::SSL::SSLContext.new
  ctx.set_params(ca_file: CA[:self])
  OpenSSL::SSL::SSLSocket.new(TCPSocket.new('127.0.0.1', PORTS[at]), ctx)
end

def read_response(s)
  buf = ''.b
  until (head = buf.index(""\r\n\r\n"")) && buf.bytesize >= head + 4 + buf[/Content-Length: (\d+)/, 1].to_i
    case data = s.read_nonblock(4096, exception: false)
    when :wait_readable then IO.select([s.to_io], nil, nil, 5) or raise 'read stalled'
    else buf << data
    end
  end
  buf[(head + 4)..-1]
end

s = tls
until (r = s.connect_nonblock(exception: false)) == s
  raise r.inspect unless r == :wait_readable
  IO.select([s.to_io], nil, nil, 5) or raise 'handshake stalled'
end
p [s.state, s.post_connection_check('127.0.0.1')]

# nothing to read yet
p s.read_nonblock(10, exception: false)
begin
  s.read_nonblock(10)
rescue OpenSSL::SSL::SSLErrorWaitReadable => e
  p [e.is_a?(IO::WaitReadable), e.message]
end
p IO.select([s.to_io], nil, nil, 0.2)

s.write ""GET /data?size=100000 HTTP/1.1\r\nHost: x\r\n\r\n""
p read_response(s).bytesize

# one byte of a response: the rest is decrypted and waiting, and select says so
s.write ""GET /data?size=10 HTTP/1.1\r\nHost: x\r\n\r\n""
IO.select([s.to_io], nil, nil, 5)
first = s.read_nonblock(1)
p [first, s.pending > 0, IO.select([s.to_io], nil, nil, 0) ? :readable : :not_readable]
read_response(s) rescue nil
s.read_nonblock(4096, exception: false) while s.pending > 0

# NIO::Selector over the TLS socket
selector = NIO::Selector.new
monitor = selector.register(s.to_io, :r)
p selector.select(0.2)
s.write_nonblock(""GET /data?size=7 HTTP/1.1\r\nHost: x\r\n\r\n"")
ready = selector.select(5)
p [ready.size, monitor.readable?]
selector.deregister(s.to_io)
s.close

# a server that reads nothing: write_nonblock runs into :wait_writable, and select agrees
s = tls
s.connect
s.write ""POST /sink HTTP/1.1\r\nHost: x\r\nContent-Length: 100000000\r\n\r\n""
chunk = 'z' * 65536
written = 0
result = nil
2000.times do
  result = s.write_nonblock(chunk, exception: false)
  if result == :wait_writable
    # A write in flight is not a full buffer (on Windows one is in flight almost always):
    # only a wait that runs out means the peer stopped reading.
    break unless IO.select(nil, [s.to_io], nil, 0.5)
    next
  end
  written += result
end
p [result, written > 0, written <= 16384 * 2000]
p IO.select(nil, [s.to_io], nil, 0.2)
begin
  s.write_nonblock(chunk)
rescue OpenSSL::SSL::SSLErrorWaitWritable => e
  p e.is_a?(IO::WaitWritable)
end
s.close
", @"
[""SSLOK "", true]
:wait_readable
[true, ""read would block""]
nil
100000
[""H"", true, :readable]
nil
[1, true]
[:wait_writable, true, true]
nil
true
");
            }
        }

        /// <summary>
        /// RubyGems' vendored copy of net-http and net-protocol (Gem::Net, what `gem install`
        /// fetches with) gets the same timeouts over TLS as the standard library's, and raises
        /// its own Gem::Net errors. Nothing of it is patched: SSLSocket's non-blocking calls are
        /// what upstream's timeouts wait on.
        /// </summary>
        public void NetHttp_GemNetTls1() {
            using (var server = new TlsTestServer()) {
                TestOutput(@"
require 'ruby4'
module Gem; end unless defined?(Gem)
require 'rubygems/vendored_net_http'
require 'openssl'
PORTS = " + server.RubyPorts + @"
CA = " + server.RubyCertificatePaths + @"

def gem_http(at, **opts)
  h = Gem::Net::HTTP.new('127.0.0.1', PORTS[at])
  h.use_ssl = true
  h.ca_file = CA[:self]
  h.max_retries = 0
  opts.each { |name, value| h.__send__(""#{name}="", value) }
  h
end

p gem_http(:self).start { |h| h.get('/data?size=5').body }
t = Time.now
begin
  gem_http(:self, read_timeout: 1).start { |h| h.get('/stall') }
rescue Gem::Net::ReadTimeout
  p [:read_timeout, Time.now - t < 5]
end
t = Time.now
begin
  gem_http(:stall, open_timeout: 1).start { |h| h.get('/') }
rescue Gem::Net::OpenTimeout
  p [:open_timeout, Time.now - t < 5]
end
p defined?(::Net::HTTP)
", @"
""xxxxx""
[:read_timeout, true]
[:open_timeout, true]
nil
");
            }
        }

        /// <summary>
        /// A plain HTTP request does not load openssl: IronRuby's net/protocol checks for its own
        /// SSLSocket without naming OpenSSL, which would run net/http's autoload.
        /// </summary>
        public void NetHttp_PlainNoOpenSsl1() {
            using (var server = new TlsTestServer()) {
                TestOutput(NetHttpPrelude(server, false) + @"
h = Net::HTTP.new('127.0.0.1', PORTS[:plain])
p h.start { |c| [c.get('/data?size=4').body, c.post('/echo', 'abc').body] }
p Object.autoload?(:OpenSSL)
", @"
[""xxxx"", ""abc""]
""openssl""
");
            }
        }

        /// <summary>
        /// HTTPS on loopback ports, one per certificate, with throwaway certificates whose PEM is
        /// written to temporary files:
        ///   self         self-signed for 127.0.0.1 (subjectAltName IP), its own CA
        ///   issued       issued for 127.0.0.1 by the CA "ca", for server authentication
        ///   client_auth  issued for 127.0.0.1 by "ca", for client authentication only
        ///   expired      self-signed for 127.0.0.1, expired yesterday
        ///   future       self-signed for 127.0.0.1, valid from tomorrow
        ///   common_name  self-signed, CN=10.0.0.5 and no subjectAltName, its own CA
        /// plain is the same without TLS, and stall accepts connections and never says a word.
        /// GET /data?size=N answers N bytes, POST /echo sends the body back, /stall and /sink
        /// answer nothing and read nothing more.
        /// </summary>
        private sealed class TlsTestServer : IDisposable {
            private static readonly Oid ServerAuthentication = new Oid("1.3.6.1.5.5.7.3.1");
            private static readonly Oid ClientAuthentication = new Oid("1.3.6.1.5.5.7.3.2");

            private readonly Dictionary<string, TcpListener> _listeners = new Dictionary<string, TcpListener>();
            private readonly Dictionary<string, string> _pems = new Dictionary<string, string>();
            private readonly List<X509Certificate2> _certificates = new List<X509Certificate2>();
            private readonly List<TcpClient> _clients = new List<TcpClient>();
            private readonly ManualResetEvent _stop = new ManualResetEvent(false);
            private volatile bool _disposed;

            public TlsTestServer() {
                var now = DateTimeOffset.UtcNow;
                using (var ca = SelfSigned("CN=IronRuby Test CA", true, null, now.AddMinutes(-5), now.AddDays(1), true)) {
                    WritePem("ca", ca);
                    Listen("self", Keep(SelfSigned("CN=127.0.0.1", true, null, now.AddMinutes(-5), now.AddDays(1), false)), true);
                    Listen("issued", Keep(Issued(ca, ServerAuthentication)));
                    Listen("client_auth", Keep(Issued(ca, ClientAuthentication)));
                    Listen("expired", Keep(SelfSigned("CN=127.0.0.1", true, null, now.AddDays(-2), now.AddDays(-1), false)));
                    Listen("future", Keep(SelfSigned("CN=127.0.0.1", true, null, now.AddDays(1), now.AddDays(2), false)));
                    Listen("common_name", Keep(SelfSigned("CN=10.0.0.5", false, null, now.AddMinutes(-5), now.AddDays(1), false)), true);
                }
                Listen("plain", null);
                Listen("stall", null);
            }

            // { self: 1234, ... } for the Ruby side.
            public string/*!*/ RubyPorts {
                get {
                    var result = new StringBuilder("{ ");
                    foreach (var entry in _listeners) {
                        result.Append(entry.Key).Append(": ").Append(((IPEndPoint)entry.Value.LocalEndpoint).Port).Append(", ");
                    }
                    return result.Append("}").ToString();
                }
            }

            public string/*!*/ RubyCertificatePaths {
                get {
                    var result = new StringBuilder("{ ");
                    foreach (var entry in _pems) {
                        result.Append(entry.Key).Append(": '").Append(entry.Value.Replace("\\", "/")).Append("', ");
                    }
                    return result.Append("}").ToString();
                }
            }

            private static CertificateRequest/*!*/ Request(string/*!*/ subject, RSA/*!*/ key, bool ipAddress, Oid usage, bool ca) {
                var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                if (ipAddress) {
                    var names = new SubjectAlternativeNameBuilder();
                    names.AddIpAddress(IPAddress.Loopback);
                    request.CertificateExtensions.Add(names.Build());
                }
                if (usage != null) {
                    request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { usage }, false));
                }
                if (ca) {
                    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
                    request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
                }
                return request;
            }

            private static X509Certificate2/*!*/ SelfSigned(string/*!*/ subject, bool ipAddress, Oid usage,
                DateTimeOffset notBefore, DateTimeOffset notAfter, bool ca) {
                using (var key = RSA.Create(2048)) {
                    return Request(subject, key, ipAddress, usage, ca).CreateSelfSigned(notBefore, notAfter);
                }
            }

            private static X509Certificate2/*!*/ Issued(X509Certificate2/*!*/ ca, Oid usage) {
                using (var key = RSA.Create(2048)) {
                    var serial = new byte[8];
                    RandomNumberGenerator.Fill(serial);
                    serial[0] &= 0x7f;
                    using (var issued = Request("CN=127.0.0.1", key, true, usage, false).Create(ca, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(12), serial)) {
                        return issued.CopyWithPrivateKey(key);
                    }
                }
            }

            // SChannel cannot serve with an ephemeral key: round-trip the certificate through PFX.
            private X509Certificate2/*!*/ Keep(X509Certificate2/*!*/ created) {
                using (created) {
#if NET9_0_OR_GREATER
                    var result = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
#else
                    var result = new X509Certificate2(created.Export(X509ContentType.Pfx));
#endif
                    _certificates.Add(result);
                    return result;
                }
            }

            private void WritePem(string/*!*/ name, X509Certificate2/*!*/ certificate) {
                string path = Path.Combine(Path.GetTempPath(), "ironruby-tls-test-" + Guid.NewGuid().ToString("N") + ".pem");
                File.WriteAllText(path, certificate.ExportCertificatePem() + "\n");
                _pems[name] = path;
            }

            private void Listen(string/*!*/ name, X509Certificate2 certificate, bool writePem = false) {
                if (writePem) {
                    WritePem(name, certificate);
                }
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                _listeners[name] = listener;
                Action<TcpClient> serve;
                if (name == "stall") {
                    serve = client => _stop.WaitOne();
                } else if (certificate == null) {
                    serve = client => Serve(client.GetStream());
                } else {
                    serve = client => {
                        using (var ssl = new SslStream(client.GetStream(), false)) {
                            ssl.AuthenticateAsServer(certificate);
                            Serve(ssl);
                        }
                    };
                }
                Start(() => Accept(listener, serve));
            }

            private static void Start(ThreadStart body) {
                new Thread(body) { IsBackground = true }.Start();
            }

            private void Accept(TcpListener/*!*/ listener, Action<TcpClient>/*!*/ serve) {
                while (!_disposed) {
                    TcpClient client;
                    try {
                        client = listener.AcceptTcpClient();
                    } catch (Exception) {
                        return;
                    }
                    lock (_clients) {
                        _clients.Add(client);
                    }
                    Start(() => {
                        try {
                            serve(client);
                        } catch (Exception) {
                            // the client went away, refused the certificate, or the test is over
                        } finally {
                            client.Dispose();
                        }
                    });
                }
            }

            private void Serve(Stream/*!*/ stream) {
                while (true) {
                    string head = ReadHead(stream);
                    if (head == null) {
                        return;
                    }
                    string path = head.Split(' ')[1];
                    if (path == "/stall" || path == "/sink") {
                        // answer nothing and read nothing until the client gives up
                        _stop.WaitOne();
                        return;
                    }

                    byte[] body;
                    if (path == "/echo") {
                        body = ReadExactly(stream, ContentLength(head));
                    } else {
                        int size = Int32.Parse(path.Substring(path.IndexOf("size=") + 5));
                        body = new byte[size];
                        for (int i = 0; i < size; i++) {
                            body[i] = (byte)'x';
                        }
                    }
                    byte[] header = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: " + body.Length + "\r\n\r\n");
                    stream.Write(header, 0, header.Length);
                    stream.Write(body, 0, body.Length);
                    stream.Flush();
                }
            }

            // The request line and headers, or null when the client closed the connection.
            private static string ReadHead(Stream/*!*/ stream) {
                var head = new StringBuilder();
                var one = new byte[1];
                while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal)) {
                    if (stream.Read(one, 0, 1) == 0) {
                        return null;
                    }
                    head.Append((char)one[0]);
                }
                return head.ToString();
            }

            private static int ContentLength(string/*!*/ head) {
                foreach (string line in head.Split(new[] { "\r\n" }, StringSplitOptions.None)) {
                    if (line.StartsWith("content-length:", StringComparison.OrdinalIgnoreCase)) {
                        return Int32.Parse(line.Substring(15).Trim());
                    }
                }
                return 0;
            }

            private static byte[]/*!*/ ReadExactly(Stream/*!*/ stream, int length) {
                var result = new byte[length];
                int offset = 0;
                while (offset < length) {
                    int read = stream.Read(result, offset, length - offset);
                    if (read == 0) {
                        throw new EndOfStreamException();
                    }
                    offset += read;
                }
                return result;
            }

            public void Dispose() {
                _disposed = true;
                _stop.Set();
                foreach (var listener in _listeners.Values) {
                    listener.Stop();
                }
                lock (_clients) {
                    foreach (var client in _clients) {
                        client.Dispose();
                    }
                }
                foreach (var certificate in _certificates) {
                    certificate.Dispose();
                }
                foreach (var path in _pems.Values) {
                    try {
                        File.Delete(path);
                    } catch (IOException) {
                    }
                }
            }
        }
    }
}
