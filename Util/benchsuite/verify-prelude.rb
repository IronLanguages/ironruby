# Replaces benchmark-interface's `benchmark` for --verify: each block runs
# exactly once and the value it returned is printed as
#
#   VERIFY <name> <class> len=<inspect length> crc=<crc32> <first 60 chars>
#
# Object addresses are normalized out of the inspect string first, so two
# interpreters that compute the same value print the same line. The global
# random generator is seeded (IronRuby's MT19937 matches CRuby's for a given
# seed), so benchmarks that draw from Kernel#rand stay comparable.
#
# A benchmark that ends `benchmark { ... }.verify { |result| ... }` (as
# benchmark-interface allows) has its own check run too, printed as
#
#   VERIFY-CHECK <name> ok|FAILED

require 'zlib'

srand(1)

module BenchSuiteVerify
  @count = 0

  Ran = Struct.new(:name, :value, :ok) do
    def verify
      return self unless ok
      puts "VERIFY-CHECK #{name} #{yield(value) ? 'ok' : 'FAILED'}"
      self
    end
  end

  def self.run(name, block)
    @count += 1
    name ||= "#{File.basename($0, '.rb')}:#{@count}"
    value = block.call
    if value.is_a?(String) && value.bytesize > 16 << 20
      # A huge string's inspect is ~4x its size, past .NET's 2G-char limit
      # for core-read-gigabyte, so digest the bytes themselves.
      text = value[0, 60]
      puts "VERIFY #{name} String bytes=#{value.bytesize} #{value.encoding} crc=#{Zlib.crc32(value)} #{text.dump}"
      return Ran.new(name, value, true)
    end
    text = value.inspect.gsub(/0x\h+/, '0x?')
    puts "VERIFY #{name} #{value.class} len=#{text.size} crc=#{Zlib.crc32(text)} #{text[0, 60].dump}"
    Ran.new(name, value, true)
  rescue Exception => e
    puts "VERIFY #{name} RAISED #{e.class}: #{e.message.lines.first.to_s.chomp[0, 120]}"
    puts e.backtrace.first(8).map { |l| "  #{l}" }
    Ran.new(name, nil, false)
  end
end

module Kernel
  private def benchmark(name = nil, &block)
    BenchSuiteVerify.run(name, block)
  end
end

module BenchmarkInterface
  def self.benchmark(name = nil, &block)
    BenchSuiteVerify.run(name, block)
  end
end
