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
using IronRuby.Builtins;
using System.Text;
using System.Collections.Generic;
using IronRuby.Runtime;
using Microsoft.Scripting;
using System.IO;
using Microsoft.Scripting.Utils;

namespace IronRuby.Tests {
    public partial class Tests {
        public void File1() {
            TestOutput(@"
stdout = IO.open(1, 'w', &nil) 
stdout.puts('hello')
", @"
hello
");
        }

        /// <summary>
        /// IO.popen(cmd, "r+") moves bytes: ones that are not valid UTF-8 used to be decoded by the
        /// pipe writer's encoding (and raise), and #getbyte returned a decoded character.
        /// </summary>
        public void IO_PopenDuplexBytes1() {
            if (OperatingSystem.IsWindows()) {
                return;
            }

            TestOutput(@"
IO.popen('cat', 'r+b') do |io|
  io.write(""\xff\x00\x80\xC3\xA9"".force_encoding(""BINARY""))
  io.close_write
  p io.read.bytes
end
IO.popen('cat', 'r+') do |io|
  io.write(""hé!"")
  io.close_write
  p io.getc, io.getbyte, io.getbyte
end
", @"
[255, 0, 128, 195, 169]
""h""
195
169
");
        }

        /// <summary>
        /// IO.pipe, IO.popen, backquotes and spawn on pipe(2) and F_SETFD instead of pipe2(2), the path
        /// macOS takes, whose libc has no pipe2: all four raised EntryPointNotFoundException there.
        /// Both ends have to be close-on-exec, or a child inherits the write end and the read end never
        /// sees end of file; an end the child is handed explicitly (w => w) is open in it.
        /// </summary>
        public void IO_PipeWithoutPipe2() {
            if (OperatingSystem.IsWindows()) {
                return;
            }

            var noPipe2 = typeof(RubyProcess).GetField("_noPipe2",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert(noPipe2 != null);
            object saved = noPipe2.GetValue(null);
            noPipe2.SetValue(null, true);
            try {
                TestOutput(@"
require 'ruby4'
r, w = IO.pipe
p [r.close_on_exec?, w.close_on_exec?]
w.write 'piped'
w.close
p r.read
r.close
p IO.popen('echo popen', &:read), $?.success?
p `echo back`
r, w = IO.pipe
pid = spawn('bash', '-c', ""echo given >&#{w.fileno}"", w => w)
w.close
Process.wait(pid)
p r.read
r.close
r, w = IO.pipe
pid = spawn('bash', '-c', ""echo leaked >&#{w.fileno}"", err: File::NULL)
w.close
Process.wait(pid)
p r.read, $?.success?
", @"
[true, true]
""piped""
""popen\n""
true
""back\n""
""given\n""
""""
false
");
            } finally {
                noPipe2.SetValue(null, saved);
            }
        }

        private class TestStream : MemoryStream {
            public TestStream(byte[] data) 
                : base(data) {
            }

            public override bool CanSeek {
                get { return false; }
            }
        }

        private byte[] B(string str) {
            return BinaryEncoding.Instance.GetBytes(str);
        }

        [Options(NoRuntime = true)]
        public void File_AppendBytes1() {
            string s;
            string crlf = "\r\n";
            var stream = new TestStream(B(
                "ab\r\r\n" +
                "e" + (s = "fgh" + crlf + "ijkl" + crlf + "mnop" + crlf + crlf + crlf + crlf + "qrst") +
                crlf + "!"
            ));
            int s_crlf_count = 6;

            var io = new RubyBufferedStream(stream);
            Assert(io.PeekByte() == (byte)'a');

            var buffer = MutableString.CreateBinary(B("foo:"));
            Assert(io.AppendBytes(buffer, 4, false) == 4);
            Assert(buffer.ToString() == "foo:ab\r\n");

            buffer = MutableString.CreateBinary();
            Assert(io.AppendBytes(buffer, 1, false) == 1);
            Assert(buffer.ToString() == "e");

            buffer = MutableString.CreateMutable("x:", RubyEncoding.Binary);
            int c = s.Length - s_crlf_count - 2;
            Assert(io.AppendBytes(buffer, c, false) == c);
            Assert(buffer.ToString() == "x:" + s.Replace(crlf, "\n").Substring(0, c));

            buffer = MutableString.CreateBinary();
            Assert(io.AppendBytes(buffer, 10, false) == 4);
            Assert(buffer.ToString() == "st\n!");

            buffer = MutableString.CreateBinary();
            Assert(io.AppendBytes(buffer, 10, false) == 0);
            Assert(buffer.ToString() == "");

            stream = new TestStream(B(s = "abcd" + crlf + "xyz" + crlf + "qqq;"));
            io = new RubyBufferedStream(stream);
            buffer = MutableString.CreateBinary();
            Assert(io.AppendBytes(buffer, Int32.MaxValue, true) == s.Length);
            io.BaseStream.Seek(0, SeekOrigin.Begin);
            Assert(io.AppendBytes(buffer, Int32.MaxValue, false) == s.Length - 2);
            Assert(buffer.ToString() == s + s.Replace(crlf, "\n"));
        }

        [Options(NoRuntime = true)]
        public void File_WriteBytes1() {
            var stream = new MemoryStream();
            var io = new RubyBufferedStream(stream);

            io.Write(new byte[] { 0, 1, 2, 3 }, 1, 2);
            Assert(stream.ToArray().ValueEquals(new byte[] { 1, 2 }));
            stream.Seek(0, SeekOrigin.Begin);

            Assert(io.WriteBytes(new byte[] { 0, 1, 2, 3 }, 1, 2, true) == 2);
            Assert(stream.ToArray().ValueEquals(new byte[] { 1, 2 }));
            stream.Seek(0, SeekOrigin.Begin);

            Assert(io.WriteBytes(new byte[] { 0, 1, 2, 3 }, 1, 2, false) == 2);
            Assert(stream.ToArray().ValueEquals(new byte[] { 1, 2 }));
            stream.Seek(0, SeekOrigin.Begin);

            Assert(io.WriteBytes(new byte[] { 0, 1, (byte)'\n', 2 }, 1, 2, false) == 3);
            Assert(stream.ToArray().ValueEquals(new byte[] { 1, (byte)'\r', (byte)'\n' }));
            stream.Seek(0, SeekOrigin.Begin);
        }

        [Options(NoRuntime = true)]
        public void File_ReadLine1() {
            var stream = new TestStream(B(
                "a\r\n\r\nbbbbbbbbbbbbbbbbbbbbb1bbbbbbbbbbbbb2bbbbbbbbbbbbbbbbbbbbb3bbbbbbbbbbbbb4\rc\nd\n\n\n\nef")
            );

            foreach (int bufferSize in new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 100 }) {
                stream.Seek(0, SeekOrigin.Begin);
                var io = new RubyBufferedStream(stream, false, bufferSize);

                TestReadLine(io, true, "a\r\n");
                TestReadLine(io, true, "\r\n");
                TestReadLine(io, true, "bbbbbbbbbbbbbbbbbbbbb1bbbbbbbbbbbbb2bbbbbbbbbbbbbbbbbbbbb3bbbbbbbbbbbbb4\rc\n");
                TestReadLine(io, true, "d\n");
                TestReadLine(io, true, "\n");
                TestReadLine(io, true, "\n");
                TestReadLine(io, true, "\n");
                TestReadLine(io, true, "ef");
                TestReadLine(io, true, null);

                stream.Seek(0, SeekOrigin.Begin);
                TestReadLine(io, false, "a\n");
                TestReadLine(io, false, "\n");
                TestReadLine(io, false, "bbbbbbbbbbbbbbbbbbbbb1bbbbbbbbbbbbb2bbbbbbbbbbbbbbbbbbbbb3bbbbbbbbbbbbb4\rc\n");
                TestReadLine(io, false, "d\n");
                TestReadLine(io, false, "\n");
                TestReadLine(io, false, "\n");
                TestReadLine(io, false, "\n");
                TestReadLine(io, false, "ef");
                TestReadLine(io, false, null);
            }
        }

        /// <summary>
        /// A non-seekable stream that hands its data back in the given pieces, the way a pipe does.
        /// </summary>
        private class ChunkedStream : MemoryStream {
            private readonly int[] _chunks;
            private int _next;

            public ChunkedStream(byte[] data, params int[] chunks)
                : base(data) {
                _chunks = chunks;
            }

            public override bool CanSeek => false;

            public override int Read(byte[] buffer, int offset, int count) {
                int chunk = _next < _chunks.Length ? _chunks[_next++] : count;
                return base.Read(buffer, offset, Math.Min(count, chunk));
            }
        }

        /// <summary>
        /// Topping up a buffer whose data no longer starts at its beginning: the new bytes go after
        /// the buffered ones, not over the bytes the earlier reads have already consumed.
        /// </summary>
        [Options(NoRuntime = true)]
        public void File_LoadBufferOffset1() {
            var io = new RubyBufferedStream(new ChunkedStream(B("ab\n\r\ncd"), 4, 2), false, 16);

            TestReadLine(io, true, "ab\n");
            // "\r" is buffered after the consumed line; peeking past it reads the "\n" of the next chunk
            Assert(io.PeekByteNormalizeEoln(false) == '\n');
            Assert(io.ReadByteNormalizeEoln(false) == '\n');
            Assert(io.ReadByte() == 'c');
            Assert(io.ReadByte() == 'd');
            Assert(io.ReadByte() == -1);
        }

        private void TestReadLine(RubyBufferedStream/*!*/ io, bool preserveEolns, string expected) {
            var s = io.ReadLine(RubyEncoding.Binary, preserveEolns, -1);
            Assert(s == null && expected == null || s.ToString() == expected);
        }

        [Options(NoRuntime = true)]
        public void File_ReadLineSeparator1() {
            // A separator is found wherever it ends first, also where a partial match of it starts
            // over: "aab" ends with "ab". Each case is: content, separator, limit (-1 for none), lines.
            var cases = new[] {
                new[] { "aabxxaab", "ab", "-1", "aab", "xxaab" },
                new[] { "xxaaab yy", "aab", "-1", "xxaaab", " yy" },
                new[] { "abababc", "ababc", "-1", "abababc" },
                new[] { "abaabab", "abab", "-1", "abaabab" },
                new[] { "1--2---3----", "--", "-1", "1--", "2--", "-3--", "--" },
                new[] { "no separator", "xy", "-1", "no separator" },
                new[] { "x\r\ny\r\n\r\nz", "\r\n", "-1", "x\r\n", "y\r\n", "\r\n", "z" },
                new[] { "aabxxaab", "ab", "2", "aa", "bx", "xa", "ab" },
                new[] { "aabxxaab", "ab", "3", "aab", "xxa", "ab" },
                new[] { "aabxxaab", "aab", "100", "aab", "xxaab" },
                new[] { "", "ab", "-1" },
            };

            foreach (var c in cases) {
                foreach (int bufferSize in new[] { 1, 2, 3, 4, 5, 7, 100 }) {
                    foreach (bool seekable in new[] { false, true }) {
                        var stream = seekable ? new MemoryStream(B(c[0])) : new TestStream(B(c[0]));
                        var io = new RubyBufferedStream(stream, false, bufferSize);
                        var separator = MutableString.CreateBinary(B(c[1]));
                        int limit = Int32.Parse(c[2]);
                        for (int i = 3; i <= c.Length; i++) {
                            var line = io.ReadLine(separator, RubyEncoding.Binary, true, limit < 0 ? Int32.MaxValue : limit);
                            Assert(i == c.Length ? line == null : line != null && line.ToString() == c[i]);
                        }
                    }
                }
            }

            // a limit that splits a character is stretched to its end:
            var utf8 = new RubyBufferedStream(new MemoryStream(Encoding.UTF8.GetBytes("hé€llo")), false, 2);
            var nl = MutableString.CreateBinary(B("ll"));
            Assert(utf8.ReadLine(nl, RubyEncoding.UTF8, true, 2).ToByteArray().ValueEquals(Encoding.UTF8.GetBytes("hé")));
            Assert(utf8.ReadLine(nl, RubyEncoding.UTF8, true, 1).ToByteArray().ValueEquals(Encoding.UTF8.GetBytes("€")));
            Assert(utf8.ReadLine(nl, RubyEncoding.UTF8, true, 100).ToString() == "ll");
            Assert(utf8.ReadLine(nl, RubyEncoding.UTF8, true, 100).ToString() == "o");
            Assert(utf8.ReadLine(nl, RubyEncoding.UTF8, true, 100) == null);
        }

        [Options(NoRuntime = true)]
        public void File_AppendBytesToEnd1() {
            // Reading to the end of a stream that knows its length, and of one that doesn't, after
            // part of it has been buffered.
            var data = new byte[10000];
            for (int i = 0; i < data.Length; i++) {
                data[i] = (byte)(i * 7);
            }

            foreach (bool seekable in new[] { false, true }) {
                var stream = seekable ? new MemoryStream(data) : new TestStream(data);
                var io = new RubyBufferedStream(stream, false, 16);
                Assert(io.PeekByte() == data[0]);

                var buffer = MutableString.CreateBinary(B("x"));
                Assert(io.AppendBytes(buffer, Int32.MaxValue, true) == data.Length);
                byte[] result = buffer.ToByteArray();
                Assert(result.Length == data.Length + 1 && result[0] == (byte)'x');
                Assert(new ReadOnlySpan<byte>(result, 1, data.Length).SequenceEqual(data));

                buffer = MutableString.CreateBinary();
                Assert(io.AppendBytes(buffer, Int32.MaxValue, true) == 0);
            }
        }

        // A stream that, like a socket's, must not be read once its data is all in the buffer: a
        // socket's Read waits for the socket to become readable even when asked for no bytes.
        private sealed class NoReadStream : TestStream {
            public bool Forbidden;

            public NoReadStream(byte[] data)
                : base(data) {
            }

            public override int Read(byte[] buffer, int offset, int count) {
                if (Forbidden) {
                    throw new InvalidOperationException("read from a stream whose data is all buffered");
                }
                return base.Read(buffer, offset, count);
            }
        }

        [Options(NoRuntime = true)]
        public void File_AppendBytesFromBuffer1() {
            // `gets' then `read(n)' of bytes gets has already buffered (Net::HTTP's test server reads a
            // request body that way): the read is answered from the buffer alone.
            foreach (bool preserveEndOfLines in new[] { true, false }) {
                var stream = new NoReadStream(B("POST /\r\n\r\ntest=test"));
                var io = new RubyBufferedStream(stream, false, 64);
                Assert(io.ReadLineOrParagraph(MutableString.CreateAscii("\r\n\r\n"), RubyEncoding.Binary, true, Int32.MaxValue).ToString() == "POST /\r\n\r\n");
                stream.Forbidden = true;
                var buffer = MutableString.CreateBinary();
                Assert(io.AppendBytes(buffer, 9, preserveEndOfLines) == 9);
                Assert(buffer.ToString() == "test=test");
                Assert(!io.DataBuffered);
            }
        }

        public class Pal1 : PlatformAdaptationLayer {
            // case sensitive
            public readonly Dictionary<string, bool> Entries = new Dictionary<string, bool>();

            public override void CreateDirectory(string path) {
                Entries[path] = true;
            }

            public override string[] GetFileSystemEntries(string path, string searchPattern, bool includeFiles, bool includeDirectories) {
                List<string> result = new List<string>();
                foreach (var entry in Entries) {
                    if (entry.Key.StartsWith(path)) {
                        result.Add(entry.Key);
                    }
                }
                return result.ToArray();
            }

            public override bool DirectoryExists(string path) {
                bool isDir;
                return Entries.TryGetValue(path, out isDir) && isDir;
            }

            public override bool FileExists(string path) {
                bool isDir;
                return Entries.TryGetValue(path, out isDir) && !isDir;
            }
        }

        [Options(Pal = typeof(Pal1))]
        public void Dir2() {
            RubyClass dir = Context.GetClass(typeof(RubyDir));
            Pal1 pal = (Pal1)Context.Platform;
            var sjis = RubyEncoding.SJIS.StrictEncoding.GetBytes("ﾎ");
            var toPath = new ConversionStorage<MutableString>(Context);

            // use the string encoding if given
            RubyDir.MakeDirectory(toPath, new ConversionStorage<int>(Context), dir, MutableString.CreateBinary(sjis, RubyEncoding.SJIS), null);
            Assert(pal.Entries["ﾎ"]);

            // IO system returns UTF8 encoded strings:
            var entries = RubyDir.GetEntries(toPath, dir, MutableString.CreateEmpty());
            Assert(entries.Count == 3);
            Assert(((MutableString)entries[0]).Equals(MutableString.CreateAscii(".")));
            Assert(((MutableString)entries[1]).Equals(MutableString.CreateAscii("..")));
            Assert(((MutableString)entries[2]).Equals(MutableString.Create("ﾎ", RubyEncoding.UTF8)));

            pal.Entries.Clear();
        }

        /// <summary>
        /// Dir.glob reads each entry's type from the directory listing rather than asking the file
        /// system about every match again (#89). Dangling links, links to files and to directories
        /// are reported as before; the expected output is CRuby 4.0's.
        /// </summary>
        public void Dir_GlobListing1() {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT) {
                return;
            }
            TestOutput(@"
require ""ruby4""
require ""tmpdir""
dir = Dir.mktmpdir
begin
  Dir.chdir(dir) do
    Dir.mkdir(""a""); Dir.mkdir(""a/sub""); Dir.mkdir(""b"")
    %w[a/x.rb a/y.txt a/sub/z.rb b/w.rb .hidden].each { File.write(it, """") }
    File.symlink(""nowhere"", ""a/dangling.rb"")
    File.symlink(""../b"", ""a/blink"")
    File.symlink(""x.rb"", ""a/flink.rb"")
    p Dir[""a/*""]
    p Dir[""a/*.rb""]
    p Dir[""a/*/""]
    p Dir[""a/*/*""]
    p Dir[""*/*.rb""]
    p Dir[""**/*.rb""].sort
    p Dir[""**/""]
    p Dir[""**/*""].sort
    p Dir[""a/**/*.rb""].sort
    p Dir[""a/dangling.rb""], Dir[""a/dangling.rb/""], Dir[""a/x.rb/""], Dir[""a/x.rb/*""]
    p Dir[""{b,a}/*.rb""]
    p Dir.glob(""*"", File::FNM_DOTMATCH).sort
    p Dir.glob(""*.rb"", base: ""a""), Dir.glob(""**/*.rb"", base: ""a"").sort, Dir.glob(""*/"", base: ""a"")
    p Dir.glob(""a/*"", sort: false).sort == Dir.glob(""a/*"")
    p Dir[""nonexistent/*""], Dir[""a/nonexistent/*""]
  end
ensure
  require 'fileutils'
  FileUtils.rm_rf(dir)
end
", @"
[""a/blink"", ""a/dangling.rb"", ""a/flink.rb"", ""a/sub"", ""a/x.rb"", ""a/y.txt""]
[""a/dangling.rb"", ""a/flink.rb"", ""a/x.rb""]
[""a/blink/"", ""a/sub/""]
[""a/blink/w.rb"", ""a/sub/z.rb""]
[""a/dangling.rb"", ""a/flink.rb"", ""a/x.rb"", ""b/w.rb""]
[""a/dangling.rb"", ""a/flink.rb"", ""a/sub/z.rb"", ""a/x.rb"", ""b/w.rb""]
[""a/"", ""a/sub/"", ""b/""]
[""a"", ""a/blink"", ""a/dangling.rb"", ""a/flink.rb"", ""a/sub"", ""a/sub/z.rb"", ""a/x.rb"", ""a/y.txt"", ""b"", ""b/w.rb""]
[""a/dangling.rb"", ""a/flink.rb"", ""a/sub/z.rb"", ""a/x.rb""]
[""a/dangling.rb""]
[]
[]
[]
[""b/w.rb"", ""a/dangling.rb"", ""a/flink.rb"", ""a/x.rb""]
[""."", "".hidden"", ""a"", ""b""]
[""dangling.rb"", ""flink.rb"", ""x.rb""]
[""dangling.rb"", ""flink.rb"", ""sub/z.rb"", ""x.rb""]
[""blink/"", ""sub/""]
true
[]
[]
");
        }

        /// <summary>
        /// File.expand_path and File.absolute_path ask for the current directory only when the path is relative and no
        /// base is given; with a base, or for an absolute path, there is no getcwd. Expected output is CRuby 4.0's.
        /// </summary>
        public void File_ExpandPathLazyCwd1() {
            // drive letters
            if (Environment.OSVersion.Platform == PlatformID.Win32NT) {
                return;
            }
            TestOutput(@"
require 'ruby4'
require 'tmpdir'
Dir.mktmpdir(""ep"") { |tmp| Dir.chdir(tmp) do
  cwd = Dir.pwd
  home = File.expand_path(ENV[""HOME""])
  parent = File.dirname(cwd)
  clean = lambda { |s| s == parent ? ""PARENT"" : s.sub(cwd, ""CWD"").sub(home, ""HOME"") }
  ["""", ""."", "".."", ""a/b"", ""./a/../b"", ""/x/../y"", ""/a/./b//c/"", ""/.."", ""~/x""].each do |pa|
    row = [nil, ""/z"", ""rel"", ""rel/../q"", ""~/base"", "".""].map do |b|
      clean[b ? File.expand_path(pa, b) : File.expand_path(pa)]
    end
    p [pa, *row]
  end
  p [File.absolute_path(""a""), File.absolute_path(""~""), File.absolute_path(""x"", ""b"")].map(&clean)
  Dir.mkdir(""sub"")
  Dir.chdir(""sub"") { p clean[File.expand_path(""q"")] }
end }
", @"
["""", ""CWD"", ""/z"", ""CWD/rel"", ""CWD/q"", ""HOME/base"", ""CWD""]
[""."", ""CWD"", ""/z"", ""CWD/rel"", ""CWD/q"", ""HOME/base"", ""CWD""]
["".."", ""PARENT"", ""/"", ""CWD"", ""CWD"", ""HOME"", ""PARENT""]
[""a/b"", ""CWD/a/b"", ""/z/a/b"", ""CWD/rel/a/b"", ""CWD/q/a/b"", ""HOME/base/a/b"", ""CWD/a/b""]
[""./a/../b"", ""CWD/b"", ""/z/b"", ""CWD/rel/b"", ""CWD/q/b"", ""HOME/base/b"", ""CWD/b""]
[""/x/../y"", ""/y"", ""/y"", ""/y"", ""/y"", ""/y"", ""/y""]
[""/a/./b//c/"", ""/a/b/c"", ""/a/b/c"", ""/a/b/c"", ""/a/b/c"", ""/a/b/c"", ""/a/b/c""]
[""/.."", ""/"", ""/"", ""/"", ""/"", ""/"", ""/""]
[""~/x"", ""HOME/x"", ""HOME/x"", ""HOME/x"", ""HOME/x"", ""HOME/x"", ""HOME/x""]
[""CWD/a"", ""CWD/~"", ""CWD/b/x""]
""CWD/sub/q""
");
        }

        /// <summary>A file system of its own: a host's PAL that globbing must ask, not bypass.</summary>
        public class GlobPal1 : PlatformAdaptationLayer {
            public readonly Dictionary<string, bool> Entries = new Dictionary<string, bool> {
                { "./a", true }, { "./a/x.rb", false }, { "./a/sub", true }, { "./a/sub/y.rb", false }, { "./b.rb", false },
            };

            public override string[] GetFileSystemEntries(string path, string searchPattern, bool includeFiles, bool includeDirectories) {
                var result = new List<string>();
                string prefix = path.EndsWith("/") ? path : path + "/";
                foreach (var entry in Entries.Keys) {
                    if (entry.StartsWith(prefix) && entry.IndexOf('/', prefix.Length) < 0) {
                        result.Add(entry);
                    }
                }
                // listed, but the PAL says it does not exist
                if (path == ".") {
                    result.Add("./ghost.rb");
                }
                return result.ToArray();
            }

            public override bool DirectoryExists(string path) {
                path = path.Length > 1 ? path.TrimEnd('/') : path;
                return path == "." || Entries.TryGetValue(path, out bool isDir) && isDir;
            }

            public override bool FileExists(string path) {
                return Entries.TryGetValue(path, out bool isDir) && !isDir;
            }
        }

        public void Dir_GlobVirtualPal1() {
            var pal = new GlobPal1();
            Func<string, string> glob = pattern => String.Join(",", Glob.GetMatches(pal, pattern, 0));
            Assert(glob("*") == "a,b.rb");
            Assert(glob("*.rb") == "b.rb");
            Assert(glob("*/") == "a/");
            Assert(glob("a/*") == "a/sub,a/x.rb");
            // MRI's order: depth first, each directory sorted
            Assert(glob("**/*.rb") == "a/sub/y.rb,a/x.rb,b.rb");
            Assert(glob("a/sub/y.rb") == "a/sub/y.rb");
            Assert(glob("a/nothing.rb") == "");
        }

        /// <summary>
        /// Dir.glob walks the tree in MRI's order (#178): depth first, each directory's entries
        /// sorted, every entry matched against all the pattern parts active there - so "**/*" gives
        /// "a", "a/a", "a/a/c", "a/b", "b", ... - and with FNM_DOTMATCH "." sorts with the other
        /// names instead of coming last. Braces are expanded when the walk reaches them, or, after
        /// "**", matched in the listing. The expected output is CRuby 4.0's.
        /// </summary>
        public void Dir_GlobOrder1() {
            TestOutput(@"
require ""ruby4""
require ""tmpdir""
dir = Dir.mktmpdir
begin
  Dir.chdir(dir) do
    %w[a a/a b b/sub c .dot sub].each { |d| Dir.mkdir(d) }
    %w[a/a/c a/b b/sub/y b/x c/x c/y .dot/z -dash e.rb sub/f.rb sub/g.txt].each { |f| File.write(f, """") }
    p Dir[""**/*""]
    p Dir[""**/c""], Dir[""**/a/*""], Dir[""**/""]
    p Dir.glob(""**/*"", File::FNM_DOTMATCH)
    p Dir.glob(""*"", File::FNM_DOTMATCH), Dir.glob(""a/**/*"", File::FNM_DOTMATCH), Dir.glob(""*/*"", File::FNM_DOTMATCH)
    p Dir["".*""], Dir[""**/.""], Dir[""**/a/**/c""], Dir[""a/**//c""], Dir[""**//c""]
    p Dir[""**/{x,y}""], Dir[""*/{y,x}""], Dir[""**/*.{txt,rb}""], Dir[""{c,b}/*""]
    p Dir.glob(""**/*"", base: ""a""), Dir.glob(""**/"", base: ""b"")
    p Dir.glob(""**/*"", sort: false).sort == Dir.glob(""**/*"")
  end
ensure
  require 'fileutils'
  FileUtils.rm_rf(dir)
end
", @"
[""-dash"", ""a"", ""a/a"", ""a/a/c"", ""a/b"", ""b"", ""b/sub"", ""b/sub/y"", ""b/x"", ""c"", ""c/x"", ""c/y"", ""e.rb"", ""sub"", ""sub/f.rb"", ""sub/g.txt""]
[""a/a/c"", ""c""]
[""a/a"", ""a/a/c"", ""a/b""]
[""a/"", ""a/a/"", ""b/"", ""b/sub/"", ""c/"", ""sub/""]
[""-dash"", ""."", "".dot"", "".dot/z"", ""a"", ""a/a"", ""a/a/c"", ""a/b"", ""b"", ""b/sub"", ""b/sub/y"", ""b/x"", ""c"", ""c/x"", ""c/y"", ""e.rb"", ""sub"", ""sub/f.rb"", ""sub/g.txt""]
[""-dash"", ""."", "".dot"", ""a"", ""b"", ""c"", ""e.rb"", ""sub""]
[""a/."", ""a/a"", ""a/a/c"", ""a/b""]
[""./-dash"", ""./.dot"", ""./a"", ""./b"", ""./c"", ""./e.rb"", ""./sub"", "".dot/z"", ""a/a"", ""a/b"", ""b/sub"", ""b/x"", ""c/x"", ""c/y"", ""sub/f.rb"", ""sub/g.txt""]
[""."", "".dot""]
[]
[""a/a/c""]
[""a/a/c""]
[""a/a/c"", ""c""]
[""b/sub/y"", ""b/x"", ""c/x"", ""c/y""]
[""b/x"", ""c/y"", ""c/x""]
[""e.rb"", ""sub/f.rb"", ""sub/g.txt""]
[""c/x"", ""c/y"", ""b/sub"", ""b/x""]
[""a"", ""a/c"", ""b""]
[""/"", ""sub/""]
true
");
        }

        /// <summary>
        /// ".." and "." in a glob pattern are resolved by the kernel, not by .NET's path
        /// normalisation (#178): "d1/ln/.." is the parent of the directory the link points to,
        /// and "file/.." and "file/." do not exist. The expected output is CRuby 4.0's.
        /// </summary>
        public void Dir_GlobDotDot1() {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT) {
                return;
            }
            TestOutput(@"
require ""ruby4""
require ""tmpdir""
dir = Dir.mktmpdir
begin
  Dir.chdir(dir) do
    Dir.mkdir(""d1""); Dir.mkdir(""d1/sub""); Dir.mkdir(""real"")
    %w[d1/only_d1 top_only real/r].each { |f| File.write(f, """") }
    File.symlink(""../real"", ""d1/ln"")
    p Dir[""d1/*/../only_d1""], Dir[""d1/*/../top_only""], Dir[""d1/ln/../top_only""]
    p Dir[""top_only/../d1""], Dir[""top_only/..""], Dir[""top_only/.""], Dir[""*/.""], Dir[""d1/*/.""]
    p Dir[""d1/ln/../*""], Dir[""d1/ln/../**/r""], Dir[""d1/sub/../*""]
    p Dir.glob(""*"", base: ""d1/ln/..""), Dir.glob(""t*"", base: ""d1/ln/../"")
    p Dir[""#{dir}/d1/ln/../t*""].map { |x| x.sub(dir, ""DIR"") }, Dir[""#{dir}/d1/ln/../only_d1""]
  end
ensure
  require 'fileutils'
  FileUtils.rm_rf(dir)
end
", @"
[""d1/sub/../only_d1""]
[""d1/ln/../top_only""]
[""d1/ln/../top_only""]
[]
[]
[]
[""d1/."", ""real/.""]
[""d1/ln/."", ""d1/sub/.""]
[""d1/ln/../d1"", ""d1/ln/../real"", ""d1/ln/../top_only""]
[""d1/ln/../real/r""]
[""d1/sub/../ln"", ""d1/sub/../only_d1"", ""d1/sub/../sub""]
[""d1"", ""real"", ""top_only""]
[""top_only""]
[""DIR/d1/ln/../top_only""]
[]
");
        }

        /// <summary>
        /// Dir.glob raises Errno::ELOOP where MRI's opendir meets a symbolic link loop - when it
        /// has to list the loop - and stays silent where MRI only stats one (#178). The expected
        /// output is CRuby 4.0's.
        /// </summary>
        public void Dir_GlobSymlinkLoop1() {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT) {
                return;
            }
            TestOutput(@"
require ""ruby4""
require ""tmpdir""
dir = Dir.mktmpdir
begin
  Dir.chdir(dir) do
    Dir.mkdir(""sub""); File.write(""sub/f"", """")
    File.symlink(""l2"", ""l1""); File.symlink(""l1"", ""l2""); File.symlink(""self"", ""self"")
    File.symlink(""../l1"", ""sub/lp"")
    [""l1/*"", ""self/**/x"", ""*/*/"", ""**/l1/*"", ""sub/*/*"", ""l1/../*"", ""*"", ""**/*"", ""*/"", ""l1/x"", ""*/x"", ""l1/"", ""l1/../x""].each do |pattern|
      begin
        p [pattern, Dir[pattern]]
      rescue SystemCallError => e
        p [pattern, e.class, e.message]
      end
    end
    p Dir.glob(""*/**/"", File::FNM_DOTMATCH)
    begin
      Dir.glob(""*"", base: ""l1"")
    rescue SystemCallError => e
      p [e.class, e.message]
    end
  end
ensure
  require 'fileutils'
  FileUtils.rm_rf(dir)
end
", @"
[""l1/*"", Errno::ELOOP, ""Too many levels of symbolic links - l1""]
[""self/**/x"", Errno::ELOOP, ""Too many levels of symbolic links - self""]
[""*/*/"", Errno::ELOOP, ""Too many levels of symbolic links - l1""]
[""**/l1/*"", Errno::ELOOP, ""Too many levels of symbolic links - l1""]
[""sub/*/*"", Errno::ELOOP, ""Too many levels of symbolic links - sub/lp""]
[""l1/../*"", Errno::ELOOP, ""Too many levels of symbolic links - l1/..""]
[""*"", [""l1"", ""l2"", ""self"", ""sub""]]
[""**/*"", [""l1"", ""l2"", ""self"", ""sub"", ""sub/f"", ""sub/lp""]]
[""*/"", [""sub/""]]
[""l1/x"", []]
[""*/x"", []]
[""l1/"", []]
[""l1/../x"", []]
[""./"", ""./sub/"", ""sub/""]
[Errno::ELOOP, ""Too many levels of symbolic links - l1""]
");
        }
    }
}
