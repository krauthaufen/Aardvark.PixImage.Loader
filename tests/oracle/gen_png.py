"""Generates the PNG fixtures in tests/fixtures/png and their .ref sidecars.

  python3 gen_png.py [-v]            regenerate everything (deterministic); -v lists who confirmed each file
  python3 gen_png.py --meta x.png    print what third-party readers report about a PNG's metadata as JSON
  python3 gen_png.py --fetch-tools   download / build pngcheck, exiftool and libjxl's cjxl + djxl into .tmp/tools

Most files are assembled by hand (make_png below) because no common encoder can produce the whole feature
matrix: every color type / bit depth, Adam7, each row filter, tRNS color keys, split IDATs, ancillary chunks.
A few are written by Pillow and ImageMagick for variety.

The .ref is never taken from the source pixels. It is the consensus of independent reference decoders:
OpenCV (libpng), ImageMagick and netpbm's pngtopam (libpng, each with its own transforms), ffmpeg (its own PNG
decoder) and Pillow (its own PNG decoder, where it can represent the result). At least two must produce a result and all that do must agree
bit for bit, otherwise generation fails.

The official PngSuite (http://www.schaik.com/pngsuite/) is in the test-data repository (fixtures/png/pngsuite of
Aardvark.PixImage.PixCodec.TestData, see pxref.testdata_dir); this script only (re)creates the refs there when it finds it.
The x*.png files in it are intentionally corrupt and get no ref.
"""
import glob
import os
import struct
import subprocess
import sys
import zlib

import numpy as np

from pxref import write_ref, test_pattern, testdata_dir

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.normpath(os.path.join(HERE, '..', 'fixtures', 'png'))
SUITE = None   # the PngSuite in the test-data repository, set by main()

TMP = os.path.normpath(os.path.join(HERE, '..', '..', '.tmp'))
TOOLS = os.path.join(TMP, 'tools')

CHANNELS = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}
ADAM7 = [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)]  # x0, y0, dx, dy


# ---------------------------------------------------------------------------------------------- extra tools

def tool(name):
    """Command line (list) of an oracle tool that distributions rarely package, or None: looked up in $EXIFTOOL
    (exiftool only), on PATH and in .tmp/tools, where `gen_png.py --fetch-tools` puts them.
      pngcheck  prints the contents of nearly every chunk (the only reader at hand for sBIT values, hIST, sPLT,
                oFFs, pCAL, sCAL, mDCV, cLLI)
      exiftool  second reader for sBIT, cICP, sCAL, sTER
      cjxl/djxl libjxl: converts an APNG to JPEG XL keeping its blend/dispose operations and renders it back with
                floating point compositing - the only decoder at hand that blends 16 bit APNG frames."""
    import shutil
    local = {'pngcheck': [os.path.join(TOOLS, 'pngcheck')],
             'exiftool': ['perl', os.path.join(TOOLS, 'exiftool-src', 'exiftool')],
             'cjxl': [os.path.join(TOOLS, 'jxl', 'tools', 'cjxl')],
             'djxl': [os.path.join(TOOLS, 'jxl', 'tools', 'djxl')]}[name]
    env = os.environ.get('EXIFTOOL') if name == 'exiftool' else None
    if env and os.path.exists(env):
        return [env] if env.lower().endswith('.exe') or os.name != 'nt' else ['perl', env]
    found = shutil.which(name)
    if found:
        return [found]
    return local if os.path.exists(local[-1]) else None


def fetch_tools():
    """Downloads and builds the tools described in tool() into .tmp/tools (needs gcc, zlib headers, perl, lzip)."""
    import urllib.request
    os.makedirs(TOOLS, exist_ok=True)

    def unpack(url, target, flag):
        os.makedirs(target, exist_ok=True)
        archive = os.path.join(TOOLS, '_download')
        urllib.request.urlretrieve(url, archive)
        subprocess.run(['tar', flag, '-xf', archive, '-C', target] + (['--strip-components=1'] if flag == '-z' else []), check=True)
        os.remove(archive)

    if not tool('pngcheck'):
        src = os.path.join(TOOLS, 'pngcheck-src')
        unpack('https://github.com/pnggroup/pngcheck/archive/refs/tags/v4.0.1.tar.gz', src, '-z')
        subprocess.run(['gcc', '-O2', '-DUSE_ZLIB', '-o', os.path.join(TOOLS, 'pngcheck'), os.path.join(src, 'pngcheck.c'), '-lz'], check=True)
    if not tool('exiftool'):
        unpack('https://github.com/exiftool/exiftool/archive/refs/tags/13.59.tar.gz', os.path.join(TOOLS, 'exiftool-src'), '-z')
    if not tool('cjxl'):
        unpack('https://github.com/libjxl/libjxl/releases/download/v0.12.0/jxl-linux-x86_64-static.tar.lz', os.path.join(TOOLS, 'jxl'), '--lzip')
    for name in ('pngcheck', 'exiftool', 'cjxl', 'djxl'):
        print(name, '->', tool(name))


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


def deflate_image(arr, depth, interlace=False, filt='mixed', level=9):
    """Filtered (and optionally Adam7 interlaced) scanlines of arr (h, w, c) as a zlib stream."""
    raw = b''
    if interlace:
        for p, (x0, y0, dx, dy) in enumerate(ADAM7):
            sub = arr[y0::dy, x0::dx]
            if sub.shape[0] and sub.shape[1]:
                raw += filter_image(sub, depth, filt, p)
    else:
        raw = filter_image(arr, depth, filt, 0)
    return zlib.compress(raw, level)


def split_chunks(z, sizes):
    if sizes is None:
        return [z]
    out, pos, k = [], 0, 0
    while pos < len(z):
        n = sizes[k % len(sizes)]
        out.append(z[pos:pos + n])
        pos += n
        k += 1
    return out


def make_png(arr, ctype, depth, interlace=False, filt='mixed', plte=None, trns=None, pre=(), post=(),
             idat_split=None, level=9):
    """arr: (h, w, c) array of sample values (palette indices for ctype 3). pre/post: raw chunks around IDAT."""
    arr = np.asarray(arr)
    h, w, c = arr.shape
    assert c == CHANNELS[ctype]
    out = b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, depth, ctype, 0, 0, 1 if interlace else 0))
    for ch in pre:
        out += ch
    if plte is not None:
        out += chunk(b'PLTE', bytes(plte))
    if trns is not None:
        out += chunk(b'tRNS', bytes(trns))
    for part in split_chunks(deflate_image(arr, depth, interlace, filt, level), idat_split):
        out += chunk(b'IDAT', part)
    for ch in post:
        out += ch
    return out + chunk(b'IEND')


