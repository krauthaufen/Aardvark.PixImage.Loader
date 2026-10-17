"""Generates src/Aardvark.PixImage.PixCodec/Avif/Av1Transform.Kernels.cs: the 1D inverse DCT (4..64 points) and ADST
(8, 16 points) of the AV1 specification (7.13.2) as straight-line code.

The butterfly sequences below follow the steps of the specification's inverse DCT / ADST processes (the brev input
permutation, B( a, b, angle, flip ) rotations with Round2( x, 12 ), H( a, b, flip ) additions clamped to r bits, the
ADST input and output permutations). They are recorded once and printed with one local per array element, so that the
generated kernels compute exactly what the specification's processes compute.

usage: python3 gen_av1_transforms.py ../../src/Aardvark.PixImage.PixCodec/Avif/Av1Transform.Kernels.cs
"""
import sys

COS128 = [4096, 4095, 4091, 4085, 4076, 4065, 4052, 4036, 4017, 3996, 3973, 3948, 3920, 3889, 3857, 3822, 3784, 3745,
          3703, 3659, 3612, 3564, 3513, 3461, 3406, 3349, 3290, 3229, 3166, 3102, 3035, 2967, 2896, 2824, 2751, 2675,
          2598, 2520, 2440, 2359, 2276, 2191, 2106, 2019, 1931, 1842, 1751, 1660, 1567, 1474, 1380, 1285, 1189, 1092,
          995, 897, 799, 700, 601, 501, 401, 301, 201, 101, 0]


def cos128(a):
    a &= 255
    if a <= 64: return COS128[a]
    if a <= 128: return -COS128[128 - a]
    if a <= 192: return -COS128[a - 128]
    return COS128[256 - a]


def sin128(a): return cos128(a - 64)


def brev(bits, x):
    return sum(((x >> i) & 1) << (bits - 1 - i) for i in range(bits))


class Rec:
    def __init__(self, n): self.ops = []; self.n = n

    def B(self, a, b, angle, flip): self.ops.append(('B', a, b, cos128(angle), sin128(angle), flip))

    def H(self, a, b, flip):
        if flip: a, b = b, a
        self.ops.append(('H', a, b))


def dct(n):
    """Inverse DCT process (spec 7.13.2.3) for 2^n points; the input permutation is applied when loading."""
    r = Rec(n)
    B, H = r.B, r.H
    if n == 6:
        for i in range(16): B(32 + i, 63 - i, 63 - 4 * brev(4, i), 0)
    if n >= 5:
        for i in range(8): B(16 + i, 31 - i, 6 + (brev(3, 7 - i) << 3), 0)
    if n == 6:
        for i in range(16): H(32 + i * 2, 33 + i * 2, i & 1)
    if n >= 4:
        for i in range(4): B(8 + i, 15 - i, 12 + (brev(2, 3 - i) << 4), 0)
    if n >= 5:
        for i in range(8): H(16 + 2 * i, 17 + 2 * i, i & 1)
    if n == 6:
        for i in range(4):
            for j in range(2): B(62 - i * 4 - j, 33 + i * 4 + j, 60 - 16 * brev(2, i) + 64 * j, 1)
    if n >= 3:
        for i in range(2): B(4 + i, 7 - i, 56 - 32 * i, 0)
    if n >= 4:
        for i in range(4): H(8 + 2 * i, 9 + 2 * i, i & 1)
    if n >= 5:
        for i in range(2):
            for j in range(2): B(30 - 4 * i - j, 17 + 4 * i + j, 24 + (j << 6) + ((1 - i) << 5), 1)
    if n == 6:
        for i in range(8):
            for j in range(2): H(32 + i * 4 + j, 35 + i * 4 - j, i & 1)
    for i in range(2): B(2 * i, 2 * i + 1, 32 + 16 * i, 1 - i)
    if n >= 3:
        for i in range(2): H(4 + 2 * i, 5 + 2 * i, i)
    if n >= 4:
        for i in range(2): B(14 - i, 9 + i, 48 + 64 * i, 1)
    if n >= 5:
        for i in range(4):
            for j in range(2): H(16 + 4 * i + j, 19 + 4 * i - j, i & 1)
    if n == 6:
        for i in range(2):
            for j in range(4): B(61 - i * 8 - j, 34 + i * 8 + j, 56 - i * 32 + (j >> 1) * 64, 1)
    for i in range(2): H(i, 3 - i, 0)
    if n >= 3: B(6, 5, 32, 1)
    if n >= 4:
        for i in range(2):
            for j in range(2): H(8 + 4 * i + j, 11 + 4 * i - j, i)
    if n >= 5:
        for i in range(4): B(29 - i, 18 + i, 48 + (i >> 1) * 64, 1)
    if n == 6:
        for i in range(4):
            for j in range(4): H(32 + 8 * i + j, 39 + 8 * i - j, i & 1)
    if n >= 3:
        for i in range(4): H(i, 7 - i, 0)
    if n >= 4:
        for i in range(2): B(13 - i, 10 + i, 32, 1)
    if n >= 5:
        for i in range(2):
            for j in range(4): H(16 + i * 8 + j, 23 + i * 8 - j, i)
    if n == 6:
        for i in range(8): B(59 - i, 36 + i, 48 if i < 4 else 112, 1)
    if n >= 4:
        for i in range(8): H(i, 15 - i, 0)
    if n >= 5:
        for i in range(4): B(27 - i, 20 + i, 32, 1)
    if n == 6:
        for i in range(8):
            H(32 + i, 47 - i, 0)
            H(48 + i, 63 - i, 1)
    if n >= 5:
        for i in range(16): H(i, 31 - i, 0)
    if n == 6:
        for i in range(8): B(55 - i, 40 + i, 32, 1)
    if n == 6:
        for i in range(32): H(i, 63 - i, 0)
    load = [brev(n, i) for i in range(1 << n)]
    store = [(i, False) for i in range(1 << n)]
    return r.ops, load, store


