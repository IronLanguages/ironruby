# frozen_string_literal: true
#
# Vendored from the sqlite3 gem 2.9.6 (lib/sqlite3/fork_safety.rb), whose Ruby half sits
# on top of a C extension.  IronRuby provides that C extension itself, in
# Src/Libraries/Sqlite3/Sqlite3.cs, so this file runs unchanged except where
# marked "IronRuby:".
#
# Copyright (c) 2004-2024, Jamis Buck, Luis Lavena, Aaron Patterson,
# Mike Dalessio, et al.  Distributed under the 3-clause BSD license; see
# Src/StdLib/ironruby/sqlite3/LICENSE for the full text.

require "weakref"

module SQLite3
  # based on Rails's active_support/fork_tracker.rb
  module ForkSafety
    module CoreExt # :nodoc:
      def _fork
        pid = super
        if pid == 0
          ForkSafety.discard
        end
        pid
      end
    end

    @databases = []
    @mutex = Mutex.new
    @suppress = false

    class << self
      def hook! # :nodoc:
        ::Process.singleton_class.prepend(CoreExt)
      end

      def track(database) # :nodoc:
        @mutex.synchronize do
          @databases << WeakRef.new(database)
        end
      end

      def discard # :nodoc:
        warned = @suppress
        @databases.each do |db|
          next unless db.weakref_alive?

          begin
            unless db.closed? || db.readonly?
              unless warned
                # If you are here, you may want to read
                # https://github.com/sparklemotion/sqlite3-ruby/pull/558
                warn("Writable sqlite database connection(s) were inherited from a forked process. " \
                     "This is unsafe and the connections are being closed to prevent possible data " \
                     "corruption. Please close writable sqlite database connections before forking.",
                  uplevel: 0)
                warned = true
              end
              db.close
            end
          rescue WeakRef::RefError
            # GC may run while this method is executing, and that's OK
          end
        end
        @databases.clear
      end

      # Call to suppress the fork-related warnings.
      def suppress_warnings!
        @suppress = true
      end
    end
  end
end

SQLite3::ForkSafety.hook!
