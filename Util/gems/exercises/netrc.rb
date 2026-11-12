require 'netrc'
require 'fileutils'
require 'tmpdir'

dir = File.join(Dir.tmpdir, "netrc-exercise-#{Process.pid}")
FileUtils.mkdir_p(dir)
path = File.join(dir, '.netrc')
File.write(path, <<~NETRC)
  machine example.com
    login alice
    password s3cret

  machine other.test login bob password pw2
  default login anon password none
NETRC
File.chmod(0o600, path)

n = Netrc.read(path)
puts n['example.com'].inspect
puts n['other.test'].inspect
puts n['nothing.test'].inspect
puts n.length
puts n.each.map { |m, _l, _p| m }.inspect

n['new.test'] = ['carol', 'pw3']
n.save
puts File.read(path).include?('new.test')
puts Netrc.read(path)['new.test'].inspect

puts Netrc.default_path.end_with?('.netrc')
puts Netrc.netrc_filename

File.chmod(0o644, path)
begin
  Netrc.read(path)
rescue Netrc::Error => e
  puts e.class
end

FileUtils.rm_rf(dir)
