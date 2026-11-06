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

#if FEATURE_CORE_DLR
using System.Linq.Expressions;
#else
using Microsoft.Scripting.Ast;
#endif

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Reflection;
using IronRuby.Compiler.Generation;
using IronRuby.Runtime;
using IronRuby.Runtime.Calls;
using IronRuby.Runtime.Conversions;
using System.Numerics;
using Microsoft.Scripting.Runtime;
using Microsoft.Scripting.Utils;

namespace IronRuby.Builtins {
    using Ast = Expression;
    using Utils = IronRuby.Runtime.Utils;
    using TranscodeStorage = CallSiteStorage<Func<CallSite, object, object, object>>;

    /// <summary>
    /// Implementation of IO builtin class. 
    /// </summary>
    [RubyClass("IO", Extends = typeof(RubyIO)), Includes(typeof(RubyFileOps.Constants), typeof(Enumerable))]
    [Includes(typeof(PrintOps), Copy = true)]
    public class RubyIOOps {
        internal static Stream/*!*/ GetDescriptorStream(RubyContext/*!*/ context, int descriptor) {
            Stream stream = context.GetStream(descriptor);
            if (stream == null) {
                // A descriptor this process was handed rather than opened - what a child of
                // Process.spawn gets through a redirection - is not in IronRuby's table, so
                // IO.new(fd) used to answer EBADF for exactly the descriptors a program has
                // been told to use.
                stream = RubyIO.TryAdoptDescriptor(descriptor);
                if (stream != null) {
                    context.SetOrAllocateDescriptor(descriptor, stream);
                }
            }
            if (stream == null) {
                throw RubyExceptions.CreateEBADF();
            }
            return stream;
        }

        #region Constants

        [RubyConstant]
        public const int SEEK_SET = RubyIO.SEEK_SET;

        [RubyConstant]
        public const int SEEK_CUR = RubyIO.SEEK_CUR;

        [RubyConstant]
        public const int SEEK_END = RubyIO.SEEK_END;

        [RubyModule("WaitReadable")]
        public static class WaitReadable {
        }

        [RubyModule("WaitWritable")]
        public static class WaitWritable {
        }

        /// <summary>
        /// The EAGAIN a non-blocking operation raises when it would have had to wait. CRuby names
        /// a class for it - IO::EAGAINWaitReadable and its three siblings - so that a rescue can
        /// tell "nothing to read yet" from a real error carrying the same errno; the
        /// WaitReadable/WaitWritable modules they include are what generic code rescues. The
        /// classes are defined in Ruby, so they are asked for by name and built by calling #new.
        /// </summary>
        public static Exception/*!*/ NonBlockingError(RubyContext/*!*/ context, bool isRead, string/*!*/ operation) {
            object ioClass;
            if (context.ObjectClass.TryGetConstant(null, "IO", out ioClass)) {
                var io = ioClass as RubyModule;
                if (io != null) {
                    var error = context.CreateLibraryException(
                        io, isRead ? "EAGAINWaitReadable" : "EAGAINWaitWritable", operation
                    );
                    if (error != null) {
                        return error;
                    }
                }
            }

            // Nothing but the runtime loaded: the modules are all there is to say it with.
            var fallback = new Errno.ResourceTemporarilyUnavailableError();
            RubyModule waitModule;
            if (context.TryGetModule(isRead ? typeof(WaitReadable) : typeof(WaitWritable), out waitModule)) {
                ModuleOps.ExtendObject(waitModule, fallback);
            }
            return fallback;
        }

        #endregion

        #region Ruby Constructors

        [RubyConstructor]
        public static RubyIO/*!*/ CreateFile(
            ConversionStorage<int?>/*!*/ toInt,
            ConversionStorage<IDictionary<object, object>>/*!*/ toHash,
            ConversionStorage<MutableString>/*!*/ toStr,
            RubyClass/*!*/ self,
            object descriptor,
            [Optional]object optionsOrMode,
            [Optional]object options) {

            return Reinitialize(toInt, toHash, toStr, new RubyIO(self.Context), descriptor, optionsOrMode, options);
        }

        [RubyMethod("initialize", RubyMethodAttributes.PrivateInstance)]
        public static RubyIO/*!*/ Reinitialize(
            ConversionStorage<int?>/*!*/ toInt,
            ConversionStorage<IDictionary<object, object>>/*!*/ toHash,
            ConversionStorage<MutableString>/*!*/ toStr,
            RubyIO/*!*/ self,
            object descriptor,
            [Optional]object optionsOrMode,
            [Optional]object optionsArgument) {

            var context = self.Context;

            // The options are a Hash or nothing at all. MRI takes an explicit nil as a third
            // argument that is not a Hash - "wrong number of arguments" - rather than as no
            // options, which is what a defaulted parameter would make of it.
            IDictionary<object, object> options = null;
            if (optionsArgument != Missing.Value) {
                var toHashSite = toHash.GetSite(TryConvertToHashAction.Make(context));
                options = (optionsArgument != null) ? toHashSite.Target(toHashSite, optionsArgument) : null;
                if (options == null) {
                    throw RubyExceptions.CreateArgumentError("wrong number of arguments (given 3, expected 1..2)");
                }
            }

            object _ = Missing.Value;
            Protocols.TryConvertToOptions(toHash, ref options, ref optionsOrMode, ref _);
            var toIntSite = toInt.GetSite(TryConvertToFixnumAction.Make(toInt.Context));

            IOInfo info = new IOInfo();
            // A nil mode argument is MRI's "no mode here", which leaves the :mode option free to
            // supply one.
            if (optionsOrMode != Missing.Value && optionsOrMode != null) {
                int? m = toIntSite.Target(toIntSite, optionsOrMode);
                info = m.HasValue ? new IOInfo(IOModeNative.ToIOMode(m.Value)) : IOInfo.Parse(context, Protocols.CastToString(toStr, optionsOrMode));
            }

            if (options != null) {
                info = info.AddOptions(toStr, options);
            }

            int? desc = toIntSite.Target(toIntSite, descriptor);
            if (!desc.HasValue) {
                throw RubyExceptions.CreateImplicitConversionError(context.GetClassDisplayName(descriptor), "Integer");
            }
            Reinitialize(self, desc.Value, info);
            self.ConversionOptions = AddTextModeDecorator(context, info, options);

            // #autoclose? is kept in an instance variable by the prelude, which is where the
            // option has to land for IO.new(fd, autoclose: false) to be visible.
            object autoclose;
            if (options != null && options.TryGetValue(context.CreateAsciiSymbol("autoclose"), out autoclose)) {
                context.SetInstanceVariable(self, "@__autoclose", Protocols.IsTrue(autoclose));
            }

            // IO.new(fd, path: "...") is how a descriptor is told the name it came from; #path
            // and #inspect answer it, and nothing else in the stream knows it.
            object path;
            if (options != null && options.TryGetValue(context.CreateAsciiSymbol("path"), out path) && path != null) {
                context.SetInstanceVariable(self, "@__io_path__", Protocols.CastToPath(toStr, path));
            }

            return self;
        }

        internal static RubyIO/*!*/ Reinitialize(RubyIO/*!*/ io, int descriptor, IOInfo info) {
            IOMode mode = info.Mode;

            // A descriptor this process was handed rather than opened carries its own access
            // mode, and that is the one to believe: Ruby's default of "r" is an answer about a
            // path, and IO.new(fd) was not given one.
            IOMode adopted;
            bool known = io.Context.GetStream(descriptor) != null;
            if (!known && RubyIO.TryGetDescriptorMode(descriptor, out adopted)) {
                mode = (mode & ~IOMode.ReadWriteMask) | adopted;
            }

            Stream stream = GetDescriptorStream(io.Context, descriptor);

            // A mode the caller spelled out has to agree with what the descriptor was opened
            // for; MRI answers EINVAL when it does not. Only for a descriptor IronRuby opened -
            // an adopted one has just told us its mode and cannot disagree with itself.
            if (info.HasMode && known) {
                if ((mode.CanRead() && !stream.CanRead) || (mode.CanWrite() && !stream.CanWrite)) {
                    throw RubyExceptions.CreateEINVAL();
                }
            }

            io.Mode = mode;
            io.SetStream(stream);
            io.SetFileDescriptor(descriptor);

            if (info.HasEncoding) {
                io.SetEncodings(info.ExternalEncoding, info.InternalEncoding);
            } else if ((mode & IOMode.PreserveEndOfLines) != 0) {
                // Binary mode with nothing said about encoding reads and writes bytes, which MRI
                // reports as an external encoding of ASCII-8BIT.
                io.SetEncodings(RubyEncoding.Binary, null);
            } else {
                // See RubyFileOps.Open: the defaults are resolved when the stream is opened.
                io.SetEncodings(null, null);
            }

            return io;
        }

        [RubyMethod("initialize_copy", RubyMethodAttributes.PrivateInstance)]
        public static RubyIO/*!*/ InitializeCopy(RubyIO/*!*/ self, [NotNull]RubyIO/*!*/ source) {
            Stream stream = source.GetStream();
            int descriptor;

            // #dup is dup(2): the copy gets a descriptor of its own, so that reopening the
            // original and then reopening it back from the copy works - which is the whole
            // point of saving a stream before redirecting it. Sharing the table entry meant
            // the copy and the original were the same descriptor, so the save was a no-op.
            int duplicated = RubyIO.TryDuplicateDescriptor(source);
            if (duplicated >= 0) {
                // dup(2) hands back a descriptor without FD_CLOEXEC whatever the original had;
                // MRI's #dup sets it, so a child never inherits a copy nobody meant to give it.
                RubyProcess.SetCloseOnExec(duplicated);
                stream = new FileStream(
                    new Microsoft.Win32.SafeHandles.SafeFileHandle((IntPtr)duplicated, true),
                    source.Mode.CanWrite() ? (source.Mode.CanRead() ? FileAccess.ReadWrite : FileAccess.Write) : FileAccess.Read,
                    1, false
                );
                descriptor = self.Context.AllocateFileDescriptor(stream);
            } else {
                descriptor = self.Context.DuplicateFileDescriptor(source.GetFileDescriptor());
            }

            self.SetStream(stream);
            self.SetFileDescriptor(descriptor);
            self.Mode = source.Mode;
            self.CopyEncodingsFrom(source);
            self.ConversionOptions = source.ConversionOptions;

            // The copy owns the descriptor dup(2) just made it, so it closes it and does not pass
            // it to a child, whatever the original had been set to. Kernel#dup copied those two
            // settings across with the rest of the instance variables.
            self.Context.SetInstanceVariable(self, "@__autoclose", true);
            self.Context.SetInstanceVariable(self, "@__close_on_exec__", true);
            return self;
        }

        [RubyMethod("for_fd", RubyMethodAttributes.PublicSingleton)]
        public static RuleGenerator/*!*/ ForFileDescriptor() {
            return new RuleGenerator(RuleGenerators.InstanceConstructor);
        }

        #endregion

        #region reopen, sysopen

        /// <summary>
        /// MRI's rb_io_check_io for #reopen's argument: anything that is not a path is asked for
        /// #to_io, and what comes back has to be an IO. Answers null when the argument names a
        /// file instead.
        /// </summary>
        private static RubyIO TryToIO(RespondToStorage/*!*/ respondToStorage,
            CallSiteStorage<Func<CallSite, object, object>>/*!*/ toIoStorage, RubyContext/*!*/ context, object obj) {
            return TryToIO(respondToStorage, toIoStorage, context, obj, true);
        }

        /// <summary>
        /// With strict false, an object whose #to_io does not give a real IO - Zlib::GzipReader
        /// wrapping a StringIO or a Gem::Package::TarReader::Entry, say - is simply "not an IO"
        /// rather than a TypeError. That is what MRI's copy_stream does: it falls back to the
        /// duck-typed #readpartial / #read path instead of refusing the object.
        /// </summary>
        private static RubyIO TryToIO(RespondToStorage/*!*/ respondToStorage,
            CallSiteStorage<Func<CallSite, object, object>>/*!*/ toIoStorage, RubyContext/*!*/ context, object obj, bool strict) {

            var io = obj as RubyIO;
            if (io != null) {
                return io;
            }
            if (obj is MutableString || !Protocols.RespondTo(respondToStorage, obj, "to_io")) {
                return null;
            }

            var site = toIoStorage.GetCallSite("to_io", 0);
            object converted = site.Target(site, obj);
            io = converted as RubyIO;
            if (io == null && strict) {
                throw RubyExceptions.CreateTypeError("can't convert {0} to IO ({0}#to_io gives {1})",
                    context.GetClassDisplayName(obj), context.GetClassDisplayName(converted));
            }
            return io;
        }

        [RubyMethod("reopen")]
        public static RubyIO/*!*/ Reopen(RubyIO/*!*/ self, [NotNull]RubyIO/*!*/ source) {
            // Neither end of a dup2 can be a stream that is already gone.
            if (self.Closed || source.Closed) {
                throw RubyExceptions.CreateIOError("closed stream");
            }

            // MRI's reopen is dup2(2): it points *this descriptor* at the other one's file,
            // which is why everything started afterwards inherits the redirection. Pointing
            // IronRuby's table entry at the other stream only redirects reads and writes made
            // from Ruby, so a redirected STDOUT went on reaching the terminal for every child
            // process - and this IO kept working through the other one's stream, which broke
            // as soon as that one was closed.
            Stream redirected;
            if (RubyIO.TryRedirectDescriptor(self, source, out redirected)) {
                self.Mode = source.Mode;
                if (redirected != null) {
                    self.Context.SetStream(self.GetFileDescriptor(), redirected);
                    self.SetStream(redirected);
                }

                // dup2(2) clears FD_CLOEXEC on the descriptor it points; MRI sets it again on
                // everything but the standard streams.
                int native = self.KernelDescriptor;
                if (native > 2) {
                    RubyProcess.SetCloseOnExec(native);
                    self.Context.SetInstanceVariable(self, "@__close_on_exec__", true);
                }
                CopyPath(self, source);
                return self;
            }

            self.Context.RedirectFileDescriptor(self.GetFileDescriptor(), source.GetFileDescriptor());
            self.SetStream(source.GetStream());
            self.Mode = source.Mode;
            CopyPath(self, source);
            return self;
        }

        // A reopened IO answers the other one's path, as MRI copies it with the descriptor.
        // MRI also gives it the other one's class, which leaves its singleton class behind.
        private static void CopyPath(RubyIO/*!*/ self, RubyIO/*!*/ source) {
            self.Context.ReplaceClrObjectClass(self, self.Context.GetClassOf(source));

            var file = self as RubyFile;
            var sourceFile = source as RubyFile;
            if (file != null && sourceFile != null) {
                file.Path = sourceFile.Path;
                file.PathEncoding = sourceFile.PathEncoding;
            }
        }

        [RubyMethod("reopen")]
        public static RubyIO/*!*/ Reopen(RespondToStorage/*!*/ respondToStorage,
            CallSiteStorage<Func<CallSite, object, object>>/*!*/ toIoStorage, ConversionStorage<MutableString>/*!*/ toPath,
            RubyIO/*!*/ self, object path, [DefaultProtocol, Optional, NotNull]MutableString mode) {

            var source = TryToIO(respondToStorage, toIoStorage, self.Context, path);
            if (source != null) {
                return Reopen(self, source);
            }
            return Reopen(toPath, self, path, mode != null ? IOInfo.Parse(self.Context, mode) : new IOInfo(ReopenMode(self.Mode)));
        }

        /// <summary>
        /// Reopened by path alone, a stream keeps its access mode, and MRI opens the file the way
        /// that mode would: written to only, it is created and truncated (even $stdout, whose own
        /// mode never said so), appended to, it is created; read-write, it has to be there.
        /// </summary>
        private static IOMode ReopenMode(IOMode mode) {
            if ((mode & IOMode.ReadWriteMask) == IOMode.WriteOnly) {
                mode |= IOMode.CreateIfNotExists;
                if ((mode & IOMode.WriteAppends) == 0) {
                    mode |= IOMode.Truncate;
                }
            }
            return mode;
        }

