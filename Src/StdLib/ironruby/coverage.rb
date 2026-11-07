load_assembly 'IronRuby.Libraries', 'IronRuby.StandardLibrary.Coverage'

# MRI's ext/coverage. Line coverage (lines, oneshot_lines, eval) counts at the
# TracePoint :line hooks of code compiled while the measurement is set up; method
# coverage records each `def' run while it is set up and counts the calls in the
# method's prologue. Branch coverage is not implemented - supported?(:branches) says
# so - but a result asked for it reports an empty set of branches per file rather
# than leaving the key out, which is what MRI's shape wants.
module Coverage
  # CoverageModes in the runtime
  LINES = 0x1
  BRANCHES = 0x2
  METHODS = 0x4
  ONESHOT_LINES = 0x8
  EVAL = 0x10
  __hide_constant__ :LINES, :BRANCHES, :METHODS, :ONESHOT_LINES, :EVAL

  @__state__ = :idle
  @__mode__ = nil

  class << self
    def setup(*args)
      raise ArgumentError, "wrong number of arguments (given #{args.size}, expected 0..1)" if args.size > 1
      raise RuntimeError, "coverage measurement is already setup" unless @__state__ == :idle

      if args.empty?
        mode = 0 # compatible mode: lines, reported as bare arrays
      elsif args[0] == :all
        mode = LINES | BRANCHES | METHODS | EVAL
      else
        opt = Hash.try_convert(args[0]) or
          raise TypeError, "no implicit conversion of #{__type_name__(args[0])} into Hash"
        mode = 0
        mode |= LINES if opt[:lines]
        mode |= BRANCHES if opt[:branches]
        mode |= METHODS if opt[:methods]
        if opt[:oneshot_lines]
          raise RuntimeError, "cannot enable lines and oneshot_lines simultaneously" if mode & LINES != 0
          mode |= LINES | ONESHOT_LINES
        end
        mode |= EVAL if opt[:eval]
      end

      @__mode__ = mode
      __setup__(mode == 0 ? LINES : mode)
      @__state__ = :suspended
      nil
    end

    def resume
      raise RuntimeError, "coverage measurement is not set up yet" if @__state__ == :idle
      raise RuntimeError, "coverage measurement is already running" if @__state__ == :running
      __resume__(true)
      @__state__ = :running
      nil
    end

    def suspend
      raise RuntimeError, "coverage measurement is not running" unless @__state__ == :running
      __resume__(false)
      @__state__ = :suspended
      nil
    end

    def start(*args)
      setup(*args)
      resume
      nil
    end

    def peek_result
      raise RuntimeError, "coverage measurement is not enabled" if @__state__ == :idle

      result = {}
      __peek__.each do |path, lines, oneshot_lines, methods|
        if @__mode__ == 0
          result[path] = lines.freeze
        else
          file = {}
          if @__mode__ & ONESHOT_LINES != 0
            file[:oneshot_lines] = oneshot_lines.freeze
          elsif @__mode__ & LINES != 0
            file[:lines] = lines.freeze
          end
          # not measured; the key is still reported, as MRI's shape has it
          file[:branches] = {}.freeze if @__mode__ & BRANCHES != 0
          if @__mode__ & METHODS != 0
            file[:methods] = methods.each_with_object({}) { |m, h| h[m[0, 6].freeze] = m[6] }.freeze
          end
          result[path] = file
        end
      end
      result.freeze
    end

    def result(*args)
      raise ArgumentError, "wrong number of arguments (given #{args.size}, expected 0..1)" if args.size > 1
      raise RuntimeError, "coverage measurement is not enabled" if @__state__ == :idle

      stop = clear = true
      unless args.empty?
        opt = Hash.try_convert(args[0]) or
          raise TypeError, "no implicit conversion of #{__type_name__(args[0])} into Hash"
        stop = !!opt[:stop]
        clear = !!opt[:clear]
      end

      result = peek_result
      if stop && !clear
        warn "stop implies clear", uplevel: 1
        clear = true
      end
      __clear__ if clear
      if stop
        suspend if @__state__ == :running
        __reset__
        @__state__ = :idle
        @__mode__ = nil
      end
      result
    end

    def state
      @__state__
    end

    def running?
      @__state__ == :running
    end

    def supported?(mode)
      raise TypeError, "wrong argument type #{__type_name__(mode)} (expected Symbol)" unless mode.is_a?(Symbol)
      mode == :lines || mode == :oneshot_lines || mode == :eval || mode == :methods
    end

    private

    def __type_name__(obj)
      case obj
      when nil, true, false then obj.inspect
      else obj.class.to_s
      end
    end
  end
end
