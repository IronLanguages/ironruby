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
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using IronRuby.Runtime;
using Microsoft.Scripting;
using System.Numerics;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using Microsoft.Scripting.Generation;
using IronRuby.Runtime.Calls;
using System.Globalization;
using System.Text;

namespace IronRuby.Builtins {

    [RubyModule("Marshal")]
    public class RubyMarshal {

        public sealed class WriterSites : RubyCallSiteStorage {
            public WriterSites(RubyContext/*!*/ context) : base(context) { }

            private CallSite<Func<CallSite, object, object>> _marshalDump;
            private CallSite<Func<CallSite, object, int, object>> _dump;

            public CallSite<Func<CallSite, object, object>>/*!*/ MarshalDump {
                get { return RubyUtils.GetCallSite(ref _marshalDump, Context, "marshal_dump", 0); }
            }

            public CallSite<Func<CallSite, object, int, object>>/*!*/ Dump {
                get { return RubyUtils.GetCallSite(ref _dump, Context, "_dump", 1); }
            }

            private CallSite<Func<CallSite, object, object>> _binmode;

            public CallSite<Func<CallSite, object, object>>/*!*/ Binmode {
                get { return RubyUtils.GetCallSite(ref _binmode, Context, "binmode", 0); }
            }

            private RespondToStorage _respondTo;

            public RespondToStorage/*!*/ RespondTo {
                get { return _respondTo ?? (_respondTo = new RespondToStorage(Context)); }
            }
        }

        public sealed class ReaderSites : RubyCallSiteStorage {
            public ReaderSites(RubyContext/*!*/ context) : base(context) { }

            private CallSite<Func<CallSite, object, object, object>> _marshalLoad;
            private CallSite<Func<CallSite, object, MutableString, object>> _load;
            public CallSite<Func<CallSite, object, object, object>> _procCall;

            public CallSite<Func<CallSite, object, object, object>>/*!*/ MarshalLoad {
                get { return RubyUtils.GetCallSite(ref _marshalLoad, Context, "marshal_load", 1); }
            }

            private CallSite<Func<CallSite, object, object, object>> _compare;

            public CallSite<Func<CallSite, object, object, object>>/*!*/ Compare {
                get { return RubyUtils.GetCallSite(ref _compare, Context, "<=>", 1); }
            }

            public CallSite<Func<CallSite, object, MutableString, object>>/*!*/ Load {
                get { return RubyUtils.GetCallSite(ref _load, Context, "_load", 1); }
            }

            // private in MRI's classes, so called as if from the object itself
            private CallSite<Func<CallSite, object, object, object>> _loadData;

            public CallSite<Func<CallSite, object, object, object>>/*!*/ LoadData {
                get { return RubyUtils.GetCallSite(ref _loadData, Context, "_load_data", RubyCallSignature.WithImplicitSelf(1)); }
            }

            public CallSite<Func<CallSite, object, object, object>>/*!*/ ProcCall {
                get { return RubyUtils.GetCallSite(ref _procCall, Context, "call", 1); }
            }

            private CallSite<Func<CallSite, object, object>> _binmode;

            public CallSite<Func<CallSite, object, object>>/*!*/ Binmode {
                get { return RubyUtils.GetCallSite(ref _binmode, Context, "binmode", 0); }
            }
        }


        #region Constants

        private static readonly MutableString _positiveInfinityString = MutableString.CreateAscii("inf").Freeze();
        private static readonly MutableString _negativeInfinityString = MutableString.CreateAscii("-inf").Freeze();
        private static readonly MutableString _nanString = MutableString.CreateAscii("nan").Freeze();

        // The two "pseudo instance variables" MRI uses to carry an object's encoding through the
        // marshal stream. ":E" is a shorthand for the two encodings that appear everywhere
        // (true => UTF-8, false => US-ASCII); everything else is spelled out by name under
        // ":encoding". ASCII-8BIT is represented by the absence of both.
        internal const string EncodingShortIVarName = "E";
        // MRI's ARG_ENCODING_NONE option bit in a regexp record (/n)
        internal const int NoEncodingFlag = 0x20;
        internal const string EncodingIVarName = "encoding";

        #endregion

        #region Encoding helpers

        /// <summary>
        /// The encoding MRI would record for <paramref name="obj"/>, or null if the object carries none
        /// (in which case no encoding instance variable is written and the object loads as ASCII-8BIT).
        /// </summary>
        internal static RubyEncoding GetMarshalEncoding(object obj) {
            var str = obj as MutableString;
            if (str != null) {
                return str.Encoding;
            }

            var regex = obj as RubyRegex;
            if (regex != null) {
                return regex.Encoding;
            }

            return null;
        }

        /// <summary>
        /// An 'o' record is instance variables and nothing else, so it can only carry an object
        /// whose whole state is instance variables. MRI refuses anything backed by C data - a
        /// StringIO, a Dir, a Method - unless its class defines _dump_data, and the classes here
        /// that are backed by a CLR object are the same set. The two exceptions are the two that
        /// Marshal writes as 'o' records itself: an exception, whose message and backtrace are
        /// written as pseudo instance variables, and a Range.
        /// </summary>
        internal static bool IsDumpableAsObject(RubyClass/*!*/ theClass) {
            for (RubyClass cls = theClass; cls != null; cls = cls.SuperClass) {
                // Reaching a root - Object, or BasicObject for a class that descends from it
                // directly - means nothing along the way was backed by a CLR object.
                if (cls.IsObjectClass || cls == cls.Context.BasicObjectClass) {
                    return true;
                }
                if (cls.IsRubyClass) {
                    continue;
                }

                Type type = cls.GetUnderlyingSystemType();
                return typeof(Exception).IsAssignableFrom(type) || typeof(Range).IsAssignableFrom(type);
            }
            return true;
        }

        internal static bool NeedsEncodingIVar(RubyEncoding encoding) {
            return encoding != null && encoding != RubyEncoding.Binary;
        }

        internal static void ForceMarshalEncoding(object obj, RubyEncoding/*!*/ encoding) {
            var str = obj as MutableString;
            if (str != null) {
                str.ForceEncoding(encoding);
                return;
            }

            var regex = obj as RubyRegex;
            if (regex != null) {
                // A regexp freezes the pattern it holds, so the encoding goes on a copy that
                // replaces it rather than on the pattern itself.
                var pattern = regex.Pattern.Clone();
                pattern.ForceEncoding(encoding);
                regex.Set(pattern, regex.Options);
            }
        }

        #endregion

        #region MarshalWriter

        internal class MarshalWriter {
            private readonly BinaryWriter/*!*/ _writer;
            private readonly RubyContext/*!*/ _context;
            private readonly WriterSites/*!*/ _sites;
            private int _recursionLimit;
            private readonly Dictionary<string/*!*/, int>/*!*/ _symbols;
            private readonly Dictionary<object, int>/*!*/ _objects;

            internal MarshalWriter(WriterSites/*!*/ sites, BinaryWriter/*!*/ writer, RubyContext/*!*/ context, int? limit) {
                Assert.NotNull(sites, writer, context);

                _sites = sites;
                _writer = writer;
                _context = context;
                _recursionLimit = (limit.HasValue ? limit.Value : -1);
                _symbols = new Dictionary<string, int>();
                _objects = new Dictionary<object, int>(ReferenceEqualityComparer<object>.Instance);
            }

            private void WritePreamble() {
                _writer.Write((byte)MAJOR_VERSION);
                _writer.Write((byte)MINOR_VERSION);
            }

            private void WriteBignumValue(BigInteger/*!*/ value) {
                char sign;
                if (value.Sign > 0) {
                    sign = '+';
                } else if (value.Sign < 0) {
                    sign = '-';
                } else {
                    sign = '0';
                }
                _writer.Write((byte)sign);
                uint[] bits = value.GetWords();
                int n = bits.Length * 2, mn = bits.Length - 1;
                bool truncate = false;
                if (bits.Length > 0 && (bits[mn] >> 16) == 0) {
                    n--;
                    truncate = true;
                }
                WriteInt32(n);
                for (int i = 0; i < bits.Length; i++) {
                    if (truncate && i == mn) {
                        _writer.Write(unchecked((ushort)(bits[i])));
                    } else {
                        _writer.Write(bits[i]);
                    }
                }
            }

            private void WriteBignum(BigInteger/*!*/ value) {
                _writer.Write((byte)'l');
                WriteBignumValue(value);
            }

            private void WriteInt32(int value) {
                if (value == 0) {
                    _writer.Write((byte)0);
                } else if (value > 0 && value < 123) {
                    _writer.Write((byte)(value + 5));
                } else if (value < 0 && value > -124) {
                    _writer.Write((sbyte)(value - 5));
                } else {
                    byte[] _buffer = new byte[5];
                    _buffer[1] = (byte)(value & 0xff);
                    _buffer[2] = (byte)((value >> 8) & 0xff);
                    _buffer[3] = (byte)((value >> 16) & 0xff);
                    _buffer[4] = (byte)((value >> 24) & 0xff);

                    int len = 4;
                    sbyte lenbyte;
                    if (value < 0) {
                        while (_buffer[len] == 0xff) {
                            len--;
                        }
                        lenbyte = (sbyte)-len;
                    } else {
                        while (_buffer[len] == 0x00) {
                            len--;
                        }
                        lenbyte = (sbyte)len;
                    }
                    _buffer[0] = unchecked((byte)lenbyte);

                    _writer.Write(_buffer, 0, len + 1);
                }
            }

