require 'tilt'
require 'tilt/erb'
require 'tilt/string'

tpl = Tilt::ERBTemplate.new { "Hello <%= name %>!\n<% 3.times do |i| %><%= i %><% end %>" }
puts tpl.render(Object.new, name: 'world')

scope = Struct.new(:name).new('scoped')
puts Tilt::ERBTemplate.new { 'Hi <%= name %>' }.render(scope)

str = Tilt::StringTemplate.new { 'sum=#{1 + 2}' }
puts str.render

puts Tilt::ERBTemplate.new { '<%= 1 %><%# comment %>' }.render(Object.new)
puts Tilt['erb'].to_s
puts Tilt['x.erb'].to_s
puts Tilt.registered?('erb')
puts Tilt::ERBTemplate.new(nil, 3) { '<%= __LINE__ %>' }.render(Object.new)

t = Tilt::ERBTemplate.new { '<%= a %> <%= b %>' }
puts t.render(Object.new, a: 1, b: 2)
puts t.render(Object.new, a: 3, b: 4)

begin
  Tilt::ERBTemplate.new { '<%= raise "in template" %>' }.render(Object.new)
rescue RuntimeError => e
  puts "#{e.class}: #{e.message}"
end
