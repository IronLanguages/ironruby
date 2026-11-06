# frozen_string_literal: true
#
# error_highlight is on unless --disable-error_highlight (or --disable-gems) is given, and loaded
# on first use: see ironruby/error_extensions.rb.
require_relative "error_extensions"
IronRuby::ErrorExtensions.enable(:error_highlight)
