# Regression checks for block scope reuse against re-entrant calls of the same block from a
# Signal.trap handler or an ObjectSpace finalizer. Both run at a safe point - a block entry is
# one - so they can start a call of a block while another call of it is being entered. A scope
# and its locals tuple must only ever be reused together: a call must never see the locals of
# another call, nor share its tuple with a Binding that another call captured.
#
#   ruby   Util/block-scope-reuse-race.rb
#   ./ir.sh Util/block-scope-reuse-race.rb      (also with -X:NoJIT -X:NoOSR)
#
# Every line must end in "stale 0 leaked 0" (CRuby prints the same); the exit status is 1 otherwise.
# ROUNDS=n scales the work (CRuby runs a trap handler only at its next thread switch: fewer rounds).

ROUNDS = (ENV['ROUNDS'] || (RUBY_ENGINE == 'ruby' ? 20 : 200)).to_i
$failed = false
def report(name, stale, leaked = 0)
  $failed = true unless stale == 0 && leaked == 0
  puts "#{name}: stale #{stale} leaked #{leaked}"
end

# Fires a trap handler (or, with :gc, a finalizer) while the block runs over and over, and returns
# whether the handler ran.
def interrupt_while(kind)
  $hit = false
  th = Thread.new do
    if kind == :gc
      ObjectSpace.define_finalizer(Object.new, FIN)
      GC.start
    else
      sleep 0.002
      Process.kill(:USR1, Process.pid)
    end
  end
  t0 = Time.now
  yield until $hit || Time.now - t0 > 2
  th.join
  $hit
end

# a local of the block that every call reads before assigning it: always nil
PR = proc { |m| z = 0 if false; r = z; z = m; $b = binding if m == :cap; r }
5.times { PR.call(1) }

class Svc
  define_method(:step) { |m| seen = nil if false; prev = seen; seen = m; raise "fail" if m == :h; prev }
end
SVC = Svc.new
5.times { SVC.step(1) }

$stale = 0
EACH = proc { |m| w = 0 if false; r = w; w = m; $b = binding if m == :cap; $stale += 1 unless r.nil?; r }
ARR = [1, 2, 3, 4, 5, 6, 7, 8]
ARR.each(&EACH)

FIN = proc { |_| $hit = true; PR.call(:cap) }

if Signal.list.key?('USR1')
  # Proc#call: the handler's call captures its scope in a Binding
  Signal.trap(:USR1) { $hit = true; PR.call(:cap) }
  stale = leaked = 0
  ROUNDS.times do
    $b = nil
    interrupt_while(:signal) { stale += 1 unless PR.call(2).nil? }
    next unless $b
    4.times { PR.call(3) }
    leaked += 1 unless $b.local_variable_get(:z) == :cap
  end
  report :trap_proc_call, stale, leaked

  # define_method: the handler's call raises after assigning a local
  Signal.trap(:USR1) { $hit = true; SVC.step(:h) rescue nil }
  stale = 0
  ROUNDS.times { interrupt_while(:signal) { v = SVC.step(2); stale += 1 unless v.nil? } }
  report :trap_define_method, stale

  # each: repeated calls through one BlockParam, interrupted by calls of the same block
  Signal.trap(:USR1) { $hit = true; EACH.call(:cap) }
  $stale = 0
  leaked = 0
  ROUNDS.times do
    $b = nil
    interrupt_while(:signal) { ARR.each(&EACH) }
    next unless $b
    ARR.each(&EACH)
    leaked += 1 unless $b.local_variable_get(:w) == :cap
  end
  report :trap_each, $stale, leaked
  Signal.trap(:USR1, 'DEFAULT')
end

# a finalizer calling the proc
stale = leaked = 0
(ROUNDS / 2).times do
  $b = nil
  interrupt_while(:gc) { stale += 1 unless PR.call(2).nil? }
  next unless $b
  4.times { PR.call(3) }
  leaked += 1 unless $b.local_variable_get(:z) == :cap
end
report :finalizer_proc_call, stale, leaked

exit 1 if $failed
