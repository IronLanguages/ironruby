require 'dry/inflector'

i = Dry::Inflector.new do |infl|
  infl.plural 'virus', 'viruses'
  infl.singular 'thieves', 'thief'
  infl.acronym 'API'
  infl.uncountable 'fish'
end

%w[book person child mouse analysis virus fish datum].each do |w|
  puts [w, i.pluralize(w), i.singularize(i.pluralize(w))].inspect
end

puts i.camelize('data_mapper/api_client')
puts i.camelize_lower('data_mapper')
puts i.classify('books')
puts i.constantize('String')
puts i.dasherize('data_mapper')
puts i.demodulize('Foo::Bar::Baz')
puts i.humanize('author_id')
puts i.foreign_key('Message')
puts i.ordinalize(1)
puts i.ordinalize(12)
puts i.ordinalize(23)
puts i.tableize('Book')
puts i.underscore('DataMapper::APIClient')
puts i.underscore('APIClient')
puts i.uncountable?('fish')
