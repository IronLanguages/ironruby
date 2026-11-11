# IronRuby Notepad: Windows Notepad, written in Ruby, on IronRuby and Windows Forms.
#
#   notepad.cmd [file]                    double-click it, drop a file on it, or run it
#   ..\..\ironruby.cmd notepad.rb [file]  the same, keeping the console for Ruby's output
#
# What Notepad does, this does: New, New Window, Open, Save, Save As, Page Setup and
# Print with a header and page numbers; Undo and Redo, Cut, Copy, Paste, Delete, Find,
# Find Next and Previous, Replace and Replace All, Go To, Select All, Time/Date; Word
# Wrap, Font, Zoom (also Ctrl+wheel), the status bar with line, column, characters,
# zoom, line endings and encoding. Files open as UTF-8, UTF-8 with BOM, UTF-16 LE/BE or
# ANSI and are saved the way they came, line endings included; both can be changed
# from the status bar. It asks before throwing away changes, opens files dropped on
# it, and remembers font, word wrap, status bar and window position.
#
# The text box is a RichEdit control switched to plain-text mode, as Notepad's is: no
# formatting can get in by pasting or by RichEdit's own formatting shortcuts, and undo
# has many levels.
require "System.Windows.Forms"
require "System.Drawing"
require "System.Text.Encoding.CodePages, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a"
require "fiddle"
require "json"

