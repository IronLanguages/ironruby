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
using Microsoft.Scripting.Utils;

namespace IronRuby.Builtins {
    public class RubyFile : RubyIO {
        public string Path { get; set; }

        /// <summary>
        /// The encoding of the string File.open was given, so File#path can hand it back
        /// unchanged.  Null for files opened from a descriptor or from a CLR string.
        /// </summary>
        public RubyEncoding PathEncoding { get; set; }

        public RubyFile(RubyContext/*!*/ context)
            : base(context) {
            Path = null;
        }

        public RubyFile(RubyContext/*!*/ context, string/*!*/ path, IOMode mode)
            : base(context, OpenFileStream(context, path, mode), mode) {
            Path = path;
        }

        public RubyFile(RubyContext/*!*/ context, Stream/*!*/ stream, int descriptor, IOMode mode)
            : base(context, stream, descriptor, mode) {
            Path = null;
        }

        private const int ENXIO = 6;
        private const int HResultInvalidName = unchecked((int)0x8007007B);   // HRESULT of ERROR_INVALID_NAME

        public static Stream/*!*/ OpenFileStream(RubyContext/*!*/ context, string/*!*/ path, IOMode mode) {
            ContractUtils.RequiresNotNull(path, "path");
            FileAccess access = mode.ToFileAccess();

            FileMode fileMode;
  
            if ((mode & IOMode.CreateIfNotExists) != 0) {
                if ((mode & IOMode.ErrorIfExists) != 0) {
                    access |= FileAccess.Write;
                    fileMode = FileMode.CreateNew;
                } else {
                    fileMode = FileMode.OpenOrCreate;
                }
            } else {
                fileMode = FileMode.Open;
            }

            // O_RDONLY|O_TRUNC and O_RDONLY|O_APPEND are legal on Unix: the file opens
            // read-only (a later write raises IOError "not opened for writing") and
            // O_TRUNC still empties it.  Truncating needs write access on the handle
            // even though the Ruby-level mode stays read-only.
            bool truncateReadOnly = (mode & IOMode.Truncate) != 0 && (access & FileAccess.Write) == 0;
            if (truncateReadOnly) {
                access |= FileAccess.Write;
            }

            if (String.IsNullOrEmpty(path)) {
                // MRI treats the empty path as a missing file, not a bad argument.
                throw RubyExceptions.CreateENOENT("No such file or directory - {0}", path);
            }

            Stream stream;
            if (path == "NUL") {
                stream = Stream.Null;
            } else {
                try {
                    stream = context.DomainManager.Platform.OpenInputFileStream(path, fileMode, access, FileShare.ReadWrite);
                } catch (FileNotFoundException) {
                    throw RubyExceptions.CreateENOENT(String.Format("No such file or directory - {0}", path));
#if FEATURE_FILESYSTEM
                } catch (DirectoryNotFoundException e) {
                    // The CLR says this for a missing directory on the way to the file and for a
                    // file standing where a directory should be alike; MRI names the path, and
                    // tells the two apart: ENOENT, and ENOTDIR (which is what a CLR
                    // DirectoryNotFoundException surfaces as in Ruby, so it is rethrown as one).
                    for (string parent = System.IO.Path.GetDirectoryName(path); !String.IsNullOrEmpty(parent); parent = System.IO.Path.GetDirectoryName(parent)) {
                        if (File.Exists(parent)) {
                            throw new DirectoryNotFoundException(String.Format("Not a directory - {0}", path), e);
                        }
                        if (Directory.Exists(parent)) {
                            break;
                        }
                    }
                    throw RubyExceptions.CreateENOENT(e, "No such file or directory - {0}", path);
                } catch (PathTooLongException e) {
                    throw RubyExceptions.CreateENOENT(e.Message, e);
#endif
                } catch (IOException e) {
                    if ((mode & IOMode.ErrorIfExists) != 0) {
                        throw RubyExceptions.CreateEEXIST(path);
                    }
                    // On Unix the CLR puts the raw errno in HResult (Interop.GetExceptionForIoErrno);
                    // on Windows HResult is an HRESULT and is negative. ENXIO is what open(2) answers
                    // for /dev/tty without a controlling terminal, and MRI raises Errno::ENXIO there -
                    // a bare IOException would have surfaced as IOError.
                    if (e.HResult == ENXIO && System.IO.Path.DirectorySeparatorChar == '/') {
                        throw RubyExceptions.CreateENXIO(path);
                    }
                    // Windows: a name with a character no file name may hold (<, |, ? ...) is
                    // ERROR_INVALID_NAME, which MRI raises as Errno::EINVAL. method_source opens the
                    // path of a method defined in "<internal:kernel>" and expects a SystemCallError.
                    if (e.HResult == HResultInvalidName) {
                        throw RubyExceptions.CreateEINVAL(e, "{0}", path);
                    }
                    throw;
                } catch (UnauthorizedAccessException) {
                    // .NET reports opening a directory as an access violation; MRI
                    // distinguishes it, so only a genuine one stays EACCES (which is
                    // what UnauthorizedAccessException already maps to).
                    if (context.DomainManager.Platform.DirectoryExists(path)) {
                        // MRI opens a directory read-only like any other file
                        if (access == FileAccess.Read) {
                            Stream directory = DescriptorStream.TryOpenDirectory(path);
                            if (directory != null) {
                                return directory;
                            }
                        }
                        throw RubyExceptions.CreateEISDIR(path);
                    }
                    throw;
                } catch (ArgumentException e) {
                    throw RubyExceptions.CreateEINVAL(e.Message, e);
                }
            }

            // O_TRUNC does nothing to what cannot be truncated, a FIFO for one
            if ((mode & IOMode.Truncate) != 0 && stream.CanSeek) {
                try {
                    stream.SetLength(0);
                } catch (IOException) {
                    // O_TRUNC is defined to do nothing to anything that is not a regular file, but
                    // ftruncate(2) on a character device reports EINVAL, which .NET raises -- so
                    // File.open("/dev/null", "w") failed outright. A stream that already measures
                    // zero has nothing to truncate either way; anything longer really did fail.
                    if (stream.CanSeek && stream.Length != 0) {
                        throw;
                    }
                }
            }

            return stream;
        }
    }
}