            private void WriteFixnum(int value) {
                _writer.Write((byte)'i');
                WriteInt32(value);
            }

            private void WriteFloat(double value) {
                _writer.Write((byte)'f');
                WriteStringValue(FormatFloat(value), RubyEncoding.Binary);
            }


            private void WriteSubclassData(object/*!*/ obj, Type type) {
                RubyClass libClass = _context.GetClass(type);
                RubyClass theClass = _context.GetClassOf(obj);

                if (libClass != theClass && !(obj is RubyStruct)) {
                    _writer.Write((byte)'C');
                    WriteModuleName(theClass);
                }
            }

            private void WriteStringValue(MutableString/*!*/ value) {
                byte[] data = value.ToByteArray();
                WriteInt32(data.Length);
                _writer.Write(data);
            }

            private void WriteModuleName(RubyModule/*!*/ module) {
                TestForAnonymous(module);
                WriteSymbol(module.Name, module.Context.GetIdentifierEncoding());
            }

            private void WriteStringValue(string/*!*/ value, RubyEncoding/*!*/ encoding) {
                byte[] data = encoding.StrictEncoding.GetBytes(value);
                WriteInt32(data.Length);
                _writer.Write(data);
            }

            private void WriteString(MutableString/*!*/ value) {
                WriteSubclassData(value, typeof(MutableString));
                _writer.Write((byte)'"');
                WriteStringValue(value);
            }

            private void WriteRegex(RubyRegex/*!*/ value) {
                WriteSubclassData(value, typeof(RubyRegex));
                _writer.Write((byte)'/');
                WriteStringValue(value.Pattern);
                // MRI only stores the three matching flags plus its "encoding is fixed" bit, which
                // it sets for any regexp that can only match one encoding - whether that is because
                // the source is not ASCII only or because Regexp::FIXEDENCODING said so.
                int flags = (int)(value.Options & (RubyRegexOptions.IgnoreCase | RubyRegexOptions.Extended | RubyRegexOptions.Multiline));
                if (value.IsFixedEncoding) {
                    flags |= (int)RubyRegexOptions.FIXED;
                }
                if ((value.Options & RubyRegexOptions.FIXED) != 0) {
                    // /n: MRI's ARG_ENCODING_NONE bit
                    flags |= NoEncodingFlag;
                }
                _writer.Write((byte)flags);
            }

            private void WriteArray(RubyArray/*!*/ value) {
                WriteSubclassData(value, typeof(RubyArray));
                _writer.Write((byte)'[');
                WriteInt32(value.Count);
                foreach (object obj in value) {
                    WriteAnObject(obj);
                }
            }

            private void WriteHash(Hash/*!*/ value) {
                if (value.DefaultProc != null) {
                    throw RubyExceptions.CreateTypeError("can't dump hash with default proc");
                }

                WriteSubclassData(value, typeof(Hash));
                if (value.ComparesByIdentity) {
                    // MRI wraps a compare_by_identity hash in a "user class" record naming Hash,
                    // which is how the flag survives a round trip.
                    _writer.Write((byte)'C');
                    WriteSymbol("Hash", _context.GetIdentifierEncoding());
                }
                char typeFlag = (value.DefaultValue != null) ? '}' : '{';
                _writer.Write((byte)typeFlag);
                WriteInt32(value.Count);
                foreach (KeyValuePair<object, object> pair in value) {
                    WriteAnObject(pair.Key);
                    WriteAnObject(pair.Value);
                }
                if (value.DefaultValue != null) {
                    WriteAnObject(value.DefaultValue);
                }
            }

            private void WriteSymbol(string/*!*/ value, RubyEncoding/*!*/ encoding) {
                WriteSymbol(value, encoding.StrictEncoding.GetBytes(value), encoding);
            }

            private void WriteSymbol(RubySymbol/*!*/ symbol) {
                WriteSymbol(symbol.ToString(), symbol.String.ToByteArray(), symbol.Encoding);
            }

            private void WriteSymbol(string/*!*/ value, byte[]/*!*/ data, RubyEncoding/*!*/ encoding) {
                int position;
                if (_symbols.TryGetValue(value, out position)) {
                    _writer.Write((byte)';');
                    WriteInt32(position);
                } else {
                    // MRI only records an encoding for a symbol whose name is not ASCII only.
                    bool writeEncoding = NeedsEncodingIVar(encoding) && !IsAscii(data);
                    if (writeEncoding) {
                        _writer.Write((byte)'I');
                    }
                    position = _symbols.Count;
                    _symbols[value] = position;
                    _writer.Write((byte)':');
                    WriteInt32(data.Length);
                    _writer.Write(data);
                    if (writeEncoding) {
                        WriteInt32(1);
                        WriteEncodingIVar(encoding);
                    }
                }
            }

            private static bool IsAscii(byte[]/*!*/ data) {
                for (int i = 0; i < data.Length; i++) {
                    if (data[i] >= 0x80) {
                        return false;
                    }
                }
                return true;
            }

            private void WriteEncodingIVar(RubyEncoding/*!*/ encoding) {
                if (encoding == RubyEncoding.UTF8) {
                    WriteSymbol(EncodingShortIVarName, RubyEncoding.Binary);
                    WriteAnObject(true);
                } else if (encoding == RubyEncoding.Ascii) {
                    WriteSymbol(EncodingShortIVarName, RubyEncoding.Binary);
                    WriteAnObject(false);
                } else {
                    WriteSymbol(EncodingIVarName, RubyEncoding.Binary);
                    WriteAnObject(MutableString.CreateBinary(Encoding.UTF8.GetBytes(encoding.Name)));
                }
            }

            private void TestForAnonymous(RubyModule/*!*/ theModule) {
                if (theModule.Name == null) {
                    throw RubyExceptions.CreateTypeError("can't dump anonymous {0} {1}",
                        theModule.IsClass ? "class" : "module",
                        theModule.GetDisplayName(_context, false)
                    );
                }
            }

            private void WriteRange(Range/*!*/ range, string[]/*!*/ instanceNames) {
                WriteObject(range);
                WriteInt32(3 + instanceNames.Length);
                // Write the attributes that are implemented in C#. Any user-defined attributes (for subtypes of Range)
                // are appended afterwards. MRI writes them in the order excl, begin, end.
                WriteSymbol("excl", RubyEncoding.Binary);
                WriteAnObject(range.ExcludeEnd);
                WriteSymbol("begin", RubyEncoding.Binary);
                WriteAnObject(range.Begin);
                WriteSymbol("end", RubyEncoding.Binary);
                WriteAnObject(range.End);
                WriteIVarPairs(range, instanceNames);
            }

            private void WriteObject(object/*!*/ obj) {
                RubyClass theClass = _context.GetClassOf(obj);
                _writer.Write((byte)'o');
                TestForAnonymous(theClass);
                WriteModuleName(theClass);
            }

            private void RequireDumpableAsObject(RubyClass/*!*/ theClass) {
                if (!IsDumpableAsObject(theClass)) {
                    throw RubyExceptions.CreateTypeError("no _dump_data is defined for class {0}",
                        theClass.Name ?? theClass.GetDisplayName(_context, false).ToString()
                    );
                }
            }

            /// <summary>
            /// An exception's message and backtrace live outside the instance variable table in
            /// IronRuby, but MRI writes them as the "mesg" and "bt" instance variables.
            /// </summary>
            private void WriteException(Exception/*!*/ exception, string[]/*!*/ instanceNames) {
                // Exception#set_backtrace and #backtrace_locations are written in the prelude and
                // keep what they were given in instance variables of their own. Those are how
                // IronRuby stores the backtrace, not something the exception was given, so they go
                // out as "bt" below and not as instance variables in their own right.
                instanceNames = Array.FindAll(instanceNames, name => !name.StartsWith("@__backtrace", StringComparison.Ordinal));

                var data = RubyExceptionData.GetInstance(exception);
                Exception cause = data.HasCause ? data.Cause : null;

                WriteObject(exception);
                WriteInt32(2 + (cause != null ? 1 : 0) + instanceNames.Length);
                WriteSymbol("mesg", RubyEncoding.Binary);
                object message = data.Message;
                var messageString = message as MutableString;
                if (messageString != null && messageString.Equals(
                        RubyExceptionData.GetDefaultMessage(_context.GetClassOf(exception)))) {
                    // MRI leaves "mesg" nil when #initialize was never given one; the class name it
                    // reports from #message is synthesised on demand, exactly as IronRuby does.
                    message = null;
                }
                WriteAnObject(message);

                WriteSymbol("bt", RubyEncoding.Binary);
                WriteAnObject(data.Backtrace);

                // MRI writes the cause as another pseudo instance variable, and only when the
                // exception has one - an exception that was never raised inside a rescue has not.
                if (cause != null) {
                    WriteSymbol("cause", RubyEncoding.Binary);
                    WriteAnObject(cause);
                }

                WriteIVarPairs(exception, instanceNames);
            }

