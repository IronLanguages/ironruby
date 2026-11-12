# Differential matrix for Dir.glob / Dir.[] / File.fnmatch?.
#
# Builds a fixture tree under a temp dir, then runs a large set of patterns and
# option combinations, printing one line per case.  Run it under both `ruby` and
# `./ir.sh` and diff the two outputs:
#
#   ruby     Util/glob-matrix.rb > /tmp/glob.mri
#   ./ir.sh  Util/glob-matrix.rb > /tmp/glob.ir
#   diff /tmp/glob.mri /tmp/glob.ir
#
# Every line is "<case>\t<result>" so a diff points straight at the pattern that
# misbehaves.  Results are sorted where the API does not itself promise an order,
# so that ordering differences do not drown out the real ones.

require 'fileutils'

ROOT = "/tmp/glob-fixture-#{Process.pid}"

DIRS = %w[
  a a/b a/b/c dir dir/sub dir/sub/deep .hidden .hidden/inner
  brace nest nest/x nest/y
]
FILES = %w[
  file.txt file.rb FILE.TXT other.txt .dotfile .dot.txt
  a/one.txt a/b/two.txt a/b/c/three.txt
  dir/f1 dir/f2 dir/sub/g1 dir/sub/deep/h1
  .hidden/secret.txt .hidden/inner/deep.txt
  brace/x.c brace/y.h brace/z.o
  nest/x/1.txt nest/y/2.txt
  ch_a ch_b ch_c ch_z ch_1 ch_2 ch_- ch_^
] + ['sp*ecial', 'que?tion', 'brack[et']

def build
  FileUtils.rm_rf(ROOT)
  DIRS.each { |d| FileUtils.mkdir_p(File.join(ROOT, d)) }
  FILES.each do |f|
    p = File.join(ROOT, f)
    FileUtils.mkdir_p(File.dirname(p))
    File.write(p, f)
  end
end

def show(v)
  case v
  when Array then '[' + v.map { |e| e.to_s }.join(' ') + ']'
  else v.inspect
  end
end

def try(label)
  r = yield
  r = r.sort if r.is_a?(Array)
  puts "#{label}\t#{show(r)}"
rescue Exception => e
  puts "#{label}\t#{e.class}: #{e.message}"
end

PATTERNS = [
  '*', '*.txt', '*.rb', '**', '**/*', '**/*.txt', '**/', '*/', '*/*',
  '*/*/*', 'a/**/*', 'a/**/*.txt', 'dir/**', 'dir/**/*', '**/deep/*',
  '.*', '.*.txt', '.hidden/*', '.hidden/**/*', '*/../*',
  'file.*', 'file.tx?', 'file.t??', '?ile.txt', 'fil?.???',
  '[fo]*.txt', '[^f]*.txt', '[!f]*.txt', 'ch_[a-c]', 'ch_[a-cz]',
  'ch_[^a-c]', 'ch_[!a-c]', 'ch_[abc]', 'ch_[0-9]', 'ch_[-]', 'ch_[]]',
  'ch_[a-', '[[:alpha:]]*', 'ch_[[:digit:]]',
  'brace/{x,y}.*', 'brace/*.{c,h}', '{a,dir}/*', '{a,dir}', 'nest/{x,y}/*',
  '{file,other}.txt', '{,a}/*', 'a{,/b}', 'brace/{x.c}', 'brace/{x.c,}',
  'brace/{}', 'brace/}', 'brace/{x', '{a,{b,c}}',
  'FILE.TXT', 'file.TXT', 'FILE.txt', '*.TXT',
  'sp\\*ecial', 'sp*ecial', 'que\\?tion', 'brack\\[et',
  'a/', 'a', 'dir/sub/', 'nosuch', 'nosuch/*', '', 'a//b',
  '**/**/*.txt', 'a/*/../*', './*', './**/*.txt',
]

FLAGCOMBOS = {
  '0' => 0,
  'DOTMATCH' => File::FNM_DOTMATCH,
  'NOESCAPE' => File::FNM_NOESCAPE,
  'PATHNAME' => File::FNM_PATHNAME,
  'CASEFOLD' => File::FNM_CASEFOLD,
  'EXTGLOB' => (defined?(File::FNM_EXTGLOB) ? File::FNM_EXTGLOB : 0),
}

