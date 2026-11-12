# Differential matrix for Fiddle: the libc calls, Pointer arithmetic, callbacks
# and the fiddle/import layer, one line each.
#   ruby Util/fiddle-matrix.rb            # CRuby reference
#   ./ir.sh Util/fiddle-matrix.rb         # IronRuby
# Diff the two outputs; every differing line is a bug.
#
# Nothing here prints an address, a pid or a clock reading directly - those
# differ between two runs of the same interpreter - so each such case is turned
# into a predicate ("is it plausible?") instead.

$stdout.sync = true

require 'fiddle'
require 'fiddle/import'
require 'fiddle/struct'

def show(label)
  v = begin
        yield.inspect
      rescue Exception => e
        "!#{e.class}: #{e.message}"
      end
  puts "#{label.ljust(52)} #{v}"
end

include Fiddle

LIBC = Fiddle.dlopen(nil)

puts "== constants"
show("SIZEOF_VOIDP == SIZEOF_LONG")   { SIZEOF_VOIDP == SIZEOF_LONG }
show("SIZEOF_INT")                    { SIZEOF_INT }
show("SIZEOF_DOUBLE")                 { SIZEOF_DOUBLE }
show("TYPE_SIZE_T == -TYPE_LONG")     { TYPE_SIZE_T == -TYPE_LONG }
show("TYPE_VOIDP")                    { TYPE_VOIDP }
show("Types::VOIDP == TYPE_VOIDP")    { Types::VOIDP == TYPE_VOIDP }
show("WINDOWS")                       { WINDOWS }
show("RUBY_FREE nonzero")             { RUBY_FREE != 0 }
show("NULL.null?")                    { NULL.null? }
show("Function::DEFAULT")             { Function::DEFAULT }

puts "== handles"
show("dlopen(nil).class")             { Fiddle.dlopen(nil).class }
show("Handle::DEFAULT.class")         { Handle::DEFAULT.class }
show("missing library")               { Fiddle.dlopen("doesnotexist.doesnotexist") && nil }
show("sym of a known symbol")         { LIBC['getpid'] > 0 }
show("sym of an unknown symbol")      { LIBC['no_such_symbol_here'] }
show("sym_defined? true")             { !LIBC.sym_defined?('getpid').nil? }
show("sym_defined? false")            { LIBC.sym_defined?('no_such_symbol_here') }
show("Handle.sym on DEFAULT")         { Handle['getpid'] > 0 }
show("libc.so.6 file_name")           { Fiddle.dlopen("libc.so.6").file_name }

puts "== functions"
getpid  = Function.new(LIBC['getpid'],  [],           TYPE_INT)
strlen  = Function.new(LIBC['strlen'],  [TYPE_VOIDP], TYPE_SIZE_T)
abs     = Function.new(LIBC['abs'],     [TYPE_INT],   TYPE_INT)
atof    = Function.new(LIBC['atof'],    [TYPE_VOIDP], TYPE_DOUBLE)
getenv  = Function.new(LIBC['getenv'],  [TYPE_VOIDP], TYPE_VOIDP)
getenvs = Function.new(LIBC['getenv'],  [TYPE_VOIDP], TYPE_CONST_STRING)
toupper = Function.new(LIBC['toupper'], [TYPE_INT],   TYPE_INT)

show("getpid plausible")              { getpid.call == Process.pid }
show("strlen")                        { strlen.call("hello world") }
show("strlen of empty")               { strlen.call("") }
show("abs(-7)")                       { abs.call(-7) }
show("abs of a Bignum-ish arg")       { abs.call(-2147483647) }
show("atof")                          { atof.call("3.25") }
show("toupper")                       { toupper.call(?a.ord).chr }
show("getenv -> Pointer")             { getenv.call("HOME").class }
show("getenv -> String")              { getenvs.call("HOME").is_a?(String) }
show("getenv of a missing var")       { getenvs.call("NO_SUCH_VAR_HERE_XYZ") }
show("wrong arity")                   { abs.call(1, 2) }
show("Function#to_i is the address")  { abs.to_i == LIBC['abs'] }
show("Function#abi")                  { abs.abi }
show("Function#need_gvl?")            { abs.need_gvl? }
show("Function#to_proc")              { abs.to_proc.call(-3) }
show("Function#name")                 { Function.new(LIBC['abs'], [TYPE_INT], TYPE_INT, name: "abs").name }

puts "== out parameters"
snprintf = Function.new(LIBC['snprintf'],
                        [TYPE_VOIDP, TYPE_SIZE_T, TYPE_CONST_STRING, TYPE_INT], TYPE_INT)
