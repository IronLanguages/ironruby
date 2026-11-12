# The catalog of ruby-bench benchmarks the runner knows about.
#
# ruby-bench (https://github.com/ruby/ruby-bench) is not vendored here; point
# the runner at a checkout with RB_DIR (default /root/workspace/ruby-bench).
# This file only records which benchmarks exist, which group they belong to,
# and what is known about running them under IronRuby.
#
#   :group  - :micro (a plain .rb in benchmarks/, or a small self-contained
#             directory benchmark) or :macro (a directory benchmark that
#             drives a real gem through its own Gemfile)
#   :path   - relative to the ruby-bench checkout; defaults to
#             "benchmarks/<name>.rb", or "benchmarks/<name>/benchmark.rb" when
#             that is what is on disk
#   :gems   - the benchmark calls use_gemfile, so it needs `bundle install`
#             in its directory and a writable GEM_HOME
#   :na     - one line saying why this benchmark cannot run under IronRuby.
#             The runner reports it as N/A and does not run it. Never use this
#             to hide a bug: a benchmark that fails because IronRuby is wrong
#             is a FAIL to be fixed, not an N/A.
#   :note   - anything else worth printing next to the result
#
# Adding the macro group: macro agents append their benchmarks here with
# group: :macro (and gems: true for the usual case). `run.sh --group macro`
# then picks them up with no change to the runner.

module RubyBenchCatalog
  ENTRIES = {
    # --- microbenchmarks: plain .rb files in benchmarks/ ---------------------
    '30k_ifelse'             => { group: :micro },
    '30k_methods'            => { group: :micro },
    '30k_variables'          => { group: :micro },
    'attr_accessor'          => { group: :micro },
    'block_methods'          => { group: :micro },
    'cfunc_itself'           => { group: :micro },
    'fib'                    => { group: :micro },
    'gcbench'                => { group: :micro },
    'getivar'                => { group: :micro },
    'getivar-module'         => { group: :micro },
    'keyword_args'           => { group: :micro },
    'loops-times'            => { group: :micro },
    'matmul'                 => { group: :micro },
    'nqueens'                => { group: :micro },
    'object-new'             => { group: :micro },
    'object-new-initialize'  => { group: :micro },
    'object-new-no-escape'   => { group: :micro },
    'respond_to'             => { group: :micro },
    'ruby-xor'               => { group: :micro },
    'send_bmethod'           => { group: :micro },
    'send_cfunc_block'       => { group: :micro },
    'send_rubyfunc_block'    => { group: :micro },
    'send_rubyfunc_inline'   => { group: :micro },
    'setivar'                => { group: :micro },
    'setivar_object'         => { group: :micro },
    'setivar_young'          => { group: :micro },
    'splay'                  => { group: :micro },
    'str_concat'             => { group: :micro },
    'string_malloc_pressure' => { group: :micro },
    'structaref'             => { group: :micro },
    'structaset'             => { group: :micro },
    'struct-new-no-escape'   => { group: :micro },
    'sudoku'                 => { group: :micro },
    'throw'                  => { group: :micro },

    # --- microbenchmarks that live in a directory of their own ---------------
    'binarytrees'            => { group: :micro },
    'fannkuchredux'          => { group: :micro },
    'nbody'                  => { group: :micro },
    'gvl_release_acquire'    => { group: :micro },
    'ruby-json'              => { group: :micro },
    'blurhash'               => { group: :micro },
    'lee'                    => { group: :micro, gems: true,
                                  note: 'needs the victor gem; first run installs it' },
    'knucleotide'            => { group: :micro,
                                  na: 'uses Process.fork for parallelism; .NET cannot fork' },

    # --- macrobenchmarks -----------------------------------------------------
    # (the macro agents add their entries here, group: :macro)
  }

  # Benchmarks whose block returns a value that is not stable across runs or
  # across implementations, so --verify cannot compare it. These either check
  # themselves (the benchmark raises on a wrong answer) or have nothing to
  # check; the reason is printed instead of a verdict.
  UNSTABLE_VALUE = {
    'gvl_release_acquire' => 'returns live Thread objects',
    'gcbench'             => 'returns a freshly allocated tree',
    'splay'               => 'returns the splay tree, whose shape embeds object ids',
    'lee'                 => 'writes an SVG; the block returns a Victor document',
  }

  module_function

  def entry(name)
    ENTRIES[name]
  end

  def names(group = :all)
    ENTRIES.select { |_, e| group == :all || e[:group] == group }.keys
  end

  # Where the benchmark's entry point lives inside a ruby-bench checkout.
  def path(dir, name)
    e = ENTRIES[name] or return nil
    return File.join(dir, e[:path]) if e[:path]
    flat = File.join(dir, 'benchmarks', "#{name}.rb")
    return flat if File.exist?(flat)
    File.join(dir, 'benchmarks', name, 'benchmark.rb')
  end
end

if $PROGRAM_NAME == __FILE__
  # run.sh reads the whole catalog in one go:
  #   catalog.rb dump <ruby-bench-dir> <group> [name...]
  # printing one record per benchmark, with the fields separated by US (\x1f):
  #   name  group  path  gems  na  note  unstable
  # US and not a tab because tab is IFS whitespace, and bash's `read` collapses
  # runs of it - which would shift every field after an empty one.
  # An unknown name on the command line is reported on stderr and exits 1, so a
  # typo does not silently measure nothing.
  abort "usage: catalog.rb dump <ruby-bench-dir> <group> [name...]" unless ARGV.shift == 'dump'
  dir, group, *want = ARGV
  group = (group.nil? || group.empty? ? 'all' : group).to_sym
  if want.empty?
    want = RubyBenchCatalog.names(group)
  else
    bad = want.reject { |n| RubyBenchCatalog.entry(n) }
    unless bad.empty?
      $stderr.puts "unknown benchmark(s): #{bad.join(' ')}"
      exit 1
    end
  end
  want.each do |name|
    e = RubyBenchCatalog.entry(name)
    puts [name, e[:group], RubyBenchCatalog.path(dir, name), e[:gems] ? 1 : 0,
          e[:na].to_s, e[:note].to_s, RubyBenchCatalog::UNSTABLE_VALUE[name].to_s].join("\x1f")
  end
end
