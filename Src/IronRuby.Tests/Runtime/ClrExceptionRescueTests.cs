/* ****************************************************************************
 *
 * CLR exceptions that derive from System.Exception directly are StandardErrors
 * to Ruby, while System::Exception (Ruby's Exception) and Ruby's own exception
 * classes keep their MRI places in the hierarchy.
 *
 * This source code is subject to terms and conditions of the Apache License,
 * Version 2.0. A copy of the license can be found in the License.html file at
 * the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Threading.Tasks;
using IronRuby.Builtins;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;

namespace IronRuby.Tests {
    public partial class Tests {
        public class RescueAppException : Exception {
            public RescueAppException() : base("app default") { }
            public RescueAppException(string message) : base(message) { }
        }

        public class RescueDerivedAppException : RescueAppException {
            public RescueDerivedAppException(string message) : base(message) { }
        }

        public class RescueLegacyException : ApplicationException {
            public RescueLegacyException(string message) : base(message) { }
        }

        public class RescueGenericException<T> : Exception {
            public RescueGenericException(string message) : base(message) { }
        }

        public static class RescueThrower {
            public static void ThrowApp() { throw new RescueAppException("app failed"); }
            public static void ThrowDerived() { throw new RescueDerivedAppException("derived failed"); }
            public static void ThrowLegacy() { throw new RescueLegacyException("legacy failed"); }
            public static void ThrowGeneric() { throw new RescueGenericException<int>("generic failed"); }
            public static void ThrowAggregate() { throw new AggregateException("aggregate failed", new InvalidOperationException("inner")); }
            public static void ThrowPlain() { throw new Exception("plain failed"); }
            public static void ThrowNotSupported() { throw new NotSupportedException("not supported"); }
            public static void Call(Action action) { action(); }
            public static void WaitTask() { Task.Run(() => { throw new RescueAppException("in task"); }).Wait(); }
            public static void InvokeReflected() { typeof(RescueThrower).GetMethod("ThrowApp").Invoke(null, null); }
            public static void RunInTask(Action action) { Task.Run(action).Wait(); }
            public static void RunWrapped(Action action) {
                try { action(); } catch (Exception e) { throw new AggregateException(e); }
            }
        }

        private void DefineRescueFixtures() {
            Context.ObjectClass.SetConstant("AppError", Context.GetClass(typeof(RescueAppException)));
            Context.ObjectClass.SetConstant("DerivedAppError", Context.GetClass(typeof(RescueDerivedAppException)));
            Context.ObjectClass.SetConstant("LegacyError", Context.GetClass(typeof(RescueLegacyException)));
            Context.ObjectClass.SetConstant("T", Context.GetClass(typeof(RescueThrower)));
        }

        /// <summary>
        /// A bare rescue, rescue StandardError and the rescue modifier catch the exceptions a C# library throws that derive
        /// from System.Exception directly (directly or through ApplicationException), as they catch SystemExceptions.
        /// </summary>
        public void ClrExceptionRescue1() {
            DefineRescueFixtures();
            TestOutput(@"
[:throw_app, :throw_derived, :throw_legacy, :throw_generic, :throw_aggregate, :throw_not_supported].each do |m|
  begin
    T.send(m)
  rescue => e
    puts ""bare #{e.class}: #{e.message}""
  end
end

begin
  T.throw_app
rescue StandardError => e
  puts ""StandardError #{e.class}""
end

p((T.throw_aggregate rescue $!.class))
x = T.throw_legacy rescue :modifier
p x

def m
  T.throw_derived
rescue
  :method_rescue
end
p m
", @"
bare IronRuby::Tests::Tests::RescueAppException: app failed
bare IronRuby::Tests::Tests::RescueDerivedAppException: derived failed
bare IronRuby::Tests::Tests::RescueLegacyException: legacy failed
bare IronRuby::Tests::Tests::RescueGenericException[Integer]: generic failed
bare System::AggregateException: aggregate failed (inner)
bare System::NotSupportedException: not supported
StandardError IronRuby::Tests::Tests::RescueAppException
System::AggregateException
:modifier
:method_rescue
");
        }

        /// <summary>
        /// is_a?, ===, ancestors and superclass agree with rescue.
        /// </summary>
        public void ClrExceptionRescue2() {
            DefineRescueFixtures();
            TestOutput(@"
p AppError.superclass, DerivedAppError.superclass, LegacyError.superclass
p System::ApplicationException.superclass, System::AggregateException.superclass
p AppError.ancestors.grep(Class)
p AppError < StandardError, DerivedAppError < StandardError, System::AggregateException <= StandardError
e = AppError.new('x')
p e.is_a?(StandardError), StandardError === e, e.is_a?(Exception)
p System::SystemException === e, System::SystemException.to_clr_type.IsInstanceOfType(e)
p StandardError.superclass, Exception.superclass, System::Exception.equal?(Exception)
p System::NotSupportedException.ancestors.grep(Class).take(3)
", @"
StandardError
IronRuby::Tests::Tests::RescueAppException
System::ApplicationException
StandardError
StandardError
[IronRuby::Tests::Tests::RescueAppException, StandardError, Exception, Object, BasicObject]
true
true
true
true
true
true
true
false
Exception
Object
true
[System::NotSupportedException, StandardError, Exception]
");
            Assert(Context.GetClass(typeof(RescueAppException)).SuperClass == Context.StandardErrorClass);
            Assert(Context.GetClass(typeof(RescueGenericException<string>)).SuperClass == Context.StandardErrorClass);
            Assert(Context.GetClass(typeof(AggregateException)).SuperClass == Context.StandardErrorClass);
            Assert(Context.GetClass(typeof(Exception)) == Context.ExceptionClass);
        }

        /// <summary>
        /// CLR exceptions raised from Ruby, Ruby subclasses of them and Ruby exceptions that pass through C# code.
        /// </summary>
        public void ClrExceptionRescue3() {
            DefineRescueFixtures();
            TestOutput(@"
class E2 < AppError; end
class E3 < E2
  def message = 'e3 ' + super
end

[-> { raise AppError.new('new') }, -> { raise AppError, 'class' }, -> { raise AppError },
 -> { raise E2, 'e2' }, -> { raise E3.new('x') }, -> { T.call(-> { raise E2, 'via C#' }) }].each do |l|
  begin
    l.()
  rescue => e
    puts ""#{e.class}: #{e.message}""
  end
end
p E2.ancestors.grep(Class).take(4), E3.new.is_a?(StandardError)
", @"
IronRuby::Tests::Tests::RescueAppException: new
IronRuby::Tests::Tests::RescueAppException: class
IronRuby::Tests::Tests::RescueAppException: app default
E2: e2
E3: e3 x
E2: via C#
[E2, IronRuby::Tests::Tests::RescueAppException, StandardError, Exception]
true
");
        }

        /// <summary>
        /// MRI semantics stay for Ruby's Exception and for the exception classes that are not StandardErrors: a bare rescue
        /// and the modifier let them through, rescue Exception catches them. System::Exception is Ruby's Exception, also
        /// when a C# library throws it.
        /// </summary>
        public void ClrExceptionRescue4() {
            DefineRescueFixtures();
            TestOutput(@"
class RubyException < Exception; end

def bare
  yield
  :none
rescue => e
  ""bare #{e.class}""
end

def modifier
  yield rescue ""modifier #{$!.class}""
end

[-> { raise Exception, 'x' }, -> { raise System::Exception, 'm' }, -> { raise System::Exception.new('m') },
 -> { T.throw_plain }, -> { raise RubyException }, -> { raise ScriptError }, -> { raise NotImplementedError },
 -> { raise LoadError }, -> { raise SecurityError }, -> { raise NoMemoryError }, -> { raise SystemStackError },
 -> { raise Interrupt }, -> { raise SystemExit }, -> { T.call(-> { raise ScriptError }) }].each do |l|
  r = begin
    bare(&l)
  rescue Exception => e
    ""Exception #{e.class}""
  end
  r2 = begin
    modifier(&l)
  rescue Exception => e
    ""Exception #{e.class}""
  end
  puts ""#{r} | #{r2}""
end
p RubyException.ancestors.grep(Class), RubyException.new.is_a?(StandardError)
p [ScriptError, SignalException, SystemExit, NoMemoryError, SecurityError, SystemStackError].map(&:superclass).uniq
p Interrupt.superclass, Exception.new.is_a?(StandardError)
", @"
Exception Exception | Exception Exception
Exception Exception | Exception Exception
Exception Exception | Exception Exception
Exception Exception | Exception Exception
Exception RubyException | Exception RubyException
Exception ScriptError | Exception ScriptError
Exception NotImplementedError | Exception NotImplementedError
Exception LoadError | Exception LoadError
Exception SecurityError | Exception SecurityError
Exception NoMemoryError | Exception NoMemoryError
Exception SystemStackError | Exception SystemStackError
Exception Interrupt | Exception Interrupt
Exception SystemExit | Exception SystemExit
Exception ScriptError | Exception ScriptError
[RubyException, Exception, Object, BasicObject]
false
[Exception]
SignalException
false
");
        }

        /// <summary>
        /// Exception's subclasses that are not errors keep it as their superclass: the CLR types emitted for Ruby subclasses
        /// of Exception and IronRuby's control flow. A struct's recursive #hash unwinds through user #hash methods like MRI's
        /// throw, unseen by their rescue clauses.
        /// </summary>
        public void ClrExceptionRescue5() {
            TestOutput(@"
class RubyException < Exception; end
p RubyException.new.GetType.to_class.superclass

S = Struct.new(:a)
o = Object.new
def o.hash
  $s.hash
rescue Exception => e
  puts ""rescued #{e.class}""
  0
end
$s = S.new(o)
p $s.hash.is_a?(Integer)
", @"
Exception
true
");
            Assert(!Context.GetClass(typeof(BlockUnwinder)).IsSubclassOf(Context.StandardErrorClass));
            Assert(Context.GetClass(typeof(StackUnwinder)).SuperClass == Context.ExceptionClass);
        }

        /// <summary>
        /// The wrappers a C# exception usually arrives in: a faulted task's AggregateException, reflection's
        /// TargetInvocationException, Thread#join. A wrapper is a StandardError whatever it carries, also a SystemExit or
        /// a throw from a Ruby callback: the class decides, as for any exception. A hosting API's syntax error is a CLR
        /// exception too; eval's is Ruby's SyntaxError, a ScriptError.
        /// </summary>
        public void ClrExceptionRescue6() {
            DefineRescueFixtures();
            TestOutput(@"
[:wait_task, :invoke_reflected].each do |m|
  begin
    T.send(m)
  rescue => e
    puts ""bare #{e.class}: #{e.InnerException.class}: #{e.InnerException.message}""
  end
end

t = Thread.new { T.throw_app }
t.report_on_exception = false
begin
  t.join
rescue => e
  puts ""join #{e.class}: #{e.message}""
end

[-> { T.run_in_task(-> { exit 2 }) }, -> { T.run_in_task(-> { raise Interrupt }) },
 -> { T.run_wrapped(-> { throw :x }) }].each do |l|
  r = begin
    catch(:x) { l.(); :none }
  rescue => e
    ""bare #{e.class}: #{e.InnerException.class.name.split('::').last}""
  end
  puts r
end

[-> { IronRuby.create_engine.execute('1+') }, -> { eval('1+') }].each do |l|
  r = begin
    l.()
  rescue => e
    ""bare #{e.class}""
  rescue Exception => e
    ""Exception #{e.class}""
  end
  puts r
end
", @"
bare System::AggregateException: IronRuby::Tests::Tests::RescueAppException: in task
bare System::Reflection::TargetInvocationException: IronRuby::Tests::Tests::RescueAppException: app failed
join IronRuby::Tests::Tests::RescueAppException: app failed
bare System::AggregateException: SystemExit
bare System::AggregateException: Interrupt
bare System::AggregateException: ThrowCatchUnwinder
bare Microsoft::Scripting::SyntaxErrorException
Exception SyntaxError
");
        }

        public class RescueLibraryException : Exception {
            public RescueLibraryException() : base("library") { }
        }

        public class RescueTouchedLibraryException : Exception {
            public RescueTouchedLibraryException() : base("touched") { }
        }

        public class RescueExceptionLibraryInitializer : LibraryInitializer {
            protected override void LoadModules() {
                // the extension defines the type's class: nothing has asked for it yet
                RubyClass libError = ExtendClass(typeof(RescueLibraryException), 0, null, DefineLibraryTag, null, null, RubyModule.EmptyArray);
                Context.ObjectClass.SetConstant("LibError", libError);
                ExtendClass(typeof(RescueTouchedLibraryException), 0, null, DefineLibraryTag, null, null, RubyModule.EmptyArray);
            }

            private void DefineLibraryTag(RubyModule/*!*/ module) {
                DefineLibraryMethod(module, "library_tag", 0x9, new[] {
                    LibraryOverload.Reflect(new Func<object, string>(LibraryTag)),
                });
            }

            public static string LibraryTag(object self) {
                return "tagged";
            }
        }

        /// <summary>
        /// A library that extends a CLR exception (ExtendClass with no superclass) gives it the superclass Ruby would, whether
        /// the library loads before or after the type is first used.
        /// </summary>
        public void ClrExceptionRescue7() {
            Context.DefineGlobalVariable("lib_name", MutableString.CreateAscii(typeof(RescueExceptionLibraryInitializer).AssemblyQualifiedName));
            Context.ObjectClass.SetConstant("TouchedError", Context.GetClass(typeof(RescueTouchedLibraryException)));
            TestOutput(@"
require $lib_name
[LibError, TouchedError].each do |c|
  begin
    raise c.new
  rescue => e
    puts ""#{c.superclass}: #{e.message} #{e.library_tag}""
  end
end
", @"
StandardError: library tagged
StandardError: touched tagged
");
        }
    }
}
