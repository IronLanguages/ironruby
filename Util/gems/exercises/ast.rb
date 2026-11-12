require 'ast'

class Node < AST::Node; end

n = Node.new(:send, [Node.new(:int, [1]), :+, Node.new(:int, [2])])
puts n.to_s
puts n.type.inspect
puts n.children.size
puts n.to_sexp
puts n.inspect

class Counter
  include AST::Processor::Mixin
  attr_reader :ints
  def initialize; @ints = []; end
  def on_int(node); @ints << node.children.first; end
  def on_send(node); process_all(node.children.grep(AST::Node)); end
end

c = Counter.new
c.process(n)
puts c.ints.inspect

puts n.updated(:other).type.inspect
puts n.append(Node.new(:int, [3])).children.size
puts n == Node.new(:send, [Node.new(:int, [1]), :+, Node.new(:int, [2])])
puts n.eql?(n.dup)
puts n.hash == n.dup.hash
puts AST::Sexp.instance_method(:s).arity