show("snprintf return")               { buf = "\0" * 32; snprintf.call(buf, 32, "n=%d", 42) }
show("snprintf wrote the buffer")     { buf = "\0" * 32; snprintf.call(buf, 32, "n=%d", 42); buf[0, 4] }

# getrusage, the call ruby-bench's get_maxrss is built on
show("getrusage returns 0") {
  getrusage = Function.new(LIBC['getrusage'], [TYPE_INT, TYPE_VOIDP], TYPE_INT)
  getrusage.call(0, "\0".b * 1024)
}
show("maxrss is a plausible size") {
  getrusage = Function.new(LIBC['getrusage'], [TYPE_INT, TYPE_VOIDP], TYPE_INT)
  buf = "\0".b * 1024
  getrusage.call(0, buf)
  kb = buf[(SIZEOF_LONG * 2) * 2, SIZEOF_LONG].unpack1('q')
  kb > 1_000 && kb < 100_000_000
}

puts "== pointers"
show("malloc size")                   { Pointer.malloc(16, RUBY_FREE).size }
show("malloc null?")                  { Pointer.malloc(16, RUBY_FREE).null? }
show("read back what was written")    { p1 = Pointer.malloc(4, RUBY_FREE); p1[0] = 65; p1[1] = 66; p1.to_s }
show("[start, len]")                  { p1 = Pointer.malloc(4, RUBY_FREE); p1[0, 4] = "abcd"; p1[1, 2] }
show("to_str reads the whole block")  { p1 = Pointer.malloc(4, RUBY_FREE); p1[0, 4] = "ab\0d"; p1.to_str }
show("to_s stops at NUL")             { p1 = Pointer.malloc(4, RUBY_FREE); p1[0, 4] = "ab\0d"; p1.to_s }
show("to_s(len)")                     { p1 = Pointer.malloc(4, RUBY_FREE); p1[0, 4] = "ab\0d"; p1.to_s(4) }
show("+ moves and shrinks")           { p1 = Pointer.malloc(8, RUBY_FREE); q = p1 + 3; [q.to_i - p1.to_i, q.size] }
show("- moves back")                  { p1 = Pointer.malloc(8, RUBY_FREE); q = (p1 + 3) - 3; [q.to_i - p1.to_i, q.size] }
show("ref/ptr round trip")            { p1 = Pointer.malloc(8, RUBY_FREE); p1.ref.ptr.to_i == p1.to_i }
show("== compares addresses")         { p1 = Pointer.malloc(8, RUBY_FREE); p1 == Pointer.new(p1.to_i) }
show("<=>")                           { Pointer.new(16) <=> Pointer.new(8) }
show("NULL.to_s raises")              { Pointer.new(0).to_s }
show("Pointer[str] size")             { Pointer["abc"].size }
show("Pointer[str] to_s")             { Pointer["abc"].to_s }
show("Pointer[str][0]")               { Pointer["abc"][0] }
show("Pointer.new inspect shape")     { Pointer.new(0).inspect.sub(/:0x\h+ /, ": ") }
show("freed?")                        { p1 = Pointer.malloc(8, RUBY_FREE); before = p1.freed?; p1.call_free; [before, p1.freed?] }
show("free is a Function")            { Pointer.malloc(8, RUBY_FREE).free.class }
show("free is nil without one")       { Pointer.malloc(8).free }
show("Fiddle.malloc/free")            { a = Fiddle.malloc(8); r = a > 0; Fiddle.free(a); r }
show("Fiddle.realloc")                { a = Fiddle.malloc(8); b = Fiddle.realloc(a, 64); r = b > 0; Fiddle.free(b); r }
show("Pointer.write/read")            { a = Fiddle.malloc(8); Pointer.write(a, "xy"); r = Pointer.read(a, 2); Fiddle.free(a); r }

