# frozen_string_literal: true

module Nokogiri
  module XML
    # A parsed document, and the owner of everything in it.
    #
    # Besides being a node, a Document holds the two pieces of bookkeeping the
    # rest of the library needs: the identity map that keeps one Ruby wrapper per
    # DOM node, and the decorator list Nokogiri lets a caller register so that
    # every node it hands out carries extra behaviour (Loofah registers its
    # +scrub!+ there).
    class Document < Node
      attr_accessor :encoding
      attr_accessor :url
      attr_reader :errors

      class << self
        # :xml, :html4 or :html5 - which parser this class parses with and which
        # serialization rules it writes back out with.
        def document_kind
          :xml
        end

        def parse(string_or_io, url = nil, encoding = nil, options = nil)
          markup =
            if string_or_io.respond_to?(:read)
              string_or_io.read
            else
              string_or_io.to_s
            end
          document = new
          document.url = url
          document.encoding ||= encoding
          document.send(:load_markup, markup)
          yield options if block_given?
          document
        end
      end

      def initialize(*args)
        @registry = {}
        @cdata = {}
        @decorators = nil
        @errors = []
        @encoding = nil
        @document = self
        @native =
          if self.class.document_kind == :xml
            Native.parse_xml("<root/>")
          else
            Native.new_html_document
          end
        if self.class.document_kind == :xml
          root = Native.document_element(@native)
          Native.remove_child(@native, root) if root
        end
        yield self if block_given?
      end

      def document
        self
      end

      def parent
        nil
      end

      def type
        DOCUMENT_NODE
      end

      def name
        "document"
      end

      def html?
        self.class.document_kind != :xml
      end

      def xml?
        self.class.document_kind == :xml
      end

      def html4?
        self.class.document_kind == :html4
      end

      def html5?
        self.class.document_kind == :html5
      end

      def root
        element_children.first
      end

      def root=(node)
        old = root
        if old
          old.replace(node)
        else
          add_child(node)
        end
        node
      end

      #
      #  the identity map
      #

      def wrap(native) # :nodoc:
        return nil if native.nil?
        return self if native.equal?(@native)

        node = @registry[native]
        return node if node

        node = Node.build(native, self)
        @registry[native] = node
        decorate(node)
        node
      end

      def register(node) # :nodoc:
        @registry[node.native] = node
        decorate(node)
        node
      end

      def reregister(node, old_native, new_native) # :nodoc:
        @registry.delete(old_native)
        @registry[new_native] = node
      end

      # HTML has no CDATA sections, but libxml2's HTML4 parser reports the
      # contents of <style> and <script> as CDATA and Loofah's escaping depends
      # on that.  The flag lives here, beside the node it describes, because the
      # DOM underneath has nowhere to put it.
      def mark_cdata(native) # :nodoc:
        @cdata[native] = true
      end

      def cdata?(native) # :nodoc:
        @cdata.key?(native)
      end

      #
      #  decorators
      #

      def decorators(key)
        @decorators ||= {}
        @decorators[key] ||= []
      end

      def decorate(node)
        return unless @decorators

        @decorators.each do |klass, list|
          next unless node.is_a?(klass)

          list.each { |mod| node.extend(mod) }
        end
      end

      def decorate!(node)
        decorate(node)
      end

      #
      #  node factories
      #

      def create_element(name, *contents_or_attrs)
        node = Node.new(name, self)
        contents_or_attrs.each do |arg|
          case arg
          when Hash then arg.each { |k, v| node[k.to_s] = v.to_s }
          else node.content = arg.to_s
          end
        end
        yield node if block_given?
        node
      end

      def create_text_node(string)
        Text.new(string.to_s, self)
      end

      def create_cdata(string)
        CDATA.new(self, string.to_s)
      end

      def create_comment(string)
        Comment.new(self, string.to_s)
      end

      def create_document_fragment
        DocumentFragment.new(self)
      end

      def fragment(markup = nil)
        DocumentFragment.new(self, markup)
      end

      # Parses markup as it would appear inside +context+ and answers the nodes,
      # adopted into this document.  This is what Node#add_child and friends use
      # when they are handed a String.
      def parse_in_context(markup, context) # :nodoc:
        if xml?
          parse_xml_in_context(markup)
        else
          context_native =
            (context && context.element? && context.native) ||
            Native.body(@native) ||
            Native.document_element(@native)
          if context_native.nil?
            context_native = Native.create_element(@native, "body")
          end
          Native.parse_html_fragment(markup.to_s, context_native, @native).map { |n| wrap(n) }
        end
      end

      # AngleSharp's XML parser has no fragment mode, so the markup is wrapped in
      # a throwaway root, parsed, and the root's children adopted.
      def parse_xml_in_context(markup) # :nodoc:
        parsed = Native.parse_xml("<nokogiri-fragment>#{markup}</nokogiri-fragment>")
        return [] if parsed.is_a?(String)

        root = Native.document_element(parsed)
        return [] if root.nil?

        Native.children(root).map do |child|
          Native.remove_child(root, child)
          wrap(child)
        end
      end

      #
      #  serialization
      #

      def to_html(options = {})
        Serializer.serialize(self, html: true)
      end

      def to_xml(options = {})
        Serializer.serialize(self, html: false)
      end

      def to_s
        html? ? to_html : to_xml
      end

      def serialize(*args)
        to_s
      end

      # A copy of the tree, the way Nokogiri's #dup is - not a round trip through
      # markup, which for an HTML4 document would read its own trailing newline
      # back in as a text node.
      def dup(level = 1)
        copy = self.class.new
        copy.send(:adopt_native, Native.clone_node(@native, level != 0))
        copy
      end
      alias_method :clone, :dup

      def collect_namespaces
        {}
      end

      def remove_namespaces!
        self
      end

      def validate
        nil
      end

      def inspect
        "#<#{self.class.name}:#{format("0x%x", object_id)}>"
      end

      private

      def adopt_native(native) # :nodoc:
        @registry = {}
        @cdata = {}
        @native = native
        mark_html4_cdata if html4?
        self
      end

      # Replaces the tree this document wraps.  Document.parse builds an empty
      # document first so that a subclass's initialize runs (Loofah registers its
      # decorators there), then loads the markup into it.
      def load_markup(markup)
        @registry = {}
        @cdata = {}
        case self.class.document_kind
        when :xml
          result = Native.parse_xml(markup)
          if result.is_a?(String)
            @errors = [Nokogiri::XML::SyntaxError.new(result)]
            @native = Native.parse_xml("")
          else
            @native = result
          end
        else
          @native = Native.parse_html(html4? ? keep_leading_newline(markup) : markup)
          # libxml2's HTML parser builds nothing at all from empty input, and
          # Loofah asserts that Loofah.document("").root is nil.  The HTML5
          # algorithm always produces html/head/body, so HTML4 drops it again.
          if html4? && markup.to_s.strip.empty?
            element = Native.document_element(@native)
            Native.remove_child(@native, element) if element
          end
          mark_html4_cdata if html4?
        end
        self
      end

      # The HTML5 tokenizer drops one newline immediately after <pre>, <textarea>
      # and <listing>; libxml2's HTML4 parser keeps it, and rails-dom-testing
      # asserts on the difference (assert_select "textarea", "\nfoo\n").  Since
      # HTML4 here is parsed by the HTML5 algorithm, the newline is doubled on the
      # way in so that one survives.
      def keep_leading_newline(markup)
        markup.to_s.gsub(/(<(?:pre|textarea|listing)\b[^>]*>)\n/i) { "#{Regexp.last_match(1)}\n\n" }
      end

      # libxml2's HTML4 parser makes the text inside <style> and <script> a CDATA
      # node.  Reproduce that, because Loofah tests the flag and escapes CDATA
      # differently from text.
      def mark_html4_cdata
        %w[style script].each do |tag|
          (Native.query_selector_all(@native, tag) || []).each do |element|
            Native.children(element).each do |child|
              mark_cdata(child) if Native.node_type(child) == TEXT_NODE
            end
          end
        end
      end
    end

  end
end
