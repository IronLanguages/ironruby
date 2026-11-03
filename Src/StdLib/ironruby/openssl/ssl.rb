# OpenSSL::SSL::SSLSocket over OpenSSL::SSL::Transport__, which is a .NET
# SslStream on the socket inside the Ruby TCPSocket it is handed.
#
# What that costs, compared with MRI:
#
#   * SslStream drives the socket itself, so "would block" is about SslStream's
#     operations, not the socket: #connect_nonblock, #read_nonblock and
#     #write_nonblock start one and answer :wait_readable / :wait_writable (or
#     raise SSLErrorWaitReadable / SSLErrorWaitWritable) while it runs.
#     IO.select, IO#wait_readable on #to_io and NIO::Selector ask the transport,
#     which is the TCP socket's readiness hook, so they see it finish - which is
#     how net/protocol applies open_timeout, read_timeout and write_timeout, for
#     the standard library's Net and RubyGems' Gem::Net alike.
#   * the blocking calls (#connect, #sysread, #syswrite) wait for such an
#     operation; #__connect(timeout), #__read_timeout= and #__write_timeout=
#     bound the wait, and running out raises IO::TimeoutError and leaves the
#     connection unusable.
#   * the chain is built by .NET: against the system trust store, or, when the
#     context has #ca_file, #ca_path or #cert_store, against the self-signed
#     certificates in those (and the system store as well for a cert_store that
#     had set_default_paths called on it); a certificate also has to be good for
#     TLS server authentication.  #verify_callback is called for each
#     certificate of the chain, root first, once per error (or once with
#     preverify_ok true), and has the last word.
#   * server sockets (SSLServer, #accept) are not implemented.

