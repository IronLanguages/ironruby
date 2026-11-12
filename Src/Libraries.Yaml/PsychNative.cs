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
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using IronRuby.Builtins;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;

namespace IronRuby.StandardLibrary.Yaml {

    /// <summary>
    /// The call sites Psych::Parser#parse sends a handler's events through.
    /// </summary>
    public sealed class PsychHandlerSites : RubyCallSiteStorage {
        private CallSite<Func<CallSite, object, object, object, object, object, object>> _eventLocation;
        private CallSite<Func<CallSite, object, object, object>> _startStream;
        private CallSite<Func<CallSite, object, object, object, object, object>> _startDocument;
        private CallSite<Func<CallSite, object, object, object>> _endDocument;
        private CallSite<Func<CallSite, object, object, object>> _alias;
        private CallSite<Func<CallSite, object, object, object, object, object, object, object, object>> _scalar;
        private CallSite<Func<CallSite, object, object, object, object, object, object>> _startSequence;
        private CallSite<Func<CallSite, object, object>> _endSequence;
        private CallSite<Func<CallSite, object, object, object, object, object, object>> _startMapping;
        private CallSite<Func<CallSite, object, object>> _endMapping;
        private CallSite<Func<CallSite, object, object>> _endStream;

        public PsychHandlerSites(RubyContext/*!*/ context)
            : base(context) {
        }

        internal CallSite<Func<CallSite, object, object, object, object, object, object>>/*!*/ EventLocation {
            get { return RubyUtils.GetCallSite(ref _eventLocation, Context, "event_location", 4); }
        }

        internal CallSite<Func<CallSite, object, object, object>>/*!*/ StartStream {
            get { return RubyUtils.GetCallSite(ref _startStream, Context, "start_stream", 1); }
        }

        internal CallSite<Func<CallSite, object, object, object, object, object>>/*!*/ StartDocument {
            get { return RubyUtils.GetCallSite(ref _startDocument, Context, "start_document", 3); }
        }

        internal CallSite<Func<CallSite, object, object, object>>/*!*/ EndDocument {
            get { return RubyUtils.GetCallSite(ref _endDocument, Context, "end_document", 1); }
        }

        internal CallSite<Func<CallSite, object, object, object>>/*!*/ Alias {
            get { return RubyUtils.GetCallSite(ref _alias, Context, "alias", 1); }
        }

        internal CallSite<Func<CallSite, object, object, object, object, object, object, object, object>>/*!*/ Scalar {
            get { return RubyUtils.GetCallSite(ref _scalar, Context, "scalar", 6); }
        }

        internal CallSite<Func<CallSite, object, object, object, object, object, object>>/*!*/ StartSequence {
            get { return RubyUtils.GetCallSite(ref _startSequence, Context, "start_sequence", 4); }
        }

        internal CallSite<Func<CallSite, object, object>>/*!*/ EndSequence {
            get { return RubyUtils.GetCallSite(ref _endSequence, Context, "end_sequence", 0); }
        }

        internal CallSite<Func<CallSite, object, object, object, object, object, object>>/*!*/ StartMapping {
            get { return RubyUtils.GetCallSite(ref _startMapping, Context, "start_mapping", 4); }
        }

        internal CallSite<Func<CallSite, object, object>>/*!*/ EndMapping {
            get { return RubyUtils.GetCallSite(ref _endMapping, Context, "end_mapping", 0); }
        }

        internal CallSite<Func<CallSite, object, object>>/*!*/ EndStream {
            get { return RubyUtils.GetCallSite(ref _endStream, Context, "end_stream", 0); }
        }
    }

    // What psych.so is to CRuby's psych: the parser and the emitter, which upstream are libyaml
    // and here are IronRuby's own YAML engine (a port of the same algorithm). Everything else
    // in Psych is psych's own Ruby code (Src/StdLib/ironruby/psych), and the Ruby half of this
    // native layer is psych/ironruby.rb.
    public static partial class RubyYaml {

        #region Parser

        // Psych::Parser's encodings, libyaml's yaml_encoding_t.
        private const int AnyEncoding = 0;
        private const int Utf8Encoding = 1;
        private const int Utf16LEEncoding = 2;
        private const int Utf16BEEncoding = 3;

