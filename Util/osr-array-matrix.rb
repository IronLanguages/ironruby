# Differential matrix for what an outlined (OSR) loop does with Arrays, constants, Float#to_i and
# values of no one type (Src/Ruby/Runtime/Jit/JitCompiler.cs: EmitArrayRead, EmitArrayStore,
# EmitConstant; JitUndoLog). Run it under CRuby and under ./ir.sh with OSR on and off, and diff:
#
#   ruby Util/osr-array-matrix.rb > /tmp/mri.txt
#   ./ir.sh Util/osr-array-matrix.rb > /tmp/osr.txt
#   ./ir.sh -X:NoOSR Util/osr-array-matrix.rb > /tmp/noosr.txt
#   diff /tmp/mri.txt /tmp/osr.txt && diff /tmp/mri.txt /tmp/noosr.txt
#
# All three must be identical. Every loop below runs well past the OSR threshold (2000 back
# edges) before it reaches the case it probes, so the specialized copy is what meets it: a deopt
# part way through an iteration that has already stored into an Array (whose stores must be
# undone before the iteration runs again), an element of another type than the ones sampled, an
# index past either end, a frozen Array, an Array subclass or one with a singleton class, Array#[]
# redefined, a constant reassigned, a Float with no Int64 value. To see which loops specialize:
#
#   IR_OSR_VERBOSE=1 ./ir.sh Util/osr-array-matrix.rb 2>&1 >/dev/null | grep -a 'osr\]'

def t(label)
  print label.ljust(60), ' => '
  begin
    v = yield
    puts "#{v.inspect} (#{v.class})"
  rescue Exception => e
    puts e.class
  end
end

N = 6000
W = 64

# ---------------------------------------------------------------- 1. reads, stores, Float#to_i

def span(fb, row, ds, co, flat, cmap, perp, px)
  x = 0
  o = row * W
  while x < W
    rd = perp * ds[x]
    tx = (px + (rd * co[x])).to_i & 15
    fb[o + x] = cmap[flat[(tx * 4) + (x & 3)]]
    x += 1
  end
end

fb = Array.new(W * 200, 0)
ds = Array.new(W) { |i| 1.0 + i * 0.01 }
co = Array.new(W) { |i| Math.cos(i * 0.1) }
flat = Array.new(64) { |i| (i * 7) % 256 }
cmap = Array.new(256) { |i| 255 - i }
200.times { |r| span(fb, r, ds, co, flat, cmap, 3.5 + r, 17.25) }
t('span: sum of the framebuffer') { fb.sum }
t('span: a row') { fb[W * 150, 12] }

def copy_mapped(dst, src, map)
  i = 0
  n = src.size
  while i < n
    dst[i] = map[src[i]]
    i += 1
  end
  dst
end
src = Array.new(N) { |i| i % 10 }
t('dst[i] = map[src[i]]') { copy_mapped(Array.new(N), src, %w[a b c d e f g h i j]).last(12) }

# ---------------------------------------------------------------- 2. a deopt after a store

def bump_then_add(a, b)
  s = 0
  i = 0
  n = a.size
  while i < n
    a[i] = a[i] + 1        # stored before ...
    s += b[i]              # ... b[i] stops being an Integer: deopt, the store undone, the iteration re-run
    i += 1
  end
  s
end
a = Array.new(N, 10)
b = Array.new(N, 1)
# off the points ElementType samples (every 375th), so b is sampled as all Integers
b[4501] = 0.5
b[5501] = 2**64
t('read-modify-write, then a Float and a Bignum') { bump_then_add(a, b) }
t('every element bumped exactly once') { a.tally.to_a }

def store_then_overflow(a, k)
  acc = 1
  limit = 2**62             # outside the loop: the emitter has no Integer#**
  i = 0
  while i < a.size
    a[i] = i
    acc = acc * k           # out of Int64 at some point: deopt after the store
    acc = 1 if acc > limit  # so acc * k leaves Int64 before the reset
    i += 1
  end
  [acc, a.sum]
end
t('store, then Integer overflow') { store_then_overflow(Array.new(N, -1), 3) }

def two_stores(a, b, poison)
  i = 0
  while i < a.size
    a[i] = a[i] * 2
    b[i] = b[i] + a[i]
    x = poison[i] * 2      # an Integer until it is nil
    i += 1
  end
  [a.sum, b.sum, x]
end
poison = Array.new(N, 1)
poison[N - 11] = nil
a = Array.new(N, 3)
b = Array.new(N, 1)
t('two stores, then nil') { two_stores(a, b, poison) }
# the iteration that met nil stored into both before it raised: each store exactly once
t('both arrays around the failed iteration') { [a[N - 12, 3], b[N - 12, 3], a.sum, b.sum] }

# ---------------------------------------------------------------- 3. elements, indexes

def mixed(col, out, cmap)
  i = 0
  while i < col.size
    c = col[i]                 # nil now and then: an element of no one type
    out[i] = cmap[c] if c      # ... used as an index once it is known not to be nil
    out[i] = -1 unless c
    i += 1
  end
  out
end
cmap50 = Array.new(50) { |i| i * 100 }
col = Array.new(N) { |i| i % 7 == 0 ? nil : i % 50 }
t('nil elements, if c, cmap[c]') { mixed(col, Array.new(N, 0), cmap50).first(15) }
col2 = Array.new(N) { |i| i % 7 == 0 ? nil : i % 50 }
col2[5000] = 2.75              # a Float index: Array#[] truncates it
t('a Float where an Integer was sampled') { mixed(col2, Array.new(N, 0), cmap50)[4998..5002] }
col3 = col2.dup
col3[5001] = 'str'
t('a String index') { mixed(col3, Array.new(N, 0), cmap50) }

