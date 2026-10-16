"""Generates tests/fixtures/pcx: name.pcx / name.dcx and name.<ext>.ref (what Pcx.Decode returns; for DCX pages after
the first name.dcx.p<N>.ref).

Files written by Pillow and ImageMagick with a tool's decode as the reference, and hand built files from the ZSoft
format description (every bits / planes combination, versions 0 to 5, uncompressed and RLE, runs across scan lines,
padded lines, offset windows, missing VGA palette, truncation, DCX) with references computed from what went into them,
all cross checked against Pillow, ImageMagick and imagecodecs.pcx_decode (disagreements are printed, --report prints
all comparisons).

Decoding rules the references follow: plane p holds bits p * bits of the palette index; 1 bit monochrome is 0 black
1 white whatever the header palette says (as Pillow and imagecodecs read it); up to 4 index bits use the 16 entry
header palette (all versions, as every tool does), more use the VGA palette at the end of the file and without one the
index is the gray value; palette images whose palette entries (the 2^bits ones that can be used) are all gray are Gray;
8 bit in 4 planes is RGBA, an alpha plane of all zeros is opaque; a truncated file is black where the data ends.

usage: python3 gen_pcx.py [--report]
"""
import io
import os
import struct
import numpy as np
from PIL import Image
from pxref import test_pattern
from legacy_common import clear, save, check, tool_ref, magick_write, rgba

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'fixtures', 'pcx')


def imagecodecs_pcx(path):
    try:
        import imagecodecs
        with open(path, 'rb') as f:
            a = imagecodecs.pcx_decode(f.read())
    except Exception:  # noqa: BLE001
        return None
    if a.dtype == bool:
        a = np.where(a, 255, 0).astype(np.uint8)
    return rgba(a)


def header(ver, enc, bits, planes, w, h, bpl, pal16=b'', x0=0, y0=0, dpi=(72, 72), palinfo=1):
    p = (pal16 + bytes(48))[:48]
    return (struct.pack('<BBBBHHHHHH', 10, ver, enc, bits, x0, y0, x0 + w - 1, y0 + h - 1, dpi[0], dpi[1]) + p
            + bytes([0, planes]) + struct.pack('<HHHH', bpl, palinfo, 0, 0) + bytes(54))


