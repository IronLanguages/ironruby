require 'rails'


# A whole Rails application, booted in-process and served a request through
# Rack::Test - no generator, no files, no server.
require 'action_controller/railtie'
require 'rack/test'

module Exercise
  class Application < Rails::Application
    config.eager_load = false
    config.load_defaults 8.0
    config.logger = Logger.new(IO::NULL)
    config.secret_key_base = 'a' * 64
    config.hosts.clear
    config.consider_all_requests_local = true
  end
end

class WelcomeController < ActionController::Base
  def index; render plain: 'welcome'; end
  def hello; render plain: "hello #{params[:name]}"; end
  def boom; raise ArgumentError, 'deliberate'; end
end

Rails.application.initialize!
Rails.application.routes.draw do
  root to: 'welcome#index'
  get '/hello/:name', to: 'welcome#hello', as: :hello
  get '/boom', to: 'welcome#boom'
end

puts Rails.application.class.name
puts Rails.env
puts Rails.application.config.eager_load
puts Rails.version.split('.').first
puts Rails.application.routes.url_helpers.root_path
puts Rails.application.routes.url_helpers.hello_path(name: 'ada')

class S
  include Rack::Test::Methods
  def app; Rails.application; end
end
s = S.new

s.get '/'
puts s.last_response.status, s.last_response.body

s.get '/hello/ada'
puts s.last_response.body

s.get '/missing'
puts s.last_response.status

puts Rails.application.config.root.class
puts Rails::Railtie.subclasses.size > 0
puts Rails.application.initializers.size > 10
