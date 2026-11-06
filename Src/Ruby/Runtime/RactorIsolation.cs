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
using IronRuby.Builtins;

namespace IronRuby.Runtime {
    /// <summary>
    /// What a non-main Ractor keeps for itself that the runtime reads: the globals CRuby makes
    /// Ractor-local ($stdin, $stdout, $stderr, $VERBOSE, $DEBUG). A Ractor starts with its own
    /// standard IO objects over the process's descriptors 0-2, and its creator's $VERBOSE and $DEBUG.
    /// </summary>
    public sealed class RactorLocals {
        public readonly RubyContext/*!*/ Context;
        public object StandardInput;
        public object StandardOutput;
        public object StandardErrorOutput;
        public object Verbose;
        public object Debug;

        public RactorLocals(RubyContext/*!*/ context) {
            Context = context;
        }
    }

    /// <summary>
    /// The isolation CRuby enforces in a non-main Ractor, for the parts of it the runtime itself
    /// implements: global variables, class variables, instance variables of classes and modules
    /// (and of shareable objects), and constants. Ractors are a library (RactorOps.cs), which
    /// installs <see cref="CurrentLocals"/> and <see cref="IsShareableObject"/> before it starts
    /// the first non-main Ractor of a runtime and sets <see cref="RubyContext.RactorsStarted"/>.
    /// Until then every check is one test of that flag, and the main Ractor never pays more than
    /// the thread lookup that tells it is the main one.
    /// </summary>
    public static class RactorIsolation {
        /// <summary>The state of the non-main Ractor the current thread runs in; null in the main Ractor.</summary>
        public static Func<RactorLocals> CurrentLocals;

        /// <summary>Ractor.shareable?</summary>
        public static Func<RubyContext, object, bool> IsShareableObject;

        /// <summary>
        /// The Ractor-local state of the current thread's Ractor in <paramref name="context"/>, or
        /// null in its main Ractor. Callers test <see cref="RubyContext.RactorsStarted"/> first.
        /// </summary>
        public static RactorLocals GetNonMain(RubyContext/*!*/ context) {
            var current = CurrentLocals;
            RactorLocals locals = (current != null) ? current() : null;
            return (locals != null && locals.Context == context) ? locals : null;
        }

        public static bool IsNonMain(RubyContext/*!*/ context) {
            return context.RactorsStarted && GetNonMain(context) != null;
        }

        public static bool IsShareable(RubyContext/*!*/ context, object value) {
            var check = IsShareableObject;
            return check == null || check(context, value);
        }

        /// <summary>Creates a Ractor::IsolationError.</summary>
        public static Func<string, Exception> CreateIsolationError;

        private static Exception/*!*/ IsolationError(string/*!*/ message) {
            var create = CreateIsolationError;
            return (create != null) ? create(message) : new RuntimeError(message);
        }

        #region Global variables

        // CRuby's rb_gvar_ractor_local: the globals a non-main Ractor may use. $stdin, $stdout,
        // $stderr, $VERBOSE and $DEBUG (and their aliases) have a value per Ractor; the others are
        // per thread or scope ($~, $_, $!), read-only ($$, $-p) or safe to share ($/).
        private static readonly HashSet<string>/*!*/ _ractorLocalGlobals = new HashSet<string>(StringComparer.Ordinal) {
            "@", "!", "/", "-0", "_", "stdin", "stdout", ">", "stderr", "-i", "$", "?",
            "~", "&", "`", "'", "+", "-p", "-l", "-a", "VERBOSE", "-v", "-w", "-W", "DEBUG", "-d",
        };

        /// <summary>
        /// The non-main Ractor's state if the current thread runs in one and the global
        /// <paramref name="name"/> (without the $) is one it may use; raises if it may not;
        /// null in the main Ractor.
        /// </summary>
        public static RactorLocals CheckGlobalVariable(RubyContext/*!*/ context, string/*!*/ name) {
            var locals = GetNonMain(context);
            if (locals != null && !_ractorLocalGlobals.Contains(name)) {
                throw IsolationError("can not access global variable $" + name + " from non-main Ractor");
            }
            return locals;
        }

        /// <summary>Kernel#global_variables and `alias $a $b`.</summary>
        public static void CheckGlobalVariables(RubyContext/*!*/ context) {
            if (IsNonMain(context)) {
                throw IsolationError("can not access global variables from non-main Ractors");
            }
        }

        internal static bool IsDebugVariable(string/*!*/ name) {
            return name == "DEBUG" || name == "-d";
        }

        #endregion

        #region Class variables, instance variables

