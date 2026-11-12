require 'chunky_png'
require 'digest'

png = ChunkyPNG::Image.new(8, 4, ChunkyPNG::Color::TRANSPARENT)
png[0, 0] = ChunkyPNG::Color.rgba(255, 0, 0, 255)
png[7, 3] = ChunkyPNG::Color.rgb(0, 0, 255)
png.rect(1, 1, 3, 2, ChunkyPNG::Color::BLACK, ChunkyPNG::Color.rgb(0, 255, 0))
png.line(0, 3, 7, 3, ChunkyPNG::Color.rgb(128, 128, 128))
png.circle(4, 2, 1, ChunkyPNG::Color::WHITE)

puts png.width, png.height
puts ChunkyPNG::Color.to_hex(png[0, 0])
puts ChunkyPNG::Color.to_hex(png[2, 1])
puts png.pixels.uniq.size

blob = png.to_blob
puts blob[1, 3]
puts Digest::SHA256.hexdigest(blob)

back = ChunkyPNG::Image.from_blob(blob)
puts back.width, back.height
puts back.pixels == png.pixels

puts ChunkyPNG::Color.r(0x12345678)
puts ChunkyPNG::Color.g(0x12345678)
puts ChunkyPNG::Color.b(0x12345678)
puts ChunkyPNG::Color.a(0x12345678)
puts ChunkyPNG::Color.to_hex(ChunkyPNG::Color.parse('#ff8000'))
puts ChunkyPNG::Color.grayscale(100) == ChunkyPNG::Color.rgb(100, 100, 100)
puts ChunkyPNG::Color.compose(ChunkyPNG::Color.rgba(255, 0, 0, 128), ChunkyPNG::Color::WHITE).to_s(16)

puts png.crop(0, 0, 2, 2).pixels.size
puts png.flip_horizontally.pixels.first == png[7, 0]
puts png.rotate_left.width
puts ChunkyPNG::Image.new(2, 2, ChunkyPNG::Color::WHITE).to_blob.bytesize > 0
