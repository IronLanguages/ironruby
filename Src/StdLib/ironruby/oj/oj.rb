# frozen_string_literal: true
#
# The oj gem's C extension, provided by IronRuby itself.
#
# oj 3.17.6's Ruby (Src/StdLib/ironruby/oj.rb and oj/, vendored unchanged) ends
# with `require 'oj/oj'`, the compiled extension on CRuby; this file answers it.
# The extension is C# (Src/Libraries/Oj) for the :strict, :null, :compat and
# :rails modes of Oj.dump/Oj.load, Oj.mimic_JSON, Oj.generate/to_json and
# Oj::Rails - what multi_json and Rails use Oj for.  Everything else raises
# NotImplementedError saying what it is: the :object, :custom and :wab modes,
# Oj::Doc, Oj::StringWriter, Oj::StreamWriter, Oj::Parser and the Saj/Scp
# callback parsers.
#
# The part of the extension that is Ruby metaprogramming done through the C API -
# replacing JSON's methods for mimic_JSON, ActiveSupport's encoder settings for
# Oj::Rails.set_encoder - is written here, as Ruby, over C# primitives named
# Oj.__mimic_*.
#
# A pinned default gemspec makes RubyGems agree that oj 3.17.6 is installed.

load_assembly 'IronRuby.Libraries', 'IronRuby.StandardLibrary.Oj'

require 'date'
begin
  require 'time'
  require 'bigdecimal'
rescue LoadError
end
require 'stringio'

