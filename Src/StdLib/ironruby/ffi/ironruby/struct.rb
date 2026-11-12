# frozen_string_literal: true
#
# FFI::Struct's core - what the ffi C extension's Struct.c builds.  The layout
# DSL, .ptr, .by_value and the rest are the gem's own Ruby (ffi/struct.rb),
# which reopens this class.

module FFI
  class Struct
    class << self
      alias_method :alloc_in, :new
      alias_method :alloc_out, :new
      alias_method :alloc_inout, :new
      alias_method :new_in, :new
      alias_method :new_out, :new
      alias_method :new_inout, :new
    end

    attr_reader :pointer, :layout

    def initialize(pointer = nil, *args)
      @layout = args.empty? ? self.class.instance_variable_get(:@layout) : self.class.layout(*args)
      unless @layout.is_a?(FFI::StructLayout)
        raise RuntimeError, "invalid Struct layout for #{self.class}"
      end

      if pointer
        unless pointer.is_a?(FFI::AbstractMemory)
          raise ArgumentError, "Invalid Memory object: #{pointer.inspect}"
        end
        @pointer = pointer
      else
        @pointer = MemoryPointer.new(@layout.size, 1, true)
      end
      self
    end

    def initialize_copy(other)
      return self if equal?(other)
      @layout = other.layout
      if other.pointer
        @pointer = MemoryPointer.new(@layout.size, 1, false)
        @pointer.__copy_from__(other.pointer, @layout.size)
      else
        @pointer = other.pointer
      end
      self
    end

    def [](name)
      field = get_layout.field_map[name]
      raise ArgumentError, "No such field '#{name}'" unless field
      field.get(@pointer)
    end

    def []=(name, value)
      field = get_layout.field_map[name]
      raise ArgumentError, "No such field '#{name}'" unless field
      field.put(@pointer, value)
      # the struct holds an address; whatever that address is into has to stay
      # reachable for as long as the struct does
      (@references ||= {})[name] = value
      value
    end

    def null?
      @pointer.null?
    end

    def order(*args)
      if args.empty?
        @pointer.order
      else
        copy = dup
        copy.send(:pointer=, @pointer.order(*args))
        copy
      end
    end

    private def pointer=(pointer)
      unless pointer.is_a?(FFI::AbstractMemory)
        raise TypeError, "wrong argument type #{pointer.class} (expected Pointer or Buffer)"
      end
      layout = get_layout
      if layout.size > pointer.size
        raise ArgumentError, "memory of #{pointer.size} bytes too small for struct " \
                             "#{self.class} (expected at least #{layout.size})"
      end
      @pointer = pointer
    end

    private def layout=(layout)
      unless layout.is_a?(FFI::StructLayout)
        raise TypeError, "wrong argument type #{layout.class} (expected #{FFI::StructLayout})"
      end
      @layout = layout
    end

    private def get_layout
      return @layout if defined?(@layout) && @layout

      layout = self.class.instance_variable_get(:@layout)
      unless layout.is_a?(FFI::StructLayout)
        raise RuntimeError, "invalid Struct layout for #{self.class}"
      end
      @pointer = MemoryPointer.new(layout.size, 1, true) unless defined?(@pointer) && @pointer
      @layout = layout
    end

    # An inline array member, as the value reading that member answers.
    class InlineArray
      include Enumerable

      def initialize(pointer, field)
        @pointer = pointer
        @offset = field.offset
        unless field.type.is_a?(FFI::Type::ArrayType)
          raise TypeError, "wrong field type (expected an inline array)"
        end
        @array_type = field.type
        @length = @array_type.length
      end

      def size
        @length
      end

      def [](index)
        check_index(index)
        @array_type.get_element_at(@pointer, @offset, index)
      end

      def []=(index, value)
        check_index(index)
        @array_type.put_element_at(@pointer, @offset, index, value)
      end

      def each
        return to_enum(:each) unless block_given?
        @length.times { |i| yield self[i] }
      end

      def to_a
        ::Array.new(@length) { |i| self[i] }
      end

      def to_ptr
        @pointer.slice(@offset, @array_type.size)
      end

      private def check_index(index)
        if @length > 0 && (index < 0 || index >= @length)
          raise IndexError, "index #{index} out of bounds"
        end
      end
    end

    # char[n] answers something that is also a String.
    class CharArray < InlineArray
      def to_s
        @pointer.get_string(@offset, @length)
      end
      alias_method :to_str, :to_s
    end
  end
end
