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

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using IronRuby.Builtins;
using IronRuby.Compiler;
using Microsoft.Scripting.Runtime;
using AstUtils = Microsoft.Scripting.Ast.Utils;

namespace IronRuby.Runtime.Calls {
    using Ast = Expression;

    /// <summary>
    /// Rules the call binder builds itself for calls of Kernel's dynamic-dispatch helpers.
    /// </summary>
    internal static class DispatchIntrinsics {
        // The method infos last found to be Kernel#respond_to? and Kernel#respond_to_missing? (or aliases of them).
        private static RubyMemberInfo _respondTo, _respondToMissing;

        // The method infos last found to be Kernel#send and BasicObject#__send__ (or aliases of them).
        private static RubyMemberInfo _send, _basicSend;

        // Rules specialized on a name, per receiver class, so that a site sending or asking about computed
        // names does not bind a rule per name: past the limit the class's sites get the general rules.
        private sealed class FoldCounts {
            internal int RespondTo;
            internal int Send;
        }

        private const int MaxFoldsPerClass = 32;
        private static readonly ConditionalWeakTable<RubyClass, FoldCounts>/*!*/ _folds = new ConditionalWeakTable<RubyClass, FoldCounts>();

        private static bool IsKernelMethod(RubyMemberInfo/*!*/ method, ref RubyMemberInfo last, string/*!*/ clrName) {
            if (ReferenceEquals(method, last)) {
                return true;
            }

            var libraryMethod = method as RubyLibraryMethodInfo;
            if (libraryMethod == null) {
                return false;
            }

            var members = libraryMethod.GetMembers();
            if (members.Length == 1 && members[0].Name == clrName && members[0].DeclaringType.FullName == "IronRuby.Builtins.KernelOps") {
                last = method;
                return true;
            }
            return false;
        }

        /// <summary>
        /// `obj.respond_to?(:name)' answered by method lookups alone - no refinement of the receiver's class
        /// with the name, the name's method or Kernel#respond_to_missing? found - is a constant for as long as
        /// the receiver class's method version stays the same, which the rule's receiver test already checks: the lookups are
        /// registered for invalidation the way a call site's own resolution is (RubyModule.GetMethodLookup).
        /// The symbol is part of the rule's test, so each literal site gets a rule of its own.
        /// </summary>
        internal static bool TryBuildRespondTo(MetaObjectBuilder/*!*/ metaBuilder, CallArguments/*!*/ args, RubyMemberInfo/*!*/ method) {
            var signature = args.Signature;
            if (signature.HasBlock || signature.HasSplattedArgument || signature.HasRhsArgument || signature.ResolveOnly ||
                args.SimpleArgumentCount != 1 || !IsKernelMethod(method, ref _respondTo, "RespondTo")) {
                return false;
            }

            // Only a Ruby object's rule tests its exact class; a CLR object's may cover an instance with singleton methods.
            var symbol = args.GetSimpleArgument(0) as RubySymbol;
            var targetClass = args.TargetClass;
            if (symbol == null || !(args.Target is IRubyObject)) {
                return false;
            }

            // Kernel#respond_to? sees the refinements active where it is called: where one could supply
            // either method it would look up, the answer is not the class's alone. Refining either name
            // re-versions the class, which the rule's receiver test checks.
            string name = symbol.ToString();
            var context = targetClass.Context;
            if (context.HasRefinements) {
                using (context.ClassHierarchyLocker()) {
                    if (context.IsRefinementSensitive(targetClass, name) || context.IsRefinementSensitive(targetClass, Symbols.RespondToMissing)) {
                        return false;
                    }
                }
            }
            int version;
            var lookup = targetClass.GetMethodLookup(name, out version);
            if (version < 0) {
                return false;
            }

            bool answer;
            if (lookup.Found && lookup.Visibility == RubyMethodVisibility.Public) {
                var libraryMethod = lookup.Info as RubyLibraryMethodInfo;
                answer = libraryMethod == null || !libraryMethod.IsNotImplemented;
            } else {
                // a user-defined respond_to_missing? is called every time:
                int missingVersion;
                var missing = targetClass.GetMethodLookup(Symbols.RespondToMissing, out missingVersion);
                if (missingVersion != version || missing.Found && !IsKernelMethod(missing.Info, ref _respondToMissing, "RespondToMissing")) {
                    return false;
                }
                answer = false;
            }

            if (Interlocked.Increment(ref _folds.GetOrCreateValue(targetClass).RespondTo) > MaxFoldsPerClass) {
                return false;
            }

            metaBuilder.AddCondition(Ast.Equal(AstUtils.Convert(args.GetSimpleArgumentExpression(0), typeof(object)), Ast.Constant(symbol, typeof(object))));
            metaBuilder.Result = Ast.Constant(ScriptingRuntimeHelpers.BooleanToObject(answer), typeof(object));
            return true;
        }

        private static bool IsSendMethod(RubyMemberInfo/*!*/ method) {
            if (ReferenceEquals(method, _send) || ReferenceEquals(method, _basicSend)) {
                return true;
            }

            var libraryMethod = method as RubyLibraryMethodInfo;
            if (libraryMethod == null) {
                return false;
            }

            var members = libraryMethod.GetMembers();
            if (members.Length == 0 || members[0].Name != "SendMessage") {
                return false;
            }

            switch (members[0].DeclaringType.FullName) {
                case "IronRuby.Builtins.KernelOps": _send = method; return true;
                case "IronRuby.Builtins.BasicObjectOps": _basicSend = method; return true;
                default: return false;
            }
        }

        /// <summary>
        /// `obj.send(:name, ...)' (or __send__) is bound as the call of name it makes - the call with implicit
        /// self, scope and block that Kernel#send's own site makes - so that it costs one dispatch rather than
        /// two, as MRI's optimized send does (no send frame appears in a backtrace there either). The symbol is
        /// part of the rule's test; the caller binds the call of <paramref name="sentName"/> on the rewritten
        /// <paramref name="args"/>.
        /// </summary>
        internal static bool TryBuildSend(MetaObjectBuilder/*!*/ metaBuilder, CallArguments/*!*/ args, RubyMemberInfo/*!*/ method,
            out string sentName) {

            sentName = null;
            var signature = args.Signature;
            if (!signature.HasScope || signature.HasSplattedArgument || signature.HasRhsArgument || signature.ResolveOnly ||
                signature.IsVirtualCall || signature.IsSuperCall || signature.IsInteropCall || args.SimpleArgumentCount < 1 ||
                !IsSendMethod(method)) {
                return false;
            }

            var symbol = args.GetSimpleArgument(0) as RubySymbol;
            if (symbol == null || Interlocked.Increment(ref _folds.GetOrCreateValue(args.TargetClass).Send) > MaxFoldsPerClass) {
                return false;
            }

            metaBuilder.AddCondition(Ast.Equal(AstUtils.Convert(args.GetSimpleArgumentExpression(0), typeof(object)), Ast.Constant(symbol, typeof(object))));
            args.RemoveSendMethodName();
            sentName = symbol.ToString();
            return true;
        }

        /// <summary>
        /// The rules TryBuildRespondTo and TryBuildSend build depend on the receiver's class, not only on the
        /// method, so RubyCallAction.BindShared must not hand them to receivers of other classes.
        /// </summary>
        internal static bool MaySpecialize(RubyMemberInfo/*!*/ method) {
            return IsKernelMethod(method, ref _respondTo, "RespondTo") || IsSendMethod(method);
        }
    }
}
