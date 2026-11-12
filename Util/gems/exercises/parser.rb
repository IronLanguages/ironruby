require 'parser/current'

Parser::Builders::Default.emit_lambda = true
Parser::Builders::Default.emit_procarg0 = true

src = <<~RUBY
  class Foo < Bar
    CONST = [1, 2, *rest]
    def baz(a, b = 2, *c, d:, e: 3, **f, &g)
      @x ||= a&.to_s
      case a
      in [1, *] then :arr
      in {k:} then k
      else "\#{a} interpolated"
      end
    rescue => err
      retry
    end
  end
RUBY

buffer = Parser::Source::Buffer.new('(exercise)', source: src)
parser = Parser::CurrentRuby.new
parser.diagnostics.all_errors_are_fatal = true
ast = parser.parse(buffer)
puts ast.to_s

puts ast.type
puts ast.children.first.type
puts ast.loc.expression.line
puts ast.loc.expression.last_line

bad = Parser::Source::Buffer.new('(bad)', source: 'def ; end')
p2 = Parser::CurrentRuby.new
p2.diagnostics.all_errors_are_fatal = true
p2.diagnostics.ignore_warnings = true
begin
  p2.parse(bad)
rescue Parser::SyntaxError => e
  puts "#{e.class}: #{e.diagnostic.message}"
end

puts Parser::CurrentRuby.parse('1 + 2').inspect
rw = Parser::Source::TreeRewriter.new(buffer)
puts rw.source_buffer.name
