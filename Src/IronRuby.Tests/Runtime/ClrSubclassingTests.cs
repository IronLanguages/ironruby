/* ****************************************************************************
 *
 * Ruby classes derived from CLR classes: super in initialize runs the CLR base
 * class's constructor.
 *
 * This source code is subject to terms and conditions of the Apache License,
 * Version 2.0. A copy of the license can be found in the License.html file at
 * the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;

namespace IronRuby.Tests {
    public partial class Tests {
        public class SubclassingPerson {
            public string Name { get; private set; }
            public string Log = "";

            public SubclassingPerson(string name) {
                Name = name;
                Log += "ctor(" + name + ")";
                Greet();
            }

            public SubclassingPerson(string first, string last) : this(first + " " + last) {
            }

            // called from the constructor: a Ruby override must already be reachable
            public virtual void Greet() {
                Log += " greet";
            }
        }

        public class SubclassingWidget {
            public string Title { get; set; }
            public int Width { get; set; }
            public int ConstructorRuns;

            public SubclassingWidget() { ConstructorRuns++; }
            public SubclassingWidget(string title) { Title = title; ConstructorRuns++; }
        }

        public abstract class SubclassingShape {
            public string Kind { get; private set; }
            protected SubclassingShape(string kind) { Kind = kind; }
            public abstract int Corners { get; }
        }

        /// <summary>
        /// super in initialize of a Ruby class derived from a CLR class runs the matching CLR base constructor on the
        /// new instance, also when the base class has no parameterless constructor.
        /// </summary>
        public void ClrSuperConstructor1() {
            Context.ObjectClass.SetConstant("Person", Context.GetClass(typeof(SubclassingPerson)));
            TestOutput(@"
class Doctor < Person
  def initialize(name)
    super(""Dr. "" + name)
    @title = 'MD'
  end

  def greet
    self.Log += ' ruby greet'
  end

  def title; @title; end
end

d = Doctor.new('Who')
puts d.name
puts d.log
puts d.title

class Doctor2 < Person
  def initialize(first, last)
    super
  end
end
puts Doctor2.new('Jane', 'Roe').name

class Doctor3 < Doctor
  def initialize
    super('Nested')
  end
end
puts Doctor3.new.name

class NoSuper < Person
  def initialize(name); @name = name; end
end

begin
  NoSuper.new('x')
rescue TypeError => e
  puts e.message
end

class WrongArgs < Person
  def initialize; super(1, 2, 3); end
end

begin
  WrongArgs.new
rescue ArgumentError, TypeError => e
  puts e.class
end
", @"
Dr. Who
ctor(Dr. Who) ruby greet
MD
Jane Roe
Dr. Nested
NoSuper.new: initialize did not call super, and SubclassingPerson has no parameterless constructor to construct the instance with
ArgumentError
");
        }

        /// <summary>
        /// A base class with a parameterless constructor is constructed with it when allocated, as before; super with
        /// arguments then runs the matching constructor, super without arguments does nothing more.
        /// </summary>
        public void ClrSuperConstructor2() {
            Context.ObjectClass.SetConstant("Widget", Context.GetClass(typeof(SubclassingWidget)));
            Context.ObjectClass.SetConstant("Shape", Context.GetClass(typeof(SubclassingShape)));
            TestOutput(@"
class Titled < Widget
  def initialize(title); super(title); end
end
w = Titled.new('t')
p [w.title.to_s, w.ConstructorRuns]

class Plain < Widget
  def initialize; super(); self.width = 3; end
end
w = Plain.new
p [w.width, w.ConstructorRuns]

class NoSuperCall < Widget
  def initialize; self.width = 4; end
end
p NoSuperCall.new.width

class Keywords < Widget
  def initialize(**options); super(**options); end
end
w = Keywords.new(title: 'k', width: 5)
p [w.title.to_s, w.width]

class Square < Shape
  def initialize; super('square'); end
  def corners; 4; end
end
p [Square.new.kind.to_s, Square.new.corners]

class IntList < System::Collections::Generic::List[Integer]
  def initialize(*args); super; end
end
p [IntList.new([1, 2]).count, IntList.new(10).capacity, IntList.new.count]
", @"
[""t"", 2]
[3, 1]
4
[""k"", 5]
[""square"", 4]
[2, 10, 0]
");
        }
    }
}
