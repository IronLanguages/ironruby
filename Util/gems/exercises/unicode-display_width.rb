require 'unicode/display_width'

[['hello', 5], ['', 0], ["你好", 4], ["á", 1],
 ["é", 1], ["\t", 1], ["❤️", 2], ["1️⃣", 2]].each do |s, _|
  puts "#{s.codepoints.map { |c| format('U+%04X', c) }.join(',')} => #{Unicode::DisplayWidth.of(s)}"
end

puts Unicode::DisplayWidth.of("你好世界")
puts Unicode::DisplayWidth.of('abc', 2)
puts Unicode::DisplayWidth.of("ä", 1, overwrite: { 0xE4 => 3 })
puts 'straße'.length
puts Unicode::DisplayWidth.of('straße')
puts Unicode::DisplayWidth::UNICODE_VERSION.split('.').first.to_i > 10
