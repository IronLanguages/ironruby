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

using System.Collections;
using System.Collections.Generic;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;

namespace IronRuby.Builtins {
    /// <summary>
    /// Mixin. Implements print, puts, putc methods.
    /// </summary>
    [RubyModule("Print", DefineIn = typeof(IronRubyOps))]
    public static class PrintOps {
        [RubyMethod("<<")]
        public static object/*!*/ Output(BinaryOpStorage/*!*/ writeStorage, object self, object value) {
            Protocols.Write(writeStorage, self, value);
            return self;
        }

        [RubyMethod("print")]
        public static void Print(BinaryOpStorage/*!*/ writeStorage, RubyScope/*!*/ scope, object self) {
            Print(writeStorage, self, scope.GetInnerMostClosureScope().LastInputLine);
        }

        // $, goes between the arguments and $\ after the last one; writing $\ after every
        // argument made `print a, b` come out as "a$\b$\".
        [RubyMethod("print")]
        public static void Print(BinaryOpStorage/*!*/ writeStorage, object self, params object[]/*!*/ args) {
            MutableString fieldSeparator = writeStorage.Context.ItemSeparator;
            if (fieldSeparator != null && args.Length > 1) {
                writeStorage.Context.ReportDeprecationWarning("$, is set to non-nil value");
            }
            for (int i = 0; i < args.Length; i++) {
                if (i > 0 && fieldSeparator != null) {
                    Protocols.Write(writeStorage, self, fieldSeparator);
                }
                // nil prints as an empty string, like every other argument's to_s
                Protocols.Write(writeStorage, self, args[i] ?? MutableString.CreateEmpty());
            }

            MutableString recordSeparator = writeStorage.Context.OutputSeparator;
            if (recordSeparator != null) {
                Protocols.Write(writeStorage, self, recordSeparator);
            }
        }

        [RubyMethod("print")]
        public static void Print(BinaryOpStorage/*!*/ writeStorage, object self, object value) {
            Protocols.Write(writeStorage, self, value ?? MutableString.CreateEmpty());

            MutableString delimiter = writeStorage.Context.OutputSeparator;
            if (delimiter != null) {
                Protocols.Write(writeStorage, self, delimiter);
            }
        }

        [RubyMethod("putc")]
        public static MutableString/*!*/ Putc(BinaryOpStorage/*!*/ writeStorage, object self, [NotNull]MutableString/*!*/ val) {
            // writes the first character of the string - the whole of it when multibyte;
            // an empty string writes nothing
            if (!val.IsEmpty) {
                Protocols.Write(writeStorage, self, val.GetSlice(0, 1));
            }
            return val;
        }

        // MRI hands back the argument it was given, not the Integer it made of it.
        [RubyMethod("putc")]
        public static object Putc(ConversionStorage<int>/*!*/ fixnumCast, BinaryOpStorage/*!*/ writeStorage, object self, object c) {
            if (c == null) {
                throw RubyExceptions.CreateImplicitConversionError("nil", "Integer");
            }
            int value = Protocols.CastToFixnum(fixnumCast, c);
            MutableString str = MutableString.CreateBinary(1).Append(unchecked((byte)value));
            Protocols.Write(writeStorage, self, str);
            return c;
        }

        public static MutableString/*!*/ ToPrintedString(ConversionStorage<MutableString>/*!*/ tosConversion, object obj) {
            if (obj == null) {
                return MutableString.CreateAscii("nil");
            } else {
                return Protocols.ConvertToString(tosConversion, obj);
            }
        }

        [RubyMethod("puts")]
        public static void PutsEmptyLine(BinaryOpStorage/*!*/ writeStorage, object self) {
            Protocols.Write(writeStorage, self, MutableString.CreateAscii("\n"));
        }

        [RubyMethod("puts")]
        public static void Puts(BinaryOpStorage/*!*/ writeStorage, object self, [NotNull]MutableString/*!*/ str) {
            Protocols.Write(writeStorage, self, str);

            if (!str.EndsWith('\n')) {
                PutsEmptyLine(writeStorage, self);
            }
        }

        [RubyMethod("puts")]
        public static void Puts(BinaryOpStorage/*!*/ writeStorage, ConversionStorage<MutableString>/*!*/ tosConversion,
            ConversionStorage<IList>/*!*/ tryToAry, object self, [NotNull]object/*!*/ val) {

            if (val == null) {
                // `puts nil` is an empty line; only Kernel#p and #inspect spell nil out.
                PutsEmptyLine(writeStorage, self);
                return;
            }

            IList list = Protocols.TryCastToArray(tryToAry, val);
            if (list == null) {
                Puts(writeStorage, self, ToPrintedString(tosConversion, val));
                return;
            }

            // MRI's rb_io_puts goes into an array with rb_exec_recursive on the object it was given, not on
            // what its to_ary returned, and writes "[...]" when it meets that object again inside itself: an
            // object whose to_ary answers a new [self] each time used to be followed forever. The descent
            // keeps a stack of its own, so a deeply nested array does not take the machine stack.
            var path = new HashSet<object>(ReferenceEqualityComparer.Instance) { val };
            var stack = new Stack<(IList List, int Index, object Owner)>();
            stack.Push((list, 0, val));
            while (stack.Count > 0) {
                var (current, index, owner) = stack.Pop();
                if (index >= current.Count) {
                    path.Remove(owner);
                    continue;
                }
                stack.Push((current, index + 1, owner));

                object item = current[index];
                if (item == null) {
                    PutsEmptyLine(writeStorage, self);
                    continue;
                }
                IList nested = item is MutableString ? null : Protocols.TryCastToArray(tryToAry, item);
                if (nested == null) {
                    Puts(writeStorage, self, ToPrintedString(tosConversion, item));
                } else if (path.Add(item)) {
                    stack.Push((nested, 0, item));
                } else {
                    Puts(writeStorage, self, MutableString.CreateAscii("[...]"));
                }
            }
        }

        [RubyMethod("puts")]
        public static void Puts(BinaryOpStorage/*!*/ writeStorage, ConversionStorage<MutableString>/*!*/ tosConversion,
            ConversionStorage<IList>/*!*/ tryToAry, object self, params object[]/*!*/ vals) {

            for (int i = 0; i < vals.Length; i++) {
                Puts(writeStorage, tosConversion, tryToAry, self, vals[i]);
            }
        }

        [RubyMethod("printf")]
        public static void PrintFormatted(
            StringFormatterSiteStorage/*!*/ storage,
            ConversionStorage<MutableString>/*!*/ stringCast,
            BinaryOpStorage/*!*/ writeStorage,
            object/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ format, params object[]/*!*/ args) {

            KernelOps.PrintFormatted(storage, stringCast, writeStorage, null, self, format, args);
        }

        internal static void ReportWarning(BinaryOpStorage/*!*/ writeStorage, ConversionStorage<MutableString>/*!*/ tosConversion, object message) {
            if (writeStorage.Context.Verbose != null) {
                // through the runtime's warning sink, so it carries "file:line: warning: " like rb_warn
                writeStorage.Context.ReportWarning(PrintOps.ToPrintedString(tosConversion, message).ToString());
            }
        }

    }
}
