# What suite.rb knows about the benchmarks it runs. The sources are not
# vendored; suite.rb finds them under BS_SRC (default /root/workspace/bench-src)
# and `run.sh --fetch` clones them there.
#
# :na is one line saying why a benchmark cannot run under IronRuby *or* why it
# is not a benchmark of Ruby at all (a TruffleRuby-only API, a launcher). It is
# never used to hide an IronRuby bug: a benchmark that fails because IronRuby
# is wrong is a FAIL to be fixed.

module BenchSuiteCatalog
  # ---------------------------------------------------------------------------
  # TruffleRuby's bench/ tree (https://github.com/oracle/truffleruby/tree/master/bench),
  # written against its benchmark-interface gem: `benchmark(name) { ... }`.
  # Every *.rb at the top of these directories that calls `benchmark` is one
  # entry; the group is the directory.
  TRUFFLE_GLOBS = %w[
    classic/*.rb
    micro/*/*.rb
    optcarrot/optcarrot.rb
    rubykon/rubykon.rb
    asciidoctor/asciidoctor*.rb
    chunky_png/chunky-*.rb
    psd.rb/psd-*.rb
    image-demo/image-demo-*.rb
    liquid/liquid-*.rb
    parallel/savina/savina-*.rb
    sinatra/sinatra-*.rb
    synthetic/*.rb
  ]

  TRUFFLE_NA = {
    'micro/dispatch/dispatch-foreign.rb'       => 'Truffle polyglot foreign objects',
    'micro/dispatch/dispatch-foreign-mixed.rb' => 'Truffle polyglot foreign objects',
    'micro/truffleruby/concurrent_map.rb'      => 'TruffleRuby::ConcurrentMap (defines nothing elsewhere)',
    'micro/truffleruby/synchronized.rb'        => 'TruffleRuby.synchronized (defines nothing elsewhere)',
    'sinatra/sinatra-hello.rb'                 => 'its vendored rack 2.2.3 needs ostruct, no longer a default gem in Ruby 4.0 (fails on CRuby 4.0 too)',
  }

  # Benchmarks whose block value is legitimately different from run to run
  # (random numbers, wall-clock time, object identity): --verify reports them
  # as SKIP rather than comparing.
  TRUFFLE_UNSTABLE = {
    'micro/random/random_object.rb'          => 'an unseeded Random.new',
    'liquid/liquid-cart-parse.rb'            => 'returns the byte count of Template#to_s, which embeds an object address',
    'asciidoctor/asciidoctor-load-file.rb'   => 'the Document carries localtime/localdatetime attributes',
    'parallel/savina/savina-trapezoidal.rb'  => 'sums four threads\' Float results in arrival order',
  }

  # Benchmarks whose value differs from CRuby's for a reason that is known and
  # is not the benchmark computing something else. --verify still compares
  # them and says KNOWN (with this reason) while they differ; once IronRuby is
  # fixed they read SAME again by themselves. Checked by dumping the whole
  # inspect string and diffing it after normalizing the reason away.
  TRUFFLE_KNOWN_DIFF = {
    'rubykon/rubykon.rb'           => 'IronRuby inspects a recursive object as "..." where CRuby prints #<Foo:0x... ...>',
    'parallel/savina/savina-apsp.rb' => 'recursive-object inspect, and Thread::Queue#inspect prints the CLR class name',
  }

  # ---------------------------------------------------------------------------
  # Are We Fast Yet (https://github.com/smarr/are-we-fast-yet), the cross-language
  # suite TruffleRuby's papers measure. Each benchmark checks its own result and
  # raises 'Benchmark failed with incorrect result'. The inner-iteration counts
  # are AWFY's rebench.conf values divided by 10, so one outer iteration is
  # 0.1-0.4s on CRuby 4.0 - except CD, Mandelbrot and NBody, which only know
  # their expected answer for particular counts and so keep one of those
  # (one iteration is then 1-2.5s on CRuby).
  AWFY = {
    'DeltaBlue'  => { inner: 1200 },
    'Richards'   => { inner: 10 },
    'Json'       => { inner: 10 },
    'CD'         => { inner: 100 },
    'Havlak'     => { inner: 150 },
    'Bounce'     => { inner: 150 },
    'List'       => { inner: 150 },
    'Mandelbrot' => { inner: 500 },
    'NBody'      => { inner: 250_000 },
    'Permute'    => { inner: 100 },
    'Queens'     => { inner: 100 },
    'Sieve'      => { inner: 300 },
    'Storage'    => { inner: 100 },
    'Towers'     => { inner: 60 },
  }

  AWFY_NA = {
    'Havlak' => "AWFY's som.rb reopens Set, which Ruby 4.0 made core (fails on CRuby 4.0 too)",
  }

  # ---------------------------------------------------------------------------
  # CRuby's own test/ (what TruffleRuby keeps a copy of as test/mri), from the
  # CRuby checkout at BS_CRUBY_SRC. One process per file.
  MRI_GLOBS = %w[test/ruby/test_*.rb]
end
