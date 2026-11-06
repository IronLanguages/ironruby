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
using System.Text;
using Microsoft.Scripting;
using Microsoft.Scripting.Utils;
using IronRuby.Builtins;
using IronRuby.Runtime;
using System.Runtime.CompilerServices;

namespace IronRuby.Tests {
    public partial class Tests {
        private bool PreciseSinglePassTraces {
            get { 
                // We have precise traces in debug mode and if we interpret the methods. We assume that the tests are not executing any metho
                return Runtime.Setup.DebugMode || !_driver.NoAdaptiveCompilation && _driver.CompilationThreshold > 0; 
            }
        }

        public void Backtrace1() {
            AssertOutput(delegate() {
                Engine.CreateScriptSourceFromString(@"
def goo()
  foo()
end

def foo()
  bar()
end

def bar()  
  baz()
end

def baz()
  puts caller[0..3]
end

goo
", "Backtrace1.rb", SourceCodeKind.File).Execute();
            }, PreciseSinglePassTraces ? @"
Backtrace1.rb:11:in 'Object#bar'
Backtrace1.rb:7:in 'Object#foo'
Backtrace1.rb:3:in 'Object#goo'
Backtrace1.rb:18
" : @"
Backtrace1.rb:10:in 'Object#bar'
Backtrace1.rb:6:in 'Object#foo'
Backtrace1.rb:2:in 'Object#goo'
Backtrace1.rb:0
");
        }

        public void Backtrace2() {
           AssertOutput(delegate() {
                CompilerTest(@"
def goo()
  foo()
end

def foo()
  bar()
end

def bar()  
  baz()
end

def baz()
  raise
end

begin
  goo
rescue
  puts $@[0..4]
end
");
            }, PreciseSinglePassTraces ? @"
Backtrace2.rb:15:in 'baz'
Backtrace2.rb:11:in 'bar'
Backtrace2.rb:7:in 'foo'
Backtrace2.rb:3:in 'goo'
Backtrace2.rb:19
" : @"
Backtrace2.rb:14:in 'baz'
Backtrace2.rb:10:in 'bar'
Backtrace2.rb:6:in 'foo'
Backtrace2.rb:2:in 'goo'
Backtrace2.rb:0
");
        }

        public void Backtrace3() {
            AssertOutput(delegate() {
                CompilerTest(@"
def goo()
  foo()
rescue  
  raise
end

def foo()
  bar()
end

def bar()
  baz()
rescue  
  raise
end

def baz()
  raise
end

begin
  goo
rescue
  puts $@[0..4]
end
");
            }, PreciseSinglePassTraces ? @"
Backtrace3.rb:19:in 'baz'
Backtrace3.rb:13:in 'bar'
Backtrace3.rb:9:in 'foo'
Backtrace3.rb:3:in 'goo'
Backtrace3.rb:23
" : @"
Backtrace3.rb:18:in 'baz'
Backtrace3.rb:12:in 'bar'
Backtrace3.rb:8:in 'foo'
Backtrace3.rb:2:in 'goo'
Backtrace3.rb:0
");
        }

        public class ClrBacktrace {
            [MethodImpl(MethodImplOptions.NoInlining)]
            public void Bar(Action d) {
                d();
            }
        }

        public void Backtrace4() {
            Context.ObjectClass.SetConstant("C", Context.GetClass(typeof(ClrBacktrace)));
            Context.ObjectClass.SetConstant("A", Context.GetClass(typeof(Action)));

            AssertOutput(delegate() {
                CompilerTest(@"
def goo
  puts caller[0..4]
end

def baz
  goo
end

def foo
  C.new.bar(A.new { baz })
end

foo
");
            }, PreciseSinglePassTraces ? @"
Backtrace4.rb:7:in 'baz'
Backtrace4.rb:11:in 'block in foo'
*:*:in 'Bar'
Backtrace4.rb:11:in 'foo'
Backtrace4.rb:14
" : @"
Backtrace4.rb:6:in 'baz'
Backtrace4.rb:11:in 'block in foo'
*:*:in 'Bar'
Backtrace4.rb:10:in 'foo'
Backtrace4.rb:0
", OutputFlags.Match);
        }

        public void Backtrace6() {
            TestOutput(@"
def f1
  f2 
end

def f2
  #
  #
  #
  1.times do
    #
    #
    f3
    #
    #
  end
  #
  #
  #
end

def f3
  raise
end

f1 rescue puts $@[0..5]
", PreciseSinglePassTraces ? @"
Backtrace6.rb:23:in 'f3'
Backtrace6.rb:13:in 'block in f2'
Backtrace6.rb:10:in 'Integer#times'
Backtrace6.rb:10:in 'f2'
Backtrace6.rb:3:in 'f1'
Backtrace6.rb:26
" : @"
Backtrace6.rb:22:in 'f3'
Backtrace6.rb:10:in 'block in f2'
Backtrace6.rb:6:in 'Integer#times'
Backtrace6.rb:6:in 'f2'
Backtrace6.rb:2:in 'f1'
Backtrace6.rb:0
");
        }
        
        /// <summary>
        /// A frame of ExceptionDispatchInfo.Throw is bookkeeping, not a Ruby frame: a rethrow through it keeps the frames the
        /// exception came through. The DLR's interpreter (IronLanguages/dlr #371) rethrows an exception a frame has no handler
        /// for that way, which put ":0:in 'Throw'" on top of every backtrace of an exception leaving an interpreted frame.
        /// </summary>
        public void Backtrace8() {
            AssertOutput(delegate() {
                CompilerTest(@"
def f
  System::Runtime::ExceptionServices::ExceptionDispatchInfo.throw(System::InvalidOperationException.new('x'))
end
begin
  f
rescue System::InvalidOperationException => e
  puts e.backtrace[0].sub(/:\d+:/, ':N:').sub('Object#', '')
  puts e.backtrace.grep(/Throw/).size
end
");
            }, @"
Backtrace8.rb:N:in 'f'
0
");
        }

        public void Backtrace7() {
            // TODO: start name by \0 -> bug in reflection?
            StringBuilder sb = new StringBuilder();

            int maxLength = _driver.IsDebug ? RubyStackTraceBuilder.MaxDebugModePathSize : Char.MaxValue;

            for (int i = 1; i <= maxLength; i++) {
                sb.Append((char)i);
            }

            var srcName = sb.ToString();

            var frameInfo = Engine.CreateScriptSourceFromString(@"
def foo 
  caller[0]
end

def bar
  foo
end

bar
", srcName).Execute<string>();

            if (_driver.IsDebug) {
                Assert(frameInfo.StartsWith(srcName.Substring(0, maxLength)));
            } else {
                Assert(frameInfo.StartsWith(srcName + ":"));
            }
        }
    }
}