        [RubyMethod("reopen")]
        public static RubyIO/*!*/ Reopen(RespondToStorage/*!*/ respondToStorage,
            CallSiteStorage<Func<CallSite, object, object>>/*!*/ toIoStorage, ConversionStorage<MutableString>/*!*/ toPath,
            RubyIO/*!*/ self, object path, int mode) {

            var source = TryToIO(respondToStorage, toIoStorage, self.Context, path);
            if (source != null) {
                return Reopen(self, source);
            }
            return Reopen(toPath, self, path, new IOInfo(IOModeNative.ToIOMode(mode)));
        }

        private static RubyIO/*!*/ Reopen(ConversionStorage<MutableString>/*!*/ toPath, RubyIO/*!*/ io, object pathObj, IOInfo info) {
            MutableString path = Protocols.CastToPath(toPath, pathObj);
            Stream newStream = RubyFile.OpenFileStream(io.Context, path.ToString(path.Encoding.Encoding), info.Mode);
            if (io.Closed) {
                // Reopening a closed stream with a path is how it is brought back to life, so
                // this one needs a descriptor again rather than the IOError a read would get.
                io.Reset(newStream, info.Mode);
                return io;
            }

            // A standard output stream is the descriptor a child process inherits, so, as with MRI's
            // dup2, the file has to become that descriptor rather than only the stream Ruby
            // writes through - or system and exec afterwards still reach the terminal.
            int native = io.KernelDescriptor;
            if (native == 1 || native == 2) {
                var opened = new RubyIO(io.Context, newStream, info.Mode);
                bool redirected;
                try {
                    Stream ignored;
                    redirected = opened.KernelDescriptor >= 0 && RubyIO.TryRedirectDescriptor(io, opened, out ignored);
                } finally {
                    opened.Close();
                }
                if (redirected) {
                    io.Mode = info.Mode;
                    if (info.HasEncoding) {
                        io.SetEncodings(info.ExternalEncoding, info.InternalEncoding);
                    }
                    return io;
                }
                // Not a descriptor that can be pointed elsewhere: Ruby's own stream is replaced
                // instead, by a new one - the first went with the IO that was wrapped around it.
                newStream = RubyFile.OpenFileStream(io.Context, path.ToString(path.Encoding.Encoding), info.Mode);
            }

            io.Context.SetStream(io.GetFileDescriptor(), newStream);
            io.SetStream(newStream);
            io.Mode = info.Mode;

            if (info.HasEncoding) {
                io.SetEncodings(info.ExternalEncoding, info.InternalEncoding);
            }

            return io;
        }

        /// <summary>
        /// MRI's rb_io_s_sysopen: the path by #to_path; the mode as open(2) flags when it has
        /// #to_int (IO.sysopen(path, File::WRONLY | File::CREAT)), as a mode string otherwise; and
        /// the permission by #to_int, applied (less the umask) to a file the call creates. A nil
        /// mode or permission means "not given". There are no keyword options.
        /// </summary>
        [RubyMethod("sysopen", RubyMethodAttributes.PublicSingleton)]
        public static int SysOpen(ConversionStorage<int?>/*!*/ toInt, ConversionStorage<MutableString>/*!*/ toPath,
            ConversionStorage<MutableString>/*!*/ toStr,
            RubyClass/*!*/ self, object pathObject, [Optional]object modeObject, [Optional]object perm) {

            var context = self.Context;
            MutableString path = Protocols.CastToPath(toPath, pathObject);
            var toIntSite = toInt.GetSite(TryConvertToFixnumAction.Make(context));

            // The mode may carry an encoding suffix ("w:utf-8"), which only IOInfo parses.
            IOInfo info = new IOInfo();
            if (modeObject != Missing.Value && modeObject != null) {
                int? flags = toIntSite.Target(toIntSite, modeObject);
                info = flags.HasValue ? new IOInfo(IOModeNative.ToIOMode(flags.Value)) : IOInfo.Parse(context, Protocols.CastToString(toStr, modeObject));
            }

            int permissions = 0;
            if (perm != Missing.Value && perm != null) {
                int? p = toIntSite.Target(toIntSite, perm);
                if (!p.HasValue) {
                    throw RubyExceptions.CreateImplicitConversionError(context.GetClassName(perm), "Integer");
                }
                permissions = p.Value;
            }

            if (RubyFileOps.DirectoryExists(self.Context, path)) {
                // open(2) takes a directory read-only, and MRI hands that descriptor back. The CLR
                // has no stream for a directory, so it goes into the table as a bare descriptor.
                if (!Posix.IsAvailable) {
                    return -1;
                }
                string directory = self.Context.DecodePath(path);
                int errno;
                int fd = Posix.Open(directory, Posix.O_RDONLY | Posix.O_CLOEXEC, out errno);
                if (fd < 0) {
                    throw Posix.Error(errno, directory);
                }
                return self.Context.AllocateFileDescriptor(new DescriptorStream(fd, true, false, true));
            }

            // The descriptor stays open: MRI hands back a descriptor the caller owns and is
            // expected to wrap in an IO. Closing it here left nothing behind but a table index,
            // and IO.new(index) then read the *kernel's* descriptor of that number - some
            // unrelated pipe - to decide what the file had been opened for.
            var file = new RubyFile(context);
            RubyFileOps.Reinitialize(file, path, info, permissions);
            return file.GetFileDescriptor();
        }

        #endregion

        internal static object TryInvokeOpenBlock(RubyContext/*!*/ context, BlockParam/*!*/ block, RubyIO/*!*/ io) {
            if (block == null)
                return io;

            using (io) {
                object result;
                block.Yield(io, out result);
                return result;
            }
        }

        #region open

        [RubyMethod("open", RubyMethodAttributes.PublicSingleton)]
        public static RuleGenerator/*!*/ Open() {
            return new RuleGenerator((metaBuilder, args, name) => {
                var targetClass = (RubyClass)args.Target;
                targetClass.BuildObjectConstructionNoFlow(metaBuilder, args, name);

                // TODO: initialize yields the block?
                if (args.Signature.HasBlock) {
                    // ignore flow builder set up so far, we need one that creates a BlockParam for library calls:
                    metaBuilder.ControlFlowBuilder = null;

                    if (metaBuilder.BfcVariable == null) {
                        metaBuilder.BfcVariable = metaBuilder.GetTemporary(typeof(BlockParam), "#bfc");
                    }

                    metaBuilder.Result = Ast.Call(new Func<UnaryOpStorage, BlockParam, object, object>(InvokeOpenBlock).GetMethodInfo(), 
                        Ast.Constant(new UnaryOpStorage(args.RubyContext)),
                        metaBuilder.BfcVariable, 
                        metaBuilder.Result
                    );

                    RubyMethodGroupInfo.RuleControlFlowBuilder(metaBuilder, args);
                } else {
                    metaBuilder.BuildControlFlow(args);
                }
            });
        }

        [Emitted]
        public static object InvokeOpenBlock(UnaryOpStorage/*!*/ closeStorage, BlockParam block, object obj) {
            object result = obj;
            if (!RubyOps.IsRetrySingleton(obj) && block != null) {
                try {
                    block.Yield(obj, out result);
                } finally {
                    try {
                        var site = closeStorage.GetCallSite("close");
                        site.Target(site, obj);                        
                    } catch (System.IO.IOException e) when (e.Message == "closed stream") {
                        // A block that closed the file itself leaves nothing for this close to
                        // do, and MRI passes over that one IOError in silence. Anything else
                        // #close raises is the caller's to hear about.
                    }
                }
            }
            return result;
        }

        #endregion

