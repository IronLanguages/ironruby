# Compatibility patches for gems that assume an engine they know about.
#
# A gem that branches on RUBY_ENGINE sometimes lands on a fallback path that is
# broken for everyone outside its matrix.  Where the fix belongs upstream but a
# popular library is unusable without it, register it here: the patch runs once,
# right after the file that needs it is required, and only if the condition that
# makes it necessary actually holds.
#
# Keep this list short, keep every entry commented with the upstream reason, and
# prefer fixing IronRuby over patching a gem.
module IronRuby
  module GemCompat
    @patches = Hash.new { |h, k| h[k] = [] }
    @applied = {}
    @refused = {}

    class << self
      # Run +block+ after the feature whose path ends in +suffix+ is required.
      def on_require(suffix, &block)
        @patches[suffix] << block
        self
      end

      # Make `require feature` raise LoadError, for a library that cannot run
      # here and fails in a way its callers do not expect.  A LoadError is what
      # a library that only *might* be present is guarded with.
      def refuse(feature, reason)
        @refused[feature] = reason
        self
      end

      # Called by the require hook before it loads anything.
      def check_refused(feature)
        return if @refused.empty? || !feature.is_a?(::String)
        reason = @refused[feature]
        raise ::LoadError, "cannot load such file -- #{feature} (#{reason})" if reason
      end

      # Called by the require hook with the feature as the caller spelled it.
      def after_require(feature)
        return if @patches.empty?
        name = feature.to_s
        @patches.each do |suffix, blocks|
          next unless name == suffix || name.end_with?("/#{suffix}")
          next if @applied[suffix]
          @applied[suffix] = true
          blocks.each do |block|
            begin
              block.call
            rescue ::StandardError, ::ScriptError
              # A compatibility patch must never break the require it follows.
            end
          end
        end
      end
    end
  end

end

# This file counts as internal (RubyStackTraceBuilder._InternalFiles), so the extra
# frame the wrapper adds is not reported -- `warn(uplevel:)` counts frames, and a
# wrapper that showed up would make every warning point one level too shallow.
#
# Wrap Kernel#require by capturing the current implementation rather than by
# prepending a module.  RubyGems boots later and does
# `alias_method :gem_original_require, :require`, which picks up whatever is in
# front -- a prepended module included -- and then calls it from its own #require.
# A wrapper that used `super` would therefore be re-entered by the very method it
# delegates to, and the second `super` would find nothing.  Binding the previous
# implementation makes the chain a straight line: RubyGems -> this -> the loader.
#
# Both copies of require have to be wrapped.  `module_function` gives Kernel a
# singleton copy of the method alongside the private instance one, and they are
# separate methods: redefining Kernel#require leaves Kernel.require pointing at
# the original.  Ruby 4.0's bundled_gems.rb routes *every* require through the
# singleton copy once Bundler.setup has run -
#
#     kernel_class.send(:alias_method, :no_warning_require, :require)
#     kernel_class.send(:define_method, :require) { |name| ...
#       kernel_class.send(:no_warning_require, name) }
#
# with kernel_class == ::Kernel, and `Kernel.send` finds the singleton's alias
# first - so wrapping only the instance method makes every patch here silently
# stop applying inside a bundle, which is where the gems that need them are.
module Kernel # :nodoc:
  previous_require = instance_method(:require)

  define_method(:require) do |feature|
    ::IronRuby::GemCompat.check_refused(feature)
    loaded = previous_require.bind(self).call(feature)
    ::IronRuby::GemCompat.after_require(feature) if loaded
    loaded
  end

  private :require

  previous_module_require = singleton_class.instance_method(:require)

  singleton_class.send(:define_method, :require) do |feature|
    ::IronRuby::GemCompat.check_refused(feature)
    loaded = previous_module_require.bind(self).call(feature)
    ::IronRuby::GemCompat.after_require(feature) if loaded
    loaded
  end

  singleton_class.send(:public, :require)
end

# concurrent-ruby: Concurrent::Map falls back to SynchronizedMapBackend for any
# engine that is not MRI, JRuby or TruffleRuby, and that backend guards each
# method with a non-reentrant Mutex -- its own comment says "the synchronized
# methods are not allowed to call each other".  A Map built with a default_proc
# does exactly that (the block runs inside #[] and calls #compute_if_absent), so
# the first lookup deadlocks.  ActiveSupport::Notifications::Fanout and i18n both
# build such a Map, which makes `require "active_record"` and every translation
# raise ThreadError on IronRuby.
#
# Monitor has the same mutual exclusion between threads and is reentrant within
# one, which is the property the backend's callers need.  Upstream fix pending.
IronRuby::GemCompat.on_require("concurrent/collection/map/synchronized_map_backend") do
  backend = Concurrent::Collection::SynchronizedMapBackend
  if backend.instance_method(:initialize).owner == backend
    backend.class_eval do
      def initialize(*args, &block)
        super
        @mutex = ::Monitor.new
      end
    end
  end
