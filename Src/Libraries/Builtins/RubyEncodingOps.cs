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
using System.Text;
using IronRuby.Builtins;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using Microsoft.Scripting;
using System.Globalization;

namespace IronRuby.Builtins {
    [RubyClass("Encoding", Extends = typeof(RubyEncoding), Inherits = typeof(Object))]
    public static partial class RubyEncodingOps {
        #region Exceptions

        [RubyException("CompatibilityError", Extends = typeof(EncodingCompatibilityError))]
        public static class CompatibilityErrorOps {
        }

        [RubyException("UndefinedConversionError", Extends = typeof(UndefinedConversionError))]
        public static class UndefinedConversionErrorOps {
            [RubyMethod("source_encoding")]
            public static RubyEncoding GetSourceEncoding(UndefinedConversionError/*!*/ self) {
                return self.SourceEncoding;
            }

            [RubyMethod("destination_encoding")]
            public static RubyEncoding GetDestinationEncoding(UndefinedConversionError/*!*/ self) {
                return self.DestinationEncoding;
            }

            [RubyMethod("source_encoding_name")]
            public static MutableString GetSourceEncodingName(UndefinedConversionError/*!*/ self) {
                // MRI hands back the name the transcoder tables carry, which is a byte string.
                return self.SourceEncoding != null ? MutableString.CreateBinary(Encoding.ASCII.GetBytes(self.SourceEncoding.Name)) : null;
            }

            [RubyMethod("destination_encoding_name")]
            public static MutableString GetDestinationEncodingName(UndefinedConversionError/*!*/ self) {
                return self.DestinationEncoding != null ? MutableString.CreateBinary(Encoding.ASCII.GetBytes(self.DestinationEncoding.Name)) : null;
            }

            [RubyMethod("error_char")]
            public static MutableString GetErrorChar(UndefinedConversionError/*!*/ self) {
                var bytes = self.ErrorCharBytes;
                if (bytes == null) {
                    return null;
                }
                return MutableString.CreateBinary(bytes, self.SourceEncoding ?? RubyEncoding.Binary);
            }
        }

        [RubyException("InvalidByteSequenceError", Extends = typeof(InvalidByteSequenceError))]
        public static class InvalidByteSequenceErrorOps {
            [RubyMethod("source_encoding")]
            public static RubyEncoding GetSourceEncoding(InvalidByteSequenceError/*!*/ self) {
                return self.SourceEncoding;
            }

            [RubyMethod("destination_encoding")]
            public static RubyEncoding GetDestinationEncoding(InvalidByteSequenceError/*!*/ self) {
                return self.DestinationEncoding;
            }

            [RubyMethod("source_encoding_name")]
            public static MutableString GetSourceEncodingName(InvalidByteSequenceError/*!*/ self) {
                return self.SourceEncoding != null ? MutableString.CreateBinary(Encoding.ASCII.GetBytes(self.SourceEncoding.Name)) : null;
            }

            [RubyMethod("destination_encoding_name")]
            public static MutableString GetDestinationEncodingName(InvalidByteSequenceError/*!*/ self) {
                return self.DestinationEncoding != null ? MutableString.CreateBinary(Encoding.ASCII.GetBytes(self.DestinationEncoding.Name)) : null;
            }

            [RubyMethod("error_bytes")]
            public static MutableString GetErrorBytes(InvalidByteSequenceError/*!*/ self) {
                return self.ErrorBytes != null ? MutableString.CreateBinary(self.ErrorBytes) : null;
            }

            [RubyMethod("readagain_bytes")]
            public static MutableString GetReadAgainBytes(InvalidByteSequenceError/*!*/ self) {
                // MRI answers nil, not "", when nothing is to be read again - #primitive_errinfo is
                // the one that reports an empty string for the same state.
                var bytes = self.ReadAgainBytes;
                return bytes != null && bytes.Length != 0 ? MutableString.CreateBinary(bytes) : null;
            }

            [RubyMethod("incomplete_input?")]
            public static object IsIncompleteInput(InvalidByteSequenceError/*!*/ self) {
                return self.IncompleteInput.HasValue ? ScriptingRuntimeHelpers.BooleanToObject(self.IncompleteInput.Value) : null;
            }
        }

        [RubyException("ConverterNotFoundError", Extends = typeof(ConverterNotFoundError))]
        public static class ConverterNotFoundErrorOps {
        }

        #endregion

#region Constants

