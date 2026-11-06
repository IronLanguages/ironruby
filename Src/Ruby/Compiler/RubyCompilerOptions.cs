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

using System.Collections.Generic;
using Microsoft.Scripting;

namespace IronRuby.Compiler {
    internal enum TopScopeFactoryKind {
        /// <summary>
        /// Simple scope with or without DLR Scope binding.
        /// Used by Execute("code") and Execute("code", scope).
        /// </summary>
        Hosted,

        /// <summary>
        /// Main script scope w/o DLR Scope binding.
        /// The factory sets TOPLEVEL_BINDING and DATA constants.
        /// Used by ExecuteProgram. 
        /// </summary>
        Main,

        /// <summary>
        /// Top scope is passed by parameter to the top-level lambda, it is not scope is created.
        /// Used by eval("code"), eval("code", binding).
        /// </summary>
        None,

        /// <summary>
        /// Creates a module scope with parent scope passed into the top-level lambda.
        /// Used by module_eval("code")/instance_eval("code").
        /// </summary>
        ModuleEval,

        /// <summary>
        /// File executed via load(false) or require.
        /// </summary>
        File,

        /// <summary>
        /// File executed via load(true).
        /// </summary>
        WrappedFile,
    }

    public sealed class RubyCompilerOptions : CompilerOptions {
        /// <summary>
        /// Embedded code. The code being compiled is embedded into an already compiled code.
        /// </summary>
        internal bool IsEval { get; set; }
        
        internal TopScopeFactoryKind FactoryKind { get; set; }
        
        private SourceLocation _initialLocation = SourceLocation.MinValue;

        internal SourceLocation InitialLocation {
            get { return _initialLocation; }
            set { _initialLocation = value; }
        }

        /// <summary>
        /// Method name used by blocks.
        /// </summary>
        internal string TopLevelMethodName { get; set; }

        /// <summary>
        /// Eval only: the label of the frame the code runs in, which MRI gives the eval's own frame
        /// ("Object#m", "block in <main>"): the label without the blocks, and how many blocks deep.
        /// </summary>
        internal string EvalFrameBaseLabel { get; set; }
        internal int EvalFrameBlockLevels { get; set; }

        /// <summary>
        /// Used by super-calls with implicit parameters.
        /// </summary>
        internal string[] TopLevelParameterNames { get; set; }

        internal bool TopLevelHasUnsplatParameter { get; set; }

        /// <summary>
        /// The declared signature of the method, if it has keyword parameters (see SuperCall.RecordKeywordSignature).
        /// </summary>
        internal IronRuby.Runtime.Calls.RubyParameterSignature TopLevelParameterSignature { get; set; }

        /// <summary>
        /// Eval only: the encoding of the string being evaluated, which is the source encoding
        /// unless a magic comment says otherwise.
        /// </summary>
        internal IronRuby.Builtins.RubyEncoding EvalSourceEncoding { get; set; }

        /// <summary>
        /// Used by dynamic variable look-up.
        /// </summary>
        internal List<string> LocalNames { get; set; }
    }
}
