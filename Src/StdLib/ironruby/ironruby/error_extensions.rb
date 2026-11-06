# frozen_string_literal: true
#
# error_highlight, did_you_mean and syntax_suggest, which MRI's startup loads, loaded on first
# use instead: requiring them up front costs IronRuby's startup about half a second, and nothing
# needs them before an error message is asked for. The ir command line requires
# ironruby/error_highlight, ironruby/did_you_mean and ironruby/syntax_suggest, which enable them
# here, unless --disable-error_highlight, --disable-did_you_mean, --disable-syntax_suggest (or
# --disable-gems) is given.
#
# The first #detailed_message of one of the errors they extend - which is what the error
# printer asks for - loads them; they then prepend their modules ahead of the Loader below, so
# the message is asked for again to go through them. error_highlight and did_you_mean are loaded
# together, in MRI's order, so that did_you_mean's suggestions come after error_highlight's
# snippet. syntax_suggest, which takes seconds to load here, waits for a SyntaxError in a file it
# can read, the only kind it has anything to say about.

module IronRuby
  module ErrorExtensions # :nodoc:
    # in the order MRI's gem_prelude requires them
    FEATURES = {
      error_highlight: [::NameError, ::TypeError, ::ArgumentError],
      did_you_mean: [::NameError, ::KeyError, ::LoadError, (::NoMatchingPatternKeyError if defined?(::NoMatchingPatternKeyError))].compact,
      syntax_suggest: [::SyntaxError],
    }.freeze

    @enabled = {}
    @loaded = false
    @syntax_suggest_loaded = false

    def self.enable(feature)
      @enabled[feature] = true
      FEATURES.fetch(feature).each { |error_class| error_class.prepend(Loader) }
      # code that names ::DidYouMean first (Thor does) loads both, so that the order holds
      ::Object.autoload(:DidYouMean, "ironruby/load_error_extensions") if feature == :did_you_mean && !::Object.const_defined?(:DidYouMean, false)
    end

    # `autoloading`: called by the autoload of ::DidYouMean, which did_you_mean is to define
    def self.load(autoloading = false)
      return false if @loaded
      @loaded = true
      [:error_highlight, :did_you_mean].each do |feature|
        next unless @enabled[feature]
        begin
          if feature == :did_you_mean && !autoloading && ::Object.autoload?(:DidYouMean)
            ::Object.send(:remove_const, :DidYouMean)
          end
          require feature.to_s
        rescue ::LoadError, ::StandardError
          # an error report goes on without it
        end
      end
      true
    end

    def self.load_for(error)
      return load unless ::SyntaxError === error
      return false if @syntax_suggest_loaded || !@enabled[:syntax_suggest]
      path = error.path
      return false unless path.is_a?(::String) && ::File.file?(path)
      @syntax_suggest_loaded = true
      begin
        require "syntax_suggest/core_ext"
        require "syntax_suggest/api"
        # Its search gives up after SYNTAX_SUGGEST_TIMEOUT seconds, 1 unless set - which here is
        # spent before the search is warm, and a timeout prints a backtrace of its own.
        unless ::ENV.key?("SYNTAX_SUGGEST_TIMEOUT")
          ::SyntaxSuggest.send(:remove_const, :TIMEOUT_DEFAULT)
          ::SyntaxSuggest.const_set(:TIMEOUT_DEFAULT, 10)
        end
      rescue ::LoadError, ::StandardError
        return false
      end
      true
    end

    module Loader
      def detailed_message(**options)
        if ::IronRuby::ErrorExtensions.load_for(self)
          return detailed_message(**options)
        end
        super
      end
    end
  end
end
