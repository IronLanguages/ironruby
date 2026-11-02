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

load_assembly 'IronRuby.Libraries', 'IronRuby.StandardLibrary.Zlib'

# Zlib::ZStream, Inflate and Deflate are the libz binding in Src/Libraries/Zlib/zlib.cs.
# Windows has no libz; there they run on the zlib .NET itself ships (Src/Libraries/Zlib/ClrZlib.cs).
# The gzip family below is written on top of them: a gzip member is a 10-byte header, a
# raw deflate stream and an 8-byte footer, none of which needs libz directly.

module Zlib

  class GzipFile
    class Error < ::Zlib::Error
    end

    # Raised when a member ends without its 8-byte footer, when the footer's CRC does not
    # match the data, and when its length does not. Nothing here raises them yet, but they
    # are part of the class's published surface and code rescues them by name.
    class NoFooter < Error; end
    class CRCError < Error; end
    class LengthError < Error; end

    def self.wrap(*args, **opts)
      gz = opts.empty? ? new(*args) : new(*args, **opts)
      return gz unless block_given?
      begin
        yield gz
      ensure
        gz.close unless gz.closed?
      end
    end

    def initialize(io)
      @io = io
      @closed = false
      @comment = nil
      @orig_name = nil
      @mtime = Time.at(0)
      @level = Zlib::DEFAULT_COMPRESSION
      @os_code = Zlib::OS_CODE
      @crc = 0
      @sync = false
    end

    # Deliberately not guarded: #close has to be able to answer after it has run.
    def closed?
      @closed
    end

    def to_io
      check_closed
      @io
    end

    def comment
      check_closed
      @comment
    end

    def orig_name
      check_closed
      @orig_name
    end

    def mtime
      check_closed
      @mtime
    end

    def level
      check_closed
      @level
    end

    def os_code
      check_closed
      @os_code
    end

    def crc
      check_closed
      @crc
    end

    def sync
      check_closed
      @sync
    end

    def sync=(value)
      check_closed
      @sync = value
    end

    def close
      finish_stream
      close_io(true)
    end

    def finish
      finish_stream
      close_io(false)
    end

    private

    # A reader has nothing to flush on the way out; a writer has its footer.
    def finish_stream
    end

    def check_closed
      raise Error, 'closed gzip stream' if @closed
    end

    def close_io(close_underlying)
      @closed = true
      if close_underlying && @io.respond_to?(:close)
        @io.close
      end
      @io
    end
  end

  class GzipReader < GzipFile
    include ::Enumerable

    def self.open(filename, **opts)
      io = ::File.open(filename, 'rb')
      begin
        gz = opts.empty? ? new(io) : new(io, **opts)
      rescue ::Exception
        io.close
        raise
      end
      return gz unless block_given?
      begin
        yield gz
      ensure
        gz.close unless gz.closed?
      end
    end

    # Concatenates every member of a multi-member stream, unlike #read, which stops at the
    # end of the first one.
    def self.zcat(io, **opts)
      result = +''.b
      until io.respond_to?(:eof?) && io.eof?
        gz = opts.empty? ? new(io) : new(io, **opts)
        chunk = gz.read
        break if chunk.nil?
        result << chunk.b
        gz.finish
      end
      if block_given?
        yield result
        nil
      else
        result
      end
    end

    def initialize(io, **opts)
      super(io)
      external = opts[:external_encoding] || opts[:encoding]
      @external_encoding = external ? ::Encoding.find(external) : ::Encoding.default_external
      @lineno = 0
      @pos = 0
      @buffer = +''.b
      @member_size = 0
      read_header
      inflate_member
    end

    attr_accessor :lineno
    attr_reader :external_encoding

    def pos
      @pos
    end
    alias_method :tell, :pos

    def eof?
      @buffer.empty?
    end
    alias_method :eof, :eof?

    # Answers nil while the member is still being read, which is all this reader ever does
    # with a single-member stream.
    def unused
      nil
    end

    def rewind
      check_closed
      # MRI winds the underlying io back by exactly what the inflater has taken, which
      # leaves it just past the header - so the header is not read a second time.
      @io.seek(-@member_size, ::IO::SEEK_CUR)
      @pos = 0
      @lineno = 0
      @crc = 0
      inflate_member
      0
    end

    def read(length = nil, outbuf = nil)
      check_closed
      if length.nil?
        return result(+''.b, outbuf) if @buffer.empty?
        return result(take(@buffer.bytesize).force_encoding(@external_encoding), outbuf)
      end

      length = Integer(length)
      raise ::ArgumentError, "negative length #{length} given" if length < 0
      return result(+''.b, outbuf) if length == 0
      return nil if @buffer.empty?
      result(take(length), outbuf)
    end

    def readpartial(maxlen, outbuf = nil)
      check_closed
      maxlen = Integer(maxlen)
      raise ::ArgumentError, "negative length #{maxlen} given" if maxlen < 0
      return result(+''.b, outbuf) if maxlen == 0
      raise ::EOFError, 'end of file reached' if @buffer.empty?
      result(take(maxlen), outbuf)
    end

    def getc
      check_closed
      return nil if @buffer.empty?
      char = @buffer.dup.force_encoding(@external_encoding)[0]
      take(char.bytesize).force_encoding(@external_encoding)
    end

    def readchar
      getc or raise ::EOFError, 'end of file reached'
    end

    def getbyte
      check_closed
      return nil if @buffer.empty?
      take(1).getbyte(0)
    end

    def readbyte
      getbyte or raise ::EOFError, 'end of file reached'
    end

    def ungetc(arg)
      check_closed
      str = arg.is_a?(::Integer) ? arg.chr(@external_encoding) : ::String.try_convert(arg)
      raise ::TypeError, "no implicit conversion into String" if str.nil?
      unget_bytes(str.b)
      nil
    end

    def ungetbyte(arg)
      check_closed
      unget_bytes((Integer(arg) & 0xff).chr)
      nil
    end

    def gets(*args)
      check_closed
      separator, limit, chomp = parse_line_args(args)
      line = next_line(separator, limit)
      return nil if line.nil?
      @lineno += 1
      line = chomp_line(line, separator) if chomp
      line.force_encoding(@external_encoding)
    end

    def readline(*args)
      gets(*args) or raise ::EOFError, 'end of file reached'
    end

    def readlines(*args)
      lines = []
      while (line = gets(*args))
        lines << line
      end
      lines
    end

    def each(*args)
      return ::Enumerator.new { |y| each(*args) { |line| y << line } } unless block_given?
      check_closed
      while (line = gets(*args))
        yield line
      end
      self
    end
    alias_method :each_line, :each

    def each_byte
      return ::Enumerator.new { |y| each_byte { |b| y << b } } unless block_given?
      check_closed
      while (byte = getbyte)
        yield byte
      end
      self
    end

    def each_char
      return ::Enumerator.new { |y| each_char { |c| y << c } } unless block_given?
      check_closed
      while (char = getc)
        yield char
      end
      self
    end

    private

    # Takes bytes off the front of the buffer. #pos counts what has been handed out, which
    # is why #ungetc can push it below zero: it puts back more than was ever taken.
    def take(count)
      taken = @buffer.byteslice(0, count)
      @buffer = @buffer.byteslice(taken.bytesize..-1) || +''.b
      @pos += taken.bytesize
      @crc = Zlib.crc32(taken, @crc)
      taken
    end

    def unget_bytes(bytes)
      return if bytes.empty?
      @buffer = bytes + @buffer
      @pos -= bytes.bytesize
    end

    def result(str, outbuf)
      return str if outbuf.nil?
      outbuf.replace(str)
      outbuf
    end

    def read_io(count)
      buffer = +''.b
      while buffer.bytesize < count
        chunk = @io.read(count - buffer.bytesize)
        break if chunk.nil? || chunk.empty?
        buffer << chunk.b
      end
      buffer
    end

    def read_header
      head = read_io(10)
      raise GzipFile::Error, 'not in gzip format' if head.bytesize < 10

      bytes = head.bytes
      unless bytes[0] == 0x1f && bytes[1] == 0x8b
        raise GzipFile::Error, 'not in gzip format'
      end
      unless bytes[2] == 8 # deflate, the only method gzip ever defined
        raise GzipFile::Error, 'unsupported compression method'
      end

      flags = bytes[3]
      @mtime = Time.at(bytes[4] | (bytes[5] << 8) | (bytes[6] << 16) | (bytes[7] << 24))
      # XFL only distinguishes "slowest" from "fastest"; everything else reads as default.
      @level = case bytes[8]
               when 2 then Zlib::BEST_COMPRESSION
               when 4 then Zlib::BEST_SPEED
               else Zlib::DEFAULT_COMPRESSION
               end
      @os_code = bytes[9]

      if (flags & 0x04) != 0 # FEXTRA
        length = read_io(2)
        raise GzipFile::Error, 'unexpected end of file' if length.bytesize < 2
        read_io(length.unpack1('v'))
      end
      @orig_name = read_stringz if (flags & 0x08) != 0 # FNAME
      @comment = read_stringz if (flags & 0x10) != 0 # FCOMMENT
      read_io(2) if (flags & 0x02) != 0 # FHCRC
    end

    def read_stringz
      result = +''.b
      while (byte = read_io(1)) && !byte.empty?
        break if byte == "\0".b
        result << byte
      end
      result
    end

    # Pulls the rest of the member out of the io in one go and inflates it. #rewind relies
    # on @member_size being exactly how far past the header the io has been advanced.
    def inflate_member
      compressed = +''.b
      while (chunk = @io.read(16384))
        break if chunk.empty?
        compressed << chunk.b
      end
      @member_size = compressed.bytesize

      stream = Zlib::Inflate.new(-Zlib::MAX_WBITS)
      begin
        @buffer = stream.inflate(compressed).b
      ensure
        stream.close unless stream.closed?
      end
    end

    def parse_line_args(args)
      chomp = false
      if args.last.is_a?(::Hash)
        opts = args.pop
        chomp = !!opts[:chomp]
      end
      raise ::ArgumentError, "wrong number of arguments (given #{args.size}, expected 0..2)" if args.size > 2

      separator = $/
      limit = nil
      case args.size
      when 1
        if args[0].is_a?(::Integer)
          limit = args[0]
        else
          separator = args[0]
        end
      when 2
        separator, limit = args
      end
      separator = ::String.try_convert(separator) unless separator.nil?
      limit = Integer(limit) unless limit.nil?
      [separator, limit, chomp]
    end

    def next_line(separator, limit)
      if separator.nil?
        return nil if @buffer.empty?
        return limit ? take(limit) : take(@buffer.bytesize)
      end

      if separator.empty?
        # Paragraph mode: the blank lines between paragraphs belong to no paragraph at
        # all, and are swallowed on both sides. Swallowing the trailing ones is what
        # makes #eof? true straight after the last paragraph rather than one call later.
        skip_newlines
        return nil if @buffer.empty?
        index = @buffer.index("\n\n".b)
        length = index ? index + 2 : @buffer.bytesize
        length = limit if limit && limit < length
        line = take(length)
        skip_newlines
        return line
      else
        return nil if @buffer.empty?
        index = @buffer.index(separator.b)
        length = index ? index + separator.bytesize : @buffer.bytesize
      end

      length = limit if limit && limit < length
      take(length)
    end

    def skip_newlines
      while @buffer.start_with?("\n".b)
        take(1)
      end
    end

    def chomp_line(line, separator)
      return line.chomp if separator.nil? || separator.empty?
      line.end_with?(separator.b) ? line.byteslice(0, line.bytesize - separator.bytesize) : line
    end
  end

  class GzipWriter < GzipFile
    def self.open(filename, level = nil, strategy = nil, **opts)
      io = ::File.open(filename, 'wb')
      begin
        gz = new(io, level, strategy, **opts)
      rescue ::Exception
        io.close
        raise
      end
      return gz unless block_given?
      begin
        yield gz
      ensure
        gz.close unless gz.closed?
      end
    end

    def initialize(io, level = nil, strategy = nil, **opts)
      super(io)
      @level = level.nil? ? Zlib::DEFAULT_COMPRESSION : Integer(level)
      @strategy = strategy.nil? ? Zlib::DEFAULT_STRATEGY : Integer(strategy)
      @deflate = Zlib::Deflate.new(@level, -Zlib::MAX_WBITS, Zlib::DEF_MEM_LEVEL, @strategy)
      @header_written = false
      @mtime_set = false
      @pos = 0
    end

    def pos
      @pos
    end
    alias_method :tell, :pos

    def mtime=(value)
      check_header_not_written
      @mtime = value.is_a?(::Time) ? value : Time.at(Integer(value))
      @mtime_set = true
      value
    end

    def orig_name=(value)
      check_header_not_written
      @orig_name = ::String.try_convert(value) or raise ::TypeError, 'no implicit conversion into String'
      # A NUL would end the header field early, so MRI truncates there rather than
      # producing a header that says something different from what was asked for.
      index = @orig_name.index("\0")
      @orig_name = @orig_name[0, index] if index
      value
    end

    def comment=(value)
      check_header_not_written
      @comment = ::String.try_convert(value) or raise ::TypeError, 'no implicit conversion into String'
      index = @comment.index("\0")
      @comment = @comment[0, index] if index
      value
    end

    def write(*args)
      check_closed
      written = 0
      args.each do |arg|
        str = arg.is_a?(::String) ? arg : arg.to_s
        write_header
        @crc = Zlib.crc32(str, @crc)
        @pos += str.bytesize
        written += str.bytesize
        @io.write(@deflate.deflate(str))
      end
      written
    end

    def <<(str)
      write(str)
      self
    end

    def print(*args)
      args.each { |arg| write(arg) }
      write($\) if $\
      nil
    end

    def printf(format, *args)
      write(::Kernel.format(format, *args))
      nil
    end

    def putc(arg)
      write(arg.is_a?(::Integer) ? (arg & 0xff).chr : arg.to_s[0])
      arg
    end

    def puts(*args)
      if args.empty?
        write("\n")
        return nil
      end
      args.each do |arg|
        case arg
        when ::Array then puts(*arg)
        when ::String then write(arg.end_with?("\n") ? arg : arg + "\n")
        else
          str = arg.nil? ? '' : arg.to_s
          write(str.end_with?("\n") ? str : str + "\n")
        end
      end
      nil
    end

    def flush(flush = Zlib::SYNC_FLUSH)
      check_closed
      write_header
      @io.write(@deflate.flush(flush))
      @io.flush if @io.respond_to?(:flush)
      self
    end

    private

    def check_header_not_written
      check_closed
      raise GzipFile::Error, 'header is already written' if @header_written
    end

    def finish_stream
      check_closed
      write_header
      @io.write(@deflate.finish)
      # The footer is the CRC32 and the uncompressed size, both little-endian.
      @io.write([@crc, @pos & 0xffffffff].pack('VV'))
      @io.flush if @io.respond_to?(:flush)
    end

    def write_header
      return if @header_written
      @header_written = true

      flags = 0
      flags |= 0x08 if @orig_name # FNAME
      flags |= 0x10 if @comment # FCOMMENT

      extra_flags = case @level
                    when Zlib::BEST_COMPRESSION then 2
                    when Zlib::BEST_SPEED then 4
                    else 0
                    end

      mtime = @mtime_set ? @mtime.to_i : ::Time.now.to_i
      header = [0x1f, 0x8b, 8, flags].pack('C4')
      header << [mtime & 0xffffffff].pack('V')
      header << [extra_flags, @os_code].pack('C2')
      header << (@orig_name + "\0") if @orig_name
      header << (@comment + "\0") if @comment
      @io.write(header)
    end
  end
end
