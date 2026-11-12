# frozen_string_literal: true

module Nokogiri
  # CSS selection.
  #
  # Nokogiri compiles CSS to XPath and runs it through libxml2.  Here the
  # selector goes to AngleSharp's selector engine instead, which is a real
  # Selectors Level 4 implementation, so #css covers considerably more of CSS
  # than a hand-written CSS-to-XPath translation would.
  #
  # The one thing the engine does not know about is Nokogiri's custom
  # pseudo-classes: a functional pseudo-class Nokogiri does not recognise is
  # dispatched to the handler object passed alongside the selector, which is how
  # `assert_select "a:match('href', ?)", /x/` works in rails-dom-testing.  Those
  # are lifted out of the selector here and applied to the result.
  module CSS
    class SyntaxError < ::StandardError; end

    # Functional pseudo-classes the selector engine handles itself.  Anything
    # else with an argument list is a caller's own.
    KNOWN_FUNCTIONS = %w[
      not is where has matches -moz-any any nth-child nth-last-child nth-of-type
      nth-last-of-type nth-col nth-last-col lang dir host host-context part slotted
    ].freeze

    # An id or class whose name starts with a digit, which CSS forbids and
    # Nokogiri's own selector parser has always allowed - assert_select "#1" is
    # in rails-dom-testing's own test suite.
    NUMERIC_ID = /\A\#(-?[0-9][^\s>+~,\[\]:()#.]*)/

    class << self
      # Splits a #css / #search argument list into the selector strings and the
      # custom-pseudo-class handler.  A Hash in that list is namespace bindings,
      # which mean nothing to the HTML selector engine and are ignored.
      def extract_params(args) # :nodoc:
        rules = []
        handler = nil
        args.each do |arg|
          case arg
          when String, Symbol then rules << arg.to_s
          when Hash then next
          else handler = arg
          end
        end
        [rules, handler]
      end

      def query(node, rule, handler = nil) # :nodoc:
        selector, customs = split_custom_pseudos(rule.to_s)
        apply_customs(node.document, descendants(node, selector, rule), customs, handler)
      end

      # The same search over a set of nodes, matching each node itself as well as
      # its descendants - what Nokogiri::XML::NodeSet#css does.  The custom
      # pseudo-classes are applied once, to everything the base selector found,
      # so that `assert_select elements, ":match('id', ?)"` sees the elements the
      # set already holds.
      def query_set(nodes, document, rule, handler = nil) # :nodoc:
        selector, customs = split_custom_pseudos(rule.to_s)
        found = []
        nodes.each do |node|
          found << node if node.element? && self_matches?(node, selector)
          found.concat(descendants(node, selector, rule))
        end
        apply_customs(document, found.uniq, customs, handler)
      end

      # Whether the node itself matches - Nokogiri::XML::Node#matches?.  Goes
      # through the same rewriting #css does, so that "#1" means the same thing
      # to both.
      def matches?(node, rule) # :nodoc:
        selector, customs = split_custom_pseudos(rule.to_s)
        unless customs.empty?
          raise SyntaxError, "#matches? cannot evaluate the custom pseudo-class in #{rule}"
        end

        selector = rewrite_numeric_names(selector.strip)
        raise SyntaxError, "unsupported selector: #{rule}" if selector.empty?

        result = Native.matches(node.native, selector)
        raise SyntaxError, "unsupported selector: #{rule}" if result.nil?

        result
      end

      # The CSS-to-XPath compiler Nokogiri exposes is not implemented; selection
      # does not go through XPath here.  Saying so is better than answering an
      # expression that would not match what #css matches.
      def xpath_for(selector, options = {})
        raise NotImplementedError,
          "Nokogiri::CSS.xpath_for is not implemented on IronRuby: #css runs the selector " \
          "through AngleSharp's selector engine rather than compiling it to XPath."
      end

      def parse(selector)
        xpath_for(selector)
      end

      private

      # Everything under +node+ that the selector (already stripped of custom
      # pseudo-classes) matches.
      def descendants(node, selector, rule)
        selector, roots = split_root(node, selector)
        selector = normalize(selector)
        if roots
          return roots if selector == "*"

          return roots.flat_map { |root| descendants(root, selector, rule) }
        end

        found = Native.query_selector_all(node.native, selector)
        raise SyntaxError, "unsupported selector: #{rule}" if found.nil?

        document = node.document
        found.map { |native| document.wrap(native) }
      end

      def self_matches?(node, selector)
        selector = normalize(selector)
        return false if selector.include?(":root")

        Native.matches(node.native, selector) ? true : false
      end

      def normalize(selector)
        selector = selector.strip
        selector = "*" if selector.empty?
        rewrite_numeric_names(selector)
      end

      def apply_customs(document, nodes, customs, handler)
        customs.each do |name, arguments|
          unless handler.respond_to?(name)
            raise SyntaxError, "unknown pseudo-class :#{name}() and no handler that answers it"
          end

          set = XML::NodeSet.new(document, nodes)
          nodes = handler.public_send(name, set, *arguments).to_a
        end
        nodes
      end

      # Lifts Nokogiri's custom functional pseudo-classes out of the selector.
      # Scanned rather than matched with a regexp because the argument list is
      # arbitrary text - rails-dom-testing substitutes a Regexp into
      # :match('id', ?) and the result has both quotes and parentheses in it.
      def split_custom_pseudos(rule)
        customs = []
        out = +""
        index = 0
        while index < rule.length
          char = rule[index]
          if char == "\"" || char == "\'"
            closing = rule.index(char, index + 1) || rule.length
            out << rule[index..closing]
            index = closing + 1
            next
          end
          unless char == ":"
            out << char
            index += 1
            next
          end

          name = rule[(index + 1)..-1][/\A[a-zA-Z_][-\w]*/]
          if name.nil? || rule[index + 1 + name.length] != "("
            out << char
            index += 1
            next
          end

          arguments, after = scan_arguments(rule, index + 1 + name.length)
          if KNOWN_FUNCTIONS.include?(name.downcase)
            out << rule[index...after]
          else
            customs << [name.tr("-", "_"), split_arguments(arguments)]
          end
          index = after
        end
        [out, customs]
      end

      # Reads the "( ... )" starting at +open+, respecting quotes and nesting.
      # Answers the text inside and the index just past the closing paren.
      def scan_arguments(rule, open)
        depth = 0
        index = open
        quote = nil
        while index < rule.length
          char = rule[index]
          if quote
            quote = nil if char == quote
          elsif char == "\"" || char == "\'"
            quote = char
          elsif char == "("
            depth += 1
          elsif char == ")"
            depth -= 1
            return [rule[(open + 1)...index], index + 1] if depth.zero?
          end
          index += 1
        end
        [rule[(open + 1)..-1].to_s, rule.length]
      end

      # ":root" means the document element, or - in a fragment, which is what
      # assert_select_encoded works on - the fragment's own top-level elements.
      # The selector engine has no root to speak of in a fragment, so the step is
      # taken here.
      def split_root(node, selector)
        stripped = selector.strip
        return [selector, nil] unless stripped == ":root" || stripped.start_with?(":root ")

        rest = stripped == ":root" ? "" : stripped[6..-1].to_s
        roots =
          if node.is_a?(XML::Document)
            [node.root].compact
          else
            node.element_children.to_a
          end
        [rest, roots]
      end

      # Rewrites "#1" to [id="1"] outside string literals, so that a quoted "#"
      # - a[href="#top"] - is left alone.
      def rewrite_numeric_names(selector)
        out = +""
        index = 0
        quote = nil
        while index < selector.length
          char = selector[index]
          if quote
            quote = nil if char == quote
            out << char
            index += 1
          elsif char == "\"" || char == "\'"
            quote = char
            out << char
            index += 1
          elsif char == "#" && (match = NUMERIC_ID.match(selector[index..-1]))
            out << "[id=\"" << match[1] << "\"]"
            index += match[0].length
          else
            out << char
            index += 1
          end
        end
        out
      end

      # `'id', "value, with comma"` -> ["id", "value, with comma"].  Commas
      # inside quotes do not separate.
      def split_arguments(source)
        arguments = []
        current = +""
        quote = nil
        source.each_char do |char|
          if quote
            if char == quote
              quote = nil
            else
              current << char
            end
          elsif char == "'" || char == "\""
            quote = char
          elsif char == ","
            arguments << current.strip
            current = +""
          else
            current << char
          end
        end
        arguments << current.strip
        arguments.reject(&:empty?)
      end
    end
  end
end
