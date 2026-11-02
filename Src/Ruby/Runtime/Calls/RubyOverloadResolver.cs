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
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Dynamic;
using System.Reflection;
using System.Text;
using IronRuby.Builtins;
using IronRuby.Compiler;
using IronRuby.Runtime.Conversions;
using Microsoft.Scripting.Actions.Calls;
using Microsoft.Scripting.Generation;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using AstUtils = Microsoft.Scripting.Ast.Utils;

namespace IronRuby.Runtime.Calls {
    using Ast = Expression;

    public sealed class RubyOverloadResolver : OverloadResolver {
        private readonly CallArguments/*!*/ _args;
        private readonly MetaObjectBuilder/*!*/ _metaBuilder;
        private readonly SelfCallConvention _callConvention;

        //
        // We want to perform Ruby protocol conversions when binding to CLR methods. 
        // However, some Ruby libraries don't allow protocol conversions on their parameters. 
        // Hence in libraries protocol conversions should only be performed when DefaultProtocol attribute is present.
        // This flag is set if protocol conversions should be applied on all parameters regardless of DefaultProtocol attribute.
        // This also implies a different narrowing level of the protocol conversions for library methods and other CLR methods 
        // (see Converter.CanConvertFrom).
        //
        private readonly bool _implicitProtocolConversions;

        private int _firstRestrictedArg;
        private int _lastSplattedArg;
        private ParameterExpression _listVariable;
        private IList _list;

        // An optional list of assumptions upon arguments that were made during resolution.
        private List<Key<int, NarrowingLevel, Expression>> _argumentAssumptions;

        // Keywords of a call to a CLR method that bind by name: to its parameters, and for a constructor
        // also to the properties and fields of the new object. Null when the call passes no keywords or
        // they are passed on as a positional Hash. _keywordNames holds the CLR name each keyword binds to.
        private RubySymbol[] _keywordSymbols;
        private string[] _keywordNames;

        // The keywords are the last item of the splatted list: f(**opts), f(*args, **opts).
        private bool _keywordsSplatted;

        // The call's block goes to the method as its last positional argument, a parameter of a delegate type.
        private bool _blockAsArgument;

        internal RubyContext/*!*/ Context {
            get { return _args.RubyContext; }
        }

        internal Expression/*!*/ ScopeExpression {
            get { return _args.MetaScope.Expression; }
        }

        internal Expression/*!*/ ContextExpression {
            get { return _args.MetaContext.Expression; }
        }

        internal RubyOverloadResolver(MetaObjectBuilder/*!*/ metaBuilder, CallArguments/*!*/ args, SelfCallConvention callConvention,
            bool implicitProtocolConversions)
            : base(args.RubyContext.Binder) {
            _args = args;
            _metaBuilder = metaBuilder;
            _callConvention = callConvention;
            _implicitProtocolConversions = implicitProtocolConversions;
        }

        #region Step 1: Special Parameters

        protected override bool AllowMemberInitialization(OverloadInfo method) {
            // Widget.new(title: "x", width: 3) sets what no constructor parameter takes, like a C# object initializer.
            return _keywordSymbols != null && (method.IsConstructor || IsDefaultValueFactory(method));
        }

        private static bool IsDefaultValueFactory(OverloadInfo/*!*/ method) {
            var info = method.ReflectionInfo as MethodInfo;
            return info != null && info.IsGenericMethod && info.GetGenericMethodDefinition() == RubyOps.CreateDefaultValueMethod;
        }

        /// <summary>
        /// We expand params arrays for library methods. Splat operator needs to be used to pass content of an array/list into params array method.
        /// </summary>
        protected override bool BindToUnexpandedParams(MethodCandidate/*!*/ candidate) {
            // TODO: separate flag?
            return _implicitProtocolConversions;
        }

        protected override BitArray MapSpecialParameters(ParameterMapping/*!*/ mapping) {
            var method = mapping.Overload;
            var infos = method.Parameters;
            var special = new BitArray(infos.Count);

            // Method signatures                                                                                  SelfCallConvention
            // RubyMethod/RubyCtor:   [(CallSiteStorage)*, (RubyContext|RubyScope)?, (BlockParam)?, self, args]  SelfIsParameter
            // static:                [(CallSiteStorage)*, (RubyContext|RubyScope)?, (BlockParam)?, args]        NoSelf
            // instance/extension/op: [self, (CallSiteStorage)*, (RubyContext|RubyScope)?, (BlockParam)?, args]  SelfIsInstace

            var i = 0;

            if (_callConvention == SelfCallConvention.SelfIsInstance) {
                if (method.IsStatic) {
                    Debug.Assert(RubyUtils.IsOperator(method) || method.IsExtension);

                    // receiver maps to the first parameter:
                    AddSimpleHiddenMapping(mapping, infos[i], true);
                    special[i++] = true;
                } else {
                    // receiver maps to the instance (no parameter info represents it):
                    mapping.AddParameter(new ParameterWrapper(null, method.DeclaringType, null, ParameterBindingFlags.ProhibitNull | ParameterBindingFlags.IsHidden));
                    mapping.AddInstanceBuilder(new InstanceBuilder(mapping.ArgIndex));
                }
            } else if (_callConvention == SelfCallConvention.NoSelf) {
                // instance methods on Object can be called with arbitrary receiver object including classes (static call):
                if (!method.IsStatic && method.DeclaringType == typeof(Object)) {
                    // insert an InstanceBuilder that doesn't consume any arguments, only inserts the target expression as instance:
                    mapping.AddInstanceBuilder(new ImplicitInstanceBuilder());
                }
            }

            while (i < infos.Count && infos[i].ParameterType.IsSubclassOf(typeof(RubyCallSiteStorage))) {
                mapping.AddBuilder(new RubyCallSiteStorageBuilder(infos[i]));
                special[i++] = true;
            }

            if (i < infos.Count) {
                var info = infos[i];

                if (info.ParameterType == typeof(RubyScope)) {
                    mapping.AddBuilder(new RubyScopeArgBuilder(info));
                    special[i++] = true;
                } else if (info.ParameterType == typeof(RubyContext)) {
                    mapping.AddBuilder(new RubyContextArgBuilder(info));
                    special[i++] = true;
                } else if (method.IsConstructor && info.ParameterType == typeof(RubyClass)) {
                    mapping.AddBuilder(new RubyClassCtorArgBuilder(info));
                    special[i++] = true;
                }
            }

            // If the method overload doesn't have a BlockParam parameter, we inject MissingBlockParam parameter and arg builder.
            // The parameter is treated as a regular explicit mandatory parameter.
            //
            // The argument builder provides no value for the actual argument expression, which makes the default binder to skip it
            // when emitting a tree for the actual method call (this is necessary since the method doesn't in fact have the parameter).
            // 
            // By injecting the missing block parameter we achieve that all overloads have either BlockParam, [NotNull]BlockParam or 
            // MissingBlockParam parameter. MissingBlockParam and BlockParam are convertible to each other. Default binder prefers 
            // those overloads where no conversion needs to happen, which ensures the desired semantics:
            //
            //                                        conversions with desired priority (the less number the higher priority)
            // Parameters:                call w/o block      call with non-null block       call with null block
            // (implicit, MBP, ... )      MBP -> MBP (1)            BP -> MBP (3)               BP -> MBP (2)
            // (implicit, BP,  ... )      MBP -> BP  (2)            BP -> BP  (2)               BP -> BP  (1)
            // (implicit, BP!, ... )          N/A                   BP -> BP! (1)                  N/A    
            //
            if (i < infos.Count && infos[i].ParameterType == typeof(BlockParam)) {
                AddSimpleHiddenMapping(mapping, infos[i], mapping.Overload.ProhibitsNull(i));
                special[i++] = true;
            } else if (i >= infos.Count || infos[i].ParameterType != typeof(BlockParam)) {
                mapping.AddBuilder(new MissingBlockArgBuilder(mapping.ArgIndex));
                mapping.AddParameter(new ParameterWrapper(null, typeof(MissingBlockParam), null, ParameterBindingFlags.IsHidden));
            }

            if (_callConvention == SelfCallConvention.SelfIsParameter) {
                // Ruby library methods only:
                Debug.Assert(method.IsStatic);
                Debug.Assert(i < infos.Count);

                // receiver maps to the first visible parameter:
                AddSimpleHiddenMapping(mapping, infos[i], mapping.Overload.ProhibitsNull(i));
                special[i++] = true;
            }

            return special;
        }

