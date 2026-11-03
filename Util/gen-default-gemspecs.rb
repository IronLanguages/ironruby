# frozen_string_literal: true
#
# Regenerates the default gemspecs IronRuby ships for the libraries it
# provides itself:
#
#     Src/StdLib/ruby/gems/4.0.0/specifications/default/<name>-<version>.gemspec
#
# Run it with the built interpreter, from the root of the source tree:
#
#     ./ir.sh Util/gen-default-gemspecs.rb
#
# Why this exists
# ---------------
# RubyGems only knows a library is already present when a *gemspec* says so.
# Without these, `gem install activesupport` resolves `bigdecimal` and `json`
# against rubygems.org, downloads the C sources and tries to compile an
# extension - for libraries IronRuby already implements in C#.  CRuby avoids
# that by gemifying its standard library; JRuby does the same for what it
# implements natively.  This is IronRuby's copy of that list.
#
# The versions below are the versions IronRuby *provides*, not the versions
# CRuby happens to ship:
#
#   * a library vendored verbatim from the CRuby release whose standard
#     library lives in Src/StdLib/ruby/4.0 carries that release's version -
#     it is literally that code;
#   * a library IronRuby implements in C# carries the version its own
#     VERSION constant reports.  The generator re-reads those constants and
#     refuses to write a gemspec that disagrees with the running interpreter,
#     so the table cannot drift away from the implementation.
#
# A library IronRuby does *not* implement must not be listed here.  Leaving it
# out means RubyGems uses the real gem, which is the right answer for anything
# pure Ruby.  CRuby 4.0's pure-Ruby bundled gems (logger, csv, base64, racc,
# rake, rexml, minitest, ...) are installed as real gems in the same gem home by
# Util/install-bundled-gems.rb; this file is only for default gems.

require "fileutils"

ROOT = File.expand_path("..", __dir__)
STDLIB = File.join(ROOT, "Src", "StdLib")

# Search order matches ir.sh's -X:StdLib: IronRuby's own library first.
ROOTS = [
  File.join(STDLIB, "ironruby"),
  File.join(STDLIB, "ruby", "4.0"),
].freeze

OUT_DIR = File.join(STDLIB, "ruby", "gems", "4.0.0", "specifications", "default")