module IronRubyNotepad
  F = System::Windows::Forms
  D = System::Drawing
  K = F::Keys
  APP_NAME = "IronRuby Notepad"
  FILTER = "Text Documents (*.txt)|*.txt|All Files (*.*)|*.*"

  # The few Win32 calls Windows Forms has no wrapper for.
  module Native
    user32 = Fiddle.dlopen("user32")
    kernel32 = Fiddle.dlopen("kernel32")
    ptr = Fiddle::TYPE_VOIDP
    SEND_MESSAGE = Fiddle::Function.new(user32["SendMessageW"],
      [ptr, Fiddle::TYPE_UINT, Fiddle::TYPE_UINTPTR_T, ptr], Fiddle::TYPE_INTPTR_T)
    GET_CARET_POS = Fiddle::Function.new(user32["GetCaretPos"], [ptr], Fiddle::TYPE_INT)
    CONSOLE_PROCESSES = Fiddle::Function.new(kernel32["GetConsoleProcessList"], [ptr, Fiddle::TYPE_UINT], Fiddle::TYPE_UINT)
    FREE_CONSOLE = Fiddle::Function.new(kernel32["FreeConsole"], [], Fiddle::TYPE_INT)

    # lparam is an Integer or a Fiddle::Pointer.
    def self.send_message(control, message, wparam = 0, lparam = 0)
      SEND_MESSAGE.call(control.handle.to_int64, message, wparam, lparam)
    end

    def self.caret_position
      point = Fiddle::Pointer.malloc(8)
      return nil if GET_CARET_POS.call(point).zero?
      D::Point.new(*point[0, 8].unpack("l2"))
    end

    # ir.exe and ironruby.exe are console programs, so started from Explorer they get a
    # console window of their own. When nobody else is attached to it, let go of it: that
    # closes the window. Under ironrubyw.exe, which notepad.cmd uses, there is none.
    def self.detach_console_if_alone
      list = Fiddle::Pointer.malloc(64)
      return unless CONSOLE_PROCESSES.call(list, 16) == 1
      FREE_CONSOLE.call
      $stdout = $stderr = File.open("NUL", "w")
    end
  end

  # Reading and writing text files the way Notepad does: the encoding is taken from the
  # byte order mark, else UTF-8 if the bytes are valid UTF-8, else the ANSI code page;
  # the line ending from the first line break. Saving writes both back unchanged.
  module TextFile
    ENCODINGS = { utf8: "UTF-8", utf8_bom: "UTF-8 with BOM", utf16le: "UTF-16 LE",
                  utf16be: "UTF-16 BE", ansi: "ANSI" }.freeze
    LINE_ENDINGS = { crlf: "Windows (CRLF)", lf: "Unix (LF)", cr: "Macintosh (CR)" }.freeze
    BREAKS = { crlf: "\r\n", lf: "\n", cr: "\r" }.freeze

    System::Text::Encoding.register_provider(System::Text::CodePagesEncodingProvider.instance)

    def self.encoding(kind)
      case kind
      when :utf8 then System::Text::UTF8Encoding.new(false)
      when :utf8_bom then System::Text::UTF8Encoding.new(true)
      when :utf16le then System::Text::UnicodeEncoding.new(false, true)
      when :utf16be then System::Text::UnicodeEncoding.new(true, true)
      else System::Text::Encoding.get_encoding(0) # the system's ANSI code page
      end
    end

    # => [text, encoding, line ending]; the text has "\n" for every line break.
    def self.read(path)
      bytes = System::IO::File.read_all_bytes(path)
      kind, skip = byte_order_mark(bytes)
      text =
        if kind
          encoding(kind).get_string(bytes, skip, bytes.length - skip)
        else
          begin
            kind = :utf8
            System::Text::UTF8Encoding.new(false, true).get_string(bytes)
          rescue System::Text::DecoderFallbackException, System::ArgumentException
            kind = :ansi
            encoding(:ansi).get_string(bytes)
          end
        end
      text = text.to_s
      [text.gsub(/\r\n?/, "\n"), kind, line_ending(text)]
    end

    def self.byte_order_mark(b)
      n = b.length
      return [:utf8_bom, 3] if n >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF
      return [:utf16le, 2] if n >= 2 && b[0] == 0xFF && b[1] == 0xFE
      return [:utf16be, 2] if n >= 2 && b[0] == 0xFE && b[1] == 0xFF
      [nil, 0]
    end

    def self.line_ending(text)
      i = text.index(/[\r\n]/)
      return :crlf unless i
      return :lf if text[i] == "\n"
      text[i + 1] == "\n" ? :crlf : :cr
    end

    def self.encode(text, kind, eol)
      enc = encoding(kind)
      body = enc.get_bytes(text.to_s.gsub("\n", BREAKS[eol]).to_clr_string)
      preamble = enc.get_preamble
      all = System::Array[System::Byte].new(preamble.length + body.length)
      preamble.copy_to(all, 0)
      body.copy_to(all, preamble.length)
      all
    end

    def self.write(path, text, kind, eol)
      System::IO::File.write_all_bytes(path, encode(text, kind, eol))
    end

    # Would saving in this encoding lose characters (ANSI cannot hold most of Unicode)?
    def self.lossless?(text, kind)
      enc = encoding(kind)
      s = text.to_s.to_clr_string
      enc.get_string(enc.get_bytes(s)).equals(s)
    end
  end

  class Settings
    DEFAULTS = {
      "font_family" => "Consolas", "font_size" => 11.0, "bold" => false, "italic" => false,
      "word_wrap" => false, "status_bar" => true, "match_case" => false, "wrap_around" => false,
      "window" => nil, "maximized" => false
    }.freeze

    def self.default_path
      File.join(ENV["APPDATA"] || Dir.home, APP_NAME, "settings.json")
    end

    def initialize(path = Settings.default_path)
      @path = path
      @values = DEFAULTS.merge(read)
    end

    def [](key) = @values[key]

    def []=(key, value)
      @values[key] = value
    end

    def save
      System::IO::Directory.create_directory(File.dirname(@path))
      File.write(@path, JSON.pretty_generate(@values))
    rescue Exception # a setting that does not stick is not worth an error box
      nil
    end

    private

    def read
      File.exist?(@path) ? JSON.parse(File.read(@path)) : {}
    rescue Exception
      {}
    end
  end

  # The text area: a RichTextBox in plain-text mode.
  class Editor
    EM_SETTEXTMODE = 0x0459
    TM_PLAINTEXT = 1
    TM_MULTILEVELUNDO = 8
    TM_MULTICODEPAGE = 32
    EM_SETUNDOLIMIT = 0x0452
    EM_SETTARGETDEVICE = 0x0448
    EM_SETTABSTOPS = 0x00CB
    EM_SETMARGINS = 0x00D3
    EM_SETZOOM = 0x04E1

    # Ctrl with a letter, digit or punctuation key is formatting in RichEdit - Ctrl+E
    # centres, Ctrl+1 sets line spacing, Ctrl+Shift+> grows the font - and in plain-text
    # mode it formats the whole document. The ones Notepad uses are menu shortcuts and
    # never get here. Ctrl+Alt is AltGr, which types characters, so it passes.
    FORMATTING_KEYS = [*("A".."Z").map { K.send(it) }, *(0..9).map { K.send("D#{it}") },
                       K.Oemcomma, K.OemPeriod, K.Oemplus, K.OemMinus, K.OemQuestion, K.Oemtilde,
                       K.OemOpenBrackets, K.OemCloseBrackets, K.OemPipe, K.OemSemicolon, K.OemQuotes,
                       K.OemBackslash].freeze

    attr_reader :box, :zoom, :wrap
    attr_accessor :on_zoom, :on_redo

    def initialize
      @box = F::RichTextBox.new
      @box.border_style = F::BorderStyle.None
      @box.dock = F::DockStyle.Fill
      @box.detect_urls = false
      @box.accepts_tab = true
      @box.hide_selection = false # a match stays visible while the Find dialog has the focus
      @box.word_wrap = false      # wrapping goes through EM_SETTARGETDEVICE, see apply_view
      @box.scroll_bars = F::RichTextBoxScrollBars.Both
      @box.allow_drop = true
      @wrap = false
      @zoom = 1.0
      @box.handle_created { configure_native }
      @box.text_changed { @text = nil }
      @box.key_down { |_, e| key_down(e) }
      @box.mouse_wheel { |_, e| mouse_wheel(e) }
    end

    def text = @box.text
    def length = @box.text_length
    def modified = @box.modified

    def modified=(value)
      @box.modified = value
    end

    def font = @box.font

    def font=(font)
      @box.font = font
      apply_view # tab stops and margins are measured in the font
    end

    def wrap=(on)
      @wrap = on
      apply_view
    end

    def zoom=(factor)
      @zoom = factor
      apply_view
    end

    def load_text(text)
      @box.text = text
      @box.clear_undo
      @box.select(0, 0)
      @box.modified = false
      @text = nil
    end

    def insert(text)
      @box.selected_text = text
    end

    def delete
      if @box.selection_length.zero?
        return if @box.selection_start >= @box.text_length
        @box.selection_length = 1
      end
      @box.selected_text = ""
    end

    # Ln and Col as Notepad counts them: logical lines, whether wrapped or not, at the
    # end of the selection the caret is on.
    def line_and_column
      pos = caret_index
      if @wrap
        before = ruby_text[0, pos]
        [before.count("\n") + 1, pos - ((before.rindex("\n") || -1) + 1) + 1]
      else
        line = @box.get_line_from_char_index(pos)
        [line + 1, pos - @box.get_first_char_index_from_line(line) + 1]
      end
    end

    def line_count
      @wrap ? ruby_text.count("\n") + 1 : @box.get_line_from_char_index(length) + 1
    end

    def go_to_line(line)
      start = 0
      if @wrap
        (line - 1).times { start = ruby_text.index("\n", start) + 1 }
      else
        start = @box.get_first_char_index_from_line(line - 1)
      end
      @box.select(start, 0)
      @box.scroll_to_caret
    end

    # Selects the next match and returns true, or returns false.
    def find(what, match_case:, up:, wrap:)
      return false if what.empty?
      options = match_case ? F::RichTextBoxFinds.MatchCase : F::RichTextBoxFinds.None
      back = options | F::RichTextBoxFinds.Reverse
      len = length
      from = @box.selection_start
      to = from + @box.selection_length
      found =
        if up
          at = from.positive? ? @box.find(what, 0, from, back) : -1
          at.negative? && wrap ? @box.find(what, 0, len, back) : at
        else
          at = to < len ? @box.find(what, to, len, options) : -1
          at.negative? && wrap ? @box.find(what, 0, len, options) : at
        end
      return false if found.negative?
      @box.scroll_to_caret
      true
    end

    def replace_selection(what, with, match_case)
      selected = @box.selected_text.to_s
      return if selected.empty?
      same = match_case ? selected == what : selected.downcase == what.downcase
      @box.selected_text = with if same
    end

    # One undo step for all of them. Returns how many there were.
    def replace_all(what, with, match_case)
      return 0 if what.empty?
      pattern = Regexp.new(Regexp.escape(what), match_case ? 0 : Regexp::IGNORECASE)
      count = 0
      result = ruby_text.gsub(pattern) do
        count += 1
        with
      end
      return 0 if count.zero?
      caret = @box.selection_start
      @box.select_all
      @box.selected_text = result
      @box.select([caret, length].min, 0)
      count
    end

    def key_down(e)
      return unless e.control && !e.alt
      code = e.key_code
      case code
      when K.Add then zoom_key(e, :in)
      when K.Subtract then zoom_key(e, :out)
      when K.NumPad0 then zoom_key(e, :reset)
      else
        if code == K.Z && e.shift
          e.suppress_key_press = true
          @on_redo&.call
        elsif FORMATTING_KEYS.include?(code)
          e.suppress_key_press = true
        end
      end
    end

    # Wrapping, tab stops, margins and zoom live in the native control. Windows Forms
    # sets wrapping again while it creates the handle, so the window calls this once
    # more when it has loaded.
    def apply_view
      return unless @box.is_handle_created
      message(EM_SETTARGETDEVICE, 0, @wrap ? 0 : 1)
      stops = Fiddle::Pointer.malloc(4)
      stops[0, 4] = [32].pack("l") # dialog units: 8 characters, as in Notepad
      message(EM_SETTABSTOPS, 1, stops)
      margin = (4 * @box.device_dpi / 96.0).round
      message(EM_SETMARGINS, 3, margin | (margin << 16))
      percent = (@zoom * 100).round
      percent == 100 ? message(EM_SETZOOM, 0, 0) : message(EM_SETZOOM, percent, 100)
    end

    private

    def ruby_text
      @text ||= @box.text.to_s
    end

    # The caret sits at one end of the selection, and RichEdit only tells which end
    # through where it drew the caret.
    def caret_index
      start = @box.selection_start
      len = @box.selection_length
      return start if len.zero?
      return start + len unless @box.focused # a match Find selected: the caret is at its end
      caret = Native.caret_position
      return start + len unless caret
      at_start = @box.get_position_from_char_index(start)
      (caret.x - at_start.x).abs <= 2 && (caret.y - at_start.y).abs <= 2 ? start : start + len
    end

    def message(msg, wparam = 0, lparam = 0) = Native.send_message(@box, msg, wparam, lparam)

    # RichEdit takes plain-text mode only while it is empty, so right after the handle
    # exists. Should text be there already (a recreated handle), it goes out and back in.
    def configure_native
      text = nil
      if @box.text_length.positive?
        text = @box.text
        modified = @box.modified
        @box.text = ""
      end
      message(EM_SETTEXTMODE, TM_PLAINTEXT | TM_MULTILEVELUNDO | TM_MULTICODEPAGE)
      message(EM_SETUNDOLIMIT, 10_000)
      if text
        @box.text = text
        @box.modified = modified
      end
      apply_view
    end

    def zoom_key(e, how)
      e.suppress_key_press = true
      @on_zoom&.call(how)
    end

    # Ctrl+wheel zooms in steps of 10 %, like Notepad, instead of RichEdit's own steps.
    def mouse_wheel(e)
      return unless F::Control.modifier_keys == K.Control
      e.handled = true
      @on_zoom&.call(e.delta.positive? ? :in : :out)
    end
  end

  # Layout pieces shared by the dialogs. Sizes are in pixels at 96 DPI; scale_to_screen
  # turns them into the screen's, and everything else sizes itself from the font.
  module Dialog
    def self.style(form, title)
      form.text = title
      form.form_border_style = F::FormBorderStyle.FixedDialog
      form.maximize_box = false
      form.minimize_box = false
      form.show_in_taskbar = false
      form.show_icon = false
      form.auto_size = true
      form.auto_size_mode = F::AutoSizeMode.GrowAndShrink
      form.padding = F::Padding.new(8)
    end

    # Once the controls are in: set on an empty form, AutoScaleMode scales the form at
    # once, finds no controls, and later ones keep their 96-DPI sizes.
    def self.scale_to_screen(form)
      form.suspend_layout
      form.auto_scale_dimensions = D::SizeF.new(96, 96)
      form.auto_scale_mode = F::AutoScaleMode.Dpi
      form.resume_layout(false)
      form.perform_layout
    end

    def self.label(text)
      label = F::Label.new
      label.text = text
      label.auto_size = true
      label.anchor = F::AnchorStyles.Left
      label
    end

    def self.text_box(width = 230)
      box = F::TextBox.new
      box.width = width
      box.anchor = F::AnchorStyles.Left | F::AnchorStyles.Right
      box
    end

    def self.button(text, &click)
      button = F::Button.new
      button.text = text
      button.auto_size = true
      button.auto_size_mode = F::AutoSizeMode.GrowAndShrink # GrowOnly would keep the scaled-up height
      button.minimum_size = D::Size.new(96, 0)
      button.click { click.call } if click
      button
    end

    def self.check_box(text)
      box = F::CheckBox.new
      box.text = text
      box.auto_size = true
      box
    end

    def self.flow(direction)
      panel = F::FlowLayoutPanel.new
      panel.flow_direction = direction
      panel.wrap_contents = false
      panel.auto_size = true
      panel.auto_size_mode = F::AutoSizeMode.GrowAndShrink
      panel.margin = F::Padding.new(0)
      panel
    end

    def self.table(columns)
      table = F::TableLayoutPanel.new
      table.auto_size = true
      table.auto_size_mode = F::AutoSizeMode.GrowAndShrink
      table.column_count = columns
      columns.times { table.column_styles.add(F::ColumnStyle.new(F::SizeType.AutoSize)) }
      table
    end
  end

  # Find and Replace: modeless, owned by the window, hidden rather than closed so that
  # they keep what was typed.
  class FindDialog
    attr_reader :form

    def initialize(window, replace:)
      @window = window
      @replace = replace
      @search = window.search
      build
    end

    def show
      selected = @window.editor.box.selected_text.to_s
      @what.text = selected.empty? || selected.include?("\n") ? @search[:text] : selected
      @with.text = @search[:replace] if @replace
      @match_case.checked = @search[:match_case]
      @wrap.checked = @search[:wrap]
      @up.checked = @search[:up] unless @replace
      @down.checked = !@search[:up] unless @replace
      if @form.visible
        @form.activate
      else
        @form.show(@window.form)
      end
      @what.select_all
      @what.focus
    end

    def hide
      @form.hide
      @window.form.activate
    end

    private

    def build
      @form = F::Form.new
      Dialog.style(@form, @replace ? "Replace" : "Find")
      @form.start_position = F::FormStartPosition.Manual
      table = Dialog.table(3)
      @what = Dialog.text_box
      table.controls.add(Dialog.label("Fi&nd what:"), 0, 0)
      table.controls.add(@what, 1, 0)
      row = 1
      if @replace
        @with = Dialog.text_box
        table.controls.add(Dialog.label("Re&place with:"), 0, row)
        table.controls.add(@with, 1, row)
        row += 1
      end
      options = Dialog.flow(F::FlowDirection.TopDown)
      @match_case = Dialog.check_box("Match &case")
      @wrap = Dialog.check_box("Wrap ar&ound")
      options.controls.add(@match_case)
      options.controls.add(@wrap)
      bottom = Dialog.flow(F::FlowDirection.LeftToRight)
      bottom.margin = F::Padding.new(0, 6, 0, 0)
      bottom.controls.add(options)
      bottom.controls.add(direction_box) unless @replace
      table.controls.add(bottom, 0, row)
      table.set_column_span(bottom, 2)

      buttons = Dialog.flow(F::FlowDirection.TopDown)
      buttons.margin = F::Padding.new(8, 0, 0, 0)
      @find_next = Dialog.button("&Find Next") { find_next }
      buttons.controls.add(@find_next)
      if @replace
        @replace_button = Dialog.button("&Replace") { replace }
        @replace_all = Dialog.button("Replace &All") { replace_all }
        buttons.controls.add(@replace_button)
        buttons.controls.add(@replace_all)
      end
      cancel = Dialog.button("Cancel") { hide }
      buttons.controls.add(cancel)
      table.controls.add(buttons, 2, 0)
      table.set_row_span(buttons, row + 1)

      @form.controls.add(table)
      @form.accept_button = @find_next
      @form.cancel_button = cancel
      Dialog.scale_to_screen(@form)
      wire
    end

    def direction_box
      group = F::GroupBox.new
      group.text = "Direction"
      group.auto_size = true
      group.auto_size_mode = F::AutoSizeMode.GrowAndShrink
      group.margin = F::Padding.new(12, 0, 0, 0)
      radios = Dialog.flow(F::FlowDirection.LeftToRight)
      radios.dock = F::DockStyle.Fill
      @up = F::RadioButton.new
      @up.text = "&Up"
      @up.auto_size = true
      @down = F::RadioButton.new
      @down.text = "&Down"
      @down.auto_size = true
      radios.controls.add(@up)
      radios.controls.add(@down)
      group.controls.add(radios)
      group
    end

    def wire
      @what.text_changed do
        @search[:text] = @what.text.to_s
        enable_buttons
      end
      @with&.text_changed { @search[:replace] = @with.text.to_s }
      @match_case.checked_changed { @search[:match_case] = @match_case.checked }
      @wrap.checked_changed { @search[:wrap] = @wrap.checked }
      @up&.checked_changed { @search[:up] = @up.checked }
      @form.form_closing do |_, e|
        if e.close_reason == F::CloseReason.UserClosing
          e.cancel = true
          hide
        end
      end
      @form.Load { center_on_owner }
    end

    def enable_buttons
      on = !@what.text.to_s.empty?
      [@find_next, @replace_button, @replace_all].compact.each { it.enabled = on }
    end

    def center_on_owner
      owner = @window.form.bounds
      @form.location = D::Point.new(owner.x + (owner.width - @form.width) / 2,
                                    owner.y + (owner.height - @form.height) / 3)
    end

    def find_next = @window.find_next(up: @replace ? false : @up.checked, owner: @form)

    def replace = @window.replace_next(owner: @form)

    def replace_all = @window.replace_all(owner: @form)
  end

  module GoToDialog
    # => the line number, or nil.
    def self.ask(owner, current, last)
      form = F::Form.new
      Dialog.style(form, "Go To Line")
      form.start_position = F::FormStartPosition.CenterParent
      table = Dialog.table(1)
      table.controls.add(Dialog.label("&Line number:"), 0, 0)
      box = Dialog.text_box(260)
      box.text = current.to_s
      box.key_press { |_, e| e.handled = !(System::Char.is_digit(e.key_char) || System::Char.is_control(e.key_char)) }
      table.controls.add(box, 0, 1)
      buttons = Dialog.flow(F::FlowDirection.RightToLeft)
      buttons.anchor = F::AnchorStyles.Right
      buttons.margin = F::Padding.new(0, 8, 0, 0)
      line = nil
      cancel = Dialog.button("Cancel")
      cancel.dialog_result = F::DialogResult.Cancel
      ok = Dialog.button("Go To") do
        n = box.text.to_s.match?(/\A\d+\z/) ? box.text.to_s.to_i : nil
        if n && n.between?(1, last)
          line = n
          form.dialog_result = F::DialogResult.OK
        else
          F::MessageBox.show(form, "The line number is beyond the total number of lines",
                             "#{APP_NAME} - Goto Line", F::MessageBoxButtons.OK, F::MessageBoxIcon.None)
          box.select_all
        end
      end
      buttons.controls.add(cancel)
      buttons.controls.add(ok)
      table.controls.add(buttons, 0, 2)
      form.controls.add(table)
      form.accept_button = ok
      form.cancel_button = cancel
      Dialog.scale_to_screen(form)
      box.select_all
      form.show_dialog(owner)
      form.dispose
      line
    end
  end

  # Page setup and printing: the editor's font, wrapped to the page, with the file name
  # as the header and "Page n" as the footer, as Notepad prints.
  class Printer
    def printer_settings
      @printer_settings ||= D::Printing::PrinterSettings.new
    end

    def page_settings
      @page_settings ||= D::Printing::PageSettings.new(printer_settings).tap do |page|
        page.margins = D::Printing::Margins.new(79, 79, 98, 98) # Notepad's 20 and 25 mm
      end
    end

    def page_setup(owner)
      dialog = F::PageSetupDialog.new
      dialog.printer_settings = printer_settings
      dialog.page_settings = page_settings
      dialog.enable_metric = true
      dialog.show_dialog(owner)
    end

    # dialog: false prints on the current printer without asking.
    def print_text(owner, text, font, title, dialog: true)
      document = D::Printing::PrintDocument.new
      document.document_name = title
      document.printer_settings = printer_settings
      document.default_page_settings = page_settings
      if dialog
        print_dialog = F::PrintDialog.new
        print_dialog.document = document
        print_dialog.use_ex_dialog = true
        return false unless print_dialog.show_dialog(owner) == F::DialogResult.OK
      else
        document.print_controller = D::Printing::StandardPrintController.new
      end
      lines = page = nil
      document.begin_print do
        lines = text.to_s.split("\n", -1).map { expand_tabs(it) }
        page = 0
      end
      document.print_page do |_, e|
        page += 1
        e.has_more_pages = print_page(e, lines, font, title, page)
      end
      document.print
      true
    end

    private

    def print_page(e, lines, font, title, page)
      g = e.graphics
      area = e.margin_bounds
      height = font.get_height(g)
      centre = D::StringFormat.new
      centre.alignment = D::StringAlignment.Center
      g.draw_string(title, font, D::Brushes.Black, D::RectangleF.new(area.left, area.top, area.width, height * 1.5), centre)
      g.draw_string("Page #{page}", font, D::Brushes.Black,
                    D::RectangleF.new(area.left, area.bottom - height, area.width, height * 1.5), centre)
      format = D::StringFormat.new(D::StringFormat.generic_typographic)
      format.format_flags = format.format_flags | D::StringFormatFlags.LineLimit
      y = area.top + height * 2
      bottom = area.bottom - height * 2
      while !lines.empty? && y + height <= bottom
        line = lines.first
        if line.empty?
          lines.shift
        else
          fit = fitting(g, line, font, area.width, height, format)
          g.draw_string(line[0, fit], font, D::Brushes.Black, D::RectangleF.new(area.left, y, area.width, height * 1.5), format)
          fit >= line.length ? lines.shift : lines[0] = line[fit, line.length - fit]
        end
        y += height
      end
      !lines.empty?
    end

    # How much of the line fits on one printed line, broken at a word where possible.
    def fitting(g, line, font, width, height, format)
      chars = System::Runtime::CompilerServices::StrongBox[System::Int32].new(0)
      rows = System::Runtime::CompilerServices::StrongBox[System::Int32].new(0)
      g.measure_string(line, font, D::SizeF.new(width, height * 1.5), format, chars, rows)
      [chars.value, 1].max
    end

    def expand_tabs(line)
      return line unless line.include?("\t")
      out = String.new
      line.each_char { |c| c == "\t" ? out << (" " * (8 - (out.length % 8))) : out << c }
      out
    end
  end

  # One Notepad window.
  class Window
    attr_reader :form, :editor, :path, :encoding, :eol, :search, :printer

    def initialize(app, cascade_from: nil)
      @app = app
      @settings = app.settings
      @search = app.search
      @printer = Printer.new
      @path = nil
      @encoding = :utf8
      @eol = :crlf
      @editor = Editor.new
      @editor.on_zoom = ->(how) { zoom(how) }
      @editor.on_redo = -> { @editor.box.redo }
      @form = F::Form.new
      @form.icon = app.icon
      build_menu
      build_status_bar
      build_context_menu
      @form.controls.add(@editor.box) # first, so that it fills what menu and status bar leave
      @form.controls.add(@status)
      @form.controls.add(@menu)
      @form.main_menu_strip = @menu
      apply_settings(cascade_from)
      wire
      update_title
      update_status
    end

    # Notepad's answer to a file name that does not exist: offer to create it.
    def open_from_command_line(name)
      path = windows_path(name)
      return open_path(path) if File.exist?(path)
      answer = F::MessageBox.show(@form, "Cannot find the #{path} file.\n\nDo you want to create a new file?",
                                  APP_NAME, F::MessageBoxButtons.YesNo, F::MessageBoxIcon.Warning)
      return unless answer == F::DialogResult.Yes
      guarded { File.write(path, "") }
      @path = path
      update_title
    end

    def open_path(path)
      path = windows_path(path)
      guarded do
        text, @encoding, @eol = TextFile.read(path)
        @editor.load_text(text)
        @path = path
      end
      update_title
      update_status
    end

    def save
      @path ? write_to(@path) : save_as
    end

    def save_as
      dialog = F::SaveFileDialog.new
      dialog.filter = FILTER
      dialog.default_ext = "txt"
      dialog.file_name = @path ? File.basename(@path) : "*.txt"
      dialog.initial_directory = File.dirname(@path) if @path
      return false unless dialog.show_dialog(@form) == F::DialogResult.OK
      write_to(dialog.file_name.to_s)
    end

    def write_to(path)
      path = windows_path(path)
      text = @editor.text
      if !TextFile.lossless?(text, @encoding) && !lossy_save_confirmed?
        return false
      end
      return false unless guarded { TextFile.write(path, text, @encoding, @eol) }
      @path = path
      @editor.modified = false
      update_title
      true
    end

    # true when there is nothing to lose, or the user chose Save (and it saved) or Don't Save.
    def confirm_discard
      return true unless @editor.modified
      save = F::TaskDialogButton.new("&Save")
      discard = F::TaskDialogButton.new("Do&n't Save")
      @prompt = F::TaskDialogPage.new
      @prompt.caption = APP_NAME
      @prompt.heading = "Do you want to save changes to #{@path || 'Untitled'}?"
      @prompt.buttons.add(save)
      @prompt.buttons.add(discard)
      # A button of our own, as the standard one speaks the language of Windows; Escape
      # and the close box still cancel, through allow_cancel.
      @prompt.buttons.add(F::TaskDialogButton.new("Cancel"))
      @prompt.allow_cancel = true
      @prompt.default_button = save
      answer = F::TaskDialog.show_dialog(@form, @prompt)
      return self.save if answer.equal?(save)
      answer.equal?(discard)
    end

    def find_next(up: false, owner: @form)
      what = @search[:text].to_s
      return show_find if what.empty?
      found = @editor.find(what, match_case: @search[:match_case], up: up, wrap: @search[:wrap])
      cannot_find(owner, what) unless found
      found
    end

    def replace_next(owner: @form)
      @editor.replace_selection(@search[:text].to_s, @search[:replace].to_s, @search[:match_case])
      find_next(owner: owner)
    end

    def replace_all(owner: @form)
      what = @search[:text].to_s
      count = @editor.replace_all(what, @search[:replace].to_s, @search[:match_case])
      cannot_find(owner, what) if count.zero?
      count
    end

    def zoom(how)
      percent = (@editor.zoom * 100).round
      percent = case how
                when :in then [percent + 10, 500].min
                when :out then [percent - 10, 10].max
                else 100
                end
      @editor.zoom = percent / 100.0
      update_status
    end

    def set_encoding(kind)
      return if kind == @encoding
      @encoding = kind
      @editor.modified = true
      update_title
      update_status
    end

    def set_line_ending(kind)
      return if kind == @eol
      @eol = kind
      @editor.modified = true
      update_title
      update_status
    end

    def store_settings
      font = @editor.font
      @settings["font_family"] = font.font_family.name.to_s
      @settings["font_size"] = font.size_in_points.to_f
      @settings["bold"] = font.bold
      @settings["italic"] = font.italic
      @settings["word_wrap"] = @editor.wrap
      @settings["status_bar"] = @status_bar
      maximized = @form.window_state == F::FormWindowState.Maximized
      bounds = @form.window_state == F::FormWindowState.Normal ? @form.bounds : @form.restore_bounds
      @settings["window"] = [bounds.x, bounds.y, bounds.width, bounds.height]
      @settings["maximized"] = maximized
    end

    def display_name = @path ? File.basename(@path) : "Untitled"

    private

    # As Windows writes it: Ruby's own paths use forward slashes.
    def windows_path(path) = System::IO::Path.get_full_path(path).to_s

    def command(text, keys = nil, display: nil, &action)
      item = F::ToolStripMenuItem.new(text)
      item.shortcut_keys = keys if keys
      item.shortcut_key_display_string = display if display
      item.click { action.call } if action
      item
    end

    def menu(text, *items)
      menu = F::ToolStripMenuItem.new(text)
      items.each { menu.drop_down_items.add(it == :separator ? F::ToolStripSeparator.new : it) }
      menu
    end

    # A menu of choices with a check at the current one; a status-bar button and a
    # Format submenu each get their own.
    def choices(owner, names, current, &choose)
      items = names.map do |key, name|
        item = F::ToolStripMenuItem.new(name)
        item.click { choose.call(key) }
        owner.drop_down_items.add(item)
        [key, item]
      end
      owner.drop_down_opening { items.each { |key, item| item.checked = key == current.call } }
      owner
    end

    def build_menu
      ctrl = K.Control
      file = menu("&File",
                  command("&New", ctrl | K.N) { new_document },
                  command("New &Window", ctrl | K.Shift | K.N) { @app.open_window(source: self) },
                  command("&Open...", ctrl | K.O) { open_dialog },
                  command("&Save", ctrl | K.S) { save },
                  command("Save &As...", ctrl | K.Shift | K.S) { save_as },
                  :separator,
                  command("Page Set&up...") { guarded { @printer.page_setup(@form) } },
                  command("&Print...", ctrl | K.P) { guarded { @printer.print_text(@form, @editor.text, @editor.font, display_name) } },
                  :separator,
                  command("E&xit") { @form.close })

      box = @editor.box
      @undo = command("&Undo", ctrl | K.Z) { box.undo }
      @redo = command("&Redo", ctrl | K.Y) { box.redo }
      @cut = command("Cu&t", ctrl | K.X) { box.cut }
      @copy = command("&Copy", ctrl | K.C) { box.copy }
      @paste = command("&Paste", ctrl | K.V) { box.paste }
      @delete = command("De&lete", display: "Del") { @editor.delete }
      @find = command("&Find...", ctrl | K.F) { show_find }
      @find_next = command("Find &Next", K.F3) { find_next }
      @find_previous = command("Find Pre&vious", K.Shift | K.F3) { find_next(up: true) }
      @replace = command("&Replace...", ctrl | K.H) { show_replace }
      @go_to = command("&Go To...", ctrl | K.G) { go_to }
      @edit_commands = [@undo, @redo, @cut, @copy, @paste, @delete, @find, @find_next, @find_previous, @replace, @go_to]
      edit = menu("&Edit", @undo, @redo, :separator, @cut, @copy, @paste, @delete, :separator,
                  @find, @find_next, @find_previous, @replace, @go_to, :separator,
                  command("Select &All", ctrl | K.A) { box.select_all },
                  command("Time/&Date", K.F5) { insert_time_date })
      edit.drop_down_opening { update_edit_menu }
      # Shortcuts of disabled items do nothing, so they are enabled again on closing.
      edit.drop_down_closed { @edit_commands.each { it.enabled = true } }

      @word_wrap_item = command("&Word Wrap") { toggle_word_wrap }
      encodings = choices(F::ToolStripMenuItem.new("&Encoding"), TextFile::ENCODINGS, -> { @encoding }) { set_encoding(it) }
      endings = choices(F::ToolStripMenuItem.new("&Line Endings"), TextFile::LINE_ENDINGS, -> { @eol }) { set_line_ending(it) }
      format = menu("F&ormat", @word_wrap_item, command("&Font...") { choose_font }, :separator, encodings, endings)

      zoom = menu("&Zoom",
                  command("Zoom &In", ctrl | K.Oemplus, display: "Ctrl+Plus") { zoom(:in) },
                  command("Zoom &Out", ctrl | K.OemMinus, display: "Ctrl+Minus") { zoom(:out) },
                  command("&Restore Default Zoom", ctrl | K.D0, display: "Ctrl+0") { zoom(:reset) })
      @status_bar_item = command("&Status Bar") { toggle_status_bar }
      view = menu("&View", zoom, @status_bar_item)
      help = menu("&Help", command("&About #{APP_NAME}") { about })

      @menu = F::MenuStrip.new
      [file, edit, format, view, help].each { @menu.items.add(it) }
    end

    def build_status_bar
      @status = F::StatusStrip.new
      spring = F::ToolStripStatusLabel.new
      spring.spring = true
      @position = status_label
      @characters = status_label
      @zoom_label = status_label
      @eol_button = choices(status_button, TextFile::LINE_ENDINGS, -> { @eol }) { set_line_ending(it) }
      @encoding_button = choices(status_button, TextFile::ENCODINGS, -> { @encoding }) { set_encoding(it) }
      @eol_button.tool_tip_text = "Line endings"
      @encoding_button.tool_tip_text = "Encoding"
      [spring, @position, @characters, @zoom_label, @eol_button, @encoding_button].each_with_index do |item, i|
        @status.items.add(F::ToolStripSeparator.new) if i.positive?
        @status.items.add(item)
      end
    end

    def status_label
      label = F::ToolStripStatusLabel.new
      label.auto_size = false
      label.text_align = D::ContentAlignment.MiddleLeft
      label
    end

    def status_button
      button = F::ToolStripDropDownButton.new
      button.show_drop_down_arrow = false
      button.auto_size = false
      button.display_style = F::ToolStripItemDisplayStyle.Text
      button.text_align = D::ContentAlignment.MiddleLeft
      button
    end

    def status_widths
      scale = @form.device_dpi / 96.0
      [[@position, 130], [@characters, 150], [@zoom_label, 50], [@eol_button, 120], [@encoding_button, 120]].each do |item, width|
        item.width = (width * scale).round
      end
    end

    def build_context_menu
      box = @editor.box
      menu = F::ContextMenuStrip.new
      undo = command("&Undo") { box.undo }
      cut = command("Cu&t") { box.cut }
      copy = command("&Copy") { box.copy }
      paste = command("&Paste") { box.paste }
      delete = command("&Delete") { @editor.delete }
      select_all = command("Select &All") { box.select_all }
      [undo, :separator, cut, copy, paste, delete, :separator, select_all].each do
        menu.items.add(it == :separator ? F::ToolStripSeparator.new : it)
      end
      menu.opening do
        selected = box.selection_length.positive?
        undo.enabled = box.can_undo
        cut.enabled = copy.enabled = delete.enabled = selected
        paste.enabled = clipboard_has_text?
        select_all.enabled = box.text_length.positive?
      end
      box.context_menu_strip = menu
    end

    def apply_settings(cascade_from)
      s = @settings
      style = D::FontStyle.Regular
      style |= D::FontStyle.Bold if s["bold"]
      style |= D::FontStyle.Italic if s["italic"]
      @editor.font = D::Font.new(s["font_family"].to_s, s["font_size"].to_f, style)
      @editor.wrap = s["word_wrap"] ? true : false
      @word_wrap_item.checked = @editor.wrap
      @status_bar = s["status_bar"] ? true : false
      @status.visible = @status_bar
      @status_bar_item.checked = @status_bar

      scale = @form.device_dpi / 96.0
      @form.size = D::Size.new((900 * scale).round, (640 * scale).round)
      bounds = s["window"]
      if cascade_from
        @form.start_position = F::FormStartPosition.Manual
        offset = (30 * scale).round
        @form.bounds = cascade_from.form.bounds
        @form.location = D::Point.new(@form.left + offset, @form.top + offset)
      elsif bounds.is_a?(Array) && bounds.length == 4 && on_screen?(D::Rectangle.new(*bounds.map(&:to_i)))
        @form.start_position = F::FormStartPosition.Manual
        @form.bounds = D::Rectangle.new(*bounds.map(&:to_i))
        @form.window_state = F::FormWindowState.Maximized if s["maximized"]
      else
        @form.start_position = F::FormStartPosition.WindowsDefaultLocation
      end
    end

    def on_screen?(rect)
      F::Screen.all_screens.any? { it.working_area.intersects_with(rect) }
    end

    def wire
      box = @editor.box
      @status_timer = F::Timer.new
      @status_timer.interval = 40
      @status_timer.tick do
        @status_timer.stop
        update_status
      end
      box.selection_changed { schedule_status }
      box.text_changed do
        update_title
        schedule_status
      end
      box.modified_changed { update_title }
      box.drag_enter { |_, e| drag_enter(e) }
      box.drag_drop { |_, e| drag_drop(e) }
      @form.handle_created { status_widths }
      @form.Load { @editor.apply_view }
      @form.dpi_changed { status_widths }
      @form.shown { box.focus }
      @form.form_closing { |_, e| e.cancel = true unless confirm_discard }
      @form.form_closed { @app.window_closed(self) }
    end

    def schedule_status
      @status_timer.stop
      @status_timer.start
    end

    def update_title
      title = "#{@editor.modified ? '*' : ''}#{display_name} - #{APP_NAME}"
      @form.text = title unless @form.text.to_s == title
    end

    def update_status
      line, column = @editor.line_and_column
      @position.text = "Ln #{line}, Col #{column}"
      total = @editor.length
      selected = @editor.box.selection_length
      count = ->(n) { System::String.format("{0:N0}", n).to_s }
      @characters.text =
        if selected.positive? then "#{count[selected]} of #{count[total]} characters"
        else "#{count[total]} character#{total == 1 ? '' : 's'}"
        end
      @zoom_label.text = "#{(@editor.zoom * 100).round}%"
      @eol_button.text = TextFile::LINE_ENDINGS[@eol]
      @encoding_button.text = TextFile::ENCODINGS[@encoding]
    end

    def update_edit_menu
      box = @editor.box
      selected = box.selection_length.positive?
      text = box.text_length.positive?
      @undo.enabled = box.can_undo
      @redo.enabled = box.can_redo
      @cut.enabled = @copy.enabled = @delete.enabled = selected
      @paste.enabled = clipboard_has_text?
      [@find, @find_next, @find_previous, @replace].each { it.enabled = text }
    end

    def clipboard_has_text?
      F::Clipboard.contains_text
    rescue Exception # another program holds the clipboard open
      true
    end

    def new_document
      return unless confirm_discard
      @editor.load_text("")
      @path = nil
      @encoding = :utf8
      @eol = :crlf
      update_title
      update_status
    end

    def open_dialog
      return unless confirm_discard
      dialog = F::OpenFileDialog.new
      dialog.filter = FILTER
      dialog.initial_directory = File.dirname(@path) if @path
      open_path(dialog.file_name.to_s) if dialog.show_dialog(@form) == F::DialogResult.OK
    end

    def lossy_save_confirmed?
      text = "This file contains characters in Unicode format which will be lost if you save this file " \
             "as an #{TextFile::ENCODINGS[@encoding]} encoded text file. To keep the Unicode information, " \
             "click Cancel and then choose one of the Unicode options from the encoding on the status bar.\n\n" \
             "Continue?"
      F::MessageBox.show(@form, text, APP_NAME, F::MessageBoxButtons.OKCancel, F::MessageBoxIcon.Warning) ==
        F::DialogResult.OK
    end

    def show_find
      @replace_dialog&.form&.hide
      (@find_dialog ||= FindDialog.new(self, replace: false)).show
    end

    def show_replace
      @find_dialog&.form&.hide
      (@replace_dialog ||= FindDialog.new(self, replace: true)).show
    end

    def cannot_find(owner, what)
      F::MessageBox.show(owner, "Cannot find \"#{what}\"", APP_NAME, F::MessageBoxButtons.OK, F::MessageBoxIcon.Information)
    end

    def go_to
      line = GoToDialog.ask(@form, @editor.line_and_column.first, @editor.line_count)
      @editor.go_to_line(line) if line
    end

    def insert_time_date
      now = System::DateTime.now
      @editor.insert("#{now.to_short_time_string} #{now.to_short_date_string}")
    end

    def toggle_word_wrap
      @editor.wrap = !@editor.wrap
      @word_wrap_item.checked = @editor.wrap
      update_status
    end

    def toggle_status_bar
      @status_bar = !@status_bar
      @status.visible = @status_bar
      @status_bar_item.checked = @status_bar
    end

    def choose_font
      dialog = F::FontDialog.new
      dialog.font = @editor.font
      dialog.show_effects = false
      dialog.font_must_exist = true
      dialog.allow_vertical_fonts = false
      @editor.font = dialog.font if dialog.show_dialog(@form) == F::DialogResult.OK
    end

    def about
      lines = File.foreach(__FILE__).count
      @prompt = F::TaskDialogPage.new
      @prompt.caption = "About #{APP_NAME}"
      @prompt.heading = APP_NAME
      @prompt.text = "Notepad in #{lines} lines of Ruby, on IronRuby and Windows Forms.\n\n#{RUBY_DESCRIPTION}"
      @prompt.icon = F::TaskDialogIcon.information
      F::TaskDialog.show_dialog(@form, @prompt)
    end

    def drag_enter(e)
      e.effect =
        if e.data.get_data_present(F::DataFormats.FileDrop) || e.data.get_data_present(F::DataFormats.UnicodeText)
          F::DragDropEffects.Copy
        else
          F::DragDropEffects.None
        end
    end

    def drag_drop(e)
      if e.data.get_data_present(F::DataFormats.FileDrop)
        files = e.data.get_data(F::DataFormats.FileDrop)
        @form.activate
        open_path(files[0].to_s) if files.length.positive? && confirm_discard
      else
        box = @editor.box
        box.select(box.get_char_index_from_position(box.point_to_client(D::Point.new(e.x, e.y))), 0)
        @editor.insert(e.data.get_data(F::DataFormats.UnicodeText).to_s)
      end
    end

    # Runs the block; an exception becomes an error box. => true when it went through.
    def guarded
      yield
      true
    rescue Exception => e # .NET's exceptions are not all StandardErrors
      F::MessageBox.show(@form, e.message.to_s, APP_NAME, F::MessageBoxButtons.OK, F::MessageBoxIcon.Error)
      false
    end
  end

  class App
    attr_reader :settings, :search, :icon, :context

    def initialize(settings)
      @settings = settings
      @search = { text: "", replace: "", match_case: settings["match_case"], wrap: settings["wrap_around"], up: false }
      @icon = App.icon
      @windows = []
      @context = F::ApplicationContext.new
    end

    def open_window(path = nil, source: nil)
      window = Window.new(self, cascade_from: source&.form&.visible ? source : nil)
      @windows << window
      window.form.show
      window.open_from_command_line(path) if path
      window
    end

    def window_closed(window)
      @windows.delete(window)
      window.store_settings
      @settings["match_case"] = @search[:match_case]
      @settings["wrap_around"] = @search[:wrap]
      @settings.save
      @context.exit_thread if @windows.empty?
    end

    # A page with a blue top edge and four lines of text, drawn rather than shipped.
    def self.icon
      bitmap = D::Bitmap.new(64, 64)
      g = D::Graphics.from_image(bitmap)
      g.smoothing_mode = D::Drawing2D::SmoothingMode.AntiAlias
      g.fill_rectangle(D::Brushes.White, 10, 5, 44, 55)
      g.draw_rectangle(D::Pen.new(D::Color.from_argb(255, 110, 110, 110), 2), 10, 5, 44, 55)
      g.fill_rectangle(D::SolidBrush.new(D::Color.from_argb(255, 15, 100, 167)), 10, 5, 45, 11)
      lines = D::Pen.new(D::Color.from_argb(255, 160, 160, 160), 3)
      [26, 35, 44].each { g.draw_line(lines, 17, it, 47, it) }
      g.draw_line(lines, 17, 53, 35, 53)
      g.dispose
      D::Icon.from_handle(bitmap.get_hicon)
    end
  end

  # What has to happen before the first window exists.
  def self.prepare
    F::Application.set_high_dpi_mode(F::HighDpiMode.PerMonitorV2)
    F::Application.enable_visual_styles
    F::Application.set_compatible_text_rendering_default(false)
    F::Application.set_unhandled_exception_mode(F::UnhandledExceptionMode.CatchException)
    F::Application.thread_exception do |_, e|
      error = e.exception
      trace = error.respond_to?(:backtrace) && error.backtrace ? error.backtrace.first(10).join("\n") : error.stack_trace.to_s
      F::MessageBox.show("#{error.message}\n\n#{trace}", "#{APP_NAME} - Error",
                         F::MessageBoxButtons.OK, F::MessageBoxIcon.Error)
    end
  end

  def self.main(args)
    prepare
    Native.detach_console_if_alone
    app = App.new(Settings.new)
    app.open_window(args.first)
    F::Application.run(app.context)
  end
end

IronRubyNotepad.main(ARGV) if $PROGRAM_NAME == __FILE__
