"""Generates the PNG fixtures in tests/fixtures/png and their .ref sidecars.

  python3 gen_png.py            regenerate everything (deterministic)

Most files are assembled by hand (make_png below) because no common encoder can produce the whole feature
matrix: every color type / bit depth, Adam7, each row filter, tRNS color keys, split IDATs, ancillary chunks.
A few are written by Pillow and ImageMagick for variety.

The .ref is never taken from the source pixels. It is the consensus of independent reference decoders:
OpenCV (libpng), ImageMagick and netpbm's pngtopam (libpng, each with its own transforms), ffmpeg (its own PNG
decoder) and Pillow (its own PNG decoder, where it can represent the result). At least two must produce a result and all that do must agree
bit for bit, otherwise generation fails.

tests/fixtures/png/pngsuite holds the official PngSuite (http://www.schaik.com/pngsuite/, free to use, see
PngSuite.LICENSE there). Those files are committed as downloaded; this script only (re)creates their refs.
The x*.png files in it are intentionally corrupt and get no ref.
"""
import glob
import os
import struct
import subprocess
import sys
import zlib

import numpy as np

from pxref import write_ref, test_pattern

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.normpath(os.path.join(HERE, '..', 'fixtures', 'png'))
SUITE = os.path.join(OUT, 'pngsuite')

CHANNELS = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}
ADAM7 = [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)]  # x0, y0, dx, dy


# ---------------------------------------------------------------------------------------------- PNG writer

def chunk(ctype, data=b''):
    return struct.pack('>I', len(data)) + ctype + data + struct.pack('>I', zlib.crc32(ctype + data) & 0xFFFFFFFF)


def pack_row(row, depth):
    """row: (w, c) integer samples -> packed PNG scanline bytes."""
    if depth == 8:
        return row.astype(np.uint8).tobytes()
    if depth == 16:
        return row.astype('>u2').tobytes()
    v = row.reshape(-1).astype(np.uint8)
    bits = ((v[:, None] >> np.arange(depth - 1, -1, -1)) & 1).astype(np.uint8).reshape(-1)
    return np.packbits(bits).tobytes()


def paeth(a, b, c):
    p = a + b - c
    pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
    if pa <= pb and pa <= pc:
        return a
    return b if pb <= pc else c


def filter_row(ftype, cur, prev, bpp):
    out = bytearray(len(cur))
    for i in range(len(cur)):
        a = cur[i - bpp] if i >= bpp else 0
        b = prev[i]
        c = prev[i - bpp] if i >= bpp else 0
        pred = (0, a, b, (a + b) >> 1, paeth(a, b, c))[ftype]
        out[i] = (cur[i] - pred) & 0xFF
    return bytes([ftype]) + bytes(out)


def filter_image(arr, depth, filt, salt):
    """arr: (h, w, c). filt: 0..4 or 'mixed' (cycles through all five filters row by row)."""
    bpp = max(1, arr.shape[2] * depth // 8)
    out = []
    prev = None
    for y in range(arr.shape[0]):
        cur = pack_row(arr[y], depth)
        if prev is None:
            prev = bytes(len(cur))
        f = (y + salt) % 5 if filt == 'mixed' else filt
        out.append(filter_row(f, cur, prev, bpp))
        prev = cur
    return b''.join(out)


def make_png(arr, ctype, depth, interlace=False, filt='mixed', plte=None, trns=None, pre=(), post=(),
             idat_split=None, level=9):
    """arr: (h, w, c) array of sample values (palette indices for ctype 3). pre/post: raw chunks around IDAT."""
    arr = np.asarray(arr)
    h, w, c = arr.shape
    assert c == CHANNELS[ctype]
    raw = b''
    if interlace:
        for p, (x0, y0, dx, dy) in enumerate(ADAM7):
            sub = arr[y0::dy, x0::dx]
            if sub.shape[0] and sub.shape[1]:
                raw += filter_image(sub, depth, filt, p)
    else:
        raw = filter_image(arr, depth, filt, 0)
    z = zlib.compress(raw, level)
    out = b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, depth, ctype, 0, 0, 1 if interlace else 0))
    for ch in pre:
        out += ch
    if plte is not None:
        out += chunk(b'PLTE', bytes(plte))
    if trns is not None:
        out += chunk(b'tRNS', bytes(trns))
    if idat_split is None:
        out += chunk(b'IDAT', z)
    else:
        pos = 0
        k = 0
        while pos < len(z):
            n = idat_split[k % len(idat_split)]
            out += chunk(b'IDAT', z[pos:pos + n])
            pos += n
            k += 1
    for ch in post:
        out += ch
    return out + chunk(b'IEND')


