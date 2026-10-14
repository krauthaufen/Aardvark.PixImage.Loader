"""Generates tests/fixtures/tga: name.tga and name.tga.ref (what Tga.Decode returns).

As gen_bmp.py: files written by Pillow and ImageMagick with Pillow's decode as the reference (cross checked against
ImageMagick), and hand built files from the format description with references computed from what went into them,
cross checked against both tools (disagreements are printed, --report prints all comparisons).

Decoding rules the references follow: 5 bit values are expanded by bit replication; 32 bit pixels and 32 bit palette
entries carry alpha, 16 bit ones only when the descriptor gives alpha bits; the TGA 2.0 extension area's alpha type 0,
1 and 2 drops alpha, 4 means premultiplied; an alpha channel that is zero everywhere is opaque; palette indices
outside the colour map are opaque black.

usage: python3 gen_tga.py [--report]
"""
import io
import os
import struct
import subprocess
import sys
import numpy as np
from PIL import Image
from pxref import write_ref, test_pattern
from gen_bmp import compare, magick

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'fixtures', 'tga')
REPORT = '--report' in sys.argv


def save(name, data, ref):
    path = os.path.join(OUT, name + '.tga')
    with open(path, 'wb') as f:
        f.write(data)
    if ref.ndim == 2:
        ref = ref[:, :, None]
    write_ref(path + '.ref', ref)
    return path


def check_tools(name, path, ref):
    c = ref.shape[2] if ref.ndim == 3 else 1
    r = ref if ref.ndim == 3 else ref[:, :, None]
    try:
        with Image.open(path) as im:
            p = np.array(im.convert({1: 'L', 2: 'LA', 3: 'RGB', 4: 'RGBA'}[c]))
        compare(name, r, p if p.ndim == 3 else p[:, :, None], 'pillow')
    except Exception as e:
        if REPORT:
            print(f'  {name}: pillow cannot read it ({e})')
    compare(name, r, magick(path, c), 'magick')


def hand(name, data, ref):
    check_tools(name, save(name, data, ref), ref)


def five(v):
    v = v & 31
    return ((v << 3) | (v >> 2)).astype(np.uint8)


def header(id_len, cmap_type, img_type, cmap_first, cmap_len, cmap_bits, w, h, bpp, desc):
    return struct.pack('<BBBHHBHHHHBB', id_len, cmap_type, img_type, cmap_first, cmap_len, cmap_bits, 0, 0, w, h, bpp, desc)


def footer(ext_offset):
    return struct.pack('<II', ext_offset, 0) + b'TRUEVISION-XFILE.\0'


def ext_area(alpha_type):
    e = bytearray(495)
    e[0:2] = struct.pack('<H', 495)
    e[494] = alpha_type
    return bytes(e)


