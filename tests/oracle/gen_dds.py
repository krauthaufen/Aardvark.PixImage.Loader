"""Generates tests/fixtures/dds: name.dds, name.dds.ref (the top surface as Dds.Decode returns it) and, for files with
more surfaces, name.dds.<item>.<face>.<mip>.<slice>.ref for every surface.

  * block compressed: random block data (every BC6H and BC7 mode forced, reserved ones too); references from bcdec
    (imagecodecs.bcn_decode), cross checked against Pillow. BC1 to BC5 are specified as real valued interpolations
    and decoders round differently, so their references hold within 1 (the tests allow that); BC6H and BC7 are bit
    exact. Signed BC4 / BC5 references come from Pillow.
  * uncompressed DXGI and legacy bit mask formats: random pixels, references computed here from the format
    definitions (cross checked against Pillow / ImageMagick where they read the file).
  * structure: mip chains, cube maps (all and some faces), texture arrays, volume and 1D textures.
  * written by Pillow and ImageMagick.

usage: python3 gen_dds.py [--report]
"""
import io
import os
import struct
import subprocess
import sys
import numpy as np
import imagecodecs
from PIL import Image
from pxref import write_ref, test_pattern

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'fixtures', 'dds')
REPORT = '--report' in sys.argv
rng = np.random.default_rng(2026)


# ---------------------------------------------------------------- writing

def header(w, h, mips=1, depth=1, pf=None, dx10=None, caps2=0):
    """pf: (flags, fourcc, bits, r, g, b, a) or None for DX10; dx10: (dxgi, dim, misc, array_size, misc2)."""
    flags = 0x1 | 0x2 | 0x4 | 0x1000 | (0x20000 if mips > 1 else 0) | (0x800000 if depth > 1 else 0)
    if pf is None:
        pf = (4, b'DX10', 0, 0, 0, 0, 0)
    pflags, fourcc, bits, r, g, b, a = pf
    fourcc = fourcc if isinstance(fourcc, bytes) else struct.pack('<I', fourcc)
    hdr = struct.pack('<4sIIIIIII', b'DDS ', 124, flags, h, w, 0, depth if depth > 1 else 0, mips) + bytes(44)
    hdr += struct.pack('<II4sIIIII', 32, pflags, fourcc, bits, r, g, b, a)
    caps = 0x1000 | (0x400008 if mips > 1 else 0) | (0x8 if caps2 else 0)
    hdr += struct.pack('<IIIII', caps, caps2, 0, 0, 0)
    assert len(hdr) == 128
    if dx10 is not None:
        hdr += struct.pack('<5I', *dx10)
    return hdr


def save(name, data, surfaces):
    """surfaces: dict (item, face, mip, slice) -> array, or a single array for the top surface only."""
    path = os.path.join(OUT, name + '.dds')
    with open(path, 'wb') as f:
        f.write(data)
    if isinstance(surfaces, np.ndarray):
        surfaces = {(0, 0, 0, 0): surfaces}
    write_ref(path + '.ref', surfaces[(0, 0, 0, 0)])
    if len(surfaces) > 1:
        for (i, fc, m, s), a in surfaces.items():
            write_ref(f'{path}.{i}.{fc}.{m}.{s}.ref', a)
    return path


def compare(name, ref, other, who, tol=0):
    if other is None:
        if REPORT:
            print(f'  {name}: {who} cannot read it')
        return
    other = np.asarray(other)
    if other.ndim == 2 and ref.ndim == 3 and ref.shape[2] == 1:
        other = other[:, :, None]
    if other.shape != ref.shape:
        print(f'  {name}: {who} gives {other.shape} {other.dtype}, reference {ref.shape} {ref.dtype}')
        return
    if ref.dtype == np.float16:
        d = int(np.max(np.abs(other.view(np.uint16).astype(int) - ref.view(np.uint16).astype(int)))) if other.dtype == np.float16 else float(np.nanmax(np.abs(other.astype(float) - ref.astype(float))))
    else:
        d = float(np.max(np.abs(other.astype(float) - ref.astype(float))))
    if d > tol or REPORT:
        print(f'  {name}: {who} differs by {d}')