def indexes(a)
  s = 0
  misses = 0
  i = 0
  while i < N
    v = a[i - 2500]          # negative, then in range, then past the end - all after OSR
    s += v if v
    misses += 1 unless v
    i += 1
  end
  [s, misses]
end
t('negative and past-the-end indexes') { indexes(Array.new(3000) { |i| i * 3 }) }

def float_index(a)
  s = 0
  i = 0
  while i < N
    s += a[(i % 10) + 0.5]   # Array#[] truncates a Float index
    i += 1
  end
  s
end
t('a Float index') { float_index((0..20).to_a) }

def bignum_index(a, big)
  s = 0
  i = 0
  while i < N
    v = a[i < 5000 ? i % 10 : big]
    s += v ? v : 1000
    i += 1
  end
  s
end
t('an index out of Int32') { bignum_index((0..20).to_a, 2**40) }

def grow(a)
  i = 0
  while i < N
    a[i] = i                 # past the end from 3000 on: the generic loop grows the array
    i += 1
  end
  [a.size, a.sum]
end
t('a store past the end') { grow(Array.new(3000, 0)) }

def negative_store(a)
  i = 0
  while i < N
    a[-1 - (i % 100)] = i
    i += 1
  end
  a.last(5)
end
t('negative store indexes') { negative_store(Array.new(200, 0)) }

def store_all(a)
  i = 0
  while i < N
    a[i % 100] = i
    i += 1
  end
  a.sum
end
t('store_all, warm') { store_all(Array.new(100, 0)) }
t('store_all into a frozen Array') { store_all(Array.new(100, 0).freeze) }

# ---------------------------------------------------------------- 4. not plain Arrays

class MyArray < Array
  def [](i) = 42
end
def sum_all(a)
  s = 0
  i = 0
  while i < N
    s += a[i % a.size]
    i += 1
  end
  s
end
t('plain Array') { sum_all((1..10).to_a) }
t('Array subclass with its own #[]') { sum_all(MyArray.new((1..10).to_a)) }
s = (1..10).to_a
def s.[](i) = 7
t('Array with a singleton #[]') { sum_all(s) }
t('Integer#[] is not Array#[]') do
  n = 0b1011_0110
  r = 0
  i = 0
  while i < N
    r += n[i % 8]
    i += 1
  end
  r
end

def sum_hot(a)
  s = 0
  i = 0
  while i < N
    s += a[i % 10]
    i += 1
  end
  s
end
t('before Array#[] is redefined') { sum_hot((1..10).to_a) }
class Array
  alias_method :__osr_matrix_get, :[]
  def [](*args) = (args == [3] ? 1000 : __osr_matrix_get(*args))
end
t('after Array#[] is redefined') { sum_hot((1..10).to_a) }
class Array
  alias_method :[], :__osr_matrix_get
  remove_method :__osr_matrix_get
end
t('after it is put back') { sum_hot((1..10).to_a) }

# ---------------------------------------------------------------- 5. constants

module Grid
  WIDTH = 8
  def self.fill(a)
    y = 0
    while y < N / WIDTH
      a[y * WIDTH] = y + WIDTH
      y += 1
    end
    a.sum
  end
end
t('constant in the loop') { Grid.fill(Array.new(N, 0)) }
# 750 iterations a call and the countdown is the loop's: two more calls get it specialized
# with WIDTH = 8 compiled in, so the reassignment below meets a copy that is already there
2.times { Grid.fill(Array.new(N, 0)) }
Grid.send(:remove_const, :WIDTH)
Grid.const_set(:WIDTH, 3)
t('constant reassigned') { Grid.fill(Array.new(N, 0)) }

# ---------------------------------------------------------------- 6. Float#to_i at the edges

def truncs(fs)
  r = 0
  i = 0
  while i < N
    r += fs[i % fs.size].to_i
    i += 1
  end
  r
end
t('Float#to_i, both signs') { truncs([1.9, -1.9, 0.5, -0.5, 1e9, -1e9]) }
t('Float#to_i past Int64') { truncs([1.5] * 50 + [1e30]) }
t('Float#to_i of NaN') { truncs([1.5] * 50 + [Float::NAN]) }
t('Float#to_i of Infinity') { truncs([1.5] * 50 + [Float::INFINITY]) }

# ---------------------------------------------------------------- 7. the loop writes the local

def rebinds(a, b)
  s = 0
  i = 0
  while i < N
    s += a[i % 5]
    a = b if i == 3000
    i += 1
  end
  s
end
t('the indexed local reassigned in the loop') { rebinds([1, 2, 3, 4, 5], [10, 20, 30, 40, 50]) }

def not_obj(a)
  r = 0
  i = 0
  while i < N
    c = a[i % 3]
    r += 1 if !c
    i += 1
  end
  r
end
t('!x of an element') { not_obj([1, nil, false]) }

# ---------------------------------------------------------------- 8. threads

t('threads storing at once') do
  # block-local: the top level has an `a' and a `b' of its own, which the threads would share
  (0...4).map { |k|
    Thread.new { |; mine, other|
      mine = Array.new(N, 0)
      other = Array.new(N, k)
      other[N - 101] = 0.25
      bump_then_add(mine, other)
      mine.sum
    }
  }.map(&:value)
end
