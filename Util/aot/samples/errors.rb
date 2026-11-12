# Backtraces and uncaught exceptions from compiled code.
class Widget
  def explode(n)
    [1].each { raise ArgumentError, "boom #{n}" }
  end
end

begin
  Widget.new.explode(3)
rescue => e
  puts e.message
  puts e.backtrace.first(3)
end

def deep(n) = n.zero? ? raise("bottom") : deep(n - 1)
deep(2)
