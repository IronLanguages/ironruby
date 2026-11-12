require 'tty-prompt'
require 'tty/prompt/test'
require 'stringio'

# tty-prompt drives a terminal, so it is exercised the way its own tests do:
# through TTY::Prompt::Test, with the keystrokes written into the input.
prompt = TTY::Prompt::Test.new

prompt.input << "Ada\r"
prompt.input.rewind
puts prompt.ask('Name?').inspect

prompt.input.truncate(0)
prompt.input.rewind
prompt.input << "\r"
prompt.input.rewind
puts prompt.ask('Name?', default: 'anon').inspect

prompt.input.truncate(0)
prompt.input.rewind
prompt.input << "42\r"
prompt.input.rewind
puts prompt.ask('Age?', convert: :int).inspect

prompt.input.truncate(0)
prompt.input.rewind
prompt.input << "y\r"
prompt.input.rewind
puts prompt.yes?('Sure?').inspect

prompt.input.truncate(0)
prompt.input.rewind
prompt.input << "\e[B\r"
prompt.input.rewind
puts prompt.select('Pick', %w[one two three]).inspect

prompt.input.truncate(0)
prompt.input.rewind
prompt.input << " \e[B \r"
prompt.input.rewind
puts prompt.multi_select('Pick many', %w[a b c]).inspect

prompt.input.truncate(0)
prompt.input.rewind
prompt.input << "secret\r"
prompt.input.rewind
puts prompt.mask('Password?').inspect

puts TTY::Prompt::VERSION.split('.').first
puts prompt.decorate('x', :red).inspect