        #region pipe, popen
#if FEATURE_PROCESS
        [RubyMethod("pipe", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_PROCESS")]
        public static RubyArray/*!*/ OpenPipe(RubyClass/*!*/ self) {
            Stream reader, writer;
            RubyPipe.CreatePipe(out reader, out writer);
            RubyArray result = new RubyArray(2);
            var reading = new RubyIO(self.Context, reader, IOMode.ReadOnly);
            var writing = new RubyIO(self.Context, writer, IOMode.WriteOnly);
            // Both ends resolve the current defaults, as every other freshly opened stream does.
            reading.SetEncodings(null, null);
            writing.SetEncodings(null, null);
            result.Add(reading);
            result.Add(writing);
            return result;
        }

        // TODO: params, conversions, options?

        [RubyMethod("popen", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_PROCESS")]
        public static object OpenPipe(RubyContext/*!*/ context, BlockParam block, RubyClass/*!*/ self,
            [DefaultProtocol, NotNull]MutableString/*!*/ command, [DefaultProtocol, Optional, NotNull]MutableString modeString) {

            Process process;
            RubyIO io = OpenPipe(context, command, IOModeEnum.Parse(modeString), out process);
            if (block == null) {
                return io;
            }

            try {
                return TryInvokeOpenBlock(context, block, io);
            } finally {
                // MRI waits for the child when the block returns, so $? reports a finished
                // process rather than one still running
                try {
                    process.WaitForExit();
                } catch (SystemException) {
                    // process already reaped
                }
            }
        }

        [RubyMethod("popen", RubyMethodAttributes.PublicSingleton, BuildConfig = "FEATURE_PROCESS")]
        public static RubyIO/*!*/ OpenPipe(RubyContext/*!*/ context, RubyClass/*!*/ self,
            [DefaultProtocol, NotNull]MutableString/*!*/ command, [DefaultProtocol, Optional, NotNull]MutableString modeString) {
            return OpenPipe(context, command, IOModeEnum.Parse(modeString));
        }

        public static RubyIO/*!*/ OpenPipe(
            RubyContext/*!*/ context, 
            MutableString/*!*/ command, 
            IOMode mode) {

            Process unused;
            return OpenPipe(context, command, mode, out unused);
        }

        internal static RubyIO/*!*/ OpenPipe(
            RubyContext/*!*/ context, 
            MutableString/*!*/ command, 
            IOMode mode,
            out Process/*!*/ process) {

            bool redirectStandardInput = mode.CanWrite();
            bool redirectStandardOutput = mode.CanRead();

            process = RubyProcess.CreateProcess(context, command, redirectStandardInput, redirectStandardOutput, false);

            StreamReader reader = null;
            StreamWriter writer = null;
            if (redirectStandardOutput) {
                reader = process.StandardOutput;
            }

            if (redirectStandardInput) {
                writer = process.StandardInput;
            }

            return new RubyIO(context, reader, writer, mode);
        }

#endif
        #endregion

        #region select

        /// <summary>
        /// IO.select(read, write, error, timeout). Blocks until one of the objects is ready, the
        /// timeout expires (nil), or - with no objects at all - forever.
        ///
        /// IronRuby's IOs are not all kernel descriptors: IO.pipe is an in-process queue, while a
        /// File or the read end of a popen really is a descriptor. So readiness is asked of the
        /// thing itself - poll(2) where there is a descriptor, the pipe's own state where there is
        /// not - and the wait is a poll loop rather than one blocking syscall. The loop also keeps
        /// the wait interruptible by Thread#kill and Thread#raise, and reports the thread as
        /// sleeping while it runs, which is what ruby/spec watches for before it writes to a pipe.
        /// </summary>
        [RubyMethod("select", RubyMethodAttributes.PublicSingleton)]
        public static RubyArray Select(RespondToStorage/*!*/ respondToStorage,
            CallSiteStorage<Func<CallSite, object, object>>/*!*/ toIoStorage,
            RubyContext/*!*/ context, object self,
            object read, [Optional]object write, [Optional]object error, [Optional]object timeout) {

            return SelectInternal(respondToStorage, toIoStorage, context, read, write, error, ToTimeInterval(context, timeout));
        }

        /// <summary>
        /// The timeout in milliseconds, or Timeout.Infinite for "wait as long as it takes". MRI
        /// reports a negative interval and NaN with these exact messages, which ruby/spec asserts.
        /// </summary>
        private static int ToTimeInterval(RubyContext/*!*/ context, object timeout) {
            if (timeout == null || timeout is Missing) {
                return Timeout.Infinite;
            }

            double seconds;
            if (timeout is int) {
                seconds = (int)timeout;
            } else if (timeout is double) {
                seconds = (double)timeout;
            } else if (timeout is long) {
                seconds = (long)timeout;
            } else if (timeout is BigInteger) {
                seconds = (double)(BigInteger)timeout;
            } else {
                throw RubyExceptions.CreateTypeError("can't convert {0} into time interval",
                    context.GetClassDisplayName(timeout));
            }

            if (Double.IsNaN(seconds)) {
                throw RubyExceptions.CreateRangeError("NaN out of Time range");
            }
            if (seconds < 0) {
                throw RubyExceptions.CreateArgumentError("time interval must not be negative");
            }
            if (Double.IsPositiveInfinity(seconds)) {
                return Timeout.Infinite;
            }

            double ms = seconds * 1000;
            return (ms >= Int32.MaxValue) ? Timeout.Infinite : (int)ms;
        }

        /// <summary>What a select set asks of an IO.</summary>
        private enum Readiness { Read, Write, Error }

        /// <summary>One entry of a select set: the object the caller passed, and the IO behind it.</summary>
        private struct SelectEntry {
            public object Object;
            public RubyIO IO;
        }

        private static SelectEntry[]/*!*/ ToEntries(RespondToStorage/*!*/ respondToStorage,
            CallSiteStorage<Func<CallSite, object, object>>/*!*/ toIoStorage, RubyContext/*!*/ context,
            object set, string/*!*/ argumentName) {

            if (set == null || set is Missing) {
                return new SelectEntry[0];
            }

            var array = set as RubyArray;
            if (array == null) {
                throw RubyExceptions.CreateTypeError("wrong argument type {0} (expected Array)",
                    context.GetClassDisplayName(set));
            }

            var result = new SelectEntry[array.Count];
            for (int i = 0; i < array.Count; i++) {
                result[i] = new SelectEntry { Object = array[i], IO = ToSelectableIo(respondToStorage, toIoStorage, context, array[i]) };
            }
            return result;
        }

        /// <summary>
        /// MRI's rb_io_check_io: an IO is itself, anything else is asked for #to_io, and whatever
        /// comes back has to be an IO. The object the caller passed is what goes in the result, not
        /// the IO it named.
        /// </summary>
        private static RubyIO/*!*/ ToSelectableIo(RespondToStorage/*!*/ respondToStorage,
            CallSiteStorage<Func<CallSite, object, object>>/*!*/ toIoStorage, RubyContext/*!*/ context, object obj) {

            var io = obj as RubyIO;
            if (io == null && Protocols.RespondTo(respondToStorage, obj, "to_io")) {
                var site = toIoStorage.GetCallSite("to_io", 0);
                io = site.Target(site, obj) as RubyIO;
            }

            if (io == null) {
                throw RubyExceptions.CreateTypeError("can't convert {0} into IO", context.GetClassDisplayName(obj));
            }
            return io;
        }

        private static RubyArray SelectInternal(RespondToStorage/*!*/ respondToStorage,
            CallSiteStorage<Func<CallSite, object, object>>/*!*/ toIoStorage, RubyContext/*!*/ context,
            object read, object write, object error, int timeoutMilliseconds) {

            var reads = ToEntries(respondToStorage, toIoStorage, context, read, "read");
            var writes = ToEntries(respondToStorage, toIoStorage, context, write, "write");
            var errors = ToEntries(respondToStorage, toIoStorage, context, error, "error");

            if (reads.Length == 0 && writes.Length == 0 && errors.Length == 0) {
                // Nothing to watch: MRI just sleeps, and IO.select(nil, nil, nil) is the documented
                // way to sleep forever in a thread that Thread#kill can still end.
                if (timeoutMilliseconds == Timeout.Infinite) {
                    ThreadOps.SleepForLibrary(Timeout.Infinite);
                } else if (timeoutMilliseconds > 0) {
                    ThreadOps.SleepForLibrary(timeoutMilliseconds);
                }
                return null;
            }

            long deadline = (timeoutMilliseconds == Timeout.Infinite)
                ? Int64.MaxValue
                : Environment.TickCount64 + timeoutMilliseconds;

            var watched = new List<RubyIO>(reads.Length + writes.Length + errors.Length);
            var kinds = new List<int>(watched.Capacity);
            AddWatched(watched, kinds, reads, IoReadiness.Read);
            AddWatched(watched, kinds, writes, IoReadiness.Write);
            AddWatched(watched, kinds, errors, IoReadiness.Error);

            return WaitUntilReady(watched, kinds, deadline, () => CollectReady(reads, writes, errors));
        }

        private static void AddWatched(List<RubyIO>/*!*/ watched, List<int>/*!*/ kinds, SelectEntry[]/*!*/ entries, int kind) {
            for (int i = 0; i < entries.Length; i++) {
                watched.Add(entries[i].IO);
                kinds.Add(kind);
            }
        }

        /// <summary>
        /// The wait IO.select, IO#wait and NIO::Selector#select share: ask <paramref name="check"/>
        /// what is ready, and if nothing is, sleep in the kernel (IoReadiness.Block) until one of
        /// the watched IOs might have changed, then ask again. A thread parked here is asleep as far
        /// as Ruby is concerned, and Thread#kill and Thread#raise reach it between two sleeps.
        ///
        /// This used to be a loop that slept a millisecond between checks, which woke every
        /// selecting thread a thousand times a second and added up to a millisecond to every wakeup.
        /// </summary>
        internal static T WaitUntilReady<T>(IList<RubyIO>/*!*/ watched, IList<int>/*!*/ kinds, long deadline, Func<T>/*!*/ check)
            where T : class {

            var info = ThreadOps.RubyThreadInfo.FromThread(Thread.CurrentThread);
            bool wasBlocked = info.Blocked;
            ReadinessWaiter waiter = null;
            List<object> pipes = null;
            try {
                info.Blocked = true;

                // In-process pipes have no descriptor to poll, and an IO with a readiness hook
                // (a TLS socket) may be ready when its descriptor is not; they signal the waiter.
                if (IoReadiness.NeedsWaiter(watched)) {
                    waiter = ReadinessWaiter.Current;
                }
                if (waiter != null) {
                    pipes = IoReadiness.Register(waiter, watched);
                }

                while (true) {
                    if (waiter != null) {
                        waiter.Reset();
                    }

                    T ready = check();
                    if (ready != null) {
                        return ready;
                    }

                    long remaining = deadline - Environment.TickCount64;
                    if (remaining <= 0) {
                        return null;
                    }

                    RubyUtils.CheckAsyncException();
                    IoReadiness.Block(watched, kinds, waiter, (int)Math.Min(remaining, IoReadiness.SliceMilliseconds));
                }
            } finally {
                if (waiter != null) {
                    IoReadiness.Unregister(waiter, pipes);
                }
                info.Blocked = wasBlocked;
            }
        }

        /// <summary>The three result arrays, or null when nothing is ready yet.</summary>
        private static RubyArray CollectReady(SelectEntry[]/*!*/ reads, SelectEntry[]/*!*/ writes, SelectEntry[]/*!*/ errors) {
            RubyArray readReady = CollectReady(reads, Readiness.Read);
            RubyArray writeReady = CollectReady(writes, Readiness.Write);
            RubyArray errorReady = CollectReady(errors, Readiness.Error);

            if (readReady.Count == 0 && writeReady.Count == 0 && errorReady.Count == 0) {
                return null;
            }

            var result = new RubyArray(3);
            result.Add(readReady);
            result.Add(writeReady);
            result.Add(errorReady);
            return result;
        }

        private static RubyArray/*!*/ CollectReady(SelectEntry[]/*!*/ entries, Readiness kind) {
            var result = new RubyArray();
            for (int i = 0; i < entries.Length; i++) {
                if (IsReady(entries[i].IO, kind)) {
                    result.Add(entries[i].Object);
                }
            }
            return result;
        }

        private static bool IsReady(RubyIO/*!*/ io, Readiness kind) {
            if (io.Closed) {
                throw RubyExceptions.CreateIOError("closed stream");
            }

            var stream = io.GetStream();
            if (kind == Readiness.Read && stream.DataBuffered) {
                return true;
            }

            var hook = IoReadiness.GetHook(io);
            if (hook != null && kind != Readiness.Error) {
                bool? ready = hook.IsReady(kind == Readiness.Read ? IoReadiness.Read : IoReadiness.Write);
                if (ready.HasValue) {
                    return ready.Value;
                }
            }

            var pipe = stream.BaseStream as RubyPipe;
            if (pipe != null) {
                // The mode settles the two ends of an in-process pipe: they share one queue, so
                // only the mode says which end a read would ever come from. MRI gets the same
                // answer from the kernel, which knows each end by its own descriptor.
                switch (kind) {
                    case Readiness.Read: return io.Mode.CanRead() && pipe.CanReadWithoutBlocking;
                    case Readiness.Write: return io.Mode.CanWrite() && pipe.CanWriteWithoutBlocking;
                    // There is no out-of-band data on an in-process pipe for the error set to report.
                    default: return false;
                }
            }

            // KernelDescriptor rather than NativeDescriptor: the standard streams are console
            // streams rather than FileStreams, so only the former knows they are descriptors 0,
            // 1 and 2. Asking the wrong one leaves STDIN in the no-descriptor case below, where
            // a readable mode is reported ready whether or not a byte is there - and a line
            // editor that asks "is anything waiting?" before deciding a keystroke is the end of
            // the line (reline does, to tell a paste from typing) is told yes for ever.
            int descriptor = io.KernelDescriptor;
            if (descriptor < 0) {
                // Off Unix a socket has no descriptor IronRuby can poll(2), but it is still a
                // socket, and the socket can be asked. Without this every socket was "ready" on
                // Windows, and a select loop over one spun instead of waiting.
                var socket = IoReadiness.GetSocket(io);
                if (socket != null) {
                    switch (kind) {
                        case Readiness.Read: return socket.Poll(0, System.Net.Sockets.SelectMode.SelectRead);
                        case Readiness.Write: return socket.Poll(0, System.Net.Sockets.SelectMode.SelectWrite);
                        default: return socket.Poll(0, System.Net.Sockets.SelectMode.SelectError);
                    }
                }

                // And a Windows pipe can be asked whether anything is waiting in it.
                var windowsPipe = IoReadiness.GetWindowsPipe(io);
                if (windowsPipe != null) {
                    switch (kind) {
                        case Readiness.Read: return io.Mode.CanRead() && IoReadiness.IsWindowsPipeReadable(windowsPipe);
                        case Readiness.Write: return io.Mode.CanWrite();
                        default: return false;
                    }
                }

                // No descriptor and no pipe - a StringIO-like stream, where the mode is all there
                // is to go on. MRI says a regular file is always ready, and this is as close as
                // we get.
                if (kind == Readiness.Read && !io.Mode.CanRead()) {
                    return false;
                }
                if (kind == Readiness.Write && !io.Mode.CanWrite()) {
                    return false;
                }
                return kind != Readiness.Error;
            }

            // Where there is a descriptor the kernel is asked, and the mode is not consulted at
            // all: poll(2) reports a regular file opened read-only as writable, so CRuby puts one
            // in IO.select's write set and answers IO::WRITABLE for it.

            short events;
            switch (kind) {
                case Readiness.Read: events = RubyIO.POLLIN; break;
                case Readiness.Write: events = RubyIO.POLLOUT; break;
                default: events = POLLPRI; break;
            }

            short[] revents = RubyIO.Poll(new object[] { descriptor }, new object[] { events }, 0);
            if (revents == null) {
                // No poll on this platform: MRI's answer for a plain file, which is what is left.
                return kind != Readiness.Error;
            }

            // A hung-up or broken descriptor is ready in the sense that the operation will not
            // block - it returns EOF or fails at once, which is what MRI reports too.
            const short broken = RubyIO.POLLERR | RubyIO.POLLHUP | RubyIO.POLLNVAL;
            if (kind != Readiness.Error && (revents[0] & broken) != 0) {
                return true;
            }
            return (revents[0] & events) != 0;
        }

        // Out-of-band data; only sockets ever report it, but the error set means nothing else.
        private const short POLLPRI = 0x002;

        #endregion

        #region wait, wait_readable, wait_writable, wait_priority

        // poll(2)'s own bits, which is where MRI takes these from: POLLIN, POLLPRI, POLLOUT. Worth
        // stating, because the obvious guess - 1, 2 and 4 in the order the documentation lists the
        // events - puts WRITABLE and PRIORITY the wrong way round.
        [RubyConstant("READABLE")]
        public const int Readable = 0x001;

        [RubyConstant("PRIORITY")]
        public const int Priority = 0x002;

        [RubyConstant("WRITABLE")]
        public const int Writable = 0x004;

        /// <summary>
        /// IO#wait. Two shapes share the name: wait(events, timeout) answers the subset of the
        /// events that came ready, and the older wait(timeout, *modes) answers the IO itself.
        /// Either answers nil when the timeout runs out first.
        /// </summary>
        [RubyMethod("wait")]
        public static object Wait(ConversionStorage<int>/*!*/ fixnumCast, RubyContext/*!*/ context, RubyIO/*!*/ self,
            [NotNull]params object[]/*!*/ args) {

            int events;
            object timeout;
            bool answerSelf;
            ParseWaitArguments(fixnumCast, context, args, out events, out timeout, out answerSelf);

            object ready = WaitForEvents(context, self, events, timeout);
            if (ready == null) {
                return null;
            }
            return answerSelf ? (object)self : ready;
        }

        /// <summary>
        /// MRI documents IO#wait(events, timeout) but implements wait(*args): exactly two arguments
        /// with no Symbol among them are the documented form, and anything else is the older
        /// wait(timeout, *modes), which is why the two can be told apart at all.
        /// </summary>
        private static void ParseWaitArguments(ConversionStorage<int>/*!*/ fixnumCast, RubyContext/*!*/ context,
            object[]/*!*/ args, out int events, out object timeout, out bool answerSelf) {

            if (args.Length == 2 && !(args[0] is RubySymbol) && !(args[1] is RubySymbol)) {
                events = Protocols.CastToFixnum(fixnumCast, args[0]);
                if (events <= 0) {
                    throw RubyExceptions.CreateArgumentError("Events must be positive integer!");
                }
                timeout = args[1];
                answerSelf = false;
                return;
            }

            events = 0;
            timeout = Missing.Value;
            answerSelf = true;
            foreach (object arg in args) {
                var mode = arg as RubySymbol;
                if (mode != null) {
                    events |= ToWaitEvents(mode);
                } else if (timeout is Missing) {
                    // Converted here rather than at the end so that a bad interval is reported in
                    // the order the arguments were written, the way MRI's rb_time_interval is.
                    ToTimeInterval(context, arg);
                    timeout = arg;
                } else {
                    throw RubyExceptions.CreateArgumentError("timeout given more than once");
                }
            }

            if (events == 0) {
                events = Readable;
            }
        }

        private static int ToWaitEvents(RubySymbol/*!*/ mode) {
            switch (mode.ToString()) {
                case "r": case "read": case "readable":
                    return Readable;
                case "w": case "write": case "writable":
                    return Writable;
                case "rw": case "read_write": case "readable_writable":
                    return Readable | Writable;
                default:
                    throw RubyExceptions.CreateArgumentError("unsupported mode: {0}", mode.ToString());
            }
        }

        /// <summary>
        /// The events among <paramref name="events"/> that are ready, or null if the timeout passed
        /// with none of them ready. Waits the same way IO.select does - a poll loop that stays
        /// interruptible by Thread#kill and reports the thread as sleeping while it runs.
        /// </summary>
        private static object WaitForEvents(RubyContext/*!*/ context, RubyIO/*!*/ io, int events, object timeout) {
            int milliseconds = ToTimeInterval(context, timeout);
            long deadline = (milliseconds == Timeout.Infinite)
                ? Int64.MaxValue
                : Environment.TickCount64 + milliseconds;

            var watched = new List<RubyIO>(3);
            var kinds = new List<int>(3);
            if ((events & Readable) != 0) { watched.Add(io); kinds.Add(IoReadiness.Read); }
            if ((events & Writable) != 0) { watched.Add(io); kinds.Add(IoReadiness.Write); }
            if ((events & Priority) != 0) { watched.Add(io); kinds.Add(IoReadiness.Error); }

            return WaitUntilReady<object>(watched, kinds, deadline, () => {
                int ready = ReadyEvents(io, events);
                return (ready != 0) ? ScriptingRuntimeHelpers.Int32ToObject(ready) : null;
            });
        }

        private static int ReadyEvents(RubyIO/*!*/ io, int events) {
            int ready = 0;
            if ((events & Readable) != 0 && IsReady(io, Readiness.Read)) {
                ready |= Readable;
            }
            if ((events & Writable) != 0 && IsReady(io, Readiness.Write)) {
                ready |= Writable;
            }
            if ((events & Priority) != 0 && IsReady(io, Readiness.Error)) {
                ready |= Priority;
            }
            // Nothing asked of the IO leaves the closed check undone, and a closed IO has to be an
            // IOError however the events were spelled.
            if (events == 0 && io.Closed) {
                throw RubyExceptions.CreateIOError("closed stream");
            }
            return ready;
        }

        /// <summary>
        /// The three single-event waits answer the IO or nil, and unlike #wait they first insist
        /// that the IO is open the way the event asks for - MRI's rb_io_check_readable/writable.
        /// </summary>
        [RubyMethod("wait_readable")]
        public static object WaitUntilReadable(RubyContext/*!*/ context, RubyIO/*!*/ self, [Optional]object timeout) {
            self.RequireReadable();
            return WaitForEvents(context, self, Readable, timeout) == null ? null : (object)self;
        }

        [RubyMethod("wait_writable")]
        public static object WaitUntilWritable(RubyContext/*!*/ context, RubyIO/*!*/ self, [Optional]object timeout) {
            self.RequireWritable();
            return WaitForEvents(context, self, Writable, timeout) == null ? null : (object)self;
        }

        [RubyMethod("wait_priority")]
        public static object WaitUntilPriority(RubyContext/*!*/ context, RubyIO/*!*/ self, [Optional]object timeout) {
            self.RequireReadable();
            return WaitForEvents(context, self, Priority, timeout) == null ? null : (object)self;
        }

        #endregion

        #region close, close_read, close_write, closed?, close_on_exec (1.9)

        /// <summary>
        /// Closing a stream that is already closed has been a no-op since Ruby 2.3 - which is
        /// what makes the usual "@io.close if @io" cleanup safe to run twice.
        /// </summary>
        [RubyMethod("close")]
        public static void Close(RubyIO/*!*/ self) {
            if (self.Closed) {
                return;
            }

            // With #autoclose false the descriptor outlives the IO object - another IO may be
            // using it (IO.new(io.fileno) shares the stream).
            object autoclose;
            if (self.Context.TryGetInstanceVariable(self, "@__autoclose", out autoclose) && autoclose is bool && !(bool)autoclose) {
                self.CloseKeepingDescriptor();
                return;
            }
            Exception flushError = null;
            try {
                // Writes currently go straight to the descriptor, so WritePending is only the
                // state MRI uses for buffering warnings. Do not probe a pipe merely because it
                // was written: that manufactures EPIPE after its reader has consumed the data.
                // A failed explicit #flush is different; MRI retries it from #close.
                if (self.GetStream().WriteFlushFailed) {
                    self.Flush();
                }
            } catch (IOException e) {
                flushError = TranslateWriteError(e, self);
            } finally {
                self.Close();
            }
            if (flushError != null) {
                throw flushError;
            }
        }

        // TODO:
        [RubyMethod("close_read")]
        public static void CloseReader(RubyIO/*!*/ self) {
            if (self.Closed) {
                return;
            }
            self.CloseReader();
        }

        // TODO:
        [RubyMethod("close_write")]
        public static void CloseWriter(RubyIO/*!*/ self) {
            if (self.Closed) {
                return;
            }
            self.CloseWriter();
        }
        
        [RubyMethod("closed?")]
        public static bool Closed(RubyIO/*!*/ self) {
            return self.Closed;
        }

        // TODO: 1.9 only
        // close_on_exec=
        // close_on_exec?

        #endregion

        //stat

        #region fcntl/ioctl, fsync/flush

        [RubyMethod("fcntl")]
        public static int FileControl(RubyIO/*!*/ self, [DefaultProtocol]int commandId, [Optional]MutableString arg) {
            // No argument is MRI's 0, which is what a command like F_GETFD or F_GETFL takes.
            if (arg == null) {
                return self.FileControl(commandId, 0);
            }
            return self.FileControl(commandId, arg.ConvertToBytes());
        }

        [RubyMethod("fcntl")]
        public static int FileControl(RubyIO/*!*/ self, [DefaultProtocol]int commandId, int arg) {
            return self.FileControl(commandId, arg);
        }

        // ioctl(2) itself; it used to be fcntl under another name. As in MRI, nil and false pass
        // 0, true passes 1, an Integer passes itself, and a String is the buffer the call reads
        // and writes - grown first to the size the request encodes, 256 bytes at least (MRI's
        // DEFAULT_IOCTL_NARG_LEN), and left at that size.
        //
        // Windows has no ioctl(2): there MRI defines the method as rb_f_notimplement, so it raises
        // NotImplementedError and #respond_to? denies it - tty-screen, for one, asks before it calls.
        [RubyMethod("ioctl")]
        [RubyNotImplemented(OnWindowsOnly = true)]
        public static int IoControl(CallSiteStorage<Func<CallSite, object, object, object, object>>/*!*/ newStorage,
            RubyIO/*!*/ self, [DefaultProtocol]IntegerValue request, [Optional]object arg) {
            self.GetStream();
            if (!Posix.IsAvailable) {
                throw new IronRuby.Builtins.NotImplementedError("ioctl() function is unimplemented on this machine");
            }
            int fd = self.KernelDescriptor;
            if (fd < 0) {
                throw new NotSupportedException();
            }

            long cmd = request.ToInt64();
            long value = 0;
            byte[] buffer = null;
            var str = arg as MutableString;
            if (str != null) {
                str.RequireNotFrozen();
                int length = ((cmd & 0xFFFF0000) == 0) ? 256 : Math.Max((int)((cmd >> 16) & 0x3FFF), 256);
                buffer = new byte[Math.Max(length, str.GetByteCount())];
                Buffer.BlockCopy(str.ToByteArray(), 0, buffer, 0, str.GetByteCount());
            } else if (arg == null || arg == Missing.Value || arg is bool) {
                value = (arg is bool && (bool)arg) ? 1 : 0;
            } else if (arg is int) {
                value = (int)arg;
            } else if (arg is long) {
                value = (long)arg;
            } else if (arg is BigInteger) {
                value = (long)(BigInteger)arg;
            } else {
                throw RubyExceptions.CreateImplicitConversionError(self.Context.GetClassDisplayName(arg), "Integer");
            }

            int errno;
            int result = Posix.Ioctl(fd, cmd, buffer, value, out errno);
            if (result < 0) {
                // SystemCallError.new(nil, errno) picks the Errno class, Errno::ENOTTY above all.
                object errorClass;
                if (self.Context.ObjectClass.TryGetConstant(null, "SystemCallError", out errorClass)) {
                    var site = newStorage.GetCallSite("new", 2);
                    var error = site.Target(site, errorClass, null, errno) as Exception;
                    if (error != null) {
                        throw error;
                    }
                }
                throw Posix.Error(errno, null);
            }
            if (str != null) {
                str.Clear();
                str.Append(buffer, 0, buffer.Length);
            }
            return result;
        }

        [RubyMethod("flush")]
        public static RubyIO/*!*/ Flush(RubyIO/*!*/ self) {
            try {
                self.Flush();
            } catch (IOException e) {
                throw TranslateWriteError(e, self);
            }
            return self;
        }

        // fsync is flush plus the write-back the name promises, and MRI answers 0 for it.
        [RubyMethod("fsync")]
        public static int FSync(RubyIO/*!*/ self) {
            Flush(self);
            return 0;
        }

        #endregion

        #region eof, pid, to_i, binmode, sync, sync=, to_io, inspect

        [RubyMethod("eof")]
        [RubyMethod("eof?")]
        public static bool Eof(RubyIO/*!*/ self) {
            self.RequireReadable();
            return self.IsEndOfStream();
        }

        [RubyMethod("pid")]
        public static object Pid(RubyIO/*!*/ self) {
            self.RequireOpen();
            return null;  // OK to return null on Windows
        }

        [RubyMethod("fileno")]
        [RubyMethod("to_i")]
        public static int FileNo(RubyIO/*!*/ self) {
            return self.GetFileDescriptor();
        }

        [RubyMethod("binmode")]
        public static RubyIO/*!*/ Binmode(RubyIO/*!*/ self) {
            self.RequireOpen();
            if (self.Position == 0) {
                self.PreserveEndOfLines = true;
            }
            return self;
        }

        [RubyMethod("binmode?")]
        public static bool IsBinmode(RubyIO/*!*/ self) {
            self.RequireOpen();
            return self.IsBinmode;
        }

        [RubyMethod("stat", BuildConfig = "FEATURE_FILESYSTEM")]
        public static System.IO.FileSystemInfo/*!*/ Stat(RubyIO/*!*/ self) {
            self.RequireOpen();
            return RubyFileOps.RubyStatOps.Create(self);
        }

        [RubyMethod("sync")]
        public static bool Sync(RubyIO/*!*/ self) {
            self.RequireOpen();
            return self.AutoFlush;
        }

        [RubyMethod("sync=")]
        public static bool Sync(RubyIO/*!*/ self, bool sync) {
            self.RequireOpen();
            self.AutoFlush = sync;
            return sync;
        }

        [RubyMethod("to_io")]
        public static RubyIO/*!*/ ToIO(RubyIO/*!*/ self) {
            return self;
        }

        [RubyMethod("inspect")]
        public static MutableString/*!*/ Inspect(RubyIO/*!*/ self) {
            var result = MutableString.CreateMutable(self.Context.GetIdentifierEncoding());
            result.Append("#<");
            result.Append(self.Context.GetClassOf(self).GetName(self.Context));
            result.Append(':');
            if (self.Initialized) {
                switch (self.ConsoleStreamType) {
                    case ConsoleStreamType.Input: result.Append("<STDIN>"); break;
                    case ConsoleStreamType.Output: result.Append("<STDOUT>"); break;
                    case ConsoleStreamType.ErrorOutput: result.Append("<STDERR>"); break;
                    case null: {
                        // A stream that knows its path shows it; one that does not shows its
                        // descriptor, which a closed stream no longer has.
                        object path;
                        self.Context.TryGetInstanceVariable(self, "@__io_path__", out path);
                        var pathString = path as MutableString;
                        if (pathString != null) {
                            result.Append(pathString);
                            if (self.Closed) {
                                result.Append(" (closed)");
                            }
                        } else if (self.Closed) {
                            result.Append("(closed)");
                        } else {
                            result.Append("fd ").Append(self.GetFileDescriptor().ToString(CultureInfo.InvariantCulture));
                        }
                        break;
                    }
                }
            } else {
                RubyUtils.AppendFormatHexObjectId(result, RubyUtils.GetObjectId(self.Context, self));
            }
            result.Append('>');
            return result;
        }

        #endregion

        #region isatty

        [RubyMethod("isatty")]
        [RubyMethod("tty?")]
        public static bool IsAtty(RubyIO/*!*/ self) {
            ConsoleStreamType? console = self.ConsoleStreamType;
            if (console == null) {
                self.RequireOpen();
                if (Environment.OSVersion.Platform == PlatformID.Unix ||
                    Environment.OSVersion.Platform == PlatformID.MacOSX) {
                    int descriptor = self.KernelDescriptor;
                    return descriptor >= 0 && isatty(descriptor) == 1;
                }
                return false;
            }

            int fd = GetStdHandleFd(console.Value);
            switch (Environment.OSVersion.Platform) {
                case PlatformID.Win32NT:
                case PlatformID.Win32S:
                case PlatformID.Win32Windows:
                case PlatformID.WinCE:
                    IntPtr handle = GetStdHandle(fd);
                    if (handle == IntPtr.Zero) {
                        throw new Win32Exception();
                    }

                    return GetFileType(handle) == FILE_TYPE_CHAR;

                default:
                    // STD_*_HANDLE are Win32 pseudo-handles; POSIX wants the descriptor itself
                    return isatty((int)console.Value) == 1;
            }
        }

        private static int GetStdHandleFd(ConsoleStreamType streamType) {
            switch (streamType) {
                case ConsoleStreamType.Input: return STD_INPUT_HANDLE;
                case ConsoleStreamType.Output: return STD_OUTPUT_HANDLE;
                case ConsoleStreamType.ErrorOutput: return STD_ERROR_HANDLE;
                default: throw Assert.Unreachable;
            }
        }

        private const int FILE_TYPE_CHAR = 0x0002;

        private const int STD_INPUT_HANDLE = -10;
        private const int STD_OUTPUT_HANDLE = -11;
        private const int STD_ERROR_HANDLE = -12;

        [DllImport("kernel32")]
        private extern static IntPtr GetStdHandle(int nStdHandle);
        
        [DllImport("kernel32")]
        private extern static int GetFileType(IntPtr hFile);

        [DllImport ("libc")]
        private static extern int isatty(int desc);

        #endregion

        #region external_encoding, internal_encoding, set_encoding

        /// <summary>
        /// CRuby's rb_io_external_encoding (io.c): the pinned external encoding if there is
        /// one, and otherwise the current Encoding.default_external for a readable stream but
        /// nil for a write-only one - which is what a plain "w" or "r+" gets.
        /// </summary>
        [RubyMethod("external_encoding")]
        public static RubyEncoding GetExternalEncoding(RubyIO/*!*/ self) {
            if (self.Enc2 != null) {
                return self.Enc2;
            }
            if (self.Mode.CanWrite()) {
                return self.Enc;
            }
            return self.Enc ?? self.Context.DefaultExternalEncoding;
        }

        /// <summary>
        /// CRuby's rb_io_internal_encoding (io.c). The internal encoding is what the bytes get
        /// transcoded *to*, so it is nil whenever there is no transcoding to do.
        /// </summary>
        [RubyMethod("internal_encoding")]
        public static RubyEncoding GetInternalEncoding(RubyIO/*!*/ self) {
            return self.Enc2 != null ? self.Enc : null;
        }

        // TODO: to-str, last param to-hash

        [RubyMethod("set_encoding")]
        public static RubyIO/*!*/ SetEncodings(ConversionStorage<IDictionary<object, object>>/*!*/ toHash, ConversionStorage<MutableString>/*!*/ toStr,
            RubyIO/*!*/ self, object external, [Optional]object @internal, [Optional]IDictionary<object, object> options) {

            self.RequireOpen();
            Protocols.TryConvertToOptions(toHash, ref options, ref external, ref @internal);
            self.ConversionOptions = options;

            RubyEncoding externalEncoding = null, internalEncoding = null;
            if (external != Missing.Value && external != null) {
                externalEncoding = ToEncodingOrWarn(toStr, external);
            }
            if (@internal != Missing.Value && @internal != null) {
                if (external == null) {
                    // set_encoding(nil, <something>) goes down MRI's "the second argument names
                    // the internal encoding of a pair" path and tries to coerce nil to a String.
                    throw RubyExceptions.CreateTypeConversionError("nil", "String");
                }
                internalEncoding = ToEncodingOrWarn(toStr, @internal);
            }
            return SetEncodings(self, externalEncoding, internalEncoding);
        }

        // An unknown encoding name is warned about and ignored, as it is in a mode string.
        private static RubyEncoding ToEncodingOrWarn(ConversionStorage<MutableString>/*!*/ toStr, object/*!*/ value) {
            var encoding = value as RubyEncoding;
            if (encoding != null) {
                return encoding;
            }
            string name = Protocols.CastToString(toStr, value).ToString();
            if (name.IndexOf('\0') >= 0) {
                throw RubyExceptions.CreateArgumentError("string contains null byte");
            }
            return IOInfo.TryParseEncoding(toStr.Context, name);
        }

        /// <summary>
        /// The conversion options the stream was opened with, for the transcoding the library
        /// code does on the way in and on the way out. Not a CRuby method.
        /// </summary>
        [RubyMethod("__conversion_options__", RubyMethodAttributes.PrivateInstance)]
        public static object GetConversionOptions(RubyIO/*!*/ self) {
            return self.ConversionOptions;
        }

        [RubyMethod("set_encoding")]
        public static RubyIO/*!*/ SetEncodings(RubyIO/*!*/ self, RubyEncoding external, [DefaultParameterValue(null)]RubyEncoding @internal) {
            self.RequireOpen();
            self.SetEncodings(external, @internal);
            return self;
        }

        #endregion

        #region rewind, seek, sysseek, pos, tell, lineno

        [RubyMethod("rewind")]
        public static int Rewind(RubyContext/*!*/ context, RubyIO/*!*/ self) {
            self.Seek(0, SeekOrigin.Begin);
            self.LineNumber = 0;
            return 0;
        }

        [RubyMethod("seek")]
        public static int Seek(RubyIO/*!*/ self, [DefaultProtocol]IntegerValue pos, [DefaultProtocol, DefaultParameterValue(SEEK_SET)]int seekOrigin) {
            self.Seek(pos.ToInt64(), self.CheckSeekOrigin(seekOrigin));
            return 0;
        }

        // MRI refuses to seek past bytes it has read ahead, which would be lost, warns about
        // written ones it still holds, and answers the new offset.
        [RubyMethod("sysseek")]
        public static object SysSeek(BinaryOpStorage/*!*/ writeStorage, ConversionStorage<MutableString>/*!*/ tosConversion,
            RubyIO/*!*/ self, [DefaultProtocol]IntegerValue pos, [DefaultProtocol, DefaultParameterValue(SEEK_SET)]int seekOrigin) {
            var stream = self.GetStream();
            if (stream.DataBuffered) {
                throw RubyExceptions.CreateIOError("sysseek for buffered IO");
            }
            if (stream.WritePending) {
                PrintOps.ReportWarning(writeStorage, tosConversion, MutableString.CreateAscii("sysseek for buffered IO"));
            }
            self.Flush();
            self.Seek(pos.ToInt64(), self.CheckSeekOrigin(seekOrigin));
            return Pos(self);
        }

        [RubyMethod("pos")]
        [RubyMethod("tell")]
        public static object/*!*/ Pos(RubyIO/*!*/ self) {
            if (self.Position <= Int32.MaxValue) {
                return (int)self.Position;
            }

            return (BigInteger)self.Position;
        }

        [RubyMethod("pos=")]
        public static void Pos(RubyIO/*!*/ self, [DefaultProtocol]IntegerValue pos) {
            self.Seek(pos.ToInt64(), SeekOrigin.Begin);
        }

        // The line number counts lines read, so a stream that cannot be read from has none.
        [RubyMethod("lineno")]
        public static int GetLineNumber(RubyIO/*!*/ self) {
            self.RequireReadable();
            return self.LineNumber;
        }

        [RubyMethod("lineno=")]
        public static void SetLineNumber(RubyContext/*!*/ context, RubyIO/*!*/ self, [DefaultProtocol]int value) {
            self.RequireReadable();
            self.LineNumber = value;
        }

        #endregion

        #region write, syswrite, write_nonblock
        /// <summary>
        /// Whether the descriptor was opened for writing. IO::Buffer.map needs to know,
        /// because a shared mapping asks mmap for PROT_WRITE and the kernel refuses that on
        /// a read-only descriptor; there is no public Ruby way to ask.
        /// </summary>
        [RubyMethod("__writable__?")]
        public static bool IsWritable(RubyIO/*!*/ self) {
            return self.Mode.CanWrite();
        }


        // A String is converted into the stream's external encoding on the way out (the
        // conversion is Ruby code, IO#__encode_for_write__ in ruby4.rb); what needs none is
        // decided here, which is nearly every write. Anything else is made a String with #to_s
        // and written as it comes, and several arguments are written one after another, adding
        // up the bytes.

        [RubyMethod("write")]
        public static int Write(TranscodeStorage/*!*/ encodeForWrite, RubyIO/*!*/ self, [NotNull]MutableString/*!*/ val) {
            return Write(self, EncodeForWrite(encodeForWrite, self, val), true);
        }

        [RubyMethod("write")]
        public static int Write(TranscodeStorage/*!*/ encodeForWrite, ConversionStorage<MutableString>/*!*/ tosConversion,
            RubyIO/*!*/ self, params object[]/*!*/ args) {
            int total = 0;
            foreach (object arg in args) {
                var str = arg as MutableString;
                total += (str != null)
                    ? Write(encodeForWrite, self, str)
                    : Write(tosConversion, self, arg);
            }
            return total;
        }

        /// <summary>
        /// Only a stream that was told an external encoding converts what is written to it: MRI
        /// leaves an ordinary stream alone, which keeps a binary string writable to $stdout. A
        /// BINARY destination converts nothing either, and neither does text already in the
        /// target encoding, or ASCII text going somewhere that keeps ASCII where it is. Newline
        /// conversion options always go through the Ruby side.
        /// </summary>
        private static MutableString/*!*/ EncodeForWrite(TranscodeStorage/*!*/ encodeForWrite, RubyIO/*!*/ self, MutableString/*!*/ val) {
            if (self.ConversionOptions == null) {
                RubyEncoding target = GetExternalEncoding(self);
                if (target == null || target == RubyEncoding.Binary || val.Encoding == target ||
                    (target.IsAsciiIdentity && val.IsAscii())) {
                    return val;
                }
            }

            var site = encodeForWrite.GetCallSite("__encode_for_write__", new RubyCallSignature(1, RubyCallFlags.HasImplicitSelf));
            return site.Target(site, self, val) as MutableString ?? val;
        }

        private static int Write(RubyIO/*!*/ self, MutableString/*!*/ val, bool signalOnPipeError) {
            // MRI's write of an empty string makes no syscall at all and answers 0, even on a
            // pipe whose reader is gone. Flushing here instead manufactures EPIPE - which is
            // what Open3.capture3 ("git --version", no stdin data) used to hit on its i.write "".
            if (val.IsEmpty) {
                return 0;
            }

            try {
                int bytesWritten = self.WriteBytes(val, 0, val.GetByteCount());
                if (self.AutoFlush || !signalOnPipeError) {
                    self.Flush();
                }
                return bytesWritten;
            } catch (IOException e) {
                throw signalOnPipeError ? TranslateWriteError(e, self) : TranslateStreamError(e);
            }
        }

        [RubyMethod("write")]
        public static int Write(ConversionStorage<MutableString>/*!*/ tosConversion, RubyIO/*!*/ self, object obj) {
            return Write(self, Protocols.ConvertToString(tosConversion, obj), true);
        }

        [RubyMethod("syswrite")]
        public static int SysWrite(BinaryOpStorage/*!*/ writeStorage, ConversionStorage<MutableString>/*!*/ tosConversion,
            RubyContext/*!*/ context, RubyIO/*!*/ self, [NotNull]MutableString/*!*/ val) {

            // MRI warns about what its write buffer holds; read-ahead is simply given back.
            RubyBufferedStream stream = self.GetWritableStream();
            if (stream.WritePending) {
                PrintOps.ReportWarning(writeStorage, tosConversion, MutableString.CreateAscii("syswrite for buffered IO"));
            }

            // MRI's syswrite is one write(2), so a pipe that only had room for part of the string
            // reports what it took rather than waiting for the rest to fit.
            var baseStream = self.GetStream().BaseStream;
            var pipe = (ISingleWriteStream)DescriptorStream.Of(baseStream) ?? baseStream as ISingleWriteStream;
            // Windows has no O_NONBLOCK on the handle for the write to go by; io/nonblock's
            // #nonblock= remembers the flag in the IO instead (see io/nonblock.rb). It is this IO's
            // and this call's: another IO over the same pipe may be in the other mode. The pipe can
            // also be the one behind STDOUT or STDERR, redirected; a blocking syswrite there keeps
            // going through the standard stream as before.
            object nonblock;
            var windowsPipe = context.TryGetInstanceVariable(self, "@__nonblock__", out nonblock) && nonblock is bool && (bool)nonblock
                ? WindowsPipeStream.Of(baseStream) : null;
            bool nonBlocking = windowsPipe != null;
            int count = val.GetByteCount();
            if ((pipe != null || nonBlocking) && count > 0) {
                int written;
                try {
                    written = nonBlocking
                        ? windowsPipe.WriteNonBlocking(val.ToByteArray(), 0, count)
                        : pipe.WriteOnce(val.ToByteArray(), 0, count);
                } catch (IOException e) {
                    throw TranslateStreamError(e);
                }
                if (written < 0) {
                    throw NonBlockingError(self.Context, false, "write would block");
                }
                return written;
            }

            // syswrite raises Errno::EPIPE even on STDOUT (MRI signals only for buffered writes)
            return Write(self, val, false);
        }

        [RubyMethod("syswrite")]
        public static int SysWrite(BinaryOpStorage/*!*/ writeStorage, ConversionStorage<MutableString>/*!*/ tosConversion,
            RubyContext/*!*/ context, RubyIO/*!*/ self, object obj) {
            return SysWrite(writeStorage, tosConversion, context, self, Protocols.ConvertToString(tosConversion, obj));
        }

        [RubyMethod("write_nonblock")]
        public static int WriteNoBlock(RubyIO/*!*/ self, [NotNull]MutableString/*!*/ val) {
            self.RequireWritable();
            int result = -1;
            self.NonBlockingOperation(() => result = WriteOnceWithoutWaiting(self, val), false);
            return result;
        }

        /// <summary>
        /// One write(2) that does not wait for room, which is what #write_nonblock is. Only a
        /// stream the kernel knows can refuse; everything else - a regular file above all -
        /// takes the whole string, which is MRI's behaviour too.
        /// </summary>
        private static int WriteOnceWithoutWaiting(RubyIO/*!*/ io, MutableString/*!*/ val) {
            var queue = io.GetStream().BaseStream as RubyPipe;
            if (queue != null) {
                io.Flush();
                int room = val.GetByteCount();
                if (room == 0) {
                    return 0;
                }
                int put = queue.WriteWithoutWaiting(val.ToByteArray(), 0, room);
                if (put == 0) {
                    throw NonBlockingError(io.Context, false, "write would block");
                }
                return put;
            }

            var pipe = io.GetStream().BaseStream as INonBlockingWriteStream;
            if (pipe == null) {
                // MRI's write_nonblock is a bare write(2): nothing of it stays in a buffer.
                int all = Write(io, val, true);
                // The write only filled the buffer; this flush is the write(2), and a reader that
                // is gone is EPIPE (or SIGPIPE on the standard streams) here as anywhere else.
                try {
                    io.Flush();
                } catch (IOException e) {
                    throw TranslateWriteError(e, io);
                }
                return all;
            }

            int count = val.GetByteCount();
            if (count == 0) {
                return 0;
            }

            int written;
            try {
                written = pipe.WriteNonBlocking(val.ToByteArray(), 0, count);
            } catch (IOException e) {
                throw TranslateStreamError(e);
            }
            if (written < 0) {
                throw NonBlockingError(io.Context, false, "write would block");
            }
            return written;
        }

        /// <summary>
        /// The errno DescriptorStream reports back as the Ruby exception for it. Only EPIPE is
        /// worth naming: MRI raises Errno::EPIPE rather than dying of SIGPIPE.
        /// </summary>
        /// <summary>
        /// A buffered write (or flush) to STDOUT or STDERR that finds the reader gone raises
        /// SignalException SIGPIPE, not Errno::EPIPE, so that the process dies of SIGPIPE quietly
        /// the way MRI's does (its FMODE_SIGNAL_ON_EPIPE). syswrite still raises Errno::EPIPE.
        /// </summary>
        private static Exception/*!*/ TranslateWriteError(IOException/*!*/ e, RubyIO/*!*/ io) {
            if (e.HResult == DescriptorStream.EPIPE) {
                int fd;
                try {
                    fd = io.KernelDescriptor;
                } catch (Exception) {
                    fd = -1;
                }
                if (fd == 1 || fd == 2) {
                    var signal = new SignalException("SIGPIPE");
                    io.Context.SetInstanceVariable(signal, "@signo", 13);
                    io.Context.SetInstanceVariable(signal, "@signm", MutableString.CreateAscii("SIGPIPE"));
                    return signal;
                }
            }
            return TranslateStreamError(e);
        }

        private static Exception/*!*/ TranslateStreamError(IOException/*!*/ e) {
            return e.HResult == DescriptorStream.EPIPE ? new Errno.PipeError() : (Exception)e;
        }

        [RubyMethod("write_nonblock")]
        public static int WriteNoBlock(ConversionStorage<MutableString>/*!*/ tosConversion, RubyIO/*!*/ self, object obj) {
            return Write(self, Protocols.ConvertToString(tosConversion, obj), true);
        }
        
        #endregion

        #region read, sysread, read_nonblock, readpartial

        private static MutableString PrepareReadBuffer(RubyIO/*!*/ io, MutableString buffer) {
            if (buffer == null) {
                buffer = MutableString.CreateBinary();
            } else {
                buffer.Clear();
            } 
#if TODO
            var internalEncoding = io.InternalEncoding ?? io.ExternalEncoding;

            if (buffer != null) {
                buffer.Clear();
                buffer.ForceEncoding(internalEncoding);
            } else if (io.ExternalEncoding == RubyEncoding.Binary && internalEncoding == RubyEncoding.Binary) {
                buffer = MutableString.CreateBinary();
            } else {
                buffer = MutableString.CreateMutable(internalEncoding);
            }
#endif            
            return buffer;
        }

        [RubyMethod("read")]
        public static MutableString/*!*/ Read(RubyIO/*!*/ self) {
            return Read(self, null, null);
        }

        [RubyMethod("read")]
        public static MutableString/*!*/ Read(RubyIO/*!*/ self, DynamicNull bytes, [DefaultProtocol, Optional]MutableString buffer) {
            buffer = PrepareReadBuffer(self, buffer);
            self.AppendBytes(buffer, Int32.MaxValue);
            return buffer;
        }

        [RubyMethod("read")]
        public static MutableString Read(RubyIO/*!*/ self, [DefaultProtocol]int bytes, [DefaultProtocol, Optional]MutableString buffer) {
            self.RequireReadable();
            if (bytes < 0) {
                throw RubyExceptions.CreateArgumentError("negative length {0} given", bytes);
            }

            buffer = PrepareReadBuffer(self, buffer);
            int bytesRead = self.AppendBytes(buffer, bytes);
            return (bytesRead == 0 && bytes != 0) ? null : buffer;
        }

        [RubyMethod("sysread")]
        public static MutableString/*!*/ SystemRead(RubyIO/*!*/ self, [DefaultProtocol]int bytes, [DefaultProtocol, Optional]MutableString buffer) {
            if (bytes < 0) {
                throw RubyExceptions.CreateArgumentError("negative string size (or size too big)");
            }
            if (bytes == 0) {
                // MRI answers a zero-length read before it looks at the stream or the buffer.
                return buffer ?? MutableString.CreateBinary();
            }

            var stream = self.GetReadableStream();
            if (stream.DataBuffered) {
                throw RubyExceptions.CreateIOError("sysread for buffered IO");
            }

            // We use Flush to simulate non-buffered IO. 
            // A better approach would be to create a parallel FileStream with 
            // System.IO.FileOptions.WriteThrough (which corresponds to FILE_FLAG_NO_BUFFERING), and also maybe 
            // System.IO.FileOptions.SequentialScan (FILE_FLAG_SEQUENTIAL_SCAN).
            // TODO: sysopen does that?
            stream.Flush();

            var result = Read(self, bytes, buffer);
            if (result == null) {
                throw new EOFError("end of file reached");
            }
            return result;
        }

        /// <summary>
        /// The primitive under IO#readpartial: at most the requested number of bytes, and only
        /// as many as are here - it waits only when nothing is here at all. Answers nil at end
        /// of file, which the Ruby side turns into EOFError.
        /// </summary>
        [RubyMethod("__read_available__", RubyMethodAttributes.PrivateInstance)]
        public static MutableString ReadAvailable(RubyIO/*!*/ self, [DefaultProtocol]int bytes) {
            self.RequireReadable();
            if (bytes < 0) {
                throw RubyExceptions.CreateArgumentError("negative length " + bytes + " given");
            }
            var buffer = MutableString.CreateBinary();
            if (bytes == 0) {
                return buffer;
            }
            if (self.GetReadableStream().DataBuffered) {
                // MRI's buffer would hold whatever the descriptor had ready - #getc and #ungetc
                // leave only a byte or so in this one - so what is ready is read on top of it.
                // With bytes in hand this never raises "would block".
                return ReadOnceWithoutWaiting(self, bytes, buffer);
            }
            return self.AppendAvailableBytes(buffer, bytes) == 0 ? null : buffer;
        }

        [RubyMethod("read_nonblock")]
        public static MutableString ReadNoBlock(RubyIO/*!*/ self, [DefaultProtocol]int bytes, [DefaultProtocol, Optional]MutableString buffer) {
            self.RequireReadable();
            MutableString result = null;
            self.NonBlockingOperation(() => result = ReadOnceWithoutWaiting(self, bytes, buffer), true);
            if (result == null) {
                throw new EOFError("end of file reached");
            }
            return result;
        }

        /// <summary>
        /// True when bytes are sitting in this IO's own buffer. IO#wait_readable asks, because a
        /// descriptor that select(2) calls quiet is still readable when the buffer holds something.
        /// </summary>
        [RubyMethod("__data_buffered__", RubyMethodAttributes.PrivateInstance)]
        public static bool IsDataBuffered(RubyIO/*!*/ self) {
            self.RequireReadable();
            return self.GetReadableStream().DataBuffered;
        }

        /// <summary>
        /// One read(2) that does not wait for data, which is what #read_nonblock is. Buffered
        /// bytes are served first - they are already here, so the read cannot block - and a
        /// stream with no descriptor behind it reads as it would blocking.
        /// </summary>
        private static MutableString ReadOnceWithoutWaiting(RubyIO/*!*/ io, int count, MutableString buffer) {
            if (count < 0) {
                throw RubyExceptions.CreateArgumentError("negative length " + count + " given");
            }

            var stream = io.GetReadableStream();
            buffer = PrepareReadBuffer(io, buffer);
            if (count == 0) {
                return buffer;
            }

            // Bytes already in this IO's buffer come first and cannot block - after #ungetc or a
            // buffering read such as #eof? there may be some - and the descriptor is then asked
            // for the rest.
            int buffered = 0;
            if (stream.DataBuffered) {
                buffered = io.AppendAvailableBytes(buffer, count);
                count -= buffered;
                if (count == 0) {
                    return buffer;
                }
            }

            var queue = stream.BaseStream as RubyPipe;
            if (queue != null) {
                // An in-process pipe has no descriptor to poll, but it knows its own state, and
                // asking it is the only thing between #read_nonblock and a wait that would never
                // end.
                if (!queue.CanReadWithoutBlocking) {
                    if (buffered > 0) {
                        return buffer;
                    }
                    throw NonBlockingError(io.Context, true, "read would block");
                }
                int queued = io.AppendAvailableBytes(buffer, count);
                return (queued == 0 && buffered == 0) ? null : buffer;
            }

            var pipe = (INonBlockingReadStream)DescriptorStream.Of(stream.BaseStream) ?? stream.BaseStream as INonBlockingReadStream;
            if (pipe == null) {
                // No non-blocking read primitive for this stream - a socket, or something with no
                // descriptor at all.  With bytes already in hand, poll(2) decides whether to ask
                // for more: an ordinary read that would block raises EAGAIN out of here, and the
                // bytes taken out of the buffer would go with it and not be in the descriptor to
                // be read again.
                if (buffered > 0 && !IoReadiness.IsReadableNow(io, stream.BaseStream)) {
                    return buffer;
                }
                int more = io.AppendBytes(buffer, count);
                return (more == 0 && buffered == 0) ? null : buffer;
            }

            var bytes = new byte[count];
            int read = pipe.ReadNonBlocking(bytes, 0, count);
            if (read < 0) {
                // EWOULDBLOCK. Whatever came out of the buffer is the answer: raising here would
                // throw those bytes away, and they are not in the descriptor to be read again.
                if (buffered > 0) {
                    return buffer;
                }
                throw NonBlockingError(io.Context, true, "read would block");
            }
            if (read == 0) {
                return (buffered > 0) ? buffer : null;
            }
            buffer.Append(bytes, 0, read);
            return buffer;
        }

        [RubyMethod("read", RubyMethodAttributes.PublicSingleton)]
        public static MutableString/*!*/ Read(
            ConversionStorage<IDictionary<object, object>>/*!*/ toHash,
            ConversionStorage<int>/*!*/ fixnumCast,
            ConversionStorage<MutableString>/*!*/ toPath,
            RubyClass/*!*/ self,
            object path, 
            [Optional]object optionsOrLength, 
            [Optional]object optionsOrOffset,
            [DefaultParameterValue(null), DefaultProtocol]IDictionary<object, object> options) {

            Protocols.TryConvertToOptions(toHash, ref options, ref optionsOrLength, ref optionsOrOffset);
            var site = fixnumCast.GetSite(ConvertToFixnumAction.Make(fixnumCast.Context));

            int length = (optionsOrLength != Missing.Value && optionsOrLength != null) ? site.Target(site, optionsOrLength) : 0;
            int offset = (optionsOrOffset != Missing.Value && optionsOrOffset != null) ? site.Target(site, optionsOrOffset) : 0;

            if (offset < 0) {
                throw RubyExceptions.CreateEINVAL();
            }

            if (length < 0) {
                throw RubyExceptions.CreateArgumentError("negative length {0} given", length);
            }

            // TODO: options

            using (RubyIO io = new RubyFile(self.Context, self.Context.DecodePath(Protocols.CastToPath(toPath, path)), IOMode.ReadOnly)) {
                if (offset > 0) {
                    io.Seek(offset, SeekOrigin.Begin);
                }

                if (optionsOrLength != Missing.Value && optionsOrLength != null) {
                    return Read(io, length, null);
                } else {
                    return Read(io);
                }
            }
        }

        //readpartial

        #endregion

        #region readchar, readbyte (1.9), readline, readlines

        // readchar is getc with an EOFError at the end of the stream.
        [RubyMethod("readchar")]
        public static object/*!*/ ReadChar(TranscodeStorage/*!*/ transcode, TranscodeStorage/*!*/ universal, RubyIO/*!*/ self) {
            object c = Getc(transcode, universal, self);
            if (c == null) {
                throw new EOFError("end of file reached");
            }
            return c;
        }

        [RubyMethod("readbyte")]
        public static int ReadByte(RubyIO/*!*/ self) {
            int b = self.ReadByteNormalizeEoln();
            if (b == -1) {
                throw new EOFError("end of file reached");
            }
            return b;
        }

        // readline is gets with an EOFError at the end of the stream. MRI defines it in Ruby
        // (io.rb, readline(sep = $/, limit = nil, chomp: false)), so unlike gets it refuses a
        // keyword it does not know.

        [RubyMethod("readline")]
        public static MutableString/*!*/ ReadLine(TranscodeStorage/*!*/ transcode, TranscodeStorage/*!*/ universal, RubyScope/*!*/ scope, RubyIO/*!*/ self) {
            return RequireLine(Gets(transcode, universal, scope, self));
        }

        [RubyMethod("readline")]
        public static MutableString/*!*/ ReadLine(TranscodeStorage/*!*/ transcode, TranscodeStorage/*!*/ universal, ConversionStorage<MutableString>/*!*/ toStr,
            ConversionStorage<int>/*!*/ toInt, RubyScope/*!*/ scope, RubyIO/*!*/ self, params object[]/*!*/ args) {

            MutableString separator;
            int limit;
            bool chomp;
            ParseLineArguments(toStr, toInt, scope.RubyContext, args, true, out separator, out limit, out chomp);
            return RequireLine(Gets(transcode, universal, scope, self, separator, limit, chomp));
        }

        private static MutableString/*!*/ RequireLine(MutableString line) {
            if (line == null) {
                throw new EOFError("end of file reached");
            }
            return line;
        }

        // The readers convert the lines when the stream has an internal encoding or conversion
        // options, and #__readlines_chomp__ and #__each_line_chomp__ are #readlines and
        // #each_line with chomp: - both done here, a line at a time, as IO#gets does. The
        // keyword itself is taken off in ruby4.rb, by a wrapper that runs once a call: a
        // trailing Hash overload here is ambiguous against (separator, limit) for a separator
        // that only converts with #to_str.

        [RubyMethod("readlines")]
        public static RubyArray/*!*/ ReadLines(TranscodeStorage/*!*/ transcode, TranscodeStorage/*!*/ universal, RubyContext/*!*/ context, RubyIO/*!*/ self) {
            return ReadLines(transcode, universal, context, self, context.InputSeparator, -1, false);
        }

        [RubyMethod("readlines")]
        public static RubyArray/*!*/ ReadLines(TranscodeStorage/*!*/ transcode, TranscodeStorage/*!*/ universal, RubyContext/*!*/ context, RubyIO/*!*/ self, DynamicNull separator) {
            return ReadLines(transcode, universal, context, self, null, -1, false);
        }

        [RubyMethod("readlines")]
        public static RubyArray/*!*/ ReadLines(TranscodeStorage/*!*/ transcode, TranscodeStorage/*!*/ universal, RubyContext/*!*/ context, RubyIO/*!*/ self, [DefaultProtocol, NotNull]Union<MutableString, int> separatorOrLimit) {
            return ReadLines(transcode, universal, context, self, separatorOrLimit, false);
        }

        [RubyMethod("readlines")]
        public static RubyArray/*!*/ ReadLines(TranscodeStorage/*!*/ transcode, TranscodeStorage/*!*/ universal, RubyContext/*!*/ context, RubyIO/*!*/ self, [DefaultProtocol]MutableString separator, DynamicNull limit) {
            return ReadLines(transcode, universal, context, self, separator, -1, false);
        }

        [RubyMethod("readlines")]
        public static RubyArray/*!*/ ReadLines(TranscodeStorage/*!*/ transcode, TranscodeStorage/*!*/ universal, RubyContext/*!*/ context, RubyIO/*!*/ self, [DefaultProtocol]MutableString separator, [DefaultProtocol]int limit) {
            return ReadLines(transcode, universal, context, self, separator, limit, false);
        }

        [RubyMethod("__readlines_chomp__", RubyMethodAttributes.PrivateInstance)]
        public static RubyArray/*!*/ ReadLinesChomp(TranscodeStorage/*!*/ transcode, TranscodeStorage/*!*/ universal, RubyContext/*!*/ context, RubyIO/*!*/ self) {
            return ReadLines(transcode, universal, context, self, context.InputSeparator, -1, true);
        }

        [RubyMethod("__readlines_chomp__", RubyMethodAttributes.PrivateInstance)]
        public static RubyArray/*!*/ ReadLinesChomp(TranscodeStorage/*!*/ transcode, TranscodeStorage/*!*/ universal, RubyContext/*!*/ context, RubyIO/*!*/ self, DynamicNull separator) {
            return ReadLines(transcode, universal, context, self, null, -1, true);
        }

        [RubyMethod("__readlines_chomp__", RubyMethodAttributes.PrivateInstance)]
        public static RubyArray/*!*/ ReadLinesChomp(TranscodeStorage/*!*/ transcode, TranscodeStorage/*!*/ universal, RubyContext/*!*/ context, RubyIO/*!*/ self, [DefaultProtocol, NotNull]Union<MutableString, int> separatorOrLimit) {
            return ReadLines(transcode, universal, context, self, separatorOrLimit, true);
        }

        [RubyMethod("__readlines_chomp__", RubyMethodAttributes.PrivateInstance)]
        public static RubyArray/*!*/ ReadLinesChomp(TranscodeStorage/*!*/ transcode, TranscodeStorage/*!*/ universal, RubyContext/*!*/ context, RubyIO/*!*/ self, [DefaultProtocol]MutableString separator, DynamicNull limit) {
            return ReadLines(transcode, universal, context, self, separator, -1, true);
        }

        [RubyMethod("__readlines_chomp__", RubyMethodAttributes.PrivateInstance)]
        public static RubyArray/*!*/ ReadLinesChomp(TranscodeStorage/*!*/ transcode, TranscodeStorage/*!*/ universal, RubyContext/*!*/ context, RubyIO/*!*/ self, [DefaultProtocol]MutableString separator, [DefaultProtocol]int limit) {
            return ReadLines(transcode, universal, context, self, separator, limit, true);
        }

        /// <summary>
        /// A limit of no bytes would read an empty line for ever, so a reader that loops refuses
        /// it - naming itself in the message, as MRI does.
        /// </summary>
        private static void CheckLineLimit(int limit, string/*!*/ methodName) {
            if (limit == 0) {
                throw RubyExceptions.CreateArgumentError("invalid limit: 0 for {0}", methodName);
            }
        }

        private static RubyArray/*!*/ ReadLines(TranscodeStorage/*!*/ transcode, TranscodeStorage/*!*/ universal, RubyContext/*!*/ context, RubyIO/*!*/ self,
            Union<MutableString, int> separatorOrLimit, bool chomp) {
            return separatorOrLimit.IsFixnum()
                ? ReadLines(transcode, universal, context, self, context.InputSeparator, separatorOrLimit.Fixnum(), chomp)
                : ReadLines(transcode, universal, context, self, separatorOrLimit.String(), -1, chomp);
        }

        private static RubyArray/*!*/ ReadLines(TranscodeStorage/*!*/ transcode, TranscodeStorage/*!*/ universal, RubyContext/*!*/ context, RubyIO/*!*/ self,
            MutableString separator, int limit, bool chomp) {
            CheckLineLimit(limit, "readlines");
            RubyArray result = new RubyArray();

            // no dynamic call, doesn't modify $_ scope variable:
            bool universalNewline = IsUniversalNewline(universal, self);
            MutableString line;
            while ((line = ReadLineOrParagraph(self, universalNewline, separator, limit)) != null) {
                if (CountsAsLine(line, separator, limit)) {
                    self.LineNumber++;
                }
                result.Add(FinishLine(transcode, self, line, separator, chomp));
            }

            context.InputProvider.LastInputLineNumber = self.LineNumber;
            return result;
        }

        /// <summary>
        /// A stream opened for reading in text mode ("rt", textmode: true) reads with MRI's
        /// universal newline decorator, as if universal_newline: true had been given - unless
        /// the options already say what to do with newlines.
        /// </summary>
        internal static IDictionary<object, object> AddTextModeDecorator(RubyContext/*!*/ context, IOInfo info,
            IDictionary<object, object> options) {

            if (!info.IsTextModeSpecified || !info.Mode.CanRead()) {
                return options;
            }

            var result = new Hash(context);
            if (options != null) {
                foreach (var name in new[] { "newline", "universal_newline", "crlf_newline", "cr_newline", "lf_newline" }) {
                    if (options.ContainsKey(context.CreateAsciiSymbol(name))) {
                        return options;
                    }
                }
                foreach (var entry in options) {
                    result[entry.Key] = entry.Value;
                }
            }
            result[context.CreateAsciiSymbol("universal_newline")] = ScriptingRuntimeHelpers.True;
            return result;
        }

        /// <summary>
        /// Whether the stream reads "\r\n" and a lone "\r" as "\n" (IO#__universal_newline__? in
        /// ruby4.rb, which knows the decorators and binmode). Only a stream with conversion
        /// options can, so nothing else pays for asking.
        /// </summary>
        private static bool IsUniversalNewline(TranscodeStorage/*!*/ storage, RubyIO/*!*/ self) {
            if (self.ConversionOptions == null) {
                return false;
            }
            var site = storage.GetCallSite("__universal_newline__?", new RubyCallSignature(1, RubyCallFlags.HasImplicitSelf));
            return RubyOps.IsTrue(site.Target(site, self, null));
        }

        // One byte as a universal newline stream reads it: "\r\n" and a lone "\r" are "\n".
        private static int ReadByteUniversal(RubyIO/*!*/ self) {
            int b = self.ReadByteNormalizeEoln();
            if (b == '\r') {
                if (self.PeekByteNormalizeEoln() == '\n') {
                    self.ReadByteNormalizeEoln();
                }
                return '\n';
            }
            return b;
        }

        /// <summary>
        /// The line reader of a universal newline stream. The newlines have to be folded before
        /// the separator is looked for - "a\rb" is two lines - so this reads a byte at a time;
        /// only streams that asked for the decorator come here. The separator, the limit and
        /// paragraph mode (an empty separator: a blank line ends it, and the newlines around it
        /// are skipped) mean what they do for any other stream.
        /// </summary>
        private static MutableString ReadLineUniversal(RubyIO/*!*/ self, MutableString separator, int limit) {
            self.RequireReadable();
            if (limit == 0) {
                return MutableString.CreateEmpty(self.ExternalEncoding);
            }

            bool paragraph = separator != null && separator.IsEmpty;
            byte[] sep = (separator == null) ? null : paragraph ? new byte[] { (byte)'\n', (byte)'\n' } : separator.ToByteArray();
            if (paragraph) {
                SkipNewlinesUniversal(self);
            }

            var bytes = new List<byte>();
            int b;
            bool atSeparator = false;
            while ((b = ReadByteUniversal(self)) != -1) {
                bytes.Add((byte)b);
                if (sep != null && EndsWith(bytes, sep)) {
                    atSeparator = true;
                    break;
                }
                if (limit > 0 && bytes.Count >= limit) {
                    break;
                }
            }

            if (bytes.Count == 0) {
                return null;
            }
            if (paragraph && atSeparator) {
                SkipNewlinesUniversal(self);
            }
            return MutableString.CreateBinary(bytes.ToArray(), self.ExternalEncoding);
        }

        private static void SkipNewlinesUniversal(RubyIO/*!*/ self) {
            int next;
            while ((next = self.PeekByteNormalizeEoln()) == '\n' || next == '\r') {
                ReadByteUniversal(self);
            }
        }

        private static bool EndsWith(List<byte>/*!*/ bytes, byte[]/*!*/ suffix) {
            int start = bytes.Count - suffix.Length;
            if (start < 0 || suffix.Length == 0) {
                return false;
            }
            for (int i = 0; i < suffix.Length; i++) {
                if (bytes[start + i] != suffix[i]) {
                    return false;
                }
            }
            return true;
        }

        private static MutableString ReadLineOrParagraph(RubyIO/*!*/ self, bool universal, MutableString separator, int limit) {
            return universal ? ReadLineUniversal(self, separator, limit) : self.ReadLineOrParagraph(separator, limit);
        }

        /// <summary>
        /// What every line reader does to a line it has read: it is tainted, converted when the
        /// stream has an internal encoding or conversion options (the conversion is Ruby code,
        /// IO#__transcode__ in ruby4.rb), and chomped when chomp: was given.
        /// </summary>
        private static MutableString/*!*/ FinishLine(TranscodeStorage/*!*/ transcode, RubyIO/*!*/ self, MutableString/*!*/ line,
            MutableString separator, bool chomp) {
            line.IsTainted = true;
            if (self.Enc2 != null || self.ConversionOptions != null) {
                var site = transcode.GetCallSite("__transcode__", new RubyCallSignature(1, RubyCallFlags.HasImplicitSelf));
                line = site.Target(site, self, line) as MutableString ?? line;
            }
            if (chomp) {
                ChompLine(line, separator);
            }
            return line;
        }

        // TODO: to_hash, to_str, to_int

        // The name goes through #to_path, and the argument after it is a separator or a byte
        // limit - the same overload set as the instance method, which is what the shared
        // io_readlines_options_19 examples ask for.

        [RubyMethod("readlines", RubyMethodAttributes.PublicSingleton)]
        public static RubyArray/*!*/ ReadLines(TranscodeStorage/*!*/ transcode, TranscodeStorage/*!*/ universal, ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object path) {
            return ReadLines(transcode, universal, toPath, self, path, self.Context.InputSeparator, -1);
        }

        [RubyMethod("readlines", RubyMethodAttributes.PublicSingleton)]
        public static RubyArray/*!*/ ReadLines(TranscodeStorage/*!*/ transcode, TranscodeStorage/*!*/ universal, ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object path, DynamicNull separator) {
            return ReadLines(transcode, universal, toPath, self, path, null, -1);
        }

        [RubyMethod("readlines", RubyMethodAttributes.PublicSingleton)]
        public static RubyArray/*!*/ ReadLines(TranscodeStorage/*!*/ transcode, TranscodeStorage/*!*/ universal, ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object path,
            [DefaultProtocol, NotNull]Union<MutableString, int> separatorOrLimit) {

            return separatorOrLimit.IsFixnum()
                ? ReadLines(transcode, universal, toPath, self, path, self.Context.InputSeparator, separatorOrLimit.Fixnum())
                : ReadLines(transcode, universal, toPath, self, path, separatorOrLimit.String(), -1);
        }

        [RubyMethod("readlines", RubyMethodAttributes.PublicSingleton)]
        public static RubyArray/*!*/ ReadLines(TranscodeStorage/*!*/ transcode, TranscodeStorage/*!*/ universal, ConversionStorage<MutableString>/*!*/ toPath, RubyClass/*!*/ self, object path,
            [DefaultProtocol]MutableString separator, [DefaultProtocol]int limit) {

            MutableString pathString = Protocols.CastToPath(toPath, path);
            using (RubyIO io = new RubyIO(self.Context, self.Context.Platform.OpenInputFileStream(pathString.ConvertToString()), IOMode.ReadOnly)) {
                return ReadLines(transcode, universal, self.Context, io, separator, limit, false);
            }
        }

        #endregion

        #region getc, gets, ungetc, getbyte (1.9)

        /// <summary>
        /// One character, as a String in the external encoding, reading as many bytes as the
        /// character needs - and converted, when the stream has an internal encoding or conversion
        /// options (IO#__transcode__ in ruby4.rb).
        /// </summary>
        [RubyMethod("getc")]
        public static object Getc(TranscodeStorage/*!*/ transcode, TranscodeStorage/*!*/ universal, RubyIO/*!*/ self) {
            int first = self.ReadByteNormalizeEoln();
            if (first == -1) {
                return null;
            }

            RubyEncoding encoding = GetExternalEncoding(self) ?? self.Context.DefaultExternalEncoding;
            // A universal newline stream reads "\r\n" as one "\n", not two characters.
            if (first == '\r' && encoding.IsAsciiIdentity && IsUniversalNewline(universal, self)
                && self.PeekByteNormalizeEoln() == '\n') {
                first = self.ReadByteNormalizeEoln();
            }
            MutableString c = ReadCharacter(self, (byte)first, encoding);
            if (self.Enc2 != null || self.ConversionOptions != null) {
                var site = transcode.GetCallSite("__transcode__", new RubyCallSignature(1, RubyCallFlags.HasImplicitSelf));
                return site.Target(site, self, c);
            }
            return c;
        }

        /// <summary>
        /// The character that starts with <paramref name="first"/>, as MRI's io_getc measures it.
        /// An ASCII byte in an ASCII-compatible encoding is a character of its own, as every byte
        /// is in BINARY. In UTF-8 a well-formed sequence (Unicode's table 3-7) is one character, a
        /// sequence cut short by the end of the stream is one too, and a byte that does not
        /// continue the sequence leaves the lead byte alone, a character of its own. Elsewhere
        /// the shortest valid run of up to four bytes is the character, and the lead byte alone
        /// when there is none. Bytes read past the character are pushed back.
        /// </summary>
        private static MutableString/*!*/ ReadCharacter(RubyIO/*!*/ self, byte first, RubyEncoding/*!*/ encoding) {
            if ((first < 0x80 && encoding.IsAsciiIdentity) || encoding == RubyEncoding.Binary) {
                return MutableString.CreateBinary(new byte[] { first }, encoding);
            }

            byte[] bytes = new byte[4];
            bytes[0] = first;
            int count = 1;

            if (encoding == RubyEncoding.UTF8) {
                int length;
                byte low = 0x80, high = 0xBF;
                if (first >= 0xC2 && first <= 0xDF) {
                    length = 2;
                } else if (first >= 0xE0 && first <= 0xEF) {
                    length = 3;
                    if (first == 0xE0) {
                        low = 0xA0;
                    } else if (first == 0xED) {
                        high = 0x9F;
                    }
                } else if (first >= 0xF0 && first <= 0xF4) {
                    length = 4;
                    if (first == 0xF0) {
                        low = 0x90;
                    } else if (first == 0xF4) {
                        high = 0x8F;
                    }
                } else {
                    length = 1;
                }

                while (count < length) {
                    int next = self.ReadByteNormalizeEoln();
                    if (next == -1) {
                        break;
                    }
                    if (next < low || next > high) {
                        self.PushBack((byte)next);
                        PushBack(self, bytes, 1, count);
                        count = 1;
                        break;
                    }
                    bytes[count++] = (byte)next;
                    low = 0x80;
                    high = 0xBF;
                }
                return MutableString.CreateBinary(Slice(bytes, count), encoding);
            }

            while (true) {
                MutableString candidate = MutableString.CreateBinary(Slice(bytes, count), encoding);
                if (!candidate.ContainsInvalidCharacters()) {
                    return candidate;
                }
                int next = (count < bytes.Length) ? self.ReadByteNormalizeEoln() : -1;
                if (next == -1) {
                    PushBack(self, bytes, 1, count);
                    return MutableString.CreateBinary(new byte[] { first }, encoding);
                }
                bytes[count++] = (byte)next;
            }
        }

        private static byte[]/*!*/ Slice(byte[]/*!*/ bytes, int count) {
            if (count == bytes.Length) {
                return bytes;
            }
            byte[] result = new byte[count];
            Buffer.BlockCopy(bytes, 0, result, 0, count);
            return result;
        }

        // Pushes bytes[start..end) back so that bytes[start] is the next one read.
        private static void PushBack(RubyIO/*!*/ self, byte[]/*!*/ bytes, int start, int end) {
            for (int i = end - 1; i >= start; i--) {
                self.PushBack(bytes[i]);
            }
        }

        [RubyMethod("getbyte")]
        public static object GetByte(RubyIO/*!*/ self) {
            int b = self.ReadByteNormalizeEoln();
            return (b != -1) ? ScriptingRuntimeHelpers.Int32ToObject(b) : null;
        }

        [RubyMethod("each_char")]
        public static object EachChar(TranscodeStorage/*!*/ transcode, TranscodeStorage/*!*/ universal, BlockParam block, RubyIO/*!*/ self) {
            // MRI hands out the enumerator before it looks at the stream, closed or not.
            if (block == null) {
                return new Enumerator(self, "each_char") { SizeOp = "none" };
            }
            self.RequireReadable();

            object c;
            while ((c = Getc(transcode, universal, self)) != null) {
                object result;
                if (block.Yield(c, out result)) {
                    return result;
                }
            }
            return self;
        }

        // IO#gets and IO#readline take a chomp: keyword, and they read into $_ of the method
        // that called them - which is why the keyword and the transcoding are handled here
        // rather than in a Ruby wrapper, whose own frame would get $_ instead. Keyword
        // arguments arrive as a trailing Hash flagged IsKeywordArguments. Everything but the
        // bare call is parsed by hand, as MRI's rb_io_getline_args does: a set of typed
        // overloads with and without a trailing Hash was ambiguous for a separator that only
        // converts with #to_str, and gave the wrong error for three positional arguments.

        [RubyMethod("gets")]
        public static MutableString Gets(TranscodeStorage/*!*/ transcode, TranscodeStorage/*!*/ universal, RubyScope/*!*/ scope, RubyIO/*!*/ self) {
            return Gets(transcode, universal, scope, self, scope.RubyContext.InputSeparator, -1, false);
        }

        [RubyMethod("gets")]
        public static MutableString Gets(TranscodeStorage/*!*/ transcode, TranscodeStorage/*!*/ universal, ConversionStorage<MutableString>/*!*/ toStr,
            ConversionStorage<int>/*!*/ toInt, RubyScope/*!*/ scope, RubyIO/*!*/ self, params object[]/*!*/ args) {

            MutableString separator;
            int limit;
            bool chomp;
            ParseLineArguments(toStr, toInt, scope.RubyContext, args, false, out separator, out limit, out chomp);
            return Gets(transcode, universal, scope, self, separator, limit, chomp);
        }

        private static MutableString Gets(TranscodeStorage/*!*/ transcode, TranscodeStorage/*!*/ universal, RubyScope/*!*/ scope, RubyIO/*!*/ self,
            MutableString separator, int limit, bool chomp) {

            MutableString result = ReadLineOrParagraph(self, IsUniversalNewline(universal, self), separator, limit);
            if (result != null) {
                if (CountsAsLine(result, separator, limit)) {
                    self.LineNumber++;
                }
                result = FinishLine(transcode, self, result, separator, chomp);
            }

            scope.GetInnerMostClosureScope().LastInputLine = result;
            scope.RubyContext.InputProvider.LastInputLineNumber = self.LineNumber;

            return result;
        }

        /// <summary>
        /// The arguments of the line readers, as rb_io_getline_args reads them: with one, nil is
        /// "no separator", anything that converts with #to_str a separator, and anything else a
        /// limit (#to_int); with two, a separator (nil or #to_str) and a limit (nil or #to_int).
        /// A negative limit is no limit. A trailing keyword Hash holds chomp:; gets lets other
        /// keywords through, readline (Ruby code in MRI) refuses them. A Hash passed positionally
        /// is just an argument, and not a usable one.
        /// </summary>
        private static void ParseLineArguments(ConversionStorage<MutableString>/*!*/ toStr, ConversionStorage<int>/*!*/ toInt,
            RubyContext/*!*/ context, object[]/*!*/ args, bool strictKeywords,
            out MutableString separator, out int limit, out bool chomp) {

            int count = args.Length;
            var options = (count > 0) ? args[count - 1] as Hash : null;
            if (options != null && options.IsKeywordArguments) {
                count--;
            } else {
                options = null;
            }

            if (count > 2) {
                throw RubyExceptions.CreateArgumentError("wrong number of arguments (given {0}, expected 0..2)", count);
            }

            separator = context.InputSeparator;
            limit = -1;
            if (count == 1) {
                object arg = args[0];
                if (arg == null) {
                    separator = null;
                } else {
                    MutableString str = Protocols.TryCastToString(toStr, arg);
                    if (str != null) {
                        separator = str;
                    } else {
                        limit = Protocols.CastToFixnum(toInt, arg);
                    }
                }
            } else if (count == 2) {
                separator = (args[0] != null) ? Protocols.CastToString(toStr, args[0]) : null;
                if (args[1] != null) {
                    limit = Protocols.CastToFixnum(toInt, args[1]);
                }
            }
            if (limit < 0) {
                limit = -1;
            }

            chomp = false;
            if (options != null) {
                var chompKey = context.CreateAsciiSymbol("chomp");
                List<string> unknown = null;
                foreach (var entry in options) {
                    if (chompKey.Equals(entry.Key)) {
                        chomp = RubyOps.IsTrue(entry.Value);
                    } else if (strictKeywords) {
                        (unknown ?? (unknown = new List<string>())).Add(context.Inspect(entry.Key).ToString());
                    }
                }
                if (unknown != null) {
                    throw RubyExceptions.CreateArgumentError(
                        (unknown.Count == 1 ? "unknown keyword: " : "unknown keywords: ") + String.Join(", ", unknown));
                }
            }
        }

        /// <summary>
        /// MRI's rb_io_getline_0 counts a read as a line unless it stopped because the limit ran
        /// out: a read that reached the separator or the end of the stream first is a line, and
        /// so is one whose limit ran out exactly at the separator. A zero limit reads nothing and
        /// counts nothing. The line is looked at before it is chomped.
        /// </summary>
        private static bool CountsAsLine(MutableString/*!*/ line, MutableString separator, int limit) {
            if (limit < 0) {
                return true;
            }
            if (limit == 0) {
                return false;
            }
            int count = line.GetByteCount();
            if (count < limit) {
                return true;
            }
            if (separator == null) {
                return false;
            }

            int sepLength = separator.GetByteCount();
            if (sepLength == 0) {
                // a paragraph ends at a blank line
                return count >= 2 && line.GetByte(count - 1) == '\n' && line.GetByte(count - 2) == '\n';
            }
            if (count < sepLength) {
                return false;
            }
            for (int i = 0; i < sepLength; i++) {
                if (line.GetByte(count - sepLength + i) != separator.GetByte(i)) {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// chomp: removes the separator the read actually stopped at. A nil separator reads
        /// everything and leaves nothing to remove; the default "\n" takes a "\r" before it too;
        /// a paragraph (empty separator) loses the run of newlines that ended it, but the last
        /// paragraph of a file ends at end of file, not at a separator, and keeps its newline.
        /// </summary>
        internal static void ChompLine(MutableString/*!*/ line, MutableString separator) {
            if (separator == null) {
                return;
            }

            int count = line.GetByteCount();
            int sepLength = separator.GetByteCount();
            if (sepLength == 0) {
                if (count >= 2 && line.GetByte(count - 1) == '\n' && line.GetByte(count - 2) == '\n') {
                    while (count > 0 && line.GetByte(count - 1) == '\n') {
                        count--;
                    }
                    line.SetByteCount(count);
                }
                return;
            }

            if (count < sepLength) {
                return;
            }
            for (int i = 0; i < sepLength; i++) {
                if (line.GetByte(count - sepLength + i) != separator.GetByte(i)) {
                    return;
                }
            }
            count -= sepLength;
            if (sepLength == 1 && separator.GetByte(0) == '\n' && count > 0 && line.GetByte(count - 1) == '\r') {
                count--;
            }
            line.SetByteCount(count);
        }

        // The byte pusher the prelude's #ungetc and #ungetbyte are written in terms of.
        [RubyMethod("__ir_ungetc__", RubyMethodAttributes.PrivateInstance)]
        public static void SetPreviousByte(RubyIO/*!*/ self, [DefaultProtocol]int b) {
            self.RequireReadable();
            self.PushBack(unchecked((byte)b));
        }

        /// <summary>
        /// ungetc pushes a String's bytes back, and takes an Integer as a codepoint in the stream's
        /// external encoding. A String, and an ASCII codepoint in an ASCII-compatible encoding,
        /// are pushed here; anything else - a longer character, an object that converts with
        /// #to_str, nil - is IO#__ungetc_rest__ in ruby4.rb.
        /// </summary>
        [RubyMethod("ungetc")]
        public static object PushBack(CallSiteStorage<Func<CallSite, object, object, object>>/*!*/ rest, RubyIO/*!*/ self, object value) {
            self.RequireReadable();

            var str = value as MutableString;
            if (str != null) {
                for (int i = str.GetByteCount() - 1; i >= 0; i--) {
                    self.PushBack(str.GetByte(i));
                }
                return null;
            }

            if (value is int) {
                int codepoint = (int)value;
                RubyEncoding encoding = GetExternalEncoding(self) ?? self.Context.DefaultExternalEncoding;
                if (codepoint >= 0 && codepoint < 0x80 && encoding.IsAsciiIdentity) {
                    self.PushBack((byte)codepoint);
                    return null;
                }
            }

            var site = rest.GetCallSite("__ungetc_rest__", new RubyCallSignature(1, RubyCallFlags.HasImplicitSelf));
            return site.Target(site, self, value);
        }

        // TODO: 1.9 only
        // getbyte

        #endregion

        #region foreach, each, each_byte, each_line

        // The reading is Ruby code (IO.__ir_foreach__ in ruby4.rb: it opens the file with the
        // mode: option and reads through #each_line). What is left here is $_: MRI's foreach
        // reads into its caller's, so once the block has had every line it is nil.
        [RubyMethod("foreach", RubyMethodAttributes.PublicSingleton)]
        public static object ForEach(CallSiteStorage<Func<CallSite, object, Proc, object, object, object, object>>/*!*/ storage,
            RubyScope/*!*/ scope, BlockParam block, RubyClass/*!*/ self, params object[]/*!*/ args) {

            object options = null;
            int count = args.Length;
            var hash = count > 0 ? args[count - 1] as Hash : null;
            if (hash != null && hash.IsKeywordArguments) {
                options = hash;
                count--;
            }
            if (count == 0) {
                throw RubyExceptions.CreateArgumentError("wrong number of arguments (given 0, expected 1..3)");
            }

            var rest = new RubyArray();
            for (int i = 1; i < count; i++) {
                rest.Add(args[i]);
            }

            var site = storage.GetCallSite("__ir_foreach__", new RubyCallSignature(3, RubyCallFlags.HasImplicitSelf | RubyCallFlags.HasBlock));
            object result = site.Target(site, self, block != null ? block.Proc : null, args[0], rest, options);
            if (block != null && !(result is BlockReturnResult)) {
                scope.GetInnerMostClosureScope().LastInputLine = null;
            }
            return result;
        }

        [RubyMethod("each")]
        [RubyMethod("each_line")]
        public static object Each(TranscodeStorage/*!*/ transcode, TranscodeStorage/*!*/ universal, RubyContext/*!*/ context, BlockParam block, RubyIO/*!*/ self) {
            return Each(transcode, universal, context, block, self, context.InputSeparator, -1, false);
        }

        [RubyMethod("each")]
        [RubyMethod("each_line")]
        public static object Each(TranscodeStorage/*!*/ transcode, TranscodeStorage/*!*/ universal, RubyContext/*!*/ context, BlockParam block, RubyIO/*!*/ self, DynamicNull separator) {
            return Each(transcode, universal, context, block, self, null, -1, false);
        }

        [RubyMethod("each")]
        [RubyMethod("each_line")]
        public static object Each(TranscodeStorage/*!*/ transcode, TranscodeStorage/*!*/ universal, RubyContext/*!*/ context, BlockParam block, RubyIO/*!*/ self, [DefaultProtocol, NotNull]Union<MutableString, int> separatorOrLimit) {
            return Each(transcode, universal, context, block, self, separatorOrLimit, false);
        }

        [RubyMethod("each")]
        [RubyMethod("each_line")]
        public static object Each(TranscodeStorage/*!*/ transcode, TranscodeStorage/*!*/ universal, RubyContext/*!*/ context, BlockParam block, RubyIO/*!*/ self, [DefaultProtocol]MutableString separator, DynamicNull limit) {
            return Each(transcode, universal, context, block, self, separator, -1, false);
        }

        [RubyMethod("each")]
        [RubyMethod("each_line")]
        public static object Each(TranscodeStorage/*!*/ transcode, TranscodeStorage/*!*/ universal, RubyContext/*!*/ context, BlockParam block, RubyIO/*!*/ self, [DefaultProtocol]MutableString separator, [DefaultProtocol]int limit) {
            return Each(transcode, universal, context, block, self, separator, limit, false);
        }

        [RubyMethod("__each_line_chomp__", RubyMethodAttributes.PrivateInstance)]
        public static object EachLineChomp(TranscodeStorage/*!*/ transcode, TranscodeStorage/*!*/ universal, RubyContext/*!*/ context, BlockParam block, RubyIO/*!*/ self) {
            return Each(transcode, universal, context, block, self, context.InputSeparator, -1, true);
        }

        [RubyMethod("__each_line_chomp__", RubyMethodAttributes.PrivateInstance)]
        public static object EachLineChomp(TranscodeStorage/*!*/ transcode, TranscodeStorage/*!*/ universal, RubyContext/*!*/ context, BlockParam block, RubyIO/*!*/ self, DynamicNull separator) {
            return Each(transcode, universal, context, block, self, null, -1, true);
        }

        [RubyMethod("__each_line_chomp__", RubyMethodAttributes.PrivateInstance)]
        public static object EachLineChomp(TranscodeStorage/*!*/ transcode, TranscodeStorage/*!*/ universal, RubyContext/*!*/ context, BlockParam block, RubyIO/*!*/ self, [DefaultProtocol, NotNull]Union<MutableString, int> separatorOrLimit) {
            return Each(transcode, universal, context, block, self, separatorOrLimit, true);
        }

        [RubyMethod("__each_line_chomp__", RubyMethodAttributes.PrivateInstance)]
        public static object EachLineChomp(TranscodeStorage/*!*/ transcode, TranscodeStorage/*!*/ universal, RubyContext/*!*/ context, BlockParam block, RubyIO/*!*/ self, [DefaultProtocol]MutableString separator, DynamicNull limit) {
            return Each(transcode, universal, context, block, self, separator, -1, true);
        }

        [RubyMethod("__each_line_chomp__", RubyMethodAttributes.PrivateInstance)]
        public static object EachLineChomp(TranscodeStorage/*!*/ transcode, TranscodeStorage/*!*/ universal, RubyContext/*!*/ context, BlockParam block, RubyIO/*!*/ self, [DefaultProtocol]MutableString separator, [DefaultProtocol]int limit) {
            return Each(transcode, universal, context, block, self, separator, limit, true);
        }

        private static object Each(TranscodeStorage/*!*/ transcode, TranscodeStorage/*!*/ universal, RubyContext/*!*/ context, BlockParam block, RubyIO/*!*/ self, Union<MutableString, int> separatorOrLimit, bool chomp) {
            return separatorOrLimit.IsFixnum()
                ? Each(transcode, universal, context, block, self, context.InputSeparator, separatorOrLimit.Fixnum(), chomp)
                : Each(transcode, universal, context, block, self, separatorOrLimit.String(), -1, chomp);
        }

        private static object Each(TranscodeStorage/*!*/ transcode, TranscodeStorage/*!*/ universal, RubyContext/*!*/ context, BlockParam block, RubyIO/*!*/ self, MutableString separator, int limit, bool chomp) {
            self.RequireReadable();
            CheckLineLimit(limit, "each_line");

            bool universalNewline = IsUniversalNewline(universal, self);
            MutableString line;
            while ((line = ReadLineOrParagraph(self, universalNewline, separator, limit)) != null) {
                if (block == null) {
                    throw RubyExceptions.NoBlockGiven();
                }

                // The line number moves before the conversion, which is Ruby code and may look.
                if (CountsAsLine(line, separator, limit)) {
                    self.LineNumber++;
                }
                context.InputProvider.LastInputLineNumber = self.LineNumber;
                line = FinishLine(transcode, self, line, separator, chomp);

                object result;
                if (block.Yield(line, out result)) {
                    return result;
                }
            }

            return self;
        }

        [RubyMethod("each_byte")]
        public static object EachByte(BlockParam block, RubyIO/*!*/ self) {
            self.RequireReadable();

            if (block == null) {
                // Without a block this is an enumerator over the bytes still to come. It has no
                // size: a stream does not say how much of it is left.
                return new Enumerator(self, "each_byte") { SizeOp = "none" };
            }

            int aByte;
            while ((aByte = self.ReadByteNormalizeEoln()) != -1) {
                object result;
                if (block.Yield(ScriptingRuntimeHelpers.Int32ToObject(aByte), out result)) {
                    return result;
                }
            }
            return self;
        }

        #endregion

        #region copy_stream

        [RubyMethod("copy_stream", RubyMethodAttributes.PublicSingleton)]
        public static object CopyStream(
            ConversionStorage<MutableString>/*!*/ toPath, ConversionStorage<int>/*!*/ toInt, RespondToStorage/*!*/ respondTo,
            BinaryOpStorage/*!*/ writeStorage, CallSiteStorage<Func<CallSite, object, object, object, object>>/*!*/ readStorage,
            CallSiteStorage<Func<CallSite, object, object, object, object>>/*!*/ readPartialStorage,
            CallSiteStorage<Func<CallSite, object, object>>/*!*/ toIoStorage,
            RubyClass/*!*/ self, object src, object dst, [DefaultParameterValue(null)]object copyLength, [DefaultParameterValue(null)]object srcOffset) {

            // Both are Integers by #to_int, nil standing for "all of it" / "where the source is",
            // and both are converted before either end is touched.
            int count = (copyLength != null) ? Protocols.CastToFixnum(toInt, copyLength) : -1;
            int src_offset = (srcOffset != null) ? Protocols.CastToFixnum(toInt, srcOffset) : -1;

            if (count < -1) {
                throw RubyExceptions.CreateArgumentError("count should be >= -1");
            }

            if (src_offset < -1) {
                throw RubyExceptions.CreateArgumentError("src_offset should be >= -1");
            }

            var context = toPath.Context;
            Stream srcStream = null, dstStream = null;
            // only a stream opened here from a path is closed here; an IO passed in stays open
            bool ownsSrc = false, ownsDst = false;
            CallSite<Func<CallSite, object, object, object>> writeSite = null;
            CallSite<Func<CallSite, object, object, object, object>> readSite = null;
            bool readPartial = false;
            long restorePosition = -1;

            try {
                // Each side on its own: an IO (or what #to_io gives, as for a Tempfile) is read or
                // written as that IO - a File is not reopened by its path, which used to truncate and
                // overwrite a file opened only for reading - and anything else is a path or an object
                // with #readpartial / #read / #write.
                var toPathSite = toPath.GetSite(TryConvertToPathAction.Make(toPath.Context));
                RubyIO srcIO = TryToIO(respondTo, toIoStorage, context, src, false);
                if (srcIO != null) {
                    srcStream = srcIO.GetReadableStream();
                } else {
                    var srcPath = toPathSite.Target(toPathSite, src);
                    if (srcPath != null) {
                        srcStream = self.Context.Platform.OpenInputFileStream(context.DecodePath(srcPath), FileMode.Open, FileAccess.Read, FileShare.Read);
                        ownsSrc = true;
                    } else if (Protocols.RespondTo(respondTo, src, "readpartial")) {
                        // MRI prefers #readpartial, which signals the end with EOFError
                        readSite = readPartialStorage.GetCallSite("readpartial", 2);
                        readPartial = true;
                    } else {
                        readSite = readStorage.GetCallSite("read", 2);
                    }
                }

                RubyIO dstIO = TryToIO(respondTo, toIoStorage, context, dst, false);
                if (dstIO != null) {
                    dstStream = dstIO.GetWritableStream();
                } else {
                    var dstPath = toPathSite.Target(toPathSite, dst);
                    if (dstPath != null) {
                        // Create, not Truncate: MRI's copy_stream makes the destination when it is not there.
                        dstStream = self.Context.Platform.OpenInputFileStream(context.DecodePath(dstPath), FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
                        ownsDst = true;
                    } else {
                        writeSite = writeStorage.GetCallSite("write", 1);
                    }
                }

                if (src_offset != -1) {
                    if (srcStream == null) {
                        throw RubyExceptions.CreateArgumentError("cannot specify src_offset for non-IO");
                    }
                    if (!srcStream.CanSeek) {
                        throw new Errno.InvalidSeekError("pread");
                    }
                    // MRI preads from the offset: the source IO's own position is left where it was.
                    if (!ownsSrc) {
                        restorePosition = srcStream.Position;
                    }
                    srcStream.Seek(src_offset, SeekOrigin.Begin);
                }

                MutableString userBuffer = null;
                byte[] buffer = null;

                long bytesCopied = 0;
                long remaining = (count < 0) ? Int64.MaxValue : count;
                int minBufferSize = 16 * 1024;

                if (srcStream != null) {
                    buffer = new byte[Math.Min(minBufferSize, remaining)];
                }

                while (remaining > 0) {
                    int bytesRead;
                    int chunkSize = (int)Math.Min(minBufferSize, remaining);
                    if (srcStream != null) {
                        userBuffer = null;
                        bytesRead = srcStream.Read(buffer, 0, chunkSize);
                    } else {
                        // MRI copies what src.read(len, buf) put in buf; read answers nil at the end of
                        // the stream, readpartial raises EOFError
                        userBuffer = MutableString.CreateBinary();
                        object chunk;
                        try {
                            chunk = readSite.Target(readSite, src, chunkSize, userBuffer);
                        } catch (EOFError) {
                            if (!readPartial) {
                                throw;
                            }
                            break;
                        }
                        if (chunk == null) {
                            break;
                        }
                        bytesRead = userBuffer.GetByteCount();
                    }

                    if (bytesRead <= 0) {
                        break;
                    }

                    if (dstStream != null) {
                        if (userBuffer != null) {
                            dstStream.Write(userBuffer, 0, bytesRead);
                        } else {
                            dstStream.Write(buffer, 0, bytesRead);
                        }
                    } else {
                        if (userBuffer == null) {
                            userBuffer = MutableString.CreateBinary(bytesRead).Append(buffer, 0, bytesRead);
                        } else {
                            userBuffer.SetByteCount(bytesRead);
                        }
                        writeSite.Target(writeSite, dst, userBuffer);
                    }
                    bytesCopied += bytesRead;
                    remaining -= bytesRead;
                }
                return Protocols.Normalize(bytesCopied);

            } finally {
                if (restorePosition != -1) {
                    srcStream.Seek(restorePosition, SeekOrigin.Begin);
                }
                if (ownsSrc) {
                    srcStream.Dispose();
                }
                if (ownsDst) {
                    dstStream.Dispose();
                } else if (dstStream != null) {
                    dstStream.Flush();
                }
            }
        }

        #endregion

        // TODO: 1.9 only
        // bytes -> Enumerable::Enumerator
        // lines -> Enumerable::Enumerator

        public static IOWrapper/*!*/ CreateIOWrapper(RespondToStorage/*!*/ respondToStorage, object io, FileAccess access) {
            return CreateIOWrapper(respondToStorage, io, access, 0x1000);
        }

        public static IOWrapper/*!*/ CreateIOWrapper(RespondToStorage/*!*/ respondToStorage, object io, FileAccess access, int bufferSize) {
            bool canRead, canWrite, canSeek, canFlush, canBeClosed;

            if (access == FileAccess.Read || access == FileAccess.ReadWrite) {
                canRead = Protocols.RespondTo(respondToStorage, io, "read");
            } else {
                canRead = false;
            }

            if (access == FileAccess.Write || access == FileAccess.ReadWrite) {
                canWrite = Protocols.RespondTo(respondToStorage, io, "write");
            } else {
                canWrite = false;
            }

            canSeek = Protocols.RespondTo(respondToStorage, io, "seek") && Protocols.RespondTo(respondToStorage, io, "tell");
            canFlush = Protocols.RespondTo(respondToStorage, io, "flush");
            canBeClosed = Protocols.RespondTo(respondToStorage, io, "close");

            return new IOWrapper(respondToStorage.Context, io, canRead, canWrite, canSeek, canFlush, canBeClosed, bufferSize);
        }
    }
}
