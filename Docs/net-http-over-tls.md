# net/http over TLS: diagnosis and proposed fix

Status: both stages below are implemented; stage 2 replaced stage 1's net/protocol
overrides, so upstream's `net/protocol` runs unmodified. Measured on IronRuby 4.0.1,
Windows 11, .NET 10.0.12, 2026-10-23. The tables and "Why" describe the state before
stage 1; afterwards `Util/net-http-tls-check.rb` reports no failures for https either,
with or without `CA=1`.

## The suspicion

`net/http` has a reputation for being undependable on Windows, put down to `IO.select`
on a socket being `poll(2)` there. Applications have worked around it by calling .NET's
`HttpClient` instead.

## What is actually the case

`Util/net-http-tls-check.rb` against `Util/net-http-tls-server.rb` (CRuby) runs keep-alive
GETs and POSTs from 10 bytes to 300 KB, chunked bodies, a fresh connection per request,
and one request that has to end in `Net::ReadTimeout`:

| | plain HTTP | HTTPS, `VERIFY_NONE` | HTTPS, `ca_file:` |
|---|---|---|---|
| CRuby 4.0 | no failures | no failures | no failures |
| IronRuby | no failures | **`read_timeout` never fires** | **every connection refused** |

- **Plain sockets are fine.** `IO.select`, `IO#wait_readable` and
  `read_nonblock` on a socket go through `Socket.Select` on Windows
  (`Src/Libraries/Builtins/IoReadiness.cs`, `BlockWindows`), not `poll(2)`, and
  `net/http`'s read timeout works. What *is* `poll(2)` on Windows is a pipe
  (README, "What Windows does not have yet"); the suspicion mixes the two up.
- **Over TLS nothing times out.** A server that stops answering leaves the caller
  blocked for ever: no `read_timeout`, no `write_timeout`, and no `open_timeout`
  for the TLS handshake. For a service that calls an API in a loop, that is a hang.
- **`ca_file`, `ca_path`, `cert_store` and `verify_callback` are ignored.** Only the
  system trust store counts, so a private CA or a self-signed test server cannot be
  verified, only switched off with `VERIFY_NONE`.

The data itself arrives intact over TLS: no corruption and no short reads were seen.

## Why

`OpenSSL::SSL::SSLSocket` (`Src/StdLib/ironruby/openssl/ssl.rb`) runs on
`OpenSSL::SSL::Transport__` (`Src/Libraries/OpenSSL/OpenSSL.cs`), an `SslStream` over
the TCP socket, which `Transport__.Create` switches to blocking mode. The header of
`ssl.rb` says what follows from that: `#read_nonblock`, `#write_nonblock` and
`#connect_nonblock` block, and never return `:wait_readable`/`:wait_writable`.

`Net::BufferedIO#rbuf_fill` (`net/protocol.rb`) applies `read_timeout` only on the
`:wait_readable` path:

```ruby
case rv = @io.read_nonblock(BUFSIZE, tmp, exception: false)
when String then ...
when :wait_readable
  (io = @io.to_io).wait_readable(@read_timeout) or raise Net::ReadTimeout.new(io)
```

An `SSLSocket` never takes that path, so `read_nonblock` sits in `SslStream.Read` until
data comes. `ssl_socket_connect` (handshake under `open_timeout`) and `write_nonblock`
(under `write_timeout`) have the same shape. `Transport__.Validate` returns
`errors == SslPolicyErrors.None` and never looks at the context's CA settings.

## Proposed fix

There are two stages. The first is small and covers what a client needs; the second
gives real non-blocking I/O.

### Stage 1: timeouts on the blocking calls, and the context's CAs (recommended next)

1. **Read and write timeouts.** `Transport__` gets `read_timeout=` and `write_timeout=`
   (milliseconds, -1 for none), which set `SslStream.ReadTimeout`/`WriteTimeout`.
   They delegate to the inner `NetworkStream`, which means `Socket.ReceiveTimeout`/
   `SendTimeout`. A read that times out throws an `IOException` whose inner
   `SocketException` is `TimedOut`. `Transport__.read`/`write` turn that into
   `IO::TimeoutError`. An `SslStream` is unusable after a timed-out read, so the
   transport marks itself broken and every later call raises.
2. **Where the timeout comes from.** IronRuby keeps a small override of
   `Net::BufferedIO` in its own stdlib layer (`Src/StdLib/ironruby/`, loaded after
   `net/protocol`). When `@io` is an IronRuby `SSLSocket`, `rbuf_fill` sets the
   transport's read timeout from `@read_timeout` before the read and maps
   `IO::TimeoutError` to `Net::ReadTimeout`. `write0` does the same with
   `@write_timeout` and `Net::WriteTimeout`. The upstream file stays untouched, as
   with the other stdlib shims.
