"""Generates tests/fixtures/sun: name.ras and name.ras.ref (what SunRaster.Decode returns).

Files written by ImageMagick (Pillow does not write the format, ImageMagick ignores -compress RLE) with a tool's decode
as the reference, and hand built files from the Sun
rasterfile description (rasterfile.h / the rasterfile(5) manual page) with references computed from what went into
them: depth 1, 8, 24 and 32; RT_OLD, RT_STANDARD, RT_BYTE_ENCODED and RT_FORMAT_RGB; no colour map, RMT_EQUAL_RGB and
RMT_RAW maps; odd widths (rows padded to 16 bits); escapes of 0x80 in the RLE; truncation. All are cross checked against
Pillow and ImageMagick (disagreements are printed, --report prints all comparisons).

Decoding rules the references follow: 1 bit without a map is 1 black, 0 white; with a map the bit is the index
(ImageMagick inverts it); 8 bit without a map is gray; indices outside the map are black (ImageMagick refuses such
files); RMT_RAW maps are ignored; colour mapped images whose map is all gray are Gray; the leading byte of 32 bit
pixels is alpha (as ImageMagick reads and writes it; Pillow takes the last byte as padding) unless it is zero everywhere; RT_OLD ignores the
length field; RT_BYTE_ENCODED codes the rows with their padding and keeps the B, G, R order (Pillow leaves the padding
out, ImageMagick takes R, G, B and stops short of the end); a truncated file is black where the data ends.

usage: python3 gen_sun.py [--report]
"""
import os
import struct
import numpy as np
from PIL import Image
from pxref import test_pattern
from legacy_common import clear, save, check, tool_ref, magick_write

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'fixtures', 'sun')


def ras(w, h, depth, typ, maptype, cmap, body, length=None):
    return struct.pack('>8I', 0x59a66a95, w, h, depth, len(body) if length is None else length, typ, maptype, len(cmap)) + cmap + body


def rows(img_bytes_per_row, h):
    """Pads rows (list of bytes) to 16 bits."""
    return b''.join(r + (b'\0' if len(r) % 2 else b'') for r in img_bytes_per_row)


