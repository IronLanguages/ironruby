# BigDecimal utility library: #to_d on everything that can sensibly become a BigDecimal,
# plus BigDecimal#to_digits. Modelled on the bigdecimal 4.0 gem's lib/bigdecimal/util.rb.
#
# The 1.9 copy this replaces was written against the 1.8 API: it called BigDecimal.new,
# went through Float#to_s for String#to_d (so "45.67 degrees" blew up instead of parsing
# the leading number), and defined #to_d on neither Integer, BigDecimal nor nil.

require 'bigdecimal'

class Integer < Numeric
  # Returns the value of +int+ as a BigDecimal.
  #
  #   42.to_d   # => 0.42e2
  def to_d
    BigDecimal(self)
  end
end

class Float < Numeric
  # Returns the value of +float+ as a BigDecimal. +precision+ is the number of significant
  # digits to keep; 0 (the default) keeps as many as the Float's own decimal form has.
  #
  #   0.5.to_d      # => 0.5e0
  #   1.234.to_d(2) # => 0.12e1
  def to_d(precision = 0)
    BigDecimal(self, precision)
  end
end

class String
  # Returns the result of interpreting the *leading* characters of +str+ as a BigDecimal,
  # so unlike Kernel#BigDecimal this never raises - trailing garbage is ignored and a
  # string with no number in it at all is zero.
  #
  #   "0.5".to_d           # => 0.5e0
  #   "45.67 degrees".to_d # => 0.4567e2
  def to_d
    BigDecimal.interpret_loosely(self)
  end
end

class BigDecimal < Numeric
  # Converts a BigDecimal to a String of the form "nnnnnn.mmm".
  # This method is deprecated; use BigDecimal#to_s("F") instead.
  def to_digits
    if self.nan? || self.infinite? || self.zero?
      self.to_s
    else
      i       = self.to_i.to_s
      _,f,_,z = self.frac.split
      i + "." + ("0"*(-z)) + f
    end
  end

  # Returns self.
  def to_d
    self
  end
end

class Rational < Numeric
  # Returns the value as a BigDecimal rounded to +precision+ significant digits.
  #
  #   Rational(22, 7).to_d(3)   # => 0.314e1
  def to_d(precision = 0)
    BigDecimal(self, precision)
  end
end

class Complex < Numeric
  # Returns the value as a BigDecimal. Raises if the imaginary part is not zero.
  def to_d(precision = 0)
    BigDecimal(self) unless self.imag.zero? # to raise the error

    BigDecimal(self.real, precision)
  end
end

class NilClass
  # Returns nil represented as a BigDecimal.
  #
  #   nil.to_d   # => 0.0
  def to_d
    BigDecimal(0)
  end
end
