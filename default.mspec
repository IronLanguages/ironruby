# MSpec configuration for running ruby/spec against IronRuby.
#
#   ir -X:StdLib=<stdlib paths> -Imspec/lib mspec/bin/mspec-run spec/language
#
class MSpecScript
  ir_root = File.expand_path(File.dirname(__FILE__))

  windows = File::ALT_SEPARATOR == "\\"
  exe = windows ? "ir.exe" : "ir"
  tfm = ENV["IR_TFM"].to_s.empty? ? "net10.0" : ENV["IR_TFM"]
  bin = File.join(ir_root, "Src", "Console", "bin", "Debug", tfm)
  # A tree cross-published for Windows puts the host one level deeper.
  bin = File.join(bin, "win-x64") if windows && !File.exist?(File.join(bin, exe))

  stdlib = %w[ironruby ruby/4.0].
    map { |d| File.join(ir_root, "Src", "StdLib", d) }.join(File::PATH_SEPARATOR)
  # The separator is ";" on Windows, which is also how cmd.exe separates arguments - and
  # ruby_exe builds a command line the shell parses. Quoting keeps the two directories
  # one argument; RubyOptionsParser.GetPaths strips the quotes back off.
  stdlib = "\"#{stdlib}\"" if windows

  set :target, File.join(bin, exe)
  set :flags, [
    # -X:StdLib rather than -I: the library goes on $LOAD_PATH after the -I paths a spec
    # passes, as MRI's does
    "-X:StdLib=#{stdlib}",
  ]

  set :language, [File.join(ir_root, "spec", "language")]

  set :excludes, [
    # IO.popen("-") is fork-without-exec. The CLR has no fork primitive, and emulating it
    # with a new interpreter would not preserve the running Ruby process's state.
    "IO.popen starts returns a forked process if the command is -",
  ]

end

# mspec's fixnum_max/fixnum_min helpers (mspec/helpers/numeric.rb) know the Fixnum range only of
# the implementations they name - MRI's from RbConfig::LIMITS, constants for JRuby, TruffleRuby,
# Rubinius and Opal - and raise "unknown implementation" for any other, which turned every spec
# of the Fixnum/Bignum boundary into an error. IronRuby publishes its range where MRI does.
unless RUBY_ENGINE == "ruby"
  require "mspec/helpers/numeric"
  require "rbconfig/sizeof"

  def fixnum_max
    RbConfig::LIMITS["FIXNUM_MAX"]
  end

  def fixnum_min
    RbConfig::LIMITS["FIXNUM_MIN"]
  end
end
