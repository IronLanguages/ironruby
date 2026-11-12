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
using System.Runtime.CompilerServices;
using System.Threading;
using IronRuby.Builtins;
using IronRuby.Compiler.Generation;
using IronRuby.Runtime.Calls;

namespace IronRuby.Runtime {

    // Storages of the dynamic-dispatch helpers (#send, #__send__, #public_send, #respond_to?).
    //
    // A library method's storage is created for the rule that binds the call, so it is a per-site
    // cache - shared only with the other sites of the same binder that the binder's rule cache
    // hands the same rule (sites of the same arity that saw a receiver of the same class). That is
    // why each of these remembers a few names rather than one.

    /// <summary>
    /// The call sites #send and friends dispatch through, by method name. A send of a literal name
    /// finds its site by comparing one reference instead of taking the context's lock and hashing the
    /// name (<see cref="RubyContext.GetOrCreateSendSite"/>), and each name gets a site of its own, as
    /// monomorphic as the send site itself is.
    /// </summary>
    public sealed class SendSiteStorage<TSiteFunc> : RubyCallSiteStorage where TSiteFunc : class {
        private sealed class Entry {
            internal readonly string/*!*/ Name;
            internal readonly RubyCallSignature Signature;
            internal readonly CallSite<TSiteFunc>/*!*/ Site;

            internal Entry(string/*!*/ name, RubyCallSignature signature, CallSite<TSiteFunc>/*!*/ site) {
                Name = name;
                Signature = signature;
                Site = site;
            }
        }

        // Past this many names a site sends computed names; those share the context's sites.
        private const int Capacity = 8;

        // Filled in order and never replaced, so a reader needs no lock.
        private readonly Entry[]/*!*/ _entries = new Entry[Capacity];
        private int _count;

        [Emitted]
        public SendSiteStorage(RubyContext/*!*/ context) : base(context) { }

        public CallSite<TSiteFunc>/*!*/ GetCallSite(string/*!*/ methodName, RubyCallSignature signature) {
            var entries = _entries;
            for (int i = 0; i < entries.Length; i++) {
                var entry = entries[i];
                if (entry == null) {
                    break;
                }
                if ((object)entry.Name == (object)methodName && entry.Signature.Equals(signature)) {
                    return entry.Site;
                }
            }
            return GetCallSiteSlow(methodName, signature);
        }

        private CallSite<TSiteFunc>/*!*/ GetCallSiteSlow(string/*!*/ methodName, RubyCallSignature signature) {
            // the same name in another string instance:
            var entries = _entries;
            for (int i = 0; i < entries.Length; i++) {
                var entry = entries[i];
                if (entry == null) {
                    break;
                }
                if (entry.Name == methodName && entry.Signature.Equals(signature)) {
                    return entry.Site;
                }
            }

            lock (entries) {
                int count = _count;
                for (int i = 0; i < count; i++) {
                    var entry = entries[i];
                    if (entry.Name == methodName && entry.Signature.Equals(signature)) {
                        return entry.Site;
                    }
                }

                if (count < Capacity) {
                    var site = CallSite<TSiteFunc>.Create(RubyCallAction.Make(Context, methodName, signature));
                    Volatile.Write(ref entries[count], new Entry(methodName, signature, site));
                    _count = count + 1;
                    return site;
                }
            }

            return Context.GetOrCreateSendSite<TSiteFunc>(methodName, signature);
        }
    }

    /// <summary>
    /// #respond_to?'s answers at one site, and the site it calls a user-defined respond_to_missing? through.
    /// An answer is kept only when it came from method lookups alone, and only for as long as the
    /// receiver class's method version stays where it was when they were made - the validity the
    /// class's own lookup cache has (<see cref="RubyModule.GetMethodLookup(string, out int)"/>), which
    /// registers its lookups for invalidation exactly as call sites do.
    /// </summary>
    public sealed class RespondToCacheStorage : CallSiteStorage<Func<CallSite, object, object, object, object>> {
        private sealed class Answer {
            internal readonly RubyClass/*!*/ Class;
            internal readonly int Version;
            internal readonly string/*!*/ Name;
            internal readonly bool IncludePrivate;
            internal readonly bool Result;

            internal Answer(RubyClass/*!*/ cls, int version, string/*!*/ name, bool includePrivate, bool result) {
                Class = cls;
                Version = version;
                Name = name;
                IncludePrivate = includePrivate;
                Result = result;
            }
        }

        private const int Capacity = 8;

        // Each slot is replaced as a whole, so a reader needs no lock.
        private readonly Answer[]/*!*/ _answers = new Answer[Capacity];
        private int _next;

        [Emitted]
        public RespondToCacheStorage(RubyContext/*!*/ context) : base(context) { }

        public bool TryGetAnswer(RubyClass/*!*/ cls, string/*!*/ name, bool includePrivate, out bool result) {
            var answers = _answers;
            for (int i = 0; i < answers.Length; i++) {
                var answer = answers[i];
                if (answer == null) {
                    break;
                }
                if ((object)answer.Name == (object)name && answer.Class == cls && answer.IncludePrivate == includePrivate
                    && answer.Version == cls.Version.Method) {
                    result = answer.Result;
                    return true;
                }
            }
            result = false;
            return false;
        }

        /// <summary>
        /// Keeps an answer computed from lookups made at method version <paramref name="version"/> of
        /// <paramref name="cls"/> (-1: lookups that must not be kept).
        /// </summary>
        public void SetAnswer(RubyClass/*!*/ cls, int version, string/*!*/ name, bool includePrivate, bool result) {
            if (version < 0) {
                return;
            }

            var answers = _answers;
            var newAnswer = new Answer(cls, version, name, includePrivate, result);

            // an answer for the same question made at an older version is replaced in place:
            for (int i = 0; i < answers.Length; i++) {
                var answer = answers[i];
                if (answer == null) {
                    break;
                }
                if ((object)answer.Name == (object)name && answer.Class == cls && answer.IncludePrivate == includePrivate) {
                    Volatile.Write(ref answers[i], newAnswer);
                    return;
                }
            }

            int index = _next;
            _next = (index + 1) % Capacity;
            Volatile.Write(ref answers[index], newAnswer);
        }
    }
}
