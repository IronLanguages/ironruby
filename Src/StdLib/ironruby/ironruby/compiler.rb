# irubyc -- IronRuby's ahead-of-time compiler, the analogue of JRuby's jrubyc.
#
# WHAT IT DOES, PLAINLY
#
#   irubyc packages Ruby sources into a self-contained .NET application: a
#   generated C# host is compiled (by the .NET SDK) into an assembly that
#   carries the Ruby files as *embedded managed resources*, next to IronRuby's
#   own assemblies, libprism and a copy of the standard library.  The result
#   runs without the source tree -- no .rb file is written to the output.
#
#   It does NOT emit IL for the Ruby code.  The Ruby is still parsed and
#   compiled by IronRuby at startup, exactly as `ir app.rb` would.  Emitting IL
#   to disk needs LambdaExpression.CompileToMethod, which .NET Core removed and
#   .NET 10 still does not have (PersistedAssemblyBuilder can save an assembly,
#   but nothing can turn a DLR expression tree into a MethodBuilder any more).
#   So this is a deployment compiler, not a code generator; see README.md.
#
#   The embedded tree behaves like a read-only directory at <app>/src: require,
#   require_relative, load, File.read, Dir.glob and Dir.entries all see it,
#   because those go through the DLR's PlatformAdaptationLayer.  File.exist?
#   and the rest of FileTest do not -- they stat the real file system.
#
# jrubyc's option names are kept where they mean the same thing (-t/--target,
# -d/--dir, -p/--prefix, --verbose); the rest are IronRuby's own.

require "optparse"
require "fileutils"
require "rbconfig"
require "json"
require_relative "compiler/library"

