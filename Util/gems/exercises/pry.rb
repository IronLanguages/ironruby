require 'pry'
require 'stringio'

# A real Pry session, driven from a string and captured: the REPL, the command
# set, and the introspection commands that are the reason people install it.
class Widget
  # Doubles the argument.
  def double(x)
    x * 2
  end
end

input = StringIO.new(<<~PRY)
  1 + 41
  defined?(Widget)
  show-source Widget#double
  Widget.instance_methods(false).inspect
  cd Widget
  self
  cd ..
  _
  exit
PRY
output = StringIO.new

Pry.config.color = false
Pry.config.pager = false
Pry.config.correct_indent = false
Pry.config.history_save = false
Pry.config.history_load = false
Pry.start(TOPLEVEL_BINDING, input: input, output: output, prompt: Pry::Prompt[:simple])

output.string.each_line do |line|
  next if line.strip.empty?
  puts line.rstrip
end

puts Pry::Code.new("a\nb\n").lines.size
puts Pry::Method.new(Widget.instance_method(:double)).name
puts Pry::Method.new(Widget.instance_method(:double)).source.lines.first.strip
puts Pry.commands.list_commands.include?('ls')
