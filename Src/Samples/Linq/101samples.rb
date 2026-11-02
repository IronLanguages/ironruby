# ##############################################################################################
#
# Copyright (c) Microsoft Corporation. 
#
# This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
# copy of the license can be found in the License.html file at the root of this distribution. If 
# you cannot locate the  Apache License, Version 2.0, please send an email to 
# ironruby@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
# by the terms of the Apache License, Version 2.0.
#
# You must not remove this notice, or any other, from this software.
#
# ##############################################################################################

# 101 LINQ Samples in Ruby (see http://msdn.microsoft.com/en-us/vcsharp/aa336746.aspx)

load_assembly 'System.Core'

# Linq Helpers

# LINQ extends IEnumerable<T>, and a Ruby Array isn't one, so the samples turn their arrays into
# sequences with to_seq. A refinement keeps it to this file.
module LinqSampleHelpers
  refine Object do
    def to_seq(type = Object)
      System::Linq::Enumerable.method(:of_type).of(type).call(self.to_a)
    end
  end
end

using System::Linq
using LinqSampleHelpers

make_pair = ->(a,b) { [a,b] }
identity = ->(a) { a }

############
### Data ###
############

Product = Struct.new(:product_name, :category, :units_in_stock, :unit_price)
products = [ 
    Product["product 1", "foo", 4, 1.3],
    Product["product 2", "bar", 3, 10.0],
    Product["product 3", "baz", 0, 4.0],
    Product["product 4", "foo", 1, 2.5],
]

Order = Struct.new(:id, :total, :order_date)
orders = [
  Order[0,   56.4, 1995],
  Order[1,  100.3, 2001],
  Order[2, 1000.0, 1992],
  Order[3, 1100.4, 2005],
  Order[4,  150.3, 2004],
  Order[5, 1040.0, 1996],
]

Customer = Struct.new(:id, :customer_name, :company_name, :region, :orders)
customers = [
  Customer[0, "customer 1", "company 1", "WA", [orders[0], orders[1], orders[5]]],
  Customer[1, "customer 2", "company 2", "CA", [orders[2], orders[3]]],
  Customer[2, "customer 3", "company 3", "NY", [orders[4]]],
  Customer[3, "customer 4", "company 4", "WA", []],
]

products = products.to_seq
orders = orders.to_seq
customers = customers.to_seq

numbers = [ 5, 4, 1, 3, 9, 8, 6, 7, 2, 0 ].to_seq
digits_array = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine"]
digits = digits_array.to_seq

###################
### Restriction ###
###################

puts '-- Where: Simple 1'
numbers.where { |n| n < 5 }.each { |x| puts x }


puts '-- Where: Simple 2'
products.where { |p| p.units_in_stock == 0 }.each { |x| puts x.product_name }


puts '-- Where: Simple 3'
products.where { |p| p.units_in_stock > 0 and p.unit_price > 3.00 }.each { |x| puts x.product_name }


puts '-- Where: Drilldown'
wa_customers = customers.where {|c| c.region == "WA"}
wa_customers.each do |customer| 
  puts "Customer #{customer.id}: #{customer.company_name}"
  customer.orders.each do |order|
    puts "  Order #{order.id}: #{order.order_date}"
  end
end


puts '-- Where: Indexed'
# A lambda, not a block: on .NET 9 and later the sequence has a Where(Func<T, bool>) of its own, which a block
# with two parameters also fits, and index would be nil.
digits.where(->(digit, index) { digit.size < index }).each { |x| puts x }

##################
### Projection ###
##################

puts '-- Select: Simple 1'
p numbers.select { |n| n + 1 }.to_a


puts '-- Select: Simple 2'
p products.select { |p| p.product_name }.to_a


puts '-- Select: Transformation'
p numbers.select { |n| digits_array[n] }.to_a


puts '-- Select: Anonymous Types 1'
words = [ "aPPLE", "BlUeBeRrY", "cHeRry" ].to_seq
s = Struct.new(:upper, :lower)
words.
  select { |w| s[w.upcase, w.downcase] }.
  each { |ul| puts "Uppercase: #{ul.upper} Lowercase: #{ul.lower}" }


puts '-- Select: Anonymous Types 2'
s = Struct.new(:digit, :even)
numbers.
  select {|n| s[digits_array[n], n % 2 == 0] }.
  each { |entry| p entry }


puts '-- Select: Anonymous Types 3'
s = Struct.new(:product_name, :category, :price)
products.
  select {|p| s[p.product_name, p.category, p.unit_price] }.
  each { |product_info| puts "#{product_info.product_name} is in the category #{product_info.category} and costs #{product_info.price} per unit" }


