# pty is a C extension in MRI (ext/pty/pty.c).  Here the syscalls - allocating
# the pseudo-terminal and starting a child on it - live in the Pty library
# (Src/Libraries/Pty/Pty.cs) and everything Ruby-shaped is this file, the same
# split io/console makes with Termios.
#
# The one real departure is how the child gets its terminal.  MRI forks, and the
# child calls setsid(2), takes the slave as its controlling terminal and execs.
# .NET cannot fork, so the child is started with posix_spawn(3) - the primitive
# behind Process.spawn - asking for setsid and opening the slave onto descriptors
# 0, 1 and 2; Pty.cs has the details.  Argument handling (an env hash, a command
# line or a command and its arguments, [cmd, argv0], the spawn options) is
# Process.spawn's own, as it is MRI's rb_execarg.
#
# MRI does not build ext/pty on Windows, where there are no ptys, so there
# `require "pty"` is a LoadError there too.

if File::ALT_SEPARATOR
  error = LoadError.new("cannot load such file -- pty")
  error.instance_variable_set(:@path, "pty")
  raise error
end

load_assembly 'IronRuby.Libraries', 'IronRuby.StandardLibrary.Pty'

module PTY
  # Raised by PTY.check(pid, true) for a child that has exited or stopped.
  class ChildExited < RuntimeError
    # The Process::Status of the child.
    def status
      @status
    end
  end

  # MRI's establishShell: $SHELL, then the login shell of $USER (or of the login
  # name), then /bin/sh.
  def self.__default_shell__
    shell = ENV["SHELL"]
    return shell if shell
    begin
      require "etc"
      user = ENV["USER"] || Etc.getlogin
      entry = user && Etc.getpwnam(user)
      return entry.shell if entry && entry.shell && !entry.shell.empty?
    rescue StandardError, LoadError
    end
    "/bin/sh"
  end

  def self.__integer__(value)
    return value if value.is_a?(Integer)
    raise TypeError, "no implicit conversion from nil to integer" if value.nil?
    if value.is_a?(String) || !value.respond_to?(:to_int)
      raise TypeError, "no implicit conversion of #{value.class} into Integer"
    end
    value.to_int
  end

  # Starts the child and answers [reader, writer, pid].  Separate from #spawn so
  # that the rescue below covers starting the child and not the caller's block.
  def self.__start__(args)
    args = [__default_shell__] if args.empty?

    # MRI parses the arguments with exception: disallowed, before it so much as
    # looks for the command.
    last = args.last
    if args.size > 1 && !last.is_a?(String) && last.respond_to?(:to_hash) && last.to_hash.key?(:exception)
      raise ArgumentError, "exception option is not allowed"
    end

    file, argv, envp, actions, pgroup, options = Process.__spawn_setup__(args)
    close_others = options[:close_others] ? true : false
    result = Process.__with_umask__(options[:umask]) do
      started = __spawn__(file, argv, envp, actions, pgroup, close_others)
      # An executable with no #! line is ENOEXEC to execve(2); MRI then runs it
      # with /bin/sh, as Process.spawn does.
      if started == -8 && file != "/bin/sh"
        started = __spawn__("/bin/sh", ["sh", file] + argv[1..-1], envp, actions, pgroup, close_others)
      end
      started
    end
    Process.__check__(result, file)
  rescue SystemCallError
    # MRI's child has been forked by the time the exec fails, and exits 127.
    Process.__set_last_status__(Process.__make_status__(-1, 127 << 8))
    raise
  end

  private_class_method :__default_shell__, :__integer__, :__start__

  # PTY.spawn([env,] command_line) => [r, w, pid]
  # PTY.spawn([env,] command, arg, ...) { |r, w, pid| ... } => nil
  #
  # Runs the command on a new pty, which becomes its controlling terminal and its
  # standard input, output and error.  r reads what the child writes, w writes to
  # its input; both are Files on the master side, close-on-exec, and w is sync.
  def spawn(*args)
    result = PTY.__send__(:__start__, args)
    return result unless block_given?
    pid = result[2]
    begin
      yield result
    ensure
      # MRI's pty_detach_process reaps the child if it has already exited, which
      # is what leaves $? describing it.  A child that is still running is left
      # alone, so that the caller can still Process.wait for it.
      Process.__waitpid__(pid, Process::WNOHANG)
    end
    nil
  end
  alias_method :getpty, :spawn
  module_function :spawn, :getpty

  # PTY.open => [master_io, slave_file]
  # PTY.open { |master_io, slave_file| ... } => block value
  #
  # Allocates a pty.  The master is an IO and the slave a File whose path is the
  # terminal device; in the block form both are closed afterwards.
  def self.open
    pair = __open__
    return pair unless block_given?
    begin
      yield pair
    ensure
      pair.each { |io| io.close unless io.closed? }
    end
  end

  # PTY.check(pid, raise = false) => Process::Status or nil
  #
  # nil while the child is running.  Once it has exited or stopped, its
  # Process::Status - or, with raise true, a PTY::ChildExited carrying it.
  def self.check(*args)
    # (*args) rather than (pid, raise = false), so that the arity is MRI's -1
    unless args.size == 1 || args.size == 2
      raise ArgumentError, "wrong number of arguments (given #{args.size}, expected 1..2)"
    end
    pid = __integer__(args[0])
    raise_on_change = args[1]
    reaped = Process.__waitpid__(pid, Process::WNOHANG | Process::WUNTRACED)
    return nil unless reaped.is_a?(Array)
    status = reaped[1]
    return status unless raise_on_change

    state =
      if status.stopped?
        "stopped"
      else
        begin
          Process.kill(0, pid)
          "changed"
        rescue SystemCallError
          "exited"
        end
      end
    error = ChildExited.new("pty - #{state}: #{pid}")
    error.instance_variable_set(:@status, status)
    raise error
  end
end
