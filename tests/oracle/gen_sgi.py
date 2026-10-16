"""Generates tests/fixtures/sgi: name.sgi (.rgb, .bw, ...) and name.<ext>.ref (what Sgi.Decode returns).

Files written by Pillow and ImageMagick with a tool's decode as the reference, and hand built files from "The SGI
Image File Format" (Haeberli) with references computed from what went into them: 1 to 5 channels, 8 and 16 bit,
verbatim and RLE (shared and out of order row offsets, literal and run packets of every length), 1, 2 and 3
dimensional headers, the DITHERED colour map type, truncation. All are cross checked against Pillow and ImageMagick
(disagreements are printed, --report prints all comparisons).

Decoding rules the references follow: the stored values (PIXMIN / PIXMAX are not applied: 16 bit stays 16 bit, which
Pillow reduces to 8 bit); rows are stored bottom-up; 2 channels are gray + alpha, more than 4 are returned as they
are; DITHERED bytes hold red in bits 0-2, green in 3-5, blue in 6-7 (neither tool reads them); a truncated file is
zero where the data ends.

usage: python3 gen_sgi.py [--report]
"""
import io
import os
import struct
import numpy as np
from PIL import Image
from pxref import test_pattern
from legacy_common import clear, save, check, tool_ref, magick_write

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'fixtures', 'sgi')


def header(storage, bpc, dim, x, y, z, pmin=0, pmax=None, cmap=0, name=b''):
    if pmax is None:
        pmax = 255 if bpc == 1 else 65535
    h = struct.pack('>hBBHHHHii4s80si', 474, storage, bpc, dim, x, y, z, pmin, pmax, b'', name, cmap)
    return h + bytes(512 - len(h))


def rows_of(img):
    """(channel, file row) -> values, file row 0 being the bottom row."""
    h, w, c = img.shape
    return {(z, y): img[h - 1 - y, :, z] for z in range(c) for y in range(h)}


def verbatim(img):
    h, w, c = img.shape
    dt = '>u1' if img.dtype == np.uint8 else '>u2'
    return b''.join(img[h - 1 - y, :, z].astype(dt).tobytes() for z in range(c) for y in range(h))


def rle_row(vals, bpc, max_run=127):
    out = []
    i, n = 0, len(vals)
    while i < n:
        run = 1
        while i + run < n and run < max_run and vals[i + run] == vals[i]:
            run += 1
        if run >= 2:
            out += [run, int(vals[i])]
            i += run
            continue
        lit = 1
        while i + lit < n and lit < max_run and not (i + lit + 1 < n and vals[i + lit] == vals[i + lit + 1]):
            lit += 1
        out += [0x80 | lit] + [int(v) for v in vals[i:i + lit]]
        i += lit
    out.append(0)
    return np.array(out, '>u1' if bpc == 1 else '>u2').tobytes()


def rle_file(img, shuffle=False, share=False, rng=None):
    """Header + offset / length tables + rows. shuffle stores the rows in random order, share lets equal rows share data."""
    h, w, c = img.shape
    bpc = 1 if img.dtype == np.uint8 else 2
    rows = rows_of(img)
    keys = [(z, y) for z in range(c) for y in range(h)]
    order = list(keys)
    if shuffle:
        rng.shuffle(order)
    body = bytearray()
    starts, lengths, seen = {}, {}, {}
    base = 512 + 8 * len(keys)
    for k in order:
        data = rle_row(rows[k], bpc)
        if share and data in seen:
            starts[k] = seen[data]
        else:
            starts[k] = base + len(body)
            seen[data] = starts[k]
            body += data
        lengths[k] = len(data)
    st = b''.join(struct.pack('>I', starts[(z, y)]) for z in range(c) for y in range(h))
    ln = b''.join(struct.pack('>I', lengths[(z, y)]) for z in range(c) for y in range(h))
    dim = 2 if c == 1 else 3
    return header(1, bpc, dim, w, h, c) + st + ln + bytes(body)


def hand(name, data, ref):
    check(name, save(OUT, name, data, ref), ref)


