# Compares two sweep TSVs and prints what moved: files that gained or lost a
# tally, and the change in the totals over the files both runs could measure.
#
#   ir.sh Util/sweep-diff.rb before.tsv after.tsv
COLUMNS = [:examples, :expectations, :failures, :errors, :tagged]

def read(path)
  rows = {}
  File.readlines(path).each do |line|
    status, file, tally, cause = line.chomp("\n").split("\t", 4)
    next unless file
    rows[file] = [tally, cause.to_s]
  end
  rows
end

def numbers(tally)
  return nil if tally.nil? || tally == "NO-TALLY"
  n = tally.scan(/(\d+) (?:files?|examples?|expectations?|failures?|errors?|tagged)/).flatten.map { |s| s.to_i }
  n.length == 6 ? n[1..-1] : nil
end

before = read(ARGV[0])
after  = read(ARGV[1])

gained = []
lost = []
(before.keys | after.keys).sort.each do |file|
  b = before[file]
  a = after[file]
  next unless b && a
  gained << file if b[0] == "NO-TALLY" && a[0] != "NO-TALLY"
  lost   << file if b[0] != "NO-TALLY" && a[0] == "NO-TALLY"
end

puts "before: #{before.size} files, #{before.count { |_, v| v[0] == 'NO-TALLY' }} NO-TALLY"
puts "after:  #{after.size} files, #{after.count { |_, v| v[0] == 'NO-TALLY' }} NO-TALLY"
puts
puts "gained a tally (#{gained.size}):"
gained.each { |f| puts "  #{f}\t#{after[f][0]}" }
puts "lost a tally (#{lost.size}):"
lost.each { |f| puts "  #{f}\t#{before[f][1]}" }
puts

# Totals over the files both runs measured, so the comparison is like for like.
sums = { before: [0] * 5, after: [0] * 5 }
common = 0
before.each_key do |file|
  b = numbers(before[file] && before[file][0])
  a = numbers(after[file] && after[file][0])
  next unless b && a
  common += 1
  5.times do |i|
    sums[:before][i] += b[i]
    sums[:after][i] += a[i]
  end
end
puts "over the #{common} files both runs tallied:"
COLUMNS.each_with_index do |name, i|
  b = sums[:before][i]
  a = sums[:after][i]
  puts format("  %-13s %6d -> %6d  (%+d)", name, b, a, a - b)
end

puts
puts "causes after:"
after.values.map { |v| v[1] }.reject(&:empty?).tally.sort_by { |_, n| -n }.each do |cause, n|
  puts "  #{cause}\t#{n}"
end