puts '-- Select: Indexed'
# A lambda, not a block: on .NET 9 and later the sequence has a Select(Func<T, R>) of its own, which a block
# with two parameters also fits, and the call would be ambiguous.
numbers.select(->(num, index) { [num, num == index] }).each { |num, in_place| puts "#{num}: #{in_place}" }


puts '-- Select: Filtered'
p numbers.where { |n| n < 5 }.select { |n| digits_array[n] }.to_a


puts '-- SelectMany: Compound from 1'
numbersA = [ 0, 2, 4, 5, 6, 8, 9 ].to_seq
numbersB = [ 1, 3, 5, 7, 8 ].to_seq

numbersA.
  select_many(->(_) { numbersB }, make_pair).
  where { |(a,b)| a < b }.
  each { |a,b| puts "#{a} is less than #{b}" }


puts '-- SelectMany: Compound from 2'
s = Struct.new(:customer_id, :order_id, :total)
customers.
  select_many(->(c) { c.orders }, make_pair).
  where { |(c,o)| o.total < 500 }.
  select { |(c,o)| s[c.id, o.id, o.total] }.
  each { |entry| p entry }


#puts '-- SelectMany: Compound from 3'


#puts '-- SelectMany: from Assignment'


puts '-- SelectMany: Multiple from'
cutoff_date = 1997
s = Struct.new(:customer_id, :order_id)
customers.
  where { |c| c.region == "WA" }.
  select_many(->(c) { c.orders }, make_pair).
  where { |(c, o)| o.order_date >= cutoff_date }.
  select { |(c, o)| s[c.id, o.id] }.
  each { |entry| p entry }


puts '-- SelectMany: Indexed'
customers.
  select_many { |cust, cust_index| cust.orders.to_seq.select {|o| "Customer #{cust_index + 1} has an order with id #{o.id}" } }.
  each { |s| puts s }

#####################
### Parititioning ###
#####################

puts '-- Take: Simple'
p numbers.take(3).to_a


#puts '-- Take: Nested'


puts '-- Skip: Simple'
p numbers.skip(3).to_a


puts '-- Skip: Nested'
s = Struct.new(:customer_id, :order_id, :order_date)
customers.
  select_many(->(c) { c.orders }, make_pair).
  where {|(c,o)| c.region == "WA" }.
  select {|(c,o)| s[c.id, o.id, o.order_date]}.
  skip(2).
  each { |entry| p entry }


puts '-- TakeWhile: Simple'
p numbers.take_while {|n| n < 6 }.to_a


puts '-- TakeWhile: Indexed'
p numbers.take_while {|n, index| n >= index }.to_a


puts '-- SkipWhile: Simple'
p numbers.skip_while {|n| n % 3 != 0 }.to_a


puts '-- SkipWhile: Indexed'
p numbers.skip_while {|n, index| n >= index }.to_a

################
### Ordering ###
################

class Comparer
  include System::Collections::Generic::IComparer[String]
  
  def initialize &comparer
    @comparer = comparer
  end
  
  def compare(x,y)
    @comparer[x,y]
  end
end

# puts '-- OrderBy - Simple 1'
# puts '-- OrderBy - Simple 2'
# puts '-- OrderBy - Simple 3'
# puts '-- OrderBy - Comparer'
# puts '-- OrderByDescending - Simple 1'
# puts '-- OrderByDescending - Simple 2'
# puts '-- OrderByDescending - Comparer'
# puts '-- ThenBy - Simple'
# puts '-- ThenBy - Comparer'

puts '-- ThenByDescending: Simple'
products.order_by { |p| p.category }.then_by_descending { |p| p.unit_price }.each { |product| p product }


puts '-- ThenByDescending: Comparer'
words = [ "aPPLE", "AbAcUs", "bRaNcH", "BlUeBeRrY", "ClOvEr", "cHeRry"  ].to_seq
p words.order_by { |a| a.size }.then_by_descending(identity, Comparer.new { |x, y| x.casecmp y }).to_a


puts '-- Reverse'
digits.where { |d| d[1] == ?i }.reverse.each { |d| puts d }


################
### Grouping ###
################

puts '-- Grouping: Simple 1'
numbers.
  group_by { |n| n % 5 }.
  each { |g| puts "Numbers with a remainder of #{g.key} when divided by 5: #{g.to_a.inspect}" }
  

puts '-- Grouping: Simple 2'
words = [ "blueberry", "chimpanzee", "abacus", "banana", "apple", "cheese" ].to_seq
words.
  group_by { |w| w[0] }.
  each { |g| puts "Words that start with the letter '#{g.key.chr}': #{g.to_a.join(', ')}" }


