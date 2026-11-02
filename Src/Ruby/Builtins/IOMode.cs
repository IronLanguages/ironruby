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
using System.IO;
using IronRuby.Runtime;
using IronRuby.Runtime.Conversions;
using System.Collections.Generic;
using System.Reflection;

namespace IronRuby.Builtins {
    [Flags]
    public enum IOMode {
        ReadOnly = 0,
        WriteOnly = 1,
        ReadWrite = 2,
        Closed = 3,
        ReadWriteMask = 3,

        WriteAppends = 0x08,

        // Accepted and ignored: they exist so File::NOCTTY, File::SYNC and
        // File::SHARE_DELETE can be OR'd into a mode without changing it.
        NonBlocking = 0x04,
        NoControllingTerminal = 0x10,
        Synchronized = 0x20,
        ShareDelete = 0x40,

        CreateIfNotExists = 0x100,
        Truncate = 0x200,
        ErrorIfExists = 0x400,

        PreserveEndOfLines = 0x8000,

        Default = ReadOnly
    }

    public struct IOInfo {
        private readonly IOMode? _mode;
        private readonly RubyEncoding _externalEncoding;
        private readonly RubyEncoding _internalEncoding;
        // Whether a mode string said "b" or "t". MRI refuses to be told the same thing twice,
        // so :binmode and :textmode contradict a mode string that already decided the question -
        // whatever they say. There is no room for it in IOMode: "t" leaves no trace there.
        private readonly bool _textOrBinarySpecified;

        public IOMode Mode { get { return _mode ?? IOMode.Default; } }
        public bool HasMode { get { return _mode.HasValue; } }
        public RubyEncoding ExternalEncoding { get { return _externalEncoding; } }
        public RubyEncoding InternalEncoding { get { return _internalEncoding; } }
        // Either side on its own counts: "internal_encoding: 'ISO-8859-1'" asks for a
        // conversion from whatever the external side turns out to be.
        public bool HasEncoding { get { return _externalEncoding != null || _internalEncoding != null; } }

        public IOInfo(IOMode mode) 
            : this(mode, null, null) {
        }

        public IOInfo(IOMode? mode, RubyEncoding externalEncoding, RubyEncoding internalEncoding)
            : this(mode, externalEncoding, internalEncoding, false) {
        }

        public IOInfo(IOMode? mode, RubyEncoding externalEncoding, RubyEncoding internalEncoding, bool textOrBinarySpecified) {
            _mode = mode;
            _externalEncoding = externalEncoding;
            _internalEncoding = internalEncoding;
            _textOrBinarySpecified = textOrBinarySpecified;
        }

        public static IOInfo Parse(RubyContext/*!*/ context, MutableString/*!*/ modeAndEncoding) {
            if (!modeAndEncoding.IsAscii()) {
                throw IOModeEnum.IllegalMode(modeAndEncoding.ToAsciiString());
            }

            string[] parts = modeAndEncoding.ToString().Split(':');
            bool textOrBinary;
            IOMode mode = IOModeEnum.Parse(parts[0], out textOrBinary);
            return new IOInfo(
                mode,
                (parts.Length > 1) ? TryParseEncoding(context, parts[1]) : null,
                (parts.Length > 2) ? TryParseEncoding(context, parts[2]) : null,
                textOrBinary
            );
        }

        public IOInfo AddModeAndEncoding(RubyContext/*!*/ context, MutableString/*!*/ modeAndEncoding) {
            IOInfo info = Parse(context, modeAndEncoding);
            if (_mode.HasValue) {
                throw RubyExceptions.CreateArgumentError("mode specified twice");
            }

            if (!HasEncoding) {
                return info;
            }

            if (!info.HasEncoding) {
                return new IOInfo(info.Mode, _externalEncoding, _internalEncoding, info._textOrBinarySpecified);
            }

            throw RubyExceptions.CreateArgumentError("encoding specified twice");
        }

        public IOInfo AddEncoding(RubyContext/*!*/ context, MutableString/*!*/ encoding) {
            if (!encoding.IsAscii()) {
                context.ReportWarning(String.Format("Unsupported encoding {0} ignored", encoding.ToAsciiString()));
                return this;
            }

            if (HasEncoding) {
                throw RubyExceptions.CreateArgumentError("encoding specified twice");
            }

            string[] parts = encoding.ToString().Split(':');
            return new IOInfo(
                _mode,
                TryParseEncoding(context, parts[0]),
                (parts.Length > 1) ? TryParseEncoding(context, parts[1]) : null,
                _textOrBinarySpecified
            );
        }

