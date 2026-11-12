require 'roda'
require 'rack/test'

class App < Roda
  plugin :json
  plugin :halt
  plugin :error_handler do |e|
    response.status = 500
    "handled #{e.class}"
  end

  route do |r|
    r.root { 'root' }

    r.on 'users' do
      r.is Integer do |id|
        r.get { "user #{id}" }
        r.post { "updated #{id}" }
      end
      r.is { ['a', 'b'] }
    end

    r.get 'query' do
      r.params.sort.map { |k, v| "#{k}=#{v}" }.join('&')
    end

    r.get 'halt' do
      r.halt [418, {}, ['teapot']]
    end

    r.get 'boom' do
      raise ArgumentError, 'deliberate'
    end
  end
end

include Rack::Test::Methods
def app; App.freeze.app; end

get '/'
puts last_response.status, last_response.body

get '/users/42'
puts last_response.body
post '/users/42'
puts last_response.body

get '/users'
puts last_response.headers['Content-Type']
puts last_response.body

get '/query', 'b' => '2', 'a' => '1'
puts last_response.body

get '/halt'
puts last_response.status, last_response.body

get '/boom'
puts last_response.status, last_response.body

get '/missing'
puts last_response.status

puts Roda::RodaVersion.split('.').first
