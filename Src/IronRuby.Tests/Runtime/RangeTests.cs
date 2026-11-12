/* ****************************************************************************
 *
 * Copyright (c) Microsoft Corporation. 
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
 * copy of the license can be found in the License.html file at the root of this distribution. If 
 * you cannot locate the  Apache License, Version 2.0, please send an email to 
 * ironruby@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 *
 * ***************************************************************************/


namespace IronRuby.Tests {

    public partial class Tests {
        public void RangeConditionInclusive1() {
            TestRangeCondition(true, @"
0: b -> false
1: FALSE
1: b -> false
2: FALSE
2: b -> true
3: e -> true
4: TRUE
4: b -> true
5: e -> true
6: TRUE
6: b -> true
7: e -> false
8: TRUE
8: e -> true
9: TRUE
9: b -> false
10: FALSE
10: b -> false
11: FALSE
11: b -> true
12: e -> true
13: TRUE
13: b -> nil
14: FALSE
");
        }

        public void RangeConditionExclusive1() {
            TestRangeCondition(false, @"
0: b -> false
1: FALSE
1: b -> false
2: FALSE
2: b -> true
3: TRUE
3: e -> true
4: TRUE
4: b -> true
5: TRUE
5: e -> true
6: TRUE
6: b -> true
7: TRUE
7: e -> false
8: TRUE
8: e -> true
9: TRUE
9: b -> false
10: FALSE
10: b -> false
11: FALSE
11: b -> true
12: TRUE
12: e -> true
13: TRUE
13: b -> nil
14: FALSE
");
        }
        
        private void TestRangeCondition(bool inclusive, string/*!*/ expected) {
            AssertOutput(delegate() {
                CompilerTest(@"
F = false
T = true
x = X = '!'

B = [F,F,T,x,T,x,T,x,x,F,F,T,x]
E = [x,x,x,T,x,T,x,F,T,x,x,x,T]
       
def b
  r = B[$j]
  puts ""#{$j}: b -> #{r.inspect}""
  
  $j += 1
  
  $continue = !r.nil?  
  r == X ? raise : r  
end

def e
  r = E[$j]
  puts ""#{$j}: e -> #{r.inspect}""
  
  $j += 1
  
  $continue = !r.nil?  
  r == X ? raise : r  
end

$j = 0
$continue = true
while $continue
  if b<..>e 
    puts ""#{$j}: TRUE"" 
  else
    puts ""#{$j}: FALSE"" 
  end
end
".Replace("<..>", inclusive ? ".." : "..."));
            }, expected);
        }

        /// <summary>
        /// The state variable of a range condition is allocated in the inner-most non-block lexical scope.
        /// </summary>
        public void RangeCondition1A() {
            TestOutput(@"
$i = 0

def foo
  $i += 1
  $i == 1
end     

def test
  l = lambda { |i|
    puts(foo..false ? 'true' : 'false')
  }
  
  1.times(&l)
  1.times(&l)
  1.times(&l)
  1.times(&l)
end

test
", @"
true
true
true
true
");
        }

        /// <summary>
        /// The state variable of a range condition allocated the inner-most non-block lexical scope.
        /// </summary>
        public void RangeCondition1B() {
            TestOutput(@"
$i = 0

2.times {
  module M  
    def self.foo
      $i += 1
      $i == 1
    end     

    2.times {
      puts(foo..false ? 'true' : 'false')
    }
  end
}  
", @"
true
true
false
false
");
        }

        /// <summary>
        /// The state variable of a range condition is statically allocated. 
        /// </summary>
        public void RangeCondition1C() {
            TestOutput(@"
$i = 0

2.times {
  module M  
    def self.foo
      $i += 1
      $i == 1
    end  

    2.times {
      eval(""puts(foo..false ? 'true' : 'false')"")
    }
  end
}  
", @"
true
false
false
false
");
        }

        /// <summary>
        /// Block expressions propagate 'in-condition' property to the last statement.
        /// A parenthesized statement list does not: its range is a Range (true..false: an ArgumentError),
        /// and ruby -w warns about the literals in void context before it.
        /// </summary>
        public void RangeCondition2() {
            TestOutput(@"
puts((true..false) ? 'true' : 'false')
puts(begin true..false end ? 'true' : 'false')

begin
  puts((0;1;true..false) ? 'true' : 'false')
rescue ArgumentError => e
  puts e.message
end
", @"
true
true
bad value for range
", 2);
        }

        /// <summary>
        /// A range that contains itself: inspect writes "(... .. ...)" where it recurs and == and eql? answer
        /// true there, as MRI's rb_exec_recursive does. All three overflowed the stack, which killed CRuby's
        /// test_range.
        /// </summary>
        public void Range_Recursive1() {
            TestOutput(@"
class CyclicRange < Range
  def <=>(other); true; end
end
x = CyclicRange.allocate; x.send(:initialize, x, 1)
y = CyclicRange.allocate; y.send(:initialize, y, 1)
z = CyclicRange.allocate; z.send(:initialize, z, :another)
e = CyclicRange.allocate; e.send(:initialize, e, 1, true)
p x, e
p x == y, x.eql?(y), x == z, x.eql?(z)
a = CyclicRange.allocate
b = CyclicRange.allocate
a.send(:initialize, b, 1)
b.send(:initialize, a, 1)
p a == b, a.eql?(b)
", @"
(... .. ...)..1
(... ... ...)...1
true
true
false
false
true
true
");
        }
    }
}
