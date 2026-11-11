# The rerunnable TruffleRuby-style benchmark and compatibility suite. Run it
# with CRuby (run.sh does); it drives IronRuby and CRuby as child processes.
#
#   ruby Util/benchsuite/suite.rb --suite truffle,awfy        # time both
#   ruby Util/benchsuite/suite.rb --suite truffle --verify    # compare answers
#   ruby Util/benchsuite/suite.rb --suite mri -j 6            # CRuby's test/ruby
#   ruby Util/benchsuite/suite.rb --suite awfy --baseline OUTDIR_OF_AN_OLDER_RUN
#   ruby Util/benchsuite/suite.rb classic/ nbody              # name filters
#
# See README.md for what each number means.

require 'optparse'
require 'fileutils'
require 'rbconfig'
require 'timeout'
require 'etc'
require_relative 'catalog'

module BenchSuite
  HERE = __dir__
  ROOT = File.expand_path('../..', HERE)

  Opts = Struct.new(:suites, :verify, :time, :ir_only, :cruby_only, :baseline,
                    :out, :jobs, :filters, :list, :mri_timeout, :ir_args, keyword_init: true)

  Result = Struct.new(:suite, :name, :status, :ir, :cruby, :note, keyword_init: true) do
    # IronRuby time / CRuby time: 1.00 is parity, 5.00 five times slower.
    def ratio
      return nil unless ir.is_a?(Numeric) && cruby.is_a?(Numeric) && ir > 0 && cruby > 0
      ir / cruby
    end
  end

  class Runner
    def initialize(opts)
      @o = opts
      @src = ENV.fetch('BS_SRC', '/root/workspace/bench-src')
      @truffle = File.join(@src, 'truffleruby/bench')
      @awfy = File.join(@src, 'are-we-fast-yet/benchmarks/Ruby')
      @cruby_src = ENV.fetch('BS_CRUBY_SRC', '/root/workspace/ruby40')
      @ir = [ENV.fetch('IR', File.join(ROOT, 'ir.sh')), *opts.ir_args]
      @ruby = [ENV.fetch('RUBY', 'ruby')]
      FileUtils.mkdir_p(@o.out)
      @log_dir = File.join(@o.out, 'logs')
      FileUtils.mkdir_p(@log_dir)
      @mutex = Mutex.new
      # One iteration of aobench is ~50s on CRuby 4.0 and ~150s on IronRuby.
      @verify_timeout = Integer(ENV.fetch('BS_TIMEOUT', 600))
    end

    def interpreters
      list = []
      list << [:ir, @ir] unless @o.cruby_only
      list << [:cruby, @ruby] unless @o.ir_only
      list
    end

    # ------------------------------------------------------------------ util

    # Runs cmd in its own process group with the output going to log.
    # With budget, the process is stopped after that many seconds and that
    # is not a failure (AWFY is given more outer iterations than it needs).
    # Returns [:exit, status] / [:budget, nil] / [:timeout, nil].
    def spawn_logged(cmd, log, timeout:, budget: nil, chdir: nil)
      File.open(log, 'w') do |f|
        f.puts "# #{cmd.join(' ')}"
        f.flush
        o = { out: f, err: f, pgroup: true, in: File::NULL }
        o[:chdir] = chdir if chdir
        pid = Process.spawn(ENV.to_h.merge('RUBYOPT' => nil), *cmd, **o)
        limit = budget || timeout
        begin
          Timeout.timeout(limit) { Process.wait(pid) }
          [:exit, $?]
        rescue Timeout::Error
          kill_group(pid)
          [budget ? :budget : :timeout, nil]
        end
      end
    end

    # TERM, a grace period, then KILL to the whole group whatever happened: the
    # leader (ir.sh) can exit while the dotnet process under it is still running,
    # and a stray CPU-bound child would distort every later timing.
    def kill_group(pid)
      Process.kill('TERM', -pid) rescue nil
      reaped = false
      10.times do
        break if (reaped = Process.wait(pid, Process::WNOHANG))
        sleep 0.2
      end
      Process.kill('KILL', -pid) rescue nil
      Process.wait(pid) rescue nil unless reaped
    end

    # A log as text: a benchmark or a stack trace can write bytes that are not
    # UTF-8, and matching a regexp against those raises ArgumentError.
    def log_text(log)
      File.read(log).scrub
    rescue SystemCallError
      ''
    end

    def log_lines(log) = log_text(log).lines(chomp: true)

    def median(xs)
      s = xs.sort
      n = s.size
      n.odd? ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2.0
    end

    # Steady state: the median of the second half of the samples.
    def steady(xs)
      return nil if xs.empty?
      median(xs.drop(xs.size / 2))
    end

    def matches?(name)
      @o.filters.empty? || @o.filters.any? { |f| name.include?(f) }
    end

    def first_error(log)
      lines = log_lines(log).drop(1)
      line = lines.find { |l| l =~ /\(\w*(Error|Exception)\)|Error:|^\s*\S+\.rb:\d+:in .*: / } ||
             lines.reverse.find { |l| !l.strip.empty? }
      # "<internal:/long/path/kernel_require.rb>:139:in 'Kernel#require': msg"
      # would be all path once cut to width; keep the method and message.
      line.to_s.strip.sub(/\A<internal:[^>]*>:\d+:/, '').sub(%r{\A/\S+/([^/\s]+:\d+:)}, '\\1')[0, 160]
    end

    def parallel_each(items, jobs, &blk)
      queue = Queue.new
      items.each { |i| queue << i }
      jobs.times.map do
        Thread.new do
          while (item = (queue.pop(true) rescue nil))
            blk.call(item)
          end
        end
      end.each(&:join)
    end

    def say(line)
      @mutex.synchronize { $stdout.puts line; $stdout.flush }
    end

    def loadavg = (File.read('/proc/loadavg').split.first(3).join(' ') rescue '?')

    def safe(s) = s.gsub(/[^\w.-]+/, '_')

    # --------------------------------------------------------------- truffle

    def truffle_entries
      abort "missing #{@truffle} - run Util/benchsuite/run.sh --fetch" unless Dir.exist?(@truffle)
      BenchSuiteCatalog::TRUFFLE_GLOBS.flat_map { |g| Dir.glob(g, base: @truffle).sort }
        .select { |rel| File.read(File.join(@truffle, rel)).scrub =~ /^\s*benchmark\b/ }
        .select { |rel| matches?("truffle/#{rel}") }
    end

    def truffle
      bi = File.join(@truffle, 'benchmark-interface/bin/benchmark')
      results = []
      run_one = lambda do |rel|
        file = File.join(@truffle, rel)
        if (na = BenchSuiteCatalog::TRUFFLE_NA[rel])
          r = Result.new(suite: 'truffle', name: rel.sub(/\.rb\z/, ''), status: 'N/A', note: na)
          report(r); @mutex.synchronize { results << r }
          next
        end
        per = {}
        interpreters.each do |key, interp|
          log = File.join(@log_dir, "truffle-#{safe(rel)}-#{key}#{@o.verify ? '-verify' : ''}.log")
          if @o.verify
            cmd = [*interp, '-r', File.join(HERE, 'prelude.rb'), '-r', File.join(HERE, 'verify-prelude.rb'), file]
            how, st = spawn_logged(cmd, log, timeout: @verify_timeout, chdir: File.dirname(file))
          else
            cmd = [*interp, '-r', File.join(HERE, 'prelude.rb'), bi, file, '--simple', '--time', @o.time.ceil.to_s]
            how, st = spawn_logged(cmd, log, timeout: [@verify_timeout, @o.time * 10].max, chdir: File.dirname(file))
          end
          per[key] = [how, st, log]
        end
        rows = @o.verify ? truffle_verify_rows(rel, per) : truffle_time_rows(rel, per)
        rows.each { |r| report(r) }
        @mutex.synchronize { results.concat(rows) }
      end
      entries = truffle_entries
      if @o.verify
        parallel_each(entries, @o.jobs, &run_one)
      else
        entries.each(&run_one)
      end
      results.sort_by(&:name)
    end

    # benchmark-interface --simple prints the benchmark's name, then one
    # iterations-per-second figure per ~1s round.
    def parse_simple(log)
      out = Hash.new { |h, k| h[k] = [] }
      cur = nil
      log_lines(log).drop(1).each do |l|
        if l =~ /\A\d+(\.\d+)?(e[+-]?\d+)?\z/
          out[cur] << l.to_f if cur
        elsif l == 'optimised away'
          out[cur] << Float::INFINITY if cur
        elsif l =~ /\A\w[\w.:\/ -]*\z/
          cur = l
          out[cur]
        end
      end
      out
    end

    def truffle_time_rows(rel, per)
      base = rel.sub(/\.rb\z/, '')
      parsed = per.transform_values { |how, st, log| [how, st, log, parse_simple(log)] }
      names = parsed.values.flat_map { |*, p| p.keys }.uniq
      if names.empty? && parsed.values.all? { |how, _, log, _| how == :exit && log_text(log).include?('No benchmarks found') }
        return [Result.new(suite: 'truffle', name: base, status: 'N/A', note: 'defines no benchmark outside TruffleRuby')]
      end
      names = [nil] if names.empty?
      names.map do |n|
        row_name = n.nil? || names.size == 1 ? base : "#{File.dirname(base)}/#{n}"
        vals = {}
        bad = {}
        notes = []
        parsed.each do |key, (how, st, log, p)|
          samples = p[n] || []
          if how == :timeout
            bad[key] = 'TIMEOUT'
            notes << "#{key}: timeout"
          elsif samples.empty? || !st.success?
            bad[key] = 'FAIL'
            notes << "#{key}: #{first_error(log)}"
          end
          # Seconds per iteration, so the ratio reads like a time ratio.
          ips = steady(samples)
          vals[key] = ips && ips > 0 && ips.finite? ? 1.0 / ips : nil
          notes << "#{key}: optimised away" if ips == Float::INFINITY
        end
        status = bad[:ir] && bad[:cruby] ? 'BOTH-FAIL' : bad[:ir] || (bad[:cruby] && 'CRUBY-FAIL') || 'OK'
        Result.new(suite: 'truffle', name: row_name, status: status,
                   ir: vals[:ir], cruby: vals[:cruby], note: notes.join('; '))
      end
    end

    def parse_verify(log)
      lines = log_lines(log)
      out = lines.grep(/\AVERIFY /).to_h do |l|
        _, name, rest = l.split(' ', 3)
        [name, rest]
      end
      lines.grep(/\AVERIFY-CHECK /).each do |l|
        _, name, verdict = l.split(' ', 3)
        out[name] = "RAISED own verify block: #{verdict}" if verdict != 'ok' && out[name]
        out[name] += ' [own check ok]' if verdict == 'ok' && out[name]
      end
      out
    end

    def truffle_verify_rows(rel, per)
      base = rel.sub(/\.rb\z/, '')
      vals = per.transform_values { |how, st, log| [how, st, log, parse_verify(log)] }
      names = vals.values.flat_map { |*, v| v.keys }.uniq
      if names.empty? && vals.values.all? { |how, st, _, _| how == :exit && st.success? }
        return [Result.new(suite: 'truffle', name: base, status: 'N/A', note: 'defines no benchmark outside TruffleRuby')]
      end
      if names.empty?
        notes = vals.map { |k, (how, _, log, _)| "#{k}: #{how == :timeout ? 'timeout' : first_error(log)}" }
        both = vals.values.none? { |how, st, _, _| how == :exit && st.success? }
        return [Result.new(suite: 'truffle', name: base, status: both && vals.size > 1 ? 'BOTH-FAIL' : 'FAIL',
                           note: notes.join('; '))]
      end
      names.map do |n|
        row_name = names.size == 1 ? base : "#{File.dirname(base)}/#{n}"
        ir = vals.dig(:ir, 3, n)
        cr = vals.dig(:cruby, 3, n)
        status, note =
          if (cr.nil? || cr.include?('RAISED')) && (ir.nil? || ir.include?('RAISED'))
            ['BOTH-FAIL', "ir: #{ir || first_error(vals.dig(:ir, 2))}; cruby: #{cr || first_error(vals.dig(:cruby, 2))}"]
          elsif cr.nil? || cr.include?('RAISED')
            ['CRUBY-FAIL', "cruby: #{cr || first_error(vals.dig(:cruby, 2))}"]
          elsif ir.nil? || ir.include?('RAISED')
            ['FAIL', "ir: #{ir || first_error(vals.dig(:ir, 2))}"]
          elsif BenchSuiteCatalog::TRUFFLE_UNSTABLE[rel]
            # only once both produced a value: a crash must not pass as a skip
            ['SKIP', BenchSuiteCatalog::TRUFFLE_UNSTABLE[rel]]
          elsif ir == cr
            ['SAME', ir[0, 90]]
          elsif (why = BenchSuiteCatalog::TRUFFLE_KNOWN_DIFF[rel])
            ['KNOWN', why]
          else
            ['DIFF', "ir: #{ir[0, 70]} | cruby: #{cr[0, 70]}"]
          end
        Result.new(suite: 'truffle', name: row_name, status: status, note: note)
      end
    end

    # ------------------------------------------------------------------ awfy

    def awfy
      abort "missing #{@awfy} - run Util/benchsuite/run.sh --fetch" unless Dir.exist?(@awfy)
      harness = File.join(@awfy, 'harness.rb')
      BenchSuiteCatalog::AWFY.select { |n, _| matches?("awfy/#{n}") }.map do |name, cfg|
        if (na = BenchSuiteCatalog::AWFY_NA[name])
          r = Result.new(suite: 'awfy', name: name, status: 'N/A', note: na)
          report(r)
          next r
        end
        vals = {}
        bad = {}
        notes = []
        interpreters.each do |key, interp|
          log = File.join(@log_dir, "awfy-#{name}-#{key}#{@o.verify ? '-verify' : ''}.log")
          outer = @o.verify ? 1 : 100_000
          cmd = [*interp, '-r', File.join(HERE, 'prelude.rb'), harness, name, outer.to_s, cfg[:inner].to_s]
          how, st = spawn_logged(cmd, log, timeout: @o.time * 20 + 300,
                                 budget: @o.verify ? nil : @o.time * 2)
          times = log_lines(log).filter_map { |l| l[/runtime: (\d+)us/, 1]&.to_f }.map { |us| us / 1e6 }
          failed = (how == :exit && !st.success?) || times.empty? ||
                   log_text(log).include?('incorrect result')
          if how == :timeout && times.empty?
            bad[key] = 'TIMEOUT'
            notes << "#{key}: timeout"
          elsif failed
            bad[key] = 'FAIL'
            notes << "#{key}: #{first_error(log)}"
          end
          vals[key] = steady(times)
          notes << "#{key}: #{times.size} iterations" if !@o.verify && times.size < 4 && !times.empty?
        end
        status = bad[:ir] && bad[:cruby] ? 'BOTH-FAIL' : bad[:ir] || (bad[:cruby] && 'CRUBY-FAIL') || (@o.verify ? 'SAME' : 'OK')
        r = Result.new(suite: 'awfy', name: name, status: status, ir: vals[:ir], cruby: vals[:cruby],
                       note: notes.join('; '))
        report(r)
        r
      end
    end

    # ------------------------------------------------------------------- mri

    def mri_files
      abort "missing #{@cruby_src}/test - set BS_CRUBY_SRC" unless Dir.exist?(File.join(@cruby_src, 'test'))
      BenchSuiteCatalog::MRI_GLOBS.flat_map { |g| Dir.glob(g, base: @cruby_src).sort }
        .select { |rel| matches?("mri/#{rel}") }
    end

    # test/unit -v prints "[ i/N] Class#test = 0.01 s" per finished test and
    # the bare "[ i/N] Class#test" just before it starts one, so the last
    # bare line of a run that never reached its summary is the test that hung
    # or killed the process.
    def mri_one(interp, key, rel)
      file = File.join(@cruby_src, rel)
      excluded = []
      attempts = 0
      loop do
        attempts += 1
        log = File.join(@log_dir, "mri-#{safe(rel)}-#{key}-#{attempts}.log")
        cmd = [*interp, '-I', File.join(@cruby_src, 'tool/lib'), '-r', File.join(HERE, 'mri-prelude.rb'),
               file, '-v', '--seed=1', *excluded.map { |t| "-n!/\\A#{Regexp.escape(t)}\\z/" }]
        how, st = spawn_logged(cmd, log, timeout: @o.mri_timeout, chdir: @cruby_src)
        text = File.binread(log).force_encoding('UTF-8').scrub
        summary = text[/^(\d+) tests, (\d+) assertions, (\d+) failures, (\d+) errors, (\d+) skips/]
        if summary
          t, _a, f, e, s = $~.captures.map(&:to_i)
          failing = text.scan(/^\s*\d+\) (?:Failure|Error):\n(\S+#\S+)/).flatten.uniq
          status = how != :exit ? 'TIMEOUT' : f + e == 0 && excluded.empty? ? 'PASS' : 'RAN'
          return { status: status, tests: t + excluded.size, failures: f, errors: e,
                   skips: s, hung: excluded, failing: failing, attempts: attempts }
        end
        started = text.scan(/^\[\s*\d+\/\d+\] (\S+#\S+?)(?: = [\d.]+ s)?$/)
        hung = unfinished_test(text, file)
        if (cext = text[/cannot load such file -- (-test-\/\S+)/, 1])
          return { status: 'N/A', tests: 0, failures: 0, errors: 0, skips: 0, hung: [], failing: [],
                   attempts: attempts, error: "needs CRuby's C test extension #{cext}" }
        end
        if hung.nil? || excluded.include?(hung) || attempts >= 10
          return { status: how == :timeout ? 'TIMEOUT' : 'CRASH', tests: started.size, failures: 0, errors: 0,
                   skips: 0, hung: excluded + [hung].compact, failing: [], attempts: attempts,
                   error: first_error(log) }
        end
        excluded << hung
      end
    end

    # The last "[ i/N] Class#test" progress line that never got its " = 0.01 s".
    # A process that dies prints its last words straight after the name
    # ("TestArray#test_join2Stack overflow."), so the name is cut back to the
    # longest prefix that is a test method defined in the file.
    def unfinished_test(text, file)
      last = text.lines.grep(/^\[\s*\d+\/\d+\] \S+#/).last or return nil
      return nil if last =~ / = [\d.]+ s\s*$/
      token = last[/\] (\S+)/, 1] or return nil
      klass, meth = token.split('#', 2)
      defs = File.read(file).scrub.scan(/^\s*def (test_\w+[?!]?)/).flatten
      best = defs.select { |d| meth.start_with?(d) }.max_by(&:size)
      "#{klass}##{best || meth}"
    end

    def mri
      results = []
      files = mri_files
      parallel_each(files, @o.jobs) do |rel|
        per = interpreters.to_h { |key, interp| [key, mri_one(interp, key, rel)] }
        r = per[:ir] || per[:cruby]
        c = per[:cruby]
        pass = ->(x) { x && x[:tests] - x[:failures] - x[:errors] - x[:skips] - x[:hung].size }
        note = []
        note << "hung/crashed: #{r[:hung].join(' ')}" unless r[:hung].empty?
        note << "failing: #{r[:failing].first(4).join(' ')}#{r[:failing].size > 4 ? ' ...' : ''}" unless r[:failing].empty?
        note << r[:error] if r[:error]
        res = Result.new(suite: 'mri', name: rel.sub(%r{\Atest/}, '').sub(/\.rb\z/, ''),
                         status: r[:status], ir: pass.(per[:ir]), cruby: pass.(c), note: note.join('; '))
        res.define_singleton_method(:detail) { per }
        @mutex.synchronize { results << res }
        report(res)
      end
      results.sort_by!(&:name)
      write_mri_failures(results)
      results
    end

    def write_mri_failures(results)
      File.open(File.join(@o.out, 'mri-failing.txt'), 'w') do |f|
        results.each do |r|
          d = r.detail[:ir] or next
          d[:hung].each { |t| f.puts "#{t}\tHUNG" }
          d[:failing].each { |t| f.puts "#{t}\tFAIL" }
        end
      end
    end

    # --------------------------------------------------------------- output

    def fmt(v)
      case v
      when nil then '-'
      when Integer then v.to_s
      # Seconds, printed with a unit that keeps three significant digits.
      when Float
        if v >= 1 then format('%.3gs', v)
        elsif v >= 1e-3 then format('%.3gms', v * 1e3)
        elsif v >= 1e-6 then format('%.3gus', v * 1e6)
        else format('%.3gns', v * 1e9)
        end
      end
    end

    def header(suite)
      if suite == 'mri'
        format('%-34s %-8s %8s %8s  %s', 'FILE', 'STATUS', 'IR PASS', 'CR PASS', 'NOTE')
      elsif @o.verify
        format('%-52s %-10s %s', 'BENCHMARK', 'VERDICT', 'NOTE')
      else
        format('%-52s %10s %10s %7s %7s %-10s %s', 'BENCHMARK', 'IR', 'CRUBY', 'RATIO', 'PREV', 'STATUS', 'NOTE')
      end
    end

    def report(r)
      line =
        if r.suite == 'mri'
          format('%-34s %-8s %8s %8s  %s', r.name, r.status, r.ir || '-', r.cruby || '-', r.note.to_s[0, 140])
        elsif @o.verify
          format('%-52s %-10s %s', "#{r.suite}/#{r.name}", r.status, r.note.to_s[0, 160])
        else
          prev = @baseline&.dig("#{r.suite}/#{r.name}")
          format('%-52s %10s %10s %7s %7s %-10s %s', "#{r.suite}/#{r.name}", fmt(r.ir), fmt(r.cruby),
                 r.ratio ? format('%.2f', r.ratio) : '-', prev ? format('%.2f', prev) : '', r.status, r.note.to_s[0, 140])
        end
      say line
    end

    def load_baseline
      return unless @o.baseline
      @baseline = {}
      Dir.glob(File.join(@o.baseline, 'results-*.tsv')).each do |f|
        File.readlines(f, chomp: true).drop(1).each do |l|
          suite, name, _st, _ir, _cr, ratio, = l.split("\t")
          @baseline["#{suite}/#{name}"] = ratio.to_f if ratio && !ratio.empty?
        end
      end
    end

    def write_tsv(suite, results)
      mode = @o.verify ? '-verify' : ''
      File.open(File.join(@o.out, "results-#{suite}#{mode}.tsv"), 'w') do |f|
        f.puts %w[suite name status ir cruby ratio note].join("\t")
        results.each do |r|
          f.puts [r.suite, r.name, r.status, r.ir, r.cruby, r.ratio&.round(4), r.note.to_s.tr("\t\n", '  ')].join("\t")
        end
      end
    end

    def summarize(suite, results)
      counts = results.map(&:status).tally.sort.map { |k, v| "#{v} #{k}" }.join(', ')
      line = "#{suite}: #{results.size} entries - #{counts}"
      if suite == 'mri'
        ir = results.sum { |r| r.ir || 0 }
        cr = results.sum { |r| r.cruby || 0 }
        line += " - passing tests IR #{ir}" + (cr > 0 ? " / CRuby #{cr} (#{(100.0 * ir / cr).round(1)}%)" : '')
      elsif !@o.verify
        ratios = results.select { |r| r.status == 'OK' }.filter_map(&:ratio)
        unless ratios.empty?
          geo = Math.exp(ratios.sum { |x| Math.log(x) } / ratios.size)
          line += format(' - geomean IR/CRuby %.2f over %d (median %.2f, %d faster than CRuby)',
                         geo, ratios.size, median(ratios), ratios.count { |x| x < 1 })
        end
      end
      line
    end

    def run
      load_baseline
      lines = []
      if @o.list
        puts truffle_entries.map { |e| "truffle/#{e}" } if @o.suites.include?('truffle')
        puts BenchSuiteCatalog::AWFY.keys.map { |e| "awfy/#{e}" } if @o.suites.include?('awfy')
        puts mri_files.map { |e| "mri/#{e}" } if @o.suites.include?('mri')
        return
      end
      say "# IR=#{@ir.join(' ')} IR_CONFIG=#{ENV['IR_CONFIG'] || '(default)'} RUBY=#{@ruby.join(' ')} " \
          "time=#{@o.time}s load=#{loadavg} out=#{@o.out}"
      @o.suites.each do |suite|
        say ''
        say header(suite)
        results = send(suite)
        write_tsv(suite, results)
        lines << summarize(suite, results)
      end
      lines << "load average at end: #{loadavg} (#{Etc.nprocessors} CPUs) - timings on a loaded box move by tens of percent"
      say ''
      lines.each { |l| say l }
      File.write(File.join(@o.out, 'summary.txt'), lines.join("\n") + "\n")
      say "results: #{@o.out}"
    end
  end

  def self.main(argv)
    o = Opts.new(suites: %w[truffle awfy], verify: false, time: Float(ENV.fetch('BS_TIME', 5)),
                 jobs: Integer(ENV.fetch('BS_JOBS', 4)), filters: [], list: false,
                 mri_timeout: Integer(ENV.fetch('BS_MRI_TIMEOUT', 180)), ir_args: [],
                 out: ENV['BS_OUT'] || File.join(ENV.fetch('TMPDIR', '/tmp'), "benchsuite-#{Time.now.strftime('%Y%m%d-%H%M%S')}"))
    OptionParser.new do |op|
      op.banner = 'usage: suite.rb [options] [name-filter...]'
      op.on('--suite LIST', 'truffle,awfy,mri (default truffle,awfy)') { |v| o.suites = v.split(',') }
      op.on('--verify', 'compare answers instead of timing') { o.verify = true }
      op.on('--time SEC', Float, 'seconds of timed work per benchmark per interpreter (default 5)') { |v| o.time = v }
      op.on('-j N', Integer, 'parallel jobs for --verify and mri (default 4)') { |v| o.jobs = v }
      op.on('--ir-only') { o.ir_only = true }
      op.on('--cruby-only') { o.cruby_only = true }
      op.on('--ir-arg ARG', 'extra IronRuby argument, e.g. -X:NoJIT (repeatable)') { |v| o.ir_args << v }
      op.on('--baseline DIR', 'an older run\'s output directory; adds a PREV ratio column') { |v| o.baseline = v }
      op.on('--out DIR', 'where results and logs go') { |v| o.out = v }
      op.on('--mri-timeout SEC', Integer, 'per test-file attempt (default 180)') { |v| o.mri_timeout = v }
      op.on('--list', 'list the entries and exit') { o.list = true }
    end.parse!(argv)
    o.filters = argv
    bad = o.suites - %w[truffle awfy mri]
    abort "unknown suite #{bad.join(',')}" unless bad.empty?
    Runner.new(o).run
  end
end

BenchSuite.main(ARGV) if $0 == __FILE__