        private void AddSimpleHiddenMapping(ParameterMapping mapping, ParameterInfo info, bool prohibitNull) {
            mapping.AddBuilder(new SimpleArgBuilder(info, info.ParameterType, mapping.ArgIndex, false, false));
            mapping.AddParameter(new ParameterWrapper(info, info.ParameterType, null, 
                ParameterBindingFlags.IsHidden | (prohibitNull ? ParameterBindingFlags.ProhibitNull : 0)
            ));
        }

        internal static int GetHiddenParameterCount(OverloadInfo/*!*/ method, SelfCallConvention callConvention) {
            int i = 0;
            var infos = method.Parameters;

            if (callConvention == SelfCallConvention.SelfIsInstance) {
                if (method.IsStatic) {
                    Debug.Assert(RubyUtils.IsOperator(method) || method.IsExtension);
                    i++;
                }
            }

            while (i < infos.Count && infos[i].ParameterType.IsSubclassOf(typeof(RubyCallSiteStorage))) {
                i++;
            }

            if (i < infos.Count) {
                var info = infos[i];

                if (info.ParameterType == typeof(RubyScope)) {
                    i++;
                } else if (info.ParameterType == typeof(RubyContext)) {
                    i++;
                } else if (method.IsConstructor && info.ParameterType == typeof(RubyClass)) {
                    i++;
                }
            }

            if (i < infos.Count && infos[i].ParameterType == typeof(BlockParam)) {
                i++;
            }

            if (callConvention == SelfCallConvention.SelfIsParameter) {
                Debug.Assert(i < infos.Count);
                Debug.Assert(method.IsStatic);
                i++;
            }

            return i;
        }

        internal static void GetParameterCount(OverloadInfo/*!*/ method, SelfCallConvention callConvention, out int mandatory, out int optional) {
            mandatory = 0;
            optional = 0;
            for (int i = GetHiddenParameterCount(method, callConvention); i < method.ParameterCount; i++) {
                var info = method.Parameters[i];

                if (method.IsParamArray(i)) {
                    // TODO: indicate splat args separately?
                    optional++;
                } else if (info.IsOutParameter()) {
                    // Python allows passing of optional "clr.Reference" to capture out parameters
                    // Ruby should allow similar
                    optional++;
                } else if (info.IsMandatory()) {
                    mandatory++;
                } else {
                    optional++;
                }
            }
        }

        #endregion

        #region Keywords and Blocks of CLR Method Calls

        /// <summary>
        /// The keywords a call passes, if they could bind to a CLR method by name: a keyword Hash with
        /// only Symbol keys, the last simple argument or the last item of the splatted list (f(**opts)).
        /// Null otherwise.
        /// </summary>
        internal static Hash GetKeywordArguments(CallArguments/*!*/ args) {
            if (args.Signature.HasRhsArgument) {
                return null;
            }

            Hash keywords;
            if (args.Signature.HasSplattedArgument) {
                var list = args.GetSplattedArgument();
                keywords = (list.Count > 0) ? list[list.Count - 1] as Hash : null;
                // a hash a ruby2_keywords method carries is turned back into keywords when splatted:
                if (keywords == null || !keywords.IsKeywordArguments && !keywords.IsRuby2KeywordsHash) {
                    return null;
                }
            } else {
                int count = args.SimpleArgumentCount;
                keywords = (count > 0) ? args.GetSimpleArgument(count - 1) as Hash : null;
                if (keywords == null || !keywords.IsKeywordArguments) {
                    return null;
                }
            }

            if (keywords.Count == 0) {
                return null;
            }

            foreach (var entry in keywords) {
                if (!(entry.Key is RubySymbol)) {
                    return null;
                }
            }
            return keywords;
        }

        /// <summary>
        /// The expression of the keyword Hash <see cref="GetKeywordArguments"/> found. A splatted one
        /// is read from the list the splat test has stored, so it may only be evaluated after that test.
        /// </summary>
        private Expression/*!*/ GetKeywordArgumentsExpression() {
            if (_args.Signature.HasSplattedArgument) {
                Debug.Assert(_listVariable != null);
                return Ast.Call(_listVariable, typeof(IList).GetMethod("get_Item"), AstUtils.Constant(_list.Count - 1));
            }
            return AstUtils.Box(_args.GetSimpleArgumentExpression(_args.SimpleArgumentCount - 1));
        }

