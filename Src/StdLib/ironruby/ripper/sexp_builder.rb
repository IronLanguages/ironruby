# Builds MRI's Ripper s-expressions out of prism's tree (Ripper.__parse__).
#
# Two shapes exist: Ripper.sexp_raw keeps the xxx_new/xxx_add events a list is
# built from, Ripper.sexp flattens each list into an Array. Both come out of the
# same walk - #list is the only place that knows which of the two is wanted.
#
# MRI puts a leading [:void_stmt] in front of a body whose opening is followed
# by a *significant* terminator - `;` always, a newline only where the lexer
# does not swallow it. prism draws exactly that distinction (NEWLINE vs
# IGNORED_NEWLINE), so the token stream answers the question directly.

class Ripper
  class SexpBuilder # :nodoc:
    CALL_SAFE_NAVIGATION = 1 << 2
    CALL_VARIABLE        = 1 << 3
    CALL_ATTRIBUTE_WRITE = 1 << 4
    RANGE_EXCLUDE_END    = 1 << 2

    UNARY_OPERATORS = { '!' => :!, '-@' => :-@, '+@' => :+@, '~' => :~ }.freeze

    def initialize(source, lineno, raw)
      @src = source
      @raw = raw
      @map = LineMap.new(source, lineno)
      @terms = []
      Ripper.__lex__(source).each do |type, start, _length, _state|
        @terms << start if type == 'NEWLINE' || type == 'SEMICOLON'
      end
    end

    def build(node)
      visit(node)
    end

    private

    # ---- primitives ------------------------------------------------------

    def visit(node)
      return nil if node.nil?
      name = :"visit_#{node['type']}"
      raise NotImplementedError, "Ripper: no s-expression for prism #{node['type']}" unless respond_to?(name, true)
      send(name, node)
    end

    def pos(offset)
      @map.position(offset)
    end

    def text(location)
      @src.byteslice(location[0], location[1])
    end

    def tok(type, location)
      [type, text(location), pos(location[0])]
    end

    def loc_end(location)
      location[0] + location[1]
    end

    def node_end(node)
      node['start'] + node['length']
    end

    def node_loc(node)
      [node['start'], node['length']]
    end

    KEYWORDS = %w[
      __ENCODING__ __LINE__ __FILE__ BEGIN END alias and begin break case class def defined?
      do else elsif end ensure false for if in module next nil not or redo rescue retry return
      self super then true undef unless until when while yield
    ].freeze

    # As name_token, but in one of the places the lexer is in EXPR_FNAME - a
    # symbol, an alias or undef name, a `def` name - where a keyword is still
    # scanned as a keyword rather than as a method name.
    def fname_token(location)
      value = text(location)
      return [:@kw, value, pos(location[0])] if KEYWORDS.include?(value)
      name_token(location)
    end

    # An identifier-shaped name, classified the way Ripper's scanner would.
    def name_token(location)
      value = text(location)
      type =
        case value
        when /\A[A-Z]/ then :@const
        when /\A@@/ then :@cvar
        when /\A@/ then :@ivar
        when /\A\$/ then :@gvar
        when '`' then :@backtick
        when /\A[a-z_]/ then :@ident
        else :@op
        end
      [type, value, pos(location[0])]
    end

    # xxx_new/xxx_add (raw) or a flat Array (pretty). Entries are [:add, value]
    # or [:add_star, value]; a star switches the list over to xxx_add_star,
    # which later entries are appended to exactly as MRI does.
    def list(kind, entries)
      accumulator = kind == :string ? [:string_content] : (@raw ? [:"#{kind}_new"] : [])
      entries.each do |operation, value|
        if operation == :add_star
          accumulator = [:"#{kind}_add_star", accumulator, value]
        elsif @raw
          accumulator = [:"#{kind}_add", accumulator, value]
        else
          accumulator = accumulator + [value]
        end
      end
      accumulator
    end

    # ---- statements ------------------------------------------------------

    def statement_list(statements, leading_void = false)
      body = statements ? statements['body'] : []
      entries = []
      entries << [:add, [:void_stmt]] if leading_void || body.empty?
      body.each { |statement| entries << [:add, visit(statement)] }
      list(:stmts, entries)
    end

    # True when a significant terminator sits between the end of a construct's
    # header and its first statement. +skip+ swallows the terminators the
    # grammar itself consumes (a paren-less def's `f_arglist term`).
    def leading_void?(from, statements, skip = 0)
      return false if statements.nil? || statements['body'].empty?
      to = statements['body'][0]['start']
      @terms.count { |offset| offset >= from && offset < to } > skip
    end

    def body_statements(body)
      body && body['type'] == 'BeginNode' ? body['statements'] : body
    end

    def bodystmt(body, leading_void)
      if body && body['type'] == 'BeginNode'
        [:bodystmt,
         statement_list(body['statements'], leading_void),
         rescue_clause(body['rescue_clause']),
         else_clause(body['else_clause']),
         ensure_clause(body['ensure_clause'])]
      else
        [:bodystmt, statement_list(body, leading_void), nil, nil, nil]
      end
    end

    def rescue_clause(node)
      return nil unless node
      exceptions = node['exceptions']
      caught =
        if exceptions.empty?
          nil
        elsif exceptions.size == 1 && exceptions[0]['type'] != 'SplatNode'
          [visit(exceptions[0])]
        else
          mrhs_list(exceptions)
        end
      # `k_rescue exc_list exc_var then compstmt`: the terminator after the
      # exception list belongs to `then`, so it does not open the body.
      from = loc_end(node['keyword_loc'])
      skip = node['then_keyword_loc'] ? 0 : 1
      [:rescue,
       caught,
       node['reference'] ? target(node['reference']) : nil,
       statement_list(node['statements'], leading_void?(from, node['statements'], skip)),
       rescue_clause(node['subsequent'])]
    end

    def else_clause(node)
      return nil unless node
      statement_list(node['statements'], leading_void?(loc_end(node['else_keyword_loc']), node['statements']))
    end

    def ensure_clause(node)
      return nil unless node
      [:ensure, statement_list(node['statements'],
                               leading_void?(loc_end(node['ensure_keyword_loc']), node['statements']))]
    end

    def visit_ProgramNode(node)
      [:program, statement_list(node['statements'])]
    end

    def visit_StatementsNode(node)
      statement_list(node)
    end

    def visit_ParenthesesNode(node)
      body = node['body']
      if body && body['type'] == 'BeginNode' &&
         (body['rescue_clause'] || body['ensure_clause'] || body['else_clause'])
        [:paren, bodystmt(body, false)]
      else
        [:paren, statement_list(body_statements(body))]
      end
    end

    # ---- literals --------------------------------------------------------

    def visit_IntegerNode(node) numeric(:@int, node) end
    def visit_FloatNode(node) numeric(:@float, node) end
    def visit_RationalNode(node) numeric(:@rational, node) end
    def visit_ImaginaryNode(node) numeric(:@imaginary, node) end

    # prism folds a literal's sign into the literal; Ripper keeps the minus as
    # the unary operator it was scanned as (a plus stays part of the number).
    def numeric(event, node)
      value = text(node_loc(node))
      return [event, value, pos(node['start'])] unless value.start_with?('-')
      [:unary, :-@, [event, value[1..-1], pos(node['start'] + 1)]]
    end

    def visit_TrueNode(node) keyword_ref(node) end
    def visit_FalseNode(node) keyword_ref(node) end
    def visit_NilNode(node) keyword_ref(node) end
    def visit_SelfNode(node) keyword_ref(node) end
    def visit_SourceFileNode(node) keyword_ref(node) end
    def visit_SourceLineNode(node) keyword_ref(node) end
    def visit_SourceEncodingNode(node) keyword_ref(node) end

    def keyword_ref(node)
      [:var_ref, [:@kw, text(node_loc(node)), pos(node['start'])]]
    end

    def visit_StringNode(node)
      opening = node['opening_loc'] ? text(node['opening_loc']) : nil
      return [:@CHAR, text(node_loc(node)), pos(node['start'])] if opening == '?'
      return [:string_literal, list(:string, dedented_entries([node]))] if squiggly?(opening)
      content = string_content_token(node)
      return content if opening.nil?
      # "" has no content event at all.
      return [:string_literal, list(:string, [])] if node['content_loc'][1] == 0
      [:string_literal, list(:string, [[:add, content]])]
    end

    def squiggly?(opening)
      !opening.nil? && opening.start_with?('<<~')
    end

    # A squiggly heredoc is reported one line at a time, with the common
    # indentation taken off each line and the column moved past what was taken
    # (MRI does this after the fact, in Ripper's on_heredoc_dedent).
    def dedented_entries(parts)
      width = dedent_width(parts)
      entries = []
      line_start = true
      parts.each do |part|
        if part['type'] == 'StringNode'
          offset = part['content_loc'][0]
          text(part['content_loc']).each_line do |line|
            # Ripper dedents every content part of the heredoc, not only the
            # ones that open a line.
            strip = dedent_prefix(line, width)
            entries << [:add, [:@tstring_content, line[strip..-1], pos(offset + strip)]]
            offset += line.bytesize
            line_start = line.end_with?("\n")
          end
        else
          # A line that is nothing but indentation in front of an interpolation
          # still produces an (empty) content event in Ripper.
          entries << [:add, empty_content(part['start'], width)] if line_start
          entries << [:add, string_part(part)]
          line_start = false
        end
      end
      entries
    end

    def empty_content(offset, width)
      line_begin = @src.rindex("\n", offset - 1)
      line_begin = line_begin.nil? ? 0 : line_begin + 1
      taken = dedent_prefix(@src.byteslice(line_begin, offset - line_begin), width)
      [:@tstring_content, '', pos(line_begin + taken)]
    end

    def dedent_width(parts)
      width = nil
      line_start = true
      parts.each do |part|
        if part['type'] == 'StringNode'
          text(part['content_loc']).each_line do |line|
            if line_start && line.strip != ''
              indent = indent_width(line)
              width = indent if width.nil? || indent < width
            end
            line_start = line.end_with?("\n")
          end
        else
          if line_start
            indent = indent_width(line_prefix(part['start']))
            width = indent if width.nil? || indent < width
          end
          line_start = false
        end
      end
      width || 0
    end

    def line_prefix(offset)
      line_begin = @src.rindex("\n", offset - 1)
      line_begin = line_begin.nil? ? 0 : line_begin + 1
      @src.byteslice(line_begin, offset - line_begin)
    end

    def indent_width(line)
      column = 0
      line.each_char do |char|
        case char
        when ' ' then column += 1
        when "\t" then column = column / 8 * 8 + 8
        else break
        end
      end
      column
    end

    # How many bytes of +line+ make up +width+ columns of indentation.
    def dedent_prefix(line, width)
      column = 0
      taken = 0
      line.each_char do |char|
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

    def string_content_token(node)
      [:@tstring_content, text(node['content_loc']), pos(node['content_loc'][0])]
    end

    def visit_InterpolatedStringNode(node)
      parts = node['parts']
      # Adjacent string literals ("a" "b") come back as an interpolation with
      # no delimiters of its own.
      if node['opening_loc'].nil?
        return parts[1..-1].inject(visit(parts[0])) { |left, part| [:string_concat, left, visit(part)] }
      end
      return [:string_literal, list(:string, dedented_entries(parts))] if squiggly?(text(node['opening_loc']))
      [:string_literal, list(:string, parts.map { |part| [:add, string_part(part)] })]
    end

    def string_part(node)
      case node['type']
      when 'StringNode' then string_content_token(node)
      when 'EmbeddedStatementsNode' then [:string_embexpr, statement_list(node['statements'])]
      when 'EmbeddedVariableNode' then [:string_dvar, visit(node['variable'])]
      else visit(node)
      end
    end

    def visit_EmbeddedStatementsNode(node)
      [:string_embexpr, statement_list(node['statements'])]
    end

    def visit_XStringNode(node)
      [:xstring_literal, list(:xstring, [[:add, string_content_token(node)]])]
    end

    def visit_InterpolatedXStringNode(node)
      [:xstring_literal, list(:xstring, node['parts'].map { |part| [:add, string_part(part)] })]
    end

    def visit_RegularExpressionNode(node)
      content = node['content_loc'][1] == 0 ? [] : [[:add, [:@tstring_content, text(node['content_loc']), pos(node['content_loc'][0])]]]
      [:regexp_literal,
       list(:regexp, content),
       [:@regexp_end, text(node['closing_loc']), pos(node['closing_loc'][0])]]
    end

    def visit_InterpolatedRegularExpressionNode(node)
      [:regexp_literal,
       list(:regexp, node['parts'].map { |part| [:add, string_part(part)] }),
       [:@regexp_end, text(node['closing_loc']), pos(node['closing_loc'][0])]]
    end

    def visit_MatchLastLineNode(node) visit_RegularExpressionNode(node) end
    def visit_InterpolatedMatchLastLineNode(node) visit_InterpolatedRegularExpressionNode(node) end

    def visit_SymbolNode(node)
      opening = node['opening_loc'] ? text(node['opening_loc']) : nil
      return [:dyna_symbol, list(:string, symbol_content(node))] if opening.nil? ? false : opening.size > 1
      value = fname_token(node['content_loc'])
      return value if opening.nil?
      [:symbol_literal, [:symbol, value]]
    end

    # `:""` and `%s()` have no content event at all.
    def symbol_content(node)
      location = node['content_loc']
      return [] if location.nil? || location[1] == 0
      [[:add, [:@tstring_content, text(location), pos(location[0])]]]
    end

    def visit_InterpolatedSymbolNode(node)
      [:dyna_symbol, list(:string, node['parts'].map { |part| [:add, string_part(part)] })]
    end

    def visit_ArrayNode(node)
      opening = node['opening_loc'] ? text(node['opening_loc']) : nil
      elements = node['elements']
      if opening && opening.start_with?('%')
        kind = { 'w' => :qwords, 'W' => :words, 'i' => :qsymbols, 'I' => :symbols }[opening[1, 1]]
        # %W and %I interpolate, so each of their elements is a word of its own.
        interpolating = opening[1, 1] == opening[1, 1].upcase
        return [:array, list(kind, elements.map { |e| [:add, word_element(e, interpolating)] })]
      end
      [:array, elements.empty? ? nil : list(:args, argument_entries(elements))]
    end

    def word_element(node, interpolating = false)
      case node['type']
      when 'StringNode'
        token = string_content_token(node)
        interpolating ? list(:word, [[:add, token]]) : token
      when 'SymbolNode'
        token = [:@tstring_content, text(node['content_loc']), pos(node['content_loc'][0])]
        interpolating ? list(:word, [[:add, token]]) : token
      when 'InterpolatedStringNode', 'InterpolatedSymbolNode'
        list(:word, node['parts'].map { |part| [:add, string_part(part)] })
      else visit(node)
      end
    end

    def visit_HashNode(node)
      elements = node['elements']
      [:hash, elements.empty? ? nil : [:assoclist_from_args, elements.map { |e| visit(e) }]]
    end

    def visit_KeywordHashNode(node)
      [:bare_assoc_hash, node['elements'].map { |e| visit(e) }]
    end

    def visit_AssocNode(node)
      value = node['value']
      # `{a:}` - the value prism fills in is not written down, so Ripper has none.
      value = nil if value && value['type'] == 'ImplicitNode'
      [:assoc_new, assoc_key(node['key']), visit(value)]
    end

    def assoc_key(node)
      # `a: 1` - prism keeps the label as a symbol whose closing is the colon.
      if node['type'] == 'SymbolNode' && node['closing_loc'] && text(node['closing_loc']).end_with?(':')
        # `a: 1` is a label; `"a": 1` keeps its quotes and stays a dynamic symbol.
        if node['opening_loc'].nil?
          return [:@label, text([node['start'], node['length']]), pos(node['start'])]
        end
        return [:dyna_symbol, list(:string, symbol_content(node))]
      end
      visit(node)
    end

    def visit_AssocSplatNode(node)
      [:assoc_splat, visit(node['value'])]
    end

    def visit_ImplicitNode(node)
      visit(node['value'])
    end

    def visit_SplatNode(node)
      [:args_add_star, list(:args, []), visit(node['expression'])]
    end

    # ---- variables -------------------------------------------------------

    def visit_LocalVariableReadNode(node) [:var_ref, [:@ident, node['name'], pos(node['start'])]] end
    def visit_ItLocalVariableReadNode(node) [:vcall, [:@ident, text(node_loc(node)), pos(node['start'])]] end
    def visit_InstanceVariableReadNode(node) [:var_ref, [:@ivar, node['name'], pos(node['start'])]] end
    def visit_ClassVariableReadNode(node) [:var_ref, [:@cvar, node['name'], pos(node['start'])]] end
    def visit_GlobalVariableReadNode(node) [:var_ref, [:@gvar, node['name'], pos(node['start'])]] end
    def visit_ConstantReadNode(node) [:var_ref, [:@const, node['name'], pos(node['start'])]] end
    def visit_BackReferenceReadNode(node) [:@backref, text(node_loc(node)), pos(node['start'])] end
    def visit_NumberedReferenceReadNode(node) [:@backref, text(node_loc(node)), pos(node['start'])] end

    def visit_ConstantPathNode(node)
      name = [:@const, node['name'], pos(node['name_loc'][0])]
      node['parent'] ? [:const_path_ref, visit(node['parent']), name] : [:top_const_ref, name]
    end

    # ---- assignment targets ----------------------------------------------

    def target(node)
      case node['type']
      when 'LocalVariableTargetNode' then [:var_field, [:@ident, node['name'], pos(node['start'])]]
      when 'InstanceVariableTargetNode' then [:var_field, [:@ivar, node['name'], pos(node['start'])]]
      when 'ClassVariableTargetNode' then [:var_field, [:@cvar, node['name'], pos(node['start'])]]
      when 'GlobalVariableTargetNode' then [:var_field, [:@gvar, node['name'], pos(node['start'])]]
      when 'ConstantTargetNode' then [:var_field, [:@const, node['name'], pos(node['start'])]]
      when 'ConstantPathTargetNode', 'ConstantPathNode'
        name = [:@const, node['name'], pos(node['name_loc'][0])]
        node['parent'] ? [:const_path_field, visit(node['parent']), name] : [:top_const_field, name]
      when 'CallTargetNode'
        [:field, visit(node['receiver']), call_operator(node), name_token(node['message_loc'])]
      when 'IndexTargetNode'
        [:aref_field, visit(node['receiver']), arguments_with_block(node['arguments'], node['block'])]
      when 'MultiTargetNode'
        mlhs_paren(multi_targets(node))
      when 'SplatNode'
        node['expression'] ? [:rest_param, target(node['expression'])] : [:rest_param, nil]
      when 'ImplicitRestNode' then nil
      else visit(node)
      end
    end

    # Ripper's mlhs list: a star becomes [:rest_param, x] pushed onto the flat
    # form but its own mlhs_add_star event in the raw one, and the targets after
    # it arrive as a second list joined with mlhs_add_post.
    def multi_targets(node, &convert)
      convert ||= method(:target)
      accumulator = @raw ? [:mlhs_new] : []
      node['lefts'].each do |left|
        value = convert.call(left)
        accumulator = @raw ? [:mlhs_add, accumulator, value] : accumulator + [value]
      end

      rest = node['rest']
      if rest && rest['type'] != 'ImplicitRestNode'
        star = rest['expression'] ? convert.call(rest['expression']) : nil
        accumulator = @raw ? [:mlhs_add_star, accumulator, star] : accumulator + [[:rest_param, star]]
      end

      rights = node['rights']
      unless rights.empty?
        if @raw
          post = [:mlhs_new]
          rights.each { |right| post = [:mlhs_add, post, convert.call(right)] }
          accumulator = [:mlhs_add_post, accumulator, post]
        else
          accumulator = accumulator + rights.map { |right| convert.call(right) }
        end
      end
      accumulator
    end

    def mlhs_paren(inner)
      @raw ? [:mlhs_paren, inner] : [:mlhs, *inner]
    end

    def visit_MultiWriteNode(node)
      targets = multi_targets(node)
      targets = mlhs_paren(targets) if node['lparen_loc']
      [:massign, targets, multi_value(node['value'])]
    end

    # `a, b = 1, 2` builds its right-hand side out of the argument list of all
    # but the last value, with the last one added on top.
    def mrhs_from_args(elements)
      base = [:mrhs_new_from_args, list(:args, argument_entries(elements[0...-1]))]
      last = visit(elements[-1])
      @raw ? [:mrhs_add, base, last] : base + [last]
    end

    # `mrhs: args ',' arg | args ',' '*' arg | '*' arg` - a trailing splat wraps
    # whatever came before it, anything else is added on top of it.
    def mrhs_list(elements)
      last = elements[-1]
      head = elements[0...-1]
      if last['type'] == 'SplatNode'
        base = head.empty? ? list(:mrhs, []) : [:mrhs_new_from_args, list(:args, argument_entries(head))]
        [:mrhs_add_star, base, visit(last['expression'])]
      else
        mrhs_from_args(elements)
      end
    end

    def multi_value(node)
      return visit(node) unless node['type'] == 'ArrayNode' && node['opening_loc'].nil?
      mrhs_list(node['elements'])
    end

    # ---- assignment ------------------------------------------------------

    def visit_LocalVariableWriteNode(node)
      [:assign, [:var_field, [:@ident, node['name'], pos(node['name_loc'][0])]], assigned_value(node['value'])]
    end

    def visit_InstanceVariableWriteNode(node)
      [:assign, [:var_field, [:@ivar, node['name'], pos(node['name_loc'][0])]], assigned_value(node['value'])]
    end

    def visit_ClassVariableWriteNode(node)
      [:assign, [:var_field, [:@cvar, node['name'], pos(node['name_loc'][0])]], assigned_value(node['value'])]
    end

    def visit_GlobalVariableWriteNode(node)
      [:assign, [:var_field, [:@gvar, node['name'], pos(node['name_loc'][0])]], assigned_value(node['value'])]
    end

    def visit_ConstantWriteNode(node)
      [:assign, [:var_field, [:@const, node['name'], pos(node['name_loc'][0])]], assigned_value(node['value'])]
    end

    def visit_ConstantPathWriteNode(node)
      [:assign, target(node['target']), assigned_value(node['value'])]
    end

    def assigned_value(node)
      return visit(node) unless node && node['type'] == 'ArrayNode' && node['opening_loc'].nil?
      multi_value(node)
    end

    OPERATOR_WRITE = {
      'LocalVariableOperatorWriteNode' => :@ident, 'LocalVariableAndWriteNode' => :@ident,
      'LocalVariableOrWriteNode' => :@ident,
      'InstanceVariableOperatorWriteNode' => :@ivar, 'InstanceVariableAndWriteNode' => :@ivar,
      'InstanceVariableOrWriteNode' => :@ivar,
      'ClassVariableOperatorWriteNode' => :@cvar, 'ClassVariableAndWriteNode' => :@cvar,
      'ClassVariableOrWriteNode' => :@cvar,
      'GlobalVariableOperatorWriteNode' => :@gvar, 'GlobalVariableAndWriteNode' => :@gvar,
      'GlobalVariableOrWriteNode' => :@gvar,
      'ConstantOperatorWriteNode' => :@const, 'ConstantAndWriteNode' => :@const,
      'ConstantOrWriteNode' => :@const,
    }.freeze

    OPERATOR_WRITE.each_key do |type|
      define_method(:"visit_#{type}") do |node|
        kind = OPERATOR_WRITE[node['type']]
        location = node['binary_operator_loc'] || node['operator_loc']
        [:opassign,
         [:var_field, [kind, node['name'], pos(node['name_loc'][0])]],
         [:@op, text(location), pos(location[0])],
         visit(node['value'])]
      end
      private :"visit_#{type}"
    end

    def visit_ConstantPathOperatorWriteNode(node) constant_path_opassign(node) end
    def visit_ConstantPathAndWriteNode(node) constant_path_opassign(node) end
    def visit_ConstantPathOrWriteNode(node) constant_path_opassign(node) end

    def constant_path_opassign(node)
      location = node['binary_operator_loc'] || node['operator_loc']
      [:opassign, target(node['target']), [:@op, text(location), pos(location[0])], visit(node['value'])]
    end

    def visit_CallOperatorWriteNode(node) call_opassign(node) end
    def visit_CallAndWriteNode(node) call_opassign(node) end
    def visit_CallOrWriteNode(node) call_opassign(node) end

    def call_opassign(node)
      location = node['binary_operator_loc'] || node['operator_loc']
      [:opassign,
       [:field, visit(node['receiver']), call_operator(node), name_token(node['message_loc'])],
       [:@op, text(location), pos(location[0])],
       visit(node['value'])]
    end

    def visit_IndexOperatorWriteNode(node) index_opassign(node) end
    def visit_IndexAndWriteNode(node) index_opassign(node) end
    def visit_IndexOrWriteNode(node) index_opassign(node) end

    def index_opassign(node)
      location = node['binary_operator_loc'] || node['operator_loc']
      [:opassign,
       [:aref_field, visit(node['receiver']), arguments_with_block(node['arguments'], node['block'])],
       [:@op, text(location), pos(location[0])],
       visit(node['value'])]
    end

    # ---- calls -----------------------------------------------------------

    def call_operator(node)
      location = node['call_operator_loc']
      return nil unless location
      value = text(location)
      value == '.' ? [:@period, value, pos(location[0])] : [:@op, value, pos(location[0])]
    end

    def argument_entries(arguments)
      entries = []
      arguments.each do |argument|
        if argument['type'] == 'SplatNode'
          entries << [:add_star, visit(argument['expression'])]
        else
          entries << [:add, visit(argument)]
        end
      end
      entries
    end

    def forwarding_arguments?(arguments)
      arguments && arguments['arguments'].any? { |a| a['type'] == 'ForwardingArgumentsNode' }
    end

    def arguments_with_block(arguments, block, bare = false)
      return [:args_forward] if forwarding_arguments?(arguments)
      given = arguments ? arguments['arguments'] : []
      if block && block['type'] == 'BlockArgumentNode'
        blockarg = block['expression'] ? visit(block['expression']) : nil
      else
        blockarg = false
      end
      return nil if given.empty? && blockarg == false

      entries = argument_entries(given)
      # `call_args: command` and `opt_call_args: args ','` both hand the parser a
      # plain argument list, with no args_add_block wrapped around it.
      if blockarg == false && (bare || sole_command?(entries))
        return list(:args, entries)
      end
      [:args_add_block, list(:args, entries), blockarg]
    end

    # MRI's `command` rule - what `call_args: command` accepts. A yield or super
    # written with parentheses is a primary rather than a command.
    def sole_command?(entries)
      return false unless entries.size == 1 && entries[0][0] == :add
      value = entries[0][1]
      return false unless value.is_a?(Array)
      case value[0]
      when :command, :command_call then true
      when :yield then !(value[1].is_a?(Array) && value[1][0] == :paren)
      when :super then !(value[1].is_a?(Array) && value[1][0] == :arg_paren)
      else false
      end
    end

    # `f(a, b,)` - the trailing comma is what drops args_add_block.
    def trailing_comma?(node)
      closing = node['closing_loc']
      arguments = node['arguments']
      return false if closing.nil? || arguments.nil?
      gap = @src.byteslice(node_end(arguments), closing[0] - node_end(arguments))
      !gap.nil? && gap.include?(',')
    end

    def visit_ForwardingArgumentsNode(_node)
      [:args_forward]
    end

    def visit_CallNode(node)
      receiver = node['receiver']
      name = node['name']
      arguments = node['arguments']
      block = node['block']
      block_node = block && block['type'] == 'BlockNode' ? block : nil
      given = arguments ? arguments['arguments'] : []

      if (node['flags'] & CALL_ATTRIBUTE_WRITE) != 0
        if name == '[]='
          value = given[-1]
          index = given[0...-1]
          inner = index.empty? ? nil : [:args_add_block, list(:args, argument_entries(index)), false]
          return [:assign, [:aref_field, visit(receiver), inner], assigned_value(value)]
        end
        field = [:field, visit(receiver), call_operator(node), name_token(node['message_loc'])]
        return [:assign, field, assigned_value(given[-1])]
      end

      result =
        if receiver && node['call_operator_loc'].nil?
          operator_call(node, receiver, name, given, block_node)
        elsif receiver
          receiver_call(node, receiver, arguments, block, block_node)
        else
          plain_call(node, name, arguments, block, block_node)
        end

      block_node ? [:method_add_block, result, block_sexp(block_node)] : result
    end

    def operator_call(node, receiver, name, given, block_node)
      if name == '[]'
        return [:aref, visit(receiver), arguments_with_block(node['arguments'], node['block'])]
      end
      if given.empty? && !block_node
        message = text(node['message_loc'])
        return [:unary, message == 'not' ? :not : UNARY_OPERATORS.fetch(name, name.to_sym), visit(receiver)]
      end
      [:binary, visit(receiver), name.to_sym, visit(given[0])]
    end

    def receiver_call(node, receiver, arguments, block, block_node)
      # `a.()` has no message to report at all; Ripper names it :call.
      message = node['message_loc'] ? name_token(node['message_loc']) : :call
      call = [:call, visit(receiver), call_operator(node), message]
      if node['opening_loc']
        [:method_add_arg, call, [:arg_paren, arguments_with_block(arguments, block, trailing_comma?(node))]]
      elsif arguments
        [:command_call, visit(receiver), call_operator(node), message,
         arguments_with_block(arguments, block)]
      elsif block_node
        call
      else
        call
      end
    end

    def plain_call(node, _name, arguments, block, block_node)
      message = node['message_loc'] ? name_token(node['message_loc']) : nil
      if (node['flags'] & CALL_VARIABLE) != 0
        return [:vcall, message]
      end
      if node['opening_loc']
        [:method_add_arg, [:fcall, message], [:arg_paren, arguments_with_block(arguments, block, trailing_comma?(node))]]
      elsif arguments || (block && block['type'] == 'BlockArgumentNode')
        [:command, message, arguments_with_block(arguments, block)]
      else
        [:method_add_arg, [:fcall, message], list(:args, [])]
      end
    end

    def visit_SuperNode(node)
      block = node['block']
      argument_block = block && block['type'] == 'BlockArgumentNode' ? block : nil
      inner =
        if node['lparen_loc']
          [:super, [:arg_paren, arguments_with_block(node['arguments'], argument_block)]]
        else
          [:super, arguments_with_block(node['arguments'], argument_block)]
        end
      block && block['type'] == 'BlockNode' ? [:method_add_block, inner, block_sexp(block)] : inner
    end

    def visit_ForwardingSuperNode(node)
      block = node['block']
      block ? [:method_add_block, [:zsuper], block_sexp(block)] : [:zsuper]
    end

    def visit_YieldNode(node)
      return [:yield0] unless node['arguments'] || node['lparen_loc']
      inner = arguments_with_block(node['arguments'], nil)
      # `yield()` still reports an (empty) argument list inside its parentheses.
      node['lparen_loc'] ? [:yield, [:paren, inner || list(:args, [])]] : [:yield, inner]
    end

    def visit_ReturnNode(node)
      node['arguments'] ? hoist_block([:return, arguments_with_block(node['arguments'], nil)]) : [:return0]
    end

    def visit_BreakNode(node)
      hoist_block([:break, node['arguments'] ? arguments_with_block(node['arguments'], nil) : list(:args, [])])
    end

    def visit_NextNode(node)
      hoist_block([:next, node['arguments'] ? arguments_with_block(node['arguments'], nil) : list(:args, [])])
    end

    # `return obj.each do ... end` is `block_call: command do_block` in MRI's
    # grammar: the block belongs to the return, not to the call inside it.
    # prism hangs it off the call, so move it back out.
    def hoist_block(built)
      inner = sole_argument(built[1])
      return built unless inner.is_a?(Array) && inner[0] == :method_add_block
      return built unless inner[2].is_a?(Array) && inner[2][0] == :do_block
      return built unless sole_command?([[:add, inner[1]]])
      [:method_add_block, [built[0], list(:args, [[:add, inner[1]]])], inner[2]]
    end

    def sole_argument(arguments)
      return nil unless arguments.is_a?(Array)
      arguments = arguments[1] if arguments[0] == :args_add_block
      return nil unless arguments.is_a?(Array)
      if @raw
        arguments[0] == :args_add && arguments[1] == [:args_new] ? arguments[2] : nil
      else
        arguments.size == 1 ? arguments[0] : nil
      end
    end

    def visit_RedoNode(_node) [:redo] end
    def visit_RetryNode(_node) [:retry] end

    def visit_DefinedNode(node)
      [:defined, visit(node['value'])]
    end

    def visit_AndNode(node)
      [:binary, visit(node['left']), text(node['operator_loc']).to_sym, visit(node['right'])]
    end

    def visit_OrNode(node)
      [:binary, visit(node['left']), text(node['operator_loc']).to_sym, visit(node['right'])]
    end

    def visit_RangeNode(node)
      event = (node['flags'] & RANGE_EXCLUDE_END) != 0 ? :dot3 : :dot2
      [event, visit(node['left']), visit(node['right'])]
    end

    def visit_FlipFlopNode(node)
      event = (node['flags'] & RANGE_EXCLUDE_END) != 0 ? :dot3 : :dot2
      [event, visit(node['left']), visit(node['right'])]
    end

    # ---- blocks and parameters -------------------------------------------

    def block_sexp(node)
      opening = text(node['opening_loc'])
      variables = block_var(node['parameters'])
      if opening == '{'
        from = node['parameters'] ? node_end(node['parameters']) : loc_end(node['opening_loc'])
        [:brace_block, variables,
         statement_list(body_statements(node['body']), leading_void?(from, body_statements(node['body'])))]
      else
        from = node['parameters'] ? node_end(node['parameters']) : loc_end(node['opening_loc'])
        [:do_block, variables, bodystmt(node['body'], leading_void?(from, body_statements(node['body'])))]
      end
    end

    def block_var(node)
      return nil if node.nil? || node['type'] != 'BlockParametersNode'
      locals = node['locals']
      [:block_var,
       parameters_sexp(node['parameters']),
       locals.nil? || locals.empty? ? false : locals.map { |l| [:@ident, l['name'], pos(l['start'])] }]
    end

    def parameters_sexp(node)
      return [:params, nil, nil, nil, nil, nil, nil, nil] if node.nil? || node['type'] != 'ParametersNode'

      requireds = node['requireds'].map { |p| parameter_target(p) }
      optionals = node['optionals'].map do |p|
        [[:@ident, p['name'], pos(p['name_loc'][0])], visit(p['value'])]
      end
      posts = node['posts'].map { |p| parameter_target(p) }
      keywords = node['keywords'].map do |p|
        label = [:@label, text(p['name_loc']), pos(p['name_loc'][0])]
        [label, p['type'] == 'OptionalKeywordParameterNode' ? visit(p['value']) : false]
      end

      rest = node['rest']
      rest_sexp =
        case rest && rest['type']
        when nil then nil
        when 'RestParameterNode'
          [:rest_param, rest['name_loc'] ? [:@ident, rest['name'], pos(rest['name_loc'][0])] : nil]
        when 'ImplicitRestNode' then [:excessed_comma]
        else visit(rest)
        end

      keyword_rest = node['keyword_rest']
      keyword_rest_sexp =
        case keyword_rest && keyword_rest['type']
        when nil then nil
        when 'KeywordRestParameterNode'
          [:kwrest_param, keyword_rest['name_loc'] ? [:@ident, keyword_rest['name'], pos(keyword_rest['name_loc'][0])] : nil]
        when 'NoKeywordsParameterNode' then :nil
        when 'ForwardingParameterNode' then [:args_forward]
        else visit(keyword_rest)
        end

      block = node['block']
      block_sexp_value =
        if block.nil?
          nil
        elsif block['type'] == 'BlockParameterNode'
          [:blockarg, block['name_loc'] ? [:@ident, block['name'], pos(block['name_loc'][0])] : nil]
        else
          nil
        end

      [:params,
       requireds.empty? ? nil : requireds,
       optionals.empty? ? nil : optionals,
       rest_sexp,
       posts.empty? ? nil : posts,
       keywords.empty? ? nil : keywords,
       keyword_rest_sexp,
       block_sexp_value]
    end

    def parameter_target(node)
      case node['type']
      when 'RequiredParameterNode' then [:@ident, node['name'], pos(node['start'])]
      when 'MultiTargetNode' then mlhs_paren(multi_targets(node) { |t| parameter_target(t) })
      else target(node)
      end
    end

    def visit_LambdaNode(node)
      parameters = node['parameters']
      params =
        if parameters.nil?
          [:params, nil, nil, nil, nil, nil, nil, nil]
        elsif parameters['type'] == 'BlockParametersNode'
          inner = parameters_sexp(parameters['parameters'])
          parameters['opening_loc'] && text(parameters['opening_loc']) == '(' ? [:paren, inner] : inner
        else
          [:params, nil, nil, nil, nil, nil, nil, nil]
        end
      body =
        if text(node['opening_loc']) == '{'
          statement_list(body_statements(node['body']))
        else
          bodystmt(node['body'], false)
        end
      [:lambda, params, body]
    end

    # ---- control flow ----------------------------------------------------

    # `a ? b : c` carries no keyword at all; `a if b` has one, but behind the
    # statement it guards.
    def modifier?(node, keyword)
      location = node[keyword]
      return false if location.nil?
      statements = node['statements']
      statements && !statements['body'].empty? && statements['body'][0]['start'] < location[0]
    end

    def visit_IfNode(node)
      predicate = visit(node['predicate'])
      if node['if_keyword_loc'].nil?
        return [:ifop, predicate, visit(node['statements']['body'][0]),
                visit(node['subsequent']['statements']['body'][0])]
      end
      return [:if_mod, predicate, visit(node['statements']['body'][0])] if modifier?(node, 'if_keyword_loc')
      [:if, predicate, clause_body(node, 'then_keyword_loc', node['predicate']),
       node['subsequent'] ? if_subsequent(node['subsequent']) : nil]
    end

    def if_subsequent(node)
      if node['type'] == 'IfNode'
        [:elsif, visit(node['predicate']), clause_body(node, 'then_keyword_loc', node['predicate']),
         node['subsequent'] ? if_subsequent(node['subsequent']) : nil]
      else
        [:else, else_clause(node)]
      end
    end

    def visit_UnlessNode(node)
      if modifier?(node, 'unless_keyword_loc')
        return [:unless_mod, visit(node['predicate']), visit(node['statements']['body'][0])]
      end
      [:unless, visit(node['predicate']), clause_body(node, 'then_keyword_loc', node['predicate']),
       node['else_clause'] ? [:else, else_clause(node['else_clause'])] : nil]
    end

    def visit_WhileNode(node) loop_sexp(node, :while, 'while_keyword_loc') end
    def visit_UntilNode(node) loop_sexp(node, :until, 'until_keyword_loc') end

    def loop_sexp(node, event, keyword)
      if modifier?(node, keyword)
        return [:"#{event}_mod", visit(node['predicate']), visit(node['statements']['body'][0])]
      end
      [event, visit(node['predicate']), clause_body(node, 'do_keyword_loc', node['predicate'])]
    end

    # `then: term | keyword_then | term keyword_then` (and `do:` likewise): the
    # terminator in front of the keyword is eaten by the rule, a terminator
    # after it opens the body with a void statement.
    def clause_body(node, keyword, header)
      location = node[keyword]
      from = location ? loc_end(location) : node_end(header)
      skip = location ? 0 : 1
      statement_list(node['statements'], leading_void?(from, node['statements'], skip))
    end

    def visit_CaseNode(node)
      [:case, visit(node['predicate']), when_clauses(node['conditions'], node['else_clause'])]
    end

    def when_clauses(conditions, else_clause)
      return else_clause ? [:else, else_clause(else_clause)] : nil if conditions.empty?
      first = conditions[0]
      [:when,
       list(:args, argument_entries(first['conditions'])),
       clause_body(first, 'then_keyword_loc', first['conditions'][-1]),
       when_clauses(conditions[1..-1], else_clause)]
    end

    def visit_CaseMatchNode(node)
      [:case, visit(node['predicate']), in_clauses(node['conditions'], node['else_clause'])]
    end

    def in_clauses(conditions, else_clause)
      return else_clause ? [:else, else_clause(else_clause)] : nil if conditions.empty?
      first = conditions[0]
      [:in, pattern(first['pattern']),
       clause_body(first, 'then_keyword_loc', first['pattern']),
       in_clauses(conditions[1..-1], else_clause)]
    end

    def visit_MatchRequiredNode(node)
      [:case, visit(node['value']), [:in, pattern(node['pattern']), nil, nil]]
    end

    def visit_MatchPredicateNode(node)
      [:case, visit(node['value']), [:in, pattern(node['pattern']), nil, nil]]
    end

    def pattern(node)
      case node['type']
      when 'LocalVariableTargetNode', 'InstanceVariableTargetNode', 'ClassVariableTargetNode',
           'GlobalVariableTargetNode', 'ConstantTargetNode', 'ConstantPathTargetNode',
           'MultiTargetNode', 'CallTargetNode', 'IndexTargetNode'
        target(node)
      when 'ParenthesesNode'
        # Parentheses around a pattern are not reported at all.
        inner = node['body']
        inner = inner['body'][0] if inner && inner['type'] == 'StatementsNode'
        pattern(inner)
      else visit(node)
      end
    end

    # A pattern's rest is a splat (`[1, *rest]`) or an assoc splat (`{a:, **rest}`);
    # an anonymous one still gets a var_field, with nothing in it.
    def pattern_rest(node)
      return nil if node.nil? || node['type'] == 'ImplicitRestNode'
      inner = node['expression'] || node['value']
      inner ? target(inner) : [:var_field, nil]
    end

    def visit_ArrayPatternNode(node)
      requireds = node['requireds'].map { |p| pattern(p) }
      posts = node['posts'].map { |p| pattern(p) }
      [:aryptn,
       node['constant'] ? visit(node['constant']) : nil,
       requireds.empty? ? nil : requireds,
       pattern_rest(node['rest']),
       posts.empty? ? nil : posts]
    end

    def visit_FindPatternNode(node)
      requireds = node['requireds'].map { |p| pattern(p) }
      [:fndptn,
       node['constant'] ? visit(node['constant']) : nil,
       pattern_rest(node['left']),
       requireds.empty? ? nil : requireds,
       pattern_rest(node['right'])]
    end

    def visit_HashPatternNode(node)
      elements = node['elements'].map do |element|
        value = element['value']
        value = nil if value && value['type'] == 'ImplicitNode'
        [pattern_key(element['key']), value ? pattern(value) : nil]
      end
      # A bare `**` in a hash pattern is reported as nothing at all, unlike the
      # bare `*` of an array pattern.
      rest = node['rest']
      rest_sexp =
        if rest.nil?
          nil
        elsif rest['type'] == 'NoKeywordsParameterNode'
          [:var_field, :nil]
        elsif rest['value'].nil?
          nil
        else
          target(rest['value'])
        end
      [:hshptn,
       node['constant'] ? visit(node['constant']) : nil,
       elements.empty? && rest.nil? ? nil : elements,
       rest_sexp]
    end

    # A quoted key in a hash pattern reports its content without the dyna_symbol
    # a hash literal would wrap it in.
    def pattern_key(node)
      key = assoc_key(node)
      key.is_a?(Array) && key[0] == :dyna_symbol ? key[1] : key
    end

    def visit_AlternationPatternNode(node)
      [:binary, pattern(node['left']), :|, pattern(node['right'])]
    end

    def visit_CapturePatternNode(node)
      [:binary, pattern(node['value']), :"=>", target(node['target'])]
    end

    def visit_PinnedVariableNode(node)
      visit(node['variable'])
    end

    def visit_PinnedExpressionNode(node)
      [:begin, visit(node['expression'])]
    end

    def visit_LocalVariableTargetNode(node) target(node) end
    def visit_InstanceVariableTargetNode(node) target(node) end
    def visit_ClassVariableTargetNode(node) target(node) end
    def visit_GlobalVariableTargetNode(node) target(node) end
    def visit_ConstantTargetNode(node) target(node) end
    def visit_ConstantPathTargetNode(node) target(node) end
    def visit_MultiTargetNode(node) mlhs_paren(multi_targets(node)) end

    def visit_MatchWriteNode(node)
      visit(node['call'])
    end

    def visit_ForNode(node)
      index = node['index']
      # `for a, b in c` lists its targets directly, with no mlhs around them.
      target = index['type'] == 'MultiTargetNode' && index['lparen_loc'].nil? ? multi_targets(index) : target(index)
      [:for, target, visit(node['collection']), clause_body(node, 'do_keyword_loc', node['collection'])]
    end

    def visit_RescueModifierNode(node)
      [:rescue_mod, visit(node['expression']), visit(node['rescue_expression'])]
    end

    def visit_BeginNode(node)
      [:begin, bodystmt(node, leading_void?(loc_end(node['begin_keyword_loc']), node['statements']))]
    end

    # ---- definitions -----------------------------------------------------

    def visit_DefNode(node)
      name = fname_token(node['name_loc'])
      params = parameters_sexp(node['parameters'])
      params = [:paren, params] if node['lparen_loc']
      statements = body_statements(node['body'])
      body =
        if node['equal_loc']
          [:bodystmt, visit(node['body']['body'][0]), nil, nil, nil]
        else
          from = node['rparen_loc'] ? loc_end(node['rparen_loc']) : loc_end(node['name_loc'])
          skip = node['lparen_loc'] ? 0 : 1
          bodystmt(node['body'], leading_void?(from, statements, skip))
        end
      if node['receiver']
        [:defs, visit(node['receiver']), operator_token(node['operator_loc']), name, params, body]
      else
        [:def, name, params, body]
      end
    end

    def operator_token(location)
      value = text(location)
      value == '.' ? [:@period, value, pos(location[0])] : [:@op, value, pos(location[0])]
    end

    def visit_ClassNode(node)
      path = constant_definition(node['constant_path'])
      header_end = node['superclass'] ? node_end(node['superclass']) : node_end(node['constant_path'])
      skip = node['superclass'] ? 1 : 0
      statements = body_statements(node['body'])
      [:class, path, node['superclass'] ? visit(node['superclass']) : nil,
       bodystmt(node['body'], leading_void?(header_end, statements, skip))]
    end

    def visit_ModuleNode(node)
      statements = body_statements(node['body'])
      [:module, constant_definition(node['constant_path']),
       bodystmt(node['body'], leading_void?(node_end(node['constant_path']), statements))]
    end

    def visit_SingletonClassNode(node)
      statements = body_statements(node['body'])
      [:sclass, visit(node['expression']),
       bodystmt(node['body'], leading_void?(node_end(node['expression']), statements, 1))]
    end

    def constant_definition(node)
      if node['type'] == 'ConstantReadNode'
        [:const_ref, [:@const, node['name'], pos(node['start'])]]
      else
        visit(node)
      end
    end

    def visit_AliasMethodNode(node)
      [:alias, alias_name(node['new_name']), alias_name(node['old_name'])]
    end

    def visit_AliasGlobalVariableNode(node)
      [:var_alias, global_name(node['new_name']), global_name(node['old_name'])]
    end

    def global_name(node)
      [node['type'] == 'BackReferenceReadNode' ? :@backref : :@gvar, text(node_loc(node)), pos(node['start'])]
    end

    def alias_name(node)
      return [:symbol_literal, fname_token(node['content_loc'])] if node['type'] == 'SymbolNode' && node['opening_loc'].nil?
      visit(node)
    end

    def visit_UndefNode(node)
      [:undef, node['names'].map { |n| alias_name(n) }]
    end

    def visit_ShareableConstantNode(node)
      visit(node['write'])
    end

    def visit_PreExecutionNode(node)
      [:BEGIN, statement_list(node['statements'])]
    end

    def visit_PostExecutionNode(node)
      [:END, statement_list(node['statements'])]
    end
  end
end
