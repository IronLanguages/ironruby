# frozen_string_literal: true
#
# What ::DidYouMean is autoloaded from while did_you_mean is enabled but not loaded yet (see
# ironruby/error_extensions.rb): error_highlight is loaded ahead of it, as MRI's startup does.
IronRuby::ErrorExtensions.load(true)
