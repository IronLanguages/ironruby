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
using System.Threading;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;

namespace IronRuby.Builtins {

    /// <summary>
    /// What an all-visible method lookup of one name on one class found: the method (forwarders
    /// already followed to the body they forward to, as <see cref="RubyModule.ResolveMethod"/> answers)
    /// and the visibility the name has *at its entry point* - the forwarder's, when `public :m' in a
    /// subclass re-exported an inherited private method, which is what #respond_to? and
    /// #public_method_defined? ask about.
    /// </summary>
    public sealed class CachedMethodLookup {
        public static readonly CachedMethodLookup/*!*/ NotFound = new CachedMethodLookup(MethodResolutionResult.NotFound, RubyMethodVisibility.None);

        public readonly MethodResolutionResult Result;
        public readonly RubyMethodVisibility Visibility;

        internal CachedMethodLookup(MethodResolutionResult result, RubyMethodVisibility visibility) {
            Result = result;
            Visibility = visibility;
        }

        public bool Found {
            get { return Result.Found; }
        }

        public RubyMemberInfo Info {
            get { return Result.Info; }
        }
    }

    // Per-class method-lookup cache: name -> CachedMethodLookup, lock-free to read.
    //
    // Invalidation keys on the class's own VersionHandle.Method, the version the call-site rules
    // check. It is bumped (under the class-hierarchy lock) for a class and every class below it
    // whenever a method change there could alter what a *cached* lookup would find - but only for
    // lookups that registered themselves the way a call site does, since IronRuby bumps lazily:
    //  - a found method is marked InvalidateSitesOnOverride, so overriding, undefining, removing,
    //    hiding or re-visibility-ing it (anywhere below its owner, including via include/extend)
    //    bumps the version;
    //  - an absent name goes into RubyContext.MissingMethodsCachedInSites, so a later definition
    //    of that name in any class, or in any module mixed into one, bumps it.
    // Prepend, refine, extension methods and TracePoint bump unconditionally. Registering exactly
    // as call sites do means the cache is sound for precisely the reason cached rules are.
    //
    // A fill takes the class-hierarchy lock, reads the version, resolves and publishes under that
    // one lock hold; versions only change under the same lock, so an entry can never be filed under
    // a version it was not computed at. A reader that races a concurrent definition sees either the
    // state before it or falls through to the lock - the same guarantee a call site's version check
    // gives. Only classes are cached: a module's VersionHandle is never bumped (only the classes
    // that depend on it are), so a module's lookups keep taking the lock.
    public partial class RubyModule {
        private sealed class MethodLookupTable {
            internal readonly int Version;
            internal readonly Dictionary<string, CachedMethodLookup>/*!*/ Entries;

            internal MethodLookupTable(int version, Dictionary<string, CachedMethodLookup>/*!*/ entries) {
                Version = version;
                Entries = entries;
            }
        }

        // An upper bound on the names cached per class, for programs that ask with generated names.
        private const int MaxCachedMethodLookups = 256;

        // Copy-on-write: a published table is never mutated, so readers need no lock.
        private MethodLookupTable _methodLookups;

        /// <summary>
        /// All-visible lookup of <paramref name="name"/>, ignoring refinements. Lock-free once warm for a class.
        /// </summary>
        public CachedMethodLookup/*!*/ GetMethodLookup(string/*!*/ name) {
            int version;
            return GetMethodLookup(name, out version);
        }

        /// <summary>
        /// <see cref="GetMethodLookup(string)"/>, and the method version the answer belongs to, for a caller
        /// that keeps the answer itself (a call site's storage): it stays right while <see cref="Version"/>.Method
        /// equals <paramref name="version"/>. -1 when the answer must not be kept.
        /// </summary>
        public CachedMethodLookup/*!*/ GetMethodLookup(string/*!*/ name, out int version) {
            var table = Volatile.Read(ref _methodLookups);
            CachedMethodLookup entry;
            if (table != null && table.Version == Version.Method && table.Entries.TryGetValue(name, out entry)) {
                version = table.Version;
                return entry;
            }
            return FillMethodLookup(name, out version);
        }

