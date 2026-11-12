require 'rack/test'

app = lambda do |env|
  req = Rack::Request.new(env)
  case [req.request_method, req.path]
  when %w[GET /] then [200, { 'content-type' => 'text/plain' }, ["root #{req.params.sort.inspect}"]]
  when %w[POST /form] then [200, { 'content-type' => 'text/plain' }, [req.POST.sort.inspect]]
  when %w[GET /cookie] then [200, { 'set-cookie' => 'a=1; path=/' }, ['set']]
  when %w[GET /read-cookie] then [200, {}, [req.cookies.sort.inspect]]
  when %w[GET /redirect] then [302, { 'location' => '/' }, []]
  else [404, {}, ['missing']]
  end
end

class Session
  include Rack::Test::Methods
  def initialize(app); @app = app; end
  attr_reader :app
end

s = Session.new(app)
s.get('/', 'b' => '2', 'a' => '1')
puts s.last_response.status
puts s.last_response.body
puts s.last_response.headers['content-type']

s.post('/form', 'x' => 'y')
puts s.last_response.body

s.get('/cookie')
puts s.last_response.headers['set-cookie']
s.get('/read-cookie')
puts s.last_response.body

s.get('/redirect')
puts s.last_response.status
puts s.last_response.headers['location']
s.follow_redirect!
puts s.last_response.status

s.get('/nope')
puts s.last_response.status
puts s.last_response.not_found?

s.header('X-Custom', 'v')
s.get('/')
puts s.last_request.env['HTTP_X_CUSTOM']
