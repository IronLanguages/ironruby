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

  # Convenience wrappers the C# module does not need to provide itself.
  def self.pretty_unparse(obj, opts = nil); pretty_generate(obj, opts); end
  def self.unparse(obj, opts = nil); generate(obj, opts); end

  # MRI declares these with module_function, so "include JSON" hands them on as
  # private instance methods - which is how JSON's own test suite calls parse().
  # The C# side can only declare singleton methods, so the instance half is here.
  %i[parse load generate dump pretty_generate unparse pretty_unparse].each do |name|
    define_method(name) { |*args, &block| JSON.send(name, *args, &block) }
    private name
  end
end

class Object
  def to_json(*args); JSON.generate(self, *args); end unless method_defined?(:to_json)
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

end
