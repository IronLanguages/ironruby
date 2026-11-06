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

using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Scripting.Utils;
using IronRuby.Builtins;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using IronRuby.Runtime.Conversions;

namespace IronRuby.Tests {

    public class DefaultProtocolTester : LibraryInitializer {
        protected override void LoadModules() {
            DefineGlobalModule("Tests", typeof(DefaultProtocolTester), (int)ModuleRestrictions.NoUnderlyingType, null, (module) => {
                    DefineLibraryMethod(module, "to_int_to_str", (int)RubyMethodAttributes.PublicSingleton, new[] {
                        LibraryOverload.Reflect(new Func<RubyModule, Union<int, MutableString>, RubyArray>(ToIntToStr)),
                    });
                    DefineLibraryMethod(module, "to_str_to_int", (int)RubyMethodAttributes.PublicSingleton, new[] {
                        LibraryOverload.Reflect(new Func<RubyModule, Union<MutableString, int>, RubyArray>(ToStrToInt)),
                    });
                }, 
                null, RubyModule.EmptyArray);
        }

        public static RubyArray ToIntToStr(RubyModule/*!*/ self, [DefaultProtocol]Union<int, MutableString> value) {
            return RubyOps.MakeArray2(value.First, value.Second);
        }

        public static RubyArray ToStrToInt(RubyModule/*!*/ self, [DefaultProtocol]Union<MutableString, int> value) {
            return RubyOps.MakeArray2(value.First, value.Second);
        }
    }

