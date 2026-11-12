# Ranks spec/core directories by failures+errors, so effort goes where the
# breakage is.
#
#   ruby Util/sweep-worst.rb Util/sweep-core-master.tsv [n]
dirs = Hash.new { |h, k| h[k] = [0, 0, 0, 0] }  # examples, failures, errors, files
File.readlines(ARGV[0]).each do |line|
  _, file, tally, = line.chomp("\n").split("\t", 4)
  next unless file && tally && tally != "NO-TALLY"
  n = tally.scan(/(\d+) (?:files?|examples?|expectations?|failures?|errors?|tagged)/).flatten.map(&:to_i)
  next unless n.size == 6
  d = file.split("/")[0, 3].join("/")
  v = dirs[d]
  v[0] += n[1]; v[1] += n[3]; v[2] += n[4]; v[3] += 1
end
top = (ARGV[1] || 25).to_i
puts format("%-38s %7s %7s %7s %6s", "directory", "ex", "fail", "err", "files")
dirs.sort_by { |_, v| -(v[1] + v[2]) }.first(top).each do |d, v|
  puts format("%-38s %7d %7d %7d %6d", d, v[0], v[1], v[2], v[3])
end
