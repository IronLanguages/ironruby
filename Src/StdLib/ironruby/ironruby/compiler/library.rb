# irubyc --library: one self-contained class library for a .NET host to load.
#
# .NET's single-file bundler only makes executables, so a library carries its own
# payload: IronRuby's assemblies, the native libraries, Lib/ and a generated
# <name>.RubyHost assembly (the embedded Ruby sources and everything that derives
# from IronRuby types) go into one zip, embedded as a resource of <name>.dll.  At
# run time the library extracts it to a per-content cache directory and resolves
# IronRuby from there -- see compiler/library.cs for how and why.
#
# Two projects are built:
#
#   <work>/rubyhost  <name>.RubyHost.dll  sources.cs + libraryhost.cs + the Ruby files
#   <work>/library   <name>.dll           library.cs + the --cs files + payload.zip
#
# The split is what makes the library safe to load: a plugin host calls
# Assembly.GetTypes() on it before anything else, and a type deriving from a DLR
# class (the platform layer that serves the embedded files) would fail to load at
# that point, before any resolver could have been installed.

require "digest"

module IronRuby
  module Compiler
    class Library
      PAYLOAD_RESOURCE = "irubyc.payload.zip"

      def initialize(options, name:, main_path:, resources:, bin:, work:, out:)
        @options = options
        @name = name
        @main_path = main_path
        @resources = resources
        @bin = bin
        @work = work
        @out = out
      end

      def build
        host_dll = build_ruby_host
        payload = File.join(@work, "payload")
        assemblies = stage_payload(payload, host_dll)
        zip = File.join(@work, "library", "payload.zip")
        write_zip(payload, zip)
        digest = Digest::SHA256.file(zip).hexdigest
        say "payload:     #{File.size(zip) / 1024} KB zipped, sha256 #{digest[0, 16]}"
        build_library(zip, "#{@name}-#{digest[0, 16]}", assemblies)
      end

      private

      def say(message)
        Compiler.say(@options, message)
      end

      def host_assembly_name
        "#{@name}.RubyHost"
      end

      # --- <name>.RubyHost.dll ------------------------------------------------

      def build_ruby_host
        dir = File.join(@work, "rubyhost")
        FileUtils.mkdir_p(dir)
        root_base = "Path.GetDirectoryName(typeof(EmbeddedSources).Assembly.Location)"
        File.write(File.join(dir, "sources.cs"), Compiler.sources_source(@main_path, @resources, root_base: root_base))
        File.write(File.join(dir, "libraryhost.cs"), Compiler.template("libraryhost.cs"))
        resources = @resources.map { |file, logical|
          %(    <EmbeddedResource Include="#{file}" LogicalName="#{logical}" />)
        }.join("\n")
        project = File.join(dir, "#{host_assembly_name}.csproj")
        File.write(project, project_xml(host_assembly_name, <<~XML))
            <DefineConstants>$(DefineConstants);IRUBYC_LIBRARY</DefineConstants>
          </PropertyGroup>
          <ItemGroup>
            <Compile Include="sources.cs" />
            <Compile Include="libraryhost.cs" />
          </ItemGroup>
          <ItemGroup>
          #{resources}
          </ItemGroup>
          <ItemGroup>
          #{runtime_references}
          </ItemGroup>
        XML
        built = File.join(dir, "out")
        dotnet_build(project, built)
        File.join(built, host_assembly_name + ".dll")
      end

      # --- the payload ---------------------------------------------------------

      # Everything the folder layout has next to the application, flattened into one
      # directory: the managed assemblies, this platform's native libraries and Lib/.
      # Returns the simple names of the managed assemblies, which the bootstrap resolves.
      def stage_payload(payload, host_dll)
        FileUtils.rm_rf(payload)
        FileUtils.mkdir_p(payload)
        assemblies = Compiler.managed_assemblies(@bin)
        assemblies.each { |n| FileUtils.cp(File.join(@bin, n + ".dll"), payload) }
        FileUtils.cp(host_dll, payload)
        assemblies << host_assembly_name

        # Native libraries: only this platform's.  A Linux build directory can hold a
        # Windows libprism.dll as well; it would be dead weight here.
        native_ext = native_extension
        runtime = Compiler.runtime_pack_files(@bin)
        Dir.glob(File.join(@bin, "*")).each do |src|
          next unless File.file?(src) && File.extname(src) == native_ext
          next if runtime.include?(File.basename(src))
          next if assemblies.include?(File.basename(src, ".dll"))
          next if native_ext == ".dll" && Compiler.managed_assembly?(src)
          say "native:      #{File.basename(src)}"
          FileUtils.cp(src, payload)
        end
        Compiler.copy_native_assets(@options, @bin, payload)
        Compiler.copy_stdlib(@options, payload)
        assemblies.sort
      end

      def native_extension
        case RbConfig::CONFIG["host_os"]
        when /mswin|mingw|cygwin/ then ".dll"
        when /darwin/ then ".dylib"
        else ".so"
        end
      end

      # A zip with sorted entries and fixed timestamps, so the same content always gives
      # the same bytes, the same hash and so the same extraction directory.
      def write_zip(payload, zip)
        load_assembly "System.IO.Compression"
        FileUtils.mkdir_p(File.dirname(zip))
        FileUtils.rm_f(zip)
        files = Dir.glob(File.join(payload, "**", "*"), File::FNM_DOTMATCH).select { |f| File.file?(f) }.sort
        stamp = System::DateTimeOffset.new(2000, 1, 1, 0, 0, 0, System::TimeSpan.zero)
        level = System::IO::Compression::CompressionLevel.optimal
        stream = System::IO::File.create(zip)
        begin
          archive = System::IO::Compression::ZipArchive.new(stream, System::IO::Compression::ZipArchiveMode.create)
          files.each do |file|
            entry = archive.create_entry(file[(payload.size + 1)..-1].tr("\\", "/"), level)
            entry.last_write_time = stamp
            input = System::IO::File.open_read(file)
            output = entry.open
            begin
              input.copy_to(output)
            ensure
              output.dispose
              input.dispose
            end
          end
          archive.dispose
        ensure
          stream.dispose
        end
      end

      # --- <name>.dll ----------------------------------------------------------

      def build_library(zip, extract_name, assemblies)
        dir = File.dirname(zip)
        rid = Compiler.runtime_identifier
        host_refs = @options.host_references.map { |f| File.expand_path(f) }
        host_names = host_refs.map { |f| System::Reflection::AssemblyName.get_assembly_name(f).name.to_s }

        source = Compiler.template("library.cs")
          .sub("@@MAIN_PATH@@") { @main_path }
          .sub("@@EXTRACT_NAME@@") { extract_name }
          .sub("@@BUILD_RID@@") { rid }
          .sub("@@BUILD_OS@@") { build_os(rid) }
          .sub("@@BUILD_ARCH@@") { System::Runtime::InteropServices::RuntimeInformation.process_architecture.to_s }
          .sub("@@PAYLOAD_ASSEMBLIES@@") { assemblies.map { |a| %(            "#{a}",) }.join("\n") }
          .sub("@@HOST_REFERENCES@@") { host_names.map { |a| %(            "#{a}",) }.join("\n") }
        File.write(File.join(dir, "library.cs"), source)

        compile = [%(    <Compile Include="library.cs" />)]
        @options.cs_files.each do |f|
          full = File.expand_path(f)
          compile << %(    <Compile Include="#{full}" Link="#{File.basename(full)}" />)
        end
        host_ref_items = host_refs.zip(host_names).map { |path, n|
          %(    <Reference Include="#{n}"><HintPath>#{path}</HintPath><Private>false</Private></Reference>)
        }
        host_dll = File.join(@work, "rubyhost", "out", host_assembly_name + ".dll")
        project = File.join(dir, "#{@name}.csproj")
        File.write(project, project_xml(@name, <<~XML))
          </PropertyGroup>
          <ItemGroup>
          #{compile.join("\n")}
          </ItemGroup>
          <ItemGroup>
            <EmbeddedResource Include="payload.zip" LogicalName="#{PAYLOAD_RESOURCE}" />
          </ItemGroup>
          <ItemGroup>
          #{runtime_references}
            <Reference Include="#{host_assembly_name}"><HintPath>#{host_dll}</HintPath><Private>false</Private></Reference>
          #{host_ref_items.join("\n")}
          </ItemGroup>
        XML
        built = File.join(dir, "out")
        dotnet_build(project, built)

        FileUtils.rm_rf(@out)
        FileUtils.mkdir_p(@out)
        FileUtils.cp(File.join(built, @name + ".dll"), @out)
      end

      def build_os(rid)
        case rid
        when /\Awin/ then "windows"
        when /\A(osx|maccatalyst)/ then "osx"
        when /\Alinux/ then "linux"
        else "other"
        end
      end

      # --- MSBuild -------------------------------------------------------------

      # IronRuby and the DLR are compile-time references only: they travel in the payload.
      def runtime_references
        Compiler::REFERENCES.map { |r|
          %(    <Reference Include="#{r}"><HintPath>#{File.join(@bin, r + ".dll")}</HintPath><Private>false</Private></Reference>)
        }.join("\n")
      end

      # The project header; +rest+ closes the PropertyGroup and adds the items.
      def project_xml(assembly_name, rest)
        <<~XML
          <Project Sdk="Microsoft.NET.Sdk">
            <PropertyGroup>
              <TargetFramework>#{Compiler.framework(@options)}</TargetFramework>
              <OutputType>Library</OutputType>
              <AssemblyName>#{assembly_name}</AssemblyName>
              <RootNamespace>IronRuby.Compiled</RootNamespace>
              <Nullable>disable</Nullable>
              <ImplicitUsings>disable</ImplicitUsings>
              <LangVersion>latest</LangVersion>
              <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
              <EnableDefaultEmbeddedResourceItems>false</EnableDefaultEmbeddedResourceItems>
              <GenerateDocumentationFile>false</GenerateDocumentationFile>
              <GenerateDependencyFile>false</GenerateDependencyFile>
              <DebugType>embedded</DebugType>
              <AllowedReferenceRelatedFileExtensions>-</AllowedReferenceRelatedFileExtensions>
              <Deterministic>true</Deterministic>
              <!-- the payload's hash names the extraction directory: keep the work directory's
                   path out of the embedded PDBs, so the same input always hashes the same -->
              <PathMap>$(MSBuildProjectDirectory)=/_/irubyc</PathMap>
          #{rest}</Project>
        XML
      end

      def dotnet_build(project, out)
        cmd = [Compiler.dotnet, "build", project, "-c", @options.configuration, "-o", out, "--nologo"]
        say "running: #{cmd.join(" ")}"
        if @options.verbose
          ok = system(*cmd)
        else
          output = IO.popen(cmd + ["-v", "quiet"], err: [:child, :out], &:read)
          ok = $?.success?
          print output unless ok
        end
        raise CompileError, "the .NET SDK failed to compile #{File.basename(project)}" unless ok
      end
    end
  end
end
