# frozen_string_literal: true
#
# The yajl-ruby gem's C extension, provided by IronRuby itself.
#
# yajl-ruby 1.4.3 is Ruby over one C extension, yajl/yajl, built on the yajl
# 1.x C library it carries.  Here both are C# (Src/Libraries/Yajl): a port of
# yajl's lexer, parser and generator and of the binding in ext/yajl/yajl_ext.c
# - Yajl::Parser, Yajl::Encoder, Yajl::Projector#project, Yajl::ParseError,
# Yajl::EncodeError and Yajl::MAX_DEPTH.
#
# Only the extension is provided.  The gem is installed as usual, from
# rubygems.org or from git (its extension is not compiled on IronRuby, see
# rubygems/defaults/ironruby.rb), and its lib/yajl.rb requires "yajl/yajl",
# which finds this file: a .rb anywhere on $LOAD_PATH wins over the gem's
# yajl.so.

load_assembly 'IronRuby.Libraries', 'IronRuby.StandardLibrary.Yajl'

module Yajl
  # An ordinary class whose #initialize lib/yajl.rb writes in Ruby; only
  # #project is native.  A class the library defines does not run #initialize
  # from .new by itself, so say what Class#new does.
  class Projector
    def self.new(*args, &block)
      obj = allocate
      obj.__send__(:initialize, *args, &block)
      obj
    end
  end

  class Encoder
    # Upstream defines to_json on these eight classes as one C function each,
    # which encodes self with the encoder it is given - or with a new default
    # one, unless it is given an instance of exactly Yajl::Encoder.
    def self.enable_json_gem_compatability
      [::Hash, ::Array, ::Integer, ::Float, ::String, ::TrueClass, ::FalseClass, ::NilClass].each do |klass|
        klass.class_eval do
          def to_json(*args)
            if args.size > 1
              raise ArgumentError, "wrong number of arguments (given #{args.size}, expected 0..1)".b
            end
            encoder = args[0]
            encoder = ::Yajl::Encoder.new if encoder.nil? || encoder.class != ::Yajl::Encoder
            encoder.encode(self)
          end
        end
      end
      nil
    end
  end
end
