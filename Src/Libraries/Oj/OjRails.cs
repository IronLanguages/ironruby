/* ****************************************************************************
 *
 * IronRuby's implementation of the oj gem's C extension: :rails mode.
 *
 * A port of the dump half of rails.c (oj 3.17.6): the ActiveSupport-compatible
 * encoder, the table of classes Oj::Rails.optimize turns on, and the optimized
 * writers for Time, BigDecimal, Struct, Enumerable, ActiveRecord and friends.
 *
 * This source code is subject to terms and conditions of the Apache License,
 * Version 2.0. A copy of the license can be found in the License.html file at
 * the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using IronRuby.Builtins;
using IronRuby.Runtime;
using Microsoft.Scripting.Runtime;

namespace IronRuby.StandardLibrary.Oj {

    internal delegate void RailsDumpFunc(object obj, int depth, Out output, bool asOk);

    /// <summary>struct _rOpt.</summary>
    internal sealed class ROpt {
        internal RubyModule Clas;
        internal bool On;
        internal RailsDumpFunc Dump;
    }

    /// <summary>struct _rOptTable: the classes being optimized.</summary>
    internal sealed class RailsOpts {
        internal readonly Dictionary<RubyModule, ROpt>/*!*/ Table = new Dictionary<RubyModule, ROpt>();

        internal RailsOpts/*!*/ Copy() {
            var result = new RailsOpts();
            foreach (var pair in Table) {
                result.Table[pair.Key] = new ROpt { Clas = pair.Value.Clas, On = pair.Value.On, Dump = pair.Value.Dump };
            }
            return result;
        }

        internal ROpt Get(object clas) {
            var module = clas as RubyModule;
            ROpt ro;
            return (module != null && Table.TryGetValue(module, out ro)) ? ro : null;
        }
    }

    internal static class OjRails {

        private static readonly string[] DumpMapNames = {
            "ActionController::Parameters",
            "ActiveRecord::Result",
            "ActiveSupport::TimeWithZone",
            "BigDecimal",
            "Range",
            "Regexp",
            "Time",
        };

        private static RailsDumpFunc DumpMap(string/*!*/ name) {
            switch (name) {
                case "ActionController::Parameters": return DumpActionControllerParameters;
                case "ActiveRecord::Result": return DumpActiveRecordResult;
                case "ActiveSupport::TimeWithZone": return DumpTimeWithZone;
                case "BigDecimal": return DumpBigDecimal;
                case "Range": return DumpToS;
                case "Regexp": return DumpRegexp;
                case "Time": return DumpTime;
            }
            return null;
        }

        #region the optimization table

        internal static object ResolveClassPath(RubyContext/*!*/ context, string/*!*/ name) {
            RubyModule clas = context.ObjectClass;
            foreach (string part in name.Split(new[] { "::" }, StringSplitOptions.None)) {
                object value;
                if (part.Length == 0 || !clas.TryGetConstant(null, part, out value) || !(value is RubyModule)) {
                    return null;
                }
                clas = (RubyModule)value;
            }
            return clas;
        }

        internal static ROpt CreateOpt(RubyContext/*!*/ context, RailsOpts/*!*/ rot, RubyModule/*!*/ clas) {
            var ro = new ROpt { Clas = clas, On = true, Dump = DumpObjAttrs };
            rot.Table[clas] = ro;

            string classname = clas.Name;
            if (classname != null) {
                RailsDumpFunc f = DumpMap(classname);
                if (f != null) {
                    ro.Dump = f;
                }
            }
            if (ro.Dump == (RailsDumpFunc)DumpObjAttrs) {
                OjState state = OjState.Get(context);
                if (state.ActiveRecordBase == null) {
                    // If not defined let an exception be raised, as upstream does.
                    object ar;
                    if (!context.ObjectClass.TryGetConstant(null, "ActiveRecord", out ar) || !(ar is RubyModule)) {
                        throw RubyExceptions.CreateNameError("uninitialized constant ActiveRecord");
                    }
                    object arBase;
                    if (!((RubyModule)ar).TryGetConstant(null, "Base", out arBase)) {
                        throw RubyExceptions.CreateNameError("uninitialized constant ActiveRecord::Base");
                    }
                    state.ActiveRecordBase = arBase;
                }
                var classClas = clas as RubyClass;
                if (state.ActiveRecordBase is RubyModule && clas.HasAncestor((RubyModule)state.ActiveRecordBase)) {
                    ro.Dump = DumpActiveRecord;
                } else if (clas.HasAncestor(context.GetClass(typeof(RubyStruct)))) {
                    ro.Dump = DumpStruct;
                } else if (clas.HasAncestor(context.GetModule(typeof(Enumerable)))) {
                    ro.Dump = DumpEnumerable;
                } else if (clas.HasAncestor(context.ExceptionClass)) {
                    ro.Dump = DumpToS;
                }
            }
            return ro;
        }

        internal static void Optimize(RubyContext/*!*/ context, object[]/*!*/ argv, RailsOpts/*!*/ rot, bool on) {
            OjState state = OjState.Get(context);
            if (argv.Length == 0) {
                state.RailsHashOpt = on;
                state.RailsArrayOpt = on;
                state.RailsFloatOpt = on;

                foreach (string name in DumpMapNames) {
                    var clas = ResolveClassPath(context, name) as RubyModule;
                    if (clas != null && rot.Get(clas) == null) {
                        CreateOpt(context, rot, clas);
                    }
                }
                foreach (var ro in rot.Table.Values) {
                    ro.On = on;
                }
            }
            foreach (object arg in argv) {
                if (ReferenceEquals(arg, context.GetClass(typeof(Hash)))) {
                    state.RailsHashOpt = on;
                } else if (ReferenceEquals(arg, context.GetClass(typeof(RubyArray)))) {
                    state.RailsArrayOpt = on;
                } else if (ReferenceEquals(arg, context.GetClass(typeof(double)))) {
                    state.RailsFloatOpt = on;
                } else {
                    ROpt ro = rot.Get(arg);
                    if (ro == null && arg is RubyModule) {
                        ro = CreateOpt(context, rot, (RubyModule)arg);
                    }
                    if (ro != null) {
                        ro.On = on;
                    }
                }
            }
        }

        #endregion

        #region the optimized writers

        private static void DumpObjAttrs(object obj, int depth, Out output, bool asOk) {
            RubyContext context = output.Context;
            output.Add((byte)'{');
            output.Depth = depth + 1;
            foreach (string name in context.GetInstanceVariableNames(obj)) {
                object value;
                if (!context.TryGetInstanceVariable(obj, name, out value)) {
                    continue;
                }
                int d = output.Depth;
                output.FillIndent(d);
                if (name.StartsWith("@", StringComparison.Ordinal)) {
                    OjDump.DumpCstr(output, name.Substring(1));
                } else {
                    string attr = "~" + name;
                    if (attr.Length > 31) {
                        attr = attr.Substring(0, 31);
                    }
                    OjDump.DumpCstr(output, attr);
                }
                output.Add((byte)':');
                DumpRailsVal(value, d, output, true);
                output.Depth = d;
                output.Add((byte)',');
            }
            if (',' == output.Last) {
                output.Cur--;
            }
            output.Depth = depth;
            output.FillIndent(depth);
            output.Add((byte)'}');
        }

        private static void DumpStruct(object obj, int depth, Out output, bool asOk) {
            RubyContext context = output.Context;
            int d3 = depth + 2;
            var st = (RubyStruct)obj;
            var members = st.GetNames();
            int cnt = st.ItemCount;
            output.Add((byte)'{');
            for (int i = 0; i < cnt; i++) {
                if (0 < i) {
                    output.Add((byte)',');
                }
                output.FillIndent(d3);
                output.Add((byte)'"');
                output.Add(Encoding.UTF8.GetBytes(members[i]));
                output.Add((byte)'"');
                output.AddSeparator();
                DumpRailsVal(st[i], d3, output, true);
            }
            output.FillIndent(depth);
            output.Add((byte)'}');
        }

        private static void DumpEnumerable(object obj, int depth, Out output, bool asOk) {
            DumpRailsVal(Sites.Call(output.Context, obj, "to_a"), depth, output, false);
        }

        private static void DumpBigDecimal(object obj, int depth, Out output, bool asOk) {
            MutableString rstr = OjDump.SafeToS(output.Context, obj);
            string str = rstr.ToString();
            if (str.StartsWith("I", StringComparison.Ordinal) || str.StartsWith("N", StringComparison.Ordinal) ||
                str.StartsWith("-I", StringComparison.Ordinal)) {
                OjDump.DumpNil(output);
            } else if (output.Opts.IntRangeMax != 0 || output.Opts.IntRangeMin != 0) {
                OjDump.DumpCstr(output, rstr.ToByteArray());
            } else if (YesNo.Yes == output.Opts.BigdecAsNum) {
                OjDump.DumpRaw(output, rstr.ToByteArray());
            } else {
                OjDump.DumpCstr(output, rstr.ToByteArray());
            }
        }

        private struct TimeInfo {
            internal int Year, Mon, Day, Hour, Min, Sec;
        }

        private const long SecsPerDay = 86400L;
        private const long SecsPerYear = 31536000L;
        private const long SecsPerLeap = 31622400L;
        private const long SecsPerQuadYear = SecsPerYear * 3 + SecsPerLeap;
        private const long SecsPerCent = SecsPerQuadYear * 24 + SecsPerYear * 4;
        private const long SecsPerLeapCent = SecsPerCent + SecsPerDay;
        private const long SecsPerQuadCent = SecsPerCent * 4 + SecsPerDay;

        private static readonly long[] EomSecs = {
            2678400, 5097600, 7776000, 10368000, 13046400, 15638400, 18316800, 20995200, 23587200, 26265600, 28857600, 31536000,
        };

        private static readonly long[] EomLeapSecs = {
            2678400, 5184000, 7862400, 10454400, 13132800, 15724800, 18403200, 21081600, 23673600, 26352000, 28944000, 31622400,
        };

        /// <summary>util.c's sec_as_time.</summary>
        private static TimeInfo SecAsTime(long secs) {
            long qc, c = 0, qy = 0, y = 0;
            bool leap = false;
            long shift = 0;
            var ti = new TimeInfo();

            secs += 62167219200L;
            if (secs < 0) {
                shift = -secs / SecsPerQuadCent;
                shift++;
                secs += shift * SecsPerQuadCent;
            }
            qc = secs / SecsPerQuadCent;
            secs = secs - qc * SecsPerQuadCent;
            if (secs < SecsPerLeap) {
                leap = true;
            } else if (secs < SecsPerQuadYear) {
                if (SecsPerLeap <= secs) {
                    secs -= SecsPerLeap;
                    y = secs / SecsPerYear;
                    secs = secs - y * SecsPerYear;
                    y++;
                    leap = false;
                }
            } else if (secs < SecsPerLeapCent) {
                qy = secs / SecsPerQuadYear;
                secs = secs - qy * SecsPerQuadYear;
                if (secs < SecsPerLeap) {
                    leap = true;
                } else {
                    secs -= SecsPerLeap;
                    y = secs / SecsPerYear;
                    secs = secs - y * SecsPerYear;
                    y++;
                }
            } else {
                secs -= SecsPerLeapCent;
                c = secs / SecsPerCent;
                secs = secs - c * SecsPerCent;
                c++;
                if (secs < SecsPerYear * 4) {
                    y = secs / SecsPerYear;
                    secs = secs - y * SecsPerYear;
                } else {
                    secs -= SecsPerYear * 4;
                    qy = secs / SecsPerQuadYear;
                    secs = secs - qy * SecsPerQuadYear;
                    qy++;
                    if (secs < SecsPerLeap) {
                        leap = true;
                    } else {
                        secs -= SecsPerLeap;
                        y = secs / SecsPerYear;
                        secs = secs - y * SecsPerYear;
                        y++;
                    }
                }
            }
            ti.Year = (int)((qc - shift) * 400 + c * 100 + qy * 4 + y);
            long[] ms = leap ? EomLeapSecs : EomSecs;
            for (int m = 1; m <= 12; m++) {
                if (secs < ms[m - 1]) {
                    if (1 < m) {
                        secs -= ms[m - 2];
                    }
                    ti.Mon = m;
                    break;
                }
            }
            ti.Day = (int)(secs / 86400L);
            secs = secs - (long)ti.Day * 86400L;
            ti.Day++;
            ti.Hour = (int)(secs / 3600L);
            secs = secs - (long)ti.Hour * 3600L;
            ti.Min = (int)(secs / 60L);
            secs = secs - (long)ti.Min * 60L;
            ti.Sec = (int)secs;
            return ti;
        }

        private static string D2(int v) {
            return v < 0 ? "-" + (-v).ToString("00", CultureInfo.InvariantCulture) : v.ToString("00", CultureInfo.InvariantCulture);
        }

        private static string D4(int v) {
            return v < 0 ? "-" + (-v).ToString("000", CultureInfo.InvariantCulture) : v.ToString("0000", CultureInfo.InvariantCulture);
        }

        private static void DumpSecNano(object obj, long sec, long nsec, Out output) {
            RubyContext context = output.Context;
            OjState state = output.State;
            long one = 1000000000;
            long tzsecs = OjDump.ToFixnum(Sites.Call(context, obj, "utc_offset"));
            int tzhour, tzmin;
            char tzsign = '+';
            string buf;

            if (9 > output.Opts.SecPrec) {
                // Rails floors rather than rounds when it drops precision
                for (int i = 9 - output.Opts.SecPrec; 0 < i; i--) {
                    nsec = nsec / 10;
                    one /= 10;
                }
                if (one <= nsec) {
                    nsec -= one;
                    sec++;
                }
            }
            sec += tzsecs;
            TimeInfo ti = SecAsTime(sec);
            if (0 > tzsecs) {
                tzsign = '-';
                tzhour = (int)(tzsecs / -3600);
                tzmin = (int)(tzsecs / -60) - (tzhour * 60);
            } else {
                tzhour = (int)(tzsecs / 3600);
                tzmin = (int)(tzsecs / 60) - (tzhour * 60);
            }
            string date = D4(ti.Year) + "-" + D2(ti.Mon) + "-" + D2(ti.Day) + "T" + D2(ti.Hour) + ":" + D2(ti.Min) + ":" + D2(ti.Sec);
            if (!state.XmlTime) {
                buf = D4(ti.Year) + "/" + D2(ti.Mon) + "/" + D2(ti.Day) + " " + D2(ti.Hour) + ":" + D2(ti.Min) + ":" + D2(ti.Sec) +
                    " " + tzsign + D2(tzhour) + D2(tzmin);
            } else if (0 == output.Opts.SecPrec) {
                if (0 == tzsecs && Protocols.IsTrue(Sites.Call(context, obj, "utc?"))) {
                    buf = date + "Z";
                } else {
                    buf = date + tzsign + D2(tzhour) + ":" + D2(tzmin);
                }
            } else {
                int width = Math.Min(output.Opts.SecPrec, 9);
                string frac = nsec.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');
                if (0 == tzsecs && Protocols.IsTrue(Sites.Call(context, obj, "utc?"))) {
                    buf = date + "." + frac + "Z";
                } else {
                    buf = date + "." + frac + tzsign + D2(tzhour) + ":" + D2(tzmin);
                }
            }
            OjDump.DumpCstr(output, Encoding.ASCII.GetBytes(buf));
        }

        private static void DumpTime(object obj, int depth, Out output, bool asOk) {
            RubyContext context = output.Context;
            long sec = OjDump.ToFixnum(Sites.Call(context, obj, "tv_sec"));
            long nsec = OjDump.ToFixnum(Sites.Call(context, obj, "tv_nsec"));
            DumpSecNano(obj, sec, nsec, output);
        }

        private static void DumpTimeWithZone(object obj, int depth, Out output, bool asOk) {
            RubyContext context = output.Context;
            long sec = OjDump.ToFixnum(Sites.Call(context, obj, "tv_sec"));
            long nsec = 0;
            if (Sites.RespondTo(context, obj, "tv_nsec")) {
                nsec = OjDump.ToFixnum(Sites.Call(context, obj, "tv_nsec"));
            } else if (Sites.RespondTo(context, obj, "tv_usec")) {
                nsec = OjDump.ToFixnum(Sites.Call(context, obj, "tv_usec")) * 1000;
            }
            DumpSecNano(obj, sec, nsec, output);
        }

        private static void DumpToS(object obj, int depth, Out output, bool asOk) {
            OjDump.DumpObjToS(output, obj);
        }

        private static void DumpActionControllerParameters(object obj, int depth, Out output, bool asOk) {
            object[] savedArgv = output.Argv;
            object value;
            output.Context.TryGetInstanceVariable(obj, "@parameters", out value);
            output.Argv = new object[0];
            DumpRailsVal(value, depth, output, true);
            output.Argv = savedArgv;
        }

        private static void DumpRow(RubyArray/*!*/ row, List<byte[]>/*!*/ cols, int depth, Out output) {
            int d2 = depth + 1;
            output.Add((byte)'{');
            for (int i = 0; i < cols.Count; i++) {
                if (output.Opts.Use) {
                    output.AddNlIndent(output.Opts.ArrayNl, d2);
                } else {
                    output.FillIndent(d2);
                }
                OjDump.DumpCstr(output, cols[i]);
                output.Add((byte)':');
                DumpRailsVal(i < row.Count ? row[i] : null, depth, output, true);
                if (i < cols.Count - 1) {
                    output.Add((byte)',');
                }
            }
            if (output.Opts.Use) {
                output.AddNlIndent(output.Opts.ArrayNl, depth);
            } else {
                output.FillIndent(depth);
            }
            output.Add((byte)'}');
        }

        private static void DumpActiveRecordResult(object obj, int depth, Out output, bool asOk) {
            RubyContext context = output.Context;
            object[] savedArgv = output.Argv;
            int d2 = depth + 1;
            output.Argv = new object[0];

            object rcols, rrows;
            context.TryGetInstanceVariable(obj, "@columns", out rcols);
            context.TryGetInstanceVariable(obj, "@rows", out rrows);
            var cols = new List<byte[]>();
            foreach (object v in (RubyArray)rcols) {
                var s = v as MutableString ?? OjDump.SafeToS(context, v);
                cols.Add(s.ToByteArray());
            }
            var rows = (RubyArray)rrows;
            output.Add((byte)'[');
            for (int i = 0; i < rows.Count; i++) {
                if (output.Opts.Use) {
                    output.AddNlIndent(output.Opts.ArrayNl, d2);
                } else {
                    output.FillIndent(d2);
                }
                DumpRow((RubyArray)rows[i], cols, d2, output);
                if (i < rows.Count - 1) {
                    output.Add((byte)',');
                }
            }
            if (output.Opts.Use) {
                output.AddNlIndent(output.Opts.ArrayNl, depth);
            } else {
                output.FillIndent(depth);
            }
            output.Add((byte)']');
            output.Argv = savedArgv;
        }

        private static void DumpActiveRecord(object obj, int depth, Out output, bool asOk) {
            object[] savedArgv = output.Argv;
            object value;
            output.Context.TryGetInstanceVariable(obj, "@attributes", out value);
            output.Argv = new object[0];
            DumpRailsVal(value, depth, output, true);
            output.Argv = savedArgv;
        }

        #endregion

        #region the as_json protocol

        private static void DumpAsString(object obj, int depth, Out output, bool asOk) {
            object[] savedArgv = output.Argv;
            if (OjDump.CodeDump(output, obj, depth)) {
                output.Argv = savedArgv;
                return;
            }
            OjDump.DumpObjToS(output, obj);
        }

        private static void DumpAsJson(object obj, int depth, Out output, bool asOk) {
            RubyContext context = output.Context;
            object ja;
            bool selected;
            object[] savedArgv = output.Argv;

            // Some classes elect to not take an options argument so check the arity of as_json.
            if (0 == OjState.MethodArity(context, obj, "as_json")) {
                ja = Sites.Call(context, obj, "as_json");
                selected = false;
            } else {
                selected = 0 < output.Argc;
                ja = Sites.CallN(context, obj, "as_json", output.Argv);
            }

            output.Argv = new object[0];
            bool keyFilterOff = output.KeyFilterOff;
            if (selected) {
                output.KeyFilterOff = true;
            }
            if (ReferenceEquals(ja, obj) || !asOk) {
                // once as_json is called it should never be called again on the same object with as_ok
                DumpRailsVal(ja, depth, output, false);
            } else {
                DumpRailsVal(ja, depth, output, true);
            }
            output.KeyFilterOff = keyFilterOff;
            output.Argv = savedArgv;
        }

        private static void DumpRegexp(object obj, int depth, Out output, bool asOk) {
            if (asOk && Sites.RespondTo(output.Context, obj, "as_json")) {
                DumpAsJson(obj, depth, output, false);
                return;
            }
            DumpAsString(obj, depth, output, asOk);
        }

        private static void DumpToHash(object obj, int depth, Out output) {
            DumpRailsVal(Sites.Call(output.Context, obj, "to_hash"), depth, output, true);
        }

        private static void DumpFloat(object obj, Out output) {
            double d = OjDump.ToDouble(output.Context, obj);
            if (0.0 == d) {
                output.Add("0.0");
            } else if (Double.IsNaN(d) || Double.IsInfinity(d)) {
                output.Add("null");
            } else if (OjDump.IsLongLong(d)) {
                output.Add(OjDump.DotOne(d));
            } else if (output.State.RailsFloatOpt) {
                output.Add(OjDump.FloatPrintf(output.Context, obj, d, "%0.16g"));
            } else {
                // copied up to the first NUL, as upstream copies it
                string s = OjDump.SafeToS(output.Context, obj).ToString();
                int nul = s.IndexOf('\0');
                output.Add(Encoding.UTF8.GetBytes(nul >= 0 ? s.Substring(0, nul) : s));
            }
        }

        private static void DumpArray(RubyArray/*!*/ a, int depth, Out output, bool asOk) {
            int d2 = depth + 1;
            if (YesNo.Yes == output.Opts.Circular) {
                if (0 > OjDump.CheckCircular(output, a)) {
                    OjDump.DumpNil(output);
                    return;
                }
            }
            if (!output.State.RailsArrayOpt && asOk && Sites.RespondTo(output.Context, a, "as_json")) {
                DumpAsJson(a, depth, output, false);
                return;
            }
            int cnt = a.Count;
            output.Add((byte)'[');
            if (0 == cnt) {
                output.Add((byte)']');
                return;
            }
            cnt--;
            for (int i = 0; i <= cnt; i++) {
                if (output.Opts.Use) {
                    output.AddNlIndent(output.Opts.ArrayNl, d2);
                } else {
                    output.FillIndent(d2);
                }
                DumpRailsVal(i < a.Count ? a[i] : null, d2, output, true);
                if (i < cnt) {
                    output.Add((byte)',');
                }
            }
            if (output.Opts.Use) {
                output.AddNlIndent(output.Opts.ArrayNl, depth);
            } else {
                output.FillIndent(depth);
            }
            output.Add((byte)']');
        }

        private static void DumpHash(Hash/*!*/ obj, int depth, Out output, bool asOk) {
            if (YesNo.Yes == output.Opts.Circular) {
                if (0 > OjDump.CheckCircular(output, obj)) {
                    OjDump.DumpNil(output);
                    return;
                }
            }
            if (!output.State.RailsHashOpt && asOk && Sites.RespondTo(output.Context, obj, "as_json")) {
                DumpAsJson(obj, depth, output, false);
                return;
            }
            int cnt = obj.Count;
            output.Add((byte)'{');
            if (0 == cnt) {
                output.Add((byte)'}');
                return;
            }
            output.Depth = depth + 1;
            foreach (var pair in OjDump.Snapshot(obj)) {
                object key = CustomStringDictionary.ObjToNull(pair.Key);
                object value = pair.Value;
                int d = output.Depth;
                if (output.OmitNil && value == null) {
                    continue;
                }
                if (!output.KeyFilterOff && (output.Opts.Only != null || output.Opts.Except != null)) {
                    if (OjDump.KeySkip(key, output.Opts.Only, output.Opts.Except)) {
                        continue;
                    }
                }
                if (!output.Opts.Use) {
                    output.FillIndent(d);
                    OjDump.DumpKey(output, key);
                    output.Add((byte)':');
                } else {
                    output.AddNlIndent(output.Opts.HashNl, d);
                    OjDump.DumpKey(output, key);
                    output.AddSeparator();
                }
                DumpRailsVal(value, d, output, true);
                output.Depth = d;
                output.Add((byte)',');
            }
            if (',' == output.Last) {
                output.Cur--;
            }
            if (!output.Opts.Use) {
                output.FillIndent(depth);
            } else {
                output.AddNlIndent(output.Opts.HashNl, depth);
            }
            output.Add((byte)'}');
        }

        private static void DumpObj(object obj, int depth, Out output, bool asOk) {
            RubyContext context = output.Context;
            object[] savedArgv = output.Argv;

            if (OjDump.CodeDump(output, obj, depth)) {
                output.Argv = savedArgv;
                return;
            }
            RubyClass clas = context.GetClassOf(obj);
            bool isBigDecimal = ReferenceEquals(clas, OjState.BigDecimalClass(context));
            if (asOk) {
                ROpt ro = (output.Ropts ?? output.State.GlobalRopts).Get(clas);
                if (ro != null && ro.On) {
                    ro.Dump(obj, depth, output, asOk);
                } else if (YesNo.Yes == output.Opts.RawJson && Sites.RespondTo(context, obj, "raw_json")) {
                    OjDump.DumpRawJson(output, obj, depth);
                } else if (Sites.RespondTo(context, obj, "as_json")) {
                    DumpAsJson(obj, depth, output, true);
                } else if (Sites.RespondTo(context, obj, "to_hash")) {
                    DumpToHash(obj, depth, output);
                } else if (isBigDecimal) {
                    DumpBigDecimal(obj, depth, output, false);
                } else {
                    OjDump.DumpObjToS(output, obj);
                }
            } else if (YesNo.Yes == output.Opts.RawJson && Sites.RespondTo(context, obj, "raw_json")) {
                OjDump.DumpRawJson(output, obj, depth);
            } else if (Sites.RespondTo(context, obj, "to_hash")) {
                DumpToHash(obj, depth, output);
            } else if (isBigDecimal) {
                DumpBigDecimal(obj, depth, output, false);
            } else {
                OjDump.DumpObjToS(output, obj);
            }
        }

        /// <summary>rails.c's dump_rails_val.</summary>
        internal static void DumpRailsVal(object obj, int depth, Out output, bool asOk) {
            if (OjDump.MaxDepth < depth) {
                throw new OutOfMemoryException("Too deeply nested.\n");
            }
            switch (OjDump.TypeOf(output.Context, obj)) {
                case RType.Nil: OjDump.DumpNil(output); return;
                case RType.True: OjDump.DumpTrue(output); return;
                case RType.False: OjDump.DumpFalse(output); return;
                case RType.Fixnum: OjDump.DumpFixnum(output, OjDump.ToFixnum(obj)); return;
                case RType.Bignum: OjDump.DumpBignum(output, OjDump.ToBignum(obj)); return;
                case RType.Float: DumpFloat(obj, output); return;
                case RType.String: OjDump.DumpStr(output, (MutableString)obj); return;
                case RType.Symbol: OjDump.DumpSym(output, (RubySymbol)obj); return;
                case RType.Array: DumpArray((RubyArray)obj, depth, output, asOk); return;
                case RType.Hash: DumpHash((Hash)obj, depth, output, asOk); return;
                case RType.Class:
                case RType.Module: OjDump.DumpClass(output, (RubyModule)obj); return;
                case RType.Regexp: DumpRegexp(obj, depth, output, asOk); return;
                case RType.Struct:
                case RType.Object:
                case RType.Data: DumpObj(obj, depth, output, asOk); return;
                case RType.File:
                case RType.Complex:
                case RType.Rational: DumpAsString(obj, depth, output, asOk); return;
            }
            OjDump.DumpNil(output);
        }

        /// <summary>oj_dump_rails_val.</summary>
        internal static void DumpRailsTop(object obj, int depth, Out output) {
            output.Opts.StrRx = new RxClass();
            output.Opts.EscapeMode = output.State.EscapeHtml ? Esc.RailsX : Esc.Rails;
            DumpRailsVal(obj, depth, output, true);
        }

        /// <summary>rails.c's encode.</summary>
        internal static MutableString/*!*/ Encode(RubyContext/*!*/ context, object obj, RailsOpts ropts, Options/*!*/ opts, object[]/*!*/ argv) {
            OjState state = OjState.Get(context);
            Options copts = opts.Clone();
            copts.StrRx = new RxClass();
            copts.Mode = OjMode.Rails;
            copts.EscapeMode = state.EscapeHtml ? Esc.RailsX : Esc.Rails;

            var output = new Out(context, copts);
            output.OmitNil = copts.OmitNil;
            output.Indent = copts.Indent;
            output.Argv = argv;
            output.Ropts = ropts;

            DumpRailsVal(obj, 0, output, true);

            if (0 < output.Indent) {
                byte last = output.Last;
                if (last == ']' || last == '}') {
                    output.Add((byte)'\n');
                }
            }
            return output.ToUtf8CString();
        }

        #endregion
    }
}