            private void WriteUsingDump(object/*!*/ obj) {
                MutableString dumpResult = _sites.Dump.Target(_sites.Dump, obj, _recursionLimit) as MutableString;
                if (dumpResult == null) {
                    throw RubyExceptions.CreateTypeError("_dump() must return string");
                }

                // What gets written are the instance variables of the string _dump returned, and
                // never the object's own: a class that wants those kept copies them onto the string
                // itself, which is what Time does with its zone, its offset and anything else it
                // was carrying.
                string[] resultIVars = _context.GetInstanceVariableNames(dumpResult);
                RubyEncoding resultEncoding = GetMarshalEncoding(dumpResult);
                bool hasIVars = resultIVars.Length > 0 || NeedsEncodingIVar(resultEncoding);

                if (hasIVars) {
                    _writer.Write((byte)'I');
                }
                _writer.Write((byte)'u');
                RubyClass theClass = _context.GetClassOf(obj);
                TestForAnonymous(theClass);
                WriteModuleName(theClass);
                WriteStringValue(dumpResult);
                if (hasIVars) {
                    WriteIVars(dumpResult, resultIVars, resultEncoding);
                }
            }

            private void WriteUsingMarshalDump(object/*!*/ obj) {
                _writer.Write((byte)'U');
                RubyClass theClass = _context.GetClassOf(obj);
                TestForAnonymous(theClass);
                WriteModuleName(theClass);
                object data = _sites.MarshalDump.Target(_sites.MarshalDump, obj);
                // The dumped form has to be something else, or loading it would never end. MRI
                // compares the immediate classes, so an object with a singleton class may answer
                // another instance of its own class as long as that one has no singleton of its own.
                if (data != null && ReferenceEquals(_context.GetImmediateClassOf(data), _context.GetImmediateClassOf(obj))) {
                    throw RubyExceptions.CreateRuntimeError("{0}#marshal_dump returned same class instance",
                        _context.GetImmediateClassOf(obj).GetDisplayName(_context, false));
                }
                WriteAnObject(data);
            }

            private void WriteClass(RubyClass/*!*/ obj) {
                if (obj.IsSingletonClass) {
                    throw RubyExceptions.CreateTypeError("singleton class can't be dumped");
                }
                WriteModuleDefinition((byte)'c', obj);
            }

            private void WriteModule(RubyModule/*!*/ obj) {
                WriteModuleDefinition((byte)'m', obj);
            }

            /// <summary>
            /// A 'c' or 'm' record is the module's name written as a string, so a name that is not
            /// ASCII only carries its encoding the same way a String does: the record is wrapped in
            /// an 'I' and followed by the one 'E' flag.
            /// </summary>
            private void WriteModuleDefinition(byte tag, RubyModule/*!*/ obj) {
                TestForAnonymous(obj);

                RubyEncoding encoding = _context.GetIdentifierEncoding();
                byte[] data = encoding.StrictEncoding.GetBytes(obj.Name);
                bool writeEncoding = NeedsEncodingIVar(encoding) && !IsAscii(data);

                if (writeEncoding) {
                    _writer.Write((byte)'I');
                }
                _writer.Write(tag);
                WriteInt32(data.Length);
                _writer.Write(data);
                if (writeEncoding) {
                    WriteInt32(1);
                    WriteEncodingIVar(encoding);
                }
            }

            /// <summary>
            /// A Data instance, and the members its class was defined with. Data keeps its values
            /// in hidden instance variables (RubyUtils.GetHiddenInstanceVariableName), and it is
            /// dumped the way a Struct is - an 'S' record of member-name/value pairs.
            /// </summary>
            private bool TryGetDataMembers(object obj, out RubyArray members) {
                members = null;

                object dataClass;
                if (!_context.ObjectClass.TryGetConstant(null, "Data", out dataClass)) {
                    return false;
                }

                var theClass = _context.GetClassOf(obj) as RubyClass;
                var dataBase = dataClass as RubyClass;
                if (theClass == null || dataBase == null || !theClass.IsSubclassOf(dataBase)) {
                    return false;
                }

                for (RubyClass klass = theClass; klass != null; klass = klass.SuperClass) {
                    object names;
                    if (_context.TryGetInstanceVariable(klass, RubyUtils.GetHiddenInstanceVariableName("data_members"), out names)) {
                        members = names as RubyArray;
                        return members != null;
                    }
                }
                return false;
            }

            private void WriteData(object/*!*/ obj, RubyArray/*!*/ members) {
                _writer.Write((byte)'S');
                RubyClass theClass = _context.GetClassOf(obj);
                TestForAnonymous(theClass);
                WriteModuleName(theClass);
                WriteInt32(members.Count);

                var identifierEncoding = _context.GetIdentifierEncoding();
                foreach (object member in members) {
                    string name = member.ToString();
                    object value;
                    if (!_context.TryGetInstanceVariable(obj, RubyUtils.GetHiddenInstanceVariableName(name), out value)) {
                        value = null;
                    }
                    WriteSymbol(name, identifierEncoding);
                    WriteAnObject(value);
                }
            }

            private void WriteStruct(RubyStruct/*!*/ obj) {
                WriteSubclassData(obj, typeof(RubyStruct));
                _writer.Write((byte)'S');
                RubyClass theClass = _context.GetClassOf(obj);
                TestForAnonymous(theClass);
                WriteModuleName(theClass);
                var names = obj.GetNames();
                WriteInt32(names.Count);
                foreach (string name in names) {
                    int index = obj.GetIndex(name);
                    WriteSymbol(name, _context.GetIdentifierEncoding());
                    WriteAnObject(obj[index]);
                }
            }

            private void WriteIVars(object/*!*/ obj, string[]/*!*/ names, RubyEncoding encoding) {
                bool writeEncoding = NeedsEncodingIVar(encoding);
                WriteInt32(names.Length + (writeEncoding ? 1 : 0));
                if (writeEncoding) {
                    WriteEncodingIVar(encoding);
                }
                WriteIVarPairs(obj, names);
            }

            private void WriteIVarPairs(object/*!*/ obj, string[]/*!*/ names) {
                var identifierEncoding = _context.GetIdentifierEncoding();
                foreach (string name in names) {
                    object value;
                    if (!_context.TryGetInstanceVariable(obj, name, out value)) {
                        value = null;
                    }
                    WriteSymbol(name, identifierEncoding);
                    WriteAnObject(value);
                }
            }

            /// <summary>
            /// MRI raises rather than serialize objects whose state lives outside the Ruby heap.
            /// </summary>
            private void CheckDumpable(object/*!*/ obj) {
                if (obj is Proc || obj is RubyMethod || obj is UnboundMethod ||
                    obj is IronRuby.StandardLibrary.Threading.RubyMutex ||
                    obj is System.Threading.Thread || obj is Binding) {

                    throw RubyExceptions.CreateTypeError("no _dump_data is defined for class {0}",
                        _context.GetClassDisplayName(obj));
                }

                if (obj is RubyIO || obj is MatchData) {
                    throw RubyExceptions.CreateTypeError("can't dump {0}", _context.GetClassDisplayName(obj));
                }
            }

            private void CheckSingleton(object/*!*/ obj) {
                RubyClass immediate = _context.GetImmediateClassOf(obj);
                if (immediate.IsSingletonClass && !immediate.IsDummySingletonClass) {
                    bool hasState = false;
                    using (_context.ClassHierarchyLocker()) {
                        immediate.EnumerateMethods((_module, _name, _member) => { hasState = true; return true; });
                    }
                    if (!hasState) {
                        hasState = _context.GetInstanceVariableNames(immediate).Length > 0;
                    }
                    if (hasState) {
                        throw RubyExceptions.CreateTypeError("singleton can't be dumped");
                    }
                }
            }

            private void WriteExtendedModules(object/*!*/ obj) {
                RubyClass theClass = _context.GetImmediateClassOf(obj);
                if (theClass.IsSingletonClass) {
                    foreach (var mixin in theClass.GetMixins()) {
                        _writer.Write((byte)'e');
                        // MRI reaches an extended module through its iclass and reports an
                        // anonymous one as a class, not as a module.
                        if (mixin.Name == null) {
                            throw RubyExceptions.CreateTypeError("can't dump anonymous class {0}",
                                mixin.GetDisplayName(_context, false)
                            );
                        }
                        WriteModuleName(mixin);
                    }
                }
            }

