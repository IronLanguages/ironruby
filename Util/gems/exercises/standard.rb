require 'standard'
require 'tmpdir'
require 'fileutils'

dir = File.join(Dir.tmpdir, "standard-exercise-#{Process.pid}")
FileUtils.rm_rf(dir)
FileUtils.mkdir_p(dir)
File.write(File.join(dir, 'sample.rb'), <<~RUBY)
  def greet(name)
    message = "hello \#{name}"
    unused = 1
    puts message
  end
RUBY

status = Dir.chdir(dir) do
  Standard::Cli.new(['--format', 'simple', '--no-fix', 'sample.rb']).run
end
puts "status=#{status}"

puts Standard::Runners::Rubocop.name
puts Standard::BuildsConfig.instance_method(:call).arity
puts defined?(Standard::Cli)
FileUtils.rm_rf(dir)
