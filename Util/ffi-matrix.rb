# Differential matrix for the ffi gem's API: the type map, Library and
# attach_function, memory, Struct/Union, callbacks, enums and errno, one line
# each.
#   ruby Util/ffi-matrix.rb            # CRuby reference (needs `gem install ffi`)
#   ./ir.sh Util/ffi-matrix.rb         # IronRuby
# Diff the two outputs; every differing line is a bug, except the ones the
# "== what IronRuby cannot do" section is there to pin down - those are
# expected to differ, and the point of printing them is that the difference
# stays an exception with a clear message rather than a wrong answer.
#
# Nothing here prints an address, a pid or a clock reading directly - those
# differ between two runs of the same interpreter - so each such case is turned
# into a predicate ("is it plausible?") instead.

$stdout.sync = true

require 'ffi'

def show(label)
  v = begin
        yield.inspect
      rescue Exception => e
        "!#{e.class}: #{e.message}"
      end
  puts "#{label.ljust(52)} #{v}"
end

puts "== constants and types"
show("VERSION")                       { FFI::VERSION }
show("Platform::ADDRESS_SIZE")        { FFI::Platform::ADDRESS_SIZE }
show("Platform::LONG_SIZE")           { FFI::Platform::LONG_SIZE }
show("Platform.windows?")             { FFI::Platform.windows? }
show("Platform::BYTE_ORDER")          { FFI::Platform::BYTE_ORDER == FFI::Platform::LITTLE_ENDIAN ? :little : :big }
show("type_size(:int)")               { FFI.type_size(:int) }
show("type_size(:long)")              { FFI.type_size(:long) }
show("type_size(:size_t)")            { FFI.type_size(:size_t) }
show("type_size(:pointer)")           { FFI.type_size(:pointer) }
show("type_size(:bool)")              { FFI.type_size(:bool) }
show("find_type(:uint32)")            { FFI.find_type(:uint32).equal?(FFI::Type::UINT32) }
show("find_type(:int32) is :int")     { FFI.find_type(:int32).equal?(FFI.find_type(:int)) }
show("find_type of nonsense")         { FFI.find_type(:no_such_type) }
show("Type::POINTER.size")            { FFI::Type::POINTER.size }
show("Pointer::NULL.null?")           { FFI::Pointer::NULL.null? }
show("Pointer.size")                  { FFI::Pointer.size }

puts "== library and attach_function"
module M
  extend FFI::Library
  ffi_lib FFI::Library::LIBC
  attach_function :strlen, [:string], :size_t
  attach_function :abs, [:int], :int
  attach_function :my_getpid, :getpid, [], :int
  attach_function :atof, [:string], :double
  attach_function :getenv, [:string], :string
  attach_function :strdup, [:string], :pointer
  attach_function :cfree, :free, [:pointer], :void
  attach_function :memcpy, [:pointer, :pointer, :size_t], :pointer
  callback :cmp, [:pointer, :pointer], :int
  attach_function :qsort, [:pointer, :size_t, :size_t, :cmp], :void
  attach_function :c_open, :open, [:string, :int], :int
  attach_function :snprintf, [:buffer_out, :size_t, :string, :varargs], :int
  attach_variable :environ, :pointer
end

show("ffi_convention")                { M.ffi_convention }
show("strlen")                        { M.strlen("hello world") }
show("strlen of empty")               { M.strlen("") }
show("abs(-7)")                       { M.abs(-7) }
show("getpid plausible")              { M.my_getpid == Process.pid }
show("atof")                          { M.atof("3.25") }
show("getenv -> String")              { M.getenv("HOME").is_a?(String) }
show("getenv of a missing var")       { M.getenv("NO_SUCH_VAR_HERE_XYZ") }
show("wrong arity")                   { M.abs(1, 2) }
show("attached_functions has :abs")   { M.attached_functions.key?(:abs) }
show("attach_function of nothing")    { M.attach_function(:no_such_c_function, [], :int) && nil }
show("attach_variable environ")       { M.environ.is_a?(FFI::Pointer) }
show("Integer as a :pointer")         { M.strlen(12345) }
show("ffi_lib of nothing")            { Module.new { extend FFI::Library; ffi_lib "no_such_library.so" } && nil }

