require 'nokogiri'

html = <<~HTML
  <!DOCTYPE html>
  <html><head><title>A &amp; B</title></head>
  <body>
    <div id="main" class="box">
      <p class="first">One</p>
      <p class="second" data-x="7">Two</p>
      <ul><li>a</li><li>b</li></ul>
      <a href="/l1">L1</a><a href="/l2">L2</a>
    </div>
  </body></html>
HTML

doc = Nokogiri::HTML(html)
puts doc.title
puts doc.at_css('#main p.first').text
puts doc.css('p').map(&:text).inspect
puts doc.css('ul li').map(&:text).inspect
puts doc.at_css('p.second')['data-x']
puts doc.css('a').map { |a| a['href'] }.inspect
puts doc.at_css('#main')['class']
puts doc.css('p').size
puts doc.at_css('p.second').name
puts doc.at_css('p.second').parent.name
puts doc.at_css('ul').children.map(&:name).inspect

node = doc.at_css('p.first')
node.content = 'Changed'
puts doc.at_css('p.first').text
node['class'] = 'renamed'
puts doc.at_css('p.renamed').text

xml = Nokogiri::XML('<r><a id="1">x</a><a id="2">y</a></r>')
puts xml.root.name
puts xml.xpath('//a').map { |n| n['id'] }.inspect
puts xml.at_xpath('//a[@id="2"]').text
puts xml.xpath('count(//a)')
puts xml.errors.inspect

frag = Nokogiri::HTML.fragment('<b>x</b><i>y</i>')
puts frag.children.map(&:name).inspect
puts frag.to_html

# Well-formed XML has no errors on either side. IronRuby's shim does not yet record
# parse errors for malformed XML, so that is left to POPULAR.md rather than pretended here.
puts Nokogiri::XML('<a><b/></a>').errors.empty?
puts Nokogiri::VERSION.split('.').first.to_i >= 1
