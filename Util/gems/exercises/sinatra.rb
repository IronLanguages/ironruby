require 'sinatra/base'
require 'rack/test'

class App < Sinatra::Base
  set :environment, :test
  set :show_exceptions, false
  set :raise_errors, false

  get '/' do
    'root'
  end

  get '/hello/:name' do
    "hello #{params[:name]}"
  end

  get '/query' do
    params.sort.map { |k, v| "#{k}=#{v}" }.join('&')
  end

  post '/create' do
    status 201
    headers 'X-Made' => 'yes'
    "made #{params['what']}"
  end

  get '/splat/*' do
    params['splat'].inspect
  end

  get '/json' do
    content_type :json
    '{"ok":true}'
  end

  get '/redirect' do
    redirect '/'
  end

  get '/boom' do
    raise 'deliberate'
  end

  error 500 do
    'handled 500'
  end

  not_found do
    'custom 404'
  end
end

include Rack::Test::Methods
def app; App; end

get '/'
puts last_response.status, last_response.body

get '/hello/world'
puts last_response.body

get '/query', 'b' => '2', 'a' => '1'
puts last_response.body

post '/create', 'what' => 'thing'
puts last_response.status, last_response.headers['X-Made'], last_response.body

get '/splat/a/b'
puts last_response.body

get '/json'
puts last_response.headers['Content-Type']

get '/redirect'
puts last_response.status, last_response.headers['Location']

get '/boom'
puts last_response.status, last_response.body

get '/nope'
puts last_response.status, last_response.body

puts Sinatra::VERSION.split('.').first
