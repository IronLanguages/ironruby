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
using System.Diagnostics;
using System.Globalization;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;
using System.Text;
using System.Numerics;
using IronRuby.Runtime;
using SM = System.Math;
using IronRuby.Runtime.Calls;
using System.Runtime.CompilerServices;
using Microsoft.Scripting.Generation;
using IronRuby.Compiler.Generation;
using IronRuby.Runtime.Conversions;

namespace IronRuby.Builtins {

    public sealed class StringFormatterSiteStorage : RubyCallSiteStorage {
        private CallSite<Func<CallSite, object, int>> _fixnumCast;
        private CallSite<Func<CallSite, object, double>> _tofConversion;
        private CallSite<Func<CallSite, object, MutableString>> _tosConversion;
        private CallSite<Func<CallSite, object, IntegerValue>> _integerConversion;
        private CallSite<Func<CallSite, object, MutableString>> _tostrTryCast;
        private CallSite<Func<CallSite, object, object, object>> _index;
        private CallSite<Func<CallSite, object, object, object>> _hasKey;
        private CallSite<Func<CallSite, object, object, object>> _new;
        private CallSite<Func<CallSite, object, object>> _numerator;
        private CallSite<Func<CallSite, object, object>> _denominator;

        [Emitted]
        public StringFormatterSiteStorage(RubyContext/*!*/ context) : base(context) {
        }

        public object Numerator(object value) {
            var site = RubyUtils.GetCallSite(ref _numerator, Context, "numerator", 0);
            return site.Target(site, value);
        }

        public object Denominator(object value) {
            var site = RubyUtils.GetCallSite(ref _denominator, Context, "denominator", 0);
            return site.Target(site, value);
        }

        public int CastToFixnum(object value) {
            var site = RubyUtils.GetCallSite(ref _fixnumCast, ConvertToFixnumAction.Make(Context));
            return site.Target(site, value);
        }

        public double CastToDouble(object value) {
            var site = RubyUtils.GetCallSite(ref _tofConversion, ConvertToFAction.Make(Context));
            return site.Target(site, value);
        }

        public MutableString/*!*/ ConvertToString(object value) {
            var site = RubyUtils.GetCallSite(ref _tosConversion, ConvertToSAction.Make(Context));
            return site.Target(site, value);
        }

        public MutableString TryConvertToStr(object value) {
            var site = RubyUtils.GetCallSite(ref _tostrTryCast, TryConvertToStrAction.Make(Context));
            return site.Target(site, value);
        }

        public IntegerValue ConvertToInteger(object value) {
            var site = RubyUtils.GetCallSite(ref _integerConversion, CompositeConversionAction.Make(Context, CompositeConversion.ToIntToI));
            return site.Target(site, value);
        }

        /// <summary>Dynamic <c>hash[key]</c> so that Hash#default and the default block are honoured.</summary>
        public object Index(object hash, object key) {
            var site = RubyUtils.GetCallSite(ref _index, Context, "[]", 1);
            return site.Target(site, hash, key);
        }

        /// <summary>Dynamic <c>hash.key?(key)</c>.</summary>
        public object HasKey(object hash, object key) {
            var site = RubyUtils.GetCallSite(ref _hasKey, Context, "key?", 1);
            return site.Target(site, hash, key);
        }

        /// <summary>Dynamic <c>cls.new(arg)</c>, used to raise exception classes that are defined in Ruby.</summary>
        public object New(object cls, object arg) {
            var site = RubyUtils.GetCallSite(ref _new, Context, "new", 1);
            return site.Target(site, cls, arg);
        }
    }

    /// <summary>
    /// StringFormatter provides Ruby's sprintf style string formatting services.
    ///
    /// The conversion specifier syntax implemented here is the one documented for Kernel#format:
    ///
    ///   % [flags] [argnum$] [&lt;name&gt;] [width] [.precision] conversion
    ///   % [flags] [width] [.precision] {name}
    ///
    /// flags       - '-' '+' ' ' '0' '#'
    /// conversion  - b B c d E e f G g i o p s u X x a A %
    ///
    /// Numeric output is produced from scratch (BigInteger arithmetic for the float
    /// conversions) rather than by delegating to System.String.Format, because the CLI's
    /// numeric formats differ from C's printf in exponent width, rounding, precision limits
    /// and negative-number handling.
    /// </summary>
    internal sealed class StringFormatter {

        const int UnspecifiedPrecision = -1; // Use the default precision

        private readonly IList/*!*/ _data;
        private readonly string/*!*/ _format;
        private readonly RubyContext/*!*/ _context;

        private bool? _useAbsolute;
        private bool _useNamed;
        private int _relativeIndex;
        private bool _tainted;

        private int _index;

        // The options for formatting the current formatting specifier in the format string
        private FormatSettings _opts;
        // Should ddd.0 be displayed as "ddd" or "ddd.0". "'%g' % ddd.0" needs "ddd", but str(ddd.0) needs "ddd.0"
        private bool _TrailingZeroAfterWholeFloat;

        private StringBuilder _buf;

        private readonly RubyEncoding/*!*/ _encoding;
        private readonly bool _formatIsAscii;
        private RubyEncoding _resultEncoding;
        private RubyEncoding _argEncoding;

        private readonly StringFormatterSiteStorage/*!*/ _siteStorage;

        #region Constructors

        // TODO: remove
        internal StringFormatter(RubyContext/*!*/ context, string/*!*/ format, RubyEncoding/*!*/ encoding, IList/*!*/ data) {
            Assert.NotNull(context, format, data, encoding);

            _context = context;
            _format = format;
            _data = data;
            _encoding = encoding;
            _resultEncoding = encoding;
            _formatIsAscii = IsAsciiOnly(format);
        }

        internal StringFormatter(StringFormatterSiteStorage/*!*/ siteStorage, string/*!*/ format, RubyEncoding/*!*/ encoding, IList/*!*/ data)
            : this(siteStorage.Context, format, encoding, data) {
            Assert.NotNull(siteStorage);
            _siteStorage = siteStorage;
        }

        #endregion

        #region Public API Surface

        public bool TrailingZeroAfterWholeFloat {
            get { return _TrailingZeroAfterWholeFloat; }
            set { _TrailingZeroAfterWholeFloat = value; }
        }

        public MutableString/*!*/ Format() {
            _index = 0;
            _buf = new StringBuilder();
            _tainted = false;
            int modIndex;

            while ((modIndex = _format.IndexOf('%', _index)) != -1) {
                _buf.Append(_format, _index, modIndex - _index);
                _index = modIndex + 1;
                DoFormatCode();
            }

            _buf.Append(_format, _index, _format.Length - _index);

            // Leftover arguments raise under $DEBUG and only warn under $VERBOSE. A lone Hash
            // argument is assumed to carry keyword references even when the format string uses
            // none, so it never counts as unused.
            if (!_useNamed && (!_useAbsolute.HasValue || !_useAbsolute.Value) && _relativeIndex != _data.Count
                && !(_data.Count == 1 && _data[0] is IDictionary)) {

                object debug;
                if (_context.TryGetGlobalVariable(null, "DEBUG", out debug) && RubyOps.IsTrue(debug)) {
                    throw RubyExceptions.CreateArgumentError("too many arguments for format string");
                }
                if (RubyOps.IsTrue(_context.Verbose)) {
                    _context.ReportWarning("too many arguments for format string");
                }
            }

            MutableString result = MutableString.Create(_buf.ToString(), _resultEncoding ?? _encoding);

            if (_tainted) {
                result.IsTainted = true;
            }

            return result;
        }

        #endregion

        #region Specifier parsing

        private static bool IsAsciiOnly(string/*!*/ str) {
            for (int i = 0; i < str.Length; i++) {
                if (str[i] > 0x7f) {
                    return false;
                }
            }
            return true;
        }

        private Exception/*!*/ MalformedFormat(char c) {
            return RubyExceptions.CreateArgumentError("malformed format string - %" + c);
        }

