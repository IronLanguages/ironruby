# Compares sweeps made by Util/ci-sweep.sh with the list of examples known to fail,
# Util/spec-known-failures.txt, and fails only on what is not in it:
#
# - a failing example the list does not name (new), unless it passed when its spec file was
#   run again on its own (Util/ci-sweep.sh does that for every new one): CLAUDE.md's "rerun the
#   file alone before believing a new one", since timing-sensitive examples flip under the
#   sweep's load. Those are reported, as candidates for a "flaky" line;
# - a spec file that printed no mspec summary line even when run alone (it crashed or hung),
#   since its examples are missing from every count and could hide anything;
# - a suite that was asked for but did not arrive whole (a shard is missing, or it ran nothing).
#
# An example the list names (not as flaky) that no longer fails is reported, not an error: the
# fix is to take it off the list.
#
#   ruby Util/sweep-check.rb [--known FILE] [--suites "core library ..."] [--summary FILE] OUT_DIR...
#   ruby Util/sweep-check.rb [--known FILE] --new-files OUT_DIR
#
# Each OUT_DIR is one Util/ci-sweep.sh output; the shards of a suite are given together.
# --summary appends a Markdown report to FILE ($GITHUB_STEP_SUMMARY in the workflow); under
# GitHub Actions the errors and warnings are also printed as annotations. Exit status 1 on
# anything that fails the sweep, 2 on bad arguments or an unreadable list. --new-files prints
# "<suite> <spec file>" for the files of the new failures, for Util/ci-sweep.sh to run again.
require "optparse"
require "set"