module Oj
  # rb_define_class_under(Oj, "CStack", rb_cObject) with no allocator.
  class CStack
    class << self
      undef_method :new
      undef_method :allocate
    end
  end

  # The classes the port does not cover.  Each says so rather than half working.
  class Doc
    NOT_IMPLEMENTED = "Oj::Doc is not implemented on IronRuby"
    def self.open(*)
      raise ::NotImplementedError, NOT_IMPLEMENTED
    end
    def self.parse(*)
      raise ::NotImplementedError, NOT_IMPLEMENTED
    end
    def self.open_file(*)
      raise ::NotImplementedError, NOT_IMPLEMENTED
    end
    def self.new(*)
      raise ::NotImplementedError, NOT_IMPLEMENTED
    end
  end

  class StringWriter
    def initialize(*)
      raise ::NotImplementedError, "Oj::StringWriter is not implemented on IronRuby"
    end
  end

  class StreamWriter
    def initialize(*)
      raise ::NotImplementedError, "Oj::StreamWriter is not implemented on IronRuby"
    end
  end

  class Parser
    NOT_IMPLEMENTED = "Oj::Parser is not implemented on IronRuby"
    def self.new(*)
      raise ::NotImplementedError, NOT_IMPLEMENTED
    end
    def self.usual(*)
      raise ::NotImplementedError, NOT_IMPLEMENTED
    end
    def self.saj(*)
      raise ::NotImplementedError, NOT_IMPLEMENTED
    end
    def self.validate(*)
      raise ::NotImplementedError, NOT_IMPLEMENTED
    end
  end

  # oj_get_json_err_class: JSON, JSON::JSONError and the class asked for, each
  # defined when it is not already.
  def self.__json_error_class(name)
    json = ::Object.const_defined?(:JSON, false) ? ::JSON : ::Object.const_set(:JSON, ::Module.new)
    json_error = if json.const_defined?(:JSONError, false)
                   json.const_get(:JSONError)
                 else
                   json.const_set(:JSONError, ::Class.new(::StandardError))
                 end
    return json_error if 'JSONError' == name

    if json.const_defined?(name, false)
      json.const_get(name)
    else
      json.const_set(name, ::Class.new(json_error))
    end
  end

  # What rb_define_module_function(mod, name, ...) does: a public singleton
  # method and a private instance method.
  def self.__module_function(mod, name, &body)
    mod.singleton_class.send(:define_method, name, &body)
    mod.send(:define_method, name, &body)
    mod.send(:private, name)
  end

  # oj_mimic_json_methods.
  def self.__mimic_json_methods(json)
    verbose = $VERBOSE
    $VERBOSE = false
    begin
      __module_function(json, :create_id=) { |id| ::Oj.__mimic_set_create_id(id) }
      __module_function(json, :create_id) { ::Oj.__mimic_create_id }
      __module_function(json, :dump) { |*args| ::Oj.__mimic_dump(*args) }
      __module_function(json, :load) { |*args, &blk| ::Oj.__mimic_load(*args, &blk) }
      __module_function(json, :restore) { |*args, &blk| ::Oj.__mimic_load(*args, &blk) }
      __module_function(json, :recurse_proc) { |obj, &blk| ::Oj.__mimic_recurse_proc(obj, &blk) }
      __module_function(json, :[]) { |*args, &blk| ::Oj.__mimic_dump_load(*args, &blk) }
      __module_function(json, :generate) { |*args| ::Oj.__mimic_generate(*args) }
      __module_function(json, :fast_generate) { |*args| ::Oj.__mimic_generate(*args) }
      __module_function(json, :pretty_generate) { |*args| ::Oj.__mimic_pretty_generate(*args) }
      __module_function(json, :unparse) { |*args| ::Oj.__mimic_generate(*args) }
      __module_function(json, :fast_unparse) { |*args| ::Oj.__mimic_generate(*args) }
      __module_function(json, :pretty_unparse) { |*args| ::Oj.__mimic_pretty_generate(*args) }
      __module_function(json, :parse) { |*args| ::Oj.__mimic_parse(false, *args) }
      __module_function(json, :parse!) { |*args| ::Oj.__mimic_parse(true, *args) }
      __module_function(json, :state) { ::Oj.__mimic_state }
    ensure
      $VERBOSE = verbose
    end

    json_error = if json.const_defined?(:JSONError, false)
                   json.const_get(:JSONError)
                 else
                   json.const_set(:JSONError, ::Class.new(::StandardError))
                 end
    parser_error = if json.const_defined?(:ParserError, false)
                     json.const_get(:ParserError)
                   else
                     json.const_set(:ParserError, ::Class.new(json_error))
                   end
    generator_error = if json.const_defined?(:GeneratorError, false)
                        json.const_get(:GeneratorError)
                      else
                        json.const_set(:GeneratorError, ::Class.new(json_error))
                      end
    json.const_set(:NestingError, ::Class.new(json_error)) unless json.const_defined?(:NestingError, false)
    ::Oj.__set_json_error_classes(parser_error, generator_error)

    ext = json.const_defined?(:Ext, false) ? json.const_get(:Ext) : json.const_set(:Ext, ::Module.new)
    generator = ext.const_defined?(:Generator, false) ? ext.const_get(:Generator) : ext.const_set(:Generator, ::Module.new)
    require 'oj/state' unless generator.const_defined?(:State, false)
    ::Oj.__set_state_class(generator.const_get(:State))
  end

  # oj_define_mimic_json.
  def self.mimic_JSON(*args)
    json = ::Object.const_defined?(:JSON, false) ? ::JSON : ::Object.const_set(:JSON, ::Module.new)
    verbose = $VERBOSE
    $VERBOSE = false
    begin
      __module_function(::Object, :JSON) { |*a, &blk| ::Oj.__mimic_dump_load(*a, &blk) }
      if $LOADED_FEATURES.is_a?(::Array)
        $LOADED_FEATURES << 'json'
        if args.empty?
          ::Oj.mimic_loaded
        else
          ::Oj.mimic_loaded(args[0])
        end
      end
      __mimic_json_methods(json)
      unless ::Object.const_defined?(:ActiveSupport)
        ::Object.send(:define_method, :to_json) { |*a| ::Oj.__mimic_object_to_json(self, *a) }
      end
    ensure
      $VERBOSE = verbose
    end
    ::Oj.__set_mimic_defaults
    json
  end

  # Oj.optimize_rails: Oj as the Rails encoder and decoder, everything optimized.
  def self.optimize_rails
    Rails.set_encoder
    Rails.set_decoder
    Rails.optimize
    Rails.mimic_JSON
    nil
  end

  module Rails
    # rails_mimic_json: the JSON methods, but not the defaults.
    def self.mimic_JSON
      json = ::Object.const_defined?(:JSON, false) ? ::JSON : ::Object.const_set(:JSON, ::Module.new)
      ::Oj.__mimic_json_methods(json)
      nil
    end

    def self.__resolve(path)
      path.split('::').inject(::Object) do |mod, name|
        return nil unless mod.const_defined?(name, false)

        mod.const_get(name, false)
      end
    end

    # rails_set_encoder.
    def self.set_encoder
      enc = __resolve('ActiveSupport::JSON::Encoding')
      if enc
        __set_escape_html(true == instance_variable_get(:@escape_html_entities_in_json))
        __set_xml_time(true == enc.instance_variable_get(:@use_standard_json_time_format))
      end
      raise ::StandardError, 'ActiveSupport not loaded.' unless ::Object.const_defined?(:ActiveSupport, false)

      active = ::ActiveSupport
      encoding = active::JSON::Encoding
      rails = self

      verbose = $VERBOSE
      $VERBOSE = false
      begin
        encoding.singleton_class.class_eval do
          define_method(:use_standard_json_time_format=) do |state|
            state = if true == state || false == state
                      state
                    else
                      !state.nil?
                    end
            instance_variable_set(:@use_standard_json_time_format, state)
            rails.__set_xml_time(true == state)
            state
          end
          define_method(:use_standard_json_time_format) { rails.__xml_time }
          define_method(:escape_html_entities_in_json=) do |state|
            instance_variable_set(:@escape_html_entities_in_json, state)
            rails.__set_escape_html(true == state)
            state
          end
          define_method(:escape_html_entities_in_json) { rails.__escape_html }
          define_method(:time_precision=) do |prec|
            instance_variable_set(:@time_precision, prec)
            rails.__set_time_precision(prec)
            prec
          end
        end

        __set_escape_html(true == encoding.instance_variable_get(:@escape_html_entities_in_json))
        __set_time_precision(encoding.instance_variable_get(:@time_precision))
      ensure
        $VERBOSE = verbose
      end

      # last: ActiveSupport builds and caches encoders inside json_encoder=
      active.json_encoder = Encoder
      nil
    end

    # rails_set_decoder: JSON.parse becomes Oj's json gem compatible parser.
    def self.set_decoder
      json = ::Object.const_defined?(:JSON, false) ? ::JSON : ::Object.const_set(:JSON, ::Module.new)
      json_error = if json.const_defined?(:JSONError, false)
                     json.const_get(:JSONError)
                   else
                     json.const_set(:JSONError, ::Class.new(::StandardError))
                   end
      parser_error = if json.const_defined?(:ParserError, false)
                       json.const_get(:ParserError)
                     else
                       json.const_set(:ParserError, ::Class.new(json_error))
                     end
      ::Oj.__set_json_error_classes(parser_error, nil)
      verbose = $VERBOSE
      $VERBOSE = false
      begin
        ::Oj.__module_function(json, :parse) { |*args| ::Oj.__mimic_parse(false, *args) }
      ensure
        $VERBOSE = verbose
      end
      nil
    end
  end
end