        private void DoFormatCode() {
            // we already pulled the first '%'; _index points just past it.
            if (_index == _format.Length) {
                throw RubyExceptions.CreateArgumentError("incomplete format specifier; use %% (double %) instead");
            }

            _opts = new FormatSettings();
            _opts.Precision = UnspecifiedPrecision;

            bool widthSeen = false;
            bool precisionSeen = false;
            bool flagSeen = false;
            bool numberSeen = false;
            bool argumentSeen = false;
            char conversion = '\0';

            while (true) {
                if (_index >= _format.Length) {
                    // A bare trailing '%' is "incomplete"; anything else that ran out mid-specifier
                    // is "malformed", with the width/precision wording when digits were expected.
                    // Ruby 3.3 and earlier were laxer here - "%1$" produced a literal '%' - but
                    // 3.4 made every truncated specifier an error (spec/core/kernel/shared/sprintf.rb).
                    if (numberSeen) {
                        throw RubyExceptions.CreateArgumentError("malformed format string - %*[0-9]");
                    }
                    if (flagSeen || argumentSeen) {
                        throw RubyExceptions.CreateArgumentError("malformed format string - %");
                    }
                    throw RubyExceptions.CreateArgumentError("incomplete format specifier; use %% (double %) instead");
                }

                char c = _format[_index];

                if (c == '#' || c == '-' || c == '+' || c == ' ' || c == '0') {
                    // MRI: flags belong before the width ("%5 d" is an error, "% 5d" is not)
                    if (widthSeen) {
                        throw RubyExceptions.CreateArgumentError("flag after width");
                    }
                    if (precisionSeen) {
                        throw RubyExceptions.CreateArgumentError("flag after precision");
                    }
                    _index++;
                    flagSeen = true;
                    switch (c) {
                        case '#': _opts.AltForm = true; break;
                        case '-': _opts.LeftAdj = true; break;
                        case '+': _opts.SignChar = true; break;
                        case ' ': _opts.Space = true; break;
                        case '0': _opts.ZeroPad = true; break;
                    }
                    continue;
                }

                if (c == '*') {
                    _index++;
                    numberSeen = true;
                    // The digits that may follow are checked before the argument is fetched, so
                    // a bare trailing "%*" is malformed whether or not an argument was supplied.
                    if (_index >= _format.Length) {
                        throw RubyExceptions.CreateArgumentError("malformed format string - %*[0-9]");
                    }
                    if (widthSeen) {
                        throw RubyExceptions.CreateArgumentError("width given twice");
                    }
                    widthSeen = true;
                    int w = _siteStorage.CastToFixnum(GetData(TryReadStarArgumentIndex(), null));
                    if (w < 0) {
                        _opts.LeftAdj = true;
                        w = -w;
                    }
                    _opts.FieldWidth = w;
                    continue;
                }

                if (c == '.') {
                    _index++;
                    numberSeen = true;
                    if (precisionSeen) {
                        throw RubyExceptions.CreateArgumentError("precision given twice");
                    }
                    precisionSeen = true;
                    if (_index < _format.Length && _format[_index] == '*') {
                        _index++;
                        int p = _siteStorage.CastToFixnum(GetData(TryReadStarArgumentIndex(), null));
                        _opts.Precision = (p < 0) ? UnspecifiedPrecision : p;
                    } else {
                        int p = 0;
                        while (_index < _format.Length && _format[_index] >= '0' && _format[_index] <= '9') {
                            // A precision that does not fit in a Fixnum is an error, not a silent
                            // wrap: "%.99999999999s" raises rather than truncating to garbage.
                            if (p > (Int32.MaxValue - 9) / 10) {
                                throw RubyExceptions.CreateArgumentError("precision too big");
                            }
                            p = p * 10 + (_format[_index++] - '0');
                        }
                        _opts.Precision = p;
                    }
                    continue;
                }

                if (c >= '1' && c <= '9') {
                    int end = _index;
                    while (end < _format.Length && _format[end] >= '0' && _format[end] <= '9') {
                        end++;
                    }
                    if (end < _format.Length && _format[end] == '$') {
                        string digits = _format.Substring(_index, end - _index);
                        if (_opts.ArgIndex.HasValue) {
                            throw RubyExceptions.CreateArgumentError("value given twice - {0}$", digits);
                        }
                        if (_useNamed) {
                            throw RubyExceptions.CreateArgumentError("numbered({0}) after named", digits);
                        }
                        if (_useAbsolute.HasValue && !_useAbsolute.Value) {
                            throw RubyExceptions.CreateArgumentError("numbered({0}) after unnumbered({1})", digits, _relativeIndex.ToString());
                        }
                        // numbered from here on: a "*" width in this directive may not be unnumbered
                        _useAbsolute = true;
                        _opts.ArgIndex = int.Parse(digits, CultureInfo.InvariantCulture);
                        _index = end + 1;
                        argumentSeen = true;
                    } else {
                        if (widthSeen) {
                            throw RubyExceptions.CreateArgumentError("width given twice");
                        }
                        widthSeen = true;
                        int width;
                        if (!Int32.TryParse(_format.Substring(_index, end - _index), NumberStyles.None, CultureInfo.InvariantCulture, out width)) {
                            throw RubyExceptions.CreateArgumentError("width too big");
                        }
                        _opts.FieldWidth = width;
                        _index = end;
                        numberSeen = true;
                    }
                    continue;
                }

                if (c == '<' || c == '{') {
                    char close = (c == '<') ? '>' : '}';
                    int end = _format.IndexOf(close, _index + 1);
                    if (end < 0) {
                        throw RubyExceptions.CreateArgumentError("malformed name - unmatched parenthesis");
                    }
                    string spelled = _format.Substring(_index, end - _index + 1); // with its brackets, as MRI quotes it
                    if (_opts.Name != null) {
                        throw RubyExceptions.CreateArgumentError("named{0} after <{1}>", spelled, _opts.Name);
                    }
                    if (_opts.ArgIndex.HasValue || (_useAbsolute.HasValue && _useAbsolute.Value)) {
                        throw RubyExceptions.CreateArgumentError("named{0} after numbered", spelled);
                    }
                    if (_useAbsolute.HasValue) {
                        throw RubyExceptions.CreateArgumentError("named{0} after unnumbered({1})", spelled, _relativeIndex.ToString());
                    }
                    _opts.Name = _format.Substring(_index + 1, end - _index - 1);
                    _opts.NameStyle = c;
                    _index = end + 1;
                    argumentSeen = true;
                    // a "*" width in this very directive is already "mixed with named"
                    _useNamed = true;
                    if (c == '{') {
                        // %{name} is a complete directive; it formats the value with to_s
                        conversion = 's';
                        break;
                    }
                    continue;
                }

                // conversion character
                _index++;
                conversion = c;
                break;
            }

            if (conversion == '%' && _opts.NameStyle != '{') {
                if (flagSeen || numberSeen || argumentSeen) {
                    throw RubyExceptions.CreateArgumentError("invalid format character - %");
                }
                _buf.Append('%');
                return;
            }

            if ("bBoxXdiueEfGgaAcps".IndexOf(conversion) < 0) {
                throw MalformedFormat(conversion);
            }

            _opts.Value = GetData(_opts.ArgIndex, _opts.Name);

            WriteConversion(conversion);
        }

        private int? TryReadStarArgumentIndex() {
            if (_index < _format.Length && _format[_index] >= '1' && _format[_index] <= '9') {
                int end = _index;
                while (end < _format.Length && _format[end] >= '0' && _format[end] <= '9') {
                    end++;
                }
                if (end < _format.Length && _format[end] == '$') {
                    int result = int.Parse(_format.Substring(_index, end - _index), CultureInfo.InvariantCulture);
                    _index = end + 1;
                    return result;
                }
            }
            return null;
        }

