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

using System.Linq.Expressions;

using System;
using System.Collections.Generic;
using System.Text;
using System.Diagnostics;
using System.Threading;
using System.Runtime.Serialization;
using Microsoft.Scripting.Utils;
using Microsoft.Scripting.Runtime;
using IronRuby.Runtime;
using IronRuby.Compiler;
using System.Collections.ObjectModel;

namespace IronRuby.Builtins {

    /// <summary>
    /// All encodings in Ruby are represented by instances of a single class. Therefore we need to wrap .NET Encoding class in RubyEncoding.
    /// Instances of this class are app-domain singletons. That's all right as far as the class is readonly and doesn't implement IRubyObject.
    /// Taint, frozen flags and instance variables need to be stored in per-runtime lookaside table.
    /// </summary>
    [Serializable]
    public class RubyEncoding : ISerializable, IExpressionSerializable {
        #region Singletons

        public const int CodePageBinary = 0;
        public const int CodePageSJIS = 932;
        public const int CodePageBig5 = 950;
        public const int CodePageAscii = 20127;

        // Windows returns 2 EUC-JP encodings (CP 20932 and CP 51932). Mono implements EUC-JP as 51932 and doesn't support 20932.
        public const int CodePageEUCJP = 51932;

        public const int CodePageUTF7 = 65000;
        public const int CodePageUTF8 = 65001;
        public const int CodePageUTF16BE = 1201;
        public const int CodePageUTF16LE = 1200;
        public const int CodePageUTF32BE = 12001;
        public const int CodePageUTF32LE = 12000;

        // Code pages for the encodings Ruby has and .NET does not - see RubyOnlyEncodings.
        // Windows allocates code page numbers below 65536, so nothing in this block can ever
        // collide with an encoding System.Text hands out.
        public const int CodePageUTF16 = 1000016;
        public const int CodePageUTF32 = 1000032;
        public const int CodePageCESU8 = 1000008;
        public const int CodePageTIS620 = 1000620;
        public const int CodePageEmacsMule = 1000230;
        public const int CodePageShiftJIS = 1000931;
        public const int CodePageUTF8Mac = 1065001;
        public const int CodePageCP51932 = 1051932;
        public const int CodePageEucJpMs = 1051933;
        public const int CodePageStatelessISO2022JP = 1002022;
        public const int CodePageISO2022JP2 = 1002023;
        public const int CodePageGB12345 = 1012345;
        public const int CodePageEUCTW = 1000951;
        public const int CodePageGB1988 = 1001988;
        public const int CodePageISO8859_10 = 1028600;
        public const int CodePageISO8859_14 = 1028604;
        public const int CodePageISO8859_16 = 1028606;

        // TODO: how does MRI sort encodings?

        // must precede the encoding fields below: field initializers run in textual order
        private static readonly bool _codePagesRegistered = RegisterCodePageProvider();

        private static bool RegisterCodePageProvider() {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            // Ruby exposes UTF-7; .NET 5+ disables it unless this switch is set
            AppContext.SetSwitch("System.Text.Encoding.EnableUnsafeUTF7Encoding", true);
            return true;
        }

        public static readonly RubyEncoding/*!*/ Binary = new RubyEncoding(BinaryEncoding.Instance, BinaryEncoding.Instance, -4);
        public static readonly RubyEncoding/*!*/ UTF8 = new RubyEncoding(CreateEncoding(CodePageUTF8, false), CreateEncoding(CodePageUTF8, true), -3);

        public static readonly RubyEncoding/*!*/ Ascii = new RubyEncoding(CreateEncoding(CodePageAscii, false), CreateEncoding(CodePageAscii, true), -2);
        public static readonly RubyEncoding/*!*/ EUCJP = new RubyEncoding(CreateEncoding(CodePageEUCJP, false), CreateEncoding(CodePageEUCJP, true), -1);
        public static readonly RubyEncoding/*!*/ SJIS = new RubyEncoding(CreateEncoding(CodePageSJIS, false), CreateEncoding(CodePageSJIS, true), 0);

        #endregion

        private readonly Encoding/*!*/ _encoding;
        private readonly Encoding/*!*/ _strictEncoding;
        private Expression _expression;
        private readonly int _ordinal;

