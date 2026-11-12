require 'kramdown'

md = <<~MD
  # Title

  Some *emphasis*, **strong**, `code`, and a [link](http://example.com "t").

  > a quote
  > continued

  1. one
  2. two
     - nested
     - items

  | a | b |
  |---|---|
  | 1 | 2 |

  ```ruby
  def x; end
  ```

  Term
  : definition

  ---

  ![img](a.png)

  Footnote reference[^1].

  [^1]: The footnote.
MD

doc = Kramdown::Document.new(md)
puts doc.to_html
puts '--- latex ---'
puts Kramdown::Document.new("# T\n\ntext *e*").to_latex
puts '--- roundtrip ---'
puts Kramdown::Document.new("# T\n\n- a\n- b").to_kramdown
puts '--- errors ---'
d2 = Kramdown::Document.new('[missing]: no such', input: 'kramdown')
puts d2.warnings.inspect
puts Kramdown::VERSION.split('.').first.to_i >= 2
