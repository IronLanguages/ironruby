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
#if FEATURE_ENCODING

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
    [RubyClass("Encoding", Extends = typeof(RubyEncoding), Inherits = typeof(Object), BuildConfig = "FEATURE_ENCODING")]
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

        [RubyConstant("ANSI_X3_4_1968")]
        [RubyConstant("US_ASCII")]
        [RubyConstant("ASCII")]
        public static readonly RubyEncoding US_ASCII = RubyEncoding.Ascii; 
    
        [RubyConstant]
        public static readonly RubyEncoding UTF_8 = RubyEncoding.UTF8;

        [RubyConstant]
        public static readonly RubyEncoding ASCII_8BIT = RubyEncoding.Binary;

        [RubyConstant]
        public static readonly RubyEncoding BINARY = RubyEncoding.Binary;

        [RubyConstant("SHIFT_JIS")]
        [RubyConstant("Shift_JIS")]
        public static readonly RubyEncoding SHIFT_JIS = RubyEncoding.GetRubyEncoding(RubyEncoding.CodePageShiftJIS);
        
        [RubyConstant]
        public static readonly RubyEncoding EUC_JP = RubyEncoding.EUCJP;

        [RubyConstant]
        public static readonly RubyEncoding KOI8_R = RubyEncoding.GetRubyEncoding(20866);

        // TIS-620 is not Windows-874: Windows-874 is TIS-620 plus the Windows C1 repertoire.
        [RubyConstant]
        public static readonly RubyEncoding TIS_620 = RubyEncoding.GetRubyEncoding(RubyEncoding.CodePageTIS620);

        [RubyConstant("Windows_874")]
        [RubyConstant("WINDOWS_874")]
        [RubyConstant("CP874")]
        public static readonly RubyEncoding Windows_874 = RubyEncoding.GetRubyEncoding(874);

        [RubyConstant]
        public static readonly RubyEncoding CESU_8 = RubyEncoding.GetRubyEncoding(RubyEncoding.CodePageCESU8);

        // Emacs-Mule: Ruby has it, .NET does not, and Ruby ships no converter for it either - only
        // a string that is all ASCII can be transcoded to or from it.
        [RubyConstant("Emacs_Mule")]
        [RubyConstant("EMACS_MULE")]
        public static readonly RubyEncoding Emacs_Mule = RubyEncoding.GetRubyEncoding(RubyEncoding.CodePageEmacsMule);

        [RubyConstant("ISO8859_9")]
        [RubyConstant("ISO_8859_9")]
        public static readonly RubyEncoding ISO_8859_9 = RubyEncoding.GetRubyEncoding(28599);

        [RubyConstant("ISO8859_15")]
        [RubyConstant("ISO_8859_15")]
        public static readonly RubyEncoding ISO_8859_15 = RubyEncoding.GetRubyEncoding(28605);

        [RubyConstant("Big5")]
        [RubyConstant("BIG5")]
        public static readonly RubyEncoding Big5 = RubyEncoding.GetRubyEncoding(RubyEncoding.CodePageBig5);

        [RubyConstant("ISO2022_JP")]
        [RubyConstant("ISO_2022_JP")]
        public static readonly RubyEncoding ISO_2022_JP = RubyEncoding.GetRubyEncoding(50220);

        [RubyConstant]
        public static readonly RubyEncoding CP50221 = RubyEncoding.GetRubyEncoding(50221);

        // TODO:
        // ...

        // TODO: lazy encoding load?

        [RubyConstant]
#pragma warning disable SYSLIB0001 // UTF-7 is obsolete in .NET, and still one of Ruby's encodings
        public static readonly RubyEncoding UTF_7 = RubyEncoding.GetRubyEncoding(Encoding.UTF7);
