# Differential matrix for ConditionVariable#wait and Mutex#sleep: timeouts, results, and what a
# failed or interrupted wait leaves behind.
#   ruby Util/condvar-matrix.rb            # CRuby reference
#   ./ir.sh Util/condvar-matrix.rb         # IronRuby
# Diff the two outputs; every differing line is a bug.

def show(label)
  v = begin
        r = yield
        r.inspect
      rescue Exception => e
        "!#{e.class}: #{e.message}"
      end
  puts "#{label.ljust(48)} #{v}"
end

M = Mutex.new
CV = ConditionVariable.new

# Timeout conversion: #wait is mutex.sleep(timeout), so both answer alike
[0, 0.01, -0.0, -1, -0.5, -Float::INFINITY, Float::INFINITY, Float::NAN, 2**70, "1", :x].each do |t|
  label = t.is_a?(Float) && (t.nan? || t.infinite?) ? t.to_s : t.inspect
  show("cv.wait(m, #{label})") { M.synchronize { CV.wait(M, t) } }
  show("m.sleep(#{label})") { M.synchronize { M.sleep(t) } }
end

# Anything else is an interval if #divmod(1) answers [whole, fraction], as for Rational and BigDecimal
require "bigdecimal"
Divmod = Struct.new(:whole, :fraction) do
  def divmod(_) = [whole, fraction]
end
[Rational(1, 100), Rational(-1, 2), BigDecimal("0.01"), Divmod.new(0, 0.01), Divmod.new(0, Rational(1, 100)),
 Divmod.new(-1, 0), Divmod.new(2**70, 0), Complex(1, 0), Object.new].each do |t|
  label = t.is_a?(Divmod) ? "divmod -> #{[t.whole, t.fraction].inspect}" : t.instance_of?(Object) ? "Object" : t.inspect
  show("cv.wait(m, #{label})") { M.synchronize { CV.wait(M, t) } }
  show("m.sleep(#{label})") { M.synchronize { M.sleep(t) } }
end

# An Integer past 32 bits is still a timeout (a long one), not a TypeError
show("m.sleep(2**40) is still sleeping") do
  t = Thread.new { M.synchronize { M.sleep(2**40) } }
  Thread.pass until t.stop?
  status = t.status
  t.kill.join
  status
end

# Timed out: nil. Signalled: the seconds slept, an Integer.
show("timed out") { M.synchronize { CV.wait(M, 0.01) } }
show("signalled") do
  t = Thread.new { M.synchronize { CV.wait(M, 5) } }
  Thread.pass until t.stop?
  M.synchronize { CV.signal }
  t.value.class
end

# A #wait on a mutex the thread does not hold raises and must not stay in line: the next #signal
# belongs to a real waiter, and the failed caller's next sleep must still time out.
show("wait unlocked") { CV.wait(M) }
show("signal after a failed wait wakes the waiter") do
  t = Thread.new { M.synchronize { CV.wait(M, 2) } }
  Thread.pass until t.stop?
  M.synchronize { CV.signal }
  t.value.class
end
show("then m.sleep(0.01) still times out") { M.synchronize { M.sleep(0.01) } }

# The mutex is held again in rescue/ensure after Thread#raise and Thread#kill
show("raise while waiting") do
  seen = nil
  t = Thread.new { M.synchronize { begin; CV.wait(M); rescue => e; seen = [e.message, M.owned?]; end } }
  Thread.pass until t.stop?
  t.raise "boom"
  t.join
  [seen, M.locked?]
end
show("kill while waiting") do
  seen = nil
  t = Thread.new { M.synchronize { begin; CV.wait(M, 10); ensure; seen = M.owned?; end } }
  Thread.pass until t.stop?
  t.kill
  t.join
  [seen, M.locked?]
end
show("broadcast wakes all") do
  ts = 3.times.map { Thread.new { M.synchronize { CV.wait(M, 5) } } }
  Thread.pass until ts.all?(&:stop?)
  M.synchronize { CV.broadcast }
  ts.map { |t| t.value.class }
end
