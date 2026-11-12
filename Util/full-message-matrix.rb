# Differential matrix for Exception#full_message / #detailed_message.
#   ruby    Util/full-message-matrix.rb > /tmp/cruby-fm.txt
#   ./ir.sh Util/full-message-matrix.rb > /tmp/ir-fm.txt
#   diff -u /tmp/cruby-fm.txt /tmp/ir-fm.txt
def show(label)
  puts "#{label}: #{(yield).inspect}"
rescue Exception => e
  puts "#{label}! #{e.class}: #{e.message.inspect}"
end

e = RuntimeError.new("Some runtime error")
e.set_backtrace(["a.rb:1", "b.rb:2"])

show("top/false")    { e.full_message(order: :top, highlight: false) }
show("bottom/false") { e.full_message(order: :bottom, highlight: false) }
show("top/true")     { e.full_message(order: :top, highlight: true) }
show("bottom/true")  { e.full_message(order: :bottom, highlight: true) }
show("to_tty?")      { Exception.to_tty? }
show("defaults")     { e.full_message }

begin
  begin
    raise 'the cause'
  rescue
    raise 'main exception'
  end
rescue => ex
end
show("cause/top")    { ex.full_message(highlight: false, order: :top).gsub(/^.*matrix\.rb/, "F") }
show("cause/bottom") { ex.full_message(highlight: false, order: :bottom).gsub(/^.*matrix\.rb/, "F") }

begin
  raise "first line\nsecond line\nthird line"
rescue => m
end
show("multi/false")  { m.detailed_message(highlight: false) }
show("multi/true")   { m.detailed_message(highlight: true) }
show("multi/full")   { m.full_message(highlight: false, order: :top).lines[1, 3] }

show("bad highlight full")     { StandardError.new("").full_message(highlight: 0) }
show("bad highlight detailed") { StandardError.new("").detailed_message(highlight: 'false') }
show("bad order")              { e.full_message(order: :sideways) }

k = Class.new(RuntimeError)
show("anon msg")        { k.new("message").detailed_message }
show("anon empty")      { k.new("").detailed_message =~ /\A#<Class:0x\h+>\z/ ? :matches : k.new("").detailed_message }
show("anon msg hl")     { k.new("message").detailed_message(highlight: true) }
show("nil message")     { RuntimeError.new(nil).detailed_message }
show("no message")      { RuntimeError.new().detailed_message }
show("empty rt bottom") { RuntimeError.new("").full_message(highlight: false, order: :bottom).sub(/\A.*\n/, "") }
show("empty rt top")    { RuntimeError.new("").full_message(highlight: false, order: :top).lines[0].sub(/\A.*matrix\.rb[^ ]* /, "") }
show("detailed extra kw") { RuntimeError.new("new error").detailed_message(foo: true) }
show("std empty")       { StandardError.new("").detailed_message }
show("std empty hl")    { StandardError.new("").detailed_message(highlight: true) }
show("rt empty hl")     { RuntimeError.new("").detailed_message(highlight: true) }
