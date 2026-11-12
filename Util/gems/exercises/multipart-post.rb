require 'multipart/post'
require 'stringio'

io = StringIO.new('file contents here')
upload = Multipart::Post::UploadIO.new(io, 'text/plain', 'note.txt')
body, headers = Multipart::Post::Multipartable.new_body_and_headers(
  { 'field' => 'value', 'file' => upload }, 'FIXEDBOUNDARY'
) rescue [nil, nil]

if body.nil?
  # Older API: build the parts directly.
  parts = [Multipart::Post::Parts::ParamPart.new('FIXEDBOUNDARY', 'field', 'value'),
           Multipart::Post::Parts::FilePart.new('FIXEDBOUNDARY', 'file', upload),
           Multipart::Post::Parts::EpiloguePart.new('FIXEDBOUNDARY')]
  body = parts.map { |p| p.to_io.read }.join
end

puts body.gsub("\r\n", "\n")
puts body.bytesize > 0
puts upload.content_type
puts upload.original_filename
puts upload.local_path
