require 'multi_json'

MultiJson.use :json_gem
puts MultiJson.adapter.to_s.split('::').last

doc = { 'b' => [1, 2.5, nil, true], 'a' => { 'c' => 'x' } }
json = MultiJson.dump(doc)
puts json
puts MultiJson.load(json).inspect
puts MultiJson.load(json, symbolize_keys: true).inspect
puts MultiJson.dump(doc, pretty: true)

begin
  MultiJson.load('{ not json')
rescue MultiJson::ParseError => e
  puts e.class
end

puts MultiJson.load('[1,2,3]').sum
puts MultiJson.dump('a' => "quote\"and\\slash\n")
puts MultiJson.load(MultiJson.dump('t' => "ä€"))['t']