    public partial class Tests {
        public void ToIntegerConversion1() {
            AssertOutput(delegate() {
                CompilerTest(@"
class C
  def to_i
    1000000000000
  end
end

class D
  def to_int
    1
  end
end

class E
  def respond_to? name
    puts name
    false
  end
end

puts Integer(123)
puts Integer(1230000000000000)
puts Integer(C.new)
puts Integer(D.new)
puts Integer(E.new) rescue puts $!
");
            }, @"
123
1230000000000000
1000000000000
1
to_int
to_i
can't convert E into Integer
");
        }

        private const string StrIntDeclarations = @"
require $protocol_tester

class A
  def to_str
    'A'
  end
end

class B
  def to_int
    1
  end
end

class C
  def to_str
    'C'
  end

  def to_int
    2
  end
end

class E
  def respond_to? name
    puts name
    false
  end
end
";
        
        public void ToIntToStrConversion1() {
            Context.DefineGlobalVariable("protocol_tester", MutableString.CreateAscii(typeof(DefaultProtocolTester).AssemblyQualifiedName));

            Engine.Execute(StrIntDeclarations);

            AssertOutput(delegate() {
                CompilerTest(@"
p Tests.to_int_to_str(123)
p Tests.to_int_to_str('xxx')
p Tests.to_int_to_str(A.new)
p Tests.to_int_to_str(B.new)
p Tests.to_int_to_str(C.new)
p Tests.to_int_to_str(E.new) rescue puts $!
");
            }, @"
[123, nil]
[0, ""xxx""]
[0, ""A""]
[1, nil]
[2, nil]
to_int
to_str
no implicit conversion of E into String
");

            AssertOutput(delegate() {
                CompilerTest(@"
p Tests.to_str_to_int(123)
p Tests.to_str_to_int('xxx')
p Tests.to_str_to_int(A.new)
p Tests.to_str_to_int(B.new)
p Tests.to_str_to_int(C.new)
p Tests.to_str_to_int(E.new) rescue puts $!
");
            }, @"
[nil, 123]
[""xxx"", 0]
[""A"", 0]
[nil, 1]
[""C"", 0]
to_str
to_int
no implicit conversion of E into Integer
");
        }

        public void ConvertToFixnum1() {
            AssertOutput(() => CompilerTest(@"
class Float
  def to_int
    2
  end
end

p Array.new(4.3004, 1)                     # implicit conversion, to_int not called 
[][8e19] rescue p $!                       # overflow -> RangeError

# Int64 is missing from this list for the same reason Int32 is: it is one of the CLR types
# the Ruby Integer class extends, so System::Int64 IS Integer and has no .new.
include System
[Byte, SByte, Int16, UInt16, UInt32, UInt64, Single, Decimal].each_with_index do |n,i|
  p Array.new(n.new(i), i)
end
"), @"
[1, 1, 1, 1]
#<RangeError: float * out of range of integer>
[]
[1]
[2, 2]
[3, 3, 3]
[4, 4, 4, 4]
[5, 5, 5, 5, 5]
[6, 6, 6, 6, 6, 6]
[7, 7, 7, 7, 7, 7, 7]
", OutputFlags.Match);
        }

        /// <summary>
        /// Kernel#Array first tries to_ary and then to_a. We need to invalidate the cache when to_ary is added.
        /// </summary>
        public void ProtocolCaching1() {
            AssertOutput(delegate() {
                CompilerTest(@"
class C
  def to_a
    puts 'to_a'
    [1]
  end
end

obj = C.new
Array(obj)

class C
  def to_ary
    puts 'to_ary'
    [2]
  end
end

Array(obj)
");
            }, @"
to_a
to_ary
");
        }

        /// <summary>
        /// We need to invalidate the cache when respond_to? is added.
        /// </summary>
        public void ProtocolCaching2() {
            AssertOutput(delegate() {
                CompilerTest(@"
class C
  def to_f
    puts 'C:to_f'
    1.0
  end
end

obj = C.new
Float(obj)

class C
  def respond_to? name
    puts name
    false
  end
end

Float(obj) rescue puts 'error'
");
            }, @"
C:to_f
to_f
error
");

        }

        /// <summary>
        /// We need to invalidate the cache when to_xxx is removed.
        /// </summary>
        public void ProtocolCaching3() {
            AssertOutput(delegate() {
                CompilerTest(@"
class C
  def to_f
    puts 'C:to_f'
    1.0
  end
end

obj = C.new
Float(obj)

class C
  remove_method(:to_f)
end

Float(obj) rescue puts 'error'
");
            }, @"
C:to_f
error
");

        }

        /// <summary>
        /// Caching of to_s conversion.
        /// </summary>
        public void ProtocolCaching4() {
            AssertOutput(delegate() {
                CompilerTest(@"
class C; end
c = C.new

puts ""(#{c})""

module Kernel
  remove_method :to_s
  
  def method_missing name
    'mm:to_s'
  end
end

puts ""(#{c})""

class C; def to_s; 'C:to_s'; end; end

puts ""(#{c})""

class Array; def to_s; 'Array:to_s'; end; end
class C; def to_s; [1,2]; end; end

puts ""(#{c})""
");
            }, @"
(#<C:0x*>)
(mm:to_s)
(C:to_s)
(#<C:0x*>)
", OutputFlags.Match);

        }

        /// <summary>
        /// CompositeConversionAction.Make hands out one action per runtime and conversion, and MakeShared - what serialized
        /// code calls - the shared one, made of shared component conversions (it used to throw NullReferenceException).
        /// </summary>
        public void CompositeConversionActions1() {
            foreach (CompositeConversion conversion in Enum.GetValues(typeof(CompositeConversion))) {
                var shared = CompositeConversionAction.MakeShared(conversion);
                Assert(shared != null && shared.Context == null);
                Assert(ReferenceEquals(shared, CompositeConversionAction.MakeShared(conversion)));

                var action = CompositeConversionAction.Make(Context, conversion);
                Assert(action != null && ReferenceEquals(action.Context, Context));
                Assert(ReferenceEquals(action, CompositeConversionAction.Make(Context, conversion)));
                Assert(ReferenceEquals(action, Context.MetaBinderFactory.CompositeConversion(conversion)));
                Assert(ReferenceEquals(shared, CompositeConversionAction.MakeShared(conversion)));
                Assert(!ReferenceEquals(action, shared));
            }
        }

        /// <summary>
        /// Error and warning wording checked against CRuby 4.0.
        /// </summary>
        public void ErrorMessages_Ruby40() {
            AssertOutput(delegate() {
                Engine.CreateScriptSourceFromString(@"
def t
  r = yield
  puts ""=> #{r.inspect}""
rescue Exception => e
  puts ""#{e.class}: #{e.message}""
end
class BadInt; def to_int; 'x'; end; end
class BadIO; def to_io; 1; end; end
t { [1, 2][BadInt.new] }
t { 'a'.split(1) }
t { 'a'.scan(1) }
t { IO.select([BadIO.new]) }
t { IO.select([1]) }
t { sleep('a') }
t { sleep(-1) }
t { 'abc'.sub }
t { 'abc'.sub { } }
t { 'abc'.dup.sub!('a') }
t { 'abc'.dup.sub!('a', 'b') { } }
t { 'abc'.sub('a', 'b', 'c') }
t { 1.round(1, 2) }
t { ""\xff"".force_encoding(""BINARY"") + ""é"" }
t { /é/ =~ ""\xff"".force_encoding(""BINARY"") }
def f1(a, k:); end
def f2(a, b = 1, k:, j:); end
t { f1(1, 2) }
t { f1(k: 1) }
t { f2(k: 1, j: 2) }
t { Module.new.class_eval { raise 'x' } rescue $!.backtrace.grep(/Module#/) }
t { Module.new.class_exec { raise 'x' } rescue $!.backtrace.grep(/Module#/) }
$stderr = $stdout
X = 1
X = 2
$VERBOSE = true
t { Float('1e400') }
t { '-1e-400'.to_f }
", "ErrorMessages.rb", Microsoft.Scripting.SourceCodeKind.File).Execute();
            }, @"
TypeError: can't convert BadInt to Integer (BadInt#to_int gives String)
TypeError: wrong argument type Integer (expected Regexp)
TypeError: wrong argument type Integer (expected Regexp)
TypeError: can't convert BadIO to IO (BadIO#to_io gives Integer)
TypeError: no implicit conversion of Integer into IO
TypeError: can't convert String into time interval
ArgumentError: time interval must not be negative
ArgumentError: wrong number of arguments (given 0, expected 2)
ArgumentError: wrong number of arguments (given 0, expected 1..2)
ArgumentError: wrong number of arguments (given 1, expected 2)
=> ""bbc""
ArgumentError: wrong number of arguments (given 3, expected 2)
ArgumentError: wrong number of arguments (given 2, expected 0..1)
Encoding::CompatibilityError: incompatible character encodings: BINARY (ASCII-8BIT) and UTF-8
Encoding::CompatibilityError: incompatible encoding regexp match (UTF-8 regexp with BINARY (ASCII-8BIT) string)
ArgumentError: wrong number of arguments (given 2, expected 1; required keyword: k)
ArgumentError: wrong number of arguments (given 0, expected 1; required keyword: k)
ArgumentError: wrong number of arguments (given 0, expected 1..2; required keywords: k, j)
=> [""ErrorMessages.rb:30:in 'Module#class_eval'""]
=> [""ErrorMessages.rb:31:in 'Module#class_exec'""]
ErrorMessages.rb:*: warning: already initialized constant X
ErrorMessages.rb:33: warning: previous definition of X was here
ErrorMessages.rb:36: warning: Float 1e400 out of range
=> Infinity
ErrorMessages.rb:37: warning: Float -1e-400 out of range
=> -0.0
", OutputFlags.Match);
        }
    }
}