# ---------------------------------------------------------------------------------------------- reference decoders

def png_layout(path):
    """(width, height, expected channels, expected dtype) of what a PNG decodes to under the PixCodec mapping."""
    b = open(path, 'rb').read()
    w, h, depth, ctype = struct.unpack('>IIBB', b[16:26])
    pos, trns = 33, False
    while pos + 12 <= len(b):
        n, = struct.unpack('>I', b[pos:pos + 4])
        t = b[pos + 4:pos + 8]
        if t == b'IDAT':
            break
        if t == b'tRNS':
            trns = True
        pos += 12 + n
    channels = {0: 1, 2: 3, 3: 3, 4: 2, 6: 4}[ctype] + (1 if trns and ctype in (0, 2, 3) else 0)
    return w, h, channels, np.uint16 if depth == 16 else np.uint8, ctype, depth, trns


def from_rgba(a, channels):
    """(h, w, 4) RGBA -> the expected channel layout; None if the data does not fit it."""
    if channels in (1, 2) and not (np.array_equal(a[..., 0], a[..., 1]) and np.array_equal(a[..., 0], a[..., 2])):
        return None
    if channels in (1, 3) and not np.all(a[..., 3] == np.iinfo(a.dtype).max):
        return None
    return {1: a[..., :1], 2: a[..., [0, 3]], 3: a[..., :3], 4: a}[channels]


def dec_cv2(path, lay):
    import cv2
    w, h, channels, dtype, ctype, depth, trns = lay
    if ctype == 0 and trns:
        return None  # OpenCV drops the gray color key
    a = cv2.imread(path, cv2.IMREAD_UNCHANGED)
    if a is None or a.dtype != dtype:
        return None
    if a.ndim == 2:
        a = a[..., None]
    if a.shape[2] == 3:
        a = a[..., ::-1]
    elif a.shape[2] == 4:
        a = a[..., [2, 1, 0, 3]]
    if a.shape[2] == channels:
        return a
    if a.shape[2] == 4:
        return from_rgba(a, channels)
    return None


