require 'excon'

Excon.defaults[:mock] = true
Excon.stub({ method: :get, path: '/ok' },
           { body: 'hello', status: 200, headers: { 'Content-Type' => 'text/plain' } })
Excon.stub({ method: :post, path: '/echo' }) { |req| { body: req[:body].to_s.upcase, status: 201 } }
Excon.stub({ method: :get, path: '/boom' }, { body: '', status: 500 })

conn = Excon.new('http://example.test')
r = conn.get(path: '/ok')
puts r.status
puts r.body
puts r.headers['Content-Type']

r = conn.post(path: '/echo', body: 'abc')
puts r.status, r.body

begin
  Excon.get('http://example.test/boom', expects: [200])
rescue Excon::Error::InternalServerError => e
  puts e.class
end

begin
  Excon.get('http://example.test/unstubbed')
rescue Excon::Errors::StubNotFound => e
  puts e.class
end

puts Excon::Utils.query_string(query: { 'b' => 2, 'a' => 1 })
puts Excon::Utils.escape_uri('a b')
puts Excon::Utils.redact(password: 'x', user: 'y').sort.inspect
puts Excon::VERSION.split('.').first.to_i >= 0
Excon.stubs.clear
