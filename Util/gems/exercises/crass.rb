require 'crass'

css = <<~CSS
  /* a comment */
  a.link, #id > p:hover { color: #fff; margin : 0 auto !important; }
  @media screen and (max-width: 100px) { .x { top: 1.5em } }
CSS

tree = Crass.parse(css)
def summarize(nodes, depth = 0)
  nodes.each do |n|
    puts "#{'  ' * depth}#{n[:node]} #{(n[:name] || n[:value] || n[:tokens] && '').to_s.strip[0, 40].inspect}"
    summarize(n[:children], depth + 1) if n[:children].is_a?(Array)
    summarize(n[:block][:value], depth + 1) if n[:block].is_a?(Hash) && n[:block][:value].is_a?(Array)
  end
end
summarize(tree)

puts Crass.parse_properties('color: red; width: 1px').map { |p| [p[:node], p[:name], p[:value]] }.inspect
puts Crass::Parser.stringify(tree).gsub(/\s+/, ' ').strip
puts Crass::Tokenizer.tokenize('a > b').map { |t| t[:node] }.inspect
puts Crass::Tokenizer.tokenize('"str"').first[:value]
puts Crass::Tokenizer.tokenize('12.5px').first.inspect
