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
using Microsoft.Scripting;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using System.Runtime.CompilerServices;

namespace IronRuby.Builtins {
    using EachSite = Func<CallSite, object, Proc, object>;

    [RubyClass("Struct", Extends = typeof(RubyStruct), Inherits = typeof(object)), Includes(typeof(Enumerable))]
    public static partial class RubyStructOps {
        [RubyConstructor]
        public static void AllocatorUndefined(RubyClass/*!*/ self, params object[] args) {
            throw RubyExceptions.CreateAllocatorUndefinedError(self);
        }

        [RubyMethod("new", RubyMethodAttributes.PublicSingleton)]
        public static object NewAnonymousStruct(BlockParam block, RubyClass/*!*/ self, [NotNull]RubySymbol/*!*/ firstAttibuteName,
            [DefaultProtocol, NotNullItems]params string/*!*/[]/*!*/ attributeNames) {

            return CreateAnonymousWithFirstAttribute(block, self, RubyOps.ConvertSymbolToClrString(firstAttibuteName), attributeNames);
        }

        [RubyMethod("new", RubyMethodAttributes.PublicSingleton)]
        public static object NewAnonymousStruct(BlockParam block, RubyClass/*!*/ self, [NotNull]string/*!*/ firstAttibuteName,
            [DefaultProtocol, NotNullItems]params string/*!*/[]/*!*/ attributeNames) {

            return CreateAnonymousWithFirstAttribute(block, self, firstAttibuteName, attributeNames);
        }

        [RubyMethod("new", RubyMethodAttributes.PublicSingleton)]
        public static object NewStruct(BlockParam block, RubyClass/*!*/ self, [DefaultProtocol]MutableString className,
            [DefaultProtocol, NotNullItems]params string/*!*/[]/*!*/ attributeNames) {

            if (className == null) {
                return Create(block, self, null, attributeNames);
            }

            string strName = className.ConvertToString();
            if (!IronRuby.Compiler.Tokenizer.IsConstantName(strName)) {
                throw RubyExceptions.CreateNameError(String.Format("identifier {0} needs to be constant", strName));
            }

            // new_struct: MRI warns (rb_warn, so even without -w) and replaces the constant.
            object existing;
            if (self.TryGetConstant(null, strName, out existing)) {
                self.Context.ReportWarning("redefining constant " + self.MakeNestedModuleName(strName));
            }
            return Create(block, self, strName, attributeNames);
        }

        public static object CreateAnonymousWithFirstAttribute(BlockParam block, RubyClass/*!*/ self,
            string/*!*/ firstAttribute, string/*!*/[]/*!*/ attributeNames) {

            return Create(block, self, null, ArrayUtils.Insert(firstAttribute, attributeNames));
        }

        /// <summary>
        /// Struct#new
        /// Creates Struct classes with the specified name and members
        /// </summary>
        private static object Create(BlockParam block, RubyClass/*!*/ self, string className, string/*!*/[]/*!*/ attributeNames) {
            var result = RubyStruct.DefineStruct(self, className, attributeNames);

            if (block != null) {
                return RubyUtils.EvaluateInModule(result, block, new object[] { result }, result);
            }

            return result;
        }

        // Struct#initialize. The overwhelmingly common call - one positional value per member, the
        // last of them not a Hash that could be keywords, on a class not built with keyword_init:
        // true - stores the values right here, which is what keeps Point.new(1, 2) cheap. Every
        // other call goes to Struct#__struct_initialize_slow__ in ruby4.rb with the positional
        // arguments as one array and the keywords, if any, as a Hash - as a
        // `def initialize(*args, **kwargs)' would have received them. The keywords arrive here as
        // a flagged Hash after the positional values; a Hash passed as a value is not flagged, and
        // stays a positional argument (MRI's rb_struct_initialize_m tells them apart the same way).
        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static object Initialize(CallSiteStorage<Func<CallSite, object, object, object, object>>/*!*/ slowPath,
            RubyStruct/*!*/ self, params object[]/*!*/ items) {

            // MRI's rb_struct_modify comes first: re-initializing a frozen struct is a FrozenError
            // before any of the arguments is looked at. A struct being constructed is never frozen.
            if (self.IsFrozen) {
                throw RubyExceptions.CreateObjectFrozenErrorFor(self, "Struct");
            }

            int count = self.ItemCount;
            if (items.Length == count && (count == 0 || !(items[count - 1] is IronRuby.Builtins.Hash)) && !self.IsKeywordInit) {
                self.SetValues(items);
                return null;
            }

            int positional = items.Length;
            Hash keywords = null;
            if (positional > 0) {
                var hash = items[positional - 1] as Hash;
                if (hash != null && hash.IsKeywordArguments) {
                    // a plain copy: the flag would make it the slow path's keywords again
                    keywords = new Hash(hash);
                    positional--;
                }
            }

            // Without the prelude (the test harness runs the libraries alone) there is no slow path:
            // store what was given, as the C# `def initialize(*args)' did.
            if (!self.ImmediateClass.ResolveMethod("__struct_initialize_slow__", VisibilityContext.AllVisible).Found) {
                self.SetValues(items);
                return null;
            }

            var site = slowPath.GetCallSite("__struct_initialize_slow__", new RubyCallSignature(2, RubyCallFlags.HasImplicitSelf));
            return site.Target(site, self, new RubyArray(items, 0, positional), keywords);
        }

