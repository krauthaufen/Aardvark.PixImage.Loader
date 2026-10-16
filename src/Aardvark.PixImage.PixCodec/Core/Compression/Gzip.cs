using System;
using System.IO;

namespace Aardvark.ImageCodecs.Compression
{
    /// <summary>
    /// gzip (RFC 1952) on top of the managed deflate of <see cref="Zlib"/>: one shot and streaming decompression (several
    /// concatenated members, trailing zero padding tolerated, CRC-32 and size verified) and compression.
    /// </summary>
    public static class Gzip
    {
        /// <summary>Whether the data starts with the gzip signature 1F 8B 08.</summary>
        public static bool IsGzip(ReadOnlySpan<byte> data) => data.Length >= 3 && data[0] == 0x1F && data[1] == 0x8B && data[2] == 8;

        /// <summary>Decompresses all members of a gzip file.</summary>
        public static byte[] Decompress(ReadOnlySpan<byte> source, int sizeHint = 0)
        {
            if (source.IsEmpty) throw new ImageFormatException("compressed data is empty");
            unsafe
            {
                fixed (byte* p = source)
                {
                    using var ms = new UnmanagedMemoryStream(p, source.Length);
                    using var g = new GzipReader(ms, leaveOpen: true);
                    using var o = new MemoryStream(Math.Max(sizeHint, Math.Min(source.Length * 4, 1 << 26)));
                    g.CopyTo(o, 1 << 16);
                    return o.ToArray();
                }
            }
        }

        /// <summary>
        /// Decompresses the start of a gzip file into <paramref name="destination"/> and returns the number of bytes written
        /// (less than the destination only when the data ends first). Only what is needed is decoded.
        /// </summary>
        public static int Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
        {
            if (source.IsEmpty) throw new ImageFormatException("compressed data is empty");
            unsafe
            {
                fixed (byte* p = source)
                {
                    using var ms = new UnmanagedMemoryStream(p, source.Length);
                    using var g = new GzipReader(ms, leaveOpen: true) { StopAtEnd = true };
                    int total = 0;
                    while (total < destination.Length)
                    {
                        int n = g.Read(destination.Slice(total));
                        if (n <= 0) break;
                        total += n;
                    }
                    return total;
                }
            }
        }

        /// <summary>Like <see cref="Decompress(ReadOnlySpan{byte}, Span{byte})"/>, but a truncated or damaged stream only ends the output early.</summary>
        internal static int DecompressPrefix(ReadOnlySpan<byte> source, Span<byte> destination)
        {
            if (source.IsEmpty) return 0;
            int total = 0;
            unsafe
            {
                fixed (byte* p = source)
                {
                    using var ms = new UnmanagedMemoryStream(p, source.Length);
                    using var g = new GzipReader(ms, leaveOpen: true) { StopAtEnd = true };
                    try
                    {
                        while (total < destination.Length)
                        {
                            int n = g.Read(destination.Slice(total));
                            if (n <= 0) break;
                            total += n;
                        }
                    }
                    catch (ImageFormatException) { }
                }
            }
            return total;
        }

        /// <summary>Compresses to a single gzip member. Level 0..9.</summary>
        public static byte[] Compress(ReadOnlySpan<byte> source, int level = 6)
        {
            using var ms = new MemoryStream(source.Length / 2 + 64);
            using (var g = new GzipWriter(ms, level, leaveOpen: true)) g.Write(source);
            return ms.ToArray();
        }

        /// <summary>A read-only stream that decompresses the gzip data read from <paramref name="input"/>.</summary>
        public static Stream CreateDecompressor(Stream input, bool leaveOpen = false) => new GzipReader(input, leaveOpen);

        /// <summary>A stream that gzip-compresses everything written to it into <paramref name="output"/>; dispose it to finish.</summary>
        public static Stream CreateCompressor(Stream output, int level = 6, bool leaveOpen = true) => new GzipWriter(output, level, leaveOpen);
    }

    /// <summary>Streaming gzip decompression of all members of the input.</summary>
    internal sealed class GzipReader : Stream
    {
        readonly Stream input;
        readonly bool leaveOpen;
        readonly byte[] buf = new byte[1 << 16];
        int pos, end;
        bool eof;
        Inflater? inflater;
        uint crc;
        long size;
        bool inMember, done;

        /// <summary>Stop after the first member (and do not look at what follows it).</summary>
        public bool StopAtEnd { get; set; }

        public GzipReader(Stream input, bool leaveOpen)
        {
            this.input = input ?? throw new ArgumentNullException(nameof(input));
            this.leaveOpen = leaveOpen;
        }

        bool Fill()
        {
            if (eof) return false;
            if (pos < end) Buffer.BlockCopy(buf, pos, buf, 0, end - pos);
            end -= pos; pos = 0;
            while (end < buf.Length)
            {
                int n = input.Read(buf, end, buf.Length - end);
                if (n <= 0) { eof = true; break; }
                end += n;
                if (end >= 512) break;
            }
            return end > 0;
        }

