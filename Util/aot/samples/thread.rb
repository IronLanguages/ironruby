$stderr.puts "start"
t = Thread.new { $stderr.puts "in thread"; 42 }
$stderr.puts "joined: #{t.value}"