puts "== memory"
show("MemoryPointer size")            { FFI::MemoryPointer.new(:int, 4).size }
show("MemoryPointer type_size")       { FFI::MemoryPointer.new(:int, 4).type_size }
show("cleared on allocation")         { FFI::MemoryPointer.new(:int, 4).read_array_of_int32(4) }
show("put/get int32")                 { p = FFI::MemoryPointer.new(:int); p.put_int32(0, -5); p.get_int32(0) }
show("put/get uint64 high bit")       { p = FFI::MemoryPointer.new(:uint64); p.put_uint64(0, 2**64 - 3); p.get_uint64(0) }
show("put int32 out of range")        { FFI::MemoryPointer.new(:int).put_int32(0, 2**40) }
show("put/get double")                { p = FFI::MemoryPointer.new(:double); p.put_double(0, 1.5); p.get_double(0) }
show("put/get pointer")               { p = FFI::MemoryPointer.new(:pointer); p.put_pointer(0, FFI::Pointer.new(64)); p.get_pointer(0).address }
show("put/get string")                { p = FFI::MemoryPointer.new(:char, 16); p.put_string(0, "hi"); p.get_string(0) }
show("get_string encoding")           { p = FFI::MemoryPointer.new(:char, 16); p.put_string(0, "hi"); p.get_string(0).encoding.to_s }
show("read past the end")             { FFI::MemoryPointer.new(:char, 4).get_bytes(0, 8) }
show("negative offset")               { FFI::MemoryPointer.new(:char, 4).get_bytes(-1, 1) }
show("from_string")                   { FFI::MemoryPointer.from_string("abc").size }
show("+ keeps the bytes")             { p = FFI::MemoryPointer.from_string("abcdef"); (p + 2).read_string }
show("slice size")                    { FFI::MemoryPointer.new(:char, 16).slice(4, 4).size }
show("dup is independent")            { a = FFI::MemoryPointer.from_string("abc"); b = a.dup; a.put_string(0, "xyz"); b.read_string }
show("dup of unbounded")              { FFI::Pointer.new(1).dup }
show("Buffer is memory")              { b = FFI::Buffer.new(8); b.put_int32(0, 7); b.get_int32(0) }
show("null pointer read")             { FFI::Pointer::NULL.read_int32 }
show("frozen write")                  {
  # the message carries the address, which differs between runs
  begin
    FFI::MemoryPointer.new(:int).freeze.put_int32(0, 1)
  rescue RuntimeError => e
    [e.class, e.message.sub(/0x\h+/, "0x...")]
  end
}
show("order(:big) round trip")        { p = FFI::MemoryPointer.new(:int); p.order(:big).put_int32(0, 1); p.read_bytes(4).unpack1("H*") }
show("order(:network) is :big")       { FFI::MemoryPointer.new(:int).order(:network).order }
show("size_limit? of a bare pointer") { FFI::Pointer.new(1).size_limit? }

puts "== c calls that move memory about"
show("strdup round trip")             { p = M.strdup("hello"); s = p.read_string; M.cfree(p); s }
show("memcpy")                        {
  src = FFI::MemoryPointer.from_string("abcdef")
  dst = FFI::MemoryPointer.new(:char, 7)
  M.memcpy(dst, src, 7)
  dst.read_string
}
show("snprintf with varargs")         {
  buf = FFI::Buffer.new(32)
  n = M.snprintf(buf, 32, "%d-%s", :int, 42, :string, "x")
  [n, buf.get_string(0)]
}

puts "== struct and union"
class TimeVal < FFI::Struct
  layout :tv_sec, :long, :tv_usec, :long
end
class Packed < FFI::Struct
  packed
  layout :a, :char, :b, :int
end
class Inner < FFI::Struct
  layout :a, :int
end
class Outer < FFI::Struct
  layout :name, [:char, 8], :inner, Inner, :nums, [:int, 3]
end
class U < FFI::Union
  layout :i, :int, :d, :double, :c, :char
end

show("Struct.size")                   { TimeVal.size }
show("Struct.alignment")              { TimeVal.alignment }
show("Struct.members")                { TimeVal.members }
show("Struct.offsets")                { TimeVal.offsets }
show("Struct.offset_of")              { TimeVal.offset_of(:tv_usec) }
show("member round trip")             { t = TimeVal.new; t[:tv_sec] = 1234; t[:tv_sec] }
show("member that is not there")      { TimeVal.new[:nope] }
show("packed size")                   { Packed.size }
show("inline char array")             { o = Outer.new; o[:name] = "abc"; o[:name].to_s }
show("inline int array")              { o = Outer.new; o[:nums][1] = 9; o[:nums].to_a }
show("inline array out of bounds")    { Outer.new[:nums][5] }
show("inner struct")                  { o = Outer.new; o[:inner][:a] = 3; o[:inner][:a] }
show("Outer.size")                    { Outer.size }
show("union size")                    { U.size }
show("union aliasing")                { u = U.new; u[:i] = 0x41414141; u[:c] }
show("struct over a pointer")         {
  p = FFI::MemoryPointer.new(TimeVal.size)
  p.put_long(0, 99)
  TimeVal.new(p)[:tv_sec]
}
show("Struct#pointer address")        { t = TimeVal.new; t.pointer.address == t.to_ptr.address }
show("Struct#dup is independent")     { a = TimeVal.new; a[:tv_sec] = 1; b = a.dup; a[:tv_sec] = 2; b[:tv_sec] }
show("array of structs")              {
  a = FFI::MemoryPointer.new(TimeVal.size, 3)
  3.times { |i| TimeVal.new(a + i * TimeVal.size)[:tv_sec] = i }
  3.times.map { |i| TimeVal.new(a + i * TimeVal.size)[:tv_sec] }
}
show("Struct.ptr is a type")          { TimeVal.ptr.is_a?(FFI::Type) }
show("gettimeofday fills it in")      {
  Module.new do
    extend FFI::Library
    ffi_lib FFI::Library::LIBC
    attach_function :gettimeofday, [:pointer, :pointer], :int
  end.gettimeofday((t = TimeVal.new).pointer, nil)
  t[:tv_sec] > 1_700_000_000
}

