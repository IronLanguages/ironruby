using System;
using System.IO;
using System.Runtime.InteropServices.JavaScript;
using System.Text;

namespace IronRuby.Wasm {
    /// <summary>
    /// $stdout / $stderr. Writes are decoded as UTF-8 (a decoder per stream, so a character split
    /// across two writes survives), collected for the result of the current run and, when the page
    /// installed globalThis.__ironrubyWrite, handed to it as they happen.
    /// </summary>
    internal sealed partial class OutputStream : Stream {
        private readonly int _fd;
        private readonly Decoder _decoder = new UTF8Encoding(false, false).GetDecoder();
        private readonly StringBuilder _captured = new StringBuilder();
        private char[] _chars = new char[256];

        public static bool Streaming;

        public OutputStream(int fd) { _fd = fd; }

        public string Take() {
            // A run ends here, and so does a character it left unfinished: flushed, it is U+FFFD in this run's
            // output; kept in the decoder, it joined the first bytes of the next run into a character neither wrote.
            int n = _decoder.GetChars(Array.Empty<byte>(), 0, 0, _chars, 0, true);
            if (n > 0) {
                _captured.Append(_chars, 0, n);
                if (Streaming) {
                    try { JsWrite(_fd, new string(_chars, 0, n)); } catch (Exception) { Streaming = false; }
                }
            }
            string s = _captured.ToString();
            _captured.Clear();
            return s;
        }

        public override void Write(byte[] buffer, int offset, int count) {
            if (count == 0) return;
            int n = _decoder.GetCharCount(buffer, offset, count, false);
            if (n > _chars.Length) _chars = new char[Math.Max(n, _chars.Length * 2)];
            n = _decoder.GetChars(buffer, offset, count, _chars, 0, false);
            if (n == 0) return;
            _captured.Append(_chars, 0, n);
            if (Streaming) {
                try { JsWrite(_fd, new string(_chars, 0, n)); } catch (Exception) { Streaming = false; }
            }
        }

        [JSImport("globalThis.__ironrubyWrite")]
        private static partial void JsWrite(int fd, string text);

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
