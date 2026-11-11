# io/nonblock - the O_NONBLOCK descriptor flag.
#
# Where the stream has an operating system descriptor this asks the kernel, so
# it gives the right answer for a descriptor nobody here opened. Not every
# stream has one: IronRuby's IO.pipe is not backed by a FileStream, so
# IO#GetNativeDescriptor answers -1 for a pipe and there is no flag to read. For
# those the value is remembered instead, which is a weaker answer.
#
# A pipe answers what it was last told: MRI opens its pipes non-blocking, so
# until #nonblock= says otherwise one reports true, though its descriptor here
# was opened blocking - IO's own reads and writes wait either way. Setting the
# flag still sets it on the descriptor, which is what #syswrite goes by.

class IO
  # F_GETFL and F_SETFL are the same on every Unix; O_NONBLOCK is not (0x800 on Linux, 4 on
  # macOS and the BSDs), and Linux's bit on a Mac is O_EXCL's, which F_SETFL ignores.
  NONBLOCK_GET__ = 3   # F_GETFL
  NONBLOCK_SET__ = 4   # F_SETFL
  NONBLOCK_FLAG__ = ::File::NONBLOCK

  def __nonblock_native__?
    (::IO.GetNativeDescriptor(self) rescue -1) >= 0
  end
  private :__nonblock_native__?

  def __nonblock_pipe__?
    stat.pipe? rescue false
  end
  private :__nonblock_pipe__?

  def nonblock?
    if __nonblock_pipe__?
      defined?(@__nonblock__) ? !!@__nonblock__ : true
    elsif __nonblock_native__?
      (fcntl(NONBLOCK_GET__, 0) & NONBLOCK_FLAG__) != 0
    else
      defined?(@__nonblock__) ? !!@__nonblock__ : false
    end
  end

  def nonblock=(value)
    if __nonblock_native__?
      flags = fcntl(NONBLOCK_GET__, 0)
      flags = value ? (flags | NONBLOCK_FLAG__) : (flags & ~NONBLOCK_FLAG__)
      fcntl(NONBLOCK_SET__, flags)
    end
    @__nonblock__ = !!value
    value
  end

  # With a block, sets the flag for the duration and puts it back afterwards.
  def nonblock(value = true)
    return (self.nonblock = value) unless block_given?
    previous = nonblock?
    self.nonblock = value
    begin
      yield
    ensure
      self.nonblock = previous
    end
  end
end
