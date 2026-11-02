# frozen_string_literal: true
#
# puma's native half, provided by IronRuby itself - what `require "puma/puma_http11"` loads in
# place of the C extension ext/puma_http11 builds.
#
# `gem install puma` cannot work here: the extension is C (a Ragel-generated HTTP parser, and
# MiniSSL over OpenSSL's memory BIOs).  IronRuby ships puma's Ruby half unchanged
# (Src/StdLib/ironruby/puma.rb and puma/, from puma 8.0.2) and implements the native half in C#:
#
#   * Puma::HttpParser and Puma::HttpParserError, in Src/Libraries/Puma/PumaHttp11.cs, a port of
#     the same state machine with the C extension's messages and limits.
#   * Puma::MiniSSL::Engine, SSLContext and SSLError, puma's TLS, in Src/Libraries/Puma/MiniSsl.cs:
#     the engine is a server SslStream over an in-memory stream that #inject fills and #extract
#     drains, so Puma.ssl? is true and ssl:// binds serve TLS.  cert/key files or cert_pem/key_pem,
#     key_password_command, ca with verify_mode none, peer or force_peer, no_tlsv1 and no_tlsv1_1
#     are honoured; ssl_cipher_filter, ssl_ciphersuites, verification_flags and reuse are not
#     (SslStream uses the system's cipher suites and its own session cache).
#
# Not implemented:
#
#   * Cluster mode.  .NET cannot fork(2), so Process.respond_to?(:fork) is false and puma
#     refuses `workers N` with its own "worker mode not supported on ironruby on this
#     platform" message.  Single mode - one process, a thread pool - is fully supported.

load_assembly 'IronRuby.Libraries', 'IronRuby.StandardLibrary.Puma'
