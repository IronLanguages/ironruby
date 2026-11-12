# OpenSSL::PKey on System.Security.Cryptography.
#
# RSA, DSA and EC are .NET's RSA, DSA, ECDsa and ECDiffieHellman.  Writing keys
# out is theirs too (ExportRSAPrivateKeyPem and friends); reading them back is
# not, because every Import* .NET added takes a ReadOnlySpan and IronRuby cannot
# hand it one.  So a key is read by parsing the DER here (OpenSSL::ASN1) and
# handing .NET the RSAParameters / DSAParameters / ECParameters it describes,
# which is the same key by a longer road.
#
# DH has no .NET equivalent at all -- .NET dropped finite-field Diffie-Hellman
# and kept only the elliptic-curve form -- so OpenSSL::PKey::DH exists to be
# named and rescued, and raises when asked to do arithmetic.

module OpenSSL
  module PKey
    Crypto = ::System::Security::Cryptography # :nodoc:

    class PKeyError < OpenSSLError; end unless const_defined?(:PKeyError, false)
    class RSAError < PKeyError; end unless const_defined?(:RSAError, false)
    class DSAError < PKeyError; end unless const_defined?(:DSAError, false)
    class DHError < PKeyError; end unless const_defined?(:DHError, false)
    class ECError < PKeyError; end unless const_defined?(:ECError, false)

    # PEM armour, the DER structures OpenSSL writes keys in, and the two
    # password-based encryptions a private key PEM can be wrapped in.
    module Codec # :nodoc: all
      module_function

      def pem?(data)
        data.include?("-----BEGIN ")
      end

      # [label, der] for the first PEM block, or [nil, data] if it is already DER.
      def decode_pem(data)
        return [nil, data.b] unless pem?(data)
        match = data.match(/-----BEGIN ([A-Z0-9 ]+)-----\r?\n(.*?)-----END \1-----/m)
        raise PKeyError, "malformed PEM" if match.nil?
        body = match[2]
        headers = {}
        if body =~ /\A((?:[A-Za-z0-9-]+:[^\n]*\r?\n)+)\r?\n(.*)\z/m
          $1.each_line { |line| name, value = line.strip.split(":", 2); headers[name] = value.to_s.strip }
          body = $2
        end
        [match[1], body.unpack1("m"), headers]
      end

      def encode_pem(label, der, headers = nil)
        out = "-----BEGIN #{label}-----\n"
        if headers && !headers.empty?
          headers.each { |name, value| out << "#{name}: #{value}\n" }
          out << "\n"
        end
        out << [der].pack("m0").scan(/.{1,64}/).join("\n") << "\n"
        out << "-----END #{label}-----\n"
        out
      end

      # An ASN.1 INTEGER's magnitude, left-padded to the length .NET's parameter
      # structures insist on.
      def fixed(bn, length)
        bytes = bn.is_a?(OpenSSL::BN) ? bn.to_s(2) : bn.b
        bytes = bytes.sub(/\A\x00+/n, "")
        raise PKeyError, "integer is longer than #{length} bytes" if bytes.bytesize > length
        ("\x00".b * (length - bytes.bytesize)) + bytes
      end

      # .NET's PEM writers leave the last line without its newline; OpenSSL's
      # always ends with one, and code that concatenates PEMs depends on it.
      def terminate(pem)
        pem.end_with?("\n") ? pem : pem + "\n"
      end

      def integers(sequence)
        sequence.value.map { |element| element.value.to_i }
      end

      # PKCS#8 PrivateKeyInfo: version, AlgorithmIdentifier, OCTET STRING key.
      # Returns [algorithm oid, inner DER].
      def unwrap_pkcs8(der)
        sequence = OpenSSL::ASN1.decode(der)
        algorithm = sequence.value[1]
        [algorithm.value[0].oid, sequence.value[2].value]
      end

      # SubjectPublicKeyInfo: AlgorithmIdentifier, BIT STRING key.
      # Returns [algorithm oid, parameters, key bits].
      def unwrap_spki(der)
        sequence = OpenSSL::ASN1.decode(der)
        algorithm = sequence.value[0]
        [algorithm.value[0].oid, algorithm.value[1], sequence.value[1].value]
      end

      # "DEK-Info: AES-128-CBC,<iv hex>" -- OpenSSL's traditional PEM encryption:
      # the key is EVP_BytesToKey(MD5, password, first 8 IV bytes, one round).
      def traditional_key(cipher_name, password, salt)
        cipher = OpenSSL::Cipher.new(cipher_name)
        material = "".b
        block = "".b
        while material.bytesize < cipher.key_len
          block = OpenSSL::Digest.digest("MD5", block + password.b + salt)
          material << block
        end
        material[0, cipher.key_len]
      end

      def encrypt_traditional(der, cipher, password)
        name = cipher.respond_to?(:name) ? cipher.name : cipher.to_s
        iv = OpenSSL::Random.random_bytes(OpenSSL::Cipher.new(name).iv_len)
        worker = OpenSSL::Cipher.new(name)
        worker.encrypt
        worker.key = traditional_key(name, password, iv[0, 8])
        worker.iv = iv
        [worker.update(der) + worker.final,
         { "Proc-Type" => "4,ENCRYPTED", "DEK-Info" => "#{name.upcase},#{iv.unpack1('H*').upcase}" }]
      end

      def decrypt_traditional(data, headers, password)
        info = headers["DEK-Info"] or raise PKeyError, "no DEK-Info in an encrypted PEM"
        name, iv_hex = info.split(",", 2)
        iv = [iv_hex.to_s].pack("H*")
        worker = OpenSSL::Cipher.new(name)
        worker.decrypt
        worker.key = traditional_key(name, password, iv[0, 8])
        worker.iv = iv
        worker.update(data) + worker.final
      end

      PBES2 = "1.2.840.113549.1.5.13".freeze
      PBKDF2 = "1.2.840.113549.1.5.12".freeze
      PRFS = { # :nodoc:
        "1.2.840.113549.2.7" => "SHA1",
        "1.2.840.113549.2.8" => "SHA224",
        "1.2.840.113549.2.9" => "SHA256",
        "1.2.840.113549.2.10" => "SHA384",
        "1.2.840.113549.2.11" => "SHA512",
      }.freeze
      PBES2_CIPHERS = { # :nodoc:
        "2.16.840.1.101.3.4.1.2" => "AES-128-CBC",
        "2.16.840.1.101.3.4.1.22" => "AES-192-CBC",
        "2.16.840.1.101.3.4.1.42" => "AES-256-CBC",
        "1.2.840.113549.3.7" => "DES-EDE3-CBC",
      }.freeze

      # EncryptedPrivateKeyInfo with PBES2 (PBKDF2 + AES-CBC), which is what
      # both OpenSSL 3 and .NET write.  The older PKCS#12 derivations are not
      # implemented.
      def decrypt_pkcs8(der, password)
        sequence = OpenSSL::ASN1.decode(der)
        scheme = sequence.value[0]
        data = sequence.value[1].value
        raise PKeyError, "unsupported key encryption: #{scheme.value[0].oid}" unless scheme.value[0].oid == PBES2
        kdf, encryption = scheme.value[1].value
        raise PKeyError, "unsupported key derivation: #{kdf.value[0].oid}" unless kdf.value[0].oid == PBKDF2

        parameters = kdf.value[1].value
        salt = parameters[0].value
        iterations = parameters[1].value.to_i
        prf = parameters.find { |element| element.is_a?(OpenSSL::ASN1::Sequence) }
        hash = prf ? PRFS[prf.value[0].oid] : "SHA1"
        raise PKeyError, "unsupported PRF" if hash.nil?

        name = PBES2_CIPHERS[encryption.value[0].oid] or
          raise PKeyError, "unsupported key encryption: #{encryption.value[0].oid}"
        cipher = OpenSSL::Cipher.new(name)
        cipher.decrypt
        cipher.key = OpenSSL::KDF.pbkdf2_hmac(password.b, salt: salt, iterations: iterations,
                                              length: cipher.key_len, hash: hash)
        cipher.iv = encryption.value[1].value
        cipher.update(data) + cipher.final
      end
    end

    # PKey.read guesses the format the way MRI's does: PEM label first, and for
    # bare DER, the shape of the structure.
    def self.read(data, password = nil)
      data = String.try_convert(data) || (data.respond_to?(:read) ? data.read : nil)
      raise TypeError, "no implicit conversion into String" if data.nil?
      label, = Codec.decode_pem(data)
      case label
      when "RSA PRIVATE KEY", "RSA PUBLIC KEY" then return RSA.new(data, password)
      when "DSA PRIVATE KEY" then return DSA.new(data, password)
      when "EC PRIVATE KEY" then return EC.new(data, password)
      end

      [RSA, EC, DSA].each do |klass|
        begin
          return klass.new(data, password)
        rescue OpenSSLError, ::System::Security::Cryptography::CryptographicException
          next
        end
      end
      raise PKeyError, "Could not parse PKey"
    end

    # What every key class shares: the digest lookup, signing and the PEM/DER
    # writers, all of which are the same call on a different .NET key object.
    class PKey
      def initialize(*)
        raise NotImplementedError, "OpenSSL::PKey::PKey is an abstract class"
      end

      def __clr_key # :nodoc:
        @key
      end

      def private?
        @private
      end

      def public?
        true
      end

      def oid
        self.class.name.split("::").last.downcase
      end

      # OpenSSL takes the digest by name or by object; .NET takes a
      # HashAlgorithmName, and only for the algorithms it can sign with.
      def __hash(digest) # :nodoc:
        name = OpenSSL::Digest.new(digest).name
        case name
        when "SHA1" then Crypto::HashAlgorithmName.SHA1
        when "SHA256" then Crypto::HashAlgorithmName.SHA256
        when "SHA384" then Crypto::HashAlgorithmName.SHA384
        when "SHA512" then Crypto::HashAlgorithmName.SHA512
        when "SHA3-256" then Crypto::HashAlgorithmName.SHA3_256
        when "SHA3-384" then Crypto::HashAlgorithmName.SHA3_384
        when "SHA3-512" then Crypto::HashAlgorithmName.SHA3_512
        else raise PKeyError, "cannot sign with #{name}: .NET has no signature algorithm for it"
        end
      end
      private :__hash

      def sign(digest, data)
        raise PKeyError, "private key needed" unless @private
        IronRubyOpenSSL__.bytes(__sign(__hash(digest), __str(data)))
      end

      def verify(digest, signature, data)
        __verify(__hash(digest), __str(signature).b, __str(data))
      rescue ::System::Security::Cryptography::CryptographicException
        false
      end

      def to_pem(cipher = nil, password = nil)
        return export(cipher, password) unless cipher.nil?
        @private ? __private_pem : public_to_pem
      end
      alias_method :to_s, :to_pem
      alias_method :private_to_pem, :to_pem

      def export(cipher = nil, password = nil)
        return to_pem if cipher.nil?
        raise PKeyError, "private key needed" unless @private
        body, headers = Codec.encrypt_traditional(to_der, cipher, __str(password))
        Codec.encode_pem(__pem_label, body, headers)
      end

      def to_der
        @private ? __private_der : public_to_der
      end
      alias_method :private_to_der, :to_der

      def public_to_pem
        Codec.terminate(IronRubyOpenSSL__.string(@key.ExportSubjectPublicKeyInfoPem))
      end

      def public_to_der
        IronRubyOpenSSL__.bytes(@key.ExportSubjectPublicKeyInfo)
      end

      def inspect
        "#<#{self.class}:0x%08x>" % (object_id << 1)
      end

      def to_text
        "#{self.class.name.split('::').last} key\n"
      end

      private

      def __str(value)
        String.try_convert(value) or
          raise TypeError, "no implicit conversion of #{value.class} into String"
      end
    end

    class RSA < PKey
      PKCS1_PADDING = 1
      SSLV23_PADDING = 2
      NO_PADDING = 3
      PKCS1_OAEP_PADDING = 4

      def initialize(argument = nil, password = nil)
        case argument
        when nil
          @key = Crypto::RSA.Create(2048)
          @private = true
        when ::Integer
          @key = Crypto::RSA.Create(argument)
          @private = true
        else
          __load(__str(argument), password)
        end
      end

      def self.generate(size, exponent = nil)
        if exponent && Integer(exponent) != 65537
          raise RSAError, ".NET's RSA always uses the public exponent 65537"
        end
        new(size)
      end

      def __replace(key, is_private) # :nodoc:
        @key = key
        @private = is_private
        self
      end

      def public_key
        copy = Crypto::RSA.Create
        copy.ImportParameters(@key.ExportParameters(false))
        RSA.allocate.__replace(copy, false)
      end

      def params
        parameters = @key.ExportParameters(@private)
        names = { "n" => :Modulus, "e" => :Exponent }
        names.update("d" => :D, "p" => :P, "q" => :Q,
                     "dmp1" => :DP, "dmq1" => :DQ, "iqmp" => :InverseQ) if @private
        result = {}
        names.each do |name, field|
          value = parameters.send(field)
          result[name] = OpenSSL::BN.new(value.nil? ? "".b : IronRubyOpenSSL__.bytes(value), 2)
        end
        unless @private
          %w[d p q dmp1 dmq1 iqmp].each { |name| result[name] = OpenSSL::BN.new(0) }
        end
        result
      end

      %w[n e d p q dmp1 dmq1 iqmp].each do |name|
        define_method(name) { params[name] }
      end

      def public_encrypt(data, padding = PKCS1_PADDING)
        IronRubyOpenSSL__.bytes(@key.Encrypt(__str(data).b, __padding(padding)))
      rescue ::System::Security::Cryptography::CryptographicException => error
        raise RSAError, error.message
      end

      def private_decrypt(data, padding = PKCS1_PADDING)
        raise RSAError, "private key needed" unless @private
        IronRubyOpenSSL__.bytes(@key.Decrypt(__str(data).b, __padding(padding)))
      rescue ::System::Security::Cryptography::CryptographicException => error
        raise RSAError, error.message
      end

      # RSA with the private exponent and no hash in front of it -- the raw
      # primitive OpenSSL exposes and .NET does not: every .NET entry point into
      # a private-key operation is either a decryption or a signature over a
      # digest it chooses the DigestInfo for.
      def private_encrypt(data, padding = PKCS1_PADDING)
        raise NotImplementedError,
              "RSA#private_encrypt is not available: .NET has no raw RSA private-key operation"
      end

      def public_decrypt(data, padding = PKCS1_PADDING)
        raise NotImplementedError,
              "RSA#public_decrypt is not available: .NET has no raw RSA public-key operation"
      end

      def sign_pss(digest, data, salt_length: :digest, mgf1_hash: nil)
        raise PKeyError, "private key needed" unless @private
        IronRubyOpenSSL__.bytes(@key.SignData(__str(data).b,
                                              __hash(digest),
                                              Crypto::RSASignaturePadding.Pss))
      end

      def verify_pss(digest, signature, data, salt_length: :auto, mgf1_hash: nil)
        @key.VerifyData(__str(data).b, __str(signature).b,
                        __hash(digest),
                        Crypto::RSASignaturePadding.Pss)
      rescue ::System::Security::Cryptography::CryptographicException
        false
      end

      private

      def __padding(padding)
        case padding
        when PKCS1_PADDING then Crypto::RSAEncryptionPadding.Pkcs1
        when PKCS1_OAEP_PADDING then Crypto::RSAEncryptionPadding.OaepSHA1
        else raise RSAError, "unsupported padding #{padding}"
        end
      end

      def __sign(hash, data)
        @key.SignData(data.b, hash, Crypto::RSASignaturePadding.Pkcs1)
      end

      def __verify(hash, signature, data)
        @key.VerifyData(data.b, signature, hash, Crypto::RSASignaturePadding.Pkcs1)
      end

      def __pem_label
        "RSA PRIVATE KEY"
      end

      def __private_pem
        Codec.terminate(IronRubyOpenSSL__.string(@key.ExportRSAPrivateKeyPem))
      end

      def __private_der
        IronRubyOpenSSL__.bytes(@key.ExportRSAPrivateKey)
      end

      def __load(data, password)
        label, der, headers = Codec.decode_pem(data)
        if headers && headers["Proc-Type"].to_s.include?("ENCRYPTED")
          raise RSAError, "a password is required" if password.nil?
          der = Codec.decrypt_traditional(der, headers, __str(password))
        end
        case label
        when "RSA PRIVATE KEY" then __import_pkcs1(der)
        when "PRIVATE KEY" then __import_pkcs1(Codec.unwrap_pkcs8(der)[1])
        when "ENCRYPTED PRIVATE KEY"
          raise RSAError, "a password is required" if password.nil?
          __import_pkcs1(Codec.unwrap_pkcs8(Codec.decrypt_pkcs8(der, __str(password)))[1])
        when "RSA PUBLIC KEY" then __import_public(der)
        when "PUBLIC KEY" then __import_public(Codec.unwrap_spki(der)[2])
        when nil then __import_der(der)
        else raise RSAError, "not an RSA key: #{label}"
        end
      end

      # Bare DER: PKCS#1 private (nine integers), PKCS#1 public (two), PKCS#8 or
      # SubjectPublicKeyInfo (an AlgorithmIdentifier first).
      def __import_der(der)
        sequence = OpenSSL::ASN1.decode(der)
        raise RSAError, "not an RSA key" unless sequence.is_a?(OpenSSL::ASN1::Sequence)
        case sequence.value.size
        when 9 then __import_pkcs1(der)
        when 2
          if sequence.value[0].is_a?(OpenSSL::ASN1::Sequence)
            __import_public(sequence.value[1].value)
          else
            __import_public(der)
          end
        when 3 then __import_pkcs1(Codec.unwrap_pkcs8(der)[1])
        else raise RSAError, "not an RSA key"
        end
      end

      def __import_pkcs1(der)
        values = Codec.integers(OpenSSL::ASN1.decode(der))
        raise RSAError, "not an RSA private key" unless values.size >= 9
        _, n, e, d, p, q, dp, dq, qinv = values
        size = OpenSSL::BN.new(n).num_bytes
        half = (size + 1) / 2
        parameters = Crypto::RSAParameters.new
        parameters.Modulus = Codec.fixed(OpenSSL::BN.new(n), size).b
        parameters.Exponent = OpenSSL::BN.new(e).to_s(2).b
        parameters.D = Codec.fixed(OpenSSL::BN.new(d), size).b
        parameters.P = Codec.fixed(OpenSSL::BN.new(p), half).b
        parameters.Q = Codec.fixed(OpenSSL::BN.new(q), half).b
        parameters.DP = Codec.fixed(OpenSSL::BN.new(dp), half).b
        parameters.DQ = Codec.fixed(OpenSSL::BN.new(dq), half).b
        parameters.InverseQ = Codec.fixed(OpenSSL::BN.new(qinv), half).b
        @key = Crypto::RSA.Create
        @key.ImportParameters(parameters)
        @private = true
        self
      end

      def __import_public(der)
        values = Codec.integers(OpenSSL::ASN1.decode(der))
        raise RSAError, "not an RSA public key" unless values.size == 2
        parameters = Crypto::RSAParameters.new
        parameters.Modulus = OpenSSL::BN.new(values[0]).to_s(2).b
        parameters.Exponent = OpenSSL::BN.new(values[1]).to_s(2).b
        @key = Crypto::RSA.Create
        @key.ImportParameters(parameters)
        @private = false
        self
      end
    end

    class DSA < PKey
      def initialize(argument = nil, password = nil)
        case argument
        when nil
          @key = Crypto::DSA.Create(2048)
          @private = true
        when ::Integer
          @key = Crypto::DSA.Create(argument)
          @private = true
        else
          __load(__str(argument), password)
        end
      end

      def self.generate(size)
        new(size)
      end

      def __replace(key, is_private) # :nodoc:
        @key = key
        @private = is_private
        self
      end

      def public_key
        copy = Crypto::DSA.Create
        copy.ImportParameters(@key.ExportParameters(false))
        DSA.allocate.__replace(copy, false)
      end

      def params
        parameters = @key.ExportParameters(@private)
        result = { "p" => parameters.P, "q" => parameters.Q, "g" => parameters.G,
                   "pub_key" => parameters.Y }
        result["priv_key"] = parameters.X if @private
        result.each_key do |name|
          value = result[name]
          result[name] = OpenSSL::BN.new(value.nil? ? "".b : IronRubyOpenSSL__.bytes(value), 2)
        end
        result
      end

      %w[p q g].each { |name| define_method(name) { params[name] } }
      def pub_key; params["pub_key"]; end
      def priv_key; params["priv_key"]; end

      private

      # OpenSSL writes a DSA signature as the DER SEQUENCE of r and s; .NET's
      # default is the bare concatenation.
      def __sign(hash, data)
        @key.SignData(data.b, hash, Crypto::DSASignatureFormat.Rfc3279DerSequence)
      end

      def __verify(hash, signature, data)
        @key.VerifyData(data.b, signature, hash, Crypto::DSASignatureFormat.Rfc3279DerSequence)
      end

      def __pem_label
        "DSA PRIVATE KEY"
      end

      def __private_pem
        Codec.encode_pem("DSA PRIVATE KEY", __private_der)
      end

      # The traditional DSA private key: SEQUENCE(version, p, q, g, y, x).
      def __private_der
        parameters = @key.ExportParameters(true)
        OpenSSL::ASN1::Sequence.new(
          [0, parameters.P, parameters.Q, parameters.G, parameters.Y, parameters.X].map { |value|
            number = value.is_a?(::Integer) ? value : OpenSSL::BN.new(IronRubyOpenSSL__.bytes(value), 2)
            OpenSSL::ASN1::Integer.new(OpenSSL::BN.new(number))
          }
        ).to_der
      end

      def __load(data, password)
        label, der, headers = Codec.decode_pem(data)
        if headers && headers["Proc-Type"].to_s.include?("ENCRYPTED")
          raise DSAError, "a password is required" if password.nil?
          der = Codec.decrypt_traditional(der, headers, __str(password))
        end
        case label
        when "DSA PRIVATE KEY" then __import_private(der)
        when "PRIVATE KEY" then __import_pkcs8(der)
        when "ENCRYPTED PRIVATE KEY"
          raise DSAError, "a password is required" if password.nil?
          __import_pkcs8(Codec.decrypt_pkcs8(der, __str(password)))
        when "PUBLIC KEY" then __import_spki(der)
        when nil
          sequence = OpenSSL::ASN1.decode(der)
          if sequence.value[0].is_a?(OpenSSL::ASN1::Sequence)
            __import_spki(der)
          elsif sequence.value.size == 6
            __import_private(der)
          else
            __import_pkcs8(der)
          end
        else raise DSAError, "not a DSA key: #{label}"
        end
      end

      def __import_private(der)
        _, p, q, g, y, x = Codec.integers(OpenSSL::ASN1.decode(der))
        __import(p, q, g, y, x)
      end

      # PKCS#8: the domain parameters are in the AlgorithmIdentifier and the
      # private exponent is the INTEGER inside the OCTET STRING.
      def __import_pkcs8(der)
        sequence = OpenSSL::ASN1.decode(der)
        p, q, g = Codec.integers(sequence.value[1].value[1])
        x = OpenSSL::ASN1.decode(sequence.value[2].value).value.to_i
        y = p.nil? ? nil : g.pow(x, p)
        __import(p, q, g, y, x)
      end

      def __import_spki(der)
        _, parameters, bits = Codec.unwrap_spki(der)
        p, q, g = Codec.integers(parameters)
        y = OpenSSL::ASN1.decode(bits).value.to_i
        __import(p, q, g, y, nil)
      end

      def __import(p, q, g, y, x)
        size = OpenSSL::BN.new(p).num_bytes
        order = OpenSSL::BN.new(q).num_bytes
        parameters = Crypto::DSAParameters.new
        parameters.P = Codec.fixed(OpenSSL::BN.new(p), size).b
        parameters.Q = Codec.fixed(OpenSSL::BN.new(q), order).b
        parameters.G = Codec.fixed(OpenSSL::BN.new(g), size).b
        parameters.Y = Codec.fixed(OpenSSL::BN.new(y), size).b
        parameters.X = Codec.fixed(OpenSSL::BN.new(x), order).b unless x.nil?
        @key = Crypto::DSA.Create
        @key.ImportParameters(parameters)
        @private = !x.nil?
        self
      end
    end

    class EC < PKey
      NAMED_CURVE = 1

      # [OpenSSL name, OID, degree, order] for the prime curves .NET's platform
      # backend has.  The binary and Koblitz curves over GF(2^m) are absent:
      # .NET's ECCurve only describes prime curves.
      CURVES = [
        ["prime192v1", "1.2.840.10045.3.1.1", 192, "FFFFFFFFFFFFFFFFFFFFFFFF99DEF836146BC9B1B4D22831"],
        ["secp224r1", "1.3.132.0.33", 224, "FFFFFFFFFFFFFFFFFFFFFFFFFFFF16A2E0B8F03E13DD29455C5C2A3D"],
        ["prime256v1", "1.2.840.10045.3.1.7", 256,
         "FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551"],
        ["secp384r1", "1.3.132.0.34", 384,
         "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFC7634D81F4372DDF581A0DB248B0A77AECEC196ACCC52973"],
        ["secp521r1", "1.3.132.0.35", 521,
         "01FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFA51868783BF2F966B7FCC0148F709A5D03BB5C9B8899C47AEBB6FB71E91386409"],
        ["secp256k1", "1.3.132.0.10", 256,
         "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141"],
      ].freeze

      ALIASES = { # :nodoc:
        "secp256r1" => "prime256v1", "P-256" => "prime256v1", "nistP256" => "prime256v1",
        "secp192r1" => "prime192v1", "P-192" => "prime192v1",
        "P-224" => "secp224r1", "nistP224" => "secp224r1",
        "P-384" => "secp384r1", "nistP384" => "secp384r1",
        "P-521" => "secp521r1", "nistP521" => "secp521r1",
      }.freeze

      def self.__curve(name) # :nodoc:
        name = ALIASES[name.to_s] || name.to_s
        CURVES.find { |curve| curve[0] == name } or
          raise ECError, "unknown curve name: #{name}"
      end

      def self.__curve_by_oid(oid) # :nodoc:
        CURVES.find { |curve| curve[1] == oid } or
          raise ECError, "unsupported curve: #{oid}"
      end

      def self.builtin_curves
        CURVES.map { |name, _, _, _| [name, name] }
      end

      def self.generate(curve)
        curve = curve.curve_name if curve.is_a?(Group)
        key = allocate
        key.__replace(Crypto::ECDsa.Create(Crypto::ECCurve.CreateFromValue(__curve(curve)[1].to_clr_string)),
                      true, __curve(curve)[0])
      end

      def initialize(argument = nil, password = nil)
        case argument
        when nil
          raise ECError, "OpenSSL::PKey::EC.new requires a curve name, a PEM or a DER"
        when Group
          __replace(Crypto::ECDsa.Create(Crypto::ECCurve.CreateFromValue(EC.__curve(argument.curve_name)[1].to_clr_string)),
                    true, argument.curve_name)
        else
          text = __str(argument)
          if !Codec.pem?(text) && text.match?(/\A[A-Za-z0-9._-]+\z/)
            name = EC.__curve(text)[0]
            __replace(Crypto::ECDsa.Create(Crypto::ECCurve.CreateFromValue(EC.__curve(name)[1].to_clr_string)),
                      true, name)
          else
            __load(text, password)
          end
        end
      end

      def __replace(key, is_private, curve_name) # :nodoc:
        @key = key
        @private = is_private
        @curve_name = curve_name
        self
      end

      def group
        Group.new(@curve_name)
      end

      def private_key
        return nil unless @private
        OpenSSL::BN.new(IronRubyOpenSSL__.bytes(@key.ExportParameters(true).D), 2)
      end

      def public_key
        Point.new(group, OpenSSL::BN.new(__point_octets, 2))
      end

      # On OpenSSL 3 a key is immutable, so MRI raises here too; the message is
      # the one MRI gives.
      def generate_key!
        raise PKeyError, "pkeys are immutable on OpenSSL 3.0"
      end
      alias_method :generate_key, :generate_key!

      def private_key=(*)
        raise PKeyError, "pkeys are immutable on OpenSSL 3.0"
      end

      def public_key=(*)
        raise PKeyError, "pkeys are immutable on OpenSSL 3.0"
      end

      def check_key
        true
      end

      # The ECDSA primitive: the argument is already a digest, and the signature
      # is the DER SEQUENCE of r and s.
      def dsa_sign_asn1(data)
        raise ECError, "private key needed" unless @private
        IronRubyOpenSSL__.bytes(
          @key.SignHash(__str(data).b, Crypto::DSASignatureFormat.Rfc3279DerSequence))
      rescue ::System::Security::Cryptography::CryptographicException => error
        raise ECError, error.message
      end

      def dsa_verify_asn1(data, signature)
        @key.VerifyHash(__str(data).b, __str(signature).b,
                        Crypto::DSASignatureFormat.Rfc3279DerSequence)
      rescue ::System::Security::Cryptography::CryptographicException
        false
      end

      # An ECDH shared secret.  .NET keeps agreement in a separate class, so the
      # key is handed over as parameters and ECDiffieHellman does the arithmetic.
      def dh_compute_key(point)
        raise ECError, "private key needed" unless @private
        mine = Crypto::ECDiffieHellman.Create
        mine.ImportParameters(@key.ExportParameters(true))
        theirs = Crypto::ECDiffieHellman.Create
        theirs.ImportParameters(EC.__parameters(@curve_name, point.to_bn.to_s(2), nil))
        IronRubyOpenSSL__.bytes(mine.DeriveRawSecretAgreement(theirs.PublicKey))
      end

      def self.__parameters(curve_name, octets, private_bytes) # :nodoc:
        curve = __curve(curve_name)
        size = (curve[2] + 7) / 8
        raise ECError, "only uncompressed points are supported" unless octets.getbyte(0) == 4
        point = Crypto::ECPoint.new
        point.X = octets.byteslice(1, size).b
        point.Y = octets.byteslice(1 + size, size).b
        parameters = Crypto::ECParameters.new
        parameters.Curve = Crypto::ECCurve.CreateFromValue(curve[1].to_clr_string)
        parameters.Q = point
        parameters.D = Codec.fixed(OpenSSL::BN.new(private_bytes, 2), size).b unless private_bytes.nil?
        parameters
      end

      private

      def __point_octets
        parameters = @key.ExportParameters(false)
        "\x04".b + IronRubyOpenSSL__.bytes(parameters.Q.X) + IronRubyOpenSSL__.bytes(parameters.Q.Y)
      end

      def __sign(hash, data)
        @key.SignData(data.b, hash, Crypto::DSASignatureFormat.Rfc3279DerSequence)
      end

      def __verify(hash, signature, data)
        @key.VerifyData(data.b, signature, hash, Crypto::DSASignatureFormat.Rfc3279DerSequence)
      end

      def __pem_label
        "EC PRIVATE KEY"
      end

      def __private_pem
        Codec.terminate(IronRubyOpenSSL__.string(@key.ExportECPrivateKeyPem))
      end

      def __private_der
        IronRubyOpenSSL__.bytes(@key.ExportECPrivateKey)
      end

      def __load(data, password)
        label, der, headers = Codec.decode_pem(data)
        if headers && headers["Proc-Type"].to_s.include?("ENCRYPTED")
          raise ECError, "a password is required" if password.nil?
          der = Codec.decrypt_traditional(der, headers, __str(password))
        end
        case label
        when "EC PRIVATE KEY" then __import_sec1(der)
        when "PRIVATE KEY" then __import_sec1(Codec.unwrap_pkcs8(der)[1])
        when "ENCRYPTED PRIVATE KEY"
          raise ECError, "a password is required" if password.nil?
          __import_sec1(Codec.unwrap_pkcs8(Codec.decrypt_pkcs8(der, __str(password)))[1])
        when "PUBLIC KEY" then __import_spki(der)
        when nil
          sequence = OpenSSL::ASN1.decode(der)
          if sequence.value[0].is_a?(OpenSSL::ASN1::Sequence)
            __import_spki(der)
          elsif sequence.value.size == 3 && sequence.value[1].is_a?(OpenSSL::ASN1::Sequence)
            __import_sec1(Codec.unwrap_pkcs8(der)[1])
          else
            __import_sec1(der)
          end
        else raise ECError, "not an EC key: #{label}"
        end
      end

      # SEC1 ECPrivateKey: SEQUENCE(1, OCTET STRING d, [0] curve OID, [1] BIT STRING point)
      def __import_sec1(der)
        sequence = OpenSSL::ASN1.decode(der)
        private_bytes = sequence.value[1].value
        oid = nil
        octets = nil
        # [0] and [1] are constructed, so their single child is already decoded.
        sequence.value.drop(2).each do |element|
          inner = element.value.is_a?(::Array) ? element.value[0] : OpenSSL::ASN1.decode(element.value)
          case element.tag
          when 0 then oid = inner.oid
          when 1 then octets = inner.value
          end
        end
        raise ECError, "EC private key without a named curve" if oid.nil?
        curve = EC.__curve_by_oid(oid)
        if octets.nil?
          raise ECError, "EC private key without its public point: .NET cannot recompute it"
        end
        key = Crypto::ECDsa.Create
        key.ImportParameters(EC.__parameters(curve[0], octets, private_bytes))
        __replace(key, true, curve[0])
      end

      def __import_spki(der)
        algorithm_oid, parameters, bits = Codec.unwrap_spki(der)
        raise ECError, "not an EC key" unless algorithm_oid == "1.2.840.10045.2.1"
        curve = EC.__curve_by_oid(parameters.oid)
        key = Crypto::ECDsa.Create
        key.ImportParameters(EC.__parameters(curve[0], bits, nil))
        __replace(key, false, curve[0])
      end

      # EC_GROUP: everything about the curve itself.
      class Group
        attr_reader :curve_name

        def initialize(name)
          name = name.curve_name if name.is_a?(Group)
          @curve_name, @oid, @degree, order = EC.__curve(name)
          @order = OpenSSL::BN.new(order, 16)
        end

        attr_reader :degree, :order

        def cofactor
          OpenSSL::BN.new(1)
        end

        def asn1_flag
          NAMED_CURVE
        end

        def asn1_flag=(value)
          value
        end

        def point_conversion_form
          :uncompressed
        end

        def point_conversion_form=(value)
          unless value == :uncompressed
            raise ECError, "only the uncompressed point conversion form is supported"
          end
          value
        end

        def generator
          raise NotImplementedError,
                "EC::Group#generator is not available: .NET's ECCurve does not expose a named curve's base point"
        end

        def ==(other)
          other.is_a?(Group) && other.curve_name == @curve_name
        end
        alias_method :eql?, :==

        def to_der
          OpenSSL::ASN1::ObjectId.new(@oid).to_der
        end

        def to_pem
          "-----BEGIN EC PARAMETERS-----\n#{[to_der].pack('m0')}\n-----END EC PARAMETERS-----\n"
        end

        def inspect
          "#<#{self.class}:0x%08x>" % (object_id << 1)
        end
      end

      # EC_POINT.  A point is kept as its uncompressed octet string, which is
      # what .NET's ECPoint is too -- an X and a Y and nothing else.
      class Point
        attr_reader :group

        def initialize(group, value = nil)
          @group = group.is_a?(Group) ? group : Group.new(group)
          @octets = case value
                    when nil then "\x00".b
                    when OpenSSL::BN then value.to_s(2)
                    else String.try_convert(value)&.b or
                      raise TypeError, "no implicit conversion into String"
                    end
        end

        def to_bn(conversion_form = :uncompressed)
          unless conversion_form == :uncompressed
            raise ECError, "only the uncompressed point conversion form is supported"
          end
          OpenSSL::BN.new(@octets, 2)
        end

        def to_octet_string(conversion_form = :uncompressed)
          unless conversion_form == :uncompressed
            raise ECError, "only the uncompressed point conversion form is supported"
          end
          @octets.dup
        end

        def infinity?
          @octets == "\x00".b
        end

        def on_curve?
          true
        end

        def ==(other)
          other.is_a?(Point) && other.group == @group &&
            other.to_octet_string == @octets
        end
        alias_method :eql?, :==

        def inspect
          "#<#{self.class}:0x%08x>" % (object_id << 1)
        end
      end
    end

    # Finite-field Diffie-Hellman.  .NET has only the elliptic-curve form
    # (ECDiffieHellman, which OpenSSL::PKey::EC#dh_compute_key uses), so this
    # class exists to be named and rescued and raises when asked to work.
    class DH
      def initialize(*)
        raise DHError,
              "OpenSSL::PKey::DH is not available: .NET has no finite-field Diffie-Hellman"
      end

      def self.generate(*)
        new
      end
    end
  end
end
