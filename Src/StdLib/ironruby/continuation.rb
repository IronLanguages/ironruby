# MRI's ext/continuation, as far as the CLR allows. A continuation captures the
# whole machine stack, which a .NET thread cannot copy or restore, so only the
# escaping (one-shot, upward) half is provided: calling a continuation while the
# callcc that made it is still running returns from that callcc at once with the
# given value, like a throw to its catch. Calling it after that callcc has
# returned would have to rebuild a stack that no longer exists, and raises
# NotImplementedError. JRuby's callcc is the same catch/throw, but raises
# ThreadError on a late call; TruffleRuby's continuations can only be created,
# and calling one raises RuntimeError. NotImplementedError, a ScriptError, keeps a
# bare rescue from mistaking the missing feature for an ordinary failure.
#
# One known difference from MRI: a jump to a continuation runs the ensure clauses
# it passes, as throw does, while MRI restores the saved stack without running them.

warn "#{__FILE__}: warning: callcc is obsolete; use Fiber instead"

class Continuation
  # The state of each continuation is [thread, fiber, live?] in a hidden instance variable,
  # out of #instance_variables. Not an ObjectSpace::WeakMap entry: that holds its values
  # weakly too, and the array had no other reference to keep it.

  class << self
    alias_method :__ir_allocate__, :allocate
    private :__ir_allocate__
    undef_method :new

    def allocate
      raise TypeError, "allocator undefined for #{self}"
    end

    # Kernel#callcc: runs the block with a new continuation, which is live until
    # the block finishes.
    private def __ir_callcc__
      cont = __ir_allocate__
      cont.__send__(:__ir_set_hidden_ivar__, :@__state__, [Thread.current, Fiber.current, true])
      begin
        catch(cont) { yield cont }
      ensure
        cont.__send__(:__ir_set_hidden_ivar__, :@__state__, [Thread.current, Fiber.current, false])
      end
    end
  end

  # call(*args) -> never returns
  # Makes the callcc that created this continuation return args (nil for none,
  # the value itself for one, an array for more).
  def call(*args)
    thread, fiber, live = __ir_hidden_ivar__(:@__state__)
    raise RuntimeError, 'continuation called across threads' unless thread.equal?(Thread.current)
    raise RuntimeError, 'continuation called across fiber' unless fiber.equal?(Fiber.current)
    unless live
      raise NotImplementedError,
        're-entering a continuation after its callcc has returned is not supported on IronRuby'
    end
    throw self, (args.size <= 1 ? args.first : args)
  end
  alias [] call
end

module Kernel
  # callcc { |cont| block } -> obj
  def callcc
    raise LocalJumpError, 'no block given' unless block_given?
    Continuation.__send__(:__ir_callcc__) { |cont| yield cont }
  end
  module_function :callcc
end
