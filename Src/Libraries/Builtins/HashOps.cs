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
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using System.Diagnostics;
using Microsoft.Scripting.Generation;
using System.Threading;
using IronRuby.Runtime.Calls;
using IronRuby.Runtime.Conversions;

namespace IronRuby.Builtins {

    /// <summary>
    /// Dictionary inherits from Object, mixes in Enumerable.
    /// Ruby hash is a Dictionary{object, object}, but it adds default value/proc
    /// 
    /// TODO: Not all .NET types implement the right Equals, GetHashCode semantics (e.g. List{object})
    /// </summary>
    [RubyClass("Hash", Extends = typeof(Hash), Inherits = typeof(object)), Includes(typeof(IDictionary<object, object>), Copy = true)]
    public static class HashOps {

        #region Construction

        [RubyConstructor]
        public static Hash/*!*/ CreateHash(RubyClass/*!*/ self) {
            return new Hash(self.Context.EqualityComparer);
        }

        [RubyConstructor]
        public static Hash/*!*/ CreateHash(BlockParam block, RubyClass/*!*/ self, object defaultValue) {
            var result = new Hash(self.Context.EqualityComparer);
            return Initialize(self.Context, block, result, defaultValue);
        }

        [RubyConstructor]
        public static Hash/*!*/ CreateHash(BlockParam block, RubyClass/*!*/ self, object defaultValue, object options) {
            var result = new Hash(self.Context.EqualityComparer);
            return Initialize(self.Context, block, result, defaultValue, options);
        }

        // Hash.new's one keyword is capacity: (3.4), a size hint that has no use here. A Hash passed
        // as keywords is options; one passed as a plain argument is the default value.
        private static bool IsOptions(object arg) {
            var hash = arg as Hash;
            return hash != null && hash.IsKeywordArguments;
        }

        private static void CheckOptions(RubyContext/*!*/ context, Hash/*!*/ options) {
            var unknown = new List<string>();
            foreach (var entry in options) {
                var symbol = entry.Key as RubySymbol;
                if (symbol == null || symbol.ToString() != "capacity") {
                    unknown.Add(context.Inspect(entry.Key).ToString());
                } else if (!(entry.Value is int || entry.Value is System.Numerics.BigInteger || entry.Value is double)) {
                    // MRI converts the hint with to_int; the hint itself is not used here.
                    throw RubyExceptions.CreateImplicitConversionError(context.GetClassDisplayName(entry.Value), "Integer");
                }
            }
            if (unknown.Count > 0) {
                throw RubyExceptions.CreateArgumentError(
                    (unknown.Count == 1 ? "unknown keyword: " : "unknown keywords: ") + String.Join(", ", unknown)
                );
            }
        }

        /// <summary>
        /// A lambda default proc has to be callable with (hash, key): MRI accepts arity 2 and any
        /// negative arity down to -3, and refuses the rest up front.
        /// </summary>
        private static Proc CheckDefaultProc(Proc proc) {
            if (proc != null && proc.Kind == ProcKind.Lambda) {
                int arity = proc.Dispatcher.Arity;
                if (arity != 2 && (arity >= 0 || arity < -3)) {
                    throw RubyExceptions.CreateTypeError("default_proc takes two arguments (2 for {0})", arity);
                }
            }
            return proc;
        }

        [RubyConstructor]
        public static Hash/*!*/ CreateHash([NotNull]BlockParam/*!*/ defaultProc, RubyClass/*!*/ self) {
            return new Hash(self.Context.EqualityComparer, CheckDefaultProc(defaultProc.Proc), null);
        }

        [RubyMethod("[]", RubyMethodAttributes.PublicSingleton)]
        public static Hash/*!*/ CreateSubclass(RubyClass/*!*/ self) {
            return Hash.CreateInstance(self);
        }

        [RubyMethod("[]", RubyMethodAttributes.PublicSingleton)]
        public static Hash/*!*/ CreateSubclass(ConversionStorage<IDictionary<object, object>>/*!*/ toHash, ConversionStorage<IList>/*!*/ toAry,
            RubyClass/*!*/ self, object listOrHash) {

            var toHashSite = toHash.GetSite(TryConvertToHashAction.Make(toHash.Context));
            var hash = toHashSite.Target(toHashSite, listOrHash);
            if (hash != null) {
                return CreateSubclass(self, hash);
            }

            var toArySite = toAry.GetSite(TryConvertToArrayAction.Make(toAry.Context));
            var array = toArySite.Target(toArySite, listOrHash);
            if (array != null) {
                return CreateSubclass(toAry, self, array);
            }

            throw RubyExceptions.CreateArgumentError("odd number of arguments for Hash");
        }

