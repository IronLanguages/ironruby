require 'sawyer'
require 'faraday'

stubs = Faraday::Adapter::Test::Stubs.new do |stub|
  stub.get('/repos/ruby') do
    [200, { 'Content-Type' => 'application/json',
            'Link' => '<https://api.test/repos/ruby?page=2>; rel="next"' },
     '{"id":1,"name":"ruby","owner":{"login":"matz"},"tags":["a","b"],"url":"https://api.test/repos/ruby"}']
  end
  stub.get('/missing') { [404, { 'Content-Type' => 'application/json' }, '{"message":"nope"}'] }
end

agent = Sawyer::Agent.new('https://api.test') do |http|
  http.adapter :test, stubs
end

res = agent.call(:get, '/repos/ruby')
puts res.status
puts res.data.name
puts res.data.owner.login
puts res.data.tags.inspect
puts res.data.id
puts res.data.fields.to_a.map(&:to_s).sort.inspect
puts res.rels[:next].href
puts res.rels[:next].method.inspect

r = agent.call(:get, '/missing')
puts r.status
puts r.data.message

resource = Sawyer::Resource.new(agent, name: 'x', nested: { a: 1 }, list: [{ b: 2 }])
puts resource.name
puts resource.nested.a
puts resource.list.first.b
puts resource.to_h.keys.sort.inspect
puts resource.key?(:name)
puts resource[:name]
resource[:name] = 'y'
puts resource.name
puts resource.respond_to?(:nested)
puts resource.attrs.class

puts Sawyer::VERSION.split('.').first
