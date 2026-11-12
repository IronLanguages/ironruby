# Differential matrix for Enumerator / Enumerable behaviour.
#   ruby Util/enum-matrix.rb            # CRuby reference
#   ./ir.sh Util/enum-matrix.rb         # IronRuby
# Diff the two outputs.

def show(label)
  v = begin
        yield.inspect
      rescue Exception => e
        "!#{e.class}: #{e.message}"
      end
  puts "#{label.ljust(46)} #{v}"
end

class Multi
  include Enumerable
  def each
    yield 1, 2, 3
    yield 4, 5, 6
    self
  end
end

class One
  include Enumerable
  def each
    yield 1
    yield 2
    self
  end
end

class Zero
  include Enumerable
  def each
    yield
    yield
    self
  end
end

def three
  yield 1, 2, 3
  yield 4, 5, 6
end

def sized
  yield 1
  yield 2
  yield 3
end

puts "== multi-value yield packing =="
show("Multi#to_a")                  { Multi.new.to_a }
show("Multi#entries")               { Multi.new.entries }
show("Multi.to_enum(:each).to_a")   { Multi.new.to_enum(:each).to_a }
show("to_enum(:three).to_a")        { to_enum(:three).to_a }
show("to_enum(:three).first")       { to_enum(:three).first }
show("to_enum(:three).first(1)")    { to_enum(:three).first(1) }
show("Multi#map{|a|a}")             { Multi.new.map { |a| a } }
show("Multi#map{|a,b|[a,b]}")       { Multi.new.map { |a, b| [a, b] } }
show("Multi#select{true}")          { Multi.new.select { true } }
show("Multi#reject{false}")         { Multi.new.reject { false } }
show("Multi#sort_by{|a|a}")         { Multi.new.sort_by { |a| a } }
show("Multi#group_by{|a|1}")        { Multi.new.group_by { |a| 1 } }
show("Multi#partition{true}")       { Multi.new.partition { true } }
show("Multi#find{true}")            { Multi.new.find { true } }
show("Multi#take(1)")               { Multi.new.take(1) }
show("Multi#take_while{true}")      { Multi.new.take_while { true } }
show("Multi#drop(1)")               { Multi.new.drop(1) }
show("Multi#drop_while{false}")     { Multi.new.drop_while { false } }
show("Multi#each_with_index.to_a")  { Multi.new.each_with_index.to_a }
show("Multi#each_with_object([]).to_a") { Multi.new.each_with_object([]).to_a }
show("Multi#zip([1,2])")            { Multi.new.zip([1, 2]) }
show("Multi#min_by{|a|a}")          { Multi.new.min_by { |a| a } }
show("Multi#flat_map{|a|[a]}")      { Multi.new.flat_map { |a| [a] } }
show("Multi#each_slice(1).to_a")    { Multi.new.each_slice(1).to_a }
show("Multi#each_cons(1).to_a")     { Multi.new.each_cons(1).to_a }
show("Multi#include?([1,2,3])")     { Multi.new.include?([1, 2, 3]) }
show("Multi#count")                 { Multi.new.count }
show("Multi#inject{|m,a| m}")       { Multi.new.inject { |m, a| m } }
show("Multi#grep(Array)")           { Multi.new.grep(Array) }
show("Multi#each_entry.to_a")       { (Multi.new.each_entry.to_a rescue "!noentry") }
show("Zero#to_a")                   { Zero.new.to_a }
show("Zero#map{|a|a}")              { Zero.new.map { |a| a } }
show("Hash#to_a")                   { { a: 1, b: 2 }.to_a }
show("Hash#each.to_a")              { { a: 1, b: 2 }.each.to_a }
show("Hash#map{|a|a}")              { { a: 1 }.map { |a| a } }
show("Hash#select{true}")           { { a: 1 }.select { true } }
show("[1,2].each.to_a")             { [1, 2].each.to_a }
show("[[1,2]].each.to_a")           { [[1, 2]].each.to_a }
show("[[1,2]].map{|a|a}")           { [[1, 2]].map { |a| a } }
show("[[1,2]].map{|a,b|[b,a]}")     { [[1, 2]].map { |a, b| [b, a] } }