end

# thread_safe 0.3.6 (2016; shipit-engine still pulls it in through virtus and
# axiom-types) picks ThreadSafe::Cache's backend by RUBY_ENGINE: JRuby gets a
# Java one, CRuby one that leans on the GVL, and every other engine
# SynchronizedCacheBackend, which does `require 'mutex_m'`.  That is the right
# backend here - IronRuby has no GVL - but mutex_m left the default gems in Ruby
# 3.4, so inside a bundle that does not list it the require is a LoadError, and
# `Bundler.require` aborts on the first gem that touches ThreadSafe::Cache.
# CRuby never takes that path, so the bundle works there.
#
# IronRuby ships mutex_m as a bundled gem; put its lib directory at the end of
# the load path once thread_safe starts loading, so that this one require finds
# it.  A Gemfile that does list mutex_m still gets its own copy first.
IronRuby::GemCompat.on_require("thread_safe/version") do
  lib = Dir.glob(File.join(::Gem.default_dir, "gems", "mutex_m-*", "lib")).max
  $LOAD_PATH << lib if lib && !$LOAD_PATH.include?(lib)
end

# ... and thread_safe.rb defines ThreadSafe::Array and ThreadSafe::Hash only
# for JRuby (JRuby::Synchronized), CRuby (plain ::Array/::Hash, safe under the
# GVL) and Rubinius (Monitor-wrapped subclasses), so on any other engine the
# first `ThreadSafe::Array.new` is a NameError.  Rubinius's version is the one
# that is right without a GVL; TruffleRuby carries the same code as
# lib/patches/thread_safe.rb.  Like TruffleRuby's, it sets the monitor up in
# initialize - Rubinius did it in a Ruby-level allocate, which IronRuby, like
# CRuby, does not call for every new object - and each wrapper also creates it
# on first use, for instances that never ran initialize (#dup, Marshal.load).
IronRuby::GemCompat.on_require("thread_safe") do
  next if ::ThreadSafe.const_defined?(:Array, false) && ::ThreadSafe.const_defined?(:Hash, false)
  require "monitor"

  [[:Hash, ::Hash], [:Array, ::Array]].each do |name, base|
    next if ::ThreadSafe.const_defined?(name, false)
    klass = ::ThreadSafe.const_set(name, ::Class.new(base))
    klass.class_eval do
      def initialize(*args, &block)
        @_monitor = ::Monitor.new
        super
      end

      private

      def _mon
        @_monitor ||= ::Monitor.new
      end
    end
    base.instance_methods(false).each do |method|
      klass.class_eval <<-RUBY, __FILE__, __LINE__ + 1
        def #{method}(*args, &block)
          _mon.synchronize { super }
        end
      RUBY
    end
  end
end

# bindex (skiptrace): it gives every exception the Bindings of the frames it
# was raised through, from a C extension on CRuby and from VM internals on JRuby
# and Rubinius; for any other engine it defines nothing, and Exception#bindings
# is a NoMethodError.  web-console, in every `rails new` Gemfile, asks for it
# while rendering the development error page - so on IronRuby each error page,
# a plain 404 for a missing record included, became a 500.
#
# The bindings themselves need the runtime to capture them at raise time, which
# IronRuby does not do yet.  Until it does, Exception#bindings answers what it
# answers on those engines for an exception nobody captured bindings for, and
# the error page renders; its console has no frame to evaluate in.
IronRuby::GemCompat.on_require("bindex") do
  unless ::Exception.method_defined?(:bindings)
    ::Exception.class_eval do
      def bindings
        @bindings || []
      end
    end
  end
end

# power_assert: Parser#valid_syntax? checks a line with
# RubyVM::InstructionSequence.compile, and PowerAssert::Parser builds one at
# load time, so on an engine without RubyVM the require dies with a NameError.
# test-unit - a CRuby 4.0 bundled gem, shipped here - requires power_assert
# under `rescue LoadError, SyntaxError` and falls back to plain assertions, so
# the NameError escapes and takes `require "test/unit"` down with it.
# power_assert itself raises LoadError on an engine whose TracePoint it cannot
# use; IronRuby's TracePoint passes that check, and RubyVM is the part that is
# missing.  Faking RubyVM would mislead everything else that tests for it.
IronRuby::GemCompat.refuse("power_assert", "power_assert needs RubyVM::InstructionSequence, which IronRuby does not have")

