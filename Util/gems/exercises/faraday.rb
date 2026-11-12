require 'faraday'
require 'json'

# No network: Faraday's own test adapter is the point of this exercise - it
# exercises the middleware stack, the request/response envelopes and the
# error classes without a socket.
stubs = Faraday::Adapter::Test::Stubs.new do |stub|
  stub.get('/ok?q=1') { [200, { 'Content-Type' => 'application/json' }, '{"a":[1,2]}'] }
  stub.post('/echo') { |env| [201, {}, env.request_headers['X-Mark'].to_s + '|' + env.body.to_s] }
  stub.get('/boom') { [500, {}, 'kaboom'] }
  stub.get('/missing') { [404, {}, ''] }
end

conn = Faraday.new(url: 'https://example.test') do |f|
  f.request :url_encoded
  f.adapter :test, stubs
end

r = conn.get('/ok', q: 1)
puts r.status
puts r.body
puts JSON.parse(r.body).inspect
puts r.headers['content-type']
puts r.success?

r = conn.post('/echo', { 'k' => 'v' }, { 'X-Mark' => 'm' })
puts r.status
puts r.body

r = conn.get('/boom')
puts r.status, r.success?

raising = Faraday.new(url: 'https://example.test') do |f|
  f.response :raise_error
  f.adapter :test, stubs
end
begin
  raising.get('/missing')
rescue Faraday::ResourceNotFound => e
  puts "#{e.class} #{e.response[:status]}"
end
begin
  raising.get('/boom')
rescue Faraday::ServerError => e
  puts "#{e.class} #{e.response[:status]}"
end

puts Faraday::Utils.build_nested_query({ 'a' => ['1', '2'] })
puts Faraday::Utils.parse_nested_query('a[]=1&a[]=2').inspect
puts Faraday::VERSION.split('.').first
stubs.verify_stubbed_calls rescue puts 'unverified'
