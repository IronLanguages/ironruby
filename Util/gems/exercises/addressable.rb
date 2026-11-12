require 'addressable/uri'
require 'addressable/template'

u = Addressable::URI.parse('https://user:pw@example.com:8443/a/b?x=1&y=2#frag')
puts u.scheme, u.user, u.password, u.host, u.port, u.path, u.query, u.fragment
puts u.query_values.sort.inspect
puts u.normalize.to_s
puts u.omit(:password, :user).to_s
puts u.join('../c').to_s
puts Addressable::URI.join('https://example.com/a/', 'b/c').to_s

puts Addressable::URI.parse('http://EXAMPLE.com/%7Ejoe/').normalize.to_s
puts Addressable::URI.parse('http://exämple.com/').normalize.to_s
puts Addressable::URI.encode('http://example.com/a b')
puts Addressable::URI.unencode('http://example.com/a%20b')
puts Addressable::URI.parse('mailto:a@b.c').scheme

t = Addressable::Template.new('http://example.com/{id}{?q}')
puts t.expand(id: '42', q: 'x y').to_s
m = t.match('http://example.com/7?q=z')
puts m.mapping.sort.inspect

rel = Addressable::URI.parse('https://example.com/a/b/c')
puts rel.route_from('https://example.com/a/').to_s
puts rel.route_to('https://example.com/a/d').to_s

u2 = Addressable::URI.parse('https://example.com/')
u2.query_values = { 'b' => '2', 'a' => '1' }
puts u2.to_s
puts u2.hash == Addressable::URI.parse(u2.to_s).hash

begin
  Addressable::URI.parse('http://[invalid')
rescue Addressable::URI::InvalidURIError => e
  puts e.class
end
