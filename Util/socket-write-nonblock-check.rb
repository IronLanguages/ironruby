# IO#write_nonblock on a socket returns what the kernel took, and no more (#14).
#
# Fill a socket's send buffer with write_nonblock until it refuses, then count what the
# peer receives: the bytes write_nonblock claimed must be the bytes that arrive. Before
# #14 a socket fell through to the buffered write, which claimed the whole string while
# the kernel took only part of it, and the rest was lost - Puma's large responses arrived
# cut short. Run by CI on Linux and Windows. Only Linux (and macOS) take part of a write:
# Winsock takes a non-blocking send whole or refuses it, so there the check passed before
# #14 too, and guards that the claimed bytes keep arriving.
require "socket"

data = "x" * 2_000_000
cap = 64 * data.bytesize # a write_nonblock that never refuses is a failure, not a hang

server = TCPServer.new("127.0.0.1", 0)
client = TCPSocket.new("127.0.0.1", server.addr[1])
peer = server.accept

claimed = 0
begin
  claimed += peer.write_nonblock(data) while claimed < cap
rescue IO::WaitWritable
  refused = true
end
abort "write_nonblock took #{claimed} bytes and never refused" unless refused

reader = Thread.new do
  got = 0
  while (chunk = (client.readpartial(65_536) rescue nil))
    got += chunk.bytesize
  end
  got
end
peer.write(data) # blocking: everything after the claimed bytes
peer.close
received = reader.value - data.bytesize

puts "write_nonblock claimed #{claimed} bytes; the peer received #{received}"
abort "write_nonblock claimed #{claimed - received} bytes the peer never got" unless claimed == received