        int ReadByteOrEof()
        {
            if (pos >= end && !Fill()) return -1;
            if (pos >= end) return -1;
            return buf[pos++];
        }

        int Need()
        {
            int b = ReadByteOrEof();
            if (b < 0) throw new ImageFormatException("gzip: truncated header");
            return b;
        }

        /// <summary>Parses a member header; false at the end of the data (or only zero padding left).</summary>
        bool StartMember(bool first)
        {
            int id1 = ReadByteOrEof();
            if (id1 < 0) { if (first) throw new ImageFormatException("gzip: empty input"); return false; }
            if (!first && id1 == 0)
            {
                // trailing zero padding (tape blocks, FITS records)
                while (true)
                {
                    int b = ReadByteOrEof();
                    if (b < 0) return false;
                    if (b != 0) throw new ImageFormatException("gzip: garbage after the last member");
                }
            }
            int id2 = Need(), cm = Need();
            if (id1 != 0x1F || id2 != 0x8B) throw new ImageFormatException("gzip: bad signature");
            if (cm != 8) throw new ImageFormatException($"gzip: unknown compression method {cm}");
            int flg = Need();
            for (int i = 0; i < 6; i++) Need(); // mtime, xfl, os
            if ((flg & 4) != 0)
            {
                int xlen = Need() | Need() << 8;
                for (int i = 0; i < xlen; i++) Need();
            }
            if ((flg & 8) != 0) while (Need() != 0) { }
            if ((flg & 16) != 0) while (Need() != 0) { }
            if ((flg & 2) != 0) { Need(); Need(); }
            if (inflater == null) inflater = new Inflater(zlibWrapper: false, verifyChecksum: false);
            else inflater.Reset();
            crc = 0; size = 0; inMember = true;
            return true;
        }

        void EndMember()
        {
            uint c = 0, s = 0;
            for (int i = 0; i < 4; i++) c |= (uint)Need() << (8 * i);
            for (int i = 0; i < 4; i++) s |= (uint)Need() << (8 * i);
            if (c != crc) throw new ImageFormatException("gzip: CRC-32 mismatch");
            if (s != (uint)size) throw new ImageFormatException("gzip: size mismatch");
            inMember = false;
        }

        public override int Read(Span<byte> output)
        {
            if (output.IsEmpty || done) return 0;
            while (true)
            {
                if (!inMember)
                {
                    if (!StartMember(inflater == null)) { done = true; return 0; }
                }
                if (pos >= end) Fill();
                var st = inflater!.Inflate(buf.AsSpan(pos, end - pos), output, out int consumed, out int written);
                pos += consumed;
                if (written > 0)
                {
                    crc = Checksums.Crc32(output.Slice(0, written), crc);
                    size += written;
                }
                if (st == InflateStatus.Done)
                {
                    EndMember();
                    if (StopAtEnd) done = true;
                    if (written > 0) return written;
                    if (done) return 0;
                    continue;
                }
                if (written > 0) return written;
                if (st == InflateStatus.NeedInput && pos >= end && !Fill()) throw new ImageFormatException("gzip: truncated data");
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !leaveOpen) input.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Streaming gzip compression (one member).</summary>
    internal sealed class GzipWriter : Stream
    {
        readonly Stream output;
        readonly Stream deflate;
        readonly bool leaveOpen;
        uint crc;
        long size;
        bool finished;

        public GzipWriter(Stream output, int level, bool leaveOpen)
        {
            this.output = output ?? throw new ArgumentNullException(nameof(output));
            this.leaveOpen = leaveOpen;
            level = Math.Clamp(level, 0, 9);
            Span<byte> h = stackalloc byte[10];
            h.Clear();
            h[0] = 0x1F; h[1] = 0x8B; h[2] = 8;
            h[8] = (byte)(level >= 9 ? 2 : level <= 1 ? 4 : 0);
            h[9] = 255; // unknown OS
            output.Write(h);
            deflate = new DeflaterStream(output, level, zlibWrapper: false);
        }

        public override void Write(ReadOnlySpan<byte> data)
        {
            if (finished) throw new ObjectDisposedException(nameof(GzipWriter));
            crc = Checksums.Crc32(data, crc);
            size += data.Length;
            deflate.Write(data);
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        protected override void Dispose(bool disposing)
        {
            if (disposing && !finished)
            {
                finished = true;
                deflate.Dispose();
                Span<byte> t = stackalloc byte[8];
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(t, crc);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(t.Slice(4), (uint)size);
                output.Write(t);
                if (!leaveOpen) output.Dispose();
            }
            base.Dispose(disposing);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !finished;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