puts "== callbacks"
show("qsort with a Ruby comparator")  {
  p = FFI::MemoryPointer.new(:int, 5)
  p.put_array_of_int32(0, [5, 3, 1, 4, 2])
  M.qsort(p, 5, 4, proc { |a, b| a.read_int32 <=> b.read_int32 })
  p.read_array_of_int32(5)
}
show("Function.new from a block")     { FFI::Function.new(:int, [:int]) { |i| i * 2 }.call(21) }
show("Function#call arity")           { FFI::Function.new(:int, [:int]) { |i| i }.call(1, 2) }
show("callback raising")              {
  p = FFI::MemoryPointer.new(:int, 2)
  begin
    M.qsort(p, 2, 4, proc { raise "boom" })
  rescue RuntimeError => e
    e.message
  end
}
show("named callback type")           {
  m = Module.new do
    extend FFI::Library
    ffi_lib FFI::Library::LIBC
    callback :cmp, [:pointer, :pointer], :int
    attach_function :qsort, [:pointer, :size_t, :size_t, :cmp], :void
  end
  p = FFI::MemoryPointer.new(:int, 3)
  p.put_array_of_int32(0, [3, 1, 2])
  m.qsort(p, 3, 4, proc { |a, b| a.read_int32 <=> b.read_int32 })
  p.read_array_of_int32(3)
}

puts "== enums, bitmasks and mapped types"
module E
  extend FFI::Library
  ffi_lib FFI::Library::LIBC
  enum :color, [:red, :green, :blue, :white, 10]
  bitmask :flags, [:a, :b, :c]
end
show("enum lookup by symbol")         { E.enum_type(:color)[:blue] }
show("enum lookup by value")          { E.enum_type(:color)[10] }
show("enum symbols")                  { E.enum_type(:color).symbols }
show("enum_value")                    { E.enum_value(:green) }
show("bitmask to_native")             { E.enum_type(:flags).to_native([:a, :c], nil) }
show("bitmask from_native")           { E.enum_type(:flags).from_native(5, nil) }
show("StrPtrConverter")               { FFI.find_type(:strptr).is_a?(FFI::Type::Mapped) }

puts "== errno"
show("errno after a failed open")     { M.c_open("/definitely/not/here", 0); FFI.errno }
show("errno after a good call")       { M.abs(-1); M.c_open("/dev/null", 0) > 0 }
show("FFI.errno= is accepted")        { FFI.errno = 13; FFI.errno.is_a?(Integer) }
show("LastError.error")               { FFI::LastError.error.is_a?(Integer) }

puts "== auto pointers"
show("AutoPointer releases")          {
  released = []
  cls = Class.new(FFI::AutoPointer) do
    define_singleton_method(:release) { |ptr| released << ptr.address }
  end
  ap = cls.new(FFI::Pointer.new(0x1234))
  ap.free
  released
}
show("AutoPointer from a proc")       {
  seen = nil
  ap = FFI::AutoPointer.new(FFI::Pointer.new(0x4321), proc { |p| seen = p.address })
  ap.free
  seen
}
show("AutoPointer of a MemoryPointer"){ FFI::AutoPointer.new(FFI::MemoryPointer.new(4), proc {}) && nil }

puts "== what IronRuby cannot do"
# Each of these is a documented limit - see the head of Src/StdLib/ironruby/ffi.rb.
# CRuby answers them; IronRuby is expected to raise, and what matters is that it
# raises with a message that says why rather than returning something wrong.
class ByValue < FFI::Struct
  layout :a, :int, :b, :int
end
show("struct by value as an argument") {
  Module.new do
    extend FFI::Library
    ffi_lib FFI::Library::LIBC
    attach_function :no_such_byvalue_fn, :abs, [ByValue.by_value], :int
  end.no_such_byvalue_fn(ByValue.new)
}
show("float variadic argument")       {
  buf = FFI::Buffer.new(32)
  M.snprintf(buf, 32, "%.1f", :double, 1.5)
  buf.get_string(0)
}
show(":long_double in a call")        {
  Module.new do
    extend FFI::Library
    ffi_lib FFI::Library::LIBC
    attach_function :ld, :abs, [:long_double], :long_double
  end.ld(1.0)
}
show(":long_double size is known")    { FFI.type_size(:long_double) }
