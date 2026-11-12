# frozen_string_literal: true
#
# Vendored from the sqlite3 gem 2.9.6 (lib/sqlite3/version_info.rb).
#
# Copyright (c) 2004-2024, Jamis Buck, Luis Lavena, Aaron Patterson,
# Mike Dalessio, et al.  Distributed under the 3-clause BSD license; see
# Src/StdLib/ironruby/sqlite3/LICENSE for the full text.

module SQLite3
  # a hash of descriptive metadata about the current version of the sqlite3 gem
  VERSION_INFO = {
    ruby: RUBY_DESCRIPTION,
    gem: {
      version: SQLite3::VERSION
    },
    sqlite: {
      compiled: SQLite3::SQLITE_VERSION,
      loaded: SQLite3::SQLITE_LOADED_VERSION,
      packaged: SQLite3::SQLITE_PACKAGED_LIBRARIES,
      precompiled: SQLite3::SQLITE_PRECOMPILED_LIBRARIES,
      sqlcipher: SQLite3.sqlcipher?,
      threadsafe: SQLite3.threadsafe?
    }
  }
end
