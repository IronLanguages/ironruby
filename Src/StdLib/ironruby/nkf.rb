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

# NKF on top of String#encode.
#
# MRI's nkf is a C extension wrapping the nkf command's own converter. Almost all of
# what it is asked to do - and all of what the standard library asks it for (rss's
# converter.rb, kconv.rb) - is "read this as X and write it as Y", which the CLR's
# Japanese code pages already do: Shift_JIS, EUC-JP and ISO-2022-JP all transcode
# here. So the encoding flags, the input guess, the line-end flags and MIME
# encode/decode are real; the parts of nkf that are text munging rather than
# transcoding (-Z folding, -x/-X half-width kana rewriting, -f line folding) are
# accepted and ignored, which is what nkf itself does with a flag it does not know.

module NKF
  # nkf 0.2.0, the gem Ruby 4.0 bundles: the codes are Encodings, nil for the three that
  # name no encoding. (They were Integers in nkf 1.x, which kconv.rb can no longer use.)
  AUTO = nil
  NOCONV = nil
  UNKNOWN = nil
  BINARY = Encoding::BINARY
  ASCII = Encoding::US_ASCII
  JIS = Encoding::ISO_2022_JP
  EUC = Encoding::EUC_JP
  SJIS = Encoding::Shift_JIS
  UTF8 = Encoding::UTF_8
  UTF16 = Encoding::UTF_16BE
  UTF32 = Encoding::UTF_32BE
  NKF_VERSION = "2.1.5"
  NKF_RELEASE_DATE = "2018-12-15"
  VERSION = "2.1.5 (2018-12-15)"
  GEM_VERSION = "0.2.0"

  # The single-letter flags, upper case for input and lower case for output.
  # "8", "16" and "32" may follow W/w, and "B"/"L" a UTF-16 or UTF-32 to ask for
  # an endianness; "0" after that means "no BOM".
  ENCODINGS = {
    "J" => "ISO-2022-JP",
    "S" => "Windows-31J",   # nkf's -S is CP932, not the narrower Shift_JIS
    "E" => "EUC-JP",
    "W" => "UTF-8",
  }
  private_constant :ENCODINGS

  class << self
    # NKF.nkf(opt, str) -> String
    def nkf(opt, str)
      opt = opt.to_str
      str = str.to_str
      o = parse_options(opt)

      input = str.dup.force_encoding(o[:input] || guess(str))
      # nkf never raises on bad bytes; it drops or substitutes them.
      input = input.encode(input.encoding, invalid: :replace, undef: :replace) unless input.valid_encoding?

      input = mime_decode(input) if o[:mime_decode]

      out = input.encode(o[:output] || "ISO-2022-JP", invalid: :replace, undef: :replace)
      # ISO-2022-JP that never left ASCII is plain ASCII, and that is how MRI tags it -
      # which matters, because String#inspect escapes every byte of a dummy encoding.
      out = out.dup.force_encoding(Encoding::US_ASCII) if out.encoding == Encoding::ISO_2022_JP && !out.b.include?("\e")
      out = out.gsub(/\r\n|\r|\n/, o[:eol]) if o[:eol]
      out = mime_encode(out, o[:mime_encode]) if o[:mime_encode]
      out
    end

    # The Encoding the bytes look like.
    def guess(str)
      str = str.to_str
      b = str.b

      return Encoding::UTF_8 if b.start_with?("\xEF\xBB\xBF".b)
      return Encoding::UTF_16LE if b.start_with?("\xFF\xFE".b)
      return Encoding::UTF_16BE if b.start_with?("\xFE\xFF".b)
      # ESC ( B / ESC $ B and friends can only be ISO-2022-JP.
      return Encoding::ISO_2022_JP if b.include?("\e$") || b.include?("\e(")
      return Encoding::US_ASCII if b.each_byte.all? { |c| c < 0x80 }

      return Encoding::UTF_8 if b.dup.force_encoding(Encoding::UTF_8).valid_encoding?

      euc = b.dup.force_encoding(Encoding::EUC_JP).valid_encoding?
      sjis = b.dup.force_encoding(Encoding::Windows_31J).valid_encoding?
      return Encoding::EUC_JP if euc && !sjis
      return Encoding::Windows_31J if sjis && !euc
      return Encoding::ASCII_8BIT unless euc || sjis

      # Both fit. EUC-JP's lead and trail bytes are both >= 0xA1, Shift_JIS's trail
      # bytes reach down into 0x40..0x7E, so a trail byte below 0xA1 settles it.
      bytes = b.bytes
      i = 0
      while i < bytes.size
        c = bytes[i]
        if c >= 0x81
          n = bytes[i + 1]
          return Encoding::Windows_31J if n && n < 0xA1
          i += 2
        else
          i += 1
        end
      end
      Encoding::EUC_JP
    end

    alias guess1 guess
    alias guess2 guess

    private

    def parse_options(opt)
      o = {}
      # --ic= / --oc= name the encodings outright and outrank the letters.
      opt = opt.gsub(/--ic=(\S+)/) { o[:input] = find_encoding($1); "" }
      opt = opt.gsub(/--oc=(\S+)/) { o[:output] = find_encoding($1); "" }


      i = 0
      while i < opt.length
        c = opt[i]
        i += 1
        case c
        when "J", "S", "E", "W"
          enc, i = read_encoding(c, opt, i)
          o[:input] = enc
        when "j", "s", "e", "w"
          enc, i = read_encoding(c.upcase, opt, i)
          o[:output] = enc
        when "m"
          n = opt[i]
          if n == "0"
            i += 1
            o[:mime_decode] = false
          else
            i += 1 if n == "B" || n == "Q" || n == "N" || n == "S"
            o[:mime_decode] = true
          end
        when "M"
          n = opt[i]
          o[:mime_encode] = (n == "Q" || n == "B") ? n : "B"
          i += 1 if n == "Q" || n == "B"
        when "L"
          case opt[i]
          when "u" then o[:eol] = "\n"
          when "w" then o[:eol] = "\r\n"
          when "m" then o[:eol] = "\r"
          end
          i += 1
        when "d" then o[:eol] = "\n"
        when "c" then o[:eol] = "\r\n"
        when "-"
          if opt[i] == "-"
            # A long option: skip it whole, --ic= and --oc= having been taken above.
            i = opt.index(" ", i) || opt.length
          end
          # Otherwise it is only the prefix of the flags that follow.
        else
          # -x, -X, -Z, -b, -u, -f, -t, ... : accepted and ignored.
        end
      end
      o
    end

    # W8 / W16 / W32, optionally with B or L and a trailing 0 for "no BOM". nkf
    # writes UTF-16 and UTF-32 big-endian by default, and with a BOM.
    def read_encoding(letter, opt, i)
      unless letter == "W"
        return [ENCODINGS[letter], i]
      end
      if opt[i, 2] == "16" || opt[i, 2] == "32"
        width = opt[i, 2]
        i += 2
        endian = "BE"
        if opt[i] == "B" || opt[i] == "L"
          endian = opt[i] == "L" ? "LE" : "BE"
          i += 1
        end
        i += 1 if opt[i] == "0"
        ["UTF-#{width}#{endian}", i]
      else
        i += 1 if opt[i] == "8"
        i += 1 if opt[i] == "0"
        ["UTF-8", i]
      end
    end

    def find_encoding(name)
      Encoding.find(name)
    rescue ArgumentError
      nil
    end

    # =?charset?B?...?= / =?charset?Q?...?= , as RFC 2047 spells it. Adjacent encoded
    # words separated only by whitespace join up with the space dropped. Every word may
    # name its own charset, so the answer is UTF-8 - the one encoding all of them reach.
    def mime_decode(str)
      out = str.encode(Encoding::UTF_8, invalid: :replace, undef: :replace)
      out.gsub(/=\?([A-Za-z0-9_\-]+)\?([BbQq])\?([^?]*)\?=(\s*(?==\?))?/) do
        charset, kind, body = $1, $2, $3
        raw = if kind.downcase == "b"
                body.unpack1("m")
              else
                body.tr("_", " ").gsub(/=([0-9A-Fa-f]{2})/) { $1.to_i(16).chr }
              end
        enc = find_encoding(charset)
        enc ? raw.force_encoding(enc).encode(Encoding::UTF_8, invalid: :replace, undef: :replace) : raw
      end
    end

    # nkf leaves text that needs no encoding alone, so a pure-ASCII line stays readable.
    def mime_encode(str, kind)
      return str if str.b.each_byte.all? { |c| c < 0x80 }
      body = str.b
      encoded = if kind == "Q"
                  body.gsub(/[^\x21-\x3C\x3E\x40-\x7E]/n) { |ch| "=%02X" % ch.ord }.gsub(" ", "_")
                else
                  [body].pack("m0")
                end
      "=?#{str.encoding.name}?#{kind}?#{encoded}?=".force_encoding(str.encoding)
    end
  end
end
