# Rational, ported from Ruby 4.0's rational.c, together with the methods rational.c
# defines on Integer, Float, Numeric and String, Kernel#Rational, and the Rational
# branches of numeric.c's Integer#** and Float#**.
#
# Loaded by ruby4.rb as part of the core prelude, before complex.rb (the two refer to
# each other only at run time). Each method names the C function it follows; where a
# comment says nothing more, the method does what that function does, in the same order,
# because ruby/spec watches which methods a mock operand is sent and in what order.
#
# A Rational is two Integers in @numerator and @denominator, the denominator positive,
# frozen from birth. rational.c undefines Rational.new and Rational.allocate; instances
# are made by __new_internal__ (nurat_s_new_internal), which takes no checks.

class Numeric
  # rb_num_coerce_bin: other.coerce(self), then the operator on the pair.
  def __ir_num_coerce_bin__(other, op)
    x, y = __ir_do_coerce__(other, true)
    x.__send__(op, y)
  end
  private :__ir_num_coerce_bin__

  # rb_num_coerce_cmp: a failed coercion is nil, not an error.
  def __ir_num_coerce_cmp__(other, op)
    pair = __ir_do_coerce__(other, false)
    return nil if pair.nil?
    pair[0].__send__(op, pair[1])
  end
  private :__ir_num_coerce_cmp__

  # do_coerce. Answers [x, y], or nil when `err` is false and there is no pair.
  def __ir_do_coerce__(other, err)
    unless __ir_responds_to__(other, :coerce)
      return nil unless err
      ::Kernel.raise(::TypeError, "#{__ir_coerce_name__(other)} can't be coerced into #{self.class}")
    end
    ary = other.coerce(self)
    if ary.nil? && !err
      return nil
    end
    unless ::Array === ary && ary.size == 2
      return nil unless err
      ::Kernel.raise(::TypeError, "coerce must return [x, y]")
    end
    ary
  end
  private :__ir_do_coerce__

  # rb_num_negative_p: `x < 0`, except that a number with no #< (a Complex) is a failed
  # comparison rather than a NoMethodError.
  def self.__ir_negative_p__(x)
    return x < 0 if ::Integer === x || ::Float === x
    unless __ir_responds_to__(x, :<)
      ::Kernel.raise(::ArgumentError, "comparison of #{__ir_class_of__(x)} with 0 failed")
    end
    x < 0 ? true : false
  end

  # rb_special_const_p values are named by their inspect, everything else by its class.
  def __ir_coerce_name__(value)
    case value
    when nil then "nil"
    when true then "true"
    when false then "false"
    else __ir_message_type_name__(value)
    end
  end
  private :__ir_coerce_name__
end

