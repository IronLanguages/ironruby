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

load_assembly 'IronRuby.Libraries', 'IronRuby.StandardLibrary.StringIO'

# The byte and character side of StringIO, which the C# class does not carry: the same
# shapes IO has in ruby4.rb, written against StringIO's own primitives.  #getc is the C#
# one, a character in the stream's encoding; the C# #readchar still answers a byte as an
# Integer, which was the 1.8 meaning, and is what #getbyte and #readbyte are made of.
class StringIO
  unless private_method_defined?(:__ir_readbyte__)
    alias_method :__ir_readbyte__, :readchar
    private :__ir_readbyte__
  end

  def readchar
    c = getc
    ::Kernel.raise(::EOFError, "end of file reached") if c.nil?
    c
  end

  # eof? first: it asks for a readable stream, as MRI's #getbyte does, and the byte reader
  # raises rather than answer nil at the end.
  def getbyte
    eof? ? nil : __ir_readbyte__
  end unless method_defined?(:getbyte)

  def readbyte
    __ir_readbyte__
  end unless method_defined?(:readbyte)

  # ungetc takes a String as well as a codepoint, and a codepoint is a character in the
  # string's encoding rather than a single byte.
  def ungetc(value)
    return nil if value.nil?
    if value.is_a?(::Integer)
      enc = external_encoding || ::Encoding.default_external
      text = (value.chr(enc) rescue value.chr)
    else
      unless value.is_a?(::String)
        unless value.respond_to?(:to_str)
          ::Kernel.raise(::TypeError, "no implicit conversion of #{value.class} into String")
        end
        value = value.to_str
      end
      text = value
    end
    __ir_unget_bytes__(text)
    nil
  end

  # ungetbyte pushes bytes, never characters, and an Integer is taken modulo 256 rather than
  # being out of range.
  def ungetbyte(byte)
    return nil if byte.nil?
    if byte.is_a?(::Integer)
      __ir_unget_bytes__((byte & 0xff).chr)
    else
      unless byte.is_a?(::String)
        unless byte.respond_to?(:to_str)
          ::Kernel.raise(::TypeError, "no implicit conversion of #{byte.class} into String")
        end
        byte = byte.to_str
      end
      __ir_unget_bytes__(byte)
    end
    nil
  end

  # Without a block an Enumerator of the method, as MRI's (#<Enumerator: #<StringIO>:each_char>), and like
  # MRI's of no known size.
  def each_char
    return to_enum(:each_char) { nil } unless block_given?
    while (c = getc)
      yield c
    end
    self
  end unless method_defined?(:each_char)

  def each_codepoint
    return to_enum(:each_codepoint) { nil } unless block_given?
    each_char { |c| yield c.ord }
    self
  end unless method_defined?(:each_codepoint)

  # A byte-order mark names the encoding, so there must be nothing named already; the stream
  # is left positioned after the mark, and answers nil when there is none to read.
  BOMS__ = [
    ["\xEF\xBB\xBF".b, "UTF-8"],
    ["\x00\x00\xFE\xFF".b, "UTF-32BE"],
    ["\xFF\xFE\x00\x00".b, "UTF-32LE"],
    ["\xFE\xFF".b, "UTF-16BE"],
    ["\xFF\xFE".b, "UTF-16LE"],
  ]

  # Unlike IO#set_encoding_by_bom there is no binmode requirement and no complaint about an
  # encoding already being set: MRI's stringio reads the mark whatever the string's encoding
  # is, and only a stream that cannot be read from answers nil without looking.
  def set_encoding_by_bom
    if frozen?
      ::Kernel.raise(::FrozenError.new("can't modify frozen StringIO: #{inspect}", receiver: self))
    end
    return nil unless __readable_stream__?
    start = pos
    head = (+read(4).to_s).force_encoding(::Encoding::BINARY)
    match = BOMS__.find { |bytes, _| head.start_with?(bytes) }
    unless match
      seek(start)
      return nil
    end
    seek(start + match[0].bytesize)
    enc = ::Encoding.find(match[1])
    set_encoding(enc)
    enc
  end unless method_defined?(:set_encoding_by_bom)

  # A string is always ready, so these are #sysread and #syswrite - with the end of file
  # reported as nil rather than raised when asked not to raise.
  def read_nonblock(maxlen, buf = nil, exception: true)
    if maxlen == 0
      return buf ? buf.replace("") : ""
    end
    begin
      buf ? sysread(maxlen, buf) : sysread(maxlen)
    rescue ::EOFError
      ::Kernel.raise if exception
      nil
    end
  end unless method_defined?(:read_nonblock)

  def write_nonblock(string, exception: true)
    syswrite(string)
  end unless method_defined?(:write_nonblock)
end

# The iterators raise without a block where MRI answers an Enumerator. Only that is wrapped
# here: the separator, limit and chomp: arguments are the built-in's own, and #gets and
# #readline are not wrapped at all because $_ belongs to the frame that called them and a
# wrapper would set it in its own.
class StringIO
  # Each alias has to be taken before its redefinition, or it names the new method and the
  # method calls itself until the stack runs out.
  alias_method :__ir_each_byte__, :each_byte
  private :__ir_each_byte__

  def each_byte(&block)
    return to_enum(:each_byte) { nil } unless block
    __ir_each_byte__(&block)
  end

  alias_method :__ir_each_line__, :each_line
  private :__ir_each_line__

  def each_line(*args, **opts, &block)
    return to_enum(:each_line, *args, **opts) { nil } unless block
    __ir_each_line__(*args, **opts, &block)
  end

  # one method in MRI, which a backtrace names by the name called
  def each(*args, **opts, &block)
    return to_enum(:each, *args, **opts) { nil } unless block
    __ir_each_line__(*args, **opts, &block)
  end
  __ir_same_method__(:each, :each_line)
end

# StringIO.open closes the stream when its block is done and lets the string go with it, so
# #string answers nil afterwards.  The block is not handed to ::new, which would warn about
# being given one.
class << StringIO
  def open(*args, **options)
    io = new(*args, **options)
    return io unless block_given?
    begin
      yield io
    ensure
      io.__send__(:__ir_finalize__)
    end
  end
end
