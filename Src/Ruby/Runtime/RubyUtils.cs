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

#if FEATURE_CORE_DLR
using System.Linq.Expressions;
#else
using Microsoft.Scripting.Ast;
#endif

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using IronRuby.Builtins;
using IronRuby.Compiler;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting;
using System.Numerics;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using Microsoft.Scripting.Actions;
using Microsoft.Scripting.Actions.Calls;
using IronRuby.Runtime.Conversions;

namespace IronRuby.Runtime {
    using EvalEntryPointDelegate = Func<RubyScope, object, RubyModule, Proc, object>;

    public static class RubyUtils {
        #region Objects

        public static readonly int FalseObjectId = 0;
        public static readonly int TrueObjectId = 2;
        public static readonly int NilObjectId = 4;

        // MRI seeds the hash of immediate values with a random per-process key (CVE-2011-4815), so
        // an attacker cannot precompute colliding keys. Integer and Float hashes are mixed with it;
        // String and Symbol hashes already come from .NET's randomized string hashing.
        // Random.Shared is seeded from the OS (getrandom/arc4random), as that string hashing is; the
        // cryptographic generator gave no stronger key for this and loading it (OpenSSL) cost ~8 ms of startup.
        private static readonly uint _hashSeed = (uint)Random.Shared.Next(Int32.MinValue, Int32.MaxValue);

        private static int MixHash(uint h) {
            unchecked {
                h ^= _hashSeed;
                h ^= h >> 16;
                h *= 0x85ebca6b;
                h ^= h >> 13;
                h *= 0xc2b2ae35;
                h ^= h >> 16;
                return (int)h;
            }
        }

        public static int GetFixnumHashCode(int value) {
            return MixHash(unchecked((uint)value));
        }

        /// <summary>
        /// The hash of an Integer, from its value rather than from the CLR type carrying it, so
        /// that the Int32 / Int64 / BigInteger representations of one value can never disagree.
        /// The narrowing funnels mean two of them cannot hold the same value at once, but a long
        /// arriving straight from a CLR method never passed through one, and #hash has to be right
        /// for it anyway.
        /// </summary>
        public static int GetIntegerHashCode(long value) {
            if (value >= Int32.MinValue && value <= Int32.MaxValue) {
                return GetFixnumHashCode((int)value);
            }
            return MixHash(unchecked((uint)value ^ (uint)(value >> 32)) + 0x7feb352d);
        }

        public static int GetIntegerHashCode(BigInteger value) {
            long small;
            if (value.AsInt64(out small)) {
                return GetIntegerHashCode(small);
            }
            return value.GetHashCode();
        }

        public static int GetFloatHashCode(double value) {
            // 0.0 and -0.0 are eql? and must hash alike, which Double.GetHashCode already ensures
            return MixHash(unchecked((uint)value.GetHashCode() + 0x9e3779b9));
        }

        /// <summary>
        /// Ruby value types:
        /// 
        /// NilClass
        /// TrueClass
        /// FalseClass
        /// Fixnum
        /// Symbol
        /// + CLR structs
        /// </summary>
        public static bool IsRubyValueType(object obj) {
            return (obj == null || obj is ValueType || obj is RubySymbol) && !(obj is float || obj is double);
        }

        /// <summary>
        /// The object maintains a state (frozen, tainted flags and instance variables).
        /// <code>
        /// obj.instance_variable_set(:@foo, 'bar')
        /// puts obj.instance_variable_get(:@foo)     # => 'bar'
        /// puts obj.taint.tainted?                   # => true
        /// </code>
        /// </summary>
        public static bool HasObjectState(object obj) {
            return !IsRubyValueType(obj);
        }

        /// <summary>
        /// Can the object be cloned?
        /// </summary>
        public static bool CanClone(object obj) {
            return !IsRubyValueType(obj);
        }

        /// <summary>
        /// Is it allowed to define a sigleton method for the object?
        /// </summary>
        /// <summary>
        /// Can user code attach singleton methods to this object? nil, true and false answer
        /// their own class and take methods on it; the rest of the immediates - an Integer of any
        /// size, a Float, a Symbol - hold no state to hang one on. Nor does a frozen String: MRI
        /// may have deduplicated it, so a singleton would be shared with every equal literal.
        ///
        /// Not the same question as <see cref="HasSingletonClass"/>: #instance_eval and
        /// #instance_exec run in a throwaway singleton of whatever they are handed, and MRI lets
        /// them do that for every one of these.
        /// </summary>
        public static bool CanDefineSingletonMethod(object obj) {
            if ((obj is ValueType || obj is RubySymbol || obj is BigInteger) && !(obj is bool)) {
                return false;
            }

            var str = obj as MutableString;
            return str == null || !str.IsFrozen;
        }

        public static void RequireDefinableSingleton(object obj) {
            if (!CanDefineSingletonMethod(obj)) {
                throw RubyExceptions.CreateTypeError("can't define singleton");
            }
        }

        /// <summary>
        /// Does the object have a sigleton class?
        /// </summary>
        public static bool HasSingletonClass(object obj) {
            return !(obj is int || obj is RubySymbol);
        }

        
        private static CallSite<Func<CallSite, object, object>> _InstanceVariablesToInspectSite;

        /// <summary>
        /// Ruby 4.0 lets an object name the instance variables its #inspect shows, by answering an
        /// array of names from a private #instance_variables_to_inspect. A name that the object
        /// does not have is simply not shown; nil means "all of them", and anything else is an
        /// error. Objects that do not define the method at all - which is nearly all of them -
        /// pay one method lookup.
        /// </summary>
        private static IEnumerable<KeyValuePair<string, object>>/*!*/ SelectInspectedVariables(
            RubyContext/*!*/ context, object obj, IEnumerable<KeyValuePair<string, object>>/*!*/ vars) {

            // The runtime's own slots in the instance variable table - the finalizer that
            // ObjectSpace.define_finalizer attaches is one, spelled with angle brackets so that
            // it cannot collide with a Ruby name - have no business in an #inspect.
            var visible = new List<KeyValuePair<string, object>>();
            foreach (KeyValuePair<string, object> var in vars) {
                if (var.Key.Length == 0 || var.Key[0] != '<') {
                    visible.Add(var);
                }
            }
            vars = visible;

            if (!context.ResolveMethod(obj, "instance_variables_to_inspect", VisibilityContext.AllVisible).Found) {
                return vars;
            }

            var site = GetCallSite(ref _InstanceVariablesToInspectSite, context, "instance_variables_to_inspect", 0);
            object names = site.Target(site, obj);
            if (names == null) {
                return vars;
            }

            var list = names as IList<object>;
            if (list == null) {
                throw RubyExceptions.CreateTypeError(String.Format(
                    "Expected #instance_variables_to_inspect to return an Array or nil, but it returned {0}",
                    context.GetClassDisplayName(names)
                ));
            }

            var wanted = new List<string>(list.Count);
            foreach (object name in list) {
                wanted.Add(name.ToString());
            }

            var result = new List<KeyValuePair<string, object>>();
            foreach (KeyValuePair<string, object> var in vars) {
                if (wanted.Contains(var.Key)) {
                    result.Add(var);
                }
            }
            return result;
        }

        public static MutableString/*!*/ InspectObject(UnaryOpStorage/*!*/ inspectStorage, ConversionStorage<MutableString>/*!*/ tosConversion, 
            object obj) {

            var context = tosConversion.Context;
            using (IDisposable handle = RubyUtils.InfiniteInspectTracker.TrackObject(obj)) {
                if (handle == null) {
                    return MutableString.CreateAscii("...");
                }

                MutableString str = MutableString.CreateMutable(context.GetIdentifierEncoding());
                str.Append("#<");
                str.Append(context.GetClassDisplayName(obj));

                // Ruby prints 2*object_id for objects
                str.Append(':');
                AppendFormatHexObjectId(str, GetObjectId(context, obj));

                RubyInstanceData data = context.TryGetInstanceData(obj);
                if (data != null) {
                    var vars = SelectInspectedVariables(context, obj, data.GetInstanceVariablePairs());
                    bool first = true;
                    foreach (KeyValuePair<string, object> var in vars) {
                        if (first) {
                            str.Append(' ');
                            first = false;
                        } else {
                            str.Append(", ");
                        }
                        // TODO (encoding):
                        str.Append(var.Key);
                        str.Append('=');

                        var inspectSite = inspectStorage.GetCallSite("inspect");
                        object inspectedValue = inspectSite.Target(inspectSite, var.Value);

                        var tosSite = tosConversion.GetSite(ConvertToSAction.Make(context));
                        str.Append(tosSite.Target(tosSite, inspectedValue));

                        str.TaintBy(var.Value, context);
                    }
                }
                str.Append(">");

                str.TaintBy(obj, context);
                return str;
            }
        }

        public static MutableString/*!*/ FormatObjectPrefix(RubyContext/*!*/ context, string/*!*/ className, long objectId, bool isTainted, bool isUntrusted) {
            MutableString str = MutableString.CreateMutable(context.GetIdentifierEncoding());
            str.Append("#<");
            str.Append(className);

            // Ruby prints 2*object_id for objects
            str.Append(':');
            AppendFormatHexObjectId(str, objectId);

            str.IsTainted |= isTainted;
            str.IsUntrusted |= isUntrusted;
            return str;
        }

        public static MutableString/*!*/ FormatObject(RubyContext/*!*/ context, string/*!*/ className, long objectId, bool isTainted, bool isUntrusted) {
            return FormatObjectPrefix(context, className, objectId, isTainted, isUntrusted).Append(">");
        }

        public static MutableString/*!*/ ObjectToMutableString(RubyContext/*!*/ context, object obj) {
            bool tainted, untrusted;
            context.GetObjectTrust(obj, out tainted, out untrusted);
            return FormatObject(context, context.GetClassDisplayName(obj), GetObjectId(context, obj), tainted, untrusted);
        }

        public static MutableString/*!*/ ObjectToMutableStringPrefix(RubyContext/*!*/ context, object obj) {
            bool tainted, untrusted;
            context.GetObjectTrust(obj, out tainted, out untrusted);
            return FormatObjectPrefix(context, context.GetClassDisplayName(obj), GetObjectId(context, obj), tainted, untrusted);
        }

        public static MutableString/*!*/ AppendFormatHexObjectId(MutableString/*!*/ str, long objectId) {
            return str.AppendFormat("0x{0:x7}", 2 * objectId);
        }

        public static MutableString/*!*/ ObjectToMutableString(IRubyObject/*!*/ self) {
            var context = self.ImmediateClass.Context;
            return RubyUtils.FormatObject(
                context,
                // an anonymous class has no Name to print, and MRI does not leave a blank there:
                // it uses how the class describes itself, "#<#<Class:0x...>:0x...>"
                self.ImmediateClass.GetNonSingletonClass().GetNonNullName(context),
                self.GetInstanceData().ObjectId, 
                self.IsTainted,
                self.IsUntrusted
            );
        }

        public static MutableString/*!*/ ObjectBaseToMutableString(IRubyObject/*!*/ self) {
            if (self is RubyObject) {
                return RubyUtils.ObjectToMutableString(self);
            } else {
                return MutableString.CreateMutable(self.BaseToString(), RubyEncoding.UTF8);
            }
        }

        public static bool TryDuplicateObject(
            CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ initializeCopyStorage,
            CallSiteStorage<Func<CallSite, RubyClass, object>>/*!*/ allocateStorage, 
            object obj, bool cloneSemantics, out object copy) {

            // Ruby value types can't be cloned
            if (!RubyUtils.CanClone(obj)) {
                copy = null;
                return false;
            }

            var context = allocateStorage.Context;

            IDuplicable clonable = obj as IDuplicable;
            if (clonable != null) {
                copy = clonable.Duplicate(context, cloneSemantics);
            } else {
                // .NET and library classes that don't implement IDuplicable:
                var allocateSite = allocateStorage.GetCallSite("allocate", 0);
                copy = allocateSite.Target(allocateSite, context.GetClassOf(obj));

                context.CopyInstanceData(obj, copy, cloneSemantics);
            }

            // MRI hands the copy to #initialize_dup or #initialize_clone, whose default
            // implementations call #initialize_copy. Calling #initialize_copy directly
            // skipped both hooks, so a class that deep-copies its internals in
            // #initialize_dup - Set copies its backing Hash there - handed out a copy that
            // still shared them, and mutating the copy mutated the original.
            var initializeCopySite = initializeCopyStorage.GetCallSite(
                cloneSemantics ? "initialize_clone" : "initialize_dup", 1);
            initializeCopySite.Target(initializeCopySite, copy, obj);
            if (cloneSemantics) {
                context.FreezeObjectBy(copy, obj);

                // #clone carries the chilled state of a string over with the frozen one; #dup
                // does not, which is why this is here and not in the copy itself.
                var original = obj as MutableString;
                var duplicate = copy as MutableString;
                if (original != null && duplicate != null) {
                    MutableString.CopyChilledState(original, duplicate);
                }
            }

