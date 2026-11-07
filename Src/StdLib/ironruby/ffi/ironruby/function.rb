# frozen_string_literal: true
#
# FFI::Function and FFI::VariadicInvoker - what the ffi C extension's
# Function.c, NativeFunction and Variadic.c build.
#
# A Function is a Pointer whose address is code.  Calling it is a
# Fiddle::Function call: ffi's types become Fiddle's TYPE_* codes, the
# arguments are converted to what Fiddle accepts, and the result is converted
# back.  A Function made from a Proc is the other direction - a
# Fiddle::Closure, whose address is the trampoline C will call.
#
# Every attached function asks Fiddle for save_last_error: true, which is what
# makes FFI.errno answer a real errno; see ffi/ironruby/last_error.rb.

module FFI
  class Function < Pointer
    attr_reader :function_type

    def initialize(return_type, param_types, function = nil, options = nil, &block)
      function ||= block
      options ||= {}

      @function_type = if return_type.is_a?(FunctionType) && param_types.nil?
        return_type
      else
        FunctionType.new(return_type, param_types, options)
      end

      case function
      when FFI::Pointer
        @address = function.address
        @closure = nil
      when Integer
        @address = function
        @closure = nil
      when ::Proc, ::Method
        @closure = FFI::Function.make_closure(@function_type, function)
        @address = @closure.to_i
      else
        if function.respond_to?(:call)
          @closure = FFI::Function.make_closure(@function_type, function)
          @address = @closure.to_i
        else
          raise ArgumentError, "Unknown how to convert #{function.inspect} to a function"
        end
      end

      @size = UNBOUNDED
      @type_size = 1
      @swap = false
      @autorelease = true
      self
    end

    def return_type
      @function_type.return_type
    end

    def param_types
      @function_type.param_types
    end

    def call(*args, &block)
      types = @function_type.param_types
      args = args.dup
      args.insert(@function_type.function_index, block) if block && @function_type.function_index

      unless args.size == types.size
        raise ArgumentError, "wrong number of arguments (#{args.size} for #{types.size})"
      end

      # asking for the Fiddle function first means a signature this runtime
      # cannot express says so before an argument conversion says something
      # less helpful about the same type
      native = fiddle_function

      enums = @function_type.enums
      converted = ::Array.new(args.size) do |i|
        FFI.convert_to_native(types[i], args[i], enums)
      end

      result = native.call(*converted)
      FFI.convert_from_native(@function_type.return_type, result)
    end

    def attach(mod, name)
      this = self
      body = lambda do |*args, &block|
        this.call(*args, &block)
      end
      mod.define_method(name, body)
      mod.define_singleton_method(name, body)
      self
    end

    def autorelease?
      @autorelease
    end

    def autorelease=(value)
      @autorelease = value
    end

    def free
      raise RuntimeError, 'cannot free function which was not allocated' unless @closure
      @closure.free
      @closure = nil
      nil
    end

    def inspect
      "#<#{self.class} address=0x#{@address.to_s(16)}>"
    end
    alias_method :to_s, :inspect

    private def type
      @function_type
    end

    private def fiddle_function
      @fiddle_function ||= begin
        type = @function_type
        Fiddle::Function.new(@address,
                             type.param_types.map { |t| FFI.fiddle_type_of(t) },
                             FFI.fiddle_type_of(type.return_type),
                             type.abi,
                             save_last_error: true)
      end
    end

    # The C side of a Ruby callable: a Fiddle::Closure whose #call converts the
    # arguments C hands it into Ruby values, runs the callable, and converts
    # what comes back.
    def self.make_closure(function_type, callable)
      param_types = function_type.param_types
      return_type = function_type.return_type
      enums = function_type.enums

      Fiddle::Closure::BlockCaller.new(
        FFI.fiddle_type_of(return_type),
        param_types.map { |t| FFI.fiddle_type_of(t) },
        function_type.abi
      ) do |*args|
        args = args.each_with_index.map { |value, i| FFI.convert_from_native(param_types[i], value) }
        result = callable.call(*args)
        FFI.convert_to_native(return_type, result, enums)
      end
    end
  end

  class VariadicInvoker
    attr_reader :return_type

    def initialize(function, args_types, return_type, options)
      @function = function
      @return_type = FFI.find_type(return_type)
      @options = options
      @fixed = args_types.map { |type| FFI.find_type(type) }.reject { |type| type.equal?(Type::VARARGS) }
      @type_map = options[:type_map]
      @enums = options[:enums]
      @convention = options[:convention]
      @options[:varargs] = @fixed.size
    end

    # param_types is the fixed types plus one entry per variadic argument,
    # which is exactly the shape Fiddle's variadic Function wants - fixed
    # values first, then type/value pairs.
    def invoke(param_types, param_values, &block)
      variadic = param_types[@fixed.size..-1] || []
      abi = @convention == :stdcall ? Fiddle::Function::STDCALL : Fiddle::Function::DEFAULT

      function = Fiddle::Function.new(
        FFI.pointer_address(@function),
        @fixed.map { |t| FFI.fiddle_type_of(t) } + [Fiddle::TYPE_VARIADIC],
        FFI.fiddle_type_of(@return_type),
        abi,
        save_last_error: true)

      args = []
      @fixed.each_with_index do |type, i|
        args << FFI.convert_to_native(type, param_values[i], @enums)
      end
      variadic.each_with_index do |type, i|
        # C promotes a float vararg to double
        type = Type::FLOAT64 if type.equal?(Type::FLOAT32)
        args << FFI.fiddle_type_of(type)
        args << FFI.convert_to_native(type, param_values[@fixed.size + i], @enums)
      end

      FFI.convert_from_native(@return_type, function.call(*args))
    end
  end

  class << self
    # The Fiddle TYPE_* code a native call passes this ffi type as.
    def fiddle_type_of(type)
      type = type.native_type if type.is_a?(Type::Mapped)
      type.fiddle_type
    end

    # A Ruby value, as the Fiddle argument standing for it.
    def convert_to_native(type, value, enums = nil)
      if type.is_a?(Type::Mapped)
        return convert_to_native(type.native_type, type.to_native(value, nil), nil)
      end

      if type.is_a?(FunctionType)
        return 0 if value.nil?
        return value.address if value.is_a?(FFI::Function)
        return closure_for(type, value).to_i
      end

      if enums && value.is_a?(::Symbol)
        mapped = enums.__map_symbol(value)
        raise ArgumentError, "invalid enum value, #{value.inspect}" unless mapped
        value = mapped
      end

      case type
      when Type::STRING
        return 0 if value.nil?
        return value.address if value.is_a?(AbstractMemory)
        value = value.to_str if !value.is_a?(::String) && value.respond_to?(:to_str)
        unless value.is_a?(::String)
          raise TypeError, "no implicit conversion of #{value.class} into String"
        end
        if value.include?("\0".b)
          raise ArgumentError, 'string contains null byte'
        end
        value
      when Type::POINTER, Type::BUFFER_IN, Type::BUFFER_OUT, Type::BUFFER_INOUT
        return value if value.is_a?(::String)
        pointer_address(value)
      when Type::BOOL
        unless value == true || value == false
          raise TypeError, "wrong argument type #{value.class} (expected bool)"
        end
        value
      when Type::FLOAT32, Type::FLOAT64
        Float(value)
      when Type::VOID
        # the only value ever converted "to" void is a void callback's result
        0
      else
        value = value.is_a?(Integer) ? value : Integer(value)
        narrow_integer(type, value)
      end
    end

    # What Fiddle handed back, as the Ruby value the ffi type names.
    def convert_from_native(type, value)
      if type.is_a?(Type::Mapped)
        return type.from_native(convert_from_native(type.native_type, value), nil)
      end
      if type.is_a?(FunctionType)
        return Function.new(type, nil, Pointer.new(fiddle_address(value)))
      end

      case type
      when Type::VOID then nil
      when Type::POINTER, Type::BUFFER_IN, Type::BUFFER_OUT, Type::BUFFER_INOUT
        Pointer.new(fiddle_address(value))
      when Type::STRING
        # Fiddle answers a String for TYPE_CONST_STRING, or nil for NULL
        value
      when Type::BOOL
        value
      else
        value
      end
    end

    # The closure standing for a callable passed as a callback argument.  It is
    # kept on the callable itself, as ffi keeps its Function on the Proc: C
    # holds nothing but the trampoline's address, so a closure that died with
    # the call would leave a library like libcurl calling freed memory the next
    # time it used the callback it was handed.
    def closure_for(type, callable)
      cache = callable.__send__(:__ir_hidden_ivar__, :@__ffi_closures__)
      unless cache
        cache = {}
        begin
          callable.__send__(:__ir_set_hidden_ivar__, :@__ffi_closures__, cache)
        rescue FrozenError, RuntimeError
          # a frozen callable cannot hold the cache; the closure then lives
          # only as long as the call, which is all that can be promised
        end
      end
      cache[type] ||= Function.make_closure(type, callable)
    end

    # Fiddle takes every integer argument as a CLR Int64, so a value that only
    # fits the unsigned range of its type has to go across as the same bits
    # read as a signed number.  Anything that fits neither is out of range, and
    # ffi says so rather than truncating.
    def narrow_integer(type, value)
      return value unless type.is_a?(Type::Builtin)
      limit = 1 << (type.size * 8)
      half = limit >> 1
      if value >= -half && value < half
        value
      elsif value >= 0 && value < limit
        value - limit
      else
        raise RangeError, "Value #{value} outside #{type.name} range"
      end
    end

    private def fiddle_address(value)
      case value
      when nil then 0
      when Integer then value
      when Fiddle::Pointer then value.to_i
      else value.to_i
      end
    end
  end
end
