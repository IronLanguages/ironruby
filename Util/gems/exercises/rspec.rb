require 'rspec/core'
require 'stringio'

# A real RSpec run - matchers, doubles, hooks, shared examples, and three
# examples that must be reported as failure/error/pending. The run is driven
# in-process so the output can be filtered down to the deterministic part:
# the timings and the seed are not the measurement, the counts are.
RSpec.shared_examples 'a collection' do
  it 'has a size' do
    expect(subject.size).to eq(3)
  end
end

RSpec.describe 'exercise' do
  subject { [1, 2, 3] }
  include_examples 'a collection'

  it 'compares' do
    expect(1 + 1).to eq(2)
    expect('abc').to match(/b/)
    expect([1, 2]).to include(2)
    expect(nil).to be_nil
    expect { raise ArgumentError, 'x' }.to raise_error(ArgumentError, 'x')
    expect(3).to be > 2
    expect({ a: 1 }).to have_key(:a)
    expect([3, 1]).to contain_exactly(1, 3)
    expect('abc').to start_with('a').and end_with('c')
  end

  it 'uses doubles' do
    d = double('thing', name: 'n')
    expect(d.name).to eq('n')
    obj = Object.new
    allow(obj).to receive(:greet).with('x').and_return('hi x')
    expect(obj.greet('x')).to eq('hi x')
    spy = double('spy')
    expect(spy).to receive(:called).once
    spy.called
  end

  it 'reports a failed expectation' do
    expect(1).to eq(2)
  end

  it 'reports a raised error' do
    raise 'boom'
  end

  it 'is pending' do
    pending 'not yet'
    raise 'still failing'
  end
end

out = StringIO.new
RSpec.configuration.tap do |c|
  c.color_mode = :off
  c.order = :defined
  c.output_stream = out
  c.formatter = :progress
end
RSpec::Core::Runner.new(RSpec::Core::ConfigurationOptions.new([])).run(out, out)

out.string.each_line do |line|
  # Drop the parts that are not the measurement: timings, the seed, and the
  # backtrace frames (whose wording is the interpreter's, not the gem's).
  next if line =~ /seconds|Randomized|^\s*#|^rspec |^\s*$/
  puts line.rstrip
end
