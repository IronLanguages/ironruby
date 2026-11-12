require 'sprockets'
require 'fileutils'
require 'tmpdir'

dir = File.join(Dir.tmpdir, "sprockets-exercise-#{Process.pid}")
FileUtils.rm_rf(dir)
FileUtils.mkdir_p(File.join(dir, 'js'))
FileUtils.mkdir_p(File.join(dir, 'css'))
File.write(File.join(dir, 'js/a.js'), "var a = 1;\n")
File.write(File.join(dir, 'js/b.js'), "var b = 2;\n")
File.write(File.join(dir, 'js/all.js'), "//= require a\n//= require b\nvar all = a + b;\n")
File.write(File.join(dir, 'css/site.css'), "body { color: red }\n")

env = Sprockets::Environment.new(dir)
env.append_path(File.join(dir, 'js'))
env.append_path(File.join(dir, 'css'))
env.cache = nil

asset = env['all.js']
puts asset.logical_path
puts asset.content_type
puts asset.to_s
puts asset.length

css = env['site.css']
puts css.content_type
puts css.to_s

puts env['a.js'].to_s
puts env['missing.js'].inspect
puts env.paths.map { |p| File.basename(p) }.sort.inspect
puts Sprockets::VERSION.split('.').first
FileUtils.rm_rf(dir)
