require 'active_support/all'

puts 'person'.pluralize
puts 'people'.singularize
puts 'active_record'.camelize
puts 'ActiveRecord::Base'.underscore
puts 'hello world'.titleize
puts 'Hello World'.parameterize
puts 'a-b-c'.dasherize.inspect
puts 'once upon a time'.truncate(12)
puts 'x'.presence.inspect
puts ''.presence.inspect
puts nil.blank?, 0.blank?, [].blank?, 'a'.present?

h = { a: 1, 'b' => { 'c' => 2 } }.with_indifferent_access
puts h[:b]['c']
puts h['a']
puts({ a: 1, b: 2 }.except(:a).inspect)
puts({ a: 1, b: 2 }.slice(:b).inspect)
puts({ 'a' => { 'b' => 1 } }.deep_symbolize_keys.inspect)
puts [1, 2, 3, 4].in_groups_of(2).inspect
puts [1, 2, 3].to_sentence
puts [[1, [2, [3]]]].flatten.sum

puts 1.kilobyte
puts 3.days.to_i
puts 90.seconds.inspect

t = Time.utc(2020, 1, 2, 3, 4, 5)
puts t.beginning_of_month.iso8601
puts (t + 1.month).iso8601
puts t.advance(years: 1).iso8601
puts Date.new(2020, 2, 29).end_of_year.to_s

puts ActiveSupport::JSON.encode({ b: 1, a: [1, nil, true] })
# ActiveSupport::JSON.decode is deliberately not exercised: it calls
# JSON.parse(json, options), which the json gem 3.0.2 on the oracle refuses.
puts ActiveSupport::Inflector.ordinalize(23)

ns = ActiveSupport::Notifications
seen = []
ns.subscribe('ex.test') { |name, _s, _f, _id, payload| seen << [name, payload[:v]] }
ns.instrument('ex.test', v: 9) { }
puts seen.inspect

puts ActiveSupport::OrderedOptions.new.tap { |o| o.foo = 1 }.foo
cache = ActiveSupport::Cache::MemoryStore.new
cache.write('k', 'v')
puts cache.fetch('k')
puts cache.fetch('other') { 'computed' }
puts ActiveSupport::Digest.hexdigest('abc')
puts ActiveSupport::StringInquirer.new('production').production?
