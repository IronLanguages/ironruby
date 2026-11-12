require 'action_controller'
require 'action_dispatch'

routes = ActionDispatch::Routing::RouteSet.new
routes.draw do
  root to: 'pages#home'
  get '/hello/:name', to: 'pages#hello', as: :hello
  resources :widgets, only: %i[index show create]
end

class PagesController < ActionController::Metal
  include ActionController::Rendering
  include AbstractController::Rendering
  def home; self.response_body = 'home'; end
  def hello; self.response_body = "hello #{params[:name]}"; end
end

puts routes.url_helpers.root_path
puts routes.url_helpers.hello_path(name: 'world')
puts routes.url_helpers.widgets_path
puts routes.url_helpers.widget_path(7)
puts routes.recognize_path('/hello/ada').sort.inspect
puts routes.routes.map { |r| r.name }.compact.sort.inspect

req = ActionDispatch::Request.new(Rack::MockRequest.env_for('/hello/ada?x=1'))
puts req.path, req.request_method, req.query_parameters.inspect
puts req.get?, req.post?

params = ActionController::Parameters.new(user: { name: 'Ada', admin: true }, other: 1)
permitted = params.require(:user).permit(:name)
puts permitted.to_h.inspect
puts permitted.permitted?
begin
  ActionController::Parameters.new({}).require(:missing)
rescue ActionController::ParameterMissing => e
  puts e.class
end

res = ActionDispatch::Response.new(201, { 'content-type' => 'text/plain' }, ['body'])
puts res.status, res.content_type, res.body

puts ActionDispatch::Http::MimeNegotiation.instance_methods.include?(:format)
puts Mime[:json].to_s
puts ActionPack::VERSION::MAJOR
