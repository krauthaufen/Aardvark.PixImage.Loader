"""Generates tests/fixtures/etc_astc: raw compressed blocks (name.bin) and reference decodes (name.bin.ref, PXRF).

The block data is laid out as the blocks of one image in raster order (x fastest, then y, then z); the reference holds
that image (3D ASTC: the z slices stacked vertically, so height = H * D). The C# tests (EtcAstcTests.cs) parse the
format and block size from the file name:

  etc1_*, etc2_rgb_*, etc2_a1_*, etc2_rgba_*        RGBA8 reference
  eac_r11_*, eac_rg11_*, eac_r11s_*, eac_rg11s_*    8 bit reference of the decoder below (see the tests for how the
                                                    exact 11 bit values are checked against it)
  astc_<mode>_<W>x<H>[x<D>]_<tag>                   mode ldr / srgb: RGBA8, hdr: RGBA float16

Oracles (only run, never read or ported):
  * ETC / EAC: texture2ddecoder (pip), decoding random blocks forced into every mode (individual, differential, T, H,
    planar; punch-through opaque and transparent; EAC with every table, multiplier 0 and base -128) and blocks
    compressed by etcpak (pip) from a synthetic image. Where texture2ddecoder departs from the specification the
    specified behaviour is applied to its input or output (transparent punch-through texels are (0, 0, 0, 0); a signed
    EAC base of -128 is read as -127), see gen_etc.
  * ASTC: ARM astcenc (Apache-2.0, https://github.com/ARM-software/astc-encoder), built locally; $ASTCENC points at the
    binary. Synthetic images (gradients, 2 to 4 colour mosaics, independent alpha, grey, constant, noise; LDR and HDR)
    are compressed with -cl / -cs / -ch / -cH at -exhaustive for every 2D and 3D footprint and decoded with
    -dl / -ds / -dH; LDR data is also decoded in HDR mode and HDR data in LDR mode (error colour). Bit flipped copies
    of those blocks and hand made blocks (void extents, reserved modes, illegal grids) cover the illegal encodings;
    blocks with chosen endpoint values cover every colour endpoint mode and HDR sub-mode. sRGB alpha: see decode_ref.

usage: ASTCENC=/path/to/astcenc ~/.venvs/etcref/bin/python gen_etc_astc.py [--report]
   (venv with texture2ddecoder, etcpak, numpy, pillow)
"""
import os
import struct
import subprocess
import sys
import tempfile
import numpy as np
import texture2ddecoder as t2d
import etcpak
from PIL import Image
from pxref import write_ref

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'fixtures', 'etc_astc')
ASTCENC = os.environ.get('ASTCENC', 'astcenc')
REPORT = '--report' in sys.argv
rng = np.random.default_rng(2026)


def save(name, blocks, ref):
    with open(os.path.join(OUT, name + '.bin'), 'wb') as f:
        f.write(bytes(blocks))
    write_ref(os.path.join(OUT, name + '.bin.ref'), ref)


# ================================================================ ETC / EAC

def s3(v):
    return v - 8 if v & 4 else v


def etc_mode(b, punch):
    """Mode of an ETC2 colour block (selection rules of the specification, used to steer the random blocks)."""
    hi = int.from_bytes(b[:4], 'big')
    if not punch and not hi >> 1 & 1:
        return 'individual'
    if not 0 <= (hi >> 27 & 31) + s3(hi >> 24 & 7) <= 31:
        return 'T'
    if not 0 <= (hi >> 19 & 31) + s3(hi >> 16 & 7) <= 31:
        return 'H'
    if not 0 <= (hi >> 11 & 31) + s3(hi >> 8 & 7) <= 31:
        return 'planar'
    return 'differential'


def random_color_blocks(modes, per_mode, punch=False, opaque=None):
    out = []
    for m in modes:
        got = 0
        while got < per_mode:
            b = bytearray(rng.integers(0, 256, 8, dtype=np.uint8).tobytes())
            if not punch and m != 'individual':
                b[3] |= 2          # differential bit 33
            if punch and opaque is not None:
                b[3] = b[3] | 2 if opaque else b[3] & ~2
            if etc_mode(b, punch) == m:
                out.append(bytes(b))
                got += 1
    return out


