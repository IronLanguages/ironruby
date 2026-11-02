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
using System.Diagnostics;
using System.IO;
using System.Security;
using IronRuby.Builtins;
using IronRuby.Runtime;
using Microsoft.Scripting;
using Microsoft.Scripting.Hosting;
using Microsoft.Scripting.Hosting.Shell;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;

namespace IronRuby.Hosting {
    public sealed class RubyConsoleOptions : ConsoleOptions {
        public string ChangeDirectory;
        public bool DisplayVersion;
    }

    public sealed class RubyOptionsParser : OptionsParser<RubyConsoleOptions> {
        private readonly List<string>/*!*/ _loadPaths = new List<string>();
        private readonly List<string>/*!*/ _requiredPaths = new List<string>();
        private RubyEncoding _defaultEncoding;
        private string _externalEncodingName;
        private string _internalEncodingName;
        private bool _disableRubyGems;
        private bool _disableRubyOpt;
        private bool _enableDidYouMean;
        private readonly List<string>/*!*/ _stdLibPaths = new List<string>();
        private readonly List<string>/*!*/ _warningCategoryFlags = new List<string>();

#if DEBUG
        private ConsoleTraceListener _debugListener;

        private sealed class CustomTraceFilter : TraceFilter {
            public readonly Dictionary<string, bool>/*!*/ Categories = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            public bool EnableAll { get; set; }

            public override bool ShouldTrace(TraceEventCache cache, string source, TraceEventType eventType, int id, string category, object[] args, object data1, object[] data) {
                string message = data1 as string;
                if (message == null) return true;

                bool enabled;
                if (Categories.TryGetValue(category, out enabled)) {
                    return enabled;
                } else {
                    return EnableAll;
                }
            }
        }

        private void SetTraceFilter(string/*!*/ arg, bool enable) {
            string[] categories = arg.Split(new[] { ';', ','}, StringSplitOptions.RemoveEmptyEntries);

            if (categories.Length == 0 && !enable) {
                Trace.Listeners.Clear();
                return;
            }

            if (_debugListener == null) {
                _debugListener = new ConsoleTraceListener { IndentSize = 4, Filter = new CustomTraceFilter { EnableAll = categories.Length == 0 } };
                Trace.Listeners.Add(_debugListener);
            } 
         
            foreach (var category in categories) {
                ((CustomTraceFilter)_debugListener.Filter).Categories[category] = enable;
            }
        }
#endif

        /// <summary>
        /// One feature named by --enable / --disable. MRI accepts the names of features it has
        /// compiled out, so the ones not implemented here are accepted too; a name that is no
        /// feature at all only gets a warning, as in MRI.
        /// </summary>
        private void SetFeature(string/*!*/ feature, bool enable) {
            // MRI treats '-' and '_' in a feature name alike.
            switch (feature.Replace('-', '_')) {
                case "gems":
                case "gem":
                    _disableRubyGems = !enable;
                    break;

                case "frozen_string_literal":
                    // prism's numbering, which is where this ends up.
                    LanguageSetup.Options["FrozenStringLiteral"] = enable ? 1 : -1;
                    break;

                case "rubyopt":
                    _disableRubyOpt = !enable;
                    break;

                // did_you_mean is only loaded when asked for: it hooks every NameError message,
                // which is more than the default startup should take on.
                case "did_you_mean":
                    _enableDidYouMean = enable;
                    break;

                case "all":
                    _disableRubyGems = !enable;
                    _disableRubyOpt = !enable;
                    _enableDidYouMean = enable;
                    LanguageSetup.Options["FrozenStringLiteral"] = enable ? 1 : -1;
                    break;

                case "error_highlight":
                case "syntax_suggest":
                case "jit":
                case "yjit":
                case "zjit":
                    // accepted and ignored: nothing here implements them
                    break;

                default:
                    // MRI warns and carries on rather than refusing the command line.
                    string option = enable ? "--enable" : "--disable";
                    Console.Error.WriteLine("ir: warning: unknown argument for {0}: '{1}'", option, feature);
                    Console.Error.WriteLine("ir: warning: features are [gems, error_highlight, did_you_mean, syntax_suggest, rubyopt, frozen_string_literal, yjit, zjit].");
                    break;
            }
        }

