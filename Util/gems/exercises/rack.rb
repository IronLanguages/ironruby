require 'rack'

app = Rack::Builder.new do
  use Rack::ContentType, 'text/plain'
  map '/hello' do
    run ->(env) {
      req = Rack::Request.new(env)
      [200, {}, ["hello #{req.params['name']} #{req.request_method} #{req.path}"]]
    }
  end
  run ->(_env) { [404, {}, ['nope']] }
end.to_app

env = Rack::MockRequest.env_for('/hello?name=world')
status, headers, body = app.call(env)
puts status
puts headers['content-type']
body.each { |c| puts c }

status, _h, body = app.call(Rack::MockRequest.env_for('/other'))
puts status
body.each { |c| puts c }

post = Rack::MockRequest.env_for('/hello', method: 'POST', input: 'a=1&b=2')
req = Rack::Request.new(post)
puts req.POST.sort.inspect
puts req.media_type.inspect

puts Rack::Utils.parse_nested_query('a[b]=1&a[c]=2').inspect
puts Rack::Utils.build_nested_query({ 'a' => { 'b' => '1' } })
puts Rack::Utils.escape('a b/c')
puts Rack::Utils.unescape('a%20b')
puts Rack::Utils.status_code(:not_found)
puts Rack::Utils::HTTP_STATUS_CODES[422]

res = Rack::Response.new
res.status = 201
res.set_header('X-Test', 'yes')
res.write('body')
st, hd, bd = res.finish
puts st
puts hd['x-test']
bd.each { |c| puts c }

puts Rack::MediaType.type('text/html; charset=utf-8')
puts Rack.release