            private void WriteAnObject(object obj) {
                if (_recursionLimit == 0) {
                    throw RubyExceptions.CreateArgumentError("exceed depth limit");
                }
                if (_recursionLimit > 0) {
                    _recursionLimit--;
                }

                if (obj is int) {
                    int value = (int)obj;
                    if (value < -(1 << 30) || value >= (1 << 30)) {
                        obj = (BigInteger)value;
                    }
                } else if (obj is long) {
                    // Marshal has only the two integer records, and a long is always outside the
                    // fixnum one, so it goes out as a bignum - byte for byte what MRI writes.
                    obj = (BigInteger)(long)obj;
                }

                RubySymbol sym;
                if (obj == null) {
                    _writer.Write((byte)'0');
                } else if (obj is bool) {
                    _writer.Write((byte)((bool)obj ? 'T' : 'F'));
                } else if (obj is int) {
                    WriteFixnum((int)obj);
                } else if ((sym = obj as RubySymbol) != null) {
                    WriteSymbol(sym);
                } else {
                    int objectRef;
                    if (_objects.TryGetValue(obj, out objectRef)) {
                        _writer.Write((byte)'@');
                        WriteInt32(objectRef);
                    } else {
                        CheckDumpable(obj);

                        // TODO: visibility?
                        bool implementsDump = _context.ResolveMethod(obj, "_dump", VisibilityContext.AllVisible).Found
                            || Protocols.RespondTo(_sites.RespondTo, obj, "_dump");
                        bool implementsMarshalDump = _context.ResolveMethod(obj, "marshal_dump", VisibilityContext.AllVisible).Found
                            || Protocols.RespondTo(_sites.RespondTo, obj, "marshal_dump");

                        if (implementsMarshalDump) {
                            _objects[obj] = _objects.Count;
                            WriteUsingMarshalDump(obj);
                        } else if (implementsDump) {
                            // MRI indexes the object *after* whatever the string returned by #_dump pulls in.
                            WriteUsingDump(obj);
                            _objects[obj] = _objects.Count;
                        } else {
                            objectRef = _objects.Count;
                            _objects[obj] = objectRef;

                            if (!(obj is RubyModule)) {
                                CheckSingleton(obj);
                            }

                            RubyEncoding encoding = GetMarshalEncoding(obj);
                            string[] instanceNames = _context.GetInstanceVariableNames(obj);

                            RubyArray dataMembers;
                            bool isData = TryGetDataMembers(obj, out dataMembers);
                            if (isData) {
                                // The members are the instance variables, and the 'S' record is
                                // already carrying them.
                                instanceNames = ArrayUtils.EmptyStrings;
                            }

                            // An 'o' record (a plain object, or a Range) carries its instance data inline
                            // rather than behind an "I" wrapper.
                            bool isObjectRecord =
                                !(obj is double || obj is float || obj is BigInteger || obj is MutableString ||
                                  obj is RubyArray || obj is Hash || obj is RubyRegex || obj is RubyModule ||
                                  obj is RubyStruct || isData);

                            bool writeInstanceData = !isObjectRecord && !(obj is RubyModule) &&
                                (instanceNames.Length > 0 || NeedsEncodingIVar(encoding));

                            if (writeInstanceData) {
                                _writer.Write((byte)'I');
                            }

                            if (!(obj is RubyModule)) {
                                WriteExtendedModules(obj);
                            }

                            if (obj is double) {
                                WriteFloat((double)obj);
                            } else if (obj is float) {
                                WriteFloat((double)(float)obj);
                            } else if (obj is BigInteger) {
                                WriteBignum((BigInteger)obj);
                            } else if (obj is MutableString) {
                                WriteString((MutableString)obj);
                            } else if (obj is RubyArray) {
                                WriteArray((RubyArray)obj);
                            } else if (obj is Hash) {
                                WriteHash((Hash)obj);
                            } else if (obj is RubyRegex) {
                                WriteRegex((RubyRegex)obj);
                            } else if (obj is RubyClass) {
                                WriteClass((RubyClass)obj);
                            } else if (obj is RubyModule) {
                                WriteModule((RubyModule)obj);
                            } else if (isData) {
                                WriteData(obj, dataMembers);
                            } else if (obj is RubyStruct) {
                                WriteStruct((RubyStruct)obj);
                            } else if (obj is Range) {
                                WriteRange((Range)obj, instanceNames);
                            } else if (obj is Exception) {
                                WriteException((Exception)obj, instanceNames);
                            } else {
                                RequireDumpableAsObject(_context.GetClassOf(obj));
                                WriteObject(obj);
                                WriteIVars(obj, instanceNames, encoding);
                            }

                            if (writeInstanceData) {
                                WriteIVars(obj, instanceNames, encoding);
                            }
                        }
                    }
                }
                if (_recursionLimit >= 0) {
                    _recursionLimit++;
                }
            }

            internal void Dump(object obj) {
                WritePreamble();
                WriteAnObject(obj);
                _writer.BaseStream.Flush();
            }
        }

        #endregion

        #region Float formatting

        /// <summary>
        /// MRI's marshal.c w_float: the shortest representation that round trips, formatted the way
        /// "%g" would with a precision equal to the number of significant digits.
        /// </summary>
        internal static string/*!*/ FormatFloat(double value) {
            if (Double.IsPositiveInfinity(value)) {
                return "inf";
            }
            if (Double.IsNegativeInfinity(value)) {
                return "-inf";
            }
            if (Double.IsNaN(value)) {
                return "nan";
            }
            if (value == 0.0) {
                return Double.IsNegative(value) ? "-0" : "0";
            }

            bool negative = value < 0;
            double abs = negative ? -value : value;

            string digits;
            int decpt;
            SplitShortestRepresentation(abs.ToString("R", CultureInfo.InvariantCulture), out digits, out decpt);

            int digs = digits.Length;
            string body;
            if (decpt - 1 < -4 || decpt - 1 >= digs) {
                StringBuilder sb = new StringBuilder();
                sb.Append(digits[0]);
                if (digs > 1) {
                    sb.Append('.');
                    sb.Append(digits, 1, digs - 1);
                }
                sb.Append('e');
                sb.Append((decpt - 1).ToString(CultureInfo.InvariantCulture));
                body = sb.ToString();
            } else if (decpt <= 0) {
                body = "0." + new String('0', -decpt) + digits;
            } else if (digs <= decpt) {
                body = digits + new String('0', decpt - digs);
            } else {
                body = digits.Substring(0, decpt) + "." + digits.Substring(decpt);
            }

            return negative ? "-" + body : body;
        }

        private static void SplitShortestRepresentation(string/*!*/ str, out string/*!*/ digits, out int decpt) {
            int e = str.IndexOfAny(new[] { 'E', 'e' });
            int exponent = 0;
            string mantissa = str;
            if (e >= 0) {
                exponent = Int32.Parse(str.Substring(e + 1), CultureInfo.InvariantCulture);
                mantissa = str.Substring(0, e);
            }

            int dot = mantissa.IndexOf('.');
            string integerPart, fractionPart;
            if (dot >= 0) {
                integerPart = mantissa.Substring(0, dot);
                fractionPart = mantissa.Substring(dot + 1);
            } else {
                integerPart = mantissa;
                fractionPart = String.Empty;
            }

            string all = integerPart + fractionPart;
            decpt = integerPart.Length + exponent;

            int first = 0;
            while (first < all.Length - 1 && all[first] == '0') {
                first++;
                decpt--;
            }
            all = all.Substring(first);

            int last = all.Length;
            while (last > 1 && all[last - 1] == '0') {
                last--;
            }
            all = all.Substring(0, last);

            digits = all;
        }

        #endregion

        #region MarshalReader

        internal class MarshalReader {
            private sealed class Symbol {
                private string _string;
                private RubySymbol _symbol;

                public Symbol(string str, RubySymbol sym) {
                    _string = str;
                    _symbol = sym;
                }

                public string/*!*/ GetString() {
                    return _string ?? (_string = _symbol.ToString());
                }

                public RubySymbol/*!*/ GetSymbol(RubyContext/*!*/ context) {
                    return _symbol ?? (_symbol = context.EncodeIdentifier(_string));
                }
            }

            private readonly BinaryReader/*!*/ _reader;
            private readonly ReaderSites/*!*/ _sites;
            private readonly RubyGlobalScope/*!*/ _globalScope;
            private readonly object _proc;
            private readonly bool _freeze;
            private readonly Dictionary<int, Symbol>/*!*/ _symbols;
            private readonly Dictionary<int, object>/*!*/ _objects;

            // Whether the dump is being read from a stream, and whether any record has begun:
            // together they tell "the file is at its end" from "the dump is cut short".
            private bool _started;

            internal bool FromStream { get; set; }

            private RubyContext/*!*/ Context {
                get { return _globalScope.Context; }
            }

            internal MarshalReader(ReaderSites/*!*/ sites, BinaryReader/*!*/ reader,
                RubyGlobalScope/*!*/ globalScope, object proc)
                : this(sites, reader, globalScope, proc, false) {
            }

