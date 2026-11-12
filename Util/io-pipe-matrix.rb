def t(label)
  puts "#{label}: #{yield.inspect}"
rescue Exception => e
  puts "#{label}: RAISED #{e.class}: #{e.message}"
end
t("plain") { r, w = IO.pipe; v = [r.class, w.class]; r.close; w.close; v }
t("ext") { r, w = IO.pipe("utf-8"); v = [r.external_encoding.to_s, r.internal_encoding.inspect]; r.close; w.close; v }
t("ext int") { r, w = IO.pipe("utf-8", "iso-8859-1"); v = [r.external_encoding.to_s, r.internal_encoding.to_s]; r.close; w.close; v }
t("combined") { r, w = IO.pipe("utf-8:iso-8859-1"); v = [r.external_encoding.to_s, r.internal_encoding.to_s, w.external_encoding.inspect]; r.close; w.close; v }
t("encoding obj") { r, w = IO.pipe(Encoding::UTF_8); v = r.external_encoding.to_s; r.close; w.close; v }
t("block value") { IO.pipe { |a, b| 42 } }
t("block closes") { x = nil; IO.pipe { |a, b| x = a }; x.closed? }
t("block classes") { IO.pipe { |a, b| [a.class, b.class] } }
t("roundtrip") { r, w = IO.pipe; w.write("hi"); w.close; v = r.read; r.close; v }