def make_apng(w, h, ctype, depth, frames, default=None, plays=0, interlace=False, plte=None, trns=None, split=None,
              pre=()):
    """frames: dicts with arr (fh, fw, c) and optional x, y, delay (num, den), dispose (0..2), blend (0..1).
    default: pixels of a default image that is not part of the animation (else frame 0 is the default image)."""
    out = b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, depth, ctype, 0, 0, 1 if interlace else 0))
    for ch in pre:
        out += ch
    out += chunk(b'acTL', struct.pack('>II', len(frames), plays))
    if plte is not None:
        out += chunk(b'PLTE', bytes(plte))
    if trns is not None:
        out += chunk(b'tRNS', bytes(trns))
    seq = 0
    if default is not None:
        out += chunk(b'IDAT', deflate_image(np.asarray(default), depth, interlace))
    for k, f in enumerate(frames):
        arr = np.asarray(f['arr'])
        num, den = f.get('delay', (1, 10))
        out += chunk(b'fcTL', struct.pack('>IIIIIHHBB', seq, arr.shape[1], arr.shape[0], f.get('x', 0), f.get('y', 0),
                                          num, den, f.get('dispose', 0), f.get('blend', 0)))
        seq += 1
        parts = split_chunks(deflate_image(arr, depth, interlace), split)
        if k == 0 and default is None:
            for part in parts:
                out += chunk(b'IDAT', part)
        else:
            for part in parts:
                out += chunk(b'fdAT', struct.pack('>I', seq) + part)
                seq += 1
    return out + chunk(b'IEND')


# ---------------------------------------------------------------------------------------------- reference decoders

def png_layout(path):
    """(width, height, expected channels, expected dtype) of what a PNG decodes to under the Aardvark.PixImage.PixCodec mapping."""
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
    animated = b'acTL' in open(path, 'rb').read(200)
    for dname, dec in DECODERS:
        if (name, dname) in KNOWN_BAD:
            continue
        if animated and dname in ('ffmpeg', 'pillow'):
            continue  # they decode the first animation frame, which need not be the default image
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


# ---------------------------------------------------------------------------------------------- APNG

APNG = os.path.join(OUT, 'apng')

CHROME_JS = r"""
const fs = require('fs');
const { browser } = require(process.env.HOME + '/.headed-chrome');
(async () => {
  const b = await browser();
  const page = await b.newPage();
  await page.setRequestInterception(true);
  page.on('request', r => r.respond({ status: 200, contentType: 'text/html', body: '<html></html>' }));
  await page.goto('http://localhost:3000/');   // a secure context is needed for ImageDecoder
  for (const f of process.argv.slice(2)) {
    const b64 = fs.readFileSync(f).toString('base64');
    const res = await page.evaluate(async (b64) => {
      const bin = Uint8Array.from(atob(b64), c => c.charCodeAt(0));
      const dec = new ImageDecoder({ data: bin, type: 'image/apng', premultiplyAlpha: 'none', colorSpaceConversion: 'none' });
      await dec.tracks.ready; await dec.completed;
      const t = dec.tracks.selectedTrack;
      const out = { frames: [], durations: [], count: t.frameCount, repetitions: t.repetitionCount };
      for (let i = 0; i < t.frameCount; i++) {
        const vf = (await dec.decode({ frameIndex: i })).image;
        out.w = vf.displayWidth; out.h = vf.displayHeight;
        const buf = new Uint8Array(vf.allocationSize({ format: 'RGBA' }));
        await vf.copyTo(buf, { format: 'RGBA' });
        out.durations.push(vf.duration);
        let s = ''; for (let k = 0; k < buf.length; k += 0x8000) s += String.fromCharCode.apply(null, buf.subarray(k, k + 0x8000));
        out.frames.push(btoa(s));
        vf.close();
      }
      return out;
    }, b64).catch(e => ({ error: String(e) }));
    console.log(JSON.stringify(res));
  }
  await page.close();
  b.disconnect();
})().catch(e => { console.error(e); process.exit(1); });
"""


def chrome_decode(paths):
    """Composited frames of each APNG as Chrome's ImageDecoder produces them (8 bit RGBA), or None per file.
    Uses the persistent headed Chromium of this machine; returns all None when that is not available."""
    import base64
    import json
    try:
        os.makedirs(TMP, exist_ok=True)
        js = os.path.join(TMP, 'png_chrome_oracle.js')
        with open(js, 'w') as f:
            f.write(CHROME_JS)
        out = []
        for i in range(0, len(paths), 40):
            r = subprocess.run(['node', js, *paths[i:i + 40]], capture_output=True, text=True, timeout=300)
            lines = r.stdout.strip().splitlines()
            if r.returncode != 0 or len(lines) != len(paths[i:i + 40]):
                return [None] * len(paths)
            out += [json.loads(line) for line in lines]
        os.remove(js)
    except Exception:
        return [None] * len(paths)
    res = []
    for j in out:
        if 'error' in j:
            res.append(None)
            continue
        fr = [np.frombuffer(base64.b64decode(f), np.uint8).reshape(j['h'], j['w'], 4) for f in j['frames']]
        res.append({'frames': np.stack(fr), 'repetitions': j['repetitions'], 'durations': j['durations']})
    return res


def to_rgba(arr, ctype, depth, plte=None, trns=None):
    e = expand(arr, ctype, depth, plte, trns)
    full = np.iinfo(e.dtype).max
    if e.shape[2] == 1:
        e = np.concatenate([e, e, e, np.full_like(e, full)], -1)
    elif e.shape[2] == 2:
        e = e[..., [0, 0, 0, 1]]
    elif e.shape[2] == 3:
        e = np.concatenate([e, np.full_like(e[..., :1], full)], -1)
    return e


