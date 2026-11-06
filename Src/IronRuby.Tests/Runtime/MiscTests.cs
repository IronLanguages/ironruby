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
using IronRuby.Compiler;
using IronRuby.Compiler.Ast;
using IronRuby.Builtins;
using IronRuby.Runtime;

namespace IronRuby.Tests {

    public partial class Tests {
        public void Scenario_Globals1() {
            Context.DefineGlobalVariable("x", 123);
            CompilerTest(@"$z = $x");
            object z = Context.GetGlobalVariable("z");
            AssertEquals(z, 123);
        }

        /// <summary>
        /// $KCODE, $-K and $SAFE lost their special meaning (1.9 and 3.0): they are plain globals.
        /// </summary>
        public void Scenario_Globals2() {
            AssertOutput(delegate() {
                CompilerTest(@"
$KCODE = 'u'
$-K = 1
$SAFE = 'x'
p $KCODE, $-K, $SAFE
");
            }, @"
""u""
1
""x""
");
        }

        /// <summary>
        /// $defout went in 1.9 and $-O never was an MRI global; $$ is the process id.
        /// </summary>
        public void Scenario_Globals3() {
            AssertOutput(delegate() {
                CompilerTest(@"
p global_variables.include?(:$defout), global_variables.include?(:$-O)
p $$ == Process.pid
");
            }, @"
false
false
true
");
        }

        /// <summary>
        /// Kernel#inspect of an object met again inside its own inspect: MRI's "#&lt;Foo:0x... ...&gt;".
        /// </summary>
        public void Scenario_InspectRecursive1() {
            AssertOutput(delegate() {
                CompilerTest(@"
class Foo; end
o = Foo.new
o.instance_variable_set(:@a, o)
puts o.inspect.gsub(/0x\h+/, 'X')
");
            }, @"
#<Foo:X @a=#<Foo:X ...>>
");
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
[1, 2, 3, {a: 2, b: 3}]
{a: 2, b: 3}
[1, 2, 3, {a: 2, b: 3}]
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
{1 => 2, 3 => 4}
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
{1 => 2, 3 => 4}
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
{1 => 2, 3 => 4}
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
{4 => 5}
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

        /// <summary>
        /// Process::CLOCK_* are MRI's Integers (Linux's numbers); clock_gettime takes those and
        /// MRI's emulated-clock Symbols and is Errno::EINVAL for any other clock. The CPU-time
        /// clocks are processor time, not the monotonic clock.
        /// </summary>
        public void Process_ClockGetTime1() {
            TestOutput(@"
p Process::CLOCK_REALTIME, Process::CLOCK_MONOTONIC, Process::CLOCK_PROCESS_CPUTIME_ID, Process::CLOCK_THREAD_CPUTIME_ID
def t; yield; rescue => e; ""#{e.class}: #{e.message}""; end
p t { Process.clock_gettime(:bogus) }, t { Process.clock_gettime(99) }, t { Process.clock_gettime(nil) }, t { Process.clock_gettime(0, :bogus) }
p (Process.clock_gettime(Process::CLOCK_REALTIME) - Time.now.to_f).abs < 5, Process.clock_gettime(:GETTIMEOFDAY_BASED_CLOCK_REALTIME).class
p Process.clock_gettime(Process::CLOCK_PROCESS_CPUTIME_ID) <= Process.clock_gettime(Process::CLOCK_MONOTONIC), Process.clock_gettime(1, :nanosecond).class
", @"
0
1
2
3
""Errno::EINVAL: Invalid argument - clock_gettime(:bogus)""
""Errno::EINVAL: Invalid argument - clock_gettime(99)""
""TypeError: no implicit conversion from nil to integer""
""ArgumentError: unexpected unit: bogus""
true
Float
true
Integer
");
        }

        // Nine "Integer out of Float range" warnings, as MRI gives under -w: one for each Bignum
        // that meets a Float in an operator - on either side of it - except fdiv and <=>/>/==.
        public void Numeric_BignumFloatOverflow1() {
            TestOutput(@"
x = 10**400
p x * 1.0, x + 1.0, x - 1.0, x / 10.0, x.fdiv(10**399), x.fdiv(10**401) <= 1e309, (-x) * 1.0
p x <=> 1e308, x > 1e308, x == 1.0 / 0
p 1.0 / x, -1.0 / x, 1.0 * x, -1.0 * x
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
0.0
-0.0
Infinity
-Infinity
", 1, 9);
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

        public void Backtrace_RedefinedSetBacktrace1() {
            // As MRI's setup_exception: a #set_backtrace of the exception's own is called once per raise
            // and decides #backtrace (nil unless it calls super), and an exception the method raises is
            // dropped - it used to escape the rescue, and the thread. #backtrace_locations, which the prelude
            // defines and this host does not load, is in Util/set-backtrace-matrix.rb.
            TestOutput(@"
$n = 0
class A < StandardError; def set_backtrace(b); $n += 1; super; end; end
class B < StandardError; def set_backtrace(b); $n += 1; end; end
class C < StandardError; def set_backtrace(b) = raise('sb'); end
def d2(k) = d1(k)
def d1(k) = raise(k, 'x')
[A, B, C].each do |k|
  $n = 0
  begin; d2(k); rescue Exception => e
    puts [k, e.class, $n, e.backtrace.class].join(' ')
  end
  begin; [1].each { raise k, 'blk' }; rescue Exception => e; puts [k, e.class, e.message].join(' '); end
end
t = Thread.new { Thread.current.report_on_exception = false; raise C, 't' }
begin; t.join; rescue Exception => e; puts [:thread, e.class, e.message].join(' '); end
$n = 0
begin; d2(B); rescue B => e
  begin; raise e; rescue B; puts [:reraise, $n].join(' '); end
end
", @"
A A 1 Array
A A blk
B B 1 NilClass
B B blk
C C 0 NilClass
C C blk
thread C t
reraise 2
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
class RtmLog; def respond_to_missing?(n, p); ($rtm ||= []) << [n, p]; false; end; end
Marshal.dump(RtmLog.new); p $rtm
class RtLog; def respond_to?(n, *a); ($rt ||= []) << [n, a]; super; end; end
Marshal.dump(RtLog.new); p $rt
class Bo < BasicObject; def initialize; @a = 1; end; end
p Marshal.dump(Bo.new).bytes
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
[[:marshal_dump, true], [:_dump, true]]
[[:marshal_dump, [true]], [:_dump, [true]]]
[4, 8, 111, 58, 7, 66, 111, 6, 58, 7, 64, 97, 105, 6]
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
p defined?(Precision), Float.ancestors[0, 3].map(&:to_s)
$VERBOSE = nil; p $-W
$VERBOSE = false; p $-W
$VERBOSE = true; p $-W
begin; $-W = 1; rescue NameError => x; puts x.message; end
", @"
[1, 2]
[1, 2]
[[1, 2], [3]]
nil
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
p '%#.0f' % 3, '%#.0f' % 10**20, '%#5.0f' % -3, '%#.0f' % 3.0
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
""3""
""100000000000000000000""
""   -3""
""3.""
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

        /// <summary>
        /// Marshal.load reads a Float's text with a hand-written strtod prefix scan instead of a Regex:
        /// leading white space is skipped, and a '.' is part of the number only when a digit
        /// follows, as in MRI's ruby_strtod ("1.e5" is 1.0).
        /// </summary>
        public void Marshal_LoadFloat1() {
            TestOutput(@"
require 'ruby4'
vals = [0.0, -0.0, 1.5, -2.25, 1e20, 1e-20, 1.0 / 3, Float::INFINITY, -Float::INFINITY, 2.0**70, Float::MAX, 5e-324, 0.1]
r = Marshal.load(Marshal.dump(vals))
p r.zip(vals).all? { |a, b| a.eql?(b) }, r.map { |f| f.to_s }, Marshal.load(Marshal.dump(Float::NAN)).nan?
[""1.5"", ""+.5"", ""."", ""-"", ""1.e5"", ""1e"", ""1e+"", ""1e+3x"", ""abc"", ""  1"", ""\t-2"", ""-.e3"", ""007"", ""1_000"", "".5e-2""].each do |str|
  p [str, Marshal.load(""\x04\bf"".b + (str.bytesize + 5).chr + str)]
end
", @"
true
[""0.0"", ""-0.0"", ""1.5"", ""-2.25"", ""1.0e+20"", ""1.0e-20"", ""0.3333333333333333"", ""Infinity"", ""-Infinity"", ""1.1805916207174113e+21"", ""1.7976931348623157e+308"", ""5.0e-324"", ""0.1""]
true
[""1.5"", 1.5]
[""+.5"", 0.5]
[""."", 0.0]
[""-"", 0.0]
[""1.e5"", 1.0]
[""1e"", 1.0]
[""1e+"", 1.0]
[""1e+3x"", 1000.0]
[""abc"", 0.0]
[""  1"", 1.0]
[""\t-2"", -2.0]
[""-.e3"", 0.0]
[""007"", 7.0]
[""1_000"", 1.0]
["".5e-2"", 0.005]
");
        }

        /// <summary>
        /// Integer.sqrt, #pow, #digits and #fdiv are C# (Integer.cs, ClrInteger.cs) rather than the
        /// prelude's Ruby: the same answers, with MRI's checks in MRI's order - #pow(b, 1) is 0, a
        /// non-Integer exponent is "unless a 1st argument is integer", #digits refuses a negative
        /// receiver before it converts the radix. Fixnum ** Fixnum is computed in a long while it
        /// fits, and #pow does not call a redefined **, as in MRI. Also Range#size, Integer#[],
        /// Kernel#Integer and #Float on their fast paths.
        /// </summary>
        public void Integer_SqrtPowDigits1() {
            TestOutput(@"
require 'ruby4'
def show(l); v = begin; yield; rescue => e; ""#{e.class}: #{e.message}""; end; puts ""#{l}: #{v.inspect}""; end
o = Object.new; def o.to_int = 17
[0, 1, 3, 4, 24, 25, 2**31 - 1, 2**52 - 1, 2**52, 2**62 - 1, 2**62, 10**40 - 1, 10**40, (2**64 + 1)**2 - 1, -1, -(2**70), 16.99, nil, ""4""].each { |n| show(""sqrt #{n.inspect}"") { Integer.sqrt(n) } }
show(""sqrt to_int"") { Integer.sqrt(o) }
show(""sqrt sweep"") { (0..3000).all? { |i| r = Integer.sqrt(i); r * r <= i && (r + 1)**2 > i } }
[[3, 1000, 97], [-3, 3, 7], [3, 3, -7], [-3, 3, -7], [5, 0, 1], [5, 0, -1], [2**70, 2**70, 10**9 + 7], [12, 3, 2**80], [3, -1, 7], [3, 2, 0], [3, 2, 2.0], [3, 2.0, 7], [3, nil, 7]].each { |a, b, m| show(""pow #{[a, b, m].inspect}"") { a.pow(b, m) } }
[[2, 10], [2, -1], [2, 0.5], [3, 39], [3, 41], [-2, 63], [-2, 64], [46341, 2], [0, -1]].each { |a, b| show(""pow1 #{[a, b].inspect}"") { a.pow(b) } }
[[0], [5], [123456789], [2**70], [-1], [255, 16], [255, 256], [10, 10**20], [3**50, 2**64], [1, 0], [1, 1], [1, -2], [1, 2.9], [1, nil], [-1, nil], [0, 2**64]].each { |a, *b| show(""digits #{[a, *b].inspect}"") { a.digits(*b) } }
[[10, 3], [10, 3.0], [1, 0], [-1, 0], [0, 0], [2**70, 3], [2**1100, 2**1000], [10, Rational(1, 3)], [2**53 + 1, 1]].each { |a, b| show(""fdiv #{[a, b].inspect}"") { a.fdiv(b) } }
[[1, 1000, false], [1, 1000, true], [5, 1, false], [5, 5, true], [-(2**70), 2**70, false]].each { |a, b, x| show(""size #{[a, b, x].inspect}"") { Range.new(a, b, x).size } }
show(""size float"") { (1..3.5).size }
[[12345, 3], [-1, 100], [5, -1], [2**70, 70], [5, 2**70], [-5, 2**70], [12345, 3.9], [12345, nil], [5, 0..2], [5, 1, 2]].each { |a, *b| show(""bit #{[a, *b].inspect}"") { a[*b] } }
[15, 2**70, 1.9, ""15"", nil, Float::NAN].each { |v| show(""Integer(#{v.inspect})"") { Integer(v) } }
[15, 2**70, 1.5, Float::NAN, ""1.5"", nil].each { |v| show(""Float(#{v.inspect})"") { Float(v) } }
show(""Integer base"") { Integer(15, 8) }
show(""redefined **"") { class Integer; alias __t_pow **; def **(x) = 42; end; r = 3.pow(2); class Integer; alias ** __t_pow; remove_method :__t_pow; end; [r, 3.pow(2)] }
", @"
sqrt 0: 0
sqrt 1: 1
sqrt 3: 1
sqrt 4: 2
sqrt 24: 4
sqrt 25: 5
sqrt 2147483647: 46340
sqrt 4503599627370495: 67108863
sqrt 4503599627370496: 67108864
sqrt 4611686018427387903: 2147483647
sqrt 4611686018427387904: 2147483648
sqrt 9999999999999999999999999999999999999999: 99999999999999999999
sqrt 10000000000000000000000000000000000000000: 100000000000000000000
sqrt 340282366920938463500268095579187314688: 18446744073709551616
sqrt -1: ""Math::DomainError: Numerical argument is out of domain - \""isqrt\""""
sqrt -1180591620717411303424: ""Math::DomainError: Numerical argument is out of domain - \""isqrt\""""
sqrt 16.99: 4
sqrt nil: ""TypeError: no implicit conversion of nil into Integer""
sqrt ""4"": ""TypeError: no implicit conversion of String into Integer""
sqrt to_int: 4
sqrt sweep: true
pow [3, 1000, 97]: 36
pow [-3, 3, 7]: 1
pow [3, 3, -7]: -1
pow [-3, 3, -7]: -6
pow [5, 0, 1]: 0
pow [5, 0, -1]: 0
pow [1180591620717411303424, 1180591620717411303424, 1000000007]: 295837643
pow [12, 3, 1208925819614629174706176]: 1728
pow [3, -1, 7]: ""RangeError: Integer#pow() 1st argument cannot be negative when 2nd argument specified""
pow [3, 2, 0]: ""ZeroDivisionError: divided by 0""
pow [3, 2, 2.0]: ""TypeError: Integer#pow() 2nd argument not allowed unless all arguments are integers""
pow [3, 2.0, 7]: ""TypeError: Integer#pow() 2nd argument not allowed unless a 1st argument is integer""
pow [3, nil, 7]: ""TypeError: Integer#pow() 2nd argument not allowed unless a 1st argument is integer""
pow1 [2, 10]: 1024
pow1 [2, -1]: (1/2)
pow1 [2, 0.5]: 1.4142135623730951
pow1 [3, 39]: 4052555153018976267
pow1 [3, 41]: 36472996377170786403
pow1 [-2, 63]: -9223372036854775808
pow1 [-2, 64]: 18446744073709551616
pow1 [46341, 2]: 2147488281
pow1 [0, -1]: ""ZeroDivisionError: divided by 0""
digits [0]: [0]
digits [5]: [5]
digits [123456789]: [9, 8, 7, 6, 5, 4, 3, 2, 1]
digits [1180591620717411303424]: [4, 2, 4, 3, 0, 3, 1, 1, 4, 7, 1, 7, 0, 2, 6, 1, 9, 5, 0, 8, 1, 1]
digits [-1]: ""Math::DomainError: out of domain""
digits [255, 16]: [15, 15]
digits [255, 256]: [255]
digits [10, 100000000000000000000]: [10]
digits [717897987691852588770249, 18446744073709551616]: [6048575297968530377, 38917]
digits [1, 0]: ""ArgumentError: invalid radix 0""
digits [1, 1]: ""ArgumentError: invalid radix 1""
digits [1, -2]: ""ArgumentError: negative radix""
digits [1, 2.9]: [1]
digits [1, nil]: ""TypeError: no implicit conversion of nil into Integer""
digits [-1, nil]: ""Math::DomainError: out of domain""
digits [0, 18446744073709551616]: [0]
fdiv [10, 3]: 3.3333333333333335
fdiv [10, 3.0]: 3.3333333333333335
fdiv [1, 0]: Infinity
fdiv [-1, 0]: -Infinity
fdiv [0, 0]: NaN
fdiv [1180591620717411303424, 3]: 3.935305402391371e+20
fdiv [13582985290493858492773514283592667786034938469317445497485196697278130927542418487205392083207560592298578262953847383475038725543234929971155548342800628721885763499406390331782864144164680730766837160526223176512798435772129956553355286032203080380775759732320198985094884004069116123084147875437183658467465148948790552744165376, 10715086071862673209484250490600018105614048117055336074437503883703510511249361224931983788156958581275946729175531468251871452856923140435984577574698574803934567774824230985421074605062371141877954182153046474983581941267398767559165543946077062914571196477686542167660429831652624386837205668069376]: 1.2676506002282294e+30
fdiv [10, (1/3)]: 30.0
fdiv [9007199254740993, 1]: 9.007199254740992e+15
size [1, 1000, false]: 1000
size [1, 1000, true]: 999
size [5, 1, false]: 0
size [5, 5, true]: 0
size [-1180591620717411303424, 1180591620717411303424, false]: 2361183241434822606849
size float: 3
bit [12345, 3]: 1
bit [-1, 100]: 1
bit [5, -1]: 0
bit [1180591620717411303424, 70]: 1
bit [5, 1180591620717411303424]: 0
bit [-5, 1180591620717411303424]: 1
bit [12345, 3.9]: 1
bit [12345, nil]: ""TypeError: no implicit conversion of nil into Integer""
bit [5, 0..2]: 5
bit [5, 1, 2]: 2
Integer(15): 15
Integer(1180591620717411303424): 1180591620717411303424
Integer(1.9): 1
Integer(""15""): 15
Integer(nil): ""TypeError: can't convert nil into Integer""
Integer(NaN): ""FloatDomainError: NaN""
Float(15): 15.0
Float(1180591620717411303424): 1.1805916207174113e+21
Float(1.5): 1.5
Float(NaN): NaN
Float(""1.5""): 1.5
Float(nil): ""TypeError: can't convert nil into Float""
Integer base: ""ArgumentError: base specified for non string value""
redefined **: [9, 9]
");
        }

        /// <summary>
        /// Digest::Base streams input past 64 KB into an IncrementalHash: #update no longer keeps all
        /// of it, a no-argument #digest reads the current hash without copying the object, #dup and
        /// #clone carry the state across, and chunks in different encodings can be mixed (they were
        /// an Encoding::CompatibilityError). #update(nil) is a TypeError, as in MRI.
        /// </summary>
        public void Digest_Incremental1() {
            TestOutput(@"
require 'ruby4'
require 'digest'
def show(l); v = begin; yield; rescue => e; ""#{e.class}: #{e.message}""; end; puts ""#{l}: #{v.inspect}""; end
show(""one"") { [Digest::SHA256.hexdigest(""abc""), Digest::MD5.hexdigest(""abc""), Digest::SHA1.hexdigest(""abc""), Digest::SHA384.hexdigest(""abc"")[0, 16], Digest::SHA512.hexdigest("""")[0, 16]] }
show(""stream"") { d = Digest::SHA256.new; 300.times { |i| d << ""x"" * i }; [d.hexdigest, Digest::SHA256.hexdigest((0...300).map { |i| ""x"" * i }.join)] }
show(""digest each"") { d = Digest::MD5.new; (1..5).map { |i| d.update(""ab"" * 20000); d.hexdigest == Digest::MD5.hexdigest(""ab"" * 20000 * i) } }
show(""dup"") { d = Digest::SHA256.new; d << ""a"" * 70_000; e = d.dup; e << ""b""; d << ""c""; [d.hexdigest == Digest::SHA256.hexdigest(""a"" * 70_000 + ""c""), e.hexdigest == Digest::SHA256.hexdigest(""a"" * 70_000 + ""b"")] }
show(""clone small"") { d = Digest::SHA256.new; d << ""a""; e = d.clone; e << ""b""; [d.hexdigest[0, 8], e.hexdigest[0, 8]] }
show(""digest!"") { d = Digest::SHA256.new; d << ""a"" * 70_000; x = d.hexdigest!; [x == Digest::SHA256.hexdigest(""a"" * 70_000), d.hexdigest == Digest::SHA256.hexdigest("""")] }
show(""digest(str)"") { d = Digest::SHA256.new; d << ""q"" * 70_000; [d.hexdigest(""abc"")[0, 8], d.hexdigest[0, 8]] }
show(""reset"") { d = Digest::MD5.new; d << ""x"" * 70_000; d.reset; d << ""abc""; d.hexdigest }
show(""update nil"") { Digest::MD5.new.update(nil) }
show(""update int"") { Digest::MD5.new.update(1) }
show(""mixed encodings"") { d = Digest::SHA256.new; d << ""é""; d << ""\xFF"".b; d.hexdigest[0, 8] }
show(""sha2"") { d = Digest::SHA2.new(512); d << ""x"" * 70_000; e = d.dup; e << ""y""; [d.hexdigest[0, 16], e.hexdigest[0, 16]] }
show(""finish override"") { c = Class.new(Digest::SHA256) { def finish; ""F"" + super; end }; d = c.new; d << ""a""; d.digest[0, 1] }
show(""base64"") { Digest::SHA256.base64digest(""abc"") }
", @"
one: [""ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"", ""900150983cd24fb0d6963f7d28e17f72"", ""a9993e364706816aba3e25717850c26c9cd0d89d"", ""cb00753f45a35e8b"", ""cf83e1357eefb8bd""]
stream: [""5732865d0cf87f5bd597718d7860a430edb1d0ed95a3a34880a44b932659fc4e"", ""5732865d0cf87f5bd597718d7860a430edb1d0ed95a3a34880a44b932659fc4e""]
digest each: [true, true, true, true, true]
dup: [true, true]
clone small: [""ca978112"", ""fb8e20fc""]
digest!: [true, true]
digest(str): [""ba7816bf"", ""e3b0c442""]
reset: ""900150983cd24fb0d6963f7d28e17f72""
update nil: ""TypeError: no implicit conversion of nil into String""
update int: ""TypeError: no implicit conversion of Integer into String""
mixed encodings: ""e6c36aed""
sha2: [""2e61d78ffedeca82"", ""2c11a4729a94b9e6""]
finish override: ""F""
base64: ""ungWv48Bz+pBQUDeXa4iI7ADYaOWF3qctBD/YfIAFa0=""
");
        }

        /// <summary>
        /// Digest::Instance#digest, #digest! and Digest::Class.digest hand back whatever #finish answers,
        /// as MRI does; the hexdigest forms run it through to_str, so a non-String is a TypeError rather
        /// than the InvalidCastException a cast to MutableString used to throw.
        /// </summary>
        public void Digest_NonStringResult1() {
            TestOutput(@"
require 'ruby4'
require 'digest'
def show(l); v = begin; yield; rescue => e; ""#{e.class}: #{e.message}""; end; puts ""#{l}: #{v.inspect}""; end
c = Class.new(Digest::Class) { def update(s); self; end; def finish; :sym; end; def reset; self; end }
show(""digest"") { [c.new.digest, c.new.digest(""x""), c.new.digest!, c.digest(""x"")] }
show(""hexdigest"") { c.new.hexdigest }
show(""hexdigest(str)"") { c.new.hexdigest(""x"") }
show(""hexdigest!"") { c.new.hexdigest! }
show(""Class.hexdigest"") { c.hexdigest(""x"") }
n = Class.new(c) { def finish; nil; end }
show(""nil finish"") { [n.new.digest, n.hexdigest(""x"")] }
s = Class.new(c) { def finish; o = Object.new; def o.to_str; ""\x01\xFF"".b; end; o; end }
show(""to_str finish"") { [s.new.hexdigest, s.hexdigest(""x""), s.new.hexdigest!] }
", @"
digest: [:sym, :sym, :sym, :sym]
hexdigest: ""TypeError: no implicit conversion of Symbol into String""
hexdigest(str): ""TypeError: no implicit conversion of Symbol into String""
hexdigest!: ""TypeError: no implicit conversion of Symbol into String""
Class.hexdigest: ""TypeError: no implicit conversion of Symbol into String""
nil finish: ""TypeError: no implicit conversion of nil into String""
to_str finish: [""01ff"", ""01ff"", ""01ff""]
");
        }

        /// <summary>
        /// main has no #initialize of its own - it is BasicObject's - so send(:initialize) answers nil
        /// where a stub used to throw NotImplementedException.
        /// </summary>
        public void Main_Initialize1() {
            TestOutput(@"
require 'ruby4'
def show(l); v = begin; yield; rescue => e; ""#{e.class}: #{e.message}""; end; puts ""#{l}: #{v.inspect}""; end
show(""initialize"") { send(:initialize) }
show(""initialize(1)"") { send(:initialize, 1) }
", @"
initialize: nil
initialize(1): ""ArgumentError: wrong number of arguments (given 1, expected 0)""
");
        }

        /// <summary>
        /// Dir.chdir, Dir.entries, Dir.mkdir and Dir.rmdir on a path that is, or runs through, a regular file
        /// raise Errno::ENOTDIR as MRI's do; chdir said ENOENT and the "file/sub" forms said ENOENT too.
        /// </summary>
        public void Dir_NotADirectory1() {
            TestOutput(@"
require 'ruby4'
require 'tmpdir'
def show(l); v = begin; yield; rescue SystemCallError => e; e.class; end; puts ""#{l}: #{v.inspect}""; end
dir = Dir.mktmpdir
file = File.join(dir, ""file"")
File.write(file, """")
begin
  show(""chdir file"") { Dir.chdir(file) }
  show(""chdir file/sub"") { Dir.chdir(File.join(file, ""sub"")) }
  show(""chdir missing"") { Dir.chdir(File.join(dir, ""missing"")) }
  show(""entries file"") { Dir.entries(file) }
  show(""entries file/sub"") { Dir.entries(File.join(file, ""sub"")) }
  show(""children missing"") { Dir.children(File.join(dir, ""missing"")) }
  show(""mkdir file"") { Dir.mkdir(file) }
  show(""mkdir file/sub"") { Dir.mkdir(File.join(file, ""sub"")) }
  show(""mkdir missing/sub"") { Dir.mkdir(File.join(dir, ""missing"", ""sub"")) }
  show(""rmdir file"") { Dir.rmdir(file) }
  show(""rmdir file/sub"") { Dir.rmdir(File.join(file, ""sub"")) }
  show(""rmdir missing"") { Dir.rmdir(File.join(dir, ""missing"")) }
ensure
  File.delete(file)
  Dir.rmdir(dir)
end
", @"
chdir file: Errno::ENOTDIR
chdir file/sub: Errno::ENOTDIR
chdir missing: Errno::ENOENT
entries file: Errno::ENOTDIR
entries file/sub: Errno::ENOTDIR
children missing: Errno::ENOENT
mkdir file: Errno::EEXIST
mkdir file/sub: Errno::ENOTDIR
mkdir missing/sub: Errno::ENOENT
rmdir file: Errno::ENOTDIR
rmdir file/sub: Errno::ENOTDIR
rmdir missing: Errno::ENOENT
");
        }

        /// <summary>
        /// Dir_NotADirectory1 through symbolic links: a dangling link is a missing directory (ENOENT), not
        /// a file, and Dir.rmdir of a link - even one to a directory - is ENOTDIR and leaves the link alone,
        /// where it used to delete it. POSIX only: making a symbolic link on Windows needs a privilege.
        /// </summary>
        public void Dir_NotADirectorySymlinks1() {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT) {
                return;
            }
            TestOutput(@"
require 'ruby4'
require 'tmpdir'
def show(l); v = begin; yield; :ok; rescue SystemCallError => e; e.class; end; puts ""#{l}: #{v.inspect}""; end
dir = Dir.mktmpdir
broken = File.join(dir, ""broken""); File.symlink(File.join(dir, ""nothing""), broken)
file = File.join(dir, ""file""); File.write(file, """"); tofile = File.join(dir, ""tofile""); File.symlink(file, tofile)
sub = File.join(dir, ""sub""); Dir.mkdir(sub); todir = File.join(dir, ""todir""); File.symlink(sub, todir)
begin
  show(""chdir broken"") { Dir.chdir(broken) { } }
  show(""chdir broken/x"") { Dir.chdir(File.join(broken, ""x"")) { } }
  show(""chdir tofile"") { Dir.chdir(tofile) { } }
  show(""chdir todir"") { Dir.chdir(todir) { } }
  show(""entries broken"") { Dir.entries(broken) }
  show(""entries tofile"") { Dir.entries(tofile) }
  show(""entries todir"") { Dir.entries(todir) }
  show(""mkdir broken/x"") { Dir.mkdir(File.join(broken, ""x"")) }
  show(""mkdir tofile/x"") { Dir.mkdir(File.join(tofile, ""x"")) }
  show(""mkdir todir/x"") { Dir.mkdir(File.join(todir, ""x"")); Dir.rmdir(File.join(sub, ""x"")) }
  show(""rmdir broken"") { Dir.rmdir(broken) }
  show(""rmdir broken/x"") { Dir.rmdir(File.join(broken, ""x"")) }
  show(""rmdir tofile"") { Dir.rmdir(tofile) }
  show(""rmdir todir"") { Dir.rmdir(todir) }
  show(""link still there"") { File.lstat(todir).symlink? or raise Errno::ENOENT }
ensure
  [broken, tofile, todir, file].each { |x| File.unlink(x) rescue nil }
  Dir.rmdir(sub)
  Dir.rmdir(dir)
end
", @"
chdir broken: Errno::ENOENT
chdir broken/x: Errno::ENOENT
chdir tofile: Errno::ENOTDIR
chdir todir: :ok
entries broken: Errno::ENOENT
entries tofile: Errno::ENOTDIR
entries todir: :ok
mkdir broken/x: Errno::ENOENT
mkdir tofile/x: Errno::ENOTDIR
mkdir todir/x: :ok
rmdir broken: Errno::ENOTDIR
rmdir broken/x: Errno::ENOENT
rmdir tofile: Errno::ENOTDIR
rmdir todir: Errno::ENOTDIR
link still there: :ok
");
        }

        /// <summary>
        /// The File and FileTest predicates MRI answers with rb_stat (exist?, file?, size, zero?, the times,
        /// identical?, ...) take an IO, or anything whose #to_io gives one, and fstat it; they raised TypeError
        /// for a #to_io object. The access(2) ones (readable?, writable?, executable?) and symlink? still take
        /// only a path, as in MRI. A #to_io that gives nil means "use the path"; one that gives anything else
        /// is a TypeError.
        /// </summary>
        public void File_StatPredicatesToIo1() {
            TestOutput(@"
require 'ruby4'
require 'tmpdir'
dir = Dir.mktmpdir
f = File.join(dir, ""file"")
File.write(f, ""abc"")
io = File.open(f)
ti = Object.new; ti.define_singleton_method(:to_io) { io }
%i[blockdev? chardev? directory? exist? file? pipe? size size? socket? zero? empty?].each do |m|
  puts ""#{m}: #{[io, ti].map { |a| File.send(m, a) rescue $!.class }.inspect}""
end
puts ""FileTest.exist?: #{FileTest.exist?(ti).inspect}""
puts ""times: #{%i[atime mtime ctime].map { |m| File.send(m, ti).class }.inspect}""
puts ""identical?: #{[File.identical?(ti, f), File.identical?(io, ti)].inspect}""
puts ""path only: #{%i[readable? writable? executable? symlink?].map { |m| File.send(m, ti) rescue $!.class }.inspect}""
x = Object.new; def x.to_io; 5; end
puts ""to_io gives Integer: #{(File.exist?(x) rescue ""#{$!.class}: #{$!.message}"")}""
x = Object.new; def x.to_io; nil; end; def x.to_path; ""/nonexistent""; end
puts ""to_io gives nil: #{File.exist?(x).inspect}""
io.close
puts ""closed: #{(File.exist?(io) rescue ""#{$!.class}: #{$!.message}"")}""
File.delete(f)
Dir.rmdir(dir)
", @"
blockdev?: [false, false]
chardev?: [false, false]
directory?: [false, false]
exist?: [true, true]
file?: [true, true]
pipe?: [false, false]
size: [3, 3]
size?: [3, 3]
socket?: [false, false]
zero?: [false, false]
empty?: [false, false]
FileTest.exist?: true
times: [Time, Time, Time]
identical?: [true, true]
path only: [TypeError, TypeError, TypeError, TypeError]
to_io gives Integer: TypeError: can't convert Object to IO (Object#to_io gives Integer)
to_io gives nil: false
closed: IOError: closed stream
");
        }

        /// <summary>
        /// File_StatPredicatesToIo1 for the streams only fstat can answer for: a pipe, and a file that was
        /// deleted while open. Windows has no fstat, and there IronRuby answers from the path a File was opened
        /// with - a pipe has none, and an open file cannot be deleted - so this one is POSIX only.
        /// </summary>
        public void File_StatPredicatesToIo2() {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT) {
                return;
            }
            TestOutput(@"
require 'ruby4'
require 'tmpdir'
dir = Dir.mktmpdir
f = File.join(dir, ""file"")
File.write(f, ""abc"")
r, w = IO.pipe
tr = Object.new; tr.define_singleton_method(:to_io) { r }
%i[exist? file? pipe? size size? zero?].each do |m|
  puts ""#{m}: #{[r, tr].map { |a| File.send(m, a) rescue $!.class }.inspect}""
end
puts ""identical?: #{[File.identical?(r, tr), File.identical?(r, f)].inspect}""
g = File.open(f); File.delete(f)
puts ""deleted but open: #{[File.exist?(g), File.file?(g), File.size(g)].inspect}""
g.close; r.close; w.close
Dir.rmdir(dir)
", @"
exist?: [true, true]
file?: [false, false]
pipe?: [true, true]
size: [0, 0]
size?: [nil, nil]
zero?: [true, true]
identical?: [true, false]
deleted but open: [true, true, 3]
");
        }

        /// <summary>
        /// IO.sysopen takes its mode as open(2) flags when it has #to_int (File::RDONLY used to be a TypeError,
        /// "no implicit conversion of Integer into String") and as a mode string otherwise, and its permission
        /// by #to_int: a String or a Hash there is a TypeError, where it used to be ignored.
        /// </summary>
        public void IO_SysOpenArguments1() {
            TestOutput(@"
require 'ruby4'
require 'tmpdir'
def show(l); v = begin; yield; rescue => e; ""#{e.class}: #{e.message.sub(/ @ \w+/, '').sub(/ - .*/, '')}""; end; puts ""#{l}: #{v.inspect}""; end
dir = Dir.mktmpdir
f = File.join(dir, ""file"")
File.write(f, ""abc"")
tp = Object.new; tp.define_singleton_method(:to_path) { f }
rd = ->(fd) { io = IO.for_fd(fd); s = io.read; io.close; s }
show(""string mode"") { rd.(IO.sysopen(f, ""r"")) }
show(""no mode"") { rd.(IO.sysopen(tp)) }
show(""nil mode, nil perm"") { rd.(IO.sysopen(f, nil, nil)) }
show(""integer mode"") { rd.(IO.sysopen(f, File::RDONLY)) }
show(""to_int mode"") { o = Object.new; def o.to_int; File::RDONLY; end; rd.(IO.sysopen(f, o)) }
show(""to_str mode"") { o = Object.new; def o.to_str; ""r""; end; rd.(IO.sysopen(f, o)) }
show(""to_int perm"") { o = Object.new; def o.to_int; 0644; end; rd.(IO.sysopen(f, ""r"", o)) }
show(""symbol mode"") { IO.sysopen(f, :r) }
show(""string perm"") { IO.sysopen(f, ""r"", ""x"") }
show(""hash perm"") { IO.sysopen(f, ""r"", {}) }
show(""bad mode"") { IO.sysopen(f, ""zz"") }
File.delete(f)
Dir.rmdir(dir)
", @"
string mode: ""abc""
no mode: ""abc""
nil mode, nil perm: ""abc""
integer mode: ""abc""
to_int mode: ""abc""
to_str mode: ""abc""
to_int perm: ""abc""
symbol mode: ""TypeError: no implicit conversion of Symbol into String""
string perm: ""TypeError: no implicit conversion of String into Integer""
hash perm: ""TypeError: no implicit conversion of Hash into Integer""
bad mode: ""ArgumentError: invalid access mode zz""
");
        }

        /// <summary>
        /// IO.sysopen's permission reaches a file the call creates, less the umask, as open(2)'s does; it
        /// used to be dropped, so the file came out 0644. POSIX only: off it the permission is not applied.
        /// </summary>
        public void IO_SysOpenPermission1() {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT) {
                return;
            }
            TestOutput(@"
require 'ruby4'
require 'tmpdir'
def show(l); v = begin; yield; rescue => e; ""#{e.class}: #{e.message.sub(/ @ \w+/, '').sub(/ - .*/, '')}""; end; puts ""#{l}: #{v.inspect}""; end
dir = Dir.mktmpdir
f = File.join(dir, ""file"")
File.write(f, ""abc"")
tp = Object.new; tp.define_singleton_method(:to_path) { f }
File.umask(022)
g = File.join(dir, ""created"")
show(""perm on create"") { IO.for_fd(IO.sysopen(g, ""w"", 0600)).close; File.stat(g).mode & 0777 }
h = File.join(dir, ""flags"")
show(""flags create"") { IO.for_fd(IO.sysopen(h, File::WRONLY | File::CREAT | File::EXCL, 0640)).close; File.stat(h).mode & 0777 }
show(""flags excl again"") { IO.sysopen(h, File::WRONLY | File::CREAT | File::EXCL) }
show(""perm on existing"") { IO.for_fd(IO.sysopen(g, ""w"", 0666)).close; File.stat(g).mode & 0777 }
show(""missing dir"") { IO.sysopen(File.join(dir, ""missing"", ""x""), ""w"") }
File.delete(f)
[g, h].each { |x| File.delete(x) }
Dir.rmdir(dir)
", @"
perm on create: 384
flags create: 416
flags excl again: ""Errno::EEXIST: File exists""
perm on existing: 384
missing dir: ""Errno::ENOENT: No such file or directory""
");
        }

        /// <summary>
        /// A String pattern whose bytes are not valid in its encoding, as MRI treats it: #sub, #gsub, their
        /// bang forms and #scan raise Regexp.new's RegexpError (string.c's get_pat_quoted), where IronRuby said
        /// ArgumentError or, with a valid receiver, matched nothing; the searches - #index, #rindex, #byteindex,
        /// #byterindex, #[], #slice, #slice!, #[]=, #partition, #rpartition - find nothing (rb_strseq_index),
        /// where IronRuby found the bytes.
        /// </summary>
        public void String_BrokenPattern1() {
            TestOutput(@"
require 'ruby4'
def show(l); v = begin; yield; rescue Exception => e; ""#{e.class}: #{e.message}""; end; puts ""#{l}: #{v.inspect}""; end
s = ""a\xffb\xffc""; pat = ""\xff""
show(""Regexp.new"") { Regexp.new(pat) }
show(""sub"") { s.sub(pat, ""Z"") }
show(""sub!"") { s.dup.sub!(pat, ""Z"") }
show(""sub blk"") { s.sub(pat) { ""Z"" } }
show(""gsub"") { s.gsub(pat, ""Z"") }
show(""gsub!"") { s.dup.gsub!(pat) { ""Z"" } }
show(""gsub enum"") { s.gsub(pat).to_a }
show(""gsub enum is lazy"") { [""abc"".gsub(pat).class, ""abc"".dup.gsub!(pat).class] }
show(""gsub hash"") { s.gsub(pat, {}) }
show(""scan"") { s.scan(pat) }
show(""scan blk"") { s.scan(pat) { } }
show(""split"") { s.split(pat) }
show(""index"") { s.index(pat) }
show(""include?"") { s.include?(pat) }
show(""start_with?"") { s.start_with?(pat) }
show(""partition"") { s.partition(pat) }
show(""count"") { s.count(pat) }
show(""binary ok"") { ""a\xffb"".b.gsub(""\xff"".b, ""Z"") }
show(""other enc broken"") { ""a\xffb"".force_encoding(""EUC-JP"").gsub(""\xff"".force_encoding(""EUC-JP""), ""Z"") }
show(""to_str broken"") { o = Object.new; def o.to_str; ""\xff""; end; ""a"".gsub(o, ""Z"") }
show(""valid receiver"") { ""abc"".gsub(""\xff"", ""Z"") }
show(""mid"") { ""x"".sub(""a\xe3b"", ""Z"") }
show(""index"") { s.index(pat) }
show(""index off"") { s.index(pat, 2) }
show(""rindex"") { s.rindex(pat) }
show(""byteindex"") { s.byteindex(pat) }
show(""byterindex"") { s.byterindex(pat) }
show(""include?"") { s.include?(pat) }
show(""[]"") { s[pat] }
show(""slice"") { s.slice(pat) }
show(""slice!"") { t = s.dup; [t.slice!(pat), t] }
show(""[]="") { t = s.dup; t[pat] = ""Z""; t }
show(""partition"") { s.partition(pat) }
show(""rpartition"") { s.rpartition(pat) }
show(""valid in broken"") { s.index(""b"") }
show(""broken mid"") { ""ab\xe3"".index(""\xe3"") }
", @"
Regexp.new: ""RegexpError: invalid multibyte character: /\\xFF/""
sub: ""RegexpError: invalid multibyte character: /\\xFF/""
sub!: ""RegexpError: invalid multibyte character: /\\xFF/""
sub blk: ""RegexpError: invalid multibyte character: /\\xFF/""
gsub: ""RegexpError: invalid multibyte character: /\\xFF/""
gsub!: ""RegexpError: invalid multibyte character: /\\xFF/""
gsub enum: ""RegexpError: invalid multibyte character: /\\xFF/""
gsub enum is lazy: [Enumerator, Enumerator]
gsub hash: ""RegexpError: invalid multibyte character: /\\xFF/""
scan: ""RegexpError: invalid multibyte character: /\\xFF/""
scan blk: ""RegexpError: invalid multibyte character: /\\xFF/""
split: ""ArgumentError: invalid byte sequence in UTF-8""
index: nil
include?: false
start_with?: false
partition: [""a\xFFb\xFFc"", """", """"]
count: ""ArgumentError: invalid byte sequence in UTF-8""
binary ok: ""aZb""
other enc broken: ""RegexpError: invalid multibyte character: /\\xFF/""
to_str broken: ""RegexpError: invalid multibyte character: /\\xFF/""
valid receiver: ""RegexpError: invalid multibyte character: /\\xFF/""
mid: ""RegexpError: invalid multibyte character: /a\\xE3b/""
index: nil
index off: nil
rindex: nil
byteindex: nil
byterindex: nil
include?: false
[]: nil
slice: nil
slice!: [nil, ""a\xFFb\xFFc""]
[]=: ""IndexError: string not matched""
partition: [""a\xFFb\xFFc"", """", """"]
rpartition: ["""", """", ""a\xFFb\xFFc""]
valid in broken: 2
broken mid: nil
");
        }

        /// <summary>
        /// A host that creates the runtime on one thread and runs Ruby on another: the creating
        /// thread is Thread.main and is listed even though no Ruby code ever ran on it.
        /// </summary>
        public void Thread_HostMainThread1() {
            Microsoft.Scripting.Hosting.ScriptEngine engine = null;
            var created = new System.Threading.ManualResetEvent(false);
            var done = new System.Threading.ManualResetEvent(false);
            var creator = new System.Threading.Thread(() => {
                engine = Ruby.CreateEngine();
                created.Set();
                done.WaitOne();
            });
            creator.IsBackground = true;
            creator.Start();
            try {
                Assert(created.WaitOne(30000));
                var result = engine.Execute<object>(@"
[Thread.list.include?(Thread.main), Thread.list.size, Thread.current != Thread.main]
");
                Assert(((RubyArray)result)[0].Equals(true));
                Assert(((RubyArray)result)[1].Equals(2));
                Assert(((RubyArray)result)[2].Equals(true));
            } finally {
                done.Set();
            }
        }

        /// <summary>
        /// A runtime created on a thread another runtime's Thread.new made: that thread is the new
        /// runtime's Thread.main and is listed there, whoever made it.
        /// </summary>
        public void Thread_HostMainThread2() {
            Microsoft.Scripting.Hosting.ScriptEngine engine = null;
            var created = new System.Threading.ManualResetEvent(false);
            var done = new System.Threading.ManualResetEvent(false);
            Action create = () => {
                engine = Ruby.CreateEngine();
                created.Set();
                done.WaitOne();
            };
            var scope = Engine.CreateScope();
            scope.SetVariable("create", create);
            Engine.Execute("$host_main_thread2 = Thread.new { create.invoke }", scope);
            try {
                Assert(created.WaitOne(30000));
                var result = (RubyArray)engine.Execute<object>("[Thread.list.include?(Thread.main), Thread.list.size]");
                Assert(result[0].Equals(true));
                Assert(result[1].Equals(2));
            } finally {
                done.Set();
                Engine.Execute("$host_main_thread2.join; $host_main_thread2 = nil");
            }
        }

        /// <summary>
        /// Thread.stop with no other thread alive is MRI's ThreadError "stopping only thread" (rb_thread_stop),
        /// where it slept forever. A fiber runs on a CLR thread of its own here but is part of the thread that
        /// owns it, so neither it nor its thread counts as another thread - for Thread.stop, nor for Thread.list
        /// and ThreadGroup#list, which used to list it.
        /// </summary>
        public void Thread_StopAlone1() {
            TestOutput(@"
require 'ruby4'
def show(l); v = begin; yield; rescue => e; ""#{e.class}: #{e.message.inspect}""; end; puts ""#{l}: #{v.inspect}""; end
show(""alone"") { Thread.stop }
show(""list"") { Thread.list.size }
show(""in fiber: list"") { Fiber.new { Thread.list.size }.resume }
show(""in fiber: group list"") { Fiber.new { ThreadGroup::Default.list.size }.resume }
show(""in fiber: stop"") { Fiber.new { Thread.stop }.resume }
f = Fiber.new { Fiber.yield }; f.resume
show(""suspended fiber: list"") { [Thread.list.size, Thread.list == [Thread.main]] }
# bounded, so that a Thread.stop that raised where it should sleep fails the test instead of hanging it
def wait_for(seconds = 10); deadline = Time.now + seconds; Thread.pass until yield || Time.now > deadline; end
show(""another thread stops"") { t = Thread.new { Thread.stop; :woke }; begin; wait_for { !t.alive? || t.status == ""sleep"" }; t.run if t.alive?; t.value; ensure; t.kill; end }
show(""main stops, another wakes it"") { t = Thread.new { wait_for { Thread.main.status == ""sleep"" }; Thread.main.wakeup }; begin; Thread.stop; t.join; :ok; ensure; t.kill; end }
show(""fiber of another thread"") { Thread.new { Fiber.new { Thread.list.size }.resume }.value }
f.resume
", @"
alone: ""ThreadError: \""stopping only thread\\n\\tnote: use sleep to stop forever\""""
list: 1
in fiber: list: 1
in fiber: group list: 1
in fiber: stop: ""ThreadError: \""stopping only thread\\n\\tnote: use sleep to stop forever\""""
suspended fiber: list: [1, true]
another thread stops: :woke
main stops, another wakes it: :ok
fiber of another thread: 2
");
        }

        /// <summary>
        /// What MRI does on Windows with the functions win32/Makefile.sub leaves out, and with the ones
        /// win32.c supplies. Dir.fchdir, Dir.for_fd, Dir#fileno, Dir.chroot, File.lutime, File.mkfifo and the
        /// process-group and set-id parts of Process are rb_f_notimplement stubs there: NotImplementedError,
        /// and #respond_to? false. Dir's threw CLR NotImplementedException or InvalidOperationException, and
        /// all of them answered #respond_to? with true. File.link and File.readlink work there,
        /// through CreateHardLinkW and the reparse point; they raised NotImplementedError. File.symlink's
        /// cases are Windows_SymLinks1, which needs the right to make symbolic links. Windows only. The
        /// expected output is MRI's as its win32 sources give it, not a recorded run.
        /// </summary>
        public void Windows_NotImplementedAndLinks1() {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) {
                return;
            }
            TestOutput(@"
require 'ruby4'
require 'tmpdir'
require 'fileutils'
def show(l); v = begin; yield; rescue Exception => e; e.is_a?(SystemCallError) ? e.class : ""#{e.class}: #{e.message}""; end; puts ""#{l}: #{v.inspect}""; end
show(""stubs respond_to?"") { [Dir.respond_to?(:fchdir), Dir.respond_to?(:for_fd), Dir.respond_to?(:chroot), Dir.open(""."") { |x| x.respond_to?(:fileno) }, File.respond_to?(:lutime), File.respond_to?(:mkfifo), Process.respond_to?(:getpgid), Process.respond_to?(:setsid), Process.respond_to?(:groups), Process.respond_to?(:uid=)] }
show(""links respond_to?"") { [File.respond_to?(:link), File.respond_to?(:symlink), File.respond_to?(:readlink)] }
show(""Dir.fchdir"") { Dir.fchdir(0) }
show(""Dir.for_fd"") { Dir.for_fd(0) }
show(""Dir#fileno"") { Dir.open(""."") { |x| x.fileno } }
show(""Dir.chroot"") { Dir.chroot(""."") }
show(""File.lutime"") { File.lutime(nil, nil, ""x"") }
show(""File.mkfifo"") { File.mkfifo(""x"") }
show(""Process.getpgid"") { Process.getpgid(0) }
show(""Process.uid="") { Process.uid = 0 }
dir = Dir.mktmpdir
begin
  Dir.chdir(dir) do
    File.write(""f"", ""x"")
    Dir.mkdir(""sub"")
    show(""link"") { File.link(""f"", ""h""); File.read(""h"") }
    show(""link existing"") { File.link(""f"", ""h"") }
    show(""link missing"") { File.link(""nope"", ""h2"") }
    show(""readlink not a link"") { File.readlink(""f"") }
    show(""readlink missing"") { File.readlink(""nope"") }
  end
ensure
  FileUtils.rm_rf(dir) rescue nil
end
", @"
stubs respond_to?: [false, false, false, false, false, false, false, false, false, false]
links respond_to?: [true, true, true]
Dir.fchdir: ""NotImplementedError: fchdir() function is unimplemented on this machine""
Dir.for_fd: ""NotImplementedError: for_fd() function is unimplemented on this machine""
Dir#fileno: ""NotImplementedError: fileno() function is unimplemented on this machine""
Dir.chroot: ""NotImplementedError: chroot() function is unimplemented on this machine""
File.lutime: ""NotImplementedError: lutime() function is unimplemented on this machine""
File.mkfifo: ""NotImplementedError: mkfifo() function is unimplemented on this machine""
Process.getpgid: ""NotImplementedError: getpgid() function is unimplemented on this machine""
Process.uid=: ""NotImplementedError: uid=() function is unimplemented on this machine""
link: ""x""
link existing: Errno::EEXIST
link missing: Errno::ENOENT
readlink not a link: Errno::EINVAL
readlink missing: Errno::ENOENT
");
        }

        /// <summary>
        /// File.symlink and the File::Stat of a link on Windows, as win32.c has them: CreateSymbolicLinkW
        /// with a directory link for a directory target (looked up from the link's directory, as ruby/ruby
        /// master does), readlink of the reparse point, an lstat that describes the link itself (S_IFLNK and
        /// S_IEXEC, neither file? nor directory?), and a stat that follows it - ENOENT for a dangling one.
        /// Windows only, and only where this process may make symbolic links (an administrator, or Developer
        /// Mode): elsewhere File.symlink is EACCES, as in MRI, and there is nothing here to check. The
        /// expected output is MRI's as its win32 sources give it; the same script prints the same under
        /// CRuby and IronRuby on Linux.
        /// </summary>
        public void Windows_SymLinks1() {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT || !CanCreateSymbolicLinks()) {
                return;
            }
            TestOutput(@"
require 'ruby4'
require 'tmpdir'
require 'fileutils'
def show(l); v = begin; yield; rescue Exception => e; e.is_a?(SystemCallError) ? e.class : ""#{e.class}: #{e.message}""; end; puts ""#{l}: #{v.inspect}""; end
dir = Dir.mktmpdir
begin
  Dir.chdir(dir) do
    File.write(""f"", ""x"")
    Dir.mkdir(""sub"")
    show(""symlink to file"") { File.symlink(""f"", ""s""); [File.symlink?(""s""), File.readlink(""s""), File.read(""s"")] }
    show(""symlink to dir"") { File.symlink(""sub"", ""sd""); [File.symlink?(""sd""), File.directory?(""sd""), File.readlink(""sd"")] }
    show(""symlink dangling"") { File.symlink(""sub/x"", ""sn""); [File.symlink?(""sn""), File.readlink(""sn"")] }
    show(""relative symlink in a subdirectory"") { Dir.mkdir(""d2""); Dir.mkdir(""d2/tgt""); File.symlink(""tgt"", ""d2/ln""); [File.symlink?(""d2/ln""), File.directory?(""d2/ln""), File.readlink(""d2/ln"")] }
    show(""lstat and stat"") { [File.lstat(""s"").symlink?, File.stat(""s"").symlink?, File.ftype(""s""), File.lstat(""sd"").symlink?, File.ftype(""sn"")] }
    show(""lstat of a link"") { st = File.lstat(""s""); [st.file?, st.directory?, st.symlink?, st.mode & 0170000 == 0120000, st.mode & 0100 != 0] }
    show(""stat follows the link"") { [File.stat(""s"").file?, File.size(""s""), File.stat(""sd"").directory?] }
    show(""stat of a dangling link"") { File.stat(""sn"") }
    show(""symlink existing"") { File.symlink(""f"", ""s"") }
  end
ensure
  FileUtils.rm_rf(dir) rescue nil
end
", @"
symlink to file: [true, ""f"", ""x""]
symlink to dir: [true, true, ""sub""]
symlink dangling: [true, ""sub/x""]
relative symlink in a subdirectory: [true, true, ""tgt""]
lstat and stat: [true, false, ""link"", true, ""link""]
lstat of a link: [false, false, true, true, true]
stat follows the link: [true, 1, true]
stat of a dangling link: Errno::ENOENT
symlink existing: Errno::EEXIST
");
        }

        private static bool CanCreateSymbolicLinks() {
            string link = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ir-symlink-probe-" + Guid.NewGuid().ToString("N"));
            try {
                System.IO.File.CreateSymbolicLink(link, "target");
                System.IO.File.Delete(link);
                return true;
            } catch (Exception) {
                return false;
            }
        }

        /// <summary>
        /// Kernel#Integer and #Float with exception: false answer nil from the C# parser instead of
        /// raising and rescuing; a String in an ASCII-incompatible encoding is refused either way,
        /// and Float names a NUL. Array#inject over Fixnums, Array#minmax as [min, max], and
        /// Enumerable#zip with an Integer Range worked out from the index.
        /// </summary>
        public void Kernel_ConversionsInjectZip1() {
            TestOutput(@"
require 'ruby4'
def t(label)
  r = begin; yield; rescue => e; [e.class, e.message]; end
  puts ""#{label}: #{r.inspect}""
end
['123', ' 0x1A ', '0b101', '1_000', '1__0', '', '  ', 'x', '12x', ""1\0"", '-0o17', '0d19', '08', '+5', 'ff'].each do |s|
  t(""Integer #{s.inspect}"") { [Integer(s, exception: false), Integer(s, 16, exception: false), Float(s, exception: false)] }
end
['1.5', '1e3', '.5', '1_0.5', 'Infinity', 'NaN', '0x1p3', ' 2.5 ', '1e', ""1\0""].each do |s|
  t(""Float #{s.inspect}"") { Float(s, exception: false) }
  t(""Float! #{s.inspect}"") { Float(s) }
end
t('radix 99') { Integer('1', 99, exception: false) }
u = '12'.encode('UTF-16LE')
t('utf16') { [(Integer(u) rescue $!.class), (Integer(u, exception: false) rescue $!.class), (Float(u, exception: false) rescue $!.class)] }
t('nil') { Integer(nil, exception: false) }
[1.5, -2.9, 1e20, Float::NAN, Float::INFINITY].each { |f| t(""Integer(#{f})"") { [(Integer(f) rescue $!.class), Integer(f, exception: false)] } }
a = [1, 2, 3, 2**62, 2**62]
t('inject +') { [a.inject(:+), a.inject(10, :+), [].inject(:+), [].inject(5, :+), [1, 2.5, 3].inject(:+), ([1, 'a'].inject(:+) rescue $!.class)] }
t('inject sym') { [[2, 3, 4].inject(:*), [1, 2].inject('+'), %w[a b c].inject(:+), (1..4).inject(:+), [3].inject(:+)] }
t('inject block') { [[1, 2, 3].inject { |s, x| s * 10 + x }, [1, 2, 3].inject(100) { |s, x| s - x }, [1, 2, 3].inject { |s, x| break s * 7 if x == 3; s + x }] }
t('inject subclass') { Class.new(Array) { def each; yield 9; end }.new([1, 2]).inject(:+) }
t('minmax') { [[3, 1, 2].minmax, [].minmax, %w[b a].minmax, [1.5, 1, 2**70].minmax, [3, 1, 2].minmax { |x, y| y <=> x }, ([1, nil].minmax rescue $!.class)] }
t('zip') { [(1..5).zip(1..3), (1..5).zip(2...4), (1..4).zip(10..), (1..3).zip(5..1), (1..3).zip(2**64..2**64 + 1), (1..3).zip('a'..'b')] }
t('zip block') { r = []; [(1..3).zip(4..6) { |x| r << x }, r] }
t('zip subclass') { (1..2).zip(Class.new(Range) { def each; yield 42; end }.new(1, 5)) }
t('zip singleton') { r = (1..3).dup; def r.each; yield 42; end; (1..2).zip(r) }
t('zip readers') { [(r = (1..3).dup; def r.begin; 10; end; (0..1).zip(r)), (r = (1..3).dup; def r.end; 1; end; (0..2).zip(r)), (r = (1..3).dup; def r.exclude_end?; true; end; (0..3).zip(r))] }
", @"
Integer ""123"": [123, 291, 123.0]
Integer "" 0x1A "": [26, 26, 26.0]
Integer ""0b101"": [5, 45313, nil]
Integer ""1_000"": [1000, 4096, 1000.0]
Integer ""1__0"": [nil, nil, nil]
Integer """": [nil, nil, nil]
Integer ""  "": [nil, nil, nil]
Integer ""x"": [nil, nil, nil]
Integer ""12x"": [nil, nil, nil]
Integer ""1\u0000"": [nil, nil, nil]
Integer ""-0o17"": [-15, nil, nil]
Integer ""0d19"": [19, 3353, nil]
Integer ""08"": [nil, 8, 8.0]
Integer ""+5"": [5, 5, 5.0]
Integer ""ff"": [nil, 255, nil]
Float ""1.5"": 1.5
Float! ""1.5"": 1.5
Float ""1e3"": 1000.0
Float! ""1e3"": 1000.0
Float "".5"": 0.5
Float! "".5"": 0.5
Float ""1_0.5"": 10.5
Float! ""1_0.5"": 10.5
Float ""Infinity"": nil
Float! ""Infinity"": [ArgumentError, ""invalid value for Float(): \""Infinity\""""]
Float ""NaN"": nil
Float! ""NaN"": [ArgumentError, ""invalid value for Float(): \""NaN\""""]
Float ""0x1p3"": 8.0
Float! ""0x1p3"": 8.0
Float "" 2.5 "": 2.5
Float! "" 2.5 "": 2.5
Float ""1e"": nil
Float! ""1e"": [ArgumentError, ""invalid value for Float(): \""1e\""""]
Float ""1\u0000"": nil
Float! ""1\u0000"": [ArgumentError, ""string for Float contains null byte""]
radix 99: [ArgumentError, ""invalid radix 99""]
utf16: [Encoding::CompatibilityError, Encoding::CompatibilityError, Encoding::CompatibilityError]
nil: nil
Integer(1.5): [1, 1]
Integer(-2.9): [-2, -2]
Integer(1.0e+20): [100000000000000000000, 100000000000000000000]
Integer(NaN): [FloatDomainError, nil]
Integer(Infinity): [FloatDomainError, nil]
inject +: [9223372036854775814, 9223372036854775824, nil, 5, 6.5, TypeError]
inject sym: [24, 3, ""abc"", 10, 3]
inject block: [123, 94, 21]
inject subclass: 9
minmax: [[1, 3], [nil, nil], [""a"", ""b""], [1, 1180591620717411303424], [3, 1], ArgumentError]
zip: [[[1, 1], [2, 2], [3, 3], [4, nil], [5, nil]], [[1, 2], [2, 3], [3, nil], [4, nil], [5, nil]], [[1, 10], [2, 11], [3, 12], [4, 13]], [[1, nil], [2, nil], [3, nil]], [[1, 18446744073709551616], [2, 18446744073709551617], [3, nil]], [[1, ""a""], [2, ""b""], [3, nil]]]
zip block: [nil, [[1, 4], [2, 5], [3, 6]]]
zip subclass: [[1, 42], [2, nil]]
zip singleton: [[1, 42], [2, nil]]
zip readers: [[[0, 1], [1, 2]], [[0, 1], [1, 2], [2, 3]], [[0, 1], [1, 2], [2, 3], [3, nil]]]
");
        }

        /// <summary>
        /// StringIO's line readers after the bare gets got an overload of its own, a one-byte
        /// separator is looked for as a byte, and chomp: cuts the line where it is; lineno counts
        /// the lines read and not the nil at the end. Also the byte side of a fresh literal, which
        /// learns it is ASCII rather than being switched to bytes to find out.
        /// </summary>
        public void StringIO_LineReaders1() {
            TestOutput(@"
require 'ruby4'
require 'stringio'
def t(label)
  r = begin; yield; rescue => e; [e.class, e.message]; end
  puts ""#{label}: #{r.inspect}""
end
src = ""one\r\ntwo\n\n\nthréeéx\nlast""
[[], [""\n""], [""e""], [""é""], [""éx""], [""""], [nil], [3], [""o"", 2], [""\r\n""]].each do |a|
  t(""gets #{a.inspect}"") { s = StringIO.new(src); r = []; while l = s.gets(*a); r << l; end; [r, $_, s.lineno] }
  t(""gets chomp #{a.inspect}"") { s = StringIO.new(src); r = []; while l = s.gets(*a, chomp: true); r << l; end; r }
  t(""readlines chomp #{a.inspect}"") { StringIO.new(src).readlines(*a, chomp: true) }
  t(""each_line chomp #{a.inspect}"") { StringIO.new(src).each_line(*a, chomp: true).to_a }
end
t(""binary sep"") { StringIO.new(""a\xFFb\xFFc"".b).readlines(""\xFF"".b, chomp: true) }
t(""gets bare"") { s = StringIO.new(""x\ny""); [s.gets, s.gets, s.gets, $_] }
t(""frozen line chomp"") { s = StringIO.new(""ab\n"".freeze); l = s.gets(chomp: true); [l, l.frozen?, s.string] }
t(""chomp keeps source"") { str = +""a\nb\n""; s = StringIO.new(str); s.readlines(chomp: true); str }
# literal byte access
[""hello"", ""héllo"", """", ""\u{1F600}x""].each do |lit|
  t(""bytes #{lit.inspect}"") { x = lit.dup; [x.bytesize, x.getbyte(0), x.getbyte(1), x.bytes, x.size, x.ascii_only?, x.b, x.byteslice(1, 2)] }
end
t(""iso literal"") { x = ""caf\xE9"".force_encoding(""iso-8859-1""); [x.bytesize, x.getbyte(3), x.bytes] }
t(""utf16"") { x = ""ab"".encode(""UTF-16LE""); [x.bytesize, x.getbyte(0), x.getbyte(1), x.bytes] }
t(""io write literal"") { s = StringIO.new(+""""); s.write(""abc""); s.write(""dé""); [s.string, s.string.bytesize, s.pos] }
t(""string << then bytes"") { x = +""ab""; x << ""c""; [x.bytesize, x.getbyte(2), x.bytes] }
", @"
gets []: [[""one\r\n"", ""two\n"", ""\n"", ""\n"", ""thréeéx\n"", ""last""], nil, 6]
gets chomp []: [""one"", ""two"", """", """", ""thréeéx"", ""last""]
readlines chomp []: [""one"", ""two"", """", """", ""thréeéx"", ""last""]
each_line chomp []: [""one"", ""two"", """", """", ""thréeéx"", ""last""]
gets [""\n""]: [[""one\r\n"", ""two\n"", ""\n"", ""\n"", ""thréeéx\n"", ""last""], nil, 6]
gets chomp [""\n""]: [""one"", ""two"", """", """", ""thréeéx"", ""last""]
readlines chomp [""\n""]: [""one"", ""two"", """", """", ""thréeéx"", ""last""]
each_line chomp [""\n""]: [""one"", ""two"", """", """", ""thréeéx"", ""last""]
gets [""e""]: [[""one"", ""\r\ntwo\n\n\nthrée"", ""éx\nlast""], nil, 3]
gets chomp [""e""]: [""on"", ""\r\ntwo\n\n\nthré"", ""éx\nlast""]
readlines chomp [""e""]: [""on"", ""\r\ntwo\n\n\nthré"", ""éx\nlast""]
each_line chomp [""e""]: [""on"", ""\r\ntwo\n\n\nthré"", ""éx\nlast""]
gets [""é""]: [[""one\r\ntwo\n\n\nthré"", ""eé"", ""x\nlast""], nil, 3]
gets chomp [""é""]: [""one\r\ntwo\n\n\nthr"", ""e"", ""x\nlast""]
readlines chomp [""é""]: [""one\r\ntwo\n\n\nthr"", ""e"", ""x\nlast""]
each_line chomp [""é""]: [""one\r\ntwo\n\n\nthr"", ""e"", ""x\nlast""]
gets [""éx""]: [[""one\r\ntwo\n\n\nthréeéx"", ""\nlast""], nil, 2]
gets chomp [""éx""]: [""one\r\ntwo\n\n\nthrée"", ""\nlast""]
readlines chomp [""éx""]: [""one\r\ntwo\n\n\nthrée"", ""\nlast""]
each_line chomp [""éx""]: [""one\r\ntwo\n\n\nthrée"", ""\nlast""]
gets [""""]: [[""one\r\ntwo\n\n\n"", ""thréeéx\nlast""], nil, 2]
gets chomp [""""]: [""one\r\ntwo"", ""thréeéx\nlast""]
readlines chomp [""""]: [""one\r\ntwo"", ""thréeéx\nlast""]
each_line chomp [""""]: [""one\r\ntwo"", ""thréeéx\nlast""]
gets [nil]: [[""one\r\ntwo\n\n\nthréeéx\nlast""], nil, 1]
gets chomp [nil]: [""one\r\ntwo\n\n\nthréeéx\nlast""]
readlines chomp [nil]: [""one\r\ntwo\n\n\nthréeéx\nlast""]
each_line chomp [nil]: [""one\r\ntwo\n\n\nthréeéx\nlast""]
gets [3]: [[""one"", ""\r\n"", ""two"", ""\n"", ""\n"", ""\n"", ""thr"", ""ée"", ""éx"", ""\n"", ""las"", ""t""], nil, 12]
gets chomp [3]: [""one"", """", ""two"", """", """", """", ""thr"", ""ée"", ""éx"", """", ""las"", ""t""]
readlines chomp [3]: [""one"", """", ""two"", """", """", """", ""thr"", ""ée"", ""éx"", """", ""las"", ""t""]
each_line chomp [3]: [""one"", """", ""two"", """", """", """", ""thr"", ""ée"", ""éx"", """", ""las"", ""t""]
gets [""o"", 2]: [[""o"", ""ne"", ""\r\n"", ""tw"", ""o"", ""\n\n"", ""\nt"", ""hr"", ""é"", ""eé"", ""x\n"", ""la"", ""st""], nil, 13]
gets chomp [""o"", 2]: ["""", ""ne"", ""\r\n"", ""tw"", """", ""\n\n"", ""\nt"", ""hr"", ""é"", ""eé"", ""x\n"", ""la"", ""st""]
readlines chomp [""o"", 2]: ["""", ""ne"", ""\r\n"", ""tw"", """", ""\n\n"", ""\nt"", ""hr"", ""é"", ""eé"", ""x\n"", ""la"", ""st""]
each_line chomp [""o"", 2]: ["""", ""ne"", ""\r\n"", ""tw"", """", ""\n\n"", ""\nt"", ""hr"", ""é"", ""eé"", ""x\n"", ""la"", ""st""]
gets [""\r\n""]: [[""one\r\n"", ""two\n\n\nthréeéx\nlast""], nil, 2]
gets chomp [""\r\n""]: [""one"", ""two\n\n\nthréeéx\nlast""]
readlines chomp [""\r\n""]: [""one"", ""two\n\n\nthréeéx\nlast""]
each_line chomp [""\r\n""]: [""one"", ""two\n\n\nthréeéx\nlast""]
binary sep: [""a"", ""b"", ""c""]
gets bare: [""x\n"", ""y"", nil, nil]
frozen line chomp: [""ab"", false, ""ab\n""]
chomp keeps source: ""a\nb\n""
bytes ""hello"": [5, 104, 101, [104, 101, 108, 108, 111], 5, true, ""hello"", ""el""]
bytes ""héllo"": [6, 104, 195, [104, 195, 169, 108, 108, 111], 5, false, ""h\xC3\xA9llo"", ""é""]
bytes """": [0, nil, nil, [], 0, true, """", nil]
bytes ""😀x"": [5, 240, 159, [240, 159, 152, 128, 120], 2, false, ""\xF0\x9F\x98\x80x"", ""\x9F\x98""]
iso literal: [4, 233, [99, 97, 102, 233]]
utf16: [4, 97, 0, [97, 0, 98, 0]]
io write literal: [""abcdé"", 6, 6]
string << then bytes: [3, 99, [97, 98, 99]]
");
        }

        /// <summary>
        /// Text mode's universal newline decorator - "\r\n" and a lone "\r" are folded before the
        /// line is split - and the newline options that are decorators for the way out only
        /// (crlf, cr, and newline: :lf). POSIX only: Windows' own text mode reads "\r\n" as "\n"
        /// whatever the options say, as MRI's does there.
        /// </summary>
        public void Io_LineReaders3() {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT) {
                return;
            }
            TestOutput(@"
require 'ruby4'
require 'tmpdir'
dir = Dir.mktmpdir
def t(label)
  r = begin; yield; rescue => e; [e.class, e.message]; end
  puts ""#{label}: #{r.inspect}""
end
u = File.join(dir, 'u.txt')
File.binwrite(u, ""a\r\nb\rc\r\n\r\nd\r"")
[['rt', {}], ['r', {universal_newline: true}], ['r', {newline: :universal}], ['r', {newline: :lf}], ['rt', {newline: :crlf}], ['rb', {}]].each do |m, kw|
  t(""#{m} #{kw.keys.inspect} #{kw.values.inspect}"") do
    File.open(u, m, **kw) do |io|
      lines = io.readlines
      io.rewind
      chars = []
      while c = io.getc; chars << c; end
      io.rewind
      [io.read, lines, chars, File.open(u, m, **kw) { |x| x.readlines('', chomp: true) }, File.open(u, m, **kw) { |x| x.readlines(2) }]
    end
  end
end
w = File.join(dir, 'w.txt')
[{newline: :lf}, {lf_newline: true}, {newline: :crlf}, {newline: :cr}, {universal_newline: true}].each do |kw|
  File.open(w, 'w', **kw) { |io| io.write(""a\r\nb\rc\n"") }
  t(""write #{kw.keys.inspect}"") { File.binread(w) }
end
", @"
rt [] []: [""a\nb\nc\n\nd\n"", [""a\n"", ""b\n"", ""c\n"", ""\n"", ""d\n""], [""a"", ""\n"", ""b"", ""\n"", ""c"", ""\n"", ""\n"", ""d"", ""\n""], [""a\nb\nc"", ""d\n""], [""a\n"", ""b\n"", ""c\n"", ""\n"", ""d\n""]]
r [:universal_newline] [true]: [""a\nb\nc\n\nd\n"", [""a\n"", ""b\n"", ""c\n"", ""\n"", ""d\n""], [""a"", ""\n"", ""b"", ""\n"", ""c"", ""\n"", ""\n"", ""d"", ""\n""], [""a\nb\nc"", ""d\n""], [""a\n"", ""b\n"", ""c\n"", ""\n"", ""d\n""]]
r [:newline] [:universal]: [""a\nb\nc\n\nd\n"", [""a\n"", ""b\n"", ""c\n"", ""\n"", ""d\n""], [""a"", ""\n"", ""b"", ""\n"", ""c"", ""\n"", ""\n"", ""d"", ""\n""], [""a\nb\nc"", ""d\n""], [""a\n"", ""b\n"", ""c\n"", ""\n"", ""d\n""]]
r [:newline] [:lf]: [""a\r\nb\rc\r\n\r\nd\r"", [""a\r\n"", ""b\rc\r\n"", ""\r\n"", ""d\r""], [""a"", ""\r"", ""\n"", ""b"", ""\r"", ""c"", ""\r"", ""\n"", ""\r"", ""\n"", ""d"", ""\r""], [""a\r\nb\rc\r\n\r\nd\r""], [""a\r"", ""\n"", ""b\r"", ""c\r"", ""\n"", ""\r\n"", ""d\r""]]
rt [:newline] [:crlf]: [""a\r\nb\rc\r\n\r\nd\r"", [""a\r\n"", ""b\rc\r\n"", ""\r\n"", ""d\r""], [""a"", ""\r"", ""\n"", ""b"", ""\r"", ""c"", ""\r"", ""\n"", ""\r"", ""\n"", ""d"", ""\r""], [""a\r\nb\rc\r\n\r\nd\r""], [""a\r"", ""\n"", ""b\r"", ""c\r"", ""\n"", ""\r\n"", ""d\r""]]
rb [] []: [""a\r\nb\rc\r\n\r\nd\r"", [""a\r\n"", ""b\rc\r\n"", ""\r\n"", ""d\r""], [""a"", ""\r"", ""\n"", ""b"", ""\r"", ""c"", ""\r"", ""\n"", ""\r"", ""\n"", ""d"", ""\r""], [""a\r\nb\rc\r\n\r\nd\r""], [""a\r"", ""\n"", ""b\r"", ""c\r"", ""\n"", ""\r\n"", ""d\r""]]
write [:newline]: ""a\nb\nc\n""
write [:lf_newline]: ""a\nb\nc\n""
write [:newline]: ""a\r\r\nb\rc\r\n""
write [:newline]: ""a\r\rb\rc\r""
write [:universal_newline]: ""a\r\nb\rc\n""
");
        }

        /// <summary>
        /// The line readers' arguments as MRI's rb_io_getline_args reads them, lineno as
        /// rb_io_getline_0 counts it (a read cut short by the limit is not a line), the open
        /// options IO.foreach and IO.readlines pass on, and binmode: true. Files with a "\r" in
        /// them are read "rb": Windows' text mode reads "\r\n" as "\n", as MRI's does there.
        /// </summary>
        public void Io_LineReaders2() {
            TestOutput(@"
require 'ruby4'
require 'tmpdir'
dir = Dir.mktmpdir
f = File.join(dir, 'a.txt')
File.binwrite(f, ""one\r\ntwo\n\n\nthree\nlast"")
o = Object.new; def o.to_str; 'o'; end
i = Object.new; def i.to_int; 2; end
def t(label)
  r = begin; yield; rescue => e; [e.class, e.message]; end
  puts ""#{label}: #{r.inspect}""
end
[[[o], {}], [[o], {chomp: true}], [[i], {}], [[i], {chomp: true}], [[nil, i], {}], [[o, i], {chomp: true}],
 [[1, 2, 3], {}], [['x', 2, 3], {chomp: true}], [[nil, nil], {chomp: true}], [[1], {chomp: true, foo: 1}], [[0], {}]].each_with_index do |(a, kw), n|
  t(""gets #{n}"") { File.open(f, 'rb') { |io| [io.gets(*a, **kw), io.gets(*a, **kw), $_, io.lineno] } }
  t(""readline #{n}"") { File.open(f, 'rb') { |io| io.readline(*a, **kw) } }
end
t('gets positional hash') { File.open(f, 'rb') { |io| io.gets({chomp: true}) } }
[[3], ['e', 2], ['', 5], [""\n"", 4], [nil, 4]].each do |a|
  t(""lineno #{a.inspect}"") { File.open(f, 'rb') { |io| x = []; while l = io.gets(*a); x << io.lineno; end; x } }
  t(""each_line lineno #{a.inspect}"") { File.open(f, 'rb') { |io| io.each_line(*a).map { io.lineno } } }
  t(""readlines lineno #{a.inspect}"") { File.open(f, 'rb') { |io| io.readlines(*a); [io.lineno, $.] } }
end
g = File.join(dir, 'l1.txt')
File.binwrite(g, ""caf\xE9\n"".b)
t('foreach encoding') { IO.foreach(g, encoding: 'iso-8859-1:utf-8').map { |l| [l, l.encoding.to_s] } }
t('readlines encoding') { IO.readlines(g, encoding: 'iso-8859-1:utf-8', chomp: true) }
t('readlines external') { IO.readlines(g, external_encoding: 'iso-8859-1').map { |l| l.encoding.to_s } }
t('readlines binmode') { IO.readlines(g, binmode: true).map { |l| l.encoding.to_s } }
t('File.open binmode') { File.open(g, binmode: true) { |io| io.gets.encoding.to_s } }
", @"
gets 0: [""o"", ""ne\r\ntwo"", ""ne\r\ntwo"", 2]
readline 0: ""o""
gets 1: ["""", ""ne\r\ntw"", ""ne\r\ntw"", 2]
readline 1: """"
gets 2: [""on"", ""e\r"", ""e\r"", 0]
readline 2: ""on""
gets 3: [""on"", ""e\r"", ""e\r"", 0]
readline 3: ""on""
gets 4: [""on"", ""e\r"", ""e\r"", 0]
readline 4: ""on""
gets 5: ["""", ""ne"", ""ne"", 1]
readline 5: """"
gets 6: [ArgumentError, ""wrong number of arguments (given 3, expected 0..2)""]
readline 6: [ArgumentError, ""wrong number of arguments (given 3, expected 0..2)""]
gets 7: [ArgumentError, ""wrong number of arguments (given 3, expected 0..2)""]
readline 7: [ArgumentError, ""wrong number of arguments (given 3, expected 0..2)""]
gets 8: [""one\r\ntwo\n\n\nthree\nlast"", nil, nil, 1]
readline 8: ""one\r\ntwo\n\n\nthree\nlast""
gets 9: [""o"", ""n"", ""n"", 0]
readline 9: [ArgumentError, ""unknown keyword: :foo""]
gets 10: ["""", """", """", 0]
readline 10: """"
gets positional hash: [TypeError, ""no implicit conversion of Hash into Integer""]
lineno [3]: [0, 1, 1, 2, 3, 4, 4, 5, 5, 6]
each_line lineno [3]: [0, 1, 1, 2, 3, 4, 4, 5, 5, 6]
readlines lineno [3]: [6, 6]
lineno [""e"", 2]: [0, 1, 1, 1, 1, 1, 1, 2, 3, 3, 3, 4]
each_line lineno [""e"", 2]: [0, 1, 1, 1, 1, 1, 1, 2, 3, 3, 3, 4]
readlines lineno [""e"", 2]: [4, 4]
lineno ["""", 5]: [0, 1, 1, 2]
each_line lineno ["""", 5]: [0, 1, 1, 2]
readlines lineno ["""", 5]: [2, 2]
lineno [""\n"", 4]: [0, 1, 2, 3, 4, 4, 5, 5]
each_line lineno [""\n"", 4]: [0, 1, 2, 3, 4, 4, 5, 5]
readlines lineno [""\n"", 4]: [5, 5]
lineno [nil, 4]: [0, 0, 0, 0, 0, 1]
each_line lineno [nil, 4]: [0, 0, 0, 0, 0, 1]
readlines lineno [nil, 4]: [1, 1]
foreach encoding: [[""café\n"", ""UTF-8""]]
readlines encoding: [""café""]
readlines external: [""ISO-8859-1""]
readlines binmode: [""ASCII-8BIT""]
File.open binmode: ""ASCII-8BIT""
");
        }

        /// <summary>
        /// IO's line and character readers chomp, convert and measure characters in the built-ins
        /// (IoOps.cs) rather than a line or a character at a time in ruby4.rb, and IO#write
        /// decides there whether a String needs converting.
        /// </summary>
        public void Io_BuiltinReaders1() {
            TestOutput(@"
require 'ruby4'
require 'tmpdir'
dir = Dir.mktmpdir
f = File.join(dir, 'a.txt')
File.binwrite(f, ""one\r\ntwo\n\n\nthree\nlast"")
o = Object.new; def o.to_str; 'o'; end
[[], [""\n""], [''], [nil], [3], ['o', 2], [o]].each do |a|
  p [a.map { |x| x.equal?(o) ? :o : x }, File.open(f, 'rb') { |io| io.readlines(*a, chomp: true) }, File.open(f, 'rb') { |io| io.each_line(*a, chomp: true).to_a }]
end
p File.open(f, 'rb') { |io| x = []; io.each_line { |l| x << l }; [x, io.lineno] }
p File.open(f, 'rb') { |io| io.each_line { |l| break l.upcase } }
p IO.foreach(f, mode: 'rb', chomp: true).to_a
p((File.open(f, 'rb') { |io| io.readlines(1, 2, 3) } rescue $!.class))
g = File.join(dir, 'u.txt')
File.binwrite(g, ""a\xC3\xA9\xE2\x82\xAC\xE9b\xF0\x9F"".b)
p File.open(g, 'r:utf-8') { |io| x = []; while c = io.getc; x << c; end; x }
p File.open(g, 'r:utf-8') { |io| io.each_char.to_a.size }
p File.open(g, 'rb') { |io| [io.getc, io.readchar, io.getbyte, io.readbyte] }
p File.open(g, 'r:iso-8859-1:utf-8') { |io| [io.getc, io.getc, io.getc.encoding.to_s] }
p File.open(g, 'r:utf-8') { |io| io.ungetc('xy'); io.ungetc(0x41); io.ungetc(0x20AC); [io.getc, io.getc, io.getc, io.getc, io.getc] }
p((File.open(g) { |io| io.read; io.readchar } rescue $!.class))
w = File.join(dir, 'w.txt')
p File.open(w, 'wb') { |io| io.write('a', 1, :b) }
File.open(w, 'wb:iso-8859-1') { |io| io.write(""café""); io.puts('x'); io.print(""ü"") }
p File.binread(w)
", @"
[[], [""one"", ""two"", """", """", ""three"", ""last""], [""one"", ""two"", """", """", ""three"", ""last""]]
[[""\n""], [""one"", ""two"", """", """", ""three"", ""last""], [""one"", ""two"", """", """", ""three"", ""last""]]
[[""""], [""one\r\ntwo"", ""three\nlast""], [""one\r\ntwo"", ""three\nlast""]]
[[nil], [""one\r\ntwo\n\n\nthree\nlast""], [""one\r\ntwo\n\n\nthree\nlast""]]
[[3], [""one"", """", ""two"", """", """", """", ""thr"", ""ee"", ""las"", ""t""], [""one"", """", ""two"", """", """", """", ""thr"", ""ee"", ""las"", ""t""]]
[[""o"", 2], ["""", ""ne"", ""\r\n"", ""tw"", """", ""\n\n"", ""\nt"", ""hr"", ""ee"", ""\nl"", ""as"", ""t""], ["""", ""ne"", ""\r\n"", ""tw"", """", ""\n\n"", ""\nt"", ""hr"", ""ee"", ""\nl"", ""as"", ""t""]]
[[:o], ["""", ""ne\r\ntw"", ""\n\n\nthree\nlast""], ["""", ""ne\r\ntw"", ""\n\n\nthree\nlast""]]
[[""one\r\n"", ""two\n"", ""\n"", ""\n"", ""three\n"", ""last""], 6]
""ONE\r\n""
[""one"", ""two"", """", """", ""three"", ""last""]
ArgumentError
[""a"", ""é"", ""€"", ""\xE9"", ""b"", ""\xF0\x9F""]
6
[""a"", ""\xC3"", 169, 226]
[""a"", ""Ã"", ""UTF-8""]
[""€"", ""A"", ""x"", ""y"", ""a""]
EOFError
3
""caf\xE9x\n\xFC""
");
        }

        /// <summary>
        /// Range#first(n) and #last(n) of an Integer range are counted rather than walked - #last(n)
        /// built the whole range first, so (1..10**9).last(3) ran out of memory - and #last takes
        /// that path for a subclass whatever its #each, as MRI's rb_int_range_last does. Also the
        /// Integer fast paths of Range#include?, Range#step and Integer#step.
        /// </summary>
        public void Range_IntegerFastPaths1() {
            TestOutput(@"
require 'ruby4'
def show(l); v = begin; yield; rescue => e; ""#{e.class}: #{e.message}""; end; puts ""#{l}: #{v.inspect}""; end
[[1..10, 3], [1..10, 0], [1..10, 20], [1...10, 3], [5..1, 2], [1..1, 5], [1.., 3], [-3..-1, 2], [2**64..2**64 + 5, 2], [1..10, -1], [1.0..3.0, 2], [""a""..""e"", 2], [1..3.5, 2], [(..5), 2]].each do |r, n|
  show(""first #{r.inspect} #{n.inspect}"") { r.first(n) }
  show(""last #{r.inspect} #{n.inspect}"") { r.last(n) }
end
c = Class.new(Range) { def each; yield :x; end }
show(""sub first"") { c.new(1, 10).first(2) }
show(""sub last"") { c.new(1, 10).last(2) }
show(""big last"") { (1..10**9).last(3) }
[[1..10, 5], [1..10, 10], [1...10, 10], [1..10, 0], [1..10, 5.5], [1..10, 2**70], [5..1, 3], [1.., 10**20], [(..5), 3], [""a""..""z"", ""cc""], [1..10, ""a""]].each { |r, v| show(""include #{r.inspect} #{v.inspect}"") { [r.include?(v), r.member?(v), r === v] } }
show(""step"") { a = []; 1.step(10, 3) { |i| a << i }; a }
show(""step neg"") { a = []; 10.step(1, -4) { |i| a << i }; a }
show(""step 1"") { a = []; 1.step(3) { |i| a << i }; a }
show(""step none"") { a = []; 5.step(1) { |i| a << i }; a }
show(""step 0"") { 1.step(3, 0) { } }
show(""step float"") { a = []; 1.step(2, 0.5) { |i| a << i }; a }
show(""step flimit"") { a = []; 1.step(2.5) { |i| a << i }; a }
show(""step kw"") { a = []; 1.step(by: 2, to: 6) { |i| a << i }; a }
show(""step ret"") { x = 1.step(3) { }; x }
show(""step break"") { 1.step(10) { |i| break i * 100 if i == 3 } }
show(""step enum"") { 1.step(10, 3).to_a }
show(""step big"") { a = []; (2**64).step(2**64 + 4, 2) { |i| a << i }; a }
show(""rstep"") { a = []; (1..10).step(3) { |i| a << i }; a }
show(""rstep excl"") { a = []; (1...10).step(3) { |i| a << i }; a }
show(""rstep endless"") { a = []; (1..).step(5) { |i| a << i; break if a.size == 3 }; a }
show(""rstep ret"") { r = (1..3); r.step(1) { }.equal?(r) }
show(""rstep float"") { a = []; (1.0..2.0).step(0.5) { |i| a << i }; a }
", @"
first 1..10 3: [1, 2, 3]
last 1..10 3: [8, 9, 10]
first 1..10 0: []
last 1..10 0: []
first 1..10 20: [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]
last 1..10 20: [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]
first 1...10 3: [1, 2, 3]
last 1...10 3: [7, 8, 9]
first 5..1 2: []
last 5..1 2: []
first 1..1 5: [1]
last 1..1 5: [1]
first 1.. 3: [1, 2, 3]
last 1.. 3: ""RangeError: cannot get the last element of endless range""
first -3..-1 2: [-3, -2]
last -3..-1 2: [-2, -1]
first 18446744073709551616..18446744073709551621 2: [18446744073709551616, 18446744073709551617]
last 18446744073709551616..18446744073709551621 2: [18446744073709551620, 18446744073709551621]
first 1..10 -1: ""ArgumentError: negative array size (or size too big)""
last 1..10 -1: ""ArgumentError: negative array size""
first 1.0..3.0 2: ""TypeError: can't iterate from Float""
last 1.0..3.0 2: ""TypeError: can't iterate from Float""
first ""a""..""e"" 2: [""a"", ""b""]
last ""a""..""e"" 2: [""d"", ""e""]
first 1..3.5 2: [1, 2]
last 1..3.5 2: [2, 3]
first ..5 2: ""RangeError: cannot get the first element of beginless range""
last ..5 2: ""TypeError: can't iterate from NilClass""
sub first: [:x]
sub last: [9, 10]
big last: [999999998, 999999999, 1000000000]
include 1..10 5: [true, true, true]
include 1..10 10: [true, true, true]
include 1...10 10: [false, false, false]
include 1..10 0: [false, false, false]
include 1..10 5.5: [true, true, true]
include 1..10 1180591620717411303424: [false, false, false]
include 5..1 3: [false, false, false]
include 1.. 100000000000000000000: [true, true, true]
include ..5 3: [true, true, true]
include ""a""..""z"" ""cc"": [false, false, true]
include 1..10 ""a"": [false, false, false]
step: [1, 4, 7, 10]
step neg: [10, 6, 2]
step 1: [1, 2, 3]
step none: []
step 0: ""ArgumentError: step can't be 0""
step float: [1.0, 1.5, 2.0]
step flimit: [1.0, 2.0]
step kw: [1, 3, 5]
step ret: 1
step break: 300
step enum: [1, 4, 7, 10]
step big: [18446744073709551616, 18446744073709551618, 18446744073709551620]
rstep: [1, 4, 7, 10]
rstep excl: [1, 4, 7]
rstep endless: [1, 6, 11]
rstep ret: true
rstep float: [1.0, 1.5, 2.0]
");
        }

        /// <summary>
        /// A method whose body only yields and returns over self, its parameters, locals and literals runs without a
        /// RubyMethodScope (MethodDefinition.TransformLightFrame): the literal block is the call's flow-control frame,
        /// and a scope is made only for a TracePoint hook or an exception filter. Everything a block can do to the
        /// call - break, next, return, redo, raise - and the TracePoint :call/:return events and callee_id still
        /// behave as with a scope; so does a call through an alias, and a lambda, proc, Method or Symbol passed as the block.
        /// </summary>
        public void Method_LightFrame1() {
            TestOutput(@"
require 'ruby4'
def show(l); v = begin; yield; rescue Exception => e; ""#{e.class}: #{e.message}""; end; puts ""#{l}: #{v.inspect}""; end
class Foo
  def y0 = yield
  def y1(x) = yield(x)
  def y2(a, b) = yield(a, b)
  def tap1 = (yield self; self)
  def twice(x); a = yield(x); b = yield(a); b; end
  def ret_early(x); return x if x; yield; end
  def with_blk(x, &b) = yield(x)
  def nested(x) = y1(x) { |v| yield(v * 2) }   # not light (calls y1) - for comparison
end
f = Foo.new
show(""y0"") { f.y0 { 42 } }
show(""y1"") { f.y1(3) { |x| x + 1 } }
show(""y2"") { f.y2(1, 2) { |a, b| a + b } }
show(""y2 one param"") { f.y2(1, 2) { |a| a } }
show(""y1 array splat"") { f.y1([1, 2]) { |a, b| [a, b] } }
show(""tap1"") { f.tap1 { |s| s.class }.class }
show(""twice"") { f.twice(1) { |v| v * 10 } }
show(""ret_early"") { [f.ret_early(5) { :no }, f.ret_early(nil) { :yes }] }
show(""break"") { f.y1(1) { |x| break x * 100 } }
show(""break twice"") { f.twice(1) { |v| break :out if v > 5; v * 10 } }
show(""next"") { f.y1(1) { |x| next x * 7 if x; 0 } }
show(""return from block"") { def m(f) = f.y1(1) { |x| return x + 1000 }; m(f) }
show(""return twice"") { def m2(f) = (f.twice(1) { |v| return :r if v > 5; v * 10 }; :not); m2(f) }
show(""no block"") { f.y1(1) }
show(""lambda"") { f.y1(5, &->(x) { x * 3 }) }
show(""lambda arity"") { f.y2(1, 2, &->(x) { x }) }
show(""lambda break"") { f.y1(5, &->(x) { break x + 1 }) }
show(""proc"") { pr = proc { |x| x - 1 }; f.y1(5, &pr) }
show(""proc break"") { pr = proc { |x| break 9 }; f.y1(5, &pr) }
show(""method obj"") { f.y1(4, &5.method(:+)) }
show(""symbol"") { f.y1(""ab"", &:upcase) }
show(""with_blk"") { f.with_blk(2) { |x| x * x } }
show(""raise in block"") { f.y1(1) { raise ArgumentError, ""boom"" } }
show(""rescue around"") { begin; f.y1(1) { raise ""x"" }; rescue => e; e.backtrace.grep(/y1/).size; end }
show(""nested"") { f.nested(3) { |v| v + 1 } }
show(""recursion"") { def rec(f, n) = n == 0 ? 0 : f.y1(n) { |x| x + rec(f, n - 1) }; rec(f, 50) }
show(""redo"") { c = 0; f.y1(1) { |x| c += 1; redo if c < 3; c } }
show(""break from loop in block"") { f.y1(1) { |x| [1, 2].each { |i| break i }; :after } }
show(""yield in proc result"") { f.y1(1) { |x| proc { x } }.call }
show(""binding in block"") { a = 5; f.y1(a) { |x| binding.local_variable_get(:a) + x } }
show(""method_missing interplay"") { f.respond_to?(:y1) }
show(""frozen self"") { f.freeze; f.y1(1) { |x| x } }
class Foo; alias yy1 y1; end
ids = []
tp2 = TracePoint.new(:call) { |t| ids << [t.method_id, t.callee_id] if t.defined_class == Foo }
tp2.enable { f.yy1(1) { } ; f.y1(1) { } }
show(""callee_id"") { ids }
show(""enable inside block"") { ev = []; t = TracePoint.new(:return) { |x| ev << x.method_id if x.defined_class == Foo }; f.y1(1) { t.enable }; t.disable; ev }
tp_events = []
tp = TracePoint.new(:call, :return) { |t| tp_events << [t.event, t.method_id, (t.event == :return ? (t.return_value.is_a?(Foo) ? Foo : t.return_value) : nil)] if t.defined_class == Foo }
tp.enable { f.y1(7) { |x| x * 2 }; f.tap1 { } }
show(""tracepoint call/return"") { tp_events }
", @"
y0: 42
y1: 4
y2: 3
y2 one param: 1
y1 array splat: [1, 2]
tap1: Foo
twice: 100
ret_early: [5, :yes]
break: 100
break twice: :out
next: 7
return from block: 1001
return twice: :r
no block: ""LocalJumpError: no block given (yield)""
lambda: 15
lambda arity: ""ArgumentError: wrong number of arguments (given 2, expected 1)""
lambda break: 6
proc: 4
proc break: ""LocalJumpError: break from proc-closure""
method obj: 9
symbol: ""AB""
with_blk: 4
raise in block: ""ArgumentError: boom""
rescue around: 1
nested: 7
recursion: 1275
redo: 3
break from loop in block: :after
yield in proc result: 1
binding in block: 10
method_missing interplay: true
frozen self: 1
callee_id: [[:y1, :yy1], [:y1, :y1]]
enable inside block: [:y1]
tracepoint call/return: [[:call, :y1, nil], [:return, :y1, 14], [:call, :tap1, nil], [:return, :tap1, Foo]]
");
        }

        /// <summary>
        /// A light frame (see Method_LightFrame1) also takes instance variable reads and writes and constant reads, whose
        /// helpers are handed the declaring scope instead of a method scope: the runtime is the same, and a method scope
        /// has no module, so a constant lookup from it starts at the declaring scope anyway. Constructors and accessors,
        /// frozen receivers, class-level and BasicObject instance variables, lexical, ::top-level and A::B constants,
        /// const_missing, a redefined constant and autoload behave as they do with a scope.
        /// </summary>
        public void Method_LightFrame2() {
            TestOutput(@"
require 'ruby4'
def show(l); v = begin; yield; rescue Exception => e; ""#{e.class}: #{e.message}""; end; puts ""#{l}: #{v.inspect}""; end
X = :top
module Outer
  X = :outer
  LIMIT = 10
  class Pt
    def initialize(x, y); @x = x; @y = y; end
    def x = @x
    def x=(v); @x = v; end
    def pair = [@x, @y]          # array literal: not light (fine either way)
    def unset = @nope
    def limit = LIMIT
    def lex = X
    def top = ::X
    def qual = Outer::LIMIT
    def missing = NOPE_CONST
    def chain = Outer::Pt
    def ytwo = yield(@x, @y)
  end
  class << self
    def sing_x = X
  end
end
class Outer::Pt
  def lex2 = X        # lexical scope here is top-level (class Outer::Pt), so X is :top
end
p1 = Outer::Pt.new(1, 2)
show(""ctor"") { [p1.x, p1.pair] }
show(""setter"") { p1.x = 5; p1.x }
show(""unset ivar"") { p1.unset }
show(""frozen setter"") { q = Outer::Pt.new(1, 2).freeze; begin; q.x = 3; rescue FrozenError => e; [e.class, e.receiver.equal?(q)]; end }
show(""constants"") { [p1.limit, p1.lex, p1.top, p1.qual, p1.chain, p1.lex2, Outer.sing_x] }
show(""const_missing"") { p1.missing }
show(""const_missing hook"") { class Outer::Pt; def self.const_missing(n) = ""cm:#{n}""; end; p1.missing }
show(""redefined const"") { Outer.send(:remove_const, :LIMIT); Outer.const_set(:LIMIT, 20); p1.limit }
show(""ytwo"") { p1.ytwo { |a, b| a + b } }
class Cls
  @count = 7
  def self.count = @count
  def self.count=(v); @count = v; end
end
show(""class ivar"") { Cls.count = Cls.count + 1; Cls.count }
class BO < BasicObject
  def initialize(v); @v = v; end
  def v = @v
end
show(""basicobject"") { BO.new(3).v }
m = Module.new do
  def self.k = X
end
show(""module new"") { m.k }
S1 = Struct.new(:a) do
  def twice_a = @twice
end
show(""struct ivar"") { S1.new(1).twice_a }
o = Object.new
def o.sing = @s
o.instance_variable_set(:@s, 9)
show(""singleton ivar"") { o.sing }
show(""ivars on frozen string"") { s = +""abc""; def s.iv = @iv; s.iv }
show(""ivar on integer receiver via instance_exec"") { 5.instance_exec { @z } }
show(""autoload const"") { module AL; autoload :Thing, ""/nonexistent/thing""; def self.get = Thing; end; AL.get }
show(""tracepoint ctor"") { ev = []; tp = TracePoint.new(:call, :return) { |t| ev << [t.event, t.method_id] if t.defined_class == Outer::Pt }; tp.enable { Outer::Pt.new(1, 2).x }; ev }
", @"
ctor: [1, [1, 2]]
setter: 5
unset ivar: nil
frozen setter: [FrozenError, true]
constants: [10, :outer, :top, 10, Outer::Pt, :top, :outer]
const_missing: ""NameError: uninitialized constant Outer::Pt::NOPE_CONST""
const_missing hook: ""cm:NOPE_CONST""
redefined const: 20
ytwo: 7
class ivar: 8
basicobject: 3
module new: :top
struct ivar: nil
singleton ivar: 9
ivars on frozen string: nil
ivar on integer receiver via instance_exec: nil
autoload const: ""LoadError: cannot load such file -- /nonexistent/thing""
tracepoint ctor: [[:call, :initialize], [:return, :initialize], [:call, :x], [:return, :x]]
");
        }

        /// <summary>
        /// BigDecimal against CRuby 4.0's bigdecimal 4.0.1: a Rational operand of a comparison is expanded as
        /// for arithmetic (== used to recurse through Rational#== until the stack overflowed), #power and #**
        /// take any real exponent and a precision, round takes half:, and a BigDecimal is frozen and has no
        /// public constructor.
        /// </summary>
        public void BigDecimal_Ruby4_1() {
            TestOutput(@"
require 'ruby4'
require 'bigdecimal'
def t(label)
  r = begin; v = yield; v.is_a?(BigDecimal) ? v.to_s : v.inspect; rescue => e; ""#{e.class}: #{e.message}""; end
  puts ""#{label}: #{r}""
end
t('==') { BigDecimal('0.5') == Rational(1, 2) }
t('<=>') { BigDecimal('0.5') <=> Rational(1, 3) }
t('r+') { Rational(1, 3) + BigDecimal('0.5') }
t('div') { BigDecimal('1').div(Rational(1, 3), 5) }
t('power') { BigDecimal('2').power(BigDecimal('0.5'), 20) }
t('**') { BigDecimal('4') ** Rational(1, 2) }
t('round') { BigDecimal('1.25').round(1, half: :even) }
t('quo') { BigDecimal('1').quo(3, 5) }
t('sqrt') { BigDecimal('-2').sqrt(10) }
t('frozen') { BigDecimal('1').frozen? }
t('new') { BigDecimal.new('1') }
t('allocate') { BigDecimal.allocate }
t('_dump') { BigDecimal('1.5')._dump }
t('BASE') { BigDecimal::BASE.class }
z = %w[-0 0 NaN Infinity -Infinity 1.5].map { |s| BigDecimal(s) }
t('specials') { z.product(z).map { |x, y| [x + y, x * y, x / y, x <=> y].map { |v| v.is_a?(BigDecimal) && v.zero? ? v.sign : v.to_s } }.flatten.join(',') }
t('x/inf raises') { BigDecimal.save_exception_mode { BigDecimal.mode(BigDecimal::EXCEPTION_INFINITY, true); BigDecimal('1') / BigDecimal('Infinity') } }
", @"
==: true
<=>: 1
r+: 0.83333333333333333333333333333333e0
div: 0.3e1
power: 0.14142135623730950488e1
**: 0.2e1
round: 0.12e1
quo: 0.33333e0
sqrt: FloatDomainError: sqrt of negative value
frozen: true
new: NoMethodError: undefined method 'new' for class BigDecimal
allocate: TypeError: allocator undefined for BigDecimal
_dump: ""18:0.15e1""
BASE: Integer
specials: ""-1,1,NaN,0,1,-1,NaN,0,NaN,NaN,NaN,,Infinity,NaN,-1,-1,-Infinity,NaN,1,1,0.15e1,-1,-1,-1,1,-1,NaN,0,1,1,NaN,0,NaN,NaN,NaN,,Infinity,NaN,1,-1,-Infinity,NaN,-1,1,0.15e1,1,1,-1,NaN,NaN,NaN,,NaN,NaN,NaN,,NaN,NaN,NaN,,NaN,NaN,NaN,,NaN,NaN,NaN,,NaN,NaN,NaN,,Infinity,NaN,-Infinity,1,Infinity,NaN,Infinity,1,NaN,NaN,NaN,,Infinity,Infinity,NaN,0,NaN,-Infinity,NaN,1,Infinity,Infinity,Infinity,1,-Infinity,NaN,Infinity,-1,-Infinity,NaN,-Infinity,-1,NaN,NaN,NaN,,NaN,-Infinity,NaN,-1,-Infinity,Infinity,NaN,0,-Infinity,-Infinity,-Infinity,-1,0.15e1,-1,-Infinity,1,0.15e1,1,Infinity,1,NaN,NaN,NaN,,Infinity,Infinity,1,-1,-Infinity,-Infinity,-1,1,0.3e1,0.225e1,0.1e1,0""
x/inf raises: FloatDomainError: Computation results in 'Infinity'
");
        }
    }
}
