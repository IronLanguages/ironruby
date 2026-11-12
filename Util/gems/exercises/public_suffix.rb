require 'public_suffix'

%w[example.com www.example.com a.b.example.co.uk
   foo.blogspot.com example.tokyo.jp shop.example.museum].each do |host|
  d = PublicSuffix.parse(host)
  puts [host, d.tld, d.sld, d.trd.inspect, d.domain, d.subdomain.inspect].join(' | ')
end

puts PublicSuffix.valid?('example.com')
puts PublicSuffix.valid?('com')
puts PublicSuffix.valid?('example.invalidtld')
puts PublicSuffix.domain('www.a.example.co.uk')
puts PublicSuffix.valid?('example.com', ignore_private: true)

begin
  PublicSuffix.parse('com')
rescue PublicSuffix::DomainNotAllowed => e
  puts e.class
end

begin
  PublicSuffix.parse('example.invalidtld')
rescue PublicSuffix::DomainInvalid => e
  puts e.class
end

list = PublicSuffix::List.default
puts [list.find('example.com').class, list.find('example.com').value].inspect
puts [list.find('a.b.ck').class, list.find('a.b.ck').value].inspect
