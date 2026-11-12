# frozen_string_literal: true

module Nokogiri
  # The version of the nokogiri API this library provides, not a version of the
  # nokogiri gem: 1.18.0 is the API level implemented here (HTML4/HTML5/XML
  # documents and fragments, Node, NodeSet, CSS, a subset of XPath).  It is
  # deliberately above 1.14.0, which is the floor Loofah checks before it turns
  # on Loofah::HTML5 - see Nokogiri.uses_gumbo?, which answers true here because
  # the parser underneath really is an HTML5 tree-construction implementation.
  VERSION = "1.18.0"

  # What Nokogiri::VERSION_INFO reports.  The libxml2 and libgumbo keys real
  # nokogiri fills in are absent: there is no libxml2 here.
  VERSION_INFO = {
    "warnings" => [],
    "nokogiri" => {
      "version" => VERSION,
    },
    "ruby" => {
      "version" => ::RUBY_VERSION,
      "platform" => ::RUBY_PLATFORM,
      "description" => ::RUBY_DESCRIPTION,
      "engine" => ::RUBY_ENGINE,
    },
    "dotnet" => {
      "parser" => "AngleSharp",
    },
  }.freeze

  class VersionInfo # :nodoc:
    def self.instance
      @instance ||= new
    end

    def jruby?
      false
    end

    # There is no libxml2 here, and callers use this to decide whether to work
    # around libxml2 bugs.  Answering false is the truth and skips the
    # workarounds, which is what the JRuby build does too.
    def libxml2?
      false
    end

    def libxml2_has_iconv?
      false
    end

    def windows?
      ::RUBY_PLATFORM.include?("mswin") || ::RUBY_PLATFORM.include?("mingw")
    end

    def loaded_parser_version
      nil
    end

    def to_hash
      VERSION_INFO.dup
    end

    def to_markdown
      require "yaml"
      "# Nokogiri (#{VERSION})\n\n#{to_hash.to_yaml}"
    end
  end

  class << self
    def jruby?
      false
    end

    def uses_libxml?(requirement = nil)
      false
    end

    # True: the HTML parser underneath (AngleSharp) implements the WHATWG
    # tokenizer and tree-construction algorithm, the same specification gumbo
    # implements, so Nokogiri::HTML5 is real here.  Loofah asks this before it
    # enables its HTML5 support.
    def uses_gumbo?
      true
    end

    def libxml2_patches
      []
    end
  end
end