#puts '-- Grouping: Simple 3'


#puts '-- GroupBy: Nested'


puts '-- GroupBy: Comparer'

class AnagramEqualityComparer
  include System::Collections::Generic::IEqualityComparer[String]
    
  def equals(x,y)
    get_canoncial_string(x) == get_canoncial_string(y)
  end
  
  def get_hash_code(x)
    get_canoncial_string(x).GetHashCode
  end
  
  private
  def get_canoncial_string(word)
    word.split('').sort.join
  end
end

anagrams = ["from   ", " salt", " earn ", "  last   ", " near ", " form  "].to_seq

anagrams.
  group_by(->(w) { w.strip }, AnagramEqualityComparer.new).
  each { |g| puts "#{g.key}: #{g.select { |w| "'#{w}'" }.to_a.join(', ')}" }
  
  
puts '-- GroupBy: Comparer, Mapped'

anagrams.
  group_by(->(w) { w.strip }, ->(a) { a.upcase }, AnagramEqualityComparer.new).
  each { |g| puts "#{g.key}: #{g.select { |w| "'#{w}'" }.to_a.join(', ')}" }
  
#####################
### Set Operators ###
#####################

puts '-- Distinct 1'
p [ 2, 2, 3, 5, 5 ].to_seq.distinct.to_a

#puts '-- Distinct 2'

puts '-- Union 1'
p numbersA.union(numbersB).to_a

#puts '-- Union 2'

puts '-- Intersect 1'
p numbersA.intersect(numbersB).to_a

#puts '-- Intersect 2'

puts '-- Except 1'
p numbersA.except(numbersB).to_a

#puts '-- Except 2'

############################
### Conversion Operators ###
############################

puts '-- ToArray'
doubles = [1.7, 2.3, 1.9, 4.1, 2.9].to_seq
p doubles.order_by(identity).to_array


puts '-- ToList'
words = ["cherry", "apple", "blueberry"].to_seq
p words.order_by(identity).to_list


puts '-- ToDictionary'
s = Struct.new(:name, :score)
score_records = [s["Alice", 50], s["Bob", 40], s["Cathy", 45]].to_seq
dict = score_records.to_dictionary {|sr| sr.name }
dict.each {|k,v| puts "#{k} => #{v}" }

puts '-- OfType'
p [ nil, 1.0, "two", 3, "four", 5, "six", 7.0 ].to_seq.method(:of_type).of(Float).call.to_a

#########################
### Element Operators ###
#########################

puts '-- First: Simple'
p products.where {|p| p.category == "foo" }.first


puts '-- First: Condition'
p digits.first {|s| s[0] == ?o}


puts '-- FirstOrDefault: Simple'
p [].to_seq(Integer).first_or_default


puts '-- FirstOrDefault: Condition'
p digits.first_or_default {|s| s[0] == ?x}.nil?


puts '-- ElementAt'
p numbers.where {|n| n > 5}.element_at(1)

############################
### Generation Operators ###
############################

puts '-- Range'
System::Linq::Enumerable.range(10, 5).each { |n| puts "#{n} is #{n % 2 == 1 ? 'odd' : 'even'}" }


puts '-- Repeat'
System::Linq::Enumerable.repeat(7, 10).each { |n| print n }
puts

###################
### Quantifiers ###
###################

puts '-- Any: Simple'
words = ["believe", "relief", "receipt", "field"].to_seq
p words.any { |w| w.include? "ei" }

puts '-- Any: Grouped'
products.
  group_by { |p| p.category }.
  where { |g| g.any { |p| p.units_in_stock == 0 } }.
  each { |g| puts "#{g.key}: #{g.select {|p| p.product_name }.to_a.join(', ')}" }

  
puts '-- All: Simple'
p numbers.all { |n| n % 2 == 1 }


puts '-- All: Grouped'
products.
  group_by { |p| p.category }.
  where { |g| g.all { |p| p.units_in_stock > 0 } }.
  each { |g| puts "#{g.key}: #{g.select {|p| p.product_name }.to_a.join(', ')}" }


###########################
### Aggregate Operators ###
###########################

puts '-- Count: Simple'
factorsOf300 = [ 2, 2, 3, 5, 5 ].to_seq
p factorsOf300.distinct.count


puts '-- Count: Conditional'
p numbers.count { |n| n % 2 == 1 }


puts '-- Count: Nested'
p customers.select {|c| [c.id, c.orders.to_seq.count]}.to_a


puts '-- Count: Grouped'
s = Struct.new(:category, :product_count)
products.
  group_by(->(p) { p.category }) { |c, g| s[c, g.count ] }.
  each { |entry| p entry }


