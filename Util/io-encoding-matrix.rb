# What IO#external_encoding and IO#internal_encoding answer, across the open
# modes, the ways of asking for an encoding, and the two default_internal
# settings. Run under both rubies and diff:
#
#   ruby Util/io-encoding-matrix.rb > /tmp/cr.txt
#   ./ir.sh Util/io-encoding-matrix.rb > /tmp/ir.txt
#   diff -u /tmp/cr.txt /tmp/ir.txt
#
# The rules this pins down, which IronRuby did not have:
#   * a write-mode stream with no encoding asked for has no external encoding
#   * internal_encoding is nil when the external encoding is BINARY
#   * internal_encoding is nil when it would equal the external encoding
require 'tempfile'

PATH = "/tmp/io-encoding-matrix.txt"
File.write(PATH, "hello\n")

MODES = %w[r r+ w w+ a a+ rb rb+ wb]
ENCODINGS = [
  nil,
  "utf-8",
  "binary",
  "utf-8:iso-8859-1",
  "binary:utf-8",
  "utf-8:utf-8",
]

def show(value)
  value.nil? ? "nil" : value.to_s
end

[nil, "ISO-8859-1"].each do |internal|
  Encoding.default_internal = internal
  puts "== default_internal = #{show(Encoding.default_internal)}"
  MODES.each do |mode|
    ENCODINGS.each do |enc|
      spec = enc ? "#{mode}:#{enc}" : mode
      begin
        f = File.open(PATH, spec)
        begin
          puts format("  %-18s ext=%-12s int=%-12s", spec, show(f.external_encoding), show(f.internal_encoding))
        ensure
          f.close
        end
      rescue Exception => e
        puts format("  %-18s RAISED %s: %s", spec, e.class, e.message)
      end
    end
  end
end

Encoding.default_internal = nil

puts "== set_encoding after opening"
[["utf-8"], ["utf-8", "iso-8859-1"], ["binary", "utf-8"], ["utf-8:iso-8859-1"],
 [Encoding::UTF_8], [Encoding::BINARY, Encoding::UTF_8], [nil]].each do |args|
  f = File.open(PATH)
  begin
    f.set_encoding(*args)
    puts format("  %-28s ext=%-12s int=%-12s", args.inspect,
                show(f.external_encoding), show(f.internal_encoding))
  rescue Exception => e
    puts format("  %-28s RAISED %s: %s", args.inspect, e.class, e.message)
  ensure
    f.close
  end
end

puts "== STDIN/STDOUT/STDERR"
[[:STDIN, STDIN], [:STDOUT, STDOUT], [:STDERR, STDERR]].each do |name, io|
  puts format("  %-8s ext=%-12s int=%-12s", name, show(io.external_encoding), show(io.internal_encoding))
end

# The "w" opens above truncated the fixture; put the content back.
File.write(PATH, "hello\n")

puts "== what a read answers"
f = File.open(PATH, "r:utf-8")
puts "  read.encoding      = #{f.read.encoding}"
f.close
f = File.open(PATH, "r:utf-8")
puts "  read(2).encoding   = #{f.read(2).encoding}"
f.close
f = File.open(PATH, "r:utf-8:iso-8859-1")
puts "  transcoded         = #{f.read.encoding}"
f.close
puts "  binread.encoding   = #{File.binread(PATH).encoding}"
puts "  readlines.first    = #{File.open(PATH, 'r:utf-8') { |h| h.readlines.first.encoding }}"
puts "  gets.encoding      = #{File.open(PATH, 'r:utf-8') { |h| h.gets.encoding }}"
