# frozen_string_literal: false
#
# Ruby 4.0's monitor.rb: MonitorMixin and its ConditionVariable as MRI's
# lib/monitor.rb has them, doc comments aside. The Monitor class, which MRI
# implements in C (ext/monitor/monitor.c), is C# here too.
#
# Copyright (C) 2001  Shugo Maeda <shugo@ruby-lang.org>
#
# This library is distributed under the terms of the Ruby license.
# You can freely distribute/modify this library.
#

# Monitor itself - try_enter, enter, exit, synchronize, mon_locked?, mon_owned?,
# mon_check_owner and wait_for_cond - is C#, like MRI's ext/monitor/monitor.c:
# Src/Libraries/Monitor/RubyMonitor.cs.
load_assembly 'IronRuby.Libraries', 'IronRuby.StandardLibrary.Monitors'

module MonitorMixin
  class ConditionVariable
    def wait(timeout = nil)
      @monitor.mon_check_owner
      @monitor.wait_for_cond(@cond, timeout)
    end

    def wait_while
      while yield
        wait
      end
    end

    def wait_until
      until yield
        wait
      end
    end

    def signal
      @monitor.mon_check_owner
      @cond.signal
    end

    def broadcast
      @monitor.mon_check_owner
      @cond.broadcast
    end

    private

    def initialize(monitor) # :nodoc:
      @monitor = monitor
      @cond = Thread::ConditionVariable.new
    end
  end

  def self.extend_object(obj) # :nodoc:
    super(obj)
    obj.__send__(:mon_initialize)
  end

  def mon_try_enter
    @mon_data.try_enter
  end
  # For backward compatibility
  alias try_mon_enter mon_try_enter

  def mon_enter
    @mon_data.enter
  end

  def mon_exit
    mon_check_owner
    @mon_data.exit
  end

  def mon_locked?
    @mon_data.mon_locked?
  end

  def mon_owned?
    @mon_data.mon_owned?
  end

  def mon_synchronize(&b)
    @mon_data.synchronize(&b)
  end
  alias synchronize mon_synchronize

  def new_cond
    unless defined?(@mon_data)
      mon_initialize
      @mon_initialized_by_new_cond = true
    end
    return ConditionVariable.new(@mon_data)
  end

  private

  # Use <tt>extend MonitorMixin</tt> or <tt>include MonitorMixin</tt> instead
  # of this constructor.  Have look at the examples above to understand how to
  # use this module.
  def initialize(...)
    super
    mon_initialize
  end

  # Initializes the MonitorMixin after being included in a class or when an
  # object has been extended with the MonitorMixin
  def mon_initialize
    if defined?(@mon_data)
      if defined?(@mon_initialized_by_new_cond)
        return # already initialized.
      elsif @mon_data_owner_object_id == self.object_id
        raise ThreadError, "already initialized"
      end
    end
    @mon_data = ::Monitor.new
    @mon_data_owner_object_id = self.object_id
  end

  def mon_check_owner
    @mon_data.mon_check_owner
  end
end

class Monitor
  def new_cond
    ::MonitorMixin::ConditionVariable.new(self)
  end

  # for compatibility
  alias try_mon_enter try_enter
  alias mon_try_enter try_enter
  alias mon_enter enter
  alias mon_exit exit
  alias mon_synchronize synchronize
end