        /// <summary>
        /// Makes the keywords of the call bind by name, to parameters of <paramref name="overloads"/> or,
        /// for a constructor, to writable properties and fields of the new object. A keyword names its
        /// parameter or member either exactly or in the snake_case form IronRuby gives CLR names
        /// (first_name: binds to firstName or FirstName).
        /// </summary>
        internal void BindKeywordsByName(Hash/*!*/ keywords, IList<OverloadInfo>/*!*/ overloads) {
            _keywordsSplatted = _args.Signature.HasSplattedArgument;
            var symbols = new RubySymbol[keywords.Count];
            var names = new string[symbols.Length];
            int i = 0;
            foreach (var entry in keywords) {
                var symbol = (RubySymbol)entry.Key;
                symbols[i] = symbol;
                names[i] = GetKeywordTarget(symbol.ToString(), overloads);
                i++;
            }
            _keywordSymbols = symbols;
            _keywordNames = names;
        }

        internal bool BindsKeywordsByName {
            get { return _keywordSymbols != null; }
        }

        /// <summary>
        /// Adds the rule test for a call that passes <paramref name="keywords"/>: it passes exactly these
        /// keywords, in any order. How keywords bind to a CLR method depends on their names. Done on the
        /// resolver whose binding the rule uses, after overload resolution.
        /// </summary>
        internal void AddKeywordArgumentsTest(MetaObjectBuilder/*!*/ metaBuilder, Hash/*!*/ keywords) {
            var symbols = new RubySymbol[keywords.Count];
            int i = 0;
            foreach (var entry in keywords) {
                symbols[i++] = (RubySymbol)entry.Key;
            }

            var expression = GetKeywordArgumentsExpression();
            var test = Ast.AndAlso(
                Methods.IsKeywordArgumentsHash.OpCall(expression),
                Methods.KeywordArgumentsMatch.OpCall(expression, AstUtils.Constant(symbols),
                    AstUtils.Constant(symbols.Length), AstUtils.Constant(false))
            );

            if (_args.Signature.HasSplattedArgument) {
                // after the splat test, which stores the list and checks its length:
                metaBuilder.AddCondition(test);
            } else {
                metaBuilder.AddRestriction(test);
            }
        }

        private static string/*!*/ GetKeywordTarget(string/*!*/ keyword, IList<OverloadInfo>/*!*/ overloads) {
            string mangledMatch = null;
            foreach (var overload in overloads) {
                foreach (var parameter in overload.Parameters) {
                    string name = parameter.Name;
                    if (name == null || IsInfrastructureType(parameter.ParameterType)) {
                        continue;
                    }
                    if (name == keyword) {
                        return name;
                    }
                    if (mangledMatch == null && RubyUtils.TryMangleName(name) == keyword) {
                        mangledMatch = name;
                    }
                }
            }

            if (mangledMatch != null) {
                return mangledMatch;
            }

            foreach (var overload in overloads) {
                Type initialized = overload.IsConstructor ? overload.DeclaringType : IsDefaultValueFactory(overload) ? overload.ReturnType : null;
                if (initialized == null) {
                    continue;
                }
                foreach (var member in initialized.GetMembers(BindingFlags.Public | BindingFlags.Instance)) {
                    if ((member is PropertyInfo || member is FieldInfo) &&
                        (member.Name == keyword || RubyUtils.TryMangleName(member.Name) == keyword)) {
                        return member.Name;
                    }
                }
            }

            // binds to nothing; overload resolution reports it
            return keyword;
        }

        private static bool IsInfrastructureType(Type/*!*/ type) {
            return type == typeof(RubyClass) || type == typeof(RubyContext) || type == typeof(RubyScope) || type == typeof(BlockParam)
                || type.IsSubclassOf(typeof(RubyCallSiteStorage));
        }

        protected override void GetNamedArguments(out IList<DynamicMetaObject> namedArgs, out IList<string> argNames) {
            if (_keywordSymbols == null) {
                base.GetNamedArguments(out namedArgs, out argNames);
                return;
            }

            Hash hash;
            if (_keywordsSplatted) {
                // The splat test goes first so the keywords can be read from the list it stores:
                var splatted = _args.GetSplattedMetaArgument();
                int listLength;
                _list = (IList)splatted.Value;
                _metaBuilder.AddSplattedArgumentTest(_list, splatted.Expression, out listLength, out _listVariable);
                hash = (Hash)_list[listLength - 1];
            } else {
                hash = (Hash)_args.GetSimpleArgument(_args.SimpleArgumentCount - 1);
            }

            var keywords = GetKeywordArgumentsExpression();
            var result = new DynamicMetaObject[_keywordSymbols.Length];
            for (int i = 0; i < result.Length; i++) {
                object value;
                hash.TryGetValue(_keywordSymbols[i], out value);
                result[i] = DynamicMetaObject.Create(value, Methods.GetKeywordArgument.OpCall(
                    keywords, AstUtils.Constant(_keywordSymbols[i]), AstUtils.Constant(i)
                ));
            }

            namedArgs = result;
            argNames = _keywordNames;
        }

        /// <summary>
        /// A CLR method called with a block takes it as its last argument if that is a delegate,
        /// List#ForEach { |x| ... } as List#ForEach(Action), the way JRuby passes a block to a Java
        /// method's trailing functional interface. Only for a block literal or a non-nil &amp;proc;
        /// whether an overload takes it is up to overload resolution.
        /// </summary>
        internal static bool CanPassBlockAsArgument(CallArguments/*!*/ args) {
            return args.Signature.HasBlock && args.GetBlock() != null && !args.Signature.HasRhsArgument;
        }

        internal void PassBlockAsArgument() {
            _blockAsArgument = true;
        }

        internal bool PassesBlockAsArgument {
            get { return _blockAsArgument; }
        }

        #endregion

        #region Step 2: Actual Arguments

        private static readonly DynamicMetaObject NullMetaBlockParam =
            new DynamicMetaObject(
                AstUtils.Constant(null, typeof(BlockParam)),
                BindingRestrictions.Empty,
                null
            );

