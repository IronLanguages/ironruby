# Differential matrix for a #set_backtrace of the exception's own, which a raise calls (MRI's
# setup_exception): it is called once per raise, #backtrace is what it leaves (nil unless it
# calls super), #backtrace_locations is the raise's either way, and an exception it raises is
# dropped - the exception raised is still the one rescued, also from a thread.
#   ruby    Util/set-backtrace-matrix.rb > /tmp/cruby-sbt.txt
#   ./ir.sh Util/set-backtrace-matrix.rb > /tmp/ir-sbt.txt
#   diff -u /tmp/cruby-sbt.txt /tmp/ir-sbt.txt
# CRuby before 3.4 labels frames `d1' rather than 'Object#d1'; the labels are left out here.
$n = 0
class A < StandardError; def set_backtrace(b); $n += 1; $a = b.class; super; end; end
class B < StandardError; def set_backtrace(b); $n += 1; end; end
class C < StandardError; def set_backtrace(b) = raise("sb"); end
class D < StandardError; def set_backtrace(b); super(["custom:1"]); end; end
def d3(k) = d2(k)
def d2(k) = d1(k)
def d1(k) = raise(k, "deep")

def show(label)
  puts "#{label}: #{(yield).inspect}"
rescue Exception => e
  puts "#{label}! #{e.class}: #{e.message.inspect}"
end

[A, B, C, D].each do |k|
  $n = 0
  show("#{k} deep") do
    begin; d3(k); rescue Exception => e
      [e.class, e.message, $n, e.backtrace&.first(3)&.map { |l| l[/:(\d+):/, 1].to_i },
       e.backtrace_locations&.first(3)&.map(&:lineno)]
    end
  end
  show("#{k} block") do
    begin; [1].each { raise k, "blk" }; rescue Exception => e; [e.class, e.message, e.backtrace.class]; end
  end
end
show("A got") { $a }
show("thread") do
  t = Thread.new { Thread.current.report_on_exception = false; raise C, "t" }
  begin; t.join; rescue Exception => e; [e.class, e.message]; end
end
show("reraise") do
  $n = 0
  begin; d3(B); rescue B => e
    begin; raise e; rescue B => e2; [$n, e2.backtrace, e2.backtrace_locations&.first&.lineno]; end
  end
end
show("full_message") do
  begin; d3(B); rescue B => e; e.full_message(highlight: false).lines.first[/: .*/]; end
end