def pillow(path, mode=None):
    try:
        with Image.open(path) as im:
            im.load()
            return np.array(im.convert(mode) if mode else im)
    except Exception as e:
        if REPORT:
            print(f'    pillow: {e}')
        return None


def magick(path, ch):
    fmt = {1: 'gray', 2: 'graya', 3: 'rgb', 4: 'rgba'}[ch]
    try:
        info = subprocess.run(['magick', 'identify', '-format', '%w %h\n', path + '[0]'], capture_output=True, check=True).stdout.split()
        w, h = int(info[0]), int(info[1])
        out = subprocess.run(['magick', path + '[0]', '-depth', '8', f'{fmt}:-'], capture_output=True, check=True).stdout
        return np.frombuffer(out, np.uint8).reshape(h, w, ch)
    except Exception:
        return None


# ---------------------------------------------------------------- block compressed

BC = {  # dxgi: (bcdec format, block bytes, channels, dtype)
    71: (1, 8, 4), 74: (2, 16, 4), 77: (3, 16, 4), 80: (4, 8, 1), 81: (-4, 8, 1), 83: (5, 16, 2), 84: (-5, 16, 2),
    95: (6, 16, 3), 96: (-6, 16, 3), 98: (7, 16, 4),
}
BC6_MODES = [0x00, 0x01, 0x02, 0x06, 0x0A, 0x0E, 0x12, 0x16, 0x1A, 0x1E, 0x03, 0x07, 0x0B, 0x0F, 0x13, 0x17, 0x1B, 0x1F]


def random_blocks(dxgi, n):
    bb = BC[dxgi][1]
    d = rng.integers(0, 256, (n, bb), dtype=np.uint8)
    for k in range(n):
        if dxgi == 98:
            mode = k % 9
            d[k, 0] = 0 if mode == 8 else ((int(d[k, 0]) << (mode + 1)) | (1 << mode)) & 0xFF
        elif dxgi in (95, 96):
            m = BC6_MODES[k % len(BC6_MODES)]
            d[k, 0] = (int(d[k, 0]) & 0xFC) | m if m < 2 else (int(d[k, 0]) & 0xE0) | m
    return d.tobytes()


