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
using Microsoft.Scripting;
using IronRuby.Runtime;
using IronRuby.Compiler;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace IronRuby.Builtins {
    using BinaryOpStorageWithScope = CallSiteStorage<Func<CallSite, RubyScope, object, object, object>>;
    using System.Globalization;

    [RubyClass("Symbol", Extends = typeof(RubySymbol), Inherits = typeof(Object))]
    [HideMethod("==")]
    // a Symbol is never constructed, and MRI says so with a NoMethodError rather than by
    // letting the default allocator raise a TypeError
    [UndefineMethod("new", IsStatic = true)]
    public static class SymbolOps {

        #region to_s, inspect, to_sym, to_clr_string, to_proc

        [RubyMethod("id2name")]
        /// <summary>
        /// A fresh String each time, chilled since Ruby 3.4: mutating it warns, because a future
        /// version means to hand back the symbol's own frozen string instead.
        /// </summary>
        [RubyMethod("to_s")]
        public static MutableString/*!*/ ToString(RubySymbol/*!*/ self) {
            return MutableString.ChillAsSymbolString(self.String.Clone());
        }

        [RubyMethod("inspect")]
        public static MutableString/*!*/ Inspect(RubyContext/*!*/ context, RubySymbol/*!*/ self) {
            var str = self.ToString();
            var result = self.String.Clone();

            // A symbol whose bytes cannot be written out as they stand is quoted whatever its
            // name looks like: :"foo" for a UTF-16 symbol, :"foo\xA4" for a binary one. MRI's
            // rule here is the encoding's, not the name's - a UTF-8 :привет stays bare, as long
            // as UTF-8 is the encoding inspect results are shown in (default_internal, or else
            // default_external); a Windows-31J symbol is bare when that is Windows-31J.
            RubyEncoding display = context.DefaultInternalEncoding ?? context.DefaultExternalEncoding;
            if (!self.String.IsAscii() && (self.String.Encoding != display || !self.String.Encoding.IsAsciiIdentity ||
                self.String.Encoding == RubyEncoding.Binary)) {
                result = MutableStringOps.Inspect(context, self.String);
                result.Insert(0, ':');
                return result;
            }

            // simple cases:
            if (
                Tokenizer.IsMethodName(str) ||
                Tokenizer.IsConstantName(str) ||
                Tokenizer.IsInstanceVariableName(str) ||
                Tokenizer.IsClassVariableName(str) ||
                Tokenizer.IsGlobalVariableName(str) ||
                IsCommandLineOptionGlobal(str)
            ) {
                result.Insert(0, ':');
            } else {
                // TODO: this is neither efficient nor complete.
                // Any string that parses as 'sym' should not be quoted.
                switch (str) {
                    case null:
                        // Ruby doesn't allow empty symbols, we can get one from outside though:
                        return MutableString.CreateAscii(":\"\"");

                    case "!":
                    case "|":
                    case "^":
                    case "&":
                    case "<=>":
                    case "==":
                    case "===":
                    case "=~":
                    case "!=":
                    case "!~":
                    case ">":
                    case ">=":
                    case "<":
                    case "<=":
                    case "<<":
                    case ">>":
                    case "+":
                    case "-":
                    case "*":
                    case "/":
                    case "%":
                    case "**":
                    case "~":
                    case "+@":
                    case "-@":
                    case "[]":
                    case "[]=":
                    case "`":

                    case "$!":
                    case "$@":
                    case "$,":
                    case "$;":
                    case "$/":
                    case "$\\":
                    case "$*":
                    case "$$":
                    case "$?":
                    case "$=":
                    case "$:":
                    case "$\"":
                    case "$<":
                    case "$>":
                    case "$.":
                    case "$~":
                    case "$&":
                    case "$`":
                    case "$'":
                    case "$+":
                        result.Insert(0, ':');
                        break;

                    default:
                        // MRI quotes a symbol that is not a bare name the same way it quotes a
                        // string, escapes and all, so a symbol carrying a control character does
                        // not write that byte out raw in the middle of someone's output.
                        result = MutableStringOps.Inspect(context, self.String);
                        result.Insert(0, ':');
                        break;
                }
            }

            if (context.RuntimeId != self.RuntimeId) {
                result.Append(" @").Append(self.RuntimeId.ToString(CultureInfo.InvariantCulture));
            }

            return result;
        }

        /// <summary>
        /// The command-line-option globals - $-w, $-d, $-0, $-I - are global variable names that
        /// the tokenizer's IsGlobalVariableName does not recognise, so :"$-w" inspects as :$-w.
        /// </summary>
        private static bool IsCommandLineOptionGlobal(string name) {
            return name != null && name.Length == 3 && name[0] == '$' && name[1] == '-'
                && (Char.IsLetterOrDigit(name[2]) || name[2] == '_');
        }

        [RubyMethod("to_sym")]
        [RubyMethod("intern", Compatibility = RubyCompatibility.Ruby19)]
        public static RubySymbol/*!*/ ToSymbol(RubySymbol/*!*/ self) {
            return self;
        }

        [RubyMethod("to_clr_string")]
        public static string/*!*/ ToClrString(RubySymbol/*!*/ self) {
            return self.ToString();
        }

        [RubyMethod("to_proc")]
        public static Proc/*!*/ ToProc(RubyScope/*!*/ scope, RubySymbol/*!*/ self) {
            return self.MethodInvoker ?? (self.MethodInvoker = Proc.CreateMethodInvoker(scope, self.ToString()));
        }

        #endregion

        #region <=>, ==, ===, casecmp

        [RubyMethod("<=>")]
        public static int Compare(RubySymbol/*!*/ self, [NotNull]RubySymbol/*!*/ other) {
            return Math.Sign(self.CompareTo(other));
        }

        [RubyMethod("<=>")]
        public static int Compare(RubyContext/*!*/ context, RubySymbol/*!*/ self, [NotNull]ClrName/*!*/ other) {
            return -ClrNameOps.Compare(context, other, self);
        }

        [RubyMethod("<=>")]
        public static object Compare(RubySymbol/*!*/ self, object other) {
            return null;
        }

        [RubyMethod("==")]
        [RubyMethod("===")]
        public static bool Equals(RubySymbol/*!*/ lhs, [NotNull]RubySymbol/*!*/ rhs) {
            return lhs.Equals(rhs);
        }

        [RubyMethod("==")]
        [RubyMethod("===")]
        public static bool Equals(RubyContext/*!*/ context, RubySymbol/*!*/ lhs, [NotNull]ClrName/*!*/ rhs) {
            return ClrNameOps.IsEqual(context, rhs, lhs);
        }

        [RubyMethod("==")]
        [RubyMethod("===")]
        public static bool Equals(RubySymbol/*!*/ self, object other) {
            return false;
        }

        // RubySymbol.Equals(object) still treats an equal MutableString as equal (1.8 interop
        // legacy), which would make :a.eql?("a") true and let a String find a Symbol key in a
        // Hash. Ruby's eql? is strict about the type.
        [RubyMethod("eql?")]
        public static bool Eql(RubySymbol/*!*/ self, [NotNull]RubySymbol/*!*/ other) {
            return self.Equals(other);
        }

        [RubyMethod("eql?")]
        public static bool Eql(RubySymbol/*!*/ self, object other) {
            return false;
        }

        [RubyMethod("casecmp")]
        public static object Casecmp(RubySymbol/*!*/ self, [NotNull]RubySymbol/*!*/ other) {
            return ScriptingRuntimeHelpers.Int32ToObject(MutableStringOps.Casecmp(self.String, other.String));
        }

        // Only a Symbol is comparable with a Symbol - :abc.casecmp("abc") is nil, not 0, and
        // no conversion is attempted on anything else either (CRuby 4.0.6).
        [RubyMethod("casecmp")]
        public static object Casecmp(RubySymbol/*!*/ self, object other) {
            return null;
        }

        #endregion

        #region =~, match

        [RubyMethod("=~", Compatibility = RubyCompatibility.Ruby19)]
        public static object Match(RubyScope/*!*/ scope, RubySymbol/*!*/ self, [NotNull]RubyRegex/*!*/ regex) {
            return MutableStringOps.Match(scope, self.String.Clone(), regex);
        }

        [RubyMethod("=~", Compatibility = RubyCompatibility.Ruby19)]
        public static object Match(ClrName/*!*/ self, [NotNull]RubySymbol/*!*/ str) {
            throw RubyExceptions.CreateTypeError("type mismatch: Symbol given");
        }

        [RubyMethod("=~", Compatibility = RubyCompatibility.Ruby19)]
        public static object Match(BinaryOpStorageWithScope/*!*/ storage, RubyScope/*!*/ scope, RubySymbol/*!*/ self, object obj) {
            return MutableStringOps.Match(storage, scope, self.String.Clone(), obj);
        }

        [RubyMethod("match", Compatibility = RubyCompatibility.Ruby19)]
        public static object Match(BinaryOpStorageWithScope/*!*/ storage, RubyScope/*!*/ scope, [Optional]BlockParam block,
            RubySymbol/*!*/ self, [NotNull]RubyRegex/*!*/ regex) {
            return MutableStringOps.Match(storage, scope, block, self.String.Clone(), regex);
        }

        [RubyMethod("match", Compatibility = RubyCompatibility.Ruby19)]
        public static object Match(BinaryOpStorageWithScope/*!*/ storage, RubyScope/*!*/ scope, [Optional]BlockParam block,
            RubySymbol/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ pattern) {
            return MutableStringOps.Match(storage, scope, block, self.String.Clone(), pattern);
        }

        // Delegating from Ruby would put an extra frame between the =~ a Regexp prefix performs
        // and the caller, and $~ is frame-local, so Regexp.last_match would come back nil.
        [RubyMethod("start_with?")]
        public static bool StartsWith(ConversionStorage<MutableString>/*!*/ stringCast, RubyScope/*!*/ scope,
            RubySymbol/*!*/ self, [NotNull]params object/*!*/[]/*!*/ prefixes) {
            return MutableStringOps.StartsWith(stringCast, scope, self.String, prefixes);
        }

        [RubyMethod("end_with?")]
        public static bool EndsWith(ConversionStorage<MutableString>/*!*/ stringCast, RubyScope/*!*/ scope,
            RubySymbol/*!*/ self, [NotNull]params object/*!*/[]/*!*/ suffixes) {
            return MutableStringOps.EndsWith(stringCast, scope, self.String, suffixes);
        }

        /// <summary>The same frozen String every time: :sym.name.equal?(:sym.name).</summary>
        [RubyMethod("name")]
        public static MutableString/*!*/ Name(RubySymbol/*!*/ self) {
            return self.FrozenName;
        }

        #endregion

        #region slice, []

        [RubyMethod("[]")]
        [RubyMethod("slice")]
        public static MutableString GetChar(RubySymbol/*!*/ self, [DefaultProtocol]int index) {
            return MutableStringOps.GetChar(self.String, index);
        }

        [RubyMethod("[]")]
        [RubyMethod("slice")]
        public static MutableString GetSubstring(RubySymbol/*!*/ self, [DefaultProtocol]int start, [DefaultProtocol]int count) {
            return MutableStringOps.GetSubstring(self.String, start, count);
        }

        [RubyMethod("[]")]
        [RubyMethod("slice")]
        public static MutableString GetSubstring(ConversionStorage<int>/*!*/ fixnumCast, RubySymbol/*!*/ self, [NotNull]Range/*!*/ range) {
            return MutableStringOps.GetSubstring(fixnumCast, self.String, range);
        }

        [RubyMethod("[]")]
        [RubyMethod("slice")]
        public static MutableString GetSubstring(RubySymbol/*!*/ self, [NotNull]MutableString/*!*/ searchStr) {
            return MutableStringOps.GetSubstring(self.String, searchStr);
        }

        [RubyMethod("[]")]
        [RubyMethod("slice")]
        public static MutableString GetSubstring(RubyScope/*!*/ scope, RubySymbol/*!*/ self, [NotNull]RubyRegex/*!*/ regex) {
            return MutableStringOps.GetSubstring(scope, self.String, regex);
        }

        [RubyMethod("[]")]
        [RubyMethod("slice")]
        public static MutableString GetSubstring(RubyScope/*!*/ scope, RubySymbol/*!*/ self, [NotNull]RubyRegex/*!*/ regex, [DefaultProtocol]int occurrance) {
            return MutableStringOps.GetSubstring(scope, self.String, regex, occurrance);
        }

        #endregion

        #region empty?, encoding, size/length

        // encoding aware
        [RubyMethod("empty?")]
        public static bool IsEmpty(RubySymbol/*!*/ self) {
            return self.IsEmpty;
        }

        // encoding aware
        [RubyMethod("encoding")]
        public static RubyEncoding/*!*/ GetEncoding(RubySymbol/*!*/ self) {
            return self.Encoding;
        }

        // encoding aware
        [RubyMethod("size")]
        [RubyMethod("length")]
        public static int GetLength(RubySymbol/*!*/ self) {
            return self.String.GetCharacterCount();
        }

        #endregion

        #region downcase, upcase, swapcase, capitalize, next/succ

        // Symbol's case methods take the same options String's do.
        [RubyMethod("downcase")]
        public static RubySymbol/*!*/ DownCase(RubyContext/*!*/ context, RubySymbol/*!*/ self, params object[]/*!*/ options) {
            return context.CreateSymbol(MutableStringOps.DownCase(self.String, options));
        }

        [RubyMethod("upcase")]
        public static RubySymbol/*!*/ UpCase(RubyContext/*!*/ context, RubySymbol/*!*/ self, params object[]/*!*/ options) {
            return context.CreateSymbol(MutableStringOps.UpCase(self.String, options));
        }

        [RubyMethod("swapcase")]
        public static RubySymbol/*!*/ SwapCase(RubyContext/*!*/ context, RubySymbol/*!*/ self, params object[]/*!*/ options) {
            return context.CreateSymbol(MutableStringOps.SwapCase(self.String, options));
        }

        [RubyMethod("capitalize")]
        public static RubySymbol/*!*/ Capitalize(RubyContext/*!*/ context, RubySymbol/*!*/ self, params object[]/*!*/ options) {
            return context.CreateSymbol(MutableStringOps.Capitalize(self.String, options));
        }

        [RubyMethod("next")]
        [RubyMethod("succ")]
        public static RubySymbol/*!*/ Succ(RubyContext/*!*/ context, RubySymbol/*!*/ self) {
            return context.CreateSymbol(MutableStringOps.Succ(self.String));
        }

        #endregion

        #region all_symbols

        [RubyMethod("all_symbols", RubyMethodAttributes.PublicSingleton)]
        public static RubyArray/*!*/ GetAllSymbols(RubyClass/*!*/ self) {
            return self.ImmediateClass.Context.GetAllSymbols();
        }

        #endregion
    }
}
