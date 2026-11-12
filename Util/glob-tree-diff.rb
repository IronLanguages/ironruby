# Compares Dir.glob / Dir.[] results over a real, large directory tree between
# this interpreter and whatever produced the reference file.
#
#   ruby     Util/glob-tree-diff.rb > /tmp/tree.mri
#   ./ir.sh  Util/glob-tree-diff.rb > /tmp/tree.ir
#   diff /tmp/tree.mri /tmp/tree.ir
#
# The point is the failure mode where a glob bug silently *drops* files: spec
# discovery, Rake FileLists and $LOAD_PATH scans all go through these patterns,
# and a file that stops being found looks like an unrelated file that "failed to
# load" rather than like a glob bug.  Printing the full sorted list, not just a
# count, is what makes a dropped or duplicated entry visible.

PATTERNS = [
  'spec/core/**/*_spec.rb',
  'spec/core/*',
  'spec/core/dir/**/*',
  'spec/**/fixtures/**/*.rb',
  'mspec/lib/**/*.rb',
  'mspec/**/*.{rb,yaml}',
  'Src/*/*.csproj',
  'Src/**/*.csproj',
  'Src/StdLib/ruby/4.0/*.rb',
  'Src/StdLib/**/net*.rb',
  'Util/*',
  'Util/*.rb',
  '*',
  '.*',
  '**/',
  'Src/Libraries/Builtins/[A-D]*.cs',
  'Src/Libraries/Builtins/[!A-D]*.cs',
  'Src/Ruby/{Builtins,Runtime}/*.cs',
]

PATTERNS.each do |pat|
  files = Dir.glob(pat)
  puts "#{pat}\tcount=#{files.size}"
  files.sort.each { |f| puts "  #{f}" }
  bracket = Dir[pat]
  puts "#{pat}\t[]-same=#{bracket.sort == files.sort}"
end
