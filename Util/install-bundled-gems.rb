# frozen_string_literal: true
#
# Installs the bundled gems of the CRuby release IronRuby's standard library
# comes from into IronRuby's own default gem home,
#
#     Src/StdLib/ruby/gems/4.0.0/gems/<name>-<version>/
#     Src/StdLib/ruby/gems/4.0.0/specifications/<name>-<version>.gemspec
#
# and writes a binstub for each of their executables into Src/StdLib/bin, the
# directory ir.sh and ir.cmd put on RUBYPATH.  Run it with the built
# interpreter, from the root of the source tree, against an unpacked CRuby
# source tarball (the .gem files are in its gems/ directory):
#
#     ./ir.sh Util/install-bundled-gems.rb /path/to/ruby-4.0.9
#     ./ir.sh Util/install-bundled-gems.rb --check /path/to/ruby-4.0.9
#
# The second form changes nothing: it reports what is out of date and every
# library in the load-path standard library that shadows a shipped gem, and
# exits 1 if there is any.
#
# Why this exists
# ---------------
# CRuby 4.0 does not keep rake, rexml, csv, logger, minitest and the rest in its
# library directory.  They are "bundled gems": real gems, installed with the
# interpreter into its default gem home, which `require` finds by activating
# them.  IronRuby used to have 1.9-era copies of most of them on $LOAD_PATH -
# rake 0.8.7, rexml 3.1.7.3, MiniTest 1.6 - and got the modern ones only on a
# machine that also had CRuby 4.0 installed, through that CRuby's gem tree
# (which IronRuby no longer reads at all).  With
# these installed here the modern gem is always present, on every machine and
# on Windows, and the 1.9 copies are gone.
#
# The result is committed, like the rest of Src/StdLib.  CI and a Windows
# checkout have no CRuby 4.0 source tree and no network access to count on, and
# the gems are plain Ruby files that are part of the standard library IronRuby
# ships.  Only the installed files are kept: the cache/*.gem copies RubyGems
# also writes are deleted (they are the same bytes again, compressed), and so
# is anything a C extension build would leave behind - there is none, see
# below.
#
# The versions are not in this file.  They come from the source tree's own
# gems/bundled_gems, so moving to a new CRuby release is: unpack its tarball,
# run this, commit.  What *is* here is the decision for each name: shipped, or
# skipped and why - and, for the few FROM_RUBYGEMS ships at another version than
# CRuby's, that version and its SHA-256.  A name in bundled_gems that is in neither table stops the
# script, so a new bundled gem cannot be shipped or dropped by accident.

require "rubygems"
require "rubygems/installer"
require "rubygems/remote_fetcher"
require "digest/sha2"
require "fileutils"
require "tmpdir"

ROOT = File.expand_path("..", __dir__)
STDLIB = File.join(ROOT, "Src", "StdLib")
GEM_HOME = File.join(STDLIB, "ruby", "gems", "4.0.0")
BIN_DIR = File.join(STDLIB, "bin")

# The directories -X:StdLib and RbConfig put on $LOAD_PATH.  A file in one of
# them whose path matches a shipped gem's lib/ file wins over the gem - RubyGems
# tries the load path before it activates anything - so --check reports every
# such file.
LOAD_PATH_DIRS = [
  File.join(STDLIB, "ironruby"),
  File.join(STDLIB, "ruby", "4.0"),
  File.join(STDLIB, "ruby", "site_ruby", "1.9.1"),
  File.join(STDLIB, "ruby", "site_ruby"),
].freeze

# The bundled gems IronRuby ships.  All pure Ruby, or - racc - pure Ruby with an
# optional C accelerator whose absence the gem handles itself (racc/parser.rb
# rescues the LoadError of racc/cparse and uses its Ruby implementation).
SHIPPED = %w[
  minitest power_assert rake test-unit rexml rss
  net-ftp net-imap net-pop net-smtp
  matrix prime racc mutex_m getoptlong base64 observer abbrev
  resolv-replace rinda drb csv ostruct pstore benchmark logger
].freeze

