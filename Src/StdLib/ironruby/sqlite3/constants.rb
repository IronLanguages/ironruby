# frozen_string_literal: true
#
# SQLite3::Constants - the numeric constants of the C API, as the sqlite3 gem
# publishes them.  The values are SQLite's own and do not change between
# versions; they are written out here rather than read from the library so that
# requiring sqlite3 does not have to open a connection.

module SQLite3
  module Constants
    module TextRep
      UTF8 = 1
      UTF16LE = 2
      UTF16BE = 3
      UTF16 = 4
      ANY = 5
      DETERMINISTIC = 0x800
    end

    module ColumnType
      INTEGER = 1
      FLOAT = 2
      TEXT = 3
      BLOB = 4
      NULL = 5
    end

    module ErrorCode
      OK = 0          # Successful result
      ERROR = 1       # SQL error or missing database
      INTERNAL = 2    # An internal logic error in SQLite
      PERM = 3        # Access permission denied
      ABORT = 4       # Callback routine requested an abort
      BUSY = 5        # The database file is locked
      LOCKED = 6      # A table in the database is locked
      NOMEM = 7       # A malloc() failed
      READONLY = 8    # Attempt to write a readonly database
      INTERRUPT = 9   # Operation terminated by sqlite_interrupt()
      IOERR = 10      # Some kind of disk I/O error occurred
      CORRUPT = 11    # The database disk image is malformed
      NOTFOUND = 12   # (Internal Only) Table or record not found
      FULL = 13       # Insertion failed because database is full
      CANTOPEN = 14   # Unable to open the database file
      PROTOCOL = 15   # Database lock protocol error
      EMPTY = 16      # (Internal Only) Database table is empty
      SCHEMA = 17     # The database schema changed
      TOOBIG = 18     # Too much data for one row of a table
      CONSTRAINT = 19 # Abort due to constraint violation
      MISMATCH = 20   # Data type mismatch
      MISUSE = 21     # Library used incorrectly
      NOLFS = 22      # Uses OS features not supported on host
      AUTH = 23       # Authorization denied
      FORMAT = 24     # Auxiliary database format error
      RANGE = 25      # 2nd parameter to sqlite3_bind out of range
      NOTADB = 26     # File opened that is not a database file
      NOTICE = 27     # Notifications from sqlite3_log()
      WARNING = 28    # Warnings from sqlite3_log()

      ROW = 100       # sqlite_step() has another row ready
      DONE = 101      # sqlite_step() has finished executing
    end

    module Open
      READONLY = 0x00000001
      READWRITE = 0x00000002
      CREATE = 0x00000004
      DELETEONCLOSE = 0x00000008
      EXCLUSIVE = 0x00000010
      AUTOPROXY = 0x00000020
      URI = 0x00000040
      MEMORY = 0x00000080
      MAIN_DB = 0x00000100
      TEMP_DB = 0x00000200
      TRANSIENT_DB = 0x00000400
      MAIN_JOURNAL = 0x00000800
      TEMP_JOURNAL = 0x00001000
      SUBJOURNAL = 0x00002000
      MASTER_JOURNAL = 0x00004000
      SUPER_JOURNAL = 0x00004000
      NOMUTEX = 0x00008000
      FULLMUTEX = 0x00010000
      SHAREDCACHE = 0x00020000
      PRIVATECACHE = 0x00040000
      WAL = 0x00080000
    end

    module Status
      MEMORY_USED = 0
      PAGECACHE_USED = 1
      PAGECACHE_OVERFLOW = 2
      SCRATCH_USED = 3
      SCRATCH_OVERFLOW = 4
      MALLOC_SIZE = 5
      PARSER_STACK = 6
      PAGECACHE_SIZE = 7
      SCRATCH_SIZE = 8
      MALLOC_COUNT = 9
    end

    module Optimize
      DEBUG = 0x01
      ANALYZE_TABLES = 0x02
      LIMIT_ANALYZE = 0x10
      CHECK_ALL_TABLES = 0x10000

      DEFAULT = ANALYZE_TABLES | LIMIT_ANALYZE
    end
  end
end
