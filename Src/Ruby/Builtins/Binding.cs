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

using Microsoft.Scripting.Utils;
using IronRuby.Runtime;

namespace IronRuby.Builtins {
    public sealed class Binding : IDuplicable {
        private RubyScope/*!*/ _localScope;
        private readonly object _self;

        /// <summary>
        /// Local scope captured by the binding.
        /// </summary>
        public RubyScope/*!*/ LocalScope {
            get { return _localScope; }
        }

        /// <summary>
        /// The scope of the frame the binding was taken in, under the locals the binding keeps of its own.
        /// </summary>
        public RubyScope/*!*/ FrameScope {
            get {
                RubyScope scope = _localScope;
                while (scope.Kind == ScopeKind.BindingCopy) {
                    scope = scope.Parent;
                }
                return scope;
            }
        }

        /// <summary>
        /// Self object captured by the binding. Can be different from LocalScope.SelfObject in MRI 1.8.
        /// </summary>
        public object SelfObject {
            get { return _self; }
        }

        /// <summary>
        /// Where the binding was taken, for Binding#source_location; null if unknown.
        /// </summary>
        public string SourcePath { get; set; }
        public int SourceLine { get; set; }

        public Binding(RubyScope/*!*/ localScope) 
            : this(localScope, localScope.SelfObject) {
        }

        public Binding(RubyScope/*!*/ localScope, object self) {
            Assert.NotNull(localScope);
            localScope.MarkCaptured();
            _localScope = localScope;
            _self = self;
        }

        /// <summary>
        /// A Binding for the given frame. In MRI every binding object extends the frame with locals of
        /// its own: a variable an eval defines through it is kept in that binding and seen by later
        /// evals through the same object, but not by the frame nor by another binding of it.
        /// </summary>
        public static Binding/*!*/ Create(RubyScope/*!*/ frame, object self) {
            return new Binding(new RubyBindingCopyScope(frame, self), self);
        }

        /// <summary>
        /// MRI's Binding#dup keeps every variable that already exists shared with the original and
        /// lets the two diverge over variables defined afterwards - in either of them. So both end up
        /// with a fresh scope of their own nested in the one they shared until now.
        /// </summary>
        object IDuplicable.Duplicate(RubyContext/*!*/ context, bool copySingletonMembers) {
            var shared = _localScope;
            _localScope = new RubyBindingCopyScope(shared, _self);
            var result = new Binding(new RubyBindingCopyScope(shared, _self), _self);
            result.SourcePath = SourcePath;
            result.SourceLine = SourceLine;
            context.CopyInstanceData(this, result, copySingletonMembers);
            return result;
        }
    }
}