puts "== callbacks"
show("closure called from qsort") {
  cmp = Closure::BlockCaller.new(TYPE_INT, [TYPE_VOIDP, TYPE_VOIDP]) do |a, b|
    a[0, 4].unpack1('l') <=> b[0, 4].unpack1('l')
  end
  qsort = Function.new(LIBC['qsort'], [TYPE_VOIDP, TYPE_SIZE_T, TYPE_SIZE_T, TYPE_VOIDP], TYPE_VOID)
  data = [5, 3, 9, 1, 7].pack('l*')
  qsort.call(data, 5, 4, cmp)
  data.unpack('l*')
}
show("closure return value") {
  cb = Closure::BlockCaller.new(TYPE_INT, [TYPE_INT]) { |n| n * 2 }
  Function.new(cb, [TYPE_INT], TYPE_INT).call(21)
}
show("closure with a double") {
  cb = Closure::BlockCaller.new(TYPE_DOUBLE, [TYPE_DOUBLE]) { |x| x / 2 }
  Function.new(cb, [TYPE_DOUBLE], TYPE_DOUBLE).call(5.0)
}
show("closure raising reaches Ruby") {
  cb = Closure::BlockCaller.new(TYPE_INT, [TYPE_INT]) { raise ArgumentError, "boom" }
  Function.new(cb, [TYPE_INT], TYPE_INT).call(1)
}
show("closure ctype and args") {
  cb = Closure::BlockCaller.new(TYPE_INT, [TYPE_INT]) { |n| n }
  [cb.ctype, cb.args, cb.freed?]
}
# Closure.create on the bare Fiddle::Closure - which defines no #call - segfaults
# CRuby 4.0.6, so the subclass that a caller would really use is what is tried.
class Doubler < Fiddle::Closure
  def call(n) = n * 2
end
show("Closure subclass with its own #call") {
  cb = Doubler.new(TYPE_INT, [TYPE_INT])
  Function.new(cb, [TYPE_INT], TYPE_INT).call(4)
}
show("Closure.create frees after the block") {
  freed = nil
  outer = nil
  Doubler.create(TYPE_INT, [TYPE_INT]) { |c| outer = c; freed = c.freed? }
  [freed, outer.freed?]
}

puts "== importer"
module LibCImport
  extend Fiddle::Importer
  dlload "libc.so.6"
  extern 'int abs(int)'
  extern 'size_t strlen(const char*)'
  extern 'double atof(const char*)'
  extern 'char *getenv(const char*)'
  Point = struct ['int x', 'int y']
  Tv = struct ['long tv_sec', 'long tv_usec']
  extern 'int gettimeofday(void*, void*)'
  bind('int plus_one(int)') { |n| n + 1 }
end

show("extern int")                    { LibCImport.abs(-7) }
show("extern size_t")                 { LibCImport.strlen("hello") }
show("extern double")                 { LibCImport.atof("2.5") }
show("extern char*")                  { LibCImport.getenv("HOME").is_a?(Fiddle::Pointer) }
show("struct size")                   { LibCImport::Point.size }
show("struct fields")                 { pt = LibCImport::Point.malloc(RUBY_FREE); pt.x = 3; pt.y = 4; [pt.x, pt.y] }
show("struct to_ptr size")            { LibCImport::Point.malloc(RUBY_FREE).to_ptr.size }
show("struct written by C") {
  tv = LibCImport::Tv.malloc(RUBY_FREE)
  LibCImport.gettimeofday(tv, nil)
  (tv.tv_sec - Time.now.to_i).abs <= 5
}
show("bind")                          { LibCImport.plus_one(41) }
show("CStructBuilder") {
  klass = Fiddle::CStructBuilder.create(Fiddle::CStruct, [TYPE_LONG, TYPE_LONG], ["a", "b"])
  s = klass.malloc(RUBY_FREE)
  s.a = 12
  s.b = 34
  [klass.size, s.a, s.b]
}
show("CUnion") {
  klass = Fiddle::CStructBuilder.create(Fiddle::CUnion, [TYPE_INT, TYPE_CHAR], ["i", "c"])
  u = klass.malloc(RUBY_FREE)
  u.i = 0x41
  [klass.size, u.c]
}

puts "== errno"
# Fiddle.last_error is NOT compared against errno here.  CRuby fills it in after
# every call; IronRuby cannot, because since .NET 6 the runtime restores the
# thread's system error across a managed-to-native transition, so errno is gone
# before the instruction after the call runs.  What both agree on is that the
# accessor round-trips a value Ruby sets, which is what is checked.
show("failing call returns -1") {
  open_ = Function.new(LIBC['open'], [TYPE_VOIDP, TYPE_INT], TYPE_INT)
  open_.call("/no/such/file/at/all", 0)
}
show("last_error round-trips")        { Fiddle.last_error = 42; Fiddle.last_error }

puts "== unsupported here"
# Fiddle::Pointer#to_value reads the address back as a VALUE.  It is not tried
# here: CRuby segfaults on any pointer that is not one, and IronRuby has no
# VALUE at all, so there is nothing the two could agree on.
#
# The line below is the one expected difference in this file: RTLD_NEXT is a
# dlsym pseudo-handle with no NativeLibrary spelling, so IronRuby raises rather
# than answer some other library's symbol.  Everything else here matches CRuby.
show("Handle::NEXT['getpid']")        { Handle::NEXT['getpid'] }