def decode_blocks(dxgi, data, w, h):
    """Reference decode of a surface (full blocks, then cropped)."""
    fmt, bb, ch = BC[dxgi]
    bw, bh = max(1, (w + 3) // 4), max(1, (h + 3) // 4)
    if fmt in (-4, -5):
        return None   # bcdec has no signed BC4 / BC5
    shape = (bh * 4, bw * 4) if ch == 1 else (bh * 4, bw * 4, ch)
    a = imagecodecs.bcn_decode(data, fmt, shape=shape)
    a = a[:h, :w]
    return a if a.ndim == 3 else a[:, :, None]


def bc_files():
    names = {71: 'bc1', 74: 'bc2', 77: 'bc3', 80: 'bc4u', 81: 'bc4s', 83: 'bc5u', 84: 'bc5s', 95: 'bc6h_uf16', 96: 'bc6h_sf16', 98: 'bc7'}
    for dxgi, nm in names.items():
        for (w, h) in ((128, 64) if dxgi in (95, 96, 98) else (64, 16), (13, 7)):
            bw, bh = (w + 3) // 4, (h + 3) // 4
            data = random_blocks(dxgi, bw * bh)
            name = f'{nm}_{w}x{h}'
            file = header(w, h, dx10=(dxgi, 3, 0, 1, 0)) + data
            ref = decode_blocks(dxgi, data, w, h)
            path = os.path.join(OUT, name + '.dds')
            with open(path, 'wb') as f:
                f.write(file)
            p = pillow(path)
            if ref is None:
                # signed BC4 / BC5: Pillow decodes to unsigned bytes (value + 128?) - recorded below by our own rule
                ref = signed_ref(dxgi, data, w, h)
                if p is not None and REPORT:
                    print(f'  {name}: pillow gives {p.dtype} {p.shape}')
            else:
                # (Pillow gives BC6H as 8 bit, and something else than the specified zeros for BC7's reserved mode)
                if p is not None and dxgi not in (95, 96):
                    if ref.shape[2] == 2 and p.ndim == 3:
                        p = p[:, :, :2]
                    p = p if p.ndim == 3 else p[:, :, None]
                    if dxgi == 98:
                        blocks = np.frombuffer(data, np.uint8).reshape(-1, 16)[:, 0].reshape(bh, bw)
                        reserved = np.repeat(np.repeat(blocks == 0, 4, 0), 4, 1)[:h, :w]
                        p = np.where(reserved[:, :, None], ref, p)
                    compare(name, ref, p, 'pillow', tol=0 if dxgi == 98 else 1)
            save(name, file, ref)

    # legacy FourCC headers for BC1 to BC5
    for cc, dxgi in ((b'DXT1', 71), (b'DXT3', 74), (b'DXT5', 77), (b'ATI1', 80), (b'ATI2', 83), (b'BC4U', 80), (b'BC5U', 83)):
        w, h = 20, 12
        data = random_blocks(dxgi, 5 * 3)
        file = header(w, h, pf=(4, cc, 0, 0, 0, 0, 0)) + data
        save('legacy_' + cc.decode().lower(), file, decode_blocks(dxgi, data, w, h))

    # premultiplied DXT2 / DXT4: the reference holds the stored (premultiplied) colours
    for cc, dxgi in ((b'DXT2', 74), (b'DXT4', 77)):
        w, h = 16, 8
        data = random_blocks(dxgi, 8)
        save('legacy_' + cc.decode().lower(), header(w, h, pf=(4, cc, 0, 0, 0, 0, 0)) + data, decode_blocks(dxgi, data, w, h))


def signed_ref(dxgi, data, w, h):
    """Signed BC4 / BC5 from the specification's formulas (rounded to nearest), as int8."""
    ch = 1 if dxgi == 81 else 2
    bw, bh = max(1, (w + 3) // 4), max(1, (h + 3) // 4)
    out = np.zeros((bh * 4, bw * 4, ch), np.int8)
    bb = 8 * ch
    for by in range(bh):
        for bx in range(bw):
            blk = data[(by * bw + bx) * bb:(by * bw + bx + 1) * bb]
            for c in range(ch):
                b = blk[c * 8:(c + 1) * 8]
                r0, r1 = struct.unpack('bb', b[:2])
                a0, a1 = max(r0, -127), max(r1, -127)
                bits = int.from_bytes(b[2:8], 'little')
                if r0 > r1:
                    pal = [a0, a1] + [rnd(((7 - k) * a0 + k * a1), 7) for k in range(1, 7)]
                else:
                    pal = [a0, a1] + [rnd(((5 - k) * a0 + k * a1), 5) for k in range(1, 5)] + [-127, 127]
                for i in range(16):
                    out[by * 4 + i // 4, bx * 4 + i % 4, c] = pal[(bits >> (3 * i)) & 7]
    return out[:h, :w]


def rnd(a, d):
    from fractions import Fraction
    v = Fraction(a, d)
    return int((v + Fraction(1, 2)) // 1) if v >= 0 else -int((-v + Fraction(1, 2)) // 1)


# ---------------------------------------------------------------- uncompressed

def f11(v, mant):
    e, m = v >> mant, v & ((1 << mant) - 1)
    return np.where(e == 0, m * 2.0 ** (-14 - mant), np.where(e == 31, np.where(m == 0, np.inf, np.nan), (1 + m / 2.0 ** mant) * 2.0 ** (e.astype(float) - 15)))


def uncompressed_files():
    w, h = 9, 5
    n = w * h

    def raw(nbytes):
        return rng.integers(0, 256, nbytes, dtype=np.uint8).tobytes()

    def dx(name, dxgi, data, ref):
        hand(name, header(w, h, dx10=(dxgi, 3, 0, 1, 0)) + data, ref)

    f32 = rng.normal(0, 10, (h, w, 4)).astype(np.float32)
    dx('r32g32b32a32_float', 2, f32.tobytes(), f32)
    dx('r32g32b32_float', 6, f32[:, :, :3].copy().tobytes(), f32[:, :, :3])
    dx('r32g32_float', 16, f32[:, :, :2].copy().tobytes(), f32[:, :, :2])
    dx('r32_float', 41, f32[:, :, :1].copy().tobytes(), f32[:, :, :1])
    f16 = rng.normal(0, 10, (h, w, 4)).astype(np.float16)
    dx('r16g16b16a16_float', 10, f16.tobytes(), f16)
    dx('r16g16_float', 34, f16[:, :, :2].copy().tobytes(), f16[:, :, :2])
    dx('r16_float', 54, f16[:, :, :1].copy().tobytes(), f16[:, :, :1])
    u16 = rng.integers(0, 65536, (h, w, 4), dtype=np.uint16)
    dx('r16g16b16a16_unorm', 11, u16.tobytes(), u16)
    dx('r16g16_unorm', 35, u16[:, :, :2].copy().tobytes(), u16[:, :, :2])
    dx('r16_unorm', 56, u16[:, :, :1].copy().tobytes(), u16[:, :, :1])
    dx('r16g16b16a16_snorm', 13, u16.tobytes(), u16.view(np.int16))
    u8 = rng.integers(0, 256, (h, w, 4), dtype=np.uint8)
    dx('r8g8b8a8_unorm', 28, u8.tobytes(), u8)
    dx('r8g8b8a8_unorm_srgb', 29, u8.tobytes(), u8)
    dx('r8g8b8a8_snorm', 31, u8.tobytes(), u8.view(np.int8))
    dx('r8g8_unorm', 49, u8[:, :, :2].copy().tobytes(), u8[:, :, :2])
    dx('r8g8_snorm', 51, u8[:, :, :2].copy().tobytes(), u8[:, :, :2].view(np.int8))
    dx('r8_unorm', 61, u8[:, :, :1].copy().tobytes(), u8[:, :, :1])
    dx('a8_unorm', 65, u8[:, :, :1].copy().tobytes(), u8[:, :, :1])
    dx('b8g8r8a8_unorm', 87, u8.tobytes(), u8[:, :, [2, 1, 0, 3]])
    dx('b8g8r8x8_unorm', 88, u8.tobytes(), u8[:, :, [2, 1, 0]])
    v = rng.integers(0, 1 << 32, (h, w), dtype=np.uint64).astype(np.uint32)
    c10 = [(v >> (10 * c)) & 1023 for c in range(3)]
    ref = np.stack([((x.astype(np.uint64) * 65535 + 511) // 1023).astype(np.uint16) for x in c10] + [((v >> 30) * 21845).astype(np.uint16)], -1)
    dx('r10g10b10a2_unorm', 24, v.tobytes(), ref)
    ref = np.stack([f11(v & 0x7FF, 6), f11((v >> 11) & 0x7FF, 6), f11(v >> 22, 5)], -1).astype(np.float32)
    dx('r11g11b10_float', 26, v.tobytes(), ref)
    e = (v >> 27).astype(float)
    ref = np.stack([((v >> (9 * c)) & 511) * 2.0 ** (e - 24) for c in range(3)], -1).astype(np.float32)
    dx('r9g9b9e5_sharedexp', 67, v.tobytes(), ref)
    p16 = rng.integers(0, 65536, (h, w), dtype=np.uint32)

    def sc(x, bits):
        m = (1 << bits) - 1
        return ((x * 255 + m // 2) // m).astype(np.uint8)
    dx('b5g6r5_unorm', 85, p16.astype(np.uint16).tobytes(), np.stack([sc(p16 >> 11 & 31, 5), sc(p16 >> 5 & 63, 6), sc(p16 & 31, 5)], -1))
    dx('b5g5r5a1_unorm', 86, p16.astype(np.uint16).tobytes(), np.stack([sc(p16 >> 10 & 31, 5), sc(p16 >> 5 & 31, 5), sc(p16 & 31, 5), sc(p16 >> 15, 1)], -1))
    dx('b4g4r4a4_unorm', 115, p16.astype(np.uint16).tobytes(), np.stack([sc(p16 >> 8 & 15, 4), sc(p16 >> 4 & 15, 4), sc(p16 & 15, 4), sc(p16 >> 12, 4)], -1))

    # legacy bit mask formats
    def legacy(name, flags, bits, masks, pixels, ref):
        hand(name, header(w, h, pf=(flags, 0, bits) + tuple(masks)) + pixels, ref)

    legacy('legacy_a8r8g8b8', 0x41, 32, (0xFF0000, 0xFF00, 0xFF, 0xFF000000), u8.tobytes(), u8[:, :, [2, 1, 0, 3]])
    legacy('legacy_x8r8g8b8', 0x40, 32, (0xFF0000, 0xFF00, 0xFF, 0), u8.tobytes(), u8[:, :, [2, 1, 0]])
    legacy('legacy_a8b8g8r8', 0x41, 32, (0xFF, 0xFF00, 0xFF0000, 0xFF000000), u8.tobytes(), u8)
    legacy('legacy_r8g8b8', 0x40, 24, (0xFF0000, 0xFF00, 0xFF, 0), u8[:, :, :3].copy().tobytes(), u8[:, :, [2, 1, 0]])
    legacy('legacy_r5g6b5', 0x40, 16, (0xF800, 0x7E0, 0x1F, 0), p16.astype(np.uint16).tobytes(), np.stack([sc(p16 >> 11 & 31, 5), sc(p16 >> 5 & 63, 6), sc(p16 & 31, 5)], -1))
    legacy('legacy_a1r5g5b5', 0x41, 16, (0x7C00, 0x3E0, 0x1F, 0x8000), p16.astype(np.uint16).tobytes(), np.stack([sc(p16 >> 10 & 31, 5), sc(p16 >> 5 & 31, 5), sc(p16 & 31, 5), sc(p16 >> 15, 1)], -1))
    legacy('legacy_a4r4g4b4', 0x41, 16, (0xF00, 0xF0, 0xF, 0xF000), p16.astype(np.uint16).tobytes(), np.stack([sc(p16 >> 8 & 15, 4), sc(p16 >> 4 & 15, 4), sc(p16 & 15, 4), sc(p16 >> 12, 4)], -1))
    p8 = u8[:, :, 0].astype(np.uint32)
    legacy('legacy_r3g3b2', 0x40, 8, (0xE0, 0x1C, 0x3, 0), u8[:, :, 0].copy().tobytes(), np.stack([sc(p8 >> 5, 3), sc(p8 >> 2 & 7, 3), sc(p8 & 3, 2)], -1))
    legacy('legacy_l8', 0x20000, 8, (0xFF, 0, 0, 0), u8[:, :, 0].copy().tobytes(), u8[:, :, :1])
    legacy('legacy_a8l8', 0x20001, 16, (0xFF, 0, 0, 0xFF00), u8[:, :, :2].copy().tobytes(), u8[:, :, :2])
    legacy('legacy_l16', 0x20000, 16, (0xFFFF, 0, 0, 0), u16[:, :, 0].copy().tobytes(), u16[:, :, :1])
    legacy('legacy_a8', 0x2, 8, (0, 0, 0, 0xFF), u8[:, :, 0].copy().tobytes(), u8[:, :, :1])
    legacy('legacy_g16r16', 0x40, 32, (0xFFFF, 0xFFFF0000, 0, 0), u16[:, :, :2].copy().tobytes(), np.concatenate([u16[:, :, :2], np.zeros((h, w, 1), np.uint16)], -1))
    legacy('legacy_v8u8', 0x80000, 16, (0xFF, 0xFF00, 0, 0), u8[:, :, :2].copy().tobytes(), u8[:, :, :2].view(np.int8))
    # numeric D3DFMT codes in the FourCC field
    hand('legacy_d3dfmt_a16b16g16r16f', header(w, h, pf=(4, 113, 0, 0, 0, 0, 0)) + f16.tobytes(), f16)
    hand('legacy_d3dfmt_a32b32g32r32f', header(w, h, pf=(4, 116, 0, 0, 0, 0, 0)) + f32.tobytes(), f32)
    hand('legacy_d3dfmt_a16b16g16r16', header(w, h, pf=(4, 36, 0, 0, 0, 0, 0)) + u16.tobytes(), u16)


def hand(name, data, ref):
    path = save(name, data, ref)
    ch = ref.shape[2] if ref.ndim == 3 else 1
    if ref.dtype == np.uint8 and ch in (1, 2, 3, 4):
        p = pillow(path, {1: 'L', 2: 'LA', 3: 'RGB', 4: 'RGBA'}[ch]) if ch != 2 else None
        compare(name, ref, p, 'pillow')
        compare(name, ref, magick(path, ch) if ch != 2 else None, 'magick')


# ---------------------------------------------------------------- structure

def rgba_surface(w, h):
    return rng.integers(0, 256, (h, w, 4), dtype=np.uint8)


def structure_files():
    pf = (0x41, 0, 32, 0xFF, 0xFF00, 0xFF0000, 0xFF000000)   # A8B8G8R8: stored bytes are RGBA

    # mip chain, non power of two
    w, h = 37, 20
    levels = [rgba_surface(max(1, w >> m), max(1, h >> m)) for m in range(6)]
    save('mips_rgba_37x20', header(w, h, mips=6, pf=pf) + b''.join(l.tobytes() for l in levels),
         {(0, 0, m, 0): l for m, l in enumerate(levels)})
    # BC1 / BC7 mip chains down to 1x1 (levels below 4x4 still take a whole block)
    for dxgi, nm in ((71, 'bc1'), (98, 'bc7')):
        w = h = 16
        surf, data = {}, b''
        for m in range(5):
            lw, lh = max(1, w >> m), max(1, h >> m)
            d = random_blocks(dxgi, max(1, (lw + 3) // 4) * max(1, (lh + 3) // 4))
            data += d
            surf[(0, 0, m, 0)] = decode_blocks(dxgi, d, lw, lh)
        save(f'mips_{nm}_16', header(w, h, mips=5, dx10=(dxgi, 3, 0, 1, 0)) + data, surf)

    # cube maps: legacy with all faces and with some, DX10 with mips
    w = 8
    faces = [rgba_surface(w, w) for _ in range(6)]
    save('cube_legacy', header(w, w, pf=pf, caps2=0x200 | 0xFC00) + b''.join(f.tobytes() for f in faces),
         {(0, k, 0, 0): f for k, f in enumerate(faces)})
    some = [0, 3, 4]   # +X, -Y, +Z
    caps2 = 0x200 | sum(0x400 << k for k in some)
    save('cube_legacy_some_faces', header(w, w, pf=pf, caps2=caps2) + b''.join(faces[k].tobytes() for k in some),
         {(0, i, 0, 0): faces[k] for i, k in enumerate(some)})
    surf, data = {}, b''
    for k in range(6):
        for m in range(4):
            s = rgba_surface(max(1, w >> m), max(1, w >> m))
            surf[(0, k, m, 0)] = s
            data += s.tobytes()
    save('cube_dx10_mips', header(w, w, mips=4, dx10=(28, 3, 4, 1, 1), caps2=0x200 | 0xFC00) + data, surf)

    # texture array with mips (DX10)
    w, h = 6, 4
    surf, data = {}, b''
    for i in range(3):
        for m in range(3):
            s = rgba_surface(max(1, w >> m), max(1, h >> m))
            surf[(i, 0, m, 0)] = s
            data += s.tobytes()
    save('array3_mips', header(w, h, mips=3, dx10=(28, 3, 0, 3, 0)) + data, surf)

    # volume textures: legacy with mips (slices halve), DX10
    w, h, d = 4, 4, 4
    surf, data = {}, b''
    for m in range(3):
        for s in range(max(1, d >> m)):
            a = rgba_surface(max(1, w >> m), max(1, h >> m))
            surf[(0, 0, m, s)] = a
            data += a.tobytes()
    save('volume_legacy_mips', header(w, h, mips=3, depth=d, pf=pf, caps2=0x200000) + data, surf)
    surf, data = {}, b''
    for s in range(3):
        a = rgba_surface(5, 3)
        surf[(0, 0, 0, s)] = a
        data += a.tobytes()
    save('volume_dx10', header(5, 3, depth=3, dx10=(28, 4, 0, 1, 0)) + data, surf)

    # 1D texture
    a = rgba_surface(17, 1)
    save('tex1d', header(17, 1, dx10=(28, 2, 0, 1, 0)) + a.tobytes(), a)

    # truncated: the missing end of the top surface is zero
    a = rgba_surface(8, 8)
    full = header(8, 8, pf=pf) + a.tobytes()
    cut = full[:128 + 8 * 4 * 5 + 6]
    ref = a.copy()
    ref[5:] = 0
    ref[5, 0] = a[5, 0]
    ref[5, 1, :2] = a[5, 1, :2]
    save('truncated', cut, ref)


# ---------------------------------------------------------------- tool written

def tool_files():
    rgb = test_pattern(64, 32, 3)
    rgba = test_pattern(64, 32, 4, seed=3)
    for mode, arr, fmts in (('RGB', rgb, ['DXT1', None]), ('RGBA', rgba, ['DXT3', 'DXT5', None])):
        for pfmt in fmts:
            bio = io.BytesIO()
            kw = {'pixel_format': pfmt} if pfmt else {}
            try:
                Image.fromarray(arr, mode).save(bio, 'DDS', **kw)
            except Exception as e:
                print(f'  pillow cannot write {mode} {pfmt}: {e}')
                continue
            name = f'pil_{mode.lower()}_{(pfmt or "raw").lower()}'
            path = os.path.join(OUT, name + '.dds')
            with open(path, 'wb') as f:
                f.write(bio.getvalue())
            ref = pillow(path, mode)
            save(name, bio.getvalue(), ref)
            compare(name, ref, magick(path, len(mode)), 'magick', tol=0 if pfmt is None else 1)
    for comp in ('dxt1', 'dxt5', 'none'):
        tmp = os.path.join(OUT, '_src.png')
        Image.fromarray(rgba, 'RGBA').save(tmp)
        name = f'im_{comp}_mips'
        path = os.path.join(OUT, name + '.dds')
        subprocess.run(['magick', tmp, '-define', f'dds:compression={comp}', '-define', 'dds:mipmaps=3', path], check=True)
        os.remove(tmp)
        data = open(path, 'rb').read()
        ref = pillow(path, 'RGBA')
        save(name, data, ref)
        compare(name, ref, magick(path, 4), 'magick', tol=0 if comp == 'none' else 1)


def main():
    os.makedirs(OUT, exist_ok=True)
    for f in os.listdir(OUT):
        os.remove(os.path.join(OUT, f))
    bc_files()
    uncompressed_files()
    structure_files()
    tool_files()
    print(f'{len([f for f in os.listdir(OUT) if f.endswith(".dds")])} files in {OUT}')


if __name__ == '__main__':
    main()
