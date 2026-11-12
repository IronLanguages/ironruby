require 'regexp_parser'

def dump(node, depth = 0)
  puts "#{'  ' * depth}#{node.type}:#{node.token} #{node.to_s.inspect}"
  node.each { |child| dump(child, depth + 1) } if node.respond_to?(:each) && node.respond_to?(:expressions)
end

re = /\A(?<name>[a-z]+)\s*(\d{2,4})?(?:x|y)+\z/i
dump(Regexp::Parser.parse(re))

root = Regexp::Parser.parse(/a|b/)
puts root.type
puts root.expressions.size

puts Regexp::Scanner.scan('a{2,3}').map { |type, token, text, *| [type, token, text].inspect }.inspect
puts Regexp::Lexer.scan(/[a-z]/).map { |t| [t.type, t.token, t.text] }.inspect

tree = Regexp::Parser.parse('(a)(?<n>b)')
puts tree.expressions.map { |e| [e.type, e.token, (e.respond_to?(:number) ? e.number : nil)] }.inspect

begin
  Regexp::Parser.parse('(')
rescue Regexp::Parser::Error, ArgumentError, RegexpError => e
  puts e.class
end

puts Regexp::Parser.parse(/a+/).expressions.first.quantifier.to_s
puts Regexp::Parser.parse(/a*?/).expressions.first.quantifier.mode.inspect
puts Regexp::Parser::VERSION.split('.').first.to_i >= 2