def composite(frames, w, h, dtype):
    """The APNG rendering rules, straight from the specification: frames are dicts with rgba, x, y, dispose,
    blend. Returns the canvas after each frame. "Over" is rounded exactly."""
    full = int(np.iinfo(dtype).max)
    canvas = np.zeros((h, w, 4), np.int64)
    out = []
    for k, f in enumerate(frames):
        a = f['rgba'].astype(np.int64)
        fh, fw = a.shape[:2]
        x, y = f.get('x', 0), f.get('y', 0)
        dispose = f.get('dispose', 0)
        if k == 0 and dispose == 2:
            dispose = 1
        saved = canvas.copy()
        reg = canvas[y:y + fh, x:x + fw]
        if f.get('blend', 0) == 0:
            reg[:] = a
        else:
            sa, da = a[..., 3:], reg[..., 3:].copy()
            sw, dw = sa * full, da * (full - sa)
            total = np.maximum(sw + dw, 1)
            mixed = np.concatenate([(a[..., :3] * sw + reg[..., :3] * dw + total // 2) // total,
                                    (sw + dw + full // 2) // full], -1)
            reg[:] = np.where(sa == 0, reg, np.where((sa == full) | (da == 0), a, mixed))
        out.append(canvas.copy())
        if dispose == 1:
            reg[:] = 0
        elif dispose == 2:
            canvas[:] = saved
    return np.stack(out).astype(dtype)


def anim_ffmpeg(path, w, h, dtype):
    fmt = 'rgba64le' if dtype == np.uint16 else 'rgba'
    r = subprocess.run(['ffmpeg', '-v', 'error', '-i', path, '-fps_mode', 'passthrough', '-f', 'rawvideo', '-pix_fmt', fmt, '-'],
                       capture_output=True)
    size = w * h * 4 * np.dtype(dtype).itemsize
    if r.returncode != 0 or not r.stdout or len(r.stdout) % size:
        return None
    return np.frombuffer(r.stdout, np.dtype(dtype).newbyteorder('<')).reshape(-1, h, w, 4)


def anim_pillow(path, w, h, dtype):
    """Pillow composites at 8 bit, also for 16 bit files (see judge: 'reduced')."""
    from PIL import Image
    im = Image.open(path)
    fr = []
    for i in range(getattr(im, 'n_frames', 1)):
        im.seek(i)
        fr.append(np.asarray(im.convert('RGBA')).copy())
    return np.stack(fr)


def anim_imagecodecs(path, w, h, dtype):
    import imagecodecs
    a = imagecodecs.apng_decode(open(path, 'rb').read())
    if a.dtype != dtype:
        return None
    if a.ndim == 3:
        a = a[..., None]
    full = np.iinfo(a.dtype).max
    if a.shape[3] == 1:
        a = np.concatenate([a, a, a, np.full_like(a, full)], -1)
    elif a.shape[3] == 2:
        a = a[..., [0, 0, 0, 1]]
    elif a.shape[3] == 3:
        a = np.concatenate([a, np.full_like(a[..., :1], full)], -1)
    return a


def anim_libjxl(path, w, h, dtype):
    """cjxl stores the APNG frames losslessly together with their blend and dispose operations; djxl renders the
    animation with floating point compositing and writes full frames, which any APNG reader can take apart.
    Its blending is rounded from float32, so it can differ from exact rounding by 1 (see judge: 'near')."""
    import imagecodecs, uuid
    cjxl, djxl = tool('cjxl'), tool('djxl')
    if not cjxl or not djxl:
        return None
    os.makedirs(TMP, exist_ok=True)
    # unique names: test processes for both frameworks call this concurrently
    tag = f'_oracle_{os.getpid()}_{uuid.uuid4().hex}'
    jxl, out = os.path.join(TMP, tag + '.jxl'), os.path.join(TMP, tag + '.apng')
    try:
        subprocess.run(cjxl + [path, jxl, '-d', '0', '-e', '3', '--quiet'], check=True, capture_output=True)
        subprocess.run(djxl + [jxl, out, '--quiet'], check=True, capture_output=True)
        with open(out, 'rb') as f:
            a = imagecodecs.apng_decode(f.read())
    finally:
        for f in (jxl, out):
            if os.path.exists(f):
                os.remove(f)
    if a.dtype != dtype:
        return None
    if a.ndim == 3:
        a = a[..., None]
    full = np.iinfo(a.dtype).max
    if a.shape[3] == 1:
        a = np.concatenate([a, a, a, np.full_like(a, full)], -1)
    elif a.shape[3] == 2:
        a = a[..., [0, 0, 0, 1]]
    elif a.shape[3] == 3:
        a = np.concatenate([a, np.full_like(a[..., :1], full)], -1)
    return a


ANIM_DECODERS = [('ffmpeg', anim_ffmpeg), ('pillow', anim_pillow), ('imagecodecs', anim_imagecodecs), ('libjxl', anim_libjxl)]


def judge(a, ref, separate_default=False):
    """How a third-party decode relates to the reference frames. Fully transparent pixels count as equal whatever
    colour they carry (decoders differ in what they leave under alpha 0).
    'exact'    identical RGBA
    'near'     RGBA within 2 (differently rounded "over" blending)
    'rgb'      identical colour wherever the reference is opaque, from a decoder without a notion of the transparent
               canvas (opaque colour types: it shows cleared regions as opaque black)
    'reduced'  an 8 bit decoder matching the 16 bit reference reduced to 8 bit ('exact' or 'rgb' sense)
    None       anything else.
    Pillow and imagecodecs also return the default image of files where it is not part of the animation; that
    extra first frame is dropped."""
    if a is None:
        return None
    if separate_default and a.shape[0] == ref.shape[0] + 1:
        a = a[1:]
    if a.shape != ref.shape:
        return None
    reduced = a.dtype == np.uint8 and ref.dtype == np.uint16
    candidates = [ref]
    if reduced:
        # the two usual 16 -> 8 bit reductions
        candidates = [(ref >> 8).astype(np.uint8), ((ref.astype(np.uint32) * 255 + 32767) // 65535).astype(np.uint8)]
    elif a.dtype != ref.dtype:
        return None
    for r in candidates:
        x, y = a.astype(np.int64), r.astype(np.int64)
        x[x[..., 3] == 0] = 0
        y[y[..., 3] == 0] = 0
        d = np.abs(x - y).max()
        if d == 0:
            return 'reduced' if reduced else 'exact'
        opaque = r[..., 3] == 255 if reduced else ref[..., 3] == np.iinfo(ref.dtype).max
        if np.all(a[..., 3] == np.iinfo(a.dtype).max) and np.array_equal(a[..., :3][opaque], r[..., :3][opaque]):
            return 'reduced' if reduced else 'rgb'
        if d <= 2 and not reduced:
            return 'near'
    return None


def judge_chrome(c, ref):
    """Chrome keeps the canvas premultiplied at 8 bit: alpha must match exactly and premultiplied colour within 1
    (16 bit references, which Chrome reduces to 8 bit before compositing: alpha within 1, colour within 2)."""
    if c is None or c['frames'].shape != ref.shape:
        return None
    a = c['frames'].astype(np.int64)
    r = ref.astype(np.int64)
    wide = ref.dtype == np.uint16
    if wide:
        r = (r * 255 + 32767) // 65535
    if np.abs(a[..., 3] - r[..., 3]).max() > (1 if wide else 0):
        return None
    pa, pr = a[..., :3] * a[..., 3:] / 255.0, r[..., :3] * r[..., 3:] / 255.0
    return 'chrome' if np.abs(pa - pr).max() <= (2.5 if wide else 1.5) else None


anims = []   # (name, path, reference frames, {decoder: verdict})


def emit_anim(name, data, ref, info):
    """Writes an APNG fixture: the file, the default image ref (still decoders), the composited frames stacked
    vertically (.frames.ref) and a JSON description. ref is the candidate reference; who confirms it is checked
    in finish_anims once Chrome has decoded all files in one go."""
    import json
    path = os.path.join(APNG, name + '.png')
    with open(path, 'wb') as f:
        f.write(data)
    still, _ = reference(path)
    write_ref(path + '.ref', still)
    n, h, w, _ = ref.shape
    verdicts = {}
    for dname, dec in ANIM_DECODERS:
        try:
            verdicts[dname] = judge(dec(path, w, h, ref.dtype.type), ref, not info.get('default_is_frame', True))
        except Exception:
            verdicts[dname] = None
    write_ref(path + '.frames.ref', ref.reshape(n * h, w, 4))
    with open(path + '.json', 'w') as f:
        json.dump(info, f, indent=1)
    anims.append((name, path, ref, verdicts))


def finish_anims():
    """Every animation reference must be confirmed by at least two third-party decoders, see judge()."""
    chrome = chrome_decode([a[1] for a in anims])
    bad = []
    for (name, path, ref, verdicts), c in zip(anims, chrome):
        verdicts['chrome'] = judge_chrome(c, ref)
        ok = [d for d, v in verdicts.items() if v]
        if len(ok) < (1 if name in SINGLE_ORACLE else 2):
            bad.append(f'{name}: confirmed only by {verdicts}')
        if name in REQUIRED_ORACLE and not verdicts.get(REQUIRED_ORACLE[name]):
            bad.append(f'{name}: not confirmed by {REQUIRED_ORACLE[name]} (see tool(); run gen_png.py --fetch-tools)')
    if bad:
        raise SystemExit('animation references lack confirmation:\n  ' + '\n  '.join(bad))


def anim_frames(rng, ctype, depth, plan, alpha='binary'):
    """plan: (w, h, x, y, dispose, blend[, delay]) per frame -> frame dicts with random pixels.
    alpha (colour types with an alpha channel): 'binary' 0/max, 'partial' anything, 'opaque'."""
    hi = 1 << depth
    frames = []
    for item in plan:
        fw, fh, x, y, dispose, blend = item[:6]
        a = rng.integers(0, hi, (fh, fw, CHANNELS[ctype]))
        if ctype in (4, 6):
            if alpha == 'binary':
                a[..., -1] = rng.integers(0, 2, (fh, fw)) * (hi - 1)
            elif alpha == 'opaque':
                a[..., -1] = hi - 1
            else:
                a[..., -1] = np.where(rng.integers(0, 4, (fh, fw)) == 0, rng.integers(0, 2, (fh, fw)) * (hi - 1), a[..., -1])
        f = dict(arr=a, x=x, y=y, dispose=dispose, blend=blend)
        if len(item) > 6:
            f['delay'] = item[6]
        frames.append(f)
    return frames


W_A, H_A = 16, 12
FULL = (W_A, H_A, 0, 0)
# every dispose (0 none, 1 background, 2 previous) x blend (0 source, 1 over) combination, in changing order
PLAN_ALL = [FULL + (0, 0), (5, 4, 2, 3, 1, 1), (6, 5, 4, 2, 2, 0), (7, 3, 1, 1, 0, 1), (4, 4, 8, 6, 0, 0), (9, 9, 1, 0, 2, 1),
            (3, 3, 0, 0, 1, 0), (2, 2, 5, 5, 0, 1), (16, 1, 0, 11, 2, 1), (1, 12, 15, 0, 1, 1), (8, 6, 8, 6, 2, 0), (16, 12, 0, 0, 0, 1)]
# canvas stays opaque: only "none"/"previous" disposal after an opaque first frame
PLAN_OPAQUE = [FULL + (0, 0), (5, 4, 2, 3, 0, 1), (6, 5, 4, 2, 2, 1), (7, 3, 1, 1, 0, 1), (9, 9, 1, 0, 2, 1), (16, 12, 0, 0, 0, 1)]


# for decoders that do not implement "previous" disposal (imagecodecs)
PLAN_NO_PREVIOUS = [f[:4] + (0 if f[4] == 2 else f[4], f[5]) for f in PLAN_ALL]
PLAN_SOURCE = [f[:5] + (0,) for f in PLAN_NO_PREVIOUS]

# References that only one third-party decoder can confirm, and why that is accepted. (None at present: blending
# of translucent 16 bit frames, which ffmpeg skips and Pillow, Chrome and imagecodecs cannot do at 16 bit, is
# confirmed by libjxl.)
SINGLE_ORACLE = {}

# Fixtures that exist to pin down one behaviour need the decoder that can actually judge it.
REQUIRED_ORACLE = {
    'ops_rgba16_partial': 'libjxl',
    'ops_ga16_partial': 'libjxl',
    'ops_rgba16_partial_on_opaque': 'libjxl',
}


def gen_apng():
    os.makedirs(APNG, exist_ok=True)

    def build(name, ctype, depth, plan, alpha='binary', seed=0, plte=None, trns=None, **kw):
        rng = np.random.default_rng(1000 + seed)
        frames = anim_frames(rng, ctype, depth, plan, alpha)
        if 'opaque_first' in kw:
            kw.pop('opaque_first')
            frames[0]['arr'][..., -1] = (1 << depth) - 1
        default = kw.get('default')
        dtype = np.uint16 if depth == 16 else np.uint8
        data = make_apng(W_A, H_A, ctype, depth, frames, plte=plte, trns=trns, **kw)
        ref = composite([dict(f, rgba=to_rgba(f['arr'], ctype, depth, plte, trns)) for f in frames], W_A, H_A, dtype)
        info = {'plays': kw.get('plays', 0), 'default_is_frame': default is None,
                'frames': [{'x': f['x'], 'y': f['y'], 'w': f['arr'].shape[1], 'h': f['arr'].shape[0],
                            'delay': list(f.get('delay', (1, 10))), 'dispose': f['dispose'], 'blend': f['blend']} for f in frames]}
        emit_anim(name, data, ref, info)

    build('ops_rgba8_binary', 6, 8, PLAN_ALL, seed=1)
    build('ops_rgba8_first_previous', 6, 8, [FULL + (2, 1)] + PLAN_ALL[1:6], seed=2)
    build('ops_rgba8_partial_on_opaque', 6, 8, PLAN_OPAQUE, 'partial', seed=3, opaque_first=True)
    build('ops_rgba8_partial', 6, 8, PLAN_ALL, 'partial', seed=4)
    build('ops_rgb8', 2, 8, PLAN_ALL, seed=5)
    build('ops_gray8', 0, 8, PLAN_ALL, seed=6)
    build('ops_ga8_binary', 4, 8, PLAN_ALL, seed=7)
    build('ops_ga8_partial_on_opaque', 4, 8, PLAN_OPAQUE, 'partial', seed=8, opaque_first=True)
    build('ops_gray2', 0, 2, PLAN_ALL, seed=9)
    build('ops_rgba16_binary', 6, 16, PLAN_ALL, seed=10)
    build('ops_rgba16_partial_on_opaque', 6, 16, PLAN_OPAQUE, 'partial', seed=11, opaque_first=True)
    build('ops_rgba16_partial_source', 6, 16, PLAN_SOURCE, 'partial', seed=12)
    # translucent over translucent at 16 bit, every dispose and blend operation
    build('ops_rgba16_partial', 6, 16, PLAN_ALL, 'partial', seed=40)
    build('ops_ga16_partial', 4, 16, PLAN_ALL, 'partial', seed=41)
    build('ops_rgb16', 2, 16, PLAN_ALL, seed=13)
    build('ops_gray16', 0, 16, PLAN_ALL, seed=14)
    build('ops_ga16_binary', 4, 16, PLAN_ALL, seed=15)
    # colour keys: a few key coloured pixels come out of the random data at these low sample counts
    rng = np.random.default_rng(77)
    build('ops_rgb8_key', 2, 8, PLAN_ALL, seed=16, trns=struct.pack('>3H', 0, 0, 0))
    build('ops_gray4_key', 0, 4, PLAN_NO_PREVIOUS, seed=17, trns=struct.pack('>H', 5))
    build('ops_gray16_key', 0, 16, PLAN_NO_PREVIOUS, seed=18, trns=struct.pack('>H', 0))
    build('ops_pal8', 3, 8, PLAN_ALL, seed=19, plte=palette(256, 19))
    build('ops_pal4_alpha_binary', 3, 4, PLAN_ALL, seed=20, plte=palette(16, 20), trns=bytes([0, 255, 0, 0, 255, 0]))
    build('ops_pal2_alpha_partial_source', 3, 2, PLAN_SOURCE, seed=21, plte=palette(4, 21), trns=bytes([0, 90, 200]))
    build('ops_pal1', 3, 1, PLAN_ALL, seed=22, plte=palette(2, 22))
    del rng

    # container variants
    rng = np.random.default_rng(2000)
    build('default_not_a_frame', 6, 8, PLAN_ALL[:6], seed=30, default=rng.integers(0, 256, (H_A, W_A, 4)))
    build('default_not_a_frame_rgb16', 2, 16, PLAN_ALL[:5], seed=31, default=rng.integers(0, 65536, (H_A, W_A, 3)))
    build('interlaced_rgba8', 6, 8, PLAN_ALL, seed=32, interlace=True)
    build('interlaced_pal4', 3, 4, PLAN_NO_PREVIOUS[:7], seed=33, interlace=True, plte=palette(16, 33), trns=bytes([0, 255, 0]))
    build('split_data_chunks', 6, 8, PLAN_ALL[:7], seed=34, split=[1, 3, 9, 30])
    build('single_frame', 2, 8, [FULL + (0, 0)], seed=35)
    build('single_frame_after_default', 6, 8, [FULL + (0, 0)], seed=36, default=rng.integers(0, 256, (H_A, W_A, 4)))
    build('plays_3_delays', 6, 8, [FULL + (0, 0, (1, 0)), (5, 4, 2, 3, 0, 0, (0, 100)), (4, 4, 8, 6, 0, 1, (65535, 1000)),
                                  (3, 3, 0, 0, 1, 0, (7, 30)), (2, 2, 5, 5, 0, 1, (2, 1))], seed=37, plays=3)
    build('with_metadata', 2, 8, PLAN_ALL[:4], seed=38,
          pre=[chunk(b'gAMA', struct.pack('>I', 45455)), chunk(b'tEXt', b'Title\x00animated')])

    gen_apng_third_party()


def gen_apng_third_party():
    """APNGs written by Pillow, ffmpeg and imagecodecs. Without source frame geometry to reason from, their
    reference is what ffmpeg decodes (binary alpha only, where every correct renderer is bit exact), confirmed like
    the others."""
    import json
    from PIL import Image
    import imagecodecs

    def finish(name, path):
        im = Image.open(path)
        w, h = im.size
        ref = anim_ffmpeg(path, w, h, np.uint8)
        if ref is None:
            raise SystemExit(name + ': ffmpeg cannot decode it')
        still, _ = reference(path)
        write_ref(path + '.ref', still)
        verdicts = {'ffmpeg': 'exact'}
        for dname, dec in ANIM_DECODERS[1:]:
            try:
                verdicts[dname] = judge(dec(path, w, h, np.uint8), ref, True)
            except Exception:
                verdicts[dname] = None
        write_ref(path + '.frames.ref', ref.reshape(-1, w, 4))
        with open(path + '.json', 'w') as f:
            json.dump({'plays': im.info.get('loop', 0), 'frames': None}, f)
        anims.append((name, path, ref, verdicts))

    rng = np.random.default_rng(3000)
    base = test_pattern(40, 30, 4, np.uint8, 500)
    base[..., 3] = 255
    frames = []
    for k in range(6):
        a = base.copy()
        a[4 + 3 * k:12 + 3 * k, 5 + 4 * k:15 + 4 * k] = rng.integers(0, 256, 4)
        a[4 + 3 * k:12 + 3 * k, 5 + 4 * k:15 + 4 * k, 3] = 255 * (k % 2)
        frames.append(Image.fromarray(a))
    path = os.path.join(APNG, 'pil_diff.png')
    frames[0].save(path, save_all=True, append_images=frames[1:], duration=[50, 100, 150, 200, 250, 300], loop=2)
    finish('pil_diff', path)
    path = os.path.join(APNG, 'pil_dispose_blend.png')
    frames[0].save(path, save_all=True, append_images=frames[1:], duration=40, loop=0, default_image=False,
                   disposal=[0, 1, 2, 0, 1, 2], blend=[0, 1, 1, 0, 1, 0])
    finish('pil_dispose_blend', path)
    path = os.path.join(APNG, 'pil_default_image.png')
    frames[0].save(path, save_all=True, append_images=frames[1:4], duration=70, default_image=True)
    finish('pil_default_image', path)

    path = os.path.join(APNG, 'imagecodecs_rgb.png')
    with open(path, 'wb') as f:
        f.write(imagecodecs.apng_encode(np.stack([np.asarray(fr)[..., :3] for fr in frames])))
    finish('imagecodecs_rgb', path)

    raw = b''.join(np.asarray(fr)[..., :3].tobytes() for fr in frames)
    path = os.path.join(APNG, 'ffmpeg_rgb.png')
    subprocess.run(['ffmpeg', '-v', 'error', '-y', '-f', 'rawvideo', '-pix_fmt', 'rgb24', '-s', '40x30', '-r', '5', '-i', '-',
                    '-plays', '4', '-f', 'apng', path], input=raw, check=True)
    finish('ffmpeg_rgb', path)


# ---------------------------------------------------------------------------------------------- metadata

def read_meta(path):
    """What third-party readers report about the ancillary chunks and the animation of a PNG:
    Pillow (gamma, chromaticities, sRGB intent, text, frame count / loop count / frame durations),
    ImageMagick (tIME) and netpbm's pngtopam (raw bKGD values, presence of sBIT)."""
    import re
    import warnings
    from PIL import Image
    warnings.simplefilter('ignore')
    m = {}
    im = Image.open(path)
    try:
        im.load()
    except Exception:
        pass  # Pillow cannot decode interlaced APNG frames; the chunk level information is still there
    for key, name in (('gamma', 'gamma'), ('chromaticity', 'chromaticity'), ('srgb', 'srgb')):
        if key in im.info:
            v = im.info[key]
            m[name] = list(v) if isinstance(v, tuple) else v
    text = []
    for k, v in getattr(im, 'text', {}).items():
        itxt = hasattr(v, 'lang')
        lang, tkey = getattr(v, 'lang', None), getattr(v, 'tkey', None)
        lang = lang.decode('latin-1') if isinstance(lang, bytes) else lang
        tkey = tkey.decode('utf-8') if isinstance(tkey, bytes) else tkey
        text.append({'keyword': k, 'text': str(v), 'itxt': itxt, 'lang': lang or '', 'tkey': tkey or ''})
    if text:
        m['text'] = text
    n = getattr(im, 'n_frames', 1)
    if getattr(im, 'is_animated', False) or b'acTL' in open(path, 'rb').read(4096):
        m['frames'] = n
        m['loop'] = im.info.get('loop', 0)
        try:
            durations = []
            for i in range(n):
                im.seek(i)
                durations.append(float(im.info.get('duration', 0)))
            m['durations'] = durations
        except Exception:
            pass
    r = subprocess.run(['magick', 'identify', '-format', '%[png:tIME]', 'PNG:' + path], capture_output=True, text=True)
    if r.returncode == 0 and r.stdout.strip():
        m['time'] = r.stdout.strip()
    r = subprocess.run(['pngtopam', '-verbose', path], capture_output=True)
    err = r.stderr.decode('latin-1').replace('\r\n', '\n')
    bk = re.search(r'background \{index, gray, red, green, blue\} = \{(\d+), (\d+), (\d+), (\d+), (\d+)\}', err)
    if bk and 'bKGD chunk: present' in err:
        m['bkgd'] = [int(x) for x in bk.groups()]
    if 'sBIT chunk: present' in err:
        m['sbit'] = True
    m.update(read_chunks(path))
    return m


def read_chunks(path):
    """Chunk contents as pngcheck prints them (sBIT values, hIST, sPLT, oFFs, pCAL, sCAL, sTER, cICP names, mDCV,
    cLLI), with exiftool as a second reader for those it decodes (sBIT, sCAL, sTER, cICP): where both report a
    value they must agree. 'tools' lists which of the two were available."""
    import json
    import re
    m = {'tools': []}
    pngcheck, exiftool = tool('pngcheck'), tool('exiftool')
    if pngcheck:
        m['tools'].append('pngcheck')
        out = subprocess.run(pngcheck + ['-vp', path], capture_output=True).stdout.decode('latin-1').replace('\r\n', '\n')
        blocks = re.split(r'\n  chunk ', out)
        num = r'([-+0-9.eE]+)'
        for b in blocks[1:]:
            name = b[:4]
            if name == 'sBIT':
                m['sbit'] = [int(v) for v in re.findall(r'= (\d+) = 0x', b)]
            elif name == 'hIST':
                m['hist'] = [int(v) for v in re.findall(r'^\s+\d+:\s+(\d+)\s*$', b, re.M)]
            elif name == 'sPLT':
                head = re.search(r'sample depth = (\d+) bits, palette name = (.*)', b)
                entries = [[int(v) for v in e] for e in re.findall(
                    r'^\s+\d+:\s+\(\s*(\d+),\s*(\d+),\s*(\d+),\s*(\d+)\).*freq = (\d+)', b, re.M)]
                m.setdefault('splt', []).append({'name': head.group(2).rstrip('\r'), 'depth': int(head.group(1)), 'entries': entries})
            elif name == 'oFFs':
                x, y, unit = re.search(r': (-?\d+)x(-?\d+) (\w+) offset', b).groups()
                # pngcheck prints the signed positions as unsigned (as signed where long has 32 bits: Windows)
                m['offs'] = [int(x) & 0xFFFFFFFF, int(y) & 0xFFFFFFFF, unit]
            elif name == 'pCAL':
                m['pcal'] = {'type': int(re.search(r'equation type (\d+)', b).group(1)),
                             'name': re.search(r'calibration name = (.*)', b).group(1),
                             'unit': re.search(r'unit name = (.*)', b).group(1),
                             'x0': int(re.search(r'x0 = (-?\d+)', b).group(1)), 'x1': int(re.search(r'x1 = (-?\d+)', b).group(1)),
                             'params': re.findall(r'^\s+p\d = (\S+)', b, re.M)}
            elif name == 'sCAL':
                w, h, unit = re.search(r'image size (\S+) x (\S+) (\w+)', b).groups()
                m['scal'] = [unit, w, h]
            elif name == 'sTER':
                m['ster'] = 1 if 'divergent' in b else 0
            elif name == 'cICP':
                m['cicp_full_range'] = 'Full range' in b
            elif name == 'mDCV':
                wx, wy, rx, ry = re.search(rf'White x = {num} y = {num},\s+Red x = {num} y = {num}', b).groups()
                gx, gy, bx, by = re.search(rf'Green x = {num} y = {num},\s+Blue x = {num} y = {num}', b).groups()
                m['mdcv'] = [float(v) for v in (rx, ry, gx, gy, bx, by, wx, wy)] + [
                    float(re.search(rf'Maximum luminance = {num}', b).group(1)), float(re.search(rf'Minimum luminance = {num}', b).group(1))]
            elif name == 'cLLI':
                m['clli'] = [float(re.search(rf'Maximum content light level = {num}', b).group(1)),
                             float(re.search(rf'Maximum frame average light level = {num}', b).group(1))]
    if exiftool:
        m['tools'].append('exiftool')
        r = subprocess.run(exiftool + ['-j', '-a', '-G1', '-n', path], capture_output=True, text=True)
        j = json.loads(r.stdout)[0] if r.stdout.strip() else {}
        if 'PNG:SignificantBits' in j:
            bits = [int(v) for v in str(j['PNG:SignificantBits']).split()]
            assert m.setdefault('sbit', bits) == bits, f'{path}: pngcheck and exiftool disagree on sBIT'
        if 'PNG-cICP:ColorPrimaries' in j:
            m['cicp'] = [j['PNG-cICP:ColorPrimaries'], j['PNG-cICP:TransferCharacteristics'], j['PNG-cICP:MatrixCoefficients'],
                         j['PNG-cICP:VideoFullRangeFlag']]
            assert m.get('cicp_full_range', bool(m['cicp'][3])) == bool(m['cicp'][3]), f'{path}: cICP range flag'
        if 'PNG:StereoMode' in j:
            assert m.setdefault('ster', j['PNG:StereoMode']) == j['PNG:StereoMode'], f'{path}: sTER'
        if 'PNG:SubjectPixelWidth' in j and 'scal' in m:
            assert float(m['scal'][1]) == j['PNG:SubjectPixelWidth'], f'{path}: sCAL'
    m.pop('cicp_full_range', None)
    return m   # always carries 'tools', so a caller can tell "chunk absent" from "no tool to look"


def gen_meta():
    """Metadata fixtures and a .meta.json sidecar (see read_meta) for every fixture that has something to report."""
    if not tool('pngcheck') or not tool('exiftool'):
        raise SystemExit('pngcheck and exiftool are needed for the metadata sidecars: run gen_png.py --fetch-tools')
    import json
    from PIL import Image, PngImagePlugin
    arr = samples(21, 13, 3, 8, 600)
    long_text = 'PixCodec ' * 200
    itxt = lambda key, lang, tkey, text, z: chunk(b'iTXt', key + b'\x00' + (b'\x01' if z else b'\x00') + b'\x00' + lang + b'\x00'
                                                  + tkey.encode() + b'\x00' + (zlib.compress(text.encode()) if z else text.encode()))
    pre = [
        chunk(b'sRGB', b'\x01'),
        chunk(b'gAMA', struct.pack('>I', 45455)),
        chunk(b'cHRM', struct.pack('>8I', 31270, 32900, 64000, 33000, 30000, 60000, 15000, 6000)),
        chunk(b'sBIT', bytes([5, 6, 5])),
        chunk(b'bKGD', struct.pack('>3H', 255, 128, 3)),
        chunk(b'tIME', struct.pack('>HBBBBB', 2024, 2, 29, 23, 59, 58)),
        chunk(b'tEXt', b'Title\x00Latin-1 text: caf\xe9'),
        chunk(b'zTXt', b'Description\x00\x00' + zlib.compress(long_text.encode('latin-1'))),
        itxt(b'Comment', b'de-AT', 'Kommentar', 'Grüße, 日本語, 😀', False),
        itxt(b'Warning', b'', '', long_text + ' ✓', True),
    ]
    post = [chunk(b'tEXt', b'Software\x00after the image data')]
    emit('meta_png_chunks', make_png(arr, 2, 8, pre=pre, post=post), arr)

    gray = samples(9, 9, 1, 4, 601)
    emit('meta_bkgd_gray4', make_png(gray, 0, 4, pre=[chunk(b'bKGD', struct.pack('>H', 9)), chunk(b'sBIT', bytes([3]))]),
         expand(gray, 0, 4))
    idx = samples(9, 9, 1, 8, 602) % 7
    pal = palette(7, 6)
    with open(os.path.join(OUT, 'meta_bkgd_palette.png'), 'wb') as f:   # bKGD has to follow PLTE
        data = make_png(idx, 3, 8, plte=pal)
        cut = data.index(b'IDAT') - 4
        f.write(data[:cut] + chunk(b'bKGD', bytes([4])) + data[cut:])
    ref, who = reference(os.path.join(OUT, 'meta_bkgd_palette.png'), expand(idx, 3, 8, pal))
    write_ref(os.path.join(OUT, 'meta_bkgd_palette.png.ref'), ref)
    written.append(('meta_bkgd_palette', who))

    # the rarely used chunks, including the HDR ones of the third edition of the specification
    rare = samples(24, 9, 3, 8, 700)
    pre = [
        chunk(b'cICP', bytes([9, 16, 0, 1])),
        chunk(b'mDCV', struct.pack('>8HII', 35400, 14600, 8500, 39850, 6550, 2300, 15635, 16450, 10000000, 50)),
        chunk(b'cLLI', struct.pack('>II', 10000000, 2500000)),
        chunk(b'sPLT', b'web safe\x00\x08' + b''.join(struct.pack('>4BH', i * 51, 255 - i * 51, 7 * i, 255 - i, 1000 - i) for i in range(6))),
        chunk(b'sPLT', b'deep\x00\x10' + b''.join(struct.pack('>5H', i * 9000, 65535 - i * 9000, 123 * i, 65535, i) for i in range(4))),
        chunk(b'oFFs', struct.pack('>iiB', -1234, 56789, 1)),
        chunk(b'pCAL', b'elevation\x00' + struct.pack('>iiBB', 0, 65535, 0, 2) + b'm\x00' + b'-12.5\x00' + b'0.001953125'),
        chunk(b'sCAL', b'\x01' + b'0.00025\x00' + b'2.5e-4'),
        chunk(b'sTER', b'\x01'),
    ]
    emit('meta_rare_chunks', make_png(rare, 2, 8, pre=pre), rare)
    emit('meta_rare_pcal_hyperbolic', make_png(rare[:3, :5], 2, 8, pre=[
        chunk(b'pCAL', b'density\x00' + struct.pack('>iiBB', 10, 250, 3, 4) + b'g/cm\xb3\x00' + b'1\x00' + b'-2.5E+1\x00' + b'0.125\x00' + b'4'),
        chunk(b'sCAL', b'\x02' + b'1e-3\x00' + b'0.002'), chunk(b'oFFs', struct.pack('>iiB', 7, -8, 0)), chunk(b'sTER', b'\x00'),
        chunk(b'mDCv', bytes(24)), chunk(b'cLLi', struct.pack('>II', 5000, 100))]), rare[:3, :5])   # draft spellings
    hidx = samples(12, 6, 1, 8, 701) % 8
    hpal = palette(8, 9)
    with open(os.path.join(OUT, 'meta_hist_palette.png'), 'wb') as f:   # hIST has to follow PLTE
        data = make_png(hidx, 3, 8, plte=hpal)
        cut = data.index(b'IDAT') - 4
        f.write(data[:cut] + chunk(b'hIST', struct.pack('>8H', 5, 0, 65535, 100, 7, 7, 1, 30000)) + data[cut:])
    ref, who = reference(os.path.join(OUT, 'meta_hist_palette.png'), expand(hidx, 3, 8, hpal))
    write_ref(os.path.join(OUT, 'meta_hist_palette.png.ref'), ref)
    written.append(('meta_hist_palette', who))

    # written by Pillow and ImageMagick
    info = PngImagePlugin.PngInfo()
    info.add_text('Author', 'Pillow')
    info.add_text('Compressed', 'z' * 3000, zip=True)
    info.add_itxt('Greeting', 'héllo wörld ✓', lang='fr', tkey='Salut')
    info.add_itxt('Packed', 'ü' * 2000, lang='de', tkey='Gepackt', zip=True)
    path = os.path.join(OUT, 'pil_text.png')
    Image.fromarray(arr.astype(np.uint8)).save(path, pnginfo=info, gamma=0.5)
    ref, who = reference(path)
    write_ref(path + '.ref', ref)
    written.append(('pil_text', who))

    path = os.path.join(OUT, 'im_chunks.png')
    tmp = os.path.join(OUT, '_src.png')
    Image.fromarray(arr.astype(np.uint8)).save(tmp)
    subprocess.run(['magick', tmp, '-background', '#102030', '-set', 'comment', 'made by ImageMagick',
                    '-define', 'png:include-chunk=all', '-define', 'png:exclude-chunk=date,tIME', '-set', 'colorspace', 'sRGB',
                    path], check=True)   # no timestamps: the fixture has to be reproducible
    os.remove(tmp)
    ref, who = reference(path)
    write_ref(path + '.ref', ref)
    written.append(('im_chunks', who))

    count = 0
    for d in (OUT, SUITE, APNG) if SUITE else (OUT, APNG):
        for path in sorted(glob.glob(os.path.join(d, '*.png'))):
            if os.path.basename(path).startswith('x') and d == SUITE:
                continue
            m = read_meta(path)
            m.pop('tools', None)
            if m:
                with open(path + '.meta.json', 'w') as f:
                    json.dump(m, f, indent=1, ensure_ascii=False)
                count += 1
    return count


def main():
    global SUITE
    SUITE = testdata_dir('fixtures', 'png', 'pngsuite')
    os.makedirs(OUT, exist_ok=True)
    stale = [os.path.join(OUT, '*.png'), os.path.join(OUT, '*.ref'), os.path.join(OUT, '*.json'), os.path.join(APNG, '*')]
    if SUITE:
        stale += [os.path.join(SUITE, '*.ref'), os.path.join(SUITE, '*.json')]
    for f in [f for pattern in stale for f in glob.glob(pattern)]:
        os.remove(f)
    gen_matrix()
    gen_filters()
    gen_transparency()
    gen_chunks()
    gen_third_party()
    if SUITE:
        gen_suite_refs()
    gen_apng()
    finish_anims()
    metas = gen_meta()
    votes = {}
    for _, who in written:
        votes[len(who)] = votes.get(len(who), 0) + 1
    print(f'{len(written)} fixtures; reference decoders agreeing per file: {dict(sorted(votes.items()))}')
    print(f'{len(anims)} animations, each confirmed by at least two decoders (except {sorted(SINGLE_ORACLE)})')
    print(f'{metas} metadata sidecars')
    if '-v' in sys.argv:
        for name, who in written:
            print(f'  {name}: {",".join(who)}')
        for name, _, _, verdicts in anims:
            print(f'  apng/{name}: ' + ', '.join(f'{d}={v}' for d, v in verdicts.items() if v))


if __name__ == '__main__':
    if len(sys.argv) == 2 and sys.argv[1] == '--fetch-tools':
        fetch_tools()
    elif len(sys.argv) == 3 and sys.argv[1] == '--meta':
        # used by the C# tests to check what OUR encoder wrote
        import json
        print(json.dumps(read_meta(sys.argv[2]), ensure_ascii=True))
    else:
        main()
