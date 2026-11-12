# Differential matrix for Array#[] with an Enumerator::ArithmeticSequence.
#   ruby Util/array-step-matrix.rb            # CRuby reference
#   ./ir.sh Util/array-step-matrix.rb         # IronRuby
# Every combination of stride, beginning, end and exclusivity, including the
# ones that run off either end of the array in either direction.

a = [0,1,2,3,4,5]
[1,2,3,10,-1,-2,-10].each do |st|
  [0,1,2,5,6,7,8,-2,-3,-9,nil].each do |b|
    [0,1,3,5,6,7,8,-1,-4,-9,nil].each do |e|
      [false,true].each do |ex|
        next if b.nil? && e.nil?
        r = ex ? Range.new(b, e, true) : Range.new(b, e)
        label = "a[(#{b.inspect}#{ex ? '...' : '..'}#{e.inspect}).step(#{st})]"
        v = begin
              a[r.step(st)].inspect
            rescue Exception => x
              "!#{x.class}"
            end
        puts "#{label.ljust(40)} #{v}"
      end
    end
  end
end
