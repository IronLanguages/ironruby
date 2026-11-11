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
using IronRuby.Builtins;
using Microsoft.Scripting;

namespace IronRuby.Tests {
    public partial class Tests {
        public void Scenario_RubyExceptions1() {
            AssertExceptionThrown<RuntimeError>(delegate() {
                CompilerTest(@"raise");
            });
        }

        public void Scenario_RubyExceptions1A() {
            AssertExceptionThrown<RuntimeError>(delegate() {
                CompilerTest(@"raise 'foo'");
            });
        }

        public void Scenario_RubyExceptions2A() {
            // a bare `raise` re-raises $!
            AssertExceptionThrown<NotImplementedError>(delegate() {
                CompilerTest(@"
begin
  raise NotImplementedError.new
rescue Exception
  raise
end");
            });
        }

        public void Scenario_RubyExceptions2B() {
            // $! is read-only (Ruby 4.0): NameError, whatever is assigned
            AssertExceptionThrown<MemberAccessException>(delegate() {
                CompilerTest("$! = NotImplementedError");
            });
        }

        public void Scenario_RubyExceptions2C() {
            AssertOutput(delegate() {
                CompilerTest(@"
x = NotImplementedError.new 'hello'
puts x.message

x.message[0] = ?H
puts x.message

x.send :initialize
puts x.message

x.send :initialize, 'Bye'
puts x.message
");
            }, @"
hello
Hello
NotImplementedError
Bye
");
        }
        
        public void Scenario_RubyExceptions2D() {
            AssertOutput(delegate() {
                CompilerTest(@"
x = IOError.new 'hello'
puts x.message

x.message[0] = ?H
puts x.message

x.send :initialize
puts x.message

x.send :initialize,'Bye'
puts x.message
");
            }, @"
hello
Hello
IOError
Bye
");
        }

        public void Scenario_RubyExceptions3() {
            AssertOutput(delegate() {
                CompilerTest(@"
begin
  raise
  print 'U'
rescue
  print 'X'
end
");
            }, "X");
        }

        public void Scenario_RubyExceptions4() {
            AssertOutput(delegate() {
                CompilerTest(@"
begin
  raise
  print 'U'
rescue IOError
  print 'U'
rescue StandardError
  print 'X'
end
");
            }, "X");
        }

        public void Scenario_RubyExceptions5() {
            AssertOutput(delegate() {
                CompilerTest(@"
begin
  raise
rescue StandardError => $x
  puts 'Caught'
  puts ""$! = '#{$!.class.name}'""
  puts ""$x = '#{$x.class.name}'""
end
");
            }, @"
Caught
$! = 'RuntimeError'
$x = 'RuntimeError'
");
        }

        public void Scenario_RubyExceptions6() {
            AssertOutput(delegate() {
                CompilerTest(@"
x = StandardError
begin
  raise
rescue x => x
  puts 'Caught'
  puts ""$! = '#{$!.class.name}'""
  puts ""x = '#{x.class.name}'""
end
");
            }, @"
Caught
$! = 'RuntimeError'
x = 'RuntimeError'
");
        }

        public void Scenario_RubyExceptions7() {
            AssertOutput(delegate() {
                CompilerTest(@"
def foo
  raise
end

begin
  foo
rescue Exception => e
  $found = false
  e.backtrace.each { |frame| if frame.index('foo') != nil then $found = true end } 
  puts $found
end");
            }, @"true");
        }

        public void Scenario_RubyExceptions8() {
            AssertOutput(delegate() {
                CompilerTest(@"
puts 'Begin'
x = begin
  puts 'Raise'
  1
  raise
  puts 'Unreachable'
  2
rescue IOError
  puts 'Rescue1'
  3
rescue
  puts 'Rescue2'
  4
else
  puts 'Else'
  5
ensure
  puts 'Ensure'
  6 
end
puts x
puts 'End'
", 2, 0);
            }, @"
Begin
Raise
Rescue2
Ensure
4
End
");
        }

        public void Scenario_RubyExceptions9() {
            AssertOutput(delegate() {
                CompilerTest(@"
puts 'Begin'
x = class C
  puts 'Raise'
  1
rescue IOError
  puts 'Rescue1'
  3
else
  puts 'Else'
  5
ensure
  puts 'Ensure'
  6 
end
puts x
puts 'End'
");
            }, @"
Begin
Raise
Else
Ensure
5
End
");
        }

        public void Scenario_RubyExceptions10() {
            AssertOutput(delegate() {
                CompilerTest(@"
puts 'Begin'
begin
  puts 'Class'
  class C
    puts 'NoRaise'
  rescue
    puts 'Rescue'
  else
    puts 'Else'
  ensure
    puts 'Ensure'
  end
  puts 'ClassEnd'
rescue
  puts 'OuterRescue'
end
puts 'End'
");
            }, @"
Begin
Class
NoRaise
Else
Ensure
ClassEnd
End
");
        }

        public void Scenario_RubyExceptions11() {
            AssertOutput(delegate() {
                CompilerTest(@"
puts 'Begin'
begin
  puts 'Class'
  class C
    puts 'NoRaise'
  rescue
    puts 'Rescue'
  else
    puts 'Else'
  ensure
    puts 'Ensure'
  end
  puts 'ClassEnd'
rescue
  puts 'OutterRescue'
end
puts 'End'
");
            }, @"
Begin
Class
NoRaise
Else
Ensure
ClassEnd
End
");
        }

        public void Scenario_RubyExceptions12() {
            AssertOutput(delegate() {
                CompilerTest(@"
class A < Exception; end
class B < Exception; end
class C < Exception; end

begin
  raise B
rescue A,B,C => e
  puts e.class
end
");
            }, @"B");
        }

        /// <summary>
        /// Order of evaluation inside rescue clauses: each class is evaluated and compared before the next one
        /// is evaluated, and the ones after a match are not evaluated (G here), as in MRI.
        /// </summary>
        public void Scenario_RubyExceptions12A() {
            AssertOutput(delegate() {
                CompilerTest(@"
class Module
  alias old ===

  def ===(other)
    puts ""cmp(#{self}, #{other})""
    
    old other
  end
end

class A < Exception; end
class B < Exception; end
class C < Exception; end
class D < Exception; end
class E < Exception; end
class F < Exception; end
class G < Exception; end

def id(t)
  puts ""r(#{t})""
  t
end

def foo
  raise F
rescue id(A),id(B),id(C)
  puts 'rescued 1'  # unreachable
rescue id(E),id(F),id(G)
  puts 'rescued 2'
end

foo
");
            }, @"
r(A)
cmp(A, F)
r(B)
cmp(B, F)
r(C)
cmp(C, F)
r(E)
cmp(E, F)
r(F)
cmp(F, F)
rescued 2
");
        }

        /// <summary>
        /// Retry try-catch block.
        /// </summary>
        public void Scenario_RubyExceptions13() {
            AssertOutput(delegate() {
                CompilerTest(@"
i = 0
begin
  puts i
  i = i + 1
  if i < 3 then raise end
rescue
  puts 'retrying'
  retry
else
  puts 'no exception'
ensure
  puts 'giving up'
end
");
            }, @"
0
retrying
1
retrying
2
no exception
giving up
");
        }

        public void Scenario_RubyExceptions14() {
            AssertOutput(delegate() {
                CompilerTest(@"
begin
  raise IOError
rescue IOError
  puts 'rescued'
end
");
            }, "rescued");
        }

        public void Scenario_RubyExceptions15() {
            AssertOutput(delegate() {
                CompilerTest(@"
class C
  def exception
    IOError.new
  end
end

begin
  raise C.new
rescue IOError
  puts 'rescued'
end
");
            }, @"rescued");
        }

        public void Scenario_RubyExceptions16() {
            AssertOutput(delegate() {
                CompilerTest(@"
begin
  raise Exception.new('foo')
rescue Exception => e
  puts e.message
end
");
            }, @"foo");
        }

        public void JumpFromEnsure1() {
            AssertOutput(delegate() {
                CompilerTest(@"
def foo
ensure
  return 1
end

p foo
");
            }, @"1");
        }

        public void Scenario_RubyExceptions_Globals() {
            AssertOutput(delegate() {
                CompilerTest(@"
p $!
p $@

begin
  raise Exception.new
rescue Exception
  p $@ = nil
  p $@ = ['foo']
  p $@ = class A < Array; new; end
  p $@ = [class S < String; new; end]
end
");
            }, @"
nil
nil
nil
[""foo""]
[]
[""""]
");

            // $! must be non-null when assigning to $@
            AssertExceptionThrown<ArgumentException>(delegate() {
                CompilerTest(@"$@ = ['foo']");
            });

            // $! non-nullity checked before type of backtracce:
            AssertExceptionThrown<ArgumentException>(delegate() {
                CompilerTest(@"$@ = 1");
            });

            // backtrace needs to be an array
            AssertExceptionThrown<InvalidOperationException>(delegate() {
                CompilerTest(@"begin; raise Exception.new; rescue Exception; $@ = 1; end");
            });

            // backtrace needs to be an array of strings
            AssertExceptionThrown<InvalidOperationException>(delegate() {
                CompilerTest(@"begin; raise Exception.new; rescue Exception; $@ = ['foo', 1]; end");
            });

            // backtrace needs to be an array, no conversion take place:
            AssertExceptionThrown<InvalidOperationException>(delegate() {
                CompilerTest(@"
class B; def to_ary; []; end; end
begin; raise Exception.new; rescue Exception; $@ = B.new; end
");
            });

            // backtrace needs to be an array of strings, no item conversion take place:
            AssertExceptionThrown<InvalidOperationException>(delegate() {
                CompilerTest(@"
class T; def to_str; ''; end; end
begin; raise Exception.new; rescue Exception; $@ = [T.new]; end
");
            });
        }

        public void Scenario_RubyRescueStatement1() {
            AssertOutput(delegate() {
                CompilerTest(@"
def foo
  raise IOError.new('1')
end

foo rescue puts $!
puts '1' rescue puts '2'
");
            }, @"
1
1");
        }

        public void Scenario_RubyRescueExpression1() {
            AssertOutput(delegate() {
                CompilerTest(@"
def foo
  raise
end

x = foo rescue 2
puts x
");
            }, @"2");
        }

        public void Scenario_RubyRescueExpression2() {
            TestOutput(@"
def foo
  raise
end

def bar
  x = foo rescue return
  puts 'unreachable'
end

def baz
  foo rescue return
  puts 'unreachable'
end

def bazz
  foo rescue return 2
  puts 'unreachable'
end

bar
baz
bazz
", @"", 1);
        }

        public void ExceptionArg1() {
            AssertOutput(delegate() {
                CompilerTest(@"
begin
  raise SystemExit, 42
rescue SystemExit
  puts $!.status
end
");
            }, @"42");
        }

        public void ExceptionArg2() {
            AssertOutput(delegate() {
                CompilerTest(@"
begin
  raise SystemExit, 'foo'
rescue SystemExit
  puts $!.message
end
");
            }, @"foo");
        }

        public void RescueSplat1() {
            AssertOutput(delegate() {
                CompilerTest(@"
begin
  raise IOError
rescue *[IOError]
  puts 'ok'
end
");
            }, @"ok");
        }

        public void RescueSplat2() {
            AssertOutput(delegate() {
                CompilerTest(@"
begin
  raise IOError
rescue SyntaxError, *[SyntaxError, SyntaxError, IOError, nil]
  puts 'ok'
end
");
            }, @"ok");
        }

        public void RescueSplat3() {
            TestOutput(@"
class C
  def respond_to? name
    puts '?' + name.to_s
    false
  end

  def to_ary
    puts 'to_ary'
    [Exception]
  end

  def to_a
    puts 'to_a'
    [SyntaxError, IOError]
  end
end

begin
  begin
    raise IOError
  rescue *C.new
    puts :a
  end
rescue
  puts :b
end
", @"
?to_a
b
");
        }

        public void RescueSplat4() {
            TestOutput(@"
class C
  def respond_to? name
    puts '?' + name.to_s
    true
  end

  def to_a
    puts 'to_a'
    1
  end
end

begin
  begin
    raise IOError
  rescue *C.new
    puts 'ok'
  end
rescue
  p $!
end
", @"
?to_a
to_a
#<TypeError: can't convert C to Array (C#to_a gives Integer)>
");
        }

        public void RescueSplat5() {
            // a rescue clause takes classes and modules only - anything else is a TypeError,
            // so the splatted lists here hold exception classes
            TestOutput(@"
class E < Exception
  
end

def foo(i)
  puts ""foo(#{i})""
  ArgumentError
end

a = []
b = [ArgumentError, TypeError]

begin
  raise E.new
rescue *a, *b, *[foo(0)], foo(1), E => x
  p x
end
", @"
foo(0)
foo(1)
#<E: E>
");
        }

        /// <summary>
        /// Splatted class lists of a rescue clause are evaluated only up to the one that matches, as plain
        /// ones are (Scenario_RubyExceptions12A).
        /// </summary>
        public void RescueListEvaluation1() {
            TestOutput(@"
x = []
begin; raise TypeError; rescue *(x << 1; [ArgumentError]), *(x << 2; [TypeError]), *(x << 3; [NameError]); end
p x
x = []
begin; raise TypeError; rescue (x << 1; ArgumentError), *(x << 2; [TypeError]), (x << 3; NameError) => e; x << e.class; end
p x
x = []
begin; raise TypeError; rescue (x << 1; ArgumentError); rescue (x << 2; TypeError); rescue (x << 3; NameError); end
p x
", @"
[1, 2]
[1, 2, TypeError]
[1, 2]
");
        }

        /// <summary>
        /// A bare rescue, a rescue list and a splatted one all match by calling ===, so a redefined
        /// Module#=== or StandardError.=== is seen.
        /// </summary>
        public void RescueCallsCaseEquality1() {
            TestOutput(@"
class Module
  alias old_eqq ===
  def ===(o); $log << [self, o.class] if $log; old_eqq(o); end
end
$log = []
begin; raise 'x'; rescue; end
p $log
$log = []
begin; raise TypeError; rescue ArgumentError, TypeError; end
p $log
$log = []
begin; raise TypeError; rescue *[ArgumentError, TypeError]; end
p $log
$log = nil
class Module; alias === old_eqq; end
class << StandardError; def ===(o); puts 'StandardError.==='; super; end; end
begin; raise IOError; rescue; puts 'rescued'; end
begin
  begin; raise Exception; rescue; puts 'wrong'; end
rescue Exception
  puts 'not a StandardError'
end
", @"
[[StandardError, RuntimeError]]
[[ArgumentError, TypeError], [TypeError, TypeError]]
[[ArgumentError, TypeError], [TypeError, TypeError]]
StandardError.===
rescued
StandardError.===
not a StandardError
");
        }

        public void ExceptionMapping1() {
            TestOutput(@"
class LoadError
  def self.new message
    ZeroDivisionError.new message
  end
end

class SecurityError                 # partial trust: current directory detection
  def self.new message
    ZeroDivisionError.new message
  end
end

begin
  require 'non-existent'
rescue ZeroDivisionError
  puts 'Caught ZeroDivisionError'
end
", @"
Caught ZeroDivisionError
");
        }

        public void ExceptionMapping2() {
            TestOutput(@"
class ZeroDivisionError
  def self.new(message)
    puts 'new ZeroDivError'
    'not an exception'
  end
end

module Any
  def self.===(other)
    puts ""?#{other.inspect}""
    true
  end
end

begin
  1/0
rescue Any
  puts 'rescue'
  p $!
end

puts 'Done'
", @"
new ZeroDivError
?#<TypeError: exception object expected>
rescue
#<TypeError: exception object expected>
Done
");
        }

        public void ExceptionMapping3() {
            TestOutput(@"
class LoadError
  def initialize *args
    puts 'initLE'
  end
end

class SecurityError
  def initialize *args
    puts 'initSE'
  end
end

begin
  require 'non-existent'
rescue SecurityError
  puts 'Caught SecurityError'
rescue LoadError
  puts 'Caught LoadError'
end
", @"
initLE
Caught LoadError
");
        }

        /// <summary>
        /// A subclass of SystemCallError reads its errno from its Errno constant, which for a class outside
        /// Errno is the Errno module (a TypeError, as in MRI). The location is shown only with a message.
        /// The text of errno 7 is the C library's: Windows' CRT calls E2BIG "Arg list too long".
        /// </summary>
        public void SystemCallErrorSubclass1() {
            TestOutput(@"
require 'ruby4'
class MyErr < SystemCallError; end
class MyErr2 < SystemCallError; Errno = 7; end
class E2 < Errno::ENOENT; end
[
  -> { MyErr.new('a', 10054) }, -> { MyErr.new }, -> { MyErr.new(1, 2, 3) },
  -> { MyErr2.new('q', 'f') }, -> { E2.new('x', 3) }, -> { SystemCallError.new(nil, 2, 'fn') },
].each { |l| p((l.call rescue $!)) }
", @"
#<TypeError: no implicit conversion of Module into Integer>
#<TypeError: no implicit conversion of Module into Integer>
#<ArgumentError: wrong number of arguments (given 3, expected 0..2)>
#<MyErr2: " + (Environment.OSVersion.Platform == PlatformID.Win32NT ? "Arg" : "Argument") + @" list too long @ f - q>
#<E2: No such file or directory @ 3 - x>
#<Errno::ENOENT: No such file or directory>
");
        }

        /// <summary>
        /// Errno numbers and messages are the platform's, as MRI takes them from its C library: the
        /// CLR-backed classes said ECONNREFUSED was 10061 "No connection could be made because the
        /// target machine actively refused it." on Linux too, where MRI says 111 "Connection refused".
        /// A name the platform has no error for is Errno::NOERROR. Errno::ENOEXEC.new made an EBADF.
        /// </summary>
        public void Errno_PlatformTable1() {
            TestOutput(@"
win = RUBY_PLATFORM =~ /mswin|mingw/
mac = RUBY_PLATFORM =~ /darwin/
# Without the prelude: the C# classes on their own.
refused = win ? 10061 : mac ? 61 : 111
e = Errno::ECONNREFUSED.new('x')
p e.errno == refused, e.message == (win ? 'No connection could be made because the target machine actively refused it. - x' : 'Connection refused - x')
p Errno::EINTR.new.message == (win ? 'Interrupted function call' : 'Interrupted system call')
p Errno::EADDRINUSE.new.errno == (win ? 10048 : mac ? 48 : 98)
p Errno::ENOEXEC.new.class

require 'ruby4'
p Errno::ECONNREFUSED::Errno == refused, SystemCallError.new(refused).class
p Errno::EWOULDBLOCK.equal?(Errno::EAGAIN) == !win
p Errno::EAUTH.equal?(Errno::NOERROR) == !mac, Errno::ENOKEY.equal?(Errno::NOERROR) == !!(win || mac)
p Errno::ECONNRESET.new.message == SystemCallError.new(Errno::ECONNRESET::Errno).message
", @"
true
true
true
true
Errno::ENOEXEC
true
Errno::ECONNREFUSED
true
true
true
true
");
        }

        /// <summary>
        /// Socket errors say which call failed, as MRI does, and name resolution is Socket::ResolutionError
        /// with the platform's EAI_* code for TCPServer too (EAI_NONAME was BSD's 8 on Linux, where it is -2).
        /// </summary>
        public void Errno_SocketCalls1() {
            TestOutput(@"
require 'ruby4'
require 'socket'
win = RUBY_PLATFORM =~ /mswin|mingw/
mac = RUBY_PLATFORM =~ /darwin/
p Socket::EAI_NONAME == (win ? 11001 : mac ? 8 : -2), Socket::EAI_AGAIN == (win ? 11002 : mac ? 2 : -3)
s = TCPServer.new('127.0.0.1', 0)
port = s.addr[1]
begin; TCPServer.new('127.0.0.1', port); rescue => e; p e.class; puts e.message[/ - .*/].sub(port.to_s, 'PORT'); end
s.close
begin; TCPSocket.new('127.0.0.1', port); rescue => e; p e.class; puts e.message[/ - .*/].sub(port.to_s, 'PORT'); end
[-> { TCPServer.new('no.such.host.invalid', 0) }, -> { TCPSocket.new('no.such.host.invalid', 80) }].each do |l|
  begin
    l.call
  rescue SocketError => e
    p [e.class, e.message[/\Agetaddrinfo(\(3\))?: /], [Socket::EAI_NONAME, Socket::EAI_AGAIN].include?(e.error_code)]
  end
end
", @"
true
true
Errno::EADDRINUSE
 - bind(2) for ""127.0.0.1"" port PORT
Errno::ECONNREFUSED
 - connect(2) for ""127.0.0.1"" port PORT
[Socket::ResolutionError, ""getaddrinfo: "", true]
[Socket::ResolutionError, ""getaddrinfo(3): "", true]
");
        }

        /// <summary>
        /// Socket.getaddrinfo and Addrinfo.getaddrinfo name the call once (it was "getaddrinfo: getaddrinfo: ..."),
        /// and a service that is not one is Socket::ResolutionError with EAI_SERVICE, as in MRI, not a plain
        /// SocketError - nor port 0 for Addrinfo, which also reads a service name for Addrinfo.tcp.
        /// </summary>
        public void Errno_SocketResolution1() {
            TestOutput(@"
require 'ruby4'
require 'socket'
[-> { Socket.getaddrinfo('no.such.host.invalid', 80) }, -> { Addrinfo.getaddrinfo('no.such.host.invalid', 80) }].each do |l|
  begin
    l.call
  rescue SocketError => e
    p [e.class, e.message.scan('getaddrinfo').size]
  end
end
[-> { Socket.getaddrinfo('localhost', 'nosuchservice') }, -> { Socket.getaddrinfo(nil, 'nosuchservice', nil, :STREAM) },
 -> { Addrinfo.getaddrinfo('localhost', 'nosuchservice') }, -> { Addrinfo.tcp('localhost', 'nosuchservice') }].each do |l|
  begin
    l.call
  rescue SocketError => e
    p [e.class, e.message, e.error_code == Socket::EAI_SERVICE]
  end
end
p Addrinfo.tcp('localhost', 'http').ip_port, Addrinfo.udp('localhost', '53').ip_port, Addrinfo.getaddrinfo('localhost', 'http').first.ip_port
", @"
[Socket::ResolutionError, 1]
[Socket::ResolutionError, 1]
[Socket::ResolutionError, ""getaddrinfo: Servname not supported for ai_socktype"", true]
[Socket::ResolutionError, ""getaddrinfo: Servname not supported for ai_socktype"", true]
[Socket::ResolutionError, ""getaddrinfo: Servname not supported for ai_socktype"", true]
[Socket::ResolutionError, ""getaddrinfo: Servname not supported for ai_socktype"", true]
80
53
80
");
        }

        /// <summary>
        /// Socket#connect and #bind name the address, a would-block read names its call, and a socket
        /// option on a closed socket is IOError before anything reaches the kernel (it was EBADF).
        /// </summary>
        public void Errno_SocketCalls2() {
            TestOutput(@"
require 'ruby4'
require 'socket'
s = TCPServer.new('127.0.0.1', 0)
port = s.addr[1]
def t(port)
  yield
rescue => e
  puts ""#{e.class}#{e.message[/ - .*/]&.sub(port.to_s, 'PORT')}""
end
t(port) { Socket.new(:INET, :STREAM).bind(Socket.sockaddr_in(port, '127.0.0.1')) }
c = TCPSocket.new('127.0.0.1', port)
t(port) { c.recv_nonblock(1) }
x = Socket.new(:INET, :STREAM)
x.close
t(port) { x.getsockopt(:SOCKET, :TYPE) }
t(port) { x.setsockopt(:SOCKET, :REUSEADDR, true) }
c.close
s.close
t(port) { Socket.new(:INET, :STREAM).connect(Socket.sockaddr_in(port, '127.0.0.1')) }
", @"
Errno::EADDRINUSE - bind(2) for 127.0.0.1:PORT
IO::EAGAINWaitReadable - recvfrom(2) would block
IOError
IOError
Errno::ECONNREFUSED - connect(2) for 127.0.0.1:PORT
");
        }

        /// <summary>
        /// A UDP socket an ICMP "port unreachable" has failed is ECONNREFUSED on the next read (recv
        /// waited forever, read_nonblock said EAGAIN), and a name gethostbyname cannot resolve is
        /// Socket::ResolutionError (a plain SocketError). connect(2) with a timeout - IO#timeout,
        /// Addrinfo#connect(timeout:), Socket.tcp - reads SO_ERROR for the verdict as CRuby does since
        /// [Bug #22223]; Addrinfo#connect and Socket.tcp ignored the timeout altogether.
        /// Windows reports the ICMP error as WSAECONNRESET (CRuby's ECONNRESET there too), and retries
        /// a refused connect for about two seconds, so a connect with no time at all has timed out.
        /// </summary>
        public void Errno_SocketCalls3() {
            bool windows = Environment.OSVersion.Platform == PlatformID.Win32NT;
            string icmp = windows ? "Errno::ECONNRESET" : "Errno::ECONNREFUSED";
            TestOutput(@"
require 'ruby4'
require 'socket'
def t
  r = yield
  puts r.inspect
rescue StandardError => e
  puts ""#{e.class}#{e.message[/ - .*/]&.gsub(/\d{4,5}/, 'PORT')}""
end
closed = (s = UDPSocket.new; s.bind('127.0.0.1', 0); port = s.addr[1]; s.close; port)
u = UDPSocket.new
u.connect('127.0.0.1', closed)
[-> { u.recv(10) }, -> { u.read_nonblock(10) }, -> { u.recvfrom_nonblock(10) }].each do |l|
  u.send('x', 0)
  sleep 0.1
  t(&l)
end
u.close
t { Socket.gethostbyname('no.such.host.invalid') }
t { Socket.gethostbyname('no.such.host.invalid') rescue $!.error_code == Socket::EAI_NONAME || $!.error_code == Socket::EAI_AGAIN }

server = TCPServer.new('127.0.0.1', 0)
port = server.addr[1]
t { Socket.tcp('127.0.0.1', port, connect_timeout: 5) { |c| c.remote_address.ip_port == port } }
t { Socket.tcp('127.0.0.1', port, open_timeout: 0) { |c| c.remote_address.ip_port == port } }
t { Addrinfo.tcp('127.0.0.1', port).connect(timeout: 5) { |c| c.remote_address.ip_port == port } }
t { s = Socket.new(:INET, :STREAM); s.timeout = 5; s.connect(Socket.sockaddr_in(port, '127.0.0.1')); s.getpeername == Socket.sockaddr_in(port, '127.0.0.1') }
t { Socket.tcp('127.0.0.1', port, open_timeout: 1, connect_timeout: 1) }
server.close
t { Socket.tcp('127.0.0.1', port, connect_timeout: 5) }
t { Addrinfo.tcp('127.0.0.1', port).connect(timeout: 0) }
t { s = Socket.new(:INET, :STREAM); s.timeout = 5; s.connect(Socket.sockaddr_in(port, '127.0.0.1')) }
t { TCPSocket.new('127.0.0.1', port, connect_timeout: 5) }
", icmp + @" - recvfrom(2)
" + icmp + @"
" + icmp + @" - recvfrom(2)
Socket::ResolutionError
true
true
true
true
true
ArgumentError
Errno::ECONNREFUSED - connect(2) for 127.0.0.1:PORT
" + (windows ? "Errno::ETIMEDOUT - user specified timeout" : "Errno::ECONNREFUSED - connect(2)") + @" for 127.0.0.1:PORT
Errno::ECONNREFUSED - connect(2) for 127.0.0.1:PORT
Errno::ECONNREFUSED - connect(2) for ""127.0.0.1"" port PORT
");
        }

        /// <summary>
        /// File.rename is rename(2): a directory renamed onto a file is ENOTDIR and a file onto a
        /// directory EISDIR - the emulation deleted the file at the target first, and lost it. A name
        /// too long for the file system is ENAMETOOLONG (Dir.mkdir said EEXIST, File.open ENOENT with
        /// .NET's text) and a symbolic link loop is ELOOP (File.open said IOError).
        /// </summary>
        public void Errno_FileCalls1() {
            TestOutput(@"
require 'ruby4'
require 'tmpdir'
win = RUBY_PLATFORM =~ /mswin|mingw/
Dir.mktmpdir do |d|
  def t(d)
    yield
    puts 'ok'
  rescue SystemCallError => e
    puts ""#{e.class}: #{e.message.sub(d, 'D').sub(d, 'D')}""
  end
  f = File.join(d, 'f')
  File.write(f, 'data')
  Dir.mkdir(File.join(d, 'sub'))
  t(d) { File.rename(File.join(d, 'sub'), f) }
  t(d) { File.rename(f, File.join(d, 'sub')) }
  p File.read(f), File.directory?(File.join(d, 'sub'))
  t(d) { File.rename(f, File.join(d, 'g')); File.rename(File.join(d, 'g'), f) }
  unless win
    t(d) { Dir.mkdir(File.join(d, 'a' * 300)) rescue raise $!.class }
    t(d) { File.open(File.join(d, 'a' * 300)) rescue raise $!.class }
    File.symlink(File.join(d, 'l1'), File.join(d, 'l2'))
    File.symlink(File.join(d, 'l2'), File.join(d, 'l1'))
    t(d) { File.open(File.join(d, 'l1')) }
  else
    # Windows has no ENAMETOOLONG for open (win32.c says ENOENT), and a symbolic link needs a privilege.
    puts 'Errno::ENAMETOOLONG: File name too long', 'Errno::ENAMETOOLONG: File name too long', 'Errno::ELOOP: Too many levels of symbolic links - D/l1'
  end
end
", @"
Errno::ENOTDIR: Not a directory - (D/sub, D/f)
Errno::EISDIR: Is a directory - (D/f, D/sub)
""data""
true
ok
Errno::ENAMETOOLONG: File name too long
Errno::ENAMETOOLONG: File name too long
Errno::ELOOP: Too many levels of symbolic links - D/l1
");
        }

        /// <summary>
        /// Objects Marshal cannot write are refused the way MRI words it: Queue and SizedQueue by name
        /// ("can't dump Thread::Queue"), and a Fiber as native data rather than by the Proc it holds.
        /// </summary>
        public void Marshal_Undumpable1() {
            TestOutput(@"
require 'ruby4'
[Thread::Queue.new, Thread::SizedQueue.new(1), Thread::ConditionVariable.new, Thread::Mutex.new, Fiber.new {}].each do |o|
  begin
    Marshal.dump(o)
  rescue TypeError => e
    puts e.message
  end
end
", @"
can't dump Thread::Queue
can't dump Thread::SizedQueue
can't dump Thread::ConditionVariable
no _dump_data is defined for class Thread::Mutex
no _dump_data is defined for class Fiber
");
        }

        /// <summary>
        /// Zlib.gunzip reports a bad gzip string with GzipFile's errors and MRI's messages, not the zlib
        /// stream's ("incorrect header check", "buffer error"); Zlib::Inflate.inflate of a stream that
        /// stops short is a BufError rather than the part that did inflate.
        /// </summary>
        public void Zlib_GunzipErrors1() {
            TestOutput(@"
require 'ruby4'
require 'zlib'
require 'stringio'
good = Zlib.gzip('hello')
def t
  r = yield
  puts ""ok #{r}""
rescue Zlib::Error => e
  puts ""#{e.class}: #{e.message}""
end
t { Zlib.gunzip('') }
t { Zlib.gunzip('hello world, not gzip') }
t { s = good.dup; s.setbyte(2, 7); Zlib.gunzip(s) }
t { s = good.dup; s.setbyte(3, 0x02); Zlib.gunzip(s) }
t { s = good.dup; s.setbyte(3, 0x20); Zlib.gunzip(s) }
t { Zlib.gunzip(good[0, 15]) }
t { Zlib.gunzip(good[0, good.size - 8]) }
t { Zlib.gunzip(good[0, good.size - 4]) }
t { s = good.dup; s.setbyte(s.size - 8, s.getbyte(s.size - 8) ^ 1); Zlib.gunzip(s) }
t { s = good.dup; s.setbyte(s.size - 1, 1); Zlib.gunzip(s) }
t { Zlib.gunzip(good + 'xyz') }
t { s = good.dup; s.setbyte(3, 0x20); Zlib::GzipReader.new(StringIO.new(s)).read }
t { Zlib::Inflate.inflate(Zlib::Deflate.deflate('hello')[0, 4]) }
", @"
Zlib::GzipFile::Error: unexpected end of string
Zlib::GzipFile::Error: not in gzip format
Zlib::GzipFile::Error: unsupported compression method 7
Zlib::GzipFile::Error: multi-part gzip file is not supported
Zlib::GzipFile::Error: encrypted gzip file is not supported
Zlib::GzipFile::Error: unexpected end of file
Zlib::GzipFile::NoFooter: footer is not found
Zlib::GzipFile::Error: unexpected end of string
Zlib::GzipFile::CRCError: invalid compressed data -- crc error
Zlib::GzipFile::LengthError: invalid compressed data -- length error
ok hello
Zlib::GzipFile::Error: encrypted gzip file is not supported
Zlib::BufError: buffer error
");
        }

        /// <summary>
        /// A recursion deeper than the stack goes is a SystemStackError, which a program can rescue and go on from, as in
        /// MRI (#128): a method, a method the JIT does not take, a define_method method, a lambda, through an ensure, and
        /// in a thread. It ended the process with a CLR stack overflow - at the recursion where no frame checked the
        /// stack, and on the way out where it did, because each catch that rethrew from inside itself kept every frame
        /// below it (Build/dlr-patches/0105). The rescue runs once the frames are gone, so it can call methods. The
        /// expected output is CRuby 4.0's.
        /// </summary>
        public void Exception_StackLevelTooDeep1() {
            TestOutput(@"
def deep(n); n == 0 ? 0 : 1 + deep(n - 1); end
def interp(n); n == 0 ? 0 : ""#{1}"".to_i + interp(n - 1); end
class D; define_method(:dm) { |n| n == 0 ? 0 : 1 + dm(n - 1) }; end
l = lambda { |n| n == 0 ? 0 : 1 + l.call(n - 1) }
$ensured = 0
def ens(n); n == 0 ? 0 : (begin; 1 + ens(n - 1); ensure; $ensured += 1; end); end
[lambda { deep(100_000_000) }, lambda { interp(100_000_000) }, lambda { D.new.dm(100_000_000) }, lambda { l.call(100_000_000) }, lambda { ens(100_000_000) }].each do |c|
  begin
    c.call
    puts :returned
  rescue SystemStackError => e
    # the rescue runs with the stack unwound: it can call methods, and recurse a little
    puts [e.class, e.message, deep(100), e.backtrace.size > 100].inspect
  end
end
p $ensured > 100
p Thread.new { begin; deep(100_000_000); rescue SystemStackError; :caught; end }.value
p deep(1000)
", @"
[SystemStackError, ""stack level too deep"", 100, true]
[SystemStackError, ""stack level too deep"", 100, true]
[SystemStackError, ""stack level too deep"", 100, true]
[SystemStackError, ""stack level too deep"", 100, true]
[SystemStackError, ""stack level too deep"", 100, true]
true
:caught
1000
");
        }

        /// <summary>
        /// A JIT-compiled method whose last act is to call itself still makes a frame per call, so a recursion with no
        /// end raises SystemStackError as in MRI (#165). RyuJIT made such a self-call a jump (an implicit tail call):
        /// `def d(n) = d(n + 1)' ran forever at 100% CPU. Each recursion here does end, after 10M calls, so a frame
        /// that is reused shows as a return value and not as a hang. It runs on a thread with a 1 MB stack, so the
        /// SystemStackError comes after some thousands of frames: .NET 8 takes seconds to throw through 100k+ dynamic
        /// method frames, .NET 10 a fraction of one.
        /// </summary>
        public void Exception_StackLevelTooDeepJitTailCall1() {
            int specialized = IronRuby.Runtime.Jit.JitRuntime.Specialized;
            Engine.Execute(@"
def jit_tail1(n) = n == 10_000_000 ? :returned : jit_tail1(n + 1)
def jit_tail2(n, step) = n >= 10_000_000 ? :returned : jit_tail2(n + step, step)
def jit_tail3(n)
  return :returned if n == 10_000_000
  jit_tail3(n + 1)
end
def jit_tail4(x) = x > 1.0e7 ? :returned : jit_tail4(x + 1.0)
");
            object result = null;
            Exception error = null;
            var worker = new System.Threading.Thread(() => {
                try {
                    result = Engine.Execute(@"
[lambda { jit_tail1(0) }, lambda { jit_tail2(0, 1) }, lambda { jit_tail3(0) }, lambda { jit_tail4(0.0) }].map do |c|
  begin
    c.call
  rescue SystemStackError => e
    e.class.name
  end
end.join(' ')
").ToString();
                } catch (Exception e) {
                    error = e;
                }
            }, 1024 * 1024);
            worker.IsBackground = true;
            worker.Start();
            Assert(worker.Join(TimeSpan.FromMinutes(2)), "the recursion did not end");
            Assert(error == null, error?.ToString());
            AreEqual(result, "SystemStackError SystemStackError SystemStackError SystemStackError");
            int specializedHere = IronRuby.Runtime.Jit.JitRuntime.Specialized - specialized;
            Assert(specializedHere >= 4, String.Format("methods specialized: {0} (expected 4)", specializedHere));
        }

        /// <summary>
        /// An exception passing interpreted frames that have only finally blocks to run (a method's own epilogue, an
        /// `ensure') goes on through them instead of being caught and thrown again in each (Build/dlr-patches/0107). The
        /// ensure clauses still run innermost first and before an outer `rescue' tests the exception, a jump out of an
        /// `ensure' still ends it, and the backtrace is the same. Run in an engine that only interprets, and in the test
        /// engine, which compiles. The expected output is CRuby 4.0's.
        /// </summary>
        public void Exception_InterpretedFramesPassThrough1() {
            const string script = @"
$log = []
def a; begin; b; ensure; $log << ""a-ens #{$!.message}""; end; end
def b; c; ensure; $log << ""b-ens""; end
def c; d; end
def d; raise ""x""; end
def m; yield; ensure; $log << ""m-ens""; end
begin
  a
rescue ($log << ""match""; RuntimeError) => e
  $log << e.backtrace.map { |f| f[/:\d+:in '[^']*'/] }.join("","")
end
def swallow; d; ensure; return :swallowed; end
$log << swallow
$log << catch(:t) { m { throw :t, 5 } }
$log << [1, 2].each { |x| m { break x * 10 } }
def ret_from_block; m { return :from_block }; :no; end
$log << ret_from_block
def lp; i = 0; while i < 3; begin; i += 1; next if i == 1; raise ""y"" if i == 2; ensure; $log << ""lp-ens #{i}""; end; end; rescue => e; $log << ""lp-rescued #{e.message}""; end
lp
$log << (m { m { Integer(""zz"") } } rescue $!.class)
$log.join(""|"")
";
            const string expected = "b-ens|a-ens x|match|:6:in 'Object#d',:5:in 'Object#c',:4:in 'Object#b',:3:in 'Object#a',:9:in '<main>'|swallowed|m-ens|5|m-ens|m-ens|1|2|m-ens|from_block|lp-ens 1|lp-ens 2|lp-rescued y|m-ens|m-ens|ArgumentError";

            var interpreting = Ruby.CreateEngine((setup) => { setup.Options["CompilationThreshold"] = Int32.MaxValue; });
            AreEqual(interpreting.CreateScriptSourceFromString(script, "pass1.rb", SourceCodeKind.File).Execute().ToString(), expected);
            AreEqual(Engine.CreateScriptSourceFromString(script, "pass2.rb", SourceCodeKind.File).Execute().ToString(), expected);
        }

        /// <summary>
        /// A compiled body with only an `ensure' lets an exception whose backtrace has been captured go through (a real exception
        /// filter, RubyOps.PassEnsure) instead of catching it and throwing it again, and a compiled frame that rescues an exception
        /// leaves the walk of the stack below it until the exception is exposed (RubyExceptionData.TryDeferCatchSite). $! in the
        /// ensure clauses and their order against an outer `rescue'; a raise and a bare re-raise from an ensure clause; the
        /// exception kept by a #=== of a rescue clause, in a global, re-raised, as a cause - also of one rescued by a recursive call
        /// of the method that rescued it -, retry, an exception kept before it was raised (by the caller, by its #initialize);
        /// and the backtraces after each. The expected output is CRuby 4.0's. Run
        /// interpreted and compiled.
        /// </summary>
        public void Exception_EnsurePassThroughDeferredTrace1() {
            const string script = @"$log = []
def bt(e) = e.backtrace.map { |f| f[/:\d+:in '[^']*'/] }.join("","")
def d; raise ""x""; end
def e1; d; ensure; $log << ""e1 #{$!.message}""; end
def e2; begin; e1; ensure; $log << ""e2 #{$!.message}""; end; end
begin
  e2
rescue ($log << ""match""; RuntimeError) => e
  $log << bt(e)
end
def ens_raise; d; ensure; raise IOError, ""ens""; end
begin; ens_raise; rescue IOError => e; $log << ""#{e.message} #{e.cause.message} #{bt(e.cause)}""; end
def ens_reraise; e1; ensure; raise; end
begin; ens_reraise; rescue => e; $log << bt(e); end
def ens_reads; e1; ensure; $log << bt($!); end
begin; ens_reads; rescue; end
class Keep; def self.===(e) = ($kept = e; false); end
def keep; d; rescue Keep; end
begin; keep; rescue; end
$log << bt($kept)
def to_global; d; rescue; $g = $!; end
to_global; $log << bt($g)
def bare; d; rescue; raise; end
begin; bare; rescue => e; $log << bt(e); end
def chain; d; rescue; raise IOError, ""c""; end
begin; chain; rescue => e; $log << ""#{bt(e)} / #{bt(e.cause)}""; end
def rr(n); d; rescue; rr(n - 1) if n > 0; raise IOError, ""r#{n}""; end
begin; rr(2); rescue => e; while e; $log << bt(e); e = e.cause; end; end
def again; n = 0; begin; n += 1; d; rescue; retry if n < 3; n; end; end
$log << again
def nomatch; d; rescue TypeError; end
begin; nomatch; rescue => e; $log << bt(e); end
$log << (d rescue bt($!))
$log << $!.inspect
def swallow(e); raise e; rescue RuntimeError; end
kept = RuntimeError.new(""k""); swallow(kept); $log << bt(kept)
class Held < StandardError; def initialize(m = ""h"") = ($held = self; super); end
def held; raise Held; rescue Held; end
held; $log << bt($held)
$log.join(""|"")
";
            const string expected = "e1 x|e2 x|match|:3:in 'Object#d',:4:in 'Object#e1',:5:in 'Object#e2',:7:in '<main>'|ens x :3:in 'Object#d',:11:in 'Object#ens_raise',:12:in '<main>'|e1 x|:3:in 'Object#d',:4:in 'Object#e1',:13:in 'Object#ens_reraise',:14:in '<main>'|e1 x|:3:in 'Object#d',:4:in 'Object#e1',:15:in 'Object#ens_reads',:16:in '<main>'|:3:in 'Object#d',:18:in 'Object#keep',:19:in '<main>'|:3:in 'Object#d',:21:in 'Object#to_global',:22:in '<main>'|:3:in 'Object#d',:23:in 'Object#bare',:24:in '<main>'|:25:in 'Object#chain',:26:in '<main>' / :3:in 'Object#d',:25:in 'Object#chain',:26:in '<main>'|:27:in 'Object#rr',:27:in 'Object#rr',:27:in 'Object#rr',:28:in '<main>'|:3:in 'Object#d',:27:in 'Object#rr',:27:in 'Object#rr',:27:in 'Object#rr',:28:in '<main>'|:3:in 'Object#d',:27:in 'Object#rr',:27:in 'Object#rr',:28:in '<main>'|:3:in 'Object#d',:27:in 'Object#rr',:28:in '<main>'|3|:3:in 'Object#d',:31:in 'Object#nomatch',:32:in '<main>'|:3:in 'Object#d',:33:in '<main>'|nil|:35:in 'Object#swallow',:36:in '<main>'|:38:in 'Object#held',:39:in '<main>'";

            var interpreting = Ruby.CreateEngine((setup) => { setup.Options["CompilationThreshold"] = Int32.MaxValue; });
            AreEqual(interpreting.CreateScriptSourceFromString(script, "pass3.rb", SourceCodeKind.File).Execute().ToString(), expected);
            AreEqual(Engine.CreateScriptSourceFromString(script, "pass4.rb", SourceCodeKind.File).Execute().ToString(), expected);
        }

        /// <summary>
        /// An exception Kernel#raise made goes through compiled ensure-only bodies before any frame has taken its backtrace
        /// (RubyOps.PassEnsure, RubyExceptionData.EnterPassedEnsure): the backtrace an ensure clause reads in $! - after calls that
        /// reuse the records of the frames left, in a recursion, as the cause of an exception the clause raises - and the one the
        /// frame that rescues it reads, from a method, a block, a rescue that does not match, a rescue modifier and a thread; an
        /// ensure clause that ends the exception with a jump. A throw through a catch of another tag, and the ensure clause it
        /// passes. The expected output is CRuby 4.0's. Run interpreted, compiled from the
        /// first call and as the default engine compiles.
        /// </summary>
        public void Exception_EnsurePassUntraced1() {
            const string script = @"$log = []
def bt(e) = e.backtrace.map { |f| f[/:\d+:in '[^']*'/] }.join("","")
def noise = [1].map { |v| v + 1 }
def d; raise ""x""; end
def e1; d; ensure; noise; end
def e2; e1; ensure; noise; $log << bt($!); end
def e3; e2; ensure; noise; end
begin; e3; rescue => e; $log << bt(e); end
def er(n); n == 0 ? d : er(n - 1); ensure; noise; $log << bt($!) if n == 1; end
begin; er(3); rescue => e; $log << bt(e); end
def ef; e1; ensure; noise; raise IOError, ""f""; end
begin; ef; rescue => e; $log << ""#{bt(e)} / #{bt(e.cause)}""; end
def eg; e1; ensure; return 7; end
$log << eg
def eh; [1].each { begin; e1; ensure; next; end }; :h; end
$log << eh
def ei; [1].each { begin; e1; rescue => e; $log << bt(e); end }; end
ei
def ej; e1; rescue TypeError; end
begin; ej; rescue => e; $log << bt(e); end
$log << (e1 rescue bt($!))
t = Thread.new { begin; e1; rescue => e; bt(e); end }
$log << t.value
$log << catch(:a) { catch(:b) { begin; throw :a, 1; ensure; $log << ""ens""; end } }
$log << catch(:a) { catch(:b) { throw :b, 2 }; 3 }
$log.join(""|"")
";
            const string expected = ":4:in 'Object#d',:5:in 'Object#e1',:6:in 'Object#e2',:7:in 'Object#e3',:8:in '<main>'|:4:in 'Object#d',:5:in 'Object#e1',:6:in 'Object#e2',:7:in 'Object#e3',:8:in '<main>'|:4:in 'Object#d',:9:in 'Object#er',:9:in 'Object#er',:9:in 'Object#er',:9:in 'Object#er',:10:in '<main>'|:4:in 'Object#d',:9:in 'Object#er',:9:in 'Object#er',:9:in 'Object#er',:9:in 'Object#er',:10:in '<main>'|:11:in 'Object#ef',:12:in '<main>' / :4:in 'Object#d',:5:in 'Object#e1',:11:in 'Object#ef',:12:in '<main>'|7|h|:4:in 'Object#d',:5:in 'Object#e1',:17:in 'block in Object#ei',:17:in 'Array#each',:17:in 'Object#ei',:18:in '<main>'|:4:in 'Object#d',:5:in 'Object#e1',:19:in 'Object#ej',:20:in '<main>'|:4:in 'Object#d',:5:in 'Object#e1',:21:in '<main>'|:4:in 'Object#d',:5:in 'Object#e1',:22:in 'block in <main>'|ens|1|3";

            var interpreting = Ruby.CreateEngine((setup) => { setup.Options["CompilationThreshold"] = Int32.MaxValue; });
            AreEqual(interpreting.CreateScriptSourceFromString(script, "pass9.rb", SourceCodeKind.File).Execute().ToString(), expected);
            var compiling = Ruby.CreateEngine((setup) => { setup.Options["CompilationThreshold"] = 0; });
            AreEqual(compiling.CreateScriptSourceFromString(script, "pass9.rb", SourceCodeKind.File).Execute().ToString(), expected);
            AreEqual(Engine.CreateScriptSourceFromString(script, "pass9.rb", SourceCodeKind.File).Execute().ToString(), expected);
        }

        /// <summary>
        /// The walk of the stack below a rescuing frame waits only for an exception Kernel#raise made itself: C# code may keep the
        /// exception it throws and hand it to Ruby code after the frame is gone (Encoding::Converter#last_error is the one convert
        /// threw), and so may a CLR exception thrown again. Their backtraces must keep the callers of the rescuing frame, and no
        /// other frame's. CRuby's #last_error is a fresh exception with no backtrace at all, which passes too.
        /// </summary>
        public void Exception_DeferredTraceKeptByClr1() {
            const string script = @"def bt(e) = e.backtrace.to_a.map { |f| f[/in '[^']*'/] }.join("","")
EC = Encoding::Converter.new(""UTF-8"", ""EUC-JP"")
def c1; begin; EC.convert(""a\xffb""); rescue Encoding::InvalidByteSequenceError; end; end
def c0; c1; end
c0
r = [EC.last_error.backtrace.nil? || bt(EC.last_error).include?(""Object#c0"")]
def k1(read); return bt(EC.last_error) if read; begin; EC.convert(""a\xffb""); rescue Encoding::InvalidByteSequenceError; end; nil; end
def first_path; k1(false); end
def other_path; k1(true); end
first_path
x = other_path
r << (x.empty? || x.include?(""first_path"") && !x.include?(""other_path""))
EX = System::Runtime::InteropServices::Marshal.GetExceptionForHR(-2147024809)
def thr; System::Runtime::ExceptionServices::ExceptionDispatchInfo.capture(EX).throw; end
def x1; begin; thr; rescue Exception; end; end
def x0; x1; end
x0
r << bt(EX).include?(""Object#x0"")
def x2; begin; thr; rescue Exception => e; bt(e); end; end
def x3; x2; end
r << x3.include?(""Object#x0"")
r.join(""|"")
";
            var interpreting = Ruby.CreateEngine((setup) => { setup.Options["CompilationThreshold"] = Int32.MaxValue; });
            AreEqual(interpreting.CreateScriptSourceFromString(script, "kept1.rb", SourceCodeKind.File).Execute().ToString(), "true|true|true|true");
            AreEqual(Engine.CreateScriptSourceFromString(script, "kept2.rb", SourceCodeKind.File).Execute().ToString(), "true|true|true|true");
        }

        /// <summary>
        /// A compiled body none of whose rescue clauses can match lets the exception go through in the CLR's first pass
        /// (RubyOps.RescuePassMayTest) instead of catching it and throwing it again; anything that could tell the difference takes the
        /// catch. Non-matching lists of constants (the backtrace and $! after), a class expression with a side effect or one that
        /// raises, a later clause with a splat, `===' redefined and a constant reassigned after the site has run, an ensure
        /// clause that redefines `===' before an outer clause is matched, an ensure clause raising another exception, retry, else,
        /// a module the exception was extended with, a bare rescue of a non-StandardError, a throw through the frames, a
        /// #set_backtrace of the exception's own that redefines `===' (it runs before the clauses are matched). The expected
        /// output is CRuby 4.0's; run interpreted and compiled.
        /// </summary>
        public void Exception_RescueFilterPassThrough1() {
            const string script = @"$log = []
def bt(e) = e.backtrace.map { |f| f[/:\d+:in '[^']*'/] }.join("","")
def boom(k = ArgumentError) = raise(k, ""b"")
def nm(n) = n == 0 ? boom : (begin; nm(n - 1); rescue TypeError, NameError; $log << :never; end)
def side; begin; boom; rescue ($log << :side; TypeError); end; end
def raising_list; begin; boom; rescue (raise IOError, ""in list""); end; end
def two(n) = (begin; boom; rescue TypeError; rescue (n << 1; NameError), *(n << 2; [ArgumentError]); :two; end)
class Later < StandardError; end
def later; begin; boom; rescue Later; :later; end; end
def k_const; begin; boom; rescue KK; :kk; end; end
KK = TypeError
def cap; begin; boom; rescue ArgumentError => e; raise e; end; end
def ens_redefine; cap; ensure; def Later.===(e) = true; end
def outer_redefine; begin; ens_redefine; rescue Later; :caught_after_ensure; end; end
def ens_other; boom; ensure; raise IOError, ""other""; end
def nm2(n) = n == 0 ? ens_other : (begin; nm2(n - 1); rescue TypeError; end)
def retr; n = 0; begin; n += 1; boom(n < 3 ? TypeError : ArgumentError); rescue TypeError; retry; end; end
def els; begin; boom; rescue TypeError; else; :else; end; end
module Tag; end
def tagged; begin; raise ArgumentError.new(""t"").extend(Tag); rescue Tag; :tag; end; end
def bare_nomatch; begin; raise Exception, ""not std""; rescue; :wrong; end; end
def thrower(n) = n == 0 ? throw(:t, :thrown) : (begin; thrower(n - 1); rescue TypeError; end)
5.times do
  $log << (begin; nm(3); rescue => e; [e.class, bt(e), $!.class]; end).inspect
  $log << (begin; side; rescue => e; e.class; end)
  $log << (begin; raising_list; rescue => e; [e.class, e.cause.class]; end).inspect
  n = []; $log << two(n) << n.inspect
  $log << (begin; later; rescue => e; e.class; end)
  $log << (begin; k_const; rescue => e; e.class; end)
  $log << (begin; nm2(2); rescue => e; [e.message, e.cause.class]; end).inspect
  $log << (begin; retr; rescue => e; e.class; end)
  $log << (begin; els; rescue => e; e.class; end)
  $log << tagged << (begin; bare_nomatch; rescue Exception => e; e.class; end)
  $log << catch(:t) { thrower(3) }
end
def Later.===(e) = true
$log << later
Object.send(:remove_const, :KK); KK = ArgumentError
$log << k_const
class << Later; remove_method :===; end
$log << outer_redefine
class Sneaky < StandardError
  def set_backtrace(bt)
    def TypeError.===(e) = true
    super
  end
end
def sneaky; begin; yield; rescue TypeError; :caught_by_set_backtrace; end; end
5.times { begin; sneaky { boom }; rescue ArgumentError; end }
$log << (sneaky { raise Sneaky } rescue :passed)
$log.join(""|"")
";
            const string expected = "[ArgumentError, \":3:in 'Object#boom',:4:in 'Object#nm',:4:in 'Object#nm',:4:in 'Object#nm',:4:in 'Object#nm',:24:in 'block in <main>',:23:in 'Integer#times',:23:in '<main>'\", ArgumentError]|side|ArgumentError|[IOError, ArgumentError]|two|[1, 2]|ArgumentError|ArgumentError|[\"other\", ArgumentError]|ArgumentError|ArgumentError|tag|Exception|thrown|[ArgumentError, \":3:in 'Object#boom',:4:in 'Object#nm',:4:in 'Object#nm',:4:in 'Object#nm',:4:in 'Object#nm',:24:in 'block in <main>',:23:in 'Integer#times',:23:in '<main>'\", ArgumentError]|side|ArgumentError|[IOError, ArgumentError]|two|[1, 2]|ArgumentError|ArgumentError|[\"other\", ArgumentError]|ArgumentError|ArgumentError|tag|Exception|thrown|[ArgumentError, \":3:in 'Object#boom',:4:in 'Object#nm',:4:in 'Object#nm',:4:in 'Object#nm',:4:in 'Object#nm',:24:in 'block in <main>',:23:in 'Integer#times',:23:in '<main>'\", ArgumentError]|side|ArgumentError|[IOError, ArgumentError]|two|[1, 2]|ArgumentError|ArgumentError|[\"other\", ArgumentError]|ArgumentError|ArgumentError|tag|Exception|thrown|[ArgumentError, \":3:in 'Object#boom',:4:in 'Object#nm',:4:in 'Object#nm',:4:in 'Object#nm',:4:in 'Object#nm',:24:in 'block in <main>',:23:in 'Integer#times',:23:in '<main>'\", ArgumentError]|side|ArgumentError|[IOError, ArgumentError]|two|[1, 2]|ArgumentError|ArgumentError|[\"other\", ArgumentError]|ArgumentError|ArgumentError|tag|Exception|thrown|[ArgumentError, \":3:in 'Object#boom',:4:in 'Object#nm',:4:in 'Object#nm',:4:in 'Object#nm',:4:in 'Object#nm',:24:in 'block in <main>',:23:in 'Integer#times',:23:in '<main>'\", ArgumentError]|side|ArgumentError|[IOError, ArgumentError]|two|[1, 2]|ArgumentError|ArgumentError|[\"other\", ArgumentError]|ArgumentError|ArgumentError|tag|Exception|thrown|later|kk|caught_after_ensure|caught_by_set_backtrace";

            var interpreting = Ruby.CreateEngine((setup) => { setup.Options["CompilationThreshold"] = Int32.MaxValue; });
            AreEqual(interpreting.CreateScriptSourceFromString(script, "rf1.rb", SourceCodeKind.File).Execute().ToString(), expected);
            AreEqual(Engine.CreateScriptSourceFromString(script, "rf2.rb", SourceCodeKind.File).Execute().ToString(), expected);
        }

        /// <summary>
        /// An exception that goes through a rescue frame unmatched (RubyOps.RescuePassMayTest) gets the cause CanRescue would have
        /// given it there: the exception an ensure clause in that frame ran for, when C# threw the new one from the clause (Kernel#raise
        /// sets a cause itself). Through a Thread#join too. The expected output is CRuby 4.0's; run interpreted and compiled.
        /// </summary>
        public void Exception_RescueFilterCause1() {
            const string script = @"def f1
  begin
    begin; raise ""first""; ensure; 1/0; end
  rescue TypeError
  end
end
def f2; begin; begin; raise ""second""; ensure; nil.zork; end; rescue TypeError; end; end
def f3; begin; begin; raise ""third""; ensure; [].fetch(5); end; rescue TypeError; end; end
def g(m) = (begin; send(m); rescue => e; [e.class, e.cause&.message]; end)
r = 50.times.map { [g(:f1), g(:f2), g(:f3)] }
t = Thread.new { f1 }
t.report_on_exception = false
x = begin; t.join; rescue => e; [e.class, e.cause&.message]; end
[r.first, r.last, x].inspect";
            const string expected = "[[[ZeroDivisionError, \"first\"], [NoMethodError, \"second\"], [IndexError, \"third\"]], [[ZeroDivisionError, \"first\"], [NoMethodError, \"second\"], [IndexError, \"third\"]], [ZeroDivisionError, \"first\"]]";

            var interpreting = Ruby.CreateEngine((setup) => { setup.Options["CompilationThreshold"] = Int32.MaxValue; });
            AreEqual(interpreting.CreateScriptSourceFromString(script, "cause1.rb", SourceCodeKind.File).Execute().ToString(), expected);
            AreEqual(Engine.CreateScriptSourceFromString(script, "cause2.rb", SourceCodeKind.File).Execute().ToString(), expected);
        }

        /// <summary>
        /// An ensure clause left by a jump - return, throw, a break from a block it yields to, next, break - ends the exception
        /// it ran for, and $! is again what it was before the begin (MRI). It stayed set, and became the cause of the next
        /// exception raised. Interpreted and compiled; expected output is CRuby 4.0's.
        /// </summary>
        public void Exception_EnsureJumpCurrentException1() {
            const string script = @"def ret; begin; raise ""a""; ensure; return 1; end; end
def thr; catch(:t) { begin; raise ""b""; ensure; throw :t, 2; end }; end
def yld; begin; raise ""c""; ensure; yield; end; end
def brk; yld { break 3 }; end
def nxt; [1].map { begin; raise ""d""; ensure; next 4; end }; end
def lp; i = 0; while i < 1; i += 1; begin; raise ""e""; ensure; break; end; end; 5; end
def in_ensure; begin; raise ""f""; ensure; return $!.message; end; end
def nest; begin; begin; raise ""g""; ensure; return 6; end; ensure; $log << $!.inspect; end; end
def resc; begin; raise ""h""; rescue; raise ""i""; ensure; return $!.message; end; end
$log = []
r = []
4.times do
  [:ret, :thr, :brk, :nxt, :lp, :in_ensure, :nest, :resc].each { |m| r << [m, send(m), $!] }
  r << (begin; raise ""later""; rescue => e; e.cause; end)
  begin
    raise ""outer""
  rescue
    r << [ret, $!.message]
  end
end
[r.uniq, $log.uniq].inspect";
            const string expected = @"[[[:ret, 1, nil], [:thr, 2, nil], [:brk, 3, nil], [:nxt, [4], nil], [:lp, 5, nil], [:in_ensure, ""f"", nil], [:nest, 6, nil], [:resc, ""i"", nil], nil, [1, ""outer""]], [""nil""]]";

            var interpreting = Ruby.CreateEngine((setup) => { setup.Options["CompilationThreshold"] = Int32.MaxValue; });
            AreEqual(interpreting.CreateScriptSourceFromString(script, "ensure_jump1.rb", SourceCodeKind.File).Execute().ToString(), expected);
            AreEqual(Engine.CreateScriptSourceFromString(script, "ensure_jump2.rb", SourceCodeKind.File).Execute().ToString(), expected);
        }

        /// <summary>
        /// An exception C# throws in a rescue clause (`1/0', a bad class in the list, `nil.foo'), a rescue modifier or an ensure
        /// clause has the exception being handled as its cause, as one Kernel#raise raises there has; it had none (the clause's
        /// finally block restored $! before the handler that caught the new exception set the cause). Also through a class
        /// with a #new of its own. Interpreted and compiled; expected output is CRuby 4.0's.
        /// </summary>
        public void Exception_CauseRaisedInClause1() {
            const string script = @"def zero; begin; raise ""a""; rescue; 1/0; end; end
def bad_class; begin; raise ""b""; rescue 42; end; end
def no_method; begin; raise ""c""; rescue => e; e.zork; end; end
def else_clause; begin; 1; rescue; else; [].fetch(1); end; end
def ensure_clause; begin; raise ""d""; ensure; Integer(""x""); end; end
def modifier; (raise ""e"" rescue 1/0); end
class Wrapped < StandardError; def self.new(*) = super(""wrapped""); end
def custom_new; begin; raise ""f""; rescue; raise Wrapped; end; end
def g(m) = (begin; send(m); rescue => e; [e.class, e.cause&.message]; end)
r = 4.times.map { [:zero, :bad_class, :no_method, :else_clause, :ensure_clause, :modifier, :custom_new].map { |m| g(m) } }
r.uniq.inspect";
            const string expected = @"[[[ZeroDivisionError, ""a""], [TypeError, ""b""], [NoMethodError, ""c""], [IndexError, nil], [ArgumentError, ""d""], [ZeroDivisionError, ""e""], [Wrapped, ""f""]]]";

            var interpreting = Ruby.CreateEngine((setup) => { setup.Options["CompilationThreshold"] = Int32.MaxValue; });
            AreEqual(interpreting.CreateScriptSourceFromString(script, "cause_clause1.rb", SourceCodeKind.File).Execute().ToString(), expected);
            AreEqual(Engine.CreateScriptSourceFromString(script, "cause_clause2.rb", SourceCodeKind.File).Execute().ToString(), expected);
        }

        /// <summary>
        /// A refinement of `===' active where a rescue clause is written is what the clause calls (MRI's check_match), whether
        /// the rescue filter takes the exception or not; a clause written before the `using' does not see it.
        /// </summary>
        public void Exception_RescueRefinedCaseEquality1() {
            TestOutput(@"
class E1 < StandardError; end
def before_using
  raise E1
rescue ArgumentError
  :wrong
rescue E1
  :plain
end
module Loud
  refine Module do
    def ===(o)
      $log << ""#{self}===""
      super
    end
  end
end
$log = []
using Loud
def after_using
  raise E1
rescue ArgumentError, *[TypeError]
  :wrong
rescue E1 => e
  e.class
end
p before_using, $log
$log.clear
p after_using, $log
$log.clear
begin
  raise ""x""
rescue
  p $log
end
module Yes
  refine E1.singleton_class do
    def ===(o) = o.message == ""match me""
  end
end
using Yes
def m2
  raise ""match me""
rescue E1
  :caught_by_refinement
end
p 3.times.map { m2 }
", @"
:plain
[]
E1
[""ArgumentError==="", ""TypeError==="", ""E1===""]
[""StandardError===""]
[:caught_by_refinement, :caught_by_refinement, :caught_by_refinement]
");
        }
    }
}
