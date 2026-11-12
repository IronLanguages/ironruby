require 'http/cookie'
require 'uri'

jar = HTTP::CookieJar.new
url = URI('https://www.example.com/path/')

HTTP::Cookie.parse("a=1; Path=/; Domain=example.com; Max-Age=3600\nb=2; Path=/path; HttpOnly", url) do |c|
  jar.add(c)
end
jar.add(HTTP::Cookie.new(name: 'c', value: '3', domain: 'www.example.com', path: '/', for_domain: false))

puts jar.cookies(url).map { |c| [c.name, c.value, c.domain, c.path, c.httponly?] }.sort_by(&:first).inspect
puts HTTP::Cookie.cookie_value(jar.cookies(url).sort_by(&:name))
puts jar.cookies(URI('https://www.example.com/other')).map(&:name).sort.inspect
puts jar.cookies(URI('https://other.test/')).map(&:name).inspect

c = HTTP::Cookie.new(name: 'k', value: 'v', domain: 'example.com', path: '/', for_domain: true)
puts c.to_s
puts c.valid_for_uri?(URI('http://sub.example.com/'))
puts c.valid_for_uri?(URI('http://example.org/'))
puts c.session?
puts c.secure?

puts DomainName.normalize('WWW.Example.COM.')
puts HTTP::Cookie.path_match?('/a/', '/a/b')
puts HTTP::Cookie.path_match?('/a/', '/b')
jar.clear
puts jar.cookies(url).size