# name => [version, summary, entries, options]
#
# An entry is a require name.  "json" means the file json.rb; a trailing "/"
# means the whole subtree, "json/" => json/**/*.rb.  An entry the interpreter
# answers without a file on disk (ruby2_keywords is built into the loader)
# is written as [name, :builtin].
#
# :deps lists runtime dependencies as [name, requirement...] - the ones the real
# gem declares, so that Bundler still installs what the vendored Ruby requires
# (websocket-driver's websocket-extensions, say) from rubygems.org.
#
# :check is the constant whose value must equal the version.  :require is what
# to require before reading it, when that is not the gem's own name.
#
# :pinned marks a library where the version IronRuby provides is the only one
# this interpreter can have, so that no release from rubygems.org is offered as
# an alternative.  Two things earn it:
#
#   * the real gem is a C extension - bigdecimal, json, psych, sqlite3,
#     nokogiri, cgi, erb - and nothing here compiles C;
#   * the library is part of IronRuby's standard library and its upstream
#     dependency tree cannot be satisfied here anyway: irb depends on rdoc,
#     rdoc 8 on rbs, and rbs is a C extension, so `gem "railties"` reaches a
#     compiler through irb - a library `ir -S irb` has been running all along.
#
# Those gemspecs carry metadata["ironruby_pinned"], which
# Gem.ironruby_pinned_gems reads and gem_compat.rb hands to Bundler.  A vendored
# pure-Ruby library is *not* pinned by default: installing a newer release of it
# over the bundled copy is fine, and is what a Gemfile asking for one should
# get.
GEMS = {
  # --- implemented in C# (Src/Libraries) -----------------------------------
  # These are the entries that matter: without them RubyGems compiles C.
  "bigdecimal" => ["4.0.1", "Arbitrary-precision decimal arithmetic",
                   ["bigdecimal", "bigdecimal/"], check: "BigDecimal::VERSION", pinned: true],
  "date" => ["3.3.4", "Date and DateTime",
             ["date", "date/"], check: "Date::VERSION", pinned: true],
  "digest" => ["3.2.1", "Message digest libraries",
               ["digest", "digest/"], check: "Digest::VERSION", pinned: true],
  "etc" => ["1.4.6", "Access to information from the passwd and group files",
            ["etc"], check: "Etc::VERSION", pinned: true],
  "fcntl" => ["1.3.0", "Constants for fcntl(2) and open(2)",
              ["fcntl"], check: "Fcntl::VERSION", pinned: true],
  "ffi" => ["1.17.4", "The ffi gem's API, on Fiddle",
            ["ffi", "ffi/"], check: "FFI::VERSION", pinned: true],
  "fiddle" => ["1.1.8", "Calling shared-library functions, on NativeLibrary and calli",
               ["fiddle", "fiddle/"], check: "Fiddle::VERSION", pinned: true],
  "io-console" => ["0.8.2", "Console size and raw mode",
                   ["io/console", "io/console/"], require: "io/console",
                   check: "IO::ConsoleMode::VERSION", pinned: true],
  "io-nonblock" => ["0.3.2", "IO#nonblock", ["io/nonblock"], require: "io/nonblock", pinned: true],
  "io-wait" => ["0.4.0", "IO#wait and friends", ["io/wait"], require: "io/wait", pinned: true],
  "json" => ["2.18.0", "JSON parsing and generation",
             ["json", "json/"], check: "JSON::VERSION", pinned: true],
  "openssl" => ["3.2.0", "OpenSSL bindings, on .NET's cryptography stack",
                ["openssl", "openssl/"], check: "OpenSSL::VERSION", pinned: true],
  "psych" => ["5.3.1", "YAML parser and emitter",
              ["psych", "psych/"], check: "Psych::VERSION", pinned: true],
  "stringio" => ["3.2.0", "IO on strings",
                 ["stringio"], check: "StringIO::VERSION", pinned: true],
  "strscan" => ["3.1.6", "Lexical scanning of strings",
                ["strscan"], check: "StringScanner::Version", pinned: true],
  "zlib" => ["3.2.3", "Deflate/inflate, on System.IO.Compression",
             ["zlib"], check: "Zlib::VERSION", pinned: true],
  "prism" => ["2.0.0", "The Prism Ruby parser - IronRuby's own front end",
              ["prism", "prism/"], check: "Prism::VERSION", pinned: true],
  "sqlite3" => ["2.9.6", "SQLite3, on the native library Microsoft.Data.Sqlite carries",
                ["sqlite3", "sqlite3/"], check: "SQLite3::VERSION", pinned: true],
  # Not a default gem anywhere else: nokogiri is a C extension over libxml2 and
  # gumbo, and neither compiles here.  It is listed because it is a hard
  # dependency of rails-html-sanitizer -> loofah -> actionview and of
  # rails-dom-testing -> actionpack, so without a gemspec saying it is present
  # `gem install actionview` stops at a C compiler.  The version is the nokogiri
  # API level implemented, not a nokogiri release.
  "nokogiri" => ["1.18.0", "HTML5, HTML4 and XML parsing, on AngleSharp",
                 ["nokogiri", "nokogiri/"], check: "Nokogiri::VERSION", pinned: true],
  # websocket-driver is pure Ruby apart from WebSocket::Mask.mask, the frame
  # masking loop, which is C upstream and C# here (Src/Libraries/WebSocketDriver).
  # The gem's Ruby is vendored unchanged.  It is a dependency of faye-websocket
  # and ActionCable.  Upstream also depends on base64, which is left out: IronRuby
  # ships base64.rb itself, and a dependency no gem satisfies would make
  # `gem "websocket-driver"` fail to activate outside a bundle.
  "websocket-driver" => ["0.8.2", "WebSocket protocol handler; WebSocket::Mask in C#",
                         ["websocket_mask", "websocket/driver", "websocket/driver/", "websocket/http",
                          "websocket/http/", "websocket/mask", "websocket/websocket_mask"],
                         pinned: true,
                         deps: [["websocket-extensions", ">= 0.1.0"]]],
  # msgpack's C extension (Buffer, Packer, Unpacker, Factory, the error
  # classes) is C# here (Src/Libraries/MessagePack); lib/msgpack is vendored
  # unchanged and requires "msgpack/msgpack", which is IronRuby's loader.
  "msgpack" => ["1.8.5", "MessagePack serialization; the C extension ported to C#",
                ["msgpack", "msgpack/"], check: "MessagePack::VERSION", pinned: true],
  # oj: the :strict, :null, :compat and :rails modes, Oj.mimic_JSON and
  # Oj::Rails are C# (Src/Libraries/Oj); :object, :custom, :wab, Oj::Doc,
  # Oj::StringWriter/StreamWriter, Oj::Parser and Saj/Scp raise
  # NotImplementedError.  lib/oj is vendored unchanged.
  "oj" => ["3.17.6", "Oj's :strict, :null, :compat and :rails modes and mimic_JSON, in C#",
           ["oj", "oj/"], check: "Oj::VERSION", pinned: true],
  # bcrypt: bcrypt_ext's BCrypt::Engine.__bc_crypt and __bc_salt (Openwall's
  # crypt_blowfish) are C# (Src/Libraries/BCrypt); lib/bcrypt is vendored
  # unchanged.  Devise and has_secure_password depend on it.
  "bcrypt" => ["3.1.22", "OpenBSD bcrypt password hashing; bcrypt_ext in C#",
               ["bcrypt", "bcrypt/", "bcrypt_ext"], pinned: true],
  # nio4r and puma: both C extensions (over libev, and a Ragel HTTP parser plus OpenSSL
  # glue), so neither installs here.  nio4r's Ruby half is vendored with its native half
  # in Src/Libraries/Nio4r; puma's Ruby half is vendored unchanged and its HTTP parser is
  # Src/Libraries/Puma, as is its MiniSSL engine (TLS, on SslStream).  puma depends on
  # nio4r, and says so here too.
  "nio4r" => ["2.7.5", "NIO::Selector, Monitor and ByteBuffer, on epoll, poll(2) or Socket.Select",
              ["nio", "nio/", "nio4r", "nio4r_ext"], require: "nio", check: "NIO::VERSION", pinned: true],
  "puma" => ["8.0.2", "Puma, with its HTTP parser and TLS engine in C#; single mode only",
             ["puma", "puma/", "rack/handler/puma"], require: "puma/const",
             check: "Puma::Const::PUMA_VERSION", pinned: true, bin: ["puma", "pumactl"],
             deps: [["nio4r", "~> 2.0"]]],
  # Ruby 4.0 removed the CGI class from the standard library and kept only the
  # escaping half, cgi/escape - which is a C extension there and is vendored in
  # Ruby here.  The `cgi` gem that brings the class back is that same C
  # extension plus pure Ruby, so it cannot install here, and a surprising number
  # of Gemfiles carry `gem "cgi"` as their Ruby 3.5/4.0 workaround (ruby-bench's
  # rails, rack and grape benchmarks all do).  What this gemspec promises is
  # exactly what IronRuby ships: CGI.escape/unescape and friends.  A program
  # that wants the full CGI class - cgi/core, cgi/cookie, cgi/session - does not
  # get it, the same as on a Ruby 4.0 without the gem.
  "cgi" => ["0.5.0", "CGI.escape and friends - Ruby 4.0's cgi/escape, not the full CGI class",
            ["cgi", "cgi/escape", "cgi/util"], pinned: true],
  # Bundled gems on CRuby 4.0 rather than default ones, but C extensions whose
  # lib/ only loads the .so, so they are not among the bundled gems
  # Util/install-bundled-gems.rb installs.  IronRuby implements both, and says
  # so here, so that a Gemfile naming one resolves to that instead of to a
  # compiler.
  "nkf" => ["0.2.0", "Network Kanji Filter, on String#encode",
            ["nkf", "kconv"], pinned: true],
  "syslog" => ["0.3.0", "Syslog, implemented in Src/Libraries/Syslog",
               ["syslog", "syslog/"], check: "Syslog::VERSION", pinned: true],

  # --- vendored from the CRuby release in Src/StdLib/ruby/4.0 --------------
  "bundler" => ["4.0.16", "The best way to manage a Ruby application's gems",
                ["bundler", "bundler/"], require: false, bin: ["bundle", "bundler"]],
  "delegate" => ["0.6.1", "Delegation of method calls", ["delegate"]],
  "did_you_mean" => ["2.0.0", "Did you mean? experience",
                     ["did_you_mean", "did_you_mean/"]],
  "english" => ["0.8.1", "Readable aliases for the special variables", ["English"]],
  # erb is here rather than under the vendored heading because ERB::Escape -
  # ERB::Util.html_escape - is a C extension upstream, so no release of the erb
  # gem installs here.  Everything else about it is the vendored Ruby.
  "erb" => ["6.0.1.1", "An easy-to-use but powerful templating system",
            ["erb", "erb/"], check: "ERB::VERSION", pinned: true],
  # IronRuby answers the error_highlight API, but ErrorHighlight.spot always
  # returns nil - see Src/StdLib/ironruby/error_highlight.rb for why.
  "error_highlight" => ["0.7.2", "The error_highlight API; spot always answers nil here",
                        ["error_highlight", "error_highlight/"]],
  "fileutils" => ["1.8.0", "Several file utility methods for copying, moving, removing, etc.",
                  ["fileutils"], check: "FileUtils::VERSION"],
  "find" => ["0.2.0", "Traverse a file tree", ["find"]],
  "forwardable" => ["1.4.0", "Provides delegation of specified methods to a designated object",
                    ["forwardable", "forwardable/"]],
  "ipaddr" => ["1.2.8", "A class to manipulate an IP address", ["ipaddr"]],
  # railties depends on irb, and irb on reline.  Without these two, installing
  # railties resolves irb against rubygems.org, which drags in rdoc 8 and then
  # rbs - a C extension - for a library IronRuby already ships and already runs
  # as `ir -S irb`.  irb is 1.18.0 from rubygems.org, newer than the 1.16.0
  # CRuby 4.0.9 bundles: 1.18 has the startup banner.
  "irb" => ["1.18.0", "Interactive Ruby", ["irb", "irb/"], require: false, bin: ["irb"],
            pinned: true],
  "reline" => ["0.6.3", "GNU Readline and Editline, in pure Ruby",
               ["reline", "reline/"], require: false, pinned: true],
  "net-http" => ["0.9.1", "HTTP client api for Ruby", ["net/http", "net/https", "net/http/"],
                 require: "net/http"],
  "net-protocol" => ["0.2.2", "The abstract interface for net-* client",
                     ["net/protocol"], require: "net/protocol"],
  "open3" => ["0.2.1", "Popen, but with stderr, too", ["open3"]],
  "open-uri" => ["0.5.0", "An easy-to-use wrapper for Net::HTTP and Net::FTP",
                 ["open-uri"], require: "open-uri"],
  "optparse" => ["0.8.1", "OptionParser is a class for command-line option analysis",
                 ["optparse", "optparse/", "optionparser"]],
  "pp" => ["0.6.3", "Pretty-prints Ruby objects", ["pp"]],
  "prettyprint" => ["0.2.0", "Implements a pretty printing algorithm for readable structure",
                    ["prettyprint"]],
  "resolv" => ["0.7.0", "Thread-aware DNS resolver library", ["resolv"]],
  "ruby2_keywords" => ["0.0.5", "Shim library for Module#ruby2_keywords",
                       [["ruby2_keywords", :builtin]], require: false],
  "securerandom" => ["0.4.1", "Interface for secure random number generator", ["securerandom"]],
  "shellwords" => ["0.2.2", "Manipulates strings with word parsing rules of UNIX Bourne shell",
                   ["shellwords"]],
  "singleton" => ["0.3.0", "The Singleton module implements the Singleton pattern",
                  ["singleton"]],
  "syntax_suggest" => ["2.0.3", "Find syntax errors in your source in a snap",
                       ["syntax_suggest", "syntax_suggest/"], require: false],
  "tempfile" => ["0.3.1", "A utility class for managing temporary files",
                 ["tempfile"], check: "Tempfile::VERSION"],
  "time" => ["0.4.2", "Extends the Time class with methods for parsing and conversion",
             ["time"]],
  "timeout" => ["0.6.0", "Auto-terminate potentially long-running operations",
                ["timeout"], check: "Timeout::VERSION"],
  "tmpdir" => ["0.3.1", "Extends the Dir class to manage the OS temporary file path",
               ["tmpdir"]],
  "tsort" => ["0.2.0", "Topological sorting using Tarjan's algorithm", ["tsort"]],
  "un" => ["0.3.0", "Utilities to replace common UNIX commands", ["un"]],
  "uri" => ["1.1.1", "URI is a module providing classes to handle Uniform Resource Identifiers",
            ["uri", "uri/"], check: "URI::VERSION"],
  "weakref" => ["0.1.4", "Allows a referenced object to be garbage-collected", ["weakref"]],
  "yaml" => ["0.4.0", "YAML Ain't Markup Language", ["yaml", "yaml/"]],
}.freeze