build
Dir.chdir(ROOT) do
  puts "== glob/positional-flags =="
  PATTERNS.each do |pat|
    FLAGCOMBOS.each do |fname, f|
      try("glob(#{pat.inspect}, #{fname})") { Dir.glob(pat, f) }
    end
  end

  puts "== element_reference =="
  PATTERNS.each { |pat| try("Dir[#{pat.inspect}]") { Dir[pat] } }
  try('Dir["*.txt","*.rb"]') { Dir['*.txt', '*.rb'] }
  try('Dir[]') { Dir[] }

  puts "== keywords =="
  try('glob * sort:false') { Dir.glob('*', sort: false) }
  try('glob * sort:true') { Dir.glob('*', sort: true) }
  try('glob * base:a') { Dir.glob('*', base: 'a') }
  try('glob **/* base:a') { Dir.glob('**/*', base: 'a') }
  try('glob * base:""') { Dir.glob('*', base: '') }
  try('glob * base:nil') { Dir.glob('*', base: nil) }
  try('glob * base:dir/sub') { Dir.glob('*', base: 'dir/sub') }
  try('glob * flags+base') { Dir.glob('*', File::FNM_DOTMATCH, base: 'a') }
  try('glob * flags:kw') { Dir.glob('*', flags: File::FNM_DOTMATCH) }
  try('glob .* base:a') { Dir.glob('.*', base: 'a') }
  try('Dir[* base:a]') { Dir['*', base: 'a'] }
  try('Dir[* sort:false]') { Dir['*', sort: false] }
  try('Dir[*,*.rb base:a]') { Dir['*', '*.txt', base: 'a'] }
  try('glob bad kw') { Dir.glob('*', bogus: 1) }
  try('glob array pattern') { Dir.glob(['*.txt', '*.rb']) }
  try('glob array + base') { Dir.glob(['*.txt'], base: 'a') }
  try('glob sort:nil') { Dir.glob('*', sort: nil) }

  puts "== default sortedness (unsorted result, order matters) =="
  puts "glob(*) order\t#{show(Dir.glob('*'))}"
  puts "glob(**/*.txt) order\t#{show(Dir.glob('**/*.txt'))}"
  puts "Dir[*] order\t#{show(Dir['*'])}"
  puts "glob({a,dir}/*) order\t#{show(Dir.glob('{a,dir}/*'))}"

  puts "== block form =="
  try('glob * block') { r = []; Dir.glob('*') { |x| r << x }; r }
  try('glob * block retval') { Dir.glob('*') { |x| } }
  try('glob * block base') { r = []; Dir.glob('*', base: 'a') { |x| r << x }; r }

  puts "== arg errors =="
  try('glob nil') { Dir.glob(nil) }
  try('glob 1') { Dir.glob(1) }
  try('glob * "1"') { Dir.glob('*', '1') }
  try('glob \0') { Dir.glob("a\0b") }
  try('glob to_path') { o = Object.new; def o.to_path; '*.txt'; end; Dir.glob(o) }

  puts "== fnmatch =="
  fnm_cases = [
    ['*', 'file.txt'], ['*', '.dotfile'], ['*.txt', 'file.txt'],
    ['*', 'a/b'], ['*', 'a/b/c'], ['**/*', 'a/b/c'], ['**', 'a/b'],
    ['a/*', 'a/b'], ['a/*', 'a/b/c'], ['?', 'a'], ['?', 'ab'],
    ['[abc]', 'b'], ['[^abc]', 'd'], ['[!abc]', 'd'], ['[a-c]', 'b'],
    ['[]]', ']'], ['[[:alpha:]]', 'a'], ['[[:digit:]]', '5'],
    ['\\*', '*'], ['\\*', 'a'], ['*', '\\*'],
    ['FILE', 'file'], ['file', 'FILE'],
    ['.*', '.dotfile'], ['*', ''], ['', ''], ['', 'a'],
    ['a/**/b', 'a/b'], ['a/**/b', 'a/x/b'], ['a/**/b', 'a/x/y/b'],
    ['{a,b}', 'a'], ['{a,b}', 'b'], ['{a,b}', 'c'],
    ['cat', 'cat'], ['ca[a-z]', 'cat'], ['ca?', 'cat'],
  ]
  fnm_cases.each do |pat, path|
    FLAGCOMBOS.each do |fname, f|
      try("fnmatch(#{pat.inspect},#{path.inspect},#{fname})") { File.fnmatch?(pat, path, f) }
    end
  end

  puts "== FNM constants =="
  %w[FNM_NOESCAPE FNM_PATHNAME FNM_DOTMATCH FNM_CASEFOLD FNM_SYSCASE FNM_EXTGLOB FNM_SHORTNAME].each do |c|
    try(c) { File.const_get(c) }
  end
end
FileUtils.rm_rf(ROOT)
