require 'mail'

Mail.defaults { delivery_method :test }

m = Mail.new do
  from    'ada@example.com'
  to      ['bob@example.com', 'cy@example.com']
  cc      'dee@example.com'
  subject 'Grüße aus Übersee'
  date    DateTime.parse('2020-01-02T03:04:05+00:00')
  message_id '<fixed@example.com>'
  body    "plain body\nline two"
end
m.charset = 'UTF-8'

puts m.from.inspect
puts m.to.inspect
puts m.subject
puts m.body.decoded
puts m.content_type
puts m.message_id

raw = m.to_s
puts raw.include?('Subject: ')
parsed = Mail.read_from_string(raw)
puts parsed.subject
puts parsed.to.inspect
puts parsed.date.to_s

multi = Mail.new do
  from 'a@example.com'
  to 'b@example.com'
  subject 'multipart'
  message_id '<mp@example.com>'
  text_part { body 'text version' }
  html_part do
    content_type 'text/html; charset=UTF-8'
    body '<h1>html version</h1>'
  end
end
puts multi.multipart?
puts multi.parts.size
puts multi.text_part.body.decoded
puts multi.html_part.body.decoded
puts multi.html_part.content_type

addr = Mail::Address.new('Ada Lovelace <ada@example.com>')
puts addr.display_name, addr.address, addr.domain, addr.local

puts Mail::Encodings::Base64.encode('hello').strip
puts Mail::Encodings::QuotedPrintable.encode('a=b').strip
puts Mail::Encodings.value_decode('=?UTF-8?B?w6RiYw==?=')
