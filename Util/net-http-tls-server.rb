# Test server for Util/net-http-tls-check.rb: HTTP/1.1 on one port, HTTPS on another,
# with a throwaway self-signed certificate for 127.0.0.1 written to Dir.tmpdir
# (net-http-tls-check-cert.pem). Run it with CRuby: IronRuby has no SSLServer.
#
#   ruby Util/net-http-tls-server.rb 18080 18443
#
# GET /data?size=N[&delay=S] answers N bytes after S seconds; /chunked?size=N the same,
# chunked; POST /echo sends the body back.
require "socket"
require "openssl"
require "tmpdir"

http_port, https_port = ARGV.map(&:to_i)

key = OpenSSL::PKey::RSA.new(2048)
cert = OpenSSL::X509::Certificate.new
cert.version = 2
cert.serial = 1
cert.subject = cert.issuer = OpenSSL::X509::Name.parse("/CN=127.0.0.1")
cert.public_key = key.public_key
cert.not_before = Time.now - 60
cert.not_after = Time.now + 86400
ef = OpenSSL::X509::ExtensionFactory.new(cert, cert)
cert.add_extension(ef.create_extension("subjectAltName", "IP:127.0.0.1"))
cert.sign(key, OpenSSL::Digest::SHA256.new)
File.write(File.join(Dir.tmpdir, "net-http-tls-check-cert.pem"), cert.to_pem)

def serve(sock)
  loop do
    request = sock.gets("\r\n\r\n") or break
    path = request[/\A\S+ (\S+)/, 1]
    length = request[/^content-length: *(\d+)/i, 1].to_i
    body_in = length > 0 ? sock.read(length) : ""
    size = path[/size=(\d+)/, 1].to_i
    sleep(path[/delay=([\d.]+)/, 1].to_f)
    body = path.include?("echo") ? body_in : ("x" * (size > 0 ? size : 100))
    if path.include?("chunked")
      sock.write "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nTransfer-Encoding: chunked\r\n\r\n"
      body.scan(/.{1,1000}/m) { |c| sock.write "#{c.bytesize.to_s(16)}\r\n#{c}\r\n"; sock.flush }
      sock.write "0\r\n\r\n"
    else
      sock.write "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: #{body.bytesize}\r\n\r\n#{body}"
    end
    sock.flush
    break if request =~ /^connection: *close/i
  end
rescue StandardError
ensure
  sock.close rescue nil
end

ctx = OpenSSL::SSL::SSLContext.new
ctx.cert = cert
ctx.key = key
plain = TCPServer.new("127.0.0.1", http_port)
tls = TCPServer.new("127.0.0.1", https_port)
ssl = OpenSSL::SSL::SSLServer.new(tls, ctx)
ssl.start_immediately = true
$stdout.puts "listening #{http_port} #{https_port}"; $stdout.flush
Thread.new { loop { s = plain.accept; Thread.new(s) { |c| serve(c) } } }
loop do
  begin
    s = ssl.accept
    Thread.new(s) { |c| serve(c) }
  rescue StandardError
  end
end
