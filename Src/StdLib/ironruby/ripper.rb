# Ripper on top of prism - the same arrangement CRuby 4.0 uses, where Ripper is
# Prism::Translation::Ripper. The two primitives come from Src/Libraries/Ripper/
# RipperOps.cs: Ripper.__lex__ (prism's token stream with MRI lexer states) and
# Ripper.__parse__ (prism's syntax tree as plain hashes).
#
# The prism token type -> Ripper event table below is taken from ruby/prism's
# lib/prism/lex_compat.rb, which is distributed under the MIT license:
#
#   Copyright (c) Shopify
#
#   Permission is hereby granted, free of charge, to any person obtaining a copy
#   of this software and associated documentation files (the "Software"), to deal
#   in the Software without restriction, including without limitation the rights
#   to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
#   copies of the Software, and to permit persons to whom the Software is
#   furnished to do so, subject to the following conditions:
#
#   The above copyright notice and this permission notice shall be included in
#   all copies or substantial portions of the Software.

load_assembly 'IronRuby.Libraries', 'IronRuby.StandardLibrary.Ripper'

class Ripper
  EXPR_NONE    = 0
  EXPR_BEG     = 1 << 0
  EXPR_END     = 1 << 1
  EXPR_ENDARG  = 1 << 2
  EXPR_ENDFN   = 1 << 3
  EXPR_ARG     = 1 << 4
  EXPR_CMDARG  = 1 << 5
  EXPR_MID     = 1 << 6
  EXPR_FNAME   = 1 << 7
  EXPR_DOT     = 1 << 8
  EXPR_CLASS   = 1 << 9
  EXPR_LABEL   = 1 << 10
  EXPR_LABELED = 1 << 11
  EXPR_FITEM   = 1 << 12
  EXPR_VALUE   = EXPR_BEG
  EXPR_BEG_ANY = EXPR_BEG | EXPR_MID | EXPR_CLASS
  EXPR_ARG_ANY = EXPR_ARG | EXPR_CMDARG
  EXPR_END_ANY = EXPR_END | EXPR_ENDARG | EXPR_ENDFN

  # Bit order is the order MRI's rb_parser_lex_state_name joins the names in.
  LEX_STATE_NAMES = [
    [EXPR_BEG,     'BEG'],
    [EXPR_END,     'END'],
    [EXPR_ENDARG,  'ENDARG'],
    [EXPR_ENDFN,   'ENDFN'],
    [EXPR_ARG,     'ARG'],
    [EXPR_CMDARG,  'CMDARG'],
    [EXPR_MID,     'MID'],
    [EXPR_FNAME,   'FNAME'],
    [EXPR_DOT,     'DOT'],
    [EXPR_CLASS,   'CLASS'],
    [EXPR_LABEL,   'LABEL'],
    [EXPR_LABELED, 'LABELED'],
    [EXPR_FITEM,   'FITEM'],
  ].freeze

  def self.lex_state_name(state)
    names = LEX_STATE_NAMES.select { |bit, _| (state & bit) != 0 }.map { |_, name| name }
    names.empty? ? 'NONE' : names.join('|')
  end

  # prism token type => Ripper scanner event (from ruby/prism's lex_compat.rb).
  RIPPER_EVENTS = {
    'AMPERSAND' => :on_op,
    'AMPERSAND_AMPERSAND' => :on_op,
    'AMPERSAND_AMPERSAND_EQUAL' => :on_op,
    'AMPERSAND_DOT' => :on_op,
    'AMPERSAND_EQUAL' => :on_op,
    'BACK_REFERENCE' => :on_backref,
    'BACKTICK' => :on_backtick,
    'BANG' => :on_op,
    'BANG_EQUAL' => :on_op,
    'BANG_TILDE' => :on_op,
    'BRACE_LEFT' => :on_lbrace,
    'BRACE_LEFT_ARGUMENT' => :on_lbrace,
    'BRACE_LEFT_HASH' => :on_lbrace,
    'BRACE_RIGHT' => :on_rbrace,
    'BRACKET_LEFT' => :on_lbracket,
    'BRACKET_LEFT_ARRAY' => :on_lbracket,
    'BRACKET_LEFT_RIGHT' => :on_op,
    'BRACKET_LEFT_RIGHT_EQUAL' => :on_op,
    'BRACKET_RIGHT' => :on_rbracket,
    'CARET' => :on_op,
    'CARET_EQUAL' => :on_op,
    'CHARACTER_LITERAL' => :on_CHAR,
    'CLASS_VARIABLE' => :on_cvar,
    'COLON' => :on_op,
    'COLON_COLON' => :on_op,
    'COMMA' => :on_comma,
    'COMMENT' => :on_comment,
    'CONSTANT' => :on_const,
    'DOT' => :on_period,
    'DOT_DOT' => :on_op,
    'DOT_DOT_DOT' => :on_op,
    'EMBDOC_BEGIN' => :on_embdoc_beg,
    'EMBDOC_END' => :on_embdoc_end,
    'EMBDOC_LINE' => :on_embdoc,
    'EMBEXPR_BEGIN' => :on_embexpr_beg,
    'EMBEXPR_END' => :on_embexpr_end,
    'EMBVAR' => :on_embvar,
    'EOF' => :on_eof,
    'EQUAL' => :on_op,
    'EQUAL_EQUAL' => :on_op,
    'EQUAL_EQUAL_EQUAL' => :on_op,
    'EQUAL_GREATER' => :on_op,
    'EQUAL_TILDE' => :on_op,
    'FLOAT' => :on_float,
    'FLOAT_IMAGINARY' => :on_imaginary,
    'FLOAT_RATIONAL' => :on_rational,
    'FLOAT_RATIONAL_IMAGINARY' => :on_imaginary,
    'GREATER' => :on_op,
    'GREATER_EQUAL' => :on_op,
    'GREATER_GREATER' => :on_op,
    'GREATER_GREATER_EQUAL' => :on_op,
    'GLOBAL_VARIABLE' => :on_gvar,
    'HEREDOC_END' => :on_heredoc_end,
    'HEREDOC_START' => :on_heredoc_beg,
    'IDENTIFIER' => :on_ident,
    'IGNORED_NEWLINE' => :on_ignored_nl,
    'INTEGER' => :on_int,
    'INTEGER_IMAGINARY' => :on_imaginary,
    'INTEGER_RATIONAL' => :on_rational,
    'INTEGER_RATIONAL_IMAGINARY' => :on_imaginary,
    'INSTANCE_VARIABLE' => :on_ivar,
    'INVALID' => :INVALID,
    'KEYWORD___ENCODING__' => :on_kw,
    'KEYWORD___LINE__' => :on_kw,
    'KEYWORD___FILE__' => :on_kw,
    'KEYWORD_ALIAS' => :on_kw,
    'KEYWORD_AND' => :on_kw,
    'KEYWORD_BEGIN' => :on_kw,
    'KEYWORD_BEGIN_UPCASE' => :on_kw,
    'KEYWORD_BREAK' => :on_kw,
    'KEYWORD_CASE' => :on_kw,
    'KEYWORD_CLASS' => :on_kw,
    'KEYWORD_DEF' => :on_kw,
    'KEYWORD_DEFINED' => :on_kw,
    'KEYWORD_DO' => :on_kw,
    'KEYWORD_DO_BLOCK' => :on_kw,
    'KEYWORD_DO_LAMBDA' => :on_kw,
    'KEYWORD_DO_LOOP' => :on_kw,
    'KEYWORD_ELSE' => :on_kw,
    'KEYWORD_ELSIF' => :on_kw,
    'KEYWORD_END' => :on_kw,
    'KEYWORD_END_UPCASE' => :on_kw,
    'KEYWORD_ENSURE' => :on_kw,
    'KEYWORD_FALSE' => :on_kw,
    'KEYWORD_FOR' => :on_kw,
    'KEYWORD_IF' => :on_kw,
    'KEYWORD_IF_MODIFIER' => :on_kw,
    'KEYWORD_IN' => :on_kw,
    'KEYWORD_MODULE' => :on_kw,
    'KEYWORD_NEXT' => :on_kw,
    'KEYWORD_NIL' => :on_kw,
    'KEYWORD_NOT' => :on_kw,
    'KEYWORD_OR' => :on_kw,
    'KEYWORD_REDO' => :on_kw,
    'KEYWORD_RESCUE' => :on_kw,
    'KEYWORD_RESCUE_MODIFIER' => :on_kw,
    'KEYWORD_RETRY' => :on_kw,
    'KEYWORD_RETURN' => :on_kw,
    'KEYWORD_SELF' => :on_kw,
    'KEYWORD_SUPER' => :on_kw,
    'KEYWORD_THEN' => :on_kw,
    'KEYWORD_TRUE' => :on_kw,
    'KEYWORD_UNDEF' => :on_kw,
    'KEYWORD_UNLESS' => :on_kw,
    'KEYWORD_UNLESS_MODIFIER' => :on_kw,
    'KEYWORD_UNTIL' => :on_kw,
    'KEYWORD_UNTIL_MODIFIER' => :on_kw,
    'KEYWORD_WHEN' => :on_kw,
    'KEYWORD_WHILE' => :on_kw,
    'KEYWORD_WHILE_MODIFIER' => :on_kw,
    'KEYWORD_YIELD' => :on_kw,
    'LABEL' => :on_label,
    'LABEL_END' => :on_label_end,
    'LAMBDA_BEGIN' => :on_tlambeg,
    'LESS' => :on_op,
    'LESS_EQUAL' => :on_op,
    'LESS_EQUAL_GREATER' => :on_op,
    'LESS_LESS' => :on_op,
    'LESS_LESS_EQUAL' => :on_op,
    'METHOD_NAME' => :on_ident,
    'MINUS' => :on_op,
    'MINUS_EQUAL' => :on_op,
    'MINUS_GREATER' => :on_tlambda,
    'NEWLINE' => :on_nl,
    'NUMBERED_REFERENCE' => :on_backref,
    'PARENTHESIS_LEFT' => :on_lparen,
    'PARENTHESIS_LEFT_GROUPING' => :on_lparen,
    'PARENTHESIS_LEFT_PARENTHESES' => :on_lparen,
    'PARENTHESIS_RIGHT' => :on_rparen,
    'PERCENT' => :on_op,
    'PERCENT_EQUAL' => :on_op,
    'PERCENT_LOWER_I' => :on_qsymbols_beg,
    'PERCENT_LOWER_W' => :on_qwords_beg,
    'PERCENT_LOWER_X' => :on_backtick,
    'PERCENT_UPPER_I' => :on_symbols_beg,
    'PERCENT_UPPER_W' => :on_words_beg,
    'PIPE' => :on_op,
    'PIPE_EQUAL' => :on_op,
    'PIPE_PIPE' => :on_op,
    'PIPE_PIPE_EQUAL' => :on_op,
    'PLUS' => :on_op,
    'PLUS_EQUAL' => :on_op,
    'QUESTION_MARK' => :on_op,
    'RATIONAL_FLOAT' => :on_rational,
    'RATIONAL_INTEGER' => :on_rational,
    'REGEXP_BEGIN' => :on_regexp_beg,
    'REGEXP_END' => :on_regexp_end,
    'SEMICOLON' => :on_semicolon,
    'SLASH' => :on_op,
    'SLASH_EQUAL' => :on_op,
    'STAR' => :on_op,
    'STAR_EQUAL' => :on_op,
    'STAR_STAR' => :on_op,
    'STAR_STAR_EQUAL' => :on_op,
    'STRING_BEGIN' => :on_tstring_beg,
    'STRING_CONTENT' => :on_tstring_content,
    'STRING_END' => :on_tstring_end,
    'SYMBOL_BEGIN' => :on_symbeg,
    'TILDE' => :on_op,
    'UAMPERSAND' => :on_op,
    'UCOLON_COLON' => :on_op,
    'UDOT_DOT' => :on_op,
    'UDOT_DOT_DOT' => :on_op,
    'UMINUS' => :on_op,
    'UMINUS_NUM' => :on_op,
    'UPLUS' => :on_op,
    'USTAR' => :on_op,
    'USTAR_STAR' => :on_op,
    'WORDS_SEP' => :on_words_sep,
    'XSTRING_BEGIN' => :on_backtick,
    '__END__' => :on___end__,
  }.freeze

  # MRI's Ripper::Lexer is a Ripper subclass; here Ripper is a CLR type and
  # sealed, so Lexer stands on its own and drives Ripper._lex directly.  The
  # only part of the inheritance anyone uses is that a Lexer can be created
  # over a source and scanned, which works the same either way.
  class Lexer # :nodoc: internal use only
    def initialize(src, filename = '-', lineno = 1, **kw)
      @src = src
      @filename = filename
      @lineno = lineno
    end

    attr_reader :src, :filename, :lineno

    # Every token, error tokens included, in source order.
    def scan
      Ripper.send(:_lex, @src, @lineno)
    end

    def lex(**kw)
      scan.map { |elem| elem.to_a }
    end

    def tokenize(**kw)
      scan.map { |elem| elem.tok }
    end

    def parse(**kw)
      scan
    end

    class State
      attr_reader :to_int, :to_s

      def initialize(i)
        @to_int = i
        @to_s = Ripper.lex_state_name(i)
        freeze
      end

      def [](index)
        case index
        when 0, :to_int then @to_int
        when 1, :to_s then @to_s
        else nil
        end
      end

      alias to_i to_int
      alias inspect to_s
      def pretty_print(q) q.text(to_s) end
      def ==(i) super or to_int == i end
      def &(i) self.class.new(to_int & i) end
      def |(i) self.class.new(to_int | i) end
      def allbits?(i) (to_int & i) == i end
      def anybits?(i) (to_int & i) != 0 end
      def nobits?(i) (to_int & i) == 0 end
    end

    class Elem
      attr_accessor :pos, :event, :tok, :state, :message

      def initialize(pos, event, tok, state, message = nil)
        @pos = pos
        @event = event
        @tok = tok
        @state = state.is_a?(State) ? state : State.new(state)
        @message = message
      end

      def [](index)
        case index
        when 0, :pos then @pos
        when 1, :event then @event
        when 2, :tok then @tok
        when 3, :state then @state
        when 4, :message then @message
        else nil
        end
      end

      def inspect
        "#<#{self.class}: #{event}@#{pos[0]}:#{pos[1]}:#{state}: #{tok.inspect}#{": " if message}#{message}>"
      end

      alias to_s inspect

      def to_a
        a = [pos, event, tok, state]
        a << message if message
        a
      end
    end
  end

  # Maps byte offsets in a source string onto [line, column] pairs. Ripper
  # reports columns in bytes, which is also what prism's locations are in.
  class LineMap # :nodoc:
    def initialize(source, start_line)
      @start_line = start_line
      @offsets = [0]
      offset = 0
      source.each_line { |line| offset += line.bytesize; @offsets << offset }
    end

    def position(offset)
      low = 0
      high = @offsets.size - 1
      while low < high
        mid = (low + high + 1) / 2
        if @offsets[mid] <= offset then low = mid else high = mid - 1 end
      end
      [low + @start_line, offset - @offsets[low]]
    end
  end

  class << self
    # [[[lineno, column], :on_type, token, state], ...]
    def lex(src, filename = '-', lineno = 1, **kw)
      _lex(src, lineno).map { |elem| elem.to_a }
    end

    # The token strings alone, whitespace included.
    def tokenize(src, filename = '-', lineno = 1, **kw)
      _lex(src, lineno).map { |elem| elem.tok }
    end

    # MRI's s-expression, with statement/argument lists flattened into arrays.
    def sexp(src, filename = '-', lineno = 1, raise_errors: false)
      _sexp(src, lineno, false, raise_errors)
    end

    # MRI's s-expression with the raw xxx_new/xxx_add list events kept.
    def sexp_raw(src, filename = '-', lineno = 1, raise_errors: false)
      _sexp(src, lineno, true, raise_errors)
    end

    private

    # Ripper reports its tokens in the encoding the source says it is in, which
    # for a file read as UTF-8 is what its magic comment names.
    MAGIC_ENCODING = /\A[ \t\v]*\#.*coding[:=][ \t]*([A-Za-z0-9_-]+)/

    def with_source_encoding(src)
      lines = src.to_s.lines
      line = lines[0]
      line = lines[1] if line && line.start_with?('#!') && !(MAGIC_ENCODING =~ line)
      match = line && MAGIC_ENCODING.match(line)
      return src unless match
      begin
        src.dup.force_encoding(match[1])
      rescue ArgumentError
        src
      end
    end

    def _sexp(src, lineno, raw, raise_errors)
      src = with_source_encoding(src.to_s)
      tree = __parse__(src)
      if tree.nil?
        raise SyntaxError, 'syntax error' if raise_errors
        return nil
      end
      SexpBuilder.new(src, lineno, raw).build(tree)
    end

    def _lex(src, lineno)
      src = with_source_encoding(src.to_s)
      map = LineMap.new(src, lineno)
      result = []
      previous = Lexer::State.new(EXPR_BEG)
      # MRI reports a comment with the lexer state the scanner had reached, which
      # an ignored newline in between does not change.
      significant = previous
      previous_end = 0
      eof_end = src.bytesize

      skip_newline_at = nil

      # prism lexes a heredoc's body where the heredoc opens, ahead of the rest
      # of that line; Ripper reports every token in source order.
      tokens = []
      __lex__(src).each do |type, start, length, state|
        if type == 'EOF'
          eof_end = start + length
          next
        end
        tokens << [type, start, length, state] unless RIPPER_EVENTS[type].nil?
      end
      dedents = squiggly_dedents(tokens, src, map)
      tokens.sort_by! { |_type, start, _length, _state| start }

      tokens.each do |type, start, length, state|
        event = RIPPER_EVENTS[type]
        next if start < previous_end
        if skip_newline_at == start && (event == :on_nl || event == :on_ignored_nl)
          skip_newline_at = nil
          previous_end = start + length
          next
        end

        value = src.byteslice(start, length)
        if event == :on_comment
          # Ripper's comment token takes the line terminator with it.
          terminator = src.byteslice(start + length, 2)
          if terminator && terminator.start_with?("\r\n")
            value += "\r\n"
            skip_newline_at = start + length
            length += 2
          elsif terminator && terminator.start_with?("\n")
            value += "\n"
            skip_newline_at = start + length
            length += 1
          end
        end

        add_spaces(result, map, src, previous_end, start, previous) if start > previous_end
        current = Lexer::State.new(state)
        # The indentation a squiggly heredoc strips is its own event.
        strip = dedents[start]
        if strip
          result << Lexer::Elem.new(map.position(start), :on_ignored_sp, value[0, strip], current)
          value = value[strip..-1]
          start += strip
          length -= strip
        end
        # MRI scans a regexp's closing delimiter before it moves to EXPR_END, so
        # the state it reports there is still the one from the token before -
        # and after an interpolation, the one the interpolation started in.
        # A heredoc's terminator and a comment are reported with the state that
        # was current when the scanner reached them.
        reported =
          case event
          when :on_regexp_end then state_before_regexp_end(result, previous)
          when :on_heredoc_end then previous
          when :on_comment then significant
          else current
          end
        if event == :on_words_sep && value.include?("\n")
          # Ripper reports one separator per line of a %w/%i list.
          offset = start
          value.each_line do |line|
            result << Lexer::Elem.new(map.position(offset), event, line, reported)
            offset += line.bytesize
          end
        else
          result << Lexer::Elem.new(map.position(start), event, value, reported)
        end
        # A real newline puts MRI's lexer back in EXPR_BEG; an ignored one and a
        # comment leave the state where the last real token left it.
        case event
        when :on_nl then significant = Lexer::State.new(EXPR_BEG)
        when :on_ignored_nl, :on_comment then nil
        else significant = current
        end
        previous = current
        previous_end = start + length
      end

      add_spaces(result, map, src, previous_end, eof_end, previous) if previous_end < eof_end
      result
    end

    # A `<<~` heredoc has its common indentation taken off every line; Ripper
    # reports what was taken as :on_ignored_sp in front of the line's content.
    # prism lists a heredoc's body right after its opening token, so the body
    # is whatever STRING_CONTENT sits between HEREDOC_START and HEREDOC_END.
    def squiggly_dedents(tokens, src, map)
      dedents = {}
      index = 0
      while index < tokens.size
        type, start, length, = tokens[index]
        index += 1
        next unless type == 'HEREDOC_START' && src.byteslice(start, length).start_with?('<<~')

        body = []
        while index < tokens.size && tokens[index][0] != 'HEREDOC_END'
          entry = tokens[index]
          body << entry if entry[0] == 'STRING_CONTENT' && map.position(entry[1])[1] == 0
          index += 1
        end

        width = nil
        body.each do |_type, token_start, token_length, _state|
          value = src.byteslice(token_start, token_length)
          next if value.strip.empty?
          indent = indent_columns(value)
          width = indent if width.nil? || indent < width
        end
        next if width.nil? || width == 0

        body.each do |_type, token_start, token_length, _state|
          taken = dedent_prefix(src.byteslice(token_start, token_length), width)
          dedents[token_start] = taken if taken > 0
        end
      end
      dedents
    end

    def indent_columns(value)
      column = 0
      value.each_char do |char|
        case char
        when ' ' then column += 1
        when "\t" then column = column / 8 * 8 + 8
        else break
        end
      end
      column
    end

    def dedent_prefix(value, width)
      column = 0
      taken = 0
      value.each_char do |char|
        break if column >= width
        case char
        when ' ' then column += 1
        when "\t" then column = column / 8 * 8 + 8
        else break
        end
        taken += char.bytesize
      end
      taken
    end

    def state_before_regexp_end(result, previous)
      return previous if result.empty? || result[-1].event != :on_embexpr_end
      depth = 1
      index = result.size - 1
      while depth > 0 && index > 0
        index -= 1
        case result[index].event
        when :on_embexpr_end then depth += 1
        when :on_embexpr_beg then depth -= 1
        end
      end
      result[index].state
    end

    # Whatever prism skipped between two tokens is whitespace, which Ripper
    # reports as :on_sp. A line continuation is split into up to three of them,
    # as MRI's lexer scans the backslash, the newline and the next line's indent
    # separately.
    def add_spaces(result, map, src, from, to, state)
      value = src.byteslice(from, to - from)
      index = value.index('\\')
      if index
        tail = index + 1
        tail += 1 if value[tail] == "\r"
        tail += 1
        line, column = map.position(from)
        head = value[0...index]
        result << Lexer::Elem.new([line, column], :on_sp, head, state) unless head.empty?
        result << Lexer::Elem.new([line, column + index], :on_sp, value[index...tail], state)
        rest = value[tail..-1] || ''
        result << Lexer::Elem.new([line + 1, 0], :on_sp, rest, state) unless rest.empty?
      else
        result << Lexer::Elem.new(map.position(from), :on_sp, value, state)
      end
    end
  end
