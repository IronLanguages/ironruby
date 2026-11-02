# frozen-string-literal: false
# (as in MRI's rbconfig.rb: CONFIG hands out mutable strings even under
# --enable-frozen-string-literal, and RbConfig.expand edits them in place)
#
# ****************************************************************************
#
# Copyright (c) Microsoft Corporation.
#
# This source code is subject to terms and conditions of the Apache License, Version 2.0. A
# copy of the license can be found in the License.html file at the root of this distribution. If
# you cannot locate the  Apache License, Version 2.0, please send an email to
# ironruby@microsoft.com. By using this source code in any fashion, you are agreeing to be bound
# by the terms of the Apache License, Version 2.0.
#
# You must not remove this notice, or any other, from this software.
#
#
# ****************************************************************************

module RbConfig
  # No `::Config = self` here. It was Ruby 1.8's spelling of RbConfig and MRI dropped it in
  # 1.9.3; keeping it squats on a top-level name that applications use constantly - `class
  # Config` in a gem's own code then fails with "Config is not a class", which is how Hashie's
  # documented example breaks. Anything still asking for ::Config is asking for Ruby 1.8.

  # The directory holding ironruby/, ruby/<version>/ and ruby/site_ruby/: <prefix>/Lib
  # in an installed IronRuby, Src/StdLib in a source tree.
  libdir = File.expand_path("..", File.dirname(__FILE__))

  # Like MRI, TOPDIR is the installation prefix, or nil when running from a source tree
  # (MRI: "These directories have no meanings before the installation").
  TOPDIR = File.basename(libdir) == "Lib" ? File.dirname(libdir) : nil

  windows = RUBY_PLATFORM =~ /mswin|mingw/

  CONFIG = {}
  CONFIG["MAJOR"], CONFIG["MINOR"], CONFIG["TEENY"] = RUBY_VERSION.split('.')
  CONFIG["PATCHLEVEL"] = RUBY_PATCHLEVEL.to_s
  CONFIG["RUBY_PROGRAM_VERSION"] = RUBY_VERSION.dup
  CONFIG["RUBY_API_VERSION"] = "#{CONFIG["MAJOR"]}.#{CONFIG["MINOR"]}"
  CONFIG["EXEEXT"] = windows ? ".exe" : ""
  # Libraries spawn RbConfig.ruby to run Ruby scripts, so this has to be a file in bindir:
  # the build puts an ironruby apphost beside ir (Ruby.Console.csproj). And Bundler and
  # Rails write it into binstubs as `#!/usr/bin/env ironruby`, which the ironruby launcher
  # answers - a #! line with "ruby" in it, as JRuby's "jruby" is. (RubyGems' own binstubs
  # name the launcher by its path: rubygems/defaults/ironruby.rb.)
  CONFIG["ruby_install_name"] = "ironruby"
  CONFIG["RUBY_INSTALL_NAME"] = "ironruby"
  CONFIG["RUBY_SO_NAME"] = "IronRuby"
  CONFIG["PATH_SEPARATOR"] = File::PATH_SEPARATOR.dup

  # There is no C-level libruby; IronRuby.dll is the runtime an embedding host links to.
  CONFIG["ENABLE_SHARED"] = "no"
  CONFIG["LIBRUBY"] = "IronRuby.dll"

  # Set up paths
  # bindir is where the running ir executable lives, so RbConfig.ruby names it.
  bindir = File.expand_path(System::IO::Path.get_directory_name(System::Reflection::Assembly.get_entry_assembly.location).to_s) rescue nil
  prefix = TOPDIR || File.dirname(libdir)

  CONFIG["bindir"] = bindir || "#{prefix}/bin"
  CONFIG["libdir"] = libdir
  CONFIG["libdirname"] = "libdir"
  CONFIG["LIBPATHENV"] = windows ? "PATH" : (RUBY_PLATFORM =~ /darwin/ ? "DYLD_LIBRARY_PATH" : "LD_LIBRARY_PATH")
  CONFIG["prefix"] = prefix.dup
  CONFIG["exec_prefix"] = prefix.dup

  # cpu, os
  cpu_and_os = RUBY_PLATFORM.split('-')
  abort("Could not parse RUBY_PLATFORM") if cpu_and_os.size != 2
  CONFIG["host_cpu"] = CONFIG["target_cpu"] = cpu_and_os[0]
  CONFIG["host_os"] = CONFIG["target_os"] =  cpu_and_os[1]

  # architecture
  clr_version = "#{System::Environment.Version.Major}.#{System::Environment.Version.Minor}"
  CONFIG["arch"] = arch = "universal-dotnet#{clr_version}" # Not strictly true. For example, while running a .NET 2.0 version of IronRuby on .NET 4
  CONFIG["sitearch"] = arch.dup

  # std lib
  # Still 1.9.1: the version names the site directory, and site_ruby/1.9.1 is where
  # IronRuby's readline (rb-readline) lives. The standard library itself is ruby/4.0
  # (rubylibdir below) and gems go under gems/4.0.0 (rubygems/defaults/ironruby.rb).
  CONFIG["ruby_version"] = stdlib_version = "1.9.1"
  CONFIG["RUBY_BASE_NAME"] = ruby_base_name = "ruby"              # directory name
  CONFIG["datadir"] = datadir = "#{prefix}/share"
  CONFIG["rubylibprefix"] = rubylibprefix = "#{libdir}/#{ruby_base_name}"
  CONFIG["rubylibdir"] = "#{rubylibprefix}/4.0"
  # What MRI keeps in its arch dir as C extensions IronRuby implements in ironruby/.
  CONFIG["rubyarchdir"] = CONFIG["archdir"] = "#{libdir}/ironruby"

  # ri
  CONFIG["RI_BASE_NAME"] = ri_base_name = "ri"
  CONFIG["ridir"] = "#{datadir}/#{ri_base_name}"

  # site and vendor dirs
  CONFIG["sitedir"] = sitedir = "#{libdir}/#{ruby_base_name}/site_ruby"
  CONFIG["sitelibdir"] = sitelibdir = "#{sitedir}/#{stdlib_version}"
  CONFIG["sitearchdir"] = sitelibdir.dup
  CONFIG["vendordir"] = vendordir = "#{rubylibprefix}/vendor_ruby"
  CONFIG["vendorlibdir"] = vendorlibdir = "#{vendordir}/#{stdlib_version}"
  CONFIG["vendorarchdir"] = vendorlibdir.dup

  # The Unicode character data version this distribution ships: the bundled
  # Ruby 4.0 standard library (unicode_normalize tables etc.) is generated
  # from Unicode 17.0.0, as in CRuby 4.0.
  CONFIG["UNICODE_VERSION"] = "17.0.0"
  CONFIG["UNICODE_EMOJI_VERSION"] = "17.0"

  # Binutils, resolved via PATH as in an MRI mingw/gcc build. IronRuby has no
  # C-extension build chain of its own; these name the platform's tools.
  CONFIG["AR"] = "ar"
  CONFIG["STRIP"] = "strip"

  # IronRuby is not built by an autoconf configure script; mkmf only ever
  # shellsplits this, so an empty argument list is the honest answer.
  CONFIG["configure_args"] = ""
  CONFIG["CROSS_COMPILING"] = "no"

  CONFIG["SHELL"] = windows ? (ENV["COMSPEC"] || "cmd.exe").dup : "/bin/sh"
  CONFIG["NULLCMD"] = windows ? "rem" : ":"
  CONFIG["DLEXT"] = "so"
  CONFIG["DLEXT2"] = "dll"
  # The standard extensions (etc, socket, zlib, ...) are built in rather than loaded from
  # shared objects, as in an MRI built with --with-static-linked-ext.
  CONFIG["EXTSTATIC"] = "static"

  # Where a distribution keeps the headers a C extension compiles against.
  # IronRuby has none: an extension here is a .NET assembly.  mkmf still refuses
  # to load unless rubyhdrdir names a directory holding ruby/ruby.h - it aborts
  # before defining MakeMakefile at all - so these name the header this tree
  # ships, which is a single #error saying so (Src/StdLib/include/ruby/ruby.h).
  hdrdir = "#{libdir}/include"
  CONFIG["rubyhdrdir"] = hdrdir
  CONFIG["rubyarchhdrdir"] = "#{hdrdir}/#{arch}"
  CONFIG["sitehdrdir"] = "#{hdrdir}/site_ruby"
  CONFIG["vendorhdrdir"] = "#{hdrdir}/vendor_ruby"
  CONFIG["topdir"] = File.dirname(__FILE__)
  CONFIG["build_os"] = CONFIG["host_os"].dup

  # The C toolchain mkmf drives.  There is none: every one of these is what MRI
  # fills in from its own build, and IronRuby was not built by a C compiler.
  # They are here because mkmf reads them while it loads and would otherwise
  # fail on nil; empty is the truthful value, and it makes every compile mkmf
  # attempts fail rather than appear to succeed.
  %w[
    ADDITIONAL_DLDFLAGS ARCH_FLAG ASSEMBLE_C ASSEMBLE_CXX BUILD_FILE_SEPARATOR
    CC_WRAPPER CFLAGS CLEANFILES COMMON_HEADERS COMMON_LIBS COMMON_MACROS
    COMPILE_C COMPILE_CXX COMPILE_RULES COUTFLAG CPPFLAGS CPPOUTFILE CSRCFLAG
    CXXFLAGS CXX_EXT DISTCLEANDIRS DISTCLEANFILES DLDFLAGS DLDLIBS EXPORT_PREFIX
    GCC LDFLAGS LIBARG LIBPATHFLAG LIBRUBYARG LIBRUBYARG_SHARED
    LIBRUBYARG_STATIC LIBS LINK_SO MAIN_DOES_NOTHING OUTFLAG RPATHFLAG
    RULE_SUBST TRY_LINK TRY_LINK_CXX UNIVERSAL_INTS warnflags
  ].each { |key| CONFIG[key] = "" }

  # Naming conventions rather than tools, so these have an answer even here.
  CONFIG["OBJEXT"] = windows ? "obj" : "o"
  CONFIG["LIBEXT"] = windows ? "lib" : "a"
  CONFIG["ASMEXT"] = "S"
  # There is no static libruby either; it must differ from LIBRUBY, which is how
  # mkmf decides whether the runtime is linked in.
  CONFIG["LIBRUBY_A"] = ""

  # In MRI this is the configuration as it went into the Makefile, with the
  # $(var) references still unexpanded; CONFIG is the expanded copy. IronRuby
  # never writes a Makefile, so nothing here holds a reference to expand and
  # the two hashes have the same contents - but they must not be the same
  # object, and mkmf mutates the values it is handed.
  MAKEFILE_CONFIG = {}
  CONFIG.each { |k, v| MAKEFILE_CONFIG[k] = v.dup }

  def RbConfig::expand(val, config = CONFIG)
    newval = val.gsub(/\$\$|\$\(([^()]+)\)|\$\{([^{}]+)\}/) do
      var = $&
      if !(v = $1 || $2)
       '$'
      elsif key = config[v = v[/\A[^:]+(?=(?::(.*?)=(.*))?\z)/]]
        pat, sub = $1, $2
        config[v] = false
        config[v] = RbConfig::expand(key, config)
        key = key.gsub(/#{Regexp.quote(pat)}(?=\s|\z)/n) {sub} if pat
        key
      else
        var
      end
    end
    val.replace(newval) unless newval == val
    val
  end

  # returns the absolute pathname of the ruby command.
  def RbConfig.ruby
    File.join(RbConfig::CONFIG["bindir"], RbConfig::CONFIG["ruby_install_name"] + RbConfig::CONFIG["EXEEXT"])
  end
end
# Non-nil if configured for cross compiling.
CROSS_COMPILING = nil unless defined? CROSS_COMPILING
