# frozen_string_literal: true

module Nokogiri
  module XML
    # A node in a document.
    #
    # Every node wraps one AngleSharp DOM node.  The wrapper is unique per node
    # for as long as the document lives - Document#wrap keeps an identity map -
    # because Nokogiri callers rely on holding a node across mutations, and
    # because Loofah mixes its own modules into node *instances* through the
    # document's decorators.
    class Node
      include Enumerable

      # libxml2's node type numbers, which are the numbers Nokogiri exposes.
      ELEMENT_NODE = 1
      ATTRIBUTE_NODE = 2
      TEXT_NODE = 3
      CDATA_SECTION_NODE = 4
      ENTITY_REF_NODE = 5
      ENTITY_NODE = 6
      PI_NODE = 7
      COMMENT_NODE = 8
      DOCUMENT_NODE = 9
      DOCUMENT_TYPE_NODE = 10
      DOCUMENT_FRAG_NODE = 11
      NOTATION_NODE = 12
      HTML_DOCUMENT_NODE = 13
      DTD_NODE = 14
      ELEMENT_DECL = 15
      ATTRIBUTE_DECL = 16
      ENTITY_DECL = 17
      NAMESPACE_DECL = 18
      XINCLUDE_START = 19
      XINCLUDE_END = 20

      ENCODE_SPECIAL_CHARS = {
        "&" => "&amp;",
        "<" => "&lt;",
        ">" => "&gt;",
        "\"" => "&quot;",
        "\r" => "&#13;",
      }.freeze

      # The AngleSharp node this wraps.  Internal: nothing outside this library
      # should reach for it, but Node#== and Document#wrap both need it.
      attr_reader :native # :nodoc:

      class << self
        # Wraps a freshly parsed or created AngleSharp node in the Nokogiri class
        # that matches its type.  Document#wrap is the only caller; it holds the
        # identity map that makes the wrapper stable.
        def build(native, document) # :nodoc:
          type = Native.node_type(native)
          klass =
            case type
            when ELEMENT_NODE then Element
            when TEXT_NODE then document.cdata?(native) ? CDATA : Text
            when CDATA_SECTION_NODE then CDATA
            when COMMENT_NODE then Comment
            when PI_NODE then ProcessingInstruction
            when DOCUMENT_TYPE_NODE then DTD
            when DOCUMENT_FRAG_NODE then DocumentFragment
            else Node
            end
          node = klass.allocate
          node.send(:bind_native, native, document)
          node
        end
      end

      # Nokogiri::XML::Node.new("div", document) creates an unparented element.
      def initialize(name, document)
        document = document.document
        native = Native.create_element(document.native, name.to_s)
        raise ArgumentError, "invalid element name: #{name.inspect}" if native.nil?

        bind_native(native, document)
        document.register(self)
        yield self if block_given?
      end

      def bind_native(native, document) # :nodoc:
        @native = native
        @document = document
      end
      private :bind_native

      # Swaps the underlying node - Node#name= has to build a new element,
      # because a DOM element's name is fixed - keeping this wrapper the one the
      # caller holds.
      def rebind_native(native) # :nodoc:
        @document.reregister(self, @native, native)
        @native = native
      end

      #
      #  identity
      #

      def document
        @document
      end

      def ==(other)
        other.is_a?(Node) && other.native.equal?(@native)
      end
      alias_method :eql?, :==

      def hash
        @native.hash
      end

      def inspect
        "#<#{self.class.name}:#{format("0x%x", object_id)} #{to_s.inspect}>"
      end

      #
      #  shape
      #

      def type
        Native.node_type(@native)
      end
      alias_method :node_type, :type

      def name
        case type
        when ELEMENT_NODE then Native.element_name(@native)
        when TEXT_NODE then "text"
        when CDATA_SECTION_NODE then "#cdata-section"
        when COMMENT_NODE then "comment"
        when DOCUMENT_NODE then "document"
        when DOCUMENT_FRAG_NODE then "#document-fragment"
        else Native.element_name(@native).to_s
        end
      end
      alias_method :node_name, :name

      # Renaming an element: see rebind_native.
      def name=(new_name)
        return new_name unless element?

        rebind_native(Native.rename(@native, new_name.to_s))
        new_name
      end
      alias_method :node_name=, :name=

      def element?
        type == ELEMENT_NODE
      end
      alias_method :elem?, :element?

      def text?
        type == TEXT_NODE
      end

      def cdata?
        type == CDATA_SECTION_NODE
      end

      def comment?
        type == COMMENT_NODE
      end

      def processing_instruction?
        type == PI_NODE
      end

      def document?
        is_a?(XML::Document)
      end

      def fragment?
        type == DOCUMENT_FRAG_NODE
      end

      def html?
        document.html?
      end

      def xml?
        document.xml?
      end

      def blank?
        text? && content.strip.empty?
      end

      #
      #  traversal
      #

      def children
        NodeSet.new(document, Native.children(@native).map { |n| document.wrap(n) })
      end

      def child
        children.first
      end

      def element_children
        NodeSet.new(document, children.select(&:element?))
      end
      alias_method :elements, :element_children

      def first_element_child
        element_children.first
      end

      def last_element_child
        element_children.last
      end

      def parent
        document.wrap(Native.parent(@native))
      end

      def next_sibling
        document.wrap(Native.next_sibling(@native))
      end
      alias_method :next, :next_sibling

      def previous_sibling
        document.wrap(Native.previous_sibling(@native))
      end
      alias_method :previous, :previous_sibling

      def next_element
        node = next_sibling
        node = node.next_sibling while node && !node.element?
        node
      end
      alias_method :next_element_sibling, :next_element

      def previous_element
        node = previous_sibling
        node = node.previous_sibling while node && !node.element?
        node
      end
      alias_method :previous_element_sibling, :previous_element

      def ancestors(selector = nil)
        list = []
        node = parent
        while node
          list << node
          node = node.parent
        end
        list = list.select { |n| n.matches?(selector) } if selector
        NodeSet.new(document, list)
      end

      def each
        return to_enum(:each) unless block_given?

        attribute_nodes.each { |attribute| yield [attribute.name, attribute.value] }
        self
      end

      def traverse(&block)
        children.each { |child| child.traverse(&block) }
        block.call(self)
      end

      def path
        return "/" if document?

        parent_node = parent
        return "?" if parent_node.nil?

        prefix = parent_node.document? ? "" : parent_node.path
        siblings = Native.children(parent_node.native).map { |n| document.wrap(n) }
        same = siblings.select { |n| n.type == type && n.name == name }
        index = same.index(self)
        suffix = same.length > 1 ? "[#{index + 1}]" : ""
        "#{prefix}/#{name}#{suffix}"
      end

      #
      #  content
      #

      def content
        Native.text_content(@native).to_s
      end
      alias_method :text, :content
      alias_method :inner_text, :content

      def content=(string)
        Native.set_text_content(@native, string.to_s)
        string
      end

      def native_content=(string)
        self.content = string
      end

      def inner_html(*)
        Serializer.serialize_children(self, html: document.html?)
      end

      def inner_html=(markup)
        children.each(&:unlink)
        add_child(markup)
        markup
      end

      def children=(markup)
        self.inner_html = markup
      end

      #
      #  attributes
      #

      def attribute_nodes
        return [] unless element?

        flat = Native.attributes(@native)
        nodes = []
        index = 0
        while index < flat.length
          nodes << Attr.new(self, flat[index], flat[index + 1], flat[index + 2], flat[index + 3])
          index += 4
        end
        nodes
      end

      def attributes
        attribute_nodes.each_with_object({}) { |attribute, hash| hash[attribute.node_name] = attribute }
      end

      def attribute(name)
        attribute_nodes.find { |attribute| attribute.node_name == name.to_s }
      end

      def [](name)
        return nil unless element?

        value = Native.get_attribute(@native, name.to_s)
        value && value.to_s
      end
      alias_method :get_attribute, :[]
      alias_method :attr, :[]

      def []=(name, value)
        Native.set_attribute(@native, name.to_s, value.to_s)
        value
      end
      alias_method :set_attribute, :[]=

      def remove_attribute(name)
        Native.remove_attribute(@native, name.to_s)
        nil
      end
      alias_method :delete, :remove_attribute

      def key?(name)
        !self[name].nil?
      end
      alias_method :has_attribute?, :key?

      def keys
        attribute_nodes.map(&:node_name)
      end

      def values
        attribute_nodes.map(&:value)
      end

      # The element's namespace URI, or nil for HTML.  Nokogiri answers a
      # Namespace object with a prefix; here only the href is known, which is all
      # the serializer and the sanitizers ask for.
      def namespace_uri
        element? ? Native.namespace_uri(@native) : nil
      end

      def namespace
        nil
      end

      # The namespaces in scope, as Nokogiri answers them.  HTML has none that
      # survive parsing - a Microsoft Word "o:p" is one element named "o:p" - so
      # this is empty for the HTML documents this library is used on.
      def namespaces
        {}
      end

      # libxml2's xmlEncodeSpecialChars: the escaping Nokogiri applies to a
      # string it is about to put in a document.
      def encode_special_chars(string)
        string.to_s.gsub(/[&<>"\r]/, ENCODE_SPECIAL_CHARS)
      end

      def namespace_definitions
        []
      end

      #
      #  mutation
      #

      def add_child(node_or_markup)
        nodes = coerce(node_or_markup)
        nodes.each { |node| Native.append_child(@native, node.native) }
        reparented(node_or_markup, nodes)
      end
      alias_method :<<, :add_child

      def add_previous_sibling(node_or_markup)
        nodes = coerce(node_or_markup, parent)
        parent_native = Native.parent(@native)
        raise ArgumentError, "cannot add sibling to a node with no parent" if parent_native.nil?

        nodes.each { |node| Native.insert_before(parent_native, node.native, @native) }
        reparented(node_or_markup, nodes)
      end
      alias_method :before, :add_previous_sibling

      def add_next_sibling(node_or_markup)
        nodes = coerce(node_or_markup, parent)
        parent_native = Native.parent(@native)
        raise ArgumentError, "cannot add sibling to a node with no parent" if parent_native.nil?

        after = Native.next_sibling(@native)
        nodes.each { |node| Native.insert_before(parent_native, node.native, after) }
        reparented(node_or_markup, nodes)
      end
      alias_method :after, :add_next_sibling

      def replace(node_or_markup)
        nodes = coerce(node_or_markup, parent)
        parent_native = Native.parent(@native)
        raise ArgumentError, "cannot replace a node with no parent" if parent_native.nil?

        nodes.each { |node| Native.insert_before(parent_native, node.native, @native) }
        Native.remove_child(parent_native, @native)
        reparented(node_or_markup, nodes)
      end

      def swap(node_or_markup)
        replace(node_or_markup)
        self
      end

      def unlink
        parent_native = Native.parent(@native)
        Native.remove_child(parent_native, @native) if parent_native
        self
      end
      alias_method :remove, :unlink

      def parent=(other)
        other.add_child(self)
        other
      end

      def dup(level = 1, new_parent_document = document)
        new_parent_document.wrap(Native.clone_node(@native, level != 0))
      end

      def clone(*args)
        dup(*args)
      end

      #
      #  searching
      #

      def css(*args)
        rules, handler = CSS.extract_params(args)
        NodeSet.new(document, rules.flat_map { |rule| CSS.query(self, rule, handler) })
      end

      def at_css(*args)
        css(*args).first
      end

      def xpath(*args)
        paths = args.select { |arg| arg.is_a?(String) }
        NodeSet.new(document, paths.flat_map { |path| XPath.query(self, path) })
      end

      def at_xpath(*args)
        xpath(*args).first
      end

      def search(*args)
        paths = args.select { |arg| arg.is_a?(String) }
        rest = args - paths
        NodeSet.new(document, paths.flat_map do |path|
          if XPath.looks_like_xpath?(path)
            XPath.query(self, path)
          else
            rules, handler = CSS.extract_params([path] + rest)
            CSS.query(self, rules.first, handler)
          end
        end)
      end
      alias_method :/, :search

      def at(*args)
        search(*args).first
      end
      alias_method :%, :at

      def matches?(selector)
        CSS.matches?(self, selector)
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
      alias_method :to_xhtml, :to_html

      def serialize(*args)
        document.html? ? to_html : to_xml
      end

      def to_s
        serialize
      end

      def to_str
        to_s
      end

      def write_to(io, *args)
        io << serialize
      end

      private

      # Turns whatever was handed to add_child/replace/before/after into an array
      # of nodes: markup is parsed in the context of this node, which is what
      # makes `node.replace("<td>x</td>")` put a cell where a cell can live.
      # Nokogiri answers the node itself when it was handed a node, and a NodeSet
      # when it was handed markup or a set - Loofah calls .first on the result of
      # add_previous_sibling("<p>"), so the difference is load-bearing.
      def reparented(input, nodes)
        return nodes.first if input.is_a?(Node)

        NodeSet.new(document, nodes)
      end

      # +context+ is the node markup is parsed as if it stood inside.  Nokogiri
      # parses a String handed to #add_child in the context of the node itself,
      # and one handed to #replace or a sibling method in the context of its
      # parent - which is what keeps `script_node.replace("<h1>x</h1>")` from
      # being read as script text.
      def coerce(input, context = self)
        case input
        when Node then [input]
        when NodeSet then input.to_a
        when Array then input.flat_map { |item| coerce(item, context) }
        else document.parse_in_context(input.to_s, context)
        end
      end
    end

    # Elements get their own class only because Nokogiri names it; everything is
    # on Node, the way libxml2's binding has it.
    class Element < Node
    end

    class CharacterData < Node
    end

    class Text < CharacterData
      def initialize(string, document)
        document = document.document
        native = Native.create_text(document.native, string.to_s)
        send(:bind_native, native, document)
        document.register(self)
      end
    end

    class CDATA < Text
      def initialize(document, string)
        document = document.document
        native = Native.create_text(document.native, string.to_s)
        send(:bind_native, native, document)
        document.mark_cdata(native)
        document.register(self)
      end

      def type
        CDATA_SECTION_NODE
      end
    end

    class Comment < CharacterData
      def initialize(document, string)
        document = document.document
        native = Native.create_comment(document.native, string.to_s)
        send(:bind_native, native, document)
        document.register(self)
      end
    end

    class ProcessingInstruction < Node
    end

    class DTD < Node
    end

    # An attribute.  Not a wrapped DOM attribute: AngleSharp's IAttr is a value
    # object whose owner can change under it, and Nokogiri callers mutate
    # attributes through the node, so this holds the element and the name and
    # goes back to the element for every read and write.
    class Attr < Node
      attr_reader :value

      def initialize(element, name, value, prefix, namespace_uri)
        @element = element
        @name = name.to_s
        @value = value.to_s
        @prefix = prefix && prefix.to_s
        @namespace_uri = namespace_uri && namespace_uri.to_s
        @document = element.document
        @native = element.native
      end

      def type
        ATTRIBUTE_NODE
      end

      def name
        @prefix ? @name.sub(/\A#{Regexp.escape(@prefix)}:/, "") : @name
      end
      alias_method :node_name, :name

      # The qualified name, which is the key Node#attributes uses.
      def qualified_name
        @name
      end

      def value=(new_value)
        @value = new_value.to_s
        Native.set_attribute(@element.native, @name, @value)
        new_value
      end
      alias_method :content=, :value=

      def content
        @value
      end
      alias_method :text, :content
      alias_method :to_s, :content
      alias_method :inner_text, :content

      def namespace
        @prefix && Namespace.new(@prefix, @namespace_uri)
      end

      def parent
        @element
      end

      def unlink
        Native.remove_attribute(@element.native, @name)
        self
      end
      alias_method :remove, :unlink
      alias_method :delete, :unlink

      def element?
        false
      end

      def blank?
        false
      end

      def inspect
        "#<#{self.class.name}:#{format("0x%x", object_id)} name=#{name.inspect} value=#{@value.inspect}>"
      end
    end

    class Namespace
      attr_reader :prefix, :href

      def initialize(prefix, href)
        @prefix = prefix
        @href = href
      end
    end
  end
end
