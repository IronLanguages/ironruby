require 'domain_name'

%w[www.example.com example.co.uk localhost 192.168.0.1 xn--kbenhavn-54a.eu].each do |host|
  d = DomainName(host)
  puts [d.hostname, d.domain.inspect, d.tld, d.canonical_tld?, d.ipaddr?].inspect
end

a = DomainName('www.example.com')
puts a.cookie_domain?('example.com')
puts a.cookie_domain?('com')
puts a.superdomain.to_s
puts a.superdomain.superdomain.to_s
puts DomainName('example.com') == DomainName('EXAMPLE.com')
puts (DomainName('a.example.com') <=> DomainName('example.com'))
puts DomainName('www.example.com').domain
puts DomainName('日本.jp').hostname