        // Creates actual/normalized arguments: inserts self, expands splats, and inserts rhs arg. 
        // Adds any restrictions/conditions applied to the arguments to the given meta-builder.
        protected override ActualArguments CreateActualArguments(IList<DynamicMetaObject> namedArgs, IList<string> argNames, int preSplatLimit, int postSplatLimit) {
            var result = new List<DynamicMetaObject>();

            // self (instance):
            if (_callConvention == SelfCallConvention.SelfIsInstance) {
                result.Add(_args.MetaTarget);
            }

            if (_args.Signature.HasBlock && !_blockAsArgument) {
                if (_args.GetBlock() == null) {
                    // the user explicitly passed nil as a block arg:
                    result.Add(NullMetaBlockParam);
                } else {
                    // pass BlockParam:
                    if (_metaBuilder.BfcVariable == null) {
                        // we add temporary even though we might not us it if the calee doesn't have block param arg:
                        _metaBuilder.BfcVariable = _metaBuilder.GetTemporary(typeof(BlockParam), "#bfc");
                    }
                    result.Add(new DynamicMetaObject(_metaBuilder.BfcVariable, BindingRestrictions.Empty));
                }
            } else {
                // no block passed into a method with a BlockParam:
                result.Add(MissingBlockParam.Meta.Instance);
            }

            // self (parameter):
            if (_callConvention == SelfCallConvention.SelfIsParameter) {
                result.Add(_args.MetaTarget);
            }

            // the next argument is the first one for which we use restrictions coming from overload resolution:
            _firstRestrictedArg = result.Count;

            // hidden args: block, self
            int hidden = _callConvention == SelfCallConvention.NoSelf ? 1 : 2;
            return CreateActualArguments(result, _metaBuilder, _args, hidden, preSplatLimit, postSplatLimit, out _lastSplattedArg, out _list, out _listVariable,
                namedArgs, argNames, _keywordSymbols != null ? 1 : 0, _blockAsArgument ? _args.GetMetaBlock() : null,
                _keywordsSplatted ? _listVariable : null);
        }

        public static IList<DynamicMetaObject/*!*/> NormalizeArguments(MetaObjectBuilder/*!*/ metaBuilder, CallArguments/*!*/ args, int minCount, int maxCount) {
            int lastSplattedArg;
            IList list;
            ParameterExpression listVariable;

            // 2 hidden arguments: block and self
            var actualArgs = CreateActualArguments(new List<DynamicMetaObject>(), metaBuilder, args, 2, maxCount, maxCount,
                out lastSplattedArg, out list, out listVariable);

            int actualCount = actualArgs.Count + actualArgs.CollapsedCount;

            if (actualCount < minCount) {
                metaBuilder.SetWrongNumberOfArgumentsError(actualCount, minCount);
                return null;
            } else if (actualCount > maxCount) {
                metaBuilder.SetWrongNumberOfArgumentsError(actualCount, maxCount);
                return null;
            }

            // any collapsed args are out of limits:
            return actualArgs.Arguments;
        }

        private static ActualArguments/*!*/ CreateActualArguments(List<DynamicMetaObject>/*!*/ normalized, MetaObjectBuilder/*!*/ metaBuilder,
            CallArguments/*!*/ args, int hidden, int preSplatLimit, int postSplatLimit, out int lastSplattedArg, out IList list, out ParameterExpression listVariable,
            IList<DynamicMetaObject> namedArgs = null, IList<string> argNames = null, int keywordArgumentCount = 0, DynamicMetaObject blockArgument = null,
            ParameterExpression splatTestedListVariable = null) {

            int firstSplattedArg, splatIndex, collapsedArgCount;

            // Simple arguments. A keyword hash that binds by name is not an argument: neither the last
            // simple argument nor, if there is a splat, the last item of the splatted list.
            int simpleCount = args.SimpleArgumentCount - (args.Signature.HasSplattedArgument ? 0 : keywordArgumentCount);
            for (int i = 0; i < simpleCount; i++) {
                normalized.Add(args.GetSimpleMetaArgument(i));
            }

            // splat argument:
            list = null;
            listVariable = null;
            if (args.Signature.HasSplattedArgument) {
                firstSplattedArg = normalized.Count;

                int listLength;
                var splatted = args.GetSplattedMetaArgument();
                list = (IList)splatted.Value;
                if (splatTestedListVariable != null) {
                    listVariable = splatTestedListVariable;
                    listLength = list.Count;
                } else {
                    metaBuilder.AddSplattedArgumentTest(list, splatted.Expression, out listLength, out listVariable);
                }
                listLength -= keywordArgumentCount;

                int i = 0;
                while (i < Math.Min(listLength, preSplatLimit - firstSplattedArg)) {
                    normalized.Add(MakeSplattedItem(list, listVariable, i));
                    i++;
                }

                // skip items that are not needed for overload resolution
                splatIndex = normalized.Count;

                // the rhs and a block passed as an argument follow the splat:
                i = Math.Max(i, listLength - (postSplatLimit - (args.Signature.HasRhsArgument ? 1 : 0) - (blockArgument != null ? 1 : 0)));
                while (i < listLength) {
                    normalized.Add(MakeSplattedItem(list, listVariable, i));
                    i++;
                }

                collapsedArgCount = listLength - (normalized.Count - firstSplattedArg);
                lastSplattedArg = normalized.Count - 1;
            } else {
                splatIndex = firstSplattedArg = lastSplattedArg = -1;
                collapsedArgCount = 0;
            }

            Debug.Assert(collapsedArgCount >= 0);

            // rhs argument:
            if (args.Signature.HasRhsArgument) {
                normalized.Add(args.GetRhsMetaArgument());
            }

            // the block, passed as the last positional argument (there is no rhs then):
            if (blockArgument != null) {
                normalized.Add(blockArgument);
            }

            return new ActualArguments(
                normalized.ToArray(),
                namedArgs ?? DynamicMetaObject.EmptyMetaObjects,
                argNames ?? ArrayUtils.EmptyStrings,
                hidden,
                collapsedArgCount,
                firstSplattedArg,
                splatIndex
            );
        }

