# frozen_string_literal: true
#
# ffi, provided by IronRuby itself.
#
# `gem install ffi` cannot work here: the ffi gem is a C extension over libffi.
# What IronRuby ships instead is the arrangement JRuby and TruffleRuby use -
# the gem's own pure-Ruby half, vendored unchanged from ffi 1.17.4 into
# Src/StdLib/ironruby/ffi, on top of a backend that implements what the C
# extension would (FFI::Type, AbstractMemory, Pointer, MemoryPointer, Buffer,
# StructLayout, Struct, Function, DynamicLibrary, LastError).
#
# The backend is pure Ruby too, written against Fiddle - IronRuby's own Fiddle,
# whose C half is C# (Src/Libraries/Fiddle): dlopen/dlsym are NativeLibrary, a
# call with a run-time signature is one OpCodes.Calli, and a callback is an
# emitted [UnmanagedFunctionPointer] delegate.  Everything ffi needs from
# libffi, Fiddle already has, so there is no second copy of that machinery.
#
# What does not work, and cannot without libffi or a Ruby object's address:
#
#   * struct by value - a struct passed to or returned from a function by
#     value (FFI::Struct.by_value, :struct arguments).  IL's calli takes a
#     signature built from CLR types, and there is no run-time way to build a
#     value type with a struct's layout that the platform ABI would then
#     classify the way C does.  Using one raises NotImplementedError.
#   * :long_double.  .NET has no 80-bit float.
#   * floating-point variadic arguments - see Fiddle::Function's own note.
#   * FFI::Pointer#read_pointer on an address a Ruby object lives at: there is
#     no such address on .NET, so nothing hands one out in the first place.
#
# What does work but differs: FFI.errno is filled in only for functions
# attached through this library (attach_function passes Fiddle's
# save_last_error: true, which routes the call through a marshalling stub that
# keeps errno; see the head of Src/Libraries/Fiddle/FiddleCall.cs).

require 'fiddle'

module FFI
  # The vendored half reopens these, so the backend has to define them first.
  TypeDefs = {}

  # ffi/platform.rb reads these three out of the C extension.  GNU_LIBC is what
  # makes FFI::Platform::LIBC name libc.so.6 rather than libc.so, which on a
  # glibc system is a linker script rather than an ELF object.
  module Platform
    ADDRESS_SIZE = Fiddle::SIZEOF_VOIDP * 8
    LONG_SIZE = Fiddle::SIZEOF_LONG * 8
    GNU_LIBC = 'libc.so.6' if RUBY_PLATFORM.include?('linux') && !RUBY_PLATFORM.include?('musl')
    # The width C's long double has here.  Nothing can be passed as one - .NET
    # has no 80-bit float - but the constant says what the platform's is, which
    # is what ffi's own code reads it for.
    LONG_DOUBLE_SIZE = ADDRESS_SIZE == 64 ? 128 : 96
  end

  # ffi/ffi.rb installs fork tracking that calls this; there is no async
  # callback dispatcher thread here, so there is nothing to reset.
  def self._async_cb_dispatcher_atfork_child
  end
end

require 'ffi/ironruby/last_error'
require 'ffi/ironruby/type'
require 'ffi/ironruby/memory'
require 'ffi/ironruby/struct_layout'
require 'ffi/ironruby/struct'
require 'ffi/ironruby/function'
require 'ffi/ironruby/dynamic_library'

require 'ffi/ffi'
