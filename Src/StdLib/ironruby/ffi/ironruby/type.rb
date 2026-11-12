# frozen_string_literal: true
#
# FFI::Type and its subclasses - what the ffi C extension's Type.c builds.
#
# A Builtin type knows three things: how big it is, how to read and write one
# at an offset in memory, and which Fiddle TYPE_* code stands for it in a
# call.  Everything else here is a type made out of those: Mapped (a
# DataConverter in front of a native type), ArrayType (an inline array in a
# struct), StructByValue, and FunctionType (a callback signature).

module FFI
  # ffi's C extension puts the builtin types in FFI::NativeType as well.
  module NativeType
  end

  class Type
    attr_reader :size, :alignment

    def initialize(size, alignment, fiddle_type = nil)
      @size = size
      @alignment = alignment
      @fiddle_type = fiddle_type
    end

    # The Fiddle TYPE_* code a call passes this as.  Types that cannot cross a
    # calli - a struct by value, a long double - have none and say so.
    def fiddle_type
      @fiddle_type or raise NotImplementedError,
        "#{inspect} cannot be passed to or returned from a native function on IronRuby"
    end

    def get_at(pointer, offset)
      raise NotImplementedError, "get_at is not implemented for #{self.class}"
    end

    def put_at(pointer, offset, value)
      raise NotImplementedError, "put_at is not implemented for #{self.class}"
    end

    def inspect
      "#<#{self.class} size=#{@size} alignment=#{@alignment}>"
    end

    # One of the primitive C types.  There is exactly one instance per type,
    # and code compares against the FFI::Type::* constants by identity.
    class Builtin < Type
      attr_reader :name

      def initialize(name, size, alignment, fiddle_type, accessor)
        super(size, alignment, fiddle_type)
        @name = name
        @accessor = accessor
        @unsigned = name.to_s.start_with?('UINT') || name == :ULONG || name == :BOOL
      end

      def native_type
        self
      end

      def unsigned?
        @unsigned
      end

      def get_at(pointer, offset)
        raise NotImplementedError, "cannot read a #{@name} out of memory" unless @accessor
        pointer.__send__(:"get_#{@accessor}", offset)
      end

      def put_at(pointer, offset, value)
        raise NotImplementedError, "cannot write a #{@name} into memory" unless @accessor
        pointer.__send__(:"put_#{@accessor}", offset, value)
      end

      def inspect
        "#<#{self.class}:#{@name} size=#{@size} alignment=#{@alignment}>"
      end

      def to_s
        @name.to_s
      end
    end

    # A DataConverter wrapped so that it can be used wherever a type can.
    class Mapped < Type
      attr_reader :native_type, :converter
      alias_method :type, :native_type

      def initialize(converter)
        [:native_type, :to_native, :from_native].each do |meth|
          unless converter.respond_to?(meth)
            raise NoMethodError, "#{meth} method not implemented for #{converter}"
          end
        end
        native_type = converter.native_type
        unless native_type.is_a?(FFI::Type)
          raise TypeError, 'native_type did not return instance of FFI::Type'
        end
        super(native_type.size, native_type.alignment, native_type.instance_variable_get(:@fiddle_type))
        @native_type = native_type
        @converter = converter
      end

      def to_native(*args)
        @converter.to_native(*args)
      end

      def from_native(*args)
        @converter.from_native(*args)
      end

      def get_at(pointer, offset)
        from_native(@native_type.get_at(pointer, offset), nil)
      end

      def put_at(pointer, offset, value)
        @native_type.put_at(pointer, offset, to_native(value, nil))
      end
    end

    # An inline array in a struct: [:char, 12].
    class ArrayType < Type
      attr_reader :length, :elem_type

      def initialize(component_type, length)
        super(component_type.size * length, component_type.alignment)
        @elem_type = component_type
        @elem_type_size = component_type.size
        @length = length
      end

      def char_array?
        FFI::Type::INT8.equal?(@elem_type) || FFI::Type::UINT8.equal?(@elem_type)
      end

      def get_element_at(pointer, offset, index)
        @elem_type.get_at(pointer, offset + index * @elem_type_size)
      end

      def put_element_at(pointer, offset, index, value)
        @elem_type.put_at(pointer, offset + index * @elem_type_size, value)
      end

      def inspect
        "#<#{self.class}[#{@length}] #{@elem_type.inspect}>"
      end
    end

    Array = ArrayType
  end

  # A struct passed or returned by value.  It can sit inside another struct -
  # that is only a layout - but it cannot cross a call: see ffi.rb.
  class StructByValue < Type
    attr_reader :struct_class

    def initialize(struct_class)
      layout = struct_class.instance_variable_get(:@layout)
      unless layout.is_a?(FFI::StructLayout)
        raise TypeError, 'wrong type in @layout ivar (expected FFI::StructLayout)'
      end
      super(layout.size, layout.alignment)
      @struct_class = struct_class
    end

    def get_at(pointer, offset)
      @struct_class.new(pointer.slice(offset, size))
    end

    def put_at(pointer, offset, value)
      unless value.is_a?(@struct_class)
        raise TypeError, "wrong value type (expected #{@struct_class})"
      end
      pointer.slice(offset, size).__copy_from__(value.pointer, size)
    end

    def fiddle_type
      raise NotImplementedError,
        "#{@struct_class} cannot be passed or returned by value on IronRuby: " \
        "a calli signature is built from CLR types, and there is no run-time way to " \
        "give one a C struct's ABI classification.  Pass it by reference instead " \
        "(#{@struct_class}.ptr / the struct's #pointer)."
    end

    def inspect
      "#<#{self.class}:#{@struct_class} size=#{size} alignment=#{alignment}>"
    end
  end

  Type::Struct = StructByValue

  # The signature of a function: what a callback is declared as, and what
  # FFI::Function is built from.
  class FunctionType < Type
    attr_reader :return_type, :param_types, :enums, :blocking, :function_index

    def initialize(return_type, param_types, options = {})
      super(FFI::Type::POINTER.size, FFI::Type::POINTER.alignment, Fiddle::TYPE_VOIDP)
      varargs = options[:varargs]
      @return_type = FFI.find_type(return_type)
      param_types = param_types.map { |type| FFI.find_type(type) }
      # C promotes a float vararg to double, so the prototype has to say double.
      param_types = param_types.map { |t| t.equal?(Type::FLOAT32) ? Type::FLOAT64 : t } if varargs
      @param_types = param_types
      @enums = options[:enums]
      @blocking = options[:blocking]
      @convention = options[:convention]
      @varargs = varargs
      @function_index = param_types.index { |type| type.is_a?(FFI::FunctionType) }
    end

    def abi
      @convention == :stdcall ? Fiddle::Function::STDCALL : Fiddle::Function::DEFAULT
    end

    def varargs?
      !@varargs.nil?
    end

    def inspect
      "#<#{self.class} (#{@param_types.map(&:inspect).join(', ')}) -> #{@return_type.inspect}>"
    end
  end

  CallbackInfo = FunctionType
  FunctionInfo = FunctionType
  Type::Function = FunctionType

  # name, size, alignment, Fiddle type code, AbstractMemory accessor, aliases
  [
    [:VOID,       1,                    1,                  Fiddle::TYPE_VOID,         nil],
    [:INT8,       1,                    Fiddle::ALIGN_CHAR, Fiddle::TYPE_CHAR,         'int8',    :CHAR, :SCHAR],
    [:UINT8,      1,                    Fiddle::ALIGN_CHAR, Fiddle::TYPE_UCHAR,        'uint8',   :UCHAR],
    [:INT16,      2,                    Fiddle::ALIGN_SHORT, Fiddle::TYPE_SHORT,       'int16',   :SHORT, :SSHORT],
    [:UINT16,     2,                    Fiddle::ALIGN_SHORT, Fiddle::TYPE_USHORT,      'uint16',  :USHORT],
    [:INT32,      4,                    Fiddle::ALIGN_INT,  Fiddle::TYPE_INT,          'int32',   :INT, :SINT],
    [:UINT32,     4,                    Fiddle::ALIGN_INT,  Fiddle::TYPE_UINT,         'uint32',  :UINT],
    [:INT64,      8,                    Fiddle::ALIGN_LONG_LONG, Fiddle::TYPE_LONG_LONG,  'int64',  :LONG_LONG, :SLONG_LONG],
    [:UINT64,     8,                    Fiddle::ALIGN_LONG_LONG, Fiddle::TYPE_ULONG_LONG, 'uint64', :ULONG_LONG],
    [:LONG,       Fiddle::SIZEOF_LONG,  Fiddle::ALIGN_LONG, Fiddle::TYPE_LONG,         'long',    :SLONG],
    [:ULONG,      Fiddle::SIZEOF_LONG,  Fiddle::ALIGN_LONG, Fiddle::TYPE_ULONG,        'ulong'],
    [:FLOAT32,    4,                    Fiddle::ALIGN_FLOAT, Fiddle::TYPE_FLOAT,       'float32', :FLOAT],
    [:FLOAT64,    8,                    Fiddle::ALIGN_DOUBLE, Fiddle::TYPE_DOUBLE,     'float64', :DOUBLE],
    # .NET has no 80-bit float; the type exists so that a layout mentioning it
    # still has the right size, and using it in a call says so.
    [:LONGDOUBLE, FFI::Platform::ADDRESS_SIZE == 64 ? 16 : 12, Fiddle::ALIGN_DOUBLE, nil, nil],
    [:POINTER,    Fiddle::SIZEOF_VOIDP, Fiddle::ALIGN_VOIDP, Fiddle::TYPE_VOIDP,       'pointer'],
    [:STRING,     Fiddle::SIZEOF_VOIDP, Fiddle::ALIGN_VOIDP, Fiddle::TYPE_CONST_STRING, 'string'],
    [:BUFFER_IN,  Fiddle::SIZEOF_VOIDP, Fiddle::ALIGN_VOIDP, Fiddle::TYPE_VOIDP,       'pointer'],
    [:BUFFER_OUT, Fiddle::SIZEOF_VOIDP, Fiddle::ALIGN_VOIDP, Fiddle::TYPE_VOIDP,       'pointer'],
    [:BUFFER_INOUT, Fiddle::SIZEOF_VOIDP, Fiddle::ALIGN_VOIDP, Fiddle::TYPE_VOIDP,     'pointer'],
    [:BOOL,       1,                    Fiddle::ALIGN_BOOL, Fiddle::TYPE_BOOL,         'bool'],
    [:VARARGS,    1,                    1,                  Fiddle::TYPE_VARIADIC,     nil],
  ].each do |name, size, alignment, fiddle_type, accessor, *aliases|
    type = Type::Builtin.new(name, size, alignment, fiddle_type, accessor)
    FFI::Type.const_set name, type
    FFI::NativeType.const_set name, type
    FFI.const_set "TYPE_#{name}", type
    aliases.each { |a| FFI::Type.const_set a, type }
  end
end
