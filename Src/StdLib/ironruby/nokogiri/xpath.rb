# frozen_string_literal: true

module Nokogiri
  # A small XPath 1.0 evaluator, written against the node wrappers.
  #
  # Nokogiri gets XPath from libxml2.  AngleSharp has no XPath at all, so this
  # is IronRuby's own: the location-path language (all thirteen axes, the node
  # tests, predicates), plus the parts of the expression language predicates
  # actually use - comparisons, and/or, and the string and node-set functions.
  #
  # What it does not have: variables, namespace-aware name tests beyond matching
  # the literal prefix, arithmetic beyond +/-, and the number and boolean
  # functions nobody in this dependency tree calls.  An expression it cannot
  # parse raises Nokogiri::XML::XPath::SyntaxError rather than quietly matching
  # nothing.
  module XPath
    class SyntaxError < ::StandardError; end

    # Nokogiri's own test for "is this argument XPath or CSS".
    LOOKS_LIKE_XPATH = %r{^(\./|/|\.\.|\.$)}

    class << self
      def looks_like_xpath?(string)
        LOOKS_LIKE_XPATH.match?(string)
      end

      def query(node, expression)
        Evaluator.new(node).evaluate(expression)
      end
    end

    class Tokenizer # :nodoc:
      TOKEN = %r{
        \s*(
          //|::|\.\.|!=|<=|>=|
          [\/\(\)\[\]@,\|\*\.=<>\+\-]|
          "[^"]*"|'[^']*'|
          \d+(?:\.\d+)?|
          [A-Za-z_][-\w.]*(?::[A-Za-z_][-\w.]*)?
        )\s*
      }x

      def initialize(source)
        @tokens = []
        position = 0
        while position < source.length
          match = TOKEN.match(source, position)
          raise SyntaxError, "cannot parse XPath: #{source}" if match.nil? || match.begin(0) != position

          @tokens << match[1]
          position = match.end(0)
        end
        @index = 0
      end

      def peek(offset = 0)
        @tokens[@index + offset]
      end

      def next_token
        token = @tokens[@index]
        @index += 1
        token
      end

      def accept(token)
        return false unless peek == token

        @index += 1
        true
      end

      def expect(token)
        raise SyntaxError, "expected #{token}, got #{peek.inspect}" unless accept(token)
      end

      def eof?
        @index >= @tokens.length
      end
    end

    # Parses an expression into a tree of arrays and evaluates it against a
    # starting node.  Parsing and evaluation are one pass over one object
    # because every expression here is evaluated exactly once.
    class Evaluator # :nodoc:
      AXES = %w[
        child descendant descendant-or-self parent ancestor ancestor-or-self
        following-sibling preceding-sibling following preceding self attribute namespace
      ].freeze

      def initialize(node)
        @node = node
        @document = node.document
      end

      def evaluate(expression)
        tokens = Tokenizer.new(expression)
        tree = parse_union(tokens)
        raise SyntaxError, "trailing input in XPath: #{expression}" unless tokens.eof?

        value = eval_node(tree, [@node])
        value.is_a?(Array) ? value : [value]
      end

      #
      #  parsing
      #

      def parse_union(tokens)
        paths = [parse_or(tokens)]
        paths << parse_or(tokens) while tokens.accept("|")
        paths.length == 1 ? paths.first : [:union, paths]
      end

      def parse_or(tokens)
        left = parse_and(tokens)
        while tokens.peek == "or"
          tokens.next_token
          left = [:or, left, parse_and(tokens)]
        end
        left
      end

      def parse_and(tokens)
        left = parse_comparison(tokens)
        while tokens.peek == "and"
          tokens.next_token
          left = [:and, left, parse_comparison(tokens)]
        end
        left
      end

      COMPARISONS = %w[= != <= >= < >].freeze

      def parse_comparison(tokens)
        left = parse_additive(tokens)
        while COMPARISONS.include?(tokens.peek)
          operator = tokens.next_token
          left = [:compare, operator, left, parse_additive(tokens)]
        end
        left
      end

      def parse_additive(tokens)
        left = parse_unary(tokens)
        while tokens.peek == "+" || tokens.peek == "-"
          operator = tokens.next_token
          left = [:arith, operator, left, parse_unary(tokens)]
        end
        left
      end

      def parse_unary(tokens)
        return [:arith, "-", [:number, 0], parse_unary(tokens)] if tokens.accept("-")

        parse_primary(tokens)
      end

      NODE_TESTS = %w[text comment node processing-instruction].freeze

      def parse_primary(tokens)
        token = tokens.peek
        raise SyntaxError, "unexpected end of XPath" if token.nil?

        if token.start_with?("\"", "'")
          tokens.next_token
          return [:string, token[1..-2]]
        end
        if token =~ /\A\d/
          tokens.next_token
          return [:number, token.include?(".") ? token.to_f : token.to_i]
        end
        if token == "("
          tokens.next_token
          inner = parse_union(tokens)
          tokens.expect(")")
          return inner
        end
        # A function call is a name directly followed by "(" that is not a node test.
        if token =~ /\A[A-Za-z_]/ && tokens.peek(1) == "(" && !NODE_TESTS.include?(token)
          name = tokens.next_token
          tokens.next_token
          arguments = []
          unless tokens.peek == ")"
            arguments << parse_union(tokens)
            arguments << parse_union(tokens) while tokens.accept(",")
          end
          tokens.expect(")")
          return [:function, name, arguments]
        end
        parse_path(tokens)
      end

      def parse_path(tokens)
        steps = []
        absolute = false
        if tokens.accept("//")
          absolute = true
          steps << { axis: "descendant-or-self", test: [:node], predicates: [] }
        elsif tokens.accept("/")
          absolute = true
          return [:path, true, []] if tokens.eof? || !step_ahead?(tokens)
        end
        steps << parse_step(tokens)
        loop do
          if tokens.accept("//")
            steps << { axis: "descendant-or-self", test: [:node], predicates: [] }
          elsif tokens.accept("/")
            # nothing
          else
            break
          end
          steps << parse_step(tokens)
        end
        [:path, absolute, steps]
      end

      def step_ahead?(tokens)
        token = tokens.peek
        return false if token.nil?

        token == "@" || token == "*" || token == "." || token == ".." || token =~ /\A[A-Za-z_]/
      end

      def parse_step(tokens)
        if tokens.accept("..")
          return { axis: "parent", test: [:node], predicates: parse_predicates(tokens) }
        end
        if tokens.accept(".")
          return { axis: "self", test: [:node], predicates: parse_predicates(tokens) }
        end

        axis = "child"
        if tokens.accept("@")
          axis = "attribute"
        elsif AXES.include?(tokens.peek.to_s) && tokens.peek(1) == "::"
          axis = tokens.next_token
          tokens.next_token
        end

        test =
          if tokens.accept("*")
            [:any]
          else
            name = tokens.next_token
            raise SyntaxError, "expected a node test" if name.nil?

            if NODE_TESTS.include?(name) && tokens.peek == "("
              tokens.next_token
              literal = nil
              unless tokens.peek == ")"
                literal = tokens.next_token
                literal = literal[1..-2] if literal.start_with?("\"", "'")
              end
              tokens.expect(")")
              [name.to_sym, literal]
            else
              [:name, name]
            end
          end

        { axis: axis, test: test, predicates: parse_predicates(tokens) }
      end

      def parse_predicates(tokens)
        predicates = []
        while tokens.accept("[")
          predicates << parse_union(tokens)
          tokens.expect("]")
        end
        predicates
      end

      #
      #  evaluation
      #

      def eval_node(tree, context)
        case tree.first
        when :path then eval_path(tree, context)
        when :union then tree[1].flat_map { |path| to_nodes(eval_node(path, context)) }.uniq
        when :string, :number then tree[1]
        when :or then truthy?(eval_node(tree[1], context)) || truthy?(eval_node(tree[2], context))
        when :and then truthy?(eval_node(tree[1], context)) && truthy?(eval_node(tree[2], context))
        when :compare then eval_compare(tree, context)
        when :arith then eval_arith(tree, context)
        when :function then eval_function(tree, context)
        else raise SyntaxError, "cannot evaluate #{tree.inspect}"
        end
      end

      def eval_path(tree, context)
        _, absolute, steps = tree
        nodes = absolute ? [root_of(context.first || @node)] : context
        steps.each do |step|
          nodes = apply_step(step, nodes)
        end
        nodes
      end

      # The node an absolute path starts from.  For a node in a document that is
      # the document; for a node in a fragment it is the fragment, which is what
      # libxml2 does too - `//p` inside a scrubbed fragment has to see the
      # fragment's own nodes, because there is no document tree holding them.
      def root_of(node)
        top = node
        top = top.parent while top.parent
        top
      end

      def apply_step(step, nodes)
        result = []
        nodes.each do |node|
          candidates = axis_nodes(step[:axis], node).select { |candidate| test_matches?(step[:test], candidate) }
          step[:predicates].each do |predicate|
            candidates = filter(candidates, predicate)
          end
          candidates.each { |candidate| result << candidate unless result.include?(candidate) }
        end
        result
      end

      def filter(candidates, predicate)
        size = candidates.length
        selected = []
        candidates.each_with_index do |candidate, index|
          @position = index + 1
          @size = size
          value = eval_node(predicate, [candidate])
          selected << candidate if value.is_a?(Numeric) ? value == index + 1 : truthy?(value)
        end
        selected
      end

      def axis_nodes(axis, node)
        case axis
        when "child" then node.children.to_a
        when "self" then [node]
        when "parent" then [node.parent].compact
        when "attribute" then node.element? ? node.attribute_nodes : []
        when "descendant" then descendants(node)
        when "descendant-or-self" then [node] + descendants(node)
        when "ancestor" then node.ancestors.to_a
        when "ancestor-or-self" then [node] + node.ancestors.to_a
        when "following-sibling" then siblings_after(node)
        when "preceding-sibling" then siblings_before(node)
        when "following" then following(node)
        when "preceding" then preceding(node)
        when "namespace" then []
        else raise SyntaxError, "unsupported axis: #{axis}"
        end
      end

      def descendants(node)
        result = []
        node.children.each do |child|
          result << child
          result.concat(descendants(child))
        end
        result
      end

      def siblings_after(node)
        parent = node.parent
        return [] if parent.nil?

        list = parent.children.to_a
        index = list.index(node)
        index ? list[(index + 1)..-1] : []
      end

      def siblings_before(node)
        parent = node.parent
        return [] if parent.nil?

        list = parent.children.to_a
        index = list.index(node)
        index ? list[0...index].reverse : []
      end

      def following(node)
        result = []
        current = node
        while current
          siblings_after(current).each do |sibling|
            result << sibling
            result.concat(descendants(sibling))
          end
          current = current.parent
        end
        result
      end

      def preceding(node)
        ancestors = node.ancestors.to_a
        result = []
        current = node
        while current
          siblings_before(current).each do |sibling|
            result.concat([sibling] + descendants(sibling))
          end
          current = current.parent
        end
        result - ancestors
      end

      def test_matches?(test, node)
        case test.first
        when :any then node.element? || node.type == XML::Node::ATTRIBUTE_NODE
        when :node then true
        when :text then node.text? || node.cdata?
        when :comment then node.comment?
        when :"processing-instruction" then node.processing_instruction?
        when :name
          name = test[1]
          if node.type == XML::Node::ATTRIBUTE_NODE
            node.qualified_name == name
          else
            node.element? && node.name == name
          end
        else false
        end
      end

      def eval_compare(tree, context)
        _, operator, left_tree, right_tree = tree
        left = eval_node(left_tree, context)
        right = eval_node(right_tree, context)
        if left.is_a?(Array) || right.is_a?(Array)
          lefts = left.is_a?(Array) ? left.map { |n| string_value(n) } : [left]
          rights = right.is_a?(Array) ? right.map { |n| string_value(n) } : [right]
          return lefts.any? { |l| rights.any? { |r| compare(operator, l, r) } }
        end
        compare(operator, left, right)
      end

      def compare(operator, left, right)
        if operator == "=" || operator == "!="
          equal =
            if left.is_a?(Numeric) || right.is_a?(Numeric)
              to_number(left) == to_number(right)
            else
              to_string(left) == to_string(right)
            end
          return operator == "=" ? equal : !equal
        end
        left_number = to_number(left)
        right_number = to_number(right)
        return false if left_number.nil? || right_number.nil?

        case operator
        when "<" then left_number < right_number
        when "<=" then left_number <= right_number
        when ">" then left_number > right_number
        when ">=" then left_number >= right_number
        end
      end

      def eval_arith(tree, context)
        _, operator, left, right = tree
        left_number = to_number(eval_node(left, context)) || 0
        right_number = to_number(eval_node(right, context)) || 0
        operator == "+" ? left_number + right_number : left_number - right_number
      end

      def eval_function(tree, context)
        _, name, argument_trees = tree
        arguments = argument_trees.map { |argument| eval_node(argument, context) }
        case name
        when "not" then !truthy?(arguments[0])
        when "true" then true
        when "false" then false
        when "contains" then to_string(arguments[0]).include?(to_string(arguments[1]))
        when "starts-with" then to_string(arguments[0]).start_with?(to_string(arguments[1]))
        when "ends-with" then to_string(arguments[0]).end_with?(to_string(arguments[1]))
        when "string" then to_string(arguments[0])
        # XPath 1.0 has one numeric type and it is a double, so the numeric functions answer
        # Floats - which is what libxml2 hands nokogiri, and what `xpath("count(//a)")` is
        # compared against.
        when "string-length" then to_string(arguments[0]).length.to_f
        when "normalize-space" then to_string(arguments[0]).strip.gsub(/\s+/, " ")
        when "concat" then arguments.map { |argument| to_string(argument) }.join
        when "count" then to_nodes(arguments[0]).length.to_f
        when "last" then (@size || 1).to_f
        when "position" then (@position || 1).to_f
        when "name", "local-name"
          node = to_nodes(arguments.empty? ? context : arguments[0]).first
          node ? node.name : ""
        when "translate"
          to_string(arguments[0]).tr(to_string(arguments[1]), to_string(arguments[2]))
        else
          raise SyntaxError, "unsupported XPath function: #{name}()"
        end
      end

      def to_nodes(value)
        value.is_a?(Array) ? value : [value]
      end

      def truthy?(value)
        case value
        when Array then !value.empty?
        when String then !value.empty?
        when Numeric then value != 0
        when nil then false
        else value ? true : false
        end
      end

      def string_value(node)
        node.respond_to?(:content) ? node.content.to_s : node.to_s
      end

      def to_string(value)
        case value
        when Array then value.empty? ? "" : string_value(value.first)
        when nil then ""
        when true then "true"
        when false then "false"
        else value.to_s
        end
      end

      def to_number(value)
        string = value.is_a?(Numeric) ? value : to_string(value)
        return string if string.is_a?(Numeric)
        return nil unless string =~ /\A\s*-?\d+(\.\d+)?\s*\z/

        string.include?(".") ? string.to_f : string.to_i
      end
    end
  end

  module XML
    # Nokogiri names the module Nokogiri::XML::XPath, and callers rescue
    # Nokogiri::XML::XPath::SyntaxError.
    XPath = Nokogiri::XPath
  end
end
