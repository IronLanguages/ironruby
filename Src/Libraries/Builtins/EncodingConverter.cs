/* ****************************************************************************
 *
 * Encoding::Converter
 *
 * Ruby's transcoding engine implemented on top of System.Text.Encoder/Decoder.
 * A converter owns a conversion path - either a direct pair or
 * "source -> UTF-8 -> destination", mirroring CRuby's transcoder graph for the
 * pairs .NET can express - plus the stream state the incremental
 * #primitive_convert protocol needs:
 *
 *   _incomplete    bytes of a character the decoder has seen but not finished
 *   _putback       "read again" bytes rewound after an invalid byte sequence
 *   _pendingOutput converted bytes that did not fit in the destination buffer
 *
 * Errors are attributed to the stage of the path they occur in: a byte the
 * decoder rejects is reported against (path[0], path[1]), a character the
 * encoder cannot represent against (path[n-2], path[n-1]). That is what makes
 * Encoding::Converter.new("EUC-JP", "ISO-8859-1") report EUC-JP -> UTF-8 for a
 * bad byte, exactly as CRuby does.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using IronRuby.Runtime;
using IronRuby.Runtime.Conversions;
using Microsoft.Scripting.Runtime;

namespace IronRuby.Builtins {
    public static partial class RubyEncodingOps {

        /// <summary>
        /// Encoding::Converter
        /// </summary>
        [RubyClass("Converter", Inherits = typeof(Object))]
        public class RubyConverter {
            #region Flags and constants

            internal const int InvalidMaskFlag = 0x0f;
            internal const int InvalidReplaceFlag = 0x02;
            internal const int UndefMaskFlag = 0xf0;
            internal const int UndefReplaceFlag = 0x20;
            internal const int UndefHexCharRefFlag = 0x30;
            internal const int UniversalNewlineFlag = 0x100;
            internal const int CrlfNewlineFlag = 0x1000;
            internal const int CrNewlineFlag = 0x2000;
            internal const int XmlTextFlag = 0x8000;
            internal const int XmlAttrContentFlag = 0x10000;
            internal const int XmlAttrQuoteFlag = 0x100000;
            internal const int PartialInputFlag = 0x20000;
            internal const int AfterOutputFlag = 0x40000;

            [RubyConstant("INVALID_MASK")]
            public static readonly int InvalidMask = InvalidMaskFlag;

            [RubyConstant("INVALID_REPLACE")]
            public static readonly int InvalidReplace = InvalidReplaceFlag;

            [RubyConstant("UNDEF_MASK")]
            public static readonly int UndefMask = UndefMaskFlag;

            [RubyConstant("UNDEF_REPLACE")]
            public static readonly int UndefReplace = UndefReplaceFlag;

            [RubyConstant("UNDEF_HEX_CHARREF")]
            public static readonly int UndefHexCharRef = UndefHexCharRefFlag;

            [RubyConstant("PARTIAL_INPUT")]
            public static readonly int PartialInput = PartialInputFlag;

            [RubyConstant("AFTER_OUTPUT")]
            public static readonly int AfterOutput = AfterOutputFlag;

            [RubyConstant("UNIVERSAL_NEWLINE_DECORATOR")]
            public static readonly int UniversalNewlineDecorator = UniversalNewlineFlag;

            [RubyConstant("CRLF_NEWLINE_DECORATOR")]
            public static readonly int CrlfNewlineDecorator = CrlfNewlineFlag;

            [RubyConstant("CR_NEWLINE_DECORATOR")]
            public static readonly int CrNewlineDecorator = CrNewlineFlag;

            [RubyConstant("XML_TEXT_DECORATOR")]
            public static readonly int XmlTextDecorator = XmlTextFlag;

            [RubyConstant("XML_ATTR_CONTENT_DECORATOR")]
            public static readonly int XmlAttrContentDecorator = XmlAttrContentFlag;

            [RubyConstant("XML_ATTR_QUOTE_DECORATOR")]
            public static readonly int XmlAttrQuoteDecorator = XmlAttrQuoteFlag;

            private static readonly byte[]/*!*/ EmptyBytes = new byte[0];
            private static readonly char[]/*!*/ EmptyChars = new char[0];

            /// <summary>Marks "replace: nil", i.e. keep the encoding's default replacement.</summary>
            private static readonly object/*!*/ DefaultReplacementMarker = new object();

            #endregion

            #region State

            private RubyEncoding/*!*/[]/*!*/ _path;
            private readonly bool _noConversion;
            private int _flags;
            private int _sourceUnitSize;

            private Decoder/*!*/ _decoder;
            private Encoder/*!*/ _encoder;

            private readonly List<byte>/*!*/ _incomplete = new List<byte>();
            private readonly List<byte>/*!*/ _putback = new List<byte>();
            private readonly List<byte>/*!*/ _pendingOutput = new List<byte>();

            private MutableString/*!*/ _replacement;
            private byte[]/*!*/ _replacementBytes;

            private string/*!*/ _status = "source_buffer_empty";
            private byte[] _errorBytes, _readAgainBytes;
            private bool _errorAtEncodeStage;
            private Exception _lastError;
            private bool _finished;

            // set when the previous output byte was CR, so a CRLF split across two
            // calls is still collapsed by the universal-newline decorator
            private bool _pendingCarriageReturn;

            // set once the :xml => :attr decorator has written its opening quote, and once it has
            // written the closing one
            private bool _xmlQuoteOpened, _decoratorsFinished;

            #endregion

            #region Construction

            /// <summary>
            /// A null path is a converter that changes no encoding and only runs its decorators:
            /// MRI's Encoding::Converter.new("", "", ...). It works on bytes, which it hands back
            /// as ASCII-8BIT.
            /// </summary>
            internal RubyConverter(RubyEncoding/*!*/[] path, int flags) {
                _noConversion = (path == null);
                path = path ?? new[] { RubyEncoding.Binary, RubyEncoding.Binary };
                _path = path;
                _flags = flags;
                _sourceUnitSize = GetUnitSize(path[0]);
                _decoder = DecodeStageSource.TranscodingEncoding.GetDecoder();
                _encoder = EncodeStageDestination.TranscodingEncoding.GetEncoder();
                _replacement = DefaultReplacement(EncodeStageDestination);
                _replacementBytes = EncodeReplacement(_replacement);
            }

            /// <summary>
            /// Number of bytes in the smallest code unit of the source encoding. The decoder is fed
            /// one unit at a time so that the boundary of a broken character is known exactly.
            /// </summary>
            private static int GetUnitSize(RubyEncoding/*!*/ encoding) {
                switch (encoding.CodePage) {
                    case RubyEncoding.CodePageUTF16LE:
                    case RubyEncoding.CodePageUTF16BE:
                        return 2;

                    case RubyEncoding.CodePageUTF32LE:
                    case RubyEncoding.CodePageUTF32BE:
                        return 4;

                    default:
                        return 1;
                }
            }

            /// <summary>
            /// MRI's default replacement is U+FFFD for a Unicode destination and "?" for anything
            /// else, and it is spelled in UTF-8 or US-ASCII respectively rather than in the
            /// destination encoding - #replacement is a description of the replacement, not the
            /// bytes the conversion will actually insert.
            /// </summary>
            private static MutableString/*!*/ DefaultReplacement(RubyEncoding/*!*/ destination) {
                return destination.IsUnicodeEncoding ?
                    MutableString.CreateMutable("�", RubyEncoding.UTF8) :
                    MutableString.CreateAscii("?");
            }

            /// <summary>
            /// The encoding #replacement answers in once one has been set: the destination if it
            /// can hold ASCII, and UTF-8 if it cannot.
            /// </summary>
            private RubyEncoding/*!*/ ReplacementEncoding {
                get { return EncodeStageDestination.IsAsciiIdentity ? EncodeStageDestination : RubyEncoding.UTF8; }
            }

            /// <summary>The encodings the decoder half works between: source -&gt; (UTF-8 | destination).</summary>
            internal RubyEncoding/*!*/ DecodeStageSource { get { return _path[0]; } }
            internal RubyEncoding/*!*/ DecodeStageDestination { get { return _path[1]; } }

            /// <summary>The encodings the encoder half works between: (source | UTF-8) -&gt; destination.</summary>
            internal RubyEncoding/*!*/ EncodeStageSource { get { return _path[_path.Length - 2]; } }
            internal RubyEncoding/*!*/ EncodeStageDestination { get { return _path[_path.Length - 1]; } }

            internal RubyEncoding/*!*/ SourceEncoding { get { return _path[0]; } }
            internal bool IsNoConversion { get { return _noConversion; } }
            internal RubyEncoding/*!*/ DestinationEncoding { get { return _path[_path.Length - 1]; } }

            #endregion

            #region Conversion path

            /// <summary>
            /// The decorators a flag word asks for, in the order MRI lists them when it complains
            /// that it cannot build the converter.
            /// </summary>
            private static string/*!*/ DecoratorNames(int flags) {
                var names = new List<string>();
                if ((flags & UniversalNewlineFlag) != 0) { names.Add("universal_newline"); }
                if ((flags & CrlfNewlineFlag) != 0) { names.Add("crlf_newline"); }
                if ((flags & CrNewlineFlag) != 0) { names.Add("cr_newline"); }
                if ((flags & XmlTextFlag) != 0) { names.Add("xml_text"); }
                if ((flags & XmlAttrContentFlag) != 0) { names.Add("xml_attr_content"); }
                if ((flags & XmlAttrQuoteFlag) != 0) { names.Add("xml_attr_quote"); }
                return String.Join(",", names);
            }

            /// <summary>
            /// How MRI describes a converter, in #inspect and when it cannot build one: "UTF-8 to
            /// EUC-JP with crlf_newline", just the encoding named when the other is "", and just
            /// the decorators - or "no-conversion" - when both are.
            /// </summary>
            private static string/*!*/ Describe(string/*!*/ source, string/*!*/ destination, int flags) {
                string decorators = DecoratorNames(flags);
                string encodings = (source.Length == 0) ? destination :
                    (destination.Length == 0) ? source : source + " to " + destination;
                if (encodings.Length == 0) {
                    return decorators.Length == 0 ? "no-conversion" : decorators;
                }
                return decorators.Length == 0 ? encodings : encodings + " with " + decorators;
            }

            /// <summary>
            /// MRI has one answer for "that pair has no converter" and for "that is not an
            /// encoding at all", because both mean the same thing to it: there is no such code
            /// converter.  So the names are reported as they were given, unresolved.
            /// </summary>
            private static Exception/*!*/ ConverterNotFound(string/*!*/ source, string/*!*/ destination, int flags) {
                return new ConverterNotFoundError("code converter not found (" + Describe(source, destination, flags) + ")");
            }

            internal static RubyEncoding/*!*/[]/*!*/ SearchPath(RubyEncoding/*!*/ source, RubyEncoding/*!*/ destination) {
                if (source == destination || source.HasNoTranscoder || destination.HasNoTranscoder) {
                    throw ConverterNotFound(source.Name, destination.Name, 0);
                }

                return RubyExceptions.ConversionPath(source, destination);
            }

            /// <summary>
            /// Resolves both ends of a conversion together. Neither end can be resolved on its own
            /// because the error for an unresolvable one names them both.
            /// </summary>
            /// <remarks>
            /// Two empty names ask for no conversion at all, only the decorators: a converter can
            /// be made of that (null here), but there is no path to search.
            /// </remarks>
            private static RubyEncoding[] ResolvePath(ConversionStorage<MutableString>/*!*/ toStr,
                object source, object destination, int flags, bool allowNoConversion) {

                string sourceName = EncodingName(toStr, source);
                string destinationName = EncodingName(toStr, destination);
                if (sourceName.Length == 0 && destinationName.Length == 0 && allowNoConversion) {
                    return null;
                }
                if (sourceName.Length == 0 || destinationName.Length == 0) {
                    throw ConverterNotFound(sourceName, destinationName, flags);
                }
                RubyEncoding from = FindEncoding(toStr.Context, source, sourceName);
                RubyEncoding to = FindEncoding(toStr.Context, destination, destinationName);

                if (from == null || to == null || from == to) {
                    throw ConverterNotFound(sourceName, destinationName, flags);
                }
                if (from.HasNoTranscoder || to.HasNoTranscoder) {
                    // Ruby ships no converter for these, so there is no path to walk either.
                    throw ConverterNotFound(sourceName, destinationName, flags);
                }
                if (flags != 0 && (from == to)) {
                    throw ConverterNotFound(sourceName, destinationName, flags);
                }
                return RubyExceptions.ConversionPath(from, to);
            }

            private static string/*!*/ EncodingName(ConversionStorage<MutableString>/*!*/ toStr, object obj) {
                var encoding = obj as RubyEncoding;
                if (encoding != null) {
                    return encoding.Name;
                }
                if (obj == null) {
                    throw RubyExceptions.CreateTypeError("no implicit conversion of nil into String");
                }
                return Protocols.CastToString(toStr, obj).ToString();
            }

            private static RubyEncoding FindEncoding(RubyContext/*!*/ context, object obj, string/*!*/ name) {
                var encoding = obj as RubyEncoding;
                if (encoding != null) {
                    return encoding;
                }
                try {
                    return context.GetRubyEncoding(MutableString.CreateAscii(name));
                } catch (ArgumentException) {
                    return null;
                }
            }

            internal static RubyArray/*!*/ MakeConvPath(RubyEncoding/*!*/[] path, int flags) {
                var result = new RubyArray();
                for (int i = 0; path != null && i < path.Length - 1; i++) {
                    var pair = new RubyArray(2);
                    pair.Add(path[i]);
                    pair.Add(path[i + 1]);
                    result.Add(pair);
                }

                // The escapers run before the newline decorators, and #convpath lists them in the
                // order they run rather than in the order the options were given.
                if ((flags & XmlTextFlag) != 0) {
                    result.Add(MutableString.CreateAscii("xml_text_escape").Freeze());
                }
                if ((flags & XmlAttrContentFlag) != 0) {
                    result.Add(MutableString.CreateAscii("xml_attr_content_escape").Freeze());
                }
                if ((flags & XmlAttrQuoteFlag) != 0) {
                    result.Add(MutableString.CreateAscii("xml_attr_quote").Freeze());
                }
                if ((flags & UniversalNewlineFlag) != 0) {
                    result.Add(MutableString.CreateAscii("universal_newline").Freeze());
                }
                if ((flags & CrlfNewlineFlag) != 0) {
                    result.Add(MutableString.CreateAscii("crlf_newline").Freeze());
                }
                if ((flags & CrNewlineFlag) != 0) {
                    result.Add(MutableString.CreateAscii("cr_newline").Freeze());
                }
                return result;
            }

            internal RubyArray/*!*/ ConvPath() {
                return MakeConvPath(_noConversion ? null : _path, _flags);
            }

            #endregion

            #region Replacement

            internal MutableString/*!*/ Replacement {
                get { return _replacement; }
            }

            private byte[]/*!*/ EncodeReplacement(MutableString/*!*/ value) {
                string chars = value.ToString();
                try {
                    return EncodeStageDestination.TranscodingEncoding.GetBytes(chars);
                } catch (EncoderFallbackException) {
                    throw new UndefinedConversionError("replacement character setup failed");
                }
            }

            /// <summary>
            /// Sets the replacement string. Raises UndefinedConversionError, leaving the current
            /// replacement in place, if it cannot be represented in the destination encoding.
            /// </summary>
            internal void SetReplacement(MutableString/*!*/ value) {
                byte[] bytes = EncodeReplacement(value);
                _replacement = MutableString.CreateMutable(value.ConvertToString(), ReplacementEncoding);
                _replacementBytes = bytes;
            }

            #endregion

            #region Error bookkeeping

            private string/*!*/ UndefinedConversionMessage(int codepoint) {
                string u = "U+" + codepoint.ToString(codepoint > 0xffff ? "X" : "X4", CultureInfo.InvariantCulture);
                return RubyExceptions.UndefinedConversionMessage(u, _path, _path.Length - 2);
            }

            private string/*!*/ InvalidByteSequenceMessage(byte[]/*!*/ errorBytes, byte[]/*!*/ readAgain, bool incomplete) {
                return RubyExceptions.InvalidByteSequenceMessage(DecodeStageSource, errorBytes, readAgain, incomplete);
            }

            private void RecordSuccess(string/*!*/ status) {
                _status = status;
                _errorBytes = null;
                _readAgainBytes = null;
                _lastError = null;
            }

            private void RecordInvalidByteSequence(byte[]/*!*/ errorBytes, byte[]/*!*/ readAgain, bool incomplete) {
                _status = incomplete ? "incomplete_input" : "invalid_byte_sequence";
                _errorBytes = errorBytes;
                _readAgainBytes = readAgain;
                _errorAtEncodeStage = false;

                var error = new InvalidByteSequenceError(InvalidByteSequenceMessage(errorBytes, readAgain, incomplete));
                error.SourceEncoding = DecodeStageSource;
                error.DestinationEncoding = DecodeStageDestination;
                error.ErrorBytes = errorBytes;
                error.ReadAgainBytes = readAgain;
                error.IncompleteInput = incomplete;
                _lastError = error;
            }

            private void RecordUndefinedConversion(int codepoint, byte[]/*!*/ errorBytes) {
                _status = "undefined_conversion";
                _errorBytes = errorBytes;
                _readAgainBytes = EmptyBytes;
                _errorAtEncodeStage = true;

                // a leg out of EUC-JP (on the way to stateless-ISO-2022-JP) names the character by its bytes
                var error = new UndefinedConversionError(EncodeStageSource == RubyEncoding.EUCJP
                    ? RubyExceptions.UndefinedConversionMessage(RubyExceptions.InspectTranscodingBytes(errorBytes), _path, _path.Length - 2)
                    : UndefinedConversionMessage(codepoint));
                error.SourceEncoding = EncodeStageSource;
                error.DestinationEncoding = EncodeStageDestination;
                error.ErrorCharBytes = errorBytes;
                _lastError = error;
            }

            /// <summary>An undefined conversion on the first leg, which MRI names by its bytes.</summary>
            private void RecordUndefinedSourceCharacter(byte[]/*!*/ errorBytes) {
                _status = "undefined_conversion";
                _errorBytes = errorBytes;
                _readAgainBytes = EmptyBytes;
                _errorAtEncodeStage = false;

                var error = new UndefinedConversionError(
                    RubyExceptions.UndefinedConversionMessage(RubyExceptions.InspectTranscodingBytes(errorBytes), _path, 0));
                error.SourceEncoding = DecodeStageSource;
                error.DestinationEncoding = DecodeStageDestination;
                error.ErrorCharBytes = errorBytes;
                _lastError = error;
            }

            internal Exception LastError {
                get { return _lastError; }
            }

            internal RubyArray/*!*/ GetErrorInfo(RubyContext/*!*/ context) {
                var result = new RubyArray(5);
                result.Add(context.CreateAsciiSymbol(_status));
                if (_errorBytes == null) {
                    result.Add(null);
                    result.Add(null);
                    result.Add(null);
                    result.Add(null);
                } else {
                    RubyEncoding from = _errorAtEncodeStage ? EncodeStageSource : DecodeStageSource;
                    RubyEncoding to = _errorAtEncodeStage ? EncodeStageDestination : DecodeStageDestination;
                    result.Add(MutableString.CreateBinary(Encoding.ASCII.GetBytes(RubyExceptions.TranscoderName(from))));
                    result.Add(MutableString.CreateBinary(Encoding.ASCII.GetBytes(RubyExceptions.TranscoderName(to))));
                    result.Add(MutableString.CreateBinary(_errorBytes));
                    result.Add(MutableString.CreateBinary(_readAgainBytes ?? EmptyBytes));
                }
                return result;
            }

            #endregion

            #region putback, insert_output

            internal MutableString/*!*/ PutBack(int? count) {
                int n = count.HasValue ? Math.Min(Math.Max(count.Value, 0), _putback.Count) : _putback.Count;
                if (n == 0) {
                    return MutableString.CreateBinary(EmptyBytes, SourceEncoding);
                }

                // CRuby hands back the tail of the read-again buffer
                var bytes = _putback.GetRange(_putback.Count - n, n).ToArray();
                _putback.RemoveRange(_putback.Count - n, n);
                return MutableString.CreateBinary(bytes, SourceEncoding);
            }

            internal void InsertOutput(MutableString/*!*/ str) {
                byte[] bytes;
                if (str.Encoding == DestinationEncoding) {
                    bytes = str.ToByteArray();
                } else {
                    try {
                        bytes = DestinationEncoding.TranscodingEncoding.GetBytes(str.ToString());
                    } catch (EncoderFallbackException e) {
                        int codepoint = e.CharUnknownHigh != '\0' ?
                            Char.ConvertToUtf32(e.CharUnknownHigh, e.CharUnknownLow) : e.CharUnknown;
                        throw new UndefinedConversionError(UndefinedConversionMessage(codepoint));
                    }
                }
                _pendingOutput.InsertRange(0, bytes);
            }

            #endregion

            #region The conversion loop

            internal bool IsFinished {
                get { return _finished; }
            }

            /// <summary>
            /// The core of #primitive_convert. Consumes bytes from <paramref name="source"/> (which
            /// is mutated in place), appends converted bytes to <paramref name="destination"/> and
            /// returns CRuby's status name.
            /// </summary>
            internal string/*!*/ PrimitiveConvert(MutableString source, MutableString/*!*/ destination,
                int? byteOffset, int? byteLimit, int flags) {

                destination.RequireNotFrozen();

                byte[] existing = destination.ToByteArray();
                int offset = byteOffset ?? existing.Length;
                if (offset > existing.Length) {
                    throw RubyExceptions.CreateArgumentError("output_byteoffset too big");
                }
                if (offset < 0) {
                    throw RubyExceptions.CreateArgumentError("negative output_byteoffset");
                }
                if (_finished) {
                    return "finished";
                }

                int limit = byteLimit.HasValue ? Math.Max(byteLimit.Value, 0) : Int32.MaxValue;
                var written = new List<byte>();

                byte[] sourceBytes = source != null ? source.ToByteArray() : EmptyBytes;
                int prefix = _putback.Count;
                byte[] input;
                if (prefix == 0) {
                    input = sourceBytes;
                } else {
                    input = new byte[prefix + sourceBytes.Length];
                    _putback.CopyTo(input, 0);
                    Array.Copy(sourceBytes, 0, input, prefix, sourceBytes.Length);
                    _putback.Clear();
                }

                int consumed = 0;
                var chars = new char[16];
                string status = null;

                // whatever didn't fit last time goes out first
                if (!Drain(_pendingOutput, written, limit)) {
                    status = "destination_buffer_full";
                } else {
                    while (true) {
                        if (consumed >= input.Length) {
                            if (_incomplete.Count != 0 && (flags & PartialInputFlag) == 0 &&
                                (_flags & InvalidMaskFlag) == InvalidReplaceFlag) {
                                // A character cut short by the end of the input is an invalid byte
                                // sequence like any other, so :invalid => :replace covers it too
                                // and the conversion finishes rather than stopping on it.
                                ResetDecoder();
                                if (!Emit(_replacementBytes, written, limit)) {
                                    status = "destination_buffer_full";
                                    break;
                                }
                                status = "finished";
                            } else if (_incomplete.Count != 0 && (flags & PartialInputFlag) == 0) {
                                var errorBytes = _incomplete.ToArray();
                                ResetDecoder();
                                RecordInvalidByteSequence(errorBytes, EmptyBytes, true);
                                status = "incomplete_input";
                            } else if ((flags & PartialInputFlag) != 0) {
                                status = "source_buffer_empty";
                            } else {
                                status = "finished";
                            }
                            break;
                        }

                        int unit = Math.Min(_sourceUnitSize, input.Length - consumed);
                        int bytesUsed, charsUsed;
                        bool completed;
                        try {
                            _decoder.Convert(input, consumed, unit, chars, 0, chars.Length, false,
                                out bytesUsed, out charsUsed, out completed);
                        } catch (DecoderFallbackException) {
                            // A character of the right shape that the table leaves out (0x81 in
                            // Windows-1252, 0xA2AF in EUC-JP) is an undefined conversion on the
                            // first leg, not an invalid byte sequence.
                            var character = new byte[_incomplete.Count + unit];
                            _incomplete.CopyTo(character, 0);
                            Array.Copy(input, consumed, character, _incomplete.Count, unit);
                            if (!MutableString.CreateBinary(character, DecodeStageSource).ContainsInvalidCharacters()) {
                                consumed += unit;
                                ResetDecoder();
                                if ((_flags & UndefMaskFlag) == UndefReplaceFlag) {
                                    if (!Emit(_replacementBytes, written, limit)) {
                                        status = "destination_buffer_full";
                                        break;
                                    }
                                    continue;
                                }
                                RecordUndefinedSourceCharacter(character);
                                status = "undefined_conversion";
                                break;
                            }

                            byte[] errorBytes, readAgain;
                            if (_incomplete.Count != 0) {
                                errorBytes = _incomplete.ToArray();
                                readAgain = Slice(input, consumed, unit);
                            } else {
                                errorBytes = Slice(input, consumed, unit);
                                readAgain = EmptyBytes;
                            }
                            consumed += unit;
                            ResetDecoder();

                            if ((_flags & InvalidMaskFlag) == InvalidReplaceFlag) {
                                // The read-again bytes were never part of the error, only proof
                                // that it was one; they go back into the input rather than into
                                // the putback buffer, because the conversion carries straight on
                                // and they may well start a character of their own.
                                consumed -= readAgain.Length;
                                if (!Emit(_replacementBytes, written, limit)) {
                                    status = "destination_buffer_full";
                                    break;
                                }
                                continue;
                            }

                            _putback.Clear();
                            _putback.AddRange(readAgain);
                            RecordInvalidByteSequence(errorBytes, readAgain, false);
                            status = "invalid_byte_sequence";
                            break;
                        }

                        int chunkStart = consumed;
                        consumed += bytesUsed;
                        if (charsUsed == 0) {
                            for (int i = 0; i < bytesUsed; i++) {
                                _incomplete.Add(input[chunkStart + i]);
                            }
                            continue;
                        }

                        _incomplete.Clear();

                        // ASCII-8BIT's bytes above 0x7F are characters that mean nothing in any
                        // other encoding: MRI's first leg out of it refuses them as undefined.
                        if (DecodeStageSource == RubyEncoding.Binary && !_noConversion && chars[0] > 0x7f) {
                            if ((_flags & UndefMaskFlag) == UndefReplaceFlag) {
                                if (!Emit(_replacementBytes, written, limit)) {
                                    status = "destination_buffer_full";
                                    break;
                                }
                                continue;
                            }
                            RecordUndefinedSourceCharacter(new[] { (byte)chars[0] });
                            status = "undefined_conversion";
                            break;
                        }

                        string encodeStatus = EncodeChars(chars, charsUsed, written, limit);
                        if (encodeStatus != null) {
                            status = encodeStatus;
                            break;
                        }

                        if ((flags & AfterOutputFlag) != 0 && written.Count > 0) {
                            status = "after_output";
                            break;
                        }
                    }
                }

                if (status != "invalid_byte_sequence" && status != "incomplete_input" && status != "undefined_conversion") {
                    RecordSuccess(status);
                }
                // a conversion that has seen the end of its input is over: #convert refuses to go
                // on, and #finish has nothing left to give
                if (status == "finished") {
                    _finished = true;
                }

                // consume from the caller's source buffer
                if (source != null) {
                    int sourceConsumed = Math.Min(Math.Max(consumed - prefix, 0), sourceBytes.Length);
                    if (sourceConsumed > 0 || sourceBytes.Length == 0) {
                        var rest = Slice(sourceBytes, sourceConsumed, sourceBytes.Length - sourceConsumed);
                        var encoding = source.Encoding;
                        source.Clear();
                        if (rest.Length > 0) {
                            source.Append(rest);
                        }
                        source.ForceEncoding(encoding);
                    }
                }

                // rewrite the destination buffer: content up to the offset plus what we produced
                var converted = ApplyOutputDecorators(written.ToArray());
                if (status == "finished") {
                    var tail = FinishDecorators();
                    if (tail.Length > 0) {
                        var whole = new byte[converted.Length + tail.Length];
                        Array.Copy(converted, 0, whole, 0, converted.Length);
                        Array.Copy(tail, 0, whole, converted.Length, tail.Length);
                        converted = whole;
                    }
                }
                destination.Clear();
                if (offset > 0) {
                    destination.Append(existing, 0, offset);
                }
                if (converted.Length > 0) {
                    destination.Append(converted);
                }
                destination.ForceEncoding(DestinationEncoding);

                return status;
            }

            /// <summary>
            /// Encodes the characters the decoder produced. Returns null on success, or the status
            /// that terminates the conversion.
            /// </summary>
            private string EncodeChars(char[]/*!*/ chars, int count, List<byte>/*!*/ written, int limit) {
                int i = 0;
                while (i < count) {
                    int size = (Char.IsHighSurrogate(chars[i]) && i + 1 < count && Char.IsLowSurrogate(chars[i + 1])) ? 2 : 1;
                    int codepoint = size == 2 ? Char.ConvertToUtf32(chars[i], chars[i + 1]) : chars[i];

                    string escape = XmlEscape(codepoint);
                    if (escape != null) {
                        if (!Emit(Encoding.ASCII.GetBytes(escape), written, limit)) {
                            return "destination_buffer_full";
                        }
                        i += size;
                        continue;
                    }

                    byte[] bytes;
                    try {
                        bytes = EncodeCodepoint(chars, i, size);
                    } catch (EncoderFallbackException) {
                        ResetEncoder();

                        int undefMode = _flags & UndefMaskFlag;
                        if (undefMode == UndefReplaceFlag) {
                            if (!Emit(_replacementBytes, written, limit)) {
                                return "destination_buffer_full";
                            }
                            i += size;
                            continue;
                        }
                        if (undefMode == UndefHexCharRefFlag) {
                            string reference = "&#x" + codepoint.ToString("X", CultureInfo.InvariantCulture) + ";";
                            if (!Emit(Encoding.ASCII.GetBytes(reference), written, limit)) {
                                return "destination_buffer_full";
                            }
                            i += size;
                            continue;
                        }

                        RecordUndefinedConversion(codepoint, ErrorCharBytes(chars, i, size));
                        return "undefined_conversion";
                    }

                    if (!Emit(bytes, written, limit)) {
                        return "destination_buffer_full";
                    }
                    i += size;
                }
                return null;
            }

            private byte[]/*!*/ EncodeCodepoint(char[]/*!*/ chars, int index, int size) {
                var buffer = new byte[16 + EncodeStageDestination.MaxBytesPerChar * (size + 4)];
                int charsUsed, bytesUsed;
                bool completed;
                _encoder.Convert(chars, index, size, buffer, 0, buffer.Length, false, out charsUsed, out bytesUsed, out completed);
                return Slice(buffer, 0, bytesUsed);
            }

            /// <summary>The offending character, expressed in the source encoding of the failing stage.</summary>
            private byte[]/*!*/ ErrorCharBytes(char[]/*!*/ chars, int index, int size) {
                try {
                    return EncodeStageSource.TranscodingEncoding.GetBytes(chars, index, size);
                } catch (EncoderFallbackException) {
                    return Encoding.UTF8.GetBytes(new String(chars, index, size));
                }
            }

            /// <summary>
            /// Moves <paramref name="bytes"/> into the output, parking the tail in _pendingOutput
            /// if the destination limit is reached. Returns false if the buffer filled up.
            /// </summary>
            private bool Emit(byte[]/*!*/ bytes, List<byte>/*!*/ written, int limit) {
                int i = 0;
                while (i < bytes.Length && written.Count < limit) {
                    written.Add(bytes[i++]);
                }
                if (i < bytes.Length) {
                    for (int j = bytes.Length - 1; j >= i; j--) {
                        _pendingOutput.Insert(0, bytes[j]);
                    }
                    return false;
                }
                return true;
            }

            private static bool Drain(List<byte>/*!*/ pending, List<byte>/*!*/ written, int limit) {
                int i = 0;
                while (i < pending.Count && written.Count < limit) {
                    written.Add(pending[i++]);
                }
                if (i > 0) {
                    pending.RemoveRange(0, i);
                }
                return pending.Count == 0;
            }

            private static byte[]/*!*/ Slice(byte[]/*!*/ array, int start, int count) {
                if (count <= 0) {
                    return EmptyBytes;
                }
                var result = new byte[count];
                Array.Copy(array, start, result, 0, count);
                return result;
            }

            private void ResetDecoder() {
                _incomplete.Clear();
                _decoder = DecodeStageSource.TranscodingEncoding.GetDecoder();
            }

            private void ResetEncoder() {
                _encoder = EncodeStageDestination.TranscodingEncoding.GetEncoder();
            }

            /// <summary>
            /// The decorators all work on the converted bytes rather than on characters. They are
            /// only meaningful for ASCII-compatible destinations; anything else passes through
            /// unchanged.
            /// </summary>
            private byte[]/*!*/ ApplyOutputDecorators(byte[]/*!*/ bytes) {
                return ApplyNewlineDecorators(ApplyXmlDecorators(bytes));
            }

            /// <summary>
            /// The :xml => :attr decorator supplies the quotes around the attribute value. The
            /// opening one goes in front of the first byte the converter ever produces, so a
            /// converter finished without ever having converted anything writes both at once.
            /// </summary>
            private byte[]/*!*/ ApplyXmlDecorators(byte[]/*!*/ bytes) {
                if (bytes.Length == 0 || (_flags & XmlAttrQuoteFlag) == 0 ||
                    !DestinationEncoding.IsAsciiIdentity || _xmlQuoteOpened) {
                    return bytes;
                }

                _xmlQuoteOpened = true;
                var result = new byte[bytes.Length + 1];
                result[0] = (byte)'"';
                Array.Copy(bytes, 0, result, 1, bytes.Length);
                return result;
            }

            /// <summary>
            /// The escape a character needs before it can go into a text node or an attribute
            /// value, or null if it can be converted as it stands. This runs on the characters
            /// rather than on the converted bytes so that the ampersand of a numeric character
            /// reference the converter itself produced is not escaped a second time.
            /// </summary>
            private string XmlEscape(int codepoint) {
                if ((_flags & (XmlTextFlag | XmlAttrContentFlag)) == 0 || !DestinationEncoding.IsAsciiIdentity) {
                    return null;
                }
                bool attribute = (_flags & XmlAttrContentFlag) != 0;
                switch (codepoint) {
                    case '&': return "&amp;";
                    case '<': return "&lt;";
                    case '>': return "&gt;";
                    case '"': return attribute ? "&quot;" : null;
                    case '\'': return attribute ? "&apos;" : null;
                    default: return null;
                }
            }

            /// <summary>The bytes a decorator still owes the output once the input is exhausted.</summary>
            private byte[]/*!*/ FinishDecorators() {
                if ((_flags & XmlAttrQuoteFlag) == 0 || !DestinationEncoding.IsAsciiIdentity || _decoratorsFinished) {
                    return EmptyBytes;
                }
                _decoratorsFinished = true;
                byte[] result = _xmlQuoteOpened ? new byte[] { (byte)'"' } : new byte[] { (byte)'"', (byte)'"' };
                _xmlQuoteOpened = true;
                return result;
            }

            private byte[]/*!*/ ApplyNewlineDecorators(byte[]/*!*/ bytes) {
                if ((_flags & (UniversalNewlineFlag | CrlfNewlineFlag | CrNewlineFlag)) == 0 ||
                    !DestinationEncoding.IsAsciiIdentity) {
                    return bytes;
                }

                var result = new List<byte>(bytes.Length + 8);
                foreach (byte b in bytes) {
                    if ((_flags & UniversalNewlineFlag) != 0) {
                        if (_pendingCarriageReturn) {
                            _pendingCarriageReturn = false;
                            result.Add((byte)'\n');
                            if (b == (byte)'\n') {
                                continue;
                            }
                        }
                        if (b == (byte)'\r') {
                            _pendingCarriageReturn = true;
                            continue;
                        }
                        result.Add(b);
                        continue;
                    }

                    if (b == (byte)'\n') {
                        if ((_flags & CrlfNewlineFlag) != 0) {
                            result.Add((byte)'\r');
                            result.Add((byte)'\n');
                            continue;
                        }
                        if ((_flags & CrNewlineFlag) != 0) {
                            result.Add((byte)'\r');
                            continue;
                        }
                    }
                    result.Add(b);
                }
                return result.ToArray();
            }

            #endregion

            #region convert, finish

            internal MutableString/*!*/ Convert(MutableString/*!*/ str) {
                if (_finished) {
                    throw RubyExceptions.CreateArgumentError("converter already finished");
                }

                var source = MutableString.CreateBinary(str.ToByteArray());
                var destination = MutableString.CreateBinary(DestinationEncoding);
                while (true) {
                    string status = PrimitiveConvert(source, destination, null, null, PartialInputFlag);
                    if (status == "source_buffer_empty" || status == "finished") {
                        return destination;
                    }
                    if (status == "destination_buffer_full" || status == "after_output") {
                        continue;
                    }
                    throw _lastError;
                }
            }

            internal MutableString/*!*/ FinishConversion() {
                var destination = MutableString.CreateBinary(DestinationEncoding);
                if (_finished) {
                    return destination;
                }

                string status = PrimitiveConvert(null, destination, null, null, 0);
                if (status != "finished") {
                    _finished = true;
                    throw _lastError ?? RubyExceptions.CreateArgumentError("conversion failed");
                }

                // flush the encoder's shift state (e.g. the closing ESC ( B of ISO-2022-JP); only the
                // stateful (dummy) encodings have one, and the Encoder an Encoding gets by default
                // - most of RubyOnlyEncodings' - throws "Conversion buffer overflow" when asked to
                // convert nothing, having nothing to flush
                if (EncodeStageDestination.IsDummy) {
                    var buffer = new byte[64];
                    int charsUsed, bytesUsed = 0;
                    bool completed;
                    try {
                        _encoder.Convert(EmptyChars, 0, 0, buffer, 0, buffer.Length, true, out charsUsed, out bytesUsed, out completed);
                    } catch (ArgumentException) {
                        bytesUsed = 0;
                    }
                    if (bytesUsed > 0) {
                        destination.Append(buffer, 0, bytesUsed);
                        destination.ForceEncoding(DestinationEncoding);
                    }
                }

                var tail = FinishDecorators();
                if (tail.Length > 0) {
                    destination.Append(tail);
                    destination.ForceEncoding(DestinationEncoding);
                }

                _finished = true;
                return destination;
            }

            #endregion

            #region Argument coercion

            private static MutableString/*!*/ ToReplacementString(ConversionStorage<MutableString>/*!*/ toStr, object obj) {
                var str = obj as MutableString;
                if (str != null) {
                    return str;
                }
                if (obj == null || obj is bool || obj is int) {
                    throw RubyExceptions.CreateTypeError(String.Format(CultureInfo.InvariantCulture,
                        "no implicit conversion of {0} into String",
                        obj == null ? "nil" : toStr.Context.GetClassDisplayName(obj)));
                }
                return Protocols.CastToString(toStr, obj);
            }

            private static string KeyName(object key) {
                var symbol = key as RubySymbol;
                if (symbol != null) {
                    return symbol.ToString();
                }
                var str = key as MutableString;
                return str != null ? str.ToString() : null;
            }

            private static string SymbolName(object value) {
                var symbol = value as RubySymbol;
                if (symbol != null) {
                    return symbol.ToString();
                }
                var str = value as MutableString;
                return str != null ? str.ToString() : null;
            }

            /// <summary>
            /// Turns the trailing argument (an Integer flag word or an options Hash) into CRuby's
            /// flags. The replacement, if the Hash specifies one, comes back separately.
            /// </summary>
            private static int ParseOptions(ConversionStorage<IDictionary<object, object>>/*!*/ toHash,
                object options, out object replacement) {
                replacement = null;
                if (options == null || options == Missing.Value) {
                    return 0;
                }

                if (options is int) {
                    return (int)options;
                }

                var hash = options as IDictionary<object, object>;
                if (hash == null) {
                    // CRuby's ** applies #to_hash at the call site; do it here so that objects
                    // that only respond to #to_hash are accepted.
                    var site = toHash.GetSite(TryConvertToHashAction.Make(toHash.Context));
                    hash = site.Target(site, options);
                }
                if (hash == null) {
                    throw RubyExceptions.CreateTypeError("no implicit conversion into Hash");
                }

                int flags = 0;
                foreach (var entry in hash) {
                    switch (KeyName(entry.Key)) {
                        case "invalid":
                            if (SymbolName(entry.Value) == "replace") {
                                flags |= InvalidReplaceFlag;
                            } else if (entry.Value != null) {
                                throw RubyExceptions.CreateArgumentError("unknown value for invalid character option");
                            }
                            break;

                        case "undef":
                            if (SymbolName(entry.Value) == "replace") {
                                flags |= UndefReplaceFlag;
                            } else if (entry.Value != null) {
                                throw RubyExceptions.CreateArgumentError("unknown value for undefined character option");
                            }
                            break;

                        case "replace":
                            replacement = entry.Value ?? DefaultReplacementMarker;
                            break;

                        case "xml":
                            // :xml also decides what happens to a character the destination
                            // cannot hold: it becomes a numeric character reference, which is
                            // always representable, so :xml never raises on undefined input.
                            if (SymbolName(entry.Value) == "text") {
                                flags |= XmlTextFlag | UndefHexCharRefFlag;
                            } else if (SymbolName(entry.Value) == "attr") {
                                flags |= XmlAttrContentFlag | XmlAttrQuoteFlag | UndefHexCharRefFlag;
                            } else {
                                throw RubyExceptions.CreateArgumentError("unexpected value for xml option: {0}",
                                    (object)SymbolName(entry.Value) ?? toHash.Context.Inspect(entry.Value));
                            }
                            break;

                        case "universal_newline":
                            if (Protocols.IsTrue(entry.Value)) {
                                flags |= UniversalNewlineFlag;
                            }
                            break;

                        case "crlf_newline":
                            if (Protocols.IsTrue(entry.Value)) {
                                flags |= CrlfNewlineFlag;
                            }
                            break;

                        case "cr_newline":
                            if (Protocols.IsTrue(entry.Value)) {
                                flags |= CrNewlineFlag;
                            }
                            break;

                        case "newline":
                            switch (SymbolName(entry.Value)) {
                                case "universal": flags |= UniversalNewlineFlag; break;
                                case "crlf": flags |= CrlfNewlineFlag; break;
                                case "cr": flags |= CrNewlineFlag; break;
                                case "lf": flags |= UniversalNewlineFlag; break;
                                default:
                                    throw RubyExceptions.CreateArgumentError("unexpected value for newline option: {0}",
                                        (object)SymbolName(entry.Value) ?? toHash.Context.Inspect(entry.Value));
                            }
                            break;

                        case "partial_input":
                            if (Protocols.IsTrue(entry.Value)) {
                                flags |= PartialInputFlag;
                            }
                            break;

                        case "after_output":
                            if (Protocols.IsTrue(entry.Value)) {
                                flags |= AfterOutputFlag;
                            }
                            break;
                    }
                }
                return flags;
            }

            private static int? ToOptionalInt(ConversionStorage<int>/*!*/ toInt, object value) {
                if (value == null || value == Missing.Value) {
                    return null;
                }
                if (value is int) {
                    return (int)value;
                }
                return Protocols.CastToFixnum(toInt, value);
            }

            #endregion

            #region Ruby methods

            [RubyConstructor]
            public static RubyConverter/*!*/ Create(ConversionStorage<MutableString>/*!*/ toStr,
                ConversionStorage<IDictionary<object, object>>/*!*/ toHash, RubyClass/*!*/ self,
                object source, object destination, [Optional]object options) {

                object replacement;
                int flags = ParseOptions(toHash, options, out replacement);
                var path = ResolvePath(toStr, source, destination, flags, true);

                var result = new RubyConverter(path, flags);
                if (replacement != null && replacement != DefaultReplacementMarker) {
                    result.SetReplacement(ToReplacementString(toStr, replacement));
                }
                return result;
            }

            [RubyMethod("search_convpath", RubyMethodAttributes.PublicSingleton)]
            public static RubyArray/*!*/ SearchConvPath(ConversionStorage<MutableString>/*!*/ toStr,
                ConversionStorage<IDictionary<object, object>>/*!*/ toHash, RubyClass/*!*/ self,
                object source, object destination, [Optional]object options) {

                object replacement;
                int flags = ParseOptions(toHash, options, out replacement);
                var path = ResolvePath(toStr, source, destination, flags, false);
                return MakeConvPath(path, flags);
            }

            [RubyMethod("asciicompat_encoding", RubyMethodAttributes.PublicSingleton)]
            public static RubyEncoding AsciiCompatibleEncoding(ConversionStorage<MutableString>/*!*/ toStr, RubyClass/*!*/ self,
                object encoding) {

                RubyEncoding enc = encoding as RubyEncoding;
                if (enc == null) {
                    var name = Protocols.CastToString(toStr, encoding);
                    try {
                        enc = self.Context.GetRubyEncoding(name);
                    } catch (Exception) {
                        // names that resolve to no encoding (e.g. "internal" with no default_internal)
                        return null;
                    }
                    if (enc == null) {
                        return null;
                    }
                }

                // MRI answers from its transcoder tables: the encoding a converter out of an
                // ASCII incompatible one produces as its first step.
                switch (enc.CodePage) {
                    case RubyEncoding.CodePageUTF16BE:
                    case RubyEncoding.CodePageUTF16LE:
                    case RubyEncoding.CodePageUTF32BE:
                    case RubyEncoding.CodePageUTF32LE:
                    case RubyEncoding.CodePageUTF16:
                    case RubyEncoding.CodePageUTF32:
                    case RubyEncoding.CodePageCESU8:
                        return RubyEncoding.UTF8;

                    case 37: // IBM037
                        return RubyEncoding.GetRubyEncoding(28591);

                    case 50220: // ISO-2022-JP
                        return RubyEncoding.GetRubyEncoding(RubyEncoding.CodePageStatelessISO2022JP);

                    case RubyEncoding.CodePageISO2022JPKDDI:
                        return RubyEncoding.GetRubyEncoding(RubyEncoding.CodePageStatelessISO2022JPKDDI);

                    case RubyEncoding.CodePageCP50220:
                    case 50221: // CP50221
                        return RubyEncoding.GetRubyEncoding(RubyEncoding.CodePageCP51932);

                    default:
                        return null;
                }
            }

            [RubyMethod("source_encoding")]
            public static RubyEncoding GetSourceEncoding(RubyConverter/*!*/ self) {
                return self.IsNoConversion ? null : self.SourceEncoding;
            }

            [RubyMethod("destination_encoding")]
            public static RubyEncoding GetDestinationEncoding(RubyConverter/*!*/ self) {
                return self.IsNoConversion ? null : self.DestinationEncoding;
            }

            [RubyMethod("inspect")]
            public static MutableString/*!*/ Inspect(RubyConverter/*!*/ self) {
                string description = self.IsNoConversion ? Describe("", "", self._flags) :
                    Describe(self.SourceEncoding.Name, self.DestinationEncoding.Name, self._flags);
                return MutableString.CreateBinary(Encoding.ASCII.GetBytes("#<Encoding::Converter: " + description + ">"));
            }

            [RubyMethod("convpath")]
            public static RubyArray/*!*/ GetConvPath(RubyConverter/*!*/ self) {
                return self.ConvPath();
            }

            [RubyMethod("convert")]
            public static MutableString/*!*/ ConvertString(RubyConverter/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ str) {
                return self.Convert(str);
            }

            [RubyMethod("finish")]
            public static MutableString/*!*/ Finish(RubyConverter/*!*/ self) {
                return self.FinishConversion();
            }

            [RubyMethod("primitive_convert")]
            public static object PrimitiveConvert(
                ConversionStorage<int>/*!*/ toInt,
                ConversionStorage<IDictionary<object, object>>/*!*/ toHash,
                RubyContext/*!*/ context,
                RubyConverter/*!*/ self,
                MutableString source,
                [NotNull]MutableString/*!*/ destination,
                [Optional]object byteOffset,
                [Optional]object byteLimit,
                [Optional]object options) {

                int? offset = ToOptionalInt(toInt, byteOffset);
                int? limit = ToOptionalInt(toInt, byteLimit);
                object replacement;
                int flags = ParseOptions(toHash, options, out replacement);

                return context.CreateAsciiSymbol(self.PrimitiveConvert(source, destination, offset, limit, flags));
            }

            [RubyMethod("primitive_errinfo")]
            public static RubyArray/*!*/ GetPrimitiveErrorInfo(RubyContext/*!*/ context, RubyConverter/*!*/ self) {
                return self.GetErrorInfo(context);
            }

            [RubyMethod("last_error")]
            public static object GetLastError(RubyConverter/*!*/ self) {
                return self.LastError;
            }

            [RubyMethod("putback")]
            public static MutableString/*!*/ PutBackAll(RubyConverter/*!*/ self) {
                return self.PutBack(null);
            }

            [RubyMethod("putback")]
            public static MutableString/*!*/ PutBackCount(RubyConverter/*!*/ self, [DefaultProtocol]int count) {
                return self.PutBack(count);
            }

            [RubyMethod("insert_output")]
            public static object InsertOutput(RubyConverter/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ str) {
                self.InsertOutput(str);
                return null;
            }

            [RubyMethod("replacement")]
            public static MutableString/*!*/ GetReplacement(RubyConverter/*!*/ self) {
                return self.Replacement;
            }

            [RubyMethod("replacement=")]
            public static object SetReplacement(ConversionStorage<MutableString>/*!*/ toStr, RubyConverter/*!*/ self, object value) {
                self.SetReplacement(ToReplacementString(toStr, value));
                return value;
            }

            #endregion
        }
    }
}
