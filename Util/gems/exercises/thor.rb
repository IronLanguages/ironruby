require 'thor'

class Tool < Thor
  package_name 'tool'

  desc 'greet NAME', 'say hello'
  method_option :loud, type: :boolean, aliases: '-l', desc: 'shout'
  method_option :times, type: :numeric, default: 1
  def greet(name)
    msg = "hello #{name}"
    msg = msg.upcase if options[:loud]
    options[:times].to_i.times { puts msg }
  end

  desc 'add A B', 'add two numbers'
  def add(a, b)
    puts a.to_i + b.to_i
  end

  desc 'fail', 'raises'
  def fail_now
    raise Thor::Error, 'deliberate'
  end

  no_commands do
    def helper; 'not a command'; end
  end
end

Tool.start(%w[greet world])
Tool.start(%w[greet world --loud --times 2])
Tool.start(%w[add 2 40])
puts Tool.commands.keys.sort.inspect
puts Tool.new.helper

puts Thor::Util.snake_case('SomeThing')
puts Thor::Util.camel_case('some_thing')
puts Thor::Util.namespace_from_thor_class(Tool)

shell = Thor::Shell::Basic.new
shell.print_table([%w[a bb], %w[ccc d]])
puts shell.set_color('x', :red, false)

begin
  Tool.start(%w[add 1])
rescue Thor::InvocationError => e
  puts e.class
end
