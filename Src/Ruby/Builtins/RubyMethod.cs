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

using System.Linq.Expressions;

using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using IronRuby.Compiler;
using Microsoft.Scripting.Utils;
using Microsoft.Scripting.Generation;
using Microsoft.Scripting.Runtime;
using AstUtils = Microsoft.Scripting.Ast.Utils;

namespace IronRuby.Builtins {
    using Ast = Expression;
    using BlockCallTargetUnsplatProcN = Func<BlockParam, object, object[], RubyArray, Proc, object>;

    [DebuggerDisplay("{GetDebugView(), nq}")]
    public partial class RubyMethod : IDuplicable {
        private readonly object _target;
        private readonly string/*!*/ _name;
        private readonly RubyMemberInfo/*!*/ _info;
        private BlockDispatcherUnsplatProcN _procDispatcher;

        /// <summary>
        /// Set for a method that stands in for one the object only answers through
        /// #method_missing: it reports the name that was asked for, and calls #method_missing
        /// with that name pushed in front of the arguments. This used to be a subclass, which
        /// gave it a Ruby class of its own - MRI hands back a plain Method, and ruby/spec checks
        /// it with #instance_of?.
        /// </summary>
        private readonly string _methodMissingName;

        public bool IsMethodMissing {
            get { return _methodMissingName != null; }
        }

        public object Target {
            get { return _target; }
        }

        public RubyMemberInfo/*!*/ Info {
            get { return _info; }
        }

        public string/*!*/ Name {
            get { return _name; } 
        }

        public RubyMethod(object target, RubyMemberInfo/*!*/ info, string/*!*/ name) {
            ContractUtils.RequiresNotNull(info, "info");
            ContractUtils.RequiresNotNull(name, "name");
                        
            _target = target;
            _info = info;
            _name = name;
        }

        private RubyMethod(object target, RubyMemberInfo/*!*/ info, string/*!*/ name, string methodMissingName)
            : this(target, info, name) {
            _methodMissingName = methodMissingName;
        }

        /// <summary>
        /// A Method standing in for a name the object only answers through #method_missing.
        /// </summary>
        public static RubyMethod/*!*/ CreateMethodMissing(object target, RubyMemberInfo/*!*/ info, string/*!*/ name) {
            return new RubyMethod(target, info, name, name);
        }

        object IDuplicable.Duplicate(RubyContext/*!*/ context, bool copySingletonMembers) {
            var result = new RubyMethod(_target, _info, _name, _methodMissingName);
            context.CopyInstanceData(this, result, copySingletonMembers);
            return result;
        }

        public RubyClass/*!*/ GetTargetClass() {
            return _info.Context.GetClassOf(_target);
        }

        public virtual Proc/*!*/ ToProc(RubyScope/*!*/ scope) {
            ContractUtils.RequiresNotNull(scope, "scope");

            if (_procDispatcher == null) {
                // the block the proc is called with goes on to the method it wraps, so that
                // `x.method(:foo).to_proc.call { ... }` reaches a `yield` inside #foo
                // HasScope, and the scope taken from the proc being called: a method that reads
                // the caller's scope - attr_reader and the other definers read the visibility
                // from it - would otherwise see a default one and define private methods, so
                // `%i(a b).each(&method(:attr_reader))` produced private readers where MRI's
                // are public.  Reading it off the proc rather than closing over the scope this
                // #to_proc ran under keeps the dispatcher below cacheable.
                var site = CallSite<Func<CallSite, RubyScope, object, Proc, object, object>>.Create(
                    // TODO: use InvokeBinder
                    RubyCallAction.Make(
                        scope.RubyContext, "call",
                        new RubyCallSignature(1, RubyCallFlags.HasScope | RubyCallFlags.HasImplicitSelf |
                            RubyCallFlags.HasSplattedArgument | RubyCallFlags.HasBlock)
                    )
                );

                var block = new BlockCallTargetUnsplatProcN((blockParam, self, args, unsplat, procArg) => {
                    // block takes no parameters but unsplat => all actual arguments are added to unsplat:
                    Debug.Assert(args.Length == 0);

                    return site.Target(site, blockParam.Proc.LocalScope, this, procArg, unsplat);
                });

                // MRI's proc reports the location of the method it wraps, not of the to_proc call
                string sourcePath = null;
                int sourceLine = 0;
                var rubyInfo = _info as RubyMethodInfo;
                if (rubyInfo != null) {
                    sourcePath = rubyInfo.Document.FileName;
                    sourceLine = rubyInfo.SourceSpan.Start.Line;
                } else {
                    var lambdaInfo = _info as RubyLambdaMethodInfo;
                    if (lambdaInfo != null) {
                        sourcePath = lambdaInfo.Lambda.Dispatcher.SourcePath;
                        sourceLine = lambdaInfo.Lambda.Dispatcher.SourceLine;
                    }
                }

                _procDispatcher = new BlockDispatcherUnsplatProcN(0, 
                    BlockDispatcher.MakeAttributes(
                        BlockSignatureAttributes.HasUnsplatParameter | BlockSignatureAttributes.HasProcParameter,
                        _info.GetArity()
                    ),
                    sourcePath, sourceLine
                );

                _procDispatcher.SetMethod(block);
                _procDispatcher.ParameterSignature = _info.GetParameterSignature();
                _procDispatcher.WrapsMethod = true;
            }

            // A method binds its arguments strictly and `return` from it returns from the
            // method, both of which are lambda behaviour, so MRI's Method#to_proc answers
            // #lambda? with true. Its self is the method's receiver rather than whatever self
            // the to_proc call happened under, which is what #binding hands back.
            scope.MarkCaptured();
            return new Proc(ProcKind.Lambda, _target, scope, _procDispatcher);
        }