            return true;
        }

        /// <summary>
        /// #clone with an explicit freeze: value. MRI hands that value on to #initialize_clone as
        /// a keyword argument, so a class overriding the hook can see which kind of copy is being
        /// made - and a hook that takes only the original is an ArgumentError, as it is in MRI.
        /// Keyword arguments have no slot of their own in this calling convention: they travel as
        /// a trailing Hash that says it came from keyword syntax.
        /// </summary>
        public static bool TryDuplicateObject(
            CallSiteStorage<Func<CallSite, object, object, object, object>>/*!*/ initializeCopyStorage,
            CallSiteStorage<Func<CallSite, RubyClass, object>>/*!*/ allocateStorage,
            object obj, bool freeze, out object copy) {

            // Ruby value types can't be cloned
            if (!RubyUtils.CanClone(obj)) {
                copy = null;
                return false;
            }

            var context = allocateStorage.Context;

            IDuplicable clonable = obj as IDuplicable;
            if (clonable != null) {
                copy = clonable.Duplicate(context, true);
            } else {
                var allocateSite = allocateStorage.GetCallSite("allocate", 0);
                copy = allocateSite.Target(allocateSite, context.GetClassOf(obj));
                context.CopyInstanceData(obj, copy, true);
            }

            var keywords = new Hash(context.EqualityComparer) { IsKeywordArguments = true };
            keywords[context.CreateAsciiSymbol("freeze")] = ScriptingRuntimeHelpers.BooleanToObject(freeze);

            var initializeCopySite = initializeCopyStorage.GetCallSite("initialize_clone", 2);
            initializeCopySite.Target(initializeCopySite, copy, obj, keywords);

            if (freeze) {
                context.FreezeObject(copy);
            }

            return true;
        }        

        public static long GetFixnumId(int number) {
            return ((long)number << 1) + 1;
        }

        public static long GetObjectId(RubyContext/*!*/ context, object obj) {
            if (obj == null) return NilObjectId;
            if (obj is bool) return (bool)obj ? TrueObjectId : FalseObjectId;
            if (obj is int) return GetFixnumId((int)obj);

            return context.GetInstanceData(obj).ObjectId;
        }

        private const int CachedCharCount = 256;
        private static object[] _charCache = new object[CachedCharCount];

        public static object/*!*/ CharToObject(char ch) {
            return (ch < CachedCharCount) ? (_charCache[(int)ch] ?? (_charCache[(int)ch] = (object)ch)) : (object)ch;
        }

        

        #endregion

        #region Names

        public static bool HasUnmangledName(string/*!*/ name) {
            return !NotUnmangledObject.Contains(name);
        }
        
        public static string TryUnmangleMethodName(string/*!*/ name) {
            return HasUnmangledName(name) ? TryUnmangleName(name) : null;
        }

        /// <summary>
        /// Converts a Ruby name to PascalCase name (foo_bar -> FooBar).
        /// Returns null if the name is not a well-formed Ruby name (it contains upper-case latter or subsequent underscores).
        /// Characters that are not upper case letters are treated as lower-case letters.
        /// </summary>
        public static string TryUnmangleName(string/*!*/ name) {
            ContractUtils.RequiresNotNull(name, "name");
            if (name.Length == 0) {
                return null;
            }

            StringBuilder mangled = new StringBuilder();

            bool lastWasSpecial = false;
            int i = 0, j = 0;
            while (i < name.Length) {
                char c;
                while (j < name.Length && (c = name[j]) != '_') {
                    if (Char.IsUpper(c)) {
                        return null;
                    }
                    j++;
                }

                if (j == i || j == name.Length - 1) {
                    return null;
                }

                if (j - i == 1) {
                    // "ip_f_xxx" -/-> "IPFXxx"
                    if (lastWasSpecial) {
                        return null;
                    }
                    mangled.Append(name[i].ToUpperInvariant());
                    lastWasSpecial = false;
                } else {
                    string special = MapSpecialWord(name, i, j - i);
                    if (special != null) {
                        // "ip_ip" -/-> "IPIP"
                        if (lastWasSpecial) {
                            return null;
                        }
                        mangled.Append(special.ToUpperInvariant());
                        lastWasSpecial = true;
                    } else {
                        mangled.Append(name[i].ToUpperInvariant());
                        mangled.Append(name, i + 1, j - i - 1);
                        lastWasSpecial = false;
                    }
                }

                i = ++j;
            }

            return mangled.ToString();
        }

        public static bool HasMangledName(string/*!*/ name) {
            return !NotMangledObject.Contains(name);
        }

        public static string TryMangleMethodName(string/*!*/ name) {
            return HasMangledName(name) ? TryMangleName(name) : null;
        }

        /// <summary>
        /// Converts a camelCase or PascalCase name to a Ruby name (FooBar -> foo_bar).
        /// Returns null if the name is not in camelCase or PascalCase (FooBAR, foo, etc.).
        /// Characters that are not upper case letters are treated as lower-case letters.
        /// </summary>
        public static string TryMangleName(string/*!*/ name) {
            ContractUtils.RequiresNotNull(name, "name");

            StringBuilder mangled = null;
            int i = 0;
            while (i < name.Length) {
                char c = name[i];
                if (Char.IsUpper(c)) {
                    int j = i + 1;
                    while (j < name.Length && Char.IsUpper(name, j)) {
                        j++;
                    }

                    if (j < name.Length) {
                        j--;
                    }

                    if (mangled == null) {
                        mangled = new StringBuilder();
                        mangled.Append(name, 0, i);
                    } 

                    if (i > 0) {
                        mangled.Append('_');
                    }

                    int count = j - i;
                    if (count == 0) {
                        // NaN{end}, NaNXxx
                        if (i + 2 < name.Length && 
                            Char.IsUpper(name[i + 2]) && 
                            (i + 3 == name.Length || Char.IsUpper(name[i + 3]) && 
                            (i + 4 < name.Length && !Char.IsUpper(name[i + 4])))) {
                            return null;
                        } else {
                            // X{end}, In, NaN, Xml, Html, ...
                            mangled.Append(c.ToLowerInvariant());
                            i++;
                        }
                    } else if (count == 1) {
                        // FXx
                        mangled.Append(c.ToLowerInvariant());
                        i++;
                    } else {
                        // FOXxx, FOOXxx, FOOOXxx, ...
                        string special = MapSpecialWord(name, i, count);
                        if (special != null) {
                            mangled.Append(special.ToLowerInvariant());
                            i = j;
                        } else {
                            return null;
                        }
                    }
                } else if (c == '_') {
                    return null;
                } else {
                    if (mangled != null) {
                        mangled.Append(c);
                    }
                    i++;
                }
            }

            return mangled != null ? mangled.ToString() : null;
        }

        private static string MapSpecialWord(string/*!*/ name, int start, int count) {
            if (count == 2) {
                return IsTwoLetterWord(name, start) ? null : name.Substring(start, count);
            }

            return null;
        }

        private static bool IsTwoLetterWord(string/*!*/ str, int index) {
            int c = LetterPair(str, index);
            switch (c) {
                case ('a' << 8) | 't':
                case ('a' << 8) | 's':
                case ('b' << 8) | 'y':
                case ('d' << 8) | 'o':
                case ('i' << 8) | 'd':
                case ('i' << 8) | 't':
                case ('i' << 8) | 'f':
                case ('i' << 8) | 'n':
                case ('i' << 8) | 's':
                case ('g' << 8) | 'o':
                case ('m' << 8) | 'e':
                case ('m' << 8) | 'y':
                case ('n' << 8) | 'o':
                case ('o' << 8) | 'f':
                case ('o' << 8) | 'k':
                case ('o' << 8) | 'n':
                case ('o' << 8) | 'r':
                case ('t' << 8) | 'o':
                case ('u' << 8) | 'p':
                    return true;
            }
            return false;
        }

        private static int LetterPair(string/*!*/ str, int index) {
            return (str[index + 1] & 0xff00) == 0 ? (str[index].ToLowerInvariant() << 8) | str[index + 1].ToLowerInvariant() : -1;
        }

        /// <summary>
        /// A list of Kernel/Object methods that are expected by common Ruby libraries to work on all objects.
        /// We don't unmangled them to allow Ruby programs work with .NET objects that define e.g. Class property. 
        /// </summary>
        internal static readonly HashSet<string> NotUnmangledObject = new HashSet<string>() {
            // Kernel
            "class",
            "clone",
            "display",
            "dup",
            "extend",
            "freeze",
            "hash",
            // "id", Kernel#id is deprecated
            "initialize",
            "inspect",
            "instance_eval",
            "instance_exec",
            "instance_variable_get",
            "instance_variable_set",
            "instance_variables",
            "method",
            "methods",
            "object_id",
            "private_methods",
            "protected_methods",
            "public_methods",
            "send",
            "singleton_methods",
            "taint",
            // "type", Kernel#type is deprecated
            "untaint",
        };

        internal static readonly HashSet<string> NotMangledObject = new HashSet<string>() {
            "Class",
            "Clone",
            "Display",
            "Dup",
            "Extend",
            "Freeze",
            "Hash",
            // "Id", Kernel#id is deprecated
            "Initialize",
            "Inspect",
            "InstanceEval",
            "InstanceExec",
            "InstanceVariableGet",
            "InstanceVariableSet",
            "InstanceVariables",
            "Method",
            "Methods",
            "ObjectId",
            "PrivateMethods",
            "ProtectedMethods",
            "PublicMethods",
            "Send",
            "SingletonMethods",
            "Taint",
            // "Type", Kernel#type is deprecated
            "Untaint",
        };

        public static void CheckConstantName(string name) {
            if (!Tokenizer.IsConstantName(name)) {
                throw RubyExceptions.CreateNameError(String.Format("wrong constant name {0}", name));
            }
        }

        public static void CheckClassVariableName(string name) {
            if (!Tokenizer.IsClassVariableName(name)) {
                throw RubyExceptions.CreateNameError(String.Format("'{0}' is not allowed as a class variable name", name));
            }
        }

        /// <summary>
        /// The name of an instance variable the runtime keeps for its own purposes: the angle
        /// brackets make it one no Ruby code can name, and RubyContext.GetInstanceVariableNames
        /// leaves it out.
        /// </summary>
        public static string/*!*/ GetHiddenInstanceVariableName(string/*!*/ name) {
            return "<ir>" + name;
        }

        public static void CheckInstanceVariableName(string name) {
            if (!Tokenizer.IsInstanceVariableName(name)) {
                throw RubyExceptions.CreateNameError(String.Format("'{0}' is not allowed as an instance variable name", name));
            }
        }

        /// <summary>
        /// Same checks, but recording NameError#name and NameError#receiver. MRI answers the
        /// name as it was passed (a String here, not a Symbol) and the object it was asked of.
        /// </summary>
        public static void CheckClassVariableName(RubyContext/*!*/ context, object receiver, string/*!*/ name) {
            CheckClassVariableName(context, receiver, name, null);
        }

        public static void CheckInstanceVariableName(RubyContext/*!*/ context, object receiver, string/*!*/ name) {
            CheckInstanceVariableName(context, receiver, name, null);
        }

        /// <summary>
        /// <paramref name="nameArg"/> is the Symbol or String the caller was given; MRI hands that
        /// very object back as NameError#name.
        /// </summary>
        public static void CheckClassVariableName(RubyContext/*!*/ context, object receiver, string/*!*/ name, object nameArg) {
            if (!Tokenizer.IsClassVariableName(name)) {
                throw RubyExceptions.WithNameAndReceiver(
                    RubyExceptions.CreateNameError(String.Format("'{0}' is not allowed as a class variable name", name)),
                    GetVariableNameForError(context, name, nameArg), receiver);
            }
        }

        public static void CheckInstanceVariableName(RubyContext/*!*/ context, object receiver, string/*!*/ name, object nameArg) {
            if (!Tokenizer.IsInstanceVariableName(name)) {
                throw RubyExceptions.WithNameAndReceiver(
                    RubyExceptions.CreateNameError(String.Format("'{0}' is not allowed as an instance variable name", name)),
                    GetVariableNameForError(context, name, nameArg), receiver);
            }
        }

        private static object/*!*/ GetVariableNameForError(RubyContext/*!*/ context, string/*!*/ name, object nameArg) {
            return nameArg is MutableString || nameArg is RubySymbol ? nameArg : MutableString.Create(name, context.GetIdentifierEncoding());
        }

        #endregion

        #region Constants

        // thread-safe:
        public static object GetConstant(RubyGlobalScope/*!*/ globalScope, RubyModule/*!*/ owner, string/*!*/ name, bool lookupObject) {
            Assert.NotNull(globalScope, owner, name);

            RubyContext context = owner.Context;
            object value = null;
            bool found = false;
            RubyModule deprecatedOwner = null;

