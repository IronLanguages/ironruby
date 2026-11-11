# Loaded with -r into every benchmark process, IronRuby and CRuby alike, so
# both run exactly the same code.
#
# TruffleRuby's micro benchmarks call Primitive.blackhole(x) to keep a value
# alive so the compiler cannot delete the work that made it. Outside
# TruffleRuby the method does not exist; an identity function is the
# portable meaning (neither CRuby nor IronRuby eliminates dead calls).

$stdout.sync = true

unless defined?(Primitive)
  module Primitive
    def self.blackhole(value) = value
  end
end