def entry_files(entry)
  name, kind = entry.is_a?(Array) ? entry : [entry, nil]
  return ["lib/#{name}.rb"] if kind == :builtin

  found = []
  ROOTS.each do |root|
    if name.end_with?("/")
      Dir.glob(File.join(root, name, "**", "*.rb")).sort.each do |path|
        found << "lib/" + path[(root.length + 1)..-1]
      end
    else
      path = File.join(root, "#{name}.rb")
      found << "lib/#{name}.rb" if File.file?(path)
    end
  end
  found
end

# A default gem's executables live in <gem_dir>/<bindir>, which on CRuby is
# lib/ruby/gems/<api>/gems/<name>-<version>/exe.  Gem.bin_path("bundler",
# "bundle") is what `rails new` runs to install an application's gems, so that
# directory has to exist and hold a runnable file.
#
# IronRuby keeps the executables it ships in one place instead - Src/StdLib/bin,
# the directory ir.sh puts on RUBYPATH, so that `ir -S bundle` finds them - so
# what goes in the gem's exe/ is a one-line loader of the real binstub.  Both
# are written here, next to the gemspec that names them, so a version bump
# cannot leave a stale directory behind.
BIN_DIR = "exe"
GEMS_DIR = File.join(STDLIB, "ruby", "gems", "4.0.0", "gems")
SHARED_BIN_DIR = File.join(STDLIB, "bin")

