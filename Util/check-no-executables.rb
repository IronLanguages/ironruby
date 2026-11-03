# frozen_string_literal: true
#
# Fails when a file in the repository is a program: a Windows PE (.exe, .dll, .NET
# assemblies included), an ELF or Mach-O binary, a Java class or WebAssembly. Everything
# IronRuby ships is compiled from the sources here, the NuGet packages the projects pin and
# the .NET runtime, so the tree itself holds nothing that runs but cannot be read. Images,
# fonts and documents are not programs and are let through. CI runs it on every change:
#
#     ruby Util/check-no-executables.rb            # every file git tracks
#     ruby Util/check-no-executables.rb FILE...    # only these
#
# A file is recognised by its first bytes, not by its name, so a program cannot pass by
# being called notes.txt, and an extension-only placeholder such as
# Tests/Experiments/Loader/Extensions/fcntl.so - which is text - is not flagged.

def kind_of(file, size)
  head = file.read(4096) || "".b
  return "ELF" if head.start_with?("\x7FELF".b)
  return "WebAssembly" if head.start_with?("\0asm".b)
  if head.start_with?("MZ".b) && head.bytesize >= 0x40
    # The PE signature is where e_lfanew says, which may be past what was read.
    pe = head.byteslice(0x3c, 4).unpack1("V")
    if pe + 4 <= size
      signature = pe + 4 <= head.bytesize ? head.byteslice(pe, 4) : (file.seek(pe) && file.read(4))
      return "PE (Windows program or .NET assembly)" if signature == "PE\0\0".b
    end
  end
  case head.byteslice(0, 4)&.unpack1("N")
  when 0xFEEDFACE, 0xFEEDFACF, 0xCEFAEDFE, 0xCFFAEDFE then "Mach-O"
  when 0xCAFEBABE then "Mach-O universal binary or Java class"
  when 0xCAFEBABF then "Mach-O universal binary (64-bit offsets)"
  end
end

paths = ARGV.empty? ? `git ls-files -z`.split("\0") : ARGV
abort "check-no-executables: git ls-files failed" if ARGV.empty? && !$?.success?

found = paths.filter_map do |path|
  next unless File.file?(path)
  size = File.size(path)
  kind = File.open(path, "rb") { |file| kind_of(file, size) }
  "#{path}: #{kind}" if kind
end

if found.empty?
  puts "check-no-executables: #{paths.size} files, no programs among them"
else
  warn "check-no-executables: #{found.size} program(s) in the repository - build them from source instead:"
  found.each { |line| warn "  #{line}" }
  exit 1
end
