# frozen_string_literal: true
#
# FFI::DynamicLibrary - what the ffi C extension's DynamicLibrary.c builds.
# One of these is a Fiddle::Handle, which is a NativeLibrary handle; the
# search order and the linker-script fallback are the gem's own Ruby in
# ffi/dynamic_library.rb, which reopens this class.

module FFI
  class DynamicLibrary
    # dlopen(3) modes.  NativeLibrary does not take one, so these are the
    # values callers pass around rather than anything that reaches dlopen;
    # they are dlfcn.h's so that code comparing against them still works.
    RTLD_LAZY   = 0x00001
    RTLD_NOW    = 0x00002
    RTLD_LOCAL  = 0x00000
    RTLD_GLOBAL = 0x00100

    attr_reader :name

    class Symbol < FFI::Pointer
      attr_reader :name

      def initialize(library, address, name)
        super(address)
        @library = library
        @name = name
      end

      def inspect
        "#<#{self.class} library=#{@library.inspect} symbol=#{@name} address=0x#{address.to_s(16)}>"
      end
    end

    def self.open(libname, flags = nil)
      handle = begin
        Fiddle::Handle.new(libname ? libname.to_s : nil)
      rescue Fiddle::DLError => e
        raise LoadError, "Could not open library '#{libname || '[current process]'}': #{e.message}"
      end
      new(libname && libname.to_s, handle)
    end

    def self.last_error
      Fiddle.last_error
    end

    def initialize(name, handle)
      @name = name
      @handle = handle
    end

    def find_symbol(name)
      address = @handle.sym_defined?(name.to_s)
      address && Symbol.new(self, address, name.to_s)
    rescue Fiddle::DLError
      nil
    end
    alias_method :find_function, :find_symbol
    alias_method :find_variable, :find_symbol

    def to_s
      "#<#{self.class} @name=#{@name.inspect}>"
    end
    alias_method :inspect, :to_s
  end
end
