require 'loofah'

dirty = <<~HTML
  <div id="keep" class="c">
    <p>Safe <b>bold</b> text</p>
    <script>alert('xss')</script>
    <a href="javascript:evil()">bad link</a>
    <a href="/ok">good link</a>
    <img src="x.png" onerror="evil()">
    <style>body { display: none }</style>
  </div>
HTML

puts Loofah.html5_fragment(dirty).scrub!(:strip).to_s.gsub(/\n\s*/, "\n").strip
puts '--- prune ---'
puts Loofah.html5_fragment(dirty).scrub!(:prune).to_s.gsub(/\n\s*/, "\n").strip
puts '--- escape ---'
puts Loofah.html5_fragment('<script>x</script>').scrub!(:escape).to_s
puts '--- text ---'
puts Loofah.html5_fragment(dirty).text.split.join(' ')
puts '--- whitewash ---'
puts Loofah.html5_fragment('<div style="x"><p>a</p></div>').scrub!(:whitewash).to_s

doc = Loofah.html5_document('<html><body><p>x</p></body></html>')
puts doc.scrub!(:strip).at_css('p').text

custom = Loofah::Scrubber.new do |node|
  node.remove if node.name == 'b'
end
puts Loofah.html5_fragment('<p>a<b>b</b>c</p>').scrub!(custom).to_s

puts Loofah::HTML5::SafeList::ALLOWED_ELEMENTS.include?('p')
puts Loofah::HTML5::SafeList::ALLOWED_ELEMENTS.include?('script')
puts Loofah::VERSION.split('.').first
