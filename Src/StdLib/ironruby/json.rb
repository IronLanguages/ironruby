# MRI implements json as a C extension. IronRuby provides it in C# instead
# (Src/Libraries/Json), the same way it provides Digest, Zlib and StringIO.
load_assembly 'IronRuby.Libraries', 'IronRuby.StandardLibrary.Json'

module JSON
  # IronRuby implements the JSON 2.x API in C#; this is the version of that
  # API, not of upstream's C extension.
  VERSION = "2.18.0"
  VERSION_ARRAY = VERSION.split(".").map {|x| x.to_i } # :nodoc:
  VERSION_MAJOR = VERSION_ARRAY[0] # :nodoc:
  VERSION_MINOR = VERSION_ARRAY[1] # :nodoc:
  VERSION_BUILD = VERSION_ARRAY[2] # :nodoc:

  # json/common.rb's create identifier, which the json/add/* files name their class under.
  def self.create_id=(new_value)
    Thread.current[:"JSON.create_id"] = new_value.dup.freeze
  end

  def self.create_id
    Thread.current[:"JSON.create_id"] || 'json_class'
  end

  # json/common.rb's JSON.dump: generate with dump_default_options, writing to an IO when given
  # one. The C# module's dump was generate under another name, so JSON.dump(obj, file) returned
  # the String and left the file empty, and a NaN raised instead of being written.
  def self.dump_default_options
    @dump_default_options ||= { max_nesting: false, allow_nan: true, script_safe: false }
  end

  def self.dump_default_options=(options)
    @dump_default_options = options
  end

  def self.dump(obj, an_io = nil, limit = nil, kwargs = nil)
    if kwargs.nil?
      if limit.nil?
        if an_io.is_a?(Hash)
          kwargs = an_io
          an_io = nil
        end
      elsif limit.is_a?(Hash)
        kwargs = limit
        limit = nil
      end
    end
    unless an_io.nil?
      if an_io.respond_to?(:to_io)
        an_io = an_io.to_io
      elsif limit.nil? && !an_io.respond_to?(:write)
        an_io, limit = nil, an_io
      end
    end
    opts = dump_default_options
    opts = opts.merge(max_nesting: limit) if limit
    opts = opts.merge(kwargs) if kwargs
    begin
      result = generate(obj, opts)
    rescue JSON::NestingError
      raise ArgumentError, "exceed depth limit"
    end
    if an_io.nil?
      result
    else
      an_io.write(result)
      an_io
    end
  end


  # MRI declares these with module_function, so "include JSON" hands them on as
  # private instance methods - which is how JSON's own test suite calls parse().
  # The C# side can only declare singleton methods, so the instance half is here.
  %i[parse load generate dump pretty_generate unparse pretty_unparse].each do |name|
    define_method(name) { |*args, &block| JSON.send(name, *args, &block) }
    private name
  end
end

# The json gem's generic to_json methods (generator/generator_methods): the core classes generate
# themselves, and any other object is its to_s as a JSON string. Object#to_json used to be
# JSON.generate(self), so a to_json that calls super with a State - ActiveSupport's does - went
# round JSON.generate and back into itself.
class Object
  def to_json(state = nil, *) = to_s.to_json(state)
end

[Hash, Array, String, Integer, Float, NilClass, TrueClass, FalseClass].each do |klass|
  klass.class_eval do
    def to_json(state = nil, *) = JSON.generate(self, state)
  end
end