        /// <summary>
        /// Parses +yaml+ (a String, or an IO) and sends each event to +handler+, exactly as
        /// psych's native Psych::Parser#_native_parse does: the event's location first, then
        /// the event. +ioEncoding+ is what psych/ironruby.rb found an IO's external encoding to
        /// be (libyaml's yaml_encoding_t). Answers nil when the whole stream parsed, and the
        /// pieces of a Psych::SyntaxError - [line, column, offset, problem, context] - when it
        /// did not: the handler has seen every event before the error, as with libyaml, and
        /// psych/ironruby.rb raises the error itself.
        /// </summary>
        [RubyMethod("__native_parse", RubyMethodAttributes.PublicSingleton)]
        public static object NativeParse(PsychHandlerSites/*!*/ sites, RespondToStorage/*!*/ respondTo,
            RubyModule/*!*/ self, object parser, object handler, object yaml, [DefaultProtocol]int ioEncoding) {

            RubyContext context = self.Context;
            LibyamlParser engine;
            try {
                engine = new LibyamlParser(MakeScanner(respondTo, yaml, ioEncoding));
            } catch (InvalidCastException) {
                // An IO whose #read answers something other than a String.
                throw RubyExceptions.CreateTypeError("wrong argument type (expected String)");
            }
            context.SetInstanceVariable(parser, "@__scanner", engine.Scanner);

            RubyEncoding internalEncoding = context.DefaultInternalEncoding;

            while (true) {
                LibyamlEvent e;
                try {
                    e = engine.Parse();
                } catch (LibyamlException ex) {
                    // Psych reports the context mark, one-based; libyaml leaves it at the start
                    // of the input when an error has no context.
                    return NewArray(ex.ContextMark.Line + 1, ex.ContextMark.Column + 1, ex.ProblemOffset,
                        MutableString.CreateMutable(ex.Problem, RubyEncoding.UTF8),
                        ex.Context != null ? MutableString.CreateMutable(ex.Context, RubyEncoding.UTF8) : null);
                }
                if (e == null) {
                    return null;
                }

                var loc = sites.EventLocation;
                loc.Target(loc, handler, e.Start.Line, e.Start.Column, e.End.Line, e.End.Column);

                switch (e.Kind) {
                    case EmitterEventKind.StreamStart: {
                            var site = sites.StartStream;
                            site.Target(site, handler, e.Encoding);
                            break;
                        }

                    case EmitterEventKind.StreamEnd: {
                            var site = sites.EndStream;
                            site.Target(site, handler);
                            return null;
                        }

                    case EmitterEventKind.DocumentStart: {
                            RubyArray version = new RubyArray();
                            if (e.Version != null) {
                                version.Add(e.Version.Major);
                                version.Add(e.Version.Minor);
                            }
                            RubyArray directives = new RubyArray();
                            if (e.TagDirectives != null) {
                                foreach (var entry in e.TagDirectives) {
                                    directives.Add(NewArray(MakeString(entry.Key, internalEncoding), MakeString(entry.Value, internalEncoding)));
                                }
                            }
                            var site = sites.StartDocument;
                            site.Target(site, handler, version, directives, ScriptingRuntimeHelpers.BooleanToObject(e.Implicit));
                            break;
                        }

                    case EmitterEventKind.DocumentEnd: {
                            var site = sites.EndDocument;
                            site.Target(site, handler, ScriptingRuntimeHelpers.BooleanToObject(e.Implicit));
                            break;
                        }

                    case EmitterEventKind.Alias: {
                            var site = sites.Alias;
                            site.Target(site, handler, MakeString(e.Anchor, internalEncoding));
                            break;
                        }

                    case EmitterEventKind.Scalar: {
                            var site = sites.Scalar;
                            site.Target(site, handler,
                                MakeString(e.Value, internalEncoding),
                                MakeString(e.Anchor, internalEncoding),
                                MakeString(e.Tag, internalEncoding),
                                ScriptingRuntimeHelpers.BooleanToObject(e.PlainImplicit),
                                ScriptingRuntimeHelpers.BooleanToObject(e.QuotedImplicit),
                                (int)e.ScalarStyle);
                            break;
                        }

                    case EmitterEventKind.SequenceStart:
                    case EmitterEventKind.MappingStart: {
                            var site = (e.Kind == EmitterEventKind.MappingStart) ? sites.StartMapping : sites.StartSequence;
                            site.Target(site, handler,
                                MakeString(e.Anchor, internalEncoding),
                                MakeString(e.Tag, internalEncoding),
                                ScriptingRuntimeHelpers.BooleanToObject(e.Implicit),
                                (int)e.CollectionStyle);
                            break;
                        }

                    case EmitterEventKind.SequenceEnd: {
                            var site = sites.EndSequence;
                            site.Target(site, handler);
                            break;
                        }

                    case EmitterEventKind.MappingEnd: {
                            var site = sites.EndMapping;
                            site.Target(site, handler);
                            break;
                        }
                }
            }
        }

