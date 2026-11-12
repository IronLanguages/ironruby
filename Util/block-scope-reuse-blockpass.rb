# Differential checks for block scope reuse together with literal-block procs (block-pass):
# break/next/return through reused scopes, scopes kept on the BlockParam (in its break-target
# slot), separate yields of one block (a BlockParam each), proc identity, escaped procs and bindings,
# threads and fibers. Every block body here makes calls, so it takes the reuse path; the output
# must match CRuby, with and without -X:NoJIT -X:NoOSR:
#
#   ruby   Util/block-scope-reuse-blockpass.rb > /tmp/cruby.txt
#   ./ir.sh Util/block-scope-reuse-blockpass.rb > /tmp/ir.txt
#   diff /tmp/cruby.txt /tmp/ir.txt

def show(label, v) = puts("#{label}: #{v.inspect}")
# block-pass interactions with block scope reuse: every body makes calls
def loop_yield(n) i = 0; r = []; while i < n; r << yield(i); i += 1; end; r; end
def two_yields; [yield(1), yield(2)]; end
def three_yields; [yield(1), yield(2), yield(3)]; end

show :next_mid, loop_yield(6) { |x| y = x * 2; next(-y) if x == 2; y.to_s }
show :break_mid, loop_yield(6) { |x| y = x * 3; break [:brk, y] if x == 3; y }
show :break_then_again, 3.times.map { |k| loop_yield(5) { |x| z = x + k; break z if x == k + 1; z.to_s } }
def ret_from_block; loop_yield(5) { |x| w = x.succ; return [:ret, w] if x == 2; w }; :no; end
show :return_through, ret_from_block
show :two_yields, two_yields { |x| a = x * 10; a.to_s }
show :three_yields, three_yields { |x| b = defined?(b).inspect; b = x; [b, x.succ] }
show :locals_nil, loop_yield(4) { |x| q = (q.nil? ? :fresh : q); q = x; [q, x.pred] }

