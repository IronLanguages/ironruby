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
using Microsoft.Scripting;
using Microsoft.Scripting.Runtime;
using IronRuby.Compiler;
using IronRuby.Compiler.Ast;
using IronRuby.Builtins;
using System.Numerics;
using System.Reflection;
using System.Reflection.Emit;
using Microsoft.Scripting.Utils;
using Microsoft.Scripting.Generation;
using System.Collections.Generic;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;

namespace IronRuby.Tests {

    public partial class Tests {
        public void Scenario_Globals1() {
            Context.DefineGlobalVariable("x", 123);
            CompilerTest(@"$z = $x");
            object z = Context.GetGlobalVariable("z");
            AssertEquals(z, 123);
        }

        public void Scenario_RubyLocals1() {
            AssertOutput(delegate() {
                CompilerTest(@"
def foo()
  x = 1
  y = 2
  puts x + y
end

foo
");
            }, "3");
        }

        public void Scenario_UninitializedVars1() {
            AssertOutput(delegate() {
                CompilerTest(@"
def foo
  x = 1 if false
  puts x
end

foo
");
            }, @"");
        }

        public void Scenario_UninitializedVars2() {
            AssertExceptionThrown<MemberAccessException>(delegate() {
                CompilerTest(@"
def foo
  puts x
end

foo
");
            });
        }

        public void Scenario_RubyParams1() {
            AssertOutput(delegate() {
                CompilerTest(@"
def foo(a,b)
    puts a + b
end

puts foo(1,2)
");
            },
            @"
3
");
        }

        public void Scenario_RubyParams2() {
            AssertOutput(delegate() {
                CompilerTest(@"
def foo(a, b, c = a + b, d = c + 1)
  puts a,b,c,d
end

foo(1,2) 
");
            },
            @"
1
2
3
4
");
        }

        public void Scenario_RubyMethodMissing1() {
            AssertOutput(delegate {
                CompilerTest(@"
class C
  def method_missing(name, *a)
    puts name, a
  end
end

C.new.foo 1,2,3
");
            }, @"
foo
1
2
3
");

        }
        
        public void Scenario_RubyMethodMissing2() {
            AssertExceptionThrown<MissingMethodException>(delegate {
                CompilerTest(@"unknown_method");
            }, delegate(MissingMethodException e) {
                return e.Message.StartsWith("undefined method");
            });
        }

        public void Scenario_RubySingletonConstants1() {
            AssertOutput(delegate {
                CompilerTest(@"
puts nil.to_s
puts true.to_s
puts false.to_s

puts nil | false
puts true & []
puts false ^ []
puts nil.nil?
puts nil.to_i
puts nil.to_f
");
            }, @"

true
false
false
true
true
true
0
0.0
");

        }

        public void Scenario_RubyMath1() {
            AssertOutput(delegate {
                CompilerTest(@"
puts Math.acos(1.0)
puts Math.acos(1)
");
            }, @"
0.0
0.0");

        }

        public void Scenario_RubyScopeParsing() {
            LoggingErrorSink log = new LoggingErrorSink();

            SourceUnitTree p;
            SourceUnit unit;

            unit = Context.CreateSnippet(@"
                class c << G
                end
            ", SourceCodeKind.File);

            // the three errors `ruby -c` reports: the class name (twice, as prism gives it), and the <<
            p = RubyContext.EffectiveParser(unit, new RubyCompilerOptions(), log);
            Assert(p == null && log.FatalErrorCount == 3, String.Format("tree: {0}, fatal errors: {1}, errors: {2}",
                p != null, log.FatalErrorCount, log.ErrorCount));
            log.ClearCounters();

            unit = Context.CreateSnippet(@"
                def goo(&b)
                    end

                    class C
                        def foo()
                           x.goo() { 
                                goo() {
                                    class << M
                                        def bar()
                                            goo() {
                                            }
                                        end
                                    end
                                }
                           } 
                        end
                    end

                BEGIN { goo1() { } }
                END { goo2() { } } 
            ", SourceCodeKind.File);

            p = RubyContext.EffectiveParser(unit, new RubyCompilerOptions(), ErrorSink.Null);
            Assert(p != null && !log.AnyError);
            log.ClearCounters();

            unit = Context.CreateSnippet(@"
                for x in array do
                    goo()
                end
            ", SourceCodeKind.File);

            p = RubyContext.EffectiveParser(unit, new RubyCompilerOptions(), ErrorSink.Null);
            Assert(p != null && !log.AnyError);
            log.ClearCounters();
        }

        /// <summary>
        /// Tests that class lexical scopes are applied to method definitions.
        /// </summary>
        public void Scenario_RubyScopes1() {
            AssertOutput(delegate() {
                CompilerTest(@"
class A; def foo; print 'A'; end; end
class B; def foo; print 'B'; end; end
A.new.foo
B.new.foo
");
            }, @"AB");
        }

        /// <summary>
        /// Uninitialized local variables.
        /// </summary>
        public void Scenario_RubyScopes2A() {
            AssertOutput(delegate() {
                CompilerTest(@"
def foo
  x = 1 if false
  p x
end

foo
");
            }, @"nil");
        }

        /// <summary>
        /// Uninitialized local variables in closure.
        /// </summary>
        public void Scenario_RubyScopes2B() {
            TestOutput(@"
def foo
  x = 1 if false
  1.times { p x }
end

foo
",
@"nil"
);
        }

        /// <summary>
        /// Tests that variables defined in module/class locals scope are not visible outside.
        /// </summary>
        public void Scenario_RubyScopes3() {
            AssertOutput(delegate() {
                CompilerTest(@"
y = 'var'
class C
    x = 'var'
end

def x; 'method'; end
def y; 'method'; end

puts x
puts y
", 1, 0);
            }, @"
method
var");
        }

        public void Scenario_RubyScopes4() {
            AssertOutput(delegate() {
                CompilerTest(@"
module Bar
    class C
      module M
        puts self
      end
    end
end
");
            }, @"
Bar::C::M
");
        }

        public void Scenario_RubyScopes5() {
            AssertOutput(delegate() {
                CompilerTest(@"
module M
  module Z
    E = self
  end
end

include M::Z

puts E
");
            }, @"
M::Z
");
        }

        /// <summary>
        /// Nested module scopes - they don't have DLR tuples and therefore need to be skipped when looking up a storage from closure.
        /// </summary>
        public void Scenario_RubyScopes6() {
            CompilerTest(@"
1.times {
  module M
    module N
      z = 1
      1.times {
        x = z
      }
    end
  end
}
", 1, 0);
        }

        public void NumericLiterals1() {
            AssertOutput(delegate() {
                CompilerTest(@"
puts(1)
puts(-1)
puts(+1)
puts(1.1)
puts(-1.1)
puts(+1.1)

x = 2.0
puts x
puts 2.0
puts 2.1560
");
            }, @"
1
-1
1
1.1
-1.1
1.1
2.0
2.0
2.156
");
        }

        public void NumericOps1() {
            // Overflow tests. What overflows an Int32 is now an Int64, not a BigInteger - only a
            // value too wide for a long reaches BigInteger.
            Assert((long)ClrInteger.Minus(Int32.MinValue) == -(long)Int32.MinValue);
            Assert((long)ClrInteger.Abs(Int32.MinValue) == -(long)Int32.MinValue);

            Assert((long)ClrInteger.Divide(Int32.MinValue, -1) == -(long)Int32.MinValue);
            Assert(ClrInteger.Modulo(Int32.MinValue, -1) == 0);

            var dm = ClrInteger.DivMod(Int32.MinValue, -1);
            Assert((long)dm[0] == -(long)Int32.MinValue);
            Assert((int)dm[1] == 0);

            Assert((int)ClrInteger.LeftShift(1, Int32.MinValue) == 0);
            AssertExceptionThrown<ArgumentOutOfRangeException>(() => ClrInteger.RightShift(1, Int32.MinValue));
        }

        public void Scenario_RubyInclusions1() {
            AssertOutput(delegate() {
                CompilerTest(@"
module M
  def foo
    puts 'foo'
  end
end

class C
  include M
end

C.new.foo
");
            }, "foo");
        }

        public void Scenario_RubyClassVersions1() {
            AssertOutput(delegate() {
                CompilerTest(@"
class C
  def m
    puts 'v1'
  end
end

C.new.m

class C
  def m
    puts 'v2'
  end
end

C.new.m
");
            },
@"
v1
v2
");
        }

        public void Scenario_RubyClassVersions2() {
            AssertOutput(delegate() {
                CompilerTest(@"
module M
  def m; puts 'v1'; end
end

module N
  def m; puts 'v2'; end
end

class C
  include M
end

class D < C 
end

D.new.m

class D
  include N
end

D.new.m
");
            },
@"
v1
v2
");
        }

        public void InvokeMemberCache1() {
            AssertOutput(delegate() {
                CompilerTest(@"
def try_call
  B.m rescue puts 'error' 
end

class B; end

try_call       # m not defined

class Class
  def m; puts 'ok' end
end

try_call       # m defined on Class
");
            },
@"
error
ok
");
        }

        public void Scenario_RubyBlockExpressions1() {
            AssertOutput(delegate() {
                CompilerTest(@"
puts class Foo
  'A'
end

puts def bar
  'B'
end

puts module M
  'C'
end

puts begin
  'D'
end

x = ('E'; 'F')
puts x
", 1, 0);
            }, @"
A
bar
C
D
F");
        }

        public void Scenario_ClassVariables1() {
            TestOutput(@"
module M
  @@m = 1
  
  def goo
    @@n = 2
  end
end

class C
  include M

  def foo
    @@a = 1
  end
  
  @@b = 2
  @@c = 1
end

class D < C
  def bar
    @@c = 3
  end
  
  @@d = 4
end

C.new.foo
D.new.bar
C.new.goo

p C.class_variables.sort
p D.class_variables.sort
p M.class_variables.sort
", @"
[:@@a, :@@b, :@@c, :@@m, :@@n]
[:@@a, :@@b, :@@c, :@@d, :@@m, :@@n]
[:@@m, :@@n]
");  // class_variables includes inherited ones by default (as CRuby does)
        }

        public void Scenario_ClassVariables2() {
            AssertOutput(delegate() {
                CompilerTest(@"
class C
  @@x = 1
end

class D < C
  remove_class_variable :@@x rescue puts 'Error'
  @@y = 'foo'
  puts remove_class_variable(:@@y)
end
");
            }, @"
Error
foo
");
        }



        public void Return1() {
            TestOutput(@"
def foo()
  return 123
end

p foo
", @"
123
");
        }

        public void Return2() {
            TestOutput(@"
def f1
  return *[1,2],*[3,4],5,*[6],*[],*7,*nil
end

def f2
  return 1,2,3, a: 2, b: 3
end

def f3
  return :a => 2, :b => 3
end

def f4
  a = [2,3]
  return 1, *a, :a => 2, :b => 3
end

p f1, f2, f3, f4
", @"
[1, 2, 3, 4, 5, 6, 7]
[1, 2, 3, {:a=>2, :b=>3}]
{:a=>2, :b=>3}
[1, 2, 3, {:a=>2, :b=>3}]
");
        }
        
        public void Return3() {
            TestOutput(@"
x = while true do
  break 1,2,3
end
puts x
", @"
1
2
3
");
        }

        public void Return4() {
            TestOutput(@"
x = while true do
  break 1 => 2, 3 => 4
end
puts x
", @"
{1=>2, 3=>4}
");
        }

        public void Return5() {
            TestOutput(@"
x = while true do
  break 'a', 'b', 1 => 2, 3 => 4
end
puts x
", @"
a
b
{1=>2, 3=>4}
");
        }

        public void Return6() {
            TestOutput(@"
x = while true do
  break 'a', 'b', *['A', 'B'], 1 => 2, 3 => 4
end
puts x
", @"
a
b
A
B
{1=>2, 3=>4}
");
        }

        public void Return7() {
            TestOutput(@"
def foo
  return 1,2,3, *[6,7], 4 => 5
end
puts foo
", @"
1
2
3
6
7
{4=>5}
");
        }

        public void Return8() {
            TestOutput(@"
def foo
  return *$x = [1,2]
end
$y = foo

puts $x.object_id == $y.object_id
puts $x.inspect
", @"
true
[1, 2]
");
        }

        public void Scenario_RubyClosures1() {
            AssertOutput(delegate() {
                CompilerTest(@"
def foo
    y = 10
    3.times { |x| puts x + y }
end

foo
");
            }, @"
10
11
12");
        }
        
        public void ClassVariables1() {
            AssertOutput(delegate() {
                CompilerTest(@"
module M
  @@m = 3
  def moo
    puts @@m
    puts @@a rescue puts '!a'
  end
end

class A
  @@a = 1

  include M  

  class B
    @@b = 2
    def foo
      puts @@b
    end
  end  

  def foo
    puts @@a
  end
end

x = A.new
y = A::B.new
x.foo
x.moo
y.foo
");
            }, @"
1
3
!a
2
");
        }

        public void Scenario_RubyThreads1() { 
            AssertOutput(delegate() {
                CompilerTest(@"
t = Thread.new 1,2,3 do |a,b,c| 
    puts a,b,c 
end
t.join
puts 4
");
            }, @"
1
2
3
4
");
        }

        public void Scenario_YieldCodeGen() {
            AssertOutput(delegate() {
                CompilerTest(@"
def foo
    puts 1, yield
end
foo { 2 }
");
            }, @"
1
2
");
        }

        public void Scenario_MainSingleton() { 
            AssertOutput(delegate() {
                CompilerTest(@"
require 'mscorlib'
include System::Collections
puts ArrayList
");
            }, @"System::Collections::ArrayList");
        }

        /// <summary>
        /// With TZ unset the zone is the machine's, daylight saving time included. Windows has no /etc/localtime,
        /// and the zone used to be TimeZoneInfo.Local's base offset alone, so Time.now, Time.local and File.mtime
        /// were an hour off all summer. Compared with .NET's own answer, so it holds in any machine zone. The zone's
        /// name, which on Windows is in the installation's language and need not be ASCII, survives Marshal.
        /// </summary>
        public void Time_MachineZone1() {
            TestOutput(@"
ENV.delete('TZ')
local = System::TimeZoneInfo.Local
# 2026-01-15 and 2026-07-15, 10:00 UTC
[1768471200, 1784109600].each do |s|
  t = Time.at(s)
  expected = local.GetUtcOffset(System::DateTimeOffset.FromUnixTimeSeconds(s)).TotalSeconds.to_i
  dst = local.IsDaylightSavingTime(System::DateTimeOffset.FromUnixTimeSeconds(s))
  w = Time.local(t.year, t.month, t.day, t.hour, t.min, t.sec)
  p [t.utc_offset == expected, t.isdst == dst, w.to_i == s, w.utc_offset == expected]
end
p Time.now.utc_offset == local.GetUtcOffset(System::DateTimeOffset.UtcNow).TotalSeconds.to_i

t = Time.at(1784109600)
p Marshal.load(Marshal.dump(t)).zone == t.zone
", @"
[true, true, true, true]
[true, true, true, true]
true
true
");
        }

        /// <summary>
        /// An Olson TZ name resolves on Windows too, where there is no zoneinfo file to read it from.
        /// </summary>
        public void Time_OlsonZone1() {
            TestOutput(@"
saved = ENV['TZ']
begin
  ENV['TZ'] = 'Europe/Zurich'
  w = Time.at(1768471200)  # 2026-01-15 10:00 UTC
  s = Time.at(1784109600)  # 2026-07-15 10:00 UTC
  p [w.hour, w.utc_offset, w.isdst], [s.hour, s.utc_offset, s.isdst]
  p Time.local(2026, 7, 15, 12).to_i, Time.local(2026, 1, 15, 11).to_i
ensure
  ENV['TZ'] = saved
end
", @"
[11, 3600, false]
[12, 7200, true]
1784109600
1768471200
");
        }

        /// <summary>
        /// Process.spawn redirected to File::NULL, and Process.kill. On Windows File::NULL was "/dev/null",
        /// so the redirect failed with an ENOENT that named the program; Process.kill P/Invoked libc's
        /// kill() and raised DllNotFoundException. The child reads a pipe nobody writes to, so it runs
        /// until it is killed. File::NULL and Process.spawn are the prelude's (ruby4.rb), which needs prism.
        /// </summary>
        public void Process_SpawnKill1() {
            TestOutput(@"
require 'ruby4'
windows = File::ALT_SEPARATOR == '\\'
p File::NULL == (windows ? 'NUL' : '/dev/null'), IO::NULL == File::NULL

command = windows ? ['cmd.exe', '/c', 'more'] : ['cat']
r, w = IO.pipe
pid = Process.spawn(*(command + [{:in => r, :out => File::NULL, :err => File::NULL}]))
r.close
p Process.kill(0, pid), Process.kill(:KILL, pid)
p Process.wait(pid) == pid
w.close
begin
  Process.kill(0, pid)
rescue Errno::ESRCH
  p :gone
end

# a redirection that cannot be opened is what the error names (Windows; posix_spawn cannot tell)
begin
  Process.spawn(*(command + [{:out => 'no/such/dir/out.txt'}]))
rescue Errno::ENOENT => e
  p(!windows || e.message.include?('no/such/dir/out.txt'))
end

# a signal the process sends itself from another thread runs its handler on the main thread
# (Windows, where there is no kill(2) to deliver it; elsewhere the kernel does)
if windows
  got = nil
  old = trap(:TERM) { got = :handled }
  Thread.new { Process.kill(:TERM, Process.pid) }.join
  20.times { break if got; sleep 0.05 }
  trap(:TERM, old)
  p got
else
  p :handled
end
", @"
true
true
1
1
true
:gone
true
:handled
");
        }

        /// <summary>
        /// A drive-relative name's dirname is "C:." on Windows, as in MRI - which is how CRuby's own
        /// pathname_builtin.rb recognises drive letters, so Pathname#absolute? depends on it.
        /// </summary>
        public void File_DriveLetters1() {
            TestOutput(@"
require 'ruby4'
require 'pathname'
windows = File::ALT_SEPARATOR == '\\'
p File.dirname('A:') == (windows ? 'A:.' : '.')
p File.dirname('C:x') == (windows ? 'C:.' : '.'), File.basename('C:x') == (windows ? 'x' : 'C:x')
p Pathname.new('C:/x/y').absolute? == windows, Pathname.new('/x').absolute?
", @"
true
true
true
true
true
");
        }

        /// <summary>
        /// Fiddle::Handle finds a Win32 function by the name without its A/W suffix, as CRuby's
        /// fiddle does (it tries "A" next): kernel32 exports FillConsoleOutputCharacterA and ...W
        /// but no FillConsoleOutputCharacter, and reline asks for that one - irb died at startup
        /// on Windows without it. Elsewhere there is no suffix to try; the answers are the same.
        /// </summary>
        public void Fiddle_AnsiSuffix1() {
            TestOutput(@"
require 'fiddle'
if File::ALT_SEPARATOR == '\\'
  k = Fiddle::Handle.new('kernel32')
  p k['GetModuleHandle'] == k['GetModuleHandleA'], !k.sym_defined?('FillConsoleOutputCharacter').nil?
  begin
    k['NoSuchFunction']
  rescue Fiddle::DLError => e
    p e.message
  end
else
  p true, true
  p 'unknown symbol ""NoSuchFunction""'
end
", @"
true
true
""unknown symbol \""NoSuchFunction\""""
");
        }

        /// <summary>
        /// [str].pack("p") is a real address, as in CRuby: C reads the String there, and what C
        /// writes there through Fiddle lands in the String. It used to be an index into the
        /// packed string's list of Strings, and C reading it crashed the process.
        /// </summary>
        public void PackPointer_RealAddress1() {
            TestOutput(@"
require 'fiddle'
lib = case RUBY_PLATFORM
      when /mswin|mingw/ then 'msvcrt.dll'
      when /darwin/ then '/usr/lib/libSystem.B.dylib'
      else 'libc.so.6'
      end
libc = Fiddle.dlopen(lib)
strlen = Fiddle::Function.new(libc['strlen'], [Fiddle::TYPE_VOIDP], Fiddle::TYPE_SIZE_T)
memset = Fiddle::Function.new(libc['memset'], [Fiddle::TYPE_VOIDP, Fiddle::TYPE_INT, Fiddle::TYPE_SIZE_T], Fiddle::TYPE_VOIDP)

# C only gets an address that can be one: what pack wrote before was an index, and C reading
# or writing there kills the process, which no test can report.
s = 'hello'.dup
addr = [s].pack('p').unpack1('J')
buf = ""\0"" * 6
buf_addr = [buf].pack('p').unpack1('J')
if addr > 0x10000 && buf_addr > 0x10000
  p Fiddle::Pointer.new(addr).to_s, strlen.call(addr)
  memset.call(buf_addr, 0x41, 3)
  p buf
else
  p [:not_an_address, addr, buf_addr]
end
p [s].pack('p').unpack1('p'), [nil].pack('p').unpack1('J')
begin
  [addr].pack('J').unpack1('p')
rescue ArgumentError => e
  p e.message
end
", @"
""hello""
5
""AAA\u0000\u0000\u0000""
""hello""
0
""no associated pointer""
");
        }

        /// <summary>
        /// reline's own Win32API, unmodified, hands the console API its buffers as
        /// [str].pack(""p"").unpack(""q*"") and reads what the call wrote from str - which is
        /// what irb does at startup in a Windows console, and what crashed there.
        /// </summary>
        public void PackPointer_RelineWin32Api1() {
            TestOutput(@"
if File::ALT_SEPARATOR == '\\'
  require 'ruby4'
  require 'reline'
  api = Reline::Windows::Win32API.new('kernel32', 'GetModuleFileNameA', ['L', 'P', 'L'], 'L')
  path = ""\0"" * 260
  if [path].pack('p').unpack1('J') > 0x10000
    n = api.call(0, path, 260)
    p n > 0, path[0, n].downcase.end_with?('.exe')
  else
    p :not_an_address
  end
else
  p true, true
end
", @"
true
true
");
        }

        /// <summary>
        /// A relative Dir.chdir is resolved once, from the directory in force - Dir.pwd keeping
        /// 8.3 short names once made it twice, so ".." went up two levels.
        /// </summary>
        public void Dir_ChdirRelative1() {
            TestOutput(@"
require 'tmpdir'
require 'fileutils'
root = File.join(Dir.tmpdir, ""ir-chdir-#{Process.pid}"")
FileUtils.mkdir_p(File.join(root, 'a', 'a', 'a'))
Dir.chdir(File.join(root, 'a', 'a')) do
  Dir.chdir('..')
  p File.basename(File.dirname(Dir.pwd)) == File.basename(root)
  Dir.chdir('a')
  p File.basename(File.dirname(File.dirname(Dir.pwd))) == File.basename(root)
end
FileUtils.rm_rf(root)
", @"
true
true
");
        }

        /// <summary>
        /// Without HOME, "~" and Dir.home both come from USERPROFILE before HOMEDRIVE + HOMEPATH,
        /// as in CRuby on Windows.
        /// </summary>
        public void File_ExpandTildeOrder1() {
            TestOutput(@"
require 'ruby4'
saved = %w[HOME USERPROFILE HOMEDRIVE HOMEPATH].to_h { |k| [k, ENV[k]] }
begin
  ENV.delete('HOME')
  ENV['USERPROFILE'] = 'Y:\\profile'
  ENV['HOMEDRIVE'], ENV['HOMEPATH'] = 'X:', '\\homepath'
  windows = File::ALT_SEPARATOR == '\\'
  p File.expand_path('~') == (windows ? 'Y:/profile' : File.expand_path('~'))
  p Dir.home == File.expand_path('~') || !windows
ensure
  saved.each { |k, v| ENV[k] = v }
end
", @"
true
true
");
        }

        /// <summary>
        /// IO#winsize off a terminal raises what MRI raises: ENOTTY from ioctl on Linux and macOS,
        /// EBADF from the console API on Windows, where IronRuby used to have no winsize at all -
        /// irb asks $stdout for it before showing each result, and died after the first one.
        /// A console's own size needs a console, which a test run does not have.
        /// </summary>
        public void IoConsole_WinsizeOffTerminal1() {
            TestOutput(@"
require 'ruby4'
require 'io/console'
require 'tmpdir'
windows = File::ALT_SEPARATOR == '\\'
path = File.join(Dir.tmpdir, ""ir-winsize-#{Process.pid}.txt"")
File.open(path, 'w') do |f|
  begin
    f.winsize
  rescue SystemCallError => e
    p e.class == (windows ? Errno::EBADF : Errno::ENOTTY)
  end
end
File.delete(path)
", @"
true
");
        }

        /// <summary>
        /// require 'ripper' gives Ripper::Filter, as MRI's ripper.rb does: rdoc's Ruby parser
        /// subclasses it, and `gem install` generating documentation died on the NameError.
        /// </summary>
        public void Ripper_Filter1() {
            TestOutput(@"
require 'ruby4'
require 'ripper'
class Words < Ripper::Filter
  def on_default(event, tok, data)
    data << [lineno, column, event, tok]
  end
end
Words.new(""x = 1 # one\n"").parse([]).each { |t| p t }
", @"
[1, 0, :on_ident, ""x""]
[1, 1, :on_sp, "" ""]
[1, 2, :on_op, ""=""]
[1, 3, :on_sp, "" ""]
[1, 4, :on_int, ""1""]
[1, 5, :on_sp, "" ""]
[1, 6, :on_comment, ""# one\n""]
");
        }

        /// <summary>
        /// require 'etc.so' - a C extension by its library name, as tmpdir.rb asks for it - loads
        /// IronRuby's etc, as JRuby loads its own for one. It used to be a LoadError, rescued in
        /// tmpdir.rb after RubyGems had searched every gem for the file (a quarter of a second of
        /// irb's startup), and Dir.tmpdir's system path fell back to /tmp, also on Windows.
        /// Etc.systmpdir is spelled as MRI spells it: forward slashes.
        /// </summary>
        public void Require_ExtensionByLibraryName1() {
            TestOutput(@"
p require('etc.so'), defined?(Etc.systmpdir), require('etc')
if File::ALT_SEPARATOR == '\\'
  base = [ENV['LOCALAPPDATA'], ENV['SystemRoot'], ENV['windir']].find { |d| d && !d.empty? } || 'C:/Windows'
  p Etc.systmpdir == base.tr('\\', '/') + '/temp'
else
  p Etc.systmpdir == '/tmp'
end
", @"
true
""method""
false
true
");
        }

        /// <summary>
        /// What RubyGems and Bundler need from the file system on Windows, where each of these
        /// used to P/Invoke libc or raise: File#flock (RubyGems locks every binstub it writes),
        /// IO#readpartial after IO#eof? has filled the buffer (Bundler checksums every .gem that
        /// way), and Process's resource limits, which MRI does not define on Windows at all -
        /// net-http-persistent calls getrlimit whenever RLIMIT_NOFILE is defined.
        /// </summary>
        public void File_FlockReadpartialRlimit1() {
            TestOutput(@"
require 'ruby4'
require 'tmpdir'
windows = File::ALT_SEPARATOR == '\\'
path = File.join(Dir.tmpdir, ""ir-flock-#{Process.pid}.lock"")

a = File.open(path, 'w')
b = File.open(path, 'r')
p a.flock(File::LOCK_EX), b.flock(File::LOCK_SH | File::LOCK_NB)
p a.flock(File::LOCK_UN), b.flock(File::LOCK_SH | File::LOCK_NB), b.flock(File::LOCK_UN)
a.close
b.close

File.binwrite(path, 'x' * 100_000)
total = 0
File.open(path, 'rb') { |io| total += io.readpartial(16384).bytesize until io.eof? }
p total
File.delete(path)

p Process.const_defined?(:RLIMIT_NOFILE) == !windows, Process.respond_to?(:getrlimit) == !windows
", @"
0
false
0
0
0
100000
true
true
");
        }

        /// <summary>
        /// A raw control character (U+0000-U+001F) inside a JSON string is a ParserError, as in the json gem;
        /// JSON.parse took a document with a literal line break in a value as it was. The gem's
        /// allow_control_characters option (2.18.0) lets them through.
        /// </summary>
        public void Json_ControlCharacters1() {
            TestOutput(@"
load_assembly 'IronRuby.Libraries', 'IronRuby.StandardLibrary.Json'

[10, 9, 1, 0, 31].each do |code|
  src = '{""k"": ""a' + code.chr + 'b""}'
  begin
    JSON.parse(src)
    p [code, :accepted]
  rescue JSON::ParserError => e
    p [code, e.message.start_with?('invalid ASCII control character in string')]
  end
end
p JSON.parse('[""a' + 10.chr + 'b""]', :allow_control_characters => true)
# an escaped one, and DEL, are fine
p JSON.parse('[""a\nb"", ""' + 127.chr + '""]') == ['a' + 10.chr + 'b', 127.chr]
", @"
[10, true]
[9, true]
[1, true]
[0, true]
[31, true]
[""a\nb""]
true
");
        }

        /// <summary>
        /// Zlib's streams and checksums. Windows has no libz; there they run on the zlib .NET ships
        /// (ClrZlib), so everything here holds on both: input CRuby compressed, round trips, and
        /// the values libz keeps in the z_stream (total_in, total_out, adler) rather than bytes a
        /// particular compressor produces.
        /// </summary>
        public void Zlib_Streams1() {
            TestOutput(@"
load_assembly 'IronRuby.Libraries', 'IronRuby.StandardLibrary.Zlib'

hex = lambda { |s| [s].pack('H*') }
text = 'hello hello hello hello'

# from CRuby: Zlib::Deflate.deflate(text), and Zlib.gzip(text)
p Zlib::Inflate.inflate(hex['789ccb48cdc9c957c8402701680308b1'])
p Zlib.gunzip(hex['1f8b0800486adb6a0003cb48cdc9c957c8402701e3513d8d17000000'])

# fed a few bytes at a time, as a PNG decoder does
data = 'IronRuby zlib ' * 2000
z = Zlib::Deflate.deflate(data)
inf = Zlib::Inflate.new
out = ''
z.bytes.each_slice(7) { |s| out << inf.inflate(s.pack('C*')) }
out << inf.finish
p [out == data, inf.finished?, inf.total_in == z.bytesize, inf.total_out, inf.adler == Zlib.adler32(data)]
inf.close

d = Zlib::Deflate.new
d << data[0, 10000]
d << data[10000..-1]
z = d.finish
p [d.total_in, d.total_out == z.bytesize, d.adler == Zlib.adler32(data), Zlib::Inflate.inflate(z) == data]
d.close

p Zlib.gunzip(Zlib.gzip(data)) == data
raw = Zlib::Deflate.new(Zlib::BEST_SPEED, -Zlib::MAX_WBITS).deflate(data, Zlib::FINISH)
p Zlib::Inflate.new(-Zlib::MAX_WBITS).inflate(raw) == data
p Zlib::Inflate.new(Zlib::MAX_WBITS + 32).inflate(Zlib.gzip(data)) == data

# a stream reset is a fresh one
d = Zlib::Deflate.new
d.deflate('discarded', Zlib::SYNC_FLUSH)
d.reset
z = d.deflate(text, Zlib::FINISH)
p [Zlib::Inflate.inflate(z), d.total_in]

# deflated with the preset dictionary 'hello': inflate asks for it by its adler32
inf = Zlib::Inflate.new
begin
  inf.inflate(hex['78bb062c0215cb00110ae5f9453929001a0b045d'])
rescue Zlib::NeedDict
  p [:need_dict, inf.adler == Zlib.adler32('hello')]
end

p [Zlib.crc32(text), Zlib.adler32(text), Zlib.crc32, Zlib.adler32, Zlib.crc32('lo', Zlib.crc32('hel'))]
p [Zlib.crc32_combine(Zlib.crc32('hel'), Zlib.crc32('lo'), 2) == Zlib.crc32('hello'),
   Zlib.adler32_combine(Zlib.adler32('hel'), Zlib.adler32('lo'), 2) == Zlib.adler32('hello')]
p Zlib.crc_table.size, Zlib.crc_table[1], Zlib.crc_table[255]

begin
  Zlib::Inflate.inflate('not zlib')
rescue Zlib::DataError => e
  p e.message
end

# deflateParams refuses bad values and leaves the stream as it was
d = Zlib::Deflate.new
begin
  d.params(99, 0)
rescue Zlib::StreamError
  p :refused
end
p Zlib::Inflate.inflate(d.deflate(text, Zlib::FINISH)) == text

# the parameters a stream already has are no change, even after data (the default level is 6)
d = Zlib::Deflate.new
d << text
d.params(6, Zlib::DEFAULT_STRATEGY)
p Zlib::Inflate.inflate(d.finish) == text

# deflate's guess at what the data is
d = Zlib::Deflate.new
d.deflate(text, Zlib::FINISH)
p d.data_type == Zlib::TEXT

# inflate detecting the wrapper keeps adler at 1 until it has read a whole gzip header
inf = Zlib::Inflate.new(Zlib::MAX_WBITS + 32)
inf.inflate(Zlib.gzip(text)[0, 5])
p inf.adler
", @"
""hello hello hello hello""
""hello hello hello hello""
[true, true, true, 28000, true]
[28000, true, true, true]
true
true
true
[""hello hello hello hello"", 23]
[:need_dict, true]
[2369606115, 1745029297, 0, 1, 907060870]
[true, true]
256
1996959894
755167117
""incorrect header check""
:refused
true
true
true
1
");
        }

        /// <summary>
        /// Time._load hands a dumped zone name to the class's find_timezone. As in MRI, a StandardError from the
        /// lookup leaves the name and anything else goes through; only bytes that are not a time are a TypeError.
        /// </summary>
        public void Time_LoadZone1() {
            TestOutput(@"
class Zone
  attr_reader :name
  def initialize(name); @name = name; end
  def utc_to_local(t); t + 3600; end
  def local_to_utc(t); t - 3600; end
end

class ZTime < Time
  def self.find_timezone(name)
    $lookup.call(name)
  end
end

class ZoneMissing < StandardError; end
class ZoneFailure < Exception; end

xst = Zone.new('XST')
dump = Marshal.dump(ZTime.new(2000, 1, 1, 12, 0, 0, xst))

$lookup = lambda { |name| xst }
t = Marshal.load(dump)
p t, t.zone.equal?(xst)

$lookup = lambda { |name| raise ArgumentError, 'unknown zone' }
t = Marshal.load(dump)
p t, t.zone, t.zone.frozen?

$lookup = lambda { |name| raise ZoneMissing }
p Marshal.load(dump).zone

$lookup = lambda { |name| throw :zone, name }
p catch(:zone) { Marshal.load(dump) }

$lookup = lambda { |name| raise ZoneFailure }
begin
  Marshal.load(dump)
rescue ZoneFailure => e
  p e.class
end

# 2000-16-01: the month is out of range
bad = [0x80000000 | (100 << 14) | (15 << 10) | (1 << 5), 0].pack('VV')
begin
  Time.send(:_load, bad)
rescue TypeError => e
  p e.message
end
", @"
2000-01-01 12:00:00 +0100
true
2000-01-01 12:00:00 +0100
""XST""
true
""XST""
""XST""
ZoneFailure
""marshaled time format differ""
");
        }

        /// <summary>
        /// A time loaded from a dump keeps the zone name as a String when no find_timezone makes an object of it. As in
        /// MRI, dumping it again writes the name as it is, and strftime's %Z prints it; the fixed offset has no name.
        /// </summary>
        public void Time_DumpLoadedZone1() {
            TestOutput(@"
class Zone
  attr_reader :name
  def initialize(name); @name = name; end
  def utc_to_local(t); t + 3600; end
  def local_to_utc(t); t - 3600; end
end

class NoZoneTime < Time
  def self.find_timezone(name); nil; end
end

xst = Zone.new('XST')
t = Time.new(2000, 1, 1, 12, 0, 0, xst)

twice = Marshal.load(Marshal.dump(Marshal.load(Marshal.dump(t))))
p twice, twice.zone, twice.utc_offset, twice == t
p twice.strftime('%H:%M %z %Z'), (twice + 60).strftime('%H:%M %z %Z')

t = Marshal.load(Marshal.dump(Marshal.load(Marshal.dump(NoZoneTime.new(2000, 1, 1, 12, 0, 0, xst)))))
p t.zone, t.strftime('%Z')
", @"
2000-01-01 12:00:00 +0100
""XST""
3600
true
""12:00 +0100 XST""
""12:01 +0100 XST""
""XST""
""XST""
");
        }
    
        /// <summary>
        /// MRI's dtoa settles a rounding it cannot tell from a tie in double arithmetic by the
        /// parity of the last digit, whatever the exact binary value: "%.2f" % 2.675 is 2.68
        /// although the double is below 2.675, and "%.1f" % 99.45 is 99.4 although it is above.
        /// "%g" then keeps the trailing zero dtoa leaves on such a value. The exponent of a value
        /// a few ulps below a power of ten is exact, not Math.Log10's rounding of it.
        /// </summary>
        public void Sprintf_MriTieRounding1() {
            TestOutput(@"
puts '%.2f' % 2.675, '%.1f' % 99.45, '%.1f' % 0.05, '%.0f' % 2.5, '%.0f' % 1.5
puts '%.2e' % 2.675, '%.0e' % 0.95, '%.1g' % 0.095
puts '%.5g' % 0.0676905, '%.5g' % 0.0676895, '%.2g' % 205.0, '%.2g' % 204.0
puts '%.15e' % 999.9999999999999, '%.16e' % 1.0e-07
puts '%.3f' % 1.0005, '%.2f' % 1.005, '%.1f' % 0.25, '%.17g' % 0.1, '%g' % 1234567.0, '%#.3g' % 1.0
", @"
2.68
99.4
0.1
2
2
2.68e+00
1e+00
0.1
0.067690
0.06769
2.0e+02
2e+02
9.999999999999999e+02
9.9999999999999995e-08
1.000
1.00
0.2
0.10000000000000001
1.23457e+06
1.00
");
        }

        /// <summary>Float#-@ negates the sign bit; Numeric#-@ (0 - x) lost it for 0.0.</summary>
        public void Float_NegativeZero1() {
            TestOutput(@"
f = 0.0
p(-f, 1.0 / -f, (-f).to_s, -(-f))
", @"
-0.0
-Infinity
""-0.0""
0.0
");
        }

        /// <summary>
        /// Math.cbrt is the C library's cbrt with MRI's correction step, and Math.gamma of a
        /// whole number comes from MRI's factorial table.
        /// </summary>
        public void Math_CLibrary1() {
            TestOutput(@"
p Math.cbrt(-27.0), Math.cbrt(27.0), Math.gamma(3.0), Math.gamma(23.0), Math.erf(0.0), Math.erfc(0.0)
", @"
-3.0
3.0
2.0
1.1240007277776077e+21
0.0
1.0
");
        }
    
        /// <summary>
        /// An Errno message built with the error's own text in it is not prefixed with that text
        /// again: "No such file or directory - path", not "No such file or directory - No such
        /// file or directory - path".
        /// </summary>
        public void Errno_MessageNotDoubled1() {
            TestOutput(@"
puts Errno::ENOENT.new.message
puts Errno::ENOENT.new('x').message
puts Errno::ENOENT.new('No such file or directory - x').message
puts Errno::ENOENT.new('No such file or directory @ rb_sysopen - x').message
", @"
No such file or directory
No such file or directory - x
No such file or directory - x
No such file or directory @ rb_sysopen - x
");
        }

        public void Numeric_BignumFloatOverflow1() {
            TestOutput(@"
x = 10**400
p x * 1.0, x + 1.0, x - 1.0, x / 10.0, x.fdiv(10**399), x.fdiv(10**401) <= 1e309, (-x) * 1.0
p x <=> 1e308, x > 1e308, x == 1.0 / 0
", @"
Infinity
Infinity
Infinity
Infinity
10.0
true
-Infinity
1
true
false
", 1);
        }

        public void Numeric_BignumExponent1() {
            TestOutput(@"
p 1 ** (2**70), (-1) ** (2**70), (-1) ** (2**70 + 1), 0 ** (2**70)
begin; 2 ** (2**70); rescue ArgumentError => e; p e.message; end
begin; (2**70) ** (2**70); rescue ArgumentError => e; p e.message; end
begin; 5 / 0; rescue ZeroDivisionError => e; p e.message; end
begin; (2**70) % 0; rescue ZeroDivisionError => e; p e.message; end
", @"
1
1
-1
0
""exponent is too large""
""exponent is too large""
""divided by 0""
""divided by 0""
");
        }

        public void Float_Eql1() {
            TestOutput(@"
nan = 0.0 / 0.0
p nan.eql?(nan), nan == nan, 1.0.eql?(1.0), 1.0.eql?(1), 1.0.eql?(1.5), 1.0.eql?(nil)
", @"
false
false
true
false
false
false
");
        }

        public void ComparisonError_AnonymousClass1() {
            TestOutput(@"
c = Class.new
x = c.new
begin; 1 < x; rescue ArgumentError => e; puts e.message.sub(/0x[0-9a-f]+/, 'ID'); end
begin; 'a' < x; rescue ArgumentError => e; puts e.message.sub(/0x[0-9a-f]+/, 'ID'); end
begin; 'a' < 1; rescue ArgumentError => e; puts e.message; end
begin; 1 < 'a'; rescue ArgumentError => e; puts e.message; end
", @"
comparison of Integer with #<Class:ID> failed
comparison of String with #<Class:ID> failed
comparison of String with 1 failed
comparison of Integer with String failed
");
        }

        public void Array_FlattenNil1() {
            TestOutput(@"
p [1, [2, [3]]].flatten(nil), [1, [2, [3]]].flatten(1.5), [1, [2]].flatten!(nil), [1].flatten!(nil)
begin; ""\xFF"".tr('', ''); rescue ArgumentError => e; p e.message; end
begin; ""\xFF"".tr('a', 'b'); rescue ArgumentError => e; p e.message; end
p ""\xFF"".force_encoding('BINARY').tr('a', 'b').bytes
", @"
[1, 2, 3]
[1, 2, [3]]
[1, 2]
nil
""invalid byte sequence in UTF-8""
""invalid byte sequence in UTF-8""
[255]
");
        }

        public void Hash_ValuesAtDefault1() {
            TestOutput(@"
p Hash.new(5).values_at(:x, :y), Hash.new { |h, k| k.to_s }.values_at(:a), {a: 1}.values_at(:a, :b)
h = {a: 1}
begin; h.each { h.rehash }; rescue RuntimeError => e; p e.message; end
begin; Hash.new(&lambda { |k| k }); rescue TypeError => e; p e.message; end
p Hash.new(&lambda { |h, k| k })[:z], Hash.new(&lambda { |*a| a.size })[:z]
", @"
[5, 5]
[""a""]
[1, nil]
""rehash during iteration""
""default_proc takes two arguments (2 for 1)""
:z
2
");
        }

        public void Lambda_ArityMessages1() {
            TestOutput(@"
begin; ->(a, *b) { }.call; rescue ArgumentError => e; p e.message; end
begin; ->(a) { }.call(1, 2); rescue ArgumentError => e; p e.message; end
begin; :to_s.to_proc.call; rescue ArgumentError => e; p e.message; end
p :to_s.to_proc.call(1)
", @"
""wrong number of arguments (given 0, expected 1+)""
""wrong number of arguments (given 2, expected 1)""
""no receiver given""
""1""
");
        }

        public void ErrorMessages_Receivers1() {
            TestOutput(@"
o = Object.new
def o.inspect; 'INSPECTED'; end
begin; o.foo; rescue NoMethodError => e; puts e.message.sub(/0x[0-9a-f]+/, 'ID'); end
begin; self.foo; rescue NoMethodError => e; puts e.message; end
c = Class.new { def self.inspect; 'INS'; end }
begin; c.new.foo; rescue NoMethodError => e; puts e.message; end
begin; Class.new { def m; end }.instance_method(:m).bind(1); rescue TypeError => e; puts e.message.sub(/0x[0-9a-f]+/, 'ID'); end
begin; Object.const_get('Foo::'); rescue NameError => e; puts e.message; end
begin; Object.const_get('Math::PI::X'); rescue TypeError => e; puts e.message; end
begin; Object.send(:remove_const, :foo); rescue NameError => e; puts e.message; end
begin; Object.new.singleton_class::Foo; rescue NameError => e; puts e.message; end
begin; Math::Foo; rescue NameError => e; puts e.message; end
", @"
undefined method 'foo' for #<Object:ID>
undefined method 'foo' for main
undefined method 'foo' for an instance of INS
bind argument must be an instance of #<Class:ID>
wrong constant name Foo::
Math::PI::X does not refer to class/module
'foo' is not allowed as a constant variable name
uninitialized constant Foo
uninitialized constant Math::Foo
");
        }

        /// <summary>
        /// Messages worded as CRuby 4.0 words them, beyond the quotes: super names its receiver,
        /// remove_class_variable and NUM2INT have wordings of their own, and a library's class clashing
        /// with a constant names the constant's class. (Util/error-message-matrix.rb checks these and the
        /// ones the Ruby prelude raises, which this runtime does not load, against CRuby.)
        /// </summary>
        public void ErrorMessages_Wording1() {
            TestOutput(@"
class C; def m = super; def self.cm = super; end
begin; C.new.m; rescue NoMethodError => e; p e.message, e.name, e.receiver.class; end
begin; C.cm; rescue NoMethodError => e; puts e.message; end
class Integer; def sup = super; end
begin; 1.sup; rescue NoMethodError => e; puts e.message; end
begin; C.send(:remove_class_variable, :x); rescue NameError => e; puts e.message; end
begin; C.class_variable_get(:x); rescue NameError => e; puts e.message; end
[2**31, -2**31 - 1, 2**64].each { |n| begin; 1.round(n); rescue RangeError => e; puts e.message; end }
StringIO = 1
begin; require 'stringio'; rescue TypeError => e; puts e.message; end
", @"
""super: no superclass method 'm' for an instance of C""
:m
C
super: no superclass method 'cm' for class C
super: no superclass method 'sup' for an instance of Integer
wrong class variable name x
'x' is not allowed as a class variable name
integer 2147483648 too big to convert to 'int'
integer -2147483649 too small to convert to 'int'
bignum too big to convert into 'long'
StringIO is not a class (Integer)
");
        }

        public void Time_StringParsing1() {
            TestOutput(@"
p Time.new('2024-01-02 03:04:05 UTC').utc?, Time.new('2024-01-02 03:04:05.5 Z').usec, Time.new('2024-01-02T03:04:05+09:00').utc_offset
['2024-01-02 03:04', '2024-01-02 03', '2024-01-02 03:04:05.', '24-01-02', '2024-1-2', '2024-01-02 03:04:05 +09:00 x', ""2024-01-02 03:04:05\n"", 'x'].each do |s|
  begin; Time.new(s); rescue ArgumentError => e; puts e.message; end
end
begin; Time.new(2024, 1, 1, 24, 1); rescue ArgumentError => e; puts e.message; end
begin; Time.new(2024, 0, 1); rescue ArgumentError => e; puts e.message; end
begin; Time.new(2024, 1, 0); rescue ArgumentError => e; puts e.message; end
begin; Time.new(2024, 1, 1, 1, 1, '1.5'); rescue ArgumentError => e; puts e.message; end
begin; Time.at(1.5).round(-1); rescue ArgumentError => e; puts e.message; end
begin; Time.at(2**62); rescue RangeError => e; puts e.class; end
begin; Time.at(0) + (0.0 / 0.0); rescue FloatDomainError => e; puts e.message; end
p Time.at(-1.5).round.to_i, Time.at(2.5).round.to_i
", @"
true
500000
32400
missing sec part: 03:04
missing min part: 03
subsecond expected after dot: 03:04:05.
year must be 4 or more digits: 24
two digits mon is expected after '-': -1-2
can't parse at: x
can't parse: ""2024-01-02 03:04:05\n""
can't parse: ""x""
min out of range
mon out of range
mday out of range
invalid value for Integer(): ""1.5""
negative ndigits given
RangeError
NaN
-1
3
");
        }

        public void Struct_IndexMessages1() {
            TestOutput(@"
s = Struct.new(:a).new(1)
begin; s[1]; rescue IndexError => e; puts e.message; end
begin; s[-2]; rescue IndexError => e; puts e.message; end
", @"
offset 1 too large for struct(size:1)
offset -2 too small for struct(size:1)
");
        }

        public void Backtrace_RaiseInRescue1() {
            // an exception raised inside a rescue clause keeps the frames of the rescuing method and its callers
            TestOutput(@"
def m
  begin; raise 'inner'; rescue; raise 'outer'; end
end
def n
  m
end
begin
  n
rescue => e
  puts e.backtrace.first(2).map { |l| l[/in [`'](.*)'/, 1] }
  puts e.cause.message
end
begin
  begin; raise 'a'; rescue; raise 'b'; end
rescue => e
  puts(e.backtrace[0] =~ /Compiler|Test/ ? 'host' : 'ruby')
end
begin; raise ArgumentError, 'x', 1; rescue TypeError => e; puts e.message; end
e = RuntimeError.new('u')
begin; e.set_backtrace(1); rescue TypeError => t; puts t.message; end
puts UncaughtThrowError.new(:t, 1, 'tag').tag.inspect
begin; throw :zz; rescue UncaughtThrowError => u; puts u.message, u.tag.inspect; end
", @"
m
n
inner
ruby
backtrace must be Array of String
backtrace must be Array of String
:t
uncaught throw :zz
:zz
");
        }

        public void Reflection_Misc1() {
            TestOutput(@"
class RBase; def self.cm; end; def priv; end; private :priv; def prot; end; protected :prot; end
class RSub < RBase; end
puts RSub.method(:cm).inspect.sub(/ \S+:\d+>/, '>'), RBase.method(:cm).inspect.sub(/ \S+:\d+>/, '>')
begin; RBase.public_instance_method(:priv); rescue NameError => e; puts e.message; end
begin; RBase.public_instance_method(:prot); rescue NameError => e; puts e.message; end
begin; RBase.public_instance_method(:nope); rescue NameError => e; puts e.message; end
p BasicObject.instance_method(:initialize).bind_call(Object.new)
", @"
#<Method: RSub(RBase).cm()>
#<Method: RBase.cm()>
method 'priv' for class 'RBase' is private
method 'prot' for class 'RBase' is protected
undefined method 'nope' for class 'RBase'
nil
");
        }

        public void Thread_JoinSelf1() {
            TestOutput(@"
t = Thread.new { Thread.current.join rescue $!.message }; puts t.value
t = Thread.new { Thread.main.join(0) rescue $!.message }; puts t.value
t = Thread.new { Thread.current.value rescue $!.message }; puts t.value
begin; Thread.current.join; rescue ThreadError => e; puts e.message; end
puts ThreadGroup.new.inspect.sub(/0x\h+/, 'X')
p Thread.respond_to?(:critical)
", @"
Target thread must not be current thread
Target thread must not be main thread
Target thread must not be current thread
Target thread must not be current thread
#<ThreadGroup:X>
false
");
        }

        public void Hash_SelfKey1() {
            TestOutput(@"
h = { b: 1 }; h[h] = 2
h.each { |k, v| puts ""#{k.equal?(h)} #{v}"" }
h.each_key { |k| puts k.equal?(h) }
h.each_value { |v| puts v }
h2 = { a: 1, b: 2 }
h2.each { |k, v| h2.delete(:b) if k == :a; puts k }
p({}.respond_to?(:index))
begin; (lambda { |x, y| [x, y] } << proc { |x| [x, x] }).call(3); rescue ArgumentError => e; puts e.message; end
p((proc { |x, y| [x, y] } << proc { |x| [x, x] }).call(3))
p :upcase.to_proc.equal?(:upcase.to_proc), :upcase.to_proc.call('a')
", @"
false 1
true 2
false
true
1
2
a
false
wrong number of arguments (given 1, expected 2)
[3, 3]
true
""A""
");
        }

        public void Marshal_Misc1() {
            TestOutput(@"
o = Object.new; o.instance_variable_set(:@a, o)
x = Marshal.load(Marshal.dump(o)); p x.instance_variable_get(:@a).equal?(x)
S = Struct.new(:a)
p Marshal.dump(S).bytes
begin; Marshal.dump(Class.new.new); rescue TypeError => e; puts e.message.sub(/0x\h+/, 'X'); end
begin; Marshal.dump(Class.new(String).new('a')); rescue TypeError => e; puts e.message.sub(/0x\h+/, 'X'); end
class Foo; def marshal_dump; self; end; end
begin; Marshal.dump(Foo.new); rescue RuntimeError => e; puts e.message; end
class Bar; def respond_to?(m, p = false); m == :marshal_dump ? true : super; end; def method_missing(m, *a); m == :marshal_dump ? 1 : super; end; end
p Marshal.dump(Bar.new).bytes
p Marshal.load(""\x04\x08f\x071e""), Marshal.load(""\x04\x08f\x06a""), Marshal.load(""\x04\x08/\x06ai\x00"").options
begin; Marshal.load(""\x04\x08[\xffi\x06""); rescue ArgumentError => e; puts e.message; end
p Marshal.load(Marshal.dump(/a/n)).options, Marshal.dump(/a/n).bytes[6]
cb = Object.new; def cb.call(x); x.is_a?(String) ? x.upcase : x; end
p Marshal.load(Marshal.dump([1, 'a']), cb)
src = Object.new; def src.to_str; ""\x04\x08i\x06""; end
p Marshal.load(src)
rd = Object.new; def rd.read(n); ''; end
begin; Marshal.load(rd); rescue TypeError => e; puts e.message; end
begin; Marshal.load(""\x04\x08u:\x0bString\x06a""); rescue TypeError => e; puts e.message; end
begin; Marshal.load(""\x04\x08c\x0fComparable""); rescue ArgumentError => e; puts e.message; end
p [0.0 / 0].pack('e').bytes, [1].pack('C', buffer: nil).bytes
begin; [1].pack('C', bogus: 1); rescue ArgumentError => e; puts e.message; end
", @"
true
[4, 8, 99, 6, 83]
can't dump anonymous class #<Class:X>
can't dump anonymous class #<Class:X>
Foo#marshal_dump returned same class instance
[4, 8, 85, 58, 8, 66, 97, 114, 105, 6]
1.0
0.0
33
negative array size (or size too big)
32
32
[1, ""A""]
1
instance of IO needed
class String needs to have method '_load'
Comparable does not refer to class
[0, 0, 192, 127]
[1]
unknown keyword: :bogus
");
        }

        public void Encoding_Misc1() {
            TestOutput(@"
s = ""a\xffb"".reverse
p s.bytes, s.inspect, s.dump
p ""\x81\x40"".force_encoding(""Shift_JIS"").ord, 0x80.chr(""Windows-1252"").ord, ""\xe9"".force_encoding(""ISO-8859-1"").ord
p ""a"".casecmp(""A"".encode(""UTF-16LE"")), """".casecmp("""".encode(""UTF-16LE"")), ""a"".casecmp?(""A"")
begin; ""a\xffb"".upcase; rescue ArgumentError => e; puts e.message; end
p ""a\xffb"".upcase(:ascii).bytes
begin; Encoding.find(nil); rescue TypeError => e; puts e.message; end
p Encoding::UTF_8.frozen?, """".encode(""UTF-16"").bytes, ""a"".encode(""UTF-16"").bytes, ""\u00e9"".encode(""UTF-8"", nil)
begin; Time.now.strftime(""%Y"".encode(""UTF-16LE"")); rescue ArgumentError => e; puts e.message; end
", @"
[98, 255, 97]
""\""b\\xFFa\""""
""\""b\\xFFa\""""
33088
128
233
nil
0
true
input string invalid
[65, 255, 66]
no implicit conversion of nil into String
true
[]
[254, 255, 0, 97]
""é""
format should have ASCII compatible encoding
");
        }

        public void Io_Messages1() {
            TestOutput(@"
r, w = IO.pipe
w.write(""ab\ncd\n\nef""); w.close
p r.gets("""", nil), r.gets(nil, nil)
begin; r.seek(0, 9); rescue SystemCallError => e; puts e.class; end
begin; ""a"" * nil; rescue TypeError => e; puts e.message; end
begin; Array.new(nil); rescue TypeError => e; puts e.message; end
begin; File.open(""/nonexistent/x"", ""wb"", textmode: true); rescue ArgumentError => e; puts e.message; end
begin; File.open(""/nonexistent/x"", ""rt"", textmode: true); rescue ArgumentError => e; puts e.message; end
begin; File.open(""/nonexistent/x"", ""r"", invalid: :bogus); rescue ArgumentError => e; puts e.message; end
begin; File.open(""/nonexistent/x"", ""r"", newline: :bogus); rescue ArgumentError => e; puts e.message; end
print ""a"", nil, 1; puts
$stdout.putc(""ab""); $stdout.putc(""""); puts
begin; $stdout.putc(nil); rescue TypeError => e; puts e.message; end
", @"
""ab\ncd\n\n""
""ef""
Errno::EINVAL
no implicit conversion of nil into Integer
no implicit conversion of nil into Integer
both textmode and binmode specified
textmode specified twice
unknown value for invalid character option
unexpected value for newline option: bogus
a1
a
no implicit conversion of nil into Integer
");
        }

        public void Enumerator_Copy1() {
            TestOutput(@"
e = [1, 2].each
p e.dup.to_a, e.clone.to_a, [1, 2, 3].each_slice(2).dup.to_a
p Float.ancestors.include?(Precision), Float.ancestors[0, 3].map(&:to_s)
$VERBOSE = nil; p $-W
$VERBOSE = false; p $-W
$VERBOSE = true; p $-W
begin; $-W = 1; rescue NameError => x; puts x.message; end
", @"
[1, 2]
[1, 2]
[[1, 2], [3]]
false
[""Float"", ""Numeric"", ""Comparable""]
0
1
2
$-W is a read-only variable
");
        }

        public void Float_Format1() {
            TestOutput(@"
p 1e-20.round(25), 1e-20.round(30), 1e-20.round(19), 1.5.round(16), 0.1.round(20), 5e-324.round(324)
p((-1e-10).ceil(1), 1e-10.floor(1), (-1e-10).floor(1), 2.1.floor(1), 1.1.ceil(1), (-2.5).truncate(1))
p Float::MAX_10_EXP, Float::MIN_10_EXP
begin; '%5 5d' % 3; rescue ArgumentError => e; puts e.message; end
begin; '%.5+d' % 3; rescue ArgumentError => e; puts e.message; end
begin; '%1$1$d' % 3; rescue ArgumentError => e; puts e.message; end
begin; '%1$<a>d' % [1]; rescue ArgumentError => e; puts e.message; end
begin; '%<a>d %1$d' % { :a => 1 }; rescue ArgumentError => e; puts e.message; end
begin; '%<a>*d' % { :a => 1 }; rescue ArgumentError => e; puts e.message; end
begin; '%<a>{b}' % { :a => 1 }; rescue ArgumentError => e; puts e.message; end
begin; '%1$*d' % [5, 1]; rescue ArgumentError => e; puts e.message; end
begin; sprintf; rescue ArgumentError => e; puts e.message; end
begin; '%d' % '1'.encode('UTF-16LE'); rescue Encoding::CompatibilityError => e; puts e.message; end
begin; '%c'.force_encoding('ASCII-8BIT') % 0x3042; rescue RangeError => e; puts e.message; end
begin; '%s %s' % [""あ"", ""ÿ"".force_encoding('ASCII-8BIT')]; rescue Encoding::CompatibilityError => e; puts e.message; end
begin; Float(""½.5""); rescue ArgumentError => e; puts e.message; end
", @"
1.0e-20
1.0e-20
0.0
1.5
0.1
5.0e-324
0.0
0.0
-0.1
2.1
1.1
-2.5
308
-307
flag after width
flag after precision
value given twice - 1$
named<a> after numbered
numbered(1) after named
unnumbered(1) mixed with named
named{b} after <a>
unnumbered(1) mixed with numbered
too few arguments
ASCII incompatible encoding: UTF-16LE
12354 out of char range
incompatible character encodings: UTF-8 and ASCII-8BIT
invalid value for Float(): ""\xC2\xBD.5""
");
        }

        public void Array_Struct_Misc1() {
            TestOutput(@"
begin; [1, 'a'].sort; rescue ArgumentError => e; puts e.message; end
begin; [nil, 1].sort; rescue ArgumentError => e; puts e.message; end
begin; [1, nil].max; rescue ArgumentError => e; puts e.message; end
begin; [2**70, 0.0/0].max; rescue ArgumentError => e; puts e.message; end
o = Object.new; def o.<=>(x); 0; end
p [o, 1].sort.size
begin; [1, 2, 3].fill; rescue ArgumentError => e; puts e.message; end
begin; [1, 2, 3].fill(0, 1, 2) { |i| i }; rescue ArgumentError => e; puts e.message; end
begin; [1, 2, 3].fill(0, 1, 2, 3); rescue ArgumentError => e; puts e.message; end
p [1, 2, 3].fill(0), [1, 2, 3].fill(9, 1), [1, 2, 3].fill(9, 1, 1), [1, 2, 3].fill(9, 1..nil), [1, 2, 3].fill { |i| i * 2 }, [1, 2, 3].fill(1) { |i| i * 2 }, [1, 2, 3].fill(1, 1) { |i| i * 2 }
begin; [1, 2].freeze.fill(0); rescue => e; puts e.class; end
begin; [1, 2, 3].combination; rescue ArgumentError => e; puts e.message; end
begin; a = [1]; a[2**31 - 1] = 1; rescue IndexError => e; puts e.message; end
begin; a = [1]; a[2**31 - 2] = 1; rescue NoMemoryError => e; puts e.class; end
begin; (a = [1, 2, 3])[-5..nil] = 9; rescue RangeError => e; puts e.message; end
a = [1, 2, 3]; a.zip([1]) { |x| a.pop }; p a
begin; a = [1, 2].freeze; a.replace(a); rescue => e; puts e.class; end
k = Struct.new(:a); s = k.new(1)
begin; s.send(:initialize_copy, Struct.new(:a).new(2)); rescue TypeError => e; puts e.message; end
begin; s.send(:initialize_copy, 1); rescue TypeError => e; puts e.message; end
begin; s.freeze.send(:initialize, 2); rescue => e; puts e.class; end
p k.new(1).method(:a).arity, k.new(1).method(:a=).arity
r = k.new; r.a = r; puts r.inspect.sub(/0x\h+/, 'X')
", @"
comparison of Integer with String failed
comparison of NilClass with 1 failed
comparison of Integer with nil failed
comparison of Integer with NaN failed
2
wrong number of arguments (given 0, expected 1..3)
wrong number of arguments (given 3, expected 0..2)
wrong number of arguments (given 4, expected 1..3)
[0, 0, 0]
[1, 9, 9]
[1, 9, 3]
[1, 9, 9]
[0, 2, 4]
[1, 2, 4]
[1, 2, 3]
FrozenError
wrong number of arguments (given 0, expected 1)
index 2147483647 too big
NoMemoryError
-5.. out of range
[1]
FrozenError
initialize_copy should take same class object
initialize_copy should take same class object
FrozenError
0
1
#<struct a=#<struct #<Class:X>:...>>
");
        }

        public void Hash_Range_Misc1() {
            TestOutput(@"
h = {}.compare_by_identity; h[1.5] = 1; h[2**40] = 2; h[2**70] = 3; h[0.0/0] = 4
p h[1.5], h[2**40], h[2**70], h[0.0/0]
h = { a: 1 }
begin; h.each { h.replace({}) }; rescue RuntimeError => e; puts e.message; end
begin; h.each { h.compare_by_identity }; rescue RuntimeError => e; puts e.message; end
p((1..3).inspect.encoding, ('a'..'c').inspect.encoding, (nil..3).inspect)
r = Range.allocate; p r.send(:initialize, 1, 2), r
r = Range.allocate; r.send(:initialize_copy, 3..4); p r, r.frozen?
begin; Range.allocate.send(:initialize_copy, 1); rescue TypeError => e; puts e.message; end
p((1..3).dup.frozen?, Marshal.load(Marshal.dump(1..3)).frozen?)
rsub = Class.new(Range); Object.const_set(:RangeSubMisc1, rsub); p Marshal.load(Marshal.dump(rsub.new(1, 2))).class == rsub
p Marshal.load(""\x04\x08o:\nRange\x07:\texclF:\nbegini\x06""), Marshal.load(""\x04\x08o:\nRange\x00"")
begin; Marshal.load(""\x04\x08o:\nRange\x08:\texclF:\nbegini\x06:\x08end\""\x06a""); rescue ArgumentError => e; puts e.message; end
", @"
1
2
nil
nil
can't replace hash during iteration
compare_by_identity during iteration
#<Encoding:US-ASCII>
#<Encoding:UTF-8>
""..3""
nil
1..2
3..4
false
initialize_copy should take same class object
false
true
true
1..
nil..nil
bad value for range
");
        }

        public void Exception_Proc_Misc1() {
            TestOutput(@"
o = Object.new; def o.to_str; 'STR'; end
puts RuntimeError.new(o).message
begin; nope_epm1(); rescue NoMethodError => e; p [e.name, e.args, e.private_call?]; end
begin; nil.nope; rescue NoMethodError => e; p e.private_call?; end
begin; self.nope_epm1; rescue NoMethodError => e; p e.private_call?; end
begin; Object.new.puts; rescue NoMethodError => e; p e.private_call?; end
p NoMethodError.new('m', :n, [1], true).private_call?, NoMethodError.new('m').private_call?
begin; Marshal.dump(Object.new.singleton_class); rescue TypeError => e; puts e.message; end
begin; Marshal.load(Marshal.dump('a'), freeze: true, bogus: 1); rescue ArgumentError => e; puts e.message; end
k = Class.new(String); Object.const_set(:StrSubEpm1, k); p Marshal.load(Marshal.dump(k.new('a')), freeze: true).class == k
begin; Proc.new(1) { }; rescue ArgumentError => e; puts e.message; end
p Class.new(Proc) { def initialize(x); @x = x; end }.new(1) { }.class.superclass
1.display($stdout); puts
", @"
STR
[:nope_epm1, [], true]
false
true
false
true
false
singleton class can't be dumped
unknown keyword: :bogus
true
wrong number of arguments (given 1, expected 0)
Proc
1
");
        }

        /// <summary>
        /// A scope holds its locals in a MutableTuple, and one of more than MutableTuple.MaxSize (128) locals keeps
        /// them in nested tuples. Those were never created: the first local stored into one threw a
        /// NullReferenceException - in any scope, and in prism's pattern matching, whose temporaries are locals.
        /// </summary>
        public void ManyLocals1() {
            var code = new System.Text.StringBuilder();
            for (int i = 1; i <= 300; i++) code.Append("t" + i + " = " + i + "\n");
            code.Append("p " + SumOfLocals("t", 300) + "\n");
            code.Append("def many_locals1\n");
            for (int i = 1; i <= 300; i++) code.Append("  m" + i + " = " + i + "\n");
            // (every local is read: prism warns about one a method or a block assigns and never reads)
            code.Append("  " + SumOfLocals("m", 300) + "\nend\np many_locals1\n");
            code.Append("p [1].map { |z| ");
            for (int i = 1; i <= 300; i++) code.Append("b" + i + " = " + i + "; ");
            code.Append("z + " + SumOfLocals("b", 300) + " }\n");
            code.Append("p eval('");
            for (int i = 1; i <= 300; i++) code.Append("e" + i + " = " + i + "; ");
            code.Append(SumOfLocals("e", 300) + "')\n");

            TestOutput(code.ToString(), @"
45150
45150
[45151]
45150
");
        }

        private static string/*!*/ SumOfLocals(string/*!*/ prefix, int count) {
            var names = new string[count];
            for (int i = 0; i < count; i++) names[i] = prefix + (i + 1);
            return String.Join(" + ", names);
        }

        /// <summary>
        /// A safe navigation call (a&amp;.b) is lowered to a temporary local each, so a chain of 200 needs a scope of more
        /// than 128 locals (see ManyLocals1); the legacy parser cannot read the syntax.
        /// </summary>
        public void ManyLocals2() {
            TestOutput("p 1" + new System.Text.StringBuilder().Insert(0, "&.to_i", 200) + "\n", @"
1
");
        }

        /// <summary>
        /// prism accepts nesting up to 10000 levels deep, and a chain of calls (a.b.c...) is a tree as deep as it is
        /// long with no limit at all. prism's own parser, the bridge, IronRuby's AST transformation, the DLR's
        /// interpreter compiler and its compiled-code rewriter all recurse once or more per level, and used to take
        /// the process down with a stack overflow: each now carries on on a stack of its own (StackGuard). The code
        /// runs on a thread with the 1 MB stack of a Windows main thread, so every one of those overflowed at these
        /// depths before; lambdas and procs are made but not called, as calling them nests calls just as deep.
        /// </summary>
        public void DeepNesting1() {
            Func<string, int, string> times = (str, n) => new System.Text.StringBuilder().Insert(0, str, n).ToString();
            var code = new System.Text.StringBuilder();
            code.Append("p 1" + times(" + (1", 500) + times(")", 500) + "\n");
            code.Append("p " + times("[", 3000) + times("]", 3000) + ".flatten\n");
            code.Append("x = " + times("-> { ", 1000) + "1" + times(" }", 1000) + "\np x.lambda?\n");
            code.Append("x = " + times("proc { ", 1000) + "1" + times(" }", 1000) + "\np x.lambda?\n");
            code.Append("p 1" + times(".to_i", 5000) + "\n");
            code.Append("def deep_nesting1 = " + times("[", 1000) + times("]", 1000) + "\np deep_nesting1.flatten\n");
            code.Append("module DeepNesting1; " + times("class C; ", 1500) + "X = 1; " + times("end; ", 1500) + "end\n");
            code.Append("p DeepNesting1" + times("::C", 1500) + "::X\n");
            code.Append("p " + times("begin; ", 500) + "1" + times("; rescue; end", 500) + "\n");

            Exception error = null;
            var thread = new System.Threading.Thread(() => {
                try {
                    TestOutput(code.ToString(), @"
501
[]
true
false
1
[]
1
1
");
                } catch (Exception e) {
                    error = e;
                }
            }, 1024 * 1024);
            thread.Start();
            thread.Join();
            if (error != null) {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            }
        }
    }
}