module OpenSSL
  module SSL
    class SSLError < OpenSSLError; end unless const_defined?(:SSLError, false)

    # What a non-blocking SSL read or write raises when the handshake needs the other
    # direction first. IronRuby's SSL sockets are blocking, so nothing here raises these - but
    # `rescue OpenSSL::SSL::SSLErrorWaitReadable` is written unconditionally by libraries that
    # do non-blocking IO, and a missing constant makes the file that mentions it fail to load.
    # aws-sdk-core's Seahorse HTTP handler is one, so every aws-sdk-* gem needs them.
    class SSLErrorWaitReadable < SSLError
      include IO::WaitReadable
    end

    class SSLErrorWaitWritable < SSLError
      include IO::WaitWritable
    end

    OP_ALL = 0x80000054
    OP_NO_SSLv2 = 0x01000000
    OP_NO_SSLv3 = 0x02000000
    OP_NO_TLSv1 = 0x04000000
    OP_NO_TLSv1_1 = 0x10000000
    OP_NO_TLSv1_2 = 0x08000000
    OP_NO_TLSv1_3 = 0x20000000
    OP_NO_COMPRESSION = 0x00020000

    TLS1_VERSION = 0x0301
    TLS1_1_VERSION = 0x0302
    TLS1_2_VERSION = 0x0303
    TLS1_3_VERSION = 0x0304

    # MRI's SSLSocket includes this and forwards the socket-level calls that an
    # SslStream does not answer itself to the underlying IO.  net/http sets
    # TCP_NODELAY through it on every connection, so without it no HTTPS request
    # gets off the ground.
    module SocketForwarder
      def addr
        to_io.addr
      end

      def peeraddr
        to_io.peeraddr
      end

      def setsockopt(level, optname, optval)
        to_io.setsockopt(level, optname, optval)
      end

      def getsockopt(level, optname)
        to_io.getsockopt(level, optname)
      end

      def fcntl(*args)
        to_io.fcntl(*args)
      end

      def closed?
        to_io.closed?
      end

      def do_not_reverse_lookup=(flag)
        to_io.do_not_reverse_lookup = flag
      end
    end

    class SSLSocket
      include SocketForwarder

      attr_reader :io, :context
      attr_accessor :hostname, :sync_close, :session

      def initialize(io, context = nil)
        unless io.respond_to?(:fileno)
          raise TypeError, "wrong argument type #{io.class} (expected IO)"
        end
        @io = io
        @context = context || SSLContext.new
        @transport = Transport__.new(io)
        @hostname = nil
        @sync_close = false
        @session = nil
        @connected = false
        @eof = false
        @sync = true
        @verify_result = OpenSSL::X509::V_OK
        @read_timeout = @write_timeout = -1
      end

      def connect
        __connect(nil)
      end

      # The handshake, given at most +timeout+ seconds (nil: no limit); running out
      # raises IO::TimeoutError.
      def __connect(timeout) # :nodoc:
        return self if @connected
        @transport.connect(@hostname.to_s, __verification, __milliseconds(timeout))
        @connected = true
        self
      end

      # Starts the handshake, or looks at the one in flight: self once it is done,
      # else SSLErrorWaitReadable, or :wait_readable with exception: false.
      # IO.select on #to_io says when to ask again.
      def connect_nonblock(exception: true)
        return self if @connected
        if @transport.connect_nonblock(@hostname.to_s, __verification) == :wait_readable
          raise SSLErrorWaitReadable, "read would block" if exception
          return :wait_readable
        end
        @connected = true
        self
      end

      # How long one blocking read or write may wait, in seconds, nil for ever.
      # Not MRI API; the non-blocking calls are bounded by whoever waits for them.
      def __read_timeout=(seconds) # :nodoc:
        milliseconds = __milliseconds(seconds)
        @transport.read_timeout = @read_timeout = milliseconds unless milliseconds == @read_timeout
      end

      def __write_timeout=(seconds) # :nodoc:
        milliseconds = __milliseconds(seconds)
        @transport.write_timeout = @write_timeout = milliseconds unless milliseconds == @write_timeout
      end

      def accept
        raise NotImplementedError,
              "OpenSSL::SSL::SSLSocket#accept is not implemented: .NET's SslStream server side is not wired up"
      end
      alias_method :accept_nonblock, :accept

      # The certificate's names (subjectAltName DNS and IP entries, the common
      # name failing those) against +hostname+, as MRI does.  net/http calls this
      # for an IP address too, which gets no SNI name and so no check during the
      # handshake.
      def post_connection_check(hostname)
        return true if @context.respond_to?(:verify_mode) &&
                       @context.verify_mode == OpenSSL::SSL::VERIFY_NONE
        raise SSLError, "hostname \"#{hostname}\" does not match the server certificate" unless
          @transport.matches_hostname?(hostname.to_s)
        true
      end

      def peer_cert
        der = @transport.peer_cert
        der.nil? ? nil : OpenSSL::X509::Certificate.new(der)
      end

      def peer_cert_chain
        cert = peer_cert
        cert.nil? ? nil : [cert]
      end

      def ssl_version
        @transport.protocol
      end

      # MRI's #cipher is [name, protocol, secret bits, algorithm bits]; .NET
      # reports the suite's name and nothing else about it.
      def cipher
        name = @transport.cipher
        return nil if name.nil?
        [name, ssl_version, 0, 0]
      end

      def state
        @connected ? "SSLOK " : "SSLNONE"
      end

      def pending
        @transport.pending
      end

      # V_OK for a connection that stands: a verify_callback that accepts a
      # certificate with an error clears the error, as on MRI.
      def verify_result
        @verify_result
      end

      def sysread(length, buffer = nil)
        connect unless @connected
        data = @transport.read(length)
        if data.nil?
          @eof = true
          raise EOFError, "end of file reached"
        end
        buffer.nil? ? data : buffer.replace(data)
      end

      def read_nonblock(length, buffer = nil, exception: true)
        connect unless @connected
        case data = @transport.read_nonblock(length)
        when :wait_readable
          raise SSLErrorWaitReadable, "read would block" if exception
          :wait_readable
        when nil
          @eof = true
          raise EOFError, "end of file reached" if exception
          nil
        else
          buffer.nil? ? data : buffer.replace(data)
        end
      end

      def readpartial(length, buffer = nil)
        sysread(length, buffer)
      end

      def read(length = nil, buffer = nil)
        result = "".b
        if length.nil?
          loop do
            chunk = (sysread(16384) rescue nil)
            break if chunk.nil?
            result << chunk
          end
        else
          while result.bytesize < length
            chunk = (sysread(length - result.bytesize) rescue nil)
            break if chunk.nil?
            result << chunk
          end
          return nil if result.empty? && length > 0
        end
        buffer.nil? ? result : buffer.replace(result)
      end

      def gets(separator = $/)
        line = "".b
        while (byte = (sysread(1) rescue nil))
          line << byte
          break if separator && line.end_with?(separator)
        end
        line.empty? ? nil : line
      end

      def each_line(separator = $/)
        while (line = gets(separator))
          yield line
        end
        self
      end

      def syswrite(data)
        connect unless @connected
        @transport.write(data)
      end

      def write(*data)
        data.inject(0) { |total, item| total + syswrite(item.to_s) }
      end

      # How much of +data+ went out - 16 KB records while they go out at once, at most
      # 1 MB - or SSLErrorWaitWritable
      # (:wait_writable with exception: false) while the last part is still going.
      def write_nonblock(data, exception: true)
        connect unless @connected
        written = @transport.write_nonblock(data.to_s)
        if written == :wait_writable
          raise SSLErrorWaitWritable, "write would block" if exception
          return :wait_writable
        end
        written
      end

      def <<(data)
        syswrite(data.to_s)
        self
      end

      def print(*args)
        args.each { |arg| syswrite(arg.to_s) }
        nil
      end

      def puts(*args)
        return syswrite("\n") if args.empty?
        args.each do |arg|
          text = arg.to_s
          syswrite(text.end_with?("\n") ? text : text + "\n")
        end
        nil
      end

      def flush
        self
      end

      def sync
        @sync
      end

      def sync=(value)
        @sync = value
      end

      def eof?
        @eof
      end
      alias_method :eof, :eof?

      def closed?
        @transport.nil? || @io.closed?
      end

      def close
        @transport.close unless @transport.nil?
        @io.close if @sync_close && !@io.closed?
        nil
      end

      def fileno
        @io.fileno
      end

      def to_io
        @io
      end

      def inspect
        "#<#{self.class}:0x%08x>" % (object_id << 1)
      end

      # net/http asks for these on the context object, not here, but code that
      # configures the socket directly expects them to exist.
      def alpn_protocol
        nil
      end

      def npn_protocol
        nil
      end

      private

      # Whether the certificate is checked at all, with the transport told what to
      # check it against.  Once per socket: a handshake in flight reads it.
      def __verification
        return @verify unless @verify.nil?
        @verify = @context.respond_to?(:verify_mode) &&
                  @context.verify_mode != OpenSSL::SSL::VERIFY_NONE
        if @verify
          anchors, system = __trust_anchors
          @transport.trust(anchors, system) unless anchors.nil?
          callback = @context.respond_to?(:verify_callback) && @context.verify_callback
          @transport.verify_callback = callback ? __verifier(callback) : nil
        end
        @verify
      end

      # Seconds as the transport's milliseconds: -1 for nil (no limit), and at least 1,
      # since 0 would mean "no limit" to the socket underneath.
      def __milliseconds(seconds)
        return -1 if seconds.nil?
        seconds = Float(seconds)
        return -1 if seconds.infinite?
        [[(seconds * 1000).ceil, 1].max, 0x7fffffff].min
      end

      # The certificates the context trusts in place of the system store, and whether
      # the system store counts as well; nil when the context names no CAs.  As on
      # MRI, ca_file and ca_path add to cert_store.
      def __trust_anchors
        ca_file = @context.respond_to?(:ca_file) ? @context.ca_file : nil
        ca_path = @context.respond_to?(:ca_path) ? @context.ca_path : nil
        store = @context.respond_to?(:cert_store) ? @context.cert_store : nil
        return nil if ca_file.nil? && ca_path.nil? && store.nil?
        anchors = OpenSSL::X509::Store.new
        anchors.add_file(ca_file) unless ca_file.nil?
        anchors.add_path(ca_path) unless ca_path.nil?
        certificates = anchors.__certificates
        system = false
        if store.respond_to?(:__certificates)
          certificates = store.__certificates + certificates
          system = store.__default_paths?
        end
        [certificates.map(&:__clr).compact, system]
      end

      # The transport's callback for SSLContext#verify_callback.  OpenSSL calls it for
      # each certificate, from the root down to the server's own: once with
      # preverify_ok true if the certificate passed, otherwise once per error, false
      # and an X509::StoreContext saying which.  .NET has checked the whole chain by
      # the time this runs, so the errors come from its X509ChainElement statuses.  It
      # runs on the thread that finished the handshake.
      def __verifier(callback)
        lambda do |chain, policy_errors|
          elements = chain.nil? ? [] : chain.ChainElements.to_a
          certificates = elements.map do |element|
            OpenSSL::X509::Certificate.new(IronRubyOpenSSL__.bytes(element.Certificate.RawData))
          end
          errors = elements.each_with_index.map do |element, depth|
            IronRubyOpenSSL__.x509_errors(element.ChainElementStatus, certificates[depth], depth, certificates.size)
          end
          next false if certificates.empty?
          # A chain that does not reach a trusted root says so on the chain, not on an element.
          if policy_errors & 4 != 0 && errors.all?(&:empty?)
            errors[-1] = [IronRubyOpenSSL__.x509_error(chain.ChainStatus, true)]
          end
          if policy_errors & 2 != 0
            errors[0] += [[OpenSSL::X509::V_ERR_HOSTNAME_MISMATCH, "hostname mismatch"]]
          end
          (certificates.size - 1).downto(0).all? do |depth|
            (errors[depth].empty? ? [[OpenSSL::X509::V_OK, "ok"]] : errors[depth]).all? do |code, message|
              __verify_call(callback, OpenSSL::X509::StoreContext.__for_callback(certificates, depth, code, message))
            end
          end
        end
      end

      # One call of the verify_callback, as CRuby's ossl_verify_cb_call: only true
      # accepts, and clears the error; anything else rejects, with
      # V_ERR_CERT_REJECTED if there was no error.  An exception in the callback is a
      # rejection too, not an exception out of #connect.
      def __verify_call(callback, context)
        accepted = begin
          callback.call(context.error == OpenSSL::X509::V_OK, context) == true
        rescue Exception
          warn "exception in verify_callback is ignored"
          false
        end
        if accepted
          context.error = OpenSSL::X509::V_OK
        else
          context.error = OpenSSL::X509::V_ERR_CERT_REJECTED if context.error == OpenSSL::X509::V_OK
          @verify_result = context.error
        end
        accepted
      end
    end

    # MRI's SSLServer wraps a TCPServer; .NET's server-side SslStream is not
    # wired up here, so the class exists only to be named.
    class SSLServer
      def initialize(*)
        raise NotImplementedError,
              "OpenSSL::SSL::SSLServer is not implemented: only the client side of SslStream is wired up"
      end
    end
  end
end