end

# Ripper.slice and the token-stream pattern language behind it, ported from
# CRuby's lib/ripper/lexer.rb (Ruby licence, (c) 2004-2005 Minero Aoki).
class Ripper
  SCANNER_EVENTS = [
    :CHAR, :__end__, :backref, :backtick, :comma, :comment, :const, :cvar, :embdoc,
    :embdoc_beg, :embdoc_end, :embexpr_beg, :embexpr_end, :embvar, :float, :gvar,
    :heredoc_beg, :heredoc_end, :ident, :ignored_nl, :imaginary, :int, :ivar, :kw,
    :label, :label_end, :lbrace, :lbracket, :lparen, :nl, :op, :period, :qsymbols_beg,
    :qwords_beg, :rational, :rbrace, :rbracket, :regexp_beg, :regexp_end, :rparen,
    :semicolon, :sp, :symbeg, :symbols_beg, :tlambda, :tlambeg, :tstring_beg,
    :tstring_content, :tstring_end, :words_beg, :words_sep, :ignored_sp,
  ].freeze

  SCANNER_EVENT_TABLE = SCANNER_EVENTS.each_with_object({}) { |event, table| table[event] = 1 }.freeze

  # The first match of +pattern+ in +src+, as the source text it covers.
  def self.slice(src, pattern, n = 0)
    match = token_match(src, pattern)
    match ? match.string(n) : nil
  end

  def self.token_match(src, pattern) # :nodoc:
    TokenPattern.compile(pattern).match(src)
  end

  class TokenPattern # :nodoc:
    class Error < ::StandardError; end
    class CompileError < Error; end
    class MatchError < Error; end

    class << self
      alias compile new
    end

    def initialize(pattern)
      @source = pattern
      @re = compile(pattern)
    end

    def match(str)
      match_list(::Ripper.lex(str))
    end

    def match_list(tokens)
      m = @re.match(map_tokens(tokens))
      m ? MatchData.new(tokens, m) : nil
    end

    private

    def compile(pattern)
      if (m = /[^\w\s$()\[\]{}?*+\.]/.match(pattern))
        raise CompileError, "invalid char in pattern: #{m[0].inspect}"
      end
      buf = +''
      pattern.scan(/(?:\w+|\$\(|[()\[\]\{\}?*+\.]+)/) do |token|
        case token
        when /\w/ then buf.concat(map_token(token))
        when '$(' then buf.concat('(')
        when '(' then buf.concat('(?:')
        when /[?*\[\])\.]/ then buf.concat(token)
        else raise 'must not happen'
        end
      end
      Regexp.compile(buf)
    rescue RegexpError => e
      raise CompileError, e.message
    end

    def map_tokens(tokens)
      tokens.map { |_pos, type, _str| map_token(type.to_s.delete_prefix('on_')) }.join
    end

    MAP = {}
    seed = ('a'..'z').to_a + ('A'..'Z').to_a + ('0'..'9').to_a
    SCANNER_EVENTS.each { |event| MAP[event.to_s] = seed.shift }
    MAP.freeze

    def map_token(token)
      MAP[token] or raise CompileError, "unknown token: #{token}"
    end

    class MatchData # :nodoc:
      def initialize(tokens, match)
        @tokens = tokens
        @match = match
      end

      def string(n = 0)
        return nil unless @match
        match(n).join
      end

      private

      def match(n = 0)
        return [] unless @match
        @tokens[@match.begin(n)...@match.end(n)].map { |_pos, _type, str| str }
      end
    end
  end
end

require 'ripper/sexp_builder'
# MRI's ripper.rb requires ripper/filter too; rdoc's Ruby parser subclasses Ripper::Filter.
# An autoload, as filter.rb requires ripper/lexer, which here is this file: required from
# here it would be a circular require.
Ripper.autoload :Filter, 'ripper/filter'