        // The plain store, for __struct_initialize_slow__.
        [RubyMethod("__struct_initialize__", RubyMethodAttributes.PrivateInstance)]
        public static void Reinitialize(RubyStruct/*!*/ self, params object[]/*!*/ items) {
            // re-initializing a frozen struct is a FrozenError, as in MRI
            if (self.IsFrozen) {
                throw RubyExceptions.CreateObjectFrozenErrorFor(self, "Struct");
            }
            self.SetValues(items);
        }

        // Copies data from one Struct instance into another:
        [RubyMethod("initialize_copy", RubyMethodAttributes.PrivateInstance)]
        public static RubyStruct/*!*/ InitializeCopy(RubyStruct/*!*/ self, object source) {
            if (ReferenceEquals(self, source)) {
                return self;
            }
            if (self.IsFrozen) {
                throw RubyExceptions.CreateObjectFrozenErrorFor(self, "Struct");
            }
            // MRI: anything but an instance of this very struct class is rejected with this message
            var other = source as RubyStruct;
            if (other == null || self.ImmediateClass.GetNonSingletonClass() != other.ImmediateClass.GetNonSingletonClass()) {
                throw RubyExceptions.CreateTypeError("initialize_copy should take same class object");
            }

            self.SetValues(other.Values);
            return self;
        }

        [RubyMethod("members")]
        public static RubyArray/*!*/ GetMembers(RubyStruct/*!*/ self) {
            return RubyStruct.GetMembers(self);
        }

        [RubyMethod("length")]
        [RubyMethod("size")]
        public static int GetSize(RubyStruct/*!*/ self) {
            return self.ItemCount;
        }

        [RubyMethod("[]")]
        public static object GetValue(RubyStruct/*!*/ self, int index) {
            return self[NormalizeIndex(self.ItemCount, index)];
        }

        [RubyMethod("[]")]
        public static object GetValue(RubyStruct/*!*/ self, [NotNull]RubySymbol/*!*/ name) {
            return self[name.ToString()];
        }

        [RubyMethod("[]")]
        public static object GetValue(RubyStruct/*!*/ self, [NotNull]MutableString/*!*/ name) {
            return self[name.ConvertToString()];
        }

        [RubyMethod("[]")]
        public static object GetValue(ConversionStorage<int>/*!*/ conversionStorage, RubyStruct/*!*/ self, object index) {
            return self[NormalizeIndex(self.ItemCount, Protocols.CastToFixnum(conversionStorage, index))];
        }

        [RubyMethod("[]=")]
        public static object SetValue(RubyStruct/*!*/ self, int index, object value) {
            return self[NormalizeIndex(self.ItemCount, index)] = value;
        }

        [RubyMethod("[]=")]
        public static object SetValue(RubyStruct/*!*/ self, [NotNull]RubySymbol/*!*/ name, object value) {
            return self[name.ToString()] = value;
        }

        [RubyMethod("[]=")]
        public static object SetValue(RubyStruct/*!*/ self, [NotNull]MutableString/*!*/ name, object value) {
            return self[name.ConvertToString()] = value;
        }

        [RubyMethod("[]=")]
        public static object SetValue(ConversionStorage<int>/*!*/ conversionStorage, RubyStruct/*!*/ self, object index, object value) {
            return self[NormalizeIndex(self.ItemCount, Protocols.CastToFixnum(conversionStorage, index))] = value;
        }

        [RubyMethod("each")]
        public static object Each(BlockParam block, RubyStruct/*!*/ self) {
            if (block == null && self.ItemCount > 0) {
                throw RubyExceptions.NoBlockGiven();
            }

            foreach (var value in self.Values) {
                object result;
                if (block.Yield(value, out result)) {
                    return result;
                }
            }

            return self;
        }

        [RubyMethod("each_pair")]
        public static object EachPair(BlockParam block, RubyStruct/*!*/ self) {
            if (block == null && self.ItemCount > 0) {
                throw RubyExceptions.NoBlockGiven();
            }

            var context = self.ImmediateClass.Context;
            foreach (KeyValuePair<string, object> entry in self.GetItems()) {
                object result;
                if (block.Yield(context.EncodeIdentifier(entry.Key), entry.Value, out result)) {
                    return result;
                }
            }

            return self;
        }

