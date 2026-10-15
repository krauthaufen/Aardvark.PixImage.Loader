using Aardvark.ImageCodecs;
using Xunit;

namespace Aardvark.ImageCodecs.Tests;

/// <summary>The vectorized row filters against a direct transcription of the PNG specification.</summary>
public unsafe class PngFilterTests
{
    static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }

    static int Predict(int filter, int a, int b, int c) => filter switch
    {
        1 => a,
        2 => b,
        3 => (a + b) >> 1,
        4 => Paeth(a, b, c),
        _ => 0,
    };

    static byte[] ReferenceUnfilter(int filter, byte[] src, byte[]? prev, int bpp)
    {
        var dst = new byte[src.Length];
        for (int i = 0; i < src.Length; i++)
        {
            int a = i >= bpp ? dst[i - bpp] : 0;
            int b = prev != null ? prev[i] : 0;
            int c = prev != null && i >= bpp ? prev[i - bpp] : 0;
            dst[i] = (byte)(src[i] + Predict(filter, a, b, c));
        }
        return dst;
    }

    static byte[] RandomRow(Random rnd, int n, int kind)
    {
        var row = new byte[n];
        switch (kind)
        {
            case 0: rnd.NextBytes(row); break;
            case 1: for (int i = 0; i < n; i++) row[i] = (byte)(rnd.Next(4) == 0 ? rnd.Next(256) : rnd.Next(2) * 255); break; // extremes
            default: for (int i = 0; i < n; i++) row[i] = (byte)(128 + rnd.Next(-3, 4)); break;                              // near ties
        }
        return row;
    }

    [Fact]
    public void UnfilterMatchesSpecification()
    {
        var rnd = new Random(7);
        foreach (int bpp in new[] { 1, 2, 3, 4, 6, 8 })
            for (int n = 1; n <= 80; n++)
                for (int kind = 0; kind < 3; kind++)
                {
                    var src = RandomRow(rnd, n, kind);
                    var prev = RandomRow(rnd, n, kind);
                    for (int filter = 0; filter <= 4; filter++)
                        foreach (var above in new[] { prev, null })
                        {
                            var expected = ReferenceUnfilter(filter, src, above, bpp);
                            // guard bytes around the destination catch writes outside the row
                            var dst = new byte[n + 32];
                            Array.Fill(dst, (byte)0xA5);
                            fixed (byte* s = src, p = above, d = dst)
                                Assert.True(PngFilters.Unfilter(filter, s, p, d + 16, n, bpp));
                            Assert.True(expected.AsSpan().SequenceEqual(dst.AsSpan(16, n)), $"filter {filter} bpp {bpp} n {n}");
                            for (int g = 0; g < 16; g++) { Assert.Equal(0xA5, dst[g]); Assert.Equal(0xA5, dst[16 + n + g]); }
                        }
                }
        fixed (byte* x = new byte[8]) Assert.False(PngFilters.Unfilter(5, x, null, x, 1, 1));
    }

    [Fact]
    public void FilterMatchesSpecification()
    {
        var rnd = new Random(8);
        foreach (int bpp in new[] { 1, 2, 3, 4, 6, 8 })
            foreach (int n in new[] { 1, 2, 7, 8, 15, 16, 31, 32, 33, 63, 64, 65, 100, 257, 9000 })
                for (int kind = 0; kind < 3; kind++)
                {
                    // rows live behind bpp zero bytes, as in the encoder
                    var cur = new byte[bpp + n]; var prev = new byte[bpp + n];
                    RandomRow(rnd, n, kind).CopyTo(cur, bpp);
                    RandomRow(rnd, n, kind).CopyTo(prev, bpp);
                    for (int filter = 0; filter <= 4; filter++)
                    {
                        var expected = new byte[n];
                        long expectedSum = 0;
                        for (int i = 0; i < n; i++)
                        {
                            int a = cur[i], b = prev[bpp + i], c = prev[i]; // i is already offset by -bpp
                            expected[i] = (byte)(cur[bpp + i] - Predict(filter, a, b, c));
                            expectedSum += Math.Abs((int)(sbyte)expected[i]);
                        }
                        var dst = new byte[n + 2];
                        dst[0] = dst[n + 1] = 0xA5;
                        long sum;
                        fixed (byte* cp = cur, pp = prev, d = dst)
                        {
                            byte* c0 = cp + bpp, p0 = pp + bpp;
                            switch (filter)
                            {
                                case 1: sum = PngFilters.FilterSub(c0, d + 1, n, bpp); break;
                                case 2: sum = PngFilters.FilterUp(c0, p0, d + 1, n); break;
                                case 3: sum = PngFilters.FilterAverage(c0, p0, d + 1, n, bpp); break;
                                case 4: sum = PngFilters.FilterPaeth(c0, p0, d + 1, n, bpp); break;
                                default: sum = PngFilters.SumAbs(c0, n); expected.CopyTo(dst, 1); break;
                            }
                        }
                        Assert.True(expected.AsSpan().SequenceEqual(dst.AsSpan(1, n)), $"filter {filter} bpp {bpp} n {n}");
                        Assert.Equal(expectedSum, sum);
                        Assert.Equal(0xA5, dst[0]);
                        Assert.Equal(0xA5, dst[n + 1]);
                    }
                }
    }

    [Fact]
    public void SumAbsHandlesLongRowsOfExtremes()
    {
        // 0x80 is the largest residual (128); a long run must not overflow the vector accumulators
        var row = new byte[300_000];
        Array.Fill(row, (byte)0x80);
        fixed (byte* p = row) Assert.Equal(128L * row.Length, PngFilters.SumAbs(p, row.Length));
    }
}
