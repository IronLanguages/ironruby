def t(label)
  puts "#{label}: #{yield.inspect}"
rescue Exception => e
  puts "#{label}: RAISED #{e.class}: #{e.message}"
end
File.write("/tmp/rk/l.txt", "a\nb\n")
t("readlines chomp") { File.readlines("/tmp/rk/l.txt", chomp: true) }
t("readlines plain") { File.readlines("/tmp/rk/l.txt") }
t("gets chomp") { File.open("/tmp/rk/l.txt") { |g| g.gets(chomp: true) } }
t("gets plain") { File.open("/tmp/rk/l.txt") { |g| g.gets } }
t("gets sep") { File.open("/tmp/rk/l.txt") { |g| g.gets("\n") } }
t("readline chomp") { File.open("/tmp/rk/l.txt") { |g| g.readline(chomp: true) } }
t("io readlines chomp") { File.open("/tmp/rk/l.txt") { |g| g.readlines(chomp: true) } }
t("io readlines plain") { File.open("/tmp/rk/l.txt") { |g| g.readlines } }
t("each_line chomp") { File.open("/tmp/rk/l.txt") { |g| g.each_line(chomp: true).to_a } }
t("each_line block") { r = []; File.open("/tmp/rk/l.txt") { |g| g.each_line(chomp: true) { |l| r << l } }; r }
t("each_line plain") { File.open("/tmp/rk/l.txt") { |g| g.each_line.to_a } }
t("each chomp") { File.open("/tmp/rk/l.txt") { |g| g.each(chomp: true).to_a } }
t("foreach chomp") { IO.foreach("/tmp/rk/l.txt", chomp: true).to_a }
t("foreach plain") { IO.foreach("/tmp/rk/l.txt").to_a }
t("foreach block") { r = []; IO.foreach("/tmp/rk/l.txt", chomp: true) { |l| r << l }; r }
