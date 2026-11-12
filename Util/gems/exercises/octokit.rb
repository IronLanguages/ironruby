require 'octokit'
require 'sawyer'
require 'faraday'

# No network. What runs here is the client's own plumbing: repository naming,
# the URL builders, the error mapping from a response, and the Sawyer resource
# wrapper that every Octokit method returns.
puts Octokit::Client.new.api_endpoint
puts Octokit::Client.new(access_token: 'x').access_token
puts Octokit::Client.new(access_token: 'x').user_authenticated?
puts Octokit::Client.new(login: 'a', password: 'b').basic_authenticated?

puts Octokit::Repository.new('ruby/ruby').to_s
puts Octokit::Repository.new('ruby/ruby').name
puts Octokit::Repository.new('ruby/ruby').owner
puts Octokit::Repository.from_url('https://github.com/a/b').slug
puts Octokit::Repository.new(id: 7).url
puts Octokit::Repository.path('ruby/ruby')
begin
  Octokit::Repository.new('not-a-slug')
rescue Octokit::InvalidRepository => e
  puts e.class
end

def error_for(status, body)
  response = { method: :get, url: 'https://api.github.com/x', status: status,
               response_headers: { 'content-type' => 'application/json' }, body: body }
  Octokit::Error.from_response(response)
end
puts error_for(404, '{"message":"Not Found"}').class
puts error_for(401, '{"message":"Bad credentials"}').class
puts error_for(422, '{"message":"Validation Failed"}').class
puts error_for(500, '{"message":"boom"}').class
puts error_for(403, '{"message":"rate limit exceeded"}').class
puts error_for(200, '{}').inspect

agent = Sawyer::Agent.new('https://api.github.com') do |http|
  http.adapter :test, Faraday::Adapter::Test::Stubs.new
end
resource = Sawyer::Resource.new(agent, id: 1, name: 'ruby', owner: { login: 'ruby' }, private: false)
puts resource.name
puts resource.owner.login
puts resource.private?
puts resource.fields.to_a.map(&:to_s).sort.inspect
puts resource.to_h.keys.sort.inspect
puts resource[:id]

puts Octokit::VERSION.split('.').first
