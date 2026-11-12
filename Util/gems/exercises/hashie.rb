require 'hashie'

m = Hashie::Mash.new(a: 1, 'b' => { 'c' => [1, { d: 2 }] })
puts m.a
puts m.b.c.last.d
puts m['b']['c'].first
puts m.a?
puts m.zzz?
puts m.zzz.inspect
m.new_key = 'v'
puts m.new_key
puts m.to_hash.keys.sort.inspect

class Config < Hashie::Dash
  property :name, required: true
  property :size, default: 10
  property :note
end
c = Config.new(name: 'x')
puts [c.name, c.size, c.note].inspect
c.size = 3
puts c.size
begin
  Config.new(size: 1)
rescue ArgumentError => e
  puts "#{e.class}: #{e.message}"
end

class T < Hashie::Trash
  property :first_name, from: :firstName
end
puts T.new(firstName: 'Ada').first_name

s = Hashie::Struct.new(:x, :y) rescue nil
e = Hashie::Extensions::IndifferentAccess
h = Hash.new.extend(e)
h['k'] = 1
puts h[:k]

class Coerced < Hash
  include Hashie::Extensions::Coercion
  coerce_value Integer, String
end
cv = Coerced.new
cv[:n] = 5
puts cv[:n].inspect

deep = Hashie::Extensions::DeepMerge
base = { a: { b: 1, c: 2 } }.extend(deep)
puts base.deep_merge(a: { c: 3, d: 4 }).inspect
