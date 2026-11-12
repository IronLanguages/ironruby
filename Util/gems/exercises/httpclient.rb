require 'httpclient'
require 'webrick'

# A real request over a loopback WEBrick, which is what this gem is for.
server = WEBrick::HTTPServer.new(
  BindAddress: '127.0.0.1', Port: 0,
  Logger: WEBrick::Log.new(File::NULL), AccessLog: []
)
server.mount_proc('/ok') { |_req, res| res.status = 200; res['Content-Type'] = 'text/plain'; res.body = 'hello' }
server.mount_proc('/echo') { |req, res| res.status = 201; res.body = "#{req.request_method}:#{req.body}" }
server.mount_proc('/redirect') { |_req, res| res.status = 302; res['Location'] = '/ok' }
server.mount_proc('/boom') { |_req, res| res.status = 500; res.body = 'server error' }
Thread.new { server.start }
port = server.listeners.first.addr[1]
base = "http://127.0.0.1:#{port}"

c = HTTPClient.new
r = c.get("#{base}/ok")
puts r.status, r.body, r.headers['Content-Type']

r = c.post("#{base}/echo", 'a=1')
puts r.status, r.body

r = c.get("#{base}/redirect")
puts r.status, URI(r.headers['Location']).path  # the port is random
puts c.get_content("#{base}/redirect")

r = c.get("#{base}/boom")
puts r.status, r.ok?

puts c.get("#{base}/ok", query: { 'q' => '1' }).status
puts c.head("#{base}/ok").status

begin
  c.get('http://127.0.0.1:1/never', follow_redirect: false)
rescue StandardError => e
  puts e.class.ancestors.include?(StandardError)
end

puts HTTPClient::VERSION.split('.').first
server.shutdown
