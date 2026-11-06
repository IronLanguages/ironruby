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

load_assembly 'IronRuby.Libraries', 'IronRuby.StandardLibrary.BigDecimal'

# As in MRI, Kernel#BigDecimal is the only way to make one: BigDecimal.new is undefined and there
# is no allocator. The C# constructor stays reachable to Kernel#BigDecimal as __new__.
class << BigDecimal
  alias_method :__new__, :new
  private :__new__
  undef_method :new

  def allocate
    raise TypeError, "allocator undefined for #{self}"
  end
end

module Kernel
  # Kernel#BigDecimal takes a good deal more than a String. Integer and BigDecimal convert
  # exactly; Float and Rational have to be rounded to a number of significant digits, and a
  # Rational insists on being told how many (its expansion may never end). `exception: false`
  # turns every conversion failure into nil instead.
  #
  # The precision means different things per type, which is easy to get wrong by reasoning:
  # for a String it is only a lower bound on the storage and the digits are all kept, for a
  # BigDecimal it is ignored outright, and only for Float and Rational does it round.
  def BigDecimal(value, precision = 0, exception: true)
    precision = 0 if precision.nil?
    precision = Integer(precision) unless precision.kind_of?(Integer)
    raise ArgumentError, "negative precision" if precision < 0

    case value
    when BigDecimal
      value
    when Integer
      BigDecimal.__send__(:__new__, value.to_s)
    when Float
      # MRI converts through the shortest round-tripping decimal form of the Float - not
      # through its exact binary value - which is why BigDecimal(1.235, 3) is 1.24 and not
      # the 1.23 that correctly rounding 1.2349999999999999866... would give.
      raise ArgumentError, "precision too large." if precision > Float::DIG + 1
      __bigdecimal_round(BigDecimal.__send__(:__new__, value.to_s), precision)
    when Rational
      raise ArgumentError, "can't omit precision for a Rational." if precision.zero?
      BigDecimal.__send__(:__new__, value.numerator.to_s).div(value.denominator, precision)
    when String
      BigDecimal.__send__(:__new__, value)
    when nil
      raise TypeError, "can't convert nil into BigDecimal"
    else
      if value.respond_to?(:to_str)
        BigDecimal.__send__(:__new__, value.to_str)
      else
        raise TypeError, "can't convert #{value.class} into BigDecimal"
      end
    end
  rescue ArgumentError, TypeError
    raise if exception
    nil
  end
  module_function :BigDecimal

  # Rounds to +precision+ significant digits. Zero, NaN and the infinities have no digits to
  # round, and #mult would lose the sign of a negative zero.
  def __bigdecimal_round(value, precision)
    return value if precision.zero? || !value.finite? || value.zero?
    value.mult(1, precision)
  end
  private :__bigdecimal_round
end