# Bundler: a library IronRuby implements itself - bigdecimal, json, psych,
# sqlite3, nokogiri, cgi, ... - has exactly one version here, the one the
# default gemspec in Src/StdLib/ruby/gems names.  Every release of the same gem
# on rubygems.org is C source, and nothing here compiles C.  Bundler does not
# know that: it resolves against the newest release, as resolvers do, so
# `gem "activerecord"` picks up whatever bigdecimal was published this month and
# the install stops at extconf.rb - for a library that has been present all
# along.
#
# So the index Source::Rubygems hands the resolver is narrowed: for each of
# those names, IronRuby's own spec is the only candidate.  A Gemfile that cannot
# be satisfied that way now says so during resolution, naming the version that
# is here, instead of failing in a compiler.
#
# A Gemfile.lock is the other half.  It names an exact version - liquid's pins
# bigdecimal 3.2.2, lobsters' bcrypt 3.1.20 and psych 5.2.6, ruby-lsp's prism
# 1.7.0 - and Bundler looks that exact version up when it materializes the
# lock, before any resolution.  The narrowed index has only IronRuby's version,
# so the lookup missed and the bundle failed with "Could not find
# bigdecimal-3.2.2 in locally installed gems" - after `bundle install` had
# downloaded the C gem and declared success.  So an exact lookup of such a name
# is answered with IronRuby's spec instead, the way a lock naming another
# platform's variant of a gem is answered with this platform's.  The lockfile
# itself is left as it is: the same checkout is often run by CRuby too, which
# must go on getting the version it names.  That substitution is made only when
# IronRuby's version satisfies what the Gemfile asks for - `gem "sqlite3",
# "~> 1.4"` must not quietly be handed sqlite3 2.x's API - and otherwise the
# lookup still misses and Bundler's own error names the locked version.
#
# This is a patch rather than an edit to the vendored copy because Bundler
# re-executes itself into the version a Gemfile.lock's BUNDLED WITH names, and
# that version is a gem, not the one in Src/StdLib - the patch has to reach it
# too.
IronRuby::GemCompat.on_require("bundler") do
  if defined?(::Gem.ironruby_pinned_gems) && ::Bundler::Source::Rubygems.method_defined?(:specs)
    unless ::Bundler::Index.method_defined?(:ironruby_replace_specs!)
      ::Bundler::Index.class_eval do
        # Make +spec+ the only candidate this index offers under its name.
        def ironruby_replace_specs!(spec)
          specs = instance_variable_get(:@specs)
          duplicates = instance_variable_get(:@duplicates)
          specs[spec.name] = { spec.full_name => spec }
          duplicates.delete(spec.name)
          instance_variable_get(:@cache).clear
          (@ironruby_pinned_specs ||= {})[spec.name] = spec
          self
        end
      end

      locked_pins = Module.new do
        # An exact lookup (a locked spec, or [name, version]) of a name the
        # index was narrowed to: IronRuby's spec when the exact one is absent.
        def local_search(query)
          results = super
          return results unless results.empty? && @ironruby_pinned_specs

          name =
            case query
            when Array then query.first
            when String then nil
            else query.respond_to?(:name) ? query.name : nil
            end
          spec = name && @ironruby_pinned_specs[name]
          return results unless spec

          unless ::Gem.ironruby_lock_substitutable?(spec)
            # Nothing installable can satisfy it: the release the Gemfile
            # wants is C source.  Say so now rather than let `bundle install`
            # fetch it, report success, and fail at Bundler.setup.
            version = query.is_a?(Array) ? query[1] : (query.version if query.respond_to?(:version))
            raise ::Bundler::GemNotFound,
                  "#{name} #{version} is locked, but IronRuby provides #{name} #{spec.version} itself " \
                  "and the Gemfile's requirement excludes that version; #{name}'s other releases " \
                  "are a C extension, which IronRuby cannot build"
          end

          [spec]
        end
      end
      ::Bundler::Index.prepend(locked_pins)
    end

    # Whether IronRuby's +spec+ may stand in for whatever version a lockfile
    # names: true unless the Gemfile's own requirement on that gem excludes it.
    def (::Gem).ironruby_lock_substitutable?(spec)
      definition = ::Bundler.instance_variable_get(:@definition)
      return true unless definition

      definition.dependencies.select {|d| d.name == spec.name }.all? do |dep|
        dep.requirement.satisfied_by?(spec.version)
      end
    rescue StandardError
      true
    end

    native_gems = Module.new do
      def specs
        index = super
        return index unless @allow_local
        return index if @ironruby_pinned_gems_applied

        @ironruby_pinned_gems_applied = true
        ::Gem.ironruby_pinned_gems.each do |name, version|
          spec = default_specs.search(name).find {|s| s.version == version }
          index.ironruby_replace_specs!(spec) if spec
        end
        index
      end

      # Bundler replaces a default gem with the real one whenever the remote has
      # a .gem of the same version - that is how a CRuby user gets a compiled
      # json over the gemified stdlib copy.  Here the real one is C source, so
      # the answer has to be "there is no such gem": the implementation IronRuby
      # already has is the one that must be used.
      def cached_built_in_gem(spec, *args, **kwargs)
        return nil if ::Gem.ironruby_pinned_gems[spec.name] == spec.version

        super
      end
    end
    ::Bundler::Source::Rubygems.prepend(native_gems)
  end

  # `bundle exec rake`: Bundler looks the command up on PATH, with the bundle's
  # own bin directory in front.  On CRuby a bundled gem's binstub is in
  # RbConfig's bindir, which is on PATH; here it is in Src/StdLib/bin, which
  # ir.sh and ir.cmd put on RUBYPATH instead - putting it on PATH would hand
  # IronRuby's `gem` and `bundle` to every CRuby a child process starts.  So
  # when the bundle's bin directory does not have the command, look in RUBYPATH
  # before PATH, which is the order `ir -S` uses.  Without this the command
  # was either missing, or - on a machine with CRuby - the host's binstub,
  # which Bundler execs, so `bundle exec rake` silently ran CRuby.
  if ::Bundler.respond_to?(:which) && !::Bundler.respond_to?(:ironruby_rubypath_which)
    rubypath_which = Module.new do
      def ironruby_rubypath_which(executable) # :nodoc:
        return nil if executable.to_s.empty? || executable.to_s.include?("/")
        (ENV["RUBYPATH"] || "").split(File::PATH_SEPARATOR).each do |dir|
          next if dir.empty?
          found = find_executable(File.expand_path(executable, dir))
          return found if found
        end
        nil
      end

      def which(executable)
        found = super
        bundle_bin = begin
          File.join(::Bundler.bundle_path.to_s, "bin")
        rescue ::StandardError
          nil
        end
        return found if found && bundle_bin && File.dirname(found) == bundle_bin
        ironruby_rubypath_which(executable) || found
      end
    end
    ::Bundler.singleton_class.prepend(rubypath_which)
  end

  # `gem "tzinfo-data", platforms: %i[windows jruby]`, which every `rails new`
  # Gemfile has: Bundler keeps a platform-restricted gem only for the lockfile
  # platforms whose *generic* platform the restriction names, and to it
  # IronRuby's universal-dotnet is generic `ruby` - on Windows as everywhere.
  # So tzinfo-data was never installed, and a Rails app on Windows, which has
  # no zoneinfo, died in its first initializer with TZInfo::DataSourceNotFound.
  #
  # The .NET platform is the same on every OS, so for it the restriction is
  # answered the way `current_platform?` answers it - by the Ruby running now.
  if ::Bundler::Dependency.method_defined?(:gem_platforms) &&
     !::Bundler::Dependency.method_defined?(:ironruby_dotnet_platforms)
    dotnet_platforms = Module.new do
      # The dotnet platforms, and - since Bundler filters what it installs and
      # loads by the *generic* local platform, which for dotnet is `ruby` - that
      # one too when it stands for the dotnet platform this process runs on.
      def ironruby_dotnet_platforms(valid_platforms) # :nodoc:
        dotnet = ->(p) { p.respond_to?(:os) && p.os == "dotnet" }
        local_is_dotnet = dotnet.call(::Bundler.local_platform)
        valid_platforms.select do |p|
          dotnet.call(p) || (local_is_dotnet && p == ::Bundler.generic_local_platform)
        end
      end

      # Same order as valid_platforms, which is what Bundler's comment promises.
      def gem_platforms(valid_platforms)
        found = super
        return found if platforms.empty? || force_ruby_platform || !current_platform?
        dotnet = ironruby_dotnet_platforms(valid_platforms)
        return found if dotnet.empty?
        valid_platforms.select { |p| found.include?(p) || dotnet.include?(p) }
      end
    end
    ::Bundler::Dependency.prepend(dotnet_platforms)
  end
end
