require 'mime/types'

puts MIME::Types['text/html'].map(&:to_s).inspect
puts MIME::Types.type_for('index.html').map(&:to_s).sort.inspect
puts MIME::Types.type_for('a.png').map(&:to_s).inspect
puts MIME::Types.type_for('a.unknown-extension').inspect

t = MIME::Types['application/json'].first
puts t.content_type
puts t.media_type
puts t.sub_type
puts t.extensions.sort.inspect
puts t.binary?
puts t.ascii?
puts t.registered?

puts MIME::Types['image/png'].first.friendly
puts MIME::Type.new('application/x-custom').to_s
puts (MIME::Types['text/plain'].first <=> MIME::Types['text/html'].first)
puts MIME::Types.count > 1000
puts MIME::Types.type_for('report.PDF').map(&:to_s).inspect
