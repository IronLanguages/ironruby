# Drives IronRuby Notepad with nobody at the keyboard: files in every encoding through
# open and save, byte for byte; editing, undo, find, replace, wrap, Ln/Col, zoom, tab
# stops, the formatting keys RichEdit must not see; printing (to PDF, where Windows has
# that printer); the modal dialogs, opened and closed again by a timer.
#
#   ir.cmd Samples\Notepad\test_notepad.rb
#
# CI runs it on Windows. NOTEPAD_SCREENSHOTS=<dir> also saves a picture of each window.
# The test window stays on top while it runs, so that what the pictures show is it.
require_relative "notepad"

N = IronRubyNotepad
F = N::F
D = N::D
K = N::K

DIR = File.join(System::IO::Path.get_temp_path.to_s, "ironruby-notepad-test")
SHOTS = ENV["NOTEPAD_SCREENSHOTS"]
System::IO::Directory.create_directory(DIR)
Dir.glob(File.join(DIR, "*")).each { File.delete(it) }
System::IO::Directory.create_directory(SHOTS) if SHOTS
$failures = 0

def check(label, expected = true)
  actual = yield
  ok = expected == actual
  $failures += 1 unless ok
  puts "#{ok ? 'ok  ' : 'FAIL'} #{label}#{ok ? '' : " - expected #{expected.inspect}, got #{actual.inspect}"}"
rescue Exception => e
  $failures += 1
  puts "FAIL #{label} - #{e.class}: #{e.message}\n  #{(e.backtrace || []).first(4).join("\n  ")}"
end

# Lets the window process its messages for a while, as Application.run would.
def pump(seconds = 0.15)
  t = Time.now
  while Time.now - t < seconds
    F::Application.do_events
    sleep 0.01
  end
end

def screenshot(rect, name)
  return unless SHOTS
  bitmap = D::Bitmap.new(rect.width, rect.height)
  g = D::Graphics.from_image(bitmap)
  g.copy_from_screen(rect.location, D::Point.empty, rect.size)
  g.dispose
  bitmap.save(File.join(SHOTS, name), D::Imaging::ImageFormat.png)
  bitmap.dispose
rescue Exception => e # a session without a screen still runs the test
  puts "no screenshot #{name}: #{e.message}"
end

# A form drawn into a bitmap by itself, wherever it is on screen and whatever covers it.
def picture(form, name)
  return unless SHOTS
  bitmap = D::Bitmap.new(form.width, form.height)
  form.draw_to_bitmap(bitmap, D::Rectangle.new(0, 0, form.width, form.height))
  bitmap.save(File.join(SHOTS, name), D::Imaging::ImageFormat.png)
  bitmap.dispose
rescue Exception => e
  puts "no picture #{name}: #{e.message}"
end

# A modal dialog runs a message loop of its own; the timer fires inside it.
def while_modal(delay = 0.9, &block)
  timer = F::Timer.new
  timer.interval = (delay * 1000).to_i
  timer.tick do
    timer.stop
    block.call
  end
  timer.start
end

def bytes(encoding, text, bom = [])
  body = encoding.get_bytes(text.to_clr_string)
  all = System::Array[System::Byte].new(bom.length + body.length)
  bom.each_with_index { |b, i| all[i] = b }
  body.copy_to(all, bom.length)
  all
end

def base64(path) = System::Convert.to_base64_string(System::IO::File.read_all_bytes(path)).to_s

N.prepare
app = N::App.new(N::Settings.new(File.join(DIR, "settings.json")))
w = app.open_window
w.form.top_most = true
pump 1.0
box = w.editor.box
position = w.instance_variable_get(:@position)
status = -> { w.send(:update_status) }

