# Complex, ported from Ruby 4.0's complex.c, together with the methods complex.c defines
# on Numeric, Float and String, and Kernel#Complex. Loaded by ruby4.rb as part of the
# core prelude, after rational.rb.
#
# A Complex is a real part in self.real and an imaginary part in self.imaginary (MRI's names, which
# its marshal format carries), frozen from birth. complex.c undefines Complex.new and
# Complex.allocate; instances are made by __raw__ (nucomp_s_new_internal).
#
# complex.c does its arithmetic through f_add, f_mul and friends rather than calling +
# and * directly: they skip an exact 0 or 1 operand, which is what keeps an exact zero
# from turning 0.0 into 0 and is part of what ruby/spec observes. They are the
# __f_<name>__ helpers below.

class Complex < Numeric
  class << self
    undef_method :allocate
    undef_method :new
  end

  # A Complex's parts are fields of the C# RubyComplex (Src/Libraries/Builtins/RubyComplex.cs),
  # read by #real and #imaginary; __raw__ (nucomp_s_new_internal: no canonicalisation, no
  # checks, frozen) and the __ir_set_parts__ #marshal_load uses are C# too.

  # --- complex.c's arithmetic helpers ----------------------------------------

  # FIXNUM_ZERO_P / `y == ONE`: an Integer 0 or 1, not 0.0 nor (0/1).
  def self.__fix_zero__(x)
    ::Integer === x && x == 0
  end

  def self.__fix_one__(x)
    ::Integer === x && x == 1
  end

  def self.__f_add__(x, y)
    if ::Integer === x
      return y if x == 0
      return x if __fix_zero__(y)
    elsif ::Float === x || ::Rational === x
      return x if __fix_zero__(y)
    end
    x + y
  end

  def self.__f_sub__(x, y)
    return x if __fix_zero__(y)
    x - y
  end

  def self.__f_mul__(x, y)
    if ::Integer === x
      return 0 if __fix_zero__(y)
      return 0 if x == 0 && ::Integer === y
      return y if x == 1
      return x if __fix_one__(y)
    else
      return x if __fix_one__(y)
    end
    x * y
  end

  def self.__f_div__(x, y)
    return x if __fix_one__(y)
    x / y
  end

  def self.__f_negate__(x)
    -x
  end

  # f_quo: Integer and Rational go through rb_numeric_quo, which hands a Float divisor to
  # #fdiv - and Rational#fdiv divides by +0.0 for any zero, so (1/2)/-0.0 is +Infinity.
  def self.__f_quo__(x, y)
    if ::Integer === x || ::Rational === x
      return x.fdiv(y) if ::Float === y
      return (::Rational === x ? x : x.to_r) / y
    end
    return x / y if ::Float === x
    x.quo(y)
  end

  def self.__f_fdiv__(x, y)
    x.fdiv(y)
  end

  # f_zero_p: 0.0 counts here, unlike k_exact_zero_p.
  def self.__zero__(x)
    case x
    when ::Float then x == 0.0
    when ::Integer then x == 0
    when ::Rational then x.numerator == 0
    else x == 0 ? true : false
    end
  end

  # k_exact_zero_p: an exact zero, so 0.0 does not qualify.
  def self.__exact_zero__(x)
    !(::Float === x) && __zero__(x)
  end

  # f_real_p: a Complex is real here when its imaginary part is any zero.
  def self.__real_p__(x)
    case x
    when ::Integer, ::Float, ::Rational then true
    when ::Complex then __zero__(x.imaginary)
    else x.real? ? true : false
    end
  end

  # f_negative_p / f_signbit
  def self.__negative__(x)
    case x
    when ::Integer, ::Float then x < 0
    when ::Rational then x.numerator < 0
    else ::Numeric.__ir_negative_p__(x)
    end
  end

  def self.__signbit__(x)
    return !x.nan? && (x < 0.0 || (x == 0.0 && (1.0 / x) < 0)) if ::Float === x
    __negative__(x)
  end

  # f_eqeq_p
  def self.__eqeq__(x, y)
    if ::Float === x || ::Float === y
      return ::Kernel.Float(x) == ::Kernel.Float(y) if ::Numeric === x && ::Numeric === y
    end
    x == y ? true : false
  end

  # f_finite_p / f_infinite_p
  def self.__finite__(x)
    return true if ::Integer === x || ::Rational === x
    return x.finite? if ::Float === x
    x.finite? ? true : false
  end

  def self.__infinite__(x)
    return false if ::Integer === x || ::Rational === x
    return !x.infinite?.nil? if ::Float === x
    x.infinite? ? true : false
  end

  # f_arg: Numeric#arg for the exact reals, Float#arg for a Float.
  def self.__arg__(x)
    x.arg
  end

  # f_numerator / f_denominator
  def self.__numerator__(x)
    return x.numerator if ::Rational === x || ::Float === x
    x
  end

  def self.__denominator__(x)
    return x.denominator if ::Rational === x || ::Float === x
    1
  end

  # --- construction ------------------------------------------------------------

  # nucomp_real_check: answers the value to use, a real Complex standing for its real part.
  def self.__real_check__(num)
    unless ::Integer === num || ::Float === num || ::Rational === num
      if ::Complex === num && __zero__(num.imaginary)
        return num.real
      end
      unless ::Numeric === num && __real_p__(num)
        ::Kernel.raise(::TypeError, "not a real")
      end
    end
    num
  end

  # nucomp_s_canonicalize_internal: a Complex in either position folds in.
  def self.__canonicalize__(real, imag)
    cr = ::Complex === real
    ci = ::Complex === imag
    if !cr && !ci
      __raw__(real, imag)
    elsif !cr
      __raw__(__f_sub__(real, imag.imaginary), __f_add__(0, imag.real))
    elsif !ci
      __raw__(real.real, __f_add__(real.imaginary, imag))
    else
      __raw__(__f_sub__(real.real, imag.imaginary), __f_add__(real.imaginary, imag.real))
    end
  end

  # f_complex_new2
  def self.__new2__(x, y)
    if ::Complex === x
      y = __f_add__(x.imaginary, y)
      x = x.real
    end
    __canonicalize__(x, y)
  end

  # nucomp_s_new
  def self.rectangular(*args)
    if args.size < 1 || args.size > 2
      ::Kernel.raise(::ArgumentError, "wrong number of arguments (given #{args.size}, expected 1..2)")
    end
    real = __real_check__(args[0])
    imag = args.size == 2 ? __real_check__(args[1]) : 0
    __raw__(real, imag)
  end

  # nucomp_s_polar
  def self.polar(*args)
    if args.size < 1 || args.size > 2
      ::Kernel.raise(::ArgumentError, "wrong number of arguments (given #{args.size}, expected 1..2)")
    end
    abs = __real_check__(args[0])
    arg = args.size == 2 ? __real_check__(args[1]) : 0
    __polar_real__(abs, arg)
  end

  class << self
    alias_method :rect, :rectangular
  end

  HALF_PI__ = ::Math::PI / 2
  THREE_HALVES_PI__ = ::Math::PI / 2 + ::Math::PI
  __hide_constant__ :HALF_PI__, :THREE_HALVES_PI__

  # f_complex_polar_real
  def self.__polar_real__(x, y)
    if __zero__(x) || __zero__(y)
      return __raw__(x, 0.0)
    end
    if ::Float === y
      if y == ::Math::PI
        x = __f_negate__(x)
        y = 0.0
      elsif y == HALF_PI__
        y = x
        x = 0.0
      elsif y == THREE_HALVES_PI__
        y = __f_negate__(x)
        x = 0.0
      elsif ::Float === x
        real = x * ::Math.cos(y)
        imag = x * ::Math.sin(y)
        x = real
        y = imag
      else
        ax = ::Math.sin(y)
        ay = ::Math.cos(y)
        y = __f_mul__(x, ax)
        x = __f_mul__(x, ay)
      end
      return __raw__(x, y)
    end
    __canonicalize__(__f_mul__(x, ::Math.cos(y)), __f_mul__(x, ::Math.sin(y)))
  end

  # f_complex_polar
  def self.__polar__(x, y)
    __polar_real__(__real_check__(x), __real_check__(y))
  end

  # modf: [fractional part, integral part], both carrying the sign of x.
  def self.__modf__(x)
    return [x, x] if x.nan?
    return [x < 0 ? -0.0 : 0.0, x] if x.infinite?
    i = x.truncate.to_f
    [x - i, i]
  end

  # rb_dbl_complex_new_polar_pi: abs at an angle of ang * pi, exact at the half turns.
  def self.__polar_pi__(abs, ang)
    fr, fi = __modf__(ang)
    pos = fr == 0.5
    if pos || fr == -0.5
      abs = -abs if (__modf__(fi / 2.0)[0] != fr) ^ pos
      __canonicalize__(0.0, abs)
    elsif fr == 0.0
      abs = -abs if __modf__(fi / 2.0)[0] != 0.0
      abs
    else
      __canonicalize__(abs * __cospi__(ang), abs * __sinpi__(ang))
    end
  end

  # cospi / sinpi, exact at the whole and half turns as glibc's are.
  def self.__cospi__(w)
    return ::Float::NAN unless w.finite?
    return (w.to_i.even? ? 1.0 : -1.0) if w == w.floor
    return 0.0 if (w * 2) == (w * 2).floor
    ::Math.cos(w * ::Math::PI)
  end

  def self.__sinpi__(w)
    return ::Float::NAN unless w.finite?
    return 0.0 if w == w.floor
    return ((w - 0.5).to_i.even? ? 1.0 : -1.0) if (w * 2) == (w * 2).floor
    ::Math.sin(w * ::Math::PI)
  end

  I = __raw__(0, 1)

  # complex.c removes the real-number protocol rather than inheriting Numeric's:
  # `Complex(1) < 2` is a NoMethodError, not a comparison of magnitudes. The undefinition
  # is an entry in Complex's own table, so it also hides what ruby4.rb adds to Numeric
  # later; a name nothing defines yet is given a definition here to undefine.
  # Comparable's #== is the exception: Complex has its own, which is C# (RubyComplex) and so
  # already in the table.
  ((::Comparable.instance_methods(false) + ::Comparable.private_instance_methods(false) - [:==]) +
   %i[% div divmod floor ceil modulo remainder round step truncate i positive? negative?]).each do |m|
    define_method(m) { |*| } unless method_defined?(m) || private_method_defined?(m)
    undef_method(m)
  end

  # --- instance methods --------------------------------------------------------
  #
  # +, -, *, /, quo, ==, #hash and #abs are C# (RubyComplex) for Integer and Float parts and
  # operands; the __ir_*_slow__ methods below are the rest of each, which C# calls.


  # rb_complex_uminus
  def -@
    ::Complex.__new2__(-self.real, -self.imaginary)
  end

  # rb_complex_plus
  def __ir_add_slow__(other)
    if ::Complex === other
      return ::Complex.__new2__(::Complex.__f_add__(self.real, other.real), ::Complex.__f_add__(self.imaginary, other.imaginary))
    end
    if ::Numeric === other && ::Complex.__real_p__(other)
      return ::Complex.__new2__(::Complex.__f_add__(self.real, other), self.imaginary)
    end
    __ir_num_coerce_bin__(other, :+)
  end

  # rb_complex_minus
  def __ir_sub_slow__(other)
    if ::Complex === other
      return ::Complex.__new2__(::Complex.__f_sub__(self.real, other.real), ::Complex.__f_sub__(self.imaginary, other.imaginary))
    end
    if ::Numeric === other && ::Complex.__real_p__(other)
      return ::Complex.__new2__(::Complex.__f_sub__(self.real, other), self.imaginary)
    end
    __ir_num_coerce_bin__(other, :-)
  end

  # safe_mul: a non-NaN Float multiplied by an exact zero is reduced to its sign, so
  # that Infinity * 0 contributes a signed zero rather than a NaN.
  def self.__safe_mul__(a, b, az, bz)
    if !az && bz && ::Float === a && !a.nan?
      a = __signbit__(a) ? -1.0 : 1.0
    end
    if !bz && az && ::Float === b && !b.nan?
      b = __signbit__(b) ? -1.0 : 1.0
    end
    __f_mul__(a, b)
  end

  # comp_mul
  def self.__comp_mul__(areal, aimag, breal, bimag)
    arzero = __zero__(areal)
    aizero = __zero__(aimag)
    brzero = __zero__(breal)
    bizero = __zero__(bimag)
    real = __f_sub__(__safe_mul__(areal, breal, arzero, brzero),
                     __safe_mul__(aimag, bimag, aizero, bizero))
    imag = __f_add__(__safe_mul__(areal, bimag, arzero, bizero),
                     __safe_mul__(aimag, breal, aizero, brzero))
    [real, imag]
  end

  # rb_complex_mul
  def __ir_mul_slow__(other)
    if ::Complex === other
      real, imag = ::Complex.__comp_mul__(self.real, self.imaginary, other.real, other.imaginary)
      return ::Complex.__new2__(real, imag)
    end
    if ::Numeric === other && ::Complex.__real_p__(other)
      return ::Complex.__new2__(::Complex.__f_mul__(self.real, other), ::Complex.__f_mul__(self.imaginary, other))
    end
    __ir_num_coerce_bin__(other, :*)
  end

  # f_divide
  def __ir_divide__(other, func, id)
    c = ::Complex
    if ::Complex === other
      ar = self.real
      ai = self.imaginary
      br = other.real
      bi = other.imaginary
      flo = ::Float === ar || ::Float === ai || ::Float === br || ::Float === bi
      if br.abs > bi.abs
        r = c.__send__(func, bi, br)
        n = c.__f_mul__(br, c.__f_add__(1, c.__f_mul__(r, r)))
        x = c.__send__(func, c.__f_add__(ar, c.__f_mul__(ai, r)), n)
        y = c.__send__(func, c.__f_sub__(ai, c.__f_mul__(ar, r)), n)
      else
        r = c.__send__(func, br, bi)
        n = c.__f_mul__(bi, c.__f_add__(1, c.__f_mul__(r, r)))
        x = c.__send__(func, c.__f_add__(c.__f_mul__(ar, r), ai), n)
        y = c.__send__(func, c.__f_sub__(c.__f_mul__(ai, r), ar), n)
      end
      unless flo
        x = ::Rational.__canonicalize_value__(x)
        y = ::Rational.__canonicalize_value__(y)
      end
      return c.__new2__(x, y)
    end
    if ::Numeric === other && c.__real_p__(other)
      x = ::Rational.__canonicalize_value__(c.__send__(func, self.real, other))
      y = ::Rational.__canonicalize_value__(c.__send__(func, self.imaginary, other))
      return c.__new2__(x, y)
    end
    __ir_num_coerce_bin__(other, id)
  end
  private :__ir_divide__

  # rb_complex_div
  def __ir_div_slow__(other)
    __ir_divide__(other, :__f_quo__, :quo)
  end

  # nucomp_fdiv
  def fdiv(other)
    __ir_divide__(other, :__f_fdiv__, :fdiv)
  end

  # zero_for
  def self.__zero_for__(x)
    return 0.0 if ::Float === x
    return ::Rational.__new_internal__(0, 1) if ::Rational === x
    0
  end

  SPECIAL_DIRS__ = [[1, 0], [1, 1], [0, 1], [-1, 1], [-1, 0], [-1, -1], [0, -1], [1, -1]].freeze
  __hide_constant__ :SPECIAL_DIRS__

  # complex_pow_for_special_angle: on an axis or a diagonal an Integer power can be
  # worked out exactly. Answers nil when self is on neither.
  def __ir_pow_special_angle__(other)
    return nil unless ::Integer === other
    c = ::Complex
    x = nil
    if c.__zero__(self.imaginary)
      x = self.real
      dir = 0
    elsif c.__zero__(self.real)
      x = self.imaginary
      dir = 2
    elsif c.__eqeq__(self.real, self.imaginary)
      x = self.real
      dir = 1
    elsif c.__eqeq__(self.real, c.__f_negate__(self.imaginary))
      x = self.imaginary
      dir = 3
    else
      dir = 0
    end
    return nil if x.nil?

    if c.__negative__(x)
      x = c.__f_negate__(x)
      dir += 4
    end

    if dir.even?
      zx = x ** other
    else
      zx = (2 * x * x) ** other.div(2)
      zx = zx * x if other.odd?
    end
    z_dir = (dir * other) % 8
    dr, di = SPECIAL_DIRS__[z_dir]
    zr = dr == 0 ? c.__zero_for__(zx) : (dr == 1 ? zx : c.__f_negate__(zx))
    zi = di == 0 ? c.__zero_for__(zx) : (di == 1 ? zx : c.__f_negate__(zx))
    c.__raw__(zr, zi)
  end
  private :__ir_pow_special_angle__

  FIXNUM_RANGE__ = (-2**62)..(2**62 - 1)
  __hide_constant__ :FIXNUM_RANGE__

  # rb_complex_pow
  def **(other)
    c = ::Complex
    if ::Numeric === other && c.__exact_zero__(other)
      return c.__raw__(1, 0)
    end
    other = other.numerator if ::Rational === other && other.denominator == 1
    if ::Complex === other && c.__exact_zero__(other.imaginary)
      other = other.real
    end
    if ::Integer === other && other == 1
      return c.__raw__(self.real, self.imaginary)
    end

    result = __ir_pow_special_angle__(other)
    return result unless result.nil?

    if ::Complex === other
      r = abs
      theta = arg
      log_r = ::Math.log(r)
      nr = ::Math.exp(c.__f_sub__(c.__f_mul__(other.real, log_r), c.__f_mul__(other.imaginary, theta)))
      ntheta = c.__f_add__(c.__f_mul__(theta, other.real), c.__f_mul__(other.imaginary, log_r))
      return c.__polar__(nr, ntheta)
    end

    if ::Integer === other && FIXNUM_RANGE__.cover?(other)
      n = other
      return c.__raw__(1, 0) if n == 0
      x = self
      if n < 0
        x = 1.quo(x)
        n = -n
      end
      xr = x.real
      xi = x.imaginary
      zr = xr
      zi = xi
      if c.__zero__(xi)
        zr = zr ** n
      elsif c.__zero__(xr)
        zi = zi ** n
        zi = c.__f_negate__(zi) if n & 2 != 0
        zr, zi = zi, zr if n & 1 == 0
      else
        n -= 1
        while n != 0
          while n.even?
            tmp = c.__f_sub__(c.__f_mul__(xr, xr), c.__f_mul__(xi, xi))
            xi = c.__f_mul__(c.__f_mul__(2, xr), xi)
            xr = tmp
            n = n.div(2)
          end
          zr, zi = c.__comp_mul__(zr, zi, xr, xi)
          n -= 1
        end
      end
      return c.__raw__(zr, zi)
    end

    if ::Numeric === other && c.__real_p__(other)
      ::Kernel.warn("in a**b, b may be too big", uplevel: 0) if ::Integer === other
      r = abs
      theta = arg
      return c.__polar__(r ** other, c.__f_mul__(theta, other))
    end
    __ir_num_coerce_bin__(other, :**)
  end

  # nucomp_eqeq_p
  def __ir_eqeq_slow__(other)
    c = ::Complex
    if ::Complex === other
      return c.__eqeq__(self.real, other.real) && c.__eqeq__(self.imaginary, other.imaginary)
    end
    if ::Numeric === other && c.__real_p__(other)
      return c.__eqeq__(self.real, other) && c.__zero__(self.imaginary)
    end
    c.__eqeq__(other, self)
  end

  # nucomp_cmp
  def <=>(other)
    c = ::Complex
    return __ir_num_coerce_cmp__(other, :<=>) unless ::Numeric === other
    return nil unless c.__zero__(self.imaginary)
    if ::Complex === other
      return self.real <=> other.real if c.__zero__(other.imaginary)
    elsif c.__real_p__(other)
      return self.real <=> other
    else
      return self.real.__send__(:__ir_num_coerce_cmp__, other, :<=>)
    end
    nil
  end

  # nucomp_coerce
  def coerce(other)
    return [other, self] if ::Complex === other
    if ::Numeric === other && ::Complex.__real_p__(other)
      return [::Complex.__raw__(other, 0), self]
    end
    ::Kernel.raise(::TypeError, "#{other.class} can't be coerced into #{self.class}")
  end

  # rb_complex_abs
  def __ir_abs_slow__
    c = ::Complex
    if c.__zero__(self.real)
      a = self.imaginary.abs
      a = a.to_f if ::Float === self.real && !(::Float === self.imaginary)
      return a
    end
    if c.__zero__(self.imaginary)
      a = self.real.abs
      a = a.to_f if !(::Float === self.real) && ::Float === self.imaginary
      return a
    end
    ::Math.hypot(self.real, self.imaginary)
  end

  # nucomp_abs2
  def abs2
    c = ::Complex
    c.__f_add__(c.__f_mul__(self.real, self.real), c.__f_mul__(self.imaginary, self.imaginary))
  end

  # rb_complex_arg
  def arg
    ::Math.atan2(self.imaginary, self.real)
  end
  alias angle arg
  alias phase arg

  def rectangular
    [self.real, self.imaginary]
  end
  alias rect rectangular

  def polar
    [abs, arg]
  end

  # rb_complex_conjugate
  def conjugate
    ::Complex.__new2__(self.real, -self.imaginary)
  end
  alias conj conjugate

  def real?
    false
  end

  # nucomp_denominator / nucomp_numerator
  def denominator
    ::Complex.__denominator__(self.real).lcm(::Complex.__denominator__(self.imaginary))
  end

  def numerator
    c = ::Complex
    cd = denominator
    c.__new2__(c.__f_mul__(c.__numerator__(self.real), c.__f_div__(cd, c.__denominator__(self.real))),
               c.__f_mul__(c.__numerator__(self.imaginary), c.__f_div__(cd, c.__denominator__(self.imaginary))))
  end

  # nucomp_hash
  def __ir_hash_slow__
    [self.real, self.imaginary, ::Complex].hash
  end

  # nucomp_eql_p
  def eql?(other)
    return false unless ::Complex === other
    self.real.class == other.real.class && self.imaginary.class == other.imaginary.class && self == other
  end

  # f_format: the imaginary part's sign, its magnitude, then "i" - with a "*" in front
  # when the magnitude does not end in a digit, as in "(3/4)*i" or "Infinity*i".
  def __ir_format__(s, inspecting)
    impos = !::Complex.__signbit__(self.imaginary)
    s << (inspecting ? self.real.inspect : ::Kernel.String(self.real))
    s << (impos ? "+" : "-")
    mag = self.imaginary.abs
    s << (inspecting ? mag.inspect : ::Kernel.String(mag))
    s << "*" unless s.end_with?("0", "1", "2", "3", "4", "5", "6", "7", "8", "9")
    s << "i"
  end
  private :__ir_format__

  def to_s
    __ir_format__(+"", false)
  end

  def inspect
    __ir_format__(+"(", true) << ")"
  end

  # rb_complex_finite_p / rb_complex_infinite_p
  def finite?
    ::Complex.__finite__(self.real) && ::Complex.__finite__(self.imaginary)
  end

  def infinite?
    return nil if !::Complex.__infinite__(self.real) && !::Complex.__infinite__(self.imaginary)
    1
  end

  # nucomp_to_i / nucomp_to_f / nucomp_to_r / nucomp_rationalize
  def to_i
    unless ::Complex.__exact_zero__(self.imaginary)
      ::Kernel.raise(::RangeError, "can't convert #{self} into Integer")
    end
    self.real.to_i
  end

  def to_f
    unless ::Complex.__exact_zero__(self.imaginary)
      ::Kernel.raise(::RangeError, "can't convert #{self} into Float")
    end
    self.real.to_f
  end

  def to_r
    if ::Float === self.imaginary && self.imaginary == 0.0
      # a Float zero imaginary part converts as well as an exact one
    elsif !::Complex.__exact_zero__(self.imaginary)
      imag = ::Rational.__check_to_rational__(self.imaginary) rescue nil
      if imag.nil? || !(::Rational === imag) || !::Complex.__exact_zero__(imag)
        ::Kernel.raise(::RangeError, "can't convert #{self} into Rational")
      end
    end
    self.real.to_r
  end

  def rationalize(*args)
    if args.size > 1
      ::Kernel.raise(::ArgumentError, "wrong number of arguments (given #{args.size}, expected 0..1)")
    end
    unless ::Complex.__exact_zero__(self.imaginary)
      ::Kernel.raise(::RangeError, "can't convert #{self} into Rational")
    end
    self.real.rationalize(*args)
  end

  def to_c
    self
  end

  # nucomp_marshal_dump / nucomp_marshal_load (by way of Complex::compatible in MRI).
  def marshal_dump
    [self.real, self.imaginary]
  end
  private :marshal_dump

  def marshal_load(a)
    ::Kernel.raise(::TypeError, "wrong argument type #{a.class} (expected Array)") unless ::Array === a
    if a.size != 2
      ::Kernel.raise(::ArgumentError, "marshaled complex must have an array whose length is 2 but #{a.size}")
    end
    __ir_set_parts__(a[0], a[1])
  end
  private :marshal_load

  # --- conversion: nucomp_convert and the string scanner ------------------------

  # complex.c's str2num: a "/" makes it a Rational, a "." or an exponent a Float,
  # otherwise a decimal Integer. An empty text is 0, as strtol would give.
  def self.__str2num__(text)
    return text unless ::String === text
    return ::Rational.__parse_string__(text, false) if text.include?("/")
    return text.to_f if text.include?(".") || text.include?("e") || text.include?("E")
    text.to_i
  end

  # complex.c's parse_comp: [complete?, value]. The scan is C# (Complex.__ir_scan__): it
  # answers the texts str2num reads - the characters read with the underscores taken out
  # - and what they make. Its read_digits backs up over a trailing or doubled underscore,
  # and where it backs up to decides whether the strict caller (Kernel#Complex) sees
  # trailing garbage: "7__9".to_c is (7+0i) and Complex("7__9") an ArgumentError.
  # The parts are real (an Integer, a Float or a Rational), so f_complex_new2 is __raw__.
  # A scan whose parts were plain Integers answers [complete?, value] itself.
  def self.__scan__(str, strict)
    result = __ir_scan__(str, strict)
    return result if result.size == 2
    kind = result[1]
    x = __str2num__(result[2])
    result[1] = case kind
                when 0 then __raw__(x, 0)
                when 1 then __raw__(0, x)
                when 2 then __raw__(x, __str2num__(result[3]))
                else __polar__(x, __str2num__(result[3]))
                end
    result
  end

  # string_to_c_strict / string_to_c
  def self.__parse_string__(str, strict, raise_error = true)
    ::Rational.__must_ascii_compatible__(str)
    if strict
      if str.include?("\0")
        return nil unless raise_error
        ::Kernel.raise(::ArgumentError, "string contains null byte")
      end
      ok, value = __scan__(str, true)
      unless ok
        return nil unless raise_error
        ::Kernel.raise(::ArgumentError, "invalid value for convert(): #{str.inspect}")
      end
      value
    else
      __scan__(str, false)[1]
    end
  end

  # to_complex: rb_convert_type(val, T_COMPLEX, "Complex", "to_c")
  def self.__to_complex__(value)
    unless __ir_responds_to__(value, :to_c)
      ::Kernel.raise(::TypeError, "can't convert #{nil.equal?(value) ? 'nil' : __ir_class_of__(value)} into Complex")
    end
    result = value.to_c
    unless ::Complex === result
      name = __ir_class_of__(value)
      ::Kernel.raise(::TypeError,
        "can't convert #{name} to Complex (#{name}#to_c gives #{__ir_conversion_result_name__(result)})")
    end
    result
  end

  # nucomp_convert. `given2` tells Complex(x) from Complex(x, nil).
  def self.__convert__(a1, a2, given2, raise_)
    if nil.equal?(a1) || (given2 && nil.equal?(a2))
      return nil unless raise_
      ::Kernel.raise(::TypeError, "can't convert nil into Complex")
    end

    if ::String === a1
      a1 = __parse_string__(a1, true, raise_)
      return nil if a1.nil?
    end
    if given2 && ::String === a2
      a2 = __parse_string__(a2, true, raise_)
      return nil if a2.nil?
    end

    a1 = a1.real if ::Complex === a1 && __exact_zero__(a1.imaginary)
    a2 = a2.real if given2 && ::Complex === a2 && __exact_zero__(a2.imaginary)

    if ::Complex === a1
      return a1 if !given2 || __exact_zero__(a2)
    end

    if !given2
      return a1 if ::Numeric === a1 && !__real_p__(a1)
      unless ::Numeric === a1
        return __to_complex__(a1) if raise_
        begin
          return __to_complex__(a1)
        rescue ::Exception
          return nil
        end
      end
    elsif ::Numeric === a1 && ::Numeric === a2 && (!__real_p__(a1) || !__real_p__(a2))
      return __f_add__(a1, __f_mul__(a2, __raw__(0, 1)))
    end

    if given2
      # With exception: false a non-real *second* argument is swallowed; a non-real
      # first argument still raises, which is MRI's asymmetry.
      unless raise_ || ::Integer === a2 || ::Float === a2 || ::Rational === a2
        return nil
      end
      rectangular(a1, a2)
    else
      rectangular(a1)
    end
  end

  # nucomp_s_convert
  def self.convert(*args)
    if args.size < 1 || args.size > 2
      ::Kernel.raise(::ArgumentError, "wrong number of arguments (given #{args.size}, expected 1..2)")
    end
    __convert__(args[0], args[1], args.size == 2, true)
  end
  private_class_method :convert
