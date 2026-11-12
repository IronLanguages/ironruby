require 'coderay'

code = <<~RUBY
  class Foo
    # a comment
    def bar(x = 1)
      "str \#{x}" + :sym.to_s
    end
  end
RUBY

puts CodeRay.scan(code, :ruby).tokens.each_slice(2).map { |t, k| [t, k] }.inspect
puts CodeRay.scan(code, :ruby).encode(:count)
puts CodeRay.scan(code, :ruby).text
puts CodeRay.scan('{"a": [1, null]}', :json).tokens.each_slice(2).map { |t, k| [t, k] }.inspect
puts CodeRay.scan("SELECT * FROM t WHERE a = 'x'", :sql).encode(:terminal).inspect
puts CodeRay.highlight('x = 1', :ruby).gsub(/\s+/, ' ')
puts CodeRay::Scanners.list.map(&:to_s).sort.first(5).inspect
puts CodeRay::Encoders.list.map(&:to_s).sort.first(5).inspect
puts CodeRay.scan('1 + 1', :ruby).encode(:statistic).lines.first.strip