        /// <summary>
        /// [index, line, column] of how far +scanner+ has read, for Psych::Parser#mark.
        /// </summary>
        [RubyMethod("__native_mark", RubyMethodAttributes.PublicSingleton)]
        public static RubyArray/*!*/ NativeMark(RubyModule/*!*/ self, object scanner) {
            LibyamlScanner s = scanner as LibyamlScanner;
            Mark mark = (s != null) ? s.CurrentMark : default(Mark);
            return NewArray(mark.Index, mark.Line, mark.Column);
        }

        private static readonly Encoding/*!*/ StrictUTF8 = new UTF8Encoding(false, true);

        // The input decoded, as libyaml's reader would read it. A String is read in its own
        // encoding (psych_parser.c's transcode_string): UTF-16 as it says, UTF-8 and binary as
        // UTF-8, anything else transcoded to UTF-8 first. An IO is read whole, as UTF-16 when
        // psych/ironruby.rb found it says so, and otherwise with libyaml's guess: a UTF-16 byte
        // order mark, or UTF-8. Bytes that are not UTF-8 are an error the scanner raises when it
        // gets that far, as libyaml's reader does.
        private static LibyamlScanner/*!*/ MakeScanner(RespondToStorage/*!*/ respondTo, object yaml, int ioEncoding) {
            byte[] bytes;
            int encoding;
            MutableString str = yaml as MutableString;
            if (str != null) {
                switch (str.Encoding.CodePage) {
                    case 1200:
                        return new LibyamlScanner(Encoding.Unicode.GetString(str.ToByteArray()), Utf16LEEncoding, -1, null, 0);
                    case 1201:
                        return new LibyamlScanner(Encoding.BigEndianUnicode.GetString(str.ToByteArray()), Utf16BEEncoding, -1, null, 0);
                }
                if (!(str.Encoding == RubyEncoding.UTF8 || str.Encoding == RubyEncoding.Binary || str.Encoding == RubyEncoding.Ascii)) {
                    return new LibyamlScanner(str.ConvertToString(), Utf8Encoding, -1, null, 0);
                }
                bytes = str.ToByteArray();
                encoding = Utf8Encoding;
            } else {
                IOWrapper wrapper = RubyIOOps.CreateIOWrapper(respondTo, yaml, FileAccess.Read);
                var buffer = new MemoryStream();
                // Not Stream.CopyTo: it asks the wrapper for its length, which an IO need not know.
                byte[] chunk = new byte[16384];
                int read;
                while ((read = wrapper.Read(chunk, 0, chunk.Length)) > 0) {
                    buffer.Write(chunk, 0, read);
                }
                bytes = buffer.ToArray();

                encoding = ioEncoding;
                if (encoding == AnyEncoding || encoding == Utf8Encoding) {
                    // yaml_parser_determine_encoding: only an unlabelled stream is guessed at.
                    if (encoding == AnyEncoding && bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) {
                        encoding = Utf16LEEncoding;
                    } else if (encoding == AnyEncoding && bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) {
                        encoding = Utf16BEEncoding;
                    } else {
                        encoding = Utf8Encoding;
                    }
                }
                if (encoding == Utf16LEEncoding || encoding == Utf16BEEncoding) {
                    var utf16 = new UnicodeEncoding(encoding == Utf16BEEncoding, false, false);
                    int skip = (ioEncoding == AnyEncoding && bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF))) ? 2 : 0;
                    return new LibyamlScanner(utf16.GetString(bytes, skip, bytes.Length - skip), encoding, -1, null, 0);
                }
            }

