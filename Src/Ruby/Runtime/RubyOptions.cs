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
using System.Collections.ObjectModel;
using System.Threading;
using IronRuby.Builtins;
using Microsoft.Scripting;
using Microsoft.Scripting.Utils;

namespace IronRuby.Runtime {

    public sealed class RubyOptions : LanguageOptions {
        private readonly ReadOnlyCollection<string>/*!*/ _arguments;
        private readonly RubyEncoding/*!*/ _localeEncoding;
        private readonly RubyEncoding _defaultEncoding;
        private readonly string _externalEncodingName;
        private readonly string _internalEncodingName;
        private readonly bool _loopOverInput;
        private readonly bool _printEachLine;
        private readonly bool _autoSplit;
        private readonly bool _chopLines;
        private readonly string _inputRecordSeparator;
        private readonly bool _noInputRecordSeparator;
        private readonly string _inplaceMode;
        private readonly string _fieldSeparator;
        private readonly bool _scriptSwitches;
        private readonly bool _skipToRubyShebang;
        private readonly int _frozenStringLiteral;
        private readonly bool _debugFrozenStringLiteral;
        private readonly int _backtraceLimit;
        private readonly bool _checkSyntaxOnly;
        private readonly ReadOnlyCollection<string>/*!*/ _warningCategoryFlags;
        private readonly string _standardLibraryPath;
        private readonly string _applicationBase;
        private readonly ReadOnlyCollection<string> _requirePaths;
        private readonly string _mainFile;
        private readonly bool _enableTracing;
        private readonly int _verbosity;
        private readonly bool _debugVariable;
        private readonly string _savePath;
        private readonly bool _profile;
        private readonly bool _hasSearchPaths;
        private readonly bool _noAssemblyResolveHook;
        private readonly bool _objectSpace;
        private readonly bool _jit;
        private readonly bool _osr;

#if DEBUG
        public static bool CompileRegexps;
        public static bool ShowRules;
#endif

        public ReadOnlyCollection<string>/*!*/ Arguments {
            get { return _arguments; }
        }

        public RubyEncoding/*!*/ LocaleEncoding {
            get { return _localeEncoding; }
        }

        public RubyEncoding DefaultEncoding {
            get { return _defaultEncoding; }
        }

        /// <summary>
        /// Encoding.default_external as named by -E / --encoding / --external-encoding, or null.
        /// Distinct from <see cref="DefaultEncoding"/>, which is -K and says how the *source* is
        /// to be read; the two are independent in MRI. Kept as a name rather than a RubyEncoding
        /// because resolving one needs the context that is not built yet when options are parsed.
        /// </summary>
        public string ExternalEncodingName {
            get { return _externalEncodingName; }
        }

        /// <summary>
        /// Encoding.default_internal as named by -E ext:int / --internal-encoding, or null - and
        /// null is the normal state, meaning no transcoding on read.
        /// </summary>
        public string InternalEncodingName {
            get { return _internalEncodingName; }
        }

        /// <summary>-n, and -p which implies it: run the program once per input line.</summary>
        public bool LoopOverInput {
            get { return _loopOverInput; }
        }

        /// <summary>-p: print $_ at the end of every iteration of the -n loop.</summary>
        public bool PrintEachLine {
            get { return _printEachLine; }
        }

        /// <summary>-a: split each input line into $F.</summary>
        public bool AutoSplit {
            get { return _autoSplit; }
        }

        /// <summary>-l: chomp each input line, and set $\ to $/.</summary>
        public bool ChopLines {
            get { return _chopLines; }
        }

        /// <summary>
        /// --enable / --disable=frozen-string-literal: 1 enabled, -1 disabled, 0 not given. The
        /// numbering is prism's own (PM_OPTIONS_FROZEN_STRING_LITERAL_*), because prism is what
        /// this feeds and it is the parser that decides which literals the flag reaches. A file's
        /// own magic comment overrides it either way.
        /// </summary>
        public int FrozenStringLiteral {
            get { return _frozenStringLiteral; }
        }

        /// <summary>
        /// --debug-frozen-string-literal, which --debug implies: every string literal remembers
        /// where it was written, so a FrozenError or a chilled-mutation warning can name it.
        /// </summary>
        public bool DebugFrozenStringLiteral {
            get { return _debugFrozenStringLiteral; }
        }

        /// <summary>
        /// --backtrace-limit=N: the most frames an uncaught exception prints. -1 (the default)
        /// means no limit. Thread::Backtrace.limit reads it back.
        /// </summary>
        public int BacktraceLimit {
            get { return _backtraceLimit; }
        }

        /// <summary>
        /// -c: the program is compiled and "Syntax OK" printed instead of running it.
        /// </summary>
        public bool CheckSyntaxOnly {
            get { return _checkSyntaxOnly; }
        }

        /// <summary>
        /// The -W:category and -W:no-category flags, in the order they were given.
        /// </summary>
        public ReadOnlyCollection<string>/*!*/ WarningCategoryFlags {
            get { return _warningCategoryFlags; }
        }

        /// <summary>The initial $/ as given by -0, or null for the default newline.</summary>
        public string InputRecordSeparator {
            get { return _inputRecordSeparator; }
        }

        /// <summary>-0 with a value of 0400 or more: $/ is nil and a read takes the whole input.</summary>
        public bool NoInputRecordSeparator {
            get { return _noInputRecordSeparator; }
        }

        /// <summary>
        /// The backup extension given by -i, or null when the option was not used. The empty
        /// string means in-place editing with no backup kept, which is not the same as null.
        /// </summary>
        public string InplaceMode {
            get { return _inplaceMode; }
        }