            internal MarshalReader(ReaderSites/*!*/ sites, BinaryReader/*!*/ reader,
                RubyGlobalScope/*!*/ globalScope, object proc, bool freeze) {
                _sites = sites;
                _reader = reader;
                _globalScope = globalScope;
                _proc = proc;
                _freeze = freeze;
                _symbols = new Dictionary<int, Symbol>();
                _objects = new Dictionary<int, object>();
            }

            private void CheckPreamble() {
                int major = _reader.ReadByte();
                int minor = _reader.ReadByte();
                if (major != MAJOR_VERSION || minor > MINOR_VERSION) {
                    throw RubyExceptions.CreateTypeError(
                        "incompatible marshal file format (can't be read)\n\tformat version {0}.{1} required; {2}.{3} given",
                        MAJOR_VERSION, MINOR_VERSION, major, minor
                    );
                }

                if (minor < MINOR_VERSION) {
                    Context.ReportWarning(
                        String.Format(CultureInfo.InvariantCulture,
                            "incompatible marshal file format (can be read)\n\tformat version {0}.{1} required; {2}.{3} given",
                            MAJOR_VERSION, MINOR_VERSION, major, minor
                        )
                    );
                }
            }

            private byte[]/*!*/ ReadBytes(int count) {
                if (count < 0) {
                    throw RubyExceptions.CreateArgumentError("negative string size (or size too big)");
                }
                byte[] data = _reader.ReadBytes(count);
                if (data.Length != count) {
                    throw RubyExceptions.CreateArgumentError("marshal data too short");
                }
                return data;
            }

            private BigInteger/*!*/ ReadBignum() {
                int sign;
                int csign = _reader.ReadByte();
                if (csign == '+') {
                    sign = 1;
                } else if (csign == '-') {
                    sign = -1;
                } else {
                    sign = 0;
                }
                int words = ReadInt32();
                int dwords_lo = words / 2;
                int dwords_hi = (words + 1) / 2;
                uint[] bits = new uint[dwords_hi];
                for (int i = 0; i < dwords_lo; i++) {
                    bits[i] = _reader.ReadUInt32();
                }
                if (dwords_lo != dwords_hi) {
                    bits[dwords_lo] = _reader.ReadUInt16();
                }

                return BigIntegerCompat.Create(sign, bits);
            }

            private int ReadInt32() {
                sbyte first = _reader.ReadSByte();
                if (first == 0) {
                    return 0;
                } else if (first > 4) {
                    return (first - 5);
                } else if (first < -4) {
                    return (first + 5);
                } else {
                    byte fill;
                    if (first < 0) {
                        fill = 0xff;
                        first = (sbyte)-first;
                    } else {
                        fill = 0x00;
                    }
                    uint value = 0;
                    for (int i = 0; i < 4; i++) {
                        uint nextByte;
                        if (i < first) {
                            nextByte = _reader.ReadByte();
                        } else {
                            nextByte = fill;
                        }
                        value |= nextByte << (i * 8);
                    }
                    return unchecked((int)value);
                }
            }

            private double ReadFloat() {
                MutableString value = ReadString();
                if (value.Equals(_positiveInfinityString)) {
                    return Double.PositiveInfinity;
                }
                if (value.Equals(_negativeInfinityString)) {
                    return Double.NegativeInfinity;
                }
                if (value.Equals(_nanString)) {
                    return Double.NaN;
                }

                // MRI may append the binary mantissa after a NUL; the decimal prefix is authoritative.
                int pos = value.IndexOf((byte)0);
                if (pos >= 0) {
                    value.Remove(pos, value.Length - pos);
                }
                // strtod: the longest numeric prefix counts, and no number at all is 0.0
                var match = System.Text.RegularExpressions.Regex.Match(value.ToString(),
                    @"\A[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?");
                double result;
                if (match.Success && Double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out result)) {
                    return result;
                }
                return 0.0;
            }

            private MutableString/*!*/ ReadString() {
                int count = ReadInt32();
                byte[] data = ReadBytes(count);
                return MutableString.CreateBinary(data, RubyEncoding.Binary);
            }

            private RubyRegex/*!*/ ReadRegex() {
                MutableString pattern = ReadString();
                int raw = _reader.ReadByte();
                var flags = (RubyRegexOptions)(raw & (int)(RubyRegexOptions.IgnoreCase | RubyRegexOptions.Extended | RubyRegexOptions.Multiline | RubyRegexOptions.FIXED));

                // The bit MRI writes at 0x10 means "this regexp's encoding is fixed", which is
                // Regexp::FIXEDENCODING here; 0x10 on its own is /n, which says the opposite,
                // and is what MRI's 0x20 (ARG_ENCODING_NONE) asks for.
                if ((flags & RubyRegexOptions.FIXED) != 0) {
                    flags = (flags & ~RubyRegexOptions.FIXED) | RubyRegexOptions.FixedEncoding;
                }
                if ((raw & NoEncodingFlag) != 0) {
                    flags |= RubyRegexOptions.FIXED;
                }

                return new RubyRegex(pattern, flags);
            }

            private RubyArray/*!*/ ReadArray(RubyArray/*!*/ result) {
                int count = ReadInt32();
                if (count < 0) {
                    throw RubyExceptions.CreateArgumentError("negative array size (or size too big)");
                }
                for (int i = 0; i < count; i++) {
                    result.Add(ReadAnObject(false));
                }
                return result;
            }

            private Hash/*!*/ ReadHash(int typeFlag, Hash/*!*/ result) {
                int count = ReadInt32();
                for (int i = 0; i < count; i++) {
                    object key = ReadAnObject(false);
                    result[key] = ReadAnObject(false);
                }
                if (typeFlag == '}') {
                    result.DefaultValue = ReadAnObject(false);
                }
                return result;
            }

            private string/*!*/ ReadIdentifier() {
                return ReadSymbolOrIdentifier(_reader.ReadByte(), false).GetString();
            }

            // We don't want to intern identifiers unnecessarily, so we read them as CLR strings.
            private Symbol/*!*/ ReadSymbolOrIdentifier(int typeFlag, bool symbol) {
                Symbol result;
                if (typeFlag == ';') {
                    int position = ReadInt32();
                    if (!_symbols.TryGetValue(position, out result) || result == null) {
                        throw RubyExceptions.CreateArgumentError("bad symbol");
                    }
                } else if (typeFlag == 'I') {
                    // An encoded symbol: "I" ":" <bytes> <ivars>.
                    int inner = _reader.ReadByte();
                    if (inner != ':') {
                        throw RubyExceptions.CreateArgumentError("dump format error for symbol");
                    }
                    result = ReadEncodedSymbol(symbol);
                } else {
                    // Ruby appears to assume ':'
                    int count = ReadInt32();
                    byte[] data = ReadBytes(count);
                    if (symbol) {
                        result = new Symbol(null, Context.CreateSymbol(data, RubyEncoding.Binary));
                    } else {
                        result = new Symbol(Context.GetIdentifierEncoding().Encoding.GetString(data, 0, data.Length), null);
                    }

                    _symbols[_symbols.Count] = result;
                }
                return result;
            }

            private Symbol/*!*/ ReadEncodedSymbol(bool symbol) {
                int count = ReadInt32();
                byte[] data = ReadBytes(count);
                int slot = _symbols.Count;
                _symbols[slot] = null;

                RubyEncoding encoding = RubyEncoding.Binary;
                int ivarCount = ReadInt32();
                for (int i = 0; i < ivarCount; i++) {
                    string name = ReadIdentifier();
                    object value = ReadAnObject(false);
                    RubyEncoding parsed = ParseEncodingIVar(name, value);
                    if (parsed != null) {
                        encoding = parsed;
                    }
                }

                Symbol result;
                if (symbol) {
                    result = new Symbol(null, Context.CreateSymbol(data, encoding));
                } else {
                    result = new Symbol(encoding.Encoding.GetString(data, 0, data.Length), null);
                }
                _symbols[slot] = result;
                return result;
            }

            /// <summary>
            /// Returns the encoding an ":E"/":encoding" pseudo instance variable denotes, or null if
            /// the name is an ordinary instance variable.
            /// </summary>
            private RubyEncoding ParseEncodingIVar(string/*!*/ name, object value) {
                if (name == EncodingShortIVarName) {
                    return (value is bool && (bool)value) ? RubyEncoding.UTF8 : RubyEncoding.Ascii;
                }
                if (name == EncodingIVarName) {
                    var str = value as MutableString;
                    if (str != null) {
                        return Context.GetRubyEncoding(str);
                    }
                }
                return null;
            }

            private RubyClass/*!*/ ReadType() {
                return (RubyClass)ReadClassOrModule('c', ReadIdentifier());
            }

            private object/*!*/ UnmarshalNewObject() {
                return RubyUtils.CreateObject(ReadType());
            }

            private object/*!*/ ReadObject(int objectRef) {
                RubyClass theClass = ReadType();

                // The writing side refuses to put a class backed by a CLR object into an 'o'
                // record, and the reading side refuses to take one out of it: an 'o' record is
                // instance variables and nothing else, so it cannot rebuild a File.
                if (!IsDumpableAsObject(theClass)) {
                    throw RubyExceptions.CreateArgumentError("dump format error");
                }

