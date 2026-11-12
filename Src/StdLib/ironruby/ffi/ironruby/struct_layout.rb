# frozen_string_literal: true
#
# FFI::StructLayout and its Field classes - what the ffi C extension's
# StructLayout.c builds.  A layout is a type (it has a size and an alignment,
# so it can sit inside another layout) plus a field for every member; a field
# knows its offset and asks its type to read or write there.

module FFI
  class StructLayout < Type
    attr_reader :fields, :field_map

    def initialize(fields, size, alignment)
      super(align_to(size, alignment), alignment)
      @field_map = {}
      @fields = fields.each_with_index.map do |field, i|
        unless field.is_a?(FFI::StructLayout::Field)
          raise TypeError, "wrong type for field #{i}."
        end
        raise RuntimeError, "type of field #{i} not supported" unless field.type
        if field.type.size == 0 && i < fields.size - 1
          raise TypeError, "type of field #{i} has zero size"
        end
        @field_map[field.name] = field
        field
      end
      raise RuntimeError, 'Struct size is zero' if self.size == 0
    end

    def [](field)
      @field_map[field]
    end

    def members
      @fields.map(&:name)
    end

    # A union's libffi representation; nothing here needs one, but the builder
    # calls it and ffi code may too.
    def __union!
      self
    end

    def inspect
      "#<#{self.class} size=#{size} alignment=#{alignment} fields=#{members.inspect}>"
    end

    private def align_to(size, alignment)
      ((size - 1) | (alignment - 1)) + 1
    end

    class Field
      attr_reader :name, :offset, :type

      def initialize(name, offset, type)
        @name = name.to_sym
        @offset = offset.to_int
        @type = type
      end

      def size
        @type.size
      end

      def alignment
        @type.alignment
      end

      def get(pointer)
        @type.get_at(pointer, @offset)
      end

      def put(pointer, value)
        @type.put_at(pointer, @offset, value)
      end

      def inspect
        "#<#{self.class} name=#{@name} offset=#{@offset} type=#{@type.inspect}>"
      end
    end

    class Number < Field
    end

    class Pointer < Field
    end

    # A char* member: reading it answers the string it points at, and ffi
    # refuses to write one, because the struct would then hold a pointer to
    # memory nothing owns.
    class String < Field
      def get(pointer)
        address = pointer.get_pointer(@offset)
        address.null? ? nil : address.read_string
      end

      def put(pointer, value)
        raise NotImplementedError, 'cannot set :string fields'
      end
    end

    class Function < Field
      def get(pointer)
        FFI::Function.new(@type, nil, pointer.get_pointer(@offset))
      end

      def put(pointer, value)
        function = if value.nil? || value.is_a?(FFI::Function)
          value
        elsif value.is_a?(::Proc) || value.respond_to?(:call)
          FFI::Function.new(@type, nil, value)
        else
          raise TypeError, 'wrong type (expected Proc or Function)'
        end
        # the struct holds only the address, so the Function - and with it the
        # closure C will call - has to stay reachable from somewhere
        (pointer.instance_variable_get(:@ffi_functions) ||
          pointer.instance_variable_set(:@ffi_functions, {}))[@name] = function
        pointer.put_pointer(@offset, function)
      end
    end

    class Array < Field
      def get(pointer)
        if @type.char_array?
          FFI::Struct::CharArray.new(pointer, self)
        else
          FFI::Struct::InlineArray.new(pointer, self)
        end
      end

      def put(pointer, value)
        if @type.char_array?
          if value.bytesize < @type.length
            pointer.put_string(@offset, value)
          elsif value.bytesize == @type.length
            pointer.put_bytes(@offset, value)
          else
            raise IndexError,
              "String is longer (#{value.bytesize} bytes) than the char array (#{@type.length} bytes)"
          end
        elsif value.is_a?(::Array)
          array = get(pointer)
          value.each_with_index { |v, i| array[i] = v }
          value
        else
          raise NotImplementedError, 'cannot set array field'
        end
      end
    end
  end
end
