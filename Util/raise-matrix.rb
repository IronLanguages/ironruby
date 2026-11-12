# Differential matrix for Kernel#raise argument handling and Exception#cause.
# Run under CRuby and under ./ir.sh and diff the output.
#
#   ruby    Util/raise-matrix.rb > /tmp/cruby.txt
#   ./ir.sh Util/raise-matrix.rb > /tmp/ir.txt
#   diff -u /tmp/cruby.txt /tmp/ir.txt

def show(label)
  r = yield
  puts "#{label} => OK #{r.inspect}"
rescue Exception => e
  c = (e.cause rescue :NO_CAUSE)
  puts "#{label} => #{e.class}: #{e.message.inspect} cause=#{c.inspect}"
end

Weird = Class.new(StandardError) do
  attr_reader :data
  def initialize(data = nil); @data = data; end
end
NoArg = Class.new(Exception) do
  def initialize; end
end

show("bare")                { raise }
show("string")              { raise "a bad thing" }
show("class")               { raise ArgumentError }
show("class,msg")           { raise ArgumentError, "message" }
show("class,msg,bt")        { raise ArgumentError, "message", ["l1", "l2"] }
show("noarg-ctor class")    { raise NoArg }
show("instance")            { raise RuntimeError.new("inst") }
show("nil")                 { raise nil }
show("true")                { raise true }
show("false")               { raise false }
show("Object.new")          { raise Object.new }
show("Object.new,msg")      { raise Object.new, "message" }
show("Object.new,msg,bt")   { raise Object.new, "message", [] }
show("4 args")              { raise ArgumentError, "m", [], 1 }
show("hash message")        { raise Weird, {data: 42} }
show("kw message")          { raise Weird, data: 42 }
show("string + hash arg")   { raise "message", {cause: RuntimeError.new} }
show("to_hash exc")         { raise(Class.new(StandardError) { def to_hash; {}; end }.new) }

e = Object.new
def e.exception = StandardError.new("from #exception")
show("respond exception/0") { raise e }

e1 = Object.new
def e1.exception(msg) = StandardError.new(msg)
show("respond exception/1")     { raise e1, "foo" }
show("respond exception/1 arity") { raise e1 }

e2 = Object.new
def e2.exception; Array; end
show("exception returns class") { raise e2 }

# ---- cause ----------------------------------------------------------------
show("cause: default no $!")     { raise "no cause" }
show("cause: explicit")          { raise "x", cause: StandardError.new("c") }
show("cause: nil")               { raise "x", cause: nil }
show("cause: only")              { raise(cause: StandardError.new("c")) }
show("cause: only nil")          { raise(cause: nil) }
show("cause: not exception")     { raise "x", cause: Object.new }
show("cause: class+msg+cause")   { raise ArgumentError, "m", cause: StandardError.new("c") }
show("cause: class+msg+bt+cause") { raise ArgumentError, "m", ["l1"], cause: StandardError.new("c") }

show("cause: auto chain") do
  begin
    raise StandardError, "first"
  rescue
    raise "second"
  end
end

show("cause: self") do
  c = StandardError.new("cc")
  raise c, cause: c
end

show("cause: circular") do
  begin
    raise "Error 1"
  rescue => error1
    begin
      raise "Error 2"
    rescue
      begin
        raise "Error 3"
      rescue => error3
        raise error1, cause: error3
      end
    end
  end
end

# cause is only assigned once
show("cause: reraise keeps first") do
  err = nil
  begin
    begin
      raise "inner"
    rescue
      raise StandardError, "outer"
    end
  rescue => err
  end
  begin
    raise err
  rescue => e2
    "reraised cause=#{e2.cause.inspect}"
  end
end

# ---- Exception#cause presence ---------------------------------------------
puts "Exception#cause defined? #{Exception.method_defined?(:cause)}"
puts "no-raise cause: #{StandardError.new('x').cause.inspect}"
