require 'zip'
require 'stringio'
require 'digest'

buf = Zip::OutputStream.write_buffer(StringIO.new(+'')) do |z|
  z.put_next_entry('a.txt')
  z.write 'first entry' * 20
  z.put_next_entry('dir/b.bin')
  z.write((0..255).to_a.pack('C*'))
  z.put_next_entry('empty.txt')
end
buf.rewind
data = buf.read
puts data[0, 2]
puts data.bytesize > 0

Zip::File.open_buffer(StringIO.new(data)) do |zf|
  puts zf.entries.map(&:name).sort.inspect
  puts zf.read('a.txt')
  puts Digest::SHA256.hexdigest(zf.read('dir/b.bin'))
  puts zf.read('empty.txt').bytesize
  e = zf.get_entry('a.txt')
  puts e.size
  puts e.compressed_size < e.size
  puts e.crc.to_s(16)
  puts e.directory?
  puts e.file?
end

Zip::InputStream.open(StringIO.new(data)) do |io|
  while (entry = io.get_next_entry)
    puts "#{entry.name} #{io.read.bytesize}"
  end
end

begin
  Zip::File.open_buffer(StringIO.new('not a zip at all'))
rescue Zip::Error => e
  puts e.class
end