        private void WriteConversion(char conversion) {
            switch (conversion) {
                case 'b':
                case 'B': AppendRadix(conversion, 2); return;
                case 'o': AppendRadix(conversion, 8); return;
                case 'x':
                case 'X': AppendRadix(conversion, 16); return;
                case 'd':
                case 'i':
                case 'u': AppendInt(); return;
                case 'e':
                case 'E':
                case 'f':
                case 'G':
                case 'g':
                case 'a':
                case 'A': AppendFloat(conversion); return;
                case 'c': AppendChar(); return;
                case 'p': AppendInspect(); return;
                case 's': AppendString(); return;
                default: throw MalformedFormat(conversion);
            }
        }

        #endregion

        #region Argument access

        private object GetData(int? absoluteIndex, string name) {
            if (name != null) {
                return GetNamedData(name);
            }

            if (_useNamed) {
                if (absoluteIndex.HasValue) {
                    throw RubyExceptions.CreateArgumentError("numbered({0}) after named", absoluteIndex.Value.ToString());
                }
                throw RubyExceptions.CreateArgumentError("unnumbered({0}) mixed with named", (_relativeIndex + 1).ToString());
            }

            if (_useAbsolute.HasValue) {
                // All arguments must use absolute or relative index. They can't be mixed
                if (_useAbsolute.Value && !absoluteIndex.HasValue) {
                    throw RubyExceptions.CreateArgumentError("unnumbered({0}) mixed with numbered", (_relativeIndex + 1).ToString());
                } else if (!_useAbsolute.Value && absoluteIndex.HasValue) {
                    throw RubyExceptions.CreateArgumentError("numbered({0}) after unnumbered({1})",
                        absoluteIndex.Value.ToString(), _relativeIndex.ToString());
                }
            } else {
                // First time through, set _useAbsolute based on our current value
                _useAbsolute = absoluteIndex.HasValue;
            }

            int index = _useAbsolute.Value ? (absoluteIndex.Value - 1) : _relativeIndex++;
            if (index >= 0 && index < _data.Count) {
                return _data[index];
            }

            throw RubyExceptions.CreateArgumentError("too few arguments");
        }

        private object GetNamedData(string/*!*/ name) {
            if (_useAbsolute.HasValue) {
                throw _useAbsolute.Value
                    ? RubyExceptions.CreateArgumentError("named<{0}> after numbered", name)
                    : RubyExceptions.CreateArgumentError("named<{0}> after unnumbered({1})", name, (_relativeIndex == 0 ? 1 : _relativeIndex).ToString());
            }
            _useNamed = true;

            if (_data.Count != 1 || !(_data[0] is IDictionary)) {
                throw RubyExceptions.CreateArgumentError("one hash required");
            }

            object hash = _data[0];
            object key = _context.CreateSymbol(name, RubyEncoding.UTF8);

            if (_siteStorage == null) {
                IDictionary dict = (IDictionary)hash;
                if (dict.Contains(key)) {
                    return dict[key];
                }
                throw CreateKeyError(hash, name, key);
            }

            if (RubyOps.IsTrue(_siteStorage.HasKey(hash, key))) {
                return _siteStorage.Index(hash, key);
            }

            // honours Hash#default and the default block; a nil result means "not found"
            object result = _siteStorage.Index(hash, key);
            if (result == null) {
                throw CreateKeyError(hash, name, key);
            }
            return result;
        }

        private Exception/*!*/ CreateKeyError(object hash, string/*!*/ name, object key) {
            string message = (_opts.NameStyle == '{')
                ? "key{" + name + "} not found"
                : "key<" + name + "> not found";

            object keyErrorClass;
            if (_siteStorage != null && _context.ObjectClass.TryGetConstant(null, "KeyError", out keyErrorClass)) {
                try {
                    object instance = _siteStorage.New(keyErrorClass, MutableString.CreateMutable(message, RubyEncoding.UTF8));
                    Exception exception = instance as Exception;
                    if (exception != null) {
                        // KeyError#receiver and #key read these; see the KeyError
                        // reopening in Src/StdLib/ironruby/ruby4.rb.
                        _context.SetInstanceVariable(instance, "@__receiver", hash);
                        _context.SetInstanceVariable(instance, "@__key", key);
                        return exception;
                    }
                } catch (Exception) {
                    // fall through to IndexError
                }
            }

            return RubyExceptions.CreateIndexError(message);
        }

        #endregion

        #region Padding

        /// <summary>
        /// Lays out sign + prefix + digits in a field of _opts.FieldWidth characters.
        /// </summary>
        private void AppendPadded(string/*!*/ sign, string/*!*/ prefix, string/*!*/ digits, bool allowZeroPad) {
            int pad = _opts.FieldWidth - (sign.Length + prefix.Length + digits.Length);
            if (pad <= 0) {
                _buf.Append(sign).Append(prefix).Append(digits);
            } else if (_opts.LeftAdj) {
                _buf.Append(sign).Append(prefix).Append(digits).Append(' ', pad);
            } else if (_opts.ZeroPad && allowZeroPad) {
                _buf.Append(sign).Append(prefix).Append('0', pad).Append(digits);
            } else {
                _buf.Append(' ', pad).Append(sign).Append(prefix).Append(digits);
            }
        }

        private string/*!*/ SignFor(bool isNegative) {
            if (isNegative) {
                return "-";
            }
            if (_opts.SignChar) {
                return "+";
            }
            if (_opts.Space) {
                return " ";
            }
            return "";
        }

        #endregion

        #region Integer conversions

        private BigInteger ToIntegerArgument() {
            object value = _opts.Value;

            if (value == null) {
                throw RubyExceptions.CreateTypeError("can't convert nil into Integer");
            }

            if (value is int) {
                return new BigInteger((int)value);
            }
            if (value is long) {
                return new BigInteger((long)value);
            }
            if (value is BigInteger) {
                return (BigInteger)value;
            }

            // Ruby tries #to_str before #to_int and runs whatever string it gets through
            // Kernel#Integer, so an object whose #to_str is not numeric raises ArgumentError
            // rather than TypeError.
            MutableString str = value as MutableString;
            // Symbols must not take the string path: they have no #to_str in Ruby 1.9+, so
            // "%d" % :s is a TypeError rather than a failed Integer().
            if (str == null && _siteStorage != null && !(value is RubySymbol)) {
                str = _siteStorage.TryConvertToStr(value);
            }
            if (str != null) {
                if (!str.Encoding.IsAsciiIdentity) {
                    throw RubyExceptions.CreateAsciiIncompatibleEncodingError(str.Encoding);
                }
                object parsed = KernelOps.ToInteger(null, str);
                return (parsed is BigInteger) ? (BigInteger)parsed : (parsed is long ? (BigInteger)(long)parsed : (BigInteger)(int)parsed);
            }

            IntegerValue integer = _siteStorage.ConvertToInteger(value);
            return integer.IsFixnum ? (BigInteger)integer.Fixnum : integer.Bignum;
        }

        private void AppendInt() {
            BigInteger value = ToIntegerArgument();
            bool isNegative = value.Sign < 0;
            string digits = BigInteger.Abs(value).ToString(CultureInfo.InvariantCulture);

            if (_opts.Precision != UnspecifiedPrecision) {
                if (value.IsZero && _opts.Precision == 0) {
                    digits = "";
                } else if (digits.Length < _opts.Precision) {
                    digits = digits.PadLeft(_opts.Precision, '0');
                }
            }

            AppendPadded(SignFor(isNegative), "", digits, _opts.Precision == UnspecifiedPrecision);
        }

