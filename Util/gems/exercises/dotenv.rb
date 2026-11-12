require 'dotenv'
require 'fileutils'
require 'tmpdir'

dir = File.join(Dir.tmpdir, "dotenv-exercise-#{Process.pid}")
FileUtils.mkdir_p(dir)
env = File.join(dir, '.env')
File.write(env, <<~ENV)
  # a comment
  PLAIN=value
  QUOTED="quoted value"
  SINGLE='single #notacomment'
  EMPTY=
  export EXPORTED=yes
  INTERPOLATED=${PLAIN}-suffix
  MULTI="line one\\nline two"
  SPACED = spaced
ENV

parsed = Dotenv.parse(env)
parsed.sort.each { |k, v| puts "#{k}=#{v.inspect}" }

%w[PLAIN QUOTED EXPORTED INTERPOLATED].each { |k| ENV.delete(k) }
Dotenv.load(env)
puts ENV['PLAIN']
puts ENV['QUOTED']
puts ENV['INTERPOLATED']

ENV['PLAIN'] = 'preset'
Dotenv.load(env)
puts ENV['PLAIN']
Dotenv.overload(env)
puts ENV['PLAIN']

puts Dotenv.parse(File.join(dir, 'missing.env')).inspect rescue puts 'raises on missing'
FileUtils.rm_rf(dir)
