require 'rack/protection'
require 'rack/test'
require 'rack/session'

inner = ->(_env) { [200, { 'content-type' => 'text/plain' }, ['ok']] }

app = Rack::Builder.new do
  use Rack::Session::Cookie, secret: 'x' * 64, key: 'rack.session'
  use Rack::Protection::XSSHeader
  use Rack::Protection::FrameOptions
  use Rack::Protection::PathTraversal
  use Rack::Protection::IPSpoofing
  use Rack::Protection::HttpOrigin, permitted_origins: ['https://allowed.test']
  run inner
end.to_app

class S
  include Rack::Test::Methods
  def initialize(app); @app = app; end
  attr_reader :app
end
s = S.new(app)

s.get '/'
puts s.last_response.status
puts s.last_response.headers['x-xss-protection']
puts s.last_response.headers['x-frame-options']
puts s.last_response.headers['x-content-type-options']

s.get '/../etc/passwd'
puts s.last_response.status
puts s.last_request.path_info

s.header 'X-Forwarded-For', '1.2.3.4'
s.header 'Client-IP', '5.6.7.8'
s.get '/'
puts s.last_response.status
s.header 'X-Forwarded-For', nil
s.header 'Client-IP', nil

s.header 'Origin', 'https://evil.test'
s.post '/'
puts s.last_response.status
s.header 'Origin', 'https://allowed.test'
s.post '/'
puts s.last_response.status

puts Rack::Protection::Base.instance_method(:react).arity
puts Rack::Protection.constants.map(&:to_s).sort.first(6).inspect
