require 'docile'

class Recipe
  attr_reader :steps, :name
  def initialize; @steps = []; end
  def title(n); @name = n; end
  def add(s); @steps << s; end
  def with(x); @steps << "with #{x}"; end
end

r = Docile.dsl_eval(Recipe.new) do
  title 'bread'
  add 'flour'
  add 'water'
  with 'salt'
  3.times { |i| add "knead #{i}" }
end
puts r.name
puts r.steps.inspect

outer = 'from the enclosing scope'
r2 = Docile.dsl_eval(Recipe.new) do
  title outer
  add outer.upcase
end
puts r2.name
puts r2.steps.inspect

arr = Docile.dsl_eval([]) do
  push 1
  push 2
  pop
  push 3
end
puts arr.inspect

h = Docile.dsl_eval_with_block_return({}) do
  store(:a, 1)
  :returned
end
puts h.inspect

class Builder
  def initialize; @parts = []; end
  def part(n); @parts << n; self; end
  def result; @parts.join('-'); end
end
puts Docile.dsl_eval_immutable(Builder.new) { part 'a'; part 'b' }.result
