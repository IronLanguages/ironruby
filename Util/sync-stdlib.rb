# frozen_string_literal: true
#
# Moves the vendored standard library in Src/StdLib/ruby/4.0 from one CRuby
# release to another, keeping IronRuby's own changes to it:
#
#     ruby Util/sync-stdlib.rb /path/to/ruby-4.0.6 /path/to/ruby-4.0.9
#
# Both arguments are unpacked CRuby source tarballs: OLD is the release the
# vendored copy currently comes from (Src/StdLib/ruby/4.0/VERSION.txt names it),
# NEW the one to move to.  A library is looked up where CRuby installs it from:
# lib/, then each ext/**/lib/.
#
# For every vendored file that NEW changes:
#
#   * unchanged here since OLD - replaced by NEW's copy;
#   * patched here - NEW's change is merged into IronRuby's copy with
#     `git merge-file`, base OLD.  A conflict is left in the file, marked
#     <<<<<<< ironruby / >>>>>>> NEW, for a person to resolve.
#
# Files NEW adds or drops are reported, not copied or deleted: whether IronRuby
# wants them (or has its own, in Src/StdLib/ironruby) is a decision.  Run it
# with any Ruby - it reads and writes files and runs git, nothing else.  It
# changes nothing outside Src/StdLib/ruby/4.0.
#
# Not synced, because they track something other than CRuby's release:
#   prism/, prism.rb - the Ruby half of the prism IronRuby parses with, which
#                      has to match that parser's serialization format
#   irb/, irb.rb     - a newer irb from rubygems.org (see VERSION.txt)
# rdoc and reline are the bundled gems' lib/ - update them from the .gem files
# the same way irb is.  The bundled gems themselves are
# Util/install-bundled-gems.rb's.

require "open3"

ROOT = File.expand_path("..", __dir__)
LIB = File.join(ROOT, "Src", "StdLib", "ruby", "4.0")
NOT_SYNCED = %r{\A(?:prism|irb)(?:/|\.rb\z)}

old_src, new_src = ARGV
unless old_src && new_src && [old_src, new_src].all? { |d| File.directory?(File.join(d, "lib")) }
  abort "usage: #{File.basename($0)} OLD_RUBY_SRC NEW_RUBY_SRC (unpacked CRuby source tarballs)"
end
new_name = File.basename(new_src)

# relative path => source file, as `make install` lays lib/ and ext/*/lib/ out.
def library_files(src)
  files = {}
  ([File.join(src, "lib")] + Dir.glob(File.join(src, "ext", "**", "lib")).sort).each do |dir|
    next unless File.directory?(dir)
    Dir.glob("**/*", File::FNM_DOTMATCH, base: dir).each do |rel|
      path = File.join(dir, rel)
      files[rel] ||= path if File.file?(path)
    end
  end
  files
end

old_files = library_files(old_src)
new_files = library_files(new_src)
vendored = Dir.glob("**/*", File::FNM_DOTMATCH, base: LIB).select { |rel| File.file?(File.join(LIB, rel)) }

copied = merged = 0
conflicts = []
vendored.sort.each do |rel|
  next if rel.match?(NOT_SYNCED)
  old, new = old_files[rel], new_files[rel]
  next unless old && new
  next if File.binread(old) == File.binread(new)

  mine = File.join(LIB, rel)
  if File.binread(mine) == File.binread(old)
    File.binwrite(mine, File.binread(new))
    copied += 1
  else
    _, status = Open3.capture2e("git", "merge-file", "-L", "ironruby", "-L", File.basename(old_src),
                                "-L", new_name, mine, old, new)
    if status.success?
      puts "merged into IronRuby's patched copy: #{rel}"
      merged += 1
    else
      conflicts << rel
    end
  end
end

# What NEW adds next to files that are vendored here, and what it drops.
added = (new_files.keys - old_files.keys).reject { |rel| rel.match?(NOT_SYNCED) }
dropped = (old_files.keys - new_files.keys) & vendored
added.sort.each { |rel| puts "new in #{new_name}, not copied: #{rel}" }
dropped.sort.each { |rel| puts "gone from #{new_name}, still vendored: #{rel}" }
conflicts.each { |rel| puts "CONFLICT, resolve by hand: #{rel}" }
puts "#{copied} replaced, #{merged} merged, #{conflicts.size} conflicts"
exit(conflicts.empty? ? 0 : 1)
