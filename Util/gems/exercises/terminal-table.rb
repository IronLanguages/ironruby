require 'terminal-table'

t = Terminal::Table.new do |x|
  x.title = 'Report'
  x.headings = ['Name', 'Qty', 'Price']
  x.rows = [['apple', 3, '1.50'], ['kiwi fruit', 12, '0.30'], :separator, ['ü-umlaut', 1, '9.99']]
  x.style = { padding_left: 2, border_x: '-', border_i: '+' }
end
t.align_column(1, :right)
t.align_column(2, :right)
puts t

puts Terminal::Table.new(rows: [[1, 2], [3, 4]]).to_s

u = Terminal::Table.new(headings: %w[a b], rows: [[{ value: 'span', colspan: 2 }]])
puts u

v = Terminal::Table.new(rows: [['x']])
v.add_row(['y'])
v.add_separator
v.add_row(['z'])
puts v
puts v.number_of_columns
puts v.rows.size