puts
puts "== Enumerator.new / yielder =="
show("E.new{|y| y<<1; y<<2}.to_a")  { Enumerator.new { |y| y << 1; y << 2 }.to_a }
show("E.new{|y| y.yield 1,2}.to_a") { Enumerator.new { |y| y.yield 1, 2 }.to_a }
show("E.new{|y| y<<1}.first")       { Enumerator.new { |y| y << 1 }.first }
show("yielder returns")             { Enumerator.new { |y| (y << 1).class }.first }

puts
puts "== #size =="
show("to_enum(:sized){3}.size")     { to_enum(:sized) { 3 }.size }
show("to_enum(:sized).size")        { to_enum(:sized).size }
show("[1,2,3].each.size")           { [1, 2, 3].each.size }
show("[1,2,3].each_slice(2).size")  { [1, 2, 3].each_slice(2).size }
show("[1,2,3].map.size")            { [1, 2, 3].map.size }
show("{a: 1}.each.size")            { { a: 1 }.each.size }
show("E.new{|y| y<<1}.size")        { Enumerator.new { |y| y << 1 }.size }
show("E.new(3){|y| y<<1}.size")     { Enumerator.new(3) { |y| y << 1 }.size }
show("E.new(->{7}){|y|}.size")      { Enumerator.new(-> { 7 }) { |y| }.size }
show("(1..3).each.size")            { (1..3).each.size }
show("5.times.size")                { 5.times.size }
show("loop-enum size")              { [1, 2].each_entry.size rescue "!noentry" }

puts
puts "== #next / #peek / #rewind =="
show("e=[1,2].each; e.next")        { e = [1, 2].each; e.next }
show("next next")                   { e = [1, 2].each; [e.next, e.next] }
show("next past end")               { e = [1].each; e.next; e.next }
show("peek")                        { e = [1, 2].each; [e.peek, e.peek, e.next, e.peek] }
show("peek past end")               { e = [1].each; e.next; e.peek }
show("rewind")                      { e = [1, 2].each; e.next; e.rewind; e.next }
show("next on generator")           { e = Enumerator.new { |y| y << 1; y << 2 }; [e.next, e.next] }
show("next multi-value")            { e = Multi.new.to_enum(:each); e.next }
show("next hash")                   { e = { a: 1 }.each; e.next }
show("next infinite")               { e = Enumerator.new { |y| i = 0; loop { y << (i += 1) } }; [e.next, e.next, e.next] }
show("next_values")                 { e = Multi.new.to_enum(:each); e.next_values rescue "!no next_values" }
show("peek_values")                 { e = Multi.new.to_enum(:each); e.peek_values rescue "!no peek_values" }
show("rewind calls rewind hook")    {
  o = Object.new
  def o.each; yield 1; end
  def o.rewind; $hook = :called; end
  e = o.to_enum(:each); e.next; e.rewind; $hook
}