end

module Kernel
  # nucomp_f_complex. Complex(1) is (1+0i) while Complex(1, nil) is a TypeError, so the
  # arity is read off *args rather than from a default.
  # Kernel#Complex is C# (KernelOps.ToComplex) for Integers and Floats; the rest comes here.
  def __ir_Complex_slow__(*args, exception: true)
    if args.size < 1 || args.size > 2
      ::Kernel.raise(::ArgumentError, "wrong number of arguments (given #{args.size}, expected 1..2)")
    end
    raise_ = ::Kernel.__opts_exception__(exception)
    return args[0] if args.size == 1 && ::Complex.equal?(::Kernel.instance_method(:class).bind(args[0]).call)
    ::Complex.__convert__(args[0], args[1], args.size == 2, raise_)
  end
  module_function :__ir_Complex_slow__
end

class Numeric
  # numeric_to_c
  def to_c
    ::Complex.__canonicalize__(self, 0)
  end

  # num_imaginary (numeric.c)
  def i
    ::Complex.__canonicalize__(0, self)
  end

  # numeric_abs2 / numeric_arg / numeric_rect / numeric_polar
  def abs2
    ::Complex.__f_mul__(self, self)
  end

  def arg
    ::Complex.__negative__(self) ? ::Math::PI : 0
  end
  alias angle arg
  alias phase arg

  def rectangular
    [self, 0]
  end
  alias rect rectangular

  def polar
    [abs, arg]
  end
end

class Float
  # float_arg: by the sign bit, so -0.0 points the other way; a NaN points nowhere.
  def arg
    return self if nan?
    ::Complex.__signbit__(self) ? ::Math::PI : 0
  end
  alias angle arg
  alias phase arg
end

# String#to_c is C# (MutableStringOps.ToComplex), which comes to Complex.__parse_string__
# for a number with a Float, Rational or polar part.

class Complex
  private :__ir_add_slow__, :__ir_sub_slow__, :__ir_mul_slow__, :__ir_div_slow__,
          :__ir_eqeq_slow__, :__ir_hash_slow__, :__ir_abs_slow__
end