        // for C# callers; Ruby sees these through DefineConstants
        public static readonly RubyEncoding/*!*/ US_ASCII = RubyEncoding.Ascii;
        public static readonly RubyEncoding/*!*/ UTF_8 = RubyEncoding.UTF8;
        public static readonly RubyEncoding/*!*/ UTF_7 = RubyEncoding.GetRubyEncoding(RubyEncoding.CodePageUTF7);
        public static readonly RubyEncoding/*!*/ UTF_16BE = RubyEncoding.GetRubyEncoding(RubyEncoding.CodePageUTF16BE);
        public static readonly RubyEncoding/*!*/ UTF_16LE = RubyEncoding.GetRubyEncoding(RubyEncoding.CodePageUTF16LE);
        public static readonly RubyEncoding/*!*/ UTF_32BE = RubyEncoding.GetRubyEncoding(RubyEncoding.CodePageUTF32BE);
        public static readonly RubyEncoding/*!*/ UTF_32LE = RubyEncoding.GetRubyEncoding(RubyEncoding.CodePageUTF32LE);
        public static readonly RubyEncoding/*!*/ KOI8_R = RubyEncoding.GetRubyEncoding(20866);
        public static readonly RubyEncoding/*!*/ ISO_8859_15 = RubyEncoding.GetRubyEncoding(28605);

        /// <summary>
        /// MRI makes a constant of every encoding name and alias that can be one (set_encoding_const):
        /// the name itself if it already is a constant name, and otherwise with each character that
        /// cannot be in one replaced by '_' and its first letter capitalized, provided the name has
        /// an upper case letter at all ("eucJP" is Encoding::EucJP, "ebcdic-cp-us" gets no such
        /// form); plus an all upper case form whenever the name has a lower case letter. A name that
        /// starts with a digit ("646") makes no constant.
        /// </summary>
        [RubyConstant("UTF_8")]
        public static RubyEncoding/*!*/ DefineConstants(RubyModule/*!*/ module) {
            foreach (var name in RubyEncoding.GetAllNames()) {
                var encoding = RubyEncoding.FindByName(name);
                foreach (var constantName in GetConstantNames(name)) {
                    LibraryInitializer.SetBuiltinConstant(module, constantName, encoding);
                }
            }
            return RubyEncoding.UTF8;
        }

        internal static IEnumerable<string>/*!*/ GetConstantNames(string/*!*/ name) {
            if (name.Length == 0 || Char.IsAsciiDigit(name[0])) {
                yield break;
            }

            bool valid = Char.IsAsciiLetterUpper(name[0]);
            bool hasLower = false, hasUpper = false;
            foreach (char c in name) {
                hasLower |= Char.IsAsciiLetterLower(c);
                hasUpper |= Char.IsAsciiLetterUpper(c);
                valid &= Char.IsAsciiLetterOrDigit(c) || c == '_';
            }

            var chars = name.ToCharArray();
            if (valid) {
                yield return name;
            } else {
                chars[0] = Char.ToUpperInvariant(chars[0]);
                for (int i = 0; i < chars.Length; i++) {
                    if (!Char.IsAsciiLetterOrDigit(chars[i])) {
                        chars[i] = '_';
                    }
                }
                if (hasUpper) {
                    yield return new string(chars);
                }
            }

            if (hasLower) {
                for (int i = 0; i < chars.Length; i++) {
                    chars[i] = Char.ToUpperInvariant(chars[i]);
                }
                yield return new string(chars);
            }
        }

        #endregion


#region to_s, inspect, based_encoding, dummy?, ascii_compatible?

        [RubyMethod("name")]
        [RubyMethod("to_s")]
        public static MutableString/*!*/ ToS(RubyEncoding/*!*/ self) {
            return MutableString.CreateAscii(self.Name).Freeze();
        }

        [RubyMethod("inspect")]
        public static MutableString/*!*/ Inspect(RubyContext/*!*/ context, RubyEncoding/*!*/ self) {
            // TODO: to_s overridden
            MutableString result = MutableString.CreateMutable(context.GetIdentifierEncoding());
            result.Append("#<");
            result.Append(context.GetClassDisplayName(self));
            result.Append(':');
            // Ruby 3.4 renamed ASCII-8BIT to BINARY but kept #name answering the old spelling, so
            // #inspect is the one place both appear.
            result.Append(self == RubyEncoding.Binary ? "BINARY (ASCII-8BIT)" : self.Name);
            if (self.IsDummy) {
                result.Append(" (dummy)");
            }
            result.Append('>');
            return result;
        }

        [RubyMethod("dummy?")]
        public static bool IsDummy(RubyEncoding/*!*/ self) {
            return self.IsDummy;
        }

        [RubyMethod("ascii_compatible?")]
        public static bool IsAsciiCompatible(RubyEncoding/*!*/ self) {
            return self.IsAsciiIdentity;
        }