class Rational < Numeric
  class << self
    alias_method :__ir_allocate__, :allocate
    private :__ir_allocate__
    undef_method :allocate
    undef_method :new
  end

  # nurat_s_new_internal: no canonicalisation, no checks; OBJ_FREEZE.
  def self.__new_internal__(num, den)
    __ir_allocate__.__send__(:__ir_set_parts__, num, den)
  end

  def __ir_set_parts__(num, den)
    @numerator = num
    @denominator = den
    freeze
  end
  private :__ir_set_parts__

  # rb_rational_raw: Integers (via #to_int) with the sign moved onto the numerator.
  def self.__raw__(num, den)
    num = ::Kernel.__ir_to_int__(num) unless ::Integer === num
    den = ::Kernel.__ir_to_int__(den) unless ::Integer === den
    if den < 0
      num = -num
      den = -den
    end
    __new_internal__(num, den)
  end

  # nurat_canonicalize + nurat_reduce: nurat_s_canonicalize_internal.
  def self.__canonicalize__(num, den)
    if den < 0
      num = -num
      den = -den
    elsif den == 0
      ::Kernel.raise(::ZeroDivisionError, "divided by 0")
    end
    unless num == 1 || den == 1
      g = num.gcd(den)
      num = num.div(g)
      den = den.div(g)
    end
    __new_internal__(num, den)
  end

  # nurat_s_canonicalize_internal_no_reduce
  def self.__canonicalize_no_reduce__(num, den)
    if den < 0
      num = -num
      den = -den
    elsif den == 0
      ::Kernel.raise(::ZeroDivisionError, "divided by 0")
    end
    __new_internal__(num, den)
  end

  # nurat_reduce
  def self.__reduce__(num, den)
    return [num, den] if num == 1 || den == 1
    g = num.gcd(den)
    [num.div(g), den.div(g)]
  end

  # nurat_int_check / nurat_int_value
  def self.__int_value__(num)
    unless ::Integer === num
      unless ::Numeric === num && num.integer?
        ::Kernel.raise(::TypeError, "not an integer")
      end
      num = num.to_i
    end
    num
  end

  # rb_rational_canonicalize: a whole Rational is its numerator.
  def self.__canonicalize_value__(x)
    ::Rational === x && x.denominator == 1 ? x.numerator : x
  end

  # f_one_p / f_zero_p / k_exact_one_p
  def self.__one__(x)
    case x
    when ::Integer then x == 1
    when ::Rational then x.numerator == 1 && x.denominator == 1
    else x == 1
    end
  end

  def self.__exact_one__(x)
    !(::Float === x) && __one__(x)
  end

  # rb_dbl_cmp
  def self.__dbl_cmp__(a, b)
    return nil if a.nan? || b.nan?
    return 0 if a == b
    a > b ? 1 : -1
  end

  attr_reader :numerator, :denominator

  # rb_rational_uminus
  def -@
    ::Rational.__canonicalize__(-@numerator, @denominator)
  end

  # f_addsub
  def __ir_addsub__(anum, aden, bnum, bden, k)
    g = aden.gcd(bden)
    a = anum * bden.div(g)
    b = bnum * aden.div(g)
    c = k == :+ ? a + b : a - b
    b = aden.div(g)
    g = c.gcd(g)
    num = c.div(g)
    a = bden.div(g)
    den = a * b
    ::Rational.__canonicalize_no_reduce__(num, den)
  end
  private :__ir_addsub__

  # rb_rational_plus
  def +(other)
    if ::Integer === other
      ::Rational.__canonicalize_no_reduce__(@numerator + other * @denominator, @denominator)
    elsif ::Float === other
      to_f + other
    elsif ::Rational === other
      __ir_addsub__(@numerator, @denominator, other.numerator, other.denominator, :+)
    else
      __ir_num_coerce_bin__(other, :+)
    end
  end

  # rb_rational_minus
  def -(other)
    if ::Integer === other
      ::Rational.__canonicalize_no_reduce__(@numerator - other * @denominator, @denominator)
    elsif ::Float === other
      to_f - other
    elsif ::Rational === other
      __ir_addsub__(@numerator, @denominator, other.numerator, other.denominator, :-)
    else
      __ir_num_coerce_bin__(other, :-)
    end
  end

  # f_muldiv
  def __ir_muldiv__(anum, aden, bnum, bden, k)
    if k == :/
      if bnum < 0
        anum = -anum
        bnum = -bnum
      end
      bnum, bden = bden, bnum
    end
    g1 = anum.gcd(bden)
    g2 = aden.gcd(bnum)
    num = anum.div(g1) * bnum.div(g2)
    den = aden.div(g2) * bden.div(g1)
    ::Rational.__canonicalize_no_reduce__(num, den)
  end
  private :__ir_muldiv__

  # rb_rational_mul
  def *(other)
    if ::Integer === other
      __ir_muldiv__(@numerator, @denominator, other, 1, :*)
    elsif ::Float === other
      to_f * other
    elsif ::Rational === other
      __ir_muldiv__(@numerator, @denominator, other.numerator, other.denominator, :*)
    else
      __ir_num_coerce_bin__(other, :*)
    end
  end

  # rb_rational_div
  def /(other)
    if ::Integer === other
      ::Kernel.raise(::ZeroDivisionError, "divided by 0") if other == 0
      __ir_muldiv__(@numerator, @denominator, other, 1, :/)
    elsif ::Float === other
      to_f / other
    elsif ::Rational === other
      ::Kernel.raise(::ZeroDivisionError, "divided by 0") if other.numerator == 0
      if @numerator == 1 && @denominator == 1
        return ::Rational.__canonicalize_no_reduce__(other.denominator, other.numerator)
      end
      __ir_muldiv__(@numerator, @denominator, other.numerator, other.denominator, :/)
    else
      __ir_num_coerce_bin__(other, :/)
    end
  end
  alias quo /

  # nurat_fdiv
  def fdiv(other)
    return self / 0.0 if other == 0
    return to_f if ::Integer === other && other == 1
    div = self / other
    return div.to_f if ::Rational === div
    return div if ::Float === div
    div.to_f
  end

  # rb_rational_pow
  def **(other)
    if ::Numeric === other && !(::Float === other) && other == 0
      return ::Rational.__new_internal__(1, 1)
    end
    other = other.numerator if ::Rational === other && other.denominator == 1

    if ::Numeric === other && !(::Float === other)
      if @denominator == 1
        if @numerator == 1
          return ::Rational.__new_internal__(1, 1)
        elsif @numerator == -1 && ::Integer === other
          return ::Rational.__new_internal__(other.odd? ? -1 : 1, 1)
        elsif @numerator == 0
          if ::Numeric.__ir_negative_p__(other)
            ::Kernel.raise(::ZeroDivisionError, "divided by 0")
          else
            return ::Rational.__new_internal__(0, 1)
          end
        end
      end
    end

    if ::Integer === other && other.abs <= FIXNUM_MAX__
      if other > 0
        num = @numerator ** other
        den = @denominator ** other
      elsif other < 0
        num = @denominator ** -other
        den = @numerator ** -other
      else
        num = 1
        den = 1
      end
      if ::Float === num
        return ::Float::NAN if ::Float === den
        return num
      end
      if ::Float === den
        num = 0
        den = 1
      end
      ::Rational.__canonicalize__(num, den)
    elsif ::Integer === other
      ::Kernel.raise(::ArgumentError, "exponent is too large")
    elsif ::Float === other || ::Rational === other
      to_f ** other
    else
      __ir_num_coerce_bin__(other, :**)
    end
  end

  # The largest Fixnum: an exponent past it is a Bignum, which rb_rational_pow refuses.
  FIXNUM_MAX__ = 2**62 - 1
  private_constant :FIXNUM_MAX__

  # rb_rational_cmp
  def <=>(other)
    case other
    when ::Integer
      return @numerator <=> other if @denominator == 1
      (@numerator - other * @denominator) <=> 0
    when ::Rational
      (@numerator * other.denominator - other.numerator * @denominator) <=> 0
    when ::Float
      ::Rational.__dbl_cmp__(to_f, other)
    else
      __ir_num_coerce_cmp__(other, :<=>)
    end
  end

  # nurat_eqeq_p
  def ==(other)
    if ::Integer === other
      return true if @numerator == 0 && other == 0
      return false unless @denominator == 1
      @numerator == other
    elsif ::Float === other
      ::Rational.__dbl_cmp__(to_f, other) == 0
    elsif ::Rational === other
      return true if @numerator == 0 && other.numerator == 0
      @numerator == other.numerator && @denominator == other.denominator
    else
      other == self ? true : false
    end
  end

  # nurat_coerce
  def coerce(other)
    case other
    when ::Integer
      [::Rational.__new_internal__(other, 1), self]
    when ::Float
      [other, to_f]
    when ::Rational
      [other, self]
    when ::Complex
      imag = other.imaginary
      unless !(::Float === imag) && imag == 0
        return [other, ::Kernel.Complex(self, 0)]
      end
      real = other.real
      [::Float === real ? real.to_r : ::Rational.__new_internal__(real, 1), self]
    else
      ::Kernel.raise(::TypeError, "#{__ir_coerce_name__(other)} can't be coerced into #{self.class}")
    end
  end

  # nurat_positive_p / nurat_negative_p
  def positive?
    @numerator > 0
  end

  def negative?
    @numerator < 0
  end

  # rb_rational_abs
  def abs
    return ::Rational.__new_internal__(-@numerator, @denominator) if @numerator < 0
    self
  end
  alias magnitude abs

  # nurat_floor / nurat_ceil / nurat_truncate: the precision-free forms.
  def __ir_floor__
    @numerator.div(@denominator)
  end
  private :__ir_floor__

  def __ir_ceil__
    -((-@numerator).div(@denominator))
  end
  private :__ir_ceil__

  def __ir_truncate__
    return -((-@numerator).div(@denominator)) if @numerator < 0
    @numerator.div(@denominator)
  end
  private :__ir_truncate__

  # nurat_round_half_up / _down / _even
  def __ir_round_half_up__
    num = @numerator
    den = @denominator
    neg = num < 0
    num = -num if neg
    num = (num * 2 + den).div(den * 2)
    neg ? -num : num
  end
  private :__ir_round_half_up__

  def __ir_round_half_down__
    num = @numerator
    den = @denominator
    neg = num < 0
    num = -num if neg
    num = (num * 2 + den - 1).div(den * 2)
    neg ? -num : num
  end
  private :__ir_round_half_down__

  def __ir_round_half_even__
    num = @numerator
    den = @denominator
    neg = num < 0
    num = -num if neg
    num, rem = (num * 2 + den).divmod(den * 2)
    num &= ~1 if rem == 0
    neg ? -num : num
  end
  private :__ir_round_half_even__

  # f_round_common: no argument is the bare function, an argument must be an Integer.
  def __ir_round_common__(args, func)
    if args.size > 1
      ::Kernel.raise(::ArgumentError, "wrong number of arguments (given #{args.size}, expected 0..1)")
    end
    return __send__(func) if args.empty?
    n = args[0]
    ::Kernel.raise(::TypeError, "not an integer") unless ::Integer === n
    __ir_round_n__(n, func)
  end
  private :__ir_round_common__

  # f_round_n. A positive precision the denominator already divides into is exact as it
  # stands; working that out first spares building 10**n for an enormous n, which is the
  # only way MRI's own `s = self * b` comes back as a Float here.
  def __ir_round_n__(n, func)
    return self if n > 0 && __ir_exact_at__(n)
    b = 10**n
    s = self * b
    if ::Float === s
      return 0 if n < 0
      return self
    end
    s = ::Rational.__new_internal__(s, 1) unless ::Rational === s
    s = s.__send__(func)
    s = ::Rational.__new_internal__(s, 1) / b
    s = s.__send__(:__ir_truncate__) if ::Rational === s && n < 1
    s
  end
  private :__ir_round_n__

  # A denominator divides a power of ten only if it is made of twos and fives.
  def __ir_exact_at__(ndigits)
    d = @denominator
    twos = 0
    while d.even?
      d >>= 1
      twos += 1
    end
    fives = 0
    while (d % 5).zero?
      d /= 5
      fives += 1
    end
    d == 1 && ndigits >= (twos > fives ? twos : fives)
  end
  private :__ir_exact_at__

  def floor(*args)
    __ir_round_common__(args, :__ir_floor__)
  end

  def ceil(*args)
    __ir_round_common__(args, :__ir_ceil__)
  end

  def truncate(*args)
    __ir_round_common__(args, :__ir_truncate__)
  end

  # nurat_round_n, with rb_num_get_rounding_option for half:.
  def round(*args, half: nil)
    __ir_round_common__(args, ::Rational.__round_func__(half))
  end

  # rb_num_get_rounding_option
  def self.__round_func__(half)
    return :__ir_round_half_up__ if half.nil?
    name =
      if ::Symbol === half
        half.to_s
      elsif ::String === half
        half
      else
        nil
      end
    case name
    when "up" then return :__ir_round_half_up__
    when "even" then return :__ir_round_half_even__
    when "down" then return :__ir_round_half_down__
    end
    if name.nil?
      str = ::String.try_convert(half) rescue nil
      ::Kernel.raise(::ArgumentError, "invalid rounding mode: #{half.inspect}") if str.nil?
      return __round_func__(str)
    end
    ::Kernel.raise(::ArgumentError, "invalid rounding mode: #{name}")
  end

  # nurat_truncate
  def to_i
    __ir_truncate__
  end

  # nurat_to_double: Integer#fdiv divides exactly before rounding, so a quotient of two
  # numbers past Float::MAX still comes out right.
  def to_f
    @numerator.fdiv(@denominator)
  end

  def to_r
    self
  end

  # nurat_rationalize_internal: the simplest rational in [a, b], walked as a continued
  # fraction until the interval holds an integer. Answers [p, q].
  def self.__rationalize_internal__(a, b)
    p0 = 0
    p1 = 1
    q0 = 1
    q1 = 0
    while true
      c = a.ceil
      break if c < b
      k = c - 1
      p2 = k * p1 + p0
      q2 = k * q1 + q0
      # f_reciprocal is f_quo(1, x), and f_quo of an Integer is Integer#/.
      t = 1 / (b - k)
      b = 1 / (a - k)
      a = t
      p0 = p1
      q0 = q1
      p1 = p2
      q1 = q2
    end
    [c * p1 + p0, c * q1 + q0]
  end

  # nurat_rationalize
  def rationalize(*args)
    if args.size > 1
      ::Kernel.raise(::ArgumentError, "wrong number of arguments (given #{args.size}, expected 0..1)")
    end
    return self if args.empty?
    e = args[0].abs
    neg = @numerator < 0
    rat = neg ? ::Rational.__canonicalize__(-@numerator, @denominator) : self
    zero = ::Integer === e && e == 0
    a = zero ? rat : rat - e
    b = zero ? rat : rat + e
    return self if a == b
    p, q = ::Rational.__rationalize_internal__(a, b)
    return ::Rational.__new_internal__(-p, q) if neg
    ::Rational.__canonicalize__(p, q)
  end

  # nurat_hash
  def hash
    [@numerator, @denominator, ::Rational].hash
  end

  # nurat_to_s / nurat_inspect
  def to_s
    "#{@numerator}/#{@denominator}"
  end

  def inspect
    "(#{@numerator.inspect}/#{@denominator.inspect})"
  end

  # Rational and Complex are special objects in MRI's sense: #dup and #clone answer the
  # receiver, like Integer and Symbol, rather than allocating a copy.
  def dup
    self
  end

  def clone(freeze: nil)
    unless freeze.nil? || freeze == true || freeze == false
      ::Kernel.raise(::ArgumentError, "unexpected value for freeze: #{freeze.class}")
    end
    self
  end

  # nurat_marshal_dump / nurat_marshal_load (by way of Rational::compatible in MRI).
  def marshal_dump
    [@numerator, @denominator]
  end
  private :marshal_dump

  def marshal_load(a)
    ::Kernel.raise(::TypeError, "wrong argument type #{a.class} (expected Array)") unless ::Array === a
    if a.size != 2
      ::Kernel.raise(::ArgumentError, "marshaled rational must have an array whose length is 2 but #{a.size}")
    end
    num, den = a
    ::Rational.__int_value__(num) unless ::Integer === num
    ::Rational.__int_value__(den) unless ::Integer === den
    if den < 0
      num = -num
      den = -den
    elsif den == 0
      ::Kernel.raise(::ZeroDivisionError, "divided by 0")
    end
    @numerator = num
    @denominator = den
    freeze
    self
  end
  private :marshal_load

  # --- conversion: nurat_convert and the string scanner -----------------------

  # rb_convert_type_with_id(val, T_RATIONAL, "Rational", :to_r). Every test has to
  # survive a BasicObject, which answers neither #nil?, #class nor #respond_to? - and
  # which MRI converts perfectly well when it has a #to_r.
  def self.__to_rational__(value)
    name = nil.equal?(value) ? 'nil' : __ir_class_of__(value)
    unless __ir_responds_to__(value, :to_r)
      ::Kernel.raise(::TypeError, "can't convert #{name} into Rational")
    end
    result = value.to_r
    unless ::Rational === result
      ::Kernel.raise(::TypeError,
        "can't convert #{name} into Rational (#{name}#to_r gives #{__ir_conversion_result_name__(result)})")
    end
    result
  end

  # rb_check_convert_type_with_id: a missing #to_r is not an error.
  def self.__check_to_rational__(value)
    return value unless __ir_responds_to__(value, :to_r)
    __to_rational__(value)
  end

  # rb_check_to_int, under rb_protect: nil unless #to_int gives an Integer.
  def self.__check_to_int__(value)
    return value if ::Integer === value
    return nil unless __ir_responds_to__(value, :to_int)
    result = value.to_int
    ::Integer === result ? result : nil
  rescue ::Exception
    nil
  end

  # The pre-pass nurat_convert gives each argument: an exactly-real Complex is its real
  # part, a Float becomes exact, a String is parsed strictly and an object with no #to_r
  # is offered #to_int. Answers [value] or nil when a quiet conversion failed.
  def self.__convert_arg__(value, raise_)
    if ::Complex === value && ::Complex.__exact_zero__(value.imaginary)
      value = value.real
    end
    case value
    when ::Integer, ::Rational
      value
    when ::Float
      value.to_r
    when ::String
      parsed = __parse_string__(value, true, raise_)
      return UNDEF__ if parsed.nil?
      parsed
    else
      unless __ir_responds_to__(value, :to_r)
        tmp = __check_to_int__(value)
        return tmp unless tmp.nil?
      end
      value
    end
  end

  UNDEF__ = ::Object.new.freeze
  private_constant :UNDEF__

  # nurat_convert. `given2` tells Rational(x) from Rational(x, nil).
  def self.__convert__(a1, a2, given2, raise_)
    if nil.equal?(a1) || (given2 && nil.equal?(a2))
      return nil unless raise_
      ::Kernel.raise(::TypeError, "can't convert nil into Rational")
    end

    a1 = __convert_arg__(a1, raise_)
    return nil if UNDEF__.equal?(a1)
    if given2
      a2 = __convert_arg__(a2, raise_)
      return nil if UNDEF__.equal?(a2)
    end

    if ::Rational === a1
      return a1 if !given2 || __exact_one__(a2)
    end

    if !given2
      unless ::Integer === a1
        return __to_rational__(a1) if raise_
        begin
          return __to_rational__(a1)
        rescue ::Exception
          return nil
        end
      end
    else
      unless ::Numeric === a1
        if raise_
          a1 = __check_to_rational__(a1)
        else
          begin
            a1 = __to_rational__(a1)
          rescue ::Exception
            return nil
          end
        end
      end
      unless ::Numeric === a2
        if raise_
          a2 = __check_to_rational__(a2)
        else
          begin
            a2 = __to_rational__(a2)
          rescue ::Exception
            return nil
          end
        end
      end
      if ::Numeric === a1 && ::Numeric === a2 && (!(::Integer === a1) || !(::Integer === a2))
        a1 = begin
          __to_rational__(a1)
        rescue ::Exception
          a1
        end
        return a2 == 1 ? a1 : a1 / a2
      end
    end

    a1 = __int_value__(a1)
    if !given2
      a2 = 1
    elsif !(::Integer === a2) && !raise_
      return nil
    else
      a2 = __int_value__(a2)
    end
    __canonicalize__(a1, a2)
  end

  # rational.c's read_num: [ok, index, num, nexp], the value being num * 10**-nexp.
  def self.__read_num__(s, i, n)
    num = 0
    nexp = 0
    fn = 0
    ok = false

    if i < n && s[i] != "."
      r = __read_int__(s, i, n)
      return [false, i, 0, 0] if r.nil?
      num = r[0]
      i = r[1]
      ok = true
    end

    if i < n && s[i] == "."
      i += 1
      r = __read_int__(s, i, n)
      return [true, i, num, nexp] if r.nil?
      fp, i, count = r
      num = num == 0 ? fp : num * (10**count) + fp
      nexp = count
      fn = count
      ok = true
    end

    if ok && i + 1 < n && (s[i] == "e" || s[i] == "E")
      i += 1
      expsign = "?"
      if s[i] == "-" || s[i] == "+"
        expsign = s[i]
        i += 1
      end
      r = __read_int__(s, i, n)
      return [true, i, num, nexp] if r.nil?
      exp = r[0]
      i = r[1]
      if exp != 0
        if expsign == "-"
          exp += fn if fn != 0
        else
          exp -= fn if fn != 0
          exp = -exp
        end
        nexp = exp
      end
    end

    [ok, i, num, nexp]
  end

  # rb_int_parse_cstr(base 10, RB_INT_PARSE_UNDERSCORE, no sign, no badcheck).
  # Underscores are allowed between digits; a doubled underscore stops the scan, and the
  # end is always just past the last digit, which is why "1_" leaves the underscore for
  # the strict check to trip over. Answers [value, end, digit_count], or nil.
  def self.__read_int__(s, i, n)
    return nil unless i < n && __digit__(s[i])
    start = i
    last = i
    nondigit = false
    digits = 0
    while i < n
      c = s[i]
      if c == "_"
        break if nondigit
        nondigit = true
      elsif __digit__(c)
        nondigit = false
        digits += 1
        last = i + 1
      else
        break
      end
      i += 1
    end
    text = s[start...last]
    text = text.delete("_") if text.include?("_")
    [text.to_i, last, digits]
  end

  def self.__digit__(c)
    !c.nil? && c >= "0" && c <= "9"
  end

  # isspace in the C locale
  WS__ = " \t\n\v\f\r"
  private_constant :WS__

  def self.__skip_ws__(s, i, n)
    i += 1 while i < n && WS__.include?(s[i])
    i
  end

  # parse_rat. Answers nil when a strict parse fails; an exponent too large for a power
  # of ten degenerates to a Float, which the callers turn into FloatDomainError.
  EXP_LIMIT__ = 1_000_000
  private_constant :EXP_LIMIT__

  def self.__parse_rat__(s, strict, raise_)
    n = s.length
    i = __skip_ws__(s, 0, n)
    sign = "?"
    if i < n && (s[i] == "-" || s[i] == "+")
      sign = s[i]
      i += 1
    end

    ok, i, num, nexp = __read_num__(s, i, n)
    unless ok
      return nil if strict
      return __new_internal__(0, 1)
    end

    den = 1
    if i < n && s[i] == "/"
      i += 1
      ok2, i, den2, dexp = __read_num__(s, i, n)
      if !ok2
        return nil if strict
      elsif den2 == 0
        return nil unless raise_
        ::Kernel.raise(::ZeroDivisionError, "divided by 0")
      elsif strict && __skip_ws__(s, i, n) != n
        return nil
      else
        den = den2
        nexp -= dexp
        num, den = __reduce__(num, den)
      end
    elsif strict && __skip_ws__(s, i, n) != n
      return nil
    end

    if nexp != 0
      if nexp < 0
        return sign == "-" ? -::Float::INFINITY : ::Float::INFINITY if -nexp > EXP_LIMIT__
        num *= 10**(-nexp)
      else
        return sign == "-" ? -0.0 : 0.0 if nexp > EXP_LIMIT__
        den *= 10**nexp
      end
      num, den = __reduce__(num, den)
    end

    num = -num if sign == "-"
    __raw__(num, den)
  end

  # string_to_r_strict / string_to_r. rational.c works on the String's bytes up to its
  # end, an embedded NUL included.
  def self.__parse_string__(str, strict, raise_error = true)
    __must_ascii_compatible__(str)
    value = __parse_rat__(str, strict, raise_error)
    if value.nil?
      return nil unless raise_error
      ::Kernel.raise(::ArgumentError, "invalid value for convert(): #{str.inspect}")
    end
    if ::Float === value && value != 0.0
      return nil unless raise_error
      ::Kernel.raise(::FloatDomainError, "Infinity")
    end
    value
  end

  # rb_must_asciicompat
  def self.__must_ascii_compatible__(str)
    return if str.encoding.ascii_compatible?
    ::Kernel.raise(::Encoding::CompatibilityError, "ASCII incompatible encoding: #{str.encoding}")
  end

  # nurat_s_convert
  def self.convert(*args)
    if args.size < 1 || args.size > 2
      ::Kernel.raise(::ArgumentError, "wrong number of arguments (given #{args.size}, expected 1..2)")
    end
    __convert__(args[0], args[1], args.size == 2, true)
  end
  private_class_method :convert