        internal static DynamicMetaObject/*!*/ MakeSplattedItem(IList/*!*/ list, Expression/*!*/ listVariable, int index) {
            return DynamicMetaObject.Create(
                list[index],
                Ast.Call(listVariable, typeof(IList).GetMethod("get_Item"), AstUtils.Constant(index))
            );
        }

        #endregion

        #region Step 3: Restrictions

        internal void AddArgumentRestrictions(MetaObjectBuilder/*!*/ metaBuilder, BindingTarget/*!*/ bindingTarget) {
            var args = GetActualArguments();
            // (keywords bound by name are arguments too; the error rule of a failed binding has to test them as well)
            var restrictedArgs = bindingTarget.Success ? bindingTarget.RestrictedArguments.GetObjects() :
                (args.NamedArguments.Count == 0 ? args.Arguments : ArrayUtils.AppendRange(ArrayUtils.ToArray(args.Arguments), args.NamedArguments));

            for (int i = _firstRestrictedArg; i < restrictedArgs.Count; i++) {
                var arg = (bindingTarget.Success ? restrictedArgs[i] : restrictedArgs[i].Restrict(restrictedArgs[i].GetLimitType()));

                if (i >= args.FirstSplattedArg && i <= _lastSplattedArg || _keywordsSplatted && i >= args.Arguments.Count) {
                    metaBuilder.AddCondition(arg.Restrictions.ToExpression());
                } else {
                    metaBuilder.AddRestriction(arg.Restrictions);
                }
            }

            // Adds condition for collapsed arguments - it is the same whether we succeed or not:
            var splatCondition = GetCollapsedArgsCondition();
            if (splatCondition != null) {
                metaBuilder.AddCondition(splatCondition);
            }

            if (_argumentAssumptions != null) {
                foreach (var assumption in _argumentAssumptions) {
                    if (assumption.Second == bindingTarget.NarrowingLevel) {
                        metaBuilder.AddCondition(assumption.Third);
                    }
                }
            }
        }

        #endregion

        #region Step 4: Argument Building, Conversions

        /// <summary>
        /// Returns true if fromArg of type fromType can be assigned to toParameter with a conversion on given narrowing level.
        /// </summary>
        public override bool CanConvertFrom(Type/*!*/ fromType, DynamicMetaObject fromArg, ParameterWrapper/*!*/ toParameter, NarrowingLevel level) {
            var result = Converter.CanConvertFrom(fromArg, fromType, toParameter.Type, toParameter.ProhibitNull, level, 
                HasExplicitProtocolConversion(toParameter), _implicitProtocolConversions
            );

            if (result.Assumption != null) {
                if (_argumentAssumptions == null) {
                    _argumentAssumptions = new List<Key<int, NarrowingLevel, Ast>>();
                }

                if (_argumentAssumptions.FindIndex((k) => k.First == toParameter.ParameterInfo.Position && k.Second == level) < 0) {
                    _argumentAssumptions.Add(Key.Create(toParameter.ParameterInfo.Position, level, result.Assumption));
                }
            }

            return result.IsConvertible;
        }

        public override bool CanConvertFrom(ParameterWrapper/*!*/ fromParameter, ParameterWrapper/*!*/ toParameter) {
            return Converter.CanConvertFrom(null, fromParameter.Type, toParameter.Type, toParameter.ProhibitNull, NarrowingLevel.None, false, false).IsConvertible;
        }

        private bool HasExplicitProtocolConversion(ParameterWrapper/*!*/ parameter) {
            return
                parameter.ParameterInfo != null &&
                parameter.ParameterInfo.IsDefined(typeof(DefaultProtocolAttribute), false) &&
                !parameter.IsParamsArray; // default protocol doesn't apply on param-array/dict itself, only on the expanded parameters
        }

        private static bool DelegateReturnsValue(Type/*!*/ delegateType) {
            MethodInfo invoke = delegateType.GetMethod("Invoke");
            return invoke != null && invoke.ReturnType != typeof(void);
        }

        private static bool DelegateReturns(Type/*!*/ delegateType, Type/*!*/ returnType) {
            MethodInfo invoke = delegateType.GetMethod("Invoke");
            return invoke != null && invoke.ReturnType == returnType;
        }

        private static int DelegateArity(Type/*!*/ delegateType) {
            MethodInfo invoke = delegateType.GetMethod("Invoke");
            return invoke != null ? invoke.GetParameters().Length : -1;
        }