SUITES = %w[core library language command_line security]
SUMMARY = /(\d+) files?, (\d+) examples?, (\d+) expectations?, (\d+) failures?, (\d+) errors?, (\d+) tagged/
FAILING = /^(.*) (?:FAILED|ERROR)$/
# a backtrace line of a spec file, which says which file a failure came from
SPEC_FILE = %r{spec/(?:#{SUITES.join("|")})/[^\s:]*_spec\.rb(?=:\d)}

known_path = File.expand_path("spec-known-failures.txt", __dir__)
expected = nil
summary_path = nil
new_files = false
OptionParser.new do |o|
  o.banner = "usage: ruby Util/sweep-check.rb [options] OUT_DIR..."
  o.on("--known FILE") { |v| known_path = v }
  o.on("--suites LIST") { |v| expected = v.split }
  o.on("--summary FILE") { |v| summary_path = v }
  o.on("--new-files") { new_files = true }
end.parse!
abort "usage: ruby Util/sweep-check.rb [options] OUT_DIR..." if ARGV.empty?

def read_log(path)
  File.binread(path).force_encoding(Encoding::UTF_8).scrub
end

# [[example, spec file or nil], ...] for the failures and errors mspec reports in a log. The
# file is the last spec file in the failure's backtrace, which ends at the file's top level.
def failures_in(text)
  lines = text.lines.map(&:chomp)
  result = []
  lines.each_with_index do |line, i|
    next unless (m = FAILING.match(line))
    file = nil
    (i + 1...lines.size).each do |j|
      break if lines[j] =~ /\A\d+\)\z|\AFinished in / || FAILING.match?(lines[j])
      f = lines[j][SPEC_FILE]
      file = f if f
    end
    result << [m[1], file]
  end
  result
end

# The list: "<suite> <example>", or "flaky <suite> <example>" for one that may pass or fail.
Known = Struct.new(:suite, :name, :flaky, :line)
known = []
File.readlines(known_path, chomp: true).each_with_index do |line, i|
  line = line.strip
  next if line.empty? || line.start_with?("#")
  m = /\A(?:(flaky)\s+)?(#{SUITES.join("|")})\s+(\S.*)\z/.match(line)
  unless m
    warn "#{known_path}:#{i + 1}: not \"[flaky] <suite> <example>\": #{line}"
    exit 2
  end
  known << Known.new(m[2], m[3], !m[1].nil?, i + 1)
end

# What the sweeps found, by suite.
# where: example => the spec files it failed in; rerun: spec file => [exit status, log] of its
# run on its own (Util/ci-sweep.sh's third pass)
Suite = Struct.new(:shards, :files, :examples, :failures, :errors, :failing, :crashed, :where, :rerun)
found = Hash.new do |h, k|
  h[k] = Suite.new(Hash.new { |hh, n| hh[n] = Set.new }, 0, 0, 0, 0, Set.new, [],
                   Hash.new { |hh, n| hh[n] = Set.new }, {})
end
problems = []

ARGV.each do |out|
  shard_file = File.join(out, "shard.txt")
  unless File.file?(shard_file)
    problems << "#{out}: no shard.txt, so not a finished Util/ci-sweep.sh output"
    next
  end
  File.readlines(shard_file, chomp: true).each do |line|
    suite, shard = line.split
    k, n = shard.split("/").map(&:to_i)
    found[suite].shards[n] << k
  end

  recheck = Hash.new { |h, k| h[k] = [] }
  recheck_file = File.join(out, "recheck.tsv")
  if File.file?(recheck_file)
    File.readlines(recheck_file, chomp: true).each do |line|
      suite, job, file, status, log = line.split("\t")
      recheck[[suite, job]] << [file, status, File.join(out, log)]
    end
  end

  rerun_file = File.join(out, "rerun.tsv")
  if File.file?(rerun_file)
    File.readlines(rerun_file, chomp: true).each do |line|
      suite, file, status, log = line.split("\t")
      found[suite].rerun[file] = [status, File.join(out, log)]
    end
  end

  File.readlines(File.join(out, "jobs.txt"), chomp: true).each do |line|
    suite, job, *files = line.split
    s = found[suite]
    runs = [[nil, nil, File.join(out, suite, "#{job}.log")]]
    unless File.file?(runs[0][2]) && SUMMARY.match?(read_log(runs[0][2]))
      # Its files were run again one by one (Util/ci-sweep.sh); without that, all of it is lost.
      runs = recheck[[suite, job]]
      if runs.empty?
        files.each { |f| s.crashed << [f, "?", "#{suite}/#{job} left no summary line and was not run again"] }
        next
      end
      # A file pass that stopped partway (or a worker that never started) leaves some files
      # without a row, and their examples would vanish from the counts without a word.
      (files - runs.map(&:first)).each do |f|
        s.crashed << [f, "?", "#{suite}/#{job} left no summary line and this file was not run again"]
      end
    end
    runs.each do |file, status, log|
      text = File.file?(log) ? read_log(log) : ""
      tally = text.scan(SUMMARY).last
      if tally
        n = tally.map(&:to_i)
        s.files += n[0]
        s.examples += n[1]
        s.failures += n[3]
        s.errors += n[4]
        failures_in(text).each do |name, f|
          s.failing << name
          s.where[name] << (f || file) if f || file
        end
      else
        # timeout(1) exits 124 when the TERM ended the run, 137 when it had to KILL (or
        # something else did); 128 + N is any other signal N
        cause = case status.to_i
                when 124 then "timed out"
                when 137 then "killed (timed out and ignored TERM, or killed by SIGKILL)"
                when 0, 1 then "exited (#{status}) without a summary line"
                when 129..192 then "died of signal #{status.to_i - 128}"
                else "exit status #{status}"
                end
        last = text.lines.map(&:rstrip).reject(&:empty?).last(1).join
        s.crashed << [file, cause, last]
      end
    end
  end
end

listed_names = Hash.new { |h, k| h[k] = Set.new }
known.each { |k| listed_names[k.suite] << k.name }

if new_files
  found.each do |suite, s|
    (s.failing - listed_names[suite]).flat_map { |n| s.where[n].to_a }.uniq.sort.each { |f| puts "#{suite} #{f}" }
  end
  exit
end

# Did a new failure pass when every file it failed in was run on its own?
passed_alone = lambda do |s, name|
  files = s.where[name]
  !files.empty? && files.all? do |f|
    _, log = s.rerun[f]
    next false unless log && File.file?(log)
    text = read_log(log)
    # A run that ran no example (it was all filtered out, or mspec loaded nothing) says nothing
    # about whether this one passes.
    tally = text.scan(SUMMARY).last
    tally && tally[1].to_i > 0 && failures_in(text).none? { |n, _| n == name }
  end
end

suites = expected || found.keys.sort_by { |s| SUITES.index(s) || SUITES.size }
suites.each do |suite|
  s = found.key?(suite) ? found[suite] : nil
  if s.nil?
    problems << "#{suite}: not swept"
    next
  end
  s.shards.each do |n, ks|
    missing = (1..n).to_a - ks.to_a
    problems << "#{suite}: shards #{missing.map { |k| "#{k}/#{n}" }.join(", ")} missing" unless missing.empty?
  end
  problems << "#{suite}: shards of different sizes (#{s.shards.keys.sort.join(", ")})" if s.shards.size > 1
  problems << "#{suite}: no examples ran" if s.examples == 0
end

seen = Set.new
known.each do |k|
  warn "#{known_path}:#{k.line}: listed twice: #{k.suite} #{k.name}" unless seen.add?([k.suite, k.name])
end

report = []
new_total = crashed_total = 0
fixed_all = []
suites.each do |suite|
  next unless found.key?(suite)
  s = found[suite]
  listed = known.select { |k| k.suite == suite }
  new, alone = (s.failing - listed_names[suite]).sort.partition { |n| !passed_alone[s, n] }
  fixed = listed.reject(&:flaky).map(&:name).reject { |n| s.failing.include?(n) }
  flaky_failed = listed.select(&:flaky).map(&:name).select { |n| s.failing.include?(n) }
  new_total += new.size
  crashed_total += s.crashed.size
  fixed_all.concat(fixed.map { |n| [suite, n] })
  report << [suite, s, listed.size, new, fixed, flaky_failed, alone]
end

out = []
out << "| suite | examples | failures | errors | failing examples | listed | new | passed alone | listed, now passing | flaky failed | files with no summary |"
out << "|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|"
report.each do |suite, s, listed, new, fixed, flaky, alone|
  out << "| `#{suite}` | #{s.examples} | #{s.failures} | #{s.errors} | #{s.failing.size} | #{listed} | " \
         "#{new.empty? ? 0 : "**#{new.size}**"} | #{alone.size} | #{fixed.size} | #{flaky.size} | " \
         "#{s.crashed.empty? ? 0 : "**#{s.crashed.size}**"} |"
end
out << ""

unless problems.empty?
  out << "### Incomplete sweep"
  out << ""
  problems.each { |p| out << "- #{p}" }
  out << ""
end

crashed = report.flat_map { |suite, s, *| s.crashed.map { |c| [suite, *c] } }
unless crashed.empty?
  out << "### Spec files with no mspec summary line (crashed or hung)"
  out << ""
  out << "Their examples are missing from the counts above. Each was run again on its own and still"
  out << "printed no summary; its log is in the artifact, under `<suite>/<job>.recheck/`."
  out << ""
  out << "```"
  crashed.each { |suite, file, cause, last| out << "#{file}: #{cause}#{last.to_s.empty? ? "" : " - last line: #{last[0, 160]}"}" }
  out << "```"
  out << ""
end

news = report.flat_map { |suite, _, _, new| new.map { |n| [suite, n] } }
unless news.empty?
  out << "### New failures"
  out << ""
  out << "Not in `Util/spec-known-failures.txt`. Fix them, or - if one is known and expected to"
  out << "stay - add its line, as below, with a comment saying why."
  out << ""
  out << "```"
  news.each { |suite, n| out << "#{suite} #{n}" }
  out << "```"
  out << ""
end

unless fixed_all.empty?
  out << "### Listed, now passing"
  out << ""
  out << "These lines of `Util/spec-known-failures.txt` can go#{crashed.empty? ? "" : " (unless the example is in one of the files that crashed above)"}:"
  out << ""
  out << "```"
  fixed_all.each { |suite, n| out << "#{suite} #{n}" }
  out << "```"
  out << ""
end

alones = report.flat_map { |suite, *, a| a.map { |n| [suite, n] } }
unless alones.empty?
  out << "### Failed in the sweep, passed when the file ran alone"
  out << ""
  out << "Not counted as new. If one keeps turning up, it is a candidate for a `flaky` line:"
  out << ""
  out << "```"
  alones.each { |suite, n| out << "flaky #{suite} #{n}" }
  out << "```"
  out << ""
end

flakies = report.flat_map { |suite, _, _, _, _, f| f.map { |n| [suite, n] } }
unless flakies.empty?
  out << "### Flaky examples that failed this time"
  out << ""
  out << "```"
  flakies.each { |suite, n| out << "#{suite} #{n}" }
  out << "```"
  out << ""
end

failed = new_total > 0 || crashed_total > 0 || !problems.empty?
verdict = if failed
            "**FAILED**: #{new_total} new failing example(s), #{crashed_total} file(s) with no summary line" \
              "#{problems.empty? ? "" : ", incomplete sweep"}"
          else
            "**passed**: no failure that `Util/spec-known-failures.txt` does not list"
          end
out.unshift(verdict, "")

puts out
File.open(summary_path, "a") { |f| f.puts out, "" } if summary_path

if ENV["GITHUB_ACTIONS"]
  # Annotations keep their own escaping: a newline or % in a message must be encoded.
  esc = ->(m) { m.gsub("%", "%25").gsub("\r", "%0D").gsub("\n", "%0A") }
  problems.each { |p| puts "::error::#{esc[p]}" }
  crashed.each { |suite, file, cause, _| puts "::error::#{esc["#{file}: #{cause}"]}" }
  news.each { |suite, n| puts "::error::#{esc["new failure in #{suite}: #{n}"]}" }
  fixed_all.each { |suite, n| puts "::warning::#{esc["listed but passing in #{suite}: #{n}"]}" }
  alones.each { |suite, n| puts "::warning::#{esc["failed in the sweep, passed alone, in #{suite}: #{n}"]}" }
end

exit(failed ? 1 : 0)