def rle(data, cross=True, line=None):
    """ZSoft RLE of a byte string; runs cross scan lines unless cross is False (then every `line` bytes break)."""
    out = bytearray()
    i, n = 0, len(data)
    while i < n:
        limit = n if cross else (i // line + 1) * line
        v = data[i]
        run = 1
        while i + run < limit and run < 63 and data[i + run] == v:
            run += 1
        if run > 1 or v >= 0xC0:
            out += bytes([0xC0 | run, v])
        else:
            out.append(v)
        i += run
    return bytes(out)


def pack_plane(vals, bits, bpl):
    """Pixel values (< 2^bits) of one plane line packed most significant first into bpl bytes."""
    out = bytearray(bpl)
    per = 8 // bits
    for x, v in enumerate(vals):
        out[x // per] |= int(v) << (8 - bits - (x % per) * bits)
    return bytes(out)


def planar_lines(idx, bits, planes, bpl):
    """Scan lines of an index image split into planes (plane p = bits p*bits .. of the index)."""
    lines = []
    m = (1 << bits) - 1
    for row in idx:
        lines.append(b''.join(pack_plane((row >> (p * bits)) & m, bits, bpl) for p in range(planes)))
    return lines


def hand(name, data, ref, ext='.pcx'):
    path = save(OUT, name + ext, data, ref)
    check(name, path, ref, extra=imagecodecs_pcx if ext == '.pcx' else None)


def tool_files():
    rgb = test_pattern(37, 23, 3)
    gray = test_pattern(37, 23, 1)[:, :, 0]
    rgba_img = test_pattern(37, 23, 4, seed=2)
    rgba_img[:, :, 3] = np.linspace(1, 255, 37, dtype=np.uint8)[None, :]

    def from_pillow(name, im, channels):
        bio = io.BytesIO()
        im.save(bio, 'PCX')
        path = os.path.join(OUT, name + '.pcx')
        with open(path, 'wb') as f:
            f.write(bio.getvalue())
        ref = tool_ref(path, channels)
        save(OUT, name + '.pcx', bio.getvalue(), ref)
        check(name, path, ref, extra=imagecodecs_pcx)

    from_pillow('pil_rgb', Image.fromarray(rgb), 3)
    from_pillow('pil_gray', Image.fromarray(gray), 1)
    from_pillow('pil_pal', Image.fromarray(rgb).quantize(60), 3)
    from_pillow('pil_mono', Image.fromarray(gray > 128), 1)

    def from_magick(name, mode, src, args, channels, who='pillow'):
        tmp = os.path.join(OUT, '_src.png')
        Image.fromarray(src, mode).save(tmp)
        path = os.path.join(OUT, name + '.pcx')
        magick_write([tmp], args, path)
        os.remove(tmp)
        ref = tool_ref(path, channels, who)
        with open(path, 'rb') as f:
            save(OUT, name + '.pcx', f.read(), ref)
        check(name, path, ref, extra=imagecodecs_pcx)

    from_magick('im_rgb', 'RGB', rgb, [], 3)
    from_magick('im_gray', 'L', gray, [], 1)
    from_magick('im_pal', 'RGB', rgb, ['-colors', '30'], 3)
    from_magick('im_mono', 'L', gray, ['-monochrome'], 1)
    from_magick('im_rgba', 'RGBA', rgba_img, [], 4, who='magick')

    # DCX written by ImageMagick: three pages
    srcs = []
    for i, (mode, a) in enumerate([('RGB', rgb), ('L', gray), ('RGB', rgb[::-1].copy())]):
        p = os.path.join(OUT, f'_src{i}.png')
        Image.fromarray(a, mode).save(p)
        srcs.append(p)
    path = os.path.join(OUT, 'im_pages.dcx')
    magick_write(srcs, [], path)
    for p in srcs:
        os.remove(p)
    with open(path, 'rb') as f:
        data = f.read()
    for i, ch in enumerate([3, 1, 3]):
        ref = tool_ref(path, ch, 'magick', frame=i)
        save(OUT, 'im_pages.dcx', data, ref, '' if i == 0 else f'.p{i}')
        check(f'im_pages.dcx[{i}]', path, ref, frame=i)


def hand_files():
    rng = np.random.default_rng(5)
    w, h = 21, 9
    pal16 = rng.integers(0, 256, (16, 3), dtype=np.uint8)
    pal16[:2] = [[10, 20, 30], [200, 100, 50]]

    # every indexed bits / planes combination with the header palette, RLE and uncompressed
    for bits, planes in [(1, 2), (1, 3), (1, 4), (2, 1), (2, 2), (4, 1)]:
        nb = bits * planes
        idx = rng.integers(0, 1 << nb, (h, w))
        idx[2, 3:12] = 1                                   # a run
        bpl = ((w * bits + 7) // 8 + 1) & ~1
        lines = planar_lines(idx, bits, planes, bpl)
        ref = pal16[idx]
        hdr = header(5, 1, bits, planes, w, h, bpl, pal16.tobytes())
        hand(f'b{bits}p{planes}_rle', hdr + rle(b''.join(lines)), ref)
        hand(f'b{bits}p{planes}_raw', header(5, 0, bits, planes, w, h, bpl, pal16.tobytes()) + b''.join(lines), ref)
    # versions 0, 2, 3, 4: the header palette is used all the same
    idx = rng.integers(0, 16, (h, w))
    bpl = 12
    lines = planar_lines(idx, 1, 4, bpl)
    for ver in (0, 2, 3, 4):
        hand(f'ega_v{ver}', header(ver, 1, 1, 4, w, h, bpl, pal16.tobytes()) + rle(b''.join(lines)), pal16[idx])
    # 4 bit gray header palette -> Gray
    gp = np.repeat(np.arange(0, 256, 17, dtype=np.uint8)[:, None], 3, 1)
    lines = planar_lines(idx, 4, 1, 12)
    hand('b4p1_graypal', header(5, 1, 4, 1, w, h, 12, gp.tobytes()) + rle(b''.join(lines)), gp[idx][:, :, :1])

    # 1 bit monochrome: odd width, padded lines, header palette ignored, an offset window, 300 dpi
    m = rng.integers(0, 2, (h, w))
    bpl = 6                                                 # more than the 3 bytes needed
    lines = planar_lines(m, 1, 1, bpl)
    ref = np.where(m == 1, 255, 0).astype(np.uint8)
    hand('mono_padded', header(5, 1, 1, 1, w, h, bpl, pal16.tobytes(), x0=7, y0=3, dpi=(300, 300)) + rle(b''.join(lines)), ref)
    hand('mono_v3_raw', header(3, 0, 1, 1, w, h, bpl, b'', dpi=(0, 0)) + b''.join(lines), ref)

    # 8 bit with the VGA palette, runs crossing scan lines, literals >= 0xC0 coded as runs of one, odd width
    pal = rng.integers(0, 256, (256, 3), dtype=np.uint8)
    idx = rng.integers(0, 256, (h, w))
    idx[4:6, :] = 0xC7                                      # a run over a line break
    bpl = w + 1
    lines = planar_lines(idx, 8, 1, bpl)
    raw = b''.join(lines)
    hand('vga_rle_cross', header(5, 1, 8, 1, w, h, bpl) + rle(raw) + b'\x0c' + pal.tobytes(), pal[idx])
    hand('vga_rle_lines', header(5, 1, 8, 1, w, h, bpl) + rle(raw, False, bpl) + b'\x0c' + pal.tobytes(), pal[idx])
    hand('vga_raw', header(5, 0, 8, 1, w, h, bpl) + raw + b'\x0c' + pal.tobytes(), pal[idx])
    gpal = np.repeat(np.arange(256, dtype=np.uint8)[::-1, None], 3, 1)
    hand('vga_graypal', header(5, 1, 8, 1, w, h, bpl, palinfo=2) + rle(raw) + b'\x0c' + gpal.tobytes(), gpal[idx][:, :, :1])
    # no VGA palette: the index is the gray value
    hand('gray8_nopal', header(5, 1, 8, 1, w, h, bpl, palinfo=2) + rle(raw), idx.astype(np.uint8)[:, :, None])
    # 2 bits in 4 planes = 8 bit index with the VGA palette
    lines = planar_lines(idx, 2, 4, 6)
    hand('b2p4_vga', header(5, 1, 2, 4, w, h, 6) + rle(b''.join(lines)) + b'\x0c' + pal.tobytes(), pal[idx])

    # 24 bit (three planes) and 32 bit (four planes), RLE and raw
    rgb = rng.integers(0, 256, (h, w, 3), dtype=np.uint8)
    rgb[1, 2:15] = [9, 200, 0xC1]
    bpl = w + 1
    lines = [b''.join(pack_plane(rgb[y, :, c], 8, bpl) for c in range(3)) for y in range(h)]
    hand('rgb24_rle', header(5, 1, 8, 3, w, h, bpl) + rle(b''.join(lines)), rgb)
    hand('rgb24_raw_v3', header(3, 0, 8, 3, w, h, bpl) + b''.join(lines), rgb)
    a4 = np.concatenate([rgb, rng.integers(0, 256, (h, w, 1), dtype=np.uint8)], -1)
    lines = [b''.join(pack_plane(a4[y, :, c], 8, bpl) for c in range(4)) for y in range(h)]
    hand('rgba32_rle', header(5, 1, 8, 4, w, h, bpl) + rle(b''.join(lines)), a4)
    z = a4.copy()
    z[:, :, 3] = 0
    lines = [b''.join(pack_plane(z[y, :, c], 8, bpl) for c in range(4)) for y in range(h)]
    exp = z.copy()
    exp[:, :, 3] = 255
    hand('rgba32_alpha_zero', header(5, 1, 8, 4, w, h, bpl) + rle(b''.join(lines)), exp)

    # truncated: an RLE stream that ends inside line 4; the rest is black
    lines = [b''.join(pack_plane(rgb[y, :, c], 8, bpl) for c in range(3)) for y in range(h)]
    body = b''.join(lines)
    cut = 3 * 3 * bpl + 10                                  # 3 lines and 10 bytes of the red plane
    data = header(5, 0, 8, 3, w, h, bpl) + body[:cut]
    exp = rgb.copy()
    exp[3:] = 0
    exp[3, :10, 0] = rgb[3, :10, 0]
    save(OUT, 'truncated24.pcx', data, exp)

    # DCX: three hand built pages of different kinds, the offset table zero terminated
    p0 = header(5, 1, 8, 3, w, h, bpl) + rle(body)
    idx = rng.integers(0, 256, (h, w))
    p1 = header(5, 1, 8, 1, w, h, w + 1) + rle(b''.join(planar_lines(idx, 8, 1, w + 1))) + b'\x0c' + pal.tobytes()
    p2 = header(5, 1, 1, 1, w, h, 4) + rle(b''.join(planar_lines(m, 1, 1, 4)))
    table = 4 + 4 * 4
    offs = [table, table + len(p0), table + len(p0) + len(p1)]
    data = struct.pack('<I', 987654321) + struct.pack('<4I', *offs, 0) + p0 + p1 + p2
    refs = [rgb, pal[idx], np.where(m == 1, 255, 0).astype(np.uint8)[:, :, None]]
    for i, r in enumerate(refs):
        path = save(OUT, 'hand_pages.dcx', data, r, '' if i == 0 else f'.p{i}')
        check(f'hand_pages.dcx[{i}]', path, r, frame=i)


def main():
    clear(OUT)
    tool_files()
    hand_files()
    print(f'{len([f for f in os.listdir(OUT) if not f.endswith(".ref")])} files in {OUT}')


if __name__ == '__main__':
    main()