        public override Candidate SelectBestConversionFor(DynamicMetaObject/*!*/ arg, ParameterWrapper/*!*/ candidateOne, 
            ParameterWrapper/*!*/ candidateTwo, NarrowingLevel level) {

            Type typeOne = candidateOne.Type;
            Type typeTwo = candidateTwo.Type;
            Type actualType = arg.GetLimitType();

            if (actualType == typeof(DynamicNull)) {
                // if nil is passed as a block argument prefers BlockParam over a missing block:
                if (typeOne == typeof(BlockParam) && typeTwo == typeof(MissingBlockParam)) {
                    Debug.Assert(!candidateOne.ProhibitNull);
                    return Candidate.One;
                }

                if (typeOne == typeof(MissingBlockParam) && typeTwo == typeof(BlockParam)) {
                    Debug.Assert(!candidateTwo.ProhibitNull);
                    return Candidate.Two;
                }
            } else {
                if (typeOne == actualType) {
                    if (typeTwo == actualType) {
                        // prefer non-nullable reference type over nullable:
                        if (!actualType.IsValueType) {
                            if (candidateOne.ProhibitNull) {
                                return Candidate.One;
                            } else if (candidateTwo.ProhibitNull) {
                                return Candidate.Two;
                            }
                        }
                    } else {
                        return Candidate.One;
                    }
                } else if (typeTwo == actualType) {
                    return Candidate.Two;
                }

                // prefer String over Byte[] and Boolean for a Ruby string (explicit conversions and the any-to-Boolean one, all on level 2,
                // e.g. Assembly.Load(String/Byte[]), BinaryWriter#Write(String/Byte[]/Boolean/Char); PreferConvert orders String over Char):
                if (typeof(MutableString).IsAssignableFrom(actualType)) {
                    if (typeOne == typeof(string) && (typeTwo == typeof(byte[]) || typeTwo == typeof(bool))) {
                        return Candidate.One;
                    }

                    if ((typeOne == typeof(byte[]) || typeOne == typeof(bool)) && typeTwo == typeof(string)) {
                        return Candidate.Two;
                    }
                }

                if ((typeof(Proc).IsAssignableFrom(actualType) || typeof(RubyMethod).IsAssignableFrom(actualType))
                    && typeof(Delegate).IsAssignableFrom(typeOne) && typeof(Delegate).IsAssignableFrom(typeTwo)) {

                    // A block fits a delegate of any arity; prefer the one that passes it as many arguments as
                    // it has parameters (Select(Func<T, R>) for { |x| }, Select(Func<T, int, R>) for { |x, i| }):
                    var proc = arg.Value as Proc;
                    if (proc != null) {
                        bool oneFits = Proc.Meta.AcceptsArity(proc, DelegateArity(typeOne));
                        bool twoFits = Proc.Meta.AcceptsArity(proc, DelegateArity(typeTwo));
                        if (oneFits != twoFits) {
                            return oneFits ? Candidate.One : Candidate.Two;
                        }
                    }

                    // A block or method fits a void delegate and one returning a value alike (Task.Run(Action/Func<T>)).
                    // Its value is what Ruby code means to produce, so prefer the delegate that keeps it - C# picks
                    // Func for a lambda with a value the same way:
                    bool oneReturns = DelegateReturnsValue(typeOne), twoReturns = DelegateReturnsValue(typeTwo);
                    if (oneReturns != twoReturns) {
                        return oneReturns ? Candidate.One : Candidate.Two;
                    }

                    // What a block returns is not known until it runs, so of delegates returning a value the one
                    // that takes any value is safe (Task.Run(Func<TResult>) with TResult = Object, not
                    // Task.Run(Func<Task<TResult>>), which a block returning 42 could not satisfy):
                    if (oneReturns && twoReturns) {
                        bool oneAny = DelegateReturns(typeOne, typeof(object)), twoAny = DelegateReturns(typeTwo, typeof(object));
                        if (oneAny != twoAny) {
                            return oneAny ? Candidate.One : Candidate.Two;
                        }
                    }
                }
            }

            // prefer integer type over enum:
            if (typeOne.IsEnum && Enum.GetUnderlyingType(typeOne) == typeTwo) {
                return Candidate.Two;
            }

            if (typeTwo.IsEnum && Enum.GetUnderlyingType(typeTwo) == typeOne) {
                return Candidate.One;
            }

            return base.SelectBestConversionFor(arg, candidateOne, candidateTwo, level);
        }

        public override Expression/*!*/ Convert(DynamicMetaObject/*!*/ metaObject, Type restrictedType, ParameterInfo info, Type/*!*/ toType) {
            Expression expr = metaObject.Expression;
            Type fromType = restrictedType ?? expr.Type;

            // block:
            if (fromType == typeof(MissingBlockParam)) {
                Debug.Assert(toType == typeof(BlockParam) || toType == typeof(MissingBlockParam));
                return AstUtils.Constant(null);
            }

            if (fromType == typeof(BlockParam) && toType == typeof(MissingBlockParam)) {
                return AstUtils.Constant(null);
            }

            // protocol conversions:
            if (info != null && info.IsDefined(typeof(DefaultProtocolAttribute), false)) {
                var action = RubyConversionAction.TryGetDefaultConversionAction(Context, toType);
                if (action != null) {
                    // TODO: inline implicit conversions:
                    return AstUtils.LightDynamic(action, toType, expr);
                }

                // Do not throw an exception here to allow generic type parameters to be used with D.P. attribute.
                // The semantics should be to use DP if available for the current instantiation and ignore it otherwise.
            }

            // A Ruby String passed for a CLR method's Object parameter arrives as a System.String: CLR code looks at what
            // it gets - ADO.NET's AddWithValue, serializers, String.Format's format items, data binding - and knows
            // System.String, not IronRuby's MutableString. JRuby passes a java.lang.String for an Object parameter too.
            // (An argument for an Object parameter has no type restriction, so whether it is a String is decided as the call runs.)
            if (_implicitProtocolConversions && toType == typeof(object)) {
                if (restrictedType != null && typeof(MutableString).IsAssignableFrom(restrictedType)) {
                    return Methods.ConvertMutableStringToClrString.OpCall(AstUtils.Convert(expr, typeof(MutableString)));
                }
                if (restrictedType == null || restrictedType == typeof(object)) {
                    return Ast.Call(RubyOps.ToClrArgumentMethod, AstUtils.Box(expr));
                }
            }

            if (restrictedType != null) {
                if (restrictedType == typeof(DynamicNull)) {
                    if (!toType.IsValueType || toType.IsGenericType && toType.GetGenericTypeDefinition() == typeof(Nullable<>)) {
                        return AstUtils.Constant(null, toType);
                    } else if (toType == typeof(bool)) {
                        return AstUtils.Constant(false);
                    }
                }

                if (toType.IsAssignableFrom(restrictedType)) {
                    // expr can be converted to restrictedType, which can be converted toType => we can convert expr to toType:
                    return AstUtils.Convert(expr, CompilerHelpers.GetVisibleType(toType));
                }

                // if there is a simple conversion from restricted type, convert the expression to the restricted type and use that conversion:
                Type visibleRestrictedType = CompilerHelpers.GetVisibleType(restrictedType);
                if (Converter.CanConvertFrom(metaObject, visibleRestrictedType, toType, false, NarrowingLevel.None, false, false).IsConvertible) {
                    expr = AstUtils.Convert(expr, visibleRestrictedType);
                }
            }

            return Converter.TryConvertToCollection(expr, restrictedType ?? expr.Type, toType, _args.RubyContext, _implicitProtocolConversions)
                ?? Converter.TryConvertTypeGroup(expr, restrictedType ?? expr.Type, toType, _args.MetaContext.Expression)
                ?? Converter.TryConvertRange(expr, restrictedType ?? expr.Type, toType)
                ?? Converter.ConvertExpression(expr, toType, _args.RubyContext, _args.MetaContext.Expression, _implicitProtocolConversions);
        }

        /// <summary>
        /// A method with out or ref parameters returns its result followed by their final values.
        /// Hand them back as a Ruby Array rather than an object[], so they are what Ruby code
        /// expects of a multi-value result: an Array to destructure, pattern match (a C#
        /// Deconstruct(out ...) method serves as #deconstruct) or call Array methods on.
        /// </summary>
        protected override Expression/*!*/ GetByRefArrayExpression(Expression/*!*/ argumentArrayExpression) {
            return Methods.MakeArrayN.OpCall(argumentArrayExpression);
        }

