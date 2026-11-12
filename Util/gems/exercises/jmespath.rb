require 'jmespath'

data = {
  'people' => [
    { 'name' => 'ada', 'age' => 36, 'tags' => %w[math logic] },
    { 'name' => 'bob', 'age' => 20, 'tags' => [] },
    { 'name' => 'cy', 'age' => 50, 'tags' => %w[math] }
  ],
  'meta' => { 'count' => 3, 'nested' => { 'deep' => 'value' } }
}

%w[
  meta.count
  meta.nested.deep
  people[0].name
  people[-1].name
  people[*].name
  people[?age>`30`].name
  people[?name=='bob']|[0].age
  people[:2].name
  length(people)
  max_by(people,&age).name
  sort_by(people,&age)[*].name
  people[*].tags[]
  {n:meta.count,d:meta.nested.deep}
  people[*].[name,age]
  nothing
  people[?contains(tags,'math')].name
].each do |expr|
  puts "#{expr} => #{JMESPath.search(expr, data).inspect}"
end

begin
  JMESPath.search('foo[', data)
rescue StandardError => e
  puts e.class
end
