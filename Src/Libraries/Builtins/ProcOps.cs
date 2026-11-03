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
using System.Diagnostics;
using System.Runtime.CompilerServices;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using Microsoft.Scripting.Generation;
using System.Globalization;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace IronRuby.Builtins {

    [RubyClass("Proc", Extends = typeof(Proc), Inherits = typeof(Object))]
    public static class ProcOps {

        [RubyConstructor]
        public static void Error(RubyClass/*!*/ self, params object[] args) {
            throw RubyExceptions.CreateAllocatorUndefinedError(self);
        }

        #region ==, eql?, hash, dup, clone

        [RubyMethod("=="), RubyMethod("eql?")]
        public static bool Equal(Proc/*!*/ self, [NotNull]Proc/*!*/ other) {
            // two procs made from the same symbol are the same proc in MRI, which hands out a
            // cached one per symbol; here each Symbol#to_proc builds its own dispatcher
            if (self.SymbolName != null || other.SymbolName != null) {
                return self.SymbolName == other.SymbolName;
            }
            return self.Dispatcher == other.Dispatcher && self.LocalScope == other.LocalScope;
        }

        [RubyMethod("=="), RubyMethod("eql?")]
        public static bool Equal(Proc/*!*/ self, object other) {
            return false;
        }

        [RubyMethod("hash")]
        public static int GetHash(Proc/*!*/ self) {
            if (self.SymbolName != null) {
                return self.SymbolName.GetHashCode();
            }
            return self.Dispatcher.GetHashCode() ^ self.LocalScope.GetHashCode();
        }

        #endregion

        #region arity, lambda?, binding, source_location

        [RubyMethod("arity")]
        public static int GetArity(Proc/*!*/ self) {
            var signature = self.Dispatcher.ParameterSignature;
            return signature != null ? signature.GetArity(self.Kind == ProcKind.Lambda) : self.Dispatcher.Arity;
        }

        /// <summary>
        /// How the block was written. Whether a positional parameter counts as required depends
        /// on how the proc binds its arguments, so a lambda-ness that disagrees with the receiver
        /// can be forced with the `lambda:` keyword (anything but nil or false means a lambda).
        /// </summary>
        [RubyMethod("parameters")]
        public static RubyArray/*!*/ GetParameters(RubyContext/*!*/ context, Proc/*!*/ self, params object[]/*!*/ args) {
            bool isLambda = self.Kind == ProcKind.Lambda;
            // the only argument is the `lambda:` keyword, which arrives as a trailing hash
            var options = args.Length == 1 ? args[0] as IDictionary<object, object> : null;
            if (args.Length > 0 && options == null) {
                throw RubyOps.MakeWrongNumberOfArgumentsError(args.Length, 0);
            }
            if (options != null) {
                foreach (var entry in options) {
                    var key = entry.Key as RubySymbol;
                    if (key == null || key.ToString() != "lambda") {
                        throw RubyExceptions.CreateArgumentError(
                            String.Format(CultureInfo.InvariantCulture, "unknown keyword: {0}",
                                context.Inspect(entry.Key).ToString()));
                    }
                    // an explicit nil means "do not override", matching MRI
                    if (entry.Value != null) {
                        isLambda = RubyOps.IsTrue(entry.Value);
                    }
                }
            }

            var signature = self.Dispatcher.ParameterSignature;
            if (signature == null) {
                // a proc with no Ruby source behind it (Symbol#to_proc, a CLR method) takes
                // whatever it is given
                var result = new RubyArray(1);
                result.Add(new RubyArray(1) { context.CreateAsciiSymbol("rest") });
                return result;
            }
            return signature.GetParameterArray(context, isLambda);
        }

        [RubyMethod("lambda?")]
        public static bool IsLambda(Proc/*!*/ self) {
            return self.Kind == ProcKind.Lambda;
        }

        [RubyMethod("binding")]
        public static Binding/*!*/ GetLocalScope(Proc/*!*/ self) {
            // a curried or composed proc has no Ruby scope of its own to hand out
            if (self.LocalScope.IsEmpty) {
                throw RubyExceptions.CreateArgumentError("Can't create Binding from C level Proc");
            }
            // Method#to_proc makes a proc whose self is the method's receiver rather than the
            // scope's, and the binding reports that one
            var result = Binding.Create(self.LocalScope, self.Self);
            result.SourcePath = self.Dispatcher.SourcePath;
            result.SourceLine = self.Dispatcher.SourceLine;
            return result;
        }

        [RubyMethod("source_location")]
        public static RubyArray GetSourceLocation(Proc/*!*/ self) {
            // a proc that no Ruby source produced - Symbol#to_proc, Method#to_proc, a curried
            // proc - has no location to report
            if (self.Dispatcher.SourcePath == null) {
                return null;
            }
            return new RubyArray(2) {
                self.LocalScope.RubyContext.EncodePath(self.Dispatcher.SourcePath),
                self.Dispatcher.SourceLine
            };
        }

        #endregion

        #region to_s, to_proc

        /// <summary>
        /// MRI's format is "#&lt;Proc:0xADDRESS file:line (lambda)&gt;", with the location separated
        /// by a space rather than by the '@' this used to print, omitted entirely when the proc has
        /// no Ruby source, and replaced by "(&amp;:name)" for a proc a Symbol produced. The result is
        /// a binary string, as every #inspect-ish description in MRI is.
        /// </summary>
        [RubyMethod("to_s"), RubyMethod("inspect")]
        public static MutableString/*!*/ ToS(Proc/*!*/ self) {
            var context = self.LocalScope.RubyContext;

            var str = RubyUtils.ObjectToMutableStringPrefix(context, self);
            if (self.SymbolName != null) {
                str.Append("(&:");
                str.Append(self.SymbolName);
                str.Append(')');
            } else if (self.SourcePath != null) {
                str.Append(' ');
                str.Append(self.SourcePath);
                str.Append(':');
                str.Append(self.SourceLine.ToString(CultureInfo.InvariantCulture));
            }

            if (self.Kind == ProcKind.Lambda) {
                str.Append(" (lambda)"); 
            }

            str.Append('>');
            str.ForceEncoding(RubyEncoding.Binary);

            return str;
        }

        [RubyMethod("to_proc")]
        public static Proc/*!*/ ToProc(Proc/*!*/ self) {
            return self;
        }

        /// <summary>
        /// The flag only means something for a proc whose whole argument list is a rest parameter,
        /// since that is the only shape that can hand a trailing hash on untouched. MRI warns and
        /// does nothing for anything else.
        /// </summary>
        [RubyMethod("ruby2_keywords")]
        public static Proc/*!*/ Ruby2Keywords(RubyContext/*!*/ context, Proc/*!*/ self) {
            var signature = self.Dispatcher.ParameterSignature;
            if (signature == null || !signature.AcceptsRuby2Keywords) {
                context.ReportWarning(
                    "Skipping set of ruby2_keywords flag for proc (proc accepts keywords or post arguments or proc does not accept argument splat)"
                );
                return self;
            }

            // On the dispatcher, which a copy of the proc shares, so that #dup carries the mark
            // in both directions - as MRI's does.
            self.Dispatcher.Ruby2Keywords = true;
            return self;
        }

        #endregion

        #region call, [],  ===, yield (TODO)
        
        // TODO: 1.9 yield: yield and call might have different semantics with respect to control-flow!

        [RubyMethod("==="), RubyMethod("[]"), RubyMethod("yield"), RubyMethod("call")]
        [RubyStackTraceHidden]
        public static object Call(BlockParam block, Proc/*!*/ self) {
            RequireParameterCount(self, 0);
            return self.Call(block != null ? block.Proc : null);
        }

        [RubyMethod("==="), RubyMethod("[]"), RubyMethod("yield"), RubyMethod("call")]
        [RubyStackTraceHidden]
        public static object Call(BlockParam block, Proc/*!*/ self, object arg1) {
            RubyOps.RequireLambdaArity(self, 1, arg1);
            return self.Call(block != null ? block.Proc : null, arg1);
        }

        [RubyMethod("==="), RubyMethod("[]"), RubyMethod("yield"), RubyMethod("call")]
        [RubyStackTraceHidden]
        public static object Call(BlockParam block, Proc/*!*/ self, object arg1, object arg2) {
            RubyOps.RequireLambdaArity(self, 2, arg2);
            return self.Call(block != null ? block.Proc : null, arg1, arg2);
        }

        [RubyMethod("==="), RubyMethod("[]"), RubyMethod("yield"), RubyMethod("call")]
        [RubyStackTraceHidden]
        public static object Call(BlockParam block, Proc/*!*/ self, object arg1, object arg2, object arg3) {
            RubyOps.RequireLambdaArity(self, 3, arg3);
            return self.Call(block != null ? block.Proc : null, arg1, arg2, arg3);
        }

        [RubyMethod("==="), RubyMethod("[]"), RubyMethod("yield"), RubyMethod("call")]
        [RubyStackTraceHidden]
        public static object Call(BlockParam block, Proc/*!*/ self, object arg1, object arg2, object arg3, object arg4) {
            RubyOps.RequireLambdaArity(self, 4, arg4);
            return self.Call(block != null ? block.Proc : null, arg1, arg2, arg3, arg4);
        }

        [RubyMethod("==="), RubyMethod("[]"), RubyMethod("yield"), RubyMethod("call")]
        [RubyStackTraceHidden]
        public static object Call(BlockParam block, Proc/*!*/ self, params object[]/*!*/ args) {
            RubyOps.RequireLambdaArity(self, args.Length, args.Length > 0 ? args[args.Length - 1] : null);
            return self.CallN(block != null ? block.Proc : null, args);
        }

        private static void RequireParameterCount(Proc/*!*/ proc, int argCount) {
            RubyOps.RequireLambdaArity(proc, argCount);
        }

        #endregion

        #region curry, >>, <<

        /// <summary>
        /// Collects arguments until there are enough of them and then calls. A lambda's arity is
        /// binding, so currying one to a different number of arguments is an error; a plain proc's
        /// is not, so any count is allowed and an unbounded arity is read as its minimum.
        /// </summary>
        [RubyMethod("curry")]
        public static Proc/*!*/ Curry(RubyContext/*!*/ context, Proc/*!*/ self) {
            int arity = GetArity(self);
            return MakeCurried(context, self, arity < 0 ? -arity - 1 : arity, null);
        }

        [RubyMethod("curry")]
        public static Proc/*!*/ Curry(RubyContext/*!*/ context, Proc/*!*/ self, [DefaultProtocol]int arity) {
            if (self.Kind == ProcKind.Lambda) {
                int min, max;
                GetArgumentCountRange(self, out min, out max);
                if (arity < min || (max >= 0 && arity > max)) {
                    throw (min == max) ? RubyOps.MakeWrongNumberOfArgumentsError(arity, min)
                        : RubyOps.MakeWrongNumberOfArgumentsErrorN(arity,
                            min.ToString(CultureInfo.InvariantCulture) +
                            (max < 0 ? "+" : ".." + max.ToString(CultureInfo.InvariantCulture)));
                }
            }
            return MakeCurried(context, self, arity, null);
        }

        /// <summary>
        /// How many arguments a call may supply: -1 as the maximum means a rest parameter absorbs
        /// any number of them.
        /// </summary>
        private static void GetArgumentCountRange(Proc/*!*/ self, out int min, out int max) {
            var signature = self.Dispatcher.ParameterSignature;
            if (signature != null) {
                min = signature.MinArgumentCount;
                max = signature.MaxArgumentCount;
                return;
            }

            int arity = self.Dispatcher.Arity;
            if (arity >= 0) {
                min = max = arity;
            } else {
                min = -arity - 1;
                max = -1;
            }
        }

        private static Proc/*!*/ MakeCurried(RubyContext/*!*/ context, Proc/*!*/ target, int arity, RubyArray collected) {
            return Proc.CreateNative(context, target.Kind, (blockParam, self, args, unsplat, procArg) => {
                var all = collected != null ? new RubyArray(collected) : new RubyArray();
                all.AddRange(unsplat);
                if (all.Count >= arity) {
                    // a lambda still objects to being handed more arguments than it takes
                    RequireParameterCount(target, all.Count);
                    return target.Call(procArg, all.ToArray());
                }
                return MakeCurried(context, target, arity, all);
            });
        }

        /// <summary>
        /// (f &gt;&gt; g).(x) is g(f(x)) and (f &lt;&lt; g).(x) is f(g(x)). The composition is a lambda when
        /// the function it applies first is one, since that is the one that sees the arguments.
        /// </summary>
        [RubyMethod(">>")]
        public static Proc/*!*/ ComposeForward(RespondToStorage/*!*/ respondTo,
            CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ callStorage, Proc/*!*/ self, object other) {

            RequireCallable(respondTo, other);
            return Proc.CreateNative(respondTo.Context, self.Kind, (blockParam, s, args, unsplat, procArg) => {
                object intermediate = self.Call(procArg, unsplat.ToArray());
                var site = callStorage.GetCallSite("call", 1);
                return site.Target(site, other, intermediate);
            });
        }

        [RubyMethod("<<")]
        public static Proc/*!*/ ComposeBackward(RespondToStorage/*!*/ respondTo,
            CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ callStorage,
            CallSiteStorage<Func<CallSite, object, Proc, RubyArray, object>>/*!*/ splatStorage, Proc/*!*/ self, object other) {

            RequireCallable(respondTo, other);
            var kind = (other as Proc)?.Kind ?? ProcKind.Lambda;
            return Proc.CreateNative(respondTo.Context, kind, (blockParam, s, args, unsplat, procArg) => {
                // the block goes to whichever function is applied first, which for << is `other'
                var splatSite = splatStorage.GetCallSite("call",
                    new RubyCallSignature(0, RubyCallFlags.HasSplattedArgument | RubyCallFlags.HasBlock));
                object intermediate = splatSite.Target(splatSite, other, procArg, unsplat);
                // through #call, so a lambda's arity is checked against the one value it gets
                var site = callStorage.GetCallSite("call", 1);
                return site.Target(site, self, intermediate);
            });
        }

        private static void RequireCallable(RespondToStorage/*!*/ respondTo, object other) {
            if (!Protocols.RespondTo(respondTo, other, "call")) {
                throw RubyExceptions.CreateTypeError("callable object is expected");
            }
        }

        #endregion

        #region new

        /// <summary>
        /// Proc.new needs a block of its own: taking the enclosing method's block instead was
        /// removed in Ruby 3.0, so a Proc.new without one is an ArgumentError even inside a method
        /// that was given a block.
        ///
        /// The block is handed straight back when it is already an instance of the class being
        /// asked for; otherwise it is wrapped in one, and that wrapper's #initialize gets whatever
        /// arguments Proc.new was given.
        /// </summary>
        [RubyMethod("new", RubyMethodAttributes.PublicSingleton)]
        public static object CreateNew(CallSiteStorage<Func<CallSite, object, Proc, RubyArray, object>>/*!*/ storage, 
            BlockParam block, RubyClass/*!*/ self, params object[]/*!*/ args) {

            if (block == null) {
                throw RubyExceptions.CreateArgumentError("tried to create Proc object without a block");
            }

            var proc = block.Proc;
            if (ReferenceEquals(self.Context.GetClassOf(proc), self)) {
                // Proc itself takes no arguments; a subclass hands them to its #initialize
                if (args.Length > 0) {
                    throw RubyExceptions.CreateArgumentError("wrong number of arguments (given {0}, expected 0)", args.Length);
                }
                return proc;
            }

            // an instance of a Proc subclass:
            var result = new Proc.Subclass(self, proc);

            // propagate retry and return control flow:
            var initialize = storage.GetCallSite("initialize",
                new RubyCallSignature(0, RubyCallFlags.HasImplicitSelf | RubyCallFlags.HasSplattedArgument | RubyCallFlags.HasBlock));
            object initResult = initialize.Target(initialize, result, block.Proc, RubyOps.MakeArrayN(args));
            if (initResult is BlockReturnResult) {
                return initResult;
            }

            return result;
        }

        #endregion
    }
}

