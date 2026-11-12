# frozen_string_literal: true
#
# FFI::AbstractMemory, Pointer, MemoryPointer and Buffer - what the ffi C
# extension's AbstractMemory.c, Pointer.c, MemoryPointer.c and Buffer.c build.
#
# A memory object here is an address plus a size limit.  Reading and writing
# go through Fiddle::Pointer.read / Fiddle::Pointer.write, which are the two
# static Fiddle entry points that take a bare address and do not second-guess
# the size, and through String#pack / #unpack1 for the scalar types.  That
# makes a get_int32 three Ruby calls rather than one C load; it is not the
# fastest thing that could be written, but it is the only thing a managed
# runtime can do without a native accessor per type, and it is the same shape
# TruffleRuby's backend has.
#
# Ownership is Fiddle's.  A MemoryPointer keeps the Fiddle::Pointer that
# malloc'd its block, and Fiddle's own finalizer frees it - so an unreferenced
# MemoryPointer releases its memory without ffi needing a finalizer of its own.

module FFI
  # Raised when a null pointer is dereferenced.
  class NullPointerError < RuntimeError; end

  class AbstractMemory
    # Every scalar accessor: [name, bytes, pack code, signed?]
    SCALAR_TYPES = [
      ['int8',   1, 'c', true],
      ['uint8',  1, 'C', false],
      ['int16',  2, 's', true],
      ['uint16', 2, 'S', false],
      ['int32',  4, 'l', true],
      ['uint32', 4, 'L', false],
      ['int64',  8, 'q', true],
      ['uint64', 8, 'Q', false],
      ['long',   Fiddle::SIZEOF_LONG, Fiddle::SIZEOF_LONG == 8 ? 'q' : 'l', true],
      ['ulong',  Fiddle::SIZEOF_LONG, Fiddle::SIZEOF_LONG == 8 ? 'Q' : 'L', false],
      ['float32', 4, 'f', true],
      ['float64', 8, 'd', true],
    ].freeze
    private_constant :SCALAR_TYPES

    POINTER_PACK = Fiddle::SIZEOF_VOIDP == 8 ? 'Q' : 'L'
    private_constant :POINTER_PACK

    # The size a pointer with no known extent reports.  ffi's C extension uses
    # LONG_MAX for it, and abstract_memory.rb compares against that value.
    UNBOUNDED = (1 << (Fiddle::SIZEOF_LONG * 8 - 1)) - 1

    attr_reader :address
    alias_method :to_i, :address

    def size
      @size
    end
    alias_method :total, :size

    def type_size
      @type_size
    end

    def null?
      @address == 0
    end

    BIG_ENDIAN_NATIVE = [1].pack('S') == [1].pack('n')
    private_constant :BIG_ENDIAN_NATIVE
    NATIVE_ORDER = BIG_ENDIAN_NATIVE ? :big : :little
    private_constant :NATIVE_ORDER

    # The byte order reads and writes use.  ffi lets a memory object be asked
    # for a big-endian view of itself; the copy shares the address, and asking
    # for the order it already has answers the object itself.
    def order(*args)
      current = @swap ? (BIG_ENDIAN_NATIVE ? :little : :big) : NATIVE_ORDER
      return current if args.empty?

      order = args.first
      order = :big if order == :network
      unless order == :big || order == :little
        raise ArgumentError, "unknown byte order #{order.inspect}"
      end
      return self if order == current

      copy = __alias__
      copy.instance_variable_set(:@swap, order != NATIVE_ORDER)
      copy
    end

    # A second object over the same memory.  #dup cannot be used for this:
    # duplicating a pointer copies what it points at, as ffi's does.
    def __alias__
      copy = self.class.allocate
      instance_variables.each do |name|
        copy.instance_variable_set(name, instance_variable_get(name))
      end
      copy
    end

    def clear
      put_bytes(0, "\0".b * @size) if @size > 0
      self
    end

    def [](index)
      self + (index * @type_size)
    end

    def __copy_from__(other, length)
      put_bytes(0, other.get_bytes(0, length))
      self
    end

    def get(type, offset)
      FFI.find_type(type).get_at(self, offset)
    end

    def put(type, offset, value)
      FFI.find_type(type).put_at(self, offset, value)
      self
    end

    #
    # Bytes
    #

    def get_bytes(offset, length)
      check_bounds(offset, length)
      return ''.b if length == 0
      Fiddle::Pointer.read(@address + offset, length)
    end

    def put_bytes(offset, str, index = 0, length = nil)
      check_write
      str = check_string(str)
      raise RangeError, 'index cannot be less than zero' if index < 0
      length ||= str.bytesize - index
      if index + length > str.bytesize
        raise RangeError, 'index+length is greater than size of string'
      end
      check_bounds(offset, length)
      return self if length == 0
      Fiddle::Pointer.write(@address + offset, str.byteslice(index, length))
      self
    end

    def read_bytes(length)
      get_bytes(0, length)
    end

    def write_bytes(str, index = 0, length = nil)
      put_bytes(0, str, index, length)
    end

    #
    # Strings
    #

    def get_string(offset, length = nil)
      if length.nil?
        check_null
        return read_c_string(@address + offset) if @size >= UNBOUNDED
        length = @size - offset
      end
      check_bounds(offset, length)
      check_null
      read_until_nul(@address + offset, length)
    end

    def put_string(offset, str)
      check_write
      str = check_string(str)
      check_bounds(offset, str.bytesize + 1)
      Fiddle::Pointer.write(@address + offset, str.b + "\0".b)
      self
    end

    def get_array_of_string(offset, count = nil)
      result = []
      if count
        check_bounds(offset, count * Fiddle::SIZEOF_VOIDP)
        count.times do |i|
          address = read_scalar(offset + i * Fiddle::SIZEOF_VOIDP, Fiddle::SIZEOF_VOIDP, POINTER_PACK)
          result << (address == 0 ? nil : read_c_string(address))
        end
      else
        check_bounds(offset, Fiddle::SIZEOF_VOIDP)
        while offset < @size - Fiddle::SIZEOF_VOIDP
          address = read_scalar(offset, Fiddle::SIZEOF_VOIDP, POINTER_PACK)
          break if address == 0
          result << read_c_string(address)
          offset += Fiddle::SIZEOF_VOIDP
        end
      end
      result
    end

    def read_array_of_string(count = nil)
      get_array_of_string(0, count)
    end

    #
    # Pointers
    #

    def get_pointer(offset)
      Pointer.new(read_scalar(offset, Fiddle::SIZEOF_VOIDP, POINTER_PACK))
    end

    def put_pointer(offset, value)
      write_scalar(offset, Fiddle::SIZEOF_VOIDP, POINTER_PACK, FFI.pointer_address(value))
      self
    end

    def read_pointer
      get_pointer(0)
    end

    def write_pointer(value)
      put_pointer(0, value)
    end

    def get_array_of_pointer(offset, count)
      ::Array.new(count) { |i| get_pointer(offset + i * Fiddle::SIZEOF_VOIDP) }
    end

    def put_array_of_pointer(offset, ary)
      ary.each_with_index { |v, i| put_pointer(offset + i * Fiddle::SIZEOF_VOIDP, v) }
      self
    end

    def read_array_of_pointer(count)
      get_array_of_pointer(0, count)
    end

    def write_array_of_pointer(ary)
      put_array_of_pointer(0, ary)
    end

    #
    # Booleans
    #

    def get_bool(offset)
      get_uint8(offset) != 0
    end

    def put_bool(offset, value)
      put_uint8(offset, value ? 1 : 0)
      self
    end

    def read_bool
      get_bool(0)
    end

    def write_bool(value)
      put_bool(0, value)
    end

    #
    # The scalar accessors, and the read_/write_/array flavours of each.
    #

    SCALAR_TYPES.each do |name, bytes, code, signed|
      limit = 1 << (bytes * 8)
      class_eval <<~RUBY, __FILE__, __LINE__ + 1
        def get_#{name}(offset)
          read_scalar(offset, #{bytes}, #{code.dump})
        end

        def put_#{name}(offset, value)
          write_scalar(offset, #{bytes}, #{code.dump}, #{code == 'f' || code == 'd' ? 'Float(value)' : 'coerce_integer(value, ' + limit.to_s + ', ' + signed.to_s + ')'})
          self
        end

        def read_#{name}
          get_#{name}(0)
        end

        def write_#{name}(value)
          put_#{name}(0, value)
        end

        def get_array_of_#{name}(offset, count)
          check_bounds(offset, count * #{bytes})
          return [] if count == 0
          bytes = Fiddle::Pointer.read(@address + offset, count * #{bytes})
          bytes = swap_units(bytes, #{bytes}) if @swap
          bytes.unpack(#{code.dump} + count.to_s)
        end

        def put_array_of_#{name}(offset, ary)
          check_write
          raise TypeError, "wrong argument type \#{ary.class} (expected Array)" unless ary.is_a?(::Array)
          check_bounds(offset, ary.length * #{bytes})
          return self if ary.empty?
          values = ary.map {|v| #{code == 'f' || code == 'd' ? 'Float(v)' : 'coerce_integer(v, ' + limit.to_s + ', ' + signed.to_s + ')'} }
          bytes = values.pack(#{code.dump} + values.length.to_s)
          bytes = swap_units(bytes, #{bytes}) if @swap
          Fiddle::Pointer.write(@address + offset, bytes)
          self
        end

        def read_array_of_#{name}(count)
          get_array_of_#{name}(0, count)
        end

        def write_array_of_#{name}(ary)
          put_array_of_#{name}(0, ary)
        end
      RUBY
    end

    # The C extension's aliases: char is int8, short is int16, int is int32,
    # long_long is int64, float is float32 and double is float64.
    {
      'char' => 'int8', 'short' => 'int16', 'int' => 'int32', 'long_long' => 'int64'
    }.each do |name, old|
      %w[put_ get_ put_u get_u write_ read_ write_u read_u
         put_array_of_ get_array_of_ put_array_of_u get_array_of_u
         write_array_of_ read_array_of_ write_array_of_u read_array_of_u].each do |prefix|
        alias_method :"#{prefix}#{name}", :"#{prefix}#{old}"
      end
    end

    { 'float' => 'float32', 'double' => 'float64' }.each do |name, old|
      %w[put_ get_ write_ read_ put_array_of_ get_array_of_
         write_array_of_ read_array_of_].each do |prefix|
        alias_method :"#{prefix}#{name}", :"#{prefix}#{old}"
      end
    end

    private

    def read_scalar(offset, bytes, code)
      check_bounds(offset, bytes)
      check_null
      raw = Fiddle::Pointer.read(@address + offset, bytes)
      raw = raw.reverse if @swap
      raw.unpack1(code)
    end

    def write_scalar(offset, bytes, code, value)
      check_write
      check_bounds(offset, bytes)
      check_null
      raw = [value].pack(code)
      raw = raw.reverse if @swap
      Fiddle::Pointer.write(@address + offset, raw)
    end

    def swap_units(bytes, unit)
      bytes.scan(/.{#{unit}}/m).map(&:reverse).join
    end

    # The C name of each width, for the RangeError message ffi raises.
    C_NAMES = {
      [1, true] => 'char', [1, false] => 'unsigned char',
      [2, true] => 'short', [2, false] => 'unsigned short',
      [4, true] => 'int', [4, false] => 'unsigned int',
      [8, true] => 'long long', [8, false] => 'unsigned long long',
    }.freeze
    private_constant :C_NAMES

    # ffi raises RangeError for a value that does not fit the field, and
    # accepts a negative number where an unsigned type is asked for only when
    # it fits the same bit pattern.
    def coerce_integer(value, limit, signed)
      value = value.to_int unless value.is_a?(Integer)
      if signed
        half = limit >> 1
        unless value >= -half && value < half
          # a value that fits the unsigned range is taken bit-for-bit
          out_of_range(value, limit, signed) unless value >= 0 && value < limit
          value -= limit
        end
      else
        value += limit if value < 0 && value >= -(limit >> 1)
        out_of_range(value, limit, signed) unless value >= 0 && value < limit
      end
      value
    end

    def out_of_range(value, limit, signed)
      name = C_NAMES[[limit.bit_length / 8, signed]] || 'integer'
      raise RangeError, "integer #{value} too #{value < 0 ? 'small' : 'big'} to convert to '#{name}'"
    end

    def check_bounds(offset, length)
      if offset < 0 || length < 0 || (@size - (offset + length)) < 0
        raise IndexError, "Memory access offset=#{offset} size=#{length} is out of bounds"
      end
    end

    def check_null
      if @address == 0
        raise NullPointerError, format('invalid memory read at address=0x%016x', @address)
      end
    end

    def check_write
      if frozen?
        raise RuntimeError, format('invalid memory write at address=0x%016x', @address)
      end
    end

    def check_string(str)
      unless str.is_a?(String)
        raise TypeError, "wrong argument type #{str.class} (expected String)"
      end
      str
    end

    def read_until_nul(address, length)
      bytes = length == 0 ? ''.b : Fiddle::Pointer.read(address, length)
      nul = bytes.index("\0".b)
      nul ? bytes.byteslice(0, nul) : bytes
    end

    # strlen, without reading a byte the caller did not promise exists: the
    # scan stops at the end of each 4096-byte page, so it never touches a page
    # the string does not already reach into.
    def read_c_string(address)
      result = nil
      offset = 0
      loop do
        chunk = 4096 - ((address + offset) & 4095)
        bytes = Fiddle::Pointer.read(address + offset, chunk)
        nul = bytes.index("\0".b)
        if nul
          bytes = bytes.byteslice(0, nul)
          return result ? (result << bytes) : bytes
        end
        result = result ? (result << bytes) : bytes.dup
        offset += chunk
      end
    end
  end

  class Pointer < AbstractMemory
    UNBOUNDED = AbstractMemory::UNBOUNDED

    def initialize(type, address = nil)
      if address.nil?
        address = type
        @type_size = 1
      else
        @type_size = FFI::Pointer.find_type_size(type)
      end

      case address
      when Integer
        @address = address
        @size = UNBOUNDED
      when FFI::Pointer
        @parent = address
        @address = address.address
        @size = address.size
      else
        raise TypeError, 'wrong argument type, expected Integer or FFI::Pointer'
      end
      @swap = false
      self
    end

    # ffi's Pointer#dup copies the memory rather than aliasing it, so that the
    # copy is independent of the original - and refuses when there is no size
    # to say how much of it to copy.
    def initialize_copy(other)
      if other.size >= UNBOUNDED
        raise RuntimeError, 'cannot duplicate unbounded memory area'
      end
      bytes = other.get_bytes(0, other.size)
      @owner = Fiddle::Pointer.malloc(other.size < 1 ? 1 : other.size, Fiddle::RUBY_FREE)
      @address = @owner.to_i
      @size = other.size
      @type_size = other.type_size
      @swap = other.instance_variable_get(:@swap)
      @parent = nil
      @autorelease = true
      put_bytes(0, bytes) if other.size > 0
      self
    end

    # How many bytes one of these is.  A plain Integer is already a size, a
    # Struct subclass answers its layout's, and anything else is an ffi type.
    def self.find_type_size(type)
      return type if type.is_a?(Integer)
      return type.size if type.is_a?(Class) && type < FFI::Struct
      return type.size if type.is_a?(FFI::Type)
      FFI.type_size(type)
    end

    def slice(offset, length)
      check_bounds(offset, length) unless @size == UNBOUNDED
      result = Pointer.new(@address + offset)
      result.instance_variable_set(:@size, length)
      result.instance_variable_set(:@type_size, @type_size)
      result.instance_variable_set(:@swap, @swap)
      result.instance_variable_set(:@parent, @parent || self)
      result
    end

    def +(offset)
      slice(offset, @size == UNBOUNDED ? UNBOUNDED : @size - offset)
    end

    def ==(other)
      return @address == 0 if other.nil?
      return false unless other.is_a?(Pointer)
      @address == other.address
    end
    alias_method :eql?, :==

    def hash
      @address.hash
    end

    def inspect
      if @size == UNBOUNDED
        "#<#{self.class} address=0x#{@address.to_s(16)}>"
      else
        "#<#{self.class} address=0x#{@address.to_s(16)} size=#{@size}>"
      end
    end
    alias_method :to_s, :inspect

    # A plain Pointer owns nothing, so releasing it is a no-op that ffi still
    # lets you ask for.
    def autorelease=(value)
      raise FrozenError, "can't modify frozen #{self.class}" if frozen?
      @autorelease = value
    end

    def autorelease?
      @autorelease.nil? ? true : @autorelease
    end

    def free
      raise RuntimeError, 'cannot free non-allocated pointer'
    end

    NULL = new(0)
  end

  # Memory this process allocated, and frees when nothing refers to it any
  # more.  The block is Fiddle's - Fiddle::Pointer.malloc installs free(3) as
  # its free function and its finalizer calls it - so there is one owner of
  # the memory rather than two.
  class MemoryPointer < Pointer
    def initialize(type, count = 1, clear = true)
      size = FFI::Pointer.find_type_size(type)
      total = size * count
      @owner = Fiddle::Pointer.malloc(total < 1 ? 1 : total, Fiddle::RUBY_FREE)
      @address = @owner.to_i
      @size = total
      @type_size = size
      @swap = false
      self.clear if clear && total > 0
      self
    end

    def self.new(type, count = 1, clear = true)
      pointer = allocate
      pointer.send(:initialize, type, count, clear)
      if block_given?
        begin
          yield pointer
        ensure
          pointer.free
        end
      else
        pointer
      end
    end

    def self.from_string(str)
      str = str.to_str
      pointer = new(1, str.bytesize + 1, false)
      pointer.put_string(0, str)
      pointer
    end

    def autorelease=(value)
      raise FrozenError, "can't modify frozen #{self.class}" if frozen?
      # Fiddle spells "do not free this" as having no free function.
      @owner.free = value ? Fiddle::RUBY_FREE : nil
      @autorelease = value
    end

    def autorelease?
      @autorelease.nil? ? true : @autorelease
    end

    def free
      unless @owner.freed?
        @owner.call_free
      end
      @address = 0
      @size = 0
      nil
    end

    class << self
      alias_method :alloc_in, :new
      alias_method :alloc_out, :new
      alias_method :alloc_inout, :new
      alias_method :new_in, :new
      alias_method :new_out, :new
      alias_method :new_inout, :new
    end
  end

  # ffi's Buffer is a MemoryPointer that a native call may read or write; the
  # C extension keeps the bytes in the Ruby heap, which is not something a
  # managed runtime can hand out an address for, so a Buffer here is exactly a
  # MemoryPointer - the same choice TruffleRuby makes.
  class Buffer < MemoryPointer
    class << self
      alias_method :alloc_in, :new
      alias_method :alloc_out, :new
      alias_method :alloc_inout, :new
      alias_method :new_in, :new
      alias_method :new_out, :new
      alias_method :new_inout, :new
    end
  end

  # The address behind whatever stands in for a pointer.  An Integer is not one
  # of them: ffi refuses a bare address where a pointer is asked for, so that a
  # number meant as a count cannot become a wild dereference.
  def self.pointer_address(value)
    case value
    when nil then 0
    when AbstractMemory then value.address
    else
      if value.respond_to?(:to_ptr)
        ptr = value.to_ptr
        raise ArgumentError, ':pointer argument is not a valid pointer' unless ptr.is_a?(AbstractMemory)
        ptr.address
      else
        raise ArgumentError, ':pointer argument is not a valid pointer'
      end
    end
  end
end
