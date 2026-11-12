require 'simplecov'

puts SimpleCov::VERSION.split('.').first.to_i >= 0
puts SimpleCov.respond_to?(:start)

# The parts that do not need a second process: the filters, the groups and the
# result model, driven from a hand-made coverage hash.
f = SimpleCov::StringFilter.new('/vendor/')
puts f.matches?(SimpleCov::SourceFile.new(__FILE__, { 'lines' => Array.new(60, 1) }))

lines = Array.new(12)
lines[0] = 1
lines[1] = 0
lines[2] = nil
lines[3] = 5
file = SimpleCov::SourceFile.new(__FILE__, { 'lines' => lines })
puts file.covered_lines.size
puts file.missed_lines.size
puts file.never_lines.size
puts file.lines_of_code
puts file.covered_percent.round(2)
puts File.basename(file.filename)
puts file.project_filename.end_with?('simplecov.rb')

result = SimpleCov::Result.new({ __FILE__ => { 'lines' => lines } })
puts result.covered_percent.round(2)
puts result.total_lines
puts result.covered_lines
puts result.missed_lines
puts result.files.size

SimpleCov.configure do
  add_group 'Exercises', 'Util/gems/exercises'
  add_filter '/nothing/'
end
puts SimpleCov.groups.keys.inspect
puts SimpleCov.filters.map { |x| x.class.name.split('::').last }.sort.uniq.inspect
puts SimpleCov::LinesClassifier.new.classify(['# comment', 'x = 1', '']).inspect
