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
    // The continuation library (Src/StdLib/ironruby/continuation.rb). The expected output is CRuby 4.0's,
    // except for the late call, which CRuby supports and IronRuby cannot.
    public partial class Tests {
        /// <summary>
        /// require 'continuation' was a LoadError, which stopped CRuby's test_hash, test_enum, test_fiber,
        /// test_settracefunc and test_beginendblock before their first test. callcc gives escaping continuations.
        /// </summary>
        public void Continuation_Escape1() {
            TestOutput(@"
require 'ruby4'
err = Object.new
def err.write(*s) = (($w ||= +'') << s.join; 0)
$stderr, old = err, $stderr
require 'continuation'
$stderr = old
puts $w.sub(/\A.*continuation\.rb: /, '')
p respond_to?(:callcc), respond_to?(:callcc, true), Kernel.respond_to?(:callcc)
p callcc { |c| c.call(1, 2); 3 }, callcc { 5 }, callcc { |c| c.call }, callcc { |c| c[3] }
p callcc { |c| [1, 2].each { |x| c.call(x * 10) }; :no }
p(callcc { |c| begin; c.call(:jumped); rescue Exception; :rescued; end })
c = callcc { |k| k }
p c.class, Continuation.instance_methods(false).sort, c.method(:call).arity, c.instance_variables
p((Continuation.new rescue $!), (Continuation.allocate rescue $!), (callcc rescue $!))
begin
  c.call
rescue NotImplementedError => e
  p e.class
end
", @"
warning: callcc is obsolete; use Fiber instead
false
true
true
[1, 2]
5
nil
3
10
:jumped
Continuation
[:[], :call]
-1
[]
#<NoMethodError: undefined method 'new' for class Continuation>
#<TypeError: allocator undefined for Continuation>
#<LocalJumpError: no block given>
NotImplementedError
");
        }

        /// <summary>
        /// A continuation called from another thread than the one its callcc ran on is CRuby's RuntimeError ("across
        /// fiber" likewise, which is not tested here: a Fiber created this late in a full run of these tests hangs).
        /// </summary>
        public void Continuation_CrossThread1() {
            TestOutput(@"
require 'ruby4'
$VERBOSE = nil
require 'continuation'
$VERBOSE = true
c = callcc { |k| k }
Thread.new { p((c.call rescue $!)) }.join
", @"
#<RuntimeError: continuation called across threads>
");
        }
    }
}
