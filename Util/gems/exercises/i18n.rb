require 'i18n'

I18n.backend = I18n::Backend::Simple.new
I18n.available_locales = [:en, :de]
I18n.default_locale = :en
I18n.enforce_available_locales = true

I18n.backend.store_translations(:en,
  greeting: 'Hello, %{name}!',
  apples: { one: 'one apple', other: '%{count} apples' },
  nested: { deep: { key: 'found' } })
I18n.backend.store_translations(:de, greeting: 'Hallo, %{name}!')

puts I18n.t(:greeting, name: 'world')
puts I18n.t(:greeting, name: 'Welt', locale: :de)
puts I18n.t(:apples, count: 1)
puts I18n.t(:apples, count: 5)
puts I18n.t('nested.deep.key')
puts I18n.t(:missing, default: 'fallback')

begin
  I18n.t(:missing, raise: true)
rescue I18n::MissingTranslationData => e
  puts "#{e.class}: #{e.message}"
end

begin
  I18n.locale = :fr
rescue I18n::InvalidLocale => e
  puts "#{e.class}: #{e.message}"
end

puts I18n.with_locale(:de) { I18n.locale }.inspect
puts I18n.locale.inspect
puts I18n.interpolate('%{a}-%{b}', a: 1, b: 2)
puts I18n.normalize_keys(:en, 'a.b', 'c').inspect
