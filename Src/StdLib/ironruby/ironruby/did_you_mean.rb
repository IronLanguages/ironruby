# frozen_string_literal: true
#
# did_you_mean is on unless --disable-did_you_mean (or --disable-gems) is given, and loaded on
# first use: see ironruby/error_extensions.rb.
require_relative "error_extensions"
IronRuby::ErrorExtensions.enable(:did_you_mean)
