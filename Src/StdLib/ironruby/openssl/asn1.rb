# OpenSSL::ASN1 -- DER encoding and decoding.
#
# .NET has System.Formats.Asn1, but its reader is a pull parser over a schema
# rather than the tree of tagged values MRI hands back, so DER is read and
# written here directly: it is a small, self-describing format and this way the
# object model is exactly MRI's.

module OpenSSL
  module ASN1
    class ASN1Error < OpenSSLError; end

    UNIVERSAL_TAG_NAME = [] # :nodoc:

    class ASN1Data
      attr_accessor :value, :tag, :tag_class, :indefinite_length

      def initialize(value, tag, tag_class = :CONTEXT_SPECIFIC)
        unless tag_class.is_a?(Symbol)
          raise ASN1Error, "invalid tag class"
        end
        @value = value
        @tag = tag
        @tag_class = tag_class
        @indefinite_length = false
      end

      def infinite_length
        @indefinite_length
      end

      def infinite_length=(value)
        @indefinite_length = value
      end

      def to_der
        ASN1.__encode(__identifier, __content)
      end

      # The bytes between the length and the end of this value.
      def __content # :nodoc:
        case @value
        when Array then @value.map { |element| ASN1.__to_der(element) }.join
        when nil then "".b
        else String.try_convert(@value) ? @value.to_s.b : ASN1.__to_der(@value)
        end
      end

      def __constructed? # :nodoc:
        @value.is_a?(Array)
      end

      def __identifier # :nodoc:
        klass = case @tag_class
                when :UNIVERSAL then 0x00
                when :APPLICATION then 0x40
                when :CONTEXT_SPECIFIC then 0x80
                when :PRIVATE then 0xC0
                else raise ASN1Error, "invalid tag class"
                end
        klass | (__constructed? ? 0x20 : 0x00) | @tag
      end
    end

    class Primitive < ASN1Data
      attr_accessor :tagging

      def initialize(value, tag = nil, tagging = nil, tag_class = nil)
        tag ||= self.class::TAG
        tag_class ||= tagging ? :CONTEXT_SPECIFIC : :UNIVERSAL
        super(value, tag, tag_class)
        @tagging = tagging
      end

      def __constructed? # :nodoc:
        false
      end
    end

    class Constructive < ASN1Data
      include Enumerable
      attr_accessor :tagging

      def initialize(value, tag = nil, tagging = nil, tag_class = nil)
        tag ||= self.class::TAG
        tag_class ||= tagging ? :CONTEXT_SPECIFIC : :UNIVERSAL
        super(value, tag, tag_class)
        @tagging = tagging
      end

      def each(&block)
        @value.each(&block)
        self
      end

      def __constructed? # :nodoc:
        true
      end
    end

    # A universal type: the class name, its tag, and how its value turns into
    # (and comes back from) the content bytes.
    def self.__define(name, tag, kind) # :nodoc:
      base = kind == :constructive ? Constructive : Primitive
      klass = ::Class.new(base)
      klass.const_set(:TAG, tag)
      const_set(name, klass)
      UNIVERSAL_TAG_NAME[tag] = name
      klass
    end

    __define(:EndOfContent, 0, :primitive)
    __define(:Boolean, 1, :primitive)
    __define(:Integer, 2, :primitive)
    __define(:BitString, 3, :primitive)
    __define(:OctetString, 4, :primitive)
    __define(:Null, 5, :primitive)
    __define(:ObjectId, 6, :primitive)
    __define(:Enumerated, 10, :primitive)
    __define(:UTF8String, 12, :primitive)
    __define(:Sequence, 16, :constructive)
    __define(:Set, 17, :constructive)
    __define(:NumericString, 18, :primitive)
    __define(:PrintableString, 19, :primitive)
    __define(:T61String, 20, :primitive)
    __define(:VideotexString, 21, :primitive)
    __define(:IA5String, 22, :primitive)
    __define(:UTCTime, 23, :primitive)
    __define(:GeneralizedTime, 24, :primitive)
    __define(:GraphicString, 25, :primitive)
    __define(:ISO64String, 26, :primitive)
    __define(:GeneralString, 27, :primitive)
    __define(:UniversalString, 28, :primitive)
    __define(:BMPString, 30, :primitive)

    class Integer
      def __content # :nodoc:
        value = @value.is_a?(OpenSSL::BN) ? @value.to_i : Kernel.Integer(@value)
        ASN1.__integer_bytes(value)
      end
    end

    class Enumerated
      def __content # :nodoc:
        value = @value.is_a?(OpenSSL::BN) ? @value.to_i : Kernel.Integer(@value)
        ASN1.__integer_bytes(value)
      end
    end

    class Boolean
      def __content # :nodoc:
        (@value ? "\xFF" : "\x00").b
      end
    end

    class Null
      def __content # :nodoc:
        "".b
      end
    end

    class BitString
      attr_accessor :unused_bits

      def __content # :nodoc:
        ((@unused_bits || 0).chr + @value.to_s).b
      end
    end

    class ObjectId
      # OpenSSL keeps a table of the object identifiers it knows and reports a
      # decoded one by its short name; this is the part of that table that turns
      # up in certificates and keys.  [short name, long name] by dotted OID.
      NAMES = { # :nodoc:
        "1.2.840.113549.1.1.1" => ["rsaEncryption", "rsaEncryption"],
        "1.2.840.113549.1.1.5" => ["RSA-SHA1", "sha1WithRSAEncryption"],
        "1.2.840.113549.1.1.10" => ["RSASSA-PSS", "rsassaPss"],
        "1.2.840.113549.1.1.11" => ["RSA-SHA256", "sha256WithRSAEncryption"],
        "1.2.840.113549.1.1.12" => ["RSA-SHA384", "sha384WithRSAEncryption"],
        "1.2.840.113549.1.1.13" => ["RSA-SHA512", "sha512WithRSAEncryption"],
        "1.2.840.10040.4.1" => ["DSA", "dsaEncryption"],
        "1.2.840.10040.4.3" => ["DSA-SHA1", "dsaWithSHA1"],
        "1.2.840.10045.2.1" => ["id-ecPublicKey", "id-ecPublicKey"],
        "1.2.840.10045.4.3.2" => ["ecdsa-with-SHA256", "ecdsa-with-SHA256"],
        "1.2.840.10045.4.3.3" => ["ecdsa-with-SHA384", "ecdsa-with-SHA384"],
        "1.2.840.10045.4.3.4" => ["ecdsa-with-SHA512", "ecdsa-with-SHA512"],
        "1.2.840.10045.3.1.1" => ["prime192v1", "prime192v1"],
        "1.2.840.10045.3.1.7" => ["prime256v1", "prime256v1"],
        "1.3.132.0.10" => ["secp256k1", "secp256k1"],
        "1.3.132.0.33" => ["secp224r1", "secp224r1"],
        "1.3.132.0.34" => ["secp384r1", "secp384r1"],
        "1.3.132.0.35" => ["secp521r1", "secp521r1"],
        "1.3.14.3.2.26" => ["SHA1", "sha1"],
        "2.16.840.1.101.3.4.2.1" => ["SHA256", "sha256"],
        "2.16.840.1.101.3.4.2.2" => ["SHA384", "sha384"],
        "2.16.840.1.101.3.4.2.3" => ["SHA512", "sha512"],
        "2.16.840.1.101.3.4.2.4" => ["SHA224", "sha224"],
        "2.16.840.1.101.3.4.1.2" => ["AES-128-CBC", "aes-128-cbc"],
        "2.16.840.1.101.3.4.1.42" => ["AES-256-CBC", "aes-256-cbc"],
        "1.2.840.113549.1.5.13" => ["PBES2", "PBES2"],
        "1.2.840.113549.1.5.12" => ["PBKDF2", "PBKDF2"],
        "2.5.29.14" => ["subjectKeyIdentifier", "X509v3 Subject Key Identifier"],
        "2.5.29.15" => ["keyUsage", "X509v3 Key Usage"],
        "2.5.29.17" => ["subjectAltName", "X509v3 Subject Alternative Name"],
        "2.5.29.19" => ["basicConstraints", "X509v3 Basic Constraints"],
        "2.5.29.31" => ["crlDistributionPoints", "X509v3 CRL Distribution Points"],
        "2.5.29.32" => ["certificatePolicies", "X509v3 Certificate Policies"],
        "2.5.29.35" => ["authorityKeyIdentifier", "X509v3 Authority Key Identifier"],
        "2.5.29.37" => ["extendedKeyUsage", "X509v3 Extended Key Usage"],
        "1.3.6.1.5.5.7.3.1" => ["serverAuth", "TLS Web Server Authentication"],
        "1.3.6.1.5.5.7.3.2" => ["clientAuth", "TLS Web Client Authentication"],
        "1.3.6.1.5.5.7.1.1" => ["authorityInfoAccess", "Authority Information Access"],
      }.freeze

      # "1.2.840.113549" -- the first two arcs share a byte, the rest are base-128.
      def __content # :nodoc:
        arcs = oid.split(".").map { |a| Kernel.Integer(a) }
        raise ASN1Error, "invalid OBJECT IDENTIFIER" if arcs.size < 2
        bytes = [arcs[0] * 40 + arcs[1]].pack("C")
        arcs.drop(2).each { |arc| bytes << ASN1.__base128(arc) }
        bytes
      end

      # The dotted form, whatever #value was set from.  A decoded ObjectId keeps
      # it alongside the short name OpenSSL reports as the value.
      def oid
        return @oid if defined?(@oid) && !@oid.nil?
        text = @value.to_s
        return text if text.match?(/\A\d+(\.\d+)+\z/)
        found = NAMES.find { |_, names| names.include?(text) }
        found ? found[0] : text
      end

      def __decoded(dotted) # :nodoc:
        @oid = dotted
        self
      end

      def short_name
        NAMES[oid]&.first || oid
      end
      alias_method :sn, :short_name

      def long_name
        NAMES[oid]&.last || oid
      end
      alias_method :ln, :long_name
    end

    # Two's-complement, shortest form: what DER calls an INTEGER.
    def self.__integer_bytes(value) # :nodoc:
      return "\x00".b if value.zero?
      bytes = []
      if value.positive?
        while value > 0
          bytes.unshift(value & 0xFF)
          value >>= 8
        end
        bytes.unshift(0) if (bytes[0] & 0x80) != 0
      else
        while value < -1
          bytes.unshift(value & 0xFF)
          value >>= 8
        end
        bytes.unshift(0xFF) if bytes.empty? || (bytes[0] & 0x80) == 0
      end
      bytes.pack("C*")
    end

    def self.__base128(value) # :nodoc:
      bytes = [value & 0x7F]
      value >>= 7
      while value > 0
        bytes.unshift((value & 0x7F) | 0x80)
        value >>= 7
      end
      bytes.pack("C*")
    end

    def self.__encode(identifier, content) # :nodoc:
      length = content.bytesize
      header = if length < 0x80
                 [identifier, length].pack("CC")
               else
                 hex = length.to_s(16)
                 hex = "0" + hex if hex.length.odd?
                 octets = [hex].pack("H*")
                 [identifier, 0x80 | octets.bytesize].pack("CC") + octets
               end
      header + content.b
    end

    # An element of a constructed value may be another ASN1Data or a plain String
    # that is already DER.
    def self.__to_der(element) # :nodoc:
      return element.to_der if element.respond_to?(:to_der)
      element.to_s.b
    end

    #
    # Decoding
    #

    def self.decode(der)
      value, _ = __decode(der.to_s.b, 0)
      value
    end

    def self.decode_all(der)
      der = der.to_s.b
      offset = 0
      values = []
      while offset < der.bytesize
        value, offset = __decode(der, offset)
        values << value
      end
      values
    end

    def self.traverse(der)
      decode_all(der).each do |value|
        yield(0, 0, 0, 0, value.__constructed?, value.tag_class, value.tag)
      end
      nil
    end

    TAG_CLASSES = [:UNIVERSAL, :APPLICATION, :CONTEXT_SPECIFIC, :PRIVATE].freeze # :nodoc:

    def self.__decode(der, offset) # :nodoc:
      raise ASN1Error, "not enough data" if offset >= der.bytesize
      identifier = der.getbyte(offset)
      offset += 1
      tag_class = TAG_CLASSES[identifier >> 6]
      constructed = (identifier & 0x20) != 0
      tag = identifier & 0x1F
      if tag == 0x1F
        tag = 0
        loop do
          raise ASN1Error, "not enough data" if offset >= der.bytesize
          byte = der.getbyte(offset)
          offset += 1
          tag = (tag << 7) | (byte & 0x7F)
          break if (byte & 0x80).zero?
        end
      end

      raise ASN1Error, "not enough data" if offset >= der.bytesize
      length = der.getbyte(offset)
      offset += 1
      if length >= 0x80
        count = length & 0x7F
        raise ASN1Error, "indefinite length is not supported" if count.zero?
        raise ASN1Error, "not enough data" if offset + count > der.bytesize
        length = der.byteslice(offset, count).each_byte.inject(0) { |a, b| (a << 8) | b }
        offset += count
      end
      raise ASN1Error, "not enough data" if offset + length > der.bytesize
      content = der.byteslice(offset, length)
      offset += length

      [__build(tag, tag_class, constructed, content), offset]
    end

    def self.__build(tag, tag_class, constructed, content) # :nodoc:
      if constructed
        elements = []
        inner = 0
        while inner < content.bytesize
          element, inner = __decode(content, inner)
          elements << element
        end
        if tag_class == :UNIVERSAL && (name = UNIVERSAL_TAG_NAME[tag]) &&
           const_get(name).ancestors.include?(Constructive)
          return const_get(name).new(elements)
        end
        return ASN1Data.new(elements, tag, tag_class)
      end

      unless tag_class == :UNIVERSAL && (name = UNIVERSAL_TAG_NAME[tag])
        return ASN1Data.new(content, tag, tag_class)
      end

      case name
      when :Integer, :Enumerated
        const_get(name).new(OpenSSL::BN.new(__decode_integer(content)))
      when :Boolean
        Boolean.new(content.getbyte(0) != 0)
      when :Null
        Null.new(nil)
      when :ObjectId
        dotted = __decode_oid(content)
        # OpenSSL reports a decoded OID by its short name when it knows one.
        ObjectId.new(ObjectId::NAMES[dotted]&.first || dotted).__decoded(dotted)
      when :BitString
        string = BitString.new(content.byteslice(1, content.bytesize - 1).to_s)
        string.unused_bits = content.bytesize.zero? ? 0 : content.getbyte(0)
        string
      when :UTF8String, :BMPString, :UniversalString
        const_get(name).new(content.dup.force_encoding(::Encoding::UTF_8))
      else
        const_get(name).new(content)
      end
    end

    def self.__decode_integer(content) # :nodoc:
      return 0 if content.empty?
      value = content.each_byte.inject(0) { |a, b| (a << 8) | b }
      value -= 1 << (content.bytesize * 8) if (content.getbyte(0) & 0x80) != 0
      value
    end

    def self.__decode_oid(content) # :nodoc:
      return "" if content.empty?
      first = content.getbyte(0)
      arcs = first < 80 ? [first / 40, first % 40] : [2, first - 80]
      value = 0
      content.each_byte.drop(1).each do |byte|
        value = (value << 7) | (byte & 0x7F)
        if (byte & 0x80).zero?
          arcs << value
          value = 0
        end
      end
      arcs.join(".")
    end
  end
end