def tool_files():
    rgb = test_pattern(37, 23, 3)
    gray = test_pattern(37, 23, 1)[:, :, 0]
    rgba = test_pattern(37, 23, 4, seed=2)
    rgb16 = test_pattern(37, 23, 3, np.uint16, seed=4)

    def from_pillow(name, im, channels):
        bio = io.BytesIO()
        im.save(bio, 'SGI')
        path = save(OUT, name, bio.getvalue(), np.zeros((1, 1, 1), np.uint8))
        ref = tool_ref(path, channels)
        save(OUT, name, bio.getvalue(), ref)
        check(name, path, ref)

    from_pillow('pil_rgb.rgb', Image.fromarray(rgb), 3)
    from_pillow('pil_gray.bw', Image.fromarray(gray), 1)
    from_pillow('pil_rgba.rgba', Image.fromarray(rgba, 'RGBA'), 4)

    def from_magick(name, src_path, args, ref):
        path = os.path.join(OUT, name)
        magick_write([src_path], args, path)
        with open(path, 'rb') as f:
            save(OUT, name, f.read(), ref)
        check(name, path, ref)

    tmp = os.path.join(OUT, '_src.png')
    Image.fromarray(rgb).save(tmp)
    from_magick('im_rgb_rle.sgi', tmp, ['-compress', 'RLE'], rgb)
    from_magick('im_rgb_none.sgi', tmp, ['-compress', 'None'], rgb)
    Image.fromarray(rgba, 'RGBA').save(tmp)
    from_magick('im_rgba_rle.sgi', tmp, ['-compress', 'RLE'], rgba)
    Image.fromarray(gray).save(tmp)
    from_magick('im_gray.sgi', tmp, [], gray[:, :, None])
    # 16 bit through a 16 bit PNG
    import subprocess
    with open(os.path.join(OUT, '_src.rgb16'), 'wb') as f:
        f.write(rgb16.astype('>u2').tobytes())
    subprocess.run(['magick', '-size', '37x23', '-depth', '16', '-endian', 'MSB', 'rgb:' + os.path.join(OUT, '_src.rgb16'), tmp], check=True)
    os.remove(os.path.join(OUT, '_src.rgb16'))
    from_magick('im_rgb16_rle.sgi', tmp, ['-compress', 'RLE'], rgb16)
    from_magick('im_rgb16_none.sgi', tmp, ['-compress', 'None'], rgb16)
    os.remove(tmp)


def hand_files():
    rng = np.random.default_rng(3)
    w, h = 19, 7

    def img(c, dt=np.uint8):
        a = rng.integers(0, 256 if dt == np.uint8 else 65536, (h, w, c)).astype(dt)
        a[1, 2:12] = a[1, 2]                     # runs
        a[4, :] = a[4, 0]
        return a

    for c in (1, 2, 3, 4):
        for dt, bits in ((np.uint8, 8), (np.uint16, 16)):
            a = img(c, dt)
            bpc = bits // 8
            dim = 2 if c == 1 else 3
            hand(f'c{c}_{bits}_verbatim.sgi', header(0, bpc, dim, w, h, c) + verbatim(a), a)
            hand(f'c{c}_{bits}_rle.sgi', rle_file(a), a)
    # five channels
    a = img(5)
    hand('c5_rle.sgi', rle_file(a), a)
    # rows in random order, equal rows sharing their data
    a = img(3)
    a[2] = a[3]
    hand('rle_shuffled_shared.rgb', rle_file(a, shuffle=True, share=True, rng=rng), a)
    # long rows: runs and literals split at 127
    lw = 400
    a = rng.integers(0, 256, (3, lw, 1)).astype(np.uint8)
    a[0, 50:350] = 9
    with_hdr = rle_file(a)
    hand('rle_long_rows.bw', with_hdr, a)
    # dimension 1: one row; the y / z sizes in the header are ignored
    a = img(1)[:1]
    hand('dim1.bw', header(0, 1, 1, w, 5, 3) + verbatim(a), a)
    # a 3 dimensional header with one channel, a name and PIXMAX below 255
    a = (img(1) // 2).astype(np.uint8)
    hand('dim3_z1_named.sgi', header(0, 1, 3, w, h, 1, 0, 127, name=b'pixcodec') + verbatim(a), a)
    # DITHERED: 3-3-2 bit RGB
    v = rng.integers(0, 256, (h, w, 1)).astype(np.uint8)
    b = v[:, :, 0].astype(int)
    exp = np.stack([(b & 7) * 255 // 7, (b >> 3 & 7) * 255 // 7, (b >> 6) * 255 // 3], -1).astype(np.uint8)
    hand('dithered.sgi', header(0, 1, 2, w, h, 1, cmap=1) + verbatim(v), exp)
    # truncated: verbatim RGB cut in the green channel
    a = img(3)
    full = header(0, 1, 3, w, h, 3) + verbatim(a)
    cut = 512 + w * h + 3 * w + 5
    exp = a.copy()
    exp[:, :, 2] = 0
    exp[:h - 3, :, 1] = 0
    exp[h - 4, 5:, 1] = 0
    exp[h - 4, :5, 1] = a[h - 4, :5, 1]
    save(OUT, 'truncated_verbatim.sgi', full[:cut], exp)


def main():
    clear(OUT)
    tool_files()
    hand_files()
    print(f'{len([f for f in os.listdir(OUT) if not f.endswith(".ref")])} files in {OUT}')


if __name__ == '__main__':
    main()