# Open and save every encoding Notepad offers; the saved file must be the opened one.
utf8 = System::Text::UTF8Encoding.new(false)
encodings = System::Text::Encoding
[
  ["utf8_lf.txt", bytes(utf8, "Grüezi mitenand\nZeile zwei\n\tmit Tab\n"), :utf8, :lf,
   "Grüezi mitenand\nZeile zwei\n\tmit Tab\n"],
  ["utf8bom_crlf.txt", bytes(utf8, "a\r\nb\r\n", [0xEF, 0xBB, 0xBF]), :utf8_bom, :crlf, "a\nb\n"],
  ["utf16le_crlf.txt", bytes(encodings.unicode, "Hello\r\nWörld €", [0xFF, 0xFE]), :utf16le, :crlf, "Hello\nWörld €"],
  ["utf16be_lf.txt", bytes(encodings.big_endian_unicode, "x\ny", [0xFE, 0xFF]), :utf16be, :lf, "x\ny"],
  ["ansi_cr.txt", bytes(encodings.get_encoding(1252), "Käse\rBrot"), :ansi, :cr, "Käse\nBrot"],
  # RichTextBox reads text that starts like RTF as RTF; a text file must stay text.
  ["rtf_like.txt", bytes(utf8, "{\\rtf1 not rtf}\r\n"), :utf8, :crlf, "{\\rtf1 not rtf}\n"],
  ["empty.txt", bytes(utf8, ""), :utf8, :crlf, ""]
].each do |name, data, encoding, eol, text|
  path = File.join(DIR, name)
  System::IO::File.write_all_bytes(path, data)
  w.open_path(path)
  pump 0.05
  check("#{name}: detected #{encoding}, #{eol}", [encoding, eol]) { [w.encoding, w.eol] }
  check("#{name}: text") { w.editor.text.to_s == text }
  copy = File.join(DIR, "saved_#{name}")
  w.write_to(copy)
  check("#{name}: saved byte for byte") { base64(copy) == base64(path) }
end

w.open_path(File.join(DIR, "utf8_lf.txt"))
pump
check("title", "utf8_lf.txt - IronRuby Notepad") { w.form.text.to_s }
box.select(0, 0)
w.editor.insert("1")
box.select(1, 0)
w.editor.insert("2")
pump
check("title marks a change", "*utf8_lf.txt - IronRuby Notepad") { w.form.text.to_s }
check("undo, undo, redo", %w[1Grü Grü 1Grü]) do
  box.undo
  a = w.editor.text.to_s[0, 4]
  box.undo
  b = w.editor.text.to_s[0, 3]
  box.redo
  [a, b, w.editor.text.to_s[0, 4]]
end
check("RichEdit is in plain-text mode") { (N::Native.send_message(box, 0x045A) & 1) == 1 } # EM_GETTEXTMODE

key = ->(keys) { F::KeyEventArgs.new(keys).tap { w.editor.key_down(it) }.suppress_key_press }
check("Ctrl+E (centre) is swallowed") { key[K.Control | K.E] }
check("Ctrl+Shift+> (bigger font) is swallowed") { key[K.Control | K.Shift | K.OemPeriod] }
check("AltGr+E (euro sign) passes", false) { key[K.Control | K.Alt | K.E] }
check("Ctrl+Backspace passes", false) { key[K.Control | K.Back] }
check("Ctrl+numpad plus zooms in", "110%") do
  key[K.Control | K.Add]
  status.call
  w.instance_variable_get(:@zoom_label).text.to_s
end
check("zoom out twice", "90%") do
  w.zoom(:out)
  w.zoom(:out)
  w.instance_variable_get(:@zoom_label).text.to_s
end
w.zoom(:reset)

box.select(0, 0)
w.search[:text] = "zeile"
w.search[:match_case] = false
check("Find Next ignores case", "Zeile") { w.find_next && box.selected_text.to_s }
check("with Match case it finds nothing") { !w.editor.find("zeile", match_case: true, up: false, wrap: true) }
check("Find Previous wraps around", "Zeile") do
  box.select(0, 0)
  w.editor.find("Zeile", match_case: true, up: true, wrap: true) && box.selected_text.to_s
end
# The text is "1Grüezi mitenand\nZeile zwei\n\tmit Tab\n" here: five lowercase e.
check("Replace All", [5, "1GrüEzi mitEnand"]) do
  n = w.editor.replace_all("e", "E", true)
  [n, w.editor.text.to_s.lines.first.chomp]
end
check("Replace All is one undo step", "1Grüezi mitenand") do
  box.undo
  w.editor.text.to_s.lines.first.chomp
end

long = "short\n#{'word ' * 300}\nlast"
w.editor.load_text(long)
w.editor.wrap = true
box.focus
box.select(long.length, 0)
pump 0.2
check("wrapping makes more lines on screen") { box.get_line_from_char_index(box.text_length) > 5 }
check("Ln/Col counts lines of the file when wrapped", "Ln 3, Col 5") { status.call; position.text.to_s }
check("Ln/Col of a selection is at its caret end", "Ln 1, Col 6") do
  box.select(0, 5)
  status.call
  position.text.to_s