        public static RubyEncoding TryParseEncoding(RubyContext/*!*/ context, string/*!*/ str) {
            // "r:UTF-8:-" - an empty internal encoding, or the "-" MRI spells it with, means
            // "no transcoding", not an unknown encoding.  RubyGems opens every gemspec that
            // way (Gem.open_file(file, "r:UTF-8:-")), so warning here is both wrong and loud.
            if (str.Length == 0 || str == "-") {
                return null;
            }

            // "r:BOM|UTF-8": the encoding after the bar, which is what Psych.load_file and
            // YAML.load_file open every file with. (The byte order mark itself is not skipped
            // here; Psych's parser skips it.)
            if (str.StartsWith("BOM|", StringComparison.OrdinalIgnoreCase)) {
                str = str.Substring(4);
            }

            try {
                return context.GetRubyEncoding(str);
            } catch (ArgumentException) {
                context.ReportWarning(String.Format("Unsupported encoding {0} ignored", str));
                return null;
            }
        }

        private static RubyEncoding ToEncoding(ConversionStorage<MutableString>/*!*/ toStr, object value) {
            if (value == null) {
                return null;
            }
            var encoding = value as RubyEncoding;
            if (encoding != null) {
                return encoding;
            }
            var name = Protocols.CastToString(toStr, value);
            // "-" is MRI's way of spelling "no internal encoding".
            return name.ToString() == "-" ? null : TryParseEncoding(toStr.Context, name.ToString());
        }

        public IOInfo AddOptions(ConversionStorage<MutableString>/*!*/ toStr, IDictionary<object, object> options) {
            var context = toStr.Context;

            IOInfo result = this;
            object optionValue;

            // :external_encoding and :internal_encoding say separately what "ext:int" says
            // together, and either of them may appear on its own. They also outrank :encoding,
            // which MRI warns about and ignores rather than refusing.
            object externalValue, internalValue;
            bool hasExternal = options.TryGetValue(context.CreateAsciiSymbol("external_encoding"), out externalValue);
            bool hasInternal = options.TryGetValue(context.CreateAsciiSymbol("internal_encoding"), out internalValue);

            if (options.TryGetValue(context.CreateAsciiSymbol("encoding"), out optionValue) && optionValue != null) {
                if (hasExternal || hasInternal) {
                    context.ReportWarning(String.Format("Ignoring encoding parameter '{0}': {1}_encoding is used",
                        EncodingOptionName(toStr, optionValue), hasExternal ? "external" : "internal"));
                } else {
                    result = result.AddEncoding(context, toStr, optionValue);
                }
            }

            // A nil :mode is MRI's way of saying "no mode here", not an empty one.
            if (options.TryGetValue(context.CreateAsciiSymbol("mode"), out optionValue) && optionValue != null) {
                // The option takes a File::Constants integer as readily as a string.
                var toIntStorage = new ConversionStorage<int?>(context);
                var toIntSite = toIntStorage.GetSite(TryConvertToFixnumAction.Make(context));
                int? numeric = toIntSite.Target(toIntSite, optionValue);
                // The integer is a platform O_* value - the same thing the positional mode
                // argument takes - so it has to be translated, not cast: O_CREAT is 64 on
                // Linux and 0x100 in IOMode.
                result = numeric.HasValue
                    ? result.AddMode(context, IOModeNative.ToIOMode(numeric.Value))
                    : result.AddModeAndEncoding(context, Protocols.CastToString(toStr, optionValue));
            }

            if (hasExternal || hasInternal) {
                // Encodings that came from the mode argument are a genuine conflict - unlike
                // :encoding, which MRI merely warns about.
                if (HasEncoding) {
                    throw RubyExceptions.CreateArgumentError("encoding specified twice");
                }

                // The mode is carried over as the nullable it is: turning it into a concrete mode
                // here would make a later "mode" option look like a second one.
                result = new IOInfo(result._mode,
                    hasExternal ? ToEncoding(toStr, externalValue) : null,
                    hasInternal ? ToEncoding(toStr, internalValue) : null,
                    result._textOrBinarySpecified
                );
            }

            result = result.AddTextOrBinaryMode(context, options);

            // The conversion options are checked when the stream opens, as MRI does, not when
            // the first transcoding happens to need them.
            CheckConversionOption(context, options, "invalid", "unknown value for invalid character option");
            CheckConversionOption(context, options, "undef", "unknown value for undefined character option");
            if (options.TryGetValue(context.CreateAsciiSymbol("newline"), out optionValue) && optionValue != null) {
                var newline = optionValue as RubySymbol;
                string newlineName = newline != null ? newline.ToString() : null;
                if (newlineName != "universal" && newlineName != "crlf" && newlineName != "cr" && newlineName != "lf") {
                    // only a Symbol gets named in the message
                    throw newlineName != null
                        ? RubyExceptions.CreateArgumentError("unexpected value for newline option: {0}", newlineName)
                        : RubyExceptions.CreateArgumentError("unexpected value for newline option");
                }
            }

            // :newline is a text-mode decorator, so it contradicts "b".
            if (options.TryGetValue(context.CreateAsciiSymbol("newline"), out optionValue)
                && (result.Mode & IOMode.PreserveEndOfLines) != 0) {
                throw RubyExceptions.CreateArgumentError("newline decorator with binary mode");
            }

            // :flags is OR'd into whatever the mode argument already said.
            if (options.TryGetValue(context.CreateAsciiSymbol("flags"), out optionValue)) {
                int extra = Protocols.CastToFixnum(new ConversionStorage<int>(context), optionValue);
                result = new IOInfo(result.Mode | IOModeNative.ToIOMode(extra), result.ExternalEncoding, result.InternalEncoding, result._textOrBinarySpecified);
            }

            return result;
        }