def write_executables(name, version, executables)
  dir = File.join(GEMS_DIR, "#{name}-#{version}", BIN_DIR)
  FileUtils.mkdir_p dir

  executables.each do |exe|
    target = File.join(SHARED_BIN_DIR, exe)
    warn "gen-default-gemspecs: #{target} does not exist" unless File.file?(target)

    path = File.join(dir, exe)
    File.write(path, <<~RUBY)
      #!/usr/bin/env ruby
      # frozen_string_literal: true
      #
      # Generated by Util/gen-default-gemspecs.rb.  Do not edit by hand.
      #
      # Gem.bin_path("#{name}", "#{exe}") answers this file, because that is where
      # RubyGems looks for a default gem's executables.  The binstub itself is
      # Src/StdLib/bin/#{exe}, the one `ir -S #{exe}` runs, and this loads it so
      # that there is only ever one copy to keep working.
      load File.expand_path("../../../../../../bin/#{exe}", __dir__)
    RUBY
    File.chmod(0o755, path)
  end
end

def gemspec_source(name, version, summary, files, executables, pinned, deps = [])
  lines = []
  lines << "# -*- encoding: utf-8 -*-"
  lines << "# stub: #{name} #{version} ruby lib"
  lines << "#"
  lines << "# Generated by Util/gen-default-gemspecs.rb.  Do not edit by hand."
  lines << ""
  lines << "Gem::Specification.new do |s|"
  lines << "  s.name = #{name.dump}.freeze"
  lines << "  s.version = #{version.dump}"
  lines << ""
  lines << "  s.required_rubygems_version = Gem::Requirement.new(\">= 0\".freeze)"
  lines << "  s.require_paths = [\"lib\".freeze]"
  lines << "  s.authors = [\"IronRuby\".freeze]"
  lines << "  s.description = #{"#{summary}.  Provided by IronRuby itself.".dump}.freeze"
  lines << "  s.summary = #{summary.dump}.freeze"
  lines << "  s.homepage = \"https://github.com/IronLanguages/ironruby\".freeze"
  lines << "  s.licenses = [\"Ruby\".freeze, \"BSD-2-Clause\".freeze]"
  if pinned
    lines << "  s.metadata = { \"ironruby_pinned\".freeze => \"true\".freeze }"
  end
  unless executables.empty?
    lines << "  s.bindir = #{BIN_DIR.dump}.freeze"
    lines << "  s.executables = [#{executables.map {|e| "#{e.dump}.freeze" }.join(", ")}]"
  end
  lines << "  s.files = ["
  files.each {|f| lines << "    #{f.dump}.freeze," }
  lines << "  ]"
  unless deps.empty?
    lines << ""
    deps.each do |dep, *reqs|
      reqs = [">= 0"] if reqs.empty?
      lines << "  s.add_runtime_dependency(#{dep.dump}.freeze, [#{reqs.map {|r| "#{r.dump}.freeze" }.join(", ")}])"
    end
  end
  lines << "end"
  lines.join("\n") + "\n"
