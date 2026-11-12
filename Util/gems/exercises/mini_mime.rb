require 'mini_mime'

%w[a.html b.png c.json d.tar.gz e.unknownext].each do |f|
  info = MiniMime.lookup_by_filename(f)
  puts "#{f} => #{info ? [info.content_type, info.extension, info.binary?].inspect : 'nil'}"
end

%w[text/html image/png application/json].each do |ct|
  info = MiniMime.lookup_by_content_type(ct)
  puts "#{ct} => #{info ? info.extension : 'nil'}"
end

puts MiniMime.lookup_by_content_type('application/octet-stream').binary?
puts MiniMime.lookup_by_extension('css').content_type
