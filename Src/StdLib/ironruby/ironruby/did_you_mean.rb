# frozen_string_literal: true
#
# did_you_mean, which MRI's gem prelude requires at startup, loaded on first use instead:
# requiring it up front costs IronRuby's startup about half a second, and nothing needs it
# before an error message is asked for. The ir command line requires this file unless
# --disable-did_you_mean (or --disable-gems) is given.
#
# ::DidYouMean is autoloaded for code that names it. The first #detailed_message of one of
# the errors did_you_mean corrects - which is what the error printer asks for - loads it;
# did_you_mean then prepends DidYouMean::Correctable ahead of the module below, so the
# message is asked for again to go through it.

Object.autoload(:DidYouMean, "did_you_mean") unless Object.const_defined?(:DidYouMean, false)

module IronRuby
  module DidYouMeanLoader # :nodoc:
    @loaded = false

    def self.load
      return false if @loaded
      @loaded = true
      ::Object.const_get(:DidYouMean)
      true
    rescue ::LoadError, ::StandardError
      false
    end

    def detailed_message(**options)
      if ::IronRuby::DidYouMeanLoader.load
        return detailed_message(**options)
      end
      super
    end
  end

  [::NameError, ::KeyError, ::LoadError, (::NoMatchingPatternKeyError if defined?(::NoMatchingPatternKeyError))].compact.each do |error_class|
    error_class.prepend(DidYouMeanLoader)
  end
end