3. **Handshake timeout.** `Transport__.connect` takes an optional timeout and calls
   `AuthenticateAsClientAsync(options, cancellationToken)` with a
   `CancellationTokenSource(timeout)`, waiting on the task. `ssl_socket_connect`'s
   timeout reaches it through an override of `SSLSocket#connect_nonblock`, or through
   `net/http` passing `open_timeout` (the override above can do that in `connect`).
4. **CA settings.** `Validate` builds the chain itself when the context has
   `ca_file`, `ca_path` or `cert_store`: `X509ChainPolicy.TrustMode =
   X509ChainTrustMode.CustomRootTrust` with those certificates in `CustomTrustStore`,
   then `chain.Build`, plus the name check SslStream reported (`RemoteCertificateNameMismatch`).
   `verify_callback`, when set, is called with the result and an `X509StoreContext`
   holding the chain, and it has the last word, as in MRI.

As built:

- `Transport__#read_timeout=`/`#write_timeout=` (ms, -1 for none) and
  `#connect(hostname, verify, timeout_ms)`, which runs `AuthenticateAsClientAsync` under a
  `CancellationTokenSource`. A timeout raises `IO::TimeoutError` and marks the transport
  broken; every later read or write raises `SSLError` ("SSL connection is unusable").
  Since stage 2 these bound the blocking calls only (`SSLSocket#connect`, `#sysread`,
  `#syswrite`, through `#__connect`, `#__read_timeout=`, `#__write_timeout=`).
- Stage 1 also had `Src/StdLib/ironruby/net/protocol.rb` prepend overrides of
  `ssl_socket_connect`, `rbuf_fill` and `write0` that handed net/protocol's timeouts to
  those blocking calls, for `Net` and RubyGems' `Gem::Net`. Stage 2 made them unnecessary
  and they are gone: see below.
- `Transport__#trust(certificates, system)`: the self-signed certificates from `ca_file`,
  `ca_path` and `cert_store` are the roots, the others go in `ExtraStore`, as in
  `X509::Store#verify`. `system` (a `cert_store` that had `set_default_paths` called) also
  accepts a chain the system store trusts.
  The chain has to be good for TLS server authentication (`ApplicationPolicy` serverAuth), as
  SslStream's own check requires.
- `Transport__#verify_callback=` gets the chain and the `SslPolicyErrors`; `SSLSocket` calls
  `verify_callback` for each certificate, root first, once per error (or once with
  `preverify_ok` true), with an `X509::StoreContext`, following CRuby's `ossl_verify_cb_call`:
  only `true` accepts and clears the error, the first rejection ends it, and an exception in
  the callback is a rejection with a warning. It runs on the thread that finishes the handshake.
- `Thread#raise` or `Timeout.timeout` during the handshake cancels it, marks the transport
  broken, and raises the interrupting exception rather than an `SSLError`.