puts '-- Sum: Simple'
# sum is only defined on IEnumerable<numeric type>, but numbers are of type IEnumerable<object>:
p numbers.to_seq(Integer).sum


puts '-- Sum: Projection'
# sum is overloaded on the Func's return type, which we don't currently infer, so we need it to be specified explicitly:
p words.sum(System::Func[Object, Integer].new { |w| w.length })


puts '-- Sum: Grouped'
s = Struct.new(:category, :total_units_in_stock)
products.
  group_by(->(p) { p.category }) { |c, g| s[c, g.sum(System::Func[Object, Integer].new { |p| p.units_in_stock })] }.
  each { |entry| p entry }


puts '-- Min: Simple'
# min is only defined on IEnumerable<numeric type>, but numbers are of type IEnumerable<object>:
p numbers.to_seq(Integer).min


puts '-- Min: Projection'
# min is overloaded on the Func's return type, which we don't currently infer, so we need it to be specified explicitly:
p words.method(:min).of(Object, Integer).call { |w| w.length }


puts '-- Min: Grouped'
s = Struct.new(:category, :cheapest_price)
products.
  group_by(->(p) { p.category }) do |c, g| 
    # min has overloads that we are currently not able to disambiguate via type inference, so we need some help:
    s[c, g.method(:min).of(Object, Float).call { |p| p.unit_price }] 
  end.
  each { |entry| p entry }
  
  
puts '-- Min: Elements'
s = Struct.new(:category, :cheapest_products)
products.
  group_by(->(p) { p.category }) do |c, g| 
    # min has overloads that we are currently not able to disambiguate via type inference, so we need some help:
    min_price = g.method(:min).of(Object, Float).call { |p| p.unit_price }
    
    s[c, g.where {|p| p.unit_price == min_price }] 
  end.
  each { |entry| puts "Cheapest products in category #{entry.category}: #{entry.cheapest_products.select {|p| p.product_name }.to_a}" }


# Max samples are similar to Min 


# Average samples are similar to Sum


puts '-- Aggregate: Simple'
doubles = [ 1.7, 2.3, 1.9, 4.1, 2.9 ].to_seq
p doubles.aggregate { |running_product, next_factor| running_product * next_factor }


puts '-- Aggregate: Seed'
start_balance = 100.0
attempted_withdrawals = [ 20, 10, 40, 50, 10, 70, 30 ].to_seq

end_balance = attempted_withdrawals.aggregate(start_balance) { |balance, next_withdrawal| 
  next_withdrawal <= balance ? balance - next_withdrawal : balance 
}

p end_balance 

###############################
### Miscellaneous Operators ###
###############################
puts '-- Concat: 1'
p numbersA.concat(numbersB).to_a


puts '-- Concat: 2'
customer_names = customers.select {|c| c.company_name }
product_names = products.select {|p| p.product_name }
p customer_names.concat(product_names).to_a


puts '-- EqualAll: 1'
wordsA = ["cherry", "apple", "blueberry"].to_seq
wordsB = ["cherry", "apple", "blueberry"].to_seq
p wordsA.sequence_equal(wordsB)


puts '-- EqualAll: 2'

wordsA = ["cherry", "apple", "blueberry"].to_seq
wordsB = [ "apple", "blueberry", "cherry" ].to_seq
p wordsA.sequence_equal(wordsB)

#################################
### Custom Sequence Operators ###
#################################

# N/A

#######################
### Query Execution ###
#######################

# ...

######################
### Join Operators ###
######################
puts '-- Cross Join'
categories = ["bar", "foo"].to_seq
categories.
  join(products, identity, ->(p) { p.category }, make_pair).
  each { |(c, p)| puts "#{p.product_name}: #{c}" }


puts '-- Group Join'
categories.
  group_join(products, identity, ->(p) { p.category }, make_pair).
  each { |(c, ps)| puts "#{c}: #{ps.select {|p| p.product_name}.to_a.join(', ')}" }
  

puts '-- Cross Join with Group Join'
s = Struct.new(:category, :product_name)
categories.
  group_join(products, identity, ->(p) { p.category }, make_pair).
  select_many(->((c, ps)) { ps }) { |(c, _), p| s[c, p.product_name] }.
  each { |entry| p entry}


puts '-- Left Outer Join'
categories = ["foo", "no such category"].to_seq
categories.
  group_join(products, identity, ->(p) { p.category }, make_pair).
  select_many(->((c, ps)) { ps.default_if_empty }) { |(c, _), p| s[c, if p.nil? then "(No Products)" else p.product_name end] }.
  each { |entry| p entry}