                int count = ReadInt32();
                Type type = theClass.GetUnderlyingSystemType();
                if (typeof(Exception).IsAssignableFrom(type) || typeof(Range).IsAssignableFrom(type)) {
                    // built from its attributes, which are not instance variables of the result
                    var attributes = new Dictionary<string, object>();
                    for (int i = 0; i < count; i++) {
                        string name = ReadIdentifier();
                        attributes[name] = ReadAnObject(false);
                    }
                    if (typeof(Exception).IsAssignableFrom(type)) {
                        return CreateException(theClass, attributes);
                    }
                    // MRI's range_loader: the two ends have to compare (begin <=> end not nil)
                    object rangeBegin, rangeEnd;
                    attributes.TryGetValue("begin", out rangeBegin);
                    attributes.TryGetValue("end", out rangeEnd);
                    if (rangeBegin != null && rangeEnd != null) {
                        var compare = _sites.Compare;
                        if (compare.Target(compare, rangeBegin, rangeEnd) == null) {
                            throw RubyExceptions.CreateArgumentError("bad value for range");
                        }
                    }
                    return RubyUtils.CreateObject(theClass, attributes);
                }

                // The object is in the link table before its instance variables are read, so one
                // that refers to itself (@a = self) links back to the object being built.
                object obj = RubyUtils.CreateObject(theClass);
                if (objectRef >= 0) {
                    _objects[objectRef] = obj;
                }
                for (int i = 0; i < count; i++) {
                    string name = ReadIdentifier();
                    Context.SetInstanceVariable(obj, name, ReadAnObject(false));
                }
                return obj;
            }

            private object/*!*/ CreateException(RubyClass/*!*/ theClass, Dictionary<string, object>/*!*/ attributes) {
                var exception = (Exception)RubyUtils.CreateObject(theClass);
                var data = RubyExceptionData.GetInstance(exception);
                foreach (var pair in attributes) {
                    switch (pair.Key) {
                        case "mesg":
                            // A nil "mesg" is MRI's way of saying "no message was ever set"; #message
                            // then answers the class name.
                            data.Message = pair.Value ?? RubyExceptionData.GetDefaultMessage(theClass);
                            break;

                        case "bt":
                            data.Backtrace = pair.Value as RubyArray;
                            break;

                        case "cause":
                            var cause = pair.Value as Exception;
                            if (cause != null) {
                                data.TrySetCause(cause);
                            }
                            break;

                        default:
                            Context.SetInstanceVariable(exception, pair.Key, pair.Value);
                            break;
                    }
                }
                return exception;
            }

            private object/*!*/ ReadUsingLoad() {
                RubyClass theClass = ReadType();
                if (!Context.ResolveMethod(theClass, "_load", VisibilityContext.AllVisible).Found) {
                    throw RubyExceptions.CreateTypeError("class {0} needs to have method '_load'",
                        theClass.GetDisplayName(Context, false).ToString());
                }
                return _sites.Load.Target(_sites.Load, theClass, ReadString());
            }

            // MRI's TYPE_DATA: an object wrapping native data, rebuilt by #_load_data from the
            // object #_dump_data gave. A class whose instances are plain objects has no such data.
            private object/*!*/ ReadData(int objectRef) {
                RubyClass theClass = ReadType();
                if (IsDumpableAsObject(theClass)) {
                    throw RubyExceptions.CreateArgumentError("dump format error");
                }

                object obj = RubyUtils.CreateObject(theClass);
                if (objectRef >= 0) {
                    _objects[objectRef] = obj;
                }
                if (!Context.ResolveMethod(obj, "_load_data", true).Found) {
                    throw RubyExceptions.CreateTypeError("class {0} needs to have instance method '_load_data'",
                        theClass.GetDisplayName(Context, false).ToString());
                }
                object data = ReadAnObject(false);
                _sites.LoadData.Target(_sites.LoadData, obj, data);
                return obj;
            }

            private object/*!*/ ReadUsingMarshalLoad(int objectRef) {
                // taken before anything nested is read, which must not be extended with it
                RubyModule extension = _pendingExtension;
                _pendingExtension = null;

                object obj = UnmarshalNewObject();
                if (objectRef >= 0) {
                    _objects[objectRef] = obj;
                }
                if (extension != null) {
                    ModuleOps.ExtendObject(extension, obj);
                }
                if (!Context.ResolveMethod(obj, "marshal_load", VisibilityContext.AllVisible).Found) {
                    throw RubyExceptions.CreateTypeError("instance of {0} needs to have method 'marshal_load'",
                        Context.GetClassOf(obj).GetDisplayName(Context, false).ToString());
                }
                _sites.MarshalLoad.Target(_sites.MarshalLoad, obj, ReadAnObject(false));
                return obj;
            }

            private object/*!*/ ReadClassOrModule(int typeFlag) {
                // The name is written as raw bytes, and a name that is not ASCII only arrives
                // wrapped in an 'I' record whose encoding flag is only read once the class has
                // been resolved - too late to decode by. MRI reads the bytes as the identifier
                // encoding either way.
                byte[] name = ReadString().ToByteArray();
                return ReadClassOrModule(typeFlag, Context.GetIdentifierEncoding().Encoding.GetString(name, 0, name.Length));
            }

            private object/*!*/ ReadClassOrModule(int typeFlag, string/*!*/ name) {
                RubyModule result;
                if (!Context.TryGetModule(_globalScope, name, out result)) {
                    throw RubyExceptions.CreateArgumentError("undefined class/module {0}", name);
                }

                bool isClass = result is RubyClass;
                if (isClass && typeFlag == 'm') {
                    throw RubyExceptions.CreateArgumentError("{0} does not refer to module", name);
                }
                if (!isClass && typeFlag == 'c') {
                    throw RubyExceptions.CreateArgumentError("{0} does not refer to class", name);
                }
                return result;
            }

            private object/*!*/ ReadStruct(int objectRef) {
                object instance = UnmarshalNewObject();
                if (objectRef >= 0) {
                    _objects[objectRef] = instance;
                }

                // Data shares the Struct record, so which of the two this is can only be told from
                // the class that was named in it.
                if (!(instance is RubyStruct)) {
                    return ReadData(instance);
                }

                RubyStruct obj = (RubyStruct)instance;

                var names = obj.GetNames();
                int count = ReadInt32();
                if (count != names.Count) {
                    throw RubyExceptions.CreateTypeError("struct {0} not compatible (struct size differs)", Context.GetClassOf(obj).Name);
                }

                for (int i = 0; i < count; i++) {
                    string name = ReadIdentifier();
                    if (name != names[i]) {
                        RubyClass theClass = Context.GetClassOf(obj);
                        throw RubyExceptions.CreateTypeError("struct {0} not compatible (:{1} for :{2})", theClass.Name, name, names[i]);
                    }
                    obj[i] = ReadAnObject(false);
                }

                return obj;
            }

            /// <summary>
            /// The members of an 'S' record whose class is a Data go back into the hidden instance
            /// variables named after them, and the result is frozen - every Data is.
            /// </summary>
            private object/*!*/ ReadData(object/*!*/ instance) {
                RubyClass dataClass = Context.GetClassOf(instance);
                RubyArray members = GetDataMembers(dataClass);
                int count = ReadInt32();
                if (members != null && count != members.Count) {
                    throw RubyExceptions.CreateTypeError("struct {0} not compatible (struct size differs)", dataClass.Name);
                }
                for (int i = 0; i < count; i++) {
                    string name = ReadIdentifier();
                    if (members != null && name != members[i].ToString()) {
                        throw RubyExceptions.CreateTypeError("struct {0} not compatible (:{1} for :{2})", dataClass.Name, name, members[i]);
                    }
                    Context.SetInstanceVariable(instance, RubyUtils.GetHiddenInstanceVariableName(name), ReadAnObject(false));
                }
                Context.FreezeObject(instance);
                return instance;
            }

            // The member list Data.define recorded on the class (or on the Data class a plain
            // subclass derives from), in a hidden instance variable; null when there is none.
            private RubyArray GetDataMembers(RubyClass/*!*/ dataClass) {
                string ivar = RubyUtils.GetHiddenInstanceVariableName("data_members");
                for (RubyClass cls = dataClass; cls != null; cls = cls.SuperClass) {
                    object names;
                    if (Context.TryGetInstanceVariable(cls, ivar, out names) && names is RubyArray) {
                        return (RubyArray)names;
                    }
                }
                return null;
            }

            private object/*!*/ ReadInstanced(int objectRef) {
                int typeFlag = _reader.ReadByte();
                if (typeFlag == ':') {
                    return ReadEncodedSymbol(true).GetSymbol(Context);
                }

