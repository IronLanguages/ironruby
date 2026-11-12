def t(label)
  r = yield
  puts "#{label}: #{r.inspect}"
rescue Exception => e
  puts "#{label}: RAISED #{e.class}: #{e.message}"
end
# "héllo" in UTF-8
File.binwrite("/tmp/rk/tr.txt", "h\xC3\xA9llo\nw\xC3\xB6rld\n")
t("read transcoded")  { File.open("/tmp/rk/tr.txt", "r:utf-8:iso-8859-1") { |f| s = f.read; [s.encoding.to_s, s.bytes] } }
t("read plain")       { File.open("/tmp/rk/tr.txt", "r:utf-8") { |f| s = f.read; [s.encoding.to_s, s.bytes.first(4)] } }
t("gets transcoded")  { File.open("/tmp/rk/tr.txt", "r:utf-8:iso-8859-1") { |f| s = f.gets; [s.encoding.to_s, s.bytes] } }
t("readlines transcoded") { File.open("/tmp/rk/tr.txt", "r:utf-8:iso-8859-1") { |f| f.readlines.map { |l| [l.encoding.to_s, l.bytes] } } }
t("each_line transcoded") { File.open("/tmp/rk/tr.txt", "r:utf-8:iso-8859-1") { |f| f.each_line.first.encoding.to_s } }
t("getc transcoded")  { File.open("/tmp/rk/tr.txt", "r:utf-8:iso-8859-1") { |f| f.getc; c = f.getc; [c.encoding.to_s, c.bytes] } }
t("read n transcoded"){ File.open("/tmp/rk/tr.txt", "r:utf-8:iso-8859-1") { |f| s = f.read(3); [s.encoding.to_s, s.bytes] } }
t("set_encoding then read") { File.open("/tmp/rk/tr.txt") { |f| f.set_encoding("utf-8", "iso-8859-1"); s = f.read; [s.encoding.to_s, s.bytes] } }
t("File.read opts")   { s = File.read("/tmp/rk/tr.txt", encoding: "utf-8:iso-8859-1"); [s.encoding.to_s, s.bytes] }
t("undefined char")   { File.binwrite("/tmp/rk/tr2.txt", "\xE3\x81\x82"); File.open("/tmp/rk/tr2.txt", "r:utf-8:iso-8859-1") { |f| f.read } }
t("same enc")         { File.open("/tmp/rk/tr.txt", "r:utf-8:utf-8") { |f| f.read.encoding.to_s } }
t("binary internal")  { File.open("/tmp/rk/tr.txt", "r:binary:utf-8") { |f| f.read.encoding.to_s } }
