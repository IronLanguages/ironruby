require 'method_source'

def sample(a, b = 1)
  a +
    b
end

class Holder
  def self.klass_method; :k; end
  def inst(x)
    x * 2
  end
end

puts method(:sample).source
puts method(:sample).source_location.last
puts Holder.instance_method(:inst).source
puts Holder.method(:klass_method).source
puts Holder.instance_method(:inst).comment.inspect

pr = proc { |x| x + 1 }
puts pr.source.strip

# A comment that documents documented_method.
# Second line of that comment.
def documented_method; end
puts method(:documented_method).comment

begin
  method(:puts).source
rescue MethodSource::SourceNotFoundError => e
  puts e.class
end
