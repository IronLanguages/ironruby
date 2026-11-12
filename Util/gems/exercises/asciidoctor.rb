require 'asciidoctor'

doc = <<~ADOC
  = Document Title
  Author Name
  :toc:
  :sectnums:

  == First Section

  Some *bold*, _italic_ and `mono` text with a https://example.com[link].

  * one
  * two
  ** nested

  . first
  . second

  [source,ruby]
  ----
  def hello
    puts 'hi'
  end
  ----

  NOTE: An admonition.

  |===
  | A | B
  | 1 | 2
  |===

  Term:: definition

  == Second Section

  Cross reference to <<_first_section>>.
ADOC

puts Asciidoctor.convert(doc, safe: :safe, standalone: false, attributes: { 'nofooter' => '' })
puts '--- document model ---'
d = Asciidoctor.load(doc, safe: :safe)
puts d.doctitle
puts d.author.inspect
puts d.blocks.map(&:context).inspect
puts d.sections.map(&:title).inspect
puts d.sections.first.blocks.map(&:context).inspect
puts d.attributes['toc'].inspect
puts Asciidoctor::VERSION.split('.').first
puts Asciidoctor.convert('*x*', safe: :safe)
