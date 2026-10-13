#!/usr/bin/env python3
"""Generates tests/fixtures/compression: raw data shapes (<shape>.bin) and reference-compressed variants
(<shape>__<variant>.<zlib|deflate|zst|xz|lzma>) written by zlib, liblzma and zstd. Deterministic for fixed
library versions; the committed files are what the tests use.

Needs: python3 (zlib, lzma), numpy, the `zstd` and `xz` command line tools.
"""
import lzma
import os
import struct
import subprocess
import sys
import zlib

import numpy as np

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "fixtures", "compression")


def rng(seed):
    return np.random.RandomState(seed)


WORDS = ("the of and image codec pixel stream window a to in is that block huffman distance length literal "
         "compress table buffer value for with this from data byte").split()


def text(n, seed):
    r = rng(seed)
    out = []
    size = 0
    while size < n:
        w = WORDS[int(r.random_sample() ** 2 * len(WORDS))]
        sep = ".\n" if r.randint(12) == 0 else (" %d " % r.randint(100000) if r.randint(20) == 0 else " ")
        out.append(w + sep)
        size += len(w) + len(sep)
    return "".join(out).encode("ascii")[:n]


def image(w, h, ch, seed, sixteen=False):
    """PNG-filtered scanlines (filter byte + residuals) of a smooth, slightly noisy image."""
    r = rng(seed)
    y, x, c = np.mgrid[0:h, 0:w, 0:ch].astype(np.float64)
    v = 0.5 + 0.3 * np.sin(x * 0.031 + c) * np.cos(y * 0.047 - c) + 0.15 * np.sin((x + y) * 0.11)
    v += (r.random_sample(v.shape) - 0.5) * np.where(y % 64 < 40, 0.012, 0.06)
    if sixteen:
        q = np.clip(v * 65535, 0, 65535).astype(">u2")
    else:
        q = np.clip(v * 255, 0, 255).astype(np.uint8)
    raw = np.frombuffer(q.tobytes(), np.uint8).reshape(h, -1).astype(np.int32)
    bpp = ch * (2 if sixteen else 1)
    rows = []
    for j in range(h):
        cur = raw[j]
        left = np.concatenate([np.zeros(bpp, np.int32), cur[:-bpp]])
        up = raw[j - 1] if j else np.zeros_like(cur)
        f = 1 if j % 2 else 2
        rows.append(bytes([f]) + ((cur - (left if f == 1 else up)) & 255).astype(np.uint8).tobytes())
    return b"".join(rows)


def runs(n, seed):
    r = rng(seed)
    out = bytearray()
    while len(out) < n:
        out += bytes([r.randint(5)]) * (1 + r.randint(2000 if r.randint(4) == 0 else 12))
    return bytes(out[:n])


