# The SQLite IronRuby's sqlite3 library loads: version, source id and compile options,
# one per line and sorted, so two builds can be compared with diff. The compiler is left
# out, as a rebuild names its own. package-release.sh runs this before and after it
# swaps in Util/build-e_sqlite3.sh's libe_sqlite3.so on Linux.
require "sqlite3"

db = SQLite3::Database.new(":memory:")
puts "sqlite_version=#{db.get_first_value('select sqlite_version()')}"
puts "source_id=#{db.get_first_value('select sqlite_source_id()')}"
puts db.execute("PRAGMA compile_options").flatten.reject { it.start_with?("COMPILER=") }.sort