        /// <summary>Any access to a class variable from a non-main Ractor.</summary>
        public static void CheckClassVariable(RubyModule/*!*/ module, string/*!*/ name) {
            var context = module.Context;
            if (IsNonMain(context)) {
                throw IsolationError("can not access class variables from non-main Ractors (" + name + " from " +
                    module.GetDisplayName(context, false) + ")");
            }
        }

        /// <summary>
        /// The value of instance variable <paramref name="name"/> of <paramref name="obj"/>, read
        /// in a non-main Ractor: a class or module may only hand out a shareable value, and the
        /// variables of other shareable objects (a Ractor) are out of reach altogether.
        /// </summary>
        public static object CheckInstanceVariableRead(RubyContext/*!*/ context, object obj, string/*!*/ name, object value) {
            if (!IsNonMain(context)) {
                return value;
            }
            var module = obj as RubyModule;
            if (module != null) {
                if (!IsShareable(context, value)) {
                    throw IsolationError("can not get unshareable values from instance variables of classes/modules from non-main Ractors (" +
                        name + " from " + module.GetDisplayName(context, false) + ")");
                }
            } else {
                CheckSharedObjectInstanceVariables(context, obj);
            }
            return value;
        }

        /// <summary>A write (or removal) of an instance variable of <paramref name="obj"/> in a non-main Ractor.</summary>
        public static void CheckInstanceVariableWrite(RubyContext/*!*/ context, object obj) {
            if (!IsNonMain(context)) {
                return;
            }
            if (obj is RubyModule) {
                throw IsolationError("can not set instance variables of classes/modules by non-main Ractors");
            }
            CheckSharedObjectInstanceVariables(context, obj);
        }

        // CRuby's ivar_ractor_check: a shareable object that is not frozen (a Ractor, a port) is
        // shared by reference, so its instance variables would be shared with it.
        private static void CheckSharedObjectInstanceVariables(RubyContext/*!*/ context, object obj) {
            if (!(obj is IRubyObject) && obj != null && !context.IsObjectFrozen(obj) && IsShareable(context, obj)) {
                throw IsolationError("can not access instance variables of shareable objects from non-main Ractors");
            }
        }

        #endregion

        #region Constants

        /// <summary>
        /// The value of constant <paramref name="name"/> of <paramref name="owner"/>, read in a
        /// non-main Ractor, which may only see shareable values.
        /// </summary>
        public static object CheckConstantRead(RubyContext/*!*/ context, RubyModule owner, string/*!*/ name, object value) {
            if (value != null && !IsTriviallyShareable(value) && IsNonMain(context) && !IsShareable(context, value)) {
                throw IsolationError("can not access non-shareable objects in constant " +
                    (owner ?? context.ObjectClass).GetDisplayName(context, false) + "::" + name + " by non-main Ractor.");
            }
            return value;
        }

        public static void CheckConstantWrite(RubyContext/*!*/ context, object value) {
            if (!IsTriviallyShareable(value) && IsNonMain(context) && !IsShareable(context, value)) {
                throw IsolationError("can not set constants with non-shareable objects by non-main Ractors");
            }
        }

        /// <summary>
        /// Values a constant site may cache as they are once Ractors have started: the rest are
        /// wrapped in a <see cref="RactorCheckedConstant"/>, which a read unwraps through the check.
        /// </summary>
        internal static bool IsTriviallyShareable(object value) {
            return value == null || value is RubyModule || value is int || value is bool || value is double || value is RubySymbol
                || value is long || value is System.Numerics.BigInteger;
        }

        #endregion
    }

    /// <summary>
    /// How many times compiled code assigns a local variable, a parameter counting as one: CRuby's
    /// lvar_states, which tell a shareable proc that an outer variable "may be reassigned"
    /// (BlockDispatcher.ReassignedOuterVariableNames). Counted while the variable's scope compiles.
    /// </summary>
    public sealed class LocalAssignmentCount {
        internal int Count;
    }

    /// <summary>
    /// What a constant site caches for a value that a non-main Ractor may not be allowed to read,
    /// once a runtime has started a Ractor (RubyOps.GetUnqualifiedConstant and friends): the
    /// compiled read hands it to <see cref="RubyOps.ReadRactorCheckedConstant"/>.
    /// </summary>
    public sealed class RactorCheckedConstant {
        internal readonly RubyContext/*!*/ Context;
        // the constant's value, or its weak reference
        internal readonly object Value;
        internal readonly RubyModule Owner;
        internal readonly string/*!*/ Name;

        internal RactorCheckedConstant(RubyContext/*!*/ context, object value, RubyModule owner, string/*!*/ name) {
            Context = context;
            Value = value;
            Owner = owner;
            Name = name;
        }
    }
}