end

module Kernel
  # rb_to_int, for rb_rational_raw.
  def self.__ir_to_int__(value)
    return value if ::Integer === value
    unless __ir_responds_to__(value, :to_int)
      ::Kernel.raise(::TypeError, "no implicit conversion of #{__ir_conversion_result_name__(value)} into Integer")
    end
    result = value.to_int
    unless ::Integer === result
      name = __ir_class_of__(value)
      ::Kernel.raise(::TypeError, "can't convert #{name} to Integer (#{name}#to_int gives #{__ir_conversion_result_name__(result)})")
    end
    result
  end

  # nurat_f_rational. Rational(x) and Rational(x, nil) are different calls - the second
  # is a TypeError - so the arity is read off *args rather than from a default.
  def Rational(*args, exception: true)
    if args.size < 1 || args.size > 2
      ::Kernel.raise(::ArgumentError, "wrong number of arguments (given #{args.size}, expected 1..2)")
    end
    ::Rational.__convert__(args[0], args[1], args.size == 2, ::Kernel.__opts_exception__(exception))
  end
  module_function :Rational
end

class Integer
  # integer_to_r / integer_rationalize
  def to_r
    ::Rational.__new_internal__(self, 1)
  end

  def rationalize(*args)
    if args.size > 1
      ::Kernel.raise(::ArgumentError, "wrong number of arguments (given #{args.size}, expected 0..1)")
    end
    ::Rational.__new_internal__(self, 1)
  end

  # numeric.c's rb_int_pow (fix_pow) for what makes a Rational or a Complex: a negative
  # exponent and a negative base to a fractional power. The rest is the C# Integer#**.
  alias_method :__ir_pow__, :**
  private :__ir_pow__

  def **(other)
    if ::Integer === other
      return __ir_pow__(other) if other >= 0
      return 1 if self == 1
      return (other.even? ? 1 : -1) if self == -1
      ::Kernel.raise(::ZeroDivisionError, "divided by 0") if self == 0
      y = __ir_pow__(-other)
      return 1.0 / y if ::Float === y
      ::Rational.__raw__(1, y)
    elsif ::Float === other
      return 1.0 if other == 0.0
      return (other < 0 ? ::Float::INFINITY : 0.0) if self == 0
      return 1.0 if self == 1
      if self < 0 && ::Float.__ir_fractional__(other)
        return ::Complex.__polar_pi__((-to_f) ** other, other)
      end
      __ir_pow__(other)
    else
      __ir_num_coerce_bin__(other, :**)
    end
  end