        #region Dynamic Operations

        internal void BuildInvoke(MetaObjectBuilder/*!*/ metaBuilder, CallArguments/*!*/ args) {
            Assert.NotNull(metaBuilder, args);
            Debug.Assert(args.Target == this);

            // A call to these methods depends on the method, the name it is called by and the arguments (self included), not on
            // which Method object holds them. Each `instance_method(:m).bind(obj).call` makes a new Method, so a rule that took 
            // the Method and its receiver as constants was bound and compiled anew for every call of such a site.
            if (_methodMissingName == null && IsReceiverIndependent(_info)) {
                var method = Ast.Convert(args.TargetExpression, typeof(RubyMethod));
                var receiver = Ast.Property(method, TargetProperty);

                // The receiver's CLR type is restricted here: the overload resolver takes self for granted.
                metaBuilder.AddRestriction(Ast.AndAlso(
                    Ast.TypeIs(args.TargetExpression, typeof(RubyMethod)),
                    Ast.AndAlso(
                        Ast.AndAlso(
                            Ast.Equal(Ast.Property(method, InfoProperty), AstUtils.Constant(_info, typeof(RubyMemberInfo))),
                            Ast.Equal(Ast.Property(method, NameProperty), AstUtils.Constant(_name))
                        ),
                        Ast.AndAlso(
                            Ast.Not(Ast.Property(method, IsMethodMissingProperty)),
                            (_target == null) ? (Expression)Ast.Equal(receiver, AstUtils.Constant(null)) : Ast.TypeEqual(receiver, _target.GetType())
                        )
                    )
                ));

                args.SetTarget(receiver, _target);
                _info.BuildCall(metaBuilder, args, _name);
                return;
            }

            // first argument must be this method:
            metaBuilder.AddRestriction(Ast.Equal(args.TargetExpression, AstUtils.Constant(this)));

            // set the target (becomes self in the called method):
            args.SetTarget(AstUtils.Constant(_target, CompilerHelpers.GetVisibleType(_target)), _target);

            if (_methodMissingName != null) {
                // MRI looks method_missing up on the receiver when the Method is called, not when
                // it was made, so one defined (or stubbed) in the meantime is the one that runs
                RubyMemberInfo methodMissing;
                var targetClass = args.TargetClass;
                using (targetClass.Context.ClassHierarchyLocker()) {
                    metaBuilder.AddTargetTypeTest(args.Target, targetClass, args.TargetExpression, args.MetaContext,
                        new[] { Symbols.MethodMissing }
                    );
                    methodMissing = targetClass.ResolveMethodForSiteNoLock(Symbols.MethodMissing, VisibilityContext.AllVisible).Info ?? _info;
                }

                args.InsertMethodName(_methodMissingName);
                methodMissing.BuildCall(metaBuilder, args, Symbols.MethodMissing);
            } else {
                _info.BuildCall(metaBuilder, args, _name);
            }
        }

        private static bool IsReceiverIndependent(RubyMemberInfo/*!*/ info) {
            Type type = info.GetType();
            return type == typeof(RubyLibraryMethodInfo) || type == typeof(RubyMethodInfo) || type == typeof(RubyLambdaMethodInfo);
        }

        private static readonly System.Reflection.PropertyInfo/*!*/ InfoProperty = typeof(RubyMethod).GetProperty("Info");
        private static readonly System.Reflection.PropertyInfo/*!*/ NameProperty = typeof(RubyMethod).GetProperty("Name");
        private static readonly System.Reflection.PropertyInfo/*!*/ TargetProperty = typeof(RubyMethod).GetProperty("Target");
        private static readonly System.Reflection.PropertyInfo/*!*/ IsMethodMissingProperty = typeof(RubyMethod).GetProperty("IsMethodMissing");

        #endregion

        #region Debug View

        private string/*!*/ GetDebugView() {
            var result = new StringBuilder();
            result.Append(_info.Visibility.ToString().ToLowerInvariant());
            result.Append(' ');

            result.Append(GetTargetClass().Name);
            result.Append('#');
            result.Append(_name);

            // TODO: parameter names?
            result.Append((_methodMissingName != null) ? "(?) via method_missing" : "()");
            return result.ToString();            
        }

        #endregion
    }
}
