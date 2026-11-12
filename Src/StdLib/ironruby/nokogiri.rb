# frozen_string_literal: true

# Nokogiri, for IronRuby.
#
# The real nokogiri is a C extension over libxml2 (XML and HTML4) and gumbo
# (HTML5).  Neither library can be compiled for this runtime, and nokogiri is a
# hard dependency of rails-html-sanitizer -> loofah -> actionview and of
# rails-dom-testing -> actionpack, so `gem install actionview` on IronRuby stops
# at a C compiler unless IronRuby provides nokogiri itself.  That is what this
# is, and why there is a default gemspec for it.
#
# What is underneath: AngleSharp, a .NET implementation of the WHATWG HTML
# parsing and serialization specification with a DOM and a CSS selector engine -
# the same specification gumbo implements.  Src/Libraries/Nokogiri/Nokogiri.cs is
# the thin layer that reaches the DOM members C# can see and Ruby cannot; every
# bit of nokogiri-shaped behaviour is here in Ruby.
#
# What is faithful: HTML5 parsing and serialization, fragments, the node and
# NodeSet API, CSS selection (through AngleSharp's selector engine, including
# nokogiri's custom pseudo-class handlers), document decorators, and the
# scrubbing surface loofah drives.
#
# What is not: HTML4 is parsed by the HTML5 algorithm rather than by libxml2's
# older one, and only its serialization is HTML4-shaped (see nokogiri/html.rb);
# XPath is this library's own evaluator and covers location paths and the common
# predicate functions rather than all of XPath 1.0 (see nokogiri/xpath.rb);
# Nokogiri::CSS.xpath_for, the Builder API, Reader, Schema/RelaxNG, XSLT and
# libxml2's parse-option flags are not implemented and say so when called.  SAX
# is here, but replayed off the parsed tree rather than streamed (see
# nokogiri/xml/sax.rb).

load_assembly 'IronRuby.Libraries', 'IronRuby.StandardLibrary.Nokogiri'

module Nokogiri
  # Raised for markup or selectors this library cannot make sense of.  Nokogiri
  # puts XML::SyntaxError under it, and callers rescue the parent.
  class SyntaxError < ::StandardError; end

  module XML
    class SyntaxError < Nokogiri::SyntaxError
      def initialize(message = nil)
        super
        @message = message
      end

      def to_s
        @message.to_s
      end
    end
  end
end

require "nokogiri/version"
require "nokogiri/serializer"
require "nokogiri/css"
require "nokogiri/xpath"
require "nokogiri/xml/node"
require "nokogiri/xml/node_set"
require "nokogiri/xml/document"
require "nokogiri/xml/document_fragment"
require "nokogiri/xml/sax"
require "nokogiri/html"

module Nokogiri
  module XML
    class << self
      def parse(*args, &block)
        Document.parse(*args, &block)
      end

      def fragment(markup)
        DocumentFragment.parse(markup)
      end

      def Document(*args)
        Document.parse(*args)
      end
    end
  end
end