module IronRuby
  module Compiler
    VERSION = "0.1"

    class Options
      attr_accessor :target, :basedir, :prefix, :main, :name, :app_host,
                    :self_contained, :rid, :stdlib, :verbose, :keep, :configuration,
                    :single_file, :library, :framework
      attr_reader :sources, :cs_files, :host_references

      def initialize
        @target = "."
        @basedir = nil
        @prefix = nil
        @main = nil
        @name = nil
        @app_host = true          # --dll turns this off
        @self_contained = false
        @rid = nil
        @stdlib = true
        @verbose = false
        @keep = false
        @configuration = "Release"
        @single_file = false      # --single-file
        @library = false          # --library
        @framework = nil          # --framework
        @sources = []
        @cs_files = []            # --cs
        @host_references = []     # --reference
      end
    end

    class CompileError < StandardError; end

    module_function

    def compile_argv(argv)
      options = parse_options(argv)
      return 0 if options == :done      # --help / --version
      return 1 if options.nil?
      compile(options)
      0
    rescue CompileError => e
      warn "irubyc: #{e.message}"
      1
    end

    def parse_options(argv)
      options = Options.new
      parser = OptionParser.new do |opts|
        opts.banner = <<~BANNER
          Usage: irubyc [options] <script.rb> [more.rb|dir ...]

          Packages Ruby sources into a runnable .NET application.  The sources are
          embedded in the output assembly as resources and are compiled by IronRuby
          at startup -- irubyc does not emit IL for the Ruby code itself.

          The first file given is the main script unless --main says otherwise; the
          rest (and every .rb under any directory given) are embedded too and can be
          required by name.  Needs the .NET SDK on the machine that runs irubyc.
        BANNER

        opts.on("-t", "--target DIR", "Directory to write the application into (default: .)") { |d| options.target = d }
        opts.on("-d", "--dir BASEDIR", "Base directory the embedded paths are relative to") { |d| options.basedir = d }
        opts.on("-p", "--prefix PREFIX", "Prefix prepended to every embedded path") { |p| options.prefix = p }
        opts.on("--main FILE", "Script to run at startup (default: the first file)") { |f| options.main = f }
        opts.on("-o", "--output NAME", "Name of the application (default: the main script's basename)") { |n| options.name = n }
        opts.on("--exe", "Produce a native launcher next to the assembly (default)") { options.app_host = true }
        opts.on("--dll", "Produce only the assembly; run it with `dotnet <name>.dll`") { options.app_host = false }
        opts.on("--self-contained [RID]", "Bundle the .NET runtime too (needs the runtime packs)") do |rid|
          options.self_contained = true
          options.rid = rid
        end
        opts.on("--single-file [RID]", "Produce one executable file (implies --exe; RID defaults to this machine's)") do |rid|
          options.single_file = true
          options.app_host = true
          options.rid = rid if rid
        end
        opts.on("--library", "Produce one self-contained class library (<name>.dll) for a .NET host",
                "to load as a plugin; see --cs and --reference") do
          options.library = true
          options.app_host = false
        end
        opts.on("--cs FILE", "C# source to compile into the --library (repeatable)") { |f| options.cs_files << f }
        opts.on("--reference DLL", "Assembly the host provides: compiled against, not bundled (repeatable)") { |f| options.host_references << f }
        opts.on("--framework TFM", "Target framework (default: the one this IronRuby was built for)") { |f| options.framework = f }
        opts.on("--no-stdlib", "Leave out Lib/ruby (the MRI library and gems); keeps the prelude") { options.stdlib = false }
        opts.on("--debug", "Compile the host with the Debug configuration") { options.configuration = "Debug" }
        opts.on("--keep", "Keep the generated C# project and say where it is") { options.keep = true }
        opts.on("--verbose", "Print each step, including the dotnet command line") { options.verbose = true }
        opts.on("--version", "Print the irubyc version and exit") do
          puts "irubyc #{VERSION} (#{RUBY_DESCRIPTION})"
          return :done
        end
        opts.on("-h", "--help", "Show this message") do
          puts opts
          return :done
        end
      end

      rest = parser.parse(argv)
      if rest.empty?
        warn parser.help
        return nil
      end
      options.sources.concat(rest)
      options
    end

    # --- source collection -------------------------------------------------

    def collect_sources(options)
      files = []
      options.sources.each do |arg|
        if File.directory?(arg)
          found = Dir.glob(File.join(arg, "**", "*.rb")).sort
          raise CompileError, "no .rb files under #{arg}" if found.empty?
          files.concat(found)
        elsif File.file?(arg)
          files << arg
        else
          raise CompileError, "no such file or directory -- #{arg}"
        end
      end
      files = files.map { |f| File.expand_path(f) }.uniq
      raise CompileError, "nothing to compile" if files.empty?
      files
    end

    def main_file(options, files)
      main = options.main ? File.expand_path(options.main) : files.first
      unless files.include?(main)
        raise CompileError, "--main #{options.main} is not among the files being compiled"
      end
      main
    end

    # The base directory embedded paths are relative to: -d if given, otherwise
    # the deepest directory that contains every source.
    def base_dir(options, files)
      return File.expand_path(options.basedir) if options.basedir
      dirs = files.map { |f| File.dirname(f).split(File::SEPARATOR) }
      common = dirs.first.each_with_index.take_while { |part, i| dirs.all? { |d| d[i] == part } }.map(&:first)
      common.empty? ? File::SEPARATOR : common.join(File::SEPARATOR)
    end

    def embedded_path(file, base, prefix)
      rel = file.sub(/\A#{Regexp.escape(base)}#{Regexp.escape(File::SEPARATOR)}?/, "")
      rel = rel.tr(File::ALT_SEPARATOR || "\\", "/")
      prefix && !prefix.empty? ? File.join(prefix, rel) : rel
    end

    # --- the .NET side -----------------------------------------------------

    def bin_dir
      dir = RbConfig::CONFIG["bindir"]
      unless File.file?(File.join(dir, "IronRuby.dll"))
        raise CompileError, "cannot find IronRuby.dll next to the running ir (looked in #{dir})"
      end
      dir
    end

    def lib_dir
      RbConfig::CONFIG["libdir"]
    end

    # The compiled app has to target the framework the interpreter it links against was
    # built for: the tree multi-targets net8.0;net10.0, and bin_dir is one of those output
    # directories.  Take the TFM from the directory name when it is one (bin/<config>/<tfm>),
    # and otherwise from the runtime this process is on -- a published or installed layout
    # has no TFM in its path, and there the running runtime is the right answer anyway.
    def target_framework
      name = File.basename(bin_dir)
      return name if name =~ /\Anet\d+\.\d+\z/
      major = begin
        System::Environment.version.major
      rescue StandardError, NameError
        8
      end
      major >= 5 ? "net#{major}.0" : "net8.0"
    end

    def dotnet
      candidates = []
      candidates << ENV["DOTNET"] if ENV["DOTNET"]
      candidates << File.join(ENV["DOTNET_ROOT"], "dotnet" + RbConfig::CONFIG["EXEEXT"]) if ENV["DOTNET_ROOT"]
      candidates << "dotnet#{RbConfig::CONFIG["EXEEXT"]}"
      candidates.each do |c|
        return c if c.include?(File::SEPARATOR) ? File.file?(c) : which(c)
      end
      raise CompileError, "the .NET SDK is required to compile; `dotnet` was not found " \
                          "(set DOTNET or DOTNET_ROOT)"
    end

    def which(cmd)
      (ENV["PATH"] || "").split(File::PATH_SEPARATOR).any? { |d| File.executable?(File.join(d, cmd)) }
    end

    def runtime_identifier
      System::Runtime::InteropServices::RuntimeInformation.runtime_identifier.to_s
    rescue StandardError, NameError
      raise CompileError, "--self-contained needs a runtime identifier; pass one, e.g. --self-contained=linux-x64"
    end

    REFERENCES = %w[
      IronRuby IronRuby.Libraries IronRuby.Prism
      Microsoft.Scripting Microsoft.Dynamic System.CodeDom
    ].freeze

    # Resource names are prefixed so that a compiled app's own resources cannot
    # collide with anything IronRuby's assemblies carry.
    RESOURCE_PREFIX = "irubyc.src/"

    def csproj(name, options, resources, bin)
      refs = references(options, bin).map { |r|
        %(    <Reference Include="#{r}"><HintPath>#{File.join(bin, r + ".dll")}</HintPath></Reference>)
      }.join("\n")
      res = resources.map { |file, logical|
        %(    <EmbeddedResource Include="#{file}" LogicalName="#{logical}" />)
      }.join("\n")

      <<~XML
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>#{framework(options)}</TargetFramework>
            <OutputType>Exe</OutputType>
            <AssemblyName>#{name}</AssemblyName>
            <RootNamespace>IronRuby.Compiled</RootNamespace>
            <Nullable>disable</Nullable>
            <UseAppHost>#{options.app_host}</UseAppHost>
            <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
            <EnableDefaultEmbeddedResourceItems>false</EnableDefaultEmbeddedResourceItems>
            <GenerateDocumentationFile>false</GenerateDocumentationFile>
            <InvariantGlobalization>false</InvariantGlobalization>
        #{single_file_properties(options)}
          </PropertyGroup>
          <ItemGroup>
            <!-- Ruby exposes UTF-7, and .NET's flock(2)-based FileShare emulation collides
                 with File#flock; ir's own host sets both, so a compiled app must too. -->
            <RuntimeHostConfigurationOption Include="System.Text.Encoding.EnableUnsafeUTF7Encoding" Value="true" />
            <RuntimeHostConfigurationOption Include="System.IO.DisableFileLocking" Value="true" />
          </ItemGroup>
          <ItemGroup>
            <Compile Include="apphost.cs" />
            <Compile Include="sources.cs" />
          </ItemGroup>
          <ItemGroup>
        #{res}
          </ItemGroup>
          <ItemGroup>
        #{refs}
          </ItemGroup>
        #{payload_items(options)}
        </Project>
      XML
    end

    # --single-file: one executable, made by the .NET SDK's own bundler.
    #
    # Everything the folder layout has next to the assembly - IronRuby's DLLs, the
    # native libprism and sqlite libraries, and Lib/ - goes into the bundle as content.
    # IncludeAllContentForSelfExtract makes the first run extract all of it to a
    # per-app cache directory (DOTNET_BUNDLE_EXTRACT_BASE_DIR, default ~/.net), and
    # points AppContext.BaseDirectory there.  That is the whole trick: the app host
    # finds Lib/ through AppContext.BaseDirectory, DllImport finds libprism next to
    # the assemblies, and load_assembly finds the YAML library by name, all exactly as
    # in the folder layout - nothing in IronRuby has to know it was bundled.
    # Extracting managed assemblies too (rather than loading them from the bundle in
    # memory) also keeps Assembly.Location non-empty, which IronRuby relies on.
    PAYLOAD_DIR = "payload"

    # The assemblies the host is compiled against: every managed assembly the runtime
    # ships, not just the ones the host calls directly.  As references the SDK resolves
    # and de-duplicates them (a transitive dependency such as Microsoft.Scripting.Metadata
    # would otherwise arrive twice under --single-file - NETSDK1152), bundles them, and
    # lists them in the app's deps.json.  That last part matters for the directory
    # layout too: an assembly the runtime only loads by name - the YAML library, which
    # yaml.rb load_assembly's - is not found by the default load context unless the
    # deps.json names it, so a copy next to the app is not enough (`require "yaml"`
    # failed in --exe/--dll/--self-contained applications).
    def references(options, bin)
      (REFERENCES + managed_assemblies(bin)).uniq
    end

    # The bundle carries this machine's native libraries (libprism, sqlite), so a
    # single file for another OS or CPU would build fine and then fail to parse Ruby
    # at all on the target.  Refuse that up front instead.
    def check_single_file_rid(rid)
      host = runtime_identifier
      return if rid == host
      raise CompileError, "--single-file #{rid}: this machine is #{host}, and the bundle " \
                          "carries this machine's native libraries (libprism, sqlite). " \
                          "Run irubyc on #{rid} to build for it."
    end

    def managed_assemblies(bin)
      runtime = runtime_pack_files(bin)
      Dir.glob(File.join(bin, "*.dll")).map { |f| File.basename(f, ".dll") }
         .reject { |n| n == "ir" || n.start_with?("ir.") }   # the console host itself
         .reject { |n| runtime.include?(n + ".dll") }
         .select { |n| managed_assembly?(File.join(bin, n + ".dll")) }.sort
    end

    # A self-contained ir (a binary release) has the whole .NET runtime next to it:
    # System.Private.CoreLib, libcoreclr and ~180 more.  Referencing those from the
    # generated host fails to compile (CS0433, every type twice), and the app gets its
    # runtime from the SDK anyway, so they are neither referenced nor copied.  ir.deps.json
    # lists them under the runtime pack; a framework-dependent build has no such entry.
    def runtime_pack_files(bin)
      deps = JSON.parse(File.read(File.join(bin, "ir.deps.json")))
      deps["targets"].values.flat_map do |libraries|
        libraries.select { |name, _| name.start_with?("runtimepack.") }.values
      end.flat_map do |files|
        (files["runtime"] || {}).keys + (files["native"] || {}).keys
      end.map { |f| File.basename(f) }
    rescue Errno::ENOENT, JSON::ParserError
      []
    end

    # A native DLL (libprism.dll from a Windows build, say) has no assembly name.
    def managed_assembly?(path)
      System::Reflection::AssemblyName.get_assembly_name(path)
      true
    rescue StandardError, NameError
      false
    end

    def single_file_properties(options)
      return "" unless options.single_file
      <<~XML.chomp
            <PublishSingleFile>true</PublishSingleFile>
            <IncludeAllContentForSelfExtract>true</IncludeAllContentForSelfExtract>
            <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
            <DebugType>embedded</DebugType>
            <!-- the references' .pdb/.xml would otherwise land next to the single file -->
            <AllowedReferenceRelatedFileExtensions>-</AllowedReferenceRelatedFileExtensions>
      XML
    end

    def payload_items(options)
      return "" unless options.single_file
      <<~XML.chomp
          <ItemGroup>
            <Content Include="#{PAYLOAD_DIR}/**/*" Link="%(RecursiveDir)%(Filename)%(Extension)"
                     CopyToOutputDirectory="Never" CopyToPublishDirectory="PreserveNewest" />
          </ItemGroup>
      XML
    end

    def template(name)
      File.read(File.expand_path("compiler/#{name}", __dir__))
    end

    def apphost_source
      template("apphost.cs")
    end

    # The embedded-sources overlay (sources.cs).  An application's virtual source root is
    # <app dir>/src; a library's is <extraction dir>/src, beside the assembly carrying them.
    def sources_source(main_path, resources, root_base: "AppContext.BaseDirectory")
      # Path.Combine understands '/' on both platforms, so keep the portable form.
      manifest = resources.map { |_file, logical|
        embedded = logical.sub(%r{\A#{Regexp.escape(RESOURCE_PREFIX)}}, "")
        %(            { @"#{embedded}", @"#{logical}" },)
      }
      template("sources.cs")
        .sub("@@MAIN_PATH@@") { main_path }
        .sub("@@MANIFEST@@") { manifest.join("\n") }
        .sub("@@SOURCE_ROOT_BASE@@") { root_base }
    end

    def framework(options)
      options.framework || target_framework
    end

    # --- the compile itself ------------------------------------------------

    def check_options(options)
      if options.library
        raise CompileError, "--library makes a class library; it cannot be combined with --single-file" if options.single_file
        raise CompileError, "--library carries its runtime inside; --self-contained does not apply" if options.self_contained
      elsif !options.cs_files.empty? || !options.host_references.empty?
        raise CompileError, "--cs and --reference are for --library"
      end
      (options.cs_files + options.host_references).each do |f|
        raise CompileError, "no such file -- #{f}" unless File.file?(f)
      end
    end

    def compile(options)
      check_options(options)
      files = collect_sources(options)
      main = main_file(options, files)
      base = base_dir(options, files)
      bin = bin_dir

      resources = files.map { |f|
        rel = embedded_path(f, base, options.prefix)
        [f, RESOURCE_PREFIX + rel]
      }
      main_path = embedded_path(main, base, options.prefix)
      name = options.name || File.basename(main, ".rb")

      out = File.expand_path(File.join(options.target, name))
      work = File.expand_path("#{name}.irubyc", options.keep ? options.target : tmp_root)

      say options, "main script: #{main_path}"
      resources.each { |f, logical| say options, "embedding:   #{logical.sub(RESOURCE_PREFIX, "")} (#{f})" }

      check_syntax(options, files)

      FileUtils.rm_rf(work)
      FileUtils.mkdir_p(work)
      if options.library
        Library.new(options, name: name, main_path: main_path, resources: resources,
                    bin: bin, work: work, out: out).build
        FileUtils.rm_rf(work) unless options.keep
        puts "irubyc: wrote #{File.join(out, name + ".dll")}"
        # Like --single-file, the payload holds this machine's native libraries; the DLL
        # itself would load anywhere, so say it here and refuse at run time.
        puts "irubyc: it carries #{runtime_identifier} native libraries (libprism, sqlite) " \
             "and runs on #{runtime_identifier} only"
        puts "irubyc: generated projects kept in #{work}" if options.keep
        return
      end

      File.write(File.join(work, "apphost.cs"), apphost_source)
      File.write(File.join(work, "sources.cs"), sources_source(main_path, resources))
      File.write(File.join(work, "#{name}.csproj"), csproj(name, options, resources, bin))

      if options.single_file
        # The runtime and library have to be in the project *before* the publish,
        # as content the bundler picks up; afterwards is too late.
        payload = File.join(work, PAYLOAD_DIR)
        copy_runtime(options, bin, payload, skip: references(options, bin))
        copy_stdlib(options, payload)
        FileUtils.rm_rf(out)
        run_dotnet(options, work, name, out)
      else
        run_dotnet(options, work, name, out)
        copy_runtime(options, bin, out)
        copy_stdlib(options, out)
      end

      FileUtils.rm_rf(work) unless options.keep

      launcher = options.app_host ? File.join(out, name + RbConfig::CONFIG["EXEEXT"].to_s) : "dotnet #{File.join(out, name + ".dll")}"
      puts "irubyc: wrote #{out}"
      puts "irubyc: run it with #{launcher}"
      if options.keep
        puts "irubyc: generated project kept in #{work}"
      end
    end

    # The Ruby is not compiled here, so nothing would otherwise notice a broken file
    # until the application ran. Ripper is prism, the same parser IronRuby uses, so a
    # file that parses here parses there. Ripper cannot be subclassed in IronRuby
    # (RipperOps is sealed), so this gets the fact of an error but not its line.
    def check_syntax(options, files)
      require "ripper"
      bad = files.reject { |f| Ripper.sexp(File.read(f), f) }
      unless bad.empty?
        raise CompileError, "syntax error in #{bad.join(", ")} " \
                            "(run `ir #{bad.first}` for the location)"
      end
      say options, "syntax:      #{files.size} file(s) parse"
    rescue LoadError
      say options, "syntax:      ripper unavailable, skipping the check"
    end

    def tmp_root
      ENV["TMPDIR"] || ENV["TMP"] || ENV["TEMP"] || "/tmp"
    end

    def run_dotnet(options, work, name, out)
      project = File.join(work, "#{name}.csproj")
      cmd = [dotnet]
      if options.single_file
        rid = options.rid || runtime_identifier
        check_single_file_rid(rid)
        cmd += ["publish", project, "-c", options.configuration, "-r", rid,
                "--self-contained", options.self_contained ? "true" : "false"]
      elsif options.self_contained
        rid = options.rid || runtime_identifier
        cmd += ["publish", project, "-c", options.configuration, "-r", rid, "--self-contained", "true"]
      else
        cmd += ["build", project, "-c", options.configuration]
      end
      cmd += ["-o", out, "--nologo"]

      say options, "running: #{cmd.join(" ")}"
      if options.verbose
        ok = system(*cmd)
      else
        # Quiet on success, but the SDK's diagnostics are the only useful thing
        # to show when the generated host does not compile, so keep them back
        # rather than throw them away.
        output = IO.popen(cmd + ["-v", "quiet"], err: [:child, :out], &:read)
        ok = $?.success?
        print output unless ok
      end
      raise CompileError, "the .NET SDK failed to compile the generated host" unless ok
    end

    # Everything the runtime needs that the SDK did not already copy: the DLLs the
    # host does not reference directly (the YAML library is load_assembly'd from
    # yaml.rb) and libprism, which is a native library.
    def copy_runtime(options, bin, out, skip: [])
      FileUtils.mkdir_p(out)
      runtime = runtime_pack_files(bin)
      Dir.glob(File.join(bin, "*")).each do |src|
        next unless File.file?(src)
        base = File.basename(src)
        next unless base =~ /\.(dll|so|dylib)\z/
        next if base.start_with?("ir.")
        next if runtime.include?(base)
        # A referenced assembly is already in the publish output; a second copy as
        # content would collide with it (NETSDK1152).
        next if skip.include?(File.basename(base, ".dll"))
        dest = File.join(out, base)
        next if File.file?(dest)
        say options, "copying:     #{base}"
        FileUtils.cp(src, dest)
      end
      copy_native_assets(options, bin, out)
    end

    # A NuGet package's native part does not sit next to the assemblies: it is under
    # runtimes/<rid>/native, and the app resolves it through its own deps.json.  The
    # generated host has a deps.json of its own that knows nothing about those
    # packages, so the files are flattened next to the executable instead, where
    # DllImport's default probing finds them.  This is what makes `require "sqlite3"`
    # work in a packaged application: libe_sqlite3.so comes from
    # Microsoft.Data.Sqlite's SQLitePCLRaw dependency.
    def copy_native_assets(options, bin, out)
      rid = begin
        System::Runtime::InteropServices::RuntimeInformation.runtime_identifier.to_s
      rescue StandardError, NameError
        nil
      end
      return if rid.nil? || rid.empty?

      # linux-x64 also matches what a package built for the portable RID ships.
      dirs = [File.join(bin, "runtimes", rid, "native")]
      dirs << File.join(bin, "runtimes", rid.sub(/-.*/, "") + "-x64", "native") if rid !~ /-/
      dirs.each do |dir|
        next unless File.directory?(dir)
        Dir.glob(File.join(dir, "*")).each do |src|
          next unless File.file?(src)
          dest = File.join(out, File.basename(src))
          next if File.file?(dest)
          say options, "copying:     #{File.basename(src)} (native, #{rid})"
          FileUtils.cp(src, dest)
        end
      end
    end

    # rbconfig.rb works out prefix/libdir from its own location, so a copy named
    # "Lib" next to the executable makes the bundle look like an installed IronRuby.
    #
    # Some of the library is not optional: the runtime loads ironruby/ruby4.rb as its
    # prelude, and that pulls in the rest of ironruby/ (Rational, Complex, Set, Pathname)
    # while booting. --no-stdlib therefore drops the vendored Ruby 4.0 tree and the
    # installed gems -- the 12 MB of it -- and keeps what IronRuby itself needs to start.
    ALWAYS_COPIED = ["ironruby"].freeze
    STDLIB_COPIED = [File.join("ruby", "4.0"), File.join("ruby", "gems"),
                     File.join("ruby", "site_ruby")].freeze

    def copy_stdlib(options, out)
      lib = lib_dir
      dest = File.join(out, "Lib")
      subs = ALWAYS_COPIED + (options.stdlib ? STDLIB_COPIED : [])
      say options, "copying:     #{subs.join(", ")} from #{lib}"
      FileUtils.rm_rf(dest)
      subs.each do |sub|
        source = File.join(lib, sub)
        next unless File.directory?(source)
        target = File.join(dest, sub)
        FileUtils.mkdir_p(File.dirname(target))
        FileUtils.cp_r(source, target)
      end
    end

    def say(options, message)
      puts "irubyc: #{message}" if options.verbose
    end
  end
end
