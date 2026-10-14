"""Generates tests/fixtures/bmp: name.bmp and name.bmp.ref (what Bmp.Decode returns).

Two kinds of fixtures:
  * written by Pillow and ImageMagick; the reference is Pillow's decode, cross checked against ImageMagick's;
  * hand built with the small writer below from the format description (things the tools do not write: OS/2 headers,
    RLE with skipped pixels, wide bit fields, embedded PNG / JPEG, short palettes, truncated data ...); the reference is
    computed from what went into the file, and cross checked against Pillow and ImageMagick where they read the file -
    disagreements are printed (--report prints all comparisons).

Decoding rules the references follow: palette images whose entries are all gray are Gray; pixels an RLE stream leaves
out are transparent black (the image is then Rgba / GrayAlpha); an alpha channel that is zero everywhere is opaque;
32 bit BI_RGB has no alpha; palette indices beyond the palette are black; bit fields are scaled to 8 bits rounding
to 8 bits rounding to nearest like the BMP Suite's reference renderings (Pillow and ImageMagick round differently,
which differs by 1); fields wider than 8 bits give 16 bit samples.

usage: python3 gen_bmp.py [--report]
"""
import io
import os
import struct
import subprocess
import sys
import numpy as np
from PIL import Image
from pxref import write_ref, test_pattern

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'fixtures', 'bmp')
REPORT = '--report' in sys.argv


def pillow(path):
    im = Image.open(path)
    im.load()
    return im


def magick(path, channels):
    fmt = {1: 'gray', 2: 'graya', 3: 'rgb', 4: 'rgba'}[channels]
    try:
        with Image.open(path) as im:
            w, h = im.size
    except Exception:
        w = h = None
    try:
        out = subprocess.run(['magick', path, '-depth', '8', f'{fmt}:-'], capture_output=True, check=True).stdout
        info = subprocess.run(['magick', 'identify', '-format', '%w %h\n', path], capture_output=True, check=True).stdout.split()
        w, h = int(info[0]), int(info[1])
        return np.frombuffer(out, np.uint8).reshape(h, w, channels)
    except Exception as e:
        return None


def compare(name, ref, other, who):
    if other is None:
        if REPORT:
            print(f'  {name}: {who} cannot read it')
        return
    if other.shape != ref.shape:
        print(f'  {name}: {who} gives shape {other.shape}, reference {ref.shape}')
        return
    d = np.abs(other.astype(int) - ref.astype(int)).max()
    if d > 0 or REPORT:
        print(f'  {name}: {who} differs by {d}')


def save(name, data, ref):
    path = os.path.join(OUT, name + '.bmp')
    with open(path, 'wb') as f:
        f.write(data)
    if ref.ndim == 2:
        ref = ref[:, :, None]
    write_ref(path + '.ref', ref)
    return path


# ---------------------------------------------------------------- tool written

