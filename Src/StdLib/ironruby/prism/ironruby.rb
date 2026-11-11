# frozen_string_literal: true
# :markup: markdown
#
# IronRuby's backend for the prism Ruby library, in place of upstream's
# prism/ffi.rb.
#
# prism's Ruby API is a decoder: every entry point asks libprism to serialize a
# parse, a lex or a comment list, and Prism::Serialize (pure Ruby, generated
# alongside the parser) turns the bytes into nodes.  Upstream gets those bytes
# from a CRuby C extension, or from the `ffi` gem on other implementations.
#
# IronRuby needs neither.  libprism is already linked in - it is the parser that
# compiles every file this interpreter runs (Src/Prism) - so the bytes come
# straight from Src/Libraries/Prism/PrismOps.cs.  That also means `require
# "prism"` works out of the box, which the FFI path never could: `ffi` is a C
# extension itself.
#
# Everything below except the backend calls themselves is upstream's
# prism/ffi.rb for the same prism revision, unchanged; `dump_options` in
# particular has to agree byte for byte with prism's docs/serialization.md.
#
# The one place this is not a straight translation is Prism.parse_stream:
# upstream hands libprism an fgets callback so that it can stop reading at
# `__END__`.  There is no callback marshalling here, so the stream is read in
# full and parsed as a string.  The tree is the same; only the number of bytes
# left in a stream that continues past `__END__` differs.

load_assembly 'IronRuby.Libraries', 'IronRuby.StandardLibrary.Prism'

# Prism::Serialize has to be loaded eagerly when Ractors exist, so that it is
# not autoloaded from within a non-main one.
require "prism/serialize" if defined?(Ractor)

