# frozen_string_literal: true

module Nokogiri
  # The HTML4 parser.
  #
  # Nokogiri's HTML4 is libxml2's HTML parser, which predates the HTML5 parsing
  # specification and has its own recovery rules.  There is no libxml2 here, so
  # HTML4 parses with the same WHATWG algorithm HTML5 parses with, and differs
  # from it only where the difference is visible to the callers that matter: the
  # HTML4 serialization carries libxml2's doctype and drops an empty <head>, and
  # the contents of <style> and <script> are CDATA sections the way libxml2
  # reports them.  Markup on which the two parsers genuinely disagree - unclosed
  # tables, misnested formatting elements - is parsed the HTML5 way.
  # What an HTML document has on top of an XML one. Small, but #title is reached for
  # constantly - it is the first thing anything scraping a page asks for.
  module HtmlDocument
    # The text of <title>, or nil when the document has none.
    def title
      node = at_xpath('//title') || at_css('title')
      node && node.text
    end

    # Replaces the text of <title>, creating <head><title> when there is none.
    def title=(text)
      node = at_xpath('//title') || at_css('title')
      if node
        node.content = text
        return text
      end

      head = at_xpath('//head') || at_css('head')
      unless head
        html = at_xpath('//html') || at_css('html') || root or return text
        head = Nokogiri::XML::Node.new('head', self)
        html.children.first ? html.children.first.add_previous_sibling(head) : html.add_child(head)
      end
      title = Nokogiri::XML::Node.new('title', self)
      title.content = text
      head.add_child(title)
      text
    end

    # The <meta> element that declares the encoding, or nil.
    def meta_encoding
      node = at_xpath('//meta[@charset]') || at_css('meta[charset]')
      node && node['charset']
    end
  end

  module HTML4
    class Document < Nokogiri::XML::Document
      include HtmlDocument

      def self.document_kind
        :html4
      end
    end

    class DocumentFragment < Nokogiri::XML::DocumentFragment
      def self.document_class
        HTML4::Document
      end
    end

    class << self
      def parse(*args, &block)
        Document.parse(*args, &block)
      end

      def fragment(markup, encoding = nil)
        DocumentFragment.parse(markup)
      end

      def call(*args, &block)
        parse(*args, &block)
      end
    end
  end

  # The HTML5 parser: AngleSharp's implementation of the WHATWG tokenizer and
  # tree-construction algorithm, which is the algorithm gumbo implements for
  # nokogiri.
  module HTML5
    class Document < Nokogiri::XML::Document
      include HtmlDocument

      def self.document_kind
        :html5
      end

      def self.parse(string_or_io, url = nil, encoding = nil, **options, &block)
        super(string_or_io, url, encoding, nil, &block)
      end
    end

    class DocumentFragment < Nokogiri::XML::DocumentFragment
      def self.document_class
        HTML5::Document
      end

      def self.parse(tags, encoding = nil, **options)
        super(tags)
      end

      def initialize(document, tags = nil, ctx = nil, **options)
        super(document, tags, ctx)
      end
    end

    class << self
      def parse(string_or_io, url = nil, encoding = nil, **options, &block)
        Document.parse(string_or_io, url, encoding, **options, &block)
      end

      def fragment(markup, encoding = nil, **options)
        DocumentFragment.parse(markup, encoding, **options)
      end

      def get(uri, options = {})
        raise NotImplementedError, "Nokogiri::HTML5.get is not implemented on IronRuby"
      end
    end
  end

  # Nokogiri::HTML has always been the HTML4 parser, and Loofah aliases its own
  # HTML to HTML4 to match.
  HTML = HTML4

  class << self
    def HTML4(*args, &block)
      HTML4::Document.parse(*args, &block)
    end

    def HTML5(input, url = nil, encoding = nil, **options, &block)
      HTML5::Document.parse(input, url, encoding, **options, &block)
    end

    def HTML(*args, &block)
      HTML4::Document.parse(*args, &block)
    end

    def XML(*args, &block)
      XML::Document.parse(*args, &block)
    end

    def parse(string, url = nil, encoding = nil, options = nil)
      if string =~ /^\s*<[^Hh>]*html/ || string !~ /^\s*</
        HTML4::Document.parse(string, url, encoding, options)
      else
        XML::Document.parse(string, url, encoding, options)
      end
    end

    def make(input = nil, opts = {}, &block)
      raise NotImplementedError, "Nokogiri.make (the Builder API) is not implemented on IronRuby"
    end
  end
end
