# frozen_string_literal: true

# Nokogiri's SAX interface.
#
# libxml2 streams these events out of its own parser as it reads. There is no
# streaming parser under this shim - AngleSharp builds a tree - so the events
# are replayed off that tree instead, depth first, in document order. What a
# handler cannot see this way is a document too large to hold in memory, which
# is the reason SAX exists; what it can see is every event, in the right order,
# with the right arguments, which is the reason libraries use it.
#
# Written because a SAX handler is how a library parses XML when it does not
# want a DOM: aws-sdk-core's Query and REST-XML protocols parse every response
# through Nokogiri::XML::SAX::Parser, so all of aws-sdk-* depends on it.

module Nokogiri
  module XML
    module SAX
      # The no-op handler. A real one subclasses this and overrides what it needs.
      class Document
        def start_document; end
        def end_document; end
        def xmldecl(version, encoding, standalone); end
        def start_element(name, attrs = []); end
        def end_element(name); end
        def start_element_namespace(name, attrs = [], prefix = nil, uri = nil, ns = []); end
        def end_element_namespace(name, prefix = nil, uri = nil); end
        def characters(string); end
        def comment(string); end
        def cdata_block(string); end
        def processing_instruction(name, content); end
        def warning(string); end
        def error(string); end
      end

      # One attribute, as the namespace-aware callbacks report it.
      class Parser
        class Attribute < Struct.new(:localname, :prefix, :uri, :value)
        end

        ENCODINGS = {
          'NONE' => 0, 'UTF-8' => 1, 'UTF8' => 1, 'ASCII' => 2, 'US-ASCII' => 2
        }.freeze

        attr_accessor :document, :encoding

        def initialize(doc = Document.new, encoding = 'UTF-8')
          @document = doc
          @encoding = encoding
        end

        # +input+ is a String or anything answering #read.
        def parse(input, &block)
          string = input.respond_to?(:read) ? input.read : input.to_s
          parse_memory(string, &block)
        end

        def parse_io(io, encoding = nil, &block)
          parse_memory(io.read, &block)
        end

        def parse_file(filename, &block)
          raise ArgumentError, 'no such file' unless File.file?(filename)
          parse_memory(File.read(filename), &block)
        end

        def parse_memory(string, &block)
          raise ArgumentError, 'input must not be nil' if string.nil?
          yield self if block
          send_event(:start_document)
          begin
            doc = Nokogiri::XML(string)
            emit(doc.root) if doc.root
          rescue Nokogiri::SyntaxError => e
            send_event(:error, e.message)
          end
          send_event(:end_document)
          self
        end

        private

        # A handler need not be a SAX::Document subclass - libxml2 asks each callback
        # whether it is there, and a handler that defines only the namespace-aware ones is
        # ordinary (aws-sdk-core's XML engine is exactly that). So ask too.
        def send_event(name, *args)
          document.__send__(name, *args) if document.respond_to?(name)
        end

        def emit(node)
          case node.type
          when Nokogiri::XML::Node::ELEMENT_NODE
            name = node.name
            pairs = node.attribute_nodes.map { |a| [a.name, a.value] }
            send_event(:start_element, name, pairs)
            send_event(:start_element_namespace,
              local_name(name), node.attribute_nodes.map { |a|
                Attribute.new(local_name(a.name), prefix_of(a.name), nil, a.value)
              }, prefix_of(name), node.namespace && node.namespace.href, []
            )
            node.children.each { |child| emit(child) }
            send_event(:end_element_namespace, local_name(name), prefix_of(name),
                       node.namespace && node.namespace.href)
            send_event(:end_element, name)
          when Nokogiri::XML::Node::TEXT_NODE
            send_event(:characters, node.content)
          when Nokogiri::XML::Node::CDATA_SECTION_NODE
            send_event(:cdata_block, node.content)
          when Nokogiri::XML::Node::COMMENT_NODE
            send_event(:comment, node.content)
          when Nokogiri::XML::Node::PI_NODE
            send_event(:processing_instruction, node.name, node.content)
          end
        end

        def local_name(name)
          name.include?(':') ? name.split(':', 2).last : name
        end

        def prefix_of(name)
          name.include?(':') ? name.split(':', 2).first : nil
        end
      end

      # nokogiri's push parser: the same events, fed in chunks. The chunks are
      # gathered and parsed at #finish, for the same reason as above.
      class PushParser
        attr_accessor :document

        def initialize(doc = Document.new, file_name = nil, encoding = 'UTF-8')
          @document = doc
          @chunks = +''
          @finished = false
        end

        def <<(chunk, last_chunk = false)
          @chunks << chunk.to_s
          finish if last_chunk
          self
        end
        alias write <<

        def finish
          return self if @finished
          @finished = true
          Parser.new(@document).parse_memory(@chunks)
          self
        end

        def finished?
          @finished
        end
      end
    end
  end
end