        // TODO:
        // Method "replicate". This would need a change in implementation of RubyEncoding - encodings are singletons now.
        // What properties are preserved during replication?

        [RubyMethod("names")]
        public static RubyArray/*!*/ GetAllNames(RubyContext/*!*/ context, RubyEncoding/*!*/ self) {
            var result = new RubyArray();
            result.Add(MutableString.CreateAscii(self.Name).Freeze());
            foreach (var alias in self.GetAliasNames()) {
                result.Add(MutableString.CreateAscii(alias).Freeze());
            }
            foreach (var special in GetSpecialAliases(context)) {
                if (special.Value == self) {
                    result.Add(MutableString.CreateAscii(special.Key).Freeze());
                }
            }
            return result;
        }

        #endregion

#region aliases, name_list, list, find

        /// <summary>
        /// The names that stand for whatever encoding the runtime currently uses for something,
        /// in MRI's order; "internal" only while there is a default internal encoding.
        /// </summary>
        private static IEnumerable<KeyValuePair<string, RubyEncoding>>/*!*/ GetSpecialAliases(RubyContext/*!*/ context) {
            yield return new KeyValuePair<string, RubyEncoding>("locale", context.RubyOptions.LocaleEncoding);
            yield return new KeyValuePair<string, RubyEncoding>("external", context.DefaultExternalEncoding);
            yield return new KeyValuePair<string, RubyEncoding>("filesystem", context.GetPathEncoding());
            if (context.DefaultInternalEncoding != null) {
                yield return new KeyValuePair<string, RubyEncoding>("internal", context.DefaultInternalEncoding);
            }
        }

        [RubyMethod("aliases", RubyMethodAttributes.PublicSingleton)]
        public static Hash/*!*/ GetAliases(RubyClass/*!*/ self) {
            var context = self.Context;
            var result = new Hash(context.EqualityComparer);
            foreach (var alias in RubyEncoding.GetAliases()) {
                result.Add(MutableString.CreateAscii(alias.Key).Freeze(), MutableString.CreateAscii(alias.Value).Freeze());
            }
            foreach (var special in GetSpecialAliases(context)) {
                result.Add(MutableString.CreateAscii(special.Key).Freeze(), MutableString.CreateAscii(special.Value.Name).Freeze());
            }
            return result;
        }

        [RubyMethod("name_list", RubyMethodAttributes.PublicSingleton)]
        public static RubyArray/*!*/ GetNameList(RubyClass/*!*/ self) {
            var result = new RubyArray();
            foreach (var name in RubyEncoding.GetAllNames()) {
                result.Add(MutableString.CreateAscii(name).Freeze());
            }
            // MRI lists "internal" even while it names no encoding
            result.Add(MutableString.CreateAscii("locale").Freeze());
            result.Add(MutableString.CreateAscii("external").Freeze());
            result.Add(MutableString.CreateAscii("filesystem").Freeze());
            result.Add(MutableString.CreateAscii("internal").Freeze());
            return result;
        }

        [RubyMethod("list", RubyMethodAttributes.PublicSingleton)]
        public static RubyArray/*!*/ GetAvailableEncodings(RubyClass/*!*/ self) {
            var result = new RubyArray();
            foreach (var encoding in RubyEncoding.GetAll()) {
                result.Add(encoding);
            }
            return result;
        }

        // Encoding.find(Encoding::UTF_8) answers with the encoding itself.
        [RubyMethod("find", RubyMethodAttributes.PublicSingleton)]
        public static RubyEncoding/*!*/ GetEncoding(RubyClass/*!*/ self, [NotNull]RubyEncoding/*!*/ encoding) {
            return encoding;
        }

        [RubyMethod("find", RubyMethodAttributes.PublicSingleton)]
        public static RubyEncoding GetEncoding(RubyClass/*!*/ self, DynamicNull name) {
            throw RubyExceptions.CreateImplicitConversionError("nil", "String");
        }

        // An Encoding is a constant of the runtime: there is nothing in it to change.
        [RubyMethod("frozen?")]
        public static bool IsFrozen(RubyEncoding/*!*/ self) {
            return true;
        }

        // A Symbol is not a name here: MRI takes only a String or something with #to_str.
        [RubyMethod("find", RubyMethodAttributes.PublicSingleton)]
        public static RubyEncoding/*!*/ GetEncoding(RubyClass/*!*/ self, [NotNull]RubySymbol/*!*/ name) {
            throw RubyExceptions.CreateTypeError("no implicit conversion of Symbol into String");
        }

