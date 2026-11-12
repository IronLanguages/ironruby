require 'colorize'

puts 'x'.red.inspect
puts 'x'.colorize(:blue).inspect
puts 'x'.colorize(color: :green, background: :yellow, mode: :bold).inspect
puts 'x'.light_black.on_white.inspect
puts 'x'.underline.inspect
puts 'x'.red.blue.inspect
puts 'plain'.colorize(:default).inspect
puts String.colors.map(&:to_s).sort.inspect
puts String.modes.map(&:to_s).sort.inspect
puts 'x'.red.uncolorize
puts 'x'.red.colorized?
puts 'x'.colorized?

String.disable_colorization = true
puts 'x'.red.inspect
String.disable_colorization = false
puts 'x'.red.inspect

begin
  'x'.colorize(:no_such_color)
rescue StandardError => e
  puts e.class
end