        private static readonly char[] _LowerDigits = new char[] { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', 'a', 'b', 'c', 'd', 'e', 'f' };
        private static readonly char[] _UpperDigits = new char[] { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', 'A', 'B', 'C', 'D', 'E', 'F' };

        private static string/*!*/ ToRadixString(BigInteger value, int radix, bool upperCase) {
            Debug.Assert(value.Sign >= 0);
            if (value.IsZero) {
                return "0";
            }
            char[] table = upperCase ? _UpperDigits : _LowerDigits;
            StringBuilder result = new StringBuilder();
            BigInteger r = new BigInteger(radix);
            while (!value.IsZero) {
                BigInteger rem;
                value = BigInteger.DivRem(value, r, out rem);
                result.Append(table[(int)rem]);
            }
            char[] chars = new char[result.Length];
            for (int i = 0; i < result.Length; i++) {
                chars[i] = result[result.Length - 1 - i];
            }
            return new string(chars);
        }

        /// <summary>
        /// Minimal infinite-two's-complement digit string of a negative number: exactly one leading
        /// (radix-1) digit followed by the remaining digits.  E.g. -255 in base 16 gives "f01".
        /// </summary>
        private static string/*!*/ ToComplementString(BigInteger magnitude, int radix, bool upperCase) {
            Debug.Assert(magnitude.Sign > 0);

            int digitCount = ToRadixString(magnitude, radix, false).Length;
            BigInteger power = BigInteger.Pow(radix, digitCount + 1);
            string s = ToRadixString(power - magnitude, radix, upperCase);

            char signDigit = (upperCase ? _UpperDigits : _LowerDigits)[radix - 1];
            int i = 0;
            while (i + 1 < s.Length && s[i] == signDigit && s[i + 1] == signDigit) {
                i++;
            }
            return s.Substring(i);
        }

        private void AppendRadix(char format, int radix) {
            BigInteger value = ToIntegerArgument();
            bool upperCase = (format == 'X' || format == 'B');
            bool isNegative = value.Sign < 0;

            string altPrefix;
            switch (radix) {
                case 2: altPrefix = (format == 'B') ? "0B" : "0b"; break;
                case 16: altPrefix = (format == 'X') ? "0X" : "0x"; break;
                default: altPrefix = ""; break;
            }

            if (!isNegative || _opts.SignChar || _opts.Space) {
                // plain magnitude with a sign
                string digits = ToRadixString(BigInteger.Abs(value), radix, upperCase);
                bool isZero = value.IsZero;

                if (_opts.Precision != UnspecifiedPrecision) {
                    if (isZero && _opts.Precision == 0) {
                        digits = "";
                    } else if (digits.Length < _opts.Precision) {
                        digits = digits.PadLeft(_opts.Precision, '0');
                    }
                }

                string prefix = "";
                if (_opts.AltForm) {
                    if (radix == 8) {
                        if (digits.Length == 0 || digits[0] != '0') {
                            digits = "0" + digits;
                        }
                    } else if (!isZero) {
                        prefix = altPrefix;
                    }
                }

                AppendPadded(SignFor(isNegative), prefix, digits, _opts.Precision == UnspecifiedPrecision);
            } else {
                // two's complement form: ..fff
                string digits = ToComplementString(BigInteger.Negate(value), radix, upperCase);
                string prefix = _opts.AltForm ? altPrefix : "";
                char signDigit = (upperCase ? _UpperDigits : _LowerDigits)[radix - 1];

                int target = -1;
                if (_opts.Precision != UnspecifiedPrecision) {
                    target = _opts.Precision - 2;
                } else if (_opts.ZeroPad && !_opts.LeftAdj && _opts.FieldWidth > 0) {
                    target = _opts.FieldWidth - 2 - prefix.Length;
                }

                if (target > digits.Length) {
                    digits = digits.PadLeft(target, signDigit);
                }

                AppendPadded("", prefix + "..", digits, false);
            }
        }

        #endregion

        #region Float conversions

        private static void DecomposeDouble(double value, out BigInteger mantissa, out int exponent) {
            long bits = BitConverter.DoubleToInt64Bits(value);
            int biasedExponent = (int)((bits >> 52) & 0x7FF);
            long fraction = bits & 0xFFFFFFFFFFFFFL;

            if (biasedExponent == 0) {
                mantissa = fraction;
                exponent = -1074;
            } else {
                mantissa = fraction | (1L << 52);
                exponent = biasedExponent - 1075;
            }
        }

        // CRuby renders floats with its own copy of David Gay's dtoa (missing/dtoa.c), and that
        // copy does one thing Gay's does not: in the fast path that generates the requested
        // digits with double arithmetic ("Generate ilim digits, then fix them up"), a remainder
        // it cannot tell apart from one half - within that path's error bound, eps - is taken
        // to *be* a half, and is settled by the parity of the last digit generated, whatever
        // the exact binary value was.  Gay falls back to exact bignum arithmetic there; CRuby
        // sets a `half` flag that the exact pass then honours.  So "%.2f" % 2.675 is "2.68"
        // in CRuby although the double 2.675 is a little below 2.675, and "%.1f" % 99.45 is
        // "99.4" although that double is a little above 99.45 - the digit before the cut is
        // what decides, 7 up and 4 down.  Only numbers that look like ties at the requested
        // precision are affected; everything else rounds exactly, as here.
        //
        // This repeats the fast path's floating-point steps - the k estimate, the scaling by
        // powers of ten, eps, the digit loop - and reports whether CRuby lands on that tie for
        // |d| rendered to ndigits in mode 3 ("%f": ndigits fraction digits) or mode 2 ("%e",
        // "%g": ndigits significant digits).  When it does, the caller rounds up exactly when
        // the last kept digit is odd.  The path is only tried for up to Quick_max (14) digits,
        // so longer renderings are exact in CRuby too.
        private static readonly double[] _Tens = {
            1e0, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9,
            1e10, 1e11, 1e12, 1e13, 1e14, 1e15, 1e16, 1e17, 1e18, 1e19,
            1e20, 1e21, 1e22
        };
        private static readonly double[] _BigTens = { 1e16, 1e32, 1e64, 1e128, 1e256 };

        private static bool RubyDtoaApparentTie(double d, int mode, int ndigits) {
            Debug.Assert(mode == 2 || mode == 3);
            d = SM.Abs(d);
            if (d == 0.0 || Double.IsNaN(d) || Double.IsInfinity(d)) {
                return false;
            }

            // log10(d) estimate: (binary exponent) * log10(2) + log10(mantissa), the latter
            // from a first-order series around 1.5, biased to err on the high side.
            long bits = BitConverter.DoubleToInt64Bits(d);
            int biasedExponent = (int)((bits >> 52) & 0x7FF);
            long fraction = bits & 0xFFFFFFFFFFFFFL;
            int i;
            double d2;
            if (biasedExponent != 0) {
                d2 = BitConverter.Int64BitsToDouble(fraction | 0x3FF0000000000000L);
                i = biasedExponent - 1023;
            } else {
                // Denormal: the mantissa's top 32 bits as an integer, scaled into [1, 2).
                int bitLength = 64 - BitOperations.LeadingZeroCount((ulong)fraction);
                uint x = bitLength > 32 ? (uint)((ulong)fraction >> (bitLength - 32)) : (uint)fraction << (32 - bitLength);
                d2 = (double)x * (1.0 / 2147483648.0);
                i = bitLength - 1075;
            }
            double ds = (d2 - 1.5) * 0.289529654602168 + 0.1760912590558 + i * 0.301029995663981;
            int k = (int)ds;
            if (ds < 0.0 && ds != k) {
                k--;
            }
            bool kCheck = true;
            if (k >= 0 && k <= 22) {
                if (d < _Tens[k]) {
                    k--;
                }
                kCheck = false;
            }

            int ilim, ilim1;
            if (mode == 2) {
                if (ndigits <= 0) {
                    ndigits = 1;
                }
                ilim = ilim1 = ndigits;
            } else {
                ilim = ndigits + k + 1;
                ilim1 = ilim - 1;
            }
            if (ilim < 0 || ilim > 14) {
                return false;
            }

            // Scale d by 10^-k into [1, 10), counting the roundings taken in ieps.
            int ieps = 2;
            i = 0;
            if (k > 0) {
                ds = _Tens[k & 0xf];
                int j = k >> 4;
                if ((j & 0x10) != 0) {
                    j &= 0x0f;
                    d /= _BigTens[4];
                    ieps++;
                }
                for (; j != 0; j >>= 1, i++) {
                    if ((j & 1) != 0) {
                        ieps++;
                        ds *= _BigTens[i];
                    }
                }
                d /= ds;
            } else if (k < 0) {
                int j1 = -k;
                d *= _Tens[j1 & 0xf];
                for (int j = j1 >> 4; j != 0; j >>= 1, i++) {
                    if ((j & 1) != 0) {
                        ieps++;
                        d *= _BigTens[i];
                    }
                }
            }
            if (kCheck && d < 1.0 && ilim > 0) {
                if (ilim1 <= 0) {
                    return false;
                }
                ilim = ilim1;
                d *= 10.0;
                ieps++;
            }
            double eps = (ieps * d + 7.0) * 2.220446049250313e-16;  // word0(eps) -= (P-1)*Exp_msk1
            if (ilim == 0) {
                // Decided against one half exactly, which is what the exact pass does too.
                return false;
            }

            eps *= _Tens[ilim - 1];
            for (i = 1; ; i++, d *= 10.0) {
                int digit = (int)d;
                d -= digit;
                if (d == 0.0) {
                    ilim = i;
                }
                if (i == ilim) {
                    // Clearly above or below one half: the exact rounding agrees. Anything in
                    // between is CRuby's tie.
                    return !(d > 0.5 + eps) && !(d < 0.5 - eps);
                }
            }
        }

        /// <summary>
        /// Exact round-half-to-even of |value| * 10^scale to an integer, except that
        /// <paramref name="rubyTie"/> (see <see cref="RubyDtoaApparentTie"/>) rounds by the
        /// parity of the last kept digit alone.
        /// </summary>
        private static BigInteger ScaledRound(double value, int scale, bool rubyTie) {
            int ignoredCmp;
            bool ignoredUp;
            return ScaledRound(value, scale, rubyTie, out ignoredCmp, out ignoredUp);
        }

        /// <param name="exactCmp">How twice the discarded remainder compares with one unit: the
        /// sign says whether the exact value lies above (+), on (0) or below (-) the half.</param>
        /// <param name="roundedUp">Whether the kept digits were incremented.</param>
        private static BigInteger ScaledRound(double value, int scale, bool rubyTie, out int exactCmp, out bool roundedUp) {
            BigInteger mantissa;
            int exponent;
            DecomposeDouble(value, out mantissa, out exponent);

            BigInteger numerator = mantissa;
            BigInteger denominator = BigInteger.One;

            if (exponent >= 0) {
                numerator <<= exponent;
            } else {
                denominator <<= -exponent;
            }

            if (scale >= 0) {
                numerator *= BigInteger.Pow(10, scale);
            } else {
                denominator *= BigInteger.Pow(10, -scale);
            }

            BigInteger remainder;
            BigInteger quotient = BigInteger.DivRem(numerator, denominator, out remainder);

            int cmp = (remainder * 2).CompareTo(denominator);
            exactCmp = cmp;
            roundedUp = rubyTie ? !quotient.IsEven : (cmp > 0 || (cmp == 0 && !quotient.IsEven));
            if (roundedUp) {
                quotient += BigInteger.One;
            }
            return quotient;
        }

        /// <summary>
        /// floor(log10(|value|)), exactly: the power of ten that |value| (not zero, finite) is at
        /// least and less than ten times.  Math.Log10 rounds to a whole number for a double a
        /// few ulps below a power of ten, and an exponent one too high there turns
        /// 999.9999999999999 into 1.000000000000000e+03 instead of 9.999999999999999e+02.
        /// </summary>
        private static int ExactExponent(double value) {
            int exponent = (int)SM.Floor(SM.Log10(value));
            BigInteger mantissa;
            int exponent2;
            DecomposeDouble(value, out mantissa, out exponent2);
            while (ComparePowerOfTen(mantissa, exponent2, exponent) < 0) {
                exponent--;
            }
            while (ComparePowerOfTen(mantissa, exponent2, exponent + 1) >= 0) {
                exponent++;
            }
            return exponent;
        }

        /// <summary>Compares mantissa * 2^exponent2 with 10^exponent10.</summary>
        private static int ComparePowerOfTen(BigInteger mantissa, int exponent2, int exponent10) {
            BigInteger left = mantissa;
            BigInteger right = BigInteger.One;
            if (exponent2 >= 0) {
                left <<= exponent2;
            } else {
                right <<= -exponent2;
            }
            if (exponent10 >= 0) {
                right *= BigInteger.Pow(10, exponent10);
            } else {
                left *= BigInteger.Pow(10, -exponent10);
            }
            return left.CompareTo(right);
        }

        /// <summary>|value| rendered as ddd.ddd with exactly <paramref name="precision"/> fraction digits.</summary>
        private static string/*!*/ FormatFixed(double value, int precision) {
            return FormatFixed(value, precision, RubyDtoaApparentTie(value, 3, precision));
        }

        private static string/*!*/ FormatFixed(double value, int precision, bool rubyTie) {
            string digits = ScaledRound(value, precision, rubyTie).ToString(CultureInfo.InvariantCulture);
            if (precision <= 0) {
                return digits;
            }
            if (digits.Length <= precision) {
                digits = digits.PadLeft(precision + 1, '0');
            }
            return digits.Substring(0, digits.Length - precision) + "." + digits.Substring(digits.Length - precision);
        }

        /// <summary>Decimal exponent that %e would produce for |value| at the given precision.</summary>
        private static int ExponentFor(double value, int precision, bool rubyTie, out BigInteger digits) {
            int ignoredCmp;
            bool ignoredUp;
            return ExponentFor(value, precision, rubyTie, out digits, out ignoredCmp, out ignoredUp);
        }

        /// <summary>
        /// Decimal exponent that %e would produce for |value| at the given precision, with the
        /// precision + 1 significant digits in <paramref name="digits"/> and how they were
        /// rounded (see <see cref="ScaledRound"/>).
        /// </summary>
        private static int ExponentFor(double value, int precision, bool rubyTie, out BigInteger digits, out int exactCmp, out bool roundedUp) {
            if (value == 0.0) {
                digits = BigInteger.Zero;
                exactCmp = 0;
                roundedUp = false;
                return 0;
            }

            int exponent = ExactExponent(value);
            BigInteger low = BigInteger.Pow(10, precision);
            BigInteger high = low * 10;

            digits = ScaledRound(value, precision - exponent, rubyTie, out exactCmp, out roundedUp);
            if (digits >= high) {
                // Rounding carried into one more digit: 9.95 at one fraction digit is 10.0,
                // which is 1.0 one power of ten up.
                digits = low;
                exponent++;
            }
            return exponent;
        }

        private static string/*!*/ FormatExponential(double value, int precision, bool upperCase, bool forceDot) {
            return FormatExponential(value, precision, upperCase, forceDot, RubyDtoaApparentTie(value, 2, precision + 1));
        }

        private static string/*!*/ FormatExponential(double value, int precision, bool upperCase, bool forceDot, bool rubyTie) {
            BigInteger scaled;
            int exponent = ExponentFor(value, precision, rubyTie, out scaled);

            string digits = scaled.ToString(CultureInfo.InvariantCulture);
            if (digits.Length < precision + 1) {
                digits = digits.PadLeft(precision + 1, '0');
            }

            StringBuilder result = new StringBuilder();
            result.Append(digits[0]);
            if (precision > 0) {
                result.Append('.').Append(digits, 1, digits.Length - 1);
            } else if (forceDot) {
                result.Append('.');
            }
            AppendExponent(result, exponent, upperCase);
            return result.ToString();
        }

        /// <summary>e+XX, with at least two exponent digits, as C prints it.</summary>
        private static void AppendExponent(StringBuilder/*!*/ result, int exponent, bool upperCase) {
            result.Append(upperCase ? 'E' : 'e');
            result.Append(exponent < 0 ? '-' : '+');
            string exponentDigits = SM.Abs(exponent).ToString(CultureInfo.InvariantCulture);
            if (exponentDigits.Length < 2) {
                exponentDigits = exponentDigits.PadLeft(2, '0');
            }
            result.Append(exponentDigits);
        }

        private static string/*!*/ FormatGeneral(double value, int precision, bool upperCase, bool altForm) {
            if (precision == 0) {
                precision = 1;
            }

            // "%g" asks dtoa for precision significant digits (mode 2) and then picks the
            // notation, so the tie is the one for those digits, and the digits are rendered
            // once: rounding them again at the notation's scale would apply the tie to the
            // wrong digit after a carry ("%.1g" % 0.095 is 0.1, by way of 9.5 -> 10).
            bool rubyTie = RubyDtoaApparentTie(value, 2, precision);
            BigInteger digits;
            int exactCmp;
            bool roundedUp;
            int exponent = ExponentFor(value, precision - 1, rubyTie, out digits, out exactCmp, out roundedUp);
            string digitString = digits.ToString(CultureInfo.InvariantCulture).PadLeft(precision, '0');

            // "%g" prints the digits dtoa hands back and pads nothing, so its trailing zeros are
            // whatever dtoa left in place.  dtoa trims them, except on CRuby's tie when the last
            // digit is even and stays: the exact pass then returns without trimming when the
            // value is above the half ("%.5g" % 0.0676905 is "0.067690"), and its separate
            // path for whole numbers below 1e15, which never trims, keeps them on an exact half
            // as well ("%.2g" % 205.0 is "2.0e+02").
            bool keepZeros = altForm;
            if (!keepZeros && rubyTie && !roundedUp) {
                bool wholeNumberPath = value == SM.Floor(value) && exponent <= 14;
                keepZeros = exactCmp > 0 || (exactCmp == 0 && wholeNumberPath);
            }
            if (!keepZeros) {
                digitString = digitString.TrimEnd('0');
                if (digitString.Length == 0) {
                    digitString = "0";
                }
            }

            StringBuilder result = new StringBuilder();
            if (exponent < -4 || exponent >= precision) {
                result.Append(digitString[0]);
                if (digitString.Length > 1) {
                    result.Append('.').Append(digitString, 1, digitString.Length - 1);
                } else if (altForm) {
                    result.Append('.');
                }
                AppendExponent(result, exponent, upperCase);
            } else if (exponent >= 0) {
                int integerDigits = exponent + 1;
                if (digitString.Length <= integerDigits) {
                    result.Append(digitString).Append('0', integerDigits - digitString.Length);
                    if (altForm) {
                        result.Append('.');
                    }
                } else {
                    result.Append(digitString, 0, integerDigits).Append('.').Append(digitString, integerDigits, digitString.Length - integerDigits);
                }
            } else {
                result.Append("0.").Append('0', -exponent - 1).Append(digitString);
            }
            return result.ToString();
        }

        private static string/*!*/ FormatHexFloat(double value, int precision, bool upperCase, bool altForm) {
            char[] table = upperCase ? _UpperDigits : _LowerDigits;

            if (value == 0.0) {
                string zeroFraction = (precision > 0) ? new string('0', precision) : "";
                string zeroBody = "0";
                if (zeroFraction.Length > 0) {
                    zeroBody += "." + zeroFraction;
                } else if (altForm) {
                    zeroBody += ".";
                }
                return zeroBody + (upperCase ? "P+0" : "p+0");
            }

            BigInteger mantissa;
            int exponent2;
            DecomposeDouble(value, out mantissa, out exponent2);

            int msb = -1;
            BigInteger probe = mantissa;
            while (!probe.IsZero) {
                probe >>= 1;
                msb++;
            }

            int exponent = exponent2 + msb;
            BigInteger fraction = mantissa - (BigInteger.One << msb);
            int fractionBits = msb;
            int hexDigits = (fractionBits + 3) / 4;
            BigInteger scaled = fraction << (hexDigits * 4 - fractionBits);
            int leading = 1;

            if (precision >= 0 && precision < hexDigits) {
                BigInteger divisor = BigInteger.Pow(16, hexDigits - precision);
                BigInteger remainder;
                BigInteger quotient = BigInteger.DivRem(scaled, divisor, out remainder);
                int cmp = (remainder * 2).CompareTo(divisor);
                // Round half to even.  The digit whose parity decides a tie is the last
                // digit that survives truncation; when the precision is zero no fraction
                // digit survives and the leading digit (always 1 here) decides, which is
                // why "%.a" % 1.5 rounds up to 0x1p+1 rather than down to 0x1p+0.
                bool lastKeptIsEven = (precision == 0) ? (leading % 2 == 0) : quotient.IsEven;
                if (cmp > 0 || (cmp == 0 && !lastKeptIsEven)) {
                    quotient += BigInteger.One;
                }
                BigInteger limit = BigInteger.Pow(16, precision);
                if (quotient >= limit) {
                    // The mantissa carried past 2.0; renormalize to 1.0 x 2^(exponent+1)
                    // the way C's printf does, so 123.456 prints as 0x1p+7, not 0x2p+6.
                    quotient -= limit;
                    exponent++;
                }
                scaled = quotient;
                hexDigits = precision;
            }

            string fractionDigits;
            if (hexDigits == 0) {
                fractionDigits = "";
            } else {
                StringBuilder sb = new StringBuilder();
                BigInteger v = scaled;
                for (int i = 0; i < hexDigits; i++) {
                    sb.Insert(0, table[(int)(v & 15)]);
                    v >>= 4;
                }
                fractionDigits = sb.ToString();
            }

            if (precision < 0) {
                fractionDigits = fractionDigits.TrimEnd('0');
            } else if (fractionDigits.Length < precision) {
                fractionDigits = fractionDigits.PadRight(precision, '0');
            }

            StringBuilder result = new StringBuilder();
            result.Append((char)('0' + leading));
            if (fractionDigits.Length > 0) {
                result.Append('.').Append(fractionDigits);
            } else if (altForm) {
                result.Append('.');
            }
            result.Append(upperCase ? 'P' : 'p');
            result.Append(exponent < 0 ? '-' : '+');
            result.Append(SM.Abs(exponent).ToString(CultureInfo.InvariantCulture));
            return result.ToString();
        }

        private void AppendFloat(char type) {
            // "%f" applied to an Integer is exact in CRuby: the value is not squeezed through
            // a double first, so 10**39 prints all forty of its digits. The other float
            // conversions do go through the double - "%.25g" of that same Integer shows the
            // nearest double - so only "%f" takes this path.
            if (type == 'f' && (_opts.Value is int || _opts.Value is long || _opts.Value is BigInteger)) {
                AppendExactInteger();
                return;
            }
            if (type == 'f' && _siteStorage != null && IsRational(_opts.Value)) {
                AppendExactRational();
                return;
            }

            double value;
            if (_siteStorage != null) {
                value = _siteStorage.CastToDouble(_opts.Value);
            } else {
                value = (double)_opts.Value;
            }

            bool upperCase = (type == 'E' || type == 'G' || type == 'A');

            if (Double.IsNaN(value)) {
                AppendPadded(_opts.SignChar ? "+" : (_opts.Space ? " " : ""), "", "NaN", false);
                return;
            }

            bool isNegative = (value < 0.0) || (value == 0.0 && Double.IsNegative(value));
            double magnitude = SM.Abs(value);

            if (Double.IsInfinity(value)) {
                AppendPadded(SignFor(isNegative), "", "Inf", false);
                return;
            }

            int precision = _opts.Precision;
            if (precision == UnspecifiedPrecision) {
                precision = (type == 'a' || type == 'A') ? -1 : 6;
            }
            if (precision > 1000) {
                precision = 1000;
            }

            string body;
            string prefix = "";

            switch (type) {
                case 'f':
                    body = FormatFixed(magnitude, precision);
                    if (precision == 0 && _opts.AltForm) {
                        body += ".";
                    }
                    if (_TrailingZeroAfterWholeFloat && precision == 0 && body.IndexOf('.') < 0) {
                        body += ".0";
                    }
                    break;

                case 'e':
                case 'E':
                    body = FormatExponential(magnitude, precision, upperCase, _opts.AltForm);
                    break;

                case 'g':
                case 'G':
                    body = FormatGeneral(magnitude, precision, upperCase, _opts.AltForm);
                    // Float#to_s is built on "%.15g" and wants the decorative ".0" that marks
                    // the result as a float: 123.0.to_s is "123.0", not "123". "%g" itself
                    // never adds it, so only the internal Float#to_s caller sets the flag.
                    if (_TrailingZeroAfterWholeFloat && body.IndexOfAny(_FloatMarkers) < 0) {
                        body += ".0";
                    }
                    break;

                default: // 'a', 'A'
                    body = FormatHexFloat(magnitude, precision, upperCase, _opts.AltForm);
                    prefix = upperCase ? "0X" : "0x";
                    break;
            }

            AppendPadded(SignFor(isNegative), prefix, body, true);
        }

        /// <summary>Characters whose presence already marks a rendered number as a float.</summary>
        private static readonly char[] _FloatMarkers = new char[] { '.', 'e', 'E', 'n', 'N' };

        private bool IsRational(object value) {
            return value != null && !(value is int) && !(value is long) && !(value is BigInteger) && !(value is double) &&
                _context.GetClassOf(value).Name == "Rational";
        }

        private static BigInteger ToBigInteger(object value) {
            if (value is int) {
                return (int)value;
            }
            if (value is long) {
                return (long)value;
            }
            return (BigInteger)value;
        }

        /// <summary>
        /// "%f" of a Rational is exact in CRuby, like that of an Integer: the digits come from
        /// numerator * 10^precision / denominator in integer arithmetic, rounded half up
        /// (sprintf.c adds half the denominator before dividing), so "%.0f" % (1/2r) is 1 and
        /// "%.1f" % (10**30 + 1/2r) keeps all thirty digits.
        /// </summary>
        private void AppendExactRational() {
            BigInteger numerator = ToBigInteger(_siteStorage.Numerator(_opts.Value));
            BigInteger denominator = ToBigInteger(_siteStorage.Denominator(_opts.Value));

            int precision = _opts.Precision;
            if (precision == UnspecifiedPrecision) {
                precision = 6;
            }
            if (precision > 1000) {
                precision = 1000;
            }

            bool negative = numerator.Sign < 0;
            numerator = BigInteger.Abs(numerator);

            string body;
            if (denominator.IsOne) {
                body = numerator.ToString(CultureInfo.InvariantCulture);
                if (precision > 0) {
                    body += "." + new string('0', precision);
                } else if (_opts.AltForm) {
                    body += ".";
                }
            } else {
                BigInteger scaled = (numerator * BigInteger.Pow(10, precision) + denominator / 2) / denominator;
                string digits = scaled.ToString(CultureInfo.InvariantCulture);
                if (precision <= 0) {
                    body = digits + (_opts.AltForm ? "." : "");
                } else {
                    if (digits.Length <= precision) {
                        digits = digits.PadLeft(precision + 1, '0');
                    }
                    body = digits.Substring(0, digits.Length - precision) + "." + digits.Substring(digits.Length - precision);
                }
            }

            AppendPadded(SignFor(negative), "", body, true);
        }

        /// <summary>"%f" of an Integer: all of its digits followed by a zero fraction.</summary>
        private void AppendExactInteger() {
            BigInteger value = (_opts.Value is int) ? new BigInteger((int)_opts.Value) : (BigInteger)_opts.Value;

            int precision = _opts.Precision;
            if (precision == UnspecifiedPrecision) {
                precision = 6;
            }
            if (precision > 1000) {
                precision = 1000;
            }

            string body = BigInteger.Abs(value).ToString(CultureInfo.InvariantCulture);
            if (precision > 0) {
                body += "." + new string('0', precision);
            } else if (_opts.AltForm || _TrailingZeroAfterWholeFloat) {
                body += _TrailingZeroAfterWholeFloat ? ".0" : ".";
            }

            AppendPadded(SignFor(value.Sign < 0), "", body, true);
        }

        #endregion

        #region String-ish conversions

        private void AppendChar() {
            object value = _opts.Value;

            MutableString str = value as MutableString;

            // Ruby uses rb_check_string_type (i.e. to_str) and falls back to NUM2INT.
            // Symbols must not take the string path - they have no to_str in Ruby 1.9+.
            if (str == null && _siteStorage != null && value != null &&
                !(value is int) && !(value is long) && !(value is BigInteger) && !(value is double) && !(value is RubySymbol)) {
                try {
                    str = _siteStorage.TryConvertToStr(value);
                } catch (InvalidOperationException e) {
                    // to_str exists but returned a non-String
                    throw RubyExceptions.CreateTypeError(e, "can't convert {0} into String", _context.GetClassDisplayName(value));
                }
            }

            string text;
            if (str != null) {
                string s = str.ToString();
                if (s.Length == 0) {
                    text = "";
                } else if (Char.IsHighSurrogate(s[0]) && s.Length > 1) {
                    text = s.Substring(0, 2);
                } else {
                    text = s.Substring(0, 1);
                }
                TrackEncoding(str);
            } else {
                if (value == null) {
                    throw RubyExceptions.CreateTypeError("no implicit conversion of nil into Integer");
                }

                int codepoint;
                if (value is int) {
                    codepoint = (int)value;
                } else {
                    try {
                        codepoint = _siteStorage.CastToFixnum(value);
                    } catch (InvalidOperationException e) {
                        string className = _context.GetClassDisplayName(value);
                        // An object that has #to_int and gave back something else is a different
                        // complaint from one that never offered a conversion at all, and MRI
                        // words the two differently. The exception the conversion site raises
                        // does not always say which it was, so ask the object.
                        throw _context.ResolveMethod(value, "to_int", VisibilityContext.AllVisible).Found
                            ? RubyExceptions.CreateTypeError(e, "can't convert {0} into Integer", className)
                            : RubyExceptions.CreateTypeError(e, "no implicit conversion of {0} into Integer", className);
                    }
                }

                text = FormatCodepoint(codepoint);
            }

            // width counts characters, not UTF-16 code units
            int length = (text.Length > 0) ? 1 : 0;
            int pad = _opts.FieldWidth - length;
            if (pad <= 0) {
                _buf.Append(text);
            } else if (_opts.LeftAdj) {
                _buf.Append(text).Append(' ', pad);
            } else {
                _buf.Append(' ', pad).Append(text);
            }
        }

        /// <summary>
        /// The character %c makes of an Integer. The code point is interpreted in the encoding of
        /// the result, as MRI's rb_enc_mbcput does: in EUC-JP 0x8FABB1 is one character, not an
        /// out of range Unicode code point.
        /// </summary>
        private string/*!*/ FormatCodepoint(int codepoint) {
            RubyEncoding encoding = _resultEncoding ?? _encoding;
            if (codepoint > 0x7F && encoding != null && !encoding.IsUnicodeEncoding && encoding.IsAsciiIdentity && encoding != RubyEncoding.Binary) {
                if (encoding == RubyEncoding.Ascii) {
                    // A US-ASCII format has no character above 0x7F, so MRI makes the result binary.
                    if (codepoint > 0xFF) {
                        throw RubyExceptions.CreateRangeError("{0} out of char range", codepoint);
                    }
                    _resultEncoding = RubyEncoding.Binary;
                    return ((char)codepoint).ToString();
                }

                MutableString character;
                try {
                    character = Integer.ToChr(encoding, encoding, codepoint);
                } catch (ArgumentOutOfRangeException e) {
                    if (e.Message.StartsWith("invalid codepoint", StringComparison.Ordinal)) {
                        throw RubyExceptions.CreateArgumentError("invalid character");
                    }
                    throw;
                }
                if (character.ContainsInvalidCharacters()) {
                    throw RubyExceptions.CreateArgumentError("invalid character");
                }
                return character.ToString();
            }

            // a binary format has single-byte characters only (MRI: rb_enc_uint_chr)
            if (encoding == RubyEncoding.Binary && codepoint > 0xFF) {
                throw RubyExceptions.CreateRangeError("{0} out of char range", codepoint);
            }

            // MRI: rb_enc_codelen fails for a code point the encoding has no character for
            if (codepoint < 0 || codepoint > 0x10FFFF || (codepoint >= 0xD800 && codepoint <= 0xDFFF)) {
                throw RubyExceptions.CreateArgumentError("invalid character");
            }
            string text = Char.ConvertFromUtf32(codepoint);

            if (codepoint > 0x7F && !IsRepresentable(text)) {
                throw RubyExceptions.CreateArgumentError("invalid character");
            }
            return text;
        }

        /// <summary>True if the text can be encoded in the encoding the result will carry.</summary>
        private bool IsRepresentable(string/*!*/ text) {
            RubyEncoding encoding = _resultEncoding ?? _encoding;
            if (encoding == null) {
                return true;
            }
            try {
                encoding.StrictEncoding.GetBytes(text);
                return true;
            } catch (EncoderFallbackException) {
                return false;
            } catch (ArgumentException) {
                return false;
            }
        }

        private void AppendInspect() {
            MutableString result = _context.Inspect(_opts.Value);
            if (_context.IsObjectTainted(result)) {
                _tainted = true;
            }

            AppendString(result);
        }

        private void AppendString() {
            MutableString/*!*/ str = (_opts.Value == null)
                ? MutableString.CreateEmpty()
                : _siteStorage.ConvertToString(_opts.Value);

            if (_context.IsObjectTainted(str)) {
                _tainted = true;
            }

            AppendString(str);
        }

        private void TrackEncoding(MutableString/*!*/ str) {
            RubyEncoding encoding = str.Encoding;

            // A string of nothing but ASCII goes into any result - but only where an ASCII byte
            // means itself. "world" in UTF-16LE is ASCII in name only: its bytes are not the
            // characters they look like, and MRI will not let them into a UTF-8 result.
            if (str.IsAscii() && (encoding == null || encoding.IsAsciiIdentity)) {
                return;
            }

            if (encoding == null) {
                return;
            }

            // The first non-ASCII argument fixes the encoding of the result, whether it agrees
            // with the format's or (an all-ASCII format) replaces it; every later one has to be
            // in the same encoding. Forgetting to record the first one when it merely agreed let
            // "%s %s" % ["\u3042", "\xFF".b] come out as a binary string holding U+3042.
            if (_argEncoding == null) {
                if (encoding == _resultEncoding) {
                    _argEncoding = encoding;
                    return;
                }
                if (_formatIsAscii && encoding.IsAsciiIdentity) {
                    _argEncoding = encoding;
                    _resultEncoding = encoding;
                    return;
                }
            } else if (encoding == _argEncoding) {
                return;
            }

            throw RubyExceptions.CreateEncodingCompatibilityError(_resultEncoding ?? _encoding, encoding);
        }

        private static int CountCharacters(string/*!*/ str) {
            int count = 0;
            for (int i = 0; i < str.Length; i++, count++) {
                if (i + 1 < str.Length && Char.IsSurrogatePair(str[i], str[i + 1])) {
                    i++;
                }
            }
            return count;
        }

        private void AppendString(MutableString/*!*/ mutable) {
            TrackEncoding(mutable);

            string str;
            try {
                str = mutable.ConvertToString();
            } catch (DecoderFallbackException) {
                // the argument holds bytes that are not valid in its own encoding; Ruby copies them
                // through verbatim. We cannot represent that in the StringBuilder we build the result
                // in, so fall back to the lossy conversion rather than letting a CLR exception escape.
                str = mutable.ToString();
            }

            // Width and precision count characters, and a surrogate pair is one.
            bool pairs = !mutable.KnowsCharIndexIsClrIndex && str.AsSpan().IndexOfAnyInRange('\uD800', '\uDBFF') >= 0;
            if (_opts.Precision != UnspecifiedPrecision && str.Length > _opts.Precision) {
                int end = _opts.Precision;
                if (pairs) {
                    end = 0;
                    for (int i = 0; i < _opts.Precision && end < str.Length; i++) {
                        end += (end + 1 < str.Length && Char.IsSurrogatePair(str[end], str[end + 1])) ? 2 : 1;
                    }
                }
                str = str.Substring(0, end);
            }

            int length = pairs ? CountCharacters(str) : str.Length;
            if (!_opts.LeftAdj && _opts.FieldWidth > length) {
                _buf.Append(' ', _opts.FieldWidth - length);
            }

            _buf.Append(str);

            if (_opts.LeftAdj && _opts.FieldWidth > length) {
                _buf.Append(' ', _opts.FieldWidth - length);
            }
        }

        #endregion

        #region Private data structures

        // The conversion specifier format is as follows:
        //   % conversionFlags fieldWidth . precision conversionType
        // where:
        //   conversionFlags - # 0 - + <space>
        //   conversionType - b B c d E e f G g i o p s u X x a A %

        [Flags]
        internal enum FormatOptions {
            ZeroPad = 0x01, // Use zero-padding to fit FieldWidth
            LeftAdj = 0x02, // Use left-adjustment to fit FieldWidth. Overrides ZeroPad
            AltForm = 0x04, // Add a leading 0 if necessary for octal, or add a leading 0x or 0X for hex
            Space = 0x08, // Leave a white-space
            SignChar = 0x10 // Force usage of a sign char even if the value is positive
        }

        internal struct FormatSettings {

            #region FormatOptions property accessors

            public bool ZeroPad {
                get {
                    return ((Options & FormatOptions.ZeroPad) != 0);
                }
                set {
                    if (value) {
                        Options |= FormatOptions.ZeroPad;
                    } else {
                        Options &= (~FormatOptions.ZeroPad);
                    }
                }
            }
            public bool LeftAdj {
                get {
                    return ((Options & FormatOptions.LeftAdj) != 0);
                }
                set {
                    if (value) {
                        Options |= FormatOptions.LeftAdj;
                    } else {
                        Options &= (~FormatOptions.LeftAdj);
                    }
                }
            }
            public bool AltForm {
                get {
                    return ((Options & FormatOptions.AltForm) != 0);
                }
                set {
                    if (value) {
                        Options |= FormatOptions.AltForm;
                    } else {
                        Options &= (~FormatOptions.AltForm);
                    }
                }
            }
            public bool Space {
                get {
                    return ((Options & FormatOptions.Space) != 0);
                }
                set {
                    if (value) {
                        Options |= FormatOptions.Space;
                    } else {
                        Options &= (~FormatOptions.Space);
                    }
                }
            }
            public bool SignChar {
                get {
                    return ((Options & FormatOptions.SignChar) != 0);
                }
                set {
                    if (value) {
                        Options |= FormatOptions.SignChar;
                    } else {
                        Options &= (~FormatOptions.SignChar);
                    }
                }
            }
            #endregion

            internal FormatOptions Options;

            // Minimum number of characters that the entire formatted string should occupy.
            // Smaller results will be left-padded with white-space or zeros depending on Options
            internal int FieldWidth;

            // Number of significant digits to display, before and after the decimal point.
            internal int Precision;

            internal object Value;

            // If using absolute indexing, the index of the argument that has the data value
            internal int? ArgIndex;

            // %<name>s / %{name} reference
            internal string Name;
            internal char NameStyle;
        }
        #endregion
    }
}