        [RubyMethod("[]", RubyMethodAttributes.PublicSingleton)]
        public static Hash/*!*/ CreateSubclass(ConversionStorage<IList>/*!*/ toAry, RubyClass/*!*/ self, [NotNull]IList/*!*/ list) {
            Hash result = Hash.CreateInstance(self);
            var toArySite = toAry.GetSite(TryConvertToArrayAction.Make(toAry.Context));
            for (int i = 0; i < list.Count; i++) {
                object item = list[i];
                IList pair = toArySite.Target(toArySite, item);
                if (pair == null) {
                    throw RubyExceptions.CreateArgumentError("wrong element type {0} at {1} (expected array)",
                        item == null ? "nil" : self.Context.GetClassDisplayName(item), i);
                }
                if (pair.Count < 1 || pair.Count > 2) {
                    throw RubyExceptions.CreateArgumentError("invalid number of elements ({0} for 1..2)", pair.Count);
                }
                RubyUtils.SetHashElement(self.Context, result, pair[0], (pair.Count == 2) ? pair[1] : null);
            }
            return result;
        }

        [RubyMethod("[]", RubyMethodAttributes.PublicSingleton)]
        public static Hash/*!*/ CreateSubclass(RubyClass/*!*/ self, [NotNull]IDictionary<object, object>/*!*/ hash) {
            // creates a new hash and copies entries of the given hash into it (no other objects associated with the has are copied):
            return IDictionaryOps.ReplaceData(Hash.CreateInstance(self), hash);
        }

        [RubyMethod("[]", RubyMethodAttributes.PublicSingleton)]
        public static Hash/*!*/ CreateSubclass(RubyClass/*!*/ self, params object[]/*!*/ items) {
            Debug.Assert(items.Length > 0);
            if (items.Length % 2 != 0) {
                throw RubyExceptions.CreateArgumentError("odd number of arguments for Hash");
            }

            return RubyUtils.SetHashElements(self.Context, Hash.CreateInstance(self), items);
        }

        // Reinitialization. Not called when a factory/non-default ctor is called.
        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static Hash/*!*/ Initialize(Hash/*!*/ self) {
            Assert.NotNull(self);
            self.RequireNotFrozen();
            // re-initializing resets the default value and the default proc
            self.DefaultProc = null;
            self.DefaultValue = null;
            return self;
        }

        // Reinitialization. Not called when a factory/non-default ctor is called.
        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static Hash/*!*/ Initialize(RubyContext/*!*/ context, BlockParam block, Hash/*!*/ self, object defaultValue) {
            Assert.NotNull(self);
            if (IsOptions(defaultValue)) {
                CheckOptions(context, (Hash)defaultValue);
                self.DefaultProc = (block != null) ? CheckDefaultProc(block.Proc) : null;
                self.DefaultValue = null;
                return self;
            }
            if (block != null) {
                throw RubyExceptions.CreateArgumentError("wrong number of arguments (given 1, expected 0)");
            }
            self.DefaultProc = null;
            self.DefaultValue = defaultValue;
            return self;
        }

        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static Hash/*!*/ Initialize(RubyContext/*!*/ context, BlockParam block, Hash/*!*/ self, object defaultValue, object options) {
            // a second argument is only allowed as keywords
            if (!IsOptions(options)) {
                throw RubyExceptions.CreateArgumentError("wrong number of arguments (given 2, expected 0..1)");
            }
            CheckOptions(context, (Hash)options);
            return Initialize(context, block, self, defaultValue);
        }

        // Reinitialization. Not called when a factory/non-default ctor is called.
        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static Hash/*!*/ Initialize([NotNull]BlockParam/*!*/ defaultProc, Hash/*!*/ self) {
            Assert.NotNull(self, defaultProc);

            self.DefaultProc = CheckDefaultProc(defaultProc.Proc);
            self.DefaultValue = null;
            return self;
        }

        [RubyMethod("initialize_copy", RubyMethodAttributes.PrivateInstance)]
        public static Hash/*!*/ InitializeCopy(RubyContext/*!*/ context, Hash/*!*/ self, [NotNull]Hash/*!*/ source) {
            self.DefaultProc = source.DefaultProc;
            self.DefaultValue = source.DefaultValue;
            IDictionaryOps.ReplaceData(self, source);
            return self;
        }

        #endregion

        [RubyMethod("try_convert", RubyMethodAttributes.PublicSingleton)]
        public static IDictionary<object, object> TryConvert(ConversionStorage<IDictionary<object, object>>/*!*/ toHash, RubyClass/*!*/ self, object obj) {
            var site = toHash.GetSite(TryConvertToHashAction.Make(toHash.Context));
            return site.Target(site, obj);
        }