# The bundled gems IronRuby does not ship, and why.
SKIPPED = {
  # IronRuby implements these itself.  The gem is a C extension whose lib/ is a
  # stub that loads the .so, so installing it would only add a second, broken
  # copy; the default gemspecs Util/gen-default-gemspecs.rb writes are what tell
  # RubyGems and Bundler that the library is present.
  "bigdecimal" => "C extension; IronRuby implements it (Src/Libraries), default gemspec",
  "fiddle" => "C extension; IronRuby implements it (Src/Libraries), default gemspec",
  "nkf" => "C extension; IronRuby implements it (Src/StdLib/ironruby/nkf.rb), default gemspec",
  "syslog" => "C extension; IronRuby implements it (Src/StdLib/ironruby/syslog.rb), default gemspec",
  "win32ole" => "C extension; IronRuby implements it over COM interop (Src/StdLib/ironruby/win32ole.rb)",
  # Part of IronRuby's standard library already - vendored in Src/StdLib/ruby/4.0
  # at the version CRuby bundles, except irb, which is the newer 1.18.0 - and
  # listed as default gems, because irb's upstream dependency tree ends in a C
  # extension (see gen-default-gemspecs.rb).  A gem copy would be a second copy.
  "irb" => "vendored in Src/StdLib/ruby/4.0 (1.18.0); a default gem here",
  "reline" => "vendored in Src/StdLib/ruby/4.0; a default gem here",
  "rdoc" => "vendored in Src/StdLib/ruby/4.0 as irb's dependency",
  "readline" => "IronRuby's readline is rb-readline, in Src/StdLib/ruby/site_ruby/1.9.1",
  # Cannot run here.
  "typeprof" => "a type checker, not part of the REPL; depends on rbs 3.x",
  "debug" => "C extension, and built on RubyVM::InstructionSequence and TracePoint internals",
}.freeze

# The bundled gems IronRuby ships at another version than CRuby's, fetched from
# rubygems.org and checked against the SHA-256 pinned here.
#
# irb's TypeCompletor is repl_type_completor, which is Ruby over rbs.  rbs's C
# extension is IronRuby's rbs_extension on librbs, and librbs is built from one
# rbs release (Util/build-librbs.sh) - 4.0.0.dev.5, not the 3.10.0 CRuby
# bundles.  repl_type_completor before 0.1.14 wants rbs < 4, so it moves too.
# It also asks for prism ~> 1.0, which IronRuby's RubyGems reads as >= 1.0
# (IRONRUBY_LIFTED_REQUIREMENTS in rubygems/defaults/ironruby.rb) until
# ruby/repl_type_completor#81 lifts it upstream.
FROM_RUBYGEMS = {
  "rbs" => {
    version: "4.0.0.dev.5",
    sha256: "273938e5a9c7f06b041be583588f2aa1976149bda2ce213e33e129c51534e174",
  },
  "repl_type_completor" => {
    version: "0.1.16",
    sha256: "f7048de5e758504c04a6c80d02ff4c7c4c6ff2785151928b830a6ac0ed416b5b",
  },
}.freeze

def usage!
  warn "usage: #{File.basename($0)} [--check] RUBY_SRC"
  warn "  RUBY_SRC is an unpacked CRuby source tarball (it has gems/bundled_gems and gems/*.gem)"
  exit 2
end

check_only = !ARGV.delete("--check").nil?
ruby_src = ARGV.shift || ENV["RUBY_SRC"] or usage!
list = File.join(ruby_src, "gems", "bundled_gems")
File.file?(list) or abort "#{list} does not exist - is #{ruby_src} an unpacked CRuby source tree?"