        // :invalid and :undef take :replace or nil and nothing else.
        private static void CheckConversionOption(RubyContext/*!*/ context, IDictionary<object, object>/*!*/ options, string/*!*/ name, string/*!*/ message) {
            object value;
            if (options.TryGetValue(context.CreateAsciiSymbol(name), out value) && value != null
                && !ReferenceEquals(value, context.CreateAsciiSymbol("replace"))) {
                throw RubyExceptions.CreateArgumentError(message);
            }
        }

        private IOInfo AddMode(RubyContext/*!*/ context, IOMode mode) {
            if (_mode.HasValue) {
                throw RubyExceptions.CreateArgumentError("mode specified twice");
            }
            return new IOInfo(mode, _externalEncoding, _internalEncoding, _textOrBinarySpecified);
        }

        /// <summary>
        /// :binmode and :textmode. MRI rejects them outright once a mode string has answered the
        /// question - even when they agree with it - and rejects asking for both at once.
        /// </summary>
        private IOInfo AddTextOrBinaryMode(RubyContext/*!*/ context, IDictionary<object, object> options) {
            object binValue, textValue;
            bool hasBin = options.TryGetValue(context.CreateAsciiSymbol("binmode"), out binValue);
            bool hasText = options.TryGetValue(context.CreateAsciiSymbol("textmode"), out textValue);
            if (!hasBin && !hasText) {
                return this;
            }

            bool binmode = hasBin && Protocols.IsTrue(binValue);
            bool textmode = hasText && Protocols.IsTrue(textValue);
            if (_textOrBinarySpecified) {
                // the mode string already said: repeating it is "twice", contradicting it is "both"
                bool modeIsBinary = (Mode & IOMode.PreserveEndOfLines) != 0;
                if ((hasBin && hasText) || hasBin != modeIsBinary) {
                    throw RubyExceptions.CreateArgumentError("both textmode and binmode specified");
                }
                throw RubyExceptions.CreateArgumentError("{0} specified twice", hasBin ? "binmode" : "textmode");
            }

            if (binmode && textmode) {
                throw RubyExceptions.CreateArgumentError("both textmode and binmode specified");
            }

            IOMode mode = Mode;
            if (binmode) {
                mode |= IOMode.PreserveEndOfLines;
            } else if (textmode) {
                mode &= ~IOMode.PreserveEndOfLines;
            }
            return new IOInfo(_mode.HasValue ? (IOMode?)mode : null, _externalEncoding, _internalEncoding, true);
        }

        /// <summary>The :encoding option as it reads back in MRI's warning about ignoring it.</summary>
        private static string EncodingOptionName(ConversionStorage<MutableString>/*!*/ toStr, object value) {
            var encoding = value as RubyEncoding;
            if (encoding != null) {
                return encoding.Name;
            }
            return Protocols.CastToString(toStr, value).ToString();
        }

