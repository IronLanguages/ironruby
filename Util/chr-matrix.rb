# Differential matrix for Integer#chr.
#
#   ruby    Util/chr-matrix.rb > /tmp/chr.mri
#   ./ir.sh Util/chr-matrix.rb > /tmp/chr.ir
#   diff /tmp/chr.mri /tmp/chr.ir
#
# Prints the encoding name, the bytes and the exception class+message for every
# combination, so both the value and the *message* can be diffed - the message
# is what several of the ruby/spec expectations actually match on.

def try(label)
  v = yield
  if v.is_a?(String)
    puts "#{label}\t#{v.encoding.name} #{v.bytes.inspect}"
  else
    puts "#{label}\t#{v.inspect}"
  end
rescue Exception => e
  puts "#{label}\t#{e.class}: #{e.message}"
end

BIG = 2**64

puts "== no argument, default_internal nil =="
Encoding.default_internal = nil
[-BIG, -256, -1, 0, 1, 65, 0x7f, 0x80, 0xa1, 0xff, 0x100, 0x3000, 256,
 2206368128, BIG].each do |n|
  try("#{n}.chr") { n.chr }
end

puts "== no argument, default_internal set =="
%w[UTF-8 SHIFT_JIS US-ASCII BINARY EUC-JP ISO-8859-9 TIS-620].each do |encname|
  begin
    Encoding.default_internal = Encoding.find(encname)
  rescue Exception => e
    puts "default_internal=#{encname}\t#{e.class}: #{e.message}"
    next
  end
  [0, 65, 0x7f, 0x80, 0xa1, 0xff, 0x100, 0x3000, 0x8140, 0xfc4b, 0xa1a0, 620,
   -1, 256].each do |n|
    try("di=#{encname} #{n}.chr") { n.chr }
  end
end
Encoding.default_internal = nil

puts "== explicit encoding argument =="
ENCS = %w[US-ASCII BINARY UTF-8 SHIFT_JIS EUC-JP ISO-8859-9 TIS-620 UTF-16
          UTF-16LE UTF-16BE UTF-32 CESU-8]
CODEPOINTS = [-BIG, -1, 0, 0x41, 0x7f, 0x80, 0xa1, 0xdf, 0xff, 0x100, 0x3000,
              0x8140, 0xfc4b, 0xa1a0, 620, 0xd800, 0xdbff, 0xdc00, 0xdfff,
              0x10400, 0x10ffff, 0x110000, 2206368128, BIG]
ENCS.each do |encname|
  enc = begin
    Encoding.find(encname)
  rescue Exception => e
    puts "Encoding.find(#{encname})\t#{e.class}: #{e.message}"
    next
  end
  CODEPOINTS.each do |n|
    try("#{n}.chr(#{encname})") { n.chr(enc) }
  end
end

puts "== encoding given as a String / case folding =="
['utf-8', 'UTF-8', 'Utf-8', 'euc-jp', 'binary', 'ASCII-8BIT'].each do |name|
  try("7894.chr(#{name.inspect})") { 7894.chr(name) }
  try("0xA4A2.chr(#{name.inspect})") { 0xA4A2.chr(name) }
end

puts "== argument errors =="
try('chr(nil)') { 65.chr(nil) }
try('chr(1)') { 65.chr(1) }
try('chr("no-such-encoding")') { 65.chr("no-such-encoding") }
try('chr(:utf8)') { 65.chr(:utf8) }
try('chr to_str') { o = Object.new; def o.to_str; 'UTF-8'; end; 65.chr(o) }
try('chr 2 args') { 65.chr(Encoding::UTF_8, Encoding::UTF_8) }
try('class of result') { 65.chr.class }
try('frozen?') { 65.chr.frozen? }
try('not equal') { 82.chr.equal?(82.chr) }