def random_eac_blocks(n, signed):
    out = []
    for i in range(n):
        b = bytearray(rng.integers(0, 256, 8, dtype=np.uint8).tobytes())
        b[1] = (b[1] & 0x0F) | ((i % 16) << 4) if i < 32 else b[1]      # every multiplier, including 0
        b[1] = (b[1] & 0xF0) | (i % 16) if 32 <= i < 48 else b[1]       # every table
        if signed and i % 7 == 0:
            b[0] = 0x80                                                 # -128, read as -127
        if i % 11 == 0:
            b[0] = 0xFF if i % 2 else 0x00
        out.append(bytes(b))
    return out


def image_of(blocks, bpb, cols):
    rows = (len(blocks) + cols - 1) // cols
    pad = rows * cols - len(blocks)
    return b''.join(blocks) + bytes(bpb * pad), cols * 4, rows * 4


def t2d_rgba(fn, data, w, h):
    a = np.frombuffer(fn(data, w, h), np.uint8).reshape(h, w, 4)
    return a[..., [2, 1, 0, 3]].copy()      # texture2ddecoder writes BGRA


def synthetic(w, h, seed):
    r = np.random.default_rng(seed)
    y, x = np.mgrid[0:h, 0:w].astype(float)
    img = np.zeros((h, w, 4))
    for c in range(4):
        img[..., c] = 0.5 + 0.5 * np.sin(x / (3 + c) + c) * np.cos(y / (4 + c))
    img[(x // 8 + y // 8) % 3 == 0] = r.random(4)
    img += 0.08 * r.random((h, w, 4))
    return (np.clip(img, 0, 1) * 255).astype(np.uint8)


def gen_etc():
    cols = 16
    sets = {
        'etc1_random': ([*random_color_blocks(['individual', 'differential'], 48)], t2d.decode_etc1),
        'etc2_rgb_random': ([*random_color_blocks(['individual', 'differential', 'T', 'H', 'planar'], 48)], t2d.decode_etc2),
        'etc2_a1_random': ([*random_color_blocks(['differential', 'T', 'H', 'planar'], 32, True, True),
                            *random_color_blocks(['differential', 'T', 'H', 'planar'], 32, True, False)], t2d.decode_etc2a1),
    }
    for name, (blocks, fn) in sets.items():
        data, w, h = image_of(blocks, 8, cols)
        a = t2d_rgba(fn, data, w, h)
        if fn is t2d.decode_etc2a1:
            # the specification (22.9) makes transparent punch-through texels (0, 0, 0, 0); texture2ddecoder keeps the
            # colour there, so only its alpha is taken for those texels
            a[a[..., 3] == 0] = 0
        save(name, data, a)
    # RGBA: EAC alpha in front of a colour block
    colors = random_color_blocks(['individual', 'differential', 'T', 'H', 'planar'], 24)
    alphas = random_eac_blocks(len(colors), False)
    data, w, h = image_of([a + c for a, c in zip(alphas, colors)], 16, cols)
    save('etc2_rgba_random', data, t2d_rgba(t2d.decode_etc2a8, data, w, h))

    # etcpak from a synthetic image
    img = synthetic(64, 48, 7)
    raw = img.tobytes()
    for name, comp, fn in [('etc1_etcpak', etcpak.compress_etc1_rgb, t2d.decode_etc1),
                           ('etc2_rgb_etcpak', etcpak.compress_etc2_rgb, t2d.decode_etc2),
                           ('etc2_rgba_etcpak', etcpak.compress_etc2_rgba, t2d.decode_etc2a8)]:
        data = comp(raw, 64, 48)
        save(name, data, t2d_rgba(fn, data, 64, 48))

    # EAC: references hold the oracle's 8 bit output of the channels (R, or R and G)
    for name, signed, two in [('eac_r11', False, False), ('eac_r11s', True, False), ('eac_rg11', False, True), ('eac_rg11s', True, True)]:
        n = 96
        blocks = random_eac_blocks(n, signed)
        if two:
            blocks = [a + b for a, b in zip(blocks, random_eac_blocks(n, signed)[::-1])]
        data, w, h = image_of(blocks, 16 if two else 8, cols)
        fn = {('eac_r11'): t2d.decode_eacr, 'eac_r11s': t2d.decode_eacr_signed, 'eac_rg11': t2d.decode_eacrg, 'eac_rg11s': t2d.decode_eacrg_signed}[name]
        oracle_in = data
        if signed:
            # a base codeword of -128 must be read as -127 (22.7); texture2ddecoder does not, so it decodes the
            # equivalent blocks with -127 written out
            q = bytearray(data)
            for i in range(0, len(q), 8):
                if q[i] == 0x80:
                    q[i] = 0x81
            oracle_in = bytes(q)
        a = t2d_rgba(fn, oracle_in, w, h)
        save(name + '_random', data, a[..., :2] if two else a[..., :1])
    img = synthetic(32, 32, 9)
    for name, comp, ch in [('eac_r11_etcpak', etcpak.compress_eac_r, 1), ('eac_rg11_etcpak', etcpak.compress_eac_rg, 2)]:
        data = comp(img.tobytes(), 32, 32)
        fn = t2d.decode_eacr if ch == 1 else t2d.decode_eacrg
        a = t2d_rgba(fn, data, 32, 32)
        save(name, data, a[..., :ch])


# ================================================================ ASTC

SIZES_2D = [(4, 4), (5, 4), (5, 5), (6, 5), (6, 6), (8, 5), (8, 6), (10, 5), (10, 6), (8, 8), (10, 8), (10, 10), (12, 10), (12, 12)]
ENDPOINT_SIZES = [(4, 4, 1), (8, 5, 1), (12, 12, 1), (3, 3, 3), (6, 6, 5)]
SIZES_3D = [(3, 3, 3), (4, 3, 3), (4, 4, 3), (4, 4, 4), (5, 4, 4), (5, 5, 4), (5, 5, 5), (6, 5, 5), (6, 6, 5), (6, 6, 6)]


def write_ktx(path, arr):
    """KTX 1 input for astcenc: arr (D, H, W, 4) uint8 or float16, top row first (as astcenc reads and writes it)."""
    d, h, w, _ = arr.shape
    if arr.dtype == np.uint8:
        gl = (0x1401, 1, 0x1908, 0x8058, 0x1908)
    else:
        gl = (0x140B, 2, 0x1908, 0x881A, 0x1908)
    hdr = b'\xabKTX 11\xbb\r\n\x1a\n' + struct.pack('<13I', 0x04030201, *gl, w, h, d if d > 1 else 0, 0, 1, 1, 0)
    data = np.ascontiguousarray(arr).tobytes()
    with open(path, 'wb') as f:
        f.write(hdr + struct.pack('<I', len(data)) + data)


def read_ktx(path):
    """astcenc's KTX output: (D, H, W, 4). It writes RGB when every alpha is 1.0; alpha is then filled in as 1.0."""
    b = open(path, 'rb').read()
    h = struct.unpack('<13I', b[12:64])
    gl_type, fmt, w, hh, d, kv = h[1], h[3], h[6], h[7], max(h[8], 1), h[12]
    o = 64 + kv
    n = struct.unpack('<I', b[o:o + 4])[0]
    dt = np.uint8 if gl_type == 0x1401 else np.float16
    c = {0x1908: 4, 0x1907: 3}[fmt]
    a = np.frombuffer(b[o + 4:o + 4 + n], dt).reshape(d, hh, w, c)
    if c == 3:
        a = np.concatenate([a, np.full((d, hh, w, 1), 255 if dt == np.uint8 else 1.0, dt)], axis=3)
    return a


def astc_file(path, blocks, dims, size):
    bw, bh, bd = dims
    w, h, d = size
    hdr = struct.pack('<I3B', 0x5CA1AB13, bw, bh, bd) + b''.join(v.to_bytes(3, 'little') for v in (w, h, d))
    with open(path, 'wb') as f:
        f.write(hdr + bytes(blocks))


def run(*args):
    r = subprocess.run([ASTCENC, *args, '-silent'], capture_output=True, text=True)
    if r.returncode != 0:
        raise RuntimeError(f'astcenc {args}: {r.stdout} {r.stderr}')


def block_content(r, n, kind, hdr):
    """n texels of one block, (n, 4) float in 0..1 (LDR) or 0..scale (HDR)."""
    t = np.linspace(0, 1, n)[:, None]
    c0, c1 = r.random(4), r.random(4)
    if kind == 0:
        v = np.tile(c0, (n, 1))                                    # constant: void extent
    elif kind == 1:
        v = c0 + (c1 - c0) * t                                     # gradient
    elif kind == 2:
        v = c0 + (c1 - c0) * t
        v[:, 3] = r.random(n)                                      # independent alpha: dual plane
    elif kind in (3, 4, 5):
        k = kind - 1                                               # 2..4 colour mosaic: partitions
        pal = r.random((k, 4))
        v = pal[r.integers(0, k, n)] + 0.03 * r.random((n, 4))
    elif kind == 6:
        g = c0[0] + (c1[0] - c0[0]) * t[:, 0]
        v = np.stack([g, g, g, np.full(n, 1.0)], 1)                 # grey
    elif kind == 7:
        g = c0[0] + (c1[0] - c0[0]) * t[:, 0]
        v = np.stack([g, g, g, 1 - t[:, 0]], 1)                     # grey + alpha
    elif kind == 8:
        v = np.tile(c0, (n, 1))
        v[:, 3] = 1.0
        v[:, :3] += (c1[:3] - c0[:3]) * t * 0.3                     # rgb, opaque: base+scale
    elif kind == 10:
        v = c0 + 0.06 * (r.random(4) - 0.5) * t                    # close colours: base+offset
    elif kind == 11:
        g = c0[0] + 0.05 * t[:, 0]
        v = np.stack([g, g, g, c0[3] - 0.05 * t[:, 0]], 1)          # close grey + alpha: base+offset
    elif kind == 12:
        pal = r.random((4, 4))
        v = pal[(np.arange(n) * 7 // 3 + np.arange(n) // 5) % 4]   # 4 colours, scattered: 3 / 4 partitions
    else:
        v = r.random((n, 4))                                       # noise
    v = np.clip(v, 0, 1)
    if hdr:
        scale = 2.0 ** r.integers(-4, 8)
        v[:, :3] = v[:, :3] ** 2 * scale
        if kind % 3 == 1:
            v[:, 3] *= 4.0                                         # HDR alpha
    return v


def astc_image(dims, grid, seed, hdr):
    bw, bh, bd = dims
    gx, gy, gz = grid
    r = np.random.default_rng(seed)
    img = np.zeros((gz * bd, gy * bh, gx * bw, 4))
    k = 0
    for z in range(gz):
        for y in range(gy):
            for x in range(gx):
                v = block_content(r, bw * bh * bd, k % 14, hdr).reshape(bd, bh, bw, 4)
                img[z * bd:(z + 1) * bd, y * bh:(y + 1) * bh, x * bw:(x + 1) * bw] = v
                k += 1
    return img.astype(np.float16) if hdr else (img * 255 + 0.5).astype(np.uint8)


def decode_ref(tmp, blocks, dims, size, mode):
    """Reference decode of the given blocks: (D*H, W, 4) uint8 (ldr, srgb) or float16 (hdr).

    sRGB: astcenc expands the alpha endpoints like the colour ones ((c << 8) | 0x80) and returns their top 8 bits, while
    the specification (23.19) expands alpha by bit replication 'in any case' and decodes it with decode_float16. The
    reference alpha is therefore astcenc's HDR mode decode of the same blocks (which for LDR endpoints is exactly that
    decode_float16 result) converted to 8 bits rounding to nearest, except for texels astcenc decodes to the error colour
    in both LDR modes (alpha 255)."""
    if mode == 'srgb':
        srgb = astcenc_decode(tmp, blocks, dims, size, "srgb").copy()
        ldr = astcenc_decode(tmp, blocks, dims, size, 'ldr')
        hdr = astcenc_decode(tmp, blocks, dims, size, 'hdr').astype(np.float64)
        magenta = np.array([255, 0, 255, 255], np.uint8)
        err = (srgb == magenta).all(-1) & (ldr == magenta).all(-1)
        assert not np.isnan(hdr[~err]).any()
        srgb[..., 3] = np.where(err, 255, np.floor(np.nan_to_num(hdr[..., 3]) * 255 + 0.5)).astype(np.uint8)
        return srgb
    return astcenc_decode(tmp, blocks, dims, size, mode)


def astcenc_decode(tmp, blocks, dims, size, mode):
    p = os.path.join(tmp, 'd.astc')
    astc_file(p, blocks, dims, size)
    o = os.path.join(tmp, 'd.ktx')
    run({'ldr': '-dl', 'srgb': '-ds', 'hdr': '-dH'}[mode], p, o)
    a = read_ktx(o)
    assert (mode == 'hdr') == (a.dtype == np.float16)
    d, h, w, c = a.shape
    if mode != 'hdr' and d == 1:
        # 2D: cross check against the PNG output, which always keeps the alpha channel
        o = os.path.join(tmp, 'd.png')
        run({'ldr': '-dl', 'srgb': '-ds'}[mode], p, o)
        assert (np.asarray(Image.open(o).convert('RGBA')) == a[0]).all()
    return a.reshape(d * h, w, c)


def dims_name(dims):
    return 'x'.join(str(v) for v in (dims if dims[2] > 1 else dims[:2]))


def mutate(blocks, r, flips):
    b = bytearray(blocks)
    for i in range(0, len(b), 16):
        for _ in range(flips):
            bit = int(r.integers(0, 128))
            b[i + bit // 8] ^= 1 << (bit % 8)
    return bytes(b)


def special_blocks(dims):
    """Hand made blocks: void extents (valid, constant, invalid, HDR, reserved bits) and illegal block modes."""
    is3d = dims[2] > 1
    out = []

    def ve(lo_coords, hdr, color, reserved_ok=True):
        v = 0x1FC | (hdr << 9)
        if not is3d:
            v |= (3 if reserved_ok else 1) << 10
            v |= lo_coords << 12
        else:
            v |= lo_coords << 10
        v |= sum(c << (64 + 16 * i) for i, c in enumerate(color))
        return v.to_bytes(16, 'little')

    if not is3d:
        all1 = (1 << 52) - 1
        good = 10 | (2000 << 13) | (5 << 26) | (6000 << 39)           # s 10..2000, t 5..6000
        bad = 3000 | (2000 << 13) | (5 << 26) | (6000 << 39)          # min s >= max s
    else:
        all1 = (1 << 54) - 1
        good = 1 | (300 << 9) | (2 << 18) | (400 << 27) | (3 << 36) | (500 << 45)
        bad = 1 | (300 << 9) | (450 << 18) | (400 << 27) | (3 << 36) | (500 << 45)
    half = lambda f: int(np.float16(f).view(np.uint16))
    out += [ve(all1, 0, (0x1234, 0x8000, 0xFFFF, 0x00FF)), ve(good, 0, (0xFF00, 0x0101, 0x7F80, 0xFFFF)),
            ve(bad, 0, (1, 2, 3, 4)), ve(all1, 1, (half(3.5), half(0.25), half(1000), half(1.0))),
            ve(good, 1, (half(0.001), half(65504), half(2), half(0.5)))]
    if not is3d:
        out.append(ve(all1, 0, (0x8080, 0x4040, 0x2020, 0xFFFF), reserved_ok=False))
    # reserved block modes: bits 3..0 zero, and (2D) bits 8..6 all one without being a void extent
    out.append(bytes(16))
    out.append(((0x1E4 if is3d else 0x1C4) | 0x0FFFF0000000 << 64).to_bytes(16, 'little'))
    if not is3d:
        out.append((0x600 | 0x100 | 0x60 | 0x4 | 0x0FFFF0000000 << 64).to_bytes(16, 'little'))   # 9x9 grid: > 64 weights
        out.append((0x000 | 0x60 | 0x4 | 0x0FFFF0000000 << 64).to_bytes(16, 'little'))           # 12x5 grid
    return b''.join(out)


def endpoint_blocks(dims, r):
    """Single partition blocks with every colour endpoint mode and chosen endpoint values: a 4x4 (2D, 2 bit weights)
    or 2x2x2 (3D, 3 bit weights) weight grid leaves room for 8 bit colour values, stored as plain bytes from bit 17.
    Covers every HDR mode 7 sub-mode, every HDR mode 11 sub-mode / major component and random values for all modes."""
    is3d = dims[2] > 1
    mode, wbits = (0x13, 24) if is3d else (0x42, 32)

    def blk(cem, values):
        v = mode | cem << 13
        for i, x in enumerate(values):
            v |= int(x) << (17 + 8 * i)
        return (v | int(r.integers(0, 1 << wbits)) << (128 - wbits)).to_bytes(16, 'little')

    n = {c: 2 * ((c >> 2) + 1) for c in range(16)}
    out = []
    for cem in range(16):
        for _ in range(3):
            out.append(blk(cem, r.integers(0, 256, n[cem])))
    for modeval in range(16):                     # HDR mode 7: v0 bits 7..6, v1 bit 7, v2 bit 7
        v = r.integers(0, 256, 4)
        v[0] = v[0] & 0x3F | (modeval & 3) << 6
        v[1] = v[1] & 0x7F | (modeval >> 2 & 1) << 7
        v[2] = v[2] & 0x7F | (modeval >> 3 & 1) << 7
        out.append(blk(7, v))
    for m in range(8):                            # HDR mode 11 / 14 / 15: mode bits v1..v3 bit 7, major component v4, v5 bit 7
        for maj in range(4):
            v = r.integers(0, 256, 8)
            for i in range(3):
                v[1 + i] = v[1 + i] & 0x7F | (m >> i & 1) << 7
            v[4] = v[4] & 0x7F | (maj & 1) << 7
            v[5] = v[5] & 0x7F | (maj >> 1) << 7
            out.append(blk((11, 14, 15)[(m + maj) % 3], v[:6] if (m + maj) % 3 == 0 else v))
    while len(out) % 8:
        out.append(blk(0, (0, 255)))
    return b''.join(out)


def gen_astc(tmp):
    r = np.random.default_rng(5)
    seed = 100
    for dims in [(bw, bh, 1) for bw, bh in SIZES_2D] + SIZES_3D:
        bw, bh, bd = dims
        is3d = bd > 1
        grid = (4, 3, 1) if is3d else (5, 4, 1)
        size = (grid[0] * bw, grid[1] * bh, grid[2] * bd)
        bs = f'{bw}x{bh}' + (f'x{bd}' if is3d else '')
        dn = dims_name(dims)
        all_blocks = []
        profiles = [('ldr', '-cl', False), ('srgb', '-cs', False), ('hdr', '-cH', True), ('hdra', '-ch', True)]
        if is3d:
            profiles = [('ldr', '-cl', False), ('hdr', '-cH', True)]
        for tag, enc, hdr in profiles:
            seed += 1
            img = astc_image(dims, grid, seed, hdr)
            src = os.path.join(tmp, 's.ktx')
            write_ktx(src, img)
            comp = os.path.join(tmp, 'c.astc')
            run(enc, src, comp, bs, '-exhaustive')
            blocks = open(comp, 'rb').read()[16:]
            all_blocks.append(blocks)
            mode = 'srgb' if tag == 'srgb' else 'hdr' if hdr else 'ldr'
            save(f'astc_{mode}_{dn}_{tag}', blocks, decode_ref(tmp, blocks, dims, size, mode))
            # the other mode: LDR data decoded as HDR, HDR data decoded as LDR (error colour)
            other = 'ldr' if hdr else 'hdr'
            if tag != 'srgb':
                save(f'astc_{other}_{dn}_{tag}', blocks, decode_ref(tmp, blocks, dims, size, other))
            if REPORT:
                report(f'{dn} {tag}', blocks, is3d)
        # bit flipped copies: illegal encodings and odd modes
        blocks = b''.join(mutate(b[:16 * (len(b) // 32)], r, flips) for b, flips in zip(all_blocks, (1, 3, 2, 1)))
        n = len(blocks) // 16 // grid[0] * grid[0]
        blocks = blocks[:n * 16]
        for mode in ('ldr', 'hdr'):
            save(f'astc_{mode}_{dn}_fuzz', blocks, decode_ref(tmp, blocks, dims, (grid[0] * bw, n // grid[0] * bh, bd), mode))
        eb = endpoint_blocks(dims, r)
        n = len(eb) // 16
        for mode in ('ldr', 'hdr') if dims in ENDPOINT_SIZES else ():
            save(f'astc_{mode}_{dn}_endpoints', eb, decode_ref(tmp, eb, dims, (8 * bw, (n // 8) * bh, bd), mode))
        sp = special_blocks(dims)
        n = len(sp) // 16
        for mode in ('ldr', 'srgb', 'hdr'):
            save(f'astc_{mode}_{dn}_special', sp, decode_ref(tmp, sp, dims, (n * bw, bh, bd), mode))


# ---------------------------------------------------------------- coverage report (structure only)

def report(label, blocks, is3d):
    stats = {}
    for i in range(0, len(blocks), 16):
        v = int.from_bytes(blocks[i:i + 16], 'little')
        if v & 0x1FF == 0x1FC:
            k = 'void-hdr' if v >> 9 & 1 else 'void-ldr'
            stats[k] = stats.get(k, 0) + 1
            continue
        parts = (v >> 11 & 3) + 1
        dual = v >> 10 & 1 if (v & 3 or (v >> 7 & 3) != 2) else 0
        cem = v >> 13 & 15 if parts == 1 else ('multi' if v >> 23 & 3 else v >> 25 & 15)
        for k in (f'p{parts}', f'dual{dual}', f'cem{cem}'):
            stats[k] = stats.get(k, 0) + 1
    print(label, dict(sorted(stats.items(), key=lambda kv: str(kv[0]))))


if __name__ == '__main__':
    os.makedirs(OUT, exist_ok=True)
    for f in os.listdir(OUT):
        os.remove(os.path.join(OUT, f))
    gen_etc()
    with tempfile.TemporaryDirectory(prefix='astcgen') as tmp:
        gen_astc(tmp)
    print('wrote', len(os.listdir(OUT)), 'files to', os.path.normpath(OUT))