                if (typeFlag == 'u') {
                    // The instance variables written with a _dump string belong to that string,
                    // and _load is handed them along with it - that is how Time gets its zone and
                    // its offset back.
                    RubyClass theClass = ReadType();
                    MutableString data = ReadString();

                    // The object itself is entered in the link table only once those instance
                    // variables have been read, so they take the earlier ids: in a dump of one
                    // Time twice, the second is a link to id 2, not to id 1.
                    if (objectRef >= 0 && _objects.Count == objectRef + 1) {
                        _objects.Remove(objectRef);
                    }

                    ReadIVars(data);
                    object loaded = _sites.Load.Target(_sites.Load, theClass, data);
                    _objects[_objects.Count] = loaded;
                    _selfLinked = true;
                    return loaded;
                }

                object obj = ReadAnObject(typeFlag, objectRef);
                ReadIVars(obj);
                return obj;
            }

            private void ReadIVars(object obj) {
                int count = ReadInt32();
                for (int i = 0; i < count; i++) {
                    string name = ReadIdentifier();
                    object value = ReadAnObject(false);
                    RubyEncoding encoding = ParseEncodingIVar(name, value);
                    if (encoding != null) {
                        ForceMarshalEncoding(obj, encoding);
                    } else {
                        Context.SetInstanceVariable(obj, name, value);
                    }
                }
            }

            private object/*!*/ ReadExtended(int objectRef) {
                string extensionName = ReadIdentifier();
                RubyModule module = ReadClassOrModule('m', extensionName) as RubyModule;
                int typeFlag = _reader.ReadByte();

                // MRI extends a user-marshaled object as soon as it is allocated, so that its
                // #marshal_load already sees the module (see ReadUsingMarshalLoad).
                if (typeFlag == 'U') {
                    _pendingExtension = module;
                }
                object obj = ReadAnObject(typeFlag, objectRef);
                if (typeFlag != 'U') {
                    ModuleOps.ExtendObject(module, obj);
                }
                return obj;
            }

            private RubyModule _pendingExtension;

            private object/*!*/ ReadUserClass(int objectRef) {
                object obj = UnmarshalNewObject();
                if (objectRef >= 0) {
                    _objects[objectRef] = obj;
                }
                bool loaded = false;
                int typeFlag = _reader.ReadByte();
                switch (typeFlag) {
                    case '"':
                        MutableString msc = (obj as MutableString);
                        if (msc != null) {
                            msc.Replace(0, msc.Length, ReadString());
                            loaded = true;
                        }
                        break;

                    case '/':
                        RubyRegex rsc = (obj as RubyRegex);
                        if (rsc != null) {
                            RubyRegex regex = ReadRegex();
                            rsc.Set(regex.Pattern, regex.Options);
                            loaded = true;
                        }
                        break;

                    case '[':
                        RubyArray asc = (obj as RubyArray);
                        if (asc != null) {
                            ReadArray(asc);
                            loaded = true;
                        }
                        break;

                    case '{':
                    case '}':
                        Hash hsc = (obj as Hash);
                        if (hsc != null) {
                            // A "C" record naming Hash itself says nothing about the class, so it
                            // says the other thing MRI writes it for: the hash compares by identity.
                            if (Context.GetClassOf(hsc) == Context.GetClass(typeof(Hash))) {
                                hsc.CompareByIdentity();
                            }
                            ReadHash(typeFlag, hsc);
                            loaded = true;
                        }
                        break;

                    case 'C':
                        // MRI writes "C" ":Hash" "{" for a compare_by_identity hash, and nests it inside
                        // another "C" record when the hash is an instance of a Hash subclass.
                        Hash inner = ReadUserClass(-1) as Hash;
                        Hash outer = obj as Hash;
                        if (inner != null && outer != null) {
                            outer.CompareByIdentity();
                            outer.DefaultValue = inner.DefaultValue;
                            foreach (var pair in inner) {
                                outer[pair.Key] = pair.Value;
                            }
                            loaded = true;
                        }
                        break;

                    default:
                        break;
                }
                if (!loaded) {
                    throw RubyExceptions.CreateArgumentError("incompatible base type");
                }
                return obj;
            }

            // Object reference slots: NewRef asks for a fresh slot, NoRef suppresses caching, and any
            // value >= 0 is a slot an enclosing record ("I", "e", "C") already reserved for this object.
            private const int NewRef = -1;
            private const int NoRef = -2;

            // Set by a record that entered its object in the link table itself, at a later id than
            // the slot reserved for it - see ReadInstanced's "u" case.
            private bool _selfLinked;

            // Link ids whose record has been read to its end, and those MRI never counts as read
            // to their end at all: only the former run the proc when a later record refers to them.
            private readonly HashSet<int>/*!*/ _finished = new HashSet<int>();
            private readonly HashSet<int>/*!*/ _unfinished = new HashSet<int>();

            private object ReadAnObject(bool noCache) {
                return ReadAnObject(_reader.ReadByte(), noCache ? NoRef : NewRef);
            }

            private object ReadAnObject(int typeFlag, int reservedRef) {
                _started = true;
                object obj = null;
                bool outermost = (reservedRef == NewRef);
                bool runProc = (outermost && _proc != null);
                bool freezable = false;

                // the slot this record took in the link table, if it took one
                int linkRef = -1;
                switch (typeFlag) {
                    case '0':
                        obj = null;
                        break;

                    case 'T':
                        obj = true;
                        break;

                    case 'F':
                        obj = false;
                        break;

                    case 'i':
                        obj = ReadInt32();
                        break;

                    case ':':
                        obj = ReadSymbolOrIdentifier(typeFlag, true).GetSymbol(Context);
                        break;

                    case ';':
                        obj = ReadSymbolOrIdentifier(typeFlag, true).GetSymbol(Context);
                        runProc = false;
                        break;

                    case '@':
                        int link = ReadInt32();
                        if (!_objects.TryGetValue(link, out obj)) {
                            throw RubyExceptions.CreateArgumentError("dump format error (unlinked)");
                        }
                        // MRI runs the proc over a back-reference too - the proc sees each place
                        // an object appears in the dump, not each object - but only once the
                        // object it points at is finished. A container that contains itself is
                        // read through a reference to its own half-built self, and that one the
                        // proc never sees.
                        runProc = runProc && _finished.Contains(link);
                        break;

                    default:
                        // Reserve a reference
                        int objectRef = reservedRef;
                        if (objectRef == NewRef) {
                            objectRef = _objects.Count;
                            _objects[objectRef] = null;
                        }

                        freezable = true;
                        switch (typeFlag) {
                            case 'f':
                                obj = ReadFloat();
                                freezable = false;
                                break;
                            case 'l':
                                // An Integer travels as a bignum record whenever it is too wide for
                                // MRI's 31-bit fixnum record, which covers most of the values this
                                // runtime carries as a long. Normalize so the loaded value has the
                                // canonical representation for its magnitude.
                                obj = Protocols.Normalize(ReadBignum());
                                freezable = false;
                                break;
                            case '"':
                                obj = ReadString();
                                break;
                            case '/':
                                obj = ReadRegex();
                                break;
                            case '[': {
                                RubyArray array = new RubyArray();
                                if (objectRef >= 0) {
                                    _objects[objectRef] = array;
                                }
                                obj = ReadArray(array);
                                break;
                            }
                            case '{':
                            case '}': {
                                Hash hash = new Hash(Context);
                                if (objectRef >= 0) {
                                    _objects[objectRef] = hash;
                                }
                                obj = ReadHash(typeFlag, hash);
                                break;
                            }
                            case 'o':
                                obj = ReadObject(objectRef);
                                break;
                            case 'u':
                                obj = ReadUsingLoad();
                                break;
                            case 'U':
                                obj = ReadUsingMarshalLoad(objectRef);
                                break;
                            case 'd':
                                obj = ReadData(objectRef);
                                break;
                            case 'c':
                            case 'm':
                                obj = ReadClassOrModule(typeFlag);
                                freezable = false;
                                break;
                            case 'M':
                                obj = ReadOldModule();
                                freezable = false;
                                break;
                            case 'S':
                                obj = ReadStruct(objectRef);
                                break;
                            case 'I':
                                obj = ReadInstanced(objectRef);
                                break;
                            case 'e':
                                obj = ReadExtended(objectRef);
                                break;
                            case 'C':
                                obj = ReadUserClass(objectRef);
                                break;
                            default:
                                throw RubyExceptions.CreateArgumentError("dump format error({0})",
                                    "0x" + ((int)typeFlag).ToString("x", CultureInfo.InvariantCulture));
                        }
                        if (objectRef >= 0 && !_selfLinked) {
                            _objects[objectRef] = obj;
                            linkRef = objectRef;
                        }
                        if (objectRef >= 0) {
                            // An object handed to #_load or #marshal_load stays among the
                            // unfinished ones, so a later reference to it does not reach the
                            // proc the way a reference to a finished object does - unless an 'I'
                            // record wraps it, which finishes it as it adds the ivars.
                            if (typeFlag == 'u' || typeFlag == 'U') {
                                _unfinished.Add(objectRef);
                            } else if (typeFlag == 'I') {
                                _unfinished.Remove(objectRef);
                            }

                            if (!_unfinished.Contains(objectRef)) {
                                _finished.Add(objectRef);
                            }
                        }
                        _selfLinked = false;
                        break;
                }
                if (_freeze && freezable && outermost && obj != null) {
                    KernelOps.Freeze(Context, obj);

                    // MRI hands a frozen String to the fstring table, so two equal strings in one
                    // dump come back as one object. The link table has to point at the survivor
                    // too, or a back-reference to the second one would answer a different object
                    // than the record itself did.
                    var str = obj as MutableString;
                    if (str != null) {
                        obj = Deduplicate(str);
                        if (linkRef >= 0 && !ReferenceEquals(obj, str)) {
                            _objects[linkRef] = obj;
                        }
                    }
                }
                if (runProc) {
                    obj = _sites.ProcCall.Target(_sites.ProcCall, _proc, obj);
                }
                return obj;
            }

