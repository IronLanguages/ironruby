require 'prawn'
require 'digest'

# prawn depends on ttfunk, which asks for bigdecimal ~> 3.1: activating it at
# all is half of what this checks (see POPULAR.md), the PDF is the other half.
puts Gem.loaded_specs.key?('ttfunk')

pdf = Prawn::Document.new(page_size: 'A4', info: { Title: 'Exercise', CreationDate: Time.utc(2026, 1, 2) })
pdf.font 'Helvetica'
pdf.text 'Hello from prawn', size: 18, style: :bold
pdf.move_down 10
pdf.text 'Line two, ' * 20
pdf.stroke_rectangle [0, 600], 200, 100
pdf.fill_color 'FF8000'
pdf.fill_circle [300, 550], 40
pdf.start_new_page
pdf.font 'Courier'
pdf.text_box 'In a box', at: [50, 700], width: 100, height: 40
pdf.number_pages '<page>/<total>', at: [pdf.bounds.right - 50, 0]

puts pdf.page_count
puts pdf.width_of('Hello', size: 12).round(3)
data = pdf.render
puts data[0, 8]
puts data.include?('/Title')
puts Digest::SHA256.hexdigest(data)
