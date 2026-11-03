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
using Microsoft.Scripting;
using Microsoft.Scripting.Utils;
using IronRuby.Builtins;
using System.Text;
using System.Diagnostics;

namespace IronRuby.Runtime {
    internal sealed class SpecialGlobalVariableInfo : GlobalVariable {
        private readonly GlobalVariableId _id;

        internal SpecialGlobalVariableInfo(GlobalVariableId id) {
            _id = id;
        }

        /// <summary>
        /// $&lt; is ARGF, which the standard library defines in Ruby - later than the context is
        /// built, so the constant is read when the variable is asked for rather than cached at
        /// startup. The object the input provider was given stays as the answer for a context
        /// that has no standard library loaded at all.
        /// </summary>
        private static object/*!*/ GetArgf(RubyContext/*!*/ context) {
            object argf;
            if (context.ObjectClass.TryGetConstant(null, "ARGF", out argf)) {
                return argf;
            }
            return context.InputProvider.Singleton;
        }

        /// <summary>
        /// $FILENAME is documented as ARGF.filename and has to be asked of it, since ARGF is what
        /// knows which file is being read.
        /// </summary>
        private static object GetCurrentFileName(RubyContext/*!*/ context) {
            object argf;
            if (context.ObjectClass.TryGetConstant(null, "ARGF", out argf)) {
                var site = context.ArgfFileNameSite;
                return site.Target(site, argf);
            }
            return context.InputProvider.CurrentFileName;
        }

        public override object GetValue(RubyContext/*!*/ context, RubyScope scope) {
            switch (_id) {
                
                // regular expressions:
                case GlobalVariableId.MatchData:
                    return (scope != null) ? scope.GetInnerMostClosureScope().CurrentMatch : null;

                case GlobalVariableId.MatchLastGroup:
                    return (scope != null) ? scope.GetInnerMostClosureScope().GetCurrentMatchLastGroup() : null;

                case GlobalVariableId.PreMatch:
                    return (scope != null) ? scope.GetInnerMostClosureScope().GetCurrentPreMatch() : null;

                case GlobalVariableId.PostMatch:
                    return (scope != null) ? scope.GetInnerMostClosureScope().GetCurrentPostMatch() : null;

                case GlobalVariableId.EntireMatch:
                    return (scope != null) ? scope.GetInnerMostClosureScope().GetCurrentMatchGroup(0) : null;


                // exceptions:
                case GlobalVariableId.CurrentException:
                    return context.CurrentException;

                case GlobalVariableId.CurrentExceptionBacktrace:
                    return context.GetCurrentExceptionBacktrace();


                // input:
                case GlobalVariableId.InputContent:
                    return GetArgf(context);

                case GlobalVariableId.InputFileName:
                    return GetCurrentFileName(context);

                case GlobalVariableId.LastInputLine:
                    return (scope != null) ? scope.GetInnerMostClosureScope().LastInputLine : null;

                case GlobalVariableId.LastInputLineNumber:
                    return context.InputProvider.LastInputLineNumber;

                case GlobalVariableId.CommandLineArguments:
                    return context.InputProvider.CommandLineArguments;


                // output:
                case GlobalVariableId.OutputStream:
                    return context.StandardOutput;

                case GlobalVariableId.ErrorOutputStream:
                    return context.StandardErrorOutput;

                case GlobalVariableId.InputStream:
                    return context.StandardInput;


                // separators:
                case GlobalVariableId.InputSeparator:
                    return context.InputSeparator;

                case GlobalVariableId.OutputSeparator:
                    return context.OutputSeparator;

                case GlobalVariableId.StringSeparator:
                    return context.StringSeparator;

                case GlobalVariableId.ItemSeparator:
                    return context.ItemSeparator;

                
                // loader:
                case GlobalVariableId.LoadPath:
                    return context.Loader.LoadPaths;

                case GlobalVariableId.LoadedFiles:
                    return context.Loader.LoadedFiles;


                // misc:
                case GlobalVariableId.SafeLevel:
                    return context.CurrentSafeLevel;

                case GlobalVariableId.Verbose:
                    return context.Verbose;

                case GlobalVariableId.WarningLevel:
                    // $-W: 0 for $VERBOSE nil, 1 for false, 2 for true
                    return context.Verbose == null ? 0 : (RubyOps.IsTrue(context.Verbose) ? 2 : 1);

                case GlobalVariableId.KCode:
                    context.ReportWarning("variable $KCODE is no longer effective");
                    return null;

                case GlobalVariableId.IgnoreCase:
                    context.ReportDeprecationWarning("variable $= is no longer effective");
                    return false;

                case GlobalVariableId.ChildProcessExitStatus:
                    return context.ChildProcessExitStatus;

                case GlobalVariableId.CommandLineProgramPath:
                    return context.CommandLineProgramPath;

                default:
                    throw Assert.Unreachable;
            }
        }