def tool_files():
    rgb = test_pattern(37, 23, 3)
    rgba = test_pattern(37, 23, 4, seed=2)
    rgba[:, :, 3] = np.linspace(0, 255, 37, dtype=np.uint8)[None, :]
    gray = test_pattern(37, 23, 1)[:, :, 0]

    def from_pillow(name, im, mode, **kw):
        bio = io.BytesIO()
        im.save(bio, 'BMP', **kw)
        path = os.path.join(OUT, name + '.bmp')
        with open(path, 'wb') as f:
            f.write(bio.getvalue())
        ref = np.array(pillow(path).convert(mode))
        save(name, bio.getvalue(), ref)
        compare(name, ref if ref.ndim == 3 else ref[:, :, None], magick(path, len(mode)), 'magick')

    from_pillow('pil_rgb', Image.fromarray(rgb), 'RGB')
    from_pillow('pil_gray', Image.fromarray(gray), 'L')
    from_pillow('pil_1bit', Image.fromarray(gray).convert('1'), 'L')
    from_pillow('pil_pal', Image.fromarray(rgb).quantize(40), 'RGB')
    from_pillow('pil_pal_dpi', Image.fromarray(rgb).quantize(200), 'RGB', dpi=(300, 150))

    def from_magick(name, src_mode, src, args, mode, prefix='', ref_from_magick=False):
        tmp = os.path.join(OUT, '_src.png')
        Image.fromarray(src, src_mode).save(tmp)
        path = os.path.join(OUT, name + '.bmp')
        subprocess.run(['magick', tmp] + args + [prefix + path], check=True)
        os.remove(tmp)
        ref = np.array(pillow(path).convert(mode))
        if ref_from_magick:
            # Pillow expands 5 / 6 bit fields by scaling, ImageMagick (like this library) by bit replication
            ref = magick(path, len(mode))
        with open(path, 'rb') as f:
            data = f.read()
        save(name, data, ref)
        compare(name, ref if ref.ndim == 3 else ref[:, :, None], magick(path, len(mode)), 'magick')

    from_magick('im_rgb_bmp3', 'RGB', rgb, ['-define', 'bmp:format=bmp3'], 'RGB')
    from_magick('im_rgba_v5', 'RGBA', rgba, [], 'RGBA')
    from_magick('im_rgba_bmp4', 'RGBA', rgba, ['-define', 'bmp:format=bmp4'], 'RGBA')
    from_magick('im_rgb565', 'RGB', rgb, ['-define', 'bmp:subtype=RGB565'], 'RGB')
    # the tools expand 5 / 6 bit fields differently from the BMP Suite's renderings: reference from the stored values
    path = os.path.join(OUT, 'im_rgb565.bmp')
    data = open(path, 'rb').read()
    off, w, h = struct.unpack_from('<I', data, 10)[0], *struct.unpack_from('<ii', data, 18)
    stride = (w * 2 + 3) // 4 * 4
    v = np.stack([np.frombuffer(data, '<u2', w, off + y * stride) for y in range(abs(h))])
    if h > 0:
        v = v[::-1]
    save('im_rgb565', data, np.stack([scale(v >> 11 & 31, 5), scale(v >> 5 & 63, 6), scale(v & 31, 5)], -1))
    from_magick('im_pal_rle8', 'RGB', rgb, ['-colors', '50', '-type', 'Palette', '-compress', 'RLE'], 'RGB')
    from_magick('im_bmp2', 'RGB', rgb, ['-colors', '50', '-type', 'Palette'], 'RGB', prefix='BMP2:')


# ---------------------------------------------------------------- hand built

def file_header(info_and_rest_len, pixel_offset):
    return b'BM' + struct.pack('<IHHI', 14 + info_and_rest_len, 0, 0, pixel_offset)


def info40(w, h, bpp, compression=0, clr_used=0, image_size=0, ppm=(0, 0)):
    return struct.pack('<IiiHHIIiiII', 40, w, h, 1, bpp, compression, image_size, ppm[0], ppm[1], clr_used, 0)


def info_v(size, w, h, bpp, compression, masks=(0, 0, 0, 0), cs=0x73524742, profile=None, clr_used=0):
    b = bytearray(struct.pack('<IiiHHIIiiII', size, w, h, 1, bpp, compression, 0, 2835, 2835, clr_used, 0))
    if size >= 52:
        b += struct.pack('<III', *masks[:3])
    if size >= 56:
        b += struct.pack('<I', masks[3])
    if size >= 108:
        b += struct.pack('<I', cs) + bytes(36) + bytes(12)
    if size >= 124:
        off, ln = profile or (0, 0)
        b += struct.pack('<IIII', 4, off, ln, 0)
    assert len(b) == size, (len(b), size)
    return bytes(b)


def palette_bytes(pal, entry=4):
    out = bytearray()
    for r, g, b in pal:
        out += bytes([b, g, r]) + (b'\0' if entry == 4 else b'')
    return bytes(out)


def pack_rows(rows_bytes, top_down=False):
    rows = list(rows_bytes)
    if not top_down:
        rows = rows[::-1]
    out = bytearray()
    for r in rows:
        out += r + bytes((-len(r)) % 4)
    return bytes(out)