class BigDecimal
  # The generation of the bigdecimal API this implements, i.e. the one that ships with the
  # Ruby we target. It is not a version of IronRuby's own code: callers (ruby/spec among
  # them) read it to decide which semantics to expect, so it has to name the semantics we
  # actually follow - 4.0's Integer-returning #div and #divmod, ZeroDivisionError from #%
  # and #remainder, and the sign-dependent answers for an infinite divisor.
  VERSION = "4.0.1"

  NAN = BigDecimal("NaN")
  INFINITY = BigDecimal("Infinity")

  # A BigDecimal is immutable, so there is nothing for #dup to copy. MRI defines the two as
  # one method, which is what the spec checks with instance_method(:clone) == instance_method(:dup).
  def dup
    self
  end
  alias_method :clone, :dup

  # Every BigDecimal is a terminating decimal, so the exact Rational is just the digits over
  # the right power of ten. NaN and the infinities have no rational value at all and raise,
  # rather than answering something like (0/0).
  def to_r
    raise FloatDomainError, "Computation results in 'NaN' (Not a Number)" if nan?
    if (i = infinite?)
      raise FloatDomainError, "Computation results in '#{i > 0 ? '' : '-'}Infinity'"
    end

    sign, digits, _base, exponent = split
    numerator = sign * digits.to_i
    scale = exponent - digits.size
    scale >= 0 ? Rational(numerator * 10**scale, 1) : Rational(numerator, 10**(-scale))
  end

  # A Rational operand does not go through Rational#coerce: that would answer a pair of
  # Floats and quietly throw the extra digits away (and MRI's Rational#coerce refuses a
  # BigDecimal outright). MRI expands the Rational into a BigDecimal itself, so
  # BigDecimal op Rational stays a BigDecimal.
  #
  # A Rational may not have a terminating expansion, so a digit count has to be chosen. MRI's
  # GetCoercePrec takes as many digits as self's internal words hold - 9 per word, the words
  # aligned at the decimal point - and at least 2 * BIGDECIMAL_DOUBLE_FIGURES (32).
  def __bigdecimal_expand(other)
    words = 1
    if finite? && !zero?
      _, digits, _, exponent = split
      words = (exponent - 1).div(9) - (exponent - digits.size).div(9) + 1
    end
    BigDecimal(other, [words * 9, 32].max)
  end
  private :__bigdecimal_expand

  # These have to be written with def rather than define_method: an alias of a
  # define_method'd method does not compare equal to it here, and #modulo has to stay
  # indistinguishable from #%.
  # The comparisons expand it the same way (BigDecimal("0.5") == Rational(1, 2)), and so does
  # #coerce, which is how Rational + BigDecimal comes back here. Comparing with Rational#==
  # instead would never end: it hands an unknown operand back to BigDecimal#==.
  { :+ => :add, :- => :sub, :* => :mul, :/ => :quo, :% => :mod, :coerce => :coerce,
    :== => :eq, :<=> => :cmp, :< => :lt, :<= => :le, :> => :gt, :>= => :ge }.each do |operator, name|
    alias_method :"__#{name}_without_rationals", operator
    private :"__#{name}_without_rationals"
    class_eval <<-RUBY, __FILE__, __LINE__ + 1
      def #{operator}(other)
        other = __bigdecimal_expand(other) if Rational === other
        __#{name}_without_rationals(other)
      end
    RUBY
  end
  alias_method :eql?, :==
  alias_method :===, :==

  { :add => "other, digits", :sub => "other, digits", :mult => "other, digits", :div => "other, *digits",
    :divmod => "other", :remainder => "other" }.each do |name, params|
    alias_method :"__#{name}_without_rationals", name
    private :"__#{name}_without_rationals"
    class_eval <<-RUBY, __FILE__, __LINE__ + 1
      def #{name}(#{params})
        other = __bigdecimal_expand(other) if Rational === other
        __#{name}_without_rationals(#{params})
      end
    RUBY
  end

  # quo(value, digits) is #div(value, digits), and the plain quotient for no or zero digits.
  def quo(other, digits = nil)
    return self / other if digits.nil?
    digits = Integer === digits ? digits : Internal.coerce_validate_prec(digits, :quo, accept_zero: true)
    raise ArgumentError, "negative precision" if digits < 0
    return self / other if digits.zero?
    div(Rational === other ? __bigdecimal_expand(other) : other, digits)
  end

  # #modulo is #%, not a copy of it - redefining #% above left #modulo pointing at the old
  # method, and BigDecimal.instance_method(:modulo) == BigDecimal.instance_method(:%) checks.
  alias_method :modulo, :%
end

# The pieces of bigdecimal 4.0.1 that bigdecimal/math, bigdecimal/jacobian and the rest of
# its Ruby half build on. BigDecimal::Internal is the gem's own lib/bigdecimal.rb verbatim;
# #n_significant_digits and #_decimal_shift are methods of its C extension, written here on
# top of #split.
class BigDecimal
  module Internal # :nodoc:

    # Coerce x to BigDecimal with the specified precision.
    # TODO: some methods (example: BigMath.exp) require more precision than specified to coerce.
    def self.coerce_to_bigdecimal(x, prec, method_name) # :nodoc:
      case x
      when BigDecimal
        return x
      when Integer, Float
        return BigDecimal(x, 0)
      when Rational
        return BigDecimal(x, [prec, 2 * BigDecimal.double_fig].max)
      end
      raise ArgumentError, "#{x.inspect} can't be coerced into BigDecimal"
    end

    def self.coerce_validate_prec(prec, method_name, accept_zero: false) # :nodoc:
      unless Integer === prec
        original = prec
        # Emulate Integer.try_convert for ruby < 3.1
        if prec.respond_to?(:to_int)
          prec = prec.to_int
        else
          raise TypeError, "no implicit conversion of #{original.class} into Integer"
        end
        raise TypeError, "can't convert #{original.class} to Integer" unless Integer === prec
      end

      if accept_zero
        raise ArgumentError, "Negative precision for #{method_name}" if prec < 0
      else
        raise ArgumentError, "Zero or negative precision for #{method_name}" if prec <= 0
      end
      prec
    end

    def self.infinity_computation_result # :nodoc:
      if BigDecimal.mode(BigDecimal::EXCEPTION_ALL).anybits?(BigDecimal::EXCEPTION_INFINITY)
        raise FloatDomainError, "Computation results in 'Infinity'"
      end
      BigDecimal::INFINITY
    end

    def self.nan_computation_result # :nodoc:
      if BigDecimal.mode(BigDecimal::EXCEPTION_ALL).anybits?(BigDecimal::EXCEPTION_NaN)
        raise FloatDomainError, "Computation results to 'NaN'"
      end
      BigDecimal::NAN
    end
  end

  # BigDecimal_n_significant_digits: the digits between the first and the last nonzero one.
  def n_significant_digits
    return 0 if zero? || !finite?
    split[1].sub(/0+\z/, "").length
  end unless method_defined?(:n_significant_digits)

  # BigDecimal_decimal_shift: self * 10**n, exactly, at whatever precision that takes.
  def _decimal_shift(n)
    n = n.to_int unless Integer === n
    return self if zero? || nan? || infinite? || n == 0
    sign, digits, _base, exponent = split
    BigDecimal("#{sign < 0 ? '-' : ''}0.#{digits}e#{exponent + n}")
  end unless method_defined?(:_decimal_shift)