        [RubyMethod("to_a")]
        [RubyMethod("values")]
        public static RubyArray/*!*/ Values(RubyStruct/*!*/ self) {
            return new RubyArray(self.Values);
        }

        [RubyMethod("hash")]
        public static int Hash(UnaryOpStorage/*!*/ hashStorage, ConversionStorage<int>/*!*/ fixnumCast, RubyStruct/*!*/ self) {
            return self.GetHashCode(hashStorage, fixnumCast);
        }

        [RubyMethod("eql?")]
        public static bool Equal(BinaryOpStorage/*!*/ eqlStorage, RubyStruct/*!*/ self, object other) {
            return self.Equals(eqlStorage, other);
        }

        // same pattern as RubyStruct.Equals, but we need to call == instead of eql?
        [RubyMethod("==")]
        public static bool Equals(BinaryOpStorage/*!*/ equals, RubyStruct/*!*/ self, object obj) {
            var other = obj as RubyStruct;
            if (!self.StructReferenceEquals(other)) {
                return false;
            }
            Debug.Assert(self.ItemCount == other.ItemCount);

            return IListOps.Equals(equals, self.Values, other.Values);
        }

        [RubyMethod("to_s")]
        [RubyMethod("inspect")]
        public static MutableString/*!*/ Inspect(RubyStruct/*!*/ self) {
            RubyContext context = self.ImmediateClass.Context;

            using (IDisposable handle = RubyUtils.InfiniteInspectTracker.TrackObject(self)) {
                // #<struct Struct::Foo name=nil, val=nil>, or #<struct name=nil> when the class
                // (or an enclosing module) is anonymous. The name is read off the class rather
                // than dispatched to #name, which Ruby does not call here.
                var result = MutableString.CreateMutable(RubyEncoding.Binary);
                result.Append("#<struct ");
                string className = context.GetClassOf(self).GetName(context);
                bool anonymous = String.IsNullOrEmpty(className) || className.StartsWith("#<") || className.StartsWith("::");
                if (!anonymous) {
                    result.Append(className);
                }

                if (handle == null) {
                    // the recursion marker always names the class, an anonymous one as #<Class:0x...>
                    if (anonymous) {
                        result.Append(context.Inspect(context.GetClassOf(self)));
                    }
                    return result.Append(":...>");
                }
                if (!anonymous) {
                    result.Append(' ');
                }

                object[] data = self.Values;
                var members = self.GetNames();
                for (int i = 0; i < data.Length; i++) {
                    if (i != 0) {
                        result.Append(", ");
                    }
                    // TODO (encoding):
                    result.Append(members[i]);
                    result.Append('=');
                    result.Append(context.Inspect(data[i]));
                }
                result.Append('>');
                return result;
            }
        }

        // For some unknown reason Struct defines the method even though it is mixed in from Enumerable
        // Until we discover the difference, delegate to Enumerable#select
        [RubyMethod("select")]
        public static object Select(CallSiteStorage<EachSite>/*!*/ each, BlockParam predicate, RubyStruct/*!*/ self) {
            return Enumerable.Select(each, predicate, self);
        }

        // equivalent to Array#values_at over the data array
        [RubyMethod("values_at")]
        public static RubyArray/*!*/ ValuesAt(ConversionStorage<int>/*!*/ fixnumCast, RubyStruct/*!*/ self, params object[]/*!*/ values) {
            RubyArray result = new RubyArray();
            object[] data = self.Values;

            for (int i = 0; i < values.Length; ++i) {
                Range range = values[i] as Range;
                if (range != null) {
                    int begin = Protocols.CastToFixnum(fixnumCast, range.Begin);
                    int end = Protocols.CastToFixnum(fixnumCast, range.End);

                    if (range.ExcludeEnd) {
                        end -= 1;
                    }

                    begin = NormalizeIndex(data.Length, begin);
                    end = NormalizeIndex(data.Length, end);
                    Debug.Assert(end - begin <= data.Length); // because we normalized the indicies

                    if (end - begin > 0) {
                        result.AddCapacity(end - begin);
                        for (int j = begin; j <= end; j++) {
                            result.Add(data[j]);
                        }
                    }
                } else {
                    int index = NormalizeIndex(data.Length, Protocols.CastToFixnum(fixnumCast, values[i]));
                    result.Add(data[index]);
                }
            }

            return result;
        }

        private static int NormalizeIndex(int itemCount, int index) {
            int normalized = index;
            if (normalized < 0) {
                normalized += itemCount;
            }
            if (normalized >= 0 && normalized < itemCount) {
                return normalized;
            }
            // MRI reports the index as given, and says which way it is out.
            throw RubyExceptions.CreateIndexError(
                index < 0 ? "offset {0} too small for struct(size:{1})" : "offset {0} too large for struct(size:{1})", index, itemCount);
        }
    }
}