module Prism
  # The version constant is the version of the libprism IronRuby links, read
  # from the library itself rather than written down here.
  VERSION = LibRubyParser.version.freeze

  class << self
    # Mirror the Prism.dump API by using the serialization API.
    def dump(source, **options)
      dump_common(string_source(source), options)
    end

    # Mirror the Prism.dump_file API by using the serialization API.
    def dump_file(filepath, **options)
      dump_common(read_file(filepath, options), options)
    end

    # Mirror the Prism.lex API by using the serialization API.
    def lex(code, **options)
      lex_common(string_source(code), code, options)
    end

    # Mirror the Prism.lex_file API by using the serialization API.
    def lex_file(filepath, **options)
      source = read_file(filepath, options)
      lex_common(source, source, options)
    end

    # Mirror the Prism.parse API by using the serialization API.
    def parse(code, **options)
      parse_common(string_source(code), code, options)
    end

    # Mirror the Prism.parse_file API by using the serialization API.
    def parse_file(filepath, **options)
      source = read_file(filepath, options)
      parse_common(source, source, options)
    end

    # Mirror the Prism.parse_stream API by using the serialization API.  See the
    # note at the top of this file for how this differs from upstream.
    def parse_stream(stream, **options)
      source = stream.read
      parse_common(string_source(source), source, options)
    end

    # Mirror the Prism.parse_comments API by using the serialization API.
    def parse_comments(code, **options)
      parse_comments_common(string_source(code), code, options)
    end

    # Mirror the Prism.parse_file_comments API by using the serialization API.
    def parse_file_comments(filepath, **options)
      source = read_file(filepath, options)
      parse_comments_common(source, source, options)
    end

    # Mirror the Prism.parse_lex API by using the serialization API.
    def parse_lex(code, **options)
      parse_lex_common(string_source(code), code, options)
    end

    # Mirror the Prism.parse_lex_file API by using the serialization API.
    def parse_lex_file(filepath, **options)
      source = read_file(filepath, options)
      parse_lex_common(source, source, options)
    end

    # Mirror the Prism.parse_success? API by using the serialization API.
    def parse_success?(code, **options)
      parse_success_common(string_source(code), options)
    end

    # Mirror the Prism.parse_failure? API by using the serialization API.
    def parse_failure?(code, **options)
      !parse_success?(code, **options)
    end

    # Mirror the Prism.parse_file_success? API by using the serialization API.
    def parse_file_success?(filepath, **options)
      parse_success_common(read_file(filepath, options), options)
    end

    # Mirror the Prism.parse_file_failure? API by using the serialization API.
    def parse_file_failure?(filepath, **options)
      !parse_file_success?(filepath, **options)
    end

    # Mirror the Prism.profile API by using the serialization API.
    def profile(source, **options)
      LibRubyParser.serialize_parse(string_source(source), dump_options(options))
      nil
    end

    # Mirror the Prism.profile_file API by using the serialization API.
    def profile_file(filepath, **options)
      LibRubyParser.serialize_parse(read_file(filepath, options), dump_options(options))
      nil
    end

    private

    # prism works on bytes, and a String's bytes are what the file held.
    def string_source(source)
      raise TypeError, "wrong argument type #{source.class} (expected String)" unless source.is_a?(String)
      source
    end

    # Upstream maps the file through pm_source_mapped_init, which raises
    # Errno::EISDIR for a directory and a SystemCallError otherwise; File.binread
    # raises the same errors.  Like upstream's PrismSource.with_file, the path
    # goes through File.path - which accepts a Pathname (or anything else with
    # to_path or to_str) and rejects a path holding a NUL byte - and the String
    # it stands for is what goes into options[:filepath].
    def read_file(filepath, options)
      filepath = File.path(filepath)
      options[:filepath] = filepath
      File.binread(filepath)
    end

    def dump_common(source, options) # :nodoc:
      if (format_type = raise_error_format_type(options))
        raise_error(source, options, format_type)
      end

      dumped = LibRubyParser.serialize_parse(source, dump_options(options))
      dumped.freeze if options.fetch(:freeze, false)
      dumped
    end

    def lex_common(source, code, options) # :nodoc:
      format_type = raise_error_format_type(options)

      serialized = LibRubyParser.serialize_lex(source, dump_options(options))
      result = Serialize.load_lex(code, serialized, options.fetch(:freeze, false))

      raise_error(source, options, format_type) if format_type && result.failure?
      result
    end

    def parse_common(source, code, options) # :nodoc:
      format_type = raise_error_format_type(options)
      serialized = dump_common(source, options)
      result = Serialize.load_parse(code, serialized, options.fetch(:freeze, false))

      raise_error(source, options, format_type) if format_type && result.failure?
      result
    end

    def parse_comments_common(source, code, options) # :nodoc:
      if (format_type = raise_error_format_type(options))
        raise_error(source, options, format_type)
      end

      serialized = LibRubyParser.serialize_parse_comments(source, dump_options(options))
      Serialize.load_parse_comments(code, serialized, options.fetch(:freeze, false))
    end

    def parse_lex_common(source, code, options) # :nodoc:
      format_type = raise_error_format_type(options)

      serialized = LibRubyParser.serialize_parse_lex(source, dump_options(options))
      result = Serialize.load_parse_lex(code, serialized, options.fetch(:freeze, false))

      raise_error(source, options, format_type) if format_type && result.failure?
      result
    end

    def parse_success_common(source, options) # :nodoc:
      format_type = raise_error_format_type(options)
      success = LibRubyParser.parse_success?(source, dump_options(options))

      raise_error(source, options, format_type) if format_type && !success
      success
    end

    # Extract the raise_error option from the given options hash and convert
    # it into the format type that should be used when formatting errors, or
    # nil if raising is disabled.
    def raise_error_format_type(options) # :nodoc:
      case (value = options.delete(:raise_error))
      when nil, false
        nil
      when true
        # When given true, mirror the behavior of CRuby itself: color when
        # $stderr is a terminal (unless NO_COLOR is set), bold styling when
        # NO_COLOR is set, plain otherwise.
        if $stderr.respond_to?(:tty?) && $stderr.tty?
          no_color = ENV["NO_COLOR"]
          (no_color.nil? || no_color.empty?) ? ERRORS_FORMAT_COLOR : ERRORS_FORMAT_STYLE
        else
          ERRORS_FORMAT_PLAIN
        end
      when :plain
        ERRORS_FORMAT_PLAIN
      when :style
        ERRORS_FORMAT_STYLE
      when :color
        ERRORS_FORMAT_COLOR
      when Symbol
        raise ArgumentError, "invalid raise_error value: #{value}"
      else
        raise TypeError, "wrong argument type #{value.class} (expected Symbol)"
      end
    end

    # pm_errors_format_type_t (prism/include/prism/errors_format.h).
    ERRORS_FORMAT_PLAIN = 1
    ERRORS_FORMAT_STYLE = 2
    ERRORS_FORMAT_COLOR = 3

    # Parse the given source and format any errors that are encountered into
    # an appropriate exception, then raise it. If the source parses without
    # any errors, then return nil.
    def raise_error(source, options, format_type) # :nodoc:
      level, formatted = LibRubyParser.errors_format(source, dump_options(options), format_type)
      return if level == -1

      encoding_name, _, message = formatted.partition("\0")

      case level
      when 0 # syntax
        error = SyntaxError.new(message.force_encoding(encoding_name))
        error.instance_variable_set(:@path, options.fetch(:filepath, ""))
        raise error
      when 1 # argument
        raise ArgumentError, message.force_encoding(encoding_name)
      when 2 # load
        error = LoadError.new(message.force_encoding(Encoding.find("locale")))
        error.instance_variable_set(:@path, nil)
        raise error
      else
        raise "Unknown error level: #{level}"
      end
    end

    # Return the value that should be dumped for the command_line option.
    def dump_options_command_line(options)
      command_line = options.fetch(:command_line, "")
      raise ArgumentError, "command_line must be a string" unless command_line.is_a?(String)

      command_line.each_char.inject(0) do |value, char|
        case char
        when "a" then value | 0b000001
        when "e" then value | 0b000010
        when "l" then value | 0b000100
        when "n" then value | 0b001000
        when "p" then value | 0b010000
        when "x" then value | 0b100000
        else raise ArgumentError, "invalid command_line option: #{char}"
        end
      end
    end

    # Return the value that should be dumped for the version option.
    def dump_options_version(version)
      case version
      when "current"
        version_string_to_number(RUBY_VERSION) || raise(CurrentVersionError, RUBY_VERSION)
      when "latest"
        0 # Handled in pm_parser_init
      when "nearest", nil
        dump = version_string_to_number(RUBY_VERSION)
        return dump if dump

        if RUBY_VERSION < "3.3"
          version_string_to_number("3.3")
        else
          0 # Handled in pm_parser_init
        end
      else
        version_string_to_number(version) || raise(ArgumentError, "invalid version: #{version}")
      end
    end

    # Converts a version string like "4.0.0" or "4.0" into a number.
    # Returns nil if the version is unknown.
    def version_string_to_number(version)
      case version
      when /\A3\.3(\.\d+)?\z/
        1
      when /\A3\.4(\.\d+)?\z/
        2
      when /\A3\.5(\.\d+)?\z/, /\A4\.0(\.\d+)?\z/
        3
      when /\A4\.1(\.\d+)?\z/
        4
      end
    end

    # The set of options that are understood by the parsing APIs. Note that
    # raise_error is not listed here because it is deleted from the options
    # hash by raise_error_format_type before the options are dumped.
    DUMP_OPTIONS_KEYS = [:attach_comments, :command_line, :encoding, :filepath, :freeze, :frozen_string_literal, :line, :main_script, :partial_script, :scopes, :version].freeze
    private_constant :DUMP_OPTIONS_KEYS

    # Convert the given options into a serialized options string.
    def dump_options(options)
      unknown_keys = options.keys - DUMP_OPTIONS_KEYS
      raise ArgumentError, "unknown keyword: #{unknown_keys.first}" unless unknown_keys.empty?

      template = +""
      values = []

      template << "L"
      if (filepath = options[:filepath])
        values.push(filepath.bytesize, filepath.b)
        template << "A*"
      else
        values << 0
      end

      template << "l"
      values << options.fetch(:line, 1)

      template << "L"
      if (encoding = options[:encoding])
        name = encoding.is_a?(Encoding) ? encoding.name : encoding
        values.push(name.bytesize, name.b)
        template << "A*"
      else
        values << 0
      end

      template << "C"
      values << (options.fetch(:frozen_string_literal, false) ? 1 : 0)

      template << "C"
      values << dump_options_command_line(options)

      template << "C"
      values << dump_options_version(options[:version])

      template << "C"
      values << (options[:encoding] == false ? 1 : 0)

      template << "C"
      values << (options.fetch(:main_script, false) ? 1 : 0)

      template << "C"
      values << (options.fetch(:partial_script, false) ? 1 : 0)

      template << "C"
      values << (options.fetch(:freeze, false) ? 1 : 0)

      template << "C"
      values << (options.fetch(:attach_comments, false) ? 1 : 0)

      template << "L"
      if (scopes = options[:scopes])
        values << scopes.length

        scopes.each do |scope|
          locals = nil
          forwarding = 0

          case scope
          when Array
            locals = scope
          when Scope
            locals = scope.locals

            scope.forwarding.each do |forward|
              case forward
              when :*     then forwarding |= 0x1
              when :**    then forwarding |= 0x2
              when :&     then forwarding |= 0x4
              when :"..." then forwarding |= 0x8
              else raise ArgumentError, "invalid forwarding value: #{forward}"
              end
            end
          else
            raise TypeError, "wrong argument type #{scope.class.inspect} (expected Array or Prism::Scope)"
          end

          template << "L"
          values << locals.length

          template << "C"
          values << forwarding

          locals.each do |local|
            name = local.name
            template << "L"
            values << name.bytesize

            template << "A*"
            values << name.b
          end
        end
      else
        values << 0
      end

      values.pack(template)
    end
  end

  # Here we are going to patch StringQuery to put in the class-level methods so
  # that it can maintain a consistent interface.
  class StringQuery
    class << self
      # Mirrors the C extension's StringQuery::local? method.
      def local?(string)
        query("local", string)
      end

      # Mirrors the C extension's StringQuery::constant? method.
      def constant?(string)
        query("constant", string)
      end

      # Mirrors the C extension's StringQuery::method_name? method.
      def method_name?(string)
        query("method_name", string)
      end

      # Mirrors the C extension's StringQuery::keyword? method.
      def keyword?(string)
        query("keyword", string)
      end

      private

      def query(kind, string)
        case LibRubyParser.string_query(kind, string, string.encoding.name)
        when -1 then raise ArgumentError, "Invalid or non ascii-compatible encoding"
        when 0 then false
        else true
        end
      end
    end
  end
end
