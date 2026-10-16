using System;
using System.IO;

namespace Aardvark.ImageCodecs.Compression
{
    /// <summary>
    /// bzip2 decompression (written from the description of the format: Burrows-Wheeler transform, move-to-front, run length
    /// and Huffman stages). Concatenated streams are read one after the other; block and stream CRCs are verified. The
    /// "randomised" blocks of bzip2 versions before 0.9.5 are not supported.
    /// </summary>
    public static class Bzip2
    {
        /// <summary>Whether the data starts with a bzip2 stream header ("BZh1" to "BZh9").</summary>
        public static bool IsBzip2(ReadOnlySpan<byte> data) => data.Length >= 4 && data[0] == 'B' && data[1] == 'Z' && data[2] == 'h' && data[3] is >= (byte)'1' and <= (byte)'9';

        /// <summary>Decompresses all streams of a bzip2 file.</summary>
        public static byte[] Decompress(ReadOnlySpan<byte> source)
        {
            if (source.IsEmpty) throw new ImageFormatException("compressed data is empty");
            unsafe
            {
                fixed (byte* p = source)
                {
                    using var ms = new UnmanagedMemoryStream(p, source.Length);
                    using var b = new Bzip2Reader(ms, leaveOpen: true);
                    using var o = new MemoryStream(Math.Min(source.Length * 4, 1 << 26) + 16);
                    b.CopyTo(o, 1 << 16);
                    return o.ToArray();
                }
            }
        }

        /// <summary>A read-only stream that decompresses the bzip2 data read from <paramref name="input"/>.</summary>
        public static Stream CreateDecompressor(Stream input, bool leaveOpen = false) => new Bzip2Reader(input, leaveOpen);
    }

    internal sealed class Bzip2Reader : Stream
    {
        const int MaxGroups = 6, MaxAlpha = 258, MaxCodeLen = 20, GroupSize = 50;

        readonly Stream input;
        readonly bool leaveOpen;
        readonly byte[] inBuf = new byte[1 << 16];
        int inPos, inEnd;
        ulong bitBuf;
        int bitCount;
        bool inputEof;

        static readonly uint[] CrcTable = MakeCrcTable();

        int blockSize; // in bytes, 0 before the first stream header
        uint[] tt = Array.Empty<uint>();
        uint streamCrc, combinedCrc;
        bool streamDone = true, finished, first = true;

        // output state of the current block
        int outLeft;      // BWT bytes not yet consumed
        uint tPos;
        int prevByte = -1, runLen, repeatLeft;
        byte repeatByte;
        uint blockCrc, expectedBlockCrc;
        bool blockActive;

        public Bzip2Reader(Stream input, bool leaveOpen)
        {
            this.input = input ?? throw new ArgumentNullException(nameof(input));
            this.leaveOpen = leaveOpen;
        }

