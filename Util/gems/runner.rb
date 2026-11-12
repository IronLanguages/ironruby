# Runs one gem: the load check, then whatever test suite is on disk.
# Invoked once per gem by Util/gems/run.sh, which reads the ##GEM## line and the
# framework's own "N tests, N assertions, ..." summary out of the log.
#
#   ir.sh -X:ObjectSpace Util/gems/runner.rb <gem-name>
#   ir.sh Util/gems/runner.rb --load-path <gem-name>   # the -I arguments to use

require_relative 'catalog'

if ARGV[0] == '--load-path'
  entry = GemCatalog.entry(ARGV[1]) or abort "unknown gem: #{ARGV[1]}"
  puts GemCatalog.load_path(ARGV[1], entry).map { |d| "-I#{d}" }.join(' ')
  exit
end

# The gem names in one tier, for run.sh.
if ARGV[0] == '--list'
  names = case ARGV[1]
          when 'popular' then GemCatalog::POPULAR.keys
          when 'stdlib'  then GemCatalog::ENTRIES.keys
          else                GemCatalog::ALL.keys
          end
  puts names.join(' ')
  exit
end

name = ARGV.shift or abort 'usage: runner.rb <gem-name>'
entry = GemCatalog.entry(name) or abort "unknown gem: #{name}"

GemCatalog.load_path(name, entry).reverse_each { |d| $LOAD_PATH.unshift(d) }

# --- load check ------------------------------------------------------------
load_state = 'ok'
note = entry[:note]
if entry[:require]
  begin
    require entry[:require]
  rescue Exception => e
    load_state = 'FAIL'
    note = "#{e.class}: #{e.message.to_s.split("\n").first}"
  end
end

files = load_state == 'ok' ? GemCatalog.test_files(entry, name) : []

# run.sh reads the suite kind: "unit" means a summary line follows, "spec" means
# it has to drive mspec itself, and "none" means there is nothing to run.
kind = if !files.empty?
         'unit'
       elsif load_state == 'ok' && entry[:specs]
         'spec'
       elsif load_state == 'ok' && GemCatalog.exercise_file(entry)
         'exercise'
       else
         'none'
       end

puts "##GEM##\t#{name}\t#{load_state}\t#{kind}\t#{entry[:specs] || entry[:exercise]}\t#{note}"
$stdout.flush
exit!(load_state == 'ok' ? 0 : 1) if files.empty?

# --- run the suite ---------------------------------------------------------
if entry[:gem]
  # A popular-tier gem that ships its own test/ in the .gem: run it in place,
  # with the gem's lib/ and test/ on the path, under whichever framework it
  # declares. Nothing from the CRuby source tree is involved.
  dir = GemCatalog.installed_dir(name)
  $LOAD_PATH.unshift(File.join(dir, 'test'), File.join(dir, 'lib'))
  Dir.chdir(dir)
  require(entry[:framework] == :minitest ? 'minitest/autorun' : 'test/unit')
  files.each do |f|
    begin
      require File.expand_path(f)
    rescue Exception => e
      warn "load #{f}: #{e.class}: #{e.message}"
    end
  end
elsif entry[:framework] == :minitest
  require 'minitest/autorun'
  $LOAD_PATH.unshift(File.join(File.dirname(File.dirname(files.first)), 'test'))
  files.each do |f|
    begin
      require File.expand_path(f)
    rescue Exception => e
      warn "load #{f}: #{e.class}: #{e.message}"
    end
  end
else
  $LOAD_PATH.unshift(File.join(GemCatalog::RUBY_SRC, 'tool', 'lib'))
  $LOAD_PATH.unshift(File.join(GemCatalog::RUBY_SRC, 'test', 'lib'))
  # Tests in a subdirectory load their suite's helper by its path under test/
  # (test/psych/visitors/*.rb: require 'psych/helper'), as CRuby's test runner
  # has test/ on the path. Last, so that nothing in it shadows a library.
  $LOAD_PATH.push(File.join(GemCatalog::RUBY_SRC, 'test'))
  require 'test/unit'

  # LeakChecker walks the heap with ObjectSpace.each_object(IO) / (File).  The CLR
  # has no heap walk, so IronRuby cannot answer that - and a leak check is not what
  # these suites are here to measure.
  class LeakChecker
    def initialize(*); end
    def check(*); end
  end

  Dir.chdir(GemCatalog::RUBY_SRC)
  files.each do |f|
    begin
      require File.expand_path(f)
    rescue Exception => e
      warn "load #{f}: #{e.class}: #{e.message}"
    end
  end
end
# Both frameworks install an at_exit hook that runs the tests and prints the
# summary line run.sh parses.
