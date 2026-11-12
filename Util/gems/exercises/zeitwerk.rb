require 'zeitwerk'
require 'fileutils'
require 'tmpdir'

root = File.join(Dir.tmpdir, "zeitwerk-exercise-#{Process.pid}")
FileUtils.rm_rf(root)
FileUtils.mkdir_p(File.join(root, 'my_app/models'))
File.write(File.join(root, 'my_app.rb'), "module MyApp; end\n")
File.write(File.join(root, 'my_app/version.rb'), "module MyApp; module Version; STRING = '1.2.3'; end; end\n")
File.write(File.join(root, 'my_app/models/user.rb'), "module MyApp; module Models; class User; def hi; 'hi'; end; end; end; end\n")
File.write(File.join(root, 'my_app/html_parser.rb'), "module MyApp; class HTMLParser; end; end\n")

loader = Zeitwerk::Loader.new
loader.push_dir(root)
loader.inflector.inflect('html_parser' => 'HTMLParser')
loaded = []
loader.on_load { |cpath, _value, _abspath| loaded << cpath }
loader.setup

puts defined?(MyApp::Version).inspect
puts MyApp::Version::STRING
puts MyApp::Models::User.new.hi
puts MyApp::HTMLParser.name
puts loaded.sort.inspect

loader.eager_load
puts MyApp.constants.map(&:to_s).sort.inspect

puts Zeitwerk::Inflector.new.camelize('api_client', nil)
puts Zeitwerk::GemInflector.instance_method(:camelize).arity

begin
  MyApp::Nope
rescue NameError => e
  puts e.class
end

loader.unload
FileUtils.rm_rf(root)
