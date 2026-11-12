# Totals a sweep TSV: the spec/core aggregate.
#
#   ruby Util/sweep-total.rb Util/sweep-core-master.tsv
files = ex = exp = fail = err = tag = 0
notally = []
File.readlines(ARGV[0]).each do |line|
  _, file, tally, cause = line.chomp("\n").split("\t", 4)
  next unless file
  files += 1
  if tally.nil? || tally == "NO-TALLY"
    notally << [file, cause]
    next
  end
  n = tally.scan(/(\d+) (?:files?|examples?|expectations?|failures?|errors?|tagged)/).flatten.map(&:to_i)
  unless n.size == 6
    warn "unparsed tally for #{file}: #{tally.inspect}"
    next
  end
  ex += n[1]; exp += n[2]; fail += n[3]; err += n[4]; tag += n[5]
end

puts "files swept:        #{files}"
puts "files with a tally: #{files - notally.size}"
puts "files with none:    #{notally.size}"
notally.each { |f, c| puts "    #{f}\t#{c}" }
puts
puts "examples:      #{ex}"
puts "expectations:  #{exp}"
puts "failures:      #{fail}"
puts "errors:        #{err}"
puts "tagged:        #{tag}"
puts "passing:       #{ex - fail - err}   (examples - failures - errors)"
puts format("pass rate:     %.1f%% of the examples that ran", 100.0 * (ex - fail - err) / ex)
