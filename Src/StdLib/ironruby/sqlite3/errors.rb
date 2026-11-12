# frozen_string_literal: true
#
# The part of the sqlite3 gem's lib/sqlite3/errors.rb that is Ruby rather than
# type.  The hierarchy itself - SQLite3::Exception and its 26 subclasses - is
# defined in C# (Src/Libraries/Sqlite3/Sqlite3.cs), because that is where the
# errors are raised from and a [RubyException] class is the only kind IronRuby
# can throw as a CLR exception.  #code, #sql and #sql_offset are instance
# variables the C# side sets before throwing, the way the core sets KeyError#key.
#
# Copyright (c) 2004-2024, Jamis Buck, Luis Lavena, Aaron Patterson,
# Mike Dalessio, et al.  Distributed under the 3-clause BSD license; see
# Src/StdLib/ironruby/sqlite3/LICENSE for the full text.

require "sqlite3/constants"

module SQLite3
  class Exception < ::StandardError
    # A convenience for accessing the error code for this exception.
    attr_reader :code

    # If the error is associated with a SQL query, this is the query
    attr_reader :sql

    # If the error is associated with a particular offset in a SQL query, this is the non-negative
    # offset. If the offset is not available, this will be -1.
    attr_reader :sql_offset

    def message
      [super, sql_error].compact.join(":\n")
    end

    private def sql_error
      return nil unless @sql
      return @sql.chomp unless @sql_offset >= 0

      offset = @sql_offset
      sql.lines.flat_map do |line|
        if offset >= 0 && line.length > offset
          blanks = " " * offset
          offset = -1
          [line.chomp, blanks + "^"]
        else
          offset -= line.length if offset
          line.chomp
        end
      end.join("\n")
    end
  end
end
