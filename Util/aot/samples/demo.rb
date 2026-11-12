# The prototype's target program: a recursive method, a class with methods and state, a block,
# string interpolation and an exception.
def fib(n)
  n < 2 ? n : fib(n - 1) + fib(n - 2)
end

class Counter
  attr_reader :count

  def initialize(name)
    @name = name
    @count = 0
  end

  def bump(by = 1)
    @count += by
    self
  end

  def to_s
    "#{@name}=#{@count}"
  end
end

c = Counter.new("hits")
[1, 2, 3].each { |i| c.bump(i) }
puts c
puts "fib(20) = #{fib(20)}"

squares = (1..5).map { |x| x * x }
puts "squares: #{squares.inspect}"

begin
  raise ArgumentError, "bad #{c.count}"
rescue ArgumentError => e
  puts "rescued #{e.class}: #{e.message}"
end

total = 0
1.upto(10) { |i| total += i if i.odd? }
puts "odd sum #{total}"
