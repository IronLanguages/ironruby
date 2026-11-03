/* ****************************************************************************
 *
 * IronRuby's implementation of the sqlite3 gem's C extension.
 *
 * The gem is split in two: a C extension that binds libsqlite3 (SQLite3::Database,
 * SQLite3::Statement and the exception hierarchy) and a Ruby half on top of it
 * (Database#execute, ResultSet, Pragmas, ...).  This file is the first half; the
 * second is Src/StdLib/ironruby/sqlite3.rb and its sqlite3/ subtree, which is a
 * faithful port of the gem's own Ruby files.
 *
 * The binding goes through SQLitePCL.raw - the 1:1 P/Invoke surface over the C
 * API that the Microsoft.Data.Sqlite package carries - rather than through
 * Microsoft.Data.Sqlite's ADO.NET classes.  SqliteConnection owns a connection
 * pool, rewrites some SQL, caches statements and hands back CLR types; none of
 * that is what a sqlite3_stmt-shaped Ruby API wants.  raw.sqlite3_* is exactly
 * the API the gem's C extension calls, so the semantics come out the same.
 *
 * This source code is subject to terms and conditions of the Apache License,
 * Version 2.0. A copy of the license can be found in the License.html file at
 * the root of this distribution.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using IronRuby.Builtins;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using Microsoft.Scripting.Runtime;
using System.Runtime.CompilerServices;
using SQLitePCL;

namespace IronRuby.StandardLibrary.Sqlite3 {

    [RubyModule("SQLite3")]
    public static class Sqlite3 {

        #region Provider initialization

        private static readonly object/*!*/ _initLock = new object();
        private static bool _initialized;

        /// <summary>
        /// SQLitePCLRaw needs a provider bound to it before any sqlite3_* call.  Batteries_V2
        /// binds e_sqlite3, the native SQLite that ships in the package under
        /// runtimes/&lt;rid&gt;/native.  Doing it lazily rather than in a static constructor keeps
        /// the cost off anyone who never requires sqlite3.
        /// </summary>
        internal static void EnsureInitialized() {
            if (_initialized) {
                return;
            }
            lock (_initLock) {
                if (!_initialized) {
                    Batteries_V2.Init();
                    _initialized = true;
                }
            }
        }

        #endregion

        #region Module methods

        [RubyMethod("libversion", RubyMethodAttributes.PublicSingleton)]
        public static int LibVersion(RubyModule/*!*/ self) {
            EnsureInitialized();
            return raw.sqlite3_libversion_number();
        }

        [RubyMethod("threadsafe", RubyMethodAttributes.PublicSingleton)]
        public static int Threadsafe(RubyModule/*!*/ self) {
            EnsureInitialized();
            return raw.sqlite3_threadsafe();
        }

        [RubyMethod("sqlcipher?", RubyMethodAttributes.PublicSingleton)]
        public static bool IsSqlCipher(RubyModule/*!*/ self) {
            return false;
        }

        [RubyMethod("sqlite_version_string", RubyMethodAttributes.PublicSingleton)]
        public static MutableString/*!*/ SqliteVersionString(RubyModule/*!*/ self) {
            EnsureInitialized();
            return MutableString.Create(raw.sqlite3_libversion().utf8_to_string(), RubyEncoding.UTF8);
        }

        [RubyMethod("status", RubyMethodAttributes.PublicSingleton)]
        public static Hash/*!*/ Status(RubyContext/*!*/ context, RubyModule/*!*/ self, int parameter, [Optional]object resetFlag) {
            EnsureInitialized();
            int current, highwater;
            raw.sqlite3_status(parameter, out current, out highwater, Protocols.IsTrue(resetFlag) ? 1 : 0);
            var result = new Hash(context);
            result[context.CreateAsciiSymbol("current")] = ScriptingRuntimeHelpers.Int32ToObject(current);
            result[context.CreateAsciiSymbol("highwater")] = ScriptingRuntimeHelpers.Int32ToObject(highwater);
            return result;
        }

        /// <summary>
        /// sqlite3_error_offset is the one entry point the gem uses that SQLitePCL.raw does
        /// not bind, and SQLite3::Exception#message needs it to draw the caret under the
        /// offending token.  It is imported straight from the same native library
        /// SQLitePCLRaw loaded ("e_sqlite3"), and answers -1 on anything older than 3.38.
        /// </summary>
        private static class Native {
            [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
            internal static extern int sqlite3_error_offset(IntPtr db);
        }

        private static bool _errorOffsetUnavailable;

        internal static int ErrorOffset(sqlite3 db) {
            if (db == null || _errorOffsetUnavailable) {
                return -1;
            }
            try {
                return Native.sqlite3_error_offset(db.DangerousGetHandle());
            } catch (EntryPointNotFoundException) {
                _errorOffsetUnavailable = true;
                return -1;
            } catch (DllNotFoundException) {
                _errorOffsetUnavailable = true;
                return -1;
            }
        }

        #endregion

        #region Exceptions

        /// <summary>
        /// SQLite3::Exception.  #code, #sql and #sql_offset are instance variables set here
        /// and read by attr_readers in Src/StdLib/ironruby/sqlite3/errors.rb, the way
        /// KeyError#key is done in the core (see StringFormatter.CreateKeyError).
        /// </summary>
        [RubyException("Exception")]
        public class SqliteException : SystemException {
            public SqliteException() : this(null, null) { }
            public SqliteException(string message) : this(message, null) { }
            public SqliteException(string message, Exception inner) : base(message ?? "SQLite3::Exception", inner) { }
        }

        [RubyException("SQLException")]
        public class SQLException : SqliteException {
            public SQLException() : this(null, null) { }
            public SQLException(string message) : this(message, null) { }
            public SQLException(string message, Exception inner) : base(message ?? "SQLException", inner) { }
        }

        [RubyException("InternalException")]
        public class InternalException : SqliteException {
            public InternalException() : this(null, null) { }
            public InternalException(string message) : this(message, null) { }
            public InternalException(string message, Exception inner) : base(message ?? "InternalException", inner) { }
        }

        [RubyException("PermissionException")]
        public class PermissionException : SqliteException {
            public PermissionException() : this(null, null) { }
            public PermissionException(string message) : this(message, null) { }
            public PermissionException(string message, Exception inner) : base(message ?? "PermissionException", inner) { }
        }

        [RubyException("AbortException")]
        public class AbortException : SqliteException {
            public AbortException() : this(null, null) { }
            public AbortException(string message) : this(message, null) { }
            public AbortException(string message, Exception inner) : base(message ?? "AbortException", inner) { }
        }

        [RubyException("BusyException")]
        public class BusyException : SqliteException {
            public BusyException() : this(null, null) { }
            public BusyException(string message) : this(message, null) { }
            public BusyException(string message, Exception inner) : base(message ?? "BusyException", inner) { }
        }

        [RubyException("LockedException")]
        public class LockedException : SqliteException {
            public LockedException() : this(null, null) { }
            public LockedException(string message) : this(message, null) { }
            public LockedException(string message, Exception inner) : base(message ?? "LockedException", inner) { }
        }

        [RubyException("MemoryException")]
        public class MemoryException : SqliteException {
            public MemoryException() : this(null, null) { }
            public MemoryException(string message) : this(message, null) { }
            public MemoryException(string message, Exception inner) : base(message ?? "MemoryException", inner) { }
        }

        [RubyException("ReadOnlyException")]
        public class ReadOnlyException : SqliteException {
            public ReadOnlyException() : this(null, null) { }
            public ReadOnlyException(string message) : this(message, null) { }
            public ReadOnlyException(string message, Exception inner) : base(message ?? "ReadOnlyException", inner) { }
        }

        [RubyException("InterruptException")]
        public class InterruptException : SqliteException {
            public InterruptException() : this(null, null) { }
            public InterruptException(string message) : this(message, null) { }
            public InterruptException(string message, Exception inner) : base(message ?? "InterruptException", inner) { }
        }

        [RubyException("IOException")]
        public class IOException : SqliteException {
            public IOException() : this(null, null) { }
            public IOException(string message) : this(message, null) { }
            public IOException(string message, Exception inner) : base(message ?? "IOException", inner) { }
        }

        [RubyException("CorruptException")]
        public class CorruptException : SqliteException {
            public CorruptException() : this(null, null) { }
            public CorruptException(string message) : this(message, null) { }
            public CorruptException(string message, Exception inner) : base(message ?? "CorruptException", inner) { }
        }

        [RubyException("NotFoundException")]
        public class NotFoundException : SqliteException {
            public NotFoundException() : this(null, null) { }
            public NotFoundException(string message) : this(message, null) { }
            public NotFoundException(string message, Exception inner) : base(message ?? "NotFoundException", inner) { }
        }

        [RubyException("FullException")]
        public class FullException : SqliteException {
            public FullException() : this(null, null) { }
            public FullException(string message) : this(message, null) { }
            public FullException(string message, Exception inner) : base(message ?? "FullException", inner) { }
        }

        [RubyException("CantOpenException")]
        public class CantOpenException : SqliteException {
            public CantOpenException() : this(null, null) { }
            public CantOpenException(string message) : this(message, null) { }
            public CantOpenException(string message, Exception inner) : base(message ?? "CantOpenException", inner) { }
        }

        [RubyException("ProtocolException")]
        public class ProtocolException : SqliteException {
            public ProtocolException() : this(null, null) { }
            public ProtocolException(string message) : this(message, null) { }
            public ProtocolException(string message, Exception inner) : base(message ?? "ProtocolException", inner) { }
        }

        [RubyException("EmptyException")]
        public class EmptyException : SqliteException {
            public EmptyException() : this(null, null) { }
            public EmptyException(string message) : this(message, null) { }
            public EmptyException(string message, Exception inner) : base(message ?? "EmptyException", inner) { }
        }

        [RubyException("SchemaChangedException")]
        public class SchemaChangedException : SqliteException {
            public SchemaChangedException() : this(null, null) { }
            public SchemaChangedException(string message) : this(message, null) { }
            public SchemaChangedException(string message, Exception inner) : base(message ?? "SchemaChangedException", inner) { }
        }

        [RubyException("TooBigException")]
        public class TooBigException : SqliteException {
            public TooBigException() : this(null, null) { }
            public TooBigException(string message) : this(message, null) { }
            public TooBigException(string message, Exception inner) : base(message ?? "TooBigException", inner) { }
        }

        [RubyException("ConstraintException")]
        public class ConstraintException : SqliteException {
            public ConstraintException() : this(null, null) { }
            public ConstraintException(string message) : this(message, null) { }
            public ConstraintException(string message, Exception inner) : base(message ?? "ConstraintException", inner) { }
        }

        [RubyException("MismatchException")]
        public class MismatchException : SqliteException {
            public MismatchException() : this(null, null) { }
            public MismatchException(string message) : this(message, null) { }
            public MismatchException(string message, Exception inner) : base(message ?? "MismatchException", inner) { }
        }

        [RubyException("MisuseException")]
        public class MisuseException : SqliteException {
            public MisuseException() : this(null, null) { }
            public MisuseException(string message) : this(message, null) { }
            public MisuseException(string message, Exception inner) : base(message ?? "MisuseException", inner) { }
        }

        [RubyException("UnsupportedException")]
        public class UnsupportedException : SqliteException {
            public UnsupportedException() : this(null, null) { }
            public UnsupportedException(string message) : this(message, null) { }
            public UnsupportedException(string message, Exception inner) : base(message ?? "UnsupportedException", inner) { }
        }

        [RubyException("AuthorizationException")]
        public class AuthorizationException : SqliteException {
            public AuthorizationException() : this(null, null) { }
            public AuthorizationException(string message) : this(message, null) { }
            public AuthorizationException(string message, Exception inner) : base(message ?? "AuthorizationException", inner) { }
        }

        [RubyException("FormatException")]
        public class FormatException : SqliteException {
            public FormatException() : this(null, null) { }
            public FormatException(string message) : this(message, null) { }
            public FormatException(string message, Exception inner) : base(message ?? "FormatException", inner) { }
        }

        [RubyException("RangeException")]
        public class RangeException : SqliteException {
            public RangeException() : this(null, null) { }
            public RangeException(string message) : this(message, null) { }
            public RangeException(string message, Exception inner) : base(message ?? "RangeException", inner) { }
        }

        [RubyException("NotADatabaseException")]
        public class NotADatabaseException : SqliteException {
            public NotADatabaseException() : this(null, null) { }
            public NotADatabaseException(string message) : this(message, null) { }
            public NotADatabaseException(string message, Exception inner) : base(message ?? "NotADatabaseException", inner) { }
        }

        /// <summary>
        /// The gem's status2klass(): the class is chosen from the *primary* result code
        /// (status &amp; 0xff), while #code keeps the full, possibly extended, code.
        /// </summary>
        private static SqliteException/*!*/ CreateByCode(int code, string message) {
            switch (code & 0xff) {
                case raw.SQLITE_ERROR: return new SQLException(message);
                case raw.SQLITE_INTERNAL: return new InternalException(message);
                case raw.SQLITE_PERM: return new PermissionException(message);
                case raw.SQLITE_ABORT: return new AbortException(message);
                case raw.SQLITE_BUSY: return new BusyException(message);
                case raw.SQLITE_LOCKED: return new LockedException(message);
                case raw.SQLITE_NOMEM: return new MemoryException(message);
                case raw.SQLITE_READONLY: return new ReadOnlyException(message);
                case raw.SQLITE_INTERRUPT: return new InterruptException(message);
                case raw.SQLITE_IOERR: return new IOException(message);
                case raw.SQLITE_CORRUPT: return new CorruptException(message);
                case raw.SQLITE_NOTFOUND: return new NotFoundException(message);
                case raw.SQLITE_FULL: return new FullException(message);
                case raw.SQLITE_CANTOPEN: return new CantOpenException(message);
                case raw.SQLITE_PROTOCOL: return new ProtocolException(message);
                case raw.SQLITE_EMPTY: return new EmptyException(message);
                case raw.SQLITE_SCHEMA: return new SchemaChangedException(message);
                case raw.SQLITE_TOOBIG: return new TooBigException(message);
                case raw.SQLITE_CONSTRAINT: return new ConstraintException(message);
                case raw.SQLITE_MISMATCH: return new MismatchException(message);
                case raw.SQLITE_MISUSE: return new MisuseException(message);
                case raw.SQLITE_NOLFS: return new UnsupportedException(message);
                case raw.SQLITE_AUTH: return new AuthorizationException(message);
                case raw.SQLITE_FORMAT: return new FormatException(message);
                case raw.SQLITE_RANGE: return new RangeException(message);
                case raw.SQLITE_NOTADB: return new NotADatabaseException(message);
                default: return new SqliteException(message);
            }
        }

        internal static Exception/*!*/ MakeError(RubyContext/*!*/ context, int code, string message, MutableString sql, int sqlOffset) {
            SqliteException result = CreateByCode(code, message);
            context.SetInstanceVariable(result, "@code", ScriptingRuntimeHelpers.Int32ToObject(code));
            context.SetInstanceVariable(result, "@sql", sql);
            context.SetInstanceVariable(result, "@sql_offset", ScriptingRuntimeHelpers.Int32ToObject(sqlOffset));
            return result;
        }

        internal static Exception/*!*/ MakeError(RubyContext/*!*/ context, sqlite3 db, int code) {
            return MakeError(context, code, raw.sqlite3_errmsg(db).utf8_to_string(), null, -1);
        }

        /// <summary>SQLite3::Exception with no result code behind it, as the gem's Ruby half raises.</summary>
        internal static Exception/*!*/ MakePlainError(RubyContext/*!*/ context, string/*!*/ message) {
            SqliteException result = new SqliteException(message);
            context.SetInstanceVariable(result, "@code", null);
            context.SetInstanceVariable(result, "@sql", null);
            context.SetInstanceVariable(result, "@sql_offset", ScriptingRuntimeHelpers.Int32ToObject(-1));
            return result;
        }

        #endregion

        #region Value conversion

        /// <summary>
        /// A column value, as the gem hands it back: Integer, Float, a frozen UTF-8 String
        /// for TEXT, a frozen ASCII-8BIT String for BLOB, nil for NULL.  TEXT is read
        /// through sqlite3_column_blob rather than _text so the bytes reach Ruby exactly as
        /// stored - SQLite does not validate that TEXT is well-formed UTF-8, and going
        /// through a CLR string would replace anything that is not.
        /// </summary>
        internal static object ColumnValue(sqlite3_stmt/*!*/ stmt, int index) {
            switch (raw.sqlite3_column_type(stmt, index)) {
                case raw.SQLITE_INTEGER:
                    return Integer(raw.sqlite3_column_int64(stmt, index));

                case raw.SQLITE_FLOAT:
                    return raw.sqlite3_column_double(stmt, index);

                case raw.SQLITE_TEXT:
                    return MutableString.CreateBinary(raw.sqlite3_column_blob(stmt, index).ToArray(), RubyEncoding.UTF8).Freeze();

                case raw.SQLITE_BLOB:
                    return MutableString.CreateBinary(raw.sqlite3_column_blob(stmt, index).ToArray(), RubyEncoding.Binary).Freeze();

                default:
                    return null;
            }
        }

        /// <summary>
        /// IronRuby's Integer is an Int32 while it fits, an Int64 beyond that and a
        /// BigInteger beyond that; a rowid or an INTEGER column is at most 64 bits, so the
        /// first two cover it.
        /// </summary>
        internal static object Integer(long value) {
            if (value >= Int32.MinValue && value <= Int32.MaxValue) {
                return ScriptingRuntimeHelpers.Int32ToObject((int)value);
            }
            return value;
        }

        #endregion

        #region Database

        [RubyClass("Database")]
        public sealed class Database : RubyObject {
            internal sqlite3 _db;
            internal bool _closed = true;

            /// <summary>
            /// Every statement this connection has prepared and not closed.  #close has to
            /// finalize them: sqlite3_close_v2 would otherwise leave the handle alive as a
            /// zombie until the last statement went, and a later #closed? would answer true
            /// on a connection the file lock is still held by.
            /// </summary>
            internal readonly List<Statement>/*!*/ _statements = new List<Statement>();

            /// <summary>
            /// The block passed to #busy_handler.  SQLitePCL.raw does not surface
            /// sqlite3_busy_handler, so the retry loop lives in Step() instead of inside
            /// SQLite - see the comment there.
            /// </summary>
            internal Proc _busyHandler;

            private readonly List<object>/*!*/ _roots = new List<object>();

            public Database(RubyClass/*!*/ rubyClass) : base(rubyClass) {
            }

            protected override RubyObject/*!*/ CreateInstance() {
                return new Database(ImmediateClass.NominalClass);
            }

            private RubyContext/*!*/ Context {
                get { return ImmediateClass.Context; }
            }

            /// <summary>
            /// Keeps a delegate handed to SQLite alive for as long as the connection is: the
            /// native side holds a bare function pointer, and a collected delegate is a crash.
            /// </summary>
            internal void Root(object value) {
                _roots.Add(value);
            }

            internal sqlite3/*!*/ Handle {
                get {
                    if (_closed) {
                        throw MakePlainError(Context, "cannot use a closed database");
                    }
                    return _db;
                }
            }

            internal Exception/*!*/ Error(int code) {
                return MakeError(Context, _db, code);
            }

            /// <summary>
            /// SQLitePCL.raw does not bind sqlite3_busy_handler, so a #busy_handler block is
            /// consulted here - where SQLite has already given up and answered SQLITE_BUSY -
            /// rather than from inside SQLite's own locking loop.  The block sees the same
            /// sequence of retry counts it would there, and the same power to stop by
            /// answering false; what it does not see is the lock attempt that SQLite's own
            /// #busy_timeout is already retrying underneath.
            /// </summary>
            internal bool ShouldRetry(int code, ref int retries) {
                if ((code & 0xff) != raw.SQLITE_BUSY || _busyHandler == null) {
                    return false;
                }
                object keepTrying = _busyHandler.Call(null, ScriptingRuntimeHelpers.Int32ToObject(retries++));
                return Protocols.IsTrue(keepTrying);
            }

            private void Check(int code) {
                if (code != raw.SQLITE_OK) {
                    throw Error(code);
                }
            }

            #region Opening and closing

            [RubyMethod("open_v2", RubyMethodAttributes.PrivateInstance)]
            public static Database/*!*/ OpenV2(Database/*!*/ self, [NotNull]MutableString/*!*/ file, [DefaultProtocol]int mode, [DefaultProtocol]MutableString vfs) {
                EnsureInitialized();
                sqlite3 db;
                string vfsName = (vfs == null) ? null : vfs.ToString();
                int code = raw.sqlite3_open_v2(file.ToString(), out db, mode, vfsName);
                if (code != raw.SQLITE_OK) {
                    string message = (db == null) ? raw.sqlite3_errstr(code).utf8_to_string() : raw.sqlite3_errmsg(db).utf8_to_string();
                    if (db != null) {
                        raw.sqlite3_close_v2(db);
                    }
                    throw MakeError(self.Context, code, message, null, -1);
                }
                self._db = db;
                self._closed = false;
                return self;
            }

            [RubyMethod("close")]
            public static Database/*!*/ Close(Database/*!*/ self) {
                if (!self._closed) {
                    // Copy: Statement.Close removes itself from the list.
                    foreach (var statement in self._statements.ToArray()) {
                        Statement.Close(statement);
                    }
                    self._statements.Clear();
                    raw.sqlite3_close_v2(self._db);
                    self._db = null;
                    self._closed = true;
                }
                return self;
            }

            [RubyMethod("closed?")]
            public static bool IsClosed(Database/*!*/ self) {
                return self._closed;
            }

            [RubyMethod("db_filename", RubyMethodAttributes.PrivateInstance)]
            public static object FileName(Database/*!*/ self, [NotNull]MutableString/*!*/ name) {
                string str = raw.sqlite3_db_filename(self.Handle, name.ToString()).utf8_to_string();
                // A temporary or in-memory database has an empty name, not a missing one.
                return (str == null) ? null : MutableString.Create(str, RubyEncoding.UTF8);
            }

            [RubyMethod("readonly_internal", RubyMethodAttributes.PrivateInstance)]
            public static bool IsReadOnly(Database/*!*/ self) {
                return raw.sqlite3_db_readonly(self.Handle, "main") == 1;
            }

            #endregion

            #region Counters and status

            [RubyMethod("last_insert_row_id")]
            public static object LastInsertRowId(Database/*!*/ self) {
                return Integer(raw.sqlite3_last_insert_rowid(self.Handle));
            }

            [RubyMethod("changes")]
            public static int Changes(Database/*!*/ self) {
                return raw.sqlite3_changes(self.Handle);
            }

            [RubyMethod("total_changes")]
            public static int TotalChanges(Database/*!*/ self) {
                return raw.sqlite3_total_changes(self.Handle);
            }

            [RubyMethod("errmsg")]
            public static MutableString/*!*/ ErrorMessage(Database/*!*/ self) {
                return MutableString.Create(raw.sqlite3_errmsg(self.Handle).utf8_to_string(), RubyEncoding.UTF8);
            }

            [RubyMethod("errcode")]
            public static int ErrorCode(Database/*!*/ self) {
                return raw.sqlite3_errcode(self.Handle);
            }

            [RubyMethod("complete?")]
            public static bool IsComplete(Database/*!*/ self, [NotNull]MutableString/*!*/ sql) {
                EnsureInitialized();
                return raw.sqlite3_complete(sql.ToString()) != 0;
            }

            [RubyMethod("transaction_active?")]
            public static bool IsTransactionActive(Database/*!*/ self) {
                return raw.sqlite3_get_autocommit(self.Handle) == 0;
            }

            [RubyMethod("interrupt")]
            public static Database/*!*/ Interrupt(Database/*!*/ self) {
                raw.sqlite3_interrupt(self.Handle);
                return self;
            }

            [RubyMethod("extended_result_codes=")]
            public static object SetExtendedResultCodes(Database/*!*/ self, object enable) {
                raw.sqlite3_extended_result_codes(self.Handle, Protocols.IsTrue(enable) ? 1 : 0);
                return enable;
            }

            [RubyMethod("busy_timeout=")]
            public static object SetBusyTimeout(Database/*!*/ self, [DefaultProtocol]int milliseconds) {
                self.Check(raw.sqlite3_busy_timeout(self.Handle, milliseconds));
                return ScriptingRuntimeHelpers.Int32ToObject(milliseconds);
            }

            [RubyMethod("busy_handler")]
            public static Database/*!*/ BusyHandler(BlockParam block, Database/*!*/ self, [Optional]object handler) {
                self._busyHandler = (block != null) ? block.Proc : handler as Proc;
                return self;
            }

            #endregion

            #region Batch execution

            /// <summary>
            /// The gem's private exec_batch, which is sqlite3_exec: every value comes back as
            /// a String because that is what the sqlite3_exec callback is handed, NULL alone
            /// is nil.  Implemented by stepping the statements rather than by calling
            /// sqlite3_exec, since raw's sqlite3_exec callback does not distinguish NULL from
            /// an empty string.
            /// </summary>
            [RubyMethod("exec_batch", RubyMethodAttributes.PrivateInstance)]
            public static object ExecBatch(Database/*!*/ self, [NotNull]MutableString/*!*/ sql, object resultsAsHash) {
                var context = self.Context;
                bool asHash = Protocols.IsTrue(resultsAsHash);
                sqlite3 db = self.Handle;
                var rows = new RubyArray();

                byte[] utf8 = Encoding.UTF8.GetBytes(sql.ToString());
                var remaining = new ReadOnlyMemory<byte>(utf8);

                while (true) {
                    // A trailing run of whitespace or a comment prepares to a null statement.
                    sqlite3_stmt stmt;
                    ReadOnlySpan<byte> tail;
                    int code = raw.sqlite3_prepare_v2(db, remaining.Span, out stmt, out tail);
                    if (code != raw.SQLITE_OK) {
                        throw MakeError(context, code, raw.sqlite3_errmsg(db).utf8_to_string(), sql, ErrorOffset(db));
                    }
                    int consumed = remaining.Length - tail.Length;
                    remaining = remaining.Slice(consumed);

                    if (stmt == null) {
                        break;
                    }
                    try {
                        int columns = raw.sqlite3_column_count(stmt);
                        while (true) {
                            code = raw.sqlite3_step(stmt);
                            if (code == raw.SQLITE_ROW) {
                                object row = StringRow(context, stmt, columns, asHash);
                                rows.Add(row);
                                continue;
                            }
                            if (code == raw.SQLITE_DONE) {
                                break;
                            }
                            throw MakeError(context, code, raw.sqlite3_errmsg(db).utf8_to_string(), sql, -1);
                        }
                    } finally {
                        raw.sqlite3_finalize(stmt);
                    }
                    if (remaining.Length == 0) {
                        break;
                    }
                }
                return rows;
            }

            private static object StringRow(RubyContext/*!*/ context, sqlite3_stmt/*!*/ stmt, int columns, bool asHash) {
                if (asHash) {
                    var hash = new Hash(context);
                    for (int i = 0; i < columns; i++) {
                        hash[MutableString.Create(raw.sqlite3_column_name(stmt, i).utf8_to_string(), RubyEncoding.UTF8).Freeze()] = TextValue(stmt, i);
                    }
                    return hash;
                }
                var row = new RubyArray(columns);
                for (int i = 0; i < columns; i++) {
                    row.Add(TextValue(stmt, i));
                }
                return row;
            }

            private static object TextValue(sqlite3_stmt/*!*/ stmt, int index) {
                if (raw.sqlite3_column_type(stmt, index) == raw.SQLITE_NULL) {
                    return null;
                }
                return MutableString.CreateBinary(raw.sqlite3_column_blob(stmt, index).ToArray(), RubyEncoding.UTF8).Freeze();
            }

            #endregion

            #region Functions and collations

            [RubyMethod("define_function_with_flags")]
            public static Database/*!*/ DefineFunctionWithFlags(BlockParam/*!*/ block, Database/*!*/ self,
                [DefaultProtocol, NotNull]MutableString/*!*/ name, [DefaultProtocol]int flags) {

                if (block == null) {
                    throw RubyExceptions.NoBlockGiven();
                }
                return DefineFunction(self, name, flags, ProcArity(block.Proc), block.Proc);
            }

            [RubyMethod("define_function")]
            public static Database/*!*/ DefineFunction(BlockParam/*!*/ block, Database/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ name) {
                if (block == null) {
                    throw RubyExceptions.NoBlockGiven();
                }
                return DefineFunction(self, name, raw.SQLITE_UTF8, ProcArity(block.Proc), block.Proc);
            }

            private static int ProcArity(Proc/*!*/ block) {
                int arity = block.Dispatcher.Arity;
                return arity < 0 ? -1 : arity;
            }

            private static Database/*!*/ DefineFunction(Database/*!*/ self, MutableString/*!*/ name, int flags, int arity, Proc/*!*/ block) {
                string functionName = name.ToString();

                delegate_function_scalar callback = (ctx, userData, values) => {
                    try {
                        var args = new object[values.Length];
                        for (int i = 0; i < values.Length; i++) {
                            args[i] = FunctionArgument(values[i]);
                        }
                        SetResult(ctx, block.Call(null, args));
                    } catch (Exception e) {
                        // The message is all that survives the native frame; the Ruby
                        // exception itself cannot be propagated through SQLite.
                        raw.sqlite3_result_error(ctx, e.Message ?? "error in user-defined function");
                    }
                };
                self.Root(callback);
                self.Check(raw.sqlite3_create_function(self.Handle, functionName, arity, flags, null, callback));
                return self;
            }

            private static object FunctionArgument(sqlite3_value/*!*/ value) {
                switch (raw.sqlite3_value_type(value)) {
                    case raw.SQLITE_INTEGER: return Integer(raw.sqlite3_value_int64(value));
                    case raw.SQLITE_FLOAT: return raw.sqlite3_value_double(value);
                    case raw.SQLITE_TEXT: return MutableString.CreateBinary(raw.sqlite3_value_blob(value).ToArray(), RubyEncoding.UTF8);
                    case raw.SQLITE_BLOB: return MutableString.CreateBinary(raw.sqlite3_value_blob(value).ToArray(), RubyEncoding.Binary);
                    default: return null;
                }
            }

            private static void SetResult(sqlite3_context/*!*/ ctx, object value) {
                if (value == null) {
                    raw.sqlite3_result_null(ctx);
                    return;
                }
                if (value is int) {
                    raw.sqlite3_result_int64(ctx, (int)value);
                    return;
                }
                if (value is long) {
                    raw.sqlite3_result_int64(ctx, (long)value);
                    return;
                }
                if (value is BigInteger) {
                    raw.sqlite3_result_int64(ctx, (long)(BigInteger)value);
                    return;
                }
                if (value is double) {
                    raw.sqlite3_result_double(ctx, (double)value);
                    return;
                }
                if (value is bool) {
                    raw.sqlite3_result_int64(ctx, (bool)value ? 1 : 0);
                    return;
                }
                var str = value as MutableString;
                if (str != null) {
                    if (str.Encoding == RubyEncoding.Binary) {
                        raw.sqlite3_result_blob(ctx, str.ToByteArray());
                    } else {
                        raw.sqlite3_result_text(ctx, str.ToByteArray());
                    }
                    return;
                }
                raw.sqlite3_result_error(ctx, "unsupported return value from a user-defined function");
            }

            /// <summary>
            /// The primitive SQLite3::Database#define_aggregator2 is written on (see
            /// sqlite3/database.rb).  The three procs keep every dynamic dispatch on the Ruby
            /// side: +init+ makes the per-aggregation accumulator, +step+ is called with it
            /// and the row's arguments, +final+ turns it into the function's result.
            /// SQLitePCLRaw parks the accumulator in sqlite3_context.state, which it maps onto
            /// sqlite3_aggregate_context for us.
            /// </summary>
            [RubyMethod("define_aggregate_function", RubyMethodAttributes.PrivateInstance)]
            public static Database/*!*/ DefineAggregateFunction(Database/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ name,
                [DefaultProtocol]int arity, [NotNull]Proc/*!*/ init, [NotNull]Proc/*!*/ step, [NotNull]Proc/*!*/ final) {

                string functionName = name.ToString();

                delegate_function_aggregate_step stepCallback = (ctx, userData, values) => {
                    try {
                        if (ctx.state == null) {
                            ctx.state = init.Call(null);
                        }
                        var args = new object[values.Length + 1];
                        args[0] = ctx.state;
                        for (int i = 0; i < values.Length; i++) {
                            args[i + 1] = FunctionArgument(values[i]);
                        }
                        step.Call(null, args);
                    } catch (Exception e) {
                        raw.sqlite3_result_error(ctx, e.Message ?? "error in user-defined aggregate");
                    }
                };

                delegate_function_aggregate_final finalCallback = (ctx, userData) => {
                    try {
                        // An aggregate over no rows never stepped, so the accumulator has to be
                        // made here - count(*) over an empty table still has to answer 0.
                        object state = ctx.state ?? init.Call(null);
                        SetResult(ctx, final.Call(null, state));
                    } catch (Exception e) {
                        raw.sqlite3_result_error(ctx, e.Message ?? "error in user-defined aggregate");
                    }
                };

                self.Root(stepCallback);
                self.Root(finalCallback);
                self.Check(raw.sqlite3_create_function(self.Handle, functionName, arity, null, stepCallback, finalCallback));
                return self;
            }

            [RubyMethod("collation")]
            public static Database/*!*/ Collation(RubyContext/*!*/ context, Database/*!*/ self, [DefaultProtocol, NotNull]MutableString/*!*/ name, object comparator) {
                string collationName = name.ToString();
                if (comparator == null) {
                    self.Check(raw.sqlite3_create_collation(self.Handle, collationName, null, null));
                    return self;
                }

                var site = CallSite<Func<CallSite, object, object, object, object>>.Create(
                    RubyCallAction.Make(context, "compare", RubyCallSignature.WithImplicitSelf(2))
                );

                strdelegate_collation callback = (userData, left, right) => {
                    object result = site.Target(site, comparator,
                        MutableString.Create(left, RubyEncoding.UTF8),
                        MutableString.Create(right, RubyEncoding.UTF8));
                    return (result is int) ? (int)result : 0;
                };
                self.Root(callback);
                self.Check(raw.sqlite3_create_collation(self.Handle, collationName, null, callback));
                return self;
            }

            #endregion

            #region Extensions and quirks

            [RubyMethod("enable_load_extension")]
            public static Database/*!*/ EnableLoadExtension(Database/*!*/ self, object enable) {
                self.Check(raw.sqlite3_enable_load_extension(self.Handle, Protocols.IsTrue(enable) ? 1 : 0));
                return self;
            }

            [RubyMethod("load_extension_internal", RubyMethodAttributes.PrivateInstance)]
            public static Database/*!*/ LoadExtensionInternal(Database/*!*/ self, [NotNull]MutableString/*!*/ path) {
                utf8z errmsg;
                int code = raw.sqlite3_load_extension(self.Handle, utf8z.FromString(path.ToString()), utf8z.FromString(null), out errmsg);
                if (code != raw.SQLITE_OK) {
                    throw MakeError(self.Context, code, errmsg.utf8_to_string() ?? "could not load extension", null, -1);
                }
                return self;
            }

            /// <summary>
            /// The gem's :strict option.  SQLITE_DBCONFIG_DQS_DML/DDL are 1013 and 1014; they
            /// turn off SQLite's "a double-quoted identifier that resolves to nothing is a
            /// string literal" misfeature.
            /// </summary>
            [RubyMethod("disable_quirk_mode", RubyMethodAttributes.PrivateInstance)]
            public static bool DisableQuirkMode(Database/*!*/ self) {
                int result;
                sqlite3 db = self.Handle;
                bool ok = raw.sqlite3_db_config(db, 1013, 0, out result) == raw.SQLITE_OK;
                ok &= raw.sqlite3_db_config(db, 1014, 0, out result) == raw.SQLITE_OK;
                return ok;
            }

            #endregion
        }

        #endregion

        #region Statement

        [RubyClass("Statement")]
        public sealed class Statement : RubyObject {
            private Database _database;
            private sqlite3_stmt _stmt;
            private bool _done;

            public Statement(RubyClass/*!*/ rubyClass) : base(rubyClass) {
            }

            protected override RubyObject/*!*/ CreateInstance() {
                return new Statement(ImmediateClass.NominalClass);
            }

            private RubyContext/*!*/ Context {
                get { return ImmediateClass.Context; }
            }

            private sqlite3_stmt/*!*/ Handle {
                get {
                    if (_stmt == null) {
                        throw MakePlainError(Context, "cannot use a closed statement");
                    }
                    return _stmt;
                }
            }

            private Exception/*!*/ Error(int code) {
                return _database.Error(code);
            }

            [RubyMethod("prepare", RubyMethodAttributes.PrivateInstance)]
            public static MutableString/*!*/ Prepare(Statement/*!*/ self, [NotNull]Database/*!*/ db, [NotNull]MutableString/*!*/ sql) {
                EnsureInitialized();
                sqlite3 handle = db.Handle;

                byte[] utf8 = sql.ToByteArray();
                sqlite3_stmt stmt;
                ReadOnlySpan<byte> tail;
                int code;
                int retries = 0;
                // sqlite3_prepare_v2 reads the schema, so it can report SQLITE_BUSY too.
                while ((code = raw.sqlite3_prepare_v2(handle, utf8, out stmt, out tail)) != raw.SQLITE_OK) {
                    if (!db.ShouldRetry(code, ref retries)) {
                        throw MakeError(self.Context, code, raw.sqlite3_errmsg(handle).utf8_to_string(), sql, ErrorOffset(handle));
                    }
                }
                byte[] rest = tail.ToArray();

                self._database = db;
                self._stmt = stmt;
                self._done = false;
                db._statements.Add(self);

                return MutableString.CreateBinary(rest, sql.Encoding);
            }

            [RubyMethod("close")]
            public static object Close(Statement/*!*/ self) {
                if (self._stmt != null) {
                    raw.sqlite3_finalize(self._stmt);
                    self._stmt = null;
                    self._database._statements.Remove(self);
                }
                return null;
            }

            [RubyMethod("closed?")]
            public static bool IsClosed(Statement/*!*/ self) {
                return self._stmt == null;
            }

            [RubyMethod("done?")]
            public static bool IsDone(Statement/*!*/ self) {
                return self._done;
            }

            [RubyMethod("reset!")]
            public static Statement/*!*/ Reset(Statement/*!*/ self) {
                raw.sqlite3_reset(self.Handle);
                self._done = false;
                return self;
            }

            [RubyMethod("clear_bindings!")]
            public static Statement/*!*/ ClearBindings(Statement/*!*/ self) {
                raw.sqlite3_clear_bindings(self.Handle);
                self._done = false;
                return self;
            }

            [RubyMethod("column_count")]
            public static int ColumnCount(Statement/*!*/ self) {
                return raw.sqlite3_column_count(self.Handle);
            }

            [RubyMethod("column_name")]
            public static object ColumnName(Statement/*!*/ self, [DefaultProtocol]int index) {
                sqlite3_stmt stmt = self.Handle;
                if (index < 0 || index >= raw.sqlite3_column_count(stmt)) {
                    return null;
                }
                return MutableString.Create(raw.sqlite3_column_name(stmt, index).utf8_to_string(), RubyEncoding.UTF8);
            }

            [RubyMethod("column_decltype")]
            public static object ColumnDeclaredType(Statement/*!*/ self, [DefaultProtocol]int index) {
                sqlite3_stmt stmt = self.Handle;
                if (index < 0 || index >= raw.sqlite3_column_count(stmt)) {
                    return null;
                }
                string type = raw.sqlite3_column_decltype(stmt, index).utf8_to_string();
                return type == null ? null : MutableString.Create(type, RubyEncoding.UTF8);
            }

            [RubyMethod("database_name")]
            public static object DatabaseName(Statement/*!*/ self, [DefaultProtocol]int index) {
                string name = raw.sqlite3_column_database_name(self.Handle, index).utf8_to_string();
                return name == null ? null : MutableString.Create(name, RubyEncoding.UTF8);
            }

            [RubyMethod("sql")]
            public static object Sql(Statement/*!*/ self) {
                string sql = raw.sqlite3_sql(self.Handle).utf8_to_string();
                return sql == null ? null : MutableString.Create(sql, RubyEncoding.UTF8).Freeze();
            }

            [RubyMethod("bind_parameter_count")]
            public static int BindParameterCount(Statement/*!*/ self) {
                return raw.sqlite3_bind_parameter_count(self.Handle);
            }

            [RubyMethod("named_params")]
            public static RubyArray/*!*/ NamedParams(Statement/*!*/ self) {
                sqlite3_stmt stmt = self.Handle;
                int count = raw.sqlite3_bind_parameter_count(stmt);
                var result = new RubyArray(count);
                for (int i = 1; i <= count; i++) {
                    string name = raw.sqlite3_bind_parameter_name(stmt, i).utf8_to_string();
                    if (name == null) {
                        result.Add(null);
                    } else {
                        // The leading ':', '@' or '$' is not part of the name the gem reports.
                        result.Add(MutableString.Create(name.Substring(1), RubyEncoding.UTF8).Freeze());
                    }
                }
                return result.Freeze();
            }

            #region Binding

            [RubyMethod("bind_param")]
            public static Statement/*!*/ BindParam(RubyContext/*!*/ context, Statement/*!*/ self, object key, object value) {
                sqlite3_stmt stmt = self.Handle;
                int index;

                if (key is int) {
                    index = (int)key;
                } else {
                    MutableString name = key as MutableString;
                    if (name == null) {
                        var symbol = key as RubySymbol;
                        if (symbol != null) {
                            name = symbol.String;
                        }
                    }
                    if (name == null) {
                        throw RubyExceptions.CreateTypeError(String.Format(
                            "can't convert {0} into Integer", context.GetClassDisplayName(key)));
                    } else {
                        string str = name.ToString();
                        if (str.Length == 0 || (str[0] != ':' && str[0] != '@' && str[0] != '$')) {
                            str = ":" + str;
                        }
                        index = raw.sqlite3_bind_parameter_index(stmt, str);
                    }
                }

                if (index == 0) {
                    throw MakePlainError(context, "no such bind parameter");
                }

                int code = Bind(context, stmt, index, value);
                if (code != raw.SQLITE_OK) {
                    throw self.Error(code);
                }
                return self;
            }

            private static int Bind(RubyContext/*!*/ context, sqlite3_stmt/*!*/ stmt, int index, object value) {
                if (value == null) {
                    return raw.sqlite3_bind_null(stmt, index);
                }
                if (value is int) {
                    return raw.sqlite3_bind_int64(stmt, index, (int)value);
                }
                if (value is long) {
                    return raw.sqlite3_bind_int64(stmt, index, (long)value);
                }
                if (value is BigInteger) {
                    var big = (BigInteger)value;
                    if (big >= Int64.MinValue && big <= Int64.MaxValue) {
                        return raw.sqlite3_bind_int64(stmt, index, (long)big);
                    }
                    // Out of an int64's range the gem falls back on a double, losing precision
                    // rather than refusing the value.
                    return raw.sqlite3_bind_double(stmt, index, (double)big);
                }
                if (value is double) {
                    return raw.sqlite3_bind_double(stmt, index, (double)value);
                }
                if (value is bool) {
                    return raw.sqlite3_bind_int64(stmt, index, (bool)value ? 1 : 0);
                }

                var str = value as MutableString;
                if (str == null) {
                    throw MakePlainError(context, String.Format("can't prepare {0}",
                        context.GetClassDisplayName(value)));
                }
                if (str.Encoding == RubyEncoding.Binary || IsBlob(context, value)) {
                    return raw.sqlite3_bind_blob(stmt, index, str.ToByteArray());
                }
                return raw.sqlite3_bind_text(stmt, index, str.ToByteArray());
            }

            /// <summary>An instance of SQLite3::Blob is bound as a BLOB whatever its encoding.</summary>
            private static bool IsBlob(RubyContext/*!*/ context, object value) {
                object sqlite3Module, blobClass;
                if (!context.ObjectClass.TryGetConstant(null, "SQLite3", out sqlite3Module)) {
                    return false;
                }
                var module = sqlite3Module as RubyModule;
                if (module == null || !module.TryGetConstant(null, "Blob", out blobClass)) {
                    return false;
                }
                var cls = blobClass as RubyClass;
                return cls != null && context.IsKindOf(value, cls);
            }

            #endregion

            #region Statistics

            // sqlite3_stmt_status op codes, in the order the gem names them.
            private static readonly string[]/*!*/ _statNames = {
                "fullscan_steps", "sorts", "autoindexes", "vm_steps",
                "reprepares", "runs", "filter_misses", "filter_hits"
            };

            private static int StatOp(RubyContext/*!*/ context, object key) {
                var symbol = key as RubySymbol;
                var str = key as MutableString;
                string name = (symbol != null) ? symbol.ToString() : (str != null) ? str.ToString() : null;
                if (name == null) {
                    throw RubyExceptions.CreateTypeError("key must be a Symbol or a String");
                }
                for (int i = 0; i < _statNames.Length; i++) {
                    if (_statNames[i] == name) {
                        // SQLITE_STMTSTATUS_FULLSCAN_STEP is 1 and the rest follow in order,
                        // except that 8 (MEMUSED) is not one of these.
                        return i + 1;
                    }
                }
                throw RubyExceptions.CreateArgumentError(String.Format("unknown key: {0}", name));
            }

            [RubyMethod("stat_for", RubyMethodAttributes.PrivateInstance)]
            public static int StatFor(RubyContext/*!*/ context, Statement/*!*/ self, object key) {
                return raw.sqlite3_stmt_status(self.Handle, StatOp(context, key), 0);
            }

            [RubyMethod("stats_as_hash", RubyMethodAttributes.PrivateInstance)]
            public static Hash/*!*/ StatsAsHash(RubyContext/*!*/ context, Statement/*!*/ self) {
                sqlite3_stmt stmt = self.Handle;
                var result = new Hash(context);
                for (int i = 0; i < _statNames.Length; i++) {
                    result[context.CreateAsciiSymbol(_statNames[i])] =
                        ScriptingRuntimeHelpers.Int32ToObject(raw.sqlite3_stmt_status(stmt, i + 1, 0));
                }
                return result;
            }

            [RubyMethod("memused")]
            public static int MemUsed(Statement/*!*/ self) {
                return raw.sqlite3_stmt_status(self.Handle, 99, 0);
            }

            #endregion

            #region Stepping

            [RubyMethod("step")]
            public static object Step(Statement/*!*/ self) {
                if (self._done) {
                    return null;
                }
                sqlite3_stmt stmt = self.Handle;
                var context = self.Context;

                int retries = 0;
                while (true) {
                    int code = raw.sqlite3_step(stmt);

                    if (code == raw.SQLITE_ROW) {
                        int columns = raw.sqlite3_column_count(stmt);
                        var row = new RubyArray(columns);
                        for (int i = 0; i < columns; i++) {
                            row.Add(ColumnValue(stmt, i));
                        }
                        return row.Freeze();
                    }

                    if (code == raw.SQLITE_DONE) {
                        self._done = true;
                        return null;
                    }

                    // A #busy_handler block gets to say whether to try again; see
                    // Database.ShouldRetry.
                    if (self._database.ShouldRetry(code, ref retries)) {
                        continue;
                    }

                    raw.sqlite3_reset(stmt);
                    throw MakeError(context, self._database._db, code);
                }
            }

            #endregion
        }

        #endregion

        #region Backup

        /// <summary>
        /// backup.c: SQLite's online backup API, one page batch per #step.  Rails apps copy an
        /// on-disk database into an in-memory one this way (lobsters' ruby-bench benchmark does
        /// it before the first request).  The Ruby objects keep both connections reachable while
        /// a backup is open, which is what SQLite needs: the handles must outlive the backup.
        /// </summary>
        [RubyClass("Backup")]
        public sealed class Backup : RubyObject {
            private sqlite3_backup _backup;
            private Database _destination, _source;

            public Backup(RubyClass/*!*/ rubyClass) : base(rubyClass) {
            }

            protected override RubyObject/*!*/ CreateInstance() {
                return new Backup(ImmediateClass.NominalClass);
            }

            private sqlite3_backup/*!*/ Handle {
                get {
                    if (_backup == null) {
                        throw MakePlainError(ImmediateClass.Context, "cannot use a closed backup");
                    }
                    return _backup;
                }
            }

            [RubyConstructor]
            public static Backup/*!*/ Create(RubyClass/*!*/ self, [NotNull]Database/*!*/ destination,
                [DefaultProtocol, NotNull]MutableString/*!*/ destinationName,
                [NotNull]Database/*!*/ source, [DefaultProtocol, NotNull]MutableString/*!*/ sourceName) {
                return Initialize(new Backup(self), destination, destinationName, source, sourceName);
            }

            [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
            public static Backup/*!*/ Initialize(Backup/*!*/ self, [NotNull]Database/*!*/ destination,
                [DefaultProtocol, NotNull]MutableString/*!*/ destinationName,
                [NotNull]Database/*!*/ source, [DefaultProtocol, NotNull]MutableString/*!*/ sourceName) {
                EnsureInitialized();
                if (source._closed) {
                    throw RubyExceptions.CreateArgumentError("cannot backup from a closed database");
                }
                if (destination._closed) {
                    throw RubyExceptions.CreateArgumentError("cannot backup to a closed database");
                }

                sqlite3_backup backup = raw.sqlite3_backup_init(destination._db, destinationName.ToString(), source._db, sourceName.ToString());
                // SQLitePCL wraps a NULL sqlite3_backup* in an invalid handle rather than null.
                if (backup == null || backup.IsInvalid) {
                    int code = raw.sqlite3_errcode(destination._db);
                    if (code != raw.SQLITE_OK) {
                        throw destination.Error(code);
                    }
                    return self;
                }
                self._backup = backup;
                self._destination = destination;
                self._source = source;
                return self;
            }

            /// <summary>The raw result code: SQLITE_OK while pages remain, SQLITE_DONE at the end.</summary>
            [RubyMethod("step")]
            public static int Step(Backup/*!*/ self, [DefaultProtocol]int pages) {
                return raw.sqlite3_backup_step(self.Handle, pages);
            }

            [RubyMethod("finish")]
            public static object Finish(Backup/*!*/ self) {
                raw.sqlite3_backup_finish(self.Handle);
                self._backup = null;
                self._destination = self._source = null;
                return null;
            }

            [RubyMethod("remaining")]
            public static int Remaining(Backup/*!*/ self) {
                return raw.sqlite3_backup_remaining(self.Handle);
            }

            [RubyMethod("pagecount")]
            public static int PageCount(Backup/*!*/ self) {
                return raw.sqlite3_backup_pagecount(self.Handle);
            }
        }

        #endregion
    }
}