        /// <summary>
        /// Whether the hash is one a ruby2_keywords method is carrying: it arrived as keyword
        /// arguments and splatting it will make it keyword arguments again.
        /// </summary>
        [RubyMethod("ruby2_keywords_hash?", RubyMethodAttributes.PublicSingleton)]
        public static bool IsRuby2KeywordsHash(RubyClass/*!*/ self, [NotNull]Hash/*!*/ hash) {
            return hash.IsRuby2KeywordsHash;
        }

        [RubyMethod("ruby2_keywords_hash?", RubyMethodAttributes.PublicSingleton)]
        public static bool IsRuby2KeywordsHash(RubyContext/*!*/ context, RubyClass/*!*/ self, object hash) {
            throw RubyExceptions.CreateTypeError("wrong argument type {0} (expected Hash)", context.GetClassDisplayName(hash));
        }

        /// <summary>
        /// Marks the hash the way a ruby2_keywords method's rest parameter would. The copying is
        /// left to Hash#dup in the prelude, which knows about subclasses and instance variables.
        /// </summary>
        [RubyMethod("__ir_mark_ruby2_keywords_hash__", RubyMethodAttributes.PrivateSingleton)]
        public static Hash/*!*/ MarkRuby2KeywordsHash(RubyClass/*!*/ self, [NotNull]Hash/*!*/ hash) {
            hash.IsRuby2KeywordsHash = true;
            return hash;
        }
        
        #region Instance Methods
        
        [RubyMethod("[]")]
        public static object GetElement(BinaryOpStorage/*!*/ storage, IDictionary<object, object>/*!*/ self, object key) {
            object result;
            if (!self.TryGetValue(CustomStringDictionary.NullToObj(key), out result)) {
                var site = storage.GetCallSite("default", 1);
                return site.Target(site, self, key);
            }
            return result;
        }

        [RubyMethod("default")]
        public static object GetDefaultValue(RubyContext/*!*/ context, Hash/*!*/ self) {
            return self.DefaultValue;
        }

        [RubyMethod("default")]
        public static object GetDefaultValue(CallSiteStorage<Func<CallSite, Proc, Hash, object, object>>/*!*/ storage, Hash/*!*/ self, object key) {
            if (self.DefaultProc != null) {
                var site = storage.GetCallSite("call", 2);
                return site.Target(site, self.DefaultProc, self, key);
            }
            return self.DefaultValue;
        }

        [RubyMethod("default=")]
        public static object SetDefaultValue(RubyContext/*!*/ context, Hash/*!*/ self, object value) {
            self.DefaultProc = null;
            return self.DefaultValue = value;
        }

        [RubyMethod("default_proc")]
        public static Proc GetDefaultProc(Hash/*!*/ self) {
            return self.DefaultProc;
        }

        [RubyMethod("compare_by_identity")]
        public static Hash/*!*/ CompareByIdentity(Hash/*!*/ self) {
            return self.CompareByIdentity();
        }

        [RubyMethod("compare_by_identity?")]
        public static bool ComparesByIdentity(Hash/*!*/ self) {
            return self.ComparesByIdentity;
        }
        
        [RubyMethod("replace")]
        public static Hash/*!*/ Replace(RubyContext/*!*/ context, Hash/*!*/ self, [DefaultProtocol, NotNull]IDictionary<object,object>/*!*/ other) {
            if (Object.ReferenceEquals(self, other)) {
                self.RequireNotFrozen();
                return self;
            }
            self.RequireNotFrozen();
            if (self.IsIterating) {
                throw RubyExceptions.CreateRuntimeError("can't replace hash during iteration");
            }

            Hash otherHash = other as Hash;
            if (otherHash != null) {
                self.DefaultValue = otherHash.DefaultValue;
                self.DefaultProc = otherHash.DefaultProc;
                // #replace takes over the argument's compare_by_identity flag, in both directions
                self.SetComparesByIdentity(otherHash.ComparesByIdentity, context.EqualityComparer);
            }
            return IDictionaryOps.ReplaceData(self, other);
        }

        [RubyMethod("shift")]
        public static object Shift(CallSiteStorage<Func<CallSite, Hash, object, object>>/*!*/ storage, Hash/*!*/ self) {
            self.RequireNotFrozen();
            
            if (self.Count == 0) {
                var site = storage.GetCallSite("default", 1);
                return site.Target(site, self, null);
            }

            IEnumerator<KeyValuePair<object, object>> e = self.GetEnumerator();
            e.MoveNext();
            KeyValuePair<object, object> pair = e.Current;
            self.Remove(pair.Key);

            return IDictionaryOps.MakeArray(pair);
        }

        #endregion
    }

}