        protected override Expression/*!*/ GetSplattedExpression() {
            return _listVariable;
        }

        protected override object GetSplattedItem(int index) {
            return _list[index];
        }

        internal sealed class RubyContextArgBuilder : ArgBuilder {
            public RubyContextArgBuilder(ParameterInfo/*!*/ info)
                : base(info) {
            }

            public override int Priority {
                get { return -1; }
            }

            public override int ConsumedArgumentCount {
                get { return 0; }
            }

            protected override Expression ToExpression(OverloadResolver/*!*/ resolver, RestrictedArguments/*!*/ args, bool[]/*!*/ hasBeenUsed) {
                return ((RubyOverloadResolver)resolver).ContextExpression;
            }
        }

        internal sealed class RubyCallSiteStorageBuilder : ArgBuilder {
            public RubyCallSiteStorageBuilder(ParameterInfo/*!*/ info)
                : base(info) {
            }

            public override int Priority {
                get { return -1; }
            }

            public override int ConsumedArgumentCount {
                get { return 0; }
            }

            protected override Expression ToExpression(OverloadResolver/*!*/ resolver, RestrictedArguments/*!*/ args, bool[]/*!*/ hasBeenUsed) {
                return AstUtils.Constant(Activator.CreateInstance(ParameterInfo.ParameterType, ((RubyOverloadResolver)resolver).Context));
            }
        }

        internal sealed class RubyScopeArgBuilder : ArgBuilder {
            public RubyScopeArgBuilder(ParameterInfo/*!*/ info)
                : base(info) {
            }

            public override int Priority {
                get { return -1; }
            }

            public override int ConsumedArgumentCount {
                get { return 0; }
            }

            protected override Expression ToExpression(OverloadResolver/*!*/ resolver, RestrictedArguments/*!*/ args, bool[]/*!*/ hasBeenUsed) {
                return ((RubyOverloadResolver)resolver).ScopeExpression;
            }
        }

        internal sealed class RubyClassCtorArgBuilder : ArgBuilder {
            public RubyClassCtorArgBuilder(ParameterInfo/*!*/ info)
                : base(info) {
            }

            public override int Priority {
                get { return -1; }
            }

            public override int ConsumedArgumentCount {
                get { return 0; }
            }

            protected override Expression ToExpression(OverloadResolver/*!*/ resolver, RestrictedArguments/*!*/ args, bool[]/*!*/ hasBeenUsed) {
                return ((RubyOverloadResolver)resolver)._args.TargetExpression;
            }
        }

        internal sealed class ImplicitInstanceBuilder : InstanceBuilder {
            public ImplicitInstanceBuilder()
                : base(-1) {
            }

            public override bool HasValue {
                get { return true; }
            }

            public override int ConsumedArgumentCount {
                get { return 0; }
            }

            protected override Expression/*!*/ ToExpression(ref MethodInfo/*!*/ method, OverloadResolver/*!*/ resolver, RestrictedArguments/*!*/ args, bool[]/*!*/ hasBeenUsed) {
                return ((RubyOverloadResolver)resolver)._args.TargetExpression;
            }
        }

        internal sealed class MissingBlockArgBuilder : SimpleArgBuilder {
            public MissingBlockArgBuilder(int index)
                : base(typeof(MissingBlockParam), index, false, false) {
            }

            public override int Priority {
                get { return -1; }
            }

            public override int ConsumedArgumentCount {
                get { return 1; }
            }

            protected override SimpleArgBuilder/*!*/ Copy(int newIndex) {
                return new MissingBlockArgBuilder(newIndex);
            }

            protected override Expression ToExpression(OverloadResolver/*!*/ resolver, RestrictedArguments/*!*/ args, bool[]/*!*/ hasBeenUsed) {
                Debug.Assert(Index < args.Length);
                Debug.Assert(Index < hasBeenUsed.Length);
                hasBeenUsed[Index] = true;
                return null;
            }
        }

        #endregion

        #region Step 5: Errors

        public override Microsoft.Scripting.Actions.ErrorInfo MakeInvalidParametersError(BindingTarget target) {
            Expression exceptionValue;
            switch (target.Result) {
                case BindingResult.AmbiguousMatch:
                    exceptionValue = MakeAmbiguousCallError(target);
                    break;

                case BindingResult.IncorrectArgumentCount:
                    exceptionValue = MakeIncorrectArgumentCountError(target);
                    break;

                case BindingResult.CallFailure:
                    exceptionValue = MakeCallFailureError(target);
                    break;

                case BindingResult.NoCallableMethod:
                    exceptionValue = Methods.CreateArgumentsError.OpCall(
                        AstUtils.Constant(String.Format("Method '{0}' is not callable", target.Name))
                    );
                    break;

                default: 
                    throw new InvalidOperationException();
            }
            return Microsoft.Scripting.Actions.ErrorInfo.FromException(exceptionValue);
        }

        private Expression MakeAmbiguousCallError(BindingTarget target) {
            StringBuilder sb = new StringBuilder(string.Format("Found multiple methods for '{0}': ", target.Name));
            string outerComma = "";
            foreach (MethodCandidate candidate in target.AmbiguousMatches) {
                IList<ParameterWrapper> parameters = candidate.GetParameters();
                
                string innerComma = "";

                sb.Append(outerComma);
                sb.Append(target.Name);
                sb.Append('(');
                foreach (var param in parameters) {
                    if (!param.IsHidden) {
                        sb.Append(innerComma);
                        sb.Append(Binder.GetTypeName(param.Type));
                        if (param.ProhibitNull) {
                            sb.Append('!');
                        }
                        innerComma = ", ";
                    }
                }

                sb.Append(')');
                outerComma = ", ";
            }

            if (_blockAsArgument) {
                sb.Append(" - the block fits more than one of them; pass the delegate itself, e.g. System::Func[...].new { ... }");
            }

            return Methods.MakeAmbiguousMatchError.OpCall(AstUtils.Constant(sb.ToString()));
        }