def indices_to_rows(idx, bpp):
    rows = []
    for line in idx:
        bits = ''.join(format(int(v), f'0{bpp}b') for v in line)
        bits += '0' * ((-len(bits)) % 8)
        rows.append(bytes(int(bits[i:i + 8], 2) for i in range(0, len(bits), 8)))
    return rows


def ref_from_palette(idx, pal, gray=None):
    pal = np.array(pal, np.uint8)
    out = np.zeros(idx.shape + (3,), np.uint8)
    ok = idx < len(pal)
    out[ok] = pal[idx[ok]]
    if gray is None:
        gray = bool(np.all(pal[:, 0] == pal[:, 1]) and np.all(pal[:, 0] == pal[:, 2]))
    return out[:, :, 0] if gray else out


def check_tools(name, path, ref):
    r = ref if ref.ndim == 3 else ref[:, :, None]
    c = r.shape[2]
    try:
        im = pillow(path)
        mode = {1: 'L', 2: 'LA', 3: 'RGB', 4: 'RGBA'}[c]
        p = np.array(im.convert(mode))
        compare(name, r, p if p.ndim == 3 else p[:, :, None], 'pillow')
    except Exception as e:
        if REPORT:
            print(f'  {name}: pillow cannot read it ({e})')
    compare(name, r, magick(path, c), 'magick')


def hand(name, data, ref):
    path = save(name, data, ref)
    check_tools(name, path, ref)