puts
puts "== #lazy =="
show("(1..Float::INFINITY).lazy.map{|x|x*2}.first(3)") { (1..Float::INFINITY).lazy.map { |x| x * 2 }.first(3) }
show("lazy class")                  { (1..3).lazy.class }
show("lazy.map class")              { (1..3).lazy.map { |x| x }.class }
show("lazy.force")                  { (1..3).lazy.map { |x| x * 2 }.force }
show("lazy.select")                 { (1..10).lazy.select(&:even?).first(2) }
show("lazy.filter")                 { (1..10).lazy.filter(&:even?).first(2) rescue "!nofilter" }
show("lazy.reject")                 { (1..10).lazy.reject(&:even?).first(2) }
show("lazy.take")                   { (1..Float::INFINITY).lazy.take(3).to_a }
show("lazy.take(0)")                { (1..Float::INFINITY).lazy.take(0).to_a }
show("lazy.take_while")             { (1..Float::INFINITY).lazy.take_while { |x| x < 4 }.to_a }
show("lazy.drop")                   { (1..6).lazy.drop(3).to_a }
show("lazy.drop_while")             { (1..6).lazy.drop_while { |x| x < 4 }.to_a }
show("lazy.first")                  { (1..6).lazy.first }
show("lazy.first(2)")               { (1..6).lazy.first(2) }
show("lazy.flat_map")               { (1..3).lazy.flat_map { |x| [x, x] }.to_a }
show("lazy.zip")                    { (1..3).lazy.zip([4, 5, 6]).to_a }
show("lazy.uniq")                   { [1, 1, 2, 2, 3].lazy.uniq.to_a }
show("lazy.filter_map")             { (1..6).lazy.filter_map { |x| x * 2 if x.even? }.first(2) }
show("lazy.with_index")             { (1..3).lazy.with_index.to_a rescue "!nowithindex" }
show("lazy.each_slice")             { (1..6).lazy.each_slice(2).to_a rescue "!noeachslice" }
show("lazy.lazy")                   { (1..3).lazy.lazy.class }
show("lazy.eager")                  { (1..3).lazy.eager.class rescue "!noeager" }
show("lazy.size")                   { (1..3).lazy.size rescue "!nosize" }
show("lazy.compact")                { [1, nil, 2].lazy.compact.to_a rescue "!nocompact" }
show("lazy chained")                { (1..Float::INFINITY).lazy.map { |x| x * 2 }.select { |x| x % 3 == 0 }.first(3) }
show("lazy.map no block")           { (1..3).lazy.map rescue "!#{$!.class}" }
show("lazy.to_a on infinite take")  { (1..Float::INFINITY).lazy.map { |x| x }.take(2).force }

puts
puts "== with_index / with_object / chain / product =="
show("[1,2].each.with_index(1).to_a")  { [1, 2].each.with_index(1).to_a }
show("[1,2].map.with_index{|x,i|x*i}") { [1, 2].map.with_index { |x, i| x * i } }
show("[1,2].each.with_object(:o).to_a") { [1, 2].each.with_object(:o).to_a }
show("[1,2].each.each_with_object([]){|x,a|a<<x}") { [1, 2].each.each_with_object([]) { |x, a| a << x } }
show("Enumerator::Chain")           { Enumerator::Chain.new([1, 2].each, [3].each).to_a rescue "!#{$!.class}" }
show("[1,2].each.+([3].each).to_a") { ([1, 2].each + [3].each).to_a rescue "!#{$!.class}" }
show("[1,2].chain([3]).to_a")       { [1, 2].chain([3]).to_a rescue "!#{$!.class}" }
show("Enumerator::Product")         { Enumerator::Product.new([1, 2], [3]).to_a rescue "!#{$!.class}" }
show("Enumerator.product")          { Enumerator.product([1, 2], [3]).to_a rescue "!#{$!.class}" }
show("Enumerator::Yielder#to_proc") { Enumerator.new { |y| [1, 2].each(&y) }.to_a rescue "!#{$!.class}" }
show("e.inspect")                   { [1, 2].each.inspect }
show("lazy.inspect")                { (1..3).lazy.map { |x| x }.inspect }
show("Enumerator::Lazy ancestors")  { Enumerator::Lazy.ancestors.take(4).inspect rescue "!#{$!.class}" }
show("e.each_entry")                { [1, 2].each_entry { |x| }.class rescue "!#{$!.class}" }
show("Enumerator#+ class")          { ([1].each + [2].each).class rescue "!#{$!.class}" }
show("e.feed")                      {
  begin
    e = Enumerator.new { |y| $fed = (y.yield 1); y << 2 }
    e.next; e.feed(:v); e.next; $fed
  rescue Exception => ex
    "!#{ex.class}"
  end
}
