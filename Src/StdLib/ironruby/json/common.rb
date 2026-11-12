# frozen_string_literal: true
#
# MRI's json splits its implementation across json/common, json/ext and json/version, and
# code that wants one piece requires that file. IronRuby's JSON is one library written in
# C# (Src/Libraries/Json), loaded by json.rb, so each piece is all of it.
require 'json'
