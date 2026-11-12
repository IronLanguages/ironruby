# frozen_string_literal: true
#
# Fiddle, provided by IronRuby itself.
#
# `gem install fiddle` cannot work here: fiddle is a C extension over libffi.
# What IronRuby ships instead is the gem's own Ruby half - fiddle/closure,
# fiddle/function, fiddle/struct, fiddle/value, fiddle/pack, fiddle/cparser,
# fiddle/import, fiddle/types, vendored unchanged from fiddle 1.1.8 into
# Src/StdLib/ironruby/fiddle - on top of a C# implementation of its C half
# (Src/Libraries/Fiddle).  That half needs no libffi: dlopen/dlsym are
# System.Runtime.InteropServices.NativeLibrary, and a call with a signature
# chosen at run time is a DynamicMethod whose body is one OpCodes.Calli.
#
# Two things CRuby's fiddle can do are out of reach and say so when used:
# Fiddle::Pointer#to_value (a Ruby object has no address on .NET) and
# Fiddle::Handle::NEXT (RTLD_NEXT has no NativeLibrary spelling).

load_assembly 'IronRuby.Libraries', 'IronRuby.StandardLibrary.Fiddle'

require 'fiddle/closure'
require 'fiddle/function'
require 'fiddle/version'

module Fiddle
  # The dlopen(3) mode flags live on Handle as well, where CRuby defines them.
  class Handle
    RTLD_LAZY   = Fiddle::RTLD_LAZY
    RTLD_NOW    = Fiddle::RTLD_NOW
    RTLD_GLOBAL = Fiddle::RTLD_GLOBAL

    # dlopen(NULL): the running program, with everything already loaded in its
    # scope - which is where libc's symbols are found.
    DEFAULT = new

    # RTLD_NEXT has no equivalent in NativeLibrary.  The object exists so that
    # code mentioning the constant loads; looking a symbol up through it raises.
    NEXT = __make_next__

    class << self
      def sym(name)
        DEFAULT.sym(name)
      end
      alias [] sym

      def sym_defined?(name)
        DEFAULT.sym_defined?(name)
      end
    end
  end

  NULL = Pointer.new(0)

  BUILD_RUBY_PLATFORM = RUBY_PLATFORM

  # Creates a new handler that opens +library+, and returns an instance of
  # Fiddle::Handle.  If +nil+ is given for the +library+, the handle for the
  # running program is used, which is the equivalent of RTLD_DEFAULT.
  #
  #   libc = Fiddle.dlopen(nil)
  #
  # On Linux a path that turns out to be a linker script rather than an ELF
  # object is followed to the file it names, as CRuby's fiddle does.
  def dlopen(library)
    Fiddle::Handle.new(library)
  rescue DLError => error
    raise unless RUBY_PLATFORM =~ /linux/
    raise unless error.message =~ /\A(\/.+?): (?:invalid ELF header|file too short)/

    path = $1
    File.open(path) do |input|
      input.each_line do |line|
        next unless line =~ /\A\s*(?:INPUT|GROUP)\s*\(\s*([^\s,\)]+)/
        first_input = $1
        first_input = "lib#{first_input[2..-1]}.so" if first_input.start_with?("-l")
        return dlopen(first_input)
      end
    end
    raise
  end
  module_function :dlopen

  # fiddle.so defines Fiddle::Types as the TYPE_* codes without their prefix;
  # fiddle/types.rb then reopens it to add the Win32 and basic type aliases.
  module Types
    constants_from = Fiddle.constants.grep(/\ATYPE_/)
    constants_from.each do |name|
      const_set(name.to_s.sub("TYPE_", ""), Fiddle.const_get(name))
    end
  end
end

require 'fiddle/types'