- With no SNI name (net/http connecting to an IP address) the handshake ignores
  `RemoteCertificateNameMismatch`, and `post_connection_check` checks the name against the
  certificate (`X509Certificate2.MatchesHostname`, IP SANs included; without a subjectAltName
  the common name, for an IP address too, as MRI's `verify_certificate_identity`). Before, it
  checked nothing.

Risk: small. Blocking I/O stays as it is, only bounded. What it does not give is real
non-blocking `read_nonblock`, so `IO.select([ssl_socket])` still says "readable" only
when the TCP socket is. A reader that selects first and then reads can block in the
middle of a TLS record. `net/http` doesn't work that way.

### Stage 2: non-blocking reads over SslStream

- `Transport__.read_nonblock(length)` keeps at most one `ReadAsync` in flight. If none is
  in flight, it starts one. If that completes at once (the plaintext was already
  decrypted), it returns the data. Otherwise it returns `:wait_readable` and keeps the
  task. The next call returns the task's result once it has completed.
- Readiness has to see that task: the in-flight read may already have drained the TCP
  socket, so `Socket.Select` would wait although the data is there. `IoReadiness`
  gets a per-`RubyIO` hook ("a pending read has completed") that `Block` checks
  before it selects. It then waits on the task's handle together with the sockets,
  the way `ReadinessWaiter` already stands in for pipes. `SSLSocket#to_io` keeps
  returning the TCP socket, which carries the hook.
- `write_nonblock` and `connect_nonblock` follow the same pattern with `WriteAsync` and
  `AuthenticateAsClientAsync`.

Risk: medium. It touches `IO.select` for every caller, and the in-flight task has to be
cancelled cleanly on `close`. It is worth doing for servers and event loops (nio4r,
puma over TLS), not for a client.

As built:

- `IReadinessHook` (`Src/Libraries/Builtins/IoReadiness.cs`): `IsReady(kind)` answers true,
  false, or null ("ask the socket"), and the hook signals the `ReadinessWaiter`s registered
  with it when that answer may have changed. `IoReadiness.SetHook` keeps hooks per `RubyIO`
  in a `ConditionalWeakTable`; with no hook set, the cost is one field read. `IO.select`,
  `IO#wait*` (`IoOps.IsReady`, `WaitUntilReady`) and `NIO::Selector` ask the hook first and
  register their waiter with it. nio4r masks off the kernel's answer for what a hook owns.
- `Transport__` is the hook of the TCP socket it runs on. Every read, write and handshake is
  one of SslStream's asynchronous operations, at most one of each kind in flight:
  `#read_nonblock`, `#write_nonblock` (16 KB records handed over for as long as each goes out
  at once, up to 1 MB, so what it reports written is about what a kernel buffer holds; net/protocol
  copies the rest of the body after every partial write, so 16 KB a call made a 64 MB body cost
  gigabytes of copying) and `#connect_nonblock` start one and answer
  `:wait_readable`/`:wait_writable` while it runs; `#read`, `#write` and `#connect` start one
  or take over the one in flight, and wait for it.
- Asking the hook whether a read would return starts that read. SslStream may hold bytes,
  encrypted or decrypted, that the TCP socket no longer shows, and only a read finds them. What a
  finished read brings beyond what was asked for waits in a read-ahead, which `#pending` counts.
- `SSLSocket#read_nonblock`, `#write_nonblock` and `#connect_nonblock` raise
  `SSLErrorWaitReadable`/`SSLErrorWaitWritable` ("read would block", "write would block"), or
  answer the symbol with `exception: false`, as on MRI. net/protocol's `rbuf_fill`, `write0`
  and `ssl_socket_connect` then wait on `#to_io` with their own timeouts, so `open_timeout`,
  `read_timeout` and `write_timeout` work without any IronRuby code in net/protocol, for
  RubyGems' vendored `Gem::Net` as well.
- puma over TLS runs, on its own engine rather than `SSLServer`: puma never uses
  `OpenSSL::SSL::SSLServer`, it drives its C extension's `Puma::MiniSSL::Engine`, OpenSSL over
  memory BIOs. `Src/Libraries/Puma/MiniSsl.cs` is that engine on a server `SslStream` over an
  in-memory stream: `#inject` fills its inbound side, `#extract` drains its outbound side, the
  handshake and the reads run as SslStream's asynchronous operations, and `#read`, `#write` and
  `#extract` first wait until SslStream has used up its input, as OpenSSL would have by the time
  `SSL_read` returns. puma's own `Puma::MiniSSL::Socket` (minissl.rb) does the socket I/O, so
  the TCP socket stays an ordinary one for puma's reactor. `ssl://` binds take `cert`/`key`
  files or `cert_pem`/`key_pem`, `key_password_command`, `ca` with `verify_mode` none, peer or
  force_peer (`puma.peercert` in the env), and `no_tlsv1`/`no_tlsv1_1`. Ignored:
  `ssl_cipher_filter`, `ssl_ciphersuites`, `verification_flags`, `reuse`.
- Not done: `OpenSSL::SSL::SSLServer` and `SSLSocket#accept` (server-side TLS for Ruby code
  other than puma) still raise NotImplementedError.

## How to test it

- `Util/net-http-tls-check.rb` (needs CRuby for the server): after stage 1 the
  `no_timeout` entry and, with `CA=1`, the refused connections are gone.
- For CI without CRuby: `IronRuby.Tests` can host the TLS server itself. Create a
  certificate with `CertificateRequest` + `CreateSelfSigned`, accept on a loopback
  `TcpListener`, `SslStream.AuthenticateAsServer`, and stall after the headers for the
  timeout case. The Ruby side then runs `Net::HTTP` with `ca_file:` pointing at the
  exported certificate and `read_timeout: 1` (the test needs `[Options(Prism = true)]`
  to load `net/http`).

  That is `Src/IronRuby.Tests/Runtime/NetHttpTlsTests.cs`: `NetHttp_Tls1` (trust, the three
  timeouts, the name check), `NetHttp_TlsVerifyCallback1`, `NetHttp_GemNetTls1`,
  `NetHttp_PlainNoOpenSsl1` and `Ssl_NonBlocking1` (stage 2: `connect_nonblock`,
  `read_nonblock`, `write_nonblock`, `IO.select` and `NIO::Selector` over TLS).
- puma over TLS: `Src/IronRuby.Tests/Runtime/PumaTlsTests.cs`, `Puma_Tls1`: puma in single mode
  on three `ssl://` binds (cert files, `cert_pem`/`key_pem`, `force_peer` with a `ca`), with
  `Net::HTTP` and `ca_file:`, keep-alive, pipelining, plain HTTP on the TLS port, and client
  certificates presented through .NET's `SslStream`.

## Before this

An application that needed timeouts or its own CA over HTTPS was better served by .NET's
`HttpClient`. The reason was never `IO.select` being `poll(2)`; it was that `net/http` over
TLS had no timeouts and ignored `ca_file`.
