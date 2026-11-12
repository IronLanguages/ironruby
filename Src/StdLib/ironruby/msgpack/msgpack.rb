# frozen_string_literal: true
#
# The msgpack gem's C extension, provided by IronRuby itself.
#
# msgpack 1.8.5 is a C extension (ext/msgpack) with Ruby on top.  The Ruby half is
# vendored unchanged next to this file, and its msgpack.rb requires "msgpack/msgpack" -
# the name the compiled extension has on CRuby - which finds this file instead.  What
# the extension defines is implemented in C# (Src/Libraries/MessagePack), a port of
# ext/msgpack: MessagePack::Buffer, Packer, Unpacker, Factory and the error classes.
# The few things upstream builds with the C API rather than as classes of their own are
# defined here, in the same shape.
#
# A pinned default gemspec in Src/StdLib/ruby/gems/4.0.0/specifications/default makes
# RubyGems agree that msgpack 1.8.5 is installed, so `gem "msgpack"` in a Gemfile
# resolves here instead of stopping at extconf.rb.

load_assembly 'IronRuby.Libraries', 'IronRuby.StandardLibrary.MessagePack'

module MessagePack
  # rb_struct_define(NULL, "type", "payload"), then assigned to the constant.
  ExtensionValue = Struct.new(:type, :payload)

  # A holder for strings a recursive extension packer must keep alive; never
  # instantiated from Ruby.
  class HeldBuffer < BasicObject
  end
  class << HeldBuffer
    # rb_undef_alloc_func
    def allocate
      ::Kernel.raise ::TypeError, "allocator undefined for MessagePack::HeldBuffer"
    end

    def new(*)
      allocate
    end
  end

  # Obsolete, kept by upstream for backward compatibility (msgpack-ruby #86):
  # UnexpectedTypeError includes it so that `rescue MessagePack::TypeError` works.
  module TypeError
  end

  class UnexpectedTypeError
    include TypeError
  end
end
