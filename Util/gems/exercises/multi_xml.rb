require 'multi_xml'

MultiXml.parser = :rexml
puts MultiXml.parser.name

xml = <<~XML
  <?xml version="1.0" encoding="UTF-8"?>
  <catalog count="2">
    <book id="1"><title>Ruby</title><price type="float">9.5</price></book>
    <book id="2"><title>Rails &amp; more</title><price type="float">19</price></book>
    <empty/>
    <flag type="boolean">true</flag>
    <when type="datetime">2020-01-02T03:04:05Z</when>
  </catalog>
XML

doc = MultiXml.parse(xml)
puts doc['catalog']['count']
puts doc['catalog']['book'].map { |b| [b['id'], b['title'], b['price']] }.inspect
puts doc['catalog']['empty'].inspect
puts doc['catalog']['flag'].inspect
puts doc['catalog']['when'].inspect

puts MultiXml.parse('<a><b>1</b></a>', symbolize_keys: true).inspect
puts MultiXml.parse('').inspect

begin
  MultiXml.parse('<a><b></a>')
rescue MultiXml::ParseError => e
  puts e.class
end