        [RubyMethod("find", RubyMethodAttributes.PublicSingleton)]
        public static RubyEncoding GetEncoding(RubyClass/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ name) {
            // "internal" is the only name that can legitimately answer nil - Encoding.default_internal
            // is nil unless the program set it.
            if (name.IsAscii() && name.ToString().ToUpperInvariant() == "INTERNAL") {
                return self.Context.DefaultInternalEncoding;
            }

            return self.Context.GetRubyEncoding(name);
        }

        #endregion

#region compatible?

        [RubyMethod("compatible?", RubyMethodAttributes.PublicSingleton)]
        public static RubyEncoding GetCompatible(RubyClass/*!*/ self, [NotNull]MutableString/*!*/ str1, [NotNull]MutableString/*!*/ str2) {
            return str1.GetCompatibleEncoding(str2);
        }

        [RubyMethod("compatible?", RubyMethodAttributes.PublicSingleton)]
        public static RubyEncoding GetCompatible(RubyClass/*!*/ self, [NotNull]RubyEncoding/*!*/ encoding1, [NotNull]RubyEncoding/*!*/ encoding2) {
            return MutableString.GetCompatibleEncoding(encoding1, encoding2);
        }

        [RubyMethod("compatible?", RubyMethodAttributes.PublicSingleton)]
        public static RubyEncoding GetCompatible(RubyClass/*!*/ self, [NotNull]RubyEncoding/*!*/ encoding, [NotNull]MutableString/*!*/ str) {
            // argument order matters: the Encoding object is the first operand here
            return MutableString.GetCompatibleEncoding(null, encoding, str, str.Encoding);
        }

        [RubyMethod("compatible?", RubyMethodAttributes.PublicSingleton)]
        public static RubyEncoding GetCompatible(RubyClass/*!*/ self, [NotNull]MutableString/*!*/ str, [NotNull]RubyEncoding/*!*/ encoding) {
            return str.GetCompatibleEncoding(encoding);
        }

        [RubyMethod("compatible?", RubyMethodAttributes.PublicSingleton)]
        public static RubyEncoding GetCompatible(RubyClass/*!*/ self, [NotNull]RubyEncoding/*!*/ encoding, [NotNull]RubySymbol/*!*/ symbol) {
            return GetCompatible(self, encoding, symbol.String);
        }

        [RubyMethod("compatible?", RubyMethodAttributes.PublicSingleton)]
        public static RubyEncoding GetCompatible(RubyClass/*!*/ self, [NotNull]MutableString/*!*/ str, [NotNull]RubySymbol/*!*/ symbol) {
            return GetCompatible(self, str, symbol.String);
        }

        [RubyMethod("compatible?", RubyMethodAttributes.PublicSingleton)]
        public static RubyEncoding GetCompatible(RubyClass/*!*/ self, [NotNull]RubySymbol/*!*/ symbol, [NotNull]RubyEncoding/*!*/ encoding) {
            return GetCompatible(self, symbol.String, encoding);
        }

        [RubyMethod("compatible?", RubyMethodAttributes.PublicSingleton)]
        public static RubyEncoding GetCompatible(RubyClass/*!*/ self, [NotNull]RubySymbol/*!*/ symbol, [NotNull]MutableString/*!*/ str) {
            return GetCompatible(self, symbol.String, str);
        }

        [RubyMethod("compatible?", RubyMethodAttributes.PublicSingleton)]
        public static RubyEncoding GetCompatible(RubyClass/*!*/ self, [NotNull]RubySymbol/*!*/ encoding1, [NotNull]RubySymbol/*!*/ encoding2) {
            return GetCompatible(self, encoding1.String, encoding2.String);
        }

        // A Regexp carries an encoding but its contents are not a String, which is the distinction
        // MRI's rb_enc_compatible draws: /abc/ in US-ASCII is compatible with a UTF-8 String the
        // way Encoding::US_ASCII is, not the way "abc" is.
        [RubyMethod("compatible?", RubyMethodAttributes.PublicSingleton)]
        public static RubyEncoding GetCompatible(RubyClass/*!*/ self, [NotNull]RubyRegex/*!*/ regex1, [NotNull]RubyRegex/*!*/ regex2) {
            return MutableString.GetCompatibleEncoding(regex1.Encoding, regex2.Encoding);
        }

        [RubyMethod("compatible?", RubyMethodAttributes.PublicSingleton)]
        public static RubyEncoding GetCompatible(RubyClass/*!*/ self, [NotNull]MutableString/*!*/ str, [NotNull]RubyRegex/*!*/ regex) {
            return str.GetCompatibleEncoding(regex.Encoding);
        }