        public override void SetValue(RubyContext/*!*/ context, RubyScope scope, string/*!*/ name, object value) {
            switch (_id) {
                // regex:
                case GlobalVariableId.MatchData:
                    if (scope == null) {
                        throw ReadOnlyError(name);
                    }

                    if (value != null && !(value is MatchData)) {
                        throw RubyExceptions.CreateTypeError(String.Format("wrong argument type {0} (expected MatchData)",
                            context.GetClassDisplayName(value)));
                    }
                    scope.GetInnerMostClosureScope().CurrentMatch = (MatchData)value;
                    return;

                case GlobalVariableId.MatchLastGroup:
                case GlobalVariableId.PreMatch:
                case GlobalVariableId.PostMatch:
                case GlobalVariableId.EntireMatch:
                    throw ReadOnlyError(name);
                
                
                // exceptions:
                case GlobalVariableId.CurrentException:
                    // read-only since Ruby 4.0; the interpreter still sets it internally
                    throw ReadOnlyError(name);

                case GlobalVariableId.CurrentExceptionBacktrace:
                    context.SetCurrentExceptionBacktrace(value);
                    return;


                // input:
                case GlobalVariableId.LastInputLine:
                    if (scope == null) {
                        throw ReadOnlyError(name);
                    }
                    scope.GetInnerMostClosureScope().LastInputLine = value;
                    return;

                case GlobalVariableId.LastInputLineNumber:
                    context.InputProvider.LastInputLineNumber = context.CastToFixnum(value);
                    return;

                case GlobalVariableId.CommandLineArguments:
                case GlobalVariableId.InputFileName:
                    throw ReadOnlyError(name);

                // output:
                case GlobalVariableId.OutputStream:
                    context.StandardOutput = RequireWriteProtocol(context, value, name);
                    return;

                case GlobalVariableId.ErrorOutputStream:
                    context.StandardErrorOutput = RequireWriteProtocol(context, value, name);
                    break;

                case GlobalVariableId.InputStream:
                    context.StandardInput = value;
                    return;

                // separators:
                case GlobalVariableId.InputContent:
                    throw ReadOnlyError(name);

                case GlobalVariableId.InputSeparator:
                    context.InputSeparator = RequireInputSeparator(context, value, name);
                    ReportNonNilDeprecation(context, name, context.InputSeparator);
                    return;

                case GlobalVariableId.OutputSeparator:
                    // unlike $/, the output separator keeps the very string it was given
                    context.OutputSeparator = (value != null) ? RequireType<MutableString>(value, name, "String") : null;
                    ReportNonNilDeprecation(context, name, context.OutputSeparator);
                    return;

                case GlobalVariableId.StringSeparator:
                    if (value != null && !(value is MutableString) && !(value is RubyRegex)) {
                        throw RubyExceptions.CreateTypeError(String.Format("value of ${0} must be String or Regexp", name));
                    }
                    context.StringSeparator = value;
                    ReportNonNilDeprecation(context, name, context.StringSeparator);
                    return;

                case GlobalVariableId.ItemSeparator:
                    context.ItemSeparator = (value != null) ? RequireType<MutableString>(value, name, "String") : null;
                    ReportNonNilDeprecation(context, name, context.ItemSeparator);
                    return;


                // loader:
                case GlobalVariableId.LoadedFiles:
                case GlobalVariableId.LoadPath:
                    throw ReadOnlyError(name);


                // misc:
                case GlobalVariableId.SafeLevel:
                    context.SetSafeLevel(RequireType<int>(value, name, "Integer"));
                    return;

                case GlobalVariableId.Verbose:
                    // nil means "no warnings at all"; anything else truthy is plain true
                    context.Verbose = (value == null) ? null : (object)RubyOps.IsTrue(value);
                    return;

                case GlobalVariableId.WarningLevel:
                    throw ReadOnlyError(name);

                case GlobalVariableId.CommandLineProgramPath:
                    context.CommandLineProgramPath = context.CastToString(value);
                    SetProcessTitle(context.CommandLineProgramPath.ToByteArray());
                    return;
                
                case GlobalVariableId.KCode:
                    context.ReportWarning("variable $KCODE is no longer effective");
                    return;

                case GlobalVariableId.IgnoreCase:
                    // MRI keeps $= readable but discards writes; the assignment expression still
                    // evaluates to the assigned value by plain assignment semantics.
                    context.ReportDeprecationWarning("variable $= is no longer effective; ignored");
                    return;

                case GlobalVariableId.ChildProcessExitStatus:
                    throw ReadOnlyError(name);
                    
                default:
                    throw Assert.Unreachable;
            }
        }
    