end

# bigdecimal 4.0.1's #**, #power, BigMath.log and BigMath.exp, which the gem writes in Ruby
# (its lib/bigdecimal.rb) on top of the C extension's primitives - verbatim. #sqrt is the
# extension's C# counterpart, which takes an exact BigInteger root.
class BigDecimal
  #  call-seq:
  #    self ** other -> bigdecimal
  #
  #  Returns the \BigDecimal value of +self+ raised to power +other+:
  #
  #    b = BigDecimal('3.14')
  #    b ** 2              # => 0.98596e1
  #    b ** 2.0            # => 0.98596e1
  #    b ** Rational(2, 1) # => 0.98596e1
  #
  #  Related: BigDecimal#power.
  #
  def **(y)
    case y
    when BigDecimal, Integer, Float, Rational
      power(y)
    when nil
      raise TypeError, 'wrong argument type NilClass'
    else
      x, y = y.coerce(self)
      x**y
    end
  end

  # call-seq:
  #   power(n)
  #   power(n, prec)
  #
  # Returns the value raised to the power of n.
  #
  # Also available as the operator **.
  #
  def power(y, prec = 0)
    prec = Internal.coerce_validate_prec(prec, :power, accept_zero: true)
    x = self
    y = Internal.coerce_to_bigdecimal(y, prec.nonzero? || n_significant_digits, :power)

    return Internal.nan_computation_result if x.nan? || y.nan?
    return BigDecimal(1) if y.zero?

    if y.infinite?
      if x < 0
        return BigDecimal(0) if x < -1 && y.negative?
        return BigDecimal(0) if x > -1 && y.positive?
        raise Math::DomainError, 'Result undefined for negative base raised to infinite power'
      elsif x < 1
        return y.positive? ? BigDecimal(0) : BigDecimal::Internal.infinity_computation_result
      elsif x == 1
        return BigDecimal(1)
      else
        return y.positive? ? BigDecimal::Internal.infinity_computation_result : BigDecimal(0)
      end
    end

    if x.infinite? && y < 0
      # Computation result will be +0 or -0. Avoid overflow.
      neg = x < 0 && y.frac.zero? && y % 2 == 1
      return neg ? -BigDecimal(0) : BigDecimal(0)
    end

    if x.zero?
      return BigDecimal(1) if y.zero?
      return BigDecimal(0) if y > 0
      if y.frac.zero? && y % 2 == 1 && x.sign == -1
        return -BigDecimal::Internal.infinity_computation_result
      else
        return BigDecimal::Internal.infinity_computation_result
      end
    elsif x < 0
      if y.frac.zero?
        if y % 2 == 0
          return (-x).power(y, prec)
        else
          return -(-x).power(y, prec)
        end
      else
        raise Math::DomainError, 'Computation results in complex number'
      end
    elsif x == 1
      return BigDecimal(1)
    end

    limit = BigDecimal.limit
    frac_part = y.frac

    if frac_part.zero? && prec.zero? && limit.zero?
      # Infinite precision calculation for `x ** int` and `x.power(int)`
      int_part = y.fix.to_i
      int_part = -int_part if (neg = int_part < 0)
      ans = BigDecimal(1)
      n = 1
      xn = x
      while true
        ans *= xn if int_part.allbits?(n)
        n <<= 1
        break if n > int_part
        xn *= xn
        # Detect overflow/underflow before consuming infinite memory
        if (xn.exponent.abs - 1) * int_part / n >= 0x7FFFFFFFFFFFFFFF
          return ((xn.exponent > 0) ^ neg ? BigDecimal::Internal.infinity_computation_result : BigDecimal(0)) * (int_part.even? || x > 0 ? 1 : -1)
        end
      end
      return neg ? BigDecimal(1) / ans : ans
    end

    result_prec = prec.nonzero? || [x.n_significant_digits, y.n_significant_digits, BigDecimal.double_fig].max + BigDecimal.double_fig
    result_prec = [result_prec, limit].min if prec.zero? && limit.nonzero?

    prec2 = result_prec + BigDecimal.double_fig

    if y < 0
      inv = x.power(-y, prec2)
      return BigDecimal(0) if inv.infinite?
      return BigDecimal::Internal.infinity_computation_result if inv.zero?
      return BigDecimal(1).div(inv, result_prec)
    end

    if frac_part.zero? && y.exponent < Math.log(result_prec) * 5 + 20
      # Use exponentiation by squaring if y is an integer and not too large
      pow_prec = prec2 + y.exponent
      n = 1
      xn = x
      ans = BigDecimal(1)
      int_part = y.fix.to_i
      while true
        ans = ans.mult(xn, pow_prec) if int_part.allbits?(n)
        n <<= 1
        break if n > int_part
        xn = xn.mult(xn, pow_prec)
      end
      ans.mult(1, result_prec)
    else
      if x > 1 && x.finite?
        # To calculate exp(z, prec), z needs prec+max(z.exponent, 0) precision if z > 0.
        # Estimate (y*log(x)).exponent
        logx_exponent = x < 2 ? (x - 1).exponent : Math.log10(x.exponent).round
        ylogx_exponent = y.exponent + logx_exponent
        prec2 += [ylogx_exponent, 0].max
      end
      BigMath.exp(BigMath.log(x, prec2).mult(y, prec2), result_prec)
    end
  end
