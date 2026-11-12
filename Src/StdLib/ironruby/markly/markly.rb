# frozen_string_literal: true
#
# The markly gem's C extension, provided by IronRuby itself.
#
# markly (a CommonMark/GFM parser) is Ruby over one C extension, markly/markly.so:
# GitHub's cmark-gfm plus markly.c, its Ruby binding.  The gem's lib/markly.rb
# starts with `require "markly/markly"`; on IronRuby that finds this file - the
# installed gem has no .so, since IronRuby never compiles an extension - and the
# gem's own node.rb, flags.rb and renderers then load on top of it.
#
# This loads markly.c's classes and methods, written in C# (Src/Libraries/Markly),
# over cmark-gfm running as native code: libmarkly_cmark, built from markly
# 0.15.2's own ext/markly sources by Util/build-markly.sh, so HTML comes out
# byte for byte as it does on CRuby.
#
# That makes the extension markly 0.15's, and only a 0.15 gem's Ruby fits it:
# 0.16 and later moved cmark-gfm on (front matter, inline code info) and call
# methods 0.15's markly.c does not have (Node#_dup, #code_info, #fence), so they
# would run here with options silently ignored and HTML that is not what CRuby
# renders.  A gem of another version is refused with a LoadError that says so.

ironruby_markly_version = "0.15.2"
ironruby_markly_installed =
  begin
    spec = defined?(Gem) && Gem.respond_to?(:loaded_specs) && Gem.loaded_specs["markly"]
    if spec
      spec.version.to_s
    else
      # Not activated through RubyGems (a plain -I): read the version.rb beside
      # the lib/markly.rb that is requiring this file.
      from = caller_locations.map(&:absolute_path).find { |path| path && File.basename(path) == "markly.rb" && path != __FILE__ }
      version_rb = from && File.join(File.dirname(from), "markly", "version.rb")
      version_rb && File.file?(version_rb) && File.read(version_rb)[/VERSION\s*=\s*["']([^"']+)["']/, 1]
    end
  rescue StandardError
    nil
  end

if ironruby_markly_installed && !ironruby_markly_installed.start_with?("0.15.")
  raise LoadError, "cannot load such file -- markly/markly (IronRuby provides markly's C extension as of markly " \
                   "#{ironruby_markly_version}, and the markly gem being loaded is #{ironruby_markly_installed}; " \
                   "use gem \"markly\", \"~> 0.15.0\")"
end

load_assembly 'IronRuby.Libraries', 'IronRuby.StandardLibrary.Markly'

# Load libmarkly_cmark now, so a missing native library is a LoadError from this
# require - what code that treats markly as optional rescues - rather than a
# failure at the first Markly.parse.
Markly.extensions