        [RubyMethod("compatible?", RubyMethodAttributes.PublicSingleton)]
        public static RubyEncoding GetCompatible(RubyClass/*!*/ self, [NotNull]RubyRegex/*!*/ regex, [NotNull]MutableString/*!*/ str) {
            // argument order matters: the Regexp is the first operand here
            return MutableString.GetCompatibleEncoding(null, regex.Encoding, str, str.Encoding);
        }

        [RubyMethod("compatible?", RubyMethodAttributes.PublicSingleton)]
        public static RubyEncoding GetCompatible(RubyClass/*!*/ self, [NotNull]RubySymbol/*!*/ symbol, [NotNull]RubyRegex/*!*/ regex) {
            return GetCompatible(self, symbol.String, regex);
        }

        [RubyMethod("compatible?", RubyMethodAttributes.PublicSingleton)]
        public static RubyEncoding GetCompatible(RubyClass/*!*/ self, [NotNull]RubyRegex/*!*/ regex, [NotNull]RubySymbol/*!*/ symbol) {
            return GetCompatible(self, regex, symbol.String);
        }

        [RubyMethod("compatible?", RubyMethodAttributes.PublicSingleton)]
        public static RubyEncoding GetCompatible(RubyClass/*!*/ self, [NotNull]RubyEncoding/*!*/ encoding, [NotNull]RubyRegex/*!*/ regex) {
            return MutableString.GetCompatibleEncoding(encoding, regex.Encoding);
        }

        [RubyMethod("compatible?", RubyMethodAttributes.PublicSingleton)]
        public static RubyEncoding GetCompatible(RubyClass/*!*/ self, [NotNull]RubyRegex/*!*/ regex, [NotNull]RubyEncoding/*!*/ encoding) {
            return MutableString.GetCompatibleEncoding(regex.Encoding, encoding);
        }

        [RubyMethod("compatible?", RubyMethodAttributes.PublicSingleton)]
        public static RubyEncoding GetCompatible(RubyClass/*!*/ self, object obj1, object obj2) {
            return null;
        }

        #endregion

#region default_external, default_internal, locale_charmap

        [RubyMethod("default_external", RubyMethodAttributes.PublicSingleton)]
        public static RubyEncoding/*!*/ GetDefaultExternalEncoding(RubyClass/*!*/ self) {
            return self.Context.DefaultExternalEncoding;
        }

        [RubyMethod("default_external=", RubyMethodAttributes.PublicSingleton)]
        public static RubyEncoding/*!*/ SetDefaultExternalEncoding(RubyClass/*!*/ self, RubyEncoding encoding) {
            if (encoding == null) {
                throw RubyExceptions.CreateArgumentError("default external can not be nil");
            }
            var old = self.Context.DefaultExternalEncoding;
            self.Context.DefaultExternalEncoding = encoding;
            return old;
        }

        [RubyMethod("default_external=", RubyMethodAttributes.PublicSingleton)]
        public static RubyEncoding/*!*/ SetDefaultExternalEncoding(RubyClass/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ encodingName) {
            return SetDefaultExternalEncoding(self, self.Context.GetRubyEncoding(encodingName));
        }

        [RubyMethod("default_internal", RubyMethodAttributes.PublicSingleton)]
        public static RubyEncoding/*!*/ GetDefaultInternalEncoding(RubyClass/*!*/ self) {
            return self.Context.DefaultInternalEncoding;
        }

        [RubyMethod("default_internal=", RubyMethodAttributes.PublicSingleton)]
        public static RubyEncoding/*!*/ SetDefaultInternalEncoding(RubyClass/*!*/ self, RubyEncoding encoding) {
            var old = self.Context.DefaultInternalEncoding;
            self.Context.DefaultInternalEncoding = encoding;
            return old;
        }

        [RubyMethod("default_internal=", RubyMethodAttributes.PublicSingleton)]
        public static RubyEncoding/*!*/ SetDefaultInternalEncoding(RubyClass/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ encodingName) {
            return SetDefaultInternalEncoding(self, self.Context.GetRubyEncoding(encodingName));
        }

        [RubyMethod("locale_charmap", RubyMethodAttributes.PublicSingleton)]
        public static MutableString/*!*/ GetDefaultCharmap(RubyClass/*!*/ self) {
            // nl_langinfo(CODESET), as the C library spells it ("ANSI_X3.4-1968" for the C locale)
            return MutableString.Create(RubyEncoding.LocaleCharmap ?? self.Context.RubyOptions.LocaleEncoding.Name);
        }

        #endregion
    }
}