        /// <summary>The :encoding option takes an Encoding object as readily as an "ext:int" string.</summary>
        private IOInfo AddEncoding(RubyContext/*!*/ context, ConversionStorage<MutableString>/*!*/ toStr, object value) {
            var encoding = value as RubyEncoding;
            if (encoding == null) {
                return AddEncoding(context, Protocols.CastToString(toStr, value));
            }

            if (HasEncoding) {
                throw RubyExceptions.CreateArgumentError("encoding specified twice");
            }
            return new IOInfo(_mode, encoding, null, _textOrBinarySpecified);
        }
    }

    /// <summary>
    /// The open(2) flag values the platform itself uses, which is what File::APPEND and the
    /// Fcntl::O_* constants have to be: Ruby hands them to fcntl(2) and reads them back from
    /// it, so a private numbering would report the wrong thing (and, passed to F_SETFL, set
    /// the wrong bit). IOMode stays IronRuby's own representation - it has to, since Unix has
    /// no O_BINARY and Windows no O_NONBLOCK - and these two methods translate.
    /// </summary>
    public static class IOModeNative {
        private static readonly bool Unix = System.IO.Path.DirectorySeparatorChar == '/';

        // Linux bits/fcntl-linux.h on the left, the MSVCRT _O_* macros on the right.
        // Zero means the platform has no such flag: Linux has no O_BINARY and no
        // O_SHARE_DELETE, and MRI's File::BINARY and File::SHARE_DELETE are 0 there too.
        public static readonly int Append = Unix ? 1024 : 0x0008;
        public static readonly int NonBlocking = Unix ? 2048 : 0x0004;
        public static readonly int NoControllingTerminal = Unix ? 256 : 0x0010;
        public static readonly int Synchronized = Unix ? 1052672 : 0x0020;
        public static readonly int ShareDelete = Unix ? 0 : 0x0040;
        public static readonly int CreateIfNotExists = Unix ? 64 : 0x0100;
        public static readonly int Truncate = Unix ? 512 : 0x0200;
        public static readonly int ErrorIfExists = Unix ? 128 : 0x0400;
        public static readonly int Binary = Unix ? 0 : 0x8000;

        // O_ACCMODE, and the same three values on both platforms.
        public static readonly int AccessMask = (int)IOMode.ReadWriteMask;

        private static bool Has(int flags, int flag) {
            return flag != 0 && (flags & flag) == flag;
        }

        /// <summary>
        /// The IOMode a numeric mode from Ruby - File.open(path, File::WRONLY | File::CREAT) -
        /// stands for. Flags IronRuby has no representation for (O_NOFOLLOW, O_DIRECT, ...) are
        /// dropped rather than refused, which is how the ones it already ignores behave.
        /// </summary>
        public static IOMode ToIOMode(int flags) {
            IOMode result = (IOMode)(flags & AccessMask);
            if (Has(flags, Append)) result |= IOMode.WriteAppends;
            if (Has(flags, NonBlocking)) result |= IOMode.NonBlocking;
            if (Has(flags, NoControllingTerminal)) result |= IOMode.NoControllingTerminal;
            if (Has(flags, Synchronized)) result |= IOMode.Synchronized;
            if (Has(flags, ShareDelete)) result |= IOMode.ShareDelete;
            if (Has(flags, CreateIfNotExists)) result |= IOMode.CreateIfNotExists;
            if (Has(flags, Truncate)) result |= IOMode.Truncate;
            if (Has(flags, ErrorIfExists)) result |= IOMode.ErrorIfExists;
            if (Has(flags, Binary)) result |= IOMode.PreserveEndOfLines;
            return result;
        }

        /// <summary>The inverse: the platform flags an IOMode stands for.</summary>
        public static int ToNativeFlags(IOMode mode) {
            int result = (int)mode & AccessMask;
            if ((mode & IOMode.WriteAppends) != 0) result |= Append;
            if ((mode & IOMode.NonBlocking) != 0) result |= NonBlocking;
            if ((mode & IOMode.NoControllingTerminal) != 0) result |= NoControllingTerminal;
            if ((mode & IOMode.Synchronized) != 0) result |= Synchronized;
            if ((mode & IOMode.ShareDelete) != 0) result |= ShareDelete;
            if ((mode & IOMode.CreateIfNotExists) != 0) result |= CreateIfNotExists;
            if ((mode & IOMode.Truncate) != 0) result |= Truncate;
            if ((mode & IOMode.ErrorIfExists) != 0) result |= ErrorIfExists;
            if ((mode & IOMode.PreserveEndOfLines) != 0) result |= Binary;
            return result;
        }
    }