end

class Float
  # Float#** is C# (ClrFloat.Power): a Float or Integer power of a Float is Math.Pow there.
  # These are the two cases it hands back.

  # rb_float_pow: a negative base to a power that is not whole leaves the real line.
  # (-self) is positive, so its ** stays in C#.
  def __ir_pow_complex__(other)
    ::Complex.__polar_pi__((-self) ** other, other)
  end
  private :__ir_pow_complex__

  def __ir_pow_coerce__(other)
    __ir_num_coerce_bin__(other, :**)
  end
  private :__ir_pow_coerce__

  # `y != round(y)` in C: true for a NaN, false for an infinity.
  def self.__ir_fractional__(y)
    return true if y.nan?
    return false if y.infinite?
    y != y.round
  end

  # float_decode_internal: [f, n] with self == f * 2**n and f an Integer.
  def __ir_decode__
    fraction, exponent = ::Math.frexp(self)
    [::Math.ldexp(fraction, 53).to_i, exponent - 53]
  end
  private :__ir_decode__

  # float_to_r
  def to_r
    ::Kernel.raise(::FloatDomainError, to_s) if nan? || infinite?
    f, n = __ir_decode__
    return ::Rational.__new_internal__(f, 1) if n == 0
    return ::Rational.__new_internal__(f << n, 1) if n > 0
    ::Rational.__canonicalize__(f, 1 << -n)
  end

  # float_rationalize, rb_flt_rationalize_with_prec and rb_flt_rationalize.
  def rationalize(*args)
    if args.size > 1
      ::Kernel.raise(::ArgumentError, "wrong number of arguments (given #{args.size}, expected 0..1)")
    end
    neg = self < 0.0
    flt = neg ? -self : self
    rat =
      if args.size == 1
        e = args[0].abs
        a = flt - e
        b = flt + e
        if a == b
          flt.to_r
        else
          p, q = ::Rational.__rationalize_internal__(a, b)
          ::Rational.__canonicalize__(p, q)
        end
      else
        ::Kernel.raise(::FloatDomainError, flt.to_s) if flt.nan? || flt.infinite?
        f, n = flt.__send__(:__ir_decode__)
        if f == 0 || n >= 0
          ::Rational.__new_internal__(f << n, 1)
        else
          radix_times_f = 2 * f
          den = 2**(1 - n)
          a = radix_times_f - 1
          b = radix_times_f + 1
          if a == b
            flt.to_r
          else
            p, q = ::Rational.__rationalize_internal__(::Rational.__canonicalize__(a, den),
                                                       ::Rational.__canonicalize__(b, den))
            ::Rational.__canonicalize__(p, q)
          end
        end
      end
    neg ? ::Rational.__new_internal__(-rat.numerator, rat.denominator) : rat
  end

  # rb_float_numerator / rb_float_denominator
  def numerator
    return self unless finite?
    to_r.numerator
  end

  def denominator
    return 1 unless finite?
    to_r.denominator
  end
end

class Numeric
  # numeric_numerator / numeric_denominator
  def numerator
    to_r.numerator
  end

  def denominator
    to_r.denominator
  end

  # rb_numeric_quo
  def quo(other)
    return fdiv(other) if ::Float === other
    x = self
    unless ::Rational === x
      x = to_r
      unless ::Rational === x
        ::Kernel.raise(::TypeError,
          "can't convert #{self.class} to Rational (#{self.class}#to_r gives #{__ir_conversion_result_name__(x)})")
      end
    end
    x / other
  end
end

class String
  # string_to_r: the leading part that reads as a rational, (0/1) when none does.
  def to_r
    ::Rational.__parse_string__(self, false)
  end
end
