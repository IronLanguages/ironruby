require 'sqlite3'

db = SQLite3::Database.new(':memory:')
db.execute('CREATE TABLE t (id INTEGER PRIMARY KEY, name TEXT, n REAL, blob BLOB)')
db.execute("INSERT INTO t (name, n) VALUES ('a', 1.5)")
db.execute('INSERT INTO t (name, n) VALUES (?, ?)', ['b', 2.5])
db.execute('INSERT INTO t (name, n) VALUES (:n, :v)', n: 'c', v: 3.5)

puts db.execute('SELECT id, name, n FROM t ORDER BY id').inspect
puts db.get_first_value('SELECT COUNT(*) FROM t')
puts db.get_first_row('SELECT name FROM t WHERE n > 2 ORDER BY n').inspect
puts db.last_insert_row_id
puts db.changes

db.results_as_hash = true
puts db.execute('SELECT name FROM t WHERE id = 1').inspect
db.results_as_hash = false

st = db.prepare('SELECT name FROM t WHERE n >= ? ORDER BY n')
puts st.execute(2.0).to_a.inspect
puts st.columns.inspect
st.close

db.execute('INSERT INTO t (name, blob) VALUES (?, ?)', ['bin', SQLite3::Blob.new("\x00\x01\xff")])
row = db.get_first_row('SELECT blob FROM t WHERE name = ?', ['bin'])
puts row.first.bytes.inspect

db.transaction
db.execute("INSERT INTO t (name) VALUES ('rolled')")
db.rollback
puts db.get_first_value('SELECT COUNT(*) FROM t WHERE name = ?', ['rolled'])

begin
  db.execute('SELECT * FROM missing_table')
rescue SQLite3::SQLException => e
  puts e.class
end

begin
  db.execute('NOT SQL AT ALL')
rescue SQLite3::Exception => e
  puts e.class
end

puts db.execute('SELECT 1 + 1').flatten.first
puts db.execute("SELECT upper('x')").flatten.first
db.close
puts db.closed?
