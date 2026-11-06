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
using System.Collections.Concurrent;
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
    /// Frozen flag and instance variables need to be stored in per-runtime lookaside table.
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
        public const int CodePageISO8859_11 = 1028601;
        public const int CodePageISO8859_14 = 1028604;
        public const int CodePageISO8859_16 = 1028606;
        public const int CodePageBig5HKSCS = 1100950;
        public const int CodePageBig5UAO = 1101950;
        public const int CodePageCP950 = 1102950;
        public const int CodePageCP951 = 1103950;
        public const int CodePageCP852 = 1100852;
        public const int CodePageCP855 = 1100855;
        public const int CodePageEUCJIS2004 = 1052004;
        public const int CodePageCP50220 = 1050220;
        public const int CodePageISO2022JPKDDI = 1150220;
        public const int CodePageStatelessISO2022JPKDDI = 1102024;
        public const int CodePageUTF8DoCoMo = 1165001;
        public const int CodePageUTF8KDDI = 1165002;
        public const int CodePageUTF8SoftBank = 1165003;
        public const int CodePageSJISDoCoMo = 1100932;
        public const int CodePageSJISKDDI = 1101932;
        public const int CodePageSJISSoftBank = 1102932;

        // Ruby encodings .NET has a code page for, but that Ruby knows only by their structure.
        // Ruby ships no converter for these, so it never maps their bytes to characters: a byte
        // above 0x7F is just a byte there, which case mapping leaves alone.
        public const int CodePageMacJapanese = 10001;
        public const int CodePageMacCentEuro = 10029;
        public const int CodePageMacThai = 10021;
        public const int CodePageWindows1258 = 1258;

        #region Names

        [Flags]
        private enum NameFlags {
            None = 0,
            Alias = 1,
            Dummy = 2,
            // Ruby ships no converter to or from the encoding: only an all-ASCII string crosses.
            NoTranscoder = 4,
        }

        private readonly struct NameEntry {
            public readonly string/*!*/ Name;
            public readonly string Target;
            public readonly int CodePage;
            public readonly NameFlags Flags;

            public NameEntry(string/*!*/ name, string target, int codePage, NameFlags flags) {
                Name = name;
                Target = target;
                CodePage = codePage;
                Flags = flags;
            }
        }

        private static NameEntry E(string/*!*/ name, int codePage, NameFlags flags = NameFlags.None) {
            return new NameEntry(name, null, codePage, flags);
        }

        private static NameEntry A(string/*!*/ alias, string/*!*/ target) {
            return new NameEntry(alias, target, 0, NameFlags.Alias);
        }

        /// <summary>
        /// Every encoding name and alias MRI 4.0 knows, in the order MRI registers them, which is
        /// the order Encoding.list, Encoding.name_list, Encoding.aliases and Encoding#names report.
        /// An alias always follows the encoding it names.
        /// </summary>
        private static readonly NameEntry[]/*!*/ _nameTable = {
            E("ASCII-8BIT", CodePageBinary),
            E("UTF-8", CodePageUTF8),
            E("US-ASCII", CodePageAscii),
            E("UTF-16BE", CodePageUTF16BE),
            E("UTF-16LE", CodePageUTF16LE),
            E("UTF-32BE", CodePageUTF32BE),
            E("UTF-32LE", CodePageUTF32LE),
            E("UTF-16", CodePageUTF16, NameFlags.Dummy),
            E("UTF-32", CodePageUTF32, NameFlags.Dummy),
            E("UTF8-MAC", CodePageUTF8Mac),
            E("EUC-JP", CodePageEUCJP),
            E("Windows-31J", CodePageSJIS),
            E("Big5", CodePageBig5),
            E("Big5-HKSCS", CodePageBig5HKSCS),
            E("Big5-UAO", CodePageBig5UAO),
            E("CESU-8", CodePageCESU8),
            E("CP949", 949),
            E("Emacs-Mule", CodePageEmacsMule, NameFlags.NoTranscoder),
            E("EUC-KR", 51949),
            E("EUC-TW", CodePageEUCTW, NameFlags.NoTranscoder),
            E("GB18030", 54936),
            E("GBK", 936),
            E("ISO-8859-1", 28591),
            E("ISO-8859-2", 28592),
            E("ISO-8859-3", 28593),
            E("ISO-8859-4", 28594),
            E("ISO-8859-5", 28595),
            E("ISO-8859-6", 28596),
            E("ISO-8859-7", 28597),
            E("ISO-8859-8", 28598),
            E("ISO-8859-9", 28599),
            E("ISO-8859-10", CodePageISO8859_10),
            E("ISO-8859-11", CodePageISO8859_11),
            E("ISO-8859-13", 28603),
            E("ISO-8859-14", CodePageISO8859_14),
            E("ISO-8859-15", 28605),
            E("ISO-8859-16", CodePageISO8859_16),
            E("KOI8-R", 20866),
            E("KOI8-U", 21866),
            E("Shift_JIS", CodePageShiftJIS),
            E("Windows-1250", 1250),
            E("Windows-1251", 1251),
            E("Windows-1252", 1252),
            E("Windows-1253", 1253),
            E("Windows-1254", 1254),
            E("Windows-1257", 1257),
            A("BINARY", "ASCII-8BIT"),
            E("IBM437", 437),
            A("CP437", "IBM437"),
            E("IBM720", 720),
            A("CP720", "IBM720"),
            E("IBM737", 737),
            A("CP737", "IBM737"),
            E("IBM775", 775),
            A("CP775", "IBM775"),
            E("CP850", 850),
            A("IBM850", "CP850"),
            E("IBM852", 852),
            E("CP852", CodePageCP852),
            E("IBM855", 855),
            E("CP855", CodePageCP855),
            E("IBM857", 857),
            A("CP857", "IBM857"),
            E("IBM860", 860),
            A("CP860", "IBM860"),
            E("IBM861", 861),
            A("CP861", "IBM861"),
            E("IBM862", 862),
            A("CP862", "IBM862"),
            E("IBM863", 863),
            A("CP863", "IBM863"),
            E("IBM864", 864),
            A("CP864", "IBM864"),
            E("IBM865", 865),
            A("CP865", "IBM865"),
            E("IBM866", 866),
            A("CP866", "IBM866"),
            E("IBM869", 869),
            A("CP869", "IBM869"),
            E("Windows-1258", CodePageWindows1258, NameFlags.NoTranscoder),
            A("CP1258", "Windows-1258"),
            E("GB1988", CodePageGB1988, NameFlags.NoTranscoder),
            E("macCentEuro", CodePageMacCentEuro, NameFlags.NoTranscoder),
            E("macCroatian", 10082),
            E("macCyrillic", 10007),
            E("macGreek", 10006),
            E("macIceland", 10079),
            E("macRoman", 10000),
            E("macRomania", 10010),
            E("macThai", CodePageMacThai, NameFlags.NoTranscoder),
            E("macTurkish", 10081),
            E("macUkraine", 10017),
            E("CP950", CodePageCP950),
            A("Big5-HKSCS:2008", "Big5-HKSCS"),
            E("CP951", CodePageCP951),
            E("IBM037", 37, NameFlags.Dummy),
            A("ebcdic-cp-us", "IBM037"),
            // MRI has a converter for this one (through EUC-JP); IronRuby does not yet.
            E("stateless-ISO-2022-JP", CodePageStatelessISO2022JP, NameFlags.NoTranscoder),
            A("eucJP", "EUC-JP"),
            E("eucJP-ms", CodePageEucJpMs),
            A("euc-jp-ms", "eucJP-ms"),
            E("CP51932", CodePageCP51932),
            E("EUC-JIS-2004", CodePageEUCJIS2004),
            A("EUC-JISX0213", "EUC-JIS-2004"),
            A("eucKR", "EUC-KR"),
            A("eucTW", "EUC-TW"),
            E("GB2312", 51936),
            A("EUC-CN", "GB2312"),
            A("eucCN", "GB2312"),
            E("GB12345", CodePageGB12345),
            A("CP936", "GBK"),
            E("ISO-2022-JP", 50220, NameFlags.Dummy),
            A("ISO2022-JP", "ISO-2022-JP"),
            E("ISO-2022-JP-2", CodePageISO2022JP2, NameFlags.Dummy | NameFlags.NoTranscoder),
            A("ISO2022-JP2", "ISO-2022-JP-2"),
            E("CP50220", CodePageCP50220, NameFlags.Dummy),
            E("CP50221", 50221, NameFlags.Dummy),
            A("ISO8859-1", "ISO-8859-1"),
            A("ISO8859-2", "ISO-8859-2"),
            A("ISO8859-3", "ISO-8859-3"),
            A("ISO8859-4", "ISO-8859-4"),
            A("ISO8859-5", "ISO-8859-5"),
            A("ISO8859-6", "ISO-8859-6"),
            E("Windows-1256", 1256),
            A("CP1256", "Windows-1256"),
            A("ISO8859-7", "ISO-8859-7"),
            A("ISO8859-8", "ISO-8859-8"),
            E("Windows-1255", 1255),
            A("CP1255", "Windows-1255"),
            A("ISO8859-9", "ISO-8859-9"),
            A("ISO8859-10", "ISO-8859-10"),
            A("ISO8859-11", "ISO-8859-11"),
            E("TIS-620", CodePageTIS620),
            E("Windows-874", 874),
            A("CP874", "Windows-874"),
            A("ISO8859-13", "ISO-8859-13"),
            A("ISO8859-14", "ISO-8859-14"),
            A("ISO8859-15", "ISO-8859-15"),
            A("ISO8859-16", "ISO-8859-16"),
            A("CP878", "KOI8-R"),
            E("MacJapanese", CodePageMacJapanese, NameFlags.NoTranscoder),
            A("MacJapan", "MacJapanese"),
            A("ASCII", "US-ASCII"),
            A("ANSI_X3.4-1968", "US-ASCII"),
            A("646", "US-ASCII"),
            E("UTF-7", CodePageUTF7, NameFlags.Dummy | NameFlags.NoTranscoder),
            A("CP65000", "UTF-7"),
            A("CP65001", "UTF-8"),
            A("UTF-8-MAC", "UTF8-MAC"),
            A("UTF-8-HFS", "UTF8-MAC"),
            A("UCS-2BE", "UTF-16BE"),
            A("UCS-4BE", "UTF-32BE"),
            A("UCS-4LE", "UTF-32LE"),
            A("CP932", "Windows-31J"),
            A("csWindows31J", "Windows-31J"),
            A("SJIS", "Windows-31J"),
            A("PCK", "Windows-31J"),
            A("CP1250", "Windows-1250"),
            A("CP1251", "Windows-1251"),
            A("CP1252", "Windows-1252"),
            A("CP1253", "Windows-1253"),
            A("CP1254", "Windows-1254"),
            A("CP1257", "Windows-1257"),
            E("UTF8-DoCoMo", CodePageUTF8DoCoMo),
            E("SJIS-DoCoMo", CodePageSJISDoCoMo),
            E("UTF8-KDDI", CodePageUTF8KDDI),
            E("SJIS-KDDI", CodePageSJISKDDI),
            E("ISO-2022-JP-KDDI", CodePageISO2022JPKDDI, NameFlags.Dummy),
            // MRI has a converter for this one (through SJIS-KDDI); IronRuby does not yet.
            E("stateless-ISO-2022-JP-KDDI", CodePageStatelessISO2022JPKDDI, NameFlags.NoTranscoder),
            E("UTF8-SoftBank", CodePageUTF8SoftBank),
            E("SJIS-SoftBank", CodePageSJISSoftBank),
        };

        // must precede the encoding fields below: static field initializers run in textual order,
        // and Name needs these as soon as an encoding exists

        // name or alias (case-insensitively, as MRI looks them up) -> code page
        private static readonly Dictionary<string, int>/*!*/ _codePagesByName = BuildCodePagesByName();
        private static readonly Dictionary<int, NameEntry>/*!*/ _encodingsByCodePage = BuildEncodingsByCodePage();

        private static Dictionary<int, NameEntry>/*!*/ BuildEncodingsByCodePage() {
            var result = new Dictionary<int, NameEntry>();
            foreach (var entry in _nameTable) {
                if (entry.Target == null) {
                    result.Add(entry.CodePage, entry);
                }
            }
            return result;
        }

        private static Dictionary<string, int>/*!*/ BuildCodePagesByName() {
            var result = new Dictionary<string, int>(_nameTable.Length, StringComparer.OrdinalIgnoreCase);
            foreach (var entry in _nameTable) {
                result.Add(entry.Name, entry.Target == null ? entry.CodePage : result[entry.Target]);
            }
            return result;
        }

        /// <summary>
        /// The encoding MRI knows by this name or alias, matched case-insensitively; null for a
        /// name that is not one of MRI's. The names of .NET's own code pages ("ibm500",
        /// "x-mac-arabic", ...) are not Ruby encodings and are not found here.
        /// </summary>
        public static RubyEncoding FindByName(string/*!*/ name) {
            int codepage;
            return _codePagesByName.TryGetValue(name, out codepage) ? GetRubyEncoding(codepage) : null;
        }

        /// <summary>Every Ruby encoding, in MRI's order (Encoding.list).</summary>
        public static IEnumerable<RubyEncoding>/*!*/ GetAll() {
            foreach (var entry in _nameTable) {
                if (entry.Target == null) {
                    yield return GetRubyEncoding(entry.CodePage);
                }
            }
        }

        /// <summary>Every name and alias, in MRI's order (Encoding.name_list).</summary>
        public static IEnumerable<string>/*!*/ GetAllNames() {
            foreach (var entry in _nameTable) {
                yield return entry.Name;
            }
        }

        /// <summary>Every alias with the name of the encoding it stands for, in MRI's order (Encoding.aliases).</summary>
        public static IEnumerable<KeyValuePair<string, string>>/*!*/ GetAliases() {
            foreach (var entry in _nameTable) {
                if (entry.Target != null) {
                    yield return new KeyValuePair<string, string>(entry.Name, entry.Target);
                }
            }
        }

        /// <summary>The aliases of this encoding, in MRI's order.</summary>
        public IEnumerable<string>/*!*/ GetAliasNames() {
            string name = Name;
            foreach (var entry in _nameTable) {
                if (entry.Target == name) {
                    yield return entry.Name;
                }
            }
        }

        private static string _localeCharmap;
        private static bool _localeCharmapRead;

        /// <summary>
        /// The codeset of the locale the environment names (LC_ALL, LC_CTYPE, LANG), as the C
        /// library spells it - what MRI's Encoding.locale_charmap answers and its locale encoding
        /// is named by. A locale that is not installed is the C locale, "ANSI_X3.4-1968" on glibc,
        /// just as for MRI, which reads it the same way. Null where there is no nl_langinfo.
        /// </summary>
        public static string LocaleCharmap {
            get {
                if (!_localeCharmapRead) {
                    _localeCharmap = ReadLocaleCharmap();
                    _localeCharmapRead = true;
                }
                return _localeCharmap;
            }
        }

        private static string ReadLocaleCharmap() {
            int category, codeset;
            if (OperatingSystem.IsLinux()) {
                category = 0;    // LC_CTYPE
                codeset = 14;    // CODESET
            } else if (OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD()) {
                category = 2;
                codeset = 0;
            } else {
                return null;
            }

            try {
                // The process's own locale is put back afterwards: MRI sets LC_CTYPE for good, but
                // .NET has its own idea of culture and nothing else here reads the C locale.
                string saved = System.Runtime.InteropServices.Marshal.PtrToStringAnsi(setlocale(category, null));
                if (setlocale(category, "") == IntPtr.Zero) {
                    setlocale(category, "C");
                }
                string result = System.Runtime.InteropServices.Marshal.PtrToStringAnsi(nl_langinfo(codeset));
                if (saved != null) {
                    setlocale(category, saved);
                }
                return String.IsNullOrEmpty(result) ? null : result;
            } catch (DllNotFoundException) {
                return null;
            } catch (EntryPointNotFoundException) {
                return null;
            }
        }

        [System.Runtime.InteropServices.DllImport("libc")]
        private static extern IntPtr setlocale(int category, string locale);

        [System.Runtime.InteropServices.DllImport("libc")]
        private static extern IntPtr nl_langinfo(int item);

        private static NameFlags GetFlags(int codepage) {
            NameEntry entry;
            return _encodingsByCodePage.TryGetValue(codepage, out entry) ? entry.Flags : NameFlags.None;
        }

        #endregion

        // must precede the encoding fields below: field initializers run in textual order
        private static readonly bool _codePagesRegistered = RegisterCodePageProvider();

        private static bool RegisterCodePageProvider() {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            // Ruby exposes UTF-7; .NET 5+ disables it unless this switch is set
            AppContext.SetSwitch("System.Text.Encoding.EnableUnsafeUTF7Encoding", true);
            return true;
        }

        // the encodings other than the singletons below, created on first use (initialized before them, in textual order)
        private static readonly ConcurrentDictionary<int, RubyEncoding>/*!*/ _encodings = new ConcurrentDictionary<int, RubyEncoding>();

        // all ASCII characters, U+0000 to U+007F; AsciiIdentity encodes them, so the singletons below need it
        private static readonly string/*!*/ _allAscii = string.Create(0x80, 0, (chars, _) => {
            for (int i = 0; i < chars.Length; i++) {
                chars[i] = (char)i;
            }
        });

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
            var rubyOnly = RubyOnlyEncodings.TryCreate(codepage, throwOnError);
            if (rubyOnly != null) {
                return rubyOnly;
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

                return _escapingEncoding ?? (_escapingEncoding = new EscapingEncoding(
                    _strictEncoding is MultiByteEncoding multiByte ? multiByte.AsLossless() : _strictEncoding));
            }
        }

        /// <summary>
        /// Name as displayed by MRI.
        /// </summary>
        public string/*!*/ Name {
            get {
                NameEntry entry;
                return _encodingsByCodePage.TryGetValue(CodePage, out entry) ? entry.Name : _encoding.WebName;
            }
        }

        /// <summary>
        /// MRI's rb_enc_inspect_name: Ruby 3.4 renamed ASCII-8BIT to BINARY but kept #name answering
        /// the old spelling, so #inspect and the messages naming an encoding show both.
        /// </summary>
        public string/*!*/ InspectName {
            get { return this == Binary ? "BINARY (ASCII-8BIT)" : Name; }
        }

        public int CodePage {
            get { return _encoding.CodePage; }
        }

        public override string/*!*/ ToString() {
            return Name;
        }

        public int CompareTo(RubyEncoding/*!*/ other) {
            return _ordinal - other._ordinal;
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

        /// <summary>The encoding a -K switch names by its initial (-Ke, -Ks, -Ku).</summary>
        public static RubyEncoding GetEncodingByNameInitial(int initial) {
            switch (initial) {
                case 'E':
                case 'e': return EUCJP;
                case 'S':
                case 's': return SJIS;
                case 'U':
                case 'u': return UTF8;
                default: return null;
            }
        }

        public static RubyEncoding/*!*/ GetRubyEncoding(Encoding/*!*/ encoding) {
            ContractUtils.RequiresNotNull(encoding, "encoding");
            // TODO: allow custom encodings (without codepage); code page 0 is Binary's
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

            // two threads may race to create an encoding, but GetOrAdd hands both the instance it stored
            return _encodings.GetOrAdd(codepage, cp => new RubyEncoding(CreateEncoding(cp, false), CreateEncoding(cp, true), cp));
        }

        /// <summary>
        /// Whether the encoding maps each ASCII character to the byte of the same value. It encodes
        /// the ASCII repertoire to find out; that runs once per encoding, as RubyEncoding caches it.
        /// </summary>
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

            return IsAsciiIdentityFallback(encoding);
        }

        /// <summary>
        /// Ruby's "dummy" encodings: stateful encodings that Ruby knows the name of but cannot
        /// operate on character by character. They are never ASCII compatible, because an ASCII
        /// byte in the middle of such a stream does not necessarily mean that character. UTF-16
        /// and UTF-32 without an endianness suffix are dummies too: they carry their byte order
        /// in a BOM, so no character can be read out of them until that BOM has been seen.
        /// </summary>
        public static bool IsDummyEncoding(int codepage) {
            switch (codepage) {
                // .NET's stateful code pages that are not Ruby encodings, for a host that hands
                // one in: ISO-2022-JP with SO/SI, ISO-2022-KR, ISO-2022-CN, HZ-GB-2312.
                case 50222:
                case 50225:
                case 50227:
                case 52936:
                    return true;

                default:
                    return (GetFlags(codepage) & NameFlags.Dummy) != 0;
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
            get { return (GetFlags(CodePage) & NameFlags.NoTranscoder) != 0; }
        }

        private static bool IsAsciiIdentityFallback(Encoding/*!*/ encoding) {
            var bytes = encoding.GetBytes(_allAscii);
            if (bytes.Length != _allAscii.Length) {
                return false;
            }

            for (int i = 0; i < bytes.Length; i++) {
                if (bytes[i] != i) {
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
        
        private static readonly int[]/*!*/ _sbcs = new int[] {
            0, 37, 437, 500, 708, 720, 737, 775, 850, 852, 855, 857, 858, 860, 861, 862, 863, 864, 865, 866, 869, 870, 874, 875, 1026,
            1047, 1140, 1141, 1142, 1143, 1144, 1145, 1146, 1147, 1148, 1149, 1250, 1251, 1252, 1253, 1254, 1255, 1256, 1257, 1258,
            10000, 10004, 10005, 10006, 10007, 10010, 10017, 10021, 10029, 10079, 10081, 10082, 20105, 20106, 20107, 20108, 20127,
            20269, 20273, 20277, 20278, 20280, 20284, 20285, 20290, 20297, 20420, 20423, 20424, 20833, 20838, 20866, 20871, 20880,
            20905, 20924, 21025, 21866, 28592, 28593, 28594, 28595, 28596, 28597, 28598, 28599, 28603, 28605, 29001, 38598
        };

        private static readonly int[]/*!*/ _dbcs = new int[] {
            932, 936, 949, 950, 1361, 10001, 10002, 10003, 10008, 20000, 20001, 20002, 20003, 20004, 20005, 20261, 20932, 20936,
            20949, 50227, 51936, 51949
        };

        private static bool IsSBCS(int codepage) {
            return Array.BinarySearch(_sbcs, codepage) >= 0;
        }

        private static bool IsDBCS(int codepage) {
            return Array.BinarySearch(_dbcs, codepage) >= 0;
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

        Expression/*!*/ IExpressionSerializable.CreateExpression() {
            // TODO: use static fields
            return Methods.CreateEncoding.OpCall(Expression.Constant(CodePage));
        }
    }

}
