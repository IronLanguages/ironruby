require 'action_view'
require 'action_view/helpers'

include ActionView::Helpers::TagHelper
include ActionView::Helpers::SanitizeHelper
include ActionView::Helpers::NumberHelper
include ActionView::Helpers::TextHelper

puts tag.div('body', class: 'a b', id: 'x')
puts tag.br
puts content_tag(:p, 'text', data: { role: 'x' })
puts tag.input(type: 'text', value: '<script>')

puts number_to_currency(1234.567, locale: :en)
puts number_with_delimiter(1_234_567)
puts number_to_percentage(12.345, precision: 1)
puts number_to_human_size(1_500_000)
puts number_with_precision(3.14159, precision: 3)

puts truncate('a long sentence here', length: 10)
puts pluralize(1, 'person')
puts pluralize(3, 'person')
puts word_wrap('one two three four', line_width: 9)
puts simple_format("a\n\nb")
puts excerpt('the quick brown fox', 'quick', radius: 4)

puts sanitize('<b>ok</b><script>bad()</script>')
puts strip_tags('<p>text <b>here</b></p>')
puts strip_links('<a href="x">link</a> rest')

lookup = ActionView::LookupContext.new([])
view = ActionView::Base.with_empty_template_cache.new(lookup, {}, nil)
template = ActionView::Template.new(
  '<%= 2 + 2 %> and <%= name %>', 'inline', ActionView::Template::Handlers::ERB.new,
  locals: [:name], format: :html
)
puts template.render(view, { name: 'Ada' })

puts ActionView::VERSION::MAJOR