def dec_magick(path, lay):
    w, h, channels, dtype = lay[:4]
    r = subprocess.run(['magick', path, '-depth', '16', 'RGBA:-'], capture_output=True)
    if r.returncode != 0 or len(r.stdout) != w * h * 8:
        return None
    a = np.frombuffer(r.stdout, dtype='<u2').reshape(h, w, 4)
    if dtype == np.uint8:
        if np.any(a % 257):
            return None
        a = (a // 257).astype(np.uint8)
    return from_rgba(a, channels)


def dec_netpbm(path, lay):
    w, h, channels, dtype = lay[:4]
    if lay[4] == 2 and lay[6]:
        return None  # pngtopam does not apply RGB color keys
    r = subprocess.run(['pngtopam', '-alphapam', path], capture_output=True)
    if r.returncode != 0 or not r.stdout.startswith(b'P7'):
        return None
    head, _, body = r.stdout.partition(b'ENDHDR\n')
    f = dict(line.split(None, 1) for line in head.decode().splitlines()[1:] if ' ' in line)
    pw, ph, pd, maxval = int(f['WIDTH']), int(f['HEIGHT']), int(f['DEPTH']), int(f['MAXVAL'])
    a = np.frombuffer(body, dtype='>u2' if maxval > 255 else np.uint8).reshape(ph, pw, pd).astype(np.uint32)
    full = np.iinfo(dtype).max
    if full % maxval:
        return None
    a = (a * (full // maxval)).astype(dtype)   # 1/2/4 bit samples arrive unscaled
    if pd == 2:
        a = a[..., [0, 0, 0, 1]]
    return from_rgba(a, channels) if a.shape[2] == 4 else None


def dec_ffmpeg(path, lay):
    w, h, channels, dtype = lay[:4]
    if lay[4] == 0 and lay[6] and lay[5] < 8:
        return None  # ffmpeg ignores / mis-scales the color key of 1, 2 and 4 bit gray
    fmt = {(1, 1): 'gray', (2, 1): 'ya8', (3, 1): 'rgb24', (4, 1): 'rgba',
           (1, 2): 'gray16le', (2, 2): 'ya16le', (3, 2): 'rgb48le', (4, 2): 'rgba64le'}[(channels, np.dtype(dtype).itemsize)]
    r = subprocess.run(['ffmpeg', '-v', 'error', '-i', path, '-frames:v', '1', '-f', 'rawvideo', '-pix_fmt', fmt, '-'],
                       capture_output=True)
    if r.returncode != 0 or len(r.stdout) != w * h * channels * np.dtype(dtype).itemsize:
        return None
    return np.frombuffer(r.stdout, dtype=np.dtype(dtype).newbyteorder('<')).reshape(h, w, channels)


def dec_pillow(path, lay):
    from PIL import Image
    w, h, channels, dtype, ctype, depth, trns = lay
    im = Image.open(path)
    if ctype == 3:
        a = np.asarray(im.convert('RGBA' if trns else 'RGB'))
    elif trns or (depth == 16 and ctype != 0):
        return None  # Pillow keeps color keys as metadata and reduces 16 bit color to 8 bit
    elif im.mode == '1':
        a = np.asarray(im.convert('L'))
    elif im.mode in ('I;16', 'I;16B', 'I'):
        a = np.asarray(im).astype(np.uint16)
    else:
        a = np.asarray(im)
    if a.ndim == 2:
        a = a[..., None]
    return a if a.shape[2] == channels and a.dtype == dtype else None


DECODERS = [('cv2', dec_cv2), ('magick', dec_magick), ('netpbm', dec_netpbm), ('ffmpeg', dec_ffmpeg), ('pillow', dec_pillow)]

# Reference decoders known to get a file wrong: (file name, decoder) -> reason. Their result is ignored.
KNOWN_BAD = {}


def reference(path, expect=None):
    """Consensus decode. expect: optional source pixels the consensus must equal as well."""
    lay = png_layout(path)
    name = os.path.basename(path)
    results = []
    for dname, dec in DECODERS:
        if (name, dname) in KNOWN_BAD:
            continue
        try:
            a = dec(path, lay)
        except Exception as e:  # a decoder failing outright simply does not vote
            a = None
        if a is not None:
            results.append((dname, np.ascontiguousarray(a)))
    if len(results) < 2:
        raise SystemExit(f'{name}: only {[n for n, _ in results]} could decode it')
    first = results[0][1]
    for dname, a in results[1:]:
        if a.shape != first.shape or a.dtype != first.dtype or not np.array_equal(a, first):
            diff = 'shape' if a.shape != first.shape else int(np.abs(a.astype(np.int64) - first.astype(np.int64)).max())
            raise SystemExit(f'{name}: {results[0][0]} and {dname} disagree ({diff})')
    if expect is not None:
        e = np.asarray(expect)
        if e.shape != first.shape or not np.array_equal(e, first):
            raise SystemExit(f'{name}: reference decoders agree with each other but not with the source pixels')
    return first, [n for n, _ in results]


# ---------------------------------------------------------------------------------------------- fixtures

def samples(w, h, c, depth, seed):
    if depth == 16:
        return test_pattern(w, h, c, np.uint16, seed)
    return test_pattern(w, h, c, np.uint8, seed) >> (8 - depth)


def palette(n, seed):
    return np.random.default_rng(seed).integers(0, 256, (n, 3), dtype=np.uint8)


def expand(arr, ctype, depth, plte=None, trns=None):
    """What the file must decode to, computed from the source samples (used as an extra check only)."""
    arr = np.asarray(arr)
    if ctype == 3:
        rgb = np.asarray(plte).reshape(-1, 3)[arr[..., 0]]
        if trns is None:
            return rgb
        alpha = np.full(len(rgb.reshape(-1, 3)) and 256, 255, np.uint8)
        alpha[:len(trns)] = np.frombuffer(bytes(trns), np.uint8)
        return np.concatenate([rgb, alpha[arr[..., 0]][..., None]], axis=-1)
    dtype = np.uint16 if depth == 16 else np.uint8
    out = arr.astype(dtype)
    if depth < 8:
        out = (arr * (255 // ((1 << depth) - 1))).astype(np.uint8)
    if trns is not None:
        key = np.frombuffer(bytes(trns), '>u2').astype(np.int64)
        hit = np.all(arr.astype(np.int64) == key[None, None, :], axis=-1)
        alpha = np.where(hit, 0, np.iinfo(dtype).max).astype(dtype)
        out = np.concatenate([out, alpha[..., None]], axis=-1)
    return out


written = []


def emit(name, data, expect=None):
    path = os.path.join(OUT, name + '.png')
    with open(path, 'wb') as f:
        f.write(data)
    ref, who = reference(path, expect)
    write_ref(path + '.ref', ref)
    written.append((name, who))


def gen_matrix():
    combos = [(0, d) for d in (1, 2, 4, 8, 16)] + [(2, 8), (2, 16)] + [(3, d) for d in (1, 2, 4, 8)] \
        + [(4, 8), (4, 16), (6, 8), (6, 16)]
    sizes = [(1, 1), (5, 3), (13, 9), (33, 17)]
    seed = 0
    for ctype, depth in combos:
        for interlace in (False, True):
            for w, h in sizes:
                seed += 1
                arr = samples(w, h, CHANNELS[ctype], depth, seed)
                plte = palette(1 << depth, seed) if ctype == 3 else None
                name = f'm_c{ctype}_d{depth:02d}_{"i" if interlace else "n"}_{w}x{h}'
                emit(name, make_png(arr, ctype, depth, interlace, plte=plte), expand(arr, ctype, depth, plte))


def gen_filters():
    # every filter type at every filter distance (bytes per pixel 1, 2, 3, 4, 6, 8); wide enough for the
    # vectorized loops and with a tail left over
    for ctype, depth in [(0, 8), (4, 8), (2, 8), (6, 8), (2, 16), (6, 16), (0, 16), (0, 1), (3, 4)]:
        for f in range(5):
            arr = samples(37, 11, CHANNELS[ctype], depth, 100 + f)
            plte = palette(1 << depth, 7) if ctype == 3 else None
            emit(f'f{f}_c{ctype}_d{depth:02d}', make_png(arr, ctype, depth, filt=f, plte=plte), expand(arr, ctype, depth, plte))
    # noise is the worst case for the Paeth/Average predictors (all branches taken)
    rng = np.random.default_rng(5)
    for ctype, depth in [(2, 8), (6, 8), (0, 8), (6, 16)]:
        hi = 65536 if depth == 16 else 256
        arr = rng.integers(0, hi, (9, 41, CHANNELS[ctype]))
        for f in (3, 4):
            emit(f'noise_f{f}_c{ctype}_d{depth:02d}', make_png(arr, ctype, depth, filt=f), expand(arr, ctype, depth))


def gen_transparency():
    for interlace in (False, True):
        il = 'i' if interlace else 'n'
        for depth in (1, 2, 4, 8, 16):
            arr = samples(19, 11, 1, depth, 200 + depth)
            key = int(arr[5, 7, 0])
            if depth == 16:
                arr[0, 0, 0] = key ^ 0x0001  # differs from the key in the low byte only
                arr[0, 1, 0] = key ^ 0x0100  # ... and in the high byte only
            trns = struct.pack('>H', key)
            emit(f't_gray_d{depth:02d}_{il}', make_png(arr, 0, depth, interlace, trns=trns), expand(arr, 0, depth, None, trns))
        for depth in (8, 16):
            arr = samples(19, 11, 3, depth, 210 + depth)
            arr[2:5, 3:9] = arr[6, 6]
            key = [int(v) for v in arr[6, 6]]
            arr[0, 0] = [key[0], key[1], key[2] ^ 1]             # two channels match
            arr[0, 1] = [key[0] ^ (256 if depth == 16 else 128), key[1], key[2]]
            trns = struct.pack('>3H', *key)
            emit(f't_rgb_d{depth:02d}_{il}', make_png(arr, 2, depth, interlace, trns=trns), expand(arr, 2, depth, None, trns))
        for depth in (1, 2, 4, 8):
            arr = samples(19, 11, 1, depth, 220 + depth)
            n = 1 << depth
            plte = palette(n, depth)
            # fewer alpha entries than palette entries: the rest is opaque
            trns = bytes(np.random.default_rng(depth).integers(0, 256, max(1, n // 2), dtype=np.uint8))
            emit(f't_pal_d{depth}_{il}', make_png(arr, 3, depth, interlace, plte=plte, trns=trns), expand(arr, 3, depth, plte, trns))


def gen_chunks():
    arr = samples(29, 23, 3, 8, 300)
    # compressed stream cut into tiny IDATs, including 1 byte ones
    emit('idat_split_small', make_png(arr, 2, 8, idat_split=[1, 2, 3, 1, 7, 50]), arr)
    emit('idat_split_il', make_png(arr, 2, 8, True, idat_split=[5]), arr)
    emit('stored_level0', make_png(arr, 2, 8, level=0), arr)

    exif = (b'II*\x00\x08\x00\x00\x00' + struct.pack('<H', 2)
            + struct.pack('<HHIHH', 0x0112, 3, 1, 6, 0)          # Orientation = 6
            + struct.pack('<HHII', 0x0128, 3, 1, 2)              # ResolutionUnit = inch
            + struct.pack('<I', 0))
    icc = bytes(np.random.default_rng(9).integers(0, 256, 300, dtype=np.uint8))  # opaque blob, not a real profile
    pre = [
        chunk(b'gAMA', struct.pack('>I', 45455)),
        chunk(b'iCCP', b'test profile\x00\x00' + zlib.compress(icc)),
        chunk(b'pHYs', struct.pack('>IIB', 11811, 5906, 1)),     # 300 x 150 dpi
        chunk(b'tEXt', b'Comment\x00hello'),
        chunk(b'zzZz', b'unknown ancillary chunk'),
        chunk(b'eXIf', exif),
    ]
    post = [chunk(b'tIME', struct.pack('>HBBBBB', 2020, 1, 2, 3, 4, 5)), chunk(b'tEXt', b'After\x00idat')]
    emit('meta_all', make_png(arr, 2, 8, pre=pre, post=post), arr)
    with open(os.path.join(OUT, 'meta_all.icc'), 'wb') as f:
        f.write(icc)
    with open(os.path.join(OUT, 'meta_all.exif'), 'wb') as f:
        f.write(exif)
    emit('meta_phys_aspect', make_png(arr, 2, 8, pre=[chunk(b'pHYs', struct.pack('>IIB', 3, 2, 0))]), arr)

    garr = samples(16, 16, 1, 8, 301)
    # a palette in a gray+alpha / RGB file is only a suggestion and must not change the decode
    emit('plte_in_rgb', make_png(arr, 2, 8, plte=palette(16, 3)), arr)
    # palette with fewer entries than 2^depth (all indices stay in range)
    idx = samples(21, 9, 1, 8, 302) % 5
    emit('pal_short', make_png(idx, 3, 8, plte=palette(5, 4)), expand(idx, 3, 8, palette(5, 4)))
    idx4 = samples(21, 9, 1, 4, 303) % 3
    emit('pal_short_d4', make_png(idx4, 3, 4, plte=palette(3, 5)), expand(idx4, 3, 4, palette(3, 5)))
    emit('gray_wide', make_png(samples(131, 3, 1, 8, 304), 0, 8), samples(131, 3, 1, 8, 304))
    emit('gray_tall', make_png(samples(1, 67, 1, 8, 305), 0, 8, True), samples(1, 67, 1, 8, 305))
    emit('bit_wide', make_png(samples(257, 2, 1, 1, 306), 0, 1), expand(samples(257, 2, 1, 1, 306), 0, 1))
    del garr


def gen_third_party():
    """Files written by other encoders (their own filter heuristics, chunk layout, interlacing)."""
    from PIL import Image
    rgb = test_pattern(160, 120, 3, np.uint8, 400)
    rgba = test_pattern(64, 48, 4, np.uint8, 401)
    gray16 = test_pattern(50, 40, 1, np.uint16, 402)[..., 0]

    def save(name, im, **kw):
        path = os.path.join(OUT, name + '.png')
        im.save(path, **kw)
        ref, who = reference(path)
        write_ref(path + '.ref', ref)
        written.append((name, who))

    save('pil_rgb', Image.fromarray(rgb), optimize=True)
    save('pil_rgba', Image.fromarray(rgba), compress_level=1)
    save('pil_gray16', Image.fromarray(gray16))
    save('pil_quant', Image.fromarray(rgb).quantize(40), optimize=True)
    save('pil_la', Image.fromarray(rgba[..., :2], 'LA'))
    save('pil_dpi_icc', Image.fromarray(rgb[:20, :30]), dpi=(72, 72))

    def magick(name, src, *args):
        path = os.path.join(OUT, name + '.png')
        tmp = os.path.join(OUT, '_src.png')
        Image.fromarray(src).save(tmp)
        subprocess.run(['magick', tmp, '-strip', *args, path], check=True)
        os.remove(tmp)
        ref, who = reference(path)
        write_ref(path + '.ref', ref)
        written.append((name, who))

    magick('im_rgb_interlaced', rgb[:60, :80], '-interlace', 'PNG')
    magick('im_rgb48', rgb[:30, :40], '-depth', '16', '-define', 'png:color-type=2')
    magick('im_rgba64_interlaced', rgba, '-depth', '16', '-interlace', 'PNG', '-define', 'png:color-type=6')
    magick('im_gray_2bit', rgb[:30, :40], '-colorspace', 'Gray', '-depth', '2', '-define', 'png:color-type=0',
           '-define', 'png:bit-depth=2')


def gen_suite_refs():
    for path in sorted(glob.glob(os.path.join(SUITE, '*.png'))):
        name = os.path.basename(path)
        if name.startswith('x'):
            continue
        ref, who = reference(path)
        write_ref(path + '.ref', ref)
        written.append(('pngsuite/' + name[:-4], who))


def main():
    os.makedirs(OUT, exist_ok=True)
    for f in glob.glob(os.path.join(OUT, '*.png')) + glob.glob(os.path.join(OUT, '*.ref')) + glob.glob(os.path.join(SUITE, '*.ref')):
        os.remove(f)
    gen_matrix()
    gen_filters()
    gen_transparency()
    gen_chunks()
    gen_third_party()
    if os.path.isdir(SUITE):
        gen_suite_refs()
    votes = {}
    for _, who in written:
        votes[len(who)] = votes.get(len(who), 0) + 1
    print(f'{len(written)} fixtures; reference decoders agreeing per file: {dict(sorted(votes.items()))}')
    if '-v' in sys.argv:
        for name, who in written:
            print(f'  {name}: {",".join(who)}')


if __name__ == '__main__':
    main()
