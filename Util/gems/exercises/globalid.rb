require 'cgi'
require 'global_id'

GlobalID.app = 'exercise'

class Widget
  include GlobalID::Identification
  attr_reader :id
  def initialize(id); @id = id; end
  def self.find(id); new(id.to_i); end
  def ==(other); other.is_a?(Widget) && other.id == id; end
end

w = Widget.new(42)
gid = w.to_global_id
puts gid.to_s
puts gid.model_name
puts gid.model_id
puts gid.app
puts GlobalID.parse(gid.to_s).find == w
puts GlobalID::Locator.locate(gid.to_s).id

# Signed global IDs are deliberately left out: they go through
# ActiveSupport::JSON.decode, which calls JSON.parse(json, options) - an arity the
# json gem 3.0.2 on the oracle refuses, so CRuby cannot answer for them here.

puts GlobalID.parse('gid://exercise/Widget/7').model_id
puts GlobalID.parse('not a gid').inspect
puts URI::GID.parse('gid://exercise/Widget/7').model_name

