# frozen_string_literal: true
#
# FFI::LastError - errno after the last attached function call.
#
# CRuby's ffi reads errno out of the thread that made the call; TruffleRuby
# calls __errno_location() afterwards.  Neither works on .NET, which restores
# the thread's system error across a managed-to-native transition, so errno is
# already back to its old value one instruction after the call returns.
#
# What does survive is a call made through a marshalling stub whose delegate
# type says SetLastError: the stub reads errno the instant the callee returns.
# IronRuby's Fiddle exposes that as Function.new(..., save_last_error: true)
# and parks the result in Fiddle.last_error, which is per-thread exactly as
# errno is.  FFI::Function passes that option for every function it attaches,
# so FFI.errno answers a real errno.
#
# The consequence to know about: a call made through plain Fiddle, or through
# any other path that does not ask for the option, leaves Fiddle.last_error
# alone rather than zeroing it.

module FFI
  module LastError
    def error
      Fiddle.last_error || 0
    end

    def error=(error)
      Fiddle.last_error = error
    end

    module_function :error, :error=

    # ffi defines these on Windows only, and its specs check that asking for
    # one anywhere else is a NoMethodError.
    if Fiddle::WINDOWS
      def winapi_error
        Fiddle.win32_last_error || 0
      end

      def winapi_error=(error)
        Fiddle.win32_last_error = error
      end

      module_function :winapi_error, :winapi_error=
    end
  end
end