        /// <summary>The pattern given by -F, which $; starts out as (a Regexp), or null.</summary>
        public string FieldSeparator {
            get { return _fieldSeparator; }
        }

        /// <summary>-s: the leading -name[=value] arguments of ARGV become global variables.</summary>
        public bool ScriptSwitches {
            get { return _scriptSwitches; }
        }

        /// <summary>-x: the main script starts after its first #!...ruby line.</summary>
        public bool SkipToRubyShebang {
            get { return _skipToRubyShebang; }
        }

        public string MainFile {
            get { return _mainFile; }
        }
        
        public int Verbosity {
            get { return _verbosity; }
        }

        public bool EnableTracing {
            get { return _enableTracing; }
        }

        public string SavePath {
            get { return _savePath; }
        }

        public bool Profile {
            get { return _profile; }
        }

        public bool NoAssemblyResolveHook {
            get { return _noAssemblyResolveHook; }
        }

        /// <summary>
        /// Record every RubyObject weakly, so that ObjectSpace.each_object can find them. Off by
        /// default: it costs a weak GC handle per object, which about triples the cost of Object#new.
        /// </summary>
        public bool ObjectSpace {
            get { return _objectSpace; }
        }

        /// <summary>
        /// The ZJIT-style method JIT: a hot method body is replaced by a copy specialized to the
        /// types it has actually been called with. On by default; -X:NoJIT turns it off, and when
        /// off nothing in the compiler or the runtime looks at it beyond this one flag at method
        /// compile time.
        /// </summary>
        public bool Jit {
            get { return _jit; }
        }

        /// <summary>
        /// On-stack replacement for loops: a loop that has taken enough back edges is left for a
        /// type-specialized copy of itself, which reads the scope's locals out of the tuple they
        /// already live in and carries on at the next iteration. On by default; -X:NoOSR turns it
        /// off, and when off the loop transform does not even emit the counter.
        /// </summary>
        public bool Osr {
            get { return _osr; }
        }

        public string StandardLibraryPath {
            get { return _standardLibraryPath; }
        }

        public string ApplicationBase {
            get { return _applicationBase; }
        }

        public ReadOnlyCollection<string> RequirePaths {
            get { return _requirePaths; }
        }

        public bool HasSearchPaths {
            get { return _hasSearchPaths; }
        }

        /// <summary>
        /// The initial value of $DEBUG variable.
        /// </summary>
        public bool DebugVariable {
            get { return _debugVariable; }
        }

        public RubyOptions(IDictionary<string, object>/*!*/ options)
            : base(options) {
            _arguments = GetStringCollectionOption(options, "Arguments") ?? EmptyStringCollection;
            _localeEncoding = GetOption(options, "LocaleEncoding", RubyEncoding.UTF8);
            _defaultEncoding = GetOption<RubyEncoding>(options, "DefaultEncoding", null);
            _externalEncodingName = GetOption<string>(options, "ExternalEncoding", null);
            _internalEncodingName = GetOption<string>(options, "InternalEncoding", null);

            _loopOverInput = GetOption(options, "LoopOverInput", false);
            _printEachLine = GetOption(options, "PrintEachLine", false);
            _autoSplit = GetOption(options, "AutoSplit", false);
            _chopLines = GetOption(options, "ChopLines", false);
            _inputRecordSeparator = GetOption<string>(options, "InputRecordSeparator", null);
            _noInputRecordSeparator = GetOption(options, "NoInputRecordSeparator", false);
            _inplaceMode = GetOption<string>(options, "InplaceMode", null);
            _fieldSeparator = GetOption<string>(options, "FieldSeparator", null);
            _scriptSwitches = GetOption(options, "ScriptSwitches", false);
            _skipToRubyShebang = GetOption(options, "SkipToRubyShebang", false);
            _frozenStringLiteral = GetOption(options, "FrozenStringLiteral", 0);
            _debugFrozenStringLiteral = GetOption(options, "DebugFrozenStringLiteral", false);
            _backtraceLimit = GetOption(options, "BacktraceLimit", -1);
            _checkSyntaxOnly = GetOption(options, "CheckSyntaxOnly", false);
            _warningCategoryFlags = GetStringCollectionOption(options, "WarningCategoryFlags") ?? EmptyStringCollection;

            _mainFile = GetOption(options, "MainFile", (string)null);
            _verbosity = GetOption(options, "Verbosity", 1);
            _debugVariable = GetOption(options, "DebugVariable", false);
            _enableTracing = GetOption(options, "EnableTracing", false);
            _savePath = GetOption(options, "SavePath", (string)null);
            _profile = GetOption(options, "Profile", false);
            _noAssemblyResolveHook = GetOption(options, "NoAssemblyResolveHook", false);
            _objectSpace = GetOption(options, "ObjectSpace", false);
            // Both specialize by compiling expression trees to IL; without dynamic code (NativeAOT)
            // that would only hand them to System.Linq.Expressions' interpreter, so they default off.
            _jit = GetOption(options, "JIT", System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported);
            _osr = GetOption(options, "OSR", System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported);
            _requirePaths = GetStringCollectionOption(options, "RequiredPaths", ';', ',');
            _hasSearchPaths = GetOption<object>(options, "SearchPaths", null) != null;
            _standardLibraryPath = GetOption(options, "StandardLibrary", (string)null);
            _applicationBase = GetOption(options, "ApplicationBase", (string)null);
        }
    }
}
