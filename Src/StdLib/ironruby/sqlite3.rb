# frozen_string_literal: true
#
# The sqlite3 gem, provided by IronRuby itself.
#
# `gem install sqlite3` cannot work here: the gem is a C extension, and the
# precompiled binaries it ships are ELF/PE objects linked against CRuby.  What
# IronRuby ships instead is the gem's own Ruby half (Src/StdLib/ironruby/sqlite3,
# vendored from sqlite3 2.9.6) on top of a C# implementation of its C half
# (Src/Libraries/Sqlite3/Sqlite3.cs), which binds SQLite through SQLitePCL.raw -
# the native SQLite that the Microsoft.Data.Sqlite package carries.
#
# A default gemspec in Src/StdLib/ruby/gems/4.0.0/specifications/default makes
# RubyGems agree that sqlite3 2.9.6 is already installed, so `gem "sqlite3"` in a
# Gemfile resolves against this rather than against rubygems.org.  That is what
# lets ActiveRecord's sqlite3 adapter connect.

load_assembly 'IronRuby.Libraries', 'IronRuby.StandardLibrary.Sqlite3'

module SQLite3
  # (String) the version of the sqlite3 gem this implements.
  VERSION = "2.9.6"

  # A String subclass whose instances are always bound as a BLOB, whatever
  # their encoding.  (In the real gem this is defined by the C extension.)
  class Blob < String
  end

  # The version of SQLite IronRuby was built against and the one actually
  # loaded are the same library: the one in the Microsoft.Data.Sqlite package.
  SQLITE_VERSION = sqlite_version_string
  SQLITE_LOADED_VERSION = SQLITE_VERSION
  SQLITE_VERSION_NUMBER = libversion

  # The native SQLite is packaged with the implementation and is precompiled.
  SQLITE_PACKAGED_LIBRARIES = true
  SQLITE_PRECOMPILED_LIBRARIES = true

  # Was sqlite3 compiled with thread safety on?
  def self.threadsafe?
    threadsafe > 0
  end
end

require "sqlite3/errors"
require "sqlite3/constants"
require "sqlite3/database"
require "sqlite3/version_info"
