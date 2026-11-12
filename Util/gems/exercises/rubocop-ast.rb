require 'rubocop-ast'

source = RuboCop::AST::ProcessedSource.new(<<~RUBY, RUBY_VERSION.to_f)
  class Foo
    def bar(a, b = 1)
      @x = a + b
      [1, 2].map { |i| i * 2 }
    end
  end
RUBY

ast = source.ast
puts ast.type
puts ast.class
puts ast.children.first.const_name
puts source.valid_syntax?

klass = ast
method = klass.each_node(:def).first
puts method.method_name
puts method.arguments.map(&:name).inspect
puts method.body.type

send_nodes = ast.each_node(:send).map(&:method_name)
puts send_nodes.inspect

puts ast.each_node(:block).first.send_node.method_name
puts ast.each_node(:ivasgn).first.name

pattern = RuboCop::AST::NodePattern.new('(send (array ...) :map)')
puts ast.each_node(:send).count { |n| pattern.match(n) }

pattern2 = RuboCop::AST::NodePattern.new('(def $_ ...)')
puts pattern2.match(method).inspect

puts RuboCop::AST::Node.new(:int, [1]).type
puts source.tokens.first.type
puts source.lines.size
puts source.comments.inspect
puts RuboCop::AST::Version::STRING.split('.').first