            using (context.ClassHierarchyLocker()) {
                ConstantStorage storage;
                if (owner.TryResolveConstantNoLock(globalScope, name, out storage)) {
                    value = storage.Value;
                    found = true;
                    deprecatedOwner = owner.GetDeprecatedConstantOwnerNoLock(name);
                } else {
                    RubyClass objectClass = context.ObjectClass;
                    if (owner != objectClass && lookupObject && objectClass.TryResolveConstantNoLock(globalScope, name, out storage)) {
                        value = storage.Value;
                        found = true;
                        deprecatedOwner = objectClass.GetDeprecatedConstantOwnerNoLock(name);
                    }
                }
            }

            if (found) {
                // outside the lock: Warning.warn can be overridden in Ruby
                if (deprecatedOwner != null) {
                    context.ReportConstantDeprecation(deprecatedOwner, name);
                }
                return value;
            }

            RubyUtils.CheckConstantName(name);
            return owner.ConstantMissing(name);
        }

        public static void SetConstant(RubyModule/*!*/ owner, string/*!*/ name, object value) {
            SetConstant(owner, name, value, null, 0);
        }

        /// <summary>
        /// Location of the Ruby code that called the currently executing builtin from <paramref name="scope"/>:
        /// what precompiled code noted for the call (PrecompiledCode.NoteCallLocation), else as below.
        /// </summary>
        public static bool TryGetCallerSourceLocation(RubyScope/*!*/ scope, out string sourcePath, out int sourceLine) {
            if (Precompiled.PrecompiledCode.TryTakeCallLocation(scope, out sourcePath, out sourceLine)) {
                return true;
            }
            return TryGetCallerSourceLocation(scope.RubyContext, out sourcePath, out sourceLine);
        }

        /// <summary>
        /// Location of the Ruby frame that called the currently executing builtin, taken from the CLR stack.
        /// Only for cold paths (Module#const_set, Module#autoload): capturing a stack trace with file info
        /// costs on the order of a millisecond.
        /// </summary>
        public static bool TryGetCallerSourceLocation(RubyContext/*!*/ context, out string sourcePath, out int sourceLine) {
            sourcePath = null;
            sourceLine = 0;

            // Nothing but the core prelude is running, and its frames are not in a backtrace: the
            // interpreted frame below would be one of the prelude's, and the walk would come back
            // empty, after ~1ms, for each of its const_set calls (Errno and Encoding make hundreds).
            if (context.Loader.IsLoadingCorePrelude) {
                return false;
            }

            // Fast path: the interpreter's frame chain already knows where we are; a CLR stack walk
            // with file info costs ~1ms and this is called once per Module#const_set.
            if (RubyStackTraceBuilder.TryGetInterpretedFrameLocation(out sourcePath, out sourceLine)) {
                return true;
            }
            sourcePath = null;
            sourceLine = 0;

            // Startup-library code is running, and its frames are not in a backtrace either.
            if (Precompiled.PrecompiledCode.IsRunningOnThread) {
                return false;
            }

            RubyArray trace;
            try {
                trace = RubyExceptionData.CreateBacktrace(context, 0);
            } catch (Exception) {
                return false;
            }

            if (trace.Count == 0) {
                return false;
            }

            string entry = trace[0].ToString();

            // "<path>:<line>" optionally followed by ":in `<method>'"
            int end = entry.IndexOf(":in ", StringComparison.Ordinal);
            if (end < 0) {
                end = entry.Length;
            }

            int colon = entry.LastIndexOf(':', end - 1);
            if (colon <= 0) {
                return false;
            }

            int line;
            if (!Int32.TryParse(entry.Substring(colon + 1, end - colon - 1), out line)) {
                return false;
            }

            sourcePath = entry.Substring(0, colon);
            sourceLine = line;
            return true;
        }

        public static void SetConstant(RubyModule/*!*/ owner, string/*!*/ name, object value, string sourcePath, int sourceLine) {
            SetConstant(owner, name, value, sourcePath, sourceLine, RubyEncoding.UTF8);
        }

        public static void SetConstant(RubyModule/*!*/ owner, string/*!*/ name, object value, string sourcePath, int sourceLine,
            RubyEncoding/*!*/ encoding) {
            Assert.NotNull(owner, name);

            owner.SetConstantLocation(name, sourcePath, sourceLine);
            owner.SetConstantEncoding(name, encoding);

            if (owner.SetConstantChecked(name, value)) {
                // MRI names the owner unless it is Object: "already initialized constant M::X",
                // and an anonymous owner by its inspect form, "#<Module:0x...>::X"
                owner.Context.ReportWarning("already initialized constant " + owner.MakeNestedModuleName(name));
            }

            // Initializes anonymous module's name, publishes the module:
            RubyModule module = value as RubyModule;
            if (module != null) {
                // A module reachable from Object gets a permanent name; one stored in a constant of an anonymous
                // module only gets a temporary one, which a later set_temporary_name (or the outer module becoming
                // permanently named) may replace. A name that is already permanent never changes.
                if (!module.HasPermanentName) {
                    bool permanent = owner.IsObjectClass || owner.HasPermanentName;
                    if (permanent || module.Name == null) {
                        module.SetName(owner.MakeNestedModuleName(name), permanent);
                    }
                }
                if (owner.IsObjectClass) {
                    module.Publish(name);
                }
            }

            owner.ConstantAdded(name);
        }

        #endregion

        #region Methods

        /// <summary>
        /// Methods MRI forces to be private however they are defined: the initializers and respond_to_missing?.
        /// </summary>
        public static bool IsForcedPrivateMethod(string/*!*/ methodName) {
            return methodName == Symbols.Initialize
                || methodName == Symbols.InitializeCopy
                || methodName == "initialize_clone"
                || methodName == "initialize_dup"
                || methodName == "respond_to_missing?";
        }

        public static RubyMethodVisibility GetSpecialMethodVisibility(RubyMethodVisibility/*!*/ visibility, string/*!*/ methodName) {
            return IsForcedPrivateMethod(methodName) ? RubyMethodVisibility.Private : visibility;
        }

        internal static string ToClrOperatorName(string/*!*/ rubyName) {
            switch (rubyName) {
                case "+": return "op_Addition";
                case "-": return "op_Subtraction";
                case "/": return "op_Division";
                case "*": return "op_Multiply";
                case "%": return "op_Modulus";
                case "==": return "op_Equality";
                case "!=": return "op_Inequality";
                case ">": return "op_GreaterThan";
                case ">=": return "op_GreaterThanOrEqual";
                case "<": return "op_LessThan";
                case "<=": return "op_LessThanOrEqual";
                case "-@": return "op_UnaryNegation";
                case "+@": return "op_UnaryPlus";
                case "<<": return "op_LeftShift";
                case ">>": return "op_RightShift";
                case "^": return "op_ExclusiveOr";
                case "~": return "op_OnesComplement";
                case "&": return "op_BitwiseAnd";
                case "|": return "op_BitwiseOr";
                
                case "**": return "Power";
                case "<=>": return "Compare";

                default:
                    return null;
            }
        }

        internal static string ToRubyOperatorName(string/*!*/ clrName) {
            switch (clrName) {
                case "op_Addition": return "+";
                case "op_Subtraction": return "-";
                case "op_Division": return "/";
                case "op_Multiply": return "*";
                case "op_Modulus": return "%";
                case "op_Equality": return "==";
                case "op_Inequality": return "!=";
                case "op_GreaterThan": return ">";
                case "op_GreaterThanOrEqual": return ">=";
                case "op_LessThan": return "<";
                case "op_LessThanOrEqual": return "<=";
                case "op_UnaryNegation": return "-@";
                case "op_UnaryPlus": return "+@";
                case "op_LeftShift": return "<<";
                case "op_RightShift": return ">>";
                case "op_BitwiseAnd": return "&";
                case "op_BitwiseOr": return "|";
                case "op_ExclusiveOr": return "^";
                case "op_OnesComplement": return "~";

                case "Power": return "**";
                case "Compare": return "<=>";
                
                default:
                    return null;
            }
        }

        internal static string MapOperator(OverloadInfo/*!*/ method) {
            return method.IsStatic && method.IsSpecialName ? ToRubyOperatorName(method.Name) : null;
        }

        internal static string MapOperator(MethodInfo/*!*/ method) {
            return method.IsStatic && method.IsSpecialName ? ToRubyOperatorName(method.Name) : null;
        }

        internal static bool IsOperator(OverloadInfo/*!*/ method) {
            return MapOperator(method) != null;
        }

        internal static string MapOperator(ExpressionType op) {
            string methodName;
            TryMapOperator(op, out methodName);
            return methodName;
        }

        internal static int TryMapOperator(ExpressionType op, out string methodName) {
            switch (op) {
                case ExpressionType.Add: methodName = "+"; return 2;
                case ExpressionType.Subtract: methodName = "-"; return 2;
                case ExpressionType.Divide: methodName = "/"; return 2;
                case ExpressionType.Multiply: methodName = "*"; return 2;
                case ExpressionType.Modulo: methodName = "%"; return 2;
                case ExpressionType.Equal: methodName = "=="; return 2;
                case ExpressionType.NotEqual: methodName = "!="; return 2;
                case ExpressionType.GreaterThan: methodName = ">"; return 2;
                case ExpressionType.GreaterThanOrEqual: methodName = ">="; return 2;
                case ExpressionType.LessThan: methodName = "<"; return 2;
                case ExpressionType.LessThanOrEqual: methodName = "<="; return 2;
                case ExpressionType.LeftShift: methodName = "<<"; return 2;
                case ExpressionType.RightShift: methodName = ">>"; return 2;
                case ExpressionType.And: methodName = "&"; return 2;
                case ExpressionType.Or: methodName = "|"; return 2;
                case ExpressionType.ExclusiveOr: methodName = "^"; return 2;
                case ExpressionType.Power: methodName = "**"; return 2;

                case ExpressionType.Negate: methodName = "-@"; return 1;
                case ExpressionType.UnaryPlus: methodName = "+@"; return 1;
                case ExpressionType.OnesComplement: methodName = "~"; return 1;
                case ExpressionType.Not: methodName = "!"; return 1;
            }

            methodName = null;
            return 0;
        }

        internal static int TryMapOperator(string/*!*/ methodName, out ExpressionType op) {
            switch (methodName) {
                case "+": op = ExpressionType.Add; return 2;
                case "-": op = ExpressionType.Subtract; return 2;
                case "/": op = ExpressionType.Divide; return 2;
                case "*": op = ExpressionType.Multiply; return 2;
                case "%": op = ExpressionType.Modulo; return 2;
                case "==": op = ExpressionType.Equal; return 2;
                case "!=": op = ExpressionType.NotEqual; return 2;
                case ">": op = ExpressionType.GreaterThan; return 2;
                case ">=": op = ExpressionType.GreaterThanOrEqual; return 2;
                case "<": op = ExpressionType.LessThan; return 2;
                case "<=": op = ExpressionType.LessThanOrEqual; return 2;
                case "<<": op = ExpressionType.LeftShift; return 2;
                case ">>": op = ExpressionType.RightShift; return 2;
                case "&": op = ExpressionType.And; return 2;
                case "|": op = ExpressionType.Or; return 2;
                case "^": op = ExpressionType.ExclusiveOr; return 2;
                case "**": op = ExpressionType.Power; return 2;

                case "-@": op = ExpressionType.Negate; return 1;
                case "+@": op = ExpressionType.UnaryPlus; return 1;
                case "~": op = ExpressionType.OnesComplement; return 1;
                case "!": op = ExpressionType.Not; return 1;
            }

            op = default(ExpressionType);
            return 0;
        }

        #endregion

        #region Modules, Classes
        
        internal static RubyModule/*!*/ GetModuleFromObject(RubyScope/*!*/ scope, object obj) {
            RubyModule module = obj as RubyModule;
            if (module == null) {
                // a type group (System::Nullable is Nullable and Nullable<T>) qualifies a constant as its non-generic member:
                var typeGroup = obj as TypeGroup;
                Type nonGenericType;
                if (typeGroup != null && typeGroup.TryGetNonGenericType(out nonGenericType)) {
                    return scope.RubyContext.GetModule(nonGenericType);
                }
                throw CreateNotModuleException(scope, obj);
            }
            return module;
        }

        internal static Exception/*!*/ CreateNotModuleException(RubyScope/*!*/ scope, object obj) {
            // MRI shows the value, not its class: "123 is not a class/module"
            return RubyExceptions.CreateTypeError(String.Format("{0} is not a class/module", scope.RubyContext.Inspect(obj)));
        }

