def t(label)
  puts "#{label}: #{yield.inspect}"
rescue Exception => e
  puts "#{label}: RAISED #{e.class}"
end
File.write("/tmp/rk/rp.txt", "hello")
t("readpartial") { f = File.open("/tmp/rk/rp.txt"); r = [f.readpartial(3), f.readpartial(3)]; f.close; r }
t("readpartial eof") { f = File.open("/tmp/rk/rp.txt"); f.read; begin; f.readpartial(3); ensure; f.close; end }
t("readpartial buf") { g = File.open("/tmp/rk/rp.txt"); b = +"xx"; r = [g.readpartial(2, b), b]; g.close; r }
t("readpartial buf identity") { g = File.open("/tmp/rk/rp.txt"); b = +"xx"; r = b.equal?(g.readpartial(2, b)); g.close; r }
t("readpartial 0") { g = File.open("/tmp/rk/rp.txt"); r = g.readpartial(0); g.close; r }
t("readpartial neg") { g = File.open("/tmp/rk/rp.txt"); begin; g.readpartial(-1); ensure; g.close; end }
t("readpartial enc") { File.open("/tmp/rk/rp.txt") { |h| h.readpartial(3).encoding } }
t("select read") { f = File.open("/tmp/rk/rp.txt"); r = IO.select([f]); f.close; r.class }
t("select timeout") { f = File.open("/tmp/rk/rp.txt"); r = IO.select([f], [], [], 0); f.close; r[0].size }
t("select write") { IO.select(nil, [STDOUT])[1].size }
t("select none") { IO.select([], [], [], 0) }
t("select bad") { IO.select("x") }
