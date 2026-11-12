# frozen_string_literal: true

module Nokogiri
  module XML
    # A parentless run of nodes.
    #
    # Nokogiri's fragment is a real node - type 11 - that belongs to a document
    # without being in it, and that is what makes DocumentFragment.new(doc, tags)
    # work: the document supplies the node factories and the decorators, the
    # fragment owns the parsed nodes.  Loofah subclasses this and relies on the
    # two-argument constructor, so it is the primitive here and .parse is the
    # convenience built on top of it.
    class DocumentFragment < Node
      attr_reader :errors

      class << self
        def document_class
          XML::Document
        end

        def parse(tags, options = nil)
          markup = tags.respond_to?(:read) ? tags.read : tags.to_s
          document = document_class.new
          document.encoding = markup.encoding.name if markup.respond_to?(:encoding)
          new(document, markup)
        end
      end

      def initialize(document, tags = nil, ctx = nil)
        document = document.document
        @document = document
        @errors = []
        @native = Native.create_fragment(document.native)
        document.register(self)
        return if tags.nil?

        markup = tags.respond_to?(:read) ? tags.read : tags.to_s
        document.parse_in_context(markup, ctx).each do |node|
          Native.append_child(@native, node.native)
        end
      end

      def type
        DOCUMENT_FRAG_NODE
      end

      def name
        "#document-fragment"
      end

      def fragment?
        true
      end

      def parent
        nil
      end

      def document
        @document
      end

      def to_html(options = {})
        Serializer.serialize_children(self, html: true)
      end

      def to_xml(options = {})
        Serializer.serialize_children(self, html: false)
      end

      def to_s
        document.html? ? to_html : to_xml
      end

      def serialize(*args)
        to_s
      end

      def inner_html(*args)
        to_s
      end

      def content
        Native.text_content(@native).to_s
      end
      alias_method :text, :content
      alias_method :inner_text, :content

      def dup(level = 1)
        copy = self.class.new(document)
        Native.children(Native.clone_node(@native, level != 0)).each do |child|
          Native.append_child(copy.native, child)
        end
        copy
      end
      alias_method :clone, :dup

      def inspect
        "#<#{self.class.name}:#{format("0x%x", object_id)} #{to_s.inspect}>"
      end
    end
  end
end