def pack1(bits_row):
    out = bytearray((len(bits_row) + 7) // 8)
    for x, b in enumerate(bits_row):
        out[x // 8] |= int(b) << (7 - x % 8)
    return bytes(out)


def rle(data):
    out = bytearray()
    i, n = 0, len(data)
    while i < n:
        v = data[i]
        run = 1
        while i + run < n and run < 256 and data[i + run] == v:
            run += 1
        if run >= 3 or v == 0x80:
            if v == 0x80 and run == 1:
                out += b'\x80\x00'
            else:
                out += bytes([0x80, run - 1, v])
        else:
            out += bytes([v]) * run
        i += run
    return bytes(out)


def hand(name, data, ref):
    check(name, save(OUT, name + '.ras', data, ref), ref)


def tool_files():
    rgb = test_pattern(37, 23, 3)
    gray = test_pattern(37, 23, 1)[:, :, 0]
    rgba = test_pattern(37, 23, 4, seed=2)
    rgba[:, :, 3] = np.linspace(1, 255, 37, dtype=np.uint8)[None, :]

    def from_magick(name, mode, src, args, channels, who='pillow'):
        tmp = os.path.join(OUT, '_src.png')
        Image.fromarray(src, mode).save(tmp)
        path = os.path.join(OUT, name + '.ras')
        magick_write([tmp], args, path)
        os.remove(tmp)
        ref = tool_ref(path, channels, who)
        with open(path, 'rb') as f:
            save(OUT, name + '.ras', f.read(), ref)
        check(name, path, ref)

    from_magick('im_rgb', 'RGB', rgb, [], 3)
    from_magick('im_gray', 'L', gray, [], 1)
    from_magick('im_pal', 'RGB', rgb, ['-colors', '30', '-type', 'Palette'], 3)
    from_magick('im_mono', 'L', gray, ['-monochrome'], 1)
    from_magick('im_rgba', 'RGBA', rgba, [], 4, who='magick')


def hand_files():
    rng = np.random.default_rng(9)
    w, h = 19, 7          # odd: rows are padded
    # 1 bit, no map: 1 black
    m = rng.integers(0, 2, (h, w))
    body = rows([pack1(r) for r in m], h)
    exp = np.where(m == 1, 0, 255).astype(np.uint8)[:, :, None]
    hand('mono', ras(w, h, 1, 1, 0, b'', body), exp)
    hand('mono_rle', ras(w, h, 1, 2, 0, b'', rle(body)), exp)
    # 1 bit with a two entry map: the bit is the index
    cmap = bytes([200, 10, 0, 20, 0, 30])
    pal = np.array([[200, 0, 0], [10, 20, 30]], np.uint8)
    hand('mono_map', ras(w, h, 1, 1, 1, cmap, body), pal[m])

    # 8 bit gray, map, raw map, out of range indices
    g = rng.integers(0, 256, (h, w)).astype(np.uint8)
    g[2, 3:15] = 0x80                     # a run of the escape byte
    g[3, 4] = 0x80                        # a single one
    body = rows([r.tobytes() for r in g], h)
    hand('gray8', ras(w, h, 8, 1, 0, b'', body), g[:, :, None])
    hand('gray8_rle', ras(w, h, 8, 2, 0, b'', rle(body)), g[:, :, None])
    hand('gray8_old', ras(w, h, 8, 0, 0, b'', body, length=0), g[:, :, None])
    hand('gray8_rawmap', ras(w, h, 8, 1, 2, bytes(range(30)), body), g[:, :, None])
    n = 200
    pal = rng.integers(0, 256, (n, 3), dtype=np.uint8)
    cmap = pal[:, 0].tobytes() + pal[:, 1].tobytes() + pal[:, 2].tobytes()
    idx = rng.integers(0, n, (h, w)).astype(np.uint8)
    idx[0, :3] = [n, 230, 255]
    body = rows([r.tobytes() for r in idx], h)
    full = np.concatenate([pal, np.zeros((256 - n, 3), np.uint8)])
    hand('map8_out_of_range', ras(w, h, 8, 1, 1, cmap, body), full[idx])
    hand('map8_rle', ras(w, h, 8, 2, 1, cmap, rle(body)), full[idx])
    gp = np.repeat(np.arange(0, 256, 2, dtype=np.uint8)[:, None], 3, 1)
    gidx = (idx // 2).astype(np.uint8)
    hand('map8_gray', ras(w, h, 8, 1, 1, gp[:, 0].tobytes() * 3, rows([r.tobytes() for r in gidx], h)), gp[gidx][:, :, :1])

    # 24 and 32 bit, standard (BGR) and RGB order, RLE
    rgb = rng.integers(0, 256, (h, w, 3), dtype=np.uint8)
    rgb[1, 2:10] = [0x80, 0x80, 0x80]
    bgr_rows = [rgb[y, :, ::-1].tobytes() for y in range(h)]
    rgb_rows = [rgb[y].tobytes() for y in range(h)]
    hand('rgb24', ras(w, h, 24, 1, 0, b'', rows(bgr_rows, h)), rgb)
    hand('rgb24_rle', ras(w, h, 24, 2, 0, b'', rle(rows(bgr_rows, h))), rgb)
    hand('rgb24_format_rgb', ras(w, h, 24, 3, 0, b'', rows(rgb_rows, h)), rgb)
    hand('rgb24_unused_map', ras(w, h, 24, 1, 1, bytes(6), rows(bgr_rows, h)), rgb)
    a = rng.integers(1, 256, (h, w, 1), dtype=np.uint8)
    rgba = np.concatenate([rgb, a], -1)
    abgr = [np.concatenate([a[y], rgb[y, :, ::-1]], -1).tobytes() for y in range(h)]
    argb = [np.concatenate([a[y], rgb[y]], -1).tobytes() for y in range(h)]
    hand('rgba32', ras(w, h, 32, 1, 0, b'', rows(abgr, h)), rgba)
    hand('rgba32_format_rgb', ras(w, h, 32, 3, 0, b'', rows(argb, h)), rgba)
    hand('rgba32_rle', ras(w, h, 32, 2, 0, b'', rle(rows(abgr, h))), rgba)
    xbgr = [np.concatenate([np.zeros((w, 1), np.uint8), rgb[y, :, ::-1]], -1).tobytes() for y in range(h)]
    exp = np.concatenate([rgb, np.full((h, w, 1), 255, np.uint8)], -1)
    hand('xbgr32_pad_zero', ras(w, h, 32, 1, 0, b'', rows(xbgr, h)), exp)

    # truncated: 24 bit cut in row 3
    body = rows(bgr_rows, h)
    rb = len(bgr_rows[0]) + len(bgr_rows[0]) % 2
    exp = rgb.copy()
    exp[3:] = 0
    exp[3, :4] = rgb[3, :4]
    save(OUT, 'truncated24.ras', ras(w, h, 24, 1, 0, b'', body)[:32 + 3 * rb + 12], exp)


def main():
    clear(OUT)
    tool_files()
    hand_files()
    print(f'{len([f for f in os.listdir(OUT) if not f.endswith(".ref")])} files in {OUT}')


if __name__ == '__main__':
    main()
