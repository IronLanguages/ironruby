# Differential matrix for Psych's dumping visitors, one line per case.
#   ruby Util/psych-visitors-matrix.rb            # CRuby reference
#   ./ir.sh Util/psych-visitors-matrix.rb         # IronRuby
# Diff the two outputs; every differing line is a bug.
#
# Each object is written twice: through Psych.dump, and through Psych::Visitors::YAMLTree
# plus Psych::Nodes::Stream#yaml by hand, which is what RubyGems does
# (Gem::Specification#to_yaml). On IronRuby both are psych's own Ruby code over the YAML
# engine's port of libyaml's emitter (Src/StdLib/ironruby/psych/ironruby.rb), as on CRuby
# they are over libyaml, so the two paths should agree with each other and with CRuby.

require 'psych'
require 'date'
require 'set'

def show(label)
  v = begin
        yield.inspect
      rescue Exception => e
        "!#{e.class}: #{e.message}"
      end
  puts "#{label.ljust(40)} #{v}"
end

def tree(o, options = {})
  builder = Psych::Visitors::YAMLTree.create(options)
  builder << o
  builder.tree.yaml
end

class Encoded
  def initialize(a, b)
    @a = a
    @b = b
  end

  def encode_with(coder)
    coder['a'] = @a
    coder['b'] = @b
  end
end

class ScalarCoded
  def encode_with(coder)
    coder.represent_scalar '!scalar-coded', 'value'
  end
end

class SeqCoded
  def encode_with(coder)
    coder.represent_seq '!seq-coded', [1, 2]
  end
end

Point = Struct.new(:x, :y) unless defined?(Point)

class ArraySub < Array; end
class HashSub < Hash; end

cyclic = ['a']
cyclic << cyclic

shared = { 'k' => 'v' }

CASES = [
  ['nil',              nil],
  ['true',             true],
  ['integer',          42],
  ['float',            3.5],
  ['float nan',        Float::NAN],
  ['float inf',        Float::INFINITY],
  ['string',           'plain'],
  ['string empty',     ''],
  ['string y',         'y'],
  ['string merge',     '<<'],
  ['string equals',    '='],
  ['string numeric',   '0123'],
  ['string multiline', "one\ntwo\n"],
  ['string binary',    "\xff\xfe\x00".b],
  ['symbol',           :sym],
  ['array',            [1, 'two', :three, nil]],
  ['hash',             { 'a' => 1, 'b' => 'two' }],
  ['hash symbol keys', { :a => 1, :b => 2 }],
  ['nested',           { 'a' => [1, { 'b' => [2, 3] }], 'c' => { 'd' => nil } }],
  ['time utc',         Time.utc(2020, 1, 2, 3, 4, 5)],
  ['date',             Date.new(2020, 1, 2)],
  ['datetime',         DateTime.new(2020, 1, 2, 3, 4, 5)],
  ['range',            1..5],
  ['range exclusive',  1...5],
  ['struct',           Point.new(1, 2)],
  ['class',            String],
  ['regexp',           /ab+c/im],
  ['rational',         Rational(3, 4)],
  ['complex',          Complex(1, 2)],
  ['exception',        RuntimeError.new('boom')],
  ['encode_with',      Encoded.new(1, 'two')],
  ['encode_with seq',  SeqCoded.new],
  ['encode_with str',  ScalarCoded.new],
  ['cyclic array',     cyclic],
  ['shared twice',     [shared, shared]],
  ['omap',             Psych::Omap[['a', 1], ['b', 2]]],
  ['set',              Set['a', 'b']],
  ['array subclass',   ArraySub.new([1, 2])],
  ['hash subclass',    HashSub.new],
  ['object',           Object.new.tap { |o| o.instance_variable_set(:@x, 1) }],
]

CASES.each do |label, object|
  show("dump #{label}") { Psych.dump(object) }
  show("tree #{label}") { tree(object) }
end

# Options YAMLTree and the emitter take.
show('tree header')       { tree({ 'a' => 1 }, header: true) }
show('tree line_width')   { tree({ 'a' => 'word ' * 30 }, line_width: 20) }
show('tree stringify')    { tree({ :a => 1 }, stringify_names: true) }

# Emitter options, through the visitor rather than through YAMLTree.
show('emit indentation') do
  builder = Psych::Visitors::YAMLTree.create
  builder << { 'a' => { 'b' => [1, 2] } }
  builder.tree.yaml(nil, indentation: 4)
end

# A tree built by hand: the styles and tags a node carries have to survive.
show('handmade tree') do
  stream = Psych::Nodes::Stream.new
  doc = Psych::Nodes::Document.new
  map = Psych::Nodes::Mapping.new
  map.children << Psych::Nodes::Scalar.new('quoted', nil, nil, false, true,
                                           Psych::Nodes::Scalar::SINGLE_QUOTED)
  map.children << Psych::Nodes::Scalar.new('tagged', nil, '!custom', false, false,
                                           Psych::Nodes::Scalar::ANY)
  seq = Psych::Nodes::Sequence.new(nil, nil, true, Psych::Nodes::Sequence::FLOW)
  seq.children << Psych::Nodes::Scalar.new('1')
  seq.children << Psych::Nodes::Scalar.new('2')
  map.children << Psych::Nodes::Scalar.new('flow')
  map.children << seq
  doc.children << map
  stream.children << doc
  stream.yaml
end

# An anchor and the alias that refers back to it.
show('handmade alias') do
  stream = Psych::Nodes::Stream.new
  doc = Psych::Nodes::Document.new
  seq = Psych::Nodes::Sequence.new
  seq.children << Psych::Nodes::Scalar.new('x', 'anch')
  seq.children << Psych::Nodes::Alias.new('anch')
  doc.children << seq
  stream.children << doc
  stream.yaml
end

# Round trip: parse what the tree path wrote, and write it again.
show('reemit parsed') { Psych.parse_stream(tree({ 'a' => [1, 2], 'b' => nil })).yaml }

# The visitors that only walk a tree.
show('depth first') do
  seen = []
  Psych::Visitors::DepthFirst.new(->(node) { seen << node.class.name }).
    accept(Psych.parse_stream("---\na:\n- 1\n"))
  seen
end

# RestrictedYAMLTree is what Psych.safe_dump is built from.
show('restricted ok')     { tree_ok = Psych::Visitors::RestrictedYAMLTree.create(permitted_classes: [Symbol]); tree_ok << { 'a' => :b }; tree_ok.tree.yaml }
show('restricted refuse') { bad = Psych::Visitors::RestrictedYAMLTree.create; bad << Object.new; bad.tree.yaml }
show('restricted alias')  { bad = Psych::Visitors::RestrictedYAMLTree.create; bad << [shared, shared]; bad.tree.yaml }

# What a node tree that is not a whole stream does (upstream refuses it).
show('document yaml') { Psych.parse("--- foo\n").yaml }
show('scalar yaml')   { Psych.parse("--- foo\n").root.yaml }
