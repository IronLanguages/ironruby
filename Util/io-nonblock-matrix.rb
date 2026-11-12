require 'io/nonblock'
def t(label)
  puts "#{label}: #{yield.inspect}"
rescue Exception => e
  puts "#{label}: RAISED #{e.class}: #{e.message}"
end
t("file default") { File.open(__FILE__) { |f| f.nonblock? } }
t("pipe default") { r, w = IO.pipe; v = [r.nonblock?, w.nonblock?]; r.close; w.close; v }
t("set true") { File.open(__FILE__) { |f| f.nonblock = true; f.nonblock? } }
t("set false") { File.open(__FILE__) { |f| f.nonblock = true; f.nonblock = false; f.nonblock? } }
t("block form") { File.open(__FILE__) { |f| [f.nonblock { f.nonblock? }, f.nonblock?] } }
t("block restores") { File.open(__FILE__) { |f| f.nonblock = true; f.nonblock(false) { }; f.nonblock? } }