        /// <summary>
        /// -I paths are expanded against the current directory (and ~), as File.expand_path does:
        /// $LOAD_PATH holds them absolute whether or not they exist. Symlinks are left alone.
        /// </summary>
        private static string/*!*/ ExpandIncludePath(string/*!*/ path) {
            try {
                if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal)) {
                    string home = Environment.GetEnvironmentVariable("HOME");
                    if (!String.IsNullOrEmpty(home)) {
                        path = home + path.Substring(1);
                    }
                }
                return Path.GetFullPath(path).Replace('\\', '/');
            } catch (Exception) {
                return path;
            }
        }

        /// <summary>
        /// MRI's set_option_encoding_once: an encoding may be named again, but naming a different
        /// one is an error ("-Eascii:ascii -U").
        /// </summary>
        private static void SetEncodingOnce(string/*!*/ type, ref string name, string/*!*/ value) {
            if (name != null && !String.Equals(name, value, StringComparison.OrdinalIgnoreCase)) {
                throw new InvalidOptionException(String.Format("ir: {0} already set to {1} (RuntimeError)", type, name));
            }
            name = value;
        }

        /// <summary>
        /// The value of -E / --encoding: "ext", "ext:", ":int" or "ext:int"; anything after a
        /// second colon is an error.
        /// </summary>
        private void SetEncodings(string/*!*/ option, string/*!*/ value) {
            int colon = value.IndexOf(':');
            string external = colon >= 0 ? value.Substring(0, colon) : value;
            if (external.Length > 0) {
                SetEncodingOnce("default_external", ref _externalEncodingName, external);
            }
            if (colon < 0) {
                return;
            }

            string rest = value.Substring(colon + 1);
            colon = rest.IndexOf(':');
            string internalName = colon >= 0 ? rest.Substring(0, colon) : rest;
            if (internalName.Length > 0) {
                SetEncodingOnce("default_internal", ref _internalEncodingName, internalName);
            }
            if (colon >= 0 && colon + 1 < rest.Length) {
                throw new InvalidOptionException(String.Format("ir: extra argument for {0}: {1} (RuntimeError)", option, rest.Substring(colon + 1)));
            }
        }

        /// <summary>
        /// RUBYOPT holds switches for every ruby run, separated by whitespace. Only the ones that
        /// merely configure the interpreter are allowed there. The command line wins over it for
        /// the warning level and the encodings, and its -I and -r come after the command line's.
        /// </summary>
        private void ProcessRubyOpt(string/*!*/ rubyopt) {
            string[] args = rubyopt.Split(new[] { ' ', '\t', '\n', '\r', '\f', '\v' }, StringSplitOptions.RemoveEmptyEntries);
            if (args.Length == 0) {
                return;
            }

            object verbosity;
            bool verbositySet = LanguageSetup.Options.TryGetValue("Verbosity", out verbosity);
            string external = _externalEncodingName, internalName = _internalEncodingName;
            RubyEncoding sourceEncoding = _defaultEncoding;
            _externalEncodingName = _internalEncodingName = null;
            _defaultEncoding = null;
            // RUBYOPT's -W:category flags go first so that the command line's win.
            var cliWarningFlags = new List<string>(_warningCategoryFlags);
            _warningCategoryFlags.Clear();

            for (int i = 0; i < args.Length; i++) {
                string arg = args[i].StartsWith("-", StringComparison.Ordinal) ? args[i] : "-" + args[i];

                // a switch whose argument is the next word
                if (i + 1 < args.Length) {
                    switch (arg) {
                        case "-I": case "-r": case "-E":
                            arg += args[++i];
                            break;
                        case "--encoding": case "--external-encoding": case "--internal-encoding":
                        case "--enable": case "--disable": case "--backtrace-limit":
                            arg += "=" + args[++i];
                            break;
                    }
                }

                ParseRubyOptSwitch(arg);
            }

            if (verbositySet) {
                LanguageSetup.Options["Verbosity"] = verbosity;
            }
            if (external != null) _externalEncodingName = external;
            if (internalName != null) _internalEncodingName = internalName;
            if (sourceEncoding != null) _defaultEncoding = sourceEncoding;
            _warningCategoryFlags.AddRange(cliWarningFlags);
        }

        private void ParseRubyOptSwitch(string/*!*/ arg) {
            if (arg.StartsWith("--", StringComparison.Ordinal)) {
                int eq = arg.IndexOf('=');
                string name = eq >= 0 ? arg.Substring(0, eq) : arg;
                switch (name) {
                    case "--debug":
                    case "--verbose":
                    case "--encoding":
                    case "--external-encoding":
                    case "--internal-encoding":
                    case "--backtrace-limit":
                    case "--jit":
                    case "--yjit":
                    case "--zjit":
                        break;
                    default:
                        if (!name.StartsWith("--enable", StringComparison.Ordinal) &&
                            !name.StartsWith("--disable", StringComparison.Ordinal) &&
                            !name.StartsWith("--yjit-", StringComparison.Ordinal) &&
                            !name.StartsWith("--zjit-", StringComparison.Ordinal)) {
                            throw InvalidRubyOptSwitch(name);
                        }
                        break;
                }
                if (name == "--jit" || name.StartsWith("--yjit", StringComparison.Ordinal) || name.StartsWith("--zjit", StringComparison.Ordinal)) {
                    return;
                }
                ParseArgument(arg);
                return;
            }

            // single-letter switches, which may be run together: -wd, -wIdir
            while (arg.Length >= 2) {
                char c = arg[1];
                switch (c) {
                    case 'd':
                    case 'v':
                    case 'w':
                    case 'U':
                        ParseArgument(arg.Substring(0, 2));
                        if (arg.Length == 2) {
                            return;
                        }
                        arg = "-" + arg.Substring(2);
                        break;

                    case 'W':
                    case 'I':
                    case 'r':
                    case 'E':
                    case 'K':
                        ParseArgument(arg);
                        return;

                    default:
                        throw InvalidRubyOptSwitch(arg.Substring(0, 2));
                }
            }
        }

        private static Exception/*!*/ InvalidRubyOptSwitch(string/*!*/ name) {
            return new InvalidOptionException(String.Format("ir: invalid switch in RUBYOPT: {0} (RuntimeError)", name));
        }

        private static string[] GetPaths(string input) {
            string[] paths = input.Split(new char[] { Path.PathSeparator }, Int32.MaxValue, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < paths.Length; i++) {
                // Trim any occurrances of "
                string[] parts = paths[i].Split(new char[] { '"' }, Int32.MaxValue, StringSplitOptions.RemoveEmptyEntries);
                paths[i] = String.Concat(parts);
            }
            return paths;
        }

        /// <exception cref="Exception">On error.</exception>
        protected override void ParseArgument(string arg) {
            ContractUtils.RequiresNotNull(arg, "arg");

            string mainFileFromPath = null;

            // The flags that take no argument cluster with whatever follows them, so -ne 'code'
            // is -n -e 'code'. Peeling one off at a time also covers -np, -nal and so on.
            if (arg.Length > 2 && arg[0] == '-' && "nplads".IndexOf(arg[1]) >= 0) {
                ParseArgument(arg.Substring(0, 2));
                ParseArgument("-" + arg.Substring(2));
                return;
            }

            if (arg.StartsWith("-e", StringComparison.Ordinal)) {
                string command;
                if (arg == "-e") {
                    command = PopNextArg();
                } else {
                    command = arg.Substring(2);
                }

                LanguageSetup.Options["MainFile"] = "-e";
                if (CommonConsoleOptions.Command == null) {
                    CommonConsoleOptions.Command = String.Empty;
                } else {
                    CommonConsoleOptions.Command += "\n";
                }
                CommonConsoleOptions.Command += command;
                return;
            }

            // --backtrace-limit=N caps how many frames an uncaught exception prints; Ruby code
            // reads the setting back through Thread::Backtrace.limit.
            if (arg.StartsWith("--backtrace-limit", StringComparison.Ordinal)) {
                string value = (arg == "--backtrace-limit") ? PopNextArg() : arg.Substring("--backtrace-limit".Length).TrimStart('=');
                int limit;
                if (!Int32.TryParse(value, out limit)) {
                    throw new InvalidOptionException(String.Format("invalid argument for --backtrace-limit: {0}", value));
                }
                LanguageSetup.Options["BacktraceLimit"] = limit;
                return;
            }

            if (arg.StartsWith("-S", StringComparison.Ordinal)) {
                mainFileFromPath = arg == "-S" ? PopNextArg() : arg.Substring(2);
            }

            if (arg.StartsWith("-I", StringComparison.Ordinal)) {
                string includePaths;
                if (arg == "-I") {
                    includePaths = PopNextArg();
                } else {
                    includePaths = arg.Substring(2);
                }

                foreach (string path in GetPaths(includePaths)) {
                    _loadPaths.Add(ExpandIncludePath(path));
                }
                return;
            }

            // -Kx names the source encoding of the main program, and the external encoding too
            // unless something already has (MRI). An unknown letter is ignored.
            if (arg.StartsWith("-K", StringComparison.Ordinal)) {
                RubyEncoding encoding = null;
                if (arg.Length >= 3) {
                    encoding = (arg[2] == 'a' || arg[2] == 'A' || arg[2] == 'n' || arg[2] == 'N')
                        ? RubyEncoding.Binary : RubyEncoding.GetEncodingByNameInitial(arg[2]);
                }
                if (encoding != null) {
                    _defaultEncoding = encoding;
                    if (_externalEncodingName == null) {
                        _externalEncodingName = encoding.Name;
                    }
                }
                return;
            }

            // -Eext, -E ext, -Eext:int and the long spellings. Unlike -K this says nothing about
            // how the source is read: it sets Encoding.default_external (and default_internal).
            if (arg.StartsWith("-E", StringComparison.Ordinal) || arg.StartsWith("--encoding", StringComparison.Ordinal)) {
                string value, option;
                if (arg == "-E" || arg == "--encoding") {
                    option = arg;
                    value = PopNextArg();
                } else if (arg.StartsWith("--encoding=", StringComparison.Ordinal)) {
                    option = "--encoding";
                    value = arg.Substring("--encoding=".Length);
                } else if (arg.StartsWith("-E", StringComparison.Ordinal)) {
                    option = "-E";
                    value = arg.Substring(2);
                } else {
                    throw new InvalidOptionException(String.Format("Option '{0}' not supported", arg));
                }

                SetEncodings(option, value);
                return;
            }

            if (arg.StartsWith("--external-encoding", StringComparison.Ordinal)) {
                SetEncodingOnce("default_external", ref _externalEncodingName, (arg == "--external-encoding")
                    ? PopNextArg() : arg.Substring("--external-encoding=".Length));
                return;
            }

            if (arg.StartsWith("--internal-encoding", StringComparison.Ordinal)) {
                SetEncodingOnce("default_internal", ref _internalEncodingName, (arg == "--internal-encoding")
                    ? PopNextArg() : arg.Substring("--internal-encoding=".Length));
                return;
            }

            // -U is -E's internal half on its own: default_internal becomes UTF-8 and the external
            // encoding is left alone.
            if (arg == "-U") {
                SetEncodingOnce("default_internal", ref _internalEncodingName, "UTF-8");
                return;
            }

            // --enable=a,b / --disable=a,b and the hyphenated --enable-a / --disable-a spellings.
            // MRI accepts a comma separated list in the '=' form.
            if (arg.StartsWith("--enable", StringComparison.Ordinal) || arg.StartsWith("--disable", StringComparison.Ordinal)) {
                bool enable = arg.StartsWith("--enable", StringComparison.Ordinal);
                string prefix = enable ? "--enable" : "--disable";
                string features;
                if (arg == prefix) {
                    features = PopNextArg();
                } else if (arg[prefix.Length] == '=' || arg[prefix.Length] == '-') {
                    features = arg.Substring(prefix.Length + 1);
                } else {
                    throw new InvalidOptionException(String.Format("Option '{0}' not supported", arg));
                }

                foreach (var feature in features.Split(',')) {
                    SetFeature(feature.Trim(), enable);
                }
                return;
            }

            if (arg.StartsWith("-r", StringComparison.Ordinal)) {
                _requiredPaths.Add((arg == "-r") ? PopNextArg() : arg.Substring(2));
                return;
            }

            if (arg.StartsWith("-C", StringComparison.Ordinal)) {
                // -Cdir and -C dir are both valid, as for -r above
                ConsoleOptions.ChangeDirectory = (arg == "-C") ? PopNextArg() : arg.Substring(2);
                return;
            }

            // -0 alone means "\0", -0<octal> names the byte, as in -072 for ':'; as in MRI a value of
            // 0 given in digits (-00) is paragraph mode ("") and one of 0400 or more (-0777) is nil.
            if (arg.StartsWith("-0", StringComparison.Ordinal)) {
                int separator = 0;
                for (int i = 2; i < arg.Length; i++) {
                    if (arg[i] < '0' || arg[i] > '7') {
                        throw new InvalidOptionException(String.Format("Option '{0}' not supported", arg));
                    }
                    separator = separator * 8 + (arg[i] - '0');
                }
                if (separator >= 0x100) {
                    LanguageSetup.Options["NoInputRecordSeparator"] = true;
                } else {
                    LanguageSetup.Options["InputRecordSeparator"] = (separator == 0 && arg.Length > 2) ? "" : ((char)separator).ToString();
                }
                return;
            }

            if (arg.StartsWith("-i", StringComparison.Ordinal)) {
                // -i[extension] edits in place; an extension keeps the original as a backup and no
                // extension throws it away. The empty string is a real answer here, so the option
                // has to carry it rather than being absent.
                LanguageSetup.Options["InplaceMode"] = arg.Substring(2);
                return;
            }

            // -Fpattern: $; for -a's split, compiled as a Regexp. An empty -F is ignored, as by MRI.
            if (arg.StartsWith("-F", StringComparison.Ordinal)) {
                if (arg.Length > 2) {
                    LanguageSetup.Options["FieldSeparator"] = arg.Substring(2);
                }
                return;
            }

            // -x[dir]: the script starts at its first #!...ruby line; a directory is cd'ed to first.
            if (arg.StartsWith("-x", StringComparison.Ordinal)) {
                LanguageSetup.Options["SkipToRubyShebang"] = true;
                if (arg.Length > 2) {
                    ConsoleOptions.ChangeDirectory = arg.Substring(2);
                }
                return;
            }

            // -X dir / -Xdir is MRI's other spelling of -C. -X:Name stays IronRuby's own option.
            if (arg.StartsWith("-X", StringComparison.Ordinal) && !arg.StartsWith("-X:", StringComparison.Ordinal)) {
                ConsoleOptions.ChangeDirectory = (arg == "-X") ? PopNextArg() : arg.Substring(2);
                return;
            }

            if (arg == "-s") {
                LanguageSetup.Options["ScriptSwitches"] = true;
                return;
            }

            // "--" ends the interpreter's options: what follows is the script and its arguments,
            // or with -e only arguments.
            if (arg == "--") {
                string[] rest = PopRemainingArgs();
                if (ConsoleOptions.Command != null) {
                    LanguageSetup.Options["MainFile"] = "-e";
                    LanguageSetup.Options["Arguments"] = rest;
                } else if (rest.Length > 0) {
                    ConsoleOptions.FileName = rest[0];
                    LanguageSetup.Options["MainFile"] = RubyUtils.CanonicalizePath(rest[0]);
                    LanguageSetup.Options["Arguments"] = ArrayUtils.ShiftLeft(rest, 1);
                }
                return;
            }

            if (arg.StartsWith("-C", StringComparison.Ordinal) ||
                arg.StartsWith("-T", StringComparison.Ordinal)) {
                throw new InvalidOptionException(String.Format("Option '{0}' not supported", arg));
            }

            int colon = arg.IndexOf(':');
            string optionName, optionValue;
            if (colon >= 0) {
                optionName = arg.Substring(0, colon);
                optionValue = arg.Substring(colon + 1);
            } else {
                optionName = arg;
                optionValue = null;
            }

            switch (optionName) {
                #region Ruby options

                // -c: compile the program and report "Syntax OK" instead of running it
                case "-c":
                    LanguageSetup.Options["CheckSyntaxOnly"] = true;
                    break;

                case "--copyright":
                    throw new InvalidOptionException(String.Format("Option '{0}' not supported", optionName));

                case "-n":
                    LanguageSetup.Options["LoopOverInput"] = true;
                    break;

                case "-p":
                    LanguageSetup.Options["LoopOverInput"] = true;
                    LanguageSetup.Options["PrintEachLine"] = true;
                    break;

                case "-a":
                    LanguageSetup.Options["AutoSplit"] = true;
                    break;

                case "-l":
                    LanguageSetup.Options["ChopLines"] = true;
                    break;

                case "-d":
                case "--debug":
                    LanguageSetup.Options["DebugVariable"] = true; // $DEBUG = true
                    LanguageSetup.Options["Verbosity"] = 2; // and $VERBOSE = true, as in MRI
                    // --debug turns on every debugging aid MRI has, which includes naming the
                    // place a string literal was written.
                    LanguageSetup.Options["DebugFrozenStringLiteral"] = true;
                    break;

                case "--debug-frozen-string-literal":
                    LanguageSetup.Options["DebugFrozenStringLiteral"] = true;
                    break;

                case "--version":
                    ConsoleOptions.PrintVersion = true;
                    ConsoleOptions.Exit = true;
                    break;

                case "-v":
                    ConsoleOptions.DisplayVersion = true;
                    goto case "-W2";

                // -W:deprecated, -W:no-deprecated and the other category switches. The parser has
                // already split the colon off, so optionValue is the category.
                case "-W":
                    if (optionValue == null) {
                        goto case "-W2";
                    }
                    _warningCategoryFlags.Add(optionValue);
                    break;

                // The -W numeric levels also move the category bits, exactly as MRI's
                // proc_W_option does over RB_WARN_CATEGORY_DEFAULT_BITS (deprecated|experimental).
                // They share the ordered list with -W:category so that the later flag wins per bit.
                case "-W0":
                    LanguageSetup.Options["Verbosity"] = 0; // $VERBOSE = nil
                    _warningCategoryFlags.Add("no-deprecated");
                    _warningCategoryFlags.Add("no-experimental");
                    break;

                case "-W1":
                    LanguageSetup.Options["Verbosity"] = 1; // $VERBOSE = false
                    _warningCategoryFlags.Add("no-deprecated");
                    break;

                case "-w":
                case "-W2":
                    LanguageSetup.Options["Verbosity"] = 2; // $VERBOSE = true
                    _warningCategoryFlags.Add("deprecated");
                    _warningCategoryFlags.Add("experimental");
                    break;

                #endregion

#if DEBUG
                case "-DT*":
                    SetTraceFilter(String.Empty, false);
                    break;

                case "-DT":
                    SetTraceFilter(PopNextArg(), false);
                    break;

                case "-ET*":
                    SetTraceFilter(String.Empty, true);
                    break;

                case "-ET":
                    SetTraceFilter(PopNextArg(), true);
                    break;

                case "-ER":
                    RubyOptions.ShowRules = true;
                    break;

                case "-save":
                    LanguageSetup.Options["SavePath"] = optionValue ?? AppDomain.CurrentDomain.BaseDirectory;
                    break;

                case "-load":
                    LanguageSetup.Options["LoadFromDisk"] = ScriptingRuntimeHelpers.True;
                    break;

                case "-useThreadAbortForSyncRaise":
                    RubyOptions.UseThreadAbortForSyncRaise = true;
                    break;

                case "-compileRegexps":
                    RubyOptions.CompileRegexps = true;
                    break;
#endif
                case "-trace":
                    LanguageSetup.Options["EnableTracing"] = ScriptingRuntimeHelpers.True;
                    break;

                case "-profile":
                    LanguageSetup.Options["Profile"] = ScriptingRuntimeHelpers.True;
                    break;

                case "-1.8.6":
                case "-1.8.7":
                case "-1.9":
                case "-2.0":
                    throw new InvalidOptionException(String.Format("Option '{0}' is no longer supported. The compatible Ruby version is 1.9.", optionName));

                case "-X":
                    switch (optionValue) {
                        case "AutoIndent":
                        case "TabCompletion":
                        case "ColorfulConsole":
                            throw new InvalidOptionException(String.Format("Option '{0}' not supported", optionName));

                        case "ObjectSpace":
                            LanguageSetup.Options["ObjectSpace"] = ScriptingRuntimeHelpers.True;
                            return;

                        case "JIT":
                            LanguageSetup.Options["JIT"] = ScriptingRuntimeHelpers.True;
                            return;

                        case "NoJIT":
                            LanguageSetup.Options["JIT"] = ScriptingRuntimeHelpers.False;
                            return;

                        case "OSR":
                            LanguageSetup.Options["OSR"] = ScriptingRuntimeHelpers.True;
                            return;

                        case "NoOSR":
                            LanguageSetup.Options["OSR"] = ScriptingRuntimeHelpers.False;
                            return;
                    }

                    // -X:StdLib=dir[:dir...] names the standard library directories. They go on
                    // $LOAD_PATH after -I, RUBYOPT's -I and RUBYLIB, where MRI keeps its own.
                    if (optionValue != null && optionValue.StartsWith("StdLib=", StringComparison.Ordinal)) {
                        foreach (string path in GetPaths(optionValue.Substring("StdLib=".Length))) {
                            if (!_stdLibPaths.Contains(path)) {
                                _stdLibPaths.Add(path);
                            }
                        }
                        return;
                    }
                    goto default;
                    
               default:
                    base.ParseArgument(arg);

                    if (ConsoleOptions.FileName != null) {
                        if (mainFileFromPath != null) {
                            ConsoleOptions.FileName = FindMainFileFromPath(mainFileFromPath);
                        }

                        if (ConsoleOptions.Command == null) {
                            SetupOptionsForMainFile();
                        } else {
                            SetupOptionsForCommand();
                        }
                    } 
                    break;
            }
        }

        private void SetupOptionsForMainFile() {
            // "-" is stdin, not a path: MRI leaves $0 spelled exactly that way.
            LanguageSetup.Options["MainFile"] = ConsoleOptions.FileName == "-"
                ? "-"
                : RubyUtils.CanonicalizePath(ConsoleOptions.FileName);
            LanguageSetup.Options["Arguments"] = PopRemainingArgs();;
        }

        private void SetupOptionsForCommand() {
            string firstArg = ConsoleOptions.FileName;
            ConsoleOptions.FileName = null;

            List<string> args = new List<string>(new string[] { firstArg });
            args.AddRange(PopRemainingArgs());

            LanguageSetup.Options["MainFile"] = "-e";
            LanguageSetup.Options["Arguments"] = args.ToArray();
        }

        /// <summary>
        /// -S looks for the script in RUBYPATH and then in PATH; a name found in neither is taken
        /// as it is.
        /// </summary>
        private string FindMainFileFromPath(string mainFileFromPath) {
            foreach (string variable in new[] { "RUBYPATH", "PATH" }) {
                string path = Platform.GetEnvironmentVariable(variable);
                if (String.IsNullOrEmpty(path)) {
                    continue;
                }
                foreach (string p in path.Split(Path.PathSeparator)) {
                    if (p.Length == 0) {
                        continue;
                    }
                    string fullPath = RubyUtils.CombinePaths(p, mainFileFromPath);
                    if (Platform.FileExists(fullPath)) {
                        return fullPath;
                    }
                }
            }
            return mainFileFromPath;
        }

        protected override void AfterParse() {
            if (!_disableRubyOpt) {
                try {
                    string rubyopt = Environment.GetEnvironmentVariable("RUBYOPT");
                    if (rubyopt != null) {
                        ProcessRubyOpt(rubyopt);
                    }
                } catch (SecurityException) {
                    // nop
                }
            }

            var existingSearchPaths =
                LanguageOptions.GetSearchPathsOption(LanguageSetup.Options) ??
                LanguageOptions.GetSearchPathsOption(RuntimeSetup.Options);

            if (existingSearchPaths != null) {
                _loadPaths.InsertRange(0, existingSearchPaths);
            }

            try {
                string rubylib = Environment.GetEnvironmentVariable("RUBYLIB");
                if (rubylib != null) {
                    _loadPaths.AddRange(GetPaths(rubylib));
                }
            } catch (SecurityException) {
                // nop
            }
            _loadPaths.AddRange(_stdLibPaths);
            LanguageSetup.Options["SearchPaths"] = _loadPaths;

            if (!_disableRubyGems) {
                if (_enableDidYouMean) {
                    _requiredPaths.Insert(0, "did_you_mean");
                }
                _requiredPaths.Insert(0, "gem_prelude.rb");
            } else {
                // gem_prelude.rb ends by requiring ruby4.rb, the Ruby half of the core library
                // (Set, pattern matching, ...). --disable-gems only turns off RubyGems in MRI,
                // so the core layer is still loaded.
                _requiredPaths.Insert(0, "ruby4.rb");
            }

            LanguageSetup.Options["RequiredPaths"] = _requiredPaths;

            LanguageSetup.Options["DefaultEncoding"] = _defaultEncoding;
            LanguageSetup.Options["WarningCategoryFlags"] = _warningCategoryFlags;
            LanguageSetup.Options["ExternalEncoding"] = _externalEncodingName;
            LanguageSetup.Options["InternalEncoding"] = _internalEncodingName;
            LanguageSetup.Options["LocaleEncoding"] = _defaultEncoding ?? GetLocaleEncoding();

#if DEBUG
            // Can be set to nl-BE, ja-JP, etc
            string culture = Environment.GetEnvironmentVariable("IR_CULTURE");
            if (culture != null) {
                System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo(culture, false);
            }
#endif
            if (ConsoleOptions.DisplayVersion && ConsoleOptions.Command == null && ConsoleOptions.FileName == null) {
                ConsoleOptions.PrintVersion = true;
                ConsoleOptions.Exit = true;
            }

            // `ruby` with neither -e nor a script reads the program from stdin until EOF, as if "-"
            // had been given, and $0 is "-" (MRI, JRuby); so does `ironruby` when stdin is a pipe
            // or a file. At a terminal it starts irb instead, as `python` starts its interactive
            // interpreter there: `ironruby` alone is then `iirb`, the -S irb that ironruby.cmd and
            // ironruby find in RUBYPATH. An explicit "-", and -n, -p, -c and -x, which are about
            // stdin, still read it. ironrubyw, which has no console, reads nothing and exits, as
            // rubyw does. Only an irb found in RUBYPATH or PATH runs: FindMainFileFromPath hands
            // back a name it did not find unchanged, and that would be a file called irb in the
            // current directory.
            if (!ConsoleOptions.Exit && ConsoleOptions.Command == null && ConsoleOptions.FileName == null) {
                string irb = StartsInteractive() ? FindMainFileFromPath("irb") : null;
                if (irb != null && irb != "irb" && Platform.FileExists(irb)) {
                    ConsoleOptions.FileName = irb;
                    SetupOptionsForMainFile();
                } else {
                    ConsoleOptions.FileName = "-";
                    LanguageSetup.Options["MainFile"] = "-";
                }
            }
        }

        /// <summary>
        /// Whether `ironruby` with no program should be interactive: stdin is a terminal (a
        /// console on Windows, which .NET tells apart from NUL), and no option asked for stdin.
        /// </summary>
        private bool StartsInteractive() {
            if (LanguageSetup.Options.ContainsKey("LoopOverInput") || LanguageSetup.Options.ContainsKey("CheckSyntaxOnly") ||
                LanguageSetup.Options.ContainsKey("SkipToRubyShebang")) {
                return false;
            }
            try {
                return !Console.IsInputRedirected;
            } catch (IOException) {
                return false;
            }
        }

        /// <summary>
        /// The C (or POSIX) locale named in LC_ALL, LC_CTYPE or LANG - the first one set wins, as
        /// in setlocale(3) - has the ASCII codeset, which is what MRI takes as the locale
        /// encoding. .NET would answer UTF-8 for it.
        /// </summary>
        private static RubyEncoding/*!*/ GetLocaleEncoding() {
            if (Environment.OSVersion.Platform == PlatformID.Unix) {
                foreach (var name in new[] { "LC_ALL", "LC_CTYPE", "LANG" }) {
                    string value = Environment.GetEnvironmentVariable(name);
                    if (!String.IsNullOrEmpty(value)) {
                        if (value == "C" || value == "POSIX") {
                            return RubyEncoding.Ascii;
                        }
                        break;
                    }
                }
            }
            return RubyEncoding.GetRubyEncoding(Console.InputEncoding);
        }

        public override void GetHelp(out string commandLine, out string[,] options, out string[,] environmentVariables, out string comments) {
            commandLine = "[options] [file] [arguments]";
            environmentVariables = null;
            comments = null;

            options = new string[,] {
             // { "-0[octal]",                   "specify record separator (\0, if no argument)" },
             // { "-a",                          "autosplit mode with -n or -p (splits $_ into $F)" },
             // { "-c",                          "check syntax only" },
                { "-Cdirectory",                 "cd to directory, before executing your script" },
                { "-d",                          "set debugging flags (set $DEBUG to true)" },
                { "-D",                          "emit debugging information (PDBs) for Visual Studio debugger" },
                { "-e 'command'",                "one line of script. Several -e's allowed. Omit [file]" },
             // { "-Fpattern",                   "split() pattern for autosplit (-a)" },
                { "-h[elp]",                     "Display usage" },
             // { "-i[extension]",               "edit ARGV files in place (make backup if extension supplied)" },
                { "-Idirectory",                 "specify $LOAD_PATH directory (may be used more than once)" },
                { "-Kkcode",                     "specifies KANJI (Japanese) code-set" },
             // { "-l",                          "enable line ending processing" },
             // { "-n",                          "assume 'while gets(); ... end' loop around your script" },
             // { "-p",                          "assume loop like -n but print line also like sed" },
                { "-rlibrary",                   "require the library, before executing your script" },
             // { "-s",                          "enable some switch parsing for switches after script name" },
                { "-S",                          "look for the script using PATH environment variable" },
             // { "-T[level]",                   "turn on tainting checks" },
                { "-v",                          "print version number, then turn on verbose mode" },
                { "-w",                          "turn warnings on for your script" },
                { "-W[level]",                   "set warning level; 0=silence, 1=medium (default), 2=verbose" },
             // { "-x[directory]",               "strip off text before #!ruby line and perhaps cd to directory" },
             // { "--copyright",                 "print the copyright" },
                { "--version",                   "print the version" },

                { "-trace",                      "enable support for set_trace_func" },
                { "-profile",                    "enable support for 'pi = IronRuby::Clr.profile { block_to_profile }'" },
                
                { "-X:ExceptionDetail",          "enable ExceptionDetail mode" },
                { "-X:ObjectSpace",              "let ObjectSpace.each_object find objects, not just modules (slows down Object#new)" },
                { "-X:NoJIT",                    "disable the method JIT (type-specialized method bodies), which is on by default" },
                { "-X:NoOSR",                    "disable on-stack replacement (specializing a loop while it runs), on by default" },
                { "-X:NoAdaptiveCompilation",    "disable adaptive compilation - all code will be compiled" },
                { "-X:CompilationThreshold",     "the number of iterations before the interpreter starts compiling" },
                { "-X:PassExceptions",           "do not catch exceptions that are unhandled by script code" },
                { "-X:PrivateBinding",           "enable binding to private members" },
                { "-X:ShowClrExceptions",        "display CLS Exception information" },
                { "-X:RemoteRuntimeChannel",     "remote console channel" }, 
             // { "-X:AutoIndent",               "Enable auto-indenting in the REPL loop" },
             // { "-X:TabCompletion",            "Enable TabCompletion mode" },
             // { "-X:ColorfulConsole",          "Enable ColorfulConsole" },

#if DEBUG
                { "-DT",                         "disables tracing of specified events [debug only]" },
                { "-DT*",                        "disables tracing of all events [debug only]" },
                { "-ET",                         "enables tracing of specified events [debug only]" },
                { "-ET*",                        "enables tracing of all events [debug only]" },
                { "-save [path]",                "save generated code to given path [debug only]" },
                { "-load",                       "load pre-compiled code [debug only]" },
                { "-useThreadAbortForSyncRaise", "for testing purposes [debug only]" },
                { "-compileRegexps",             "faster throughput, slower startup [debug only]" },
                { "-X:AssembliesDir <dir>",      "set the directory for saving generated assemblies [debug only]" },
                { "-X:SaveAssemblies",           "save generated assemblies [debug only]" },
                { "-X:TrackPerformance",         "track performance sensitive areas [debug only]" },
                { "-X:PerfStats",                "print performance stats when the process exists [debug only]" },
#endif
            };
        }
    }
}
