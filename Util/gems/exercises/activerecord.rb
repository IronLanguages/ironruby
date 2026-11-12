require 'active_record'

# An in-memory SQLite database, through IronRuby's own sqlite3 (a shim over
# Microsoft.Data.Sqlite) - the adapter, the schema DSL, migrations, the query
# builder and associations all run for real.
ActiveRecord::Base.logger = nil
ActiveRecord::Base.establish_connection(adapter: 'sqlite3', database: ':memory:')

ActiveRecord::Schema.verbose = false
ActiveRecord::Schema.define do
  create_table :authors do |t|
    t.string :name, null: false
    t.integer :born
  end
  create_table :books do |t|
    t.string :title
    t.integer :pages
    t.references :author
  end
end

class Author < ActiveRecord::Base
  has_many :books
  validates :name, presence: true
end

class Book < ActiveRecord::Base
  belongs_to :author
  scope :long, -> { where('pages > 300') }
end

ada = Author.create!(name: 'Ada', born: 1815)
bob = Author.create!(name: 'Bob', born: 1900)
ada.books.create!(title: 'Notes', pages: 120)
ada.books.create!(title: 'Engines', pages: 400)
bob.books.create!(title: 'Later', pages: 500)

puts Author.count
puts Book.count
puts Author.order(:name).pluck(:name).inspect
puts ada.books.order(:title).pluck(:title).inspect
puts Book.long.order(:title).pluck(:title).inspect
puts Book.where(author: ada).count
puts Author.joins(:books).where(books: { pages: 400 }).pluck(:name).inspect
puts Book.group(:author_id).count.sort.inspect
puts Book.sum(:pages)
puts Book.average(:pages).to_f
puts Book.maximum(:pages)
puts Author.find_by(name: 'Bob').born
puts Author.where(name: 'None').first.inspect

ada.update!(born: 1816)
puts Author.find(ada.id).born
puts ada.books.first.author.name

bad = Author.new
puts bad.valid?
puts bad.errors.full_messages.inspect
begin
  Author.find(9999)
rescue ActiveRecord::RecordNotFound => e
  puts e.class
end

puts Author.where(born: 1816).to_sql
puts Book.column_names.sort.inspect
puts Author.connection.adapter_name

ActiveRecord::Base.transaction do
  Author.create!(name: 'Temp')
  raise ActiveRecord::Rollback
end
puts Author.count