        private CachedMethodLookup/*!*/ FillMethodLookup(string/*!*/ name, out int publishedVersion) {
            publishedVersion = -1;
            using (Context.ClassHierarchyLocker()) {
                int version = Version.Method;
                var entry = ResolveMethodLookupNoLock(name);

                // Not while a library method table is being populated: its methods go in without the
                // version bump a later definition would make.
                if (!IsClass || Version.Method != version || Context.MethodTableInitializationDepth != 0) {
                    return entry;
                }

                var table = _methodLookups;
                Dictionary<string, CachedMethodLookup> entries;
                if (table == null || table.Version != version) {
                    entries = new Dictionary<string, CachedMethodLookup>(StringComparer.Ordinal);
                } else if (table.Entries.Count >= MaxCachedMethodLookups) {
                    // Not filed. A method found is registered for invalidation all the same (InvalidateSitesOnOverride),
                    // so a caller may keep it as long as a filed answer; an absent name would need registering first.
                    if (entry.Found) {
                        publishedVersion = version;
                    }
                    return entry;
                } else {
                    entries = new Dictionary<string, CachedMethodLookup>(table.Entries, StringComparer.Ordinal);
                }

                if (!entry.Found) {
                    // A definition of this name anywhere now bumps the versions of the classes it affects:
                    entry.Result.InvalidateSitesOnMissingMethodAddition(name, Context);
                }

                entries[name] = entry;
                Volatile.Write(ref _methodLookups, new MethodLookupTable(version, entries));
                publishedVersion = version;
                return entry;
            }
        }

        /// <summary>
        /// The definition of <paramref name="name"/> in this module's own method table only (method_defined?'s
        /// inherit = false: MRI looks up from the origin and requires the entry's owner to be this module, so
        /// neither prepended modules nor ancestors count). Not cached; it is rare.
        /// </summary>
        public CachedMethodLookup/*!*/ GetOwnMethodLookup(string/*!*/ name) {
            using (Context.ClassHierarchyLocker()) {
                InitializeMethodsNoLock();

                RubyMemberInfo info;
                bool skipHidden = false;
                if (!TryGetMethod(name, ref skipHidden, out info) || info == null || info.IsUndefined) {
                    return CachedMethodLookup.NotFound;
                }

                if (info.IsSuperForwarder) {
                    var target = ResolveSuperMethodNoLock(((SuperForwarderInfo)info).SuperName, this);
                    return target.Found ? new CachedMethodLookup(target, info.Visibility) : CachedMethodLookup.NotFound;
                }

                return new CachedMethodLookup(new MethodResolutionResult(info, this, true), info.Visibility);
            }
        }

        private CachedMethodLookup/*!*/ ResolveMethodLookupNoLock(string/*!*/ name) {
            Context.RequiresClassHierarchyLock();

            // The same walk as ResolveMethodNoLock(name, AllVisible), except that a super-forwarder is
            // returned so its visibility can be kept, then followed the way that walk follows it.
            var result = ResolveMethodNoLock(name, VisibilityContext.AllVisible, MethodLookup.ReturnForwarder).InvalidateSitesOnOverride();
            if (!result.Found) {
                return CachedMethodLookup.NotFound;
            }

            var visibility = result.Info.Visibility;
            if (result.Info.IsSuperForwarder) {
                var forwarder = (SuperForwarderInfo)result.Info;
                result = result.Owner.ResolveSuperMethodNoLock(forwarder.SuperName, result.Owner).InvalidateSitesOnOverride();
                if (!result.Found) {
                    return CachedMethodLookup.NotFound;
                }
            }

            return new CachedMethodLookup(result, visibility);
        }
    }
}