end
check("Go To line 2", 6) { w.editor.go_to_line(2); box.selection_start }
check("line count", 3) { w.editor.line_count }
w.editor.wrap = false
pump
check("unwrapped, the same Ln/Col", "Ln 3, Col 5") { box.select(long.length, 0); status.call; position.text.to_s }

w.editor.load_text("\tX\nMMMMMMMM")
pump
tab = -> { [box.get_position_from_char_index(1).x, box.get_position_from_char_index(11).x] }
check("a tab is 8 characters wide") { a, b = tab.call; (a - b).abs <= 1 }
check("and still is after a font change") do
  w.editor.font = D::Font.new("Consolas", 16.0)
  pump
  a, b = tab.call
  (a - b).abs <= 1
end
w.editor.font = D::Font.new("Consolas", 11.0)

check("changing the encoding marks the document", ["UTF-16 LE", true]) do
  w.set_encoding(:utf16le)
  status.call
  [w.instance_variable_get(:@encoding_button).text.to_s, w.form.text.to_s.start_with?("*")]
end
check("ANSI holds umlauts and the euro sign") { N::TextFile.lossless?("Grüezi €", :ansi) }
check("ANSI cannot hold an arrow", false) { N::TextFile.lossless?("a → b", :ansi) }

pdf_printer = "Microsoft Print to PDF"
printers = begin
  D::Printing::PrinterSettings.installed_printers.to_a.map(&:to_s)
rescue Exception # no print spooler
  []
end
if printers.include?(pdf_printer)
  pdf = File.join(DIR, "printed.pdf")
  settings = w.printer.printer_settings
  settings.printer_name = pdf_printer
  settings.print_to_file = true
  settings.print_file_name = pdf
  text = "#{(1..130).map { "Line #{it}\twith a tab" }.join("\n")}\n#{'long ' * 200}"
  check("prints") { w.printer.print_text(w.form, text, w.editor.font, "printed.txt", dialog: false) }
  pump 3.0
  check("on three pages or more") { File.exist?(pdf) && File.binread(pdf).scan(%r{/Type\s*/Page[^s]}).length >= 3 }
else
  puts "skip printing: no #{pdf_printer} here"
end

sample = File.join(DIR, "screenshot.txt")
File.binwrite(sample, "IronRuby Notepad\r\n\r\nRuby on .NET, Windows Forms, RichEdit in plain-text mode.\r\n" \
                      "\tTabs are eight characters, as in Notepad.\r\nGrüezi, ça va? Tschüss! €\r\n")
w.open_path(sample)
scale = w.form.device_dpi / 96.0
w.form.bounds = D::Rectangle.new(80, 80, (720 * scale).round, (420 * scale).round)
box.select(20, 0)
pump 0.5
check("a file Ruby wrote is CRLF, UTF-8", %i[utf8 crlf]) { [w.encoding, w.eol] }
screenshot(w.form.bounds, "main.png")

w.search[:text] = "Ruby"
w.send(:show_find)
pump 0.6
find = w.instance_variable_get(:@find_dialog).form
screenshot(find.bounds, "find.png")
w.send(:show_replace)
pump 0.6
screenshot(w.instance_variable_get(:@replace_dialog).form.bounds, "replace.png")
check("Find goes away when Replace comes", false) { find.visible }
w.instance_variable_get(:@replace_dialog).form.hide
pump 0.2

while_modal do
  form = nil
  F::Application.open_forms.each { form = it if it.text.to_s == "Go To Line" }
  if form
    picture(form, "goto.png") # a modal dialog stays behind an owner that is on top
    form.close
  end
end
check("Go To, cancelled, goes nowhere", nil) { w.send(:go_to) }

box.select(0, 0)
w.editor.insert("x")
while_modal do
  screenshot(w.form.bounds, "save_prompt.png")
  w.instance_variable_get(:@prompt).bound_dialog.close
end
check("Cancel on the save prompt keeps the window", false) { w.confirm_discard }
check("the prompt names the file as Windows writes it") do
  w.instance_variable_get(:@prompt).heading.to_s.include?("\\screenshot.txt")
end

while_modal do
  screenshot(w.form.bounds, "about.png")
  w.instance_variable_get(:@prompt).bound_dialog.close
end
w.send(:about)

w.editor.modified = false
w.form.close
pump 0.5
check("settings are saved on close") do
  saved = JSON.parse(File.read(File.join(DIR, "settings.json")))
  saved["font_family"] == "Consolas" && saved["window"].is_a?(Array)
end

puts
puts $failures.zero? ? "all passed" : "#{$failures} failed"
exit($failures.zero? ? 0 : 1)