        private Expression MakeIncorrectArgumentCountError(BindingTarget target) {
            return Methods.MakeWrongNumberOfArgumentsErrorN.OpCall(
                AstUtils.Constant(target.ActualArgumentCount),
                AstUtils.Constant(DescribeExpectedArgumentCount(target.ExpectedArgumentCount))
            );
        }

        /// <summary>
        /// MRI spells the acceptable arities as a single number ("expected 1"), a range when they are
        /// contiguous ("expected 1..3") or a list otherwise ("expected 2, 3, or 5"). Verified against
        /// CRuby 3.3.8 (File.open, String#bytesplice).
        /// </summary>
        private static string/*!*/ DescribeExpectedArgumentCount(IList<int>/*!*/ available) {
            if (available.Count == 0) {
                // no overload is callable:
                return "0";
            }

            var sorted = new List<int>(available);
            sorted.Sort();
            // the same arity can be contributed by several overloads:
            for (int i = sorted.Count - 1; i > 0; i--) {
                if (sorted[i] == sorted[i - 1]) {
                    sorted.RemoveAt(i);
                }
            }

            if (sorted.Count == 1) {
                return sorted[0].ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            // An overload taking `params` reports Int32.MaxValue as its maximum; MRI spells an
            // unbounded arity "3+", not "3 or 2147483647".
            if (sorted[sorted.Count - 1] == Int32.MaxValue) {
                sorted.RemoveAt(sorted.Count - 1);
                return sorted[0].ToString(System.Globalization.CultureInfo.InvariantCulture) + "+";
            }

            if (sorted[sorted.Count - 1] - sorted[0] == sorted.Count - 1) {
                return sorted[0].ToString(System.Globalization.CultureInfo.InvariantCulture) + ".." +
                    sorted[sorted.Count - 1].ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            var sb = new StringBuilder();
            for (int i = 0; i < sorted.Count; i++) {
                if (i > 0) {
                    sb.Append(i == sorted.Count - 1 ? (sorted.Count == 2 ? " or " : ", or ") : ", ");
                }
                sb.Append(sorted[i]);
            }
            return sb.ToString();
        }

        /// <summary>
        /// What a conversion target is called in an error message. The CLR name of the parameter
        /// type is not what Ruby code sees: a parameter typed IList is an Array, not a
        /// System::Collections::IList, and saying so turned "no implicit conversion of nil into
        /// Array" into something no Ruby programmer would recognise.
        /// </summary>
        private string/*!*/ GetConversionTargetName(Type/*!*/ type) {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Union<,>)) {
                var g = type.GetGenericArguments();
                return GetConversionTargetName(g[0]) + " or " + GetConversionTargetName(g[1]);
            }
            if (typeof(IList).IsAssignableFrom(type)) {
                return "Array";
            }
            if (typeof(IDictionary<object, object>).IsAssignableFrom(type)) {
                return "Hash";
            }
            if (typeof(MutableString).IsAssignableFrom(type)) {
                return "String";
            }
            return Binder.GetTypeName(type);
        }

        // The keywords as the call spelled them (:first_name), not the CLR names they were bound to.
        private string/*!*/ FormatKeywords(IList<string>/*!*/ clrNames) {
            var result = new StringBuilder();
            foreach (string clrName in clrNames) {
                int index = (_keywordNames != null) ? Array.IndexOf(_keywordNames, clrName) : -1;
                if (result.Length > 0) {
                    result.Append(", ");
                }
                result.Append(':').Append(index >= 0 ? _keywordSymbols[index].ToString() : clrName);
            }
            return result.ToString();
        }

        private Expression MakeCallFailureError(BindingTarget target) {
            foreach (CallFailure cf in target.CallFailures) {
                switch (cf.Reason) {
                    case CallFailureReason.ConversionFailure:
                        foreach (ConversionResult cr in cf.ConversionResults) {
                            if (cr.Failed) {
                                if (typeof(Proc).IsAssignableFrom(cr.To)) {
                                    return Methods.CreateArgumentsErrorForProc.OpCall(AstUtils.Constant(cr.GetArgumentTypeName(Binder)));
                                }

                                Debug.Assert(typeof(BlockParam).IsSealed);
                                if (cr.To == typeof(BlockParam)) {
                                    return Methods.CreateArgumentsErrorForMissingBlock.OpCall();
                                }

                                // A CLR string parameter is how a Symbol is spelled in this tree, and
                                // MRI reports a bad method name with its own wording rather than the
                                // implicit-conversion one. nil reaches here rather than the conversion
                                // action, which accepts it and leaves [NotNull] to do the rejecting.
                                if (cr.To == typeof(string)) {
                                    string argument = cr.GetArgumentTypeName(Binder);
                                    return Methods.CreateNotSymbolNorStringErrorFor.OpCall(
                                        AstUtils.Constant(argument == "NilClass" ? "nil" : argument)
                                    );
                                }

                                string toType = GetConversionTargetName(cr.To);

                                // An argument that failed to convert to a [DefaultProtocol] parameter type is
                                // MRI's implicit-conversion failure: "no implicit conversion of nil into String".
                                return Methods.CreateImplicitConversionError.OpCall(
                                    AstUtils.Constant(cr.GetArgumentTypeName(Binder)),
                                    AstUtils.Constant(toType)
                                );
                            }
                        }
                        break;

                    case CallFailureReason.TypeInference:
                        // TODO: Display generic parameters so it's clear what we couldn't infer.
                        return Methods.CreateArgumentsError.OpCall(
                            AstUtils.Constant(String.Format("generic arguments could not be infered for method '{0}'", target.Name))
                        );

                    case CallFailureReason.UnassignableKeyword:
                        // MRI: unknown keyword: :zz / unknown keywords: :a, :b
                        return Methods.CreateArgumentsError.OpCall(AstUtils.Constant(
                            (cf.KeywordArguments.Count == 1 ? "unknown keyword: " : "unknown keywords: ") + FormatKeywords(cf.KeywordArguments)
                        ));

                    case CallFailureReason.DuplicateKeyword:
                        return Methods.CreateArgumentsError.OpCall(AstUtils.Constant(
                            "keyword " + FormatKeywords(cf.KeywordArguments) + " given for a parameter that is already passed positionally"
                        ));

                    default: 
                        throw new InvalidOperationException();
                }
            }
            throw new InvalidOperationException();
        }

        #endregion
    }
}