            // UTF-8. yaml_parser_determine_encoding drops the byte order mark of a stream whose
            // encoding it had to guess; anywhere else the scanner skips it at the start of a line.
            int start = (ioEncoding == AnyEncoding && str == null && bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) ? 3 : 0;
            int bad = FindInvalidUtf8(bytes, start);
            if (bad < 0) {
                return new LibyamlScanner(StrictUTF8.GetString(bytes, start, bytes.Length - start), encoding, -1, null, 0);
            }
            // Everything before the first bad byte is readable; the scanner stops there.
            string prefix = StrictUTF8.GetString(bytes, start, bad - start);
            return new LibyamlScanner(prefix, encoding, CountCodePoints(prefix), "invalid leading UTF-8 octet", bad);
        }

        // Where the first byte that does not belong to a well-formed UTF-8 sequence is, or -1.
        private static int FindInvalidUtf8(byte[]/*!*/ bytes, int start) {
            int i = start;
            while (i < bytes.Length) {
                int b = bytes[i];
                int width = (b & 0x80) == 0 ? 1 : (b & 0xE0) == 0xC0 ? 2 : (b & 0xF0) == 0xE0 ? 3 : (b & 0xF8) == 0xF0 ? 4 : 0;
                if (width == 0 || i + width > bytes.Length) {
                    return i;
                }
                int value = (width == 1) ? b : (width == 2) ? (b & 0x1F) : (width == 3) ? (b & 0x0F) : (b & 0x07);
                for (int k = 1; k < width; k++) {
                    int t = bytes[i + k];
                    if ((t & 0xC0) != 0x80) {
                        return i;
                    }
                    value = (value << 6) | (t & 0x3F);
                }
                // Overlong forms, surrogates and values past U+10FFFF are not UTF-8 either.
                if ((width == 2 && value < 0x80) || (width == 3 && value < 0x800) || (width == 4 && value < 0x10000) ||
                    (value >= 0xD800 && value <= 0xDFFF) || value > 0x10FFFF) {
                    return i;
                }
                i += width;
            }
            return -1;
        }

        private static int CountCodePoints(string/*!*/ str) {
            int count = 0;
            for (int i = 0; i < str.Length; i++) {
                if (!(Char.IsLowSurrogate(str[i]) && i > 0 && Char.IsHighSurrogate(str[i - 1]))) {
                    count++;
                }
            }
            return count;
        }

        private static MutableString MakeString(string value, RubyEncoding internalEncoding) {
            if (value == null) {
                return null;
            }
            // Psych hands back UTF-8, transcoded to Encoding.default_internal when there is one.
            return MutableString.CreateMutable(value, internalEncoding ?? RubyEncoding.UTF8);
        }

        #endregion

        #region Emitter

        // Event kinds. Keep in sync with Psych::Emitter in psych/ironruby.rb.
        private const int StreamStartKind = 0;
        private const int StreamEndKind = 1;
        private const int DocumentStartKind = 2;
        private const int DocumentEndKind = 3;
        private const int AliasKind = 4;
        private const int ScalarKind = 5;
        private const int SequenceStartKind = 6;
        private const int SequenceEndKind = 7;
        private const int MappingStartKind = 8;
        private const int MappingEndKind = 9;

        /// <summary>
        /// A new emitter, set up as psych_emitter.c sets up libyaml's: +indent+ and +lineWidth+
        /// as yaml_emitter_set_indent and yaml_emitter_set_width take them.
        /// </summary>
        [RubyMethod("__emitter_open", RubyMethodAttributes.PublicSingleton)]
        public static LibyamlEmitter/*!*/ OpenEmitter(RubyModule/*!*/ self,
            [DefaultProtocol]int indent, [DefaultProtocol]int lineWidth, object canonical) {
            return new LibyamlEmitter(indent, lineWidth, Protocols.IsTrue(canonical));
        }

        /// <summary>
        /// Emits one event and answers the text written since the last call (often none: the
        /// emitter looks ahead a few events before it decides how to write one). An emitter
        /// error is a RuntimeError, as psych_emitter.c raises it.
        /// </summary>
        [RubyMethod("__emitter_emit", RubyMethodAttributes.PublicSingleton)]
        public static MutableString/*!*/ EmitEvent(RubyModule/*!*/ self, [NotNull]LibyamlEmitter/*!*/ emitter, [NotNull]IList/*!*/ ev) {
            if (ev.Count == 0) {
                throw RubyExceptions.CreateArgumentError("malformed emitter event");
            }
            return Emit(emitter, MakeEvent(ev)) ?? MutableString.CreateMutable(RubyEncoding.UTF8);
        }

        // The two events a document is mostly made of, without an event array in between: a
        // Psych::Emitter sends one of these for nearly every node. Each answers the text the
        // emitter wrote, or nil when it wrote none.

        /// <summary>
        /// psych_emitter.c's scalar: +value+ must be a String, +anchor+ and +tag+ nil or
        /// convertible to one, and +style+ a number.
        /// </summary>
        [RubyMethod("__emitter_scalar", RubyMethodAttributes.PublicSingleton)]
        public static MutableString EmitScalar(ConversionStorage<MutableString>/*!*/ stringCast, RubyModule/*!*/ self,
            [NotNull]LibyamlEmitter/*!*/ emitter, object value, object anchor, object tag, object plain, object quoted, object style) {

            MutableString str = value as MutableString;
            if (str == null) {
                throw RubyExceptions.CreateTypeError("wrong argument type {0} (expected String)", self.Context.GetClassDisplayName(value));
            }
            var e = new LibyamlEvent();
            e.Kind = EmitterEventKind.Scalar;
            e.Value = str.ConvertToString();
            e.Anchor = ToOptionalString(stringCast, anchor);
            e.Tag = ToOptionalString(stringCast, tag);
            e.PlainImplicit = IsTrue(plain);
            e.QuotedImplicit = IsTrue(quoted);
            e.ScalarStyle = (EmitterScalarStyle)ToStyle(self.Context, style);
            return Emit(emitter, e);
        }

        /// <summary>
        /// psych_emitter.c's start_sequence (+mapping+ false) and start_mapping.
        /// </summary>
        [RubyMethod("__emitter_collection", RubyMethodAttributes.PublicSingleton)]
        public static MutableString EmitCollectionStart(ConversionStorage<MutableString>/*!*/ stringCast, RubyModule/*!*/ self,
            [NotNull]LibyamlEmitter/*!*/ emitter, bool mapping, object anchor, object tag, object @implicit, object style) {

            var e = new LibyamlEvent();
            e.Kind = mapping ? EmitterEventKind.MappingStart : EmitterEventKind.SequenceStart;
            e.Anchor = ToOptionalString(stringCast, anchor);
            e.Tag = ToOptionalString(stringCast, tag);
            e.Implicit = IsTrue(@implicit);
            e.CollectionStyle = (EmitterCollectionStyle)ToStyle(self.Context, style);
            return Emit(emitter, e);
        }

        private static readonly LibyamlEvent/*!*/ _SequenceEnd = new LibyamlEvent { Kind = EmitterEventKind.SequenceEnd };
        private static readonly LibyamlEvent/*!*/ _MappingEnd = new LibyamlEvent { Kind = EmitterEventKind.MappingEnd };

        [RubyMethod("__emitter_end_collection", RubyMethodAttributes.PublicSingleton)]
        public static MutableString EmitCollectionEnd(RubyModule/*!*/ self, [NotNull]LibyamlEmitter/*!*/ emitter, bool mapping) {
            return Emit(emitter, mapping ? _MappingEnd : _SequenceEnd);
        }

        private static MutableString Emit(LibyamlEmitter/*!*/ emitter, LibyamlEvent/*!*/ e) {
            try {
                emitter.Emit(e);
            } catch (EmitterException ex) {
                throw new RuntimeError(ex.Message, ex);
            }
            return emitter.HasOutput ? TakeOutput(emitter) : null;
        }

        private static MutableString/*!*/ TakeOutput(LibyamlEmitter/*!*/ emitter) {
            string text = emitter.TakeOutput();
            switch (emitter.Encoding) {
                case Utf16LEEncoding:
                    // What libyaml writes for a UTF-16 stream; psych hands the bytes over as they are.
                    return MutableString.CreateBinary(Encoding.Unicode.GetBytes(text), RubyEncoding.UTF8);
                case Utf16BEEncoding:
                    return MutableString.CreateBinary(Encoding.BigEndianUnicode.GetBytes(text), RubyEncoding.UTF8);
                default:
                    return MutableString.CreateMutable(text, RubyEncoding.UTF8);
            }
        }

        // StringValue, or nil.
        private static string ToOptionalString(ConversionStorage<MutableString>/*!*/ stringCast, object obj) {
            if (obj == null) {
                return null;
            }
            MutableString str = obj as MutableString ?? Protocols.CastToString(stringCast, obj);
            return str.ConvertToString();
        }

        // NUM2INT.
        private static int ToStyle(RubyContext/*!*/ context, object obj) {
            if (obj is int) {
                return (int)obj;
            }
            if (obj is double) {
                return (int)(double)obj;
            }
            throw RubyExceptions.CreateTypeError("no implicit conversion of {0} into Integer", context.GetClassDisplayName(obj));
        }

        private static LibyamlEvent/*!*/ MakeEvent(IList/*!*/ ev) {
            var e = new LibyamlEvent();
            switch (ToInt(ev[0])) {
                case StreamStartKind:
                    // encoding
                    e.Kind = EmitterEventKind.StreamStart;
                    e.Encoding = ToInt(ev[1]);
                    break;

                case StreamEndKind:
                    e.Kind = EmitterEventKind.StreamEnd;
                    break;

                case DocumentStartKind:
                    // version, tag directives, implicit
                    e.Kind = EmitterEventKind.DocumentStart;
                    e.Version = ToVersion(ev[1]);
                    e.TagDirectives = ToTagDirectives(ev[2]);
                    e.Implicit = IsTrue(ev[3]);
                    break;

                case DocumentEndKind:
                    // implicit
                    e.Kind = EmitterEventKind.DocumentEnd;
                    e.Implicit = IsTrue(ev[1]);
                    break;

                case AliasKind:
                    e.Kind = EmitterEventKind.Alias;
                    e.Anchor = ToClrString(ev[1]);
                    break;

                case ScalarKind:
                    // value, anchor, tag, plain, quoted, style
                    e.Kind = EmitterEventKind.Scalar;
                    e.Value = ToClrString(ev[1]) ?? String.Empty;
                    e.Anchor = ToClrString(ev[2]);
                    e.Tag = ToClrString(ev[3]);
                    e.PlainImplicit = IsTrue(ev[4]);
                    e.QuotedImplicit = IsTrue(ev[5]);
                    e.ScalarStyle = (EmitterScalarStyle)ToInt(ev[6]);
                    break;

                case SequenceStartKind:
                case MappingStartKind:
                    // anchor, tag, implicit, style
                    e.Kind = (ToInt(ev[0]) == SequenceStartKind) ? EmitterEventKind.SequenceStart : EmitterEventKind.MappingStart;
                    e.Anchor = ToClrString(ev[1]);
                    e.Tag = ToClrString(ev[2]);
                    e.Implicit = IsTrue(ev[3]);
                    e.CollectionStyle = (EmitterCollectionStyle)ToInt(ev[4]);
                    break;

                case SequenceEndKind:
                    e.Kind = EmitterEventKind.SequenceEnd;
                    break;

                case MappingEndKind:
                    e.Kind = EmitterEventKind.MappingEnd;
                    break;

                default:
                    throw RubyExceptions.CreateArgumentError("unknown emitter event");
            }
            return e;
        }

        private static bool IsTrue(object obj) {
            return Protocols.IsTrue(obj);
        }

        private static int ToInt(object obj) {
            return (obj is int) ? (int)obj : 0;
        }

        private static string ToClrString(object obj) {
            MutableString str = obj as MutableString;
            return (str != null) ? str.ConvertToString() : null;
        }

        private static Version ToVersion(object obj) {
            IList version = obj as IList;
            if (version == null || version.Count < 2) {
                return null;
            }
            return new Version(ToInt(version[0]), ToInt(version[1]));
        }

        private static List<KeyValuePair<string, string>> ToTagDirectives(object obj) {
            IList list = obj as IList;
            if (list == null || list.Count == 0) {
                return null;
            }
            var result = new List<KeyValuePair<string, string>>();
            foreach (object item in list) {
                IList pair = item as IList;
                if (pair != null && pair.Count >= 2) {
                    result.Add(new KeyValuePair<string, string>(ToClrString(pair[0]), ToClrString(pair[1])));
                }
            }
            return result;
        }

        #endregion

        private static RubyArray/*!*/ NewArray(params object[]/*!*/ items) {
            return new RubyArray((IList)items);
        }

        [RubyMethod("libyaml_version", RubyMethodAttributes.PublicSingleton)]
        public static RubyArray/*!*/ LibyamlVersion(RubyModule/*!*/ self) {
            return NewArray(0, 2, 5);
        }
    }
}