        static uint[] MakeCrcTable()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i << 24;
                for (int k = 0; k < 8; k++) c = (c & 0x80000000) != 0 ? c << 1 ^ 0x04C11DB7 : c << 1;
                t[i] = c;
            }
            return t;
        }

        // ---------------------------------------------------------------- bits

        bool FillByte()
        {
            if (inPos >= inEnd)
            {
                if (inputEof) return false;
                inEnd = input.Read(inBuf, 0, inBuf.Length);
                inPos = 0;
                if (inEnd <= 0) { inEnd = 0; inputEof = true; return false; }
            }
            bitBuf = bitBuf << 8 | inBuf[inPos++];
            bitCount += 8;
            return true;
        }

        uint Bits(int n)
        {
            while (bitCount < n)
                if (!FillByte()) throw new ImageFormatException("bzip2: truncated data");
            bitCount -= n;
            return (uint)(bitBuf >> bitCount) & (uint)((1UL << n) - 1);
        }

        uint Bit() => Bits(1);

        /// <summary>Skips to the next byte boundary and reads a stream header; false at the end of the input.</summary>
        bool StartStream()
        {
            bitCount -= bitCount & 7;
            int b0;
            if (bitCount >= 8) { bitCount -= 8; b0 = (int)(bitBuf >> bitCount) & 0xFF; }
            else
            {
                if (!FillByte()) { if (first) throw new ImageFormatException("bzip2: empty input"); return false; }
                bitCount -= 8; b0 = (int)(bitBuf >> bitCount) & 0xFF;
            }
            if (!first && b0 == 0)
            {
                // zero padding after the last stream
                while (true)
                {
                    if (bitCount < 8 && !FillByte()) return false;
                    bitCount -= 8;
                    if (((bitBuf >> bitCount) & 0xFF) != 0) throw new ImageFormatException("bzip2: garbage after the last stream");
                }
            }
            if (b0 != 'B' || Bits(8) != 'Z' || Bits(8) != 'h') throw new ImageFormatException("bzip2: bad signature");
            uint level = Bits(8);
            if (level < '1' || level > '9') throw new ImageFormatException("bzip2: bad block size");
            blockSize = (int)(level - '0') * 100000;
            if (tt.Length < blockSize) tt = new uint[blockSize];
            first = false;
            streamDone = false;
            combinedCrc = 0;
            return true;
        }

        /// <summary>Reads the next block of the current stream; false at the stream end marker.</summary>
        bool ReadBlock()
        {
            ulong magic = (ulong)Bits(24) << 24 | Bits(24);
            if (magic == 0x177245385090)
            {
                streamCrc = Bits(32);
                if (streamCrc != combinedCrc) throw new ImageFormatException("bzip2: stream CRC mismatch");
                streamDone = true;
                return false;
            }
            if (magic != 0x314159265359) throw new ImageFormatException("bzip2: bad block signature");
            expectedBlockCrc = Bits(32);
            if (Bit() != 0) throw new ImageNotSupportedException("bzip2: randomised blocks (bzip2 before 0.9.5) are not supported");
            int origPtr = (int)Bits(24);

            // symbol map
            Span<byte> seqToUnseq = stackalloc byte[256];
            int nInUse = 0;
            uint inUse16 = Bits(16);
            for (int i = 0; i < 16; i++)
                if ((inUse16 & (0x8000u >> i)) != 0)
                {
                    uint m = Bits(16);
                    for (int j = 0; j < 16; j++)
                        if ((m & (0x8000u >> j)) != 0) seqToUnseq[nInUse++] = (byte)(i * 16 + j);
                }
            if (nInUse == 0) throw new ImageFormatException("bzip2: no symbols in use");
            int alphaSize = nInUse + 2;

            int nGroups = (int)Bits(3);
            if (nGroups < 2 || nGroups > MaxGroups) throw new ImageFormatException("bzip2: bad number of Huffman tables");
            int nSelectors = (int)Bits(15);
            if (nSelectors < 1) throw new ImageFormatException("bzip2: no selectors");
            var selectors = new byte[nSelectors];
            Span<byte> mtfGroups = stackalloc byte[MaxGroups];
            for (int i = 0; i < nGroups; i++) mtfGroups[i] = (byte)i;
            for (int i = 0; i < nSelectors; i++)
            {
                int j = 0;
                while (Bit() != 0)
                {
                    j++;
                    if (j >= nGroups) throw new ImageFormatException("bzip2: bad selector");
                }
                byte v = mtfGroups[j];
                for (; j > 0; j--) mtfGroups[j] = mtfGroups[j - 1];
                mtfGroups[0] = v;
                selectors[i] = v;
            }

            // code lengths and decoding tables
            var limit = new int[MaxGroups * (MaxCodeLen + 2)];
            var baseT = new int[MaxGroups * (MaxCodeLen + 2)];
            var perm = new int[MaxGroups * MaxAlpha];
            var minLens = new int[MaxGroups];
            Span<byte> len = stackalloc byte[MaxAlpha];
            for (int t = 0; t < nGroups; t++)
            {
                int curr = (int)Bits(5);
                int minL = 32, maxL = 0;
                for (int i = 0; i < alphaSize; i++)
                {
                    while (true)
                    {
                        if (curr < 1 || curr > MaxCodeLen) throw new ImageFormatException("bzip2: bad code length");
                        if (Bit() == 0) break;
                        curr += Bit() == 0 ? 1 : -1;
                    }
                    len[i] = (byte)curr;
                    if (curr < minL) minL = curr;
                    if (curr > maxL) maxL = curr;
                }
                // canonical codes: by length, then by symbol
                int lo = t * (MaxCodeLen + 2), po = t * MaxAlpha, pp = 0;
                for (int l = minL; l <= maxL; l++)
                    for (int i = 0; i < alphaSize; i++)
                        if (len[i] == l) perm[po + pp++] = i;
                int code = 0, idx = 0;
                for (int l = 0; l <= MaxCodeLen + 1; l++) { limit[lo + l] = -1; baseT[lo + l] = 0; }
                for (int l = minL; l <= maxL; l++)
                {
                    int count = 0;
                    for (int i = 0; i < alphaSize; i++) if (len[i] == l) count++;
                    // codes of length l are code .. code + count - 1; perm index of the first is idx
                    baseT[lo + l] = idx - code;
                    code += count;
                    idx += count;
                    limit[lo + l] = code - 1;
                    code <<= 1;
                }
                minLens[t] = minL;
                if (code >> 1 > 1 << maxL) throw new ImageFormatException("bzip2: oversubscribed Huffman code");
            }

            // MTF / RLE2 decoding of the symbols into tt (low byte = value)
            Span<byte> mtf = stackalloc byte[256];
            for (int i = 0; i < 256; i++) mtf[i] = (byte)i;
            Span<int> counts = stackalloc int[256];
            counts.Clear();
            int nblock = 0, groupNo = -1, groupPos = 0, sel = 0;
            int runN = 0, runAcc = 0; bool inRun = false;
            int lim = blockSize;
            var ttl = tt;
            while (true)
            {
                if (groupPos == 0)
                {
                    groupNo++;
                    if (groupNo >= nSelectors) throw new ImageFormatException("bzip2: ran out of selectors");
                    groupPos = GroupSize;
                    sel = selectors[groupNo];
                    if (sel >= nGroups) throw new ImageFormatException("bzip2: bad selector");
                }
                groupPos--;
                int lo = sel * (MaxCodeLen + 2);
                int l = minLens[sel];
                int c = (int)Bits(l);
                while (c > limit[lo + l])
                {
                    l++;
                    if (l > MaxCodeLen) throw new ImageFormatException("bzip2: bad Huffman code");
                    c = c << 1 | (int)Bit();
                }
                int pi = baseT[lo + l] + c;
                if ((uint)pi >= (uint)alphaSize) throw new ImageFormatException("bzip2: bad Huffman code");
                int sym = perm[sel * MaxAlpha + pi];
                if (sym <= 1)
                {
                    if (!inRun) { inRun = true; runN = 1; runAcc = 0; }
                    runAcc += runN << sym;
                    runN <<= 1;
                    if (runAcc > lim) throw new ImageFormatException("bzip2: run exceeds the block size");
                    continue;
                }
                if (inRun)
                {
                    inRun = false;
                    byte v = seqToUnseq[mtf[0]];
                    if (nblock + runAcc > lim) throw new ImageFormatException("bzip2: block too large");
                    counts[v] += runAcc;
                    ttl.AsSpan(nblock, runAcc).Fill(v);
                    nblock += runAcc;
                }
                if (sym == alphaSize - 1) break;
                {
                    int k = sym - 1;
                    if (k >= nInUse) throw new ImageFormatException("bzip2: bad MTF index");
                    byte u = mtf[k];
                    mtf.Slice(0, k).CopyTo(mtf.Slice(1));
                    mtf[0] = u;
                    byte v = seqToUnseq[u];
                    if (nblock >= lim) throw new ImageFormatException("bzip2: block too large");
                    counts[v]++;
                    ttl[nblock++] = v;
                }
            }
            if (origPtr >= nblock && nblock > 0 || nblock == 0 && origPtr != 0) throw new ImageFormatException("bzip2: bad origin pointer");

            // inverse BWT
            Span<int> cf = stackalloc int[256];
            int sum = 0;
            for (int i = 0; i < 256; i++) { cf[i] = sum; sum += counts[i]; }
            for (int i = 0; i < nblock; i++)
            {
                int v = (int)(ttl[i] & 0xFF);
                ttl[cf[v]++] |= (uint)i << 8;
            }
            outLeft = nblock;
            tPos = nblock > 0 ? ttl[origPtr] >> 8 : 0;
            prevByte = -1; runLen = 0; repeatLeft = 0;
            blockCrc = 0xFFFFFFFF;
            blockActive = true;
            return true;
        }

        void EndBlock()
        {
            blockActive = false;
            uint crc = ~blockCrc;
            if (crc != expectedBlockCrc) throw new ImageFormatException("bzip2: block CRC mismatch");
            combinedCrc = (combinedCrc << 1 | combinedCrc >> 31) ^ crc;
        }

        /// <summary>Emits the block's bytes with the initial run length coding undone.</summary>
        int Emit(Span<byte> output)
        {
            int n = 0;
            uint crc = blockCrc;
            var t = tt;
            var table = CrcTable;
            while (n < output.Length)
            {
                byte b;
                if (repeatLeft > 0) { repeatLeft--; b = repeatByte; }
                else
                {
                    if (outLeft == 0) break;
                    uint e = t[tPos];
                    b = (byte)e; tPos = e >> 8; outLeft--;
                    if (runLen == 4)
                    {
                        // count byte after four equal bytes
                        repeatLeft = b; repeatByte = (byte)prevByte;
                        runLen = 0; prevByte = -1;
                        continue;
                    }
                    if (b == prevByte) runLen++;
                    else { runLen = 1; prevByte = b; }
                }
                output[n++] = b;
                crc = crc << 8 ^ table[(crc >> 24 ^ b) & 0xFF];
            }
            blockCrc = crc;
            return n;
        }

        public override int Read(Span<byte> output)
        {
            if (output.IsEmpty || finished) return 0;
            while (true)
            {
                if (blockActive)
                {
                    int n = Emit(output);
                    if (outLeft == 0 && repeatLeft == 0) EndBlock();
                    if (n > 0) return n;
                    continue;
                }
                if (streamDone)
                {
                    if (!StartStream()) { finished = true; return 0; }
                }
                if (!ReadBlock()) continue; // stream end: maybe another stream follows
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
}