def adst(n):
    """Inverse ADST8 / ADST16 processes (7.13.2.6, 7.13.2.7) with their input and output permutations."""
    r = Rec(n)
    B, H = r.B, r.H
    if n == 3:
        for i in range(4): B(2 * i, 2 * i + 1, 60 - 16 * i, 1)
        for i in range(4): H(i, 4 + i, 0)
        for i in range(2): B(4 + 3 * i, 5 + i, 48 - 32 * i, 1)
        for i in range(2):
            for j in range(2): H(4 * j + i, 2 + 4 * j + i, 0)
        for i in range(2): B(2 + 4 * i, 3 + 4 * i, 32, 1)
    else:
        for i in range(8): B(2 * i, 2 * i + 1, 62 - 8 * i, 1)
        for i in range(8): H(i, 8 + i, 0)
        for i in range(2):
            B(8 + 2 * i, 9 + 2 * i, 56 - 32 * i, 1)
            B(13 + 2 * i, 12 + 2 * i, 8 + 32 * i, 1)
        for i in range(4):
            for j in range(2): H(8 * j + i, 4 + 8 * j + i, 0)
        for i in range(2):
            for j in range(2): B(4 + 8 * j + 3 * i, 5 + 8 * j + i, 48 - 32 * i, 1)
        for i in range(2):
            for j in range(4): H(4 * j + i, 2 + 4 * j + i, 0)
        for i in range(4): B(2 + 4 * i, 3 + 4 * i, 32, 1)
    n0 = 1 << n
    load = [i - 1 if i & 1 else n0 - i - 1 for i in range(n0)]
    store = []
    for i in range(n0):
        a = (i >> 3) & 1
        b = ((i >> 2) & 1) ^ ((i >> 3) & 1)
        c = ((i >> 1) & 1) ^ ((i >> 2) & 1)
        d = (i & 1) ^ ((i >> 1) & 1)
        store.append((((d << 3) | (c << 2) | (b << 1) | a) >> (4 - n), bool(i & 1)))
    return r.ops, load, store


def emit(name, n, ops, load, store, w):
    n0 = 1 << n
    w(f'        /// <summary>{name}: in place on t[0 .. {n0 - 1}], H results clamped to lo .. hi.</summary>\n')
    w(f'        static void {name}(Span<long> t, long lo, long hi)\n        {{\n')
    w('            _ = t[%d];\n' % (n0 - 1))
    for i in range(n0): w(f'            long t{i} = t[{load[i]}];\n')
    w('            long x, y;\n')
    for op in ops:
        if op[0] == 'B':
            _, a, b, c, s, flip = op
            w(f'            x = t{a} * ({c}) - t{b} * ({s}); y = t{a} * ({s}) + t{b} * ({c});\n')
            if flip: w(f'            t{a} = (y + 2048) >> 12; t{b} = (x + 2048) >> 12;\n')
            else: w(f'            t{a} = (x + 2048) >> 12; t{b} = (y + 2048) >> 12;\n')
        else:
            _, a, b = op
            w(f'            x = t{a}; y = t{b}; t{a} = Clamp(x + y, lo, hi); t{b} = Clamp(x - y, lo, hi);\n')
    for i in range(n0):
        src, neg = store[i]
        w(f'            t[{i}] = {"-" if neg else ""}t{src};\n')
    w('        }\n\n')


def main(out):
    with open(out, 'w') as f:
        w = f.write
        w('// <auto-generated>\n// The 1D inverse DCT and ADST processes of the AV1 specification (7.13.2) as straight-line code, written by\n'
          '// tests/oracle/gen_av1_transforms.py. Do not edit.\n// </auto-generated>\nusing System;\nusing System.Runtime.CompilerServices;\n\n'
          'namespace Aardvark.ImageCodecs.Av1\n{\n    internal sealed partial class Av1Transform\n    {\n'
          '        [MethodImpl(MethodImplOptions.AggressiveInlining)]\n'
          '        static long Clamp(long v, long lo, long hi) => v < lo ? lo : v > hi ? hi : v;\n\n')
        for n in range(2, 7):
            ops, load, store = dct(n)
            emit(f'Dct{1 << n}', n, ops, load, store, w)
        for n in (3, 4):
            ops, load, store = adst(n)
            emit(f'Adst{1 << n}K', n, ops, load, store, w)
        w('    }\n}\n')


if __name__ == '__main__':
    main(sys.argv[1])
