# frozen_string_literal: true
#
# syntax_suggest is on unless --disable-syntax_suggest (or --disable-gems) is given, and loaded
# on first use: see ironruby/error_extensions.rb.
require_relative "error_extensions"
IronRuby::ErrorExtensions.enable(:syntax_suggest)
