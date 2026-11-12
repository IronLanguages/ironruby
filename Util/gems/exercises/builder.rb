require 'builder'

xml = Builder::XmlMarkup.new(indent: 2)
xml.instruct!
xml.catalog(xmlns: 'urn:x') do
  xml.book(id: 1) do
    xml.title 'Ruby & friends'
    xml.author 'A <B>'
    xml.tags do
      %w[a b].each { |t| xml.tag t }
    end
  end
  xml.comment! 'a comment'
  xml.empty!
end
puts xml.target!

m = Builder::XmlMarkup.new
m.text! 'a & b < c'
puts m.target!

e = Builder::XmlMarkup.new
e.tag!('ns:name', 'v')
puts e.target!

puts Builder::XChar.encode('<&>').unpack('U*').inspect
