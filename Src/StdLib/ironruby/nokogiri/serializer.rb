# frozen_string_literal: true

module Nokogiri
  # Markup generation.
  #
  # Nokogiri serializes through libxml2 (HTML4, XML) or through gumbo's
  # implementation of the WHATWG serializing algorithm (HTML5).  AngleSharp has
  # its own serializer, but it is not reachable per node with the options
  # Nokogiri's callers pass, and the escaping it applies is not the escaping
  # Nokogiri applies, so the markup is written here instead.
  #
  # The HTML rules are the WHATWG "HTML fragment serialisation algorithm":
  # void elements have no end tag, the children of a raw-text element are
  # written verbatim, and escaping differs between text (&amp;, &lt;, &gt;,
  # &nbsp;) and attribute values (&amp;, &quot;, &nbsp;).
  module Serializer # :nodoc:
    VOID_ELEMENTS = %w[
      area base basefont bgsound br col embed frame hr img input keygen link
      meta param source track wbr
    ].freeze

    RAW_TEXT_ELEMENTS = %w[
      style script xmp iframe noembed noframes plaintext noscript
    ].freeze

    # Only an HTML element is a raw-text element.  A <style> inside <svg> or
    # <math> is a foreign element whose text is escaped on the way out; treating
    # it as raw text is the mXSS hole rails-html-sanitizer regression-tests
    # (HackerOne 2503220, 2519936, 2519941).
    HTML_NAMESPACE = "http://www.w3.org/1999/xhtml"
    FOREIGN_ELEMENTS = %w[svg math].freeze

    TEXT_ESCAPES = {
      "&" => "&amp;",
      "<" => "&lt;",
      ">" => "&gt;",
      "\u00a0" => "&nbsp;",
    }.freeze

    ATTRIBUTE_ESCAPES = {
      "&" => "&amp;",
      "\"" => "&quot;",
      "\u00a0" => "&nbsp;",
    }.freeze

    # libxml2's HTML serializer escapes < and > inside attribute values as well;
    # the WHATWG algorithm HTML5 follows does not.  Loofah tests both.
    HTML4_ATTRIBUTE_ESCAPES = ATTRIBUTE_ESCAPES.merge(
      "<" => "&lt;",
      ">" => "&gt;",
    ).freeze

    # The doctype libxml2 writes for an HTML4 document that has none of its own.
    # Nokogiri::HTML4 output carries it, and Loofah's tests compare whole
    # documents, so it is reproduced rather than dropped.
    HTML4_DOCTYPE =
      "<!DOCTYPE html PUBLIC \"-//W3C//DTD HTML 4.0 Transitional//EN\" " \
      "\"http://www.w3.org/TR/REC-html40/loose.dtd\">\n"

    class << self
      def escape_text(string)
        string.gsub(/[&<>\u00a0]/, TEXT_ESCAPES)
      end

      def escape_attribute(string, html4 = false)
        if html4
          string.gsub(/[&"<>\u00a0]/, HTML4_ATTRIBUTE_ESCAPES)
        else
          string.gsub(/[&"\u00a0]/, ATTRIBUTE_ESCAPES)
        end
      end

      # +node+ is a Nokogiri node; +html+ picks the HTML rules over the XML ones.
      def serialize(node, html:)
        out = +""
        write(node, out, html)
        out
      end

      def serialize_children(node, html:)
        out = +""
        node.children.each { |child| write(child, out, html) }
        out
      end

      private

      def write(node, out, html)
        case node.type
        when Nokogiri::XML::Node::ELEMENT_NODE
          write_element(node, out, html)
        when Nokogiri::XML::Node::TEXT_NODE
          write_text(node, out, html)
        when Nokogiri::XML::Node::CDATA_SECTION_NODE
          if html
            # libxml2's HTML serializer writes the content of a CDATA node
            # verbatim, which is what makes Loofah's cdata_escape work.
            out << node.content
          else
            out << "<![CDATA[" << node.content << "]]>"
          end
        when Nokogiri::XML::Node::COMMENT_NODE
          out << "<!--" << node.content << "-->"
        when Nokogiri::XML::Node::PI_NODE
          out << "<?" << node.name << " " << node.content.to_s << "?>"
        when Nokogiri::XML::Node::DTD_NODE
          out << "<!DOCTYPE " << node.name << ">"
        when Nokogiri::XML::Node::DOCUMENT_NODE
          write_document(node, out, html)
        else
          node.children.each { |child| write(child, out, html) }
        end
      end

      def write_document(node, out, html)
        if html && node.html4?
          out << HTML4_DOCTYPE unless node.children.any? { |c| c.type == Nokogiri::XML::Node::DTD_NODE }
        end
        node.children.each { |child| write(child, out, html) }
        out << "\n" if html && node.html4?
      end

      def write_element(node, out, html)
        name = node.name
        # libxml2 does not create an empty <head>; AngleSharp always does,
        # because the HTML5 tree construction algorithm says to.  Dropping it
        # again for HTML4 output keeps Nokogiri::HTML4 markup recognisable.
        return if html && name == "head" && node.children.empty? && node.document.html4?
        out << "<" << name
        node.attribute_nodes.each do |attribute|
          out << " " << attribute.name
          # < and > are escaped in an attribute of a foreign element as well as in
          # HTML4 output.  The HTML5 algorithm leaves them alone, but a <style>
          # inside <math> is not a raw-text element while the same markup re-read
          # outside <math> is, so an unescaped "</style>" sitting in one of its
          # attributes is an mXSS payload (HackerOne 2519936).  An entity there
          # reads back as the same character, so nothing is lost.
          strict = html && (node.document.html4? || !raw_text_host?(node))
          out << "=\"" << escape_attribute(attribute.value.to_s, strict) << "\""
        end
        if html && VOID_ELEMENTS.include?(name)
          out << ">"
          return
        end
        if !html && node.children.empty?
          out << "/>"
          return
        end
        out << ">"
        node.children.each { |child| write(child, out, html) }
        out << "</" << name << ">"
      end

      # Whether a <style> or <script> here is a raw-text element.  It is one only
      # in HTML: inside <svg> or <math> the same tag is a foreign element whose
      # text is escaped on the way out, and writing it verbatim is the mXSS hole
      # rails-html-sanitizer regression-tests.
      #
      # The ancestors are checked as well as the element's own namespace, because
      # the tree builder puts an HTML-namespace <style> under <mglyph> for the
      # markup in HackerOne 2519941 - and a <style> with a foreign element over it
      # is not something a browser reads as CSS either way, so escaping is both
      # the safe answer and the one gumbo gives.
      def raw_text_host?(element)
        uri = element.namespace_uri
        return false unless uri.nil? || uri.empty? || uri == HTML_NAMESPACE

        node = element.parent
        while node && node.element?
          return false if FOREIGN_ELEMENTS.include?(node.name)

          node = node.parent
        end
        true
      end

      def write_text(node, out, html)
        content = node.content
        parent = node.parent
        if html && parent && parent.element? && RAW_TEXT_ELEMENTS.include?(parent.name) &&
           raw_text_host?(parent)
          out << content
        elsif html
          out << escape_text(content)
        else
          out << content.gsub(/[&<>]/, TEXT_ESCAPES)
        end
      end
    end
  end
end