    public static class IOModeEnum {
        public static bool IsClosed(this IOMode mode) {
            return (mode & IOMode.ReadWriteMask) == IOMode.Closed;
        }

        public static bool CanRead(this IOMode mode) {
            return ((int)mode & 1) == 0;
        }

        public static bool CanWrite(this IOMode mode) {
            return ((int)mode & 1) != (((int)mode >> 1) & 1);
        }

        public static IOMode Close(this IOMode mode) {
            return (mode & ~IOMode.ReadWriteMask) | IOMode.Closed;
        }

        public static IOMode CloseRead(this IOMode mode) {
            return (mode & ~IOMode.ReadWriteMask) | (mode.CanWrite() ? IOMode.WriteOnly : IOMode.Closed);
        }

        public static IOMode CloseWrite(this IOMode mode) {
            return (mode & ~IOMode.ReadWriteMask) | (mode.CanRead() ? IOMode.ReadOnly : IOMode.Closed);
        }

        public static FileAccess ToFileAccess(this IOMode mode) {
            switch (mode & IOMode.ReadWriteMask) {
                case IOMode.WriteOnly: return FileAccess.Write;
                case IOMode.ReadOnly: return FileAccess.Read;
                case IOMode.ReadWrite: return FileAccess.ReadWrite;
                default: throw RubyExceptions.CreateEINVAL("invalid access mode {0}", mode);
            }
        }

        public static IOMode Parse(MutableString mode) {
            return Parse(mode, IOMode.Default);
        }

        public static IOMode Parse(MutableString mode, IOMode defaultMode) {
            return (mode != null) ? IOModeEnum.Parse(mode.ToString()) : defaultMode;
        }

        public static IOMode Parse(string mode) {
            bool textOrBinary;
            return Parse(mode, out textOrBinary);
        }

        public static IOMode Parse(string mode, out bool textOrBinarySpecified) {
            textOrBinarySpecified = false;
            if (String.IsNullOrEmpty(mode)) {
                throw IllegalMode(mode);
            }

            IOMode result = IOMode.Default;
            bool plus = false, binary = false, text = false, exclusive = false;

            // The access letter comes first; after it MRI accepts the b/t/+ flags in
            // any order and tolerates repeats ("r+b" and "rb+" are both fine, so is
            // "r++").  Only "b" and "t" conflict, since they contradict each other.
            for (int i = 1; i < mode.Length; i++) {
                switch (mode[i]) {
                    case '+':
                        plus = true;
                        break;

                    case 'b':
                        if (text) throw IllegalMode(mode);
                        binary = true;
                        break;

                    case 't':
                        if (binary) throw IllegalMode(mode);
                        text = true;
                        break;

                    case 'x':
                        // O_EXCL; only meaningful after 'w', which is what CRuby enforces.
                        if (mode[0] != 'w') throw IllegalMode(mode);
                        exclusive = true;
                        break;

                    default:
                        throw IllegalMode(mode);
                }
            }

            textOrBinarySpecified = binary || text;

            if (binary) {
                result |= IOMode.PreserveEndOfLines;
            }

            if (exclusive) {
                result |= IOMode.ErrorIfExists;
            }

            switch (mode[0]) {
                case 'r':
                    return result | (plus ? IOMode.ReadWrite : IOMode.ReadOnly);

                case 'w':
                    return result | (plus ? IOMode.ReadWrite : IOMode.WriteOnly) | IOMode.Truncate | IOMode.CreateIfNotExists;

                case 'a':
                    return result | (plus ? IOMode.ReadWrite : IOMode.WriteOnly) | IOMode.WriteAppends | IOMode.CreateIfNotExists;

                default:
                    throw IllegalMode(mode);
            }
        }

        internal static Exception/*!*/ IllegalMode(string/*!*/ modeString) {
            return RubyExceptions.CreateArgumentError("invalid access mode {0}", modeString);
        }
    }
}