        public static void RequireMixins(RubyModule/*!*/ target, params RubyModule[]/*!*/ modules) {
            foreach (RubyModule module in modules) {
                if (module == null) {
                    throw RubyExceptions.CreateTypeError("wrong argument type nil (expected Module)");
                }

                if (module.IsClass) {
                    throw RubyExceptions.CreateTypeError("wrong argument type Class (expected Module)");
                }

                // including a module that already has the target among its ancestors would make a cycle
                // (MRI checks the whole chain, not just the module itself); the monitor is reentrant:
                bool cyclic;
                using (target.Context.ClassHierarchyLocker()) {
                    cyclic = module == target || module.HasAncestorNoLock(target);
                }
                if (cyclic) {
                    throw RubyExceptions.CreateArgumentError("cyclic include detected");
                }

                if (module.Context != target.Context) {
                    throw RubyExceptions.CreateTypeError(String.Format("cannot mix a foreign module '{0}' into '{1}' (runtime mismatch)",
                        module.GetName(target.Context), target.GetName(module.Context)
                    ));
                }
            }
        }

        public static void RequirePrepends(RubyModule/*!*/ target, params RubyModule[]/*!*/ modules) {
            foreach (RubyModule module in modules) {
                if (module == null) {
                    throw RubyExceptions.CreateTypeError("wrong argument type nil (expected Module)");
                }

                if (module.IsClass) {
                    throw RubyExceptions.CreateTypeError("wrong argument type Class (expected Module)");
                }

                // the monitor is reentrant, so this is safe whether or not the caller already holds the lock:
                bool cyclic;
                using (target.Context.ClassHierarchyLocker()) {
                    cyclic = module == target || module.HasAncestorNoLock(target);
                }
                if (cyclic) {
                    throw RubyExceptions.CreateArgumentError("cyclic prepend detected");
                }

                if (module.Context != target.Context) {
                    throw RubyExceptions.CreateTypeError(String.Format("cannot mix a foreign module '{0}' into '{1}' (runtime mismatch)",
                        module.GetName(target.Context), target.GetName(module.Context)
                    ));
                }
            }
        }

        #endregion

        #region Tracking operations that have the potential for infinite recursion

        public static readonly MutableString InfiniteRecursionMarker = MutableString.CreateAscii("[...]").Freeze();

        public class RecursionTracker {
            // The set of objects an operation is already inside must be per thread: the
            // trackers themselves are process-wide singletons, so a Dictionary held here is
            // read and written by every thread at once.  [ThreadStatic] does not make it per
            // thread - the attribute only has an effect on a *static* field and is silently
            // ignored on an instance field, which is what this used to be.  Two threads
            // comparing or inspecting arrays (RubyGems installs each gem on a thread of its
            // own) corrupted the dictionary and failed with IndexOutOfRangeException or
            // "a concurrent update was performed on this collection".
            private readonly System.Threading.ThreadLocal<Dictionary<object, bool>>/*!*/ _infiniteTracker =
                new System.Threading.ThreadLocal<Dictionary<object, bool>>(
                    () => new Dictionary<object, bool>(ReferenceEqualityComparer<object>.Instance)
                );

            private Dictionary<object, bool> TryPushInfinite(object obj) {
                Dictionary<object, bool> infinite = _infiniteTracker.Value;
                if (infinite.ContainsKey(obj)) {
                    return null;
                }
                infinite.Add(obj, true);
                return infinite;
            }

            public IDisposable TrackObject(object obj) {
                obj = CustomStringDictionary.NullToObj(obj);
                Dictionary<object, bool> tracker = TryPushInfinite(obj);
                return (tracker == null) ? null : new RecursionHandle(tracker, obj);
            }

            private class RecursionHandle : IDisposable {
                private readonly Dictionary<object, bool>/*!*/ _tracker;
                private readonly object _obj;

                internal RecursionHandle(Dictionary<object, bool>/*!*/ tracker, object obj) {
                    _tracker = tracker;
                    _obj = obj;
                }

                public void Dispose() {
                    _tracker.Remove(_obj);
                }
            }
        }

        [MultiRuntimeAware]
        private static readonly RecursionTracker _infiniteInspectTracker = new RecursionTracker();

        public static RecursionTracker InfiniteInspectTracker {
            get { return _infiniteInspectTracker; }
        }

        [MultiRuntimeAware]
        private static readonly RecursionTracker _infiniteToSTracker = new RecursionTracker();

        public static RecursionTracker InfiniteToSTracker {
            get { return _infiniteToSTracker; }
        }

        #endregion

        #region Arrays, Hashes

        public static object SetHashElement(RubyContext/*!*/ context, IDictionary<object, object>/*!*/ obj, object key, object value) {
            MutableString str = key as MutableString;
            Hash hash = obj as Hash;
            // an identity hash stores the string object it was given, it must not copy it;
            // neither is there a reason to copy an already frozen string
            if (str != null && !str.IsFrozen && (hash == null || !hash.ComparesByIdentity)) {
                key = str.Duplicate(context, false, str.Clone()).Freeze();
            } else {
                key = CustomStringDictionary.NullToObj(key);
            }
            return obj[key] = value;
        }

        public static Hash/*!*/ SetHashElements(RubyContext/*!*/ context, Hash/*!*/ hash, object[]/*!*/ items) {
            Assert.NotNull(context, hash, items);
            Debug.Assert(items != null && items.Length % 2 == 0);

            for (int i = 0; i < items.Length; i += 2) {
                Debug.Assert(i + 1 < items.Length);
                SetHashElement(context, hash, items[i], items[i + 1]);
            }

            return hash;
        }

        #endregion

        #region Evals

        //                         scope                   parent scope            self
        // M.module_eval {}        block                   current                 M 
        // x.instance_eval {}      block                   current                 x
        // M.module_eval ""        ModEval(module: M)      current                 M
        // x.instance_eval ""      ModEval(module: S(x))   current                 x 
        // eval "", binding        binding.scope           binding.scope.parent    binding.scope.self
        //

#if DEBUG
        private static int _stringEvalCounter;
#endif

        public static RubyCompilerOptions/*!*/ CreateCompilerOptionsForEval(RubyScope/*!*/ targetScope, int line) {
            return CreateCompilerOptionsForEval(targetScope, targetScope.GetInnerMostMethodScope(), false, line);
        }

        private static RubyCompilerOptions/*!*/ CreateCompilerOptionsForEval(RubyScope/*!*/ targetScope, RubyMethodScope methodScope,
            bool isModuleEval, int line) {

            int blockLevels;
            string baseLabel = targetScope.GetFrameBaseLabel(out blockLevels);

            return new RubyCompilerOptions(targetScope.RubyContext.RubyOptions) {
                IsEval = true,
                EvalFrameBaseLabel = baseLabel,
                EvalFrameBlockLevels = blockLevels,
                FactoryKind = isModuleEval ? TopScopeFactoryKind.ModuleEval : TopScopeFactoryKind.None,
                LocalNames = targetScope.GetVisibleLocalNames(),
                TopLevelMethodName = (methodScope != null) ? methodScope.DefinitionName : null,
                TopLevelParameterNames = (methodScope != null) ? methodScope.GetVisibleParameterNames() : null,
                TopLevelHasUnsplatParameter = (methodScope != null) ? methodScope.HasUnsplatParameter : false,
                InitialLocation = new SourceLocation(0, line <= 0 ? 1 : line, 1),
            };
        }

        // eval("...", b, file, line) with a line below 1: a SourceLocation (and the CLR's debug info)
        // can't start there, so the code is compiled from line 1 and the difference travels in the
        // document's file name, after this marker, to be taken back out wherever a line is reported.
        private const char EvalLineOffsetMarker = '\u0001';