def shapes():
    s = {}
    s["empty"] = b""
    s["one"] = b"\x2a"
    s["zeros"] = bytes(70000)
    s["random"] = rng(1).bytes(6000)
    s["text"] = text(24000, 2)
    s["image"] = image(96, 70, 3, 3)
    s["image16"] = image(60, 40, 3, 4, True)
    s["runs"] = runs(30000, 5)
    # > 64 KiB of varying content: several deflate blocks, several zstd blocks, LZMA2 chunk type changes
    s["big"] = text(50000, 6) + rng(7).bytes(9000) + image(120, 70, 3, 8) + bytes(12000) + text(45000, 9)
    # repeats at long distances (right up to the windows of the formats that have one)
    far = bytearray(rng(10).bytes(32768))
    for i in range(32768, 70000):  # zlib itself only looks back 32506 bytes; hand_alldist covers the full 32768
        far.append(far[i - 32500 + (i // 7000) % 3] if i % 331 else i & 255)
    s["far"] = bytes(far)
    return s


# ---------------------------------------------------------------------------------- hand made deflate streams

LEN_BASE = [3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258]
LEN_EXTRA = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0]
DIST_BASE = [1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073,
             4097, 6145, 8193, 12289, 16385, 24577]
DIST_EXTRA = [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13]
ORDER = [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15]


class Bits:
    def __init__(self):
        self.out = bytearray()
        self.acc = 0
        self.n = 0

    def put(self, value, bits):
        self.acc |= value << self.n
        self.n += bits
        while self.n >= 8:
            self.out.append(self.acc & 255)
            self.acc >>= 8
            self.n -= 8

    def code(self, code, bits):  # Huffman codes go out most significant bit first
        for i in range(bits - 1, -1, -1):
            self.put((code >> i) & 1, 1)

    def align(self):
        if self.n:
            self.put(0, 8 - self.n)

    def bytes(self):
        self.align()
        return bytes(self.out)


def canonical(lens):
    count = [0] * 16
    for l in lens:
        count[l] += 1
    count[0] = 0
    code, nxt = 0, [0] * 16
    for b in range(1, 16):
        code = (code + count[b - 1]) << 1
        nxt[b] = code
    codes = []
    for l in lens:
        codes.append(nxt[l])
        nxt[l] += 1
    return codes


def len_sym(length):
    for i in range(28, -1, -1):
        if length >= LEN_BASE[i]:
            if i == 28 or length < LEN_BASE[i] + (1 << LEN_EXTRA[i]):
                return i
    raise ValueError(length)


def dist_sym(dist):
    for i in range(29, -1, -1):
        if dist >= DIST_BASE[i]:
            return i
    raise ValueError(dist)


def put_symbols(b, syms, lit_lens, dist_lens):
    """syms: ints (literals) or (length, distance) tuples."""
    lc, dc = canonical(lit_lens), canonical(dist_lens)
    for s in syms:
        if isinstance(s, tuple):
            i = len_sym(s[0])
            if s[0] == 258:
                i = 28
            b.code(lc[257 + i], lit_lens[257 + i])
            b.put(s[0] - LEN_BASE[i], LEN_EXTRA[i])
            j = dist_sym(s[1])
            b.code(dc[j], dist_lens[j])
            b.put(s[1] - DIST_BASE[j], DIST_EXTRA[j])
        else:
            b.code(lc[s], lit_lens[s])
    b.code(lc[256], lit_lens[256])


FIXED_LIT = [8] * 144 + [9] * 112 + [7] * 24 + [8] * 8
FIXED_DIST = [5] * 32


def fixed_block(b, syms, final):
    b.put(1 if final else 0, 1)
    b.put(1, 2)
    put_symbols(b, syms, FIXED_LIT, FIXED_DIST)


def stored_block(b, data, final):
    b.put(1 if final else 0, 1)
    b.put(0, 2)
    b.align()
    b.put(len(data), 16)
    b.put(len(data) ^ 0xFFFF, 16)
    b.out += data


def dynamic_block(b, syms, lit_lens, dist_lens, final, cl_lens=None, use_repeats=True):
    """Writes a dynamic block with the given code lengths (trailing zero lengths are trimmed)."""
    hlit = max(257, max(i + 1 for i, l in enumerate(lit_lens) if l))
    hdist = max(1, max([i + 1 for i, l in enumerate(dist_lens) if l] or [1]))
    seq = list(lit_lens[:hlit]) + list(dist_lens[:hdist])
    toks = []
    i = 0
    while i < len(seq):
        v = seq[i]
        run = 1
        while i + run < len(seq) and seq[i + run] == v:
            run += 1
        i += run
        if use_repeats and v == 0:
            while run >= 11:
                r = min(run, 138)
                toks.append((18, r - 11))
                run -= r
            if run >= 3:
                toks.append((17, run - 3))
                run = 0
        elif use_repeats:
            toks.append((v, 0))
            run -= 1
            while run >= 3:
                r = min(run, 6)
                toks.append((16, r - 3))
                run -= r
        toks += [(v, 0)] * run
    if cl_lens is None:
        # a simple valid code length code: 5 bits for everything that is used, padded to a complete code
        used = sorted(set(t[0] for t in toks))
        cl_lens = [0] * 19
        for u in range(19):
            cl_lens[u] = 5
        # 19 codes of length 5 leave room: give the first 13 symbols length 4 to complete the code (13*2+6 = 32)
        for u in range(13):
            cl_lens[u] = 4
    cc = canonical(cl_lens)
    hclen = max(4, max(i + 1 for i, o in enumerate(ORDER) if cl_lens[o]))
    b.put(1 if final else 0, 1)
    b.put(2, 2)
    b.put(hlit - 257, 5)
    b.put(hdist - 1, 5)
    b.put(hclen - 4, 4)
    for i in range(hclen):
        b.put(cl_lens[ORDER[i]], 3)
    for s, e in toks:
        b.code(cc[s], cl_lens[s])
        if s == 16:
            b.put(e, 2)
        elif s == 17:
            b.put(e, 3)
        elif s == 18:
            b.put(e, 7)
    put_symbols(b, syms, lit_lens, dist_lens)


def zwrap(deflate, raw):
    return b"\x78\x9c" + deflate + struct.pack(">I", zlib.adler32(raw))


def handmade():
    """name -> zlib stream; the expected output is whatever zlib itself inflates it to."""
    out = {}
    r = rng(20)

    # every length and every distance code, including the largest distance and the longest match
    b = Bits()
    stored_block(b, r.bytes(32768), False)
    syms = []
    dists = sorted(set(DIST_BASE + [d + (1 << e) - 1 for d, e in zip(DIST_BASE, DIST_EXTRA)] + [32768, 32767, 6, 8, 16]))
    for i, length in enumerate(range(3, 259)):
        syms.append((length, dists[i % len(dists)]))
        syms.append(int(r.randint(256)))
    syms += [(258, 32768)] * 3 + [(3, 32768), (258, 1), (258, 2), (258, 3), (4, 32768)]
    fixed_block(b, syms, True)
    out["hand_alldist"] = b.bytes()

    # dynamic block whose codes use every length 1..15: forces second level tables for both alphabets
    lit = [0] * 286
    order = list(range(97, 110)) + [257, 256, 285]
    for i, s in enumerate(order):
        lit[s] = min(i + 1, 15)
    dist = [0] * 30
    for i in range(16):
        dist[i] = min(i + 1, 15)
    syms = [97 + int(r.randint(13) * r.random_sample()) for _ in range(400)]
    for i in range(300):
        syms.append((3 if i % 3 else 258, DIST_BASE[i % 16] if DIST_BASE[i % 16] < 300 else 1 + i % 200))
        syms.append(97 + i % 13)
    b = Bits()
    dynamic_block(b, syms, lit, dist, True)
    out["hand_longcodes"] = b.bytes()

    # a single distance code of length one (an incomplete code that inflate must accept)
    lit = [0] * 286
    for s in range(256):
        lit[s] = 9
    lit[256] = lit[259] = lit[264] = lit[285] = 9
    lit[257], lit[258], lit[260], lit[261], lit[262], lit[263] = 2, 3, 4, 5, 6, 7
    # in 512ths: 256 literals + 4 + 128 + 64 + 32 + 16 + 8 + 4 = 512, a complete code
    dist = [0] * 30
    dist[3] = 1
    syms = [int(x) for x in r.randint(0, 256, 300)]
    for i in range(200):
        syms.append(([3, 4, 6, 7, 8, 9, 10, 258][i % 8], 4))
        syms.append(int(r.randint(256)))
    b = Bits()
    dynamic_block(b, syms, lit, dist, True)
    out["hand_onedist"] = b.bytes()

    # literals only, no distance code at all (HDIST = 1 with a zero length), written without repeat codes
    lit = [0] * 286
    for s in range(256):
        lit[s] = 8 if s < 255 else 9
    lit[256] = 9
    dist = [0] * 30
    b = Bits()
    dynamic_block(b, [int(x) for x in r.randint(0, 256, 2000)], lit, dist, True, use_repeats=False)
    out["hand_nodist"] = b.bytes()

    # stored, fixed and dynamic blocks mixed, with empty blocks of each kind in between
    b = Bits()
    stored_block(b, b"", False)
    fixed_block(b, [], False)
    stored_block(b, b"stored block one. ", False)
    fixed_block(b, [ord(c) for c in "fixed block. "] + [(13, 13), (18, 31)], False)
    lit = [0] * 286
    for s in range(256):
        lit[s] = 8 if s < 255 else 9
    lit[256] = 9
    dynamic_block(b, [], lit, [0] * 30, False)
    stored_block(b, r.bytes(65535), False)
    fixed_block(b, [(258, 65535 // 2), (258, 1), 65, 66, 67, (5, 3)], False)
    stored_block(b, b"", True)
    out["hand_mixed"] = b.bytes()
    return out


# -------------------------------------------------------------------------------------------------- writers

def write(name, data):
    with open(os.path.join(OUT, name), "wb") as f:
        f.write(data)


def zl(data, level=6, wbits=15, mem=8, strategy=zlib.Z_DEFAULT_STRATEGY):
    c = zlib.compressobj(level, zlib.DEFLATED, wbits, mem, strategy)
    return c.compress(data) + c.flush()


def gen_zlib(s):
    for name, data in s.items():
        levels = [6] if name in ("big", "far", "zeros") else [1, 6, 9]
        for lv in levels:
            write("%s__l%d.zlib" % (name, lv), zl(data, lv))
    for name in ("text", "image", "random", "runs", "one", "empty"):
        d = s[name]
        if name not in ("image", "runs"):
            write("%s__l0.zlib" % name, zl(d, 0))
        write("%s__fixed.zlib" % name, zl(d, 6, strategy=zlib.Z_FIXED))
        write("%s__huffman.zlib" % name, zl(d, 6, strategy=zlib.Z_HUFFMAN_ONLY))
        write("%s__rle.zlib" % name, zl(d, 6, strategy=zlib.Z_RLE))
        write("%s__filtered.zlib" % name, zl(d, 6, strategy=zlib.Z_FILTERED))
        write("%s__mem1.zlib" % name, zl(d, 6, mem=1))  # tiny symbol buffer: very many dynamic blocks
        write("%s__w9.zlib" % name, zl(d, 9, wbits=9))
        write("%s__raw.deflate" % name, zl(d, 6, wbits=-15))
    for lv in (2, 3, 4, 5, 7, 8):
        write("text__l%d.zlib" % lv, zl(s["text"], lv))
    # flushes: empty stored blocks, byte aligned restarts, history resets
    d = s["text"]
    c = zlib.compressobj(6)
    out = b""
    for i, flush in enumerate((zlib.Z_SYNC_FLUSH, zlib.Z_FULL_FLUSH, zlib.Z_SYNC_FLUSH, zlib.Z_FULL_FLUSH)):
        out += c.compress(d[i * 5000:(i + 1) * 5000]) + c.flush(flush)
    out += c.compress(d[20000:]) + c.flush()
    write("text__flush.zlib", out)
    for name, deflate in handmade().items():
        raw = zlib.decompress(deflate, -15)
        write(name + ".bin", raw)
        write(name + "__hand.zlib", zwrap(deflate, raw))


def zstd_cli(data, *args):
    return subprocess.run(["zstd", "-q", "-c", "--no-progress"] + list(args), input=data, stdout=subprocess.PIPE, check=True).stdout


def gen_zstd(s):
    mix = s["text"][:7000] + s["image"][:7000] + s["runs"][:2000]
    write("mix.bin", mix)
    for lv in list(range(1, 20)):
        write("mix__l%d.zst" % lv, zstd_cli(mix, "-%d" % lv))
    for lv in (20, 22):
        write("mix__l%d.zst" % lv, zstd_cli(mix, "--ultra", "-%d" % lv))
    for fast in (1, 3, 10, 50):
        write("mix__fast%d.zst" % fast, zstd_cli(mix, "--fast=%d" % fast))
    for name, data in s.items():
        if name == "far":  # a deflate window test; the long distance zstd test generates its data at run time
            continue
        write("%s__l3.zst" % name, zstd_cli(data, "-3"))
        if name not in ("big", "far"):
            write("%s__l3check.zst" % name, zstd_cli(data, "-3", "--check"))
        if name not in ("empty", "one"):
            write("%s__l19.zst" % name, zstd_cli(data, "-19", "--no-check"))
    big = s["big"]
    write("big__long.zst", zstd_cli(big, "-12", "--long=27", "--check"))
    write("big__w10.zst", zstd_cli(big, "-5", "--zstd=wlog=10"))  # smallest window: 1 KiB blocks
    write("text__nolit.zst", zstd_cli(s["text"], "-3", "--no-compress-literals"))
    # a file argument stores the content size in the header and "single segment" for small inputs; stdin does not
    tmp = os.path.join(OUT, "_tmp.bin")
    for name in ("text", "one", "empty", "big", "zeros"):
        write("_tmp.bin", s[name])
        z = subprocess.run(["zstd", "-q", "-c", "-7", "--check", tmp], stdout=subprocess.PIPE, check=True).stdout
        write("%s__sized.zst" % name, z)
    os.remove(tmp)
    # several frames, with skippable frames before, between and after
    skip = struct.pack("<II", 0x184D2A50, 11) + b"hello world"
    skip2 = struct.pack("<II", 0x184D2A5F, 0)
    parts = [s["text"][:9000], s["text"][9000:], s["image"], b"", s["zeros"][:5000]]
    write("frames.bin", b"".join(parts))
    frames = skip + zstd_cli(parts[0], "-3", "--check") + skip2 + zstd_cli(parts[1], "-19") + zstd_cli(parts[2], "--fast=2") \
        + skip + zstd_cli(parts[3], "-1") + zstd_cli(parts[4], "-5", "--check") + skip2
    write("frames__multi.zst", frames)
    # the content checksum (xxHash64) over every length around its 32, 8 and 4 byte steps
    t = s["text"]
    write("xxlens.bin", b"".join(t[:n] for n in range(0, 101)))
    write("xxlens__multi.zst", b"".join(zstd_cli(t[:n], "-1", "--check") for n in range(0, 101)))


def gen_zstd_dict(s):
    """Dictionaries: one trained by zstd (structured: entropy tables + content) and one of plain content."""
    import shutil
    import tempfile
    r = rng(77)

    def record(i):
        fields = ["id", "name", "width", "height", "channels", "codec", "created", "tags"]
        vals = [str(i * 7919 % 100000), WORDS[r.randint(len(WORDS))] + "_" + WORDS[r.randint(len(WORDS))], str(r.randint(8000)), str(r.randint(8000)),
                str(r.randint(1, 5)), ["png", "jpeg", "tiff", "exr", "gif"][r.randint(5)], "2026-%02d-%02dT%02d:%02d" % (r.randint(1, 13), r.randint(1, 29), r.randint(24), r.randint(60)),
                "[" + ",".join('"%s"' % WORDS[r.randint(len(WORDS))] for _ in range(r.randint(1, 6))) + "]"]
        return ("{" + ",".join('"%s":"%s"' % kv for kv in zip(fields, vals)) + "}\n").encode()

    tmp = tempfile.mkdtemp(dir=OUT)
    try:
        names = []
        for i in range(600):
            n = os.path.join(tmp, "s%04d" % i)
            with open(n, "wb") as f:
                f.write(b"".join(record(i * 3 + k) for k in range(1 + r.randint(4))))
            names.append(n)
        trained = os.path.join(tmp, "trained")
        subprocess.run(["zstd", "-q", "--train", "--maxdict=3000", "-o", trained] + names, check=True)
        write("trained.zdict", open(trained, "rb").read())
        rawdict = os.path.join(tmp, "raw")
        with open(rawdict, "wb") as f:
            f.write(b"".join(record(900000 + k) for k in range(25)))
        write("rawcontent.zdict", open(rawdict, "rb").read())
        small = b"".join(record(5000 + k) for k in range(3))
        large = b"".join(record(7000 + k) for k in range(900))  # several blocks: the dictionary tables only start it off
        write("dictsmall.bin", small)
        write("dictlarge.bin", large)
        for name, data in (("dictsmall", small), ("dictlarge", large)):
            for tag, d in (("trained", trained), ("rawcontent", rawdict)):
                for lv in (("1", "3", "19") if name == "dictsmall" else ("3",) if tag == "rawcontent" else ("1", "19")):
                    write("%s__%s_l%s.zstd" % (name, tag, lv), subprocess.run(["zstd", "-q", "-c", "-" + lv, "--check", "-D", d], input=data, stdout=subprocess.PIPE, check=True).stdout)
            if name == "dictsmall":
                write("%s__trained_noid.zstd" % name, subprocess.run(["zstd", "-q", "-c", "-5", "--no-dictID", "-D", trained], input=data, stdout=subprocess.PIPE, check=True).stdout)
    finally:
        shutil.rmtree(tmp)


def bcj_data():
    """Bytes that make every BCJ filter convert something: branch instructions of all architectures between noise."""
    r = rng(42)
    out = bytearray()

    def le(v):
        return struct.pack("<I", v & 0xFFFFFFFF)

    def be(v):
        return struct.pack(">I", v & 0xFFFFFFFF)

    while len(out) < 5000:
        k = r.randint(13)
        if 5 <= k <= 10 and r.randint(4):  # usually aligned as real code is
            out += r.bytes(-len(out) % (16 if k == 10 else 4))
        if k < 3:
            out += r.bytes(r.randint(1, 40))
        elif k == 3:  # x86 call/jmp, sometimes back to back or cut short
            for _ in range(r.randint(1, 4)):
                out += bytes([0xE8 if r.randint(2) else 0xE9]) + le(int(r.randint(-70000, 70000)))[:(4 if r.randint(5) else r.randint(1, 4))]
        elif k == 4:
            out += bytes([0xE8, 0xE8, 0xE9, 0, 0, 0xFF, 0xFF, 0xE8, 0xFF, 0xFF, 0xFF])[:r.randint(3, 12)]
        elif k == 5:  # ARM bl
            out += le(0xEB000000 | r.randint(1 << 24))
        elif k == 6:  # ARM64 bl, adrp
            out += le(0x94000000 | r.randint(1 << 26)) + le(0x90000000 | (r.randint(4) << 29) | r.randint(1 << 24))
        elif k == 7:  # PowerPC bl
            out += be(0x48000001 | (r.randint(1 << 24) << 2))
        elif k == 8:  # SPARC call
            out += be((0x40000000 if r.randint(2) else 0x7FC00000) | r.randint(1 << 22))
        elif k == 9:  # Thumb bl
            out += bytes([r.randint(256), 0xF0 | r.randint(8), r.randint(256), 0xF8 | r.randint(8)])
        elif k == 10:  # IA-64 bundle with branch slots
            b = bytearray(r.bytes(16))
            b[0] = (b[0] & 0xE0) | [0x10, 0x11, 0x12, 0x13, 0x16, 0x17, 0x18, 0x19, 0x1C, 0x1D][r.randint(10)]
            v = int.from_bytes(b, "little")
            for slot in range(3):
                pos = 5 + 41 * slot
                if r.randint(3):
                    v &= ~(0xF << (pos + 37))
                    v |= 5 << (pos + 37)
                    v &= ~(7 << (pos + 9))
            out += v.to_bytes(16, "little")
        elif k == 11:  # RISC-V jal and auipc pairs
            out += le(0x6F | ([1, 5, 0, 3][r.randint(4)] << 7) | (r.randint(1 << 20) << 12))
            rd = r.randint(0, 32)
            out += le(0x17 | (rd << 7) | (r.randint(1 << 20) << 12))
            out += le([0x67, 0x13, 0x03, 0x23, 0x01][r.randint(5)] | (r.randint(32) << 7) | (r.randint(8) << 12) | ((rd if r.randint(4) else r.randint(32)) << 15) | (r.randint(1 << 12) << 20))
        else:  # input that already looks like the RISC-V filter's marker word
            out += le(0x3117 | (r.randint(1 << 18) << 14)) + r.bytes(4)
    return bytes(out[:5000])


def gen_xz(s):
    for name, data in s.items():
        if name != "far":
            write("%s__p6.xz" % name, lzma.compress(data, lzma.FORMAT_XZ, lzma.CHECK_CRC64, 6))
    d = s["text"]
    for check, tag in ((lzma.CHECK_NONE, "none"), (lzma.CHECK_CRC32, "crc32"), (lzma.CHECK_SHA256, "sha256")):
        write("text__%s.xz" % tag, lzma.compress(d, lzma.FORMAT_XZ, check, 3))
    for p in (0, 1, 9, 9 | lzma.PRESET_EXTREME):
        write("text__p%d%s.xz" % (p & 15, "e" if p > 9 else ""), lzma.compress(d, lzma.FORMAT_XZ, lzma.CHECK_CRC32, p))
        write("image__p%d%s.xz" % (p & 15, "e" if p > 9 else ""), lzma.compress(s["image"], lzma.FORMAT_XZ, lzma.CHECK_CRC32, p))
    # delta filters (what TIFF writers put in front of LZMA2 when a predictor is used)
    for name in ("image", "image16", "text", "one", "empty"):
        for dist in (1, 3, 6, 256):
            f = [{"id": lzma.FILTER_DELTA, "dist": dist}, {"id": lzma.FILTER_LZMA2, "preset": 6}]
            write("%s__delta%d.xz" % (name, dist), lzma.compress(s[name], lzma.FORMAT_XZ, lzma.CHECK_CRC32, filters=f))
    f = [{"id": lzma.FILTER_DELTA, "dist": 3}, {"id": lzma.FILTER_DELTA, "dist": 1}, {"id": lzma.FILTER_DELTA, "dist": 7},
         {"id": lzma.FILTER_LZMA2, "preset": 2}]
    write("image__delta3x.xz", lzma.compress(s["image"], lzma.FORMAT_XZ, lzma.CHECK_CRC64, filters=f))
    # literal context / position bits and small dictionaries
    for lc, lp, pb in ((0, 0, 0), (4, 0, 0), (0, 4, 4), (1, 3, 2), (3, 1, 1), (2, 2, 4)):
        f = [{"id": lzma.FILTER_LZMA2, "preset": 5, "lc": lc, "lp": lp, "pb": pb, "dict_size": 1 << 16}]
        write("text__lc%dlp%dpb%d.xz" % (lc, lp, pb), lzma.compress(s["text"], lzma.FORMAT_XZ, lzma.CHECK_CRC32, filters=f))
        write("image16__lc%dlp%dpb%d.xz" % (lc, lp, pb), lzma.compress(s["image16"], lzma.FORMAT_XZ, lzma.CHECK_CRC32, filters=f))
    f = [{"id": lzma.FILTER_LZMA2, "preset": 9, "dict_size": 4096, "nice_len": 273, "mf": lzma.MF_BT4, "depth": 200}]
    write("runs__dict4k.xz", lzma.compress(s["runs"], lzma.FORMAT_XZ, lzma.CHECK_CRC32, filters=f))
    f = [{"id": lzma.FILTER_LZMA2, "preset": 0, "mode": lzma.MODE_FAST, "mf": lzma.MF_HC3, "nice_len": 8}]
    write("text__hc3.xz", lzma.compress(s["text"], lzma.FORMAT_XZ, lzma.CHECK_CRC32, filters=f))
    # several blocks (block headers then carry the sizes), several streams, stream padding
    blocks = subprocess.run(["xz", "-c", "-4", "--block-size=40000", "-T1"], input=s["big"], stdout=subprocess.PIPE, check=True).stdout
    write("big__blocks.xz", blocks)
    threads = subprocess.run(["xz", "-c", "-2", "--block-size=65536", "-T2", "--check=sha256"], input=s["big"], stdout=subprocess.PIPE, check=True).stdout
    write("big__threads.xz", threads)
    parts = [s["text"][:9000], s["text"][9000:], b"", s["image"], s["random"]]
    write("streams.bin", b"".join(parts))
    multi = lzma.compress(parts[0], lzma.FORMAT_XZ, lzma.CHECK_CRC32, 1) + bytes(8) \
        + lzma.compress(parts[1], lzma.FORMAT_XZ, lzma.CHECK_NONE, 6) \
        + lzma.compress(parts[2], lzma.FORMAT_XZ, lzma.CHECK_SHA256, 6) + bytes(4) \
        + lzma.compress(parts[3], lzma.FORMAT_XZ, lzma.CHECK_CRC64, filters=[{"id": lzma.FILTER_DELTA, "dist": 3}, {"id": lzma.FILTER_LZMA2, "preset": 9}]) \
        + lzma.compress(parts[4], lzma.FORMAT_XZ, lzma.CHECK_CRC32, 0) + bytes(12)
    write("streams__multi.xz", multi)
    # BCJ filters: machine code flavoured data for every architecture (see bcj_data), with and without a start offset
    f = [{"id": lzma.FILTER_X86}, {"id": lzma.FILTER_LZMA2, "preset": 1}]
    write("text__x86.xz", lzma.compress(s["text"], lzma.FORMAT_XZ, lzma.CHECK_CRC32, filters=f))
    code = bcj_data()
    write("bcj.bin", code)
    for name, align in (("x86", 1), ("powerpc", 4), ("ia64", 16), ("arm", 4), ("armthumb", 2), ("sparc", 4), ("arm64", 4), ("riscv", 2)):
        write("bcj__%s.xz" % name, subprocess.run(["xz", "-c", "--" + name, "--lzma2=preset=1"], input=code, stdout=subprocess.PIPE, check=True).stdout)
        write("bcj__%s_start.xz" % name, subprocess.run(["xz", "-c", "--%s=start=%d" % (name, align * 4099 * 16), "--delta=dist=2", "--lzma2=preset=0"],
                                                         input=code, stdout=subprocess.PIPE, check=True).stdout)
    # legacy .lzma: liblzma writes "unknown size + end marker"; the patched copies declare the size instead
    for name in ("text", "image", "big", "one", "empty", "random", "zeros"):
        z = lzma.compress(s[name], lzma.FORMAT_ALONE, preset=5)
        write("%s__alone.lzma" % name, z)
        if name != "big":
            write("%s__sized.lzma" % name, z[:5] + struct.pack("<Q", len(s[name])) + z[13:])
    f = [{"id": lzma.FILTER_LZMA1, "preset": 9, "lc": 0, "lp": 4, "pb": 3, "dict_size": 1 << 20}]
    write("text__lc0lp4.lzma", lzma.compress(s["text"], lzma.FORMAT_ALONE, filters=f))


def main():
    os.makedirs(OUT, exist_ok=True)
    for f in os.listdir(OUT):
        os.remove(os.path.join(OUT, f))
    s = shapes()
    for name, data in s.items():
        write(name + ".bin", data)
    gen_zlib(s)
    gen_zstd(s)
    gen_zstd_dict(s)
    gen_xz(s)
    # every fixture must round trip through its own reference decoder
    total = 0
    for f in sorted(os.listdir(OUT)):
        total += os.path.getsize(os.path.join(OUT, f))
    print("%d files, %d bytes" % (len(os.listdir(OUT)), total))


if __name__ == "__main__":
    sys.exit(main())
