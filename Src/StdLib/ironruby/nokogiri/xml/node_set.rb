# frozen_string_literal: true

module Nokogiri
  module XML
    # An ordered collection of nodes.  A NodeSet is what every search answers,
    # and it is a node container in its own right: #css and #xpath search within
    # it, #remove unlinks everything in it, #text concatenates.
    class NodeSet
      include Enumerable

      attr_accessor :document

      def initialize(document, list = [])
        @document = document
        @nodes = list.to_a
        document.decorate(self) if document.respond_to?(:decorate)
        yield self if block_given?
      end

      def to_a
        @nodes.dup
      end
      alias_method :to_ary, :to_a

      def each
        return to_enum(:each) unless block_given?

        @nodes.each { |node| yield node }
        self
      end

      def length
        @nodes.length
      end
      alias_method :size, :length

      def empty?
        @nodes.empty?
      end

      def [](*args)
        result = @nodes[*args]
        result.is_a?(Array) ? NodeSet.new(@document, result) : result
      end
      alias_method :slice, :[]

      def at(index_or_selector, *rest)
        if index_or_selector.is_a?(Integer)
          @nodes[index_or_selector]
        else
          search(index_or_selector, *rest).first
        end
      end
      alias_method :%, :at

      def first(count = nil)
        count ? NodeSet.new(@document, @nodes.first(count)) : @nodes.first
      end

      def last
        @nodes.last
      end

      def index(node = nil, &block)
        block ? @nodes.index(&block) : @nodes.index(node)
      end

      def include?(node)
        @nodes.include?(node)
      end

      def push(node)
        @nodes.push(node)
        self
      end
      alias_method :<<, :push

      def delete(node)
        @nodes.delete(node)
      end

      def pop
        @nodes.pop
      end

      def shift
        @nodes.shift
      end

      def +(other)
        NodeSet.new(@document, @nodes + other.to_a)
      end
      alias_method :|, :+
      alias_method :union, :+

      def -(other)
        NodeSet.new(@document, @nodes - other.to_a)
      end

      def &(other)
        NodeSet.new(@document, @nodes & other.to_a)
      end
      alias_method :intersection, :&

      def ==(other)
        return false unless other.respond_to?(:to_a)

        to_a == other.to_a
      end

      def dup
        NodeSet.new(@document, @nodes.dup)
      end
      alias_method :clone, :dup

      def reverse
        NodeSet.new(@document, @nodes.reverse)
      end

      def children
        NodeSet.new(@document, @nodes.flat_map { |node| node.children.to_a })
      end

      def filter(selector)
        NodeSet.new(@document, @nodes.select { |node| node.matches?(selector) })
      end

      # Nokogiri's NodeSet#css matches the nodes in the set as well as their
      # descendants - `assert_select elements, "#1"` in rails-dom-testing selects
      # an element the set already holds - so a self match comes first, then the
      # descendants, and the result is deduplicated.
      def css(*args)
        rules, handler = CSS.extract_params(args)
        NodeSet.new(@document, rules.flat_map { |rule| CSS.query_set(@nodes, @document, rule, handler) })
      end

      def at_css(*args)
        css(*args).first
      end

      def xpath(*args)
        NodeSet.new(@document, @nodes.flat_map { |node| node.xpath(*args).to_a })
      end

      def at_xpath(*args)
        xpath(*args).first
      end

      def search(*args)
        NodeSet.new(@document, @nodes.flat_map { |node| node.search(*args).to_a })
      end
      alias_method :/, :search

      def attr(key, value = nil, &block)
        if value.nil? && block.nil?
          return first && first[key]
        end

        each do |node|
          node[key] = block ? block.call(node) : value
        end
        self
      end
      alias_method :set, :attr
      alias_method :attribute, :attr

      def remove_attr(name)
        each { |node| node.remove_attribute(name) }
        self
      end

      def remove
        each(&:unlink)
        self
      end
      alias_method :unlink, :remove

      def wrap(markup)
        each do |node|
          wrapper = node.document.parse_in_context(markup, node.parent).first
          node.add_previous_sibling(wrapper)
          wrapper.add_child(node)
        end
        self
      end

      def before(markup)
        each { |node| node.add_previous_sibling(markup) }
        self
      end

      def after(markup)
        each { |node| node.add_next_sibling(markup) }
        self
      end

      def text
        map(&:text).join
      end
      alias_method :inner_text, :text
      alias_method :content, :text

      def inner_html(*args)
        map { |node| node.inner_html(*args) }.join
      end

      def to_html(*args)
        map { |node| node.to_html(*args) }.join
      end

      def to_xml(*args)
        map { |node| node.to_xml(*args) }.join
      end

      def to_s
        map(&:to_s).join
      end

      def serialize(*args)
        map { |node| node.serialize(*args) }.join
      end

      def inspect
        "[#{map(&:inspect).join(", ")}]"
      end

    end
  end
end
