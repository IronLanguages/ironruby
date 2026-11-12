require 'digest'

# MRI's digest/bubblebabble is a C extension; the encoding itself is the
# Bubble Babble Binary Data Encoding (Antti Huima), as used by OpenSSH.
module Digest
  VOWELS = "aeiouy".freeze
  CONSONANTS = "bcdfghklmnprstvzx".freeze
  private_constant :VOWELS, :CONSONANTS

  def self.bubblebabble(str)
    # String#+ performs the implicit to_str conversion (and raises its TypeError).
    bytes = ("".b + str).bytes
    vowels, consonants = VOWELS, CONSONANTS
    seed = 1
    rounds = bytes.size / 2 + 1
    out = "x".b
    rounds.times do |i|
      if i + 1 < rounds || bytes.size.odd?
        b0 = bytes[2 * i]
        out << vowels[(((b0 >> 6) & 3) + seed) % 6]
        out << consonants[(b0 >> 2) & 15]
        out << vowels[((b0 & 3) + seed / 6) % 6]
        if i + 1 < rounds
          b1 = bytes[2 * i + 1]
          out << consonants[(b1 >> 4) & 15] << "-" << consonants[b1 & 15]
          seed = (seed * 5 + b0 * 7 + b1) % 36
        end
      else
        out << vowels[seed % 6] << consonants[16] << vowels[seed / 6]
      end
    end
    out << "x"
  end

  module Instance
    def bubblebabble
      Digest.bubblebabble(digest)
    end
  end

  class Class
    def self.bubblebabble(*args)
      Digest.bubblebabble(digest(*args))
    end
  end
end