        // TODO: combine into a single integer (tables could be merged)
        private readonly int _maxBytesPerChar;
        private readonly bool _isAsciiIdentity;
        private bool? _isSingleByteCharacterSet;
        private bool? _isDoubleByteCharacterSet;

        private RubyEncoding(Encoding/*!*/ encoding, Encoding/*!*/ strictEncoding, int ordinal) {
            Assert.NotNull(encoding, strictEncoding);
            _ordinal = ordinal;
            _encoding = encoding;
            _strictEncoding = strictEncoding;
            _maxBytesPerChar = strictEncoding.GetMaxByteCount(1);
            _isAsciiIdentity = AsciiIdentity(encoding);
        }

        public override int GetHashCode() {
            return _ordinal;
        }

        internal Expression/*!*/ Expression {
            get { return _expression ?? (_expression = Expression.Constant(this)); }
        }

        public bool IsAsciiIdentity {
            get { return _isAsciiIdentity; }
        }

        private static Encoding/*!*/ CreateEncoding(int codepage, bool throwOnError) {
            if (RubyOnlyEncodings.IsRubyOnly(codepage)) {
                return RubyOnlyEncodings.Create(codepage, throwOnError);
            }
            if (codepage == CodePageUTF7) {
                return CreateUTF7Encoding(throwOnError);
            }
            if (throwOnError) {
                return Encoding.GetEncoding(codepage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            } else {
                return Encoding.GetEncoding(codepage, EncoderFallback.ReplacementFallback, BinaryDecoderFallback.Instance);
            }
        }

        /// <summary>
        /// UTF-7 without System.Text.Encoding.EnableUnsafeUTF7Encoding. .NET 5+ refuses
        /// Encoding.GetEncoding(65000) unless that switch is on, and it reads the switch only once
        /// per process: the AppContext.SetSwitch in RegisterCodePageProvider works for ir and for a
        /// host that has not asked yet, but a host that has already enumerated encodings (anything
        /// calling Encoding.GetEncodings(), as ASP.NET and logging stacks do) has cached "off", and
        /// the engine then failed to start at all - the Encoding class initializer builds UTF-7
        /// eagerly. The switch gates only the lookup; the UTF7Encoding class works regardless.
        /// UTF-7 is a dummy encoding in Ruby, so strings tagged with it are kept as bytes, and
        /// this object is rarely asked to transcode anything.
        /// </summary>
        private static Encoding/*!*/ CreateUTF7Encoding(bool throwOnError) {
#pragma warning disable SYSLIB0001 // UTF-7 is obsolete in .NET, and still one of Ruby's encodings
            var encoding = (Encoding)new UTF7Encoding().Clone();
#pragma warning restore SYSLIB0001
            if (throwOnError) {
                encoding.EncoderFallback = EncoderFallback.ExceptionFallback;
                encoding.DecoderFallback = DecoderFallback.ExceptionFallback;
            } else {
                encoding.EncoderFallback = EncoderFallback.ReplacementFallback;
                encoding.DecoderFallback = BinaryDecoderFallback.Instance;
            }
            return encoding;
        }

        #region Serialization
        private RubyEncoding(SerializationInfo/*!*/ info, StreamingContext context) {
            throw Assert.Unreachable;
        }

        [Serializable]
#pragma warning disable SYSLIB0050 // IObjectReference: BinaryFormatter gets the RubyEncoding singleton back (net8.0, Serialization1)
        internal sealed class Deserializer : ISerializable, IObjectReference {
#pragma warning restore SYSLIB0050
            private readonly int _codePage;
            
            private Deserializer(SerializationInfo/*!*/ info, StreamingContext context) {
                _codePage = info.GetInt32("CodePage");
            }

            public object GetRealObject(StreamingContext context) {
                return GetRubyEncoding(_codePage);
            }

            void ISerializable.GetObjectData(SerializationInfo/*!*/ info, StreamingContext context) {
                throw Assert.Unreachable;
            }
        }

        void ISerializable.GetObjectData(SerializationInfo/*!*/ info, StreamingContext context) {
            info.AddValue("CodePage", CodePage);
            info.SetType(typeof(Deserializer));
        }
        #endregion

        public int MaxBytesPerChar {
            get { return _maxBytesPerChar; }
        }

        /// <summary>
        /// The smallest number of bytes a character of this encoding occupies. It is 1 for
        /// everything but UTF-16 and UTF-32, whose code units are 2 and 4 bytes wide; a run of
        /// bytes that decodes to nothing still counts as one character per unit there, which is
        /// how MRI measures a string whose bytes are not valid in its own encoding. The
        /// BOM-sniffing "UTF-16" and "UTF-32" are dummy encodings measured in bytes, so they
        /// are not included.
        /// </summary>
        public int MinBytesPerChar {
            get {
                switch (CodePage) {
                    case CodePageUTF16LE:
                    case CodePageUTF16BE:
                        return 2;

                    case CodePageUTF32LE:
                    case CodePageUTF32BE:
                        return 4;

                    default:
                        return 1;
                }
            }
        }

        /// <summary>True for the big endian forms of UTF-16 and UTF-32.</summary>
        public bool IsBigEndianUnicode {
            get { return CodePage == CodePageUTF16BE || CodePage == CodePageUTF32BE; }
        }

        public Encoding/*!*/ Encoding {
            get { return _encoding; }
        }

        public Encoding/*!*/ StrictEncoding {
            get { return _strictEncoding; }
        }

        private Encoding _escapingEncoding;

        /// <summary>
        /// The encoding used to move a string between its byte and character representations.
        /// Unlike StrictEncoding it never refuses a byte, so a Ruby string whose bytes are not
        /// valid in its own encoding can still be reversed, indexed and sliced - see
        /// EscapingEncoding for how the bytes survive the round trip.
        /// </summary>
        public Encoding/*!*/ EscapingEncoding {
            get {
                // A dummy encoding has no characters to speak of: Ruby keeps a string tagged with
                // one as the bytes it was given, so the two representations are the same bytes.
                if (IsDummy) {
                    return BinaryEncoding.Instance;
                }

                return _escapingEncoding ?? (_escapingEncoding = new EscapingEncoding(_strictEncoding));
            }
        }

        /// <summary>
        /// Name as displayed by MRI.
        /// </summary>
        public string/*!*/ Name {
            get {
                return GetRubySpecificName(CodePage) ?? _encoding.WebName;
            }
        }

        public static string GetRubySpecificName(int codepage) {
            switch (codepage) {
                case RubyEncoding.CodePageUTF8: return "UTF-8";
                case RubyEncoding.CodePageUTF7: return "UTF-7";
                case RubyEncoding.CodePageUTF16BE: return "UTF-16BE";
                case RubyEncoding.CodePageUTF16LE: return "UTF-16LE";
                case RubyEncoding.CodePageUTF32BE: return "UTF-32BE";
                case RubyEncoding.CodePageUTF32LE: return "UTF-32LE";

                // no .NET counterpart, see RubyOnlyEncodings:
                case RubyEncoding.CodePageUTF16: return "UTF-16";
                case RubyEncoding.CodePageUTF32: return "UTF-32";
                case RubyEncoding.CodePageCESU8: return "CESU-8";
                case RubyEncoding.CodePageTIS620: return "TIS-620";
                case RubyEncoding.CodePageEmacsMule: return "Emacs-Mule";
                case RubyEncoding.CodePageShiftJIS: return "Shift_JIS";
                case RubyEncoding.CodePageUTF8Mac: return "UTF8-MAC";
                case RubyEncoding.CodePageCP51932: return "CP51932";
                case RubyEncoding.CodePageEucJpMs: return "eucJP-ms";
                case RubyEncoding.CodePageStatelessISO2022JP: return "stateless-ISO-2022-JP";
                case RubyEncoding.CodePageISO2022JP2: return "ISO-2022-JP-2";
                case RubyEncoding.CodePageGB12345: return "GB12345";
                case RubyEncoding.CodePageEUCTW: return "EUC-TW";
                case RubyEncoding.CodePageGB1988: return "GB1988";
                case RubyEncoding.CodePageISO8859_10: return "ISO-8859-10";
                case RubyEncoding.CodePageISO8859_14: return "ISO-8859-14";
                case RubyEncoding.CodePageISO8859_16: return "ISO-8859-16";
                // .NET's code page 932 is Microsoft's Shift_JIS, which Ruby calls Windows-31J (CP932, and
                // the SJIS of -Ks and /s); Ruby's narrower Shift_JIS is a code page of its own.
                case RubyEncoding.CodePageSJIS: return "Windows-31J";
                case RubyEncoding.CodePageAscii: return "US-ASCII";

                // disambiguates CP 20932 and CP 51932:
                case RubyEncoding.CodePageEUCJP: return "EUC-JP";
                case 20932: return "CP20932";

                case 50220: return "ISO-2022-JP";
                case 50221: return "CP50221";
                case 50222: return "CP50222";

                // .NET reports IANA "WebName"s, which are lower case. Ruby spells most of
                // these differently; Encoding#name and every error message that embeds an
                // encoding name have to match Ruby exactly.
                case 28591: return "ISO-8859-1";
                case 28592: return "ISO-8859-2";
                case 28593: return "ISO-8859-3";
                case 28594: return "ISO-8859-4";
                case 28595: return "ISO-8859-5";
                case 28596: return "ISO-8859-6";
                case 28597: return "ISO-8859-7";
                case 28598: return "ISO-8859-8";
                case 28599: return "ISO-8859-9";
                case 28603: return "ISO-8859-13";
                case 28605: return "ISO-8859-15";

                case RubyEncoding.CodePageBig5: return "Big5";

                // .NET's "gb2312" at code page 936 is the GBK superset; Ruby's GB2312 is EUC-CN,
                // which is code page 51936.
                case 936: return "GBK";
                case 51936: return "GB2312";
                case 51949: return "EUC-KR";
                case 54936: return "GB18030";
                case 949: return "CP949";

                // Ruby's names for the Mac code pages; .NET spells them "x-mac-*".
                case 10000: return "macRoman";
                case 10001: return "MacJapanese";
                case 10006: return "macGreek";
                case 10007: return "macCyrillic";
                case 10010: return "macRomania";
                case 10017: return "macUkraine";
                case 10021: return "macThai";
                case 10029: return "macCentEuro";
                case 10079: return "macIceland";
                case 10081: return "macTurkish";
                case 10082: return "macCroatian";

                case 20866: return "KOI8-R";
                case 21866: return "KOI8-U";

                case 1250: return "Windows-1250";
                case 1251: return "Windows-1251";
                case 1252: return "Windows-1252";
                case 1253: return "Windows-1253";
                case 1254: return "Windows-1254";
                case 1255: return "Windows-1255";
                case 1256: return "Windows-1256";
                case 1257: return "Windows-1257";
                case 1258: return "Windows-1258";
                case 874: return "Windows-874";

                case 37: return "IBM037";
                case 437: return "IBM437";
                case 720: return "IBM720";
                case 862: return "IBM862";
                case 737: return "IBM737";
                case 775: return "IBM775";
                case 850: return "CP850";
                case 852: return "IBM852";
                case 855: return "IBM855";
                case 857: return "IBM857";
                case 860: return "IBM860";
                case 861: return "IBM861";
                case 863: return "IBM863";
                case 864: return "IBM864";
                case 865: return "IBM865";
                case 866: return "IBM866";
                case 869: return "IBM869";
                default: return null;
            }
        }

        public int CodePage {
            get { return GetCodePage(_encoding); }
        }

        public override string/*!*/ ToString() {
            return Name;
        }

        public int CompareTo(RubyEncoding/*!*/ other) {
            return _ordinal - other._ordinal;
        }

        public static RubyRegexOptions ToRegexOption(RubyEncoding encoding) {
            if (encoding == RubyEncoding.Binary) {
                return RubyRegexOptions.FIXED;
            }
            
            if (encoding == null) {
                return RubyRegexOptions.NONE;
            }

            switch (encoding.CodePage) {
                case RubyEncoding.CodePageSJIS: return RubyRegexOptions.SJIS;
                case RubyEncoding.CodePageEUCJP: return RubyRegexOptions.EUC;
                case RubyEncoding.CodePageUTF8: return RubyRegexOptions.UTF8;
            }

            throw Assert.Unreachable;
        }

        public static RubyEncoding GetRegexEncoding(RubyRegexOptions options) {
            switch (options & RubyRegexOptions.EncodingMask) {
                case RubyRegexOptions.EUC: return RubyEncoding.EUCJP;
                case RubyRegexOptions.SJIS: return RubyEncoding.SJIS;
                case RubyRegexOptions.UTF8: return RubyEncoding.UTF8;
                case RubyRegexOptions.FIXED: return RubyEncoding.Binary;
                default: return null;
            }
        }

        internal static int GetCodePage(int nameInitial) {
            switch (nameInitial) {
                case 'E':
                case 'e': return CodePageEUCJP;
                case 'S':
                case 's': return CodePageSJIS;
                case 'U':
                case 'u': return CodePageUTF8;
                default:
                    return -1;
            }
        }

        public static RubyEncoding GetEncodingByNameInitial(int initial) {
            int codepage = GetCodePage(initial);
            return codepage > 0 ? GetRubyEncoding(codepage) : null;
        }

        public void RequireAsciiIdentity() {
            if (!_isAsciiIdentity) {
                throw new NotSupportedException(String.Format("Encoding {0} (code page {1}) is not supported", Name, CodePage));
            }
        }

        private static Dictionary<int, RubyEncoding> _Encodings;
        
        public static RubyEncoding/*!*/ GetRubyEncoding(Encoding/*!*/ encoding) {
            ContractUtils.RequiresNotNull(encoding, "encoding");
            
            if (encoding.CodePage == 0) {
                if (encoding == BinaryEncoding.Instance) {
                    return Binary;
                }

                // TODO: allow custom encodings (without codepage)
            }

            return GetRubyEncoding(encoding.CodePage);
        }

        public static RubyEncoding/*!*/ GetRubyEncoding(int codepage) {
            switch (codepage) {
                case CodePageBinary: return Binary;
                case CodePageAscii: return Ascii;
                case CodePageUTF8: return UTF8;
                case CodePageSJIS: return SJIS;
                case CodePageEUCJP: return EUCJP;
            }

            if (_Encodings == null) {
                Interlocked.CompareExchange(ref _Encodings, new Dictionary<int, RubyEncoding>(), null); 
            }

            RubyEncoding result;
            lock (_Encodings) {
                if (!_Encodings.TryGetValue(codepage, out result)) {
                    result = new RubyEncoding(
                        CreateEncoding(codepage, false),
                        CreateEncoding(codepage, true),
                        codepage
                    );

                    _Encodings.Add(codepage, result);
                }
            }

            return result;
        }

        private static int GetCodePage(Encoding/*!*/ encoding) {
            return encoding.CodePage;
        }

        public static bool AsciiIdentity(Encoding/*!*/ encoding) {
            if (encoding == BinaryEncoding.Instance) {
                return true;
            }

            // A stateful encoding is never ASCII compatible even though it happens to encode the
            // ASCII repertoire as itself: an ASCII byte only means that character when the escape
            // state says so. Ruby calls these "dummy" encodings.
            if (IsDummyEncoding(encoding.CodePage)) {
                return false;
            }

            switch (encoding.CodePage) {
                case 437: // OEM United States
                case 708: // Arabic (ASMO 708)
                case 720: // Arabic (DOS)
                case 737: // Greek (DOS)
                case 775: // Baltic (DOS)
                case 850: // Western European (DOS)
                case 852: // Central European (DOS)
                case 855: // OEM Cyrillic
                case 857: // Turkish (DOS)
                case 858: // OEM Multilingual Latin I
                case 860: // Portuguese (DOS)
                case 861: // Icelandic (DOS)
                case 862: // Hebrew (DOS)
                case 863: // French Canadian (DOS)
                case 864: // Arabic (864)
                case 865: // Nordic (DOS)
                case 866: // Cyrillic (DOS)
                case 869: // Greek, Modern (DOS)
                case 874: // Thai (Windows)
                case 932: // Japanese (Shift-JIS)
                case 936: // Chinese Simplified (GB2312)
                case 949: // Korean
                case 950: // Chinese Traditional (Big5)
                case 1250: // Central European (Windows)
                case 1251: // Cyrillic (Windows)
                case 1252: // Western European (Windows)
                case 1253: // Greek (Windows)
                case 1254: // Turkish (Windows)
                case 1255: // Hebrew (Windows)
                case 1256: // Arabic (Windows)
                case 1257: // Baltic (Windows)
                case 1258: // Vietnamese (Windows)
                case 1361: // Korean (Johab)
                case 10000: // Western European (Mac)
                case 10001: // Japanese (Mac)
                case 10002: // Chinese Traditional (Mac)
                case 10003: // Korean (Mac)
                case 10004: // Arabic (Mac)
                case 10005: // Hebrew (Mac)
                case 10006: // Greek (Mac)
                case 10007: // Cyrillic (Mac)
                case 10008: // Chinese Simplified (Mac)
                case 10010: // Romanian (Mac)
                case 10017: // Ukrainian (Mac)
                case 10029: // Central European (Mac)
                case 10079: // Icelandic (Mac)
                case 10081: // Turkish (Mac)
                case 10082: // Croatian (Mac)
                case 20000: // Chinese Traditional (CNS)
                case 20001: // TCA Taiwan
                case 20002: // Chinese Traditional (Eten)
                case 20003: // IBM5550 Taiwan
                case 20004: // TeleText Taiwan
                case 20005: // Wang Taiwan
                case 20127: // US-ASCII
                case 20866: // Cyrillic (KOI8-R)
                case 20932: // Japanese (JIS 0208-1990 and 0212-1990)
                case 20936: // Chinese Simplified (GB2312-80)
                case 20949: // Korean Wansung
                case 21866: // Cyrillic (KOI8-U)
                case 28591: // Western European (ISO)
                case 28592: // Central European (ISO)
                case 28593: // Latin 3 (ISO)
                case 28594: // Baltic (ISO)
                case 28595: // Cyrillic (ISO)
                case 28596: // Arabic (ISO)
                case 28597: // Greek (ISO)
                case 28598: // Hebrew (ISO-Visual)
                case 28599: // Turkish (ISO)
                case 28603: // Estonian (ISO)
                case 28605: // Latin 9 (ISO)
                case 38598: // Hebrew (ISO-Logical)
                case 51932: // Japanese (EUC)
                case 51936: // Chinese Simplified (EUC)
                case 51949: // Korean (EUC)
                case 54936: // Chinese Simplified (GB18030)
                case 57002: // ISCII Devanagari
                case 57003: // ISCII Bengali
                case 57004: // ISCII Tamil
                case 57005: // ISCII Telugu
                case 57006: // ISCII Assamese
                case 57007: // ISCII Oriya
                case 57008: // ISCII Kannada
                case 57009: // ISCII Malayalam
                case 57010: // ISCII Gujarati
                case 57011: // ISCII Punjabi
                case 65001: // Unicode (UTF-8)
                    Debug.Assert(IsAsciiIdentityFallback(encoding));
                    return true;

                default: 
                    return IsAsciiIdentityFallback(encoding);
            }
        }

        private static string _AllAscii;

        /// <summary>
        /// Ruby's "dummy" encodings: stateful encodings that Ruby knows the name of but cannot
        /// operate on character by character. They are never ASCII compatible, because an ASCII
        /// byte in the middle of such a stream does not necessarily mean that character.
        /// </summary>
        public static bool IsDummyEncoding(int codepage) {
            switch (codepage) {
                case 50220: // ISO-2022-JP
                case 50221: // CP50221 (ISO-2022-JP allowing 1-byte kana)
                case 50222: // CP50222 (ISO-2022-JP, SO/SI)
                case 50225: // ISO-2022-KR
                case 50227: // ISO-2022-CN
                case CodePageISO2022JP2:
                case 52936: // HZ-GB-2312
                case 37:    // IBM037, EBCDIC - no ASCII byte means itself
                case CodePageUTF7:

                // UTF-16 and UTF-32 without an endianness suffix carry their byte order in a BOM,
                // so no character can be read out of them until that BOM has been seen. Ruby makes
                // them dummy encodings; the suffixed UTF-16LE/BE and UTF-32LE/BE are not.
                case CodePageUTF16:
                case CodePageUTF32:
                    return true;

                default:
                    return false;
            }
        }

        public bool IsDummy {
            get { return IsDummyEncoding(CodePage); }
        }

        /// <summary>
        /// Ruby ships some encodings without a converter to or from anything else: a string in
        /// one of them can only be transcoded while it holds nothing but ASCII, and asking for
        /// more is an Encoding::ConverterNotFoundError rather than an undefined conversion.
        /// </summary>
        public bool HasNoTranscoder {
            get {
                switch (CodePage) {
                    case CodePageEmacsMule:
                    case CodePageStatelessISO2022JP:
                    case CodePageEUCTW:
                    case CodePageGB1988:
                    case CodePageUTF7:
                        return true;

                    default:
                        return false;
                }
            }
        }

        private static bool IsAsciiIdentityFallback(Encoding/*!*/ encoding) {
            if (_AllAscii == null) {
                // all ASCII characters:
                var sb = new StringBuilder(0x80);
                for (int i = 0; i < 0x80; i++) {
                    sb.Append((char)i);
                }
                _AllAscii = sb.ToString();
            }

            var bytes = encoding.GetBytes(_AllAscii);
            if (bytes.Length != _AllAscii.Length) {
                return false;
            }

            for (int i = 0; i < _AllAscii.Length; i++) {
                if ((int)_AllAscii[i] != (int)bytes[i]) {
                    return false;
                }
            }

            return true;
        }

        public bool IsSingleByteCharacterSet {
            get {
                if (!_isSingleByteCharacterSet.HasValue) {
                    // A dummy encoding says nothing about characters - Ruby treats a string
                    // tagged with one as a string of bytes - so it counts as single byte here
                    // even where the underlying .NET encoding would gladly decode something.
                    _isSingleByteCharacterSet = IsSBCS(RubyOnlyEncodings.GetTableCodePage(CodePage)) || IsDummy;
                }

                return _isSingleByteCharacterSet.Value;
            }
        }

        public bool IsDoubleByteCharacterSet {
            get {
                if (!_isDoubleByteCharacterSet.HasValue) {
                    _isDoubleByteCharacterSet = IsDBCS(RubyOnlyEncodings.GetTableCodePage(CodePage));
                }

                return _isDoubleByteCharacterSet.Value;
            }
        }
        
        private static int[] _sbsc;
        private static int[] _dbsc; 

        private static bool IsSBCS(int codepage) {
            if (_sbsc == null) {
                _sbsc = new int[] {
                    0, 37, 437, 500, 708, 720, 737, 775, 850, 852, 855, 857, 858, 860, 861, 862, 863, 864, 865, 866, 869, 870, 874, 875, 1026, 
                    1047, 1140, 1141, 1142, 1143, 1144, 1145, 1146, 1147, 1148, 1149, 1250, 1251, 1252, 1253, 1254, 1255, 1256, 1257, 1258, 
                    10000, 10004, 10005, 10006, 10007, 10010, 10017, 10021, 10029, 10079, 10081, 10082, 20105, 20106, 20107, 20108, 20127, 
                    20269, 20273, 20277, 20278, 20280, 20284, 20285, 20290, 20297, 20420, 20423, 20424, 20833, 20838, 20866, 20871, 20880, 
                    20905, 20924, 21025, 21866, 28592, 28593, 28594, 28595, 28596, 28597, 28598, 28599, 28603, 28605, 29001, 38598
                };
            }

            return Array.BinarySearch(_sbsc, codepage) >= 0;
        }

        private static bool IsDBCS(int codepage) {
            if (_dbsc == null) {
                _dbsc = new int[] {
                    932, 936, 949, 950, 1361, 10001, 10002, 10003, 10008, 20000, 20001, 20002, 20003, 20004, 20005, 20261, 20932, 20936, 
                    20949, 50227, 51936, 51949
                };
            }

            return Array.BinarySearch(_dbsc, codepage) >= 0;
        }

        public bool InUnicodeBasicPlane {
            get {
                // TODO: others
                return this == Ascii || this == Binary;
            }
        }

        public bool IsUnicodeEncoding {
            get {
                switch (CodePage) {
                    case CodePageUTF7:
                    case CodePageUTF8:
                    case CodePageUTF8Mac:
                    case CodePageUTF16BE:
                    case CodePageUTF16LE:
                    case CodePageUTF32BE:
                    case CodePageUTF32LE:
                        return true;
                }
                return false;
            }
        }

        private static ReadOnlyDictionary<string, string> _aliases;

        public static ReadOnlyDictionary<string, string> Aliases {
            get { return _aliases ?? (_aliases = CreateAliases()); } 
        }

        private static ReadOnlyDictionary<string, string> CreateAliases() {
            return new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                { "646", "US-ASCII" }, 
                { "ASCII", "US-ASCII" }, 
                { "ANSI_X3.4-1968", "US-ASCII" }, 
                { "BINARY", "ASCII-8BIT" }, 
                { "CP437", "IBM437" }, 
                { "CP737", "IBM737" }, 
                { "CP775", "IBM775" }, 
                { "CP857", "IBM857" }, 
                { "CP860", "IBM860" }, 
                { "CP861", "IBM861" }, 
                { "CP862", "IBM862" }, 
                { "CP863", "IBM863" }, 
                { "CP864", "IBM864" },
                { "CP865", "IBM865" }, 
                { "CP866", "IBM866" }, 
                { "CP869", "IBM869" }, 
                { "CP874", "Windows-874" }, 
                { "CP878", "KOI8-R" }, 
                { "CP932", "Windows-31J" }, 
                { "CP936", "GBK" }, 
                { "CP720", "IBM720" }, 
                { "EUC-CN", "GB2312" }, 
                { "eucCN", "GB2312" }, 
                { "MacJapan", "MacJapanese" }, 
                { "ebcdic-cp-us", "IBM037" }, 
                { "CP950", "Big5" }, 
                { "CP951", "Big5-HKSCS" }, 
                { "CP1258", "Windows-1258" },
                { "CP1252", "Windows-1252" }, 
                { "CP1250", "Windows-1250" }, 
                { "CP1256", "Windows-1256" }, 
                { "CP1251", "Windows-1251" },
                { "CP1253", "Windows-1253" }, 
                { "CP1255", "Windows-1255" }, 
                { "CP1254", "Windows-1254" }, 
                { "CP1257", "Windows-1257" }, 
                { "CP65000", "UTF-7" }, 
                { "CP65001", "UTF-8" }, 
                { "IBM850", "CP850" }, 
                { "eucJP", "EUC-JP" }, 
                { "eucKR", "EUC-KR" }, 
                { "eucTW", "EUC-TW" }, 
                { "ISO2022-JP", "ISO-2022-JP" }, 
                { "ISO2022-JP2", "ISO-2022-JP-2" }, 
                { "ISO8859-1", "ISO-8859-1" }, 
                { "ISO8859-2", "ISO-8859-2" }, 
                { "ISO8859-3", "ISO-8859-3" }, 
                { "ISO8859-4", "ISO-8859-4" }, 
                { "ISO8859-5", "ISO-8859-5" }, 
                { "ISO8859-6", "ISO-8859-6" }, 
                { "ISO8859-7", "ISO-8859-7" }, 
                { "ISO8859-8", "ISO-8859-8" }, 
                { "ISO8859-9", "ISO-8859-9" }, 
                { "ISO8859-10", "ISO-8859-10" }, 
                { "ISO8859-11", "ISO-8859-11" }, 
                { "ISO8859-13", "ISO-8859-13" }, 
                { "ISO8859-14", "ISO-8859-14" }, 
                { "ISO8859-15", "ISO-8859-15" }, 
                { "ISO8859-16", "ISO-8859-16" }, 
                // MRI's SJIS is Windows-31J, not Shift_JIS
                { "SJIS", "Windows-31J" }, 
                { "PCK", "Windows-31J" }, 
                { "euc-jp-ms", "eucJP-ms" }, 
                { "csWindows31J", "Windows-31J" }, 
                // { "MacJapan", "MacJapanese" }, 
                { "UTF-8-MAC", "UTF8-MAC" }, 
                { "UTF-8-HFS", "UTF8-MAC" }, 
                { "UCS-2BE", "UTF-16BE" }, 
                { "UCS-4BE", "UTF-32BE" }, 
                { "UCS-4LE", "UTF-32LE" },  
            });
        }

        Expression/*!*/ IExpressionSerializable.CreateExpression() {
            // TODO: use static fields
            return Methods.CreateEncoding.OpCall(Expression.Constant(CodePage));
        }
    }

}