        internal static string/*!*/ EncodeEvalLineOffset(string/*!*/ path, int lineOffset) {
            return path + EvalLineOffsetMarker + lineOffset.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Removes the line offset of an eval with a line below 1 from a document file name (see
        /// EncodeEvalLineOffset) and returns it; 0 for any other name.
        /// </summary>
        public static int DecodeEvalLineOffset(ref string path) {
            int marker;
            if (path == null || (marker = path.LastIndexOf(EvalLineOffsetMarker)) < 0) {
                return 0;
            }
            // only a marker followed by nothing but the offset counts: a real name may contain \u0001
            int offset;
            if (!Int32.TryParse(path.Substring(marker + 1), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out offset)) {
                return 0;
            }
            path = path.Substring(0, marker);
            return offset;
        }

        private static SourceUnit/*!*/ CreateRubySourceUnit(RubyContext/*!*/ context, MutableString/*!*/ code, string path) {
            return context.CreateSourceUnit(new BinaryContentProvider(code.ToByteArray()), path, code.Encoding.Encoding, SourceCodeKind.File);
        }

        /// <summary>
        /// What `__FILE__' is inside an eval that was not told a file name: MRI names the place
        /// the eval was written, as "(eval at file:line)". A nested eval nests the same way,
        /// because the outer one's name is the file the inner one was written in.
        /// </summary>
        private static string/*!*/ DefaultEvalFileName(RubyContext/*!*/ context) {
            string path;
            int line;
            if (!context.TryGetCurrentSourceLocation(out path, out line) || path == null) {
                return "(eval)";
            }
            return "(eval at " + path + ":" + line + ")";
        }

        public static object Evaluate(MutableString/*!*/ code, RubyScope/*!*/ targetScope, object self, RubyModule module, MutableString file, int line) {
            return Evaluate(code, targetScope, self, module, null, file, line);
        }

        public static object Evaluate(MutableString/*!*/ code, RubyScope/*!*/ targetScope, object self, RubyModule module,
            RubyModule lexicalConstantModule, MutableString file, int line) {
            Assert.NotNull(code, targetScope);

            // the code can make a Proc, Binding or method over the scope (a block scope is then not reused)
            targetScope.MarkCaptured();

            RubyContext context = targetScope.RubyContext;
            RubyMethodScope methodScope = targetScope.GetInnerMostMethodScope();

#if DEBUG
            Utils.Log(Interlocked.Increment(ref _stringEvalCounter).ToString() + ": " + (file != null ? file.ToString() : "?") + " : " + line, "EVAL");
#endif

            // we want to create a new top-level local scope:
            var options = CreateCompilerOptionsForEval(targetScope, methodScope, module != null, line);
            options.EvalSourceEncoding = code.Encoding;
            string path = file != null ? file.ConvertToString() : DefaultEvalFileName(context);
            var source = CreateRubySourceUnit(context, code, (line <= 0) ? EncodeEvalLineOffset(path, line - 1) : path);

            Expression<EvalEntryPointDelegate> lambda;
            try {
                lambda = context.ParseSourceCode<EvalEntryPointDelegate>(source, options, context.RuntimeErrorSink);
            } catch (SyntaxError e) {
                Utils.Log(e.Message, "EVAL_ERROR");
                Utils.Log(new String('-', 50), "EVAL_ERROR");
                Utils.Log(source.GetCode(), "EVAL_ERROR");
                Utils.Log(new String('-', 50), "EVAL_ERROR");

                // MRI names the file and the line the error is on in front of the message, so
                // that code which evaluated a string knows which string and where in it.
                if (e.HasLineInfo && e.File != null) {
                    // A SourceLocation cannot start before line 1, so a zero or negative starting
                    // line was compiled from 1 and is put back here: eval with a line of -100
                    // reports its first line as -100, which is what MRI does.
                    int reportedLine = (line <= 0) ? e.Line - 1 + line : e.Line;
                    string errorFile = e.File;
                    DecodeEvalLineOffset(ref errorFile);
                    throw new SyntaxError(
                        String.Format("{0}:{1}: {2}", errorFile, reportedLine, e.Message),
                        errorFile, reportedLine, e.Column, e.LineSourceCode
                    );
                }
                throw;
            }
            Debug.Assert(lambda != null);

            var compiled = (EvalEntryPointDelegate)RubyScriptCode.CompileLambda(lambda, context);
            var blockParameter = (methodScope != null) ? methodScope.BlockParameter : null;

            if ((TracePoint.ActiveEvents & (int)TraceEvents.ScriptCompiled) != 0) {
                TracePoint.OnScriptCompiled(targetScope, context, code);
            }

            // module-eval: gets a scope of its own, which starts out public
            if (module != null) {
                var moduleScope = CreateModuleEvalScope(targetScope, self, module);
                if (lexicalConstantModule != null) {
                    moduleScope.SetLexicalConstantModule(lexicalConstantModule);
                }
                return compiled(moduleScope, self, module, blockParameter);
            }

            // A plain string eval runs in the scope it was called from, so it has nowhere of its
            // own to keep `private' or `module_function'.  MRI confines a visibility modifier used
            // inside the string to the string: `eval "module_function"' does not reach a `def'
            // written after it.  The state is inherited on the way in - a `def' inside the string
            // does see a modifier set outside it - so saving and restoring it is the whole rule.
            var attributesScope = targetScope.GetMethodAttributesDefinitionScope();
            var attributes = attributesScope.MethodAttributes;
            try {
                return compiled(targetScope, self, module, blockParameter);
            } finally {
                attributesScope.MethodAttributes = attributes;
            }
        }

        private static RubyModuleEvalScope/*!*/ CreateModuleEvalScope(RubyScope/*!*/ parent, object self, RubyModule/*!*/ module) {
            // A module-eval scope keeps its new locals in its parent; that parent is a scope of the
            // string's own, so that they are not left behind in the caller's frame.
            var scope = new RubyModuleEvalScope(new RubyBindingCopyScope(parent, parent.SelfObject), module, self);
            scope.SetDebugName("instance/module-eval");
            return scope;
        }

        /// <summary>
        /// Runs a Module#refine block.  Inside it the refinements of <paramref name="holder"/> are active -
        /// all of them, including ones added by later refine calls on the same module, which is why the
        /// activation names the module rather than snapshotting its refinements.  The activation rides on
        /// the Proc because a refine block's lexical extent has no scope object until the block is entered.
        /// </summary>
        public static object EvaluateRefinementBlock(RubyModule/*!*/ holder, RubyModule/*!*/ refinement, BlockParam/*!*/ block) {
            Proc proc = block.Proc;
            RefinementActivation saved = proc.RefinementOverride;
            RefinementActivation active = RefinementActivation.CreateSingle(proc.LocalScope.GetActiveRefinements(), holder);

            // While the count is not zero the lexical walk looks at every block's override, and the rules that
            // read a method's record for a block in it fail (RubyScope.GetLexicalRefinements, RubyCallAction.Resolve).
            // Nothing that can throw comes between the increment and the try, whose finally undoes it: a count left
            // above zero would turn those fast paths off for the rest of the process.
            Interlocked.Increment(ref RubyOps.RefineBlocksRunning);
            try {
                proc.RefinementOverride = active;
                holder.Context.RefinementVersion++;
                return EvaluateInModule(refinement, block, null);
            } finally {
                proc.RefinementOverride = saved;
                holder.Context.RefinementVersion++;
                Interlocked.Decrement(ref RubyOps.RefineBlocksRunning);
            }
        }

        public static object EvaluateInModule(RubyModule/*!*/ self, BlockParam/*!*/ block, object[] args) {
            object result;
            EvaluateBlock(block, self, self, args, out result);
            return result;
        }

        public static object EvaluateInModule(RubyModule/*!*/ self, BlockParam/*!*/ block, object[] args, object defaultReturnValue) {
            object result;
            return EvaluateBlock(block, self, self, args, out result) ? result : defaultReturnValue;
        }

        public static object EvaluateInSingleton(object self, BlockParam/*!*/ block, object[] args) {
            // MRI runs the block against anything and only refuses a `def' inside it, so the
            // question here is what the runtime can make a singleton of at all - not what user
            // code is allowed to hang methods on. A Float, a Bignum and a frozen String are all
            // perfectly good receivers for #instance_eval.
            //
            // An Integer and a Symbol have no singleton class at all, not even in MRI, so the
            // block looks methods up through the dummy singleton standing in front of their
            // class instead. It sits directly below the class, so a private method of Integer
            // is still reached; a `def' landing in it raises, which is what MRI does too.
            //
            // MRI makes the singleton class of an object only when the block defines something into it (see
            // BlockParam.SetLazySingletonMethodLookup); a module's is always there.
            RubyModule lookupModule;
            if (RubyUtils.HasSingletonClass(self)) {
                var context = block.RubyContext;
                if (self is RubyModule) {
                    lookupModule = context.GetOrCreateSingletonClass(self);
                } else {
                    RubyClass immediate = context.GetImmediateClassOf(self);
                    lookupModule = immediate.IsSingletonClass ? immediate : null;
                }
            } else {
                lookupModule = block.RubyContext.GetImmediateClassOf(self).GetDummySingletonClass();
            }

            object result;
            EvaluateBlock(block, lookupModule, self, args, out result);
            return result;
        }

        // A null module: the singleton class of self, made when a definition needs it.
        private static bool EvaluateBlock(BlockParam/*!*/ block, RubyModule module, object self, object[] args, out object result) {
            Assert.NotNull(block);
            if (module != null) {
                block.MethodLookupModule = module;
            } else {
                block.SetLazySingletonMethodLookup();
            }

            if (args != null) {
                result = RubyOps.Yield(args, null, self, block);
            } else {
                result = RubyOps.Yield0(null, self, block);
            }

            return block.BlockJumped(result);
        }

        #endregion

        #region Object Construction

        private static readonly Type[] _ccTypes1 = new Type[] { typeof(RubyClass) };
        private static readonly Type[] _ccTypes2 = new Type[] { typeof(RubyContext) };
        private static readonly Type[] _serializableTypeSignature = new Type[] { typeof(SerializationInfo), typeof(StreamingContext) };

        public static readonly string SerializationInfoClassKey = "#immediateClass";

        public static object/*!*/ CreateObject(RubyClass/*!*/ theclass, IEnumerable<KeyValuePair<string, object>>/*!*/ attributes) {
            Assert.NotNull(theclass, attributes);

            Type baseType = theclass.GetUnderlyingSystemType();
            object obj;
            if (typeof(ISerializable).IsAssignableFrom(baseType)) {
                BindingFlags bindingFlags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
                ConstructorInfo ci = baseType.GetConstructor(bindingFlags, null, _serializableTypeSignature, null);
                if (ci == null) {
                string message = String.Format("Class {0} does not have a valid deserializing constructor", baseType.FullName);
                    throw new NotSupportedException(message);
                }
#pragma warning disable SYSLIB0050 // Marshal.load hands the instance variables to the deserialization constructor; no formatter involved
                SerializationInfo info = new SerializationInfo(baseType, new FormatterConverter());
                info.AddValue(SerializationInfoClassKey, theclass);
                foreach (var pair in attributes) {
                    info.AddValue(pair.Key, pair.Value);
                }
                obj = ci.Invoke(new object[2] { info, new StreamingContext(StreamingContextStates.Other, theclass) });
#pragma warning restore SYSLIB0050
            } else {
                obj = CreateObject(theclass);
                foreach (var pair in attributes) {
                    theclass.Context.SetInstanceVariable(obj, pair.Key, pair.Value);
                }
            }
            return obj;
        }

        private static bool IsAvailable(MethodBase method) {
            return method != null && !method.IsPrivate && !method.IsAssembly && !method.IsFamilyAndAssembly;
        }

        // TODO: remove
        public static object/*!*/ CreateObject(RubyClass/*!*/ theClass) {
            Assert.NotNull(theClass);

            Type baseType = theClass.GetUnderlyingSystemType();
            if (baseType == typeof(RubyStruct)) {
                return RubyStruct.Create(theClass);
            }

            object result;
            BindingFlags bindingFlags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
            ConstructorInfo ci;
            if (IsAvailable(ci = baseType.GetConstructor(bindingFlags, null, Type.EmptyTypes, null))) {
                result = ci.Invoke(new object[0] { });
            } else if (IsAvailable(ci = baseType.GetConstructor(bindingFlags, null, _ccTypes1, null))) {
                result = ci.Invoke(new object[1] { theClass });
            } else if (IsAvailable(ci = baseType.GetConstructor(bindingFlags, null, _ccTypes2, null))) {
                result = ci.Invoke(new object[1] { theClass.Context });
            } else {
                string message = String.Format("Class {0} does not have a valid constructor", theClass.Name);
                throw new NotSupportedException(message);
            }
            return result;
        }

        #endregion

        #region Call Site Storage Extensions

        public static CallSite<TCallSiteFunc>/*!*/ GetCallSite<TCallSiteFunc>(ref CallSite<TCallSiteFunc>/*!*/ site, RubyContext/*!*/ context,
            string/*!*/ methodName, int argumentCount) where TCallSiteFunc : class {

            if (site == null) {
                Interlocked.CompareExchange(ref site,
                    CallSite<TCallSiteFunc>.Create(RubyCallAction.Make(context, methodName, RubyCallSignature.WithImplicitSelf(argumentCount))), null);
            }
            return site;
        }

        public static CallSite<TCallSiteFunc>/*!*/ GetCallSite<TCallSiteFunc>(ref CallSite<TCallSiteFunc>/*!*/ site, RubyContext/*!*/ context,
            string/*!*/ methodName, RubyCallSignature signature) where TCallSiteFunc : class {

            if (site == null) {
                Interlocked.CompareExchange(ref site,
                    CallSite<TCallSiteFunc>.Create(RubyCallAction.Make(context, methodName, signature)), null);
            }
            return site;
        }

        public static CallSite<Func<CallSite, object, TResult>>/*!*/ GetCallSite<TResult>(
            ref CallSite<Func<CallSite, object, TResult>>/*!*/ site, RubyConversionAction/*!*/ conversion) {

            if (site == null) {
                Interlocked.CompareExchange(ref site, CallSite<Func<CallSite, object, TResult>>.Create(conversion), null);
            }
            return site;
        }

        #endregion

        #region Exceptions

#if FEATURE_THREAD
#if FEATURE_EXCEPTION_STATE
        // Unwinds only discarded fiber stacks. Set before throwing, cleared by the worker
        // boundary after CLR finally cleanup has completed; Ruby ensure bodies consult it.
        public sealed class FiberReclaimSignal : StackUnwinder {
            public FiberReclaimSignal() : base(null) { }
        }

        [ThreadStatic]
        private static bool _reclaimingFiber;

        public static bool IsReclaimingFiber { get { return _reclaimingFiber; } }

        public static void BeginFiberReclamation() {
            if (!_reclaimingFiber) {
                _reclaimingFiber = true;
                RequestSafePoint();
            }
            throw new FiberReclaimSignal();
        }

        public static void EndFiberReclamation() {
            if (_reclaimingFiber) {
                _reclaimingFiber = false;
                SafePointRequestDone();
            }
        }

        /// <summary>
        /// Control-flow exception used to unwind a thread that was killed with Thread#kill / #exit / #terminate.
        /// Deriving from StackUnwinder makes RubyOps.CanRescue skip it, so - exactly like in MRI - a killed
        /// thread cannot be caught by "rescue Exception" and does not touch $!, while ensure clauses (which
        /// compile to CLR finally blocks) still run.
        /// </summary>
        public sealed class ThreadExitSignal : StackUnwinder {
            public ThreadExitSignal() : base(null) { }
        }

        // Thread#raise and Thread#kill used to be implemented with System.Threading.Thread.Abort, which throws
        // PlatformNotSupportedException on .NET Core (and took the whole process down via Thread.ResetAbort).
        // There is no way to inject an exception into another thread on this runtime, so delivery is cooperative:
        // the exception is parked here and the target thread is nudged with Thread.Interrupt, which does work on
        // .NET Core and unblocks any managed wait (Monitor.Wait, WaitHandle.WaitOne, Thread.Sleep, Thread.Join).
        // The blocking primitives that IronRuby itself uses translate ThreadInterruptedException into the parked
        // exception; every other safe point calls CheckAsyncException. A thread that is spinning in pure Ruby code
        // without ever blocking cannot be interrupted - see the comment on CheckAsyncException.
        private static readonly Dictionary<int, Exception> _pendingAsyncExceptions = new Dictionary<int, Exception>();

        public static void RaiseAsyncException(Thread thread, Exception e) {
            if (thread == Thread.CurrentThread) {
                throw e;
            }

            ParkAsyncException(thread, e);
            NudgeThread(thread);
        }

        /// <summary>
        /// The first half of RaiseAsyncException: parks <paramref name="e"/> for <paramref name="thread"/>
        /// (another thread) without waking it. For a caller that parks under a lock of its own, and
        /// wakes the thread with <see cref="NudgeThread"/> once it has let go of it: an interrupt that
        /// finds the thread waiting for that lock would break the wait off with a
        /// ThreadInterruptedException where nothing expects one.
        /// </summary>
        public static void ParkAsyncException(Thread/*!*/ thread, Exception/*!*/ e) {
            using (new UninterruptedLock(_pendingAsyncExceptions)) {
                if (!_pendingAsyncExceptions.ContainsKey(thread.ManagedThreadId)) {
                    RequestSafePoint();
                }
                _pendingAsyncExceptions[thread.ManagedThreadId] = e;
            }
        }

        /// <summary>
        /// Wakes <paramref name="thread"/> if it is blocked, so that it picks up what is parked for it.
        /// </summary>
        public static void NudgeThread(Thread/*!*/ thread) {
            int key = thread.ManagedThreadId;
            using (new UninterruptedLock(_pendingAsyncExceptions)) {
                if (!_pendingAsyncExceptions.ContainsKey(key)) {
                    // taken, moved on or dropped already: an interrupt now would find nothing to deliver
                    return;
                }
                int count;
                _nudgesInFlight.TryGetValue(key, out count);
                _nudgesInFlight[key] = count + 1;
            }
            try {
                if ((thread.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0) {
                    thread.Interrupt();
                }
            } catch (PlatformNotSupportedException) {
                // nothing else we can do; the exception stays parked until the thread reaches a safe point
            } catch (ThreadStateException) {
            } finally {
                using (new UninterruptedLock(_pendingAsyncExceptions)) {
                    int count = _nudgesInFlight[key] - 1;
                    if (count == 0) {
                        _nudgesInFlight.Remove(key);
                    } else {
                        _nudgesInFlight[key] = count;
                    }
                }
            }
        }

        /// <summary>
        /// `using (new UninterruptedLock(o)) { ... }` is `lock (o) { ... }` whose wait for the monitor a nudge
        /// (<see cref="NudgeThread"/>) does not break off. Thread.Interrupt breaks a contended Monitor.Enter
        /// as it does Monitor.Wait, and the locks the parking, forwarding and fiber switching take are held
        /// for moments only, by threads a Thread#raise is aimed at - a ThreadInterruptedException out of
        /// one of them ends that thread or leaves a fiber switch half done. What the nudge was for stays
        /// parked, and the thread's next safe point raises it.
        /// </summary>
        public struct UninterruptedLock : IDisposable {
            private readonly object/*!*/ _monitor;
            private bool _taken;

            public UninterruptedLock(object/*!*/ monitor) {
                _monitor = monitor;
                _taken = false;
                while (true) {
                    try {
                        Monitor.Enter(monitor, ref _taken);
                        return;
                    } catch (ThreadInterruptedException) {
                        // a nudge; Monitor.Enter has told us whether it took the lock
                        if (_taken) {
                            return;
                        }
                    }
                }
            }

            public void Dispose() {
                if (_taken) {
                    _taken = false;
                    Monitor.Exit(_monitor);
                }
            }
        }

        // CLR thread id -> NudgeThread calls between their check that something is parked for the
        // thread and the end of their Thread.Interrupt. Guarded by _pendingAsyncExceptions.
        private static readonly Dictionary<int, int> _nudgesInFlight = new Dictionary<int, int>();

        /// <summary>
        /// Called by a thread that has just taken away what was parked for it - delivered it, moved it to
        /// another fiber's thread, dropped it. A nudge meant for it may still come, or has come and found
        /// the thread between two waits: Thread.Interrupt then stays latched and goes off at the thread's
        /// next managed wait, whichever that is - a `lock` in the fiber pool, say, which nothing translates,
        /// and a bare ThreadInterruptedException (or, once something is parked again, the exception with no
        /// Ruby frames) ends the thread. Waits for the nudges still under way and drops what they latched.
        /// A raise that parks something new for the thread meanwhile may lose its nudge here; what it
        /// parked stays, and the thread's next safe point (or the wait it is in, once that ends) raises it.
        /// Latching it again instead would only go off at whatever wait comes next.
        /// </summary>
        private static void DropStaleNudge() {
            Thread current = Thread.CurrentThread;
            int key = current.ManagedThreadId;
            while (true) {
                using (new UninterruptedLock(_pendingAsyncExceptions)) {
                    if (!_nudgesInFlight.ContainsKey(key)) {
                        break;
                    }
                }
                // not Thread.Sleep or SpinWait.SpinOnce, which may sleep: a latched interrupt throws there
                Thread.Yield();
            }
            try {
                Thread.Sleep(0);
            } catch (ThreadInterruptedException) {
                // the stale nudge
            }
        }

        public static void ExitThread(Thread/*!*/ thread) {
            if (thread == Thread.CurrentThread) {
                throw new ThreadExitSignal();
            }

            // Do not let a following Thread#kill erase a Thread#raise that is already waiting to
            // be delivered. MRI finishes unwinding the raised exception (and reports it) first - and
            // kills the thread after it all the same, should it rescue the exception: the kill waits
            // behind it (_killsBehindRaise) and is parked once the exception is taken.
            using (new UninterruptedLock(_pendingAsyncExceptions)) {
                int key = thread.ManagedThreadId;
                Exception parked;
                if (!_pendingAsyncExceptions.TryGetValue(key, out parked)) {
                    RequestSafePoint();
                    _pendingAsyncExceptions[key] = new ThreadExitSignal();
                } else if (!(parked is ThreadExitSignal)) {
                    _killsBehindRaise.Add(key);
                }
            }
            // Interrupting a running CLR thread queues ThreadInterruptedException for its
            // next managed wait, where it can bypass Ruby ensure blocks. Running Ruby code
            // observes the parked exception at safe points; only a blocked thread needs a nudge.
            NudgeThread(thread);
        }

        // What the target thread does to a parked exception before it throws it (see below).
        private static readonly ConditionalWeakTable<Exception, Func<Exception, Exception>>/*!*/ _asyncExceptionFinishers =
            new ConditionalWeakTable<Exception, Func<Exception, Exception>>();

        /// <summary>
        /// As RaiseAsyncException, and <paramref name="finisher"/> runs on the target thread just
        /// before the exception is thrown there: Thread#raise calls #exception once in the caller
        /// and once more in the target thread, which is where MRI raises it.
        /// </summary>
        public static void RaiseAsyncException(Thread thread, Exception e, Func<Exception, Exception> finisher) {
            if (finisher != null && thread != Thread.CurrentThread) {
                _asyncExceptionFinishers.AddOrUpdate(e, finisher);
            }
            RaiseAsyncException(thread, e);
        }

        /// <summary>
        /// <see cref="ParkAsyncException(Thread, Exception)"/> with a finisher, as for RaiseAsyncException.
        /// </summary>
        public static void ParkAsyncException(Thread/*!*/ thread, Exception/*!*/ e, Func<Exception, Exception> finisher) {
            if (finisher != null) {
                _asyncExceptionFinishers.AddOrUpdate(e, finisher);
            }
            ParkAsyncException(thread, e);
        }

        /// <summary>The exception to throw for a parked one, on the thread it was parked for.</summary>
        public static Exception/*!*/ FinishAsyncException(Exception/*!*/ e) {
            Func<Exception, Exception> finisher;
            if (_asyncExceptionFinishers.TryGetValue(e, out finisher)) {
                _asyncExceptionFinishers.Remove(e);
                return finisher(e) ?? e;
            }
            return e;
        }

        // IronRuby runs every Fiber on its own CLR thread, but Ruby semantics say that all fibers of a
        // thread share that thread's identity for Mutex ownership and for deadlock detection. A fiber
        // thread records the thread that owns its fiber group here.
        [ThreadStatic]
        private static Thread _fiberOwnerThread;

        /// <summary>
        /// The thread Ruby considers "current" for ownership purposes: the fiber group's owner if we are
        /// running inside a fiber, otherwise the CLR thread itself.
        /// </summary>
        public static Thread/*!*/ CurrentRubyThread {
            get { return _fiberOwnerThread ?? Thread.CurrentThread; }
        }

        public static void SetFiberOwnerThread(Thread thread) {
            _fiberOwnerThread = thread;
        }

        /// <summary>
        /// Removes and returns the asynchronous exception parked for the given thread, if any.
        /// </summary>
        // CLR thread id -> a Thread#kill that came in while a Thread#raise was parked for the thread
        // (ExitThread). Guarded by _pendingAsyncExceptions.
        private static readonly HashSet<int>/*!*/ _killsBehindRaise = new HashSet<int>();

        // Takes what is parked under <paramref name="key"/> away; a kill waiting behind it takes its
        // place. Under the _pendingAsyncExceptions lock.
        private static void UnparkLocked(int key) {
            if (_killsBehindRaise.Remove(key)) {
                // one parked entry for another: the safe point request stays
                _pendingAsyncExceptions[key] = new ThreadExitSignal();
            } else {
                _pendingAsyncExceptions.Remove(key);
                SafePointRequestDone();
            }
        }

        /// <summary>
        /// A Thread#kill waiting behind the exception parked for <paramref name="thread"/> takes the
        /// exception's place: for a wait that cannot raise the exception (a fiber waiting for control),
        /// where the kill has to end the thread all the same. Answers whether there was one.
        /// </summary>
        private static bool PromoteKillBehindRaise(Thread/*!*/ thread) {
            using (new UninterruptedLock(_pendingAsyncExceptions)) {
                int key = thread.ManagedThreadId;
                if (!_killsBehindRaise.Remove(key)) {
                    return false;
                }
                _pendingAsyncExceptions[key] = new ThreadExitSignal();
                return true;
            }
        }

        /// <summary>
        /// Drops everything parked for <paramref name="thread"/>, a kill waiting behind an exception too:
        /// for a thread that is ending, or a fiber thread whose fiber is done.
        /// </summary>
        public static void DropPendingAsyncExceptions(Thread/*!*/ thread) {
            bool dropped;
            using (new UninterruptedLock(_pendingAsyncExceptions)) {
                int key = thread.ManagedThreadId;
                _killsBehindRaise.Remove(key);
                dropped = _pendingAsyncExceptions.Remove(key);
                if (dropped) {
                    SafePointRequestDone();
                }
            }
            if (dropped && ReferenceEquals(thread, Thread.CurrentThread)) {
                DropStaleNudge();
            }
        }

        public static Exception GetPendingAsyncException(Thread/*!*/ thread) {
            Exception e;
            using (new UninterruptedLock(_pendingAsyncExceptions)) {
                int key = thread.ManagedThreadId;
                if (!_pendingAsyncExceptions.TryGetValue(key, out e)) {
                    return null;
                }
                UnparkLocked(key);
            }
            if (ReferenceEquals(thread, Thread.CurrentThread)) {
                DropStaleNudge();
            }
            return e;
        }

        public static bool HasPendingAsyncException(Thread/*!*/ thread) {
            using (new UninterruptedLock(_pendingAsyncExceptions)) {
                return _pendingAsyncExceptions.ContainsKey(thread.ManagedThreadId);
            }
        }

        /// <summary>
        /// Moves the Thread#raise exception parked for <paramref name="from"/> to <paramref name="to"/>
        /// and nudges <paramref name="to"/> if it is blocked. All fibers of a Ruby thread share it, but
        /// each runs on a CLR thread of its own, and an interrupt belongs to whichever of them is
        /// running (MRI checks the thread's interrupt flag in the current fiber). A Thread#kill stays
        /// where it is: it ends the CLR thread it was meant for. Answers whether anything moved.
        /// </summary>
        public static bool ForwardAsyncException(Thread/*!*/ from, Thread/*!*/ to) {
            if (!MoveAsyncException(from, to)) {
                return false;
            }
            NudgeThread(to);
            return true;
        }

        /// <summary>
        /// <see cref="ForwardAsyncException"/> without waking <paramref name="to"/>: for a caller that is
        /// about to hand <paramref name="to"/> control, which wakes it anyway.
        /// </summary>
        public static bool MoveAsyncException(Thread/*!*/ from, Thread/*!*/ to) {
            if (ReferenceEquals(from, to)) {
                return false;
            }
            using (new UninterruptedLock(_pendingAsyncExceptions)) {
                Exception e;
                int key = from.ManagedThreadId;
                if (!_pendingAsyncExceptions.TryGetValue(key, out e) || e is ThreadExitSignal) {
                    return false;
                }
                if (_killsBehindRaise.Contains(key)) {
                    // the thread is being killed: the exception stays with the kill (CheckAsyncException)
                    return false;
                }
                Exception there;
                if (_pendingAsyncExceptions.TryGetValue(to.ManagedThreadId, out there)) {
                    if (there is ThreadExitSignal) {
                        // a raise does not overwrite a kill
                        return false;
                    }
                    // two parked exceptions become one; the later raise wins, as in RaiseAsyncException
                    SafePointRequestDone();
                }
                _pendingAsyncExceptions.Remove(key);
                _pendingAsyncExceptions[to.ManagedThreadId] = e;
            }
            if (ReferenceEquals(from, Thread.CurrentThread)) {
                DropStaleNudge();
            }
            return true;
        }

        /// <summary>
        /// Drops <paramref name="e"/> if it is still what is parked for <paramref name="thread"/>.
        /// </summary>
        public static bool RetractAsyncException(Thread/*!*/ thread, Exception/*!*/ e) {
            using (new UninterruptedLock(_pendingAsyncExceptions)) {
                Exception parked;
                int key = thread.ManagedThreadId;
                if (!_pendingAsyncExceptions.TryGetValue(key, out parked) || !ReferenceEquals(parked, e)) {
                    return false;
                }
                UnparkLocked(key);
            }
            if (ReferenceEquals(thread, Thread.CurrentThread)) {
                DropStaleNudge();
            }
            return true;
        }

        /// <summary>
        /// Installed by the thread library: called on a thread that has an exception parked for it
        /// (other than a Thread#kill) before it is delivered there. True means it is not to be raised
        /// there: it belonged to another fiber of the same Ruby thread - the one now running - and
        /// has been moved there, or the thread is a fiber thread outside its fiber and keeps it.
        /// </summary>
        public static Func<bool> AsyncExceptionRedirectHook;

        /// <summary>The exception parked for a thread, left in place. Null when there is none.</summary>
        public static Exception PeekPendingAsyncException(Thread/*!*/ thread) {
            using (new UninterruptedLock(_pendingAsyncExceptions)) {
                Exception e;
                return _pendingAsyncExceptions.TryGetValue(thread.ManagedThreadId, out e) ? e : null;
            }
        }

        #region Thread.handle_interrupt

        /// <summary>MRI's three timings, in the order of increasing deferral.</summary>
        public const int InterruptImmediate = 0;
        public const int InterruptOnBlocking = 1;
        public const int InterruptNever = 2;

        private sealed class InterruptMask {
            internal RubyModule[] Classes;
            internal int[] Timings;
        }

        // One entry per enclosing Thread.handle_interrupt block. Per CLR thread, which here is
        // also per fiber - MRI's mask is per thread, but a fiber that sets one is the only thing
        // running on its thread anyway.
        [ThreadStatic]
        private static List<InterruptMask> _interruptMasks;

        public static void PushInterruptMask(RubyModule[]/*!*/ classes, int[]/*!*/ timings) {
            var masks = _interruptMasks ?? (_interruptMasks = new List<InterruptMask>());
            masks.Add(new InterruptMask { Classes = classes, Timings = timings });
        }

        public static void PopInterruptMask() {
            var masks = _interruptMasks;
            if (masks != null && masks.Count > 0) {
                masks.RemoveAt(masks.Count - 1);
            }
        }

        /// <summary>
        /// How soon <paramref name="e"/> may be delivered to the current thread. The mask stack is
        /// searched innermost first, and within one mask the last matching class wins - which is
        /// what MRI's "most recently given" comes to for a Hash literal.
        ///
        /// Thread#kill is never deferred: it is not a Ruby exception and MRI does not let a mask
        /// hold it back either.
        /// </summary>
        public static int GetInterruptTiming(Exception/*!*/ e) {
            var masks = _interruptMasks;
            if (masks == null || masks.Count == 0 || e is ThreadExitSignal) {
                return InterruptImmediate;
            }

            // The exception's class is looked up in the runtime the mask's classes belong to - not in
            // the process's first runtime, which a host with several of them (IronRuby.Tests) would
            // have answer for all of them: no class of another runtime is an ancestor of its classes,
            // so every mask let everything through.
            RubyContext context = null;
            RubyClass cls = null;
            for (int i = masks.Count - 1; i >= 0; i--) {
                InterruptMask mask = masks[i];
                for (int j = mask.Classes.Length - 1; j >= 0; j--) {
                    RubyModule maskClass = mask.Classes[j];
                    if (maskClass == null) {
                        continue;
                    }
                    if (!ReferenceEquals(maskClass.Context, context)) {
                        context = maskClass.Context;
                        cls = context.GetClassOf(e);
                    }
                    if (cls.HasAncestor(maskClass)) {
                        return mask.Timings[j];
                    }
                }
            }
            return InterruptImmediate;
        }

        #endregion

        #region Safe points in running code

        // How many pieces of safe-point work are outstanding anywhere in the process: exceptions
        // parked by Thread#raise / Thread#kill, POSIX signals and Ruby finalizers parked for the
        // main thread. Zero almost always, and then a safe point costs one load and a branch.
        //
        // One process-wide counter rather than a flag per thread, because a static field is the
        // cheapest thing emitted code can test: a [ThreadStatic] costs a TLS lookup on every back
        // edge. The price is that while one thread has work parked, every thread's safe points
        // take the slow path; that lasts until the work is delivered, and a thread that dies with
        // an exception still parked for it has it dropped (ThreadOps.RubyThreadStart).
        private static int _safePointRequests;

        /// <summary>Records one piece of work a safe point has to pick up.</summary>
        public static void RequestSafePoint() {
            Interlocked.Increment(ref _safePointRequests);
        }

        /// <summary>The work recorded by <see cref="RequestSafePoint"/> has been taken.</summary>
        public static void SafePointRequestDone() {
            Interlocked.Decrement(ref _safePointRequests);
        }

        /// <summary>True while any safe-point work is outstanding. One volatile load.</summary>
        public static bool IsSafePointRequested {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return Volatile.Read(ref _safePointRequests) != 0; }
        }

        /// <summary>
        /// The safe point running Ruby code passes through: every loop back edge and every method
        /// and block entry. MRI checks for interrupts at the same places (its backward branches
        /// and method calls), which is what makes Thread#raise, Thread#kill and Timeout.timeout
        /// reach a thread that is spinning in `loop {}` or `while true`.
        ///
        /// The fast path is one volatile load of a static and a not-taken branch; it is inlined
        /// wherever the CLR JIT sees the call, emitted code included. The load is volatile so
        /// that the JIT cannot hoist it out of a loop whose body calls nothing.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SafePoint() {
            if (Volatile.Read(ref _safePointRequests) != 0) {
                SafePointSlow();
            }
        }

        /// <summary><see cref="SafePoint"/>, for code that emits a call to it.</summary>
        public static readonly MethodInfo/*!*/ SafePointMethod = typeof(RubyUtils).GetMethod("SafePoint");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SafePointSlow() {
            // Not a blocking call: Thread.handle_interrupt(... => :on_blocking) holds its
            // exceptions back here, as MRI does at a backward branch.
            CheckAsyncException(false);
        }

        #endregion

        /// <summary>
        /// Installed by the thread library (ThreadOps.RubyThreadInfo): marks the current thread as
        /// blocked or not - Thread#status "sleep" - and answers what it was before. A thread waiting in
        /// a syscall is Running as far as the CLR can tell, so code down here that parks a thread in
        /// poll(2) says so through this rather than through a managed wait it does not need.
        /// </summary>
        public static Func<bool, bool> NativeWaitHook;

        /// <summary>Marks the current thread blocked; pass the answer to ExitNativeWait.</summary>
        public static bool EnterNativeWait() {
            var hook = NativeWaitHook;
            return hook != null && hook(true);
        }

        public static void ExitNativeWait(bool wasBlocked) {
            var hook = NativeWaitHook;
            if (hook != null) {
                hook(wasBlocked);
            }
        }

        /// <summary>
        /// A safe point: throws the asynchronous exception parked for the current thread, if there is one.
        /// Called from the blocking primitives and from Thread.pass, and - through <see cref="SafePoint"/> -
        /// from every loop back edge and every method and block entry.
        /// </summary>
        public static void CheckAsyncException() {
            CheckAsyncException(true);
        }

        /// <summary>
        /// <paramref name="atBlockingCall"/> distinguishes MRI's two kinds of safe point: a
        /// blocking operation (Kernel#sleep, Queue#pop, a Monitor wait) from Thread.pass, which
        /// blocks on nothing.  Thread.handle_interrupt's :on_blocking only delivers at the former.
        /// </summary>
        public static void CheckAsyncException(bool atBlockingCall) {
            CheckAsyncException(atBlockingCall, false);
        }

        /// <summary>
        /// For a wait that hands control between the fibers of a thread (ThreadOps.FiberMailbox), which
        /// a Thread#raise must not break off: the fiber waiting there is not the thread's running one,
        /// or is about to become it - the message that makes it so is on the way. The exception goes
        /// to the running fiber or stays parked until control has arrived. Thread#kill still ends the
        /// thread there.
        /// </summary>
        public static void TranslateFiberWaitInterrupt() {
            CheckAsyncException(true, true);
        }

        private static void CheckAsyncException(bool atBlockingCall, bool killOnly) {
            // Everything below - a fiber being reclaimed, a parked signal, a parked finalizer, an
            // exception parked by Thread#raise / #kill - is counted in _safePointRequests while it is
            // outstanding, so with none of it there is nothing to look up. Mutex#lock and Monitor#enter
            // come through here on every call.
            if (Volatile.Read(ref _safePointRequests) == 0) {
                return;
            }

            // Reclamation runs CLR cleanup only. Do not enter Ruby signal/finalizer callbacks
            // or replace the unwind signal with a pending asynchronous exception.
            if (_reclaimingFiber) {
                // A CLR catch(Exception) can swallow a control-flow signal. Keep the request
                // active until the worker boundary, so another Ruby entry cannot continue it.
                throw new FiberReclaimSignal();
            }
            // Trap handlers run on the main thread at a safe point, like MRI's interrupt check.
            // The hook is installed by the Signal library, which lives in another assembly.
            Action safePoint = SafePointHandler;
            if (safePoint != null) {
                safePoint();
            }

            // Object finalizers are parked by the CLR's finalizer thread and run here, for the
            // same reason: a Ruby finalizer is arbitrary Ruby code, and running it off the main
            // thread races every unsynchronized table the main thread is using.
            Action finalizers = FinalizerHandler;
            if (finalizers != null) {
                finalizers();
            }

            Exception e = PeekPendingAsyncException(Thread.CurrentThread);
            if (e == null) {
                return;
            }

            if (!(e is ThreadExitSignal)) {
                if (killOnly && PromoteKillBehindRaise(Thread.CurrentThread)) {
                    // A Thread#kill came in behind the exception, and this wait (a fiber waiting for
                    // control) cannot raise the exception: the kill ends the thread now, and the
                    // exception goes with it. Handed on, it could be rescued by the running fiber
                    // and the kill never come.
                    e = PeekPendingAsyncException(Thread.CurrentThread);
                    if (e == null) {
                        return;
                    }
                } else {
                    // A fiber that is not the running one of its thread - waiting for the fiber it
                    // resumed, finishing, parked in the pool - hands the exception on instead of
                    // raising it.
                    Func<bool> redirect = AsyncExceptionRedirectHook;
                    if ((redirect != null && redirect()) || killOnly) {
                        return;
                    }
                }
            }

            int timing = GetInterruptTiming(e);
            if (timing == InterruptNever || (timing == InterruptOnBlocking && !atBlockingCall)) {
                // Masked by an enclosing Thread.handle_interrupt; it stays parked. A Thread#kill
                // waiting behind it is not held back by a mask that does not cover the kill (MRI
                // queues the two side by side): it goes first, and the exception goes with the thread.
                if (e is ThreadExitSignal || !PromoteKillBehindRaise(Thread.CurrentThread)) {
                    return;
                }
                e = PeekPendingAsyncException(Thread.CurrentThread);
                if (e == null) {
                    return;
                }
                timing = GetInterruptTiming(e);
                if (timing == InterruptNever || (timing == InterruptOnBlocking && !atBlockingCall)) {
                    return;
                }
            }

            // Take it only now: a masked exception has to stay parked for Thread.pending_interrupt?
            // and for the delivery at the end of the handle_interrupt block.
            e = GetPendingAsyncException(Thread.CurrentThread);
            if (e != null) {
                throw FinishAsyncException(e);
            }
        }

        /// <summary>
        /// Work the main thread owes: signal handlers parked by .NET's POSIX signal thread. Set once
        /// by the Signal library; a null check is all this costs on every other safe point.
        /// </summary>
        public static Action SafePointHandler;

        /// <summary>
        /// The other half of that work: Ruby finalizers the CLR's finalizer thread has parked for
        /// the main thread. Set once by ObjectSpace.define_finalizer, in another assembly; a
        /// program that defines no finalizer never pays more than the null check.
        /// </summary>
        public static Action FinalizerHandler;

        /// <summary>
        /// Called from a catch (ThreadInterruptedException) around a blocking wait: if the interrupt was our
        /// doing, the parked exception is thrown instead. Otherwise the wait is simply resumed by the caller.
        /// </summary>
        public static void TranslateThreadInterrupt() {
            CheckAsyncException();
        }

        public static bool IsRubyThreadExit(Exception e) {
            return e is ThreadExitSignal;
        }

        /// <summary>
        /// Can return null for Thread#kill
        /// </summary>
        public static Exception GetVisibleException(Exception e) {
            if (e is ThreadExitSignal) {
                return null;
            }
            return e;
        }
#else
        public static Exception GetVisibleException(Exception e) { return e; }

        public static void ExitThread(Thread/*!*/ thread) {
            thread.Abort();
        }

        public static bool IsRubyThreadExit(Exception e) {
            return e is ThreadAbortException;
        }
#endif
#else
        public static Exception GetVisibleException(Exception e) { return e; }
#endif
        #endregion

        #region Paths

        /// <summary>
        /// Whether the first line of a script is a #! line that runs IronRuby's own launcher. MRI
        /// and prism take a #! line without "ruby" in it as the start of some other program and
        /// look further down for the Ruby one; but RubyGems and Rails write binstubs from
        /// RbConfig's ruby_install_name, which was "ir", so `rails new` started bin/rails with
        /// "#!/usr/bin/env ir.exe" - a Ruby script that does not say "ruby". It is "ironruby"
        /// now, which does; this keeps the binstubs written before that running.
        /// </summary>
        public static bool ShebangNamesIronRuby(string/*!*/ code) {
            if (code == null || !code.StartsWith("#!", StringComparison.Ordinal)) {
                return false;
            }
            int end = code.IndexOf('\n');
            string shebang = (end < 0) ? code.Substring(2) : code.Substring(2, end - 2);
            foreach (string word in shebang.Split(new[] { ' ', '\t', '\r', '/', '\\', '"', '\'' }, StringSplitOptions.RemoveEmptyEntries)) {
                switch (word.ToLowerInvariant()) {
                    case "ir": case "ir.exe": case "ir.cmd": case "ir.sh":
                        return true;
                }
            }
            return false;
        }

        // '\' is only a path separator on Windows. On Unix it is an ordinary character in a
        // file name, and rewriting it silently loses files: mspec's own rm_r expands the path
        // first, so a fixture called "special/\a" was never found, never deleted, and every
        // later `before :all` that recreated the fixture tree then failed on a non-empty dir.
        public static MutableString CanonicalizePath(MutableString path) {
            if (!FileSystemUsesDriveLetters) {
                return path;
            }
            for (int i = 0; i < path.Length; i++) {
                if (path.GetChar(i) == '\\')
                    path.SetChar(i, '/');
            }
            return path;
        }

        public static String CanonicalizePath(string path) {
            return FileSystemUsesDriveLetters ? path.Replace('\\', '/') : path;
        }

        public static String CombinePaths(string basePath, string path) {
            return basePath.Length == 0 || basePath.EndsWith("\\", StringComparison.Ordinal) || basePath.EndsWith("/", StringComparison.Ordinal) ? 
                basePath + path :
                basePath + "/" + path;
        }

        public static char DirectorySeparatorChar = Path.DirectorySeparatorChar;

        // TODO: virtualize via PAL
        public static bool FileSystemUsesDriveLetters { 
            get { return DirectorySeparatorChar == '\\'; } 
        }

        // Is path something like "/foo/bar" (or "c:/foo/bar" on Windows)
        // We need this instead of Path.IsPathRooted since we need to be able to deal with Unix-style path names even on Windows
        public static bool IsAbsolutePath(string path) {
            if (IsAbsoluteDriveLetterPath(path)) {
                return true;
            }

            if (String.IsNullOrEmpty(path)) {
                return false;
            }

            return path[0] == '/';
        }

        // Is path something like "c:/foo/bar" (on Windows)
        public static bool IsAbsoluteDriveLetterPath(string path) {
            if (String.IsNullOrEmpty(path)) {
                return false;
            }

            if (!FileSystemUsesDriveLetters) {
                return false;
            }

            return Tokenizer.IsLetter(path[0]) && path.Length >= 2 && path[1] == ':' && path[2] == '/';
        }

        // returns "//", "///", ... or something like "c:/"
        public static string GetPathRoot(PlatformAdaptationLayer/*!*/ platform, string path, out string pathAfterRoot) {
            Debug.Assert(IsAbsolutePath(path));
            if (IsAbsoluteDriveLetterPath(path)) {
                pathAfterRoot = path.Substring(3);
                return path.Substring(0, 3);
            } else {
                Debug.Assert(path[0] == '/');

                // The root for "////foo" is "/////"
                string withoutInitialSlashes = path.TrimStart('/');
                int initialSlashesCount = path.Length - withoutInitialSlashes.Length;
                string initialSlashes = path.Substring(0, initialSlashesCount);
                pathAfterRoot = path.Substring(initialSlashesCount);

                if (!FileSystemUsesDriveLetters || initialSlashesCount > 1) {
                    return initialSlashes;
                } else {
                    string currentDirectory = RubyUtils.CanonicalizePath(platform.CurrentDirectory);
                    Debug.Assert(IsAbsoluteDriveLetterPath(currentDirectory));
                    string temp;
                    return GetPathRoot(platform, currentDirectory, out temp);
                }
            }
        }

        // Is path something like "c:foo" (note that this is not "c:/foo")
        public static bool HasPartialDriveLetter(string path, out char partialDriveLetter, out string relativePath) {
            partialDriveLetter = '\0';
            relativePath = null;

            if (String.IsNullOrEmpty(path)) {
                return false;
            }

            if (!FileSystemUsesDriveLetters) {
                return false;
            }

            if (Tokenizer.IsLetter(path[0]) && path.Length >= 2 && path[1] == ':' && (path.Length == 2 || path[2] != '/')) {
                partialDriveLetter = path[0];
                relativePath = path.Substring(2);
                return true;
            } else {
                return false;
            }
        }

        #region expand_path

        // Algorithm to find HOME equivalents under Windows. This is equivalent to Ruby 1.9 behavior:
        // 
        // 1. Try get HOME
        // 2. Try to generate HOME equivalent using HOMEDRIVE + HOMEPATH
        // 3. Try to generate HOME equivalent from USERPROFILE
        // 4. Try to generate HOME equivalent from Personal special folder 

        public static string/*!*/ GetHomeDirectory(PlatformAdaptationLayer/*!*/ pal) {
            string result = RubyEnvironment.GetVariable(pal, "HOME");
            if (result == null) {
                string homeDrive = RubyEnvironment.GetVariable(pal, "HOMEDRIVE");
                string homePath = RubyEnvironment.GetVariable(pal, "HOMEPATH");
                if (homeDrive == null && homePath == null) {
                    string userEnvironment = RubyEnvironment.GetVariable(pal, "USERPROFILE");
                    if (userEnvironment == null) {
                        // This will always succeed with a non-null string, but it can fail
                        // if the Personal folder was renamed or deleted. In this case it returns
                        // an empty string.
#if FEATURE_FILESYSTEM // TODO: virtualize via PAL?
                        result = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
#else
                        result = null;
#endif
                    } else {
                        result = userEnvironment;
                    }
                } else if (homeDrive == null) {
                    result = homePath;
                } else if (homePath == null) {
                    result = homeDrive + RubyUtils.DirectorySeparatorChar;
                } else {
                    result = homeDrive + homePath;
                }

                if (result != null) {
                    result = ExpandPath(pal, result);
                }
            }

            return result;
        }

        class PathExpander {
            List<string> _pathComponents = new List<string>(); // does not include the root
            string _root; // Typically "c:/" on Windows, and "/" on Unix

            internal PathExpander(PlatformAdaptationLayer/*!*/ platform, string absoluteBasePath) {
                Debug.Assert(RubyUtils.IsAbsolutePath(absoluteBasePath));

                string basePathAfterRoot = null;
                _root = RubyUtils.GetPathRoot(platform, absoluteBasePath, out basePathAfterRoot);

                // Normally, basePathAfterRoot[0] will not be '/', but here we deal with cases like "c:////foo"
                basePathAfterRoot = basePathAfterRoot.TrimStart('/');

                AddRelativePath(basePathAfterRoot);
            }

            internal void AddRelativePath(string relPath) {
                Debug.Assert(!RubyUtils.IsAbsolutePath(relPath));

                string[] relPathComponents = relPath.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

                foreach (string pathComponent in relPathComponents) {
                    if (pathComponent == "..") {
                        if (_pathComponents.Count == 0) {
                            // MRI allows more pops than the base path components
                            continue;
                        }
                        _pathComponents.RemoveAt(_pathComponents.Count - 1);
                    } else if (pathComponent == ".") {
                        continue;
                    } else {
                        _pathComponents.Add(pathComponent);
                    }
                }
            }

            internal string/*!*/ GetResult() {
                StringBuilder result = new StringBuilder(_root);

                if (_pathComponents.Count >= 1) {
                    // Here we make this work:
                    //   File.expand_path("c:/..a..") -> "c:/..a"
                    string lastComponent = _pathComponents[_pathComponents.Count - 1];
                    if (RubyUtils.FileSystemUsesDriveLetters && !String.IsNullOrEmpty(lastComponent.TrimEnd('.'))) {
                        _pathComponents[_pathComponents.Count - 1] = lastComponent.TrimEnd('.');
                    }
                }

                for (int i = 0; i < _pathComponents.Count; i++) {
                    result.Append(_pathComponents[i]);
                    if (i < (_pathComponents.Count - 1)) {
                        result.Append('/');
                    }
                }
#if DEBUG
                _pathComponents = null;
                _root = null;
#endif
                return result.ToString();
            }
        }

        // Expand directory path - these cases exist:
        //
        // 1. Empty string or nil means return current directory
        // 2. ~ with non-existent HOME directory throws exception
        // 3. ~, ~/ or ~\ which expands to HOME
        // 4. ~foo is left unexpanded
        // 5. Expand to full path if path is a relative path
        // 
        // No attempt is made to determine whether the path is valid or not
        // Returned path is always canonicalized to forward slashes

        public static string/*!*/ ExpandPath(PlatformAdaptationLayer/*!*/ platform, string/*!*/ path) {
            return ExpandPath(platform, path, platform.CurrentDirectory, true);
        }

        public static string/*!*/ ExpandPath(PlatformAdaptationLayer/*!*/ platform, string/*!*/ path, string/*!*/ basePath, bool expandHome) {
            return ExpandPath(platform, path, basePath, expandHome, true);
        }

        private static string/*!*/ ExpandPath(PlatformAdaptationLayer/*!*/ platform, string/*!*/ path, string/*!*/ basePath, bool expandHome, bool expandBase) {
            Assert.NotNull(platform, path, basePath);

            if (expandHome) {
                int length = path.Length;
                if (length > 0 && path[0] == '~') {
                    if (length == 1 || path[1] == '/' || (FileSystemUsesDriveLetters && path[1] == '\\')) {
                        string homeDirectory = RubyEnvironment.GetVariable(platform, "HOME");
                        if (homeDirectory == null) {
                            throw RubyExceptions.CreateArgumentError("couldn't find HOME environment -- expanding '~'");
                        }

                        if (length <= 2) {
                            path = homeDirectory;
                        } else {
                            path = Path.Combine(homeDirectory, path.Substring(2));
                        }
                    } else {
                        // MRI doesn't expand path that starts with ~, seems like a bug
                        // http://redmine.ruby-lang.org/issues/show/3629
                        return path;
                    }
                }
                // MRI deosn't expand content of HOME variable (could be a relative path). We do.
                // See http://redmine.ruby-lang.org/issues/show/3630
            }

            path = RubyUtils.CanonicalizePath(path);
            basePath = RubyUtils.CanonicalizePath(basePath);

            if (RubyUtils.IsAbsolutePath(path)) {
                // "basePath" can be ignored if "path" is an absolute path
                return new PathExpander(platform, path).GetResult();
            }

            // expand base path:
            string expandedBasePath = expandBase ? ExpandPath(platform, basePath, platform.CurrentDirectory, expandHome, false) : basePath;

            char drive;
            string relativePath;
            if (RubyUtils.HasPartialDriveLetter(path, out drive, out relativePath)) {
                string _;
                string root = RubyUtils.GetPathRoot(platform, expandedBasePath, out _);
                if (root[0].ToLowerInvariant() != drive.ToLowerInvariant()) {
                    expandedBasePath = RubyUtils.CanonicalizePath(platform.CurrentDirectory);
                    root = RubyUtils.GetPathRoot(platform, expandedBasePath, out _);
                    if (root[0].ToLowerInvariant() != drive.ToLowerInvariant()) {
                        // MRI: Converts "X:path" to "X:/path" if X != base drive or current drive:
                        return new PathExpander(platform, drive.ToString() + ":/" + relativePath).GetResult();
                    }
                }
                path = relativePath;
            }

            PathExpander pathExpander = new PathExpander(platform, expandedBasePath);
            pathExpander.AddRelativePath(path);
            return pathExpander.GetResult();
        }

        /// <summary>
        /// Returns the extension of given path.
        /// Equivalent to <see cref="Path.GetExtension"/> but it doesn't check for an invalid path and 
        /// it considers ".foo" to be a file name with no extension rather than an extension with an empty file name.
        /// </summary>
        /// <returns>Null iff path is null. Empty string if the path doesn't have any extension.</returns>
        public static string GetExtension(string path) {
            if (path == null) {
                return null;
            }

            int length = path.Length;
            int i = length;
            while (--i >= 0) {
                char c = path[i];
                if (c == '.') {
                    if (i != length - 1 && i > 0) {
                        return path.Substring(i, length - i);
                    }
                    return String.Empty;
                }
                if (c == '\\' || c == '/' || c == ':') {
                    break;
                }
            }
            return String.Empty;
        }

        #endregion

        #endregion

        #region Streams

        /// <summary>
        /// Writes binary content of <see cref="MutableString"/> into the given buffer.
        /// </summary>
        public static void Write(this Stream/*!*/ stream, MutableString/*!*/ str, int start, int count) {
            int byteCount;
            byte[] bytes = str.GetByteArray(out byteCount);
            if (start < byteCount) {
                stream.Write(bytes, start, Math.Min(byteCount - start, count));
            }
        }

        #endregion
    }
}