def scale(v, bits, out_bits=8):
    """Scaled to the output range rounding to nearest, as the BMP Suite's renderings (wider fields keep their top bits)."""
    v = v.astype(np.uint64)
    if bits >= out_bits:
        r = v >> (bits - out_bits)
    else:
        m, o = (1 << bits) - 1, (1 << out_bits) - 1
        r = (v * o + m // 2) // m
    return r.astype(np.uint16 if out_bits > 8 else np.uint8)


def hand_files():
    rng = np.random.default_rng(7)
    w, h = 21, 13

    # palette depths, bottom-up and top-down, OS/2 1.x (12 byte header, 3 byte entries)
    for bpp in (1, 2, 4, 8):
        n = 1 << bpp
        pal = [tuple(int(x) for x in rng.integers(0, 256, 3)) for _ in range(n)]
        idx = rng.integers(0, n, (h, w))
        rows = indices_to_rows(idx, bpp)
        pix = pack_rows(rows)
        info = info40(w, h, bpp)
        pb = palette_bytes(pal)
        hand(f'pal{bpp}', file_header(len(info) + len(pb) + len(pix), 14 + len(info) + len(pb)) + info + pb + pix, ref_from_palette(idx, pal))
        info = info40(w, -h, bpp)
        pix = pack_rows(rows, top_down=True)
        hand(f'pal{bpp}_topdown', file_header(len(info) + len(pb) + len(pix), 14 + len(info) + len(pb)) + info + pb + pix, ref_from_palette(idx, pal))
        if bpp != 2:
            core = struct.pack('<IHHHH', 12, w, h, 1, bpp)
            pb3 = palette_bytes(pal, 3)
            pix = pack_rows(rows)
            hand(f'os2v1_pal{bpp}', file_header(len(core) + len(pb3) + len(pix), 14 + len(core) + len(pb3)) + core + pb3 + pix, ref_from_palette(idx, pal))

    # short palette (clrUsed) with out of range indices -> black; gray palette -> Gray
    pal = [(10, 20, 30), (200, 100, 50), (0, 255, 0)]
    idx = rng.integers(0, 6, (h, w))
    rows = indices_to_rows(idx, 8)
    pix = pack_rows(rows)
    info = info40(w, h, 8, clr_used=3)
    pb = palette_bytes(pal)
    hand('pal8_short_palette', file_header(len(info) + len(pb) + len(pix), 14 + len(info) + len(pb)) + info + pb + pix, ref_from_palette(idx, pal))
    gpal = [(i * 17, i * 17, i * 17) for i in range(16)]
    idx = rng.integers(0, 16, (h, w))
    pix = pack_rows(indices_to_rows(idx, 4))
    pb = palette_bytes(gpal)
    info = info40(w, h, 4, clr_used=16)
    hand('pal4_gray', file_header(len(info) + len(pb) + len(pix), 14 + len(info) + len(pb)) + info + pb + pix, ref_from_palette(idx, gpal))

    # 24 bit, 32 bit BI_RGB (fourth byte ignored), 16 bit default 5:5:5
    rgb = rng.integers(0, 256, (h, w, 3), dtype=np.uint8)
    rows = [bytes(rgb[y, :, ::-1].tobytes()) for y in range(h)]
    pix = pack_rows(rows)
    info = info40(w, h, 24, ppm=(11811, 3937))
    hand('rgb24_dpi', file_header(len(info) + len(pix), 54) + info + pix, rgb)
    junk = rng.integers(0, 256, (h, w, 1), dtype=np.uint8)
    rows = [bytes(np.concatenate([rgb[y, :, ::-1], junk[y]], axis=1).tobytes()) for y in range(h)]
    pix = pack_rows(rows)
    info = info40(w, h, 32)
    hand('rgb32_reserved', file_header(len(info) + len(pix), 54) + info + pix, rgb)
    v555 = rng.integers(0, 1 << 15, (h, w), dtype=np.uint16)
    rows = [v555[y].astype('<u2').tobytes() for y in range(h)]
    pix = pack_rows(rows)
    info = info40(w, h, 16)
    ref = np.stack([scale(v555 >> 10 & 31, 5), scale(v555 >> 5 & 31, 5), scale(v555 & 31, 5)], axis=-1)
    hand('rgb16_555', file_header(len(info) + len(pix), 54) + info + pix, ref)

    # bit fields: 565 (masks after a 40 byte header), 4444 with alpha (V4), 10:10:10:2 (wide -> UInt16), BI_ALPHABITFIELDS
    def bitfields(name, bpp, masks, size, compression=3, alpha_zero=False):
        n = bpp // 8
        vals = rng.integers(0, 1 << min(bpp, 32), (h, w), dtype=np.uint64).astype(np.uint32)
        if alpha_zero:
            vals &= ~np.uint32(masks[3])
        rows = [b''.join(int(v).to_bytes(4, 'little')[:n] for v in vals[y]) for y in range(h)]
        pix = pack_rows(rows)
        widths = [bin(m).count('1') for m in masks]
        wide = max(widths) > 8
        chans = []
        for m, bw in zip(masks, widths):
            if m == 0:
                continue
            sh = (m & -m).bit_length() - 1
            chans.append(scale((vals >> sh) & ((1 << bw) - 1), bw, 16 if wide else 8))
        ref = np.stack(chans[:3] + [chans[3]] if masks[3] else chans[:3], axis=-1)
        if masks[3] and alpha_zero:
            ref[:, :, 3] = 65535 if wide else 255
        if size == 40:
            info = info40(w, h, bpp, compression)
            extra = struct.pack('<4I', *masks) if compression == 6 else struct.pack('<3I', *masks[:3])
        else:
            info = info_v(size, w, h, bpp, compression, masks)
            extra = b''
        hand(name, file_header(len(info) + len(extra) + len(pix), 14 + len(info) + len(extra)) + info + extra + pix, ref)

    bitfields('bf565', 16, (0xF800, 0x07E0, 0x001F, 0), 40)
    bitfields('bf4444_v4', 16, (0x0F00, 0x00F0, 0x000F, 0xF000), 108)
    bitfields('bf1555_v3', 16, (0x7C00, 0x03E0, 0x001F, 0x8000), 56)
    bitfields('bf101010_2_v5', 32, (0x3FF00000, 0x000FFC00, 0x000003FF, 0xC0000000), 124)
    bitfields('bf_alphabitfields', 32, (0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000), 40, compression=6)
    bitfields('bf_rgba_alpha_zero', 32, (0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000), 108, alpha_zero=True)
    bitfields('bf24', 24, (0xFF0000, 0x00FF00, 0x0000FF, 0), 40)
    bitfields('bf_bgr_order_v2', 32, (0x000000FF, 0x0000FF00, 0x00FF0000, 0), 52)
    bitfields('bf_narrow_332', 16, (0xE0, 0x1C, 0x03, 0), 40)

    # RLE8 / RLE4: plain runs, absolute runs, delta and early EOL / EOB leaving holes, top-down
    def rle8(idx, holes):
        out = bytearray()
        defined = np.ones(idx.shape, bool)
        hh, ww = idx.shape
        y = 0
        while y < hh:
            row = idx[hh - 1 - y]
            x = 0
            if holes and y == 2:
                # delta: skip 5 right, 2 up
                out += bytes([0, 2, 5, 2])
                defined[hh - 1 - y, 0:ww] = False
                defined[hh - 1 - (y + 1), :] = False
                defined[hh - 1 - (y + 2), 0:5] = False
                y += 2
                row = idx[hh - 1 - y]
                x = 5
            while x < ww:
                if holes and y == 5 and x == 9:   # early end of line
                    defined[hh - 1 - y, x:] = False
                    break
                run = 1
                while x + run < ww and row[x + run] == row[x] and run < 255:
                    run += 1
                if run >= 2:
                    out += bytes([run, int(row[x])])
                    x += run
                else:
                    lit = min(ww - x, 5)
                    if lit >= 3:
                        out += bytes([0, lit]) + bytes(int(v) for v in row[x:x + lit])
                        if lit % 2:
                            out += b'\0'
                        x += lit
                    else:
                        out += bytes([1, int(row[x])])
                        x += 1
            out += bytes([0, 0])
            y += 1
            if holes and y == hh - 2:   # end of bitmap: the last two rows stay empty
                defined[0:2, :] = False
                break
        out += bytes([0, 1])
        return bytes(out), defined

    def rle4(idx):
        out = bytearray()
        hh, ww = idx.shape
        for y in range(hh):
            row = idx[hh - 1 - y]
            x = 0
            while x < ww:
                # alternating pair run where possible, else absolute run of 4
                if x + 4 <= ww and (x // 4) % 2 == 0:
                    vals = [int(v) for v in row[x:x + 4]]
                    out += bytes([0, 4, vals[0] << 4 | vals[1], vals[2] << 4 | vals[3]])
                    x += 4
                else:
                    n = min(ww - x, 6)
                    a, b = int(row[x]), int(row[x + 1]) if x + 1 < ww else 0
                    # a run of n pixels alternates a, b: only usable where the data does
                    k = 1
                    while k < n and int(row[x + k]) == (a if k % 2 == 0 else b):
                        k += 1
                    out += bytes([k, a << 4 | b])
                    x += k
            out += bytes([0, 0])
        out += bytes([0, 1])
        return bytes(out)

    pal = [tuple(int(x) for x in rng.integers(0, 256, 3)) for _ in range(256)]
    idx = (np.add.outer(np.arange(h) // 3, np.arange(w) // 4) % 7).astype(np.int64)
    idx[4, 3:12] = rng.integers(0, 256, 9)
    for holes in (False, True):
        data, defined = rle8(idx, holes)
        info = info40(w, h, 8, compression=1, image_size=len(data))
        pb = palette_bytes(pal)
        ref = ref_from_palette(idx, pal, gray=False)
        if holes:
            ref = np.concatenate([ref, np.full(ref.shape[:2] + (1,), 255, np.uint8)], axis=-1)
            ref[~defined] = 0
        hand('rle8' + ('_holes' if holes else ''), file_header(len(info) + len(pb) + len(data), 14 + len(info) + len(pb)) + info + pb + data, ref)
    pal16 = pal[:16]
    idx4 = rng.integers(0, 16, (h, w))
    data = rle4(idx4)
    info = info40(w, h, 4, compression=2, image_size=len(data))
    pb = palette_bytes(pal16)
    hand('rle4', file_header(len(info) + len(pb) + len(data), 14 + len(info) + len(pb)) + info + pb + data, ref_from_palette(idx4, pal16))

    # OS/2 2.x (64 byte header) with RLE24
    rgb = (np.add.outer(np.arange(h) // 4, np.arange(w) // 5)[:, :, None] * np.array([40, 90, 20])[None, None, :] % 256).astype(np.uint8)
    out = bytearray()
    for y in range(h):
        row = rgb[h - 1 - y]
        x = 0
        while x < w:
            run = 1
            while x + run < w and (row[x + run] == row[x]).all() and run < 255:
                run += 1
            if run >= 2:
                out += bytes([run]) + bytes(row[x, ::-1].tolist())
                x += run
            elif w - x >= 3:
                out += bytes([0, 3]) + b''.join(bytes(row[x + i, ::-1].tolist()) for i in range(3))
                out += b'\0'   # 9 bytes: padded to even
                x += 3
            else:
                out += bytes([1]) + bytes(row[x, ::-1].tolist())
                x += 1
        out += bytes([0, 0])
    out += bytes([0, 1])
    info = struct.pack('<IiiHHIIiiII', 64, w, h, 1, 24, 4, len(out), 0, 0, 0, 0) + bytes(24)
    hand('os2v2_rle24', file_header(len(info) + len(out), 14 + len(info)) + info + bytes(out), rgb)

    # embedded PNG and JPEG (the pixel data is a complete file of that format)
    src = test_pattern(29, 17, 3)
    bio = io.BytesIO()
    Image.fromarray(src).save(bio, 'PNG')
    png = bio.getvalue()
    info = info40(29, 17, 0, compression=5, image_size=len(png))
    hand('embedded_png', file_header(len(info) + len(png), 54) + info + png, src)
    bio = io.BytesIO()
    Image.fromarray(src).save(bio, 'JPEG', quality=90)
    jpg = bio.getvalue()
    info = info40(29, 17, 0, compression=4, image_size=len(jpg))
    hand('embedded_jpeg', file_header(len(info) + len(jpg), 54) + info + jpg, np.array(Image.open(io.BytesIO(jpg)).convert('RGB')))

    # V5 with an embedded ICC profile after the pixels
    rgb = rng.integers(0, 256, (h, w, 3), dtype=np.uint8)
    pix = pack_rows([bytes(rgb[y, :, ::-1].tobytes()) for y in range(h)])
    icc = bytes(range(256)) * 3
    info = info_v(124, w, h, 24, 0, cs=0x4D424544, profile=(124 + len(pix), len(icc)))
    hand('v5_icc', file_header(len(info) + len(pix) + len(icc), 14 + len(info)) + info + pix + icc, rgb)

    # truncated pixel data: the missing rows are black (bottom-up: the top rows)
    data = file_header(len(info40(w, h, 24)) + len(pack_rows([bytes(rgb[y, :, ::-1].tobytes()) for y in range(h)])), 54) + info40(w, h, 24) \
        + pack_rows([bytes(rgb[y, :, ::-1].tobytes()) for y in range(h)])
    stride = (w * 3 + 3) // 4 * 4
    cut = 54 + stride * 8 + 10
    ref = rgb.copy()
    ref[:h - 8] = 0
    ref[h - 9, :10 // 3] = rgb[h - 9, :10 // 3]
    save('truncated24', data[:cut], ref)   # (the tools disagree among themselves here)

    # wrong bfOffBits (zero): pixels follow the palette
    pal = [(i, 255 - i, i // 2) for i in range(0, 256, 16)]
    idx = rng.integers(0, 16, (h, w))
    pix = pack_rows(indices_to_rows(idx, 4))
    info = info40(w, h, 4, clr_used=16)
    pb = palette_bytes(pal)
    hand('bad_offset', file_header(len(info) + len(pb) + len(pix), 0) + info + pb + pix, ref_from_palette(idx, pal))


def main():
    os.makedirs(OUT, exist_ok=True)
    for f in os.listdir(OUT):
        os.remove(os.path.join(OUT, f))
    tool_files()
    hand_files()
    print(f'{len([f for f in os.listdir(OUT) if f.endswith(".bmp")])} files in {OUT}')


if __name__ == '__main__':
    main()
