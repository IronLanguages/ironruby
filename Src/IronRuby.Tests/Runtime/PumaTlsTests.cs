/* ****************************************************************************
 *
 * Copyright (c) IronRuby contributors.
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

namespace IronRuby.Tests {

    public partial class Tests {
        /// <summary>
        /// puma serving TLS in single mode, on Puma::MiniSSL::Engine (Src/Libraries/Puma/MiniSsl.cs):
        /// an ssl:// bind with cert and key files, an ssl_bind with cert_pem and key_pem, and one
        /// with verify_mode=force_peer and a ca. Net::HTTP talks to the first two with ca_file,
        /// keep-alive and a large POST; an SSLSocket pipelines two requests; plain HTTP on the TLS
        /// port is turned away and puma serves on. The client certificates are presented by
        /// PumaTlsClient (.NET's SslStream), as IronRuby's SSLSocket has no client certificates.
        /// </summary>
        public void Puma_Tls1() {
            using (var files = new PumaTlsFiles()) {
                Runtime.Globals.SetVariable("MtlsClient", new PumaTlsClient(files.ClientPfx, files.RoguePfx));
                TestOutput(@"
require 'ruby4'
require 'net/http'
require 'openssl'
require 'stringio'
require 'puma'
require 'puma/configuration'
require 'puma/launcher'
require 'puma/log_writer'

PORTS = " + files.RubyPorts + @"
CERT = '" + files.Cert + @"'
KEY = '" + files.Key + @"'
CA = '" + files.Ca + @"'

p Puma.ssl?

app = lambda do |env|
  body = env['rack.input'].read
  cert = env['puma.peercert']
  text = env['PATH_INFO'] == '/big' ? 'x' * 200_000 :
    [env['REQUEST_METHOD'], env['PATH_INFO'], body.bytesize, env['HTTPS'], cert && cert.subject].compact.join(' ')
  [200, { 'content-type' => 'text/plain' }, [text]]
end

conf = Puma::Configuration.new do |c|
  c.bind ""ssl://127.0.0.1:#{PORTS[0]}?cert=#{CERT}&key=#{KEY}""
  c.ssl_bind '127.0.0.1', PORTS[1], cert_pem: File.read(CERT), key_pem: File.read(KEY)
  c.bind ""ssl://127.0.0.1:#{PORTS[2]}?cert=#{CERT}&key=#{KEY}&ca=#{CA}&verify_mode=force_peer""
  c.app app
  c.threads 1, 4
  c.workers 0
end
$0 ||= 'puma' # the launcher looks for the script it was started from, and for Gem.ruby
require 'rubygems'
log = StringIO.new
launcher = Puma::Launcher.new(conf, log_writer: Puma::LogWriter.new(log, log))
server = Thread.new { launcher.run }
100.times do
  begin
    TCPSocket.new('127.0.0.1', PORTS[2]).close
    break
  rescue SystemCallError
    sleep 0.1
  end
end

def http(port)
  h = Net::HTTP.new('127.0.0.1', port)
  h.use_ssl = true
  h.ca_file = CERT
  h.max_retries = 0
  h
end

# keep-alive: four requests on one connection
http(PORTS[0]).start do |h|
  puts h.get('/a').body
  puts h.post('/b', 'y' * 300_000).body
  p h.get('/big').body.bytesize
  puts h.get('/c').body
end
puts http(PORTS[1]).start { |h| h.get('/pem').body }

# two requests in one TLS record
ctx = OpenSSL::SSL::SSLContext.new
ctx.set_params(ca_file: CERT)
s = OpenSSL::SSL::SSLSocket.new(TCPSocket.new('127.0.0.1', PORTS[0]), ctx)
s.sync_close = true
s.connect
s.write ""GET /one HTTP/1.1\r\nHost: x\r\n\r\nGET /two HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n""
p s.read.scan(/GET \S+/)
s.close

# plain HTTP on the TLS port
c = TCPSocket.new('127.0.0.1', PORTS[0])
c.write ""GET / HTTP/1.1\r\nHost: x\r\n\r\n""
answer = begin; c.read; rescue SystemCallError; ''; end
p answer.include?('HTTP')
c.close
puts http(PORTS[0]).start { |h| h.get('/after').body }

# force_peer: only a certificate the ca issued gets in
puts MtlsClient.Get(PORTS[2], '/issued', 'client')
puts MtlsClient.Get(PORTS[2], '/rogue', 'rogue')
puts MtlsClient.Get(PORTS[2], '/none', nil)

launcher.stop
server.join
p log.string.include?('SSL error')
", @"
true
GET /a 0 https
POST /b 300000 https
200000
GET /c 0 https
GET /pem 0 https
[""GET /one"", ""GET /two""]
false
GET /after 0 https
GET /issued 0 https /CN=test client
refused
refused
true
");
            }
        }

        /// <summary>
        /// A server certificate for 127.0.0.1 and its key as PEM files, a CA (PEM) and a client
        /// certificate it issued, a self-signed client certificate (both PFX), and three free ports.
        /// </summary>
        private sealed class PumaTlsFiles : IDisposable {
            private static readonly Oid ClientAuthentication = new Oid("1.3.6.1.5.5.7.3.2");
            private readonly List<string> _paths = new List<string>();
            private readonly int[] _ports = new int[3];

            public readonly string Cert, Key, Ca, ClientPfx, RoguePfx;

            public PumaTlsFiles() {
                var now = DateTimeOffset.UtcNow;
                using (var key = RSA.Create(2048)) {
                    var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    var names = new SubjectAlternativeNameBuilder();
                    names.AddIpAddress(IPAddress.Loopback);
                    request.CertificateExtensions.Add(names.Build());
                    using (var cert = request.CreateSelfSigned(now.AddMinutes(-5), now.AddDays(1))) {
                        Cert = Write(".pem", cert.ExportCertificatePem() + "\n");
                        Key = Write(".pem", key.ExportPkcs8PrivateKeyPem() + "\n");
                    }
                }

                using (var caKey = RSA.Create(2048)) {
                    var request = new CertificateRequest("CN=Test Client CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
                    request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
                    using (var ca = request.CreateSelfSigned(now.AddMinutes(-5), now.AddDays(1))) {
                        Ca = Write(".pem", ca.ExportCertificatePem() + "\n");
                        ClientPfx = WritePfx(ClientCertificate("CN=test client", ca));
                    }
                }
                RoguePfx = WritePfx(ClientCertificate("CN=rogue client", null));

                for (int i = 0; i < _ports.Length; i++) {
                    var listener = new TcpListener(IPAddress.Loopback, 0);
                    listener.Start();
                    _ports[i] = ((IPEndPoint)listener.LocalEndpoint).Port;
                    listener.Stop();
                }
            }

            public string/*!*/ RubyPorts {
                get { return "[" + String.Join(", ", _ports) + "]"; }
            }

            // Issued by ca, or self-signed without one.
            private static X509Certificate2/*!*/ ClientCertificate(string/*!*/ subject, X509Certificate2 ca) {
                using (var key = RSA.Create(2048)) {
                    var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { ClientAuthentication }, false));
                    var now = DateTimeOffset.UtcNow;
                    if (ca == null) {
                        return request.CreateSelfSigned(now.AddMinutes(-5), now.AddDays(1));
                    }
                    var serial = new byte[8];
                    RandomNumberGenerator.Fill(serial);
                    serial[0] &= 0x7f;
                    using (var issued = request.Create(ca, now.AddMinutes(-5), now.AddHours(12), serial)) {
                        return issued.CopyWithPrivateKey(key);
                    }
                }
            }

            private string/*!*/ Write(string/*!*/ extension, string/*!*/ content) {
                string path = Path.Combine(Path.GetTempPath(), "ironruby-puma-tls-" + Guid.NewGuid().ToString("N") + extension);
                File.WriteAllText(path, content);
                _paths.Add(path);
                return path.Replace("\\", "/");
            }

            private string/*!*/ WritePfx(X509Certificate2/*!*/ certificate) {
                using (certificate) {
                    string path = Path.Combine(Path.GetTempPath(), "ironruby-puma-tls-" + Guid.NewGuid().ToString("N") + ".pfx");
                    File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx));
                    _paths.Add(path);
                    return path;
                }
            }

            public void Dispose() {
                foreach (var path in _paths) {
                    try {
                        File.Delete(path);
                    } catch (IOException) {
                    }
                }
            }
        }

        /// <summary>
        /// A TLS client with a client certificate: GET path, and the response body, or "refused".
        /// </summary>
        public sealed class PumaTlsClient {
            private readonly Dictionary<string, string> _pfx = new Dictionary<string, string>();

            internal PumaTlsClient(string/*!*/ client, string/*!*/ rogue) {
                _pfx["client"] = client;
                _pfx["rogue"] = rogue;
            }

            public string/*!*/ Get(int port, string/*!*/ path, string certificate) {
                var certificates = new X509CertificateCollection();
                if (certificate != null) {
#if NET9_0_OR_GREATER
                    certificates.Add(X509CertificateLoader.LoadPkcs12FromFile(_pfx[certificate], null));
#else
                    certificates.Add(new X509Certificate2(_pfx[certificate]));
#endif
                }
                try {
                    using (var tcp = new TcpClient("127.0.0.1", port))
                    using (var ssl = new SslStream(tcp.GetStream(), false, (sender, cert, chain, errors) => true)) {
                        ssl.ReadTimeout = ssl.WriteTimeout = 10000;
                        ssl.AuthenticateAsClient("127.0.0.1", certificates, false);
                        var request = Encoding.ASCII.GetBytes("GET " + path + " HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");
                        ssl.Write(request, 0, request.Length);
                        var response = new MemoryStream();
                        ssl.CopyTo(response);
                        string text = Encoding.ASCII.GetString(response.ToArray());
                        int body = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                        return body >= 0 ? text.Substring(body + 4) : "refused";
                    }
                } catch (Exception e) when (e is IOException || e is SocketException || e is System.Security.Authentication.AuthenticationException) {
                    return "refused";
                }
            }
        }
    }
}
