# The kernel of the browser host (Interop.cs runs this file as the main program, so the core
# prelude ruby4.rb is loaded first, exactly as for `ir --disable-gems`).

module IronRubyWasm
  BOOT_FILE = __FILE__
  # The binding of a session after a reset is the top level of this file, loaded anew each time
  # (see IronRubyWasm.reset). The host writes it next to this one: `IronRubyWasm.session = binding`.
  SESSION_FILE = File.join(File.dirname(__FILE__), "session.rb")
  SESSION_FRAME = "in '<top (required)>'"

  class << self
    attr_reader :count
    attr_writer :session

    # A fresh top-level binding: locals defined by one run stay visible to the next, like the
    # cells of a notebook or the lines of irb. reset starts over (methods and constants stay).
    def reset
      @count = 0
      # The first session runs in TOPLEVEL_BINDING itself (frames read "<main>"); a reset gets a
      # binding of its own with no locals, so the old ones are gone but methods and constants stay.
      # Not a block's binding inside TOPLEVEL_BINDING: a block sees the locals around it, and `x`
      # of the first session was still there after a reset. Nor a method body's on main: a `def`
      # there makes a public method, which every object then answers to, where the top level makes
      # a private one. A loaded file's top level has main, no locals and private definitions.
      if @binding
        load SESSION_FILE
        @binding = @session
      else
        @binding = TOPLEVEL_BINDING
      end
      nil
    end

    # => [:ok, inspect] | [:exit, status] | [:error, class name, message, backtrace]
    def run(code, file = nil)
      @count += 1
      file ||= "cell#{@count}"
      begin
        value = @binding.eval(code, file, 1)
        [:ok, safe_inspect(value)]
      rescue SystemExit => e
        [:exit, e.status]
      rescue Exception => e
        [:error, (e.class.name || e.class.inspect), safe_message(e), clean_backtrace(e)]
      ensure
        begin; $stdout.flush; rescue Exception; end
        begin; $stderr.flush; rescue Exception; end
        begin; STDOUT.flush; rescue Exception; end
      end
    end

    private

    def safe_inspect(value)
      value.inspect.to_s
    rescue Exception => e
      "#<#{value.class} (inspect raised #{e.class}: #{e.message})>"
    end

    def safe_message(e)
      e.message.to_s
    rescue Exception
      ""
    end

    def clean_backtrace(e)
      # leave out the kernel's own frames and the host's CLR frames (":0:in 'Run'", ...)
      # and a session after a reset reads "<main>" like the first one
      lines = (e.backtrace || []).reject { |line| line.start_with?(BOOT_FILE) || line.start_with?(":0:") }
      # The session's own frame is the outermost one with that label; a file the code required has
      # the same label further in and keeps it. (The first session's frame is <main> already.)
      unless @binding.equal?(TOPLEVEL_BINDING)
        i = lines.rindex { |line| line.include?(SESSION_FRAME) }
        lines[i] = lines[i].sub(SESSION_FRAME, "in '<main>'") if i
      end
      lines
    end
  end

  reset
end
