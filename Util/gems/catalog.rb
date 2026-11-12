# The catalog of gems the harness knows about, in two tiers.
#
#   :stdlib   - CRuby's default and bundled gems, already on disk
#   :popular  - the most-downloaded gems on rubygems.org, installed from there
#               with IronRuby's own `./igem.sh install`
#
# Each entry says what to require, where a test suite for it lives, and whether
# IronRuby implements it itself (in which case the gem's lib/ must stay off
# $LOAD_PATH - it is only a shim over a C extension that does not exist here).
#
#   :require  - the feature name, or nil to skip the load check
#   :tests    - test/unit or minitest files: globs under the CRuby source tree
#               (RUBY_SRC), or absolute globs
#   :specs    - a directory under spec/library, run with mspec when there is no
#               :tests suite. The gem's own lib/ is on the path for it, so this
#               exercises the modern gem and not IronRuby's 1.9 copy.
#   :native   - IronRuby supplies this library; do not put the gem's lib/ first
#   :note     - what is worth saying about it in the table
#
# The test sources come from two places on disk:
#   GEM_DIR  - installed gems, for lib/ and for the few gems that ship test/
#   RUBY_SRC - an unpacked CRuby source tree, for test/ and tool/lib
# Nothing is downloaded. A library with neither suite is reported "load only",
# which is the honest answer rather than a fake pass.