# gems/bundled_gems: "name version repository-url [revision]", # comments.
bundled = {}
File.foreach(list) do |line|
  line = line.sub(/#.*/, "").strip
  next if line.empty?
  name, version = line.split
  bundled[name] = version
end

unknown = bundled.keys - SHIPPED - SKIPPED.keys - FROM_RUBYGEMS.keys
unless unknown.empty?
  abort "#{list} lists #{unknown.join(", ")}, which is in none of SHIPPED, SKIPPED and FROM_RUBYGEMS here - decide"
end
missing = (SHIPPED + FROM_RUBYGEMS.keys) - bundled.keys
abort "#{missing.join(", ")} is shipped here, but #{list} does not list it" unless missing.empty?

gems = SHIPPED.map do |name|
  path = File.join(ruby_src, "gems", "#{name}-#{bundled[name]}.gem")
  File.file?(path) or abort "#{path} does not exist"
  [name, bundled[name], path]
end
# Fetched when they are installed, not for --check.
gems += FROM_RUBYGEMS.map {|name, pin| [name, pin[:version], nil] }

##
# Every file a shipped gem puts under lib/ that a load-path directory also has.

def shadows(gems)
  found = []
  gems.each do |name, version, _path|
    lib = File.join(GEM_HOME, "gems", "#{name}-#{version}", "lib")
    next unless Dir.exist?(lib)
    Dir.glob("**/*.rb", base: lib).each do |rel|
      LOAD_PATH_DIRS.each do |dir|
        file = File.join(dir, rel)
        found << [name, file] if File.file?(file)
      end
    end
  end
  found
end

if check_only
  problems = 0
  gems.each do |name, version, _path|
    spec = File.join(GEM_HOME, "specifications", "#{name}-#{version}.gemspec")
    next if File.file?(spec)
    puts "not installed: #{name}-#{version}"
    problems += 1
  end
  shadows(gems).each do |name, file|
    puts "shadows #{name}: #{file.delete_prefix("#{ROOT}/")}"
    problems += 1
  end
  puts "ok" if problems.zero?
  exit(problems.zero? ? 0 : 1)
end

# Never build an extension.  On IronRuby the installer already skips it
# (rubygems/defaults/ironruby.rb); under CRuby this script would otherwise
# compile racc's cparse into the tree.
module SkipExtensionBuild
  def build_extensions; end
end
Gem::Installer.prepend(SkipExtensionBuild)

##
# The binstub for +exe+ of gem +name+: RubyGems' own, with a portable shebang
# and a header saying where it comes from.  ir.sh puts Src/StdLib/bin on
# RUBYPATH, so `ir -S rake` runs this rather than whatever is on PATH.

def binstub(installer, name, exe)
  text = installer.app_script_text(exe)
  text = text.sub(/\A#!.*\n/, "")
  header = <<~RUBY
    #!/usr/bin/env ruby
    #
    # Generated by Util/install-bundled-gems.rb.  Do not edit by hand.
    #
    # The #{exe} executable of the #{name} gem, which IronRuby bundles in
    # Src/StdLib/ruby/gems/4.0.0 the way CRuby bundles it in its own gem home.
    # RubyGems writes this binstub into RbConfig's bindir on CRuby; ir.sh and
    # ir.cmd put this directory on RUBYPATH, so `ir -S #{exe}` runs it.  It
    # activates the newest installed #{name}, so one installed over this one wins.
  RUBY
  header + text.sub(/\A#\n# This file was generated by RubyGems\.\n#\n/, "#\n")
end

##
# Downloads FROM_RUBYGEMS[name]'s .gem into +dir+ and returns its path, or
# stops when its SHA-256 is not the pinned one.

def fetch_pinned(name, dir)
  pin = FROM_RUBYGEMS.fetch(name)
  file = "#{name}-#{pin[:version]}.gem"
  data = Gem::RemoteFetcher.fetcher.fetch_path("https://rubygems.org/downloads/#{file}")
  actual = Digest::SHA256.hexdigest(data)
  abort "#{file} is sha256 #{actual}, not the pinned #{pin[:sha256]}" unless actual == pin[:sha256]
  path = File.join(dir, file)
  File.binwrite(path, data)
  path
end

tmp_bin = Dir.mktmpdir("ir-bundled-bin")
tmp_gems = Dir.mktmpdir("ir-bundled-gems")
begin
  gems.each do |name, version, path|
    path ||= fetch_pinned(name, tmp_gems)
    # Drop any other version this script installed before.
    Dir.glob(File.join(GEM_HOME, "specifications", "#{name}-*.gemspec")).each do |old|
      next unless File.basename(old, ".gemspec") =~ /\A#{Regexp.escape(name)}-\d[^-]*\z/
      next if File.basename(old) == "#{name}-#{version}.gemspec"
      FileUtils.rm_f old
      FileUtils.rm_rf File.join(GEM_HOME, "gems", File.basename(old, ".gemspec"))
    end
    FileUtils.rm_rf File.join(GEM_HOME, "gems", "#{name}-#{version}")

    installer = Gem::Installer.at(path,
                                  install_dir: GEM_HOME,
                                  bin_dir: tmp_bin,
                                  ignore_dependencies: true,
                                  force: true,
                                  wrappers: true,
                                  env_shebang: true,
                                  document: [])
    spec = installer.install

    spec.executables.each do |exe|
      target = File.join(BIN_DIR, exe)
      File.binwrite(target, binstub(installer, name, exe))
      File.chmod(0o755, target)
    end

    size = Dir.glob("**/*", base: spec.full_gem_path).sum {|f| File.size?(File.join(spec.full_gem_path, f)) || 0 }
    puts format("%-19s %-11s %4d files %7.1f KB%s", name, version, spec.files.size, size / 1024.0,
                spec.executables.empty? ? "" : "  bin: #{spec.executables.join(", ")}")
  end
ensure
  FileUtils.rm_rf tmp_bin
  FileUtils.rm_rf tmp_gems
end

# RubyGems keeps a copy of every .gem it installs; these are rebuilt from the
# CRuby tarball, so the copy is only weight.  Nothing builds extensions, and no
# shipped gem has a RubyGems plugin, but clear the directories either would
# have used so that the tree holds exactly what is committed.
%w[cache extensions build_info plugins doc].each do |dir|
  FileUtils.rm_rf File.join(GEM_HOME, dir)
end

left = shadows(gems)
unless left.empty?
  warn ""
  warn "These files on the load path shadow a shipped gem - RubyGems loads them"
  warn "instead of activating the gem.  Delete them:"
  left.each {|name, file| warn "  #{name}: #{file.delete_prefix("#{ROOT}/")}" }
  exit 1
end
