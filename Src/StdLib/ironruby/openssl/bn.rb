# OpenSSL::BN -- libcrypto's arbitrary-precision integer.
#
# Ruby already has the arithmetic, so a BN here is an Integer plus the
# conversions OpenSSL's BIGNUM offers: the binary (big-endian, unsigned)
# and MPI encodings that keys and ASN.1 integers are written in.

module OpenSSL
  class BNError < OpenSSLError; end unless const_defined?(:BNError, false)

  class BN
    include Comparable

    # value may be an Integer, another BN or a String in the given base:
    #   0   MPI: a 4-byte big-endian length, then the magnitude, sign in bit 7
    #   2   big-endian magnitude, unsigned
    #   10  decimal, optionally signed
    #   16  hexadecimal, optionally signed
    def initialize(value = 0, base = 10)
      case value
      when BN
        @value = value.to_i
      when Integer
        @value = value
      when String
        @value = BN.__parse(value, base)
      else
        str = String.try_convert(value)
        if str.nil?
          raise TypeError, "Cannot convert into OpenSSL::BN"
        end
        @value = BN.__parse(str, base)
      end
    end

    def self.__parse(str, base) # :nodoc:
      case base
      when 0
        raise BNError, "invalid MPI" if str.bytesize < 4
        length = str.byteslice(0, 4).unpack1("N")
        body = str.byteslice(4, length).to_s
        return 0 if body.empty?
        negative = (body.getbyte(0) & 0x80) != 0
        magnitude = ([body.getbyte(0) & 0x7F] + body.bytes.drop(1)).inject(0) { |a, b| (a << 8) | b }
        negative ? -magnitude : magnitude
      when 2
        str.each_byte.inject(0) { |a, b| (a << 8) | b }
      when 10
        raise BNError, "invalid decimal" unless str.match?(/\A[+-]?\d+\z/)
        Integer(str, 10)
      when 16
        raise BNError, "invalid hex" unless str.match?(/\A[+-]?\h+\z/)
        sign = str.start_with?("-") ? -1 : 1
        sign * Integer(str.delete("+-"), 16)
      else
        raise ArgumentError, "invalid radix #{base}"
      end
    end

    def self.rand(bits, fill = 0, odd = false)
      return BN.new(0) if bits <= 0
      value = OpenSSL::Random.random_bytes((bits + 7) / 8).each_byte.inject(0) { |a, b| (a << 8) | b }
      value &= (1 << bits) - 1
      case fill
      when 0 then value |= 1 << (bits - 1)
      when 1 then value |= 3 << (bits - 2) if bits >= 2
      end
      value |= 1 if odd
      BN.new(value)
    end

    class << self
      alias_method :pseudo_rand, :rand

      def rand_range(range)
        BN.new(Kernel.rand(BN.new(range).to_i))
      end
      alias_method :pseudo_rand_range, :rand_range
    end

    def to_i
      @value
    end
    alias_method :to_int, :to_i

    def to_bn
      self
    end

    def to_s(base = 10)
      case base
      when 0
        body = __magnitude_bytes
        body = "\x00".b + body if body.empty? || (body.getbyte(0) & 0x80) != 0
        body.setbyte(0, body.getbyte(0) | 0x80) if @value < 0
        [body.bytesize].pack("N") + body
      when 2
        __magnitude_bytes
      when 10
        @value.to_s(10)
      when 16
        hex = @value.abs.to_s(16).upcase
        hex = "0" + hex if hex.length.odd?
        (@value < 0 ? "-" : "") + hex
      else
        raise ArgumentError, "invalid radix #{base}"
      end
    end

    def inspect
      "#<#{self.class}:0x%08x>" % (object_id << 1)
    end

    def num_bytes
      __magnitude_bytes.bytesize
    end

    def num_bits
      @value == 0 ? 0 : @value.abs.bit_length
    end

    def zero?
      @value.zero?
    end

    def one?
      @value == 1
    end

    def odd?
      @value.odd?
    end

    def negative?
      @value.negative?
    end

    def <=>(other)
      other = BN.new(other) unless other.is_a?(BN)
      @value <=> other.to_i
    rescue TypeError
      nil
    end

    def ==(other)
      cmp = (self <=> other)
      !cmp.nil? && cmp.zero?
    end
    alias_method :eql?, :==

    def hash
      @value.hash
    end

    def coerce(other)
      [BN.new(other), self]
    end

    def +(other); BN.new(@value + BN.new(other).to_i); end
    def -(other); BN.new(@value - BN.new(other).to_i); end
    def *(other); BN.new(@value * BN.new(other).to_i); end
    def %(other); BN.new(@value % BN.new(other).to_i); end
    def **(other); BN.new(@value ** BN.new(other).to_i); end
    def <<(n); BN.new(@value << n); end
    def >>(n); BN.new(@value >> n); end
    def -@; BN.new(-@value); end

    # BN#/ is integer division with the remainder alongside it, like BN_div.
    def /(other)
      divisor = BN.new(other).to_i
      [BN.new(@value / divisor), BN.new(@value % divisor)]
    end

    def mod_exp(exponent, modulus)
      BN.new(@value.pow(BN.new(exponent).to_i, BN.new(modulus).to_i))
    end

    def mod_inverse(modulus)
      m = BN.new(modulus).to_i
      BN.new(@value.pow(-1, m))
    end

    def prime?(checks = nil)
      value = @value
      return false if value < 2
      return true if value < 4
      return false if value.even?
      # Miller-Rabin with the first twelve primes as bases, which is a proof for
      # everything below 3.3e24 and overwhelmingly likely above it.
      d = value - 1
      r = 0
      while d.even?
        d >>= 1
        r += 1
      end
      [2, 3, 5, 7, 11, 13, 17, 19, 23, 29, 31, 37].each do |a|
        next if a >= value
        x = a.pow(d, value)
        next if x == 1 || x == value - 1
        composite = true
        (r - 1).times do
          x = x.pow(2, value)
          if x == value - 1
            composite = false
            break
          end
        end
        return false if composite
      end
      true
    end

    private

    # The magnitude, big-endian, in as few bytes as it fits -- zero is no bytes
    # at all, which is what BN_bn2bin does.
    def __magnitude_bytes
      magnitude = @value.abs
      return "".b if magnitude.zero?
      hex = magnitude.to_s(16)
      hex = "0" + hex if hex.length.odd?
      [hex].pack("H*")
    end
  end
end

class Integer
  def to_bn
    OpenSSL::BN.new(self)
  end
end

class String
  def to_bn(base = 10)
    OpenSSL::BN.new(self, base)
  end
end