            /// <summary>
            /// The one frozen string with these bytes and this encoding, as MRI's fstring table
            /// keeps it. Only strings with nothing else about them qualify: one carrying instance
            /// variables or a singleton class is its own object.
            /// </summary>
            private MutableString/*!*/ Deduplicate(MutableString/*!*/ str) {
                // only a plain String joins the table; an instance of a subclass keeps its class
                if (Context.HasInstanceVariables(str) || str.GetType() != typeof(MutableString)) {
                    return str;
                }

                return RubyOps.InternFrozenString(str);
            }

            private object/*!*/ ReadOldModule() {
                // Marshal format 4.6 and earlier wrote classes and modules with a single "M" tag.
                string name = ReadString().ToString();
                RubyModule result;
                if (!Context.TryGetModule(_globalScope, name, out result)) {
                    throw RubyExceptions.CreateArgumentError("undefined class/module {0}", name);
                }
                return result;
            }

            internal object Load() {
                try {
                    CheckPreamble();
                    return ReadAnObject(false);
                } catch (EndOfStreamException e) {
                    // A stream that ended before a single record began is at its end, which MRI
                    // reports as such; a record that started and ran out is a short dump.
                    if (FromStream && !_started) {
                        throw new EOFError("end of file reached");
                    }
                    throw RubyExceptions.CreateArgumentError("marshal data too short", e);
                } catch (IOException e) {
                    throw RubyExceptions.CreateArgumentError("marshal data too short", e);
                }
            }
        }

        #endregion

        #region Public Instance Methods

        // TODO: Use DefaultValue attribute when it works with the binder
        /// <summary>
        /// A marshalled stream is bytes, so MRI puts the IO it was handed into binary mode first -
        /// for anything that has a #binmode to put into it, which a StringIO does.
        /// </summary>
        private static void SetBinaryMode(CallSite<Func<CallSite, object, object>>/*!*/ binmode,
            RespondToStorage/*!*/ respondToStorage, object io) {

            if (io != null && Protocols.RespondTo(respondToStorage, io, "binmode")) {
                binmode.Target(binmode, io);
            }
        }

        [RubyMethod("dump", RubyMethodAttributes.PublicSingleton)]
        public static MutableString Dump(WriterSites/*!*/ sites, RubyModule/*!*/ self, object obj) {
            return Dump(sites, self, obj, -1);
        }

        // TODO: Use DefaultValue attribute when it works with the binder
        [RubyMethod("dump", RubyMethodAttributes.PublicSingleton)]
        public static MutableString Dump(WriterSites/*!*/ sites, RubyModule/*!*/ self, object obj, int limit) {
            MemoryStream buffer = new MemoryStream();
            BinaryWriter writer = new BinaryWriter(buffer);
            MarshalWriter dumper = new MarshalWriter(sites, writer, self.Context, limit);
            dumper.Dump(obj);
            return MutableString.CreateBinary(buffer.ToArray());
        }

        // TODO: Use DefaultValue attribute when it works with the binder
        [RubyMethod("dump", RubyMethodAttributes.PublicSingleton)]
        public static object Dump(WriterSites/*!*/ sites, RubyModule/*!*/ self, object obj, [NotNull]RubyIO/*!*/ io, [Optional]int? limit) {
            BinaryWriter writer = io.GetBinaryWriter();
            MarshalWriter dumper = new MarshalWriter(sites, writer, self.Context, limit);
            dumper.Dump(obj);
            return io;
        }

        // TODO: Use DefaultValue attribute when it works with the binder
        [RubyMethod("dump", RubyMethodAttributes.PublicSingleton)]
        public static object Dump(WriterSites/*!*/ sites, RespondToStorage/*!*/ respondToStorage,
            RubyModule/*!*/ self, object obj, object io, [Optional]int? limit) {
            Stream stream = null;
            if (io != null) {
                stream = RubyIOOps.CreateIOWrapper(respondToStorage, io, FileAccess.Write);
            }
            if (stream == null || !stream.CanWrite) {
                throw RubyExceptions.CreateTypeError("instance of IO needed");
            }

            SetBinaryMode(sites.Binmode, respondToStorage, io);

            BinaryWriter writer = new BinaryWriter(stream);
            MarshalWriter dumper = new MarshalWriter(sites, writer, self.Context, limit);
            dumper.Dump(obj);
            return io;
        }

        /// <summary>
        /// Splits the optional trailing arguments of Marshal.load into the proc and the "freeze:" option.
        /// </summary>
        private static bool ParseLoadOptions(RubyContext/*!*/ context, object arg1, object arg2, out object proc) {
            proc = null;
            bool freeze = false;

            for (int i = 0; i < 2; i++) {
                object arg = (i == 0) ? arg1 : arg2;
                if (arg == null || arg == System.Reflection.Missing.Value) {
                    continue;
                }

                var options = arg as IDictionary<object, object>;
                if (options != null) {
                    foreach (var pair in options) {
                        var key = pair.Key as RubySymbol;
                        if (key != null && key.ToString() == "freeze") {
                            freeze = RubyOps.IsTrue(pair.Value);
                        } else {
                            throw RubyExceptions.CreateArgumentError("unknown keyword: {0}", context.Inspect(pair.Key));
                        }
                    }
                    continue;
                }

                // anything that answers #call will do; one that does not fails when it is called
                proc = arg;
            }

            return freeze;
        }

        [RubyMethod("load", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("restore", RubyMethodAttributes.PublicSingleton)]
        public static object Load(ReaderSites/*!*/ sites, RubyScope/*!*/ scope, RubyModule/*!*/ self, [NotNull]MutableString/*!*/ source,
            [Optional]object proc, [Optional]object options) {

            object block;
            bool freeze = ParseLoadOptions(self.Context, proc, options, out block);
            BinaryReader reader = new BinaryReader(new MemoryStream(source.ConvertToBytes()));
            MarshalReader loader = new MarshalReader(sites, reader, scope.GlobalScope, block, freeze);
            return loader.Load();
        }

        [RubyMethod("load", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("restore", RubyMethodAttributes.PublicSingleton)]
        public static object Load(ReaderSites/*!*/ sites, RubyScope/*!*/ scope, RubyModule/*!*/ self, [NotNull]RubyIO/*!*/ source,
            [Optional]object proc, [Optional]object options) {

            object block;
            bool freeze = ParseLoadOptions(self.Context, proc, options, out block);
            BinaryReader reader = source.GetBinaryReader();
            MarshalReader loader = new MarshalReader(sites, reader, scope.GlobalScope, block, freeze) { FromStream = true };
            return loader.Load();
        }

        [RubyMethod("load", RubyMethodAttributes.PublicSingleton)]
        [RubyMethod("restore", RubyMethodAttributes.PublicSingleton)]
        public static object Load(ReaderSites/*!*/ sites, RespondToStorage/*!*/ respondToStorage, ConversionStorage<MutableString>/*!*/ toStr,
            RubyScope/*!*/ scope, RubyModule/*!*/ self, object source, [Optional]object proc, [Optional]object options) {

            // MRI: anything with #to_str is the dump itself; otherwise the source has to answer
            // both #getbyte and #read to count as an IO.
            MutableString dump = source != null ? Protocols.TryCastToString(toStr, source) : null;
            if (dump != null) {
                return Load(sites, scope, self, dump, proc, options);
            }

            object block;
            bool freeze = ParseLoadOptions(self.Context, proc, options, out block);

            Stream stream = null;
            if (source != null && Protocols.RespondTo(respondToStorage, source, "getbyte")) {
                stream = RubyIOOps.CreateIOWrapper(respondToStorage, source, FileAccess.Read);
            }
            if (stream == null || !stream.CanRead) {
                throw RubyExceptions.CreateTypeError("instance of IO needed");
            }

            SetBinaryMode(sites.Binmode, respondToStorage, source);

            BinaryReader reader = new BinaryReader(stream);
            MarshalReader loader = new MarshalReader(sites, reader, scope.GlobalScope, block, freeze) { FromStream = true };
            return loader.Load();
        }

        #endregion

        #region Declared Constants

        [RubyConstant]
        public const int MAJOR_VERSION = 4;

        [RubyConstant]
        public const int MINOR_VERSION = 8;

        #endregion
    }
}