end

# Core BigMath methods for BigDecimal (log, exp) are defined here.
# Other methods (sin, cos, atan) are defined in 'bigdecimal/math.rb'.
module BigMath
  module_function

  # call-seq:
  #   BigMath.log(decimal, numeric)    -> BigDecimal
  #
  # Computes the natural logarithm of +decimal+ to the specified number of
  # digits of precision, +numeric+.
  #
  # If +decimal+ is zero or negative, raises Math::DomainError.
  #
  # If +decimal+ is positive infinity, returns Infinity.
  #
  # If +decimal+ is NaN, returns NaN.
  #
  def log(x, prec)
    prec = BigDecimal::Internal.coerce_validate_prec(prec, :log)
    raise Math::DomainError, 'Complex argument for BigMath.log' if Complex === x

    x = BigDecimal::Internal.coerce_to_bigdecimal(x, prec, :log)
    return BigDecimal::Internal.nan_computation_result if x.nan?
    raise Math::DomainError, 'Negative argument for log' if x < 0
    return -BigDecimal::Internal.infinity_computation_result if x.zero?
    return BigDecimal::Internal.infinity_computation_result if x.infinite?
    return BigDecimal(0) if x == 1

    prec2 = prec + BigDecimal.double_fig
    BigDecimal.save_limit do
      BigDecimal.limit(0)
      if x > 10 || x < 0.1
        log10 = log(BigDecimal(10), prec2)
        exponent = x.exponent
        x = x._decimal_shift(-exponent)
        if x < 0.3
          x *= 10
          exponent -= 1
        end
        return (log10 * exponent).add(log(x, prec2), prec)
      end

      x_minus_one_exponent = (x - 1).exponent

      # log(x) = log(sqrt(sqrt(sqrt(sqrt(x))))) * 2**sqrt_steps
      sqrt_steps = [Integer.sqrt(prec2) + 3 * x_minus_one_exponent, 0].max

      lg2 = 0.3010299956639812
      sqrt_prec = prec2 + [-x_minus_one_exponent, 0].max + (sqrt_steps * lg2).ceil

      sqrt_steps.times do
        x = x.sqrt(sqrt_prec)
      end

      # Taylor series for log(x) around 1
      # log(x) = -log((1 + X) / (1 - X)) where X = (x - 1) / (x + 1)
      # log(x) = 2 * (X + X**3 / 3 + X**5 / 5 + X**7 / 7 + ...)
      x = (x - 1).div(x + 1, sqrt_prec)
      y = x
      x2 = x.mult(x, prec2)
      1.step do |i|
        n = prec2 + x.exponent - y.exponent + x2.exponent
        break if n <= 0 || x.zero?
        x = x.mult(x2.round(n - x2.exponent), n)
        y = y.add(x.div(2 * i + 1, n), prec2)
      end

      y.mult(2 ** (sqrt_steps + 1), prec)
    end
  end

  # Taylor series for exp(x) around 0
  private_class_method def _exp_taylor(x, prec) # :nodoc:
    xn = BigDecimal(1)
    y = BigDecimal(1)
    1.step do |i|
      n = prec + xn.exponent
      break if n <= 0 || xn.zero?
      xn = xn.mult(x, n).div(i, n)
      y = y.add(xn, prec)
    end
    y
  end

  # call-seq:
  #   BigMath.exp(decimal, numeric)    -> BigDecimal
  #
  # Computes the value of e (the base of natural logarithms) raised to the
  # power of +decimal+, to the specified number of digits of precision.
  #
  # If +decimal+ is infinity, returns Infinity.
  #
  # If +decimal+ is NaN, returns NaN.
  #
  def exp(x, prec)
    prec = BigDecimal::Internal.coerce_validate_prec(prec, :exp)
    x = BigDecimal::Internal.coerce_to_bigdecimal(x, prec, :exp)
    return BigDecimal::Internal.nan_computation_result if x.nan?
    return x.positive? ? BigDecimal::Internal.infinity_computation_result : BigDecimal(0) if x.infinite?
    return BigDecimal(1) if x.zero?

    # exp(x * 10**cnt) = exp(x)**(10**cnt)
    cnt = x < -1 || x > 1 ? x.exponent : 0
    prec2 = prec + BigDecimal.double_fig + cnt
    x = x._decimal_shift(-cnt)

    # Calculation of exp(small_prec) is fast because calculation of x**n is fast
    # Calculation of exp(small_abs) converges fast.
    # exp(x) = exp(small_prec_part + small_abs_part) = exp(small_prec_part) * exp(small_abs_part)
    x_small_prec = x.round(Integer.sqrt(prec2))
    y = _exp_taylor(x_small_prec, prec2).mult(_exp_taylor(x.sub(x_small_prec, prec2), prec2), prec2)

    # calculate exp(x * 10**cnt) from exp(x)
    # exp(x * 10**k) = exp(x * 10**(k - 1)) ** 10
    cnt.times do
      y2 = y.mult(y, prec2)
      y5 = y2.mult(y2, prec2).mult(y, prec2)
      y = y5.mult(y5, prec2)
    end

    y.mult(1, prec)
  end
