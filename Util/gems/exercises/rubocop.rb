require 'rubocop'
require 'stringio'
require 'tmpdir'
require 'fileutils'

dir = File.join(Dir.tmpdir, "rubocop-exercise-#{Process.pid}")
FileUtils.rm_rf(dir)
FileUtils.mkdir_p(dir)
File.write(File.join(dir, '.rubocop.yml'), <<~YML)
  AllCops:
    NewCops: disable
    DisabledByDefault: true
  Layout/TrailingWhitespace:
    Enabled: true
  Style/StringLiterals:
    Enabled: true
    EnforcedStyle: single_quotes
  Lint/UselessAssignment:
    Enabled: true
YML
target = File.join(dir, 'sample.rb')
File.write(target, <<~RUBY)
  def greet(name)
    message = "hello \#{name}"
    unused = 1
    puts message
  end
RUBY

out = StringIO.new
status = Dir.chdir(dir) do
  RuboCop::CLI.new.run(['--format', 'emacs', '--no-color', '--cache', 'false',
                        '--out', File.join(dir, 'report.txt'), 'sample.rb'])
end
puts "status=#{status}"
File.read(File.join(dir, 'report.txt')).each_line do |line|
  puts line.sub(dir + '/', '').rstrip
end

puts RuboCop::Version::STRING.split('.').first
cfg = RuboCop::ConfigLoader.configuration_from_file(File.join(dir, '.rubocop.yml'))
puts cfg['Style/StringLiterals']['EnforcedStyle']
puts cfg['Layout/TrailingWhitespace']['Enabled']

source = RuboCop::ProcessedSource.new("x = 1\ny = x\n", RUBY_VERSION.to_f)
puts source.ast.type
puts source.tokens.map(&:type).inspect
puts source.valid_syntax?

FileUtils.rm_rf(dir)