        /// <summary>
        /// MRI's rb_deprecated_str_setter: after the type check, a non-nil value for one of the
        /// separator globals is deprecated. The name is the one that was assigned, so $-0 reports
        /// itself rather than $/ even though both write the same slot.
        /// </summary>
        private static void ReportNonNilDeprecation(RubyContext/*!*/ context, string/*!*/ name, object newValue) {
            if (newValue != null) {
                context.ReportDeprecationWarning(String.Format("non-nil '${0}' is deprecated", name));
            }
        }

        /// <summary>
        /// Assigning $0 retitles the process, as MRI's setproctitle does: on Linux the title
        /// overwrites the argument area that /proc/PID/cmdline (and so ps) shows, padded with NULs
        /// and cut to that area's size. Best effort - nothing happens where that isn't possible.
        /// </summary>
        internal static void SetProcessTitle(byte[]/*!*/ title) {
            if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux)) {
                return;
            }

            try {
                // arg_start and arg_end are fields 48 and 49 of /proc/self/stat; the command name
                // (field 2) is in parentheses and may contain spaces, so count from the last ')'.
                string stat = System.IO.File.ReadAllText("/proc/self/stat");
                string[] fields = stat.Substring(stat.LastIndexOf(')') + 2).Split(' ');
                long argStart = Int64.Parse(fields[48 - 3]);
                long argEnd = Int64.Parse(fields[49 - 3]);
                int size = (int)Math.Min(argEnd - argStart, 4096);
                if (argStart <= 0 || size <= 1) {
                    return;
                }

                var buffer = new byte[size];
                Array.Copy(title, buffer, Math.Min(title.Length, size - 1));
                using (var mem = new System.IO.FileStream("/proc/self/mem", System.IO.FileMode.Open, System.IO.FileAccess.Write)) {
                    mem.Seek(argStart, System.IO.SeekOrigin.Begin);
                    mem.Write(buffer, 0, buffer.Length);
                }
            } catch (Exception) {
                // not permitted or not available
            }
        }

        private object RequireWriteProtocol(RubyContext/*!*/ context, object value, string/*!*/ variableName) {
            if (!context.RespondTo(value, "write")) {
                throw RubyExceptions.CreateTypeError(String.Format("${0} must have write method, {1} given", variableName, context.GetClassDisplayName(value)));
            }

            return value;
        }
    }
}