end

# bigdecimal 3.1's precision API that the C# class has no methods for, and the save_* blocks of
# BigDecimal's class; all five were NoMethodErrors.
class BigDecimal
  # The digits after the decimal point - BigDecimal("123.456").scale is 3, BigDecimal("1e10").scale 0.
  def scale
    return 0 if zero? || !finite?
    [n_significant_digits - exponent, 0].max
  end unless method_defined?(:scale)

  def precision_scale
    [precision, scale]
  end unless method_defined?(:precision_scale)

  class << self
    def save_rounding_mode
      mode = self.mode(ROUND_MODE)
      yield
    ensure
      self.mode(ROUND_MODE, mode)
    end unless method_defined?(:save_rounding_mode)

    def save_exception_mode
      saved = self.mode(EXCEPTION_ALL)
      yield
    ensure
      # mode(flag, value) takes true or false, so the saved flags go back one by one
      [EXCEPTION_NaN, EXCEPTION_INFINITY, EXCEPTION_UNDERFLOW, EXCEPTION_OVERFLOW, EXCEPTION_ZERODIVIDE].each do |flag|
        self.mode(flag, (saved & flag) != 0)
      end
    end unless method_defined?(:save_exception_mode)

    def save_limit
      limit = self.limit
      yield
    ensure
      self.limit(limit)
    end unless method_defined?(:save_limit)
  end
end
