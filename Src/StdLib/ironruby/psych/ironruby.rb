# frozen_string_literal: true
#
# What psych.so is on CRuby, for IronRuby: the parts of Psych that upstream are written in C
# over libyaml. Everything else under psych/ is psych 5.3.1's own Ruby code, unchanged, so
# Psych here behaves as CRuby's does by construction; only this file and psych.rb's
# `when 'ironruby'` line are IronRuby's.
#
# The YAML engine is IronRuby.Libraries.Yaml, a port of libyaml 0.2.5's scanner, parser and
# emitter: Psych.__native_parse runs the parser and sends each event, with its marks, to a
# handler, and Psych.__emitter_open / __emitter_* drive the emitter one event at a time.
# psych_parser.c, psych_emitter.c, psych_to_ruby.c and psych.c are the files this stands in for.

load_assembly 'IronRuby.Libraries.Yaml', 'IronRuby.StandardLibrary.Yaml'

require_relative 'handler'
require_relative 'syntax_error' # psych_parser.c's Init does this
require_relative 'visitors/visitor'

module Psych
  class Parser
    # libyaml's yaml_encoding_t.
    ANY = 0
    UTF8 = 1
    UTF16LE = 2
    UTF16BE = 3

    # psych_parser.c's parse: every event goes to +handler+, preceded by its location, and a
    # document that is not YAML raises Psych::SyntaxError once the events before the error
    # have been sent.
    def _native_parse(handler, yaml, path)
      io_encoding = ANY
      if yaml.respond_to?(:read)
        # psych_parser.c's transcode_io: an IO says it is UTF-8 (or US-ASCII) or UTF-16, and
        # libyaml guesses for anything else.
        enc = yaml.respond_to?(:external_encoding) ? yaml.external_encoding : nil
        io_encoding = case enc
                      when Encoding::UTF_8, Encoding::US_ASCII then UTF8
                      when Encoding::UTF_16LE then UTF16LE
                      when Encoding::UTF_16BE then UTF16BE
                      else ANY
                      end
      else
        yaml = String.try_convert(yaml) || raise(TypeError, "no implicit conversion of #{yaml.class} into String")
      end
      error = Psych.__native_parse(self, handler, yaml, io_encoding)
      if error
        line, column, offset, problem, context = error
        raise Psych::SyntaxError.new(path, line, column, offset, problem, context)
      end
      self
    end
    private :_native_parse

    # How far the parser has read: an index, a zero-based line and a column.
    def mark
      Mark.new(*Psych.__native_mark(@__scanner))
    end
  end

  # psych_emitter.c: a handler that writes the YAML for the events it is sent to +io+. The
  # engine's emitter looks ahead a few events before it writes one, as libyaml's does, and
  # the text reaches +io+ at the end of each document and of the stream, where libyaml
  # flushes.
  class Emitter < Psych::Handler
    attr_accessor :line_width, :indentation, :canonical

    def initialize(io, options = nil)
      @io = io
      @state = nil
      @pending = nil
      if options
        @line_width = options.line_width
        @indentation = options.indentation
        @canonical = options.canonical
      else
        @line_width = 0
        @indentation = 2
        @canonical = false
      end
    end

    def start_stream(encoding)
      raise TypeError, "no implicit conversion of #{encoding.class} into Integer" unless Integer === encoding
      @state = Psych.__emitter_open(@indentation, @line_width, @canonical)
      @pending = +''
      __emit [0, encoding]
    end

    def end_stream
      __emit [1]
      __flush
      self
    end

    def start_document(version, tag_directives, implicit)
      raise TypeError, "wrong argument type #{version.class} (expected Array)" unless Array === version
      version = version.empty? ? version : [__int(version[0]), __int(version[1])]
      if tag_directives
        raise TypeError, "wrong argument type #{tag_directives.class} (expected Array)" unless Array === tag_directives
        tag_directives = tag_directives.map do |tuple|
          raise TypeError, "wrong argument type #{tuple.class} (expected Array)" unless Array === tuple
          raise RuntimeError, 'tag tuple must be of length 2' if tuple.length < 2
          [__string(tuple[0], false), __string(tuple[1], false)]
        end
      end
      __emit [2, version, tag_directives, implicit]
    end

    def end_document(implicit_end = !streaming?)
      __emit [3, implicit_end]
      __flush
      self
    end

    def alias(anchor)
      __emit [4, __string(anchor)]
    end

    # The events most of a document is, straight to the engine (it checks the arguments as
    # psych_emitter.c does).
    def scalar(value, anchor, tag, plain, quoted, style)
      raise RuntimeError, 'expected STREAM-START' unless @state
      out = Psych.__emitter_scalar(@state, value, anchor, tag, plain, quoted, style)
      @pending << out if out
      self
    end

    def start_sequence(anchor, tag, implicit, style)
      raise RuntimeError, 'expected STREAM-START' unless @state
      out = Psych.__emitter_collection(@state, false, anchor, tag, implicit, style)
      @pending << out if out
      self
    end

    def end_sequence
      raise RuntimeError, 'expected STREAM-START' unless @state
      out = Psych.__emitter_end_collection(@state, false)
      @pending << out if out
      self
    end

    def start_mapping(anchor, tag, implicit, style)
      raise RuntimeError, 'expected STREAM-START' unless @state
      out = Psych.__emitter_collection(@state, true, anchor, tag, implicit, style)
      @pending << out if out
      self
    end

    def end_mapping
      raise RuntimeError, 'expected STREAM-START' unless @state
      out = Psych.__emitter_end_collection(@state, true)
      @pending << out if out
      self
    end

    private

    # The emitter writes UTF-8, as libyaml does; a value in another encoding is converted.
    def __string(value, nil_ok = true)
      return nil if value.nil? && nil_ok
      str = String.try_convert(value) or raise TypeError, "no implicit conversion of #{value.class} into String"
      str.encoding == Encoding::UTF_8 ? str : str.encode(Encoding::UTF_8)
    end

    # NUM2INT: an Integer, or a Float truncated to one.
    def __int(value)
      case value
      when Integer then value
      when Float then value.to_i
      else raise TypeError, "no implicit conversion of #{value.nil? ? 'nil' : value.class} into Integer"
      end
    end

    def __emit(event)
      raise RuntimeError, 'expected STREAM-START' unless @state
      @pending << Psych.__emitter_emit(@state, event)
      self
    end

    def __flush
      return if @pending.nil? || @pending.empty?
      text, @pending = @pending, +''
      @io.write text
    end
  end

  module Visitors
    class ToRuby < Psych::Visitors::Visitor
      private

      # psych_to_ruby.c: an exception of +klass+ carrying +message+, made without calling
      # the class's own initialize (which may want other arguments).
      def build_exception(klass, message)
        e = klass.allocate
        ::Exception.instance_method(:initialize).bind_call(e, message)
        e
      end
    end
  end

  module Visitors
    class Visitor
      # Visitor#visit maps a node's class to its visit_ method through one Hash shared by every
      # visitor in the process, filled in by a default block on first sight of each class.  On
      # CRuby the GVL makes that insert atomic.  IronRuby's Hash is not synchronized, so two
      # threads loading YAML at once - Bundler's parallel installer reads every downloaded .gem's
      # checksums.yaml.gz this way - could insert together, corrupt the table, and surface as a
      # System::IndexOutOfRangeException out of Hash#[]=.
      #
      # This cache is copy-on-write instead: lookups read a Hash that is never mutated once
      # published, and a miss builds the next one under a lock and swaps it in.  A visitor sees
      # a handful of node classes, so misses stop after the first document.
      class DispatchCache
        def initialize
          @map = {}.compare_by_identity.freeze
          @lock = ::Thread::Mutex.new
        end

        def [](klass)
          @map[klass] || @lock.synchronize do
            @map[klass] || begin
              map = @map.dup
              map[klass] = :"visit_#{klass.name.gsub('::', '_')}"
              @map = map.freeze
              map[klass]
            end
          end
        end
      end

      remove_const(:DISPATCH) if const_defined?(:DISPATCH, false)
      DISPATCH = DispatchCache.new

      def self.dispatch_cache
        DispatchCache.new
      end
    end
  end

  class ClassLoader
    private

    # rb_path_to_class: "Foo::Bar" to the class or module, which must exist.
    def path2class(path)
      path.to_s.split('::').inject(::Object) do |mod, name|
        raise ArgumentError, "undefined class/module #{path}" if name.empty? || !mod.const_defined?(name, false)
        c = mod.const_get(name, false)
        raise TypeError, "#{path} does not refer to class/module" unless c.is_a?(::Module)
        c
      end
    end
  end
end