module JSON
  ##
  # The generator's options object.
  #
  # Upstream this is JSON::Ext::Generator::State, a C struct with accessors, and
  # JSON::State is an alias for it.  It has to exist here for two reasons: a
  # Gemfile's worth of code passes a State to JSON.generate, and ActiveSupport
  # asks `options.is_a?(::JSON::State)` inside *every* to_json it defines, to
  # tell a call from JSON.generate apart from a direct one - so without the
  # constant a Rails app raises NameError from the first object it renders.
  #
  # IronRuby's generator lives in C# and takes a Hash of options, so this is that
  # Hash with the accessors upstream gives it, and #generate hands it back.
  # Upstream registers the class as JSON::Ext::Generator::State and makes
  # JSON::State the alias, and #inspect and error messages show the long name,
  # so it is defined in that place here too.
  module Ext
    module Generator
      class State
      end
    end
  end

  State = Ext::Generator::State

  class State
    ATTRIBUTES = {
      indent: "",
      space: "",
      space_before: "",
      object_nl: "",
      array_nl: "",
      as_json: false,
      allow_nan: false,
      ascii_only: false,
      script_safe: false,
      strict: false,
      allow_duplicate_key: false,
      max_nesting: 100,
      depth: 0,
      buffer_initial_length: 1024,
    }.freeze

    ATTRIBUTES.each_key do |name|
      attr_accessor name
    end

    # Upstream spells the predicates without the "is" and answers a boolean.
    def allow_nan? = !!@allow_nan
    def ascii_only? = !!@ascii_only
    def script_safe? = !!@script_safe
    def strict? = !!@strict
    def allow_duplicate_key? = !!@allow_duplicate_key

    # Circular references are refused whenever there is a nesting limit.
    def check_circular? = !@max_nesting.zero?

    def self.from_state(opts)
      case opts
      when self then opts
      when Hash then new(opts)
      when nil then new
      else
        opts.respond_to?(:to_hash) ? new(opts.to_hash) : new
      end
    end

    def initialize(opts = nil)
      ATTRIBUTES.each { |name, default| instance_variable_set(:"@#{name}", default) }
      configure(opts) if opts
    end

    def configure(opts)
      opts = opts.to_h if !opts.is_a?(Hash) && opts.respond_to?(:to_h)
      opts.each do |key, value|
        key = key.to_sym
        instance_variable_set(:"@#{key}", value) if ATTRIBUTES.key?(key)
      end
      self
    end
    alias merge configure

    def to_h
      ATTRIBUTES.each_key.to_h {|name| [name, instance_variable_get(:"@#{name}")] }
    end
    alias to_hash to_h

    def [](name)
      name = name.to_sym
      ATTRIBUTES.key?(name) ? instance_variable_get(:"@#{name}") : nil
    end

    def []=(name, value)
      instance_variable_set(:"@#{name.to_sym}", value)
    end

    def generate(obj)
      JSON.generate(obj, to_h)
    end
  end

  # JSON.state is how upstream hands out the generator's state class.
  def self.state
    State
  end

  # The C# generator takes its options as a Hash; upstream's also takes a State, or anything
  # with #to_hash - a State's options were dropped.
  class << self
    alias_method :__ir_generate__, :generate
    alias_method :__ir_pretty_generate__, :pretty_generate

    def generate(obj, opts = nil)
      __ir_generate__(obj, __ir_options__(opts))
    end

    def pretty_generate(obj, opts = nil)
      __ir_pretty_generate__(obj, __ir_options__(opts))
    end

    def unparse(obj, opts = nil) = generate(obj, opts)
    def pretty_unparse(obj, opts = nil) = pretty_generate(obj, opts)

    private

    def __ir_options__(opts)
      case opts
      when nil, ::Hash then opts
      else
        if opts.is_a?(State) then opts.to_h
        elsif opts.respond_to?(:to_hash) then opts.to_hash
        else ::Kernel.raise ::TypeError, "can't convert #{opts.class} into Hash"
        end
      end
    end
  end

  GENERIC_TO_JSON = [::Object, ::Hash, ::Array, ::String, ::Integer, ::Float, ::NilClass, ::TrueClass, ::FalseClass].
    map { |klass| klass.instance_method(:to_json) }.freeze
  private_constant :GENERIC_TO_JSON

  # json/common.rb's parsing API on top of the C# parser. The parser reads the grammar's options
  # itself (symbolize_names, allow_control_characters, allow_nan, allow_trailing_comma,
  # max_nesting, decimal_class); what upstream does with the values as each Array or object is
  # closed - object_class, array_class, create_additions - and freeze are done here, bottom up as
  # upstream does them. None of these, nor JSON.load's IO and blank handling, load_file, parse!,
  # unsafe_load or Kernel#JSON, existed before.
  class << self
    alias_method :__ir_parse__, :parse

    def parse(source, opts = nil)
      opts = __ir_parse_options__(opts)
      source = source.to_str if !source.is_a?(::String) && source.respond_to?(:to_str)
      result = __ir_parse__(source, opts)
      __ir_after_parse__(result, opts)
    end

    def parse!(source, opts = nil)
      parse(source, { max_nesting: false, allow_nan: true }.merge(__ir_parse_options__(opts) || {}))
    end

    def load_file(filespec, opts = nil)
      parse(::File.read(filespec, encoding: ::Encoding::UTF_8), opts)
    end

    def load_file!(filespec, opts = nil)
      parse!(::File.read(filespec, encoding: ::Encoding::UTF_8), opts)
    end

    attr_writer :load_default_options, :unsafe_load_default_options

    def load_default_options
      @load_default_options ||= { allow_nan: true, allow_blank: true, create_additions: false }
    end

    def unsafe_load_default_options
      @unsafe_load_default_options ||= { max_nesting: false, allow_nan: true, allow_blank: true, create_additions: true }
    end

    def load(source, proc = nil, options = nil)
      __ir_load__(source, proc, load_default_options.merge(options || {}))
    end

    def unsafe_load(source, proc = nil, options = nil)
      __ir_load__(source, proc, unsafe_load_default_options.merge(options || {}))
    end
    alias_method :restore, :load

    private

    def __ir_load__(source, proc, opts)
      unless source.is_a?(::String)
        if source.respond_to?(:to_str)
          source = source.to_str
        elsif source.respond_to?(:to_io)
          source = source.to_io.read
        elsif source.respond_to?(:read)
          source = source.read
        end
      end
      source = "null" if opts[:allow_blank] && (source.nil? || source.empty?)
      result = parse(source, opts)
      proc ? __ir_on_load__(result, proc) : result
    end

    # The parser's on_load: every value, bottom up and a key before its value, is replaced by what
    # the proc answers for it - the whole document included.
    def __ir_on_load__(value, proc)
      case value
      when ::Array
        value.map! { |x| __ir_on_load__(x, proc) }
      when ::Hash
        pairs = value.map { |k, v| [__ir_on_load__(k, proc), __ir_on_load__(v, proc)] }
        value.clear
        pairs.each { |k, v| value[k] = v }
      end
      proc.call(value)
    end

    def __ir_parse_options__(opts)
      return nil if opts.nil?
      opts = opts.to_hash if !opts.is_a?(::Hash) && opts.respond_to?(:to_hash)
      opts = opts.to_h if !opts.is_a?(::Hash) && opts.respond_to?(:to_h)
      opts
    end

    def __ir_after_parse__(result, opts)
      return result if opts.nil?
      object_class = opts[:object_class]
      array_class = opts[:array_class]
      additions = opts[:create_additions]
      if object_class || array_class || additions
        create_id = opts[:create_id] || JSON.create_id
        result = __ir_rebuild__(result, object_class, array_class, additions, create_id)
      end
      __ir_deep_freeze__(result) if opts[:freeze]
      result
    end

    def __ir_rebuild__(value, object_class, array_class, additions, create_id)
      case value
      when ::Array
        items = value.map { |x| __ir_rebuild__(x, object_class, array_class, additions, create_id) }
        if array_class
          rebuilt = array_class.new
          items.each { |x| rebuilt << x }
          rebuilt
        else
          items
        end
      when ::Hash
        rebuilt = object_class ? object_class.new : {}
        value.each { |k, v| rebuilt[k] = __ir_rebuild__(v, object_class, array_class, additions, create_id) }
        if additions && (class_name = rebuilt[create_id])
          klass = JSON.deep_const_get(class_name)
          return klass.json_create(rebuilt) if klass.respond_to?(:json_creatable?) ? klass.json_creatable? : klass.respond_to?(:json_create)
        end
        rebuilt
      else
        value
      end
    end

    def __ir_deep_freeze__(value)
      case value
      when ::Array then value.each { |x| __ir_deep_freeze__(x) }
      when ::Hash then value.each { |k, v| __ir_deep_freeze__(k); __ir_deep_freeze__(v) }
      end
      value.freeze
    end
  end

  # json/common.rb: the class a create_additions name refers to
  def self.deep_const_get(path)
    ::Object.const_get(path)
  rescue ::NameError => e
    ::Kernel.raise ::ArgumentError, "can't get const #{path}: #{e}"
  end unless respond_to?(:deep_const_get)

  # The decimal_class of JSON.parse, given a Float literal's text, as upstream's parser makes it:
  # Kernel#BigDecimal for BigDecimal, else the class's try_convert, its new, or the Kernel method
  # named after it (Rational).
  def self.__ir_decimal__(decimal_class, text)
    if defined?(::BigDecimal) && decimal_class == ::BigDecimal
      ::Kernel.BigDecimal(text)
    elsif decimal_class.respond_to?(:try_convert)
      decimal_class.try_convert(text)
    elsif decimal_class.respond_to?(:new)
      decimal_class.new(text)
    elsif decimal_class.is_a?(::Class) && ::Kernel.respond_to?(decimal_class.name, true)
      ::Kernel.send(decimal_class.name, text)
    else
      ::Kernel.raise ::ArgumentError, "can't create a #{decimal_class} from a JSON number"
    end
  end

  # Called by the C# generator for an object that is not exactly one of the core classes, the
  # way the json gem calls obj.to_json(state) for one: the String the object's own to_json makes,
  # or nil when its to_json is one of the generic ones above: a subclass of a core class is then
  # written as that class, and anything else as its to_s.
  def self.__ir_to_json__(obj, options, depth)
    to_json = begin
      ::Kernel.instance_method(:method).bind_call(obj, :to_json)
    rescue ::NameError
      return nil
    end
    return nil if GENERIC_TO_JSON.include?(to_json.unbind)
    # A to_json of a core subclass that calls super - HashWithIndifferentAccess and SafeBuffer under
    # ActiveSupport - comes back to JSON.generate with the same object; the gem then writes it as its
    # core class, and so does the C# writer when this answers nil for it.
    active = (::Thread.current[:__ir_json_to_json_active__] ||= {}.compare_by_identity)
    return nil if active.key?(obj)
    state = State.new(options)
    state.depth = depth
    active[obj] = true
    begin
      result = obj.to_json(state)
    ensure
      active.delete(obj)
    end
    unless result.is_a?(::String)
      ::Kernel.raise ::TypeError, "wrong argument type #{result.class} (expected String)"
    end
    result
  end

end

module Kernel
  private

  # json/common.rb's Kernel#JSON, #j and #jj
  def JSON(object, *args)
    if object.is_a?(::String)
      ::JSON.parse(object, args.first)
    elsif object.respond_to?(:to_str)
      ::JSON.parse(object.to_str, args.first)
    else
      ::JSON.generate(object, args.first)
    end
  end

  def j(*objs)
    objs.each { |obj| puts ::JSON.generate(obj, allow_nan: true, max_nesting: false) }
    nil
  end

  def jj(*objs)
    objs.each { |obj| puts ::JSON.pretty_generate(obj, allow_nan: true, max_nesting: false) }
    nil
  end
end
