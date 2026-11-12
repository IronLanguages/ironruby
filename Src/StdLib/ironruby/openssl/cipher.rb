# OpenSSL::Cipher on System.Security.Cryptography.
#
# The block ciphers come from Aes/TripleDES, the AEAD ones from AesGcm and
# ChaCha20Poly1305.  Two differences from libcrypto follow from that and are
# visible:
#
#   * .NET's AEAD classes are one-shot, so #update buffers and #final does the
#     whole operation.  update(a) + update(b) + final still equals the whole
#     ciphertext, which is how the API is used, but #update on its own returns
#     "" for those modes.
#   * .NET has no CTR mode; it is built here out of ECB, which is what CTR is.
#
# Everything else libcrypto offers -- RC4, Blowfish, CAST, IDEA, CFB/OFB, the
# XTS and CCM modes, SEED, ARIA, Camellia -- has no .NET implementation and is
# not offered here.

module OpenSSL
  class Cipher
    class CipherError < OpenSSLError; end
    class AuthTagError < CipherError; end

    Crypto = ::System::Security::Cryptography # :nodoc:

    # name => [algorithm, key bytes, iv bytes, block bytes, mode]
    ALGORITHMS = {} # :nodoc:
    [128, 192, 256].each do |bits|
      %w[cbc ecb ctr gcm].each do |mode|
        iv = case mode
             when "ecb" then 0
             when "gcm" then 12
             else 16
             end
        block = (mode == "ctr" || mode == "gcm") ? 1 : 16
        ALGORITHMS["AES-#{bits}-#{mode.upcase}"] = [:aes, bits / 8, iv, block, mode.to_sym]
      end
    end
    ALGORITHMS["DES-EDE3-CBC"] = [:des3, 24, 8, 8, :cbc]
    ALGORITHMS["DES-EDE3"] = [:des3, 24, 0, 8, :ecb]
    ALGORITHMS["CHACHA20-POLY1305"] = [:chacha20poly1305, 32, 12, 1, :gcm]
    ALGORITHMS.freeze

    def self.ciphers
      names = ALGORITHMS.keys.reject { |name| name.start_with?("CHACHA20") && !Crypto::ChaCha20Poly1305.IsSupported }
      (names + names.map(&:downcase)).sort
    end

    def self.__lookup(name) # :nodoc:
      key = name.to_s.upcase
      key = "AES-#{$1}-CBC" if key =~ /\AAES(\d+)\z/          # "AES256" is AES-256-CBC
      key = "DES-EDE3-CBC" if key == "DES3"
      entry = ALGORITHMS[key]
      if entry.nil? || (entry[0] == :chacha20poly1305 && !Crypto::ChaCha20Poly1305.IsSupported)
        raise CipherError, "unsupported cipher algorithm: #{name}"
      end
      [key, entry]
    end

    def initialize(name)
      @name, (@algorithm, @key_len, @iv_len, @block_size, @mode) = Cipher.__lookup(name)
      @direction = nil
      @key = nil
      @iv = nil
      @padding = true
      @auth_data = "".b
      @auth_tag = nil
      @auth_tag_len = 16
      @buffer = "".b
      @finished = false
    end

    attr_reader :name

    def key_len
      @key_len
    end

    def iv_len
      @iv_len
    end

    def block_size
      @block_size
    end

    def encrypt
      @direction = :encrypt
      __restart
      self
    end

    def decrypt
      @direction = :decrypt
      __restart
      self
    end

    def reset
      __restart
      self
    end

    def key=(value)
      value = __string(value)
      raise ArgumentError, "key must be #{@key_len} bytes" unless value.bytesize == @key_len
      @key = value.b
      value
    end

    def iv=(value)
      value = __string(value)
      raise ArgumentError, "iv must be #{@iv_len} bytes" unless value.bytesize == @iv_len
      @iv = value.b
      value
    end

    def auth_tag_len=(length)
      @auth_tag_len = Integer(length)
    end

    def random_key
      self.key = OpenSSL::Random.random_bytes(@key_len)
    end

    def random_iv
      self.iv = OpenSSL::Random.random_bytes(@iv_len)
    end

    def padding=(value)
      @padding = Integer(value) != 0
      value
    end

    def auth_data=(value)
      raise CipherError, "authentication data is only for AEAD modes" unless __aead?
      @auth_data = __string(value).b
      value
    end

    def auth_tag(length = @auth_tag_len)
      raise CipherError, "authentication tag is only for AEAD modes" unless __aead?
      raise CipherError, "tag not generated yet" if @auth_tag.nil?
      @auth_tag[0, length]
    end

    def auth_tag=(value)
      raise CipherError, "authentication tag is only for AEAD modes" unless __aead?
      @auth_tag = __string(value).b
      value
    end

    def authenticated?
      __aead?
    end

    # EVP_BytesToKey with one round of MD5, which is what OpenSSL's
    # EVP_BytesToKey does by default and what #pkcs5_keyivgen has always been.
    def pkcs5_keyivgen(pass, salt = nil, iterations = 2048, digest = "MD5")
      salt = salt.nil? ? "".b : __string(salt).b
      raise ArgumentError, "salt must be an 8-octet string" unless salt.empty? || salt.bytesize == 8
      material = "".b
      block = "".b
      name = OpenSSL::Digest.new(digest).name
      while material.bytesize < @key_len + @iv_len
        block = OpenSSL::Digest.digest(name, block + __string(pass).b + salt)
        (iterations - 1).times { block = OpenSSL::Digest.digest(name, block) }
        material << block
      end
      self.key = material[0, @key_len]
      self.iv = material[@key_len, @iv_len] if @iv_len > 0
      nil
    end

    def update(data, buffer = nil)
      raise CipherError, "cipher not initialized" if @direction.nil?
      @buffer << __string(data).b
      result = __aead? ? "".b : __transform_available
      buffer.nil? ? result : buffer.replace(result)
    end

    def final
      raise CipherError, "cipher not initialized" if @direction.nil?
      result = __aead? ? __aead_final : __block_final
      @finished = true
      result
    end

    def inspect
      "#<#{self.class}:0x%08x>" % (object_id << 1)
    end

    private

    def __string(value)
      String.try_convert(value) or
        raise TypeError, "no implicit conversion of #{value.class} into String"
    end

    def __aead?
      @mode == :gcm
    end

    def __restart
      @buffer = "".b
      @pending = "".b
      @counter = nil
      @finished = false
      @auth_tag = nil if @direction == :encrypt
    end

    def __check_key
      raise CipherError, "key not set" if @key.nil?
      raise CipherError, "iv not set" if @iv.nil? && @iv_len > 0
    end

    # CTR and the AEAD modes are stream ciphers; the block modes have to keep
    # whole blocks back so that the final one can be padded or unpadded.
    def __transform_available
      return "".b if @mode == :cbc || @mode == :ecb
      __check_key
      taken = @buffer
      @buffer = "".b
      __ctr(taken)
    end

    def __block_final
      __check_key
      data = @buffer
      @buffer = "".b
      return __ctr(data) if @mode == :ctr
      transform = nil
      begin
        transform = __transform
        IronRubyOpenSSL__.bytes(transform.TransformFinalBlock(data.b, 0, data.bytesize))
      rescue ::System::Security::Cryptography::CryptographicException => error
        raise CipherError, error.message
      ensure
        transform.Dispose unless transform.nil?
      end
    end

    def __transform
      algorithm = case @algorithm
                  when :aes then Crypto::Aes.Create
                  when :des3 then Crypto::TripleDES.Create
                  end
      algorithm.Mode = @mode == :ecb ? Crypto::CipherMode.ECB : Crypto::CipherMode.CBC
      algorithm.Padding = @padding ? Crypto::PaddingMode.PKCS7 : Crypto::PaddingMode.None
      algorithm.Key = @key.b
      algorithm.IV = @iv.b if @iv_len > 0
      @direction == :encrypt ? algorithm.CreateEncryptor : algorithm.CreateDecryptor
    end

    # CTR: the keystream is ECB(counter), counter big-endian over the whole IV.
    def __ctr(data)
      return "".b if data.empty?
      @counter ||= @iv.b.dup
      @pending ||= "".b
      out = "".b
      offset = 0
      while offset < data.bytesize
        if @pending.empty?
          algorithm = Crypto::Aes.Create
          algorithm.Mode = Crypto::CipherMode.ECB
          algorithm.Padding = Crypto::PaddingMode.None
          algorithm.Key = @key.b
          transform = algorithm.CreateEncryptor
          @pending = IronRubyOpenSSL__.bytes(transform.TransformFinalBlock(@counter.b, 0, @counter.bytesize))
          transform.Dispose
          __increment_counter
        end
        take = [@pending.bytesize, data.bytesize - offset].min
        chunk = data.byteslice(offset, take)
        keystream = @pending.byteslice(0, take)
        out << chunk.bytes.each_with_index.map { |b, i| (b ^ keystream.getbyte(i)).chr }.join
        @pending = @pending.byteslice(take, @pending.bytesize - take)
        offset += take
      end
      out.b
    end

    def __increment_counter
      index = @counter.bytesize - 1
      while index >= 0
        byte = (@counter.getbyte(index) + 1) & 0xFF
        @counter.setbyte(index, byte)
        break unless byte.zero?
        index -= 1
      end
    end

    def __aead_final
      __check_key
      data = @buffer
      @buffer = "".b
      cipher = __aead_cipher
      begin
        if @direction == :encrypt
          ciphertext = ::System::Array.of(::System::Byte).new(data.bytesize)
          tag = ::System::Array.of(::System::Byte).new(@auth_tag_len)
          cipher.Encrypt(@iv.b, data.b, ciphertext, tag, @auth_data.b)
          @auth_tag = IronRubyOpenSSL__.bytes(tag)
          IronRubyOpenSSL__.bytes(ciphertext)
        else
          raise CipherError, "tag not set" if @auth_tag.nil?
          plaintext = ::System::Array.of(::System::Byte).new(data.bytesize)
          cipher.Decrypt(@iv.b, data.b, @auth_tag.b, plaintext, @auth_data.b)
          IronRubyOpenSSL__.bytes(plaintext)
        end
      rescue ::System::Security::Cryptography::AuthenticationTagMismatchException
        raise AuthTagError, "AEAD authentication tag verification failed"
      rescue ::System::Security::Cryptography::CryptographicException => error
        raise CipherError, error.message
      ensure
        cipher.Dispose
      end
    end

    def __aead_cipher
      if @algorithm == :chacha20poly1305
        Crypto::ChaCha20Poly1305.new(@key.b)
      else
        Crypto::AesGcm.new(@key.b, @auth_tag_len)
      end
    end
  end
end
