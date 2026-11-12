# ****************************************************************************
#
# Copyright (c) Microsoft Corporation. 
#
# This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
# copy of the license can be found in the License.html file at the root of this distribution. If 
# you cannot locate the  Apache License, Version 2.0, please send an email to 
# ironruby@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
# by the terms of the Apache License, Version 2.0.
#
# You must not remove this notice, or any other, from this software.
#
#
# ****************************************************************************

load_assembly 'IronRuby.Libraries', 'IronRuby.StandardLibrary.Syslog'

# Syslog: the constants, the state and the formatting. The syscalls behind it are in
# Src/Libraries/Syslog/Syslog.cs.
module Syslog
  VERSION = "0.3.0"

  # LOG_MASK and LOG_UPTO are macros in <syslog.h>, so they are methods here as they are in
  # MRI, and a module that includes them hands them on to whatever includes it.
  module Macros
    def LOG_MASK(priority)
      1 << priority
    end

    def LOG_UPTO(priority)
      (1 << (priority + 1)) - 1
    end

    def self.included(mod)
      mod.extend(self)
    end
  end

  module Level
    LOG_EMERG   = 0
    LOG_ALERT   = 1
    LOG_CRIT    = 2
    LOG_ERR     = 3
    LOG_WARNING = 4
    LOG_NOTICE  = 5
    LOG_INFO    = 6
    LOG_DEBUG   = 7
  end

  # The facility is the top of the priority word, which is why each is a multiple of eight.
  module Facility
    LOG_KERN     = 0 << 3
    LOG_USER     = 1 << 3
    LOG_MAIL     = 2 << 3
    LOG_DAEMON   = 3 << 3
    LOG_AUTH     = 4 << 3
    LOG_SYSLOG   = 5 << 3
    LOG_LPR      = 6 << 3
    LOG_NEWS     = 7 << 3
    LOG_UUCP     = 8 << 3
    LOG_CRON     = 9 << 3
    LOG_AUTHPRIV = 10 << 3
    LOG_FTP      = 11 << 3
    LOG_LOCAL0   = 16 << 3
    LOG_LOCAL1   = 17 << 3
    LOG_LOCAL2   = 18 << 3
    LOG_LOCAL3   = 19 << 3
    LOG_LOCAL4   = 20 << 3
    LOG_LOCAL5   = 21 << 3
    LOG_LOCAL6   = 22 << 3
    LOG_LOCAL7   = 23 << 3
  end

  module Option
    LOG_PID    = 0x01
    LOG_CONS   = 0x02
    LOG_ODELAY = 0x04
    LOG_NDELAY = 0x08
    LOG_NOWAIT = 0x10
    LOG_PERROR = 0x20
  end

  # These are all macros in <syslog.h>, so there is nothing to read them out of at run time:
  # MRI hardcodes them through the C preprocessor when it is built and this hardcodes the
  # same values, which are glibc's and are what Linux uses.
  module Constants
    include Macros
    include Level
    include Facility
    include Option

    def self.included(mod)
      mod.extend(self)
    end
  end

  include Constants

  class << self
    # nil rather than a stale answer: MRI reports these only while the log is open.
    attr_reader :ident, :options, :facility

    # The mask is remembered across opens, but is still only reported while one is open.
    def mask
      opened? ? @mask : nil
    end

    def instance
      self
    end

    def opened?
      !!@opened
    end

    # open(ident = $0, options = LOG_PID | LOG_CONS, facility = LOG_USER)
    #
    # With a block the log is opened, the module handed to the block, and the log closed
    # again afterwards however the block ends. The module is the answer either way - not the
    # block's value, which is what MRI returns.
    def open(ident = nil, options = nil, facility = nil)
      ::Kernel.raise(::RuntimeError, "syslog already open") if opened?

      ident = $0 if ident.nil?
      ident = ident.to_s
      options = (LOG_PID | LOG_CONS) if options.nil?
      facility = LOG_USER if facility.nil?

      __openlog__(ident, options, facility)
      @opened = true
      @ident = ident
      @options = options
      @facility = facility
      # The mask outlives the log it was set on: opening does not reset it, which is why a
      # reopened log still reports whatever the last #mask= asked for.
      __setlogmask__(@mask)

      if block_given?
        begin
          yield self
        ensure
          # Unconditionally, so that a block which closed the log itself gets the
          # "syslog not opened" that MRI raises rather than a silent second close.
          close
        end
      end
      self
    end

    # open! is a reopen and nothing else: it wants a log that is already open, and refuses
    # with the same error #close gives when there is none.
    def open!(ident = nil, options = nil, facility = nil, &block)
      ::Kernel.raise(::RuntimeError, "syslog not opened") unless opened?
      close
      open(ident, options, facility, &block)
    end

    alias_method :reopen, :open!

    def close
      ::Kernel.raise(::RuntimeError, "syslog not opened") unless opened?
      __closelog__
      @opened = false
      @ident = nil
      @options = nil
      @facility = nil
      nil
    end

    def mask=(value)
      ::Kernel.raise(::RuntimeError, "syslog not opened") unless opened?
      # Anything with a to_int, so a Float is accepted and truncated the way NUM2INT does;
      # a String has none and is a TypeError rather than a missing method.
      unless value.respond_to?(:to_int)
        ::Kernel.raise(::TypeError,
                       "no implicit conversion of #{value.nil? ? "nil" : value.class} into Integer")
      end
      value = value.to_int
      __setlogmask__(value)
      @mask = value
    end

    # log(priority, format, *args) - the format is a Kernel#format string, and with no
    # arguments to substitute it is still one, so a stray percent sign is an error there
    # exactly as it is in MRI.
    def log(priority, format, *args)
      ::Kernel.raise(::RuntimeError, "must open syslog before write") unless opened?
      if format.nil?
        ::Kernel.raise(::TypeError, "no implicit conversion of nil into String")
      end
      # Always a format string, even with nothing to substitute into it: MRI reports a
      # stray percent sign in a one-argument #log as "too few arguments" rather than
      # logging it, and code that means a literal percent has to pass "%s".
      message = ::Kernel.format(format, *args)
      __syslog__(priority, message)
      self
    end

    { emerg: :LOG_EMERG, alert: :LOG_ALERT, crit: :LOG_CRIT, err: :LOG_ERR,
      warning: :LOG_WARNING, notice: :LOG_NOTICE, info: :LOG_INFO, debug: :LOG_DEBUG
    }.each do |name, level|
      define_method(name) do |format, *args|
        log(Level.const_get(level), format, *args)
      end
    end

    def inspect
      if opened?
        "<#Syslog: opened=true, ident=#{@ident.inspect}, options=#{@options}, " \
          "facility=#{@facility}, mask=#{@mask}>"
      else
        "<#Syslog: opened=false>"
      end
    end
  end

  @opened = false
  # setlogmask(3) starts wide open, and the value survives a log being closed and reopened.
  @mask = 255
end
