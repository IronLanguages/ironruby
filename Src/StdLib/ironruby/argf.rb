# ARGF - the files named on the command line read as though they were one
# stream, or standard input when there are none.  CRuby implements it in C
# (argf.c); this is the same object built on File and IO.
#
# Two pieces of state carry most of the behaviour:
#
#   @current   the IO being read.  It is *kept* after it has been read to the
#              end and closed, because ARGF.file and ARGF.filename go on
#              answering for the file that was read last.
#   @spent     the current file has nothing more to give, so the next read has
#              to move on.  Separate from @current.closed? because the file is
#              only closed when it is left behind.
#
# ARGV is consumed as files are opened - ARGF.argv is the same array, and what
# it holds is what is still to come.

ARGF_CLASS = Class.new do
  include Enumerable

  def initialize(*argv)
    @argv = argv
    @current = nil
    @current_name = nil
    @started = false      # a first file has been taken (so an empty ARGV means "done", not "stdin")
    @ended = false        # there is nothing left to open
    @spent = true
    @lineno = 0
    @binmode = false
    @external = nil
    @internal = nil
    @inplace = nil
    @output = nil
    @saved_stdout = nil
    @backup_name = nil
  end

  def argv
    @argv
  end

  def to_s
    "ARGF"
  end
  alias_method :inspect, :to_s

  # ---- the current file ----

  def file
    open_first
    @current
  end
  alias_method :to_io, :file

  def filename
    open_first
    @current_name
  end
  alias_method :path, :filename

  def fileno
    open_first
    raise ArgumentError, "no stream" if @current.nil? || @current.closed?
    @current.fileno
  end
  alias_method :to_i, :fileno

  def closed?
    open_first
    @current.nil? || @current.closed?
  end

  def close
    open_first
    unless @current.nil? || @current.closed? || @current.equal?($stdin)
      @current.close
    end
    finish_inplace
    @spent = true
    self
  end

  # Abandons the rest of the current file.  Doing it twice in a row, or before
  # anything has been read, is not an error - it simply has nothing to skip.
  def skip
    return self if @spent
    leave_current
    self
  end

  def rewind
    raise ArgumentError, "no stream to rewind" if @current.nil? || @current.closed?
    @lineno -= @current.lineno
    @current.rewind
  end

  def pos
    raise ArgumentError, "no stream to tell" if @current.nil? || @current.closed?
    @current.pos
  end
  alias_method :tell, :pos

  def pos=(position)
    raise ArgumentError, "no stream to tell" if @current.nil? || @current.closed?
    @current.pos = position
  end

  def seek(amount, whence = IO::SEEK_SET)
    raise ArgumentError, "no stream to seek" if @current.nil? || @current.closed?
    @current.seek(amount, whence)
  end

  # ARGF.eof? is the end of the *current* file, not of the whole stream: with
  # two files it is true twice.  So it opens a first file if none is open yet,
  # but never steps over one that has been read to the end.
  def eof?
    open_first
    raise IOError, "closed stream" if @current.nil? || @current.closed?
    @current.eof?
  end
  alias_method :eof, :eof?

  def lineno
    @lineno
  end

  def lineno=(value)
    @lineno = value
    $. = value
  end

  # ---- reading ----

  def __ir_gets__(*args)
    # counted before there is anything to read: at the end too, as MRI's argf_getline does
    positional = args.last.is_a?(Hash) ? args.size - 1 : args.size
    raise ArgumentError, "wrong number of arguments (given #{positional}, expected 0..2)" if positional > 2
    return nil unless prepare_read
    # chomp: arrives here as a trailing positional Hash (through the C# alias); IO#gets takes
    # no Hash positionally, so hand it back as keywords
    line = args.last.is_a?(Hash) ? @current.gets(*args[0...-1], **args.last) : @current.gets(*args)
    count_line(line)
  end

  def __ir_readline__(*args)
    line = __ir_gets__(*args)
    raise EOFError, "end of file reached" if line.nil?
    line
  end

  # These are C# (Kernel#__ir_argf_gets__ and friends) so that the line lands in
  # the caller's $_, as it does in MRI.
  alias_method :gets, :__ir_argf_gets__
  alias_method :readline, :__ir_argf_readline__
  public :gets, :readline

  def readlines(*args)
    result = []
    while (line = gets(*args))
      result << line
    end
    result
  end
  alias_method :to_a, :readlines

  def each_line(*args)
    return to_enum(:each_line, *args) unless block_given?
    while (line = gets(*args))
      yield line
    end
    self
  end
  alias_method :each, :each_line

  def each_byte
    return to_enum(:each_byte) unless block_given?
    while (byte = getbyte)
      yield byte
    end
    self
  end

  def each_char
    return to_enum(:each_char) unless block_given?
    while (char = getc)
      yield char
    end
    self
  end

  def each_codepoint
    return to_enum(:each_codepoint) unless block_given?
    each_char { |char| yield char.ord }
    self
  end

  def getc
    return nil unless prepare_read
    @current.getc
  end

  def getbyte
    return nil unless prepare_read
    @current.getbyte
  end

  def readchar
    char = getc
    raise EOFError, "end of file reached" if char.nil?
    char
  end

  def readbyte
    byte = getbyte
    raise EOFError, "end of file reached" if byte.nil?
    byte
  end

  # read spans files; read(length) counts bytes across all of them and comes
  # back binary, the way IO#read does.  Both answer nil - not "" - once every
  # file has been read and left behind.
  def read(length = nil, buffer = nil)
    buffer = clear_buffer(buffer)

    # A file that is open but finished still answers "" rather than nothing at
    # all, which is why this asks for a current file rather than for a readable
    # one: ARGF.pos = 1000 followed by ARGF.read is "", and only a stream with
    # no files left at all is nil.
    if length.nil?
      result = nil
      while prepare_current
        chunk = @current.read
        result = result.nil? ? chunk : (result + chunk)
        leave_current
      end
      return nil if result.nil?
      buffer ? buffer.replace(result) : result
    else
      length = Integer(length)
      raise ArgumentError, "negative length #{length} given" if length < 0

      result = nil
      loop do
        break unless prepare_current
        chunk = @current.read(length)
        if chunk.nil?
          leave_current
          next
        end
        result = result.nil? ? chunk : (result + chunk)
        length -= chunk.bytesize
        break if length <= 0
        leave_current
      end
      return nil if result.nil?
      buffer ? buffer.replace(result) : result
    end
  end

  # readpartial and read_nonblock take from one file at a time.  Running out
  # while another file is still to come is not the end of anything, so they
  # answer "" and move on; running out on the last file is an EOFError.
  def readpartial(maxlen, buffer = nil)
    read_one(:readpartial, maxlen, buffer, {})
  end

  def read_nonblock(maxlen, buffer = nil, exception: true)
    unless exception == true || exception == false
      raise ArgumentError, "expected true or false as exception: #{exception.inspect}"
    end
    read_one(:read_nonblock, maxlen, buffer, { exception: exception })
  end

  # ---- encoding ----

  def binmode
    @binmode = true
    @external = Encoding::BINARY
    @internal = nil
    @current.binmode if @current && !@current.closed?
    self
  end

  def binmode?
    @binmode
  end

  def external_encoding
    open_first
    return @external if @external
    return @current.external_encoding if @current && !@current.closed?
    Encoding.default_external
  end

  def internal_encoding
    open_first
    return @internal if @internal
    return @current.internal_encoding if @current && !@current.closed?
    Encoding.default_internal
  end

  def set_encoding(external, internal = nil, **options)
    if external.is_a?(String) && internal.nil? && external.include?(":")
      external, internal = external.split(":", 2)
    end
    @external = external.nil? ? nil : Encoding.find(external)
    @internal = (internal.nil? || internal == "") ? nil : Encoding.find(internal)
    if @current && !@current.closed?
      @current.set_encoding(*[@external, @internal].compact, **options)
    end
    self
  end

  # ---- in-place editing ----

  def inplace_mode
    @inplace
  end

  def inplace_mode=(extension)
    @inplace = extension.nil? ? nil : String(extension)
    self
  end

  def to_write_io
    raise IOError, "not opened for writing" unless @output
    @output
  end

  def write(*args)
    to_write_io.write(*args)
  end

  def print(*args)
    to_write_io.print(*args)
  end

  def printf(*args)
    to_write_io.printf(*args)
  end

  def putc(object)
    to_write_io.putc(object)
  end

  def puts(*args)
    to_write_io.puts(*args)
  end

  private

  # Points this ARGF at an array that already exists, so that ARGF.argv is
  # ARGV itself rather than a copy of it - what is left in it is what ARGF has
  # still to read.
  def replace_argv!(argv)
    @argv = argv
    self
  end

  def open_first
    open_next if @spent && !@ended && @current.nil?
  end

  # Ensures there is something to read, moving through files as they run out.
  # False means every file has been read.
  def prepare_read
    loop do
      if @spent
        return false unless open_next
      end
      if @current.closed?
        @spent = true
        next
      end
      return true unless @current.eof?
      leave_current
    end
  end

  # Opens the next file, or answers false and leaves the last one in place.
  def open_next
    if @argv.empty?
      if @started
        @ended = true
        return false
      end
      @started = true
      set_current($stdin, "-")
      return true
    end

    @started = true
    name = @argv.shift
    name = name.to_path if name.respond_to?(:to_path)
    name = String(name)

    if name == "-"
      set_current($stdin, name)
    else
      set_current(open_input(name), name)
    end
    true
  end

  def open_input(name)
    if @inplace
      start_inplace(name)
    else
      File.open(name, @binmode ? "rb" : "r")
    end
  end

  def set_current(io, name)
    @current = io
    @current_name = name
    @spent = false
    io.binmode if @binmode && !io.equal?($stdin)
    if @external && !io.equal?($stdin)
      io.set_encoding(*[@external, @internal].compact)
    end
  end

  # Closes the current file and marks it spent, keeping it and its name for
  # ARGF.file and ARGF.filename to go on answering with.
  def leave_current
    if @current && !@current.closed? && !@current.equal?($stdin)
      @current.close
    end
    finish_inplace
    @spent = true
  end

  def count_line(line)
    return nil if line.nil?
    @lineno += 1
    $. = @lineno
    line
  end

  def clear_buffer(buffer)
    return nil if buffer.nil?
    buffer = String(buffer)
    buffer.replace("")
    buffer
  end

  def read_one(operation, maxlen, buffer, options)
    buffer = clear_buffer(buffer)
    arguments = buffer ? [maxlen, buffer] : [maxlen]

    loop do
      unless prepare_current
        raise EOFError, "end of file reached"
      end

      begin
        if options.empty?
          return @current.send(operation, *arguments)
        else
          return @current.send(operation, *arguments, **options)
        end
      rescue EOFError
        leave_current
        raise EOFError, "end of file reached" if @argv.empty?
        return buffer ? buffer.replace("") : +""
      end
    end
  end

  # A current file, open, whether or not there is anything left in it.  The
  # partial reads must not ask whether it is at the end: on standard input that
  # waits for data, which is the one thing read_nonblock is there to avoid.
  # They find out by trying instead.
  def prepare_current
    loop do
      if @spent
        return false unless open_next
      end
      return true unless @current.closed?
      @spent = true
    end
  end

  # In-place editing: the file being read is moved aside and a new one takes
  # its name, with $stdout pointed at it for as long as that file is current.
  def start_inplace(name)
    if @inplace.empty?
      backup = name + ".ARGF-inplace"
    else
      backup = name + @inplace
    end

    File.delete(backup) if File.exist?(backup)
    File.rename(name, backup)
    @backup_name = @inplace.empty? ? backup : nil

    input = File.open(backup, @binmode ? "rb" : "r")
    @output = File.open(name, @binmode ? "wb" : "w")
    @saved_stdout = $stdout
    $stdout = @output
    input
  end

  def finish_inplace
    return unless @output
    $stdout = @saved_stdout if @saved_stdout
    @saved_stdout = nil
    @output.close unless @output.closed?
    @output = nil
    if @backup_name
      File.delete(@backup_name) if File.exist?(@backup_name)
      @backup_name = nil
    end
  end
end

# CRuby calls the class "ARGF.class", which is not a constant name any amount
# of const_set will accept - it names the class directly in C.  Answering it
# from the three methods that report a name is as close as Ruby gets.
class << ARGF_CLASS
  def name
    "ARGF.class"
  end
  alias_method :to_s, :name
  alias_method :inspect, :name
end

Object.send(:remove_const, :ARGF) if Object.const_defined?(:ARGF, false)
ARGF = ARGF_CLASS.new
ARGF.send(:replace_argv!, ARGV)
# ruby -i[extension] asks for in-place editing; CRuby reports the extension as
# $-i, and the option parser leaves it there for ARGF to pick up.
ARGF.inplace_mode = $-i if $-i
Object.send(:remove_const, :ARGF_CLASS)