def rle(pixels, bpp, cross_rows=True, w=None):
    """pixels: list of bytes objects, one per pixel, in file order."""
    out = bytearray()
    i, n = 0, len(pixels)
    while i < n:
        limit = n if cross_rows else (i // w + 1) * w
        run = 1
        while i + run < limit and run < 128 and pixels[i + run] == pixels[i]:
            run += 1
        if run >= 2:
            out += bytes([0x80 | (run - 1)]) + pixels[i]
            i += run
        else:
            lit = 1
            while i + lit < limit and lit < 128 and not (i + lit + 1 < limit and pixels[i + lit] == pixels[i + lit + 1]):
                lit += 1
            out += bytes([lit - 1]) + b''.join(pixels[i:i + lit])
            i += lit
    return bytes(out)


def file_order(img, top_down, right_to_left):
    rows = img if top_down else img[::-1]
    if right_to_left:
        rows = rows[:, ::-1]
    return rows


def tool_files():
    rgb = test_pattern(37, 23, 3)
    rgba = test_pattern(37, 23, 4, seed=2)
    rgba[:, :, 3] = np.linspace(1, 255, 37, dtype=np.uint8)[None, :]
    gray = test_pattern(37, 23, 1)[:, :, 0]

    def from_pillow(name, im, mode, **kw):
        bio = io.BytesIO()
        im.save(bio, 'TGA', **kw)
        path = save(name, bio.getvalue(), np.zeros((1, 1, 1), np.uint8))
        with Image.open(path) as back:
            ref = np.array(back.convert(mode))
        save(name, bio.getvalue(), ref)
        compare(name, ref if ref.ndim == 3 else ref[:, :, None], magick(path, len(mode)), 'magick')

    from_pillow('pil_rgb', Image.fromarray(rgb), 'RGB')
    from_pillow('pil_rgba', Image.fromarray(rgba, 'RGBA'), 'RGBA')
    from_pillow('pil_rgb_rle', Image.fromarray(rgb), 'RGB', compression='tga_rle')
    from_pillow('pil_rgba_rle', Image.fromarray(rgba, 'RGBA'), 'RGBA', compression='tga_rle')
    from_pillow('pil_gray', Image.fromarray(gray), 'L')
    from_pillow('pil_gray_rle', Image.fromarray(gray), 'L', compression='tga_rle')
    from_pillow('pil_la', Image.fromarray(np.stack([gray, rgba[:, :, 3]], -1), 'LA'), 'LA')
    from_pillow('pil_pal', Image.fromarray(rgb).quantize(60), 'RGB')
    from_pillow('pil_pal_rle', Image.fromarray(rgb).quantize(60), 'RGB', compression='tga_rle')
    from_pillow('pil_rgb_topleft', Image.fromarray(rgb), 'RGB', orientation=1)

    def from_magick(name, src_mode, src, args, mode):
        tmp = os.path.join(OUT, '_src.png')
        Image.fromarray(src, src_mode).save(tmp)
        path = os.path.join(OUT, name + '.tga')
        subprocess.run(['magick', tmp] + args + [path], check=True)
        os.remove(tmp)
        with Image.open(path) as im:
            ref = np.array(im.convert(mode))
        with open(path, 'rb') as f:
            data = f.read()
        save(name, data, ref)
        compare(name, ref if ref.ndim == 3 else ref[:, :, None], magick(path, len(mode)), 'magick')

    from_magick('im_rgb', 'RGB', rgb, [], 'RGB')
    from_magick('im_rgba_rle', 'RGBA', rgba, ['-compress', 'RLE'], 'RGBA')
    from_magick('im_pal', 'RGB', rgb, ['-colors', '30', '-type', 'Palette'], 'RGB')
    from_magick('im_gray_rle', 'L', gray, ['-compress', 'RLE'], 'L')


def hand_files():
    rng = np.random.default_rng(11)
    w, h = 19, 11

    # true colour 24 / 32 bit, every origin corner, raw and RLE (runs crossing rows)
    rgb = (np.add.outer(np.arange(h) // 2, np.arange(w) // 3)[:, :, None] * np.array([30, 70, 110])[None, None, :] % 256).astype(np.uint8)
    rgb[3, 4:9] = rng.integers(0, 256, (5, 3))
    for td in (False, True):
        for rtl in (False, True):
            tag = ('t' if td else 'b') + ('r' if rtl else 'l')
            fo = file_order(rgb, td, rtl)
            px = [bytes(fo[y, x, ::-1].tolist()) for y in range(h) for x in range(w)]
            desc = (0x20 if td else 0) | (0x10 if rtl else 0)
            hand(f'rgb24_{tag}', header(0, 0, 2, 0, 0, 0, w, h, 24, desc) + b''.join(px), rgb)
            hand(f'rgb24_rle_{tag}', header(0, 0, 10, 0, 0, 0, w, h, 24, desc) + rle(px, 3), rgb)

    rgba = np.concatenate([rgb, rng.integers(1, 256, (h, w, 1), dtype=np.uint8)], -1)
    fo = file_order(rgba, False, False)
    px = [bytes([fo[y, x, 2], fo[y, x, 1], fo[y, x, 0], fo[y, x, 3]]) for y in range(h) for x in range(w)]
    hand('rgba32', header(0, 0, 2, 0, 0, 0, w, h, 32, 8) + b''.join(px), rgba)
    hand('rgba32_no_alpha_bits', header(0, 0, 2, 0, 0, 0, w, h, 32, 0) + b''.join(px), rgba)
    body = header(0, 0, 2, 0, 0, 0, w, h, 32, 8) + b''.join(px)
    hand('rgba32_ext_no_alpha', body + ext_area(0) + footer(len(body)), rgb)
    hand('rgba32_ext_alpha', body + ext_area(3) + footer(len(body)), rgba)
    # premultiplied: store premultiplied colours, expect them divided out again
    pm = rgba.copy()
    a = rgba[:, :, 3:4].astype(int)
    pm[:, :, :3] = (rgba[:, :, :3].astype(int) * a + 127) // 255
    exp = pm.copy()
    exp[:, :, :3] = np.minimum(255, (pm[:, :, :3].astype(int) * 255 + a // 2) // np.maximum(a, 1))
    fo = file_order(pm, False, False)
    pxp = [bytes([fo[y, x, 2], fo[y, x, 1], fo[y, x, 0], fo[y, x, 3]]) for y in range(h) for x in range(w)]
    body = header(0, 0, 2, 0, 0, 0, w, h, 32, 8) + b''.join(pxp)
    hand('rgba32_ext_premultiplied', body + ext_area(4) + footer(len(body)), exp)
    # all zero alpha: opaque
    z = rgba.copy()
    z[:, :, 3] = 0
    fo = file_order(z, False, False)
    pxz = [bytes([fo[y, x, 2], fo[y, x, 1], fo[y, x, 0], 0]) for y in range(h) for x in range(w)]
    exp = z.copy()
    exp[:, :, 3] = 255
    hand('rgba32_alpha_zero', header(0, 0, 2, 0, 0, 0, w, h, 32, 8) + b''.join(pxz), exp)

    # 15 / 16 bit
    v = rng.integers(0, 1 << 16, (h, w), dtype=np.uint32)
    fo = file_order(v, False, False)
    px16 = [int(fo[y, x]).to_bytes(2, 'little') for y in range(h) for x in range(w)]
    c = np.stack([five(v >> 10), five(v >> 5), five(v)], -1)
    hand('rgb15', header(0, 0, 2, 0, 0, 0, w, h, 15, 0) + b''.join(px16), c)
    hand('rgb16_no_alpha_bits', header(0, 0, 2, 0, 0, 0, w, h, 16, 0) + b''.join(px16), c)
    hand('argb1555', header(0, 0, 10, 0, 0, 0, w, h, 16, 1) + rle(px16, 2),
         np.concatenate([c, np.where(v >> 15, 255, 0).astype(np.uint8)[:, :, None]], -1))

    # gray 8 and gray + alpha 16, with an image ID field
    g = rng.integers(0, 256, (h, w), dtype=np.uint8)
    ident = b'pixcodec test'
    fo = file_order(g, True, False)
    hand('gray8_id_topdown', header(len(ident), 0, 3, 0, 0, 0, w, h, 8, 0x20) + ident + fo.tobytes(), g)
    ga = np.stack([g, rng.integers(1, 256, (h, w), dtype=np.uint8)], -1)
    fo = file_order(ga, False, False)
    pxg = [bytes(fo[y, x].tolist()) for y in range(h) for x in range(w)]
    hand('gray16_alpha_rle', header(0, 0, 11, 0, 0, 0, w, h, 16, 8) + rle(pxg, 2), ga)

    # colour-mapped: 24 bit entries with a first index, 32 bit entries (alpha), 16 bit entries, 16 bit indices,
    # out of range indices, and a colour map in a true-colour file (skipped)
    n = 40
    pal = rng.integers(0, 256, (n, 3), dtype=np.uint8)
    first = 5
    idx = rng.integers(first, first + n, (h, w))
    fo = file_order(idx, False, False)
    cmap = b''.join(bytes(p[::-1].tolist()) for p in pal)
    hand('cmap24_first5', header(0, 1, 1, first, n, 24, w, h, 8, 0) + cmap + fo.astype(np.uint8).tobytes(), pal[idx - first])
    px8 = [bytes([int(fo[y, x])]) for y in range(h) for x in range(w)]
    hand('cmap24_rle', header(0, 1, 9, first, n, 24, w, h, 8, 0) + cmap + rle(px8, 1), pal[idx - first])
    pal4 = np.concatenate([pal, rng.integers(1, 256, (n, 1), dtype=np.uint8)], -1)
    cmap32 = b''.join(bytes([p[2], p[1], p[0], p[3]]) for p in pal4)
    hand('cmap32_alpha', header(0, 1, 1, first, n, 32, w, h, 8, 8) + cmap32 + fo.astype(np.uint8).tobytes(), pal4[idx - first])
    p16 = rng.integers(0, 1 << 15, n, dtype=np.uint32)
    cmap16 = b''.join(int(v).to_bytes(2, 'little') for v in p16)
    pc = np.stack([five(p16 >> 10), five(p16 >> 5), five(p16)], -1)
    hand('cmap15_idx16', header(0, 1, 1, first, n, 15, w, h, 16, 0) + cmap16 + fo.astype('<u2').tobytes(), pc[idx - first])
    bad = idx.copy()
    bad[0, :4] = [0, 1, first + n, 255]
    fo2 = file_order(bad, False, False)
    exp = np.zeros((h, w, 3), np.uint8)
    ok = (bad >= first) & (bad < first + n)
    exp[ok] = pal[bad[ok] - first]
    hand('cmap_out_of_range', header(0, 1, 1, first, n, 24, w, h, 8, 0) + cmap + fo2.astype(np.uint8).tobytes(), exp)
    fo = file_order(rgb, False, False)
    px = [bytes(fo[y, x, ::-1].tolist()) for y in range(h) for x in range(w)]
    hand('rgb24_with_unused_cmap', header(0, 1, 2, 0, 3, 24, w, h, 24, 0) + bytes(9) + b''.join(px), rgb)

    # truncated: the missing (top) rows are black; a cut inside an RLE packet keeps what came before
    body = header(0, 0, 2, 0, 0, 0, w, h, 24, 0) + b''.join(px)
    cut = 18 + 3 * (w * 6 + 4)
    exp = rgb.copy()
    exp[:h - 6] = 0
    exp[h - 7, :4] = rgb[h - 7, :4]
    save('truncated24', body[:cut], exp)


def main():
    os.makedirs(OUT, exist_ok=True)
    for f in os.listdir(OUT):
        os.remove(os.path.join(OUT, f))
    tool_files()
    hand_files()
    print(f'{len([f for f in os.listdir(OUT) if f.endswith(".tga")])} files in {OUT}')


if __name__ == '__main__':
    main()
