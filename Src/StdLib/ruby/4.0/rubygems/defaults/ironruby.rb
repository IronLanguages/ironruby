# frozen_string_literal: true
#
# RubyGems defaults for IronRuby.  rubygems.rb requires
# "rubygems/defaults/#{RUBY_ENGINE}" after rubygems/defaults.rb, which is the
# hook upstream reserves for an alternative implementation - see the comment at
# the top of rubygems.rb.  Everything IronRuby-specific about gem locations,
# the Ruby command RubyGems re-executes, and the platform a gem is built for
# lives here rather than in a patched copy of the vendored files.

module Gem
  ##
  # IronRuby ships the Ruby 4.0 standard library, so its gems live under
  # <libdir>/ruby/gems/4.0.0 - not under RbConfig's "ruby_version", which is
  # still the 1.9.1 that names the site_ruby directory.

  def self.default_dir
    @default_dir ||= File.join(RbConfig::CONFIG["rubylibprefix"], "gems", ruby_api_version)
  end

  ##
  # The API version gems are installed under.  RbConfig::CONFIG["ruby_version"]
  # is "1.9.1" for the site_ruby directory; the gem tree follows RUBY_VERSION
  # instead, as it does on CRuby.

  def self.ruby_api_version
    @ruby_api_version ||= RUBY_VERSION.split(".").first(2).push(0).join(".")
  end

  ##
  # Path for gems in the user's home directory.  Same layout as CRuby, but
  # keyed by engine ("ironruby") and the 4.0.0 API version, so IronRuby's
  # user gems never collide with the host Ruby's.

  def self.user_dir
    gem_dir = File.join(Gem.user_home, ".gem")
    gem_dir = File.join(Gem.data_home, "gem") unless File.exist?(gem_dir)
    File.join(gem_dir, ruby_engine, ruby_api_version)
  end

  ##
  # No host CRuby's gems.
  #
  # IronRuby reads only its own gem trees: the user's (user_dir), GEM_HOME and
  # GEM_PATH, and default_dir with the gems it ships.  It used to also offer a
  # CRuby 4.0 found on PATH - its <prefix>/lib/ruby/gems/4.0.0 - for third-party
  # gems, filtered so that none IronRuby ships came from there.  That still
  # loaded that CRuby's code into IronRuby in ways no filter caught: its
  # RubyGems plugins (rdoc's hooked rdoc 7.0.4 into every `gem install`), and
  # gems installed for one interpreter running on the other.  A gem IronRuby
  # needs is installed for IronRuby, with `igem install` or Bundler.

  ##
  # Where the default gems' specifications live.
  #
  # IronRuby ships its own, in <default_dir>/specifications/default, generated
  # by Util/gen-default-gemspecs.rb: one per library it provides itself, C#
  # implementations (bigdecimal, json, psych, openssl, zlib, prism, ...) and
  # vendored Ruby ones alike.  They are what tells the resolver that a library
  # is already here - without them `gem install activesupport` downloads
  # bigdecimal's C sources and tries to compile them.
  #
  # They are committed, so the directory always exists.  There is deliberately
  # no fallback to a host CRuby's specifications/default: that would make the
  # list of libraries IronRuby claims to provide depend on the machine.

  def self.default_specifications_dir
    @default_specifications_dir ||= File.join(default_dir, "specifications", "default")
  end

  ##
  # The standard library directories this interpreter was started with, in
  # $LOAD_PATH order.  -X:StdLib puts them at the front (behind -I, RUBYLIB and
  # RUBYOPT's -I, which come first), which is where the Ruby 4.0 tree and the
  # IronRuby tree live.

  def self.ironruby_stdlib_dirs # :nodoc:
    @ironruby_stdlib_dirs ||= begin
      lib = ironruby_lib_dir                                # <root>/Src/StdLib/ruby/4.0
      stdlib = File.expand_path("../..", lib)               # <root>/Src/StdLib
      [lib,
       File.join(stdlib, "ironruby")].select {|d| Dir.exist?(d) }
    end
  end

  ##
  # Where an activated gem's directories go on $LOAD_PATH.
  #
  # Upstream looks for RbConfig's sitelibdir, because on CRuby that entry sits
  # *ahead* of the standard library's own - so a gem that replaces a gemified
  # stdlib library wins.  Here the -X:StdLib directories are in front of
  # sitelibdir, so upstream's answer would insert activated gems *behind* the
  # bundled standard library: `gem "logger", "2.0"` would resolve, and then
  # `require "logger"` would load IronRuby's bundled 1.7.0 anyway.  Rails
  # depends on exactly this working (it installs newer logger, csv, base64,
  # benchmark, ... over the versions shipped here).
  #
  # So insert at the first standard library directory instead, which is the
  # same position relative to the library that CRuby uses, and still behind -I
  # and RUBYLIB.

  def self.load_path_insert_index
    indexes = ironruby_stdlib_dirs.filter_map {|dir| $LOAD_PATH.index(dir) }
    indexes.min || $LOAD_PATH.index(RbConfig::CONFIG["sitelibdir"]) || 0
  end

  ##
  # The gems whose version here is the only one this interpreter can have.
  # Their gemspecs carry metadata["ironruby_pinned"], written by
  # Util/gen-default-gemspecs.rb, which is also where the list and the reason for
  # each entry live.
  #
  # Two things put a library on it.  Most of them - bigdecimal, json, psych,
  # openssl, sqlite3, nokogiri, cgi, erb - have a C extension for their real
  # gem, and there is no compiler at the other end of a download.  The rest, irb
  # and reline, are part of IronRuby's standard library and their upstream
  # dependency tree ends at one: irb depends on rdoc, rdoc 8 on rbs, and rbs is
  # a C extension.
  #
  # A resolver that does not know this picks the newest release, as resolvers
  # do, and the install stops at extconf.rb: `gem "activerecord"` fails on
  # bigdecimal, and `gem "railties"` on rbs, for libraries IronRuby has been
  # providing all along.  So the answer for these names is fixed, and Bundler
  # uses this to say so.
  #
  # Returns name => Gem::Version.

  def self.ironruby_pinned_gems
    @ironruby_pinned_gems ||= begin
      pinned = {}
      begin
        Gem::Specification.default_stubs("*.gemspec").each do |stub|
          next unless stub.respond_to?(:to_spec)
          spec = begin
            stub.to_spec
          rescue StandardError
            nil
          end
          next unless spec
          next unless spec.metadata.is_a?(Hash) && spec.metadata["ironruby_pinned"]
          pinned[spec.name] = spec.version
        end
      rescue StandardError
        pinned = {}
      end
      pinned.freeze
    end
  end

  def self.default_path
    path = []
    path << user_dir if user_home && File.exist?(user_home)
    path << default_dir
    path << vendor_dir if vendor_dir && File.directory?(vendor_dir)
    path
  end

  ##
  # IronRuby's own gem home stays on Gem.path even when something narrows
  # GEM_HOME and GEM_PATH - which Bundler does on every command, pinning both to
  # its bundle_path (and GEM_PATH to nothing at all when a path is configured).
  # On CRuby that loses little, because bundle_path defaults to the very
  # directory the bundled gems are in; here it would hide them, and a Gemfile
  # that names rake or minitest would go to rubygems.org for a gem this tree
  # already has.  default_dir holds the bundled gems IronRuby ships
  # (Util/install-bundled-gems.rb), so it is a read-only extra source, like a
  # vendor directory, and appending it is always safe.  It goes last, so a gem
  # installed into GEM_HOME or GEM_PATH is found first.

  def self.path
    paths.path | [default_dir]
  end

  ##
  # No documentation unless asked for, as on TruffleRuby: generating ri and
  # rdoc takes longer than installing most gems, and is rarely read.  `gem
  # install --document` (or a "gem: --document" line in ~/.gemrc) still builds
  # it, through the hooks below.

  def self.platform_defaults
    { "gem" => "--no-document" }
  end

  # rdoc ships in IronRuby's standard library rather than as a gem, so no
  # install wrote the plugin stub that registers its hooks on CRuby (rdoc's
  # rubygems_plugin.rb, through <gem home>/plugins).  These register the same
  # two, and load rdoc only when one runs.  RDoc::RubyGemsHook.generate reads
  # the install's --document / --no-document itself.  ::RDoc, because inside
  # Gem, RDoc is Gem::RDoc - rubygems/rdoc.rb's alias for the hook class.
  done_installing do |installer, specs|
    require "rdoc/rubygems_hook"
    ::RDoc::RubyGemsHook.generate(installer, specs)
  end

  pre_uninstall do |uninstaller|
    require "rdoc/rubygems_hook"
    ::RDoc::RubyGemsHook.remove(uninstaller)
  end

  ##
  # RbConfig.ruby names the `ironruby` apphost, which starts without the -X:StdLib
  # argument that gives it Ruby 4's standard library.  RubyGems and
  # Bundler shell out to Gem.ruby to run binstubs and extconf.rb, so point it
  # at the ironruby (ironruby.cmd on Windows) launcher next to the source tree
  # when there is one - or at ir.sh (ir.cmd), its short name, in a tree from
  # before the launcher was called ironruby.

  def self.ruby
    @ruby ||= begin
      wrapper = nil
      begin
        # <root>/Src/StdLib/ruby/4.0/rubygems/defaults/ironruby.rb -> <root>
        root = File.expand_path("../../../../../..", __dir__)
        names = Gem.win_platform? ? %w[ironruby.cmd ir.cmd] : %w[ironruby ir.sh]
        wrapper = names.map { |name| File.join(root, name) }.find { |candidate| File.file?(candidate) }
      rescue StandardError
        wrapper = nil
      end

      ruby = wrapper || RbConfig.ruby
      ruby = "\"#{ruby}\"" if /\s/.match?(ruby)
      ruby
    end
  end

  ##
  # A binstub RubyGems writes gets `#!/usr/bin/env <ruby_install_name>` by
  # default, and RbConfig's ruby_install_name here is "ironruby" - which is the
  # launcher only where its directory is on PATH, and otherwise the apphost,
  # which would start without the arguments that make it a Ruby 4 interpreter.
  # Turn the env shebang off for every install, so the line becomes
  # `#!<Gem.ruby>`, the ironruby launcher.  That also makes
  # Bundler's CLI::Exec#ruby_shebang? recognise the binstub - it matches
  # "#!#{Gem.ruby}" - so `bundle exec <gem executable>` loads it in this process
  # instead of exec'ing it into whatever `ruby` is on PATH.
  #
  # This is a pre_install hook rather than a reopened Gem::Installer because
  # rubygems/installer.rb is loaded long after this file and would overwrite the
  # method.  A hook that returns false aborts the install, so it returns true.
  pre_install do |installer|
    if installer.respond_to?(:instance_variable_set) && !Gem.configuration[:custom_shebang]
      installer.instance_variable_set(:@env_shebang, false)
    end
    true
  end

  ##
  # Gems whose C extensions IronRuby cannot load, collected while scanning the
  # gem index.  See BasicSpecification#ignored? below.

  def self.ironruby_ignored_gems # :nodoc:
    @ironruby_ignored_gems ||= []
  end

  ##
  # The names of the gems IronRuby ships gemspecs for in
  # default_specifications_dir - the libraries it implements itself.  Read from
  # the file names rather than from the gemspecs, so that nothing is evaluated.

  def self.ironruby_default_gem_names # :nodoc:
    @ironruby_default_gem_names ||=
      Dir.glob(File.join(default_specifications_dir, "*.gemspec")).map do |file|
        File.basename(file, ".gemspec")[/\A(.+)-[^-]+\z/, 1]
      end.compact
  rescue StandardError
    @ironruby_default_gem_names = []
  end

  ##
  # Lazy loading of the default gemspecs.
  #
  # Upstream's Gem::Specification.load_defaults evaluates every gemspec in
  # Gem.default_specifications_dir at boot, purely to feed
  # Gem.register_default_spec - which only wants, per gem, the list of paths
  # that `require` should map to it.  On CRuby that directory holds a handful
  # of specs and the eval is compiled C; here it is IronRuby's own default
  # gemspecs, and evaluating them is by far the largest single cost of
  # `require "rubygems"`.
  #
  # Nothing at boot needs the Gem::Specification objects themselves, so the
  # require-name -> gemspec-file mapping is computed once and cached on disk.
  # A later boot restores the mapping from the cache and stores the gemspec's
  # *path* in Gem's map; Gem.find_default_spec - the only reader of that map -
  # evaluates the gemspec the first time a require actually hits it, and then
  # replaces the placeholders through the ordinary register_default_spec, so
  # the map ends up holding exactly what upstream would have put there.
  #
  # Set IRONRUBY_NO_GEM_CACHE=1 to fall back to upstream's eager scan.

  IRONRUBY_DEFAULT_SPEC_INDEX_VERSION = 1 # :nodoc:

  ##
  # The standard library this process is running out of.  A source tree can be
  # checked out many times over - every worktree on this box shares one home
  # directory - so it is part of the cache's identity: the
  # recorded feature paths point into *this* tree, and requiring another
  # worktree's copy of rubygems/dependency.rb would be a real bug.

  def self.ironruby_lib_dir # :nodoc:
    @ironruby_lib_dir ||= File.expand_path("../..", __dir__)
  end

  def self.ironruby_default_spec_index_file # :nodoc:
    # A plain deterministic checksum: String#hash is salted per process, and
    # requiring digest at boot would cost more than this whole cache saves.
    tag = ironruby_lib_dir.each_byte.inject(17) {|a, b| (a * 31 + b) & 0xffffffff }
    File.join(Gem.cache_home, "ironruby",
              "default-specs-#{ruby_api_version}-#{tag.to_s(36)}.cache")
  end

  ##
  # Cheap fingerprint of the default specifications directory: every gemspec's
  # name, size and mtime.  Installing, removing or updating a default gem
  # changes it, which is exactly when the mapping has to be rebuilt.

  def self.ironruby_default_spec_stamp(files) # :nodoc:
    files.sort.map do |file|
      stat = File.stat(file)
      [File.basename(file), stat.size, stat.mtime.to_i]
    end
  end

  ##
  # Evaluate +file+, a default gemspec, and let upstream's
  # register_default_spec put the real Gem::Specification into the map under
  # every name it owns.  Returns the spec, or nil when the gemspec is bad.

  def self.ironruby_register_default_spec_file(file) # :nodoc:
    spec = Gem::Specification.load(file)
    return nil unless spec

    register_default_spec spec
    spec
  end

  def self.find_default_spec(path) # :nodoc:
    spec = @path_to_default_spec_map[path]
    return spec unless spec.is_a?(String)

    # A placeholder: the gemspec has not been evaluated yet.  Doing so rewrites
    # every entry this gemspec owns, this one included.
    ironruby_register_default_spec_file(spec)
    spec = @path_to_default_spec_map[path]
    spec.is_a?(String) ? nil : spec
  end

  ##
  # Replacement for Gem::Specification.load_defaults.  Uses the on-disk
  # mapping when it is still valid, and otherwise does what upstream does
  # while recording the mapping for next time.

  def self.ironruby_load_default_specs # :nodoc:
    dir = Gem.default_specifications_dir
    files = Gem::Util.glob_files_in_dir("*.gemspec", dir)
    return if files.empty?

    stamp = nil
    cached = nil
    features = nil
    unless ENV["IRONRUBY_NO_GEM_CACHE"]
      begin
        stamp = ironruby_default_spec_stamp(files)
        data = File.open(ironruby_default_spec_index_file, "rb", &:read)
        data = Marshal.load(data)
        if data.is_a?(Hash) &&
           data[:version] == IRONRUBY_DEFAULT_SPEC_INDEX_VERSION &&
           data[:dir] == dir && data[:lib] == ironruby_lib_dir &&
           data[:stamp] == stamp
          cached = data[:map]
          features = data[:features]
        end
      rescue StandardError, NotImplementedError
        cached = nil
      end
    end

    if cached
      # Evaluating the gemspecs pulls a few of RubyGems' own autoloaded files
      # in - a gemspec with a dependency loads rubygems/dependency.rb.  Load
      # them here so that $LOADED_FEATURES is the same either way.
      Array(features).each {|f| require f if File.file?(f) }
      @path_to_default_spec_map.update(cached)
      ironruby_activate_already_loaded_defaults(cached)
      return
    end

    loaded_before = $LOADED_FEATURES.dup

    # Cache miss: do exactly what upstream does, and note which gemspec each
    # registered name came from so the next boot can skip the evaluation.
    origin = {}
    files.each do |file|
      spec = Gem::Specification.load(file)
      next unless spec

      origin[spec.object_id] = file
      register_default_spec spec
    end

    return unless stamp

    map = {}
    @path_to_default_spec_map.each do |name, spec|
      file = origin[spec.object_id]
      map[name] = file if file
    end
    ironruby_write_default_spec_index(dir, stamp, map, $LOADED_FEATURES - loaded_before)
  end

  def self.ironruby_write_default_spec_index(dir, stamp, map, features) # :nodoc:
    return if ENV["IRONRUBY_NO_GEM_CACHE"]

    file = ironruby_default_spec_index_file
    require "fileutils"
    FileUtils.mkdir_p File.dirname(file)
    tmp = "#{file}.#{Process.pid}"
    File.open(tmp, "wb") do |io|
      io.write Marshal.dump(version: IRONRUBY_DEFAULT_SPEC_INDEX_VERSION,
                            dir: dir, lib: ironruby_lib_dir,
                            stamp: stamp, map: map, features: features)
    end
    File.rename tmp, file
  rescue StandardError, NotImplementedError
    begin
      File.delete tmp if tmp && File.exist?(tmp)
    rescue StandardError
      nil
    end
  end

  ##
  # register_default_spec activates a default gem whose file the interpreter
  # has already loaded.  With placeholders in the map that check has to run the
  # other way round - over $LOADED_FEATURES rather than over every name - so
  # that the same gems end up activated without evaluating the other 45
  # gemspecs.

  def self.ironruby_activate_already_loaded_defaults(map) # :nodoc:
    return if $LOADED_FEATURES.empty?

    prefixes = default_gem_load_paths.map {|lp| "#{lp}/" }
    pending = nil
    $LOADED_FEATURES.each do |feature|
      prefixes.each do |prefix|
        next unless feature.start_with?(prefix)

        file = map[feature[prefix.length..-1]]
        next unless file.is_a?(String)

        (pending ||= []) << file
      end
    end
    return unless pending

    pending.uniq.each {|file| ironruby_register_default_spec_file(file) }
  end

  class Specification
    def self.load_defaults # :nodoc:
      Gem.ironruby_load_default_specs
    end

    ##
    # Never compile a C extension.
    #
    # RubyGems builds a gem's extensions lazily, the first time something
    # activates it - installing a gem builds its dependencies' extensions too.
    # On IronRuby that can only fail: there is no ruby.h and no CRuby ABI to
    # compile against.  The attempt writes a half-built Makefile and a
    # gem_make.out into the gem's directory before it fails, and takes the whole
    # `gem install` down with it.
    #
    # Doing nothing leaves the gem exactly as it is: its .rb files load, the
    # missing .so raises a plain LoadError if something really reaches for it,
    # and the pure-Ruby fallback most such gems carry (racc/parser.rb rescues
    # the LoadError of racc/cparse) is used instead.  The name is recorded so
    # that `gem env` and -w can report it.

    def build_extensions # :nodoc:
      return if extensions.empty?
      return if default_gem?

      Gem.ironruby_ignored_gems << full_name unless Gem.ironruby_ignored_gems.include?(full_name)
      nil
    end
  end

  ##
  # ... and do not compile one while *installing* a gem either.
  #
  # Gem::Installer#build_extensions is a different method from the one above -
  # it runs as part of `gem install` / `bundle install`, before the gem is ever
  # activated - and it is the one that runs extconf.rb.  Leaving it alone makes
  # every Gemfile that mentions a gem with an extension uninstallable, even when
  # the extension is beside the point: liquid's Gemfile.lock pins bigdecimal
  # 3.2.2, and bundler would rather abort the whole bundle than install the
  # .rb files of a gem whose .so will not build.
  #
  # Skipping the build installs the gem's Ruby files and nothing else, which is
  # what is actually usable here - and for a library IronRuby provides itself
  # (bigdecimal, json, psych, ...) BasicSpecification#ignored? below then routes
  # `require` to the implementation in Src/Libraries rather than to the .so that
  # was never built.
  #
  # Prepended rather than reopened: rubygems/installer.rb is loaded long after
  # this file and would overwrite a plain definition.
  #
  # And prepended only once that file has created the class (const_added below),
  # however it gets there - Gem's autoload or one of the plain requires of
  # rubygems/installer scattered through RubyGems and Bundler.  Reopening
  # Gem::Installer here would trigger the autoload at boot, and with it the
  # whole installer stack - package, the tar readers, the extension builders,
  # security and the openssl it requires - which is a third of the cost of
  # `require "rubygems"`, and which CRuby does not load until something
  # actually installs a gem.

  module IronRubyNoExtensionBuild # :nodoc:
    def build_extensions
      return if spec.extensions.empty?

      name = spec.full_name
      Gem.ironruby_ignored_gems << name unless Gem.ironruby_ignored_gems.include?(name)
      nil
    end
  end

  ##
  # Gems that are C extensions upstream and that IronRuby has exactly one of,
  # built in, at a newer major version than some popular gems were released
  # for.  Such a gem cannot get the version it asks for here - there is no
  # compiler for the C one - and the one here does what it uses, so a
  # requirement on an older major version is read with its upper bound lifted -
  # "~> 1.7" as ">= 1.7" - for these names only, from the given major version
  # on, and only where the built-in version then satisfies it.  Only the bound
  # "~>" implies is lifted: "< 4" written out is a gem saying 4 does not work.
  #
  # prism: IronRuby parses with it, at 2.x since it follows prism 2.0's
  # serialization; its FFI backend would need a libprism 1.x beside that.
  # rubocop-ast (~> 1.7), so rubocop and standard, and minitest 6 (~> 1.5),
  # which every Rails app's tests pull in, ask for 1.x and use the Ruby API,
  # which 2.0 kept for what they call: their exercises in Util/gems give the
  # same output as on CRuby with prism 1.9.  0.x is an API from before 1.0.
  #
  # bigdecimal: the C# one in Src/Libraries presents as 4.0.1 and passes
  # ruby/spec's library/bigdecimal.  ttfunk, so prawn, asks for ~> 3.1 and
  # only tells a BigDecimal operand from the others by its class.  2.0 removed
  # BigDecimal.new, so a requirement for 1.x is left alone.
  IRONRUBY_LIFTED_REQUIREMENTS = { "prism" => 1, "bigdecimal" => 2 }.freeze # :nodoc:

  module IronRubyBuiltInRequirement # :nodoc:
    def requirement
      requirement = super
      from_major = IRONRUBY_LIFTED_REQUIREMENTS[name]
      return requirement unless from_major
      built_in = Gem.ironruby_pinned_gems[name]
      return requirement if built_in.nil? || requirement.satisfied_by?(built_in)
      return requirement if requirement.requirements.any? { |_, version| version.segments.first < from_major }

      lifted = requirement.requirements.map do |op, version|
        op == "~>" ? ">= #{version}" : "#{op} #{version}"
      end
      lifted = Gem::Requirement.new(lifted)
      lifted.satisfied_by?(built_in) ? lifted : requirement
    end
  end

  def self.const_added(name) # :nodoc:
    super
    # Registering an autoload fires this too; only a real class is prepended to.
    Installer.prepend(IronRubyNoExtensionBuild) if name == :Installer && !autoload?(:Installer)
    Dependency.prepend(IronRubyBuiltInRequirement) if name == :Dependency && !autoload?(:Dependency)
  end

  Installer.prepend(IronRubyNoExtensionBuild) if const_defined?(:Installer, false) && !autoload?(:Installer)
  Dependency.prepend(IronRubyBuiltInRequirement) if const_defined?(:Dependency, false) && !autoload?(:Dependency)

  class BasicSpecification; end

  module IronRubyProvidedGem # :nodoc:
    ##
    # An *installed* gem that is the Ruby half of a C extension IronRuby
    # implements itself: bigdecimal, json, psych, ...  Its lib/<name>.rb is a
    # one-line stub that requires <name>.so, so it has nothing to offer here and
    # everything to break - the complete implementation is in Src/Libraries.
    #
    # Decided from the gemspec's own extensions rather than from
    # missing_extensions?, which is not a reliable answer to this question:
    # Bundler replaces it (Bundler::StubSpecification#missing_extensions? has an
    # IronRuby carve-out of its own, so that extension gems stay resolvable),
    # and under `bundle exec` it reports false for exactly these gems.

    def ironruby_provided_library? # :nodoc:
      return @ironruby_provided_library unless @ironruby_provided_library.nil?

      @ironruby_provided_library =
        begin
          !default_gem? &&
            Gem.ironruby_default_gem_names.include?(name) &&
            respond_to?(:extensions) && !extensions.empty?
        rescue StandardError
          false
        end
    end

    ##
    # Upstream marks a gem "ignored" when its extensions are not built for the
    # running Ruby, and Gem::Dependency#matching_specs then rejects it.  On
    # IronRuby a C extension is never built, so that rule would make every gem
    # with an extension - and every gem that merely depends on one - impossible
    # to activate: parser depends on racc, and racc's .so exists only for
    # CRuby.  Most such gems carry a pure-Ruby fallback that the extension
    # merely accelerates (racc/parser.rb rescues the LoadError of racc/cparse),
    # and the ones that do not fail at require time with a plain LoadError for
    # the extension file, which is a better answer than refusing to resolve.
    #
    # The one case where upstream's rule is still right is a library IronRuby
    # provides itself: there the default gem must win.
    #
    # The names are recorded so `gem env` and -w can report them.

    def ignored?
      if @ironruby_ignored.nil?
        @ironruby_ignored = ironruby_provided_library?
        if @ironruby_ignored || super
          Gem.ironruby_ignored_gems << full_name unless Gem.ironruby_ignored_gems.include?(full_name)
        end
      end

      @ironruby_ignored
    end

    ##
    # ... and say nothing about it.
    #
    # The warning upstream prints from contains_requirable_file? - "Ignoring
    # bigdecimal-4.0.1 because its extensions are not built.  Try: gem pristine
    # bigdecimal" - is good advice on CRuby and wrong here twice over: the
    # extension cannot be built at all, and the library is not missing, it is in
    # Src/Libraries.  Nothing is being lost, so nothing is worth warning about.
    # A gem IronRuby does *not* provide still warns, which is the case the
    # message was written for.

    def contains_requirable_file?(file)
      return false if ironruby_provided_library?

      super
    end

    ##
    # Nor may such a gem put its lib directory on $LOAD_PATH.
    #
    # contains_requirable_file? above covers the gem *activation* route, but not
    # Bundler's: Bundler resolves a Gemfile itself and then unshifts every
    # selected gem's load paths (Bundler::Runtime#setup -> spec.load_paths,
    # which is this method).  bigdecimal's lib/bigdecimal.rb is a one-line stub
    # that requires bigdecimal.so, so a Gemfile.lock pinning bigdecimal 3.2.2 -
    # liquid's does - would otherwise put that stub ahead of IronRuby's own
    # implementation and turn `require "bigdecimal"` into a LoadError for a .so
    # that was never built and never can be.  With no require paths the gem is
    # installed and resolved but invisible to require, which is exactly right:
    # what it provides is already here.

    def full_require_paths
      return [] if ironruby_provided_library?

      super
    end
  end

  BasicSpecification.prepend(IronRubyProvidedGem)
end
