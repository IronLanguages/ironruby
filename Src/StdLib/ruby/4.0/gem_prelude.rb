# frozen_string_literal: true
# IronRuby: modern RubyGems is loaded eagerly at startup, the way CRuby >= 1.9.3
# does it (MRI's gem_prelude.rb is a one-liner `require "rubygems"` there too).
#
# ruby4.rb - the Ruby half of the core library - comes first: it supplies
# Kernel#require_relative, which rubygems.rb uses on its very first line.
require "ruby4"
require "rubygems"
