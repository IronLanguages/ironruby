# io/console is a C extension in MRI (console.so): termios and ioctl on unix,
# the Win32 console API on Windows.  Here the syscalls live in the Termios
# library (Src/Libraries/Termios/Termios.cs) and everything Ruby-shaped is this
# file, the same split the Ripper library uses.
#
# What MRI's version does that this one does not: ConsoleMode is a plain struct
# of the four termios flag words rather than a typed object with named
# accessors, #pressed? is not implemented (it needs the keyboard state, which
# is a Windows-only call in MRI too), and #getch ignores :intr.  Everything
# reline and irb reach for - IO.console, #raw/#raw!/#cooked/#cooked!,
# #echo=/#echo?/#noecho, #getch, #getpass, #winsize/#winsize=, #iflush/#oflush/
# #ioflush, #cursor/#cursor=/#goto and #console_mode - is here.

load_assembly 'IronRuby.Libraries', 'IronRuby.StandardLibrary.Termios'
require 'stringio'

class IO
  # The four termios flag words plus the control characters, as tcgetattr
  # hands them over.  MRI calls the same thing IO::ConsoleMode.
  class ConsoleMode
    # The version of the io-console gem whose interface this library provides.
    VERSION = "0.8.2"

    attr_accessor :iflag, :oflag, :cflag, :lflag, :cc

    def initialize(attrs)
      @iflag, @oflag, @cflag, @lflag, @cc = *attrs
    end

    def to_a
      [@iflag, @oflag, @cflag, @lflag, @cc.dup]
    end

    def echo=(flag)
      if flag
        @lflag |= ECHO_BITS
      else
        @lflag &= ~ECHO_BITS
      end
    end

    def echo?
      (@lflag & (::Termios::ECHO | ::Termios::ECHONL)) != 0
    end

    ECHO_BITS = ::Termios::ECHO | ::Termios::ECHOE | ::Termios::ECHOK | ::Termios::ECHONL

    def dup
      ConsoleMode.new(to_a)
    end
  end

  # ------------------------------------------------------------------ private

  private

  # What MRI names in the ENOTTY it raises off a non-terminal: the path, when
  # this IO has one.  #path raises on a stream that was never opened from one.
  def __console_name__
    path
  rescue StandardError
    nil
  end

  # Sends a terminal escape and returns the stream it went to.  MRI writes to
  # the descriptor itself; a read-only IO - STDIN is one here - cannot be
  # written to, and the controlling terminal is the same device anyway.
  def __console_write__(escape)
    write(escape)
    flush
    self
  rescue IOError
    out = IO.console or raise
    out.write(escape)
    out.flush
    out
  end

  def __console_attrs__
    attrs = ::Termios.tcgetattr(self)
    raise Errno::ENOTTY, __console_name__ unless attrs
    ConsoleMode.new(attrs)
  end

  def __console_attrs__=(mode)
    ::Termios.tcsetattr(self, mode.to_a)
  end

  # cfmakeraw(3), then the two adjustments MRI's set_rawmode makes: ECHOE and
  # ECHOK off as well, and the :min/:time/:intr options folded in.
  def __make_raw__(mode, min: nil, time: nil, intr: false)
    t = ::Termios
    mode.iflag &= ~(t::IGNBRK | t::BRKINT | t::PARMRK | t::ISTRIP |
                    t::INLCR | t::IGNCR | t::ICRNL | t::IXON)
    mode.oflag &= ~t::OPOST
    mode.lflag &= ~(t::ECHO | t::ECHOE | t::ECHOK | t::ECHONL |
                    t::ICANON | t::ISIG | t::IEXTEN)
    mode.cflag &= ~(t::CSIZE | t::PARENB)
    mode.cflag |= t::CS8
    mode.cc = mode.cc.dup
    mode.cc.setbyte(t::VMIN, 1)
    mode.cc.setbyte(t::VTIME, 0)
    mode.cc.setbyte(t::VMIN, min) if min
    # time: is in seconds and VTIME in tenths of one, as MRI converts it
    mode.cc.setbyte(t::VTIME, (time * 10).to_i) if time
    if intr
      mode.iflag |= t::BRKINT
      mode.lflag |= t::ISIG
      mode.oflag |= t::OPOST
    end
    mode
  end

  def __make_cooked__(mode)
    t = ::Termios
    mode.iflag |= t::BRKINT | t::ISTRIP | t::ICRNL | t::IXON
    mode.oflag |= t::OPOST
    mode.lflag |= t::ECHO | t::ECHOE | t::ECHOK | t::ECHONL |
                  t::ICANON | t::ISIG | t::IEXTEN
    mode
  end

  # Runs the block with the terminal in the mode the block builds, restoring
  # whatever was there before.  Without a block the mode just stays on, which
  # is what the bang forms are.
  def __with_console_mode__(mode)
    saved = __console_attrs__
    self.__console_attrs__ = mode
    return self unless block_given?
    begin
      yield self
    ensure
      self.__console_attrs__ = saved
    end
  end

  public

  # ------------------------------------------------------------------- modes

  # Raw mode: no line editing, no echo, no signals unless intr: true.
  def raw(min: nil, time: nil, intr: false)
    mode = __make_raw__(__console_attrs__, min: min, time: time, intr: intr)
    if block_given?
      __with_console_mode__(mode) { |io| yield io }
    else
      __with_console_mode__(mode)
    end
  end

  # Raw mode, left on.
  def raw!(min: nil, time: nil, intr: false)
    self.__console_attrs__ = __make_raw__(__console_attrs__, min: min, time: time, intr: intr)
    self
  end

  def cooked
    mode = __make_cooked__(__console_attrs__)
    if block_given?
      __with_console_mode__(mode) { |io| yield io }
    else
      __with_console_mode__(mode)
    end
  end

  def cooked!
    self.__console_attrs__ = __make_cooked__(__console_attrs__)
    self
  end

  def echo=(flag)
    mode = __console_attrs__
    mode.echo = flag
    self.__console_attrs__ = mode
    flag
  end

  def echo?
    __console_attrs__.echo?
  end

  # With a block: echo off for the duration.  Without one MRI has no #noecho,
  # so neither does this.
  def noecho
    mode = __console_attrs__
    mode.echo = false
    __with_console_mode__(mode) { |io| yield io }
  end

  # The whole terminal mode, for saving and restoring by hand.
  def console_mode
    __console_attrs__
  end

  def console_mode=(mode)
    self.__console_attrs__ = mode
    mode
  end

  def ttymode
    raise ArgumentError, 'no block given' unless block_given?
    __with_console_mode__(__console_attrs__) { |io| yield io }
  end

  # -------------------------------------------------------------------- size

  # [rows, columns] of the terminal this IO is connected to.  Off one, MRI raises
  # ENOTTY from ioctl, and on Windows EBADF - the error GetConsoleScreenBufferInfo
  # leaves for a handle that is no console output, STDIN's included.
  def winsize
    size = ::Termios.winsize(self)
    raise (::Termios.supported? ? Errno::ENOTTY : Errno::EBADF), __console_name__ unless size
    size
  end

  def winsize=(size)
    rows, columns = size
    ::Termios.set_winsize(self, rows.to_i, columns.to_i)
    size
  end

  # ------------------------------------------------------------------ flushing

  def iflush
    ::Termios.tcflush(self, ::Termios::TCIFLUSH)
    self
  end

  def oflush
    ::Termios.tcflush(self, ::Termios::TCOFLUSH)
    self
  end

  def ioflush
    ::Termios.tcflush(self, ::Termios::TCIOFLUSH)
    self
  end

  # ------------------------------------------------------------------ reading

  # One character, unechoed and without waiting for a newline.
  def getch(min: nil, time: nil, intr: false)
    raw(min: min, time: time, intr: intr) do |io|
      io.getc
    end
  end

  # Prompts on the terminal (on $stderr when reading STDIN, as MRI does) and
  # reads a line without echoing it; the line comes back without its newline.
  def getpass(prompt = nil)
    wio = equal?(STDIN) ? $stderr : self
    wio.write(prompt) if prompt
    wio.flush
    begin
      # a line is "\n"-terminated whatever $/ says, as MRI's rb_io_gets reads it
      str = noecho { gets("\n") }
    ensure
      wio.print("\n")
      wio.flush
    end
    str&.chomp("\n")
  end

  # ------------------------------------------------------------------- cursor

  # [row, column] of the cursor, zero-based, asked for with the DSR escape and
  # read back in raw mode.
  def cursor
    raw do |io|
      io.__send__(:__console_write__, "\e[6n")
      buf = +''
      # MRI blocks here; a terminal that never answers the DSR query would
      # hang the process, so give up after a second and report nil.
      while io.wait_readable(1) && (c = io.getc)
        buf << c
        break if c == 'R'
      end
      m = buf.match(/\e\[(\d+);(\d+)R/)
      m ? [m[1].to_i - 1, m[2].to_i - 1] : nil
    end
  end

  def cursor=(position)
    row, column = position
    __console_write__("\e[#{row.to_i + 1};#{column.to_i + 1}H")
    position
  end

  def goto(row, column)
    self.cursor = [row, column]
    self
  end

  def goto_column(column)
    __console_write__("\e[#{column.to_i + 1}G")
    self
  end

  def erase_screen(mode = 2)
    __console_write__("\e[#{mode.to_i}J")
    self
  end

  def erase_line(mode = 2)
    __console_write__("\e[#{mode.to_i}K")
    self
  end

  def scroll_forward(lines = 1)
    __console_write__("\e[#{lines.to_i}S")
    self
  end

  def scroll_backward(lines = 1)
    __console_write__("\e[#{lines.to_i}T")
    self
  end

  def clear_screen
    __console_write__("\e[H\e[2J")
    self
  end

  def beep
    __console_write__("\a")
    self
  end

  # MRI implements #pressed? with the Windows keyboard state API and does not
  # have it on unix at all.
  def pressed?(_key)
    raise NotImplementedError, 'pressed?() function is unimplemented on this machine'
  end

  # -------------------------------------------------------------- IO.console

  class << self
    # The process's controlling terminal, or nil when there is not one (a
    # pipeline, a cron job, mspec redirecting everything).  MRI caches the IO
    # and reopens it if it was closed; so does this.
    def console(sym = nil, *args)
      if @console && !@console.closed?
        io = @console
      else
        io = @console = __open_console__
      end
      return nil unless io
      return io unless sym
      io.__send__(sym, *args)
    end

    # IO.console_size is not defined here, exactly as in MRI: it belongs to
    # io/console/size, which is a separate file and defines it on top of this
    # one (irb's debugger UI requires it by name).

    private

    def __open_console__
      return nil unless ::Termios.supported?
      io = begin
        File.open('/dev/tty', 'r+')
      rescue SystemCallError
        # A warm-start worker (Util/warm) has no controlling terminal of its own; it is told
        # the name of its client's.
        raise unless @__warm_console__
        File.open(@__warm_console__, 'r+')
      end
      return io if ::Termios.tty?(io)
      io.close
      nil
    rescue SystemCallError
      nil
    end
  end
end

# MRI adds these to IO::generic_readable, which StringIO includes: the
# console-free meanings of #getch (#getc) and #getpass (a prompted #gets).
class StringIO
  def getch(*args)
    getc(*args)
  end

  def getpass(prompt = nil)
    write(prompt) if prompt
    flush
    str = gets
    puts
    str&.chomp
  end
end