# proc identity / escaped procs
def keep(&b) b end
pr = keep { |x| v = x * 2; v + 1 }
show :pr_identity, [pr.equal?(keep(&pr)), pr.lambda?, 5.times.map { |i| pr.call(i) }]
procs = []; loop_yield(4) { |x| t = x * 2; procs << proc { [x, t] } if x.odd?; t }
show :escaped, procs.map(&:call)
bs = []; 4.times { |x| m = x.to_s; bs << binding if x == 2; m }
show :binding_mid, bs.map { |b| [b.local_variable_get(:m), b.local_variable_get(:x)] }
pr2 = proc { |x| s = x.to_s; break s * 2 if x == 3; s }
show :proc_break, (begin; 5.times.map { |i| pr2.call(i) }; rescue LocalJumpError => e; [e.class, e.reason]; end)
l = lambda { |x| u = x * 2; return u if x > 1; u.succ }
show :lambda_return, 4.times.map { |i| l.call(i) }
f = proc { |n| t = n * 1; n == 0 ? 0 : t + f.call(n - 1) }
show :recursion, [f.call(10), f.call(5), f.call(20)]
class DM; define_method(:dm) { |x| k = x * 2; k.to_s }; define_method(:rec) { |n| j = n; n == 0 ? [] : [j] + rec(n - 1) }; end
show :define_method, [5.times.map { |i| DM.new.dm(i) }, DM.new.rec(4)]
# then/tap/synchronize single-call library blocks, repeated
mu = Mutex.new
show :then_tap, 4.times.map { |i| [i.then { |y| y * 2 }, i.tap { |y| y * 3 }, mu.synchronize { z = i.succ; z.to_s }] }
show :then_break, 3.times.map { |i| i.then { |y| break y * 100 if y == 1; y.to_s } }
# threads: one proc called on several threads, one blockparam per thread
pt = proc { |x| a = x * 2; Thread.pass; [a, x] }
show :threads, 4.times.map { |t| Thread.new { 300.times.map { |i| pt.call(i + t)[0] }.sum } }.map(&:value)
show :thread_each, 3.times.map { |t| Thread.new { r = 0; 2000.times { |i| c = i + t; r += c } ; r } }.map(&:value)
# fibers: each driven externally, the same block across fiber switches
e = [1, 2, 3, 4].each_with_index.lazy.map { |x, i| s = x * i; s.to_s }
show :lazy, e.first(3)
en = Enumerator.new { |y| 5.times { |i| w = i * i; y << w } }
show :ext_next, [en.next, en.next, en.peek, en.next, en.take(2)]
fb = Fiber.new { 3.times { |i| v = i * 7; Fiber.yield v }; :done }
show :fiber, 4.times.map { fb.resume }
fs = 2.times.map { |k| Fiber.new { loop_yield(3) { |x| Fiber.yield(x + k); x * 10 } } }
show :fibers_interleaved, 4.times.flat_map { fs.map { |f| f.alive? ? f.resume : nil } }
# exception through a reused scope, then reuse again
show :raise_mid, loop_yield(5) { |x| begin; g = x * 2; raise "e#{g}" if x == 2; g; rescue => ex; ex.message; end }
show :raise_out, (loop_yield(5) { |x| h = x; raise ArgumentError, h.to_s if x == 3; h } rescue $!.message)
show :catch_throw, catch(:t) { loop_yield(5) { |x| c = x * 4; throw :t, c if x == 2; c } }
# block with &blk passed further (block-pass) in a loop
def pass_on(&b) [1, 2, 3].map(&b) end
show :pass_on, 3.times.map { |k| pass_on { |x| d = x + k; d.to_s } }
def yield_pass(&b) loop_yield(3, &b) end
show :yield_pass, yield_pass { |x| e2 = x * 5; e2 }
# $~ and $_ scoping in reused scopes
show :match, loop_yield(4) { |x| m = "a#{x}b" =~ /\d/; [m, $~ && $~[0]] }
show :match_nil, loop_yield(3) { |x| x.even? ? ("q" =~ /z/) : 1; $~.inspect }
# local_variables / eval in reused scopes
show :eval, loop_yield(3) { |x| eval("ev = x * 2; ev") + (defined?(ev) ? 1 : 0) }
show :lv, loop_yield(2) { |x| r1 = local_variables.sort; nn = 1; [r1, local_variables.sort] }
def four(x) [yield(x), yield(x + 1), yield(x + 2), yield(x + 3)]; end
def five_sep(x) a = yield(x); b = yield(x + 1); c = yield(x + 2); d = yield(x + 3); e = yield(x + 4); [a, b, c, d, e]; end
# separate yields of one block: each yield has a BlockParam of its own
show :four, four(1) { |y| q = defined?(q).inspect; z = y * 2; [q, z] }
show :five_sep, five_sep(1) { |y| w = (w || :nil); w2 = y; [w, w2.to_s] }
bnd = []
show :five_bind, five_sep(10) { |y| t = y * 3; bnd << binding if y == 12; t }
show :five_bind_vals, bnd.map { |b| [b.local_variable_get(:t), b.local_variable_get(:y)] }
pr = proc { |x| y = x * 2; $b = binding if x == 3; y }
show :pr_calls, 6.times.map { |i| pr.call(i) }
show :pr_binding_kept, [$b.local_variable_get(:y), $b.local_variable_get(:x)]
pq = proc { |x| u = x; u }
6.times { |i| pq.call(i) }
show :pr_threads, 3.times.map { |t| Thread.new { 50.times.map { |i| pq.call(i * t) }.sum } }.map(&:value)
show :pr_after_threads, 4.times.map { |i| pq.call(i) }
# breaks/returns after a scope was parked on the BlockParam
show :break_late, loop_yield(8) { |x| v = x * 5; break [:late, v] if x == 5; v }
def ret_late; loop_yield(8) { |x| v = x.succ; return [:ret_late, v] if x == 6; v }; :none; end
show :ret_late, ret_late
show :lib_break_late, (1..10).each { |x| y = x * x; break y if x == 7 }
show :lib_next_late, (1..6).map { |x| y = x * x; next -y if x > 3; y }
show :lib_after_break, 3.times.map { (1..10).each { |x| y = x; break y if x == 4 } }
# break target vs parked scope: the break from a later call targets the right frame
def outer; [1, 2, 3].each { |a| loop_yield(5) { |x| c = x + a; break c if x == 3 } }; :done; end
show :nested_break, outer
r = []
def inner_break; [1, 2].map { |k| loop_yield(6) { |x| m = x * k; break m if x == 4; m } }; end
show :inner_break, inner_break
# a lambda yielded repeatedly by a library method, returns
lam = lambda { |x| l2 = x * 2; return l2 if x > 2; -l2 }
show :lambda_each, [1, 2, 3, 4].map(&lam)
# exceptions after parking leave the scope behind, then reuse resumes
show :raise_late, loop_yield(7) { |x| begin; e = x; raise "x" if x == 4; e; rescue; :resc; end }
show :raise_through, (begin; loop_yield(7) { |x| e = x; raise "out#{e}" if x == 5; e }; rescue => ex; ex.message; end)
show :after_raise, loop_yield(4) { |x| e = x * 2; e }
# recursion through one BlockParam (each_with_object with recursive lambda)
fib = proc { |n| a = n; a < 2 ? a : fib.call(a - 1) + fib.call(a - 2) }
show :fib, (0..12).map { |i| fib.call(i) }
def walk(t, &b) t.is_a?(Array) ? t.map { |c| walk(c, &b) } : b.call(t) end
show :walk, walk([1, [2, [3, 4]], 5, [6]]) { |v| s = v * 10; s }
# Enumerator over a reused block via external next across fibers
en = (1..8).each_slice(3)
show :ext, [en.next, en.next, en.next, (en.next rescue :stop)]
e2 = Enumerator.new { |y| i = 0; loop { k = i * 2; y << k; i += 1 } }
show :ext2, [e2.next, e2.next, e2.next, e2.take(4)]
# $~ in reused scopes across calls
show :match_reuse, loop_yield(5) { |x| ("ab#{x}" =~ /b(\d)/) && $1 }
show :match_after, loop_yield(4) { |x| x.even? ? ("z" =~ /q/) : ("q#{x}" =~ /q(.)/); $~ && $1 }
# ObjectSpace / trace_var / TracePoint capturing late
tps = []
tp = TracePoint.new(:b_call) { |t| tps << t.binding.local_variables.size if tps.size < 3 }
loop_yield(3) { |x| y = x; y }
tp.enable { loop_yield(3) { |x| y = x; y } }
show :tracepoint, tps
show :after_tp, loop_yield(4) { |x| g = x; local_variables.size + g }
