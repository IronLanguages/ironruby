require 'active_model'

class Person
  include ActiveModel::Model
  include ActiveModel::Attributes
  include ActiveModel::Serializers::JSON

  attribute :name, :string
  attribute :age, :integer
  attribute :active, :boolean, default: true
  attribute :born_on, :date

  validates :name, presence: true, length: { minimum: 2 }
  validates :age, numericality: { greater_than: 0, less_than: 200 }, allow_nil: true
  validate :no_bob

  def no_bob
    errors.add(:name, 'must not be bob') if name == 'bob'
  end
end

p1 = Person.new(name: 'Ada', age: '36', born_on: '1815-12-10')
puts p1.valid?
puts p1.age.inspect
puts p1.born_on.inspect
puts p1.active.inspect
puts p1.attributes.sort.inspect
puts p1.as_json.sort.inspect

p2 = Person.new(name: '', age: -1)
puts p2.valid?
puts p2.errors.full_messages.sort.inspect
puts p2.errors[:name].inspect
puts p2.errors.count

p3 = Person.new(name: 'bob')
p3.valid?
puts p3.errors.full_messages.inspect

class Dirty
  include ActiveModel::Dirty
  define_attribute_methods :title
  def title; @title; end
  def title=(v); title_will_change! unless v == @title; @title = v; end
end
d = Dirty.new
d.title = 'x'
puts d.changed.inspect
puts d.title_changed?
puts d.changes.inspect

puts ActiveModel::Name.new(Person).singular
puts ActiveModel::Name.new(Person).plural
puts Person.new(name: 'Ada').to_model.class
puts ActiveModel::Type.lookup(:integer).cast('42').inspect