#pragma warning restore SYSLIB0001

        // UTF-16 and UTF-32 without an endianness suffix are dummy encodings that carry a BOM.
        // They are distinct from UTF-16LE/BE and UTF-32LE/BE, which are not dummy.
        [RubyConstant]
        public static readonly RubyEncoding UTF_16 = RubyEncoding.GetRubyEncoding(RubyEncoding.CodePageUTF16);

        [RubyConstant]
        public static readonly RubyEncoding UTF_32 = RubyEncoding.GetRubyEncoding(RubyEncoding.CodePageUTF32);

        [RubyConstant]
        public static readonly RubyEncoding UTF_16BE = RubyEncoding.GetRubyEncoding(Encoding.BigEndianUnicode);

        [RubyConstant]
        public static readonly RubyEncoding UTF_16LE = RubyEncoding.GetRubyEncoding(Encoding.Unicode);

        [RubyConstant]
        public static readonly RubyEncoding UTF_32BE = RubyEncoding.GetRubyEncoding(RubyEncoding.CodePageUTF32BE);

        [RubyConstant]
        public static readonly RubyEncoding UTF_32LE = RubyEncoding.GetRubyEncoding(Encoding.UTF32);

        #endregion

#region to_s, inspect, based_encoding, dummy?, ascii_compatible?

        [RubyMethod("name")]
        [RubyMethod("to_s")]
        public static MutableString/*!*/ ToS(RubyEncoding/*!*/ self) {
            return MutableString.CreateAscii(self.Name);
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

        [RubyMethod("based_encoding")]
        public static RubyEncoding BasedEncoding(RubyEncoding/*!*/ self) {
            return null;
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
            
            string name = self.Name;
            result.Add(MutableString.Create(name));
            
            foreach (var alias in RubyEncoding.Aliases) {
                if (StringComparer.OrdinalIgnoreCase.Equals(alias.Value, name)) {
                    result.Add(MutableString.CreateAscii(alias.Key));
                }
            }

            if (self == context.RubyOptions.LocaleEncoding) {
                result.Add(MutableString.CreateAscii("locale"));
            }

            if (self == context.DefaultExternalEncoding) {
                result.Add(MutableString.CreateAscii("external"));
            }

            if (self == context.GetPathEncoding()) {
                result.Add(MutableString.CreateAscii("filesystem"));
            }

            return result;
        }

        #endregion

#region aliases, name_list, list, find

        [RubyMethod("aliases", RubyMethodAttributes.PublicSingleton)]
        public static Hash/*!*/ GetAliases(RubyClass/*!*/ self) {
            var context = self.Context;
            var result = new Hash(context.EqualityComparer, RubyEncoding.Aliases.Count + 3);
            foreach (var alias in RubyEncoding.Aliases) {
                result.Add(MutableString.CreateAscii(alias.Key).Freeze(), MutableString.CreateAscii(alias.Value).Freeze());
            }

            result.Add(MutableString.CreateAscii("locale").Freeze(), MutableString.Create(context.RubyOptions.LocaleEncoding.Name).Freeze());
            result.Add(MutableString.CreateAscii("external").Freeze(), MutableString.Create(context.DefaultExternalEncoding.Name).Freeze());
            result.Add(MutableString.CreateAscii("filesystem").Freeze(), MutableString.Create(context.GetPathEncoding().Name).Freeze());
            return result;
        }

        /// <summary>
        /// The encodings Encoding.list reports, each exactly once.
        ///
        /// .NET's Encoding.GetEncodings() is not enough on its own to build this. It reports the
        /// code pages the platform's provider happens to enumerate, which on Linux leaves out
        /// several that Encoding.GetEncoding still resolves - EUC-JP is the obvious one - and it
        /// knows nothing about the encodings Ruby has and .NET does not. Everything an alias points
        /// at has to be in here as well, or Encoding.list.include?(Encoding.find(alias)) is false.
        /// </summary>
        private static List<RubyEncoding>/*!*/ GetEncodingList(RubyContext/*!*/ context) {
            var seen = new Dictionary<int, bool>();
            var result = new List<RubyEncoding>();

            foreach (var encoding in new[] {
                RubyEncoding.Binary, RubyEncoding.UTF8, RubyEncoding.Ascii, RubyEncoding.SJIS, RubyEncoding.EUCJP,
                UTF_16, UTF_32, UTF_16BE, UTF_16LE, UTF_32BE, UTF_32LE, CESU_8, TIS_620, Emacs_Mule,
            }) {
                AddEncoding(seen, result, encoding);
            }

            // Code pages Encoding.GetEncoding resolves but Encoding.GetEncodings() does not
            // enumerate on this platform. Leaving them out would make Encoding.find answer with an
            // encoding that Encoding.list says does not exist.
            foreach (var codepage in new[] { 51936 /* GB2312 */, 51949 /* EUC-KR */, 54936 /* GB18030 */, 50220, 50221,
                RubyEncoding.CodePageShiftJIS, RubyEncoding.CodePageUTF8Mac, RubyEncoding.CodePageCP51932, RubyEncoding.CodePageEucJpMs,
                RubyEncoding.CodePageStatelessISO2022JP, RubyEncoding.CodePageISO2022JP2, RubyEncoding.CodePageGB12345, RubyEncoding.CodePageEUCTW,
                RubyEncoding.CodePageGB1988, RubyEncoding.CodePageISO8859_10, RubyEncoding.CodePageISO8859_14, RubyEncoding.CodePageISO8859_16 }) {
                try {
                    AddEncoding(seen, result, RubyEncoding.GetRubyEncoding(codepage));
                } catch (ArgumentException) {
                    // not available here
                } catch (NotSupportedException) {
                    // not available here
                }
            }

            foreach (var info in Encoding.GetEncodings()) {
                AddEncoding(seen, result, RubyEncoding.GetRubyEncoding(info.CodePage));
            }
            // GetEncodings() leaves UTF-7 out when the host has not enabled
            // System.Text.Encoding.EnableUnsafeUTF7Encoding; Ruby always has it.
            AddEncoding(seen, result, UTF_7);

            foreach (var alias in RubyEncoding.Aliases) {
                try {
                    AddEncoding(seen, result, context.GetRubyEncoding(alias.Value));
                } catch (ArgumentException) {
                    // an alias for an encoding this platform doesn't have
                }
            }

            return result;
        }

        private static void AddEncoding(Dictionary<int, bool>/*!*/ seen, List<RubyEncoding>/*!*/ result, RubyEncoding encoding) {
            if (encoding != null && !seen.ContainsKey(encoding.CodePage)) {
                seen.Add(encoding.CodePage, true);
                result.Add(encoding);
            }
        }

        [RubyMethod("name_list", RubyMethodAttributes.PublicSingleton)]
        public static RubyArray/*!*/ GetNameList(RubyClass/*!*/ self) {
            var encodings = GetEncodingList(self.Context);
            var result = new RubyArray(encodings.Count + RubyEncoding.Aliases.Count + 3);

            foreach (var encoding in encodings) {
                result.Add(MutableString.Create(encoding.Name));
            }

            foreach (var alias in RubyEncoding.Aliases.Keys) {
                result.Add(MutableString.CreateAscii(alias));
            }

            result.Add(MutableString.CreateAscii("locale"));
            result.Add(MutableString.CreateAscii("external"));
            result.Add(MutableString.CreateAscii("filesystem"));

            return result;
        }

        [RubyMethod("list", RubyMethodAttributes.PublicSingleton)]
        public static RubyArray/*!*/ GetAvailableEncodings(RubyClass/*!*/ self) {
            var encodings = GetEncodingList(self.Context);
            var result = new RubyArray(encodings.Count);
            foreach (var encoding in encodings) {
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
            var encoding = self.Context.RubyOptions.LocaleEncoding;
            // nl_langinfo(CODESET) of the C locale, as glibc spells it
            if (encoding == RubyEncoding.Ascii && Environment.OSVersion.Platform == PlatformID.Unix) {
                return MutableString.CreateAscii("ANSI_X3.4-1968");
            }
            return MutableString.Create(encoding.Name);
        }

        #endregion
    }
}
#endif