end

# Cross-check the table against the running interpreter.
mismatches = []
GEMS.each do |name, (version, _summary, _entries, opts)|
  opts ||= {}
  const = opts[:check]
  next unless const

  feature = opts.key?(:require) ? opts[:require] : name
  begin
    require feature if feature
    actual = const.split("::").inject(Object) {|mod, part| mod.const_get(part) }.to_s
  rescue StandardError, LoadError, NameError => e
    mismatches << "#{name}: cannot read #{const} (#{e.class}: #{e.message})"
    next
  end
  mismatches << "#{name}: #{const} is #{actual}, table says #{version}" unless actual == version
end

unless mismatches.empty?
  warn "gen-default-gemspecs: the table disagrees with this interpreter:"
  mismatches.each {|m| warn "  #{m}" }
  exit 1
end

FileUtils.mkdir_p OUT_DIR
Dir.glob(File.join(OUT_DIR, "*.gemspec")).each {|f| File.delete(f) }

GEMS.each do |name, (version, summary, entries, opts)|
  opts ||= {}
  executables = Array(opts[:bin])
  files = entries.flat_map {|e| entry_files(e) }.uniq
  if files.empty?
    warn "gen-default-gemspecs: #{name} matched no files - skipping"
    next
  end

  write_executables(name, version, executables) unless executables.empty?

  path = File.join(OUT_DIR, "#{name}-#{version}.gemspec")
  File.write(path, gemspec_source(name, version, summary, files, executables, opts[:pinned],
                                   Array(opts[:deps])))
  puts "#{name}-#{version} (#{files.length} files)"
end
