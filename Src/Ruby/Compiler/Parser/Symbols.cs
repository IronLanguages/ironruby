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

using Microsoft.Scripting;
using IronRuby.Compiler.Ast;
using System;

namespace IronRuby.Compiler {
    public static class Symbols {
        public static readonly string RestArgsLocal = "?rest?";
        
        public static readonly string MethodMissing = "method_missing";
        public static readonly string ConstantAdded = "const_added";
        public static readonly string MethodAdded = "method_added";
        public static readonly string MethodRemoved = "method_removed";
        public static readonly string MethodUndefined = "method_undefined";
        public static readonly string SingletonMethodAdded = "singleton_method_added";
        public static readonly string SingletonMethodRemoved = "singleton_method_removed";
        public static readonly string SingletonMethodUndefined = "singleton_method_undefined";
        public static readonly string Inherited = "inherited";
        public static readonly string RespondTo = "respond_to?";
        public static readonly string RespondToMissing = "respond_to_missing?";
        public static readonly string ToProc = "to_proc";
        public static readonly string ToStr = "to_str";
        public static readonly string ToPath = "to_path";
        public static readonly string ToA = "to_a";
        public static readonly string ToAry = "to_ary";
        public static readonly string ToHash = "to_hash";
        public static readonly string ToInt = "to_int";
        public static readonly string ToI = "to_i";
        public static readonly string ToF = "to_f";
        public static readonly string Initialize = "initialize";
        public static readonly string InitializeCopy = "initialize_copy";
        public static readonly string BasicObject = "BasicObject";
        public static readonly string Object = "Object";
        public static readonly string Kernel = "Kernel";
        public static readonly string Module = "Module";
        public static readonly string Class = "Class";

        public static readonly string GreaterThan = ">";
        public static readonly string LessThan = "<";
        public static readonly string Or = "||";
        public static readonly string And = "&&";
        public static readonly string Multiply = "*";
        public static readonly string Divide = "/";
        public static readonly string Bang = "!";

        // variables:
        public static readonly string CurrentException = Bang;
        public static readonly string CurrentExceptionBacktrace = "@";
        public static readonly string ItemSeparator = ",";
        public static readonly string StringSeparator = ";";
        public static readonly string InputSeparator = Divide;
        public static readonly string OutputSeparator = "\\";
        public static readonly string CommandLineArguments = Multiply;
        public static readonly string CommandLineProgramPath = "0";
        public static readonly string CurrentProcessId = "$";
        public static readonly string ChildProcessExitStatus = "?";
        public static readonly string LoadPath = ":";
        public static readonly string LoadedFiles = "\"";
        public static readonly string InputContent = LessThan;
        public static readonly string OutputStream = GreaterThan;
        public static readonly string LastInputLine = "_";
        public static readonly string LastInputLineNumber = ".";
        
        // match references:
        public static readonly string MatchData = RegexMatchReference.MatchDataName;
        public static readonly string EntireMatch = RegexMatchReference.EntireMatchName;
        public static readonly string MatchLastGroup = RegexMatchReference.MatchLastGroupName;
        public static readonly string PreMatch = RegexMatchReference.PreMatchName;
        public static readonly string PostMatch = RegexMatchReference.PostMatchName;
    }
}
