require 'webmock'
require 'net/http'
require 'uri'

include WebMock::API
WebMock.enable!
WebMock.disable_net_connect!

stub_request(:get, 'https://example.test/ok')
  .to_return(status: 200, body: 'hello', headers: { 'Content-Type' => 'text/plain' })
stub_request(:post, 'https://example.test/echo')
  .with(body: 'a=1', headers: { 'X-Mark' => 'm' })
  .to_return(status: 201, body: 'echoed')
stub_request(:get, %r{https://example\.test/re/\d+}).to_return(body: 'regexp match')

res = Net::HTTP.get_response(URI('https://example.test/ok'))
puts res.code, res.body, res['content-type']

uri = URI('https://example.test/echo')
http = Net::HTTP.new(uri.host, uri.port)
http.use_ssl = true
res = http.post(uri.path, 'a=1', 'X-Mark' => 'm')
puts res.code, res.body

puts Net::HTTP.get(URI('https://example.test/re/77'))

begin
  Net::HTTP.get(URI('https://example.test/unstubbed'))
rescue WebMock::NetConnectNotAllowedError => e
  puts e.class
end

puts WebMock::API.instance_method(:stub_request).arity
puts a_request(:get, 'https://example.test/ok').class

begin
  assert_requested(:get, 'https://example.test/ok', times: 1)
  puts 'requested once'
rescue StandardError => e
  puts "#{e.class}"
end

WebMock.reset!
WebMock.disable!
