# Net::HTTP over plain TCP and over TLS against Util/net-http-tls-server.rb: keep-alive
# GETs and POSTs of several sizes, chunked bodies, fresh connections, and a read_timeout
# that has to fire. See Docs/net-http-over-tls.md.
#
#   ruby Util/net-http-tls-server.rb 18080 18443      # CRuby, in another terminal
#   ir.cmd Util/net-http-tls-check.rb 18080 18443 40   # or ./ir.sh
#
# CA=1 verifies against the server's certificate (ca_file) instead of VERIFY_NONE.
# IronRuby 4.0.1: http has no failures; https reports no_timeout (the read_timeout never
# fires), and with CA=1 every connection is refused (ca_file is not consulted).
require "net/http"
require "openssl"
require "tmpdir"
http_port, https_port, rounds = ARGV.map(&:to_i)
rounds = 20 if rounds.zero?
cert = File.join(Dir.tmpdir, "net-http-tls-check-cert.pem")
[[:http, http_port, false], [:https, https_port, true]].each do |label, port, tls|
  fails = Hash.new(0)
  t0 = Time.now
  begin
    Net::HTTP.start("127.0.0.1", port, use_ssl: tls, **(tls ? (ENV["CA"] ? {ca_file: cert} : {verify_mode: OpenSSL::SSL::VERIFY_NONE}) : {}), open_timeout: 5, read_timeout: 10) do |h|
      rounds.times do |i|
        size = [10, 1000, 20_000, 300_000][i % 4]
        path = (i % 3 == 0) ? "/chunked?size=#{size}" : "/data?size=#{size}"
        begin
          r = h.get(path)
          fails[:size] += 1 unless r.body.bytesize == size
          r = h.post("/echo", "y" * size)
          fails[:echo] += 1 unless r.body.bytesize == size
        rescue Exception => e
          fails["#{e.class}: #{e.message[0, 60]}"] += 1
        end
      end
    end
  rescue Exception => e
    fails["start #{e.class}: #{e.message[0, 80]}"] += 1
  end
  # one fresh connection per request too
  rounds.times do
    begin
      Net::HTTP.start("127.0.0.1", port, use_ssl: tls, **(tls ? (ENV["CA"] ? {ca_file: cert} : {verify_mode: OpenSSL::SSL::VERIFY_NONE}) : {}), open_timeout: 5, read_timeout: 10) { |h| h.get("/data?size=5000") }
    rescue Exception => e
      fails["fresh #{e.class}: #{e.message[0, 60]}"] += 1
    end
  end
  # a read timeout must fire
  begin
    Net::HTTP.start("127.0.0.1", port, use_ssl: tls, **(tls ? (ENV["CA"] ? {ca_file: cert} : {verify_mode: OpenSSL::SSL::VERIFY_NONE}) : {}), read_timeout: 1) { |h| h.get("/data?delay=3") }
    fails[:no_timeout] += 1
  rescue Net::ReadTimeout
  rescue Exception => e
    fails["timeout #{e.class}"] += 1
  end
  puts "#{label}: #{rounds} rounds in #{(Time.now - t0).round(1)}s, failures #{fails.inspect}"
end
