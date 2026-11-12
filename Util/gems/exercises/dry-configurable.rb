require 'dry/configurable'

class App
  extend Dry::Configurable

  setting :name, default: 'app'
  setting :port, default: 8080, constructor: ->(v) { v.to_i }
  setting :db do
    setting :url, default: 'sqlite://x'
    setting :pool, default: 5
  end
  setting :required, constructor: ->(v) { v.nil? ? 'defaulted' : v }
end

puts App.config.name
puts App.config.port
puts App.config.db.url
puts App.config.db.pool
puts App.config.required

App.configure do |c|
  c.name = 'changed'
  c.port = '9090'
  c.db.pool = 10
end
puts App.config.name, App.config.port, App.config.db.pool
puts App.config.to_h.sort.inspect

class Instance
  include Dry::Configurable
  setting :level, default: :info
end
a = Instance.new
b = Instance.new
a.config.level = :debug
puts a.config.level.inspect
puts b.config.level.inspect

App.finalize!
begin
  App.config.name = 'nope'
rescue Dry::Configurable::FrozenConfigError => e
  puts e.class
end

puts App.settings.to_a.map(&:name).map(&:to_s).sort.inspect