module GemCatalog
  GEM_DIR = ENV['GEM_DIR'] || '/usr/local/lib/ruby/gems/4.0.0/gems'
  RUBY_SRC = ENV['RUBY_SRC'] || '/root/workspace/cosmoruby-upgrade/ruby-4.0.6'

  ENTRIES = {
    # --- pure-Ruby default gems whose tests live in the CRuby tree ------------
    'tsort'        => { require: 'tsort',        tests: %w[test/test_tsort.rb] },
    'weakref'      => { require: 'weakref',      tests: %w[test/test_weakref.rb] },
    'fileutils'    => { require: 'fileutils',    tests: %w[test/fileutils/*.rb] },
    'tmpdir'       => { require: 'tmpdir',       tests: %w[test/test_tmpdir.rb] },
    'tempfile'     => { require: 'tempfile',     tests: %w[test/test_tempfile.rb] },
    'open3'        => { require: 'open3',        tests: %w[test/test_open3.rb] },
    'shellwords'   => { require: 'shellwords',   tests: %w[test/test_shellwords.rb] },
    'securerandom' => { require: 'securerandom', tests: %w[test/test_securerandom.rb] },
    'singleton'    => { require: 'singleton',    tests: %w[test/test_singleton.rb] },
    'forwardable'  => { require: 'forwardable',  tests: %w[test/test_forwardable.rb] },
    'delegate'     => { require: 'delegate',     tests: %w[test/test_delegate.rb] },
    'timeout'      => { require: 'timeout',      tests: %w[test/test_timeout.rb] },
    'find'         => { require: 'find',         tests: %w[test/test_find.rb] },
    'ipaddr'       => { require: 'ipaddr',       tests: %w[test/test_ipaddr.rb] },
    'pp'           => { require: 'pp',           tests: %w[test/test_pp.rb] },
    'prettyprint'  => { require: 'prettyprint',  tests: %w[test/test_prettyprint.rb] },
    'time'         => { require: 'time',         tests: %w[test/test_time.rb] },
    'optparse'     => { require: 'optparse',     tests: %w[test/optparse/*.rb] },
    'uri'          => { require: 'uri',          tests: %w[test/uri/*.rb] },
    'erb'          => { require: 'erb',          tests: %w[test/erb/*.rb] },
    'resolv'       => { require: 'resolv',       tests: %w[test/resolv/*.rb] },
    'open-uri'     => { require: 'open-uri',     tests: %w[test/open-uri/*.rb] },
    'monitor'      => { require: 'monitor',      tests: %w[test/monitor/*.rb] },
    'pathname'     => { require: 'pathname',     tests: %w[test/pathname/*.rb] },

    'net-http'     => { require: 'net/http',     tests: %w[test/net/http/*.rb],
                        note: 'spawns servers; slow' },
    'net-imap'     => { require: 'net/imap' },
    'net-smtp'     => { require: 'net/smtp' },
    'net-pop'      => { require: 'net/pop' },
    'net-ftp'      => { require: 'net/ftp',      specs: 'net-ftp' },

    # --- libraries IronRuby implements itself (C extensions in MRI) ----------
    'digest'       => { require: 'digest',   native: true, tests: %w[test/digest/*.rb] },
    'stringio'     => { require: 'stringio', native: true, tests: %w[test/stringio/*.rb] },
    'strscan'      => { require: 'strscan',  native: true, tests: %w[test/strscan/*.rb] },
    'zlib'         => { require: 'zlib',     native: true, tests: %w[test/zlib/*.rb] },
    'etc'          => { require: 'etc',      native: true, tests: %w[test/etc/*.rb] },
    'fcntl'        => { require: 'fcntl',    native: true },
    'json'         => { require: 'json',     native: true, tests: %w[test/json/*.rb] },
    'psych'        => { require: 'psych',    native: true, tests: %w[test/psych/*.rb test/psych/*/*.rb] },
    'date'         => { require: 'date',     native: true, tests: %w[test/date/*.rb] },
    'bigdecimal'   => { require: 'bigdecimal', native: true, specs: 'bigdecimal' },
    'syslog'       => { require: 'syslog',   native: true, specs: 'syslog',
                        note: "IronRuby's own; the gem's lib/ only loads a .so" },
    'nkf'          => { require: 'nkf',      native: true,
                        note: "IronRuby's own, on String#encode" },
    'objspace'     => { require: 'objspace', native: true, specs: 'objectspace' },

    # --- pure-Ruby gems whose own suite is not on disk -----------------------
    'rake'         => { require: 'rake' },
    'rexml'        => { require: 'rexml/document' },
    'rss'          => { require: 'rss' },
    'csv'          => { require: 'csv',        specs: 'csv' },
    'matrix'       => { require: 'matrix',     specs: 'matrix' },
    'prime'        => { require: 'prime',      specs: 'prime' },
    'ostruct'      => { require: 'ostruct',    specs: 'openstruct' },
    'pstore'       => { require: 'pstore' },
    'observer'     => { require: 'observer',   specs: 'observer' },
    'abbrev'       => { require: 'abbrev',     specs: 'abbrev' },
    'drb'          => { require: 'drb',        specs: 'drb' },
    'racc'         => { require: 'racc/parser' },
    'getoptlong'   => { require: 'getoptlong', specs: 'getoptlong' },
    'rinda'        => { require: 'rinda/rinda' },
    'resolv-replace' => { require: 'resolv-replace' },
    'benchmark'    => { require: 'benchmark' },
    'logger'       => { require: 'logger',     specs: 'logger' },
    'base64'       => { require: 'base64',     specs: 'base64' },
    'english'      => { require: 'English',    specs: 'English' },
    'mutex_m'      => { require: 'mutex_m' },

    # --- gems that ship their own tests --------------------------------------
    'minitest'     => { require: 'minitest/autorun', framework: :minitest,
                        tests: ["#{GEM_DIR}/minitest-*/test/**/*_test.rb"] },

    # --- deliberately not run ------------------------------------------------
    'test-unit'    => { require: 'test/unit', isolated: true,
                        note: "shadows CRuby's tool/lib/test/unit, so only loaded" },
    'power_assert' => { require: 'power_assert',
                        note: 'needs RubyVM::InstructionSequence' },
  }

  # --- the popular tier ----------------------------------------------------
  #
  # The most-downloaded gems on rubygems.org (rubygems.org/stats), in that
  # order, minus the ones the stdlib tier above already covers. These are NOT
  # on disk: install them with IronRuby's own tooling first, into a GEM_HOME
  # of your own so that concurrent agents do not share one,
  #
  #   export GEM_HOME="$PWD/.gems"
  #   Util/gems/install-popular.sh
  #
  # and then `Util/gems/run.sh popular`.
  #
  # Extra keys, on top of the ones above:
  #   :gem       - always true here: resolved through RubyGems out of GEM_HOME
  #                rather than found on disk under GEM_DIR
  #   :tests     - globs *relative to the installed gem directory*
  #   :exercise  - a script under Util/gems/exercises/. It is run under both
  #                IronRuby and CRuby 4.0.6 and the two outputs are compared;
  #                CRuby is the oracle. This is what most popular gems get,
  #                because most of them ship no test/ in the .gem.
  #   :status    - what the last run found, so the table can be read without
  #                installing anything: :works, :broken, :impossible
  POPULAR = {
    # -- top 20 --------------------------------------------------------------
    'concurrent-ruby' => { require: 'concurrent', gem: true, exercise: 'concurrent-ruby',
                           status: :works,
                           note: 'needs the Monitor patch in ironruby/gem_compat.rb' },
    'i18n'            => { require: 'i18n',            gem: true, exercise: 'i18n',        status: :works },
    'activesupport'   => { require: 'active_support/all', gem: true, exercise: 'activesupport', status: :works },
    'rack'            => { require: 'rack',            gem: true, exercise: 'rack',        status: :works },
    'tzinfo'          => { require: 'tzinfo',          gem: true, exercise: 'tzinfo',      status: :works },
    'addressable'     => { require: 'addressable/uri', gem: true, exercise: 'addressable', status: :works },
    'faraday'         => { require: 'faraday',         gem: true, exercise: 'faraday',     status: :works },
    'public_suffix'   => { require: 'public_suffix',   gem: true, exercise: 'public_suffix', status: :works },
    'diff-lcs'        => { require: 'diff/lcs',        gem: true, exercise: 'diff-lcs',    status: :works },
    'rspec'           => { require: 'rspec',           gem: true, exercise: 'rspec',       status: :works },
    'multi_json'      => { require: 'multi_json',      gem: true, exercise: 'multi_json',  status: :works },
    'thor'            => { require: 'thor',            gem: true, exercise: 'thor',        status: :works },

    # -- 20-60 ---------------------------------------------------------------
    'unicode-display_width' => { require: 'unicode/display_width', gem: true,
                                 exercise: 'unicode-display_width', status: :works },
    'builder'         => { require: 'builder',         gem: true, exercise: 'builder',     status: :works },
    'mime-types'      => { require: 'mime/types',      gem: true, exercise: 'mime-types',  status: :works },
    'multipart-post'  => { require: 'multipart/post',  gem: true, exercise: 'multipart-post', status: :works },
    'activemodel'     => { require: 'active_model',    gem: true, exercise: 'activemodel', status: :works },
    'mini_mime'       => { require: 'mini_mime',       gem: true, exercise: 'mini_mime',   status: :works },
    'parser'          => { require: 'parser/current',  gem: true, exercise: 'parser',      status: :works },
    'activerecord'    => { require: 'active_record',   gem: true, exercise: 'activerecord', status: :works,
                           note: 'with the sqlite3 adapter over Src/Libraries/Sqlite3' },
    'parallel'        => { require: 'parallel',        gem: true, exercise: 'parallel',    status: :works },
    'jwt'             => { require: 'jwt',             gem: true, exercise: 'jwt',         status: :works },
    'rack-test'       => { require: 'rack/test',       gem: true, exercise: 'rack-test',   status: :works },
    'ast'             => { require: 'ast',             gem: true, exercise: 'ast',         status: :works },
    'tilt'            => { require: 'tilt',            gem: true, exercise: 'tilt',        status: :works },
    'rubyzip'         => { require: 'zip',             gem: true, exercise: 'rubyzip',     status: :works },
    'method_source'   => { require: 'method_source',   gem: true, exercise: 'method_source', status: :works },
    'mail'            => { require: 'mail',            gem: true, exercise: 'mail',        status: :works },
    'rainbow'         => { require: 'rainbow',         gem: true, exercise: 'rainbow',     status: :works },
    'ruby-progressbar'=> { require: 'ruby-progressbar', gem: true, exercise: 'ruby-progressbar', status: :works },
    'loofah'          => { require: 'loofah',          gem: true, exercise: 'loofah',      status: :works,
                           note: 'on the AngleSharp-backed Nokogiri shim' },
    'rubocop'         => { require: 'rubocop',         gem: true, exercise: 'rubocop', status: :works,
                           note: 'needed the nested-character-class and Psych::TreeBuilder fixes' },
    'connection_pool' => { require: 'connection_pool', gem: true, exercise: 'connection_pool', status: :works },
    'excon'           => { require: 'excon',           gem: true, exercise: 'excon',       status: :works },
    'regexp_parser'   => { require: 'regexp_parser',   gem: true, exercise: 'regexp_parser', status: :works },
    'zeitwerk'        => { require: 'zeitwerk',        gem: true, exercise: 'zeitwerk',    status: :works },
    'websocket-driver'=> { require: 'websocket/driver', gem: true, status: :broken,
                           note: 'C extension: the mask loop. The gem does not install without it' },
    'websocket-extensions' => { require: 'websocket/extensions', gem: true,
                                exercise: 'websocket-extensions', status: :works },
    'http-cookie'     => { require: 'http/cookie',     gem: true, exercise: 'http-cookie', status: :works },
    'domain_name'     => { require: 'domain_name',     gem: true, exercise: 'domain_name', status: :works },
    'coderay'         => { require: 'coderay',         gem: true, exercise: 'coderay',     status: :works },
    'pry'             => { require: 'pry',             gem: true, exercise: 'pry',         status: :works },
    'erubi'           => { require: 'erubi',           gem: true, exercise: 'erubi',       status: :works },
    'crass'           => { require: 'crass',           gem: true, exercise: 'crass',       status: :works },
    'nio4r'           => { require: 'nio4r',           gem: true, status: :impossible,
                           note: 'C extension: an epoll/kqueue selector. A .NET Socket.Select shim is plausible' },

    # -- 60-100 --------------------------------------------------------------
    'multi_xml'       => { require: 'multi_xml',       gem: true, exercise: 'multi_xml',   status: :works },
    'redis'           => { require: 'redis',           gem: true, exercise: 'redis',       status: :works },
    'rubocop-ast'     => { require: 'rubocop-ast',     gem: true, exercise: 'rubocop-ast', status: :works },
    'netrc'           => { require: 'netrc',           gem: true, exercise: 'netrc',       status: :works },
    'dotenv'          => { require: 'dotenv',          gem: true, exercise: 'dotenv',      status: :works },
    'httpclient'      => { require: 'httpclient',      gem: true, exercise: 'httpclient',  status: :works },
    'msgpack'         => { require: 'msgpack',         gem: true, status: :impossible,
                           note: 'C extension, no pure-Ruby fallback' },
    'rack-protection' => { require: 'rack/protection', gem: true, exercise: 'rack-protection', status: :works },
    'docile'          => { require: 'docile',          gem: true, exercise: 'docile',      status: :works },
    'simplecov'       => { require: 'simplecov',       gem: true, exercise: 'simplecov',   status: :works },
    'puma'            => { require: 'puma',            gem: true, status: :impossible,
                           note: 'C extension (the HTTP parser and the IO reactor)' },
    'sprockets'       => { require: 'sprockets',       gem: true, exercise: 'sprockets',   status: :works },
    'actionpack'      => { require: 'action_controller', gem: true, exercise: 'actionpack', status: :works },
    'actionview'      => { require: 'action_view',     gem: true, exercise: 'actionview',  status: :works },
    'activejob'       => { require: 'active_job',      gem: true, exercise: 'activejob',   status: :works },
    'globalid'        => { require: 'global_id',       gem: true, exercise: 'globalid',    status: :works },
    'railties'        => { require: 'rails',           gem: true, exercise: 'railties',    status: :works },
    'aws-sdk-core'    => { require: 'aws-sdk-core',    gem: true, exercise: 'aws-sdk-core', status: :works },
    'jmespath'        => { require: 'jmespath',        gem: true, exercise: 'jmespath',    status: :works },
    'ffi'             => { require: 'ffi',             gem: true, status: :impossible,
                           note: 'C extension; a .NET P/Invoke shim is feasible - see POPULAR.md' },

    # -- further down, but named in the brief ---------------------------------
    'sqlite3'         => { require: 'sqlite3',         gem: true, exercise: 'sqlite3',     status: :works,
                           note: 'IronRuby ships its own over Microsoft.Data.Sqlite' },
    'nokogiri'        => { require: 'nokogiri',        gem: true, exercise: 'nokogiri',    status: :works,
                           note: 'IronRuby ships its own over AngleSharp' },
    'sinatra'         => { require: 'sinatra/base',    gem: true, exercise: 'sinatra',     status: :works },
    'roda'            => { require: 'roda',            gem: true, exercise: 'roda',        status: :works },
    'sidekiq'         => { require: 'sidekiq',         gem: true, exercise: 'sidekiq',     status: :works },
    'webmock'         => { require: 'webmock',         gem: true, exercise: 'webmock',     status: :works },
    'vcr'             => { require: 'vcr',             gem: true, exercise: 'vcr',         status: :works },
    'yard'            => { require: 'yard',            gem: true, status: :broken,
                           note: "loads, but parsing Ruby needs Ripper's event parser (PARSER_EVENT_TABLE)" },
    'kramdown'        => { require: 'kramdown',        gem: true, exercise: 'kramdown',    status: :works },
    'asciidoctor'     => { require: 'asciidoctor',     gem: true, exercise: 'asciidoctor', status: :works },
    'terminal-table'  => { require: 'terminal-table',  gem: true, exercise: 'terminal-table', status: :works },
    'colorize'        => { require: 'colorize',        gem: true, exercise: 'colorize',    status: :works },
    'hashie'          => { require: 'hashie',          gem: true, exercise: 'hashie',      status: :works },
    'dry-inflector'   => { require: 'dry/inflector',   gem: true, exercise: 'dry-inflector', status: :works },
    'dry-configurable'=> { require: 'dry/configurable', gem: true, exercise: 'dry-configurable', status: :works },
    'tty-prompt'      => { require: 'tty-prompt',      gem: true, exercise: 'tty-prompt',  status: :works },
    'octokit'         => { require: 'octokit',         gem: true, exercise: 'octokit',     status: :works },
    'sawyer'          => { require: 'sawyer',          gem: true, exercise: 'sawyer',      status: :works },
    'listen'          => { require: 'listen',          gem: true, exercise: 'listen',      status: :works },
    'chunky_png'      => { require: 'chunky_png',      gem: true, exercise: 'chunky_png',  status: :works },
    'prawn'           => { require: 'prawn',           gem: true, exercise: 'prawn',       status: :works,
                           note: 'ttfunk pins bigdecimal ~> 3.1; read as >= 3.1 against the built-in 4.0.1' },
    'standard'        => { require: 'standard',        gem: true, exercise: 'standard', status: :works },
    'bcrypt'          => { require: 'bcrypt',          gem: true, status: :impossible,
                           note: 'C extension (the Blowfish KDF); a .NET port is a real project' },
    'oj'              => { require: 'oj',              gem: true, status: :impossible,
                           note: 'C extension; use json, which IronRuby implements' },
    'google-protobuf' => { require: 'google/protobuf', gem: true, status: :impossible,
                           note: 'C extension over upb' },
    'pg'              => { require: 'pg',              gem: true, status: :impossible,
                           note: 'C extension over libpq; Npgsql is the .NET answer, not a shim' },
    'mysql2'          => { require: 'mysql2',          gem: true, status: :impossible,
                           note: 'C extension over libmysqlclient' },
    'sassc'           => { require: 'sassc',           gem: true, status: :impossible,
                           note: 'C++ extension over libsass' },
    'rb-inotify'      => { require: 'rb-inotify',      gem: true, status: :broken,
                           note: 'needs ffi' },

    # -- top-100 gems that come along as somebody else's dependency ----------
    # There is nothing to exercise in these that the gem that pulls them in does
    # not already exercise, so the load check is the whole measurement and the
    # table says "load only" rather than pretending otherwise.
    'aws-sigv4'       => { require: 'aws-sigv4',       gem: true, status: :works,
                           note: 'the signing itself is exercised by aws-sdk-core' },
    'aws-partitions'  => { require: 'aws-partitions',  gem: true, status: :works },
    'aws-eventstream' => { require: 'aws-eventstream', gem: true, status: :works },
    'googleauth'      => { require: 'googleauth',      gem: true, status: :works },
    'signet'          => { require: 'signet/oauth_2/client', gem: true, status: :works },
    'unf'             => { require: 'unf',             gem: true, status: :works,
                           note: 'the pure-Ruby normaliser; unf_ext is a C++ extension' },
    'thread_safe'     => { require: 'thread_safe',     gem: true, status: :works,
                           note: 'a deprecated shim over concurrent-ruby' },
    'mini_portile2'   => { require: 'mini_portile2',   gem: true, status: :works,
                           note: 'a build helper for C extensions, so only loaded' },
    'rb-fsevent'      => { require: 'rb-fsevent',      gem: true, status: :works,
                           note: 'macOS only; inert here, exactly as on Linux CRuby' },
    'rails-html-sanitizer' => { require: 'rails-html-sanitizer', gem: true, status: :works,
                           note: 'on the Nokogiri shim, through loofah' },
    'rails-dom-testing' => { require: 'rails-dom-testing', gem: true, status: :works },
  }

  ALL = ENTRIES.merge(POPULAR)

  def self.entry(name)
    ALL[name]
  end

  def self.popular?(name)
    POPULAR.key?(name)
  end

  # The pure-Ruby default/bundled gems that go on $LOAD_PATH for every run, so
  # that a gem's dependencies (net-protocol, prettyprint, ...) resolve to the
  # modern copy rather than to nothing at all.  Native gems are excluded:
  # their lib/ is a stub that only requires a .so.  test-unit is excluded too -
  # its lib/test/unit.rb would shadow the one the suites are run with.
  PATH_GEMS = %w[
    abbrev base64 benchmark csv delegate drb english erb find fileutils
    forwardable getoptlong ipaddr logger matrix mutex_m net-ftp net-http
    net-imap net-pop net-protocol net-smtp observer open3 open-uri optparse
    ostruct prettyprint prime pp pstore racc rake resolv resolv-replace rexml
    rinda rss securerandom shellwords singleton time timeout tmpdir tsort uri
    weakref
  ]

  # One gem is installed under several versions (bigdecimal 3 and 4, rake 13.3
  # and 13.4, json 2 and 3); take the highest, and skip the platform-specific
  # builds, whose lib/ is a stub for a .so.
  def self.gem_lib(name)
    Dir["#{GEM_DIR}/#{name}-*/lib"].
      map { |d| [File.basename(File.dirname(d)).sub(/\A#{Regexp.escape(name)}-/, ''), d] }.
      reject { |v, _| v !~ /\A\d/ || v =~ /-/ }.
      max_by { |v, _| v.split('.').map(&:to_i) }&.last
  end

  def self.load_path(name, entry)
    # A popular-tier gem is resolved by RubyGems out of GEM_HOME, and putting
    # the stdlib tier's 1.9-era copies in front of it would be the wrong test.
    return [] if entry[:native] || entry[:gem]
    ([gem_lib(name)] + PATH_GEMS.map { |g| gem_lib(g) }).compact.uniq
  end

  # Where `igem.sh install <name>` put the gem, highest version wins.
  def self.installed_dir(name)
    home = ENV['GEM_HOME'] || File.join(Dir.home, '.local/share/gem/ironruby')
    Dir["#{home}/gems/#{name}-*"].
      select { |d| File.basename(d) =~ /\A#{Regexp.escape(name)}-\d/ }.
      max_by { |d| File.basename(d).sub(/\A#{Regexp.escape(name)}-/, '').split('.').map(&:to_i) }
  end

  def self.test_files(entry, name = nil)
    globs = entry[:tests] || []
    if entry[:gem]
      dir = name && installed_dir(name) or return []
      return globs.flat_map { |g| Dir[File.join(dir, g)] }.sort
    end
    globs.flat_map { |g|
      g.start_with?('/') ? Dir[g] : Dir[File.join(RUBY_SRC, g)]
    }.sort
  end

  # Util/gems/exercises/<name>.rb, the small hand-written exercise that is run
  # under IronRuby and under CRuby and diffed.
  def self.exercise_file(entry)
    return nil unless entry[:exercise]
    f = File.join(__dir__, 'exercises', "#{entry[:exercise]}.rb")
    File.exist?(f) ? f : nil
  end
end
