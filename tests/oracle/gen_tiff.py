"""Generates the TIFF fixtures (tests/fixtures/tiff) and their PXRF reference sidecars.

usage: python3 gen_tiff.py            (from tests/oracle; deterministic, overwrites the fixture directory)

Most files come from the small TIFF writer below because none of the installed tools can produce the whole
matrix (tifffile lacks LZW / PackBits / floating point predictor without `imagecodecs`, ImageMagick rewrites
channel layouts and is not an HDRI build). Every file is then decoded by the independent reference decoders
that can read it - tifffile, OpenCV (libtiff 4.7) and Pillow (libtiff) - and has to match the pixels the
fixture was built from. Files that none of them can read (e.g. half floats with the floating point predictor,
5-channel LZW) are listed as "by construction" in the summary: their reference is the source array, with the
codec path they use being covered by other, verified fixtures.

The .ref holds what PixCodec is specified to return, i.e. the reference decoder's samples after the documented
conversions (MinIsWhite inversion, 1/2/4-bit scaling, palette expansion, naive CMYK, un-premultiplied alpha).
Those conversions are cross-checked against Pillow / ImageMagick where these implement the same thing.
"""
import io
import os
import shutil
import struct
import subprocess
import sys
import warnings
import zlib

import numpy as np

from pxref import write_ref, test_pattern

warnings.filterwarnings('ignore')
os.environ['OPENCV_LOG_LEVEL'] = 'SILENT'
import cv2  # noqa: E402
import tifffile  # noqa: E402
from PIL import Image  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, '..', 'fixtures', 'tiff')

NONE, LZW, DEFLATE, PACKBITS = 1, 5, 8, 32773


# ----------------------------------------------------------------------------------------------- codecs

def lzw_encode(data, old_style=False):
    """TIFF LZW. old_style: the pre-6.0 variant (LSB-first codes, no early change)."""
    out = bytearray()
    acc = 0
    nb = 0

    def put(code, width):
        nonlocal acc, nb
        if old_style:
            acc |= code << nb
            nb += width
            while nb >= 8:
                out.append(acc & 0xFF)
                acc >>= 8
                nb -= 8
        else:
            acc = (acc << width) | code
            nb += width
            while nb >= 8:
                nb -= 8
                out.append((acc >> nb) & 0xFF)
            acc &= (1 << nb) - 1

    limit = 1 if old_style else 0
    table = {bytes([i]): i for i in range(256)}
    nxt, width = 258, 9
    put(256, width)
    w = b''
    for c in data:
        wc = w + bytes([c])
        if wc in table:
            w = wc
            continue
        put(table[w], width)
        table[wc] = nxt
        nxt += 1
        if nxt == (1 << width) + limit and width < 12:
            width += 1
        if nxt >= 4094:
            put(256, width)
            table = {bytes([i]): i for i in range(256)}
            nxt, width = 258, 9
        w = bytes([c])
    if w:
        put(table[w], width)
        nxt += 1
        if nxt == (1 << width) + limit and width < 12:
            width += 1
    put(257, width)
    if nb:
        if old_style:
            out.append(acc & 0xFF)
        else:
            out.append((acc << (8 - nb)) & 0xFF)
    return bytes(out)


def packbits_encode(data, row_bytes):
    out = bytearray()
    for r in range(0, len(data), row_bytes):
        row = data[r:r + row_bytes]
        i, n = 0, len(row)
        while i < n:
            run = 1
            while i + run < n and run < 128 and row[i + run] == row[i]:
                run += 1
            if run >= 3 or (run == 2 and i + 2 >= n):
                out.append((1 - run) & 0xFF)
                out.append(row[i])
                i += run
                continue
            start = i
            i += 1
            while i < n and i - start < 128 and not (i + 2 < n and row[i] == row[i + 1] == row[i + 2]):
                i += 1
            out.append(i - start - 1)
            out += row[start:i]
    return bytes(out)


REV = bytes(int(f'{i:08b}'[::-1], 2) for i in range(256))


# ----------------------------------------------------------------------------------------------- writer

TYPE_FMT = {1: 'B', 2: 'c', 3: 'H', 4: 'I', 5: 'II', 6: 'b', 7: 'B', 8: 'h', 9: 'i', 10: 'ii', 11: 'f', 12: 'd',
            16: 'Q', 17: 'q'}


def pack_bits_rows(a, bits):
    """a: (rows, n) small integers -> rows of MSB-first packed samples, each row padded to a byte."""
    rows, n = a.shape
    b = ((a[:, :, None].astype(np.uint16) >> np.arange(bits - 1, -1, -1)) & 1).astype(np.uint8).reshape(rows, n * bits)
    return np.packbits(b, axis=1)


def encode_chunk(a, p, e):
    """a: (rows, cols, samples) samples of one strip/tile (one plane when planar) -> compressed bytes."""
    rows, cols, s = a.shape
    bits = p.get('bits', a.dtype.itemsize * 8)
    pred = p.get('predictor', 1)
    comp = p.get('compression', NONE)
    if bits < 8:
        raw = pack_bits_rows(a.reshape(rows, cols * s), bits)
        row_bytes = raw.shape[1]
        raw = raw.tobytes()
    else:
        row_bytes = cols * s * a.dtype.itemsize
        if pred == 3:
            b = np.frombuffer(a.astype(a.dtype.newbyteorder('>')).tobytes(), np.uint8).reshape(rows, cols * s, a.dtype.itemsize)
            b = np.ascontiguousarray(b.transpose(0, 2, 1)).reshape(rows, -1).copy()
            b[:, s:] -= b[:, :-s].copy()
            raw = b.tobytes()
        else:
            if pred == 2:
                u = a.view(np.dtype(f'u{a.dtype.itemsize}')).copy()
                u[:, 1:, :] -= u[:, :-1, :].copy()
                a = u
            raw = a.astype(a.dtype.newbyteorder(e)).tobytes()
    if comp == LZW:
        raw = lzw_encode(raw, p.get('lzw_old', False))
    elif comp in (DEFLATE, 32946):
        raw = zlib.compress(raw, 6)
    elif comp == PACKBITS:
        raw = packbits_encode(raw, row_bytes)
    if p.get('fillorder', 1) == 2:
        raw = raw.translate(REV)
    return raw


def write_tiff(path, pages, byteorder='<', bigtiff=False):
    """pages: list of dicts with 'arr' (H,W,C) and options:
    bits, compression, predictor, planar(1|2), tile(tw,th), rps, photometric, extrasamples, colormap,
    fillorder, lzw_old, sampleformat, tags [(id, type, values)], no_bytecounts, zero_bytecounts, sparse (set of chunk indices),
    raw_chunks (pre-encoded strips, used for hand-built YCbCr data)."""
    e = byteorder
    f = io.BytesIO()
    f.write((b'II' if e == '<' else b'MM') + struct.pack(e + 'H', 43 if bigtiff else 42))
    if bigtiff:
        f.write(struct.pack(e + 'HHQ', 8, 0, 0))
    else:
        f.write(struct.pack(e + 'I', 0))
    link_pos = 8 if bigtiff else 4
    osz = 8 if bigtiff else 4
    ofmt = 'Q' if bigtiff else 'I'
    for p in pages:
        a = p['arr']
        if a.ndim == 2:
            a = a[:, :, None]
        h, w, c = a.shape
        bits = p.get('bits', a.dtype.itemsize * 8)
        planar = p.get('planar', 1)
        tile = p.get('tile')
        cw, chh = tile if tile else (w, p.get('rps', h))
        chunks = []
        if 'raw_chunks' in p:
            chunks = list(p['raw_chunks'])
        else:
            for plane in (range(c) if planar == 2 else [None]):
                src = a if plane is None else a[:, :, plane:plane + 1]
                for y in range(0, h, chh):
                    for x in range(0, w, cw):
                        blk = src[y:y + chh, x:x + cw]
                        if tile:
                            pad = np.zeros((chh, cw, blk.shape[2]), a.dtype)
                            pad[:blk.shape[0], :blk.shape[1]] = blk
                            blk = pad
                        chunks.append(encode_chunk(np.ascontiguousarray(blk), p, e))
        offsets, counts = [], []
        for i, ch in enumerate(chunks):
            if i in p.get('sparse', ()):
                offsets.append(0)
                counts.append(0)
                continue
            offsets.append(f.tell())
            counts.append(len(ch))
            f.write(ch)
        if f.tell() & 1:
            f.write(b'\0')

        kind = a.dtype.kind
        tags = [(256, 4, [w]), (257, 4, [h]), (258, 3, [bits] * c), (259, 3, [p.get('compression', NONE)]),
                (262, 3, [p.get('photometric', 2 if c >= 3 else 1)]), (277, 3, [c]), (284, 3, [planar])]
        big_t = 16 if bigtiff else 4
        if tile:
            tags += [(322, 3, [cw]), (323, 3, [chh]), (324, big_t, offsets)]
            if not p.get('no_bytecounts'):
                tags.append((325, big_t, counts))
        else:
            tags += [(273, big_t, offsets), (278, 3, [chh])]
            if not p.get('no_bytecounts'):
                tags.append((279, big_t, [0] * len(counts) if p.get('zero_bytecounts') else counts))
        sf = p.get('sampleformat', {'u': 1, 'i': 2, 'f': 3}[kind])
        if sf != 1:
            tags.append((339, 3, [sf] * c))
        if p.get('predictor', 1) != 1:
            tags.append((317, 3, [p['predictor']]))
        if p.get('fillorder', 1) != 1:
            tags.append((266, 3, [p['fillorder']]))
        if 'extrasamples' in p:
            tags.append((338, 3, list(p['extrasamples'])))
        if 'colormap' in p:
            tags.append((320, 3, list(p['colormap'])))
        tags += p.get('tags', [])
        tags.sort(key=lambda t: t[0])

        ifd_pos = f.tell()
        here = f.tell()
        f.seek(link_pos)
        f.write(struct.pack(e + ofmt, ifd_pos))
        f.seek(here)
        nfmt, esz = ('Q', 20) if bigtiff else ('H', 12)
        entries = bytearray()
        extra = bytearray()
        extra_pos = ifd_pos + struct.calcsize(nfmt) + len(tags) * esz + osz
        for tag, typ, vals in tags:
            if typ == 2:
                data = vals.encode('latin1') + b'\0'
                count = len(data)
            elif typ == 7 and isinstance(vals, (bytes, bytearray)):
                data = bytes(vals)
                count = len(data)
            elif typ in (5, 10):
                data = b''.join(struct.pack(e + TYPE_FMT[typ], *v) for v in vals)
                count = len(vals)
            else:
                data = struct.pack(e + str(len(vals)) + TYPE_FMT[typ], *vals)
                count = len(vals)
            entries += struct.pack(e + 'HH' + ofmt, tag, typ, count)
            if len(data) <= osz:
                entries += data.ljust(osz, b'\0')
            else:
                entries += struct.pack(e + ofmt, extra_pos + len(extra))
                extra += data
                if len(extra) & 1:
                    extra += b'\0'
        f.write(struct.pack(e + nfmt, len(tags)))
        f.write(entries)
        link_pos = f.tell()
        f.write(struct.pack(e + ofmt, 0))
        f.write(extra)
    with open(path, 'wb') as o:
        o.write(f.getvalue())


# ----------------------------------------------------------------------------------------------- expected output

def umax(dt):
    return np.iinfo(dt).max


def unpremultiply(a):
    """c = round(c * max / alpha) clamped; alpha 0 leaves the colour as stored."""
    a = a.copy()
    al = a[:, :, -1:]
    col = a[:, :, :-1]
    if a.dtype.kind == 'u':
        m = umax(a.dtype)
        al64 = al.astype(np.uint64)
        safe = np.maximum(al64, 1)
        v = np.minimum((col.astype(np.uint64) * m + al64 // 2) // safe, m)
        a[:, :, :-1] = np.where(al64 == 0, col, v).astype(a.dtype)
    elif a.dtype.kind == 'f':
        with np.errstate(all='ignore'):
            if a.dtype == np.float16:
                v = (col.astype(np.float32) / al.astype(np.float32)).astype(np.float16)
            else:
                v = col / al
        a[:, :, :-1] = np.where((al == 0) | (al == 1), col, v)
    return a


def cmyk_to_rgb(a):
    m = umax(a.dtype)
    k = m - a[:, :, 3:4].astype(np.uint64)
    rgb = (((m - a[:, :, :3].astype(np.uint64)) * k + m // 2) // m).astype(a.dtype)
    return np.concatenate([rgb, a[:, :, 4:5]], axis=2) if a.shape[2] > 4 else rgb


def expected(p):
    """What PixCodec has to return for a page description."""
    a = p['arr']
    if a.ndim == 2:
        a = a[:, :, None]
    a = a.copy()
    bits = p.get('bits', a.dtype.itemsize * 8)
    pm = p.get('photometric', 2 if a.shape[2] >= 3 else 1)
    es = list(p.get('extrasamples', []))
    base = {0: 1, 1: 1, 3: 1, 4: 1, 2: 3, 5: 4, 6: 3}[pm]
    extra = a.shape[2] - base
    alpha = extra == 1 and len(es) >= 1 and es[0] in (1, 2)
    if pm == 3:
        n = 1 << bits
        cm = np.array(p['colormap'], np.uint16).reshape(3, n)
        lut = (cm if cm.max() < 256 else cm >> 8).astype(np.uint8).T
        rgb = lut[a[:, :, 0]]
        if alpha:
            rgb = np.concatenate([rgb, (a[:, :, 1:2].astype(np.uint16) * (255 // (n - 1))).astype(np.uint8)], axis=2)
        return rgb
    if bits < 8:
        a = (a * (255 // ((1 << bits) - 1))).astype(np.uint8)
    if pm == 0 and a.dtype.kind == 'u':
        a[:, :, 0] = umax(a.dtype) - a[:, :, 0]
    if pm == 5:
        a = cmyk_to_rgb(a)
    if alpha and es[0] == 1:
        a = unpremultiply(a)
    return a


# ----------------------------------------------------------------------------------------------- reference decoders

def ref_tifffile(path, page=0):
    with tifffile.TiffFile(path) as t:
        pg = t.pages[page]
        a = pg.asarray()
        if pg.planarconfig == 2 and pg.samplesperpixel > 1:
            a = np.moveaxis(a, 0, -1)
        if a.ndim == 2:
            a = a[:, :, None]
        return a


def ref_cv2(path):
    a = cv2.imread(path, cv2.IMREAD_UNCHANGED)
    if a is None:
        raise ValueError('cv2 cannot read')
    if a.ndim == 2:
        a = a[:, :, None]
    if a.shape[2] == 3:
        a = a[:, :, ::-1]
    elif a.shape[2] == 4:
        a = a[:, :, [2, 1, 0, 3]]
    return a


def same(a, b):
    return a.shape == b.shape and a.dtype == b.dtype and a.tobytes() == b.tobytes()


SUMMARY = []


def verify(name, path, p, page=0):
    """Checks the stored samples of one page against the independent decoders. Returns the names that agreed."""
    raw = p['arr'] if p['arr'].ndim == 3 else p['arr'][:, :, None]
    bits = p.get('bits', raw.dtype.itemsize * 8)
    pm = p.get('photometric', 2 if raw.shape[2] >= 3 else 1)
    ok, failed = [], []
    # tifffile returns the stored samples without any photometric interpretation
    try:
        t = ref_tifffile(path, page)
        if pm == 6 or p.get('zero_bytecounts'):
            pass  # tifffile takes zero byte counts literally (libtiff recovers); YCbCr is checked separately
        else:
            if bits < 8:
                t = t.astype(np.uint8)  # bilevel comes back as bool
            (ok if same(t, raw) else failed).append('tifffile')
    except Exception:
        pass
    # OpenCV: gray / RGB(A) only, no palette, and it applies no inversion for 8+ bit MinIsWhite.
    # libtiff ignores the Predictor tag for PackBits (tifffile honours it, as does PixCodec), so skip that combination.
    libtiff_ok = not (p.get('compression') == PACKBITS and p.get('predictor', 1) != 1)
    simple = page == 0 and bits >= 8 and pm in (1, 2) and raw.shape[2] in (1, 3) and 'extrasamples' not in p
    if libtiff_ok and simple and raw.dtype in (np.uint8, np.uint16):
        r = subprocess.run(['magick', path, '-depth', str(bits), '-endian', 'LSB', ('gray' if raw.shape[2] == 1 else 'rgb') + ':-'],
                           capture_output=True)
        if r.returncode == 0 and len(r.stdout) == raw.nbytes:
            (ok if r.stdout == raw.tobytes() else failed).append('magick')
    if not libtiff_ok or p.get('planar', 1) == 2:
        pass  # OpenCV misreads 16-bit planar files
    elif page == 0 and bits >= 8 and pm in (1, 2) and raw.shape[2] <= 4 and 'extrasamples' not in p or \
            (page == 0 and pm == 2 and raw.shape[2] == 4 and p.get('extrasamples') == [2] and bits >= 16):
        try:
            c = ref_cv2(path)
            (ok if same(c, raw) else failed).append('cv2')
        except Exception:
            pass
    if failed:
        raise AssertionError(f'{name}: reference decoder(s) {failed} disagree with the fixture source')
    return ok


def emit(name, pages, byteorder='<', bigtiff=False, check=None, subdir=None):
    """Writes name.tif + name.tif.ref (first page) [+ name.tif.<n>.ref for further pages]."""
    if isinstance(pages, dict):
        pages = [pages]
    d = OUT if subdir is None else os.path.join(OUT, subdir)
    os.makedirs(d, exist_ok=True)
    path = os.path.join(d, name + '.tif')
    write_tiff(path, pages, byteorder, bigtiff)
    agreed = None
    for i, p in enumerate(pages):
        exp = expected(p)
        ok = verify(name, path, p, i)
        if check is not None and i == 0:
            ok += check(path, exp)
        write_ref(path + ('.ref' if i == 0 else f'.{i}.ref'), exp)
        agreed = ok if agreed is None else agreed
    SUMMARY.append((name, agreed))
    return path


def emit_external(name, path, arr, by):
    """A file written by another tool; arr is what the reference decoder(s) named in `by` returned."""
    write_ref(path + '.ref', arr)
    SUMMARY.append((name, by))


# ----------------------------------------------------------------------------------------------- cross checks

def check_pillow(mode=None, tol=0):
    def f(path, exp):
        im = Image.open(path)
        if mode:
            im = im.convert(mode)
        a = np.asarray(im)
        if a.ndim == 2:
            a = a[:, :, None]
        if a.dtype == bool:
            a = a.astype(np.uint8) * 255
        assert a.shape == exp.shape, (path, a.shape, exp.shape)
        d = np.abs(a.astype(np.int64) - exp.astype(np.int64)).max()
        assert d <= tol, f'{path}: Pillow differs by {d}'
        return [f'pillow(+-{d})' if d else 'pillow']
    return f


def check_magick(fmt, dtype=np.uint8, tol=0):
    """Compares against ImageMagick's interpretation (fmt: gray / rgb / rgba raw output)."""
    def f(path, exp):
        depth = np.dtype(dtype).itemsize * 8
        r = subprocess.run(['magick', path, '-depth', str(depth), '-endian', 'LSB', f'{fmt}:-'], capture_output=True)
        assert r.returncode == 0, r.stderr
        a = np.frombuffer(r.stdout, dtype).reshape(exp.shape)
        d = np.abs(a.astype(np.int64) - exp.astype(np.int64)).max()
        assert d <= tol, f'{path}: ImageMagick differs by {d}'
        return [f'magick(+-{d})' if d else 'magick']
    return f


def both(*checks):
    def f(path, exp):
        r = []
        for c in checks:
            r += c(path, exp)
        return r
    return f


# ----------------------------------------------------------------------------------------------- fixtures

W, H = 23, 17
DTYPES = ['uint8', 'uint16', 'uint32', 'int8', 'int16', 'int32', 'float16', 'float32', 'float64']
SHORT = {'uint8': 'u8', 'uint16': 'u16', 'uint32': 'u32', 'int8': 'i8', 'int16': 'i16', 'int32': 'i32',
         'float16': 'f16', 'float32': 'f32', 'float64': 'f64'}
CNAME = {NONE: 'none', LZW: 'lzw', DEFLATE: 'deflate', PACKBITS: 'packbits'}


def pat(c, dt, w=W, h=H, seed=1):
    a = test_pattern(w, h, c, np.dtype(dt), seed)
    if np.dtype(dt).kind == 'f':
        # elevation-like smooth data plus specials
        a = (a * 100).astype(dt)
        a[0, 0, 0] = np.nan
        if a.size > 6:
            a[0, 1, 0] = np.inf
            a[1, 0, 0] = -0.0
    return a


def gen_matrix():
    # every sample type, gray and RGB, uncompressed, several strips
    for dt in DTYPES:
        for c in (1, 3):
            emit(f'{SHORT[dt]}_{"gray" if c == 1 else "rgb"}_none', dict(arr=pat(c, dt), rps=5))
    # compression x predictor
    for comp in (LZW, DEFLATE, PACKBITS):
        cn = CNAME[comp]
        emit(f'u8_rgb_{cn}', dict(arr=pat(3, 'uint8'), compression=comp, rps=6))
        emit(f'u8_gray_{cn}_pred2', dict(arr=pat(1, 'uint8'), compression=comp, predictor=2, rps=4))
        emit(f'u8_rgb_{cn}_pred2', dict(arr=pat(3, 'uint8'), compression=comp, predictor=2, rps=7))
        emit(f'u8_rgba_{cn}_pred2', dict(arr=pat(4, 'uint8'), compression=comp, predictor=2, extrasamples=[2]))
        emit(f'u16_rgb_{cn}_pred2', dict(arr=pat(3, 'uint16'), compression=comp, predictor=2, rps=3))
        emit(f'u16_gray_{cn}', dict(arr=pat(1, 'uint16'), compression=comp))
        emit(f'i16_gray_{cn}_pred2', dict(arr=pat(1, 'int16'), compression=comp, predictor=2, rps=8))
        emit(f'u32_gray_{cn}_pred2', dict(arr=pat(1, 'uint32'), compression=comp, predictor=2))
        emit(f'i32_rgb_{cn}_pred2', dict(arr=pat(3, 'int32'), compression=comp, predictor=2))
        emit(f'i8_rgb_{cn}', dict(arr=pat(3, 'int8'), compression=comp))
        emit(f'f32_gray_{cn}', dict(arr=pat(1, 'float32'), compression=comp, rps=5))
        emit(f'f32_gray_{cn}_pred3', dict(arr=pat(1, 'float32'), compression=comp, predictor=3, rps=5))
        emit(f'f32_rgb_{cn}_pred3', dict(arr=pat(3, 'float32'), compression=comp, predictor=3))
        emit(f'f64_gray_{cn}_pred3', dict(arr=pat(1, 'float64'), compression=comp, predictor=3, rps=9))
        emit(f'f16_rgb_{cn}_pred3', dict(arr=pat(3, 'float16'), compression=comp, predictor=3))
        emit(f'f32_gray_{cn}_pred2', dict(arr=pat(1, 'float32'), compression=comp, predictor=2))
        emit(f'f64_rgb_{cn}_pred2', dict(arr=pat(3, 'float64'), compression=comp, predictor=2))
    emit('u8_rgb_deflate_old32946', dict(arr=pat(3, 'uint8'), compression=32946, predictor=2))

    # big endian
    for dt, comp, pred in [('uint8', LZW, 2), ('uint16', NONE, 1), ('uint16', LZW, 2), ('uint32', DEFLATE, 2), ('int16', PACKBITS, 1),
                           ('int32', NONE, 1), ('float16', NONE, 1), ('float32', DEFLATE, 3), ('float32', LZW, 2),
                           ('float64', NONE, 1), ('float64', LZW, 3), ('float16', DEFLATE, 3)]:
        emit(f'be_{SHORT[dt]}_rgb_{CNAME[comp]}_pred{pred}', dict(arr=pat(3, dt), compression=comp, predictor=pred, rps=6), byteorder='>')
    emit('be_u16_gray_planar_tiled', dict(arr=pat(2, 'uint16', 40, 33), compression=DEFLATE, predictor=2, planar=2, tile=(16, 16),
                                         extrasamples=[2]), byteorder='>')

    # BigTIFF
    emit('big_u16_rgb_deflate_tiled', dict(arr=pat(3, 'uint16', 50, 37), compression=DEFLATE, predictor=2, tile=(16, 32)), bigtiff=True)
    emit('big_be_f32_gray_lzw_pred3', dict(arr=pat(1, 'float32'), compression=LZW, predictor=3, rps=4), byteorder='>', bigtiff=True)
    emit('big_u8_rgb_none', dict(arr=pat(3, 'uint8')), bigtiff=True)

    # tiles (image size deliberately not a multiple of the tile size)
    emit('tiled_u8_rgb_lzw_pred2', dict(arr=pat(3, 'uint8', 50, 37), compression=LZW, predictor=2, tile=(16, 16)))
    emit('tiled_u8_gray_none', dict(arr=pat(1, 'uint8', 50, 37), tile=(32, 16)))
    emit('tiled_f32_gray_deflate_pred3', dict(arr=pat(1, 'float32', 50, 37), compression=DEFLATE, predictor=3, tile=(16, 16)))
    emit('tiled_u16_rgba_packbits', dict(arr=pat(4, 'uint16', 33, 20), compression=PACKBITS, tile=(16, 16), extrasamples=[2]))
    emit('tiled_f64_gray_lzw_pred2', dict(arr=pat(1, 'float64', 33, 20), compression=LZW, predictor=2, tile=(48, 32)))
    emit('tiled_i16_dem_deflate_pred2', dict(arr=pat(1, 'int16', 70, 45), compression=DEFLATE, predictor=2, tile=(32, 32)))

    # planar configuration 2
    emit('planar_u8_rgb_none', dict(arr=pat(3, 'uint8'), planar=2, rps=5))
    emit('planar_u16_rgb_lzw_pred2', dict(arr=pat(3, 'uint16'), planar=2, compression=LZW, predictor=2, rps=6))
    emit('planar_f32_rgb_deflate_pred3', dict(arr=pat(3, 'float32'), planar=2, compression=DEFLATE, predictor=3))
    emit('planar_u8_rgba_packbits', dict(arr=pat(4, 'uint8'), planar=2, compression=PACKBITS, extrasamples=[2]))
    emit('planar_u8_rgb_tiled_lzw', dict(arr=pat(3, 'uint8', 50, 37), planar=2, compression=LZW, tile=(16, 16)))
    emit('planar_u16_5band_deflate', dict(arr=pat(5, 'uint16'), planar=2, compression=DEFLATE, photometric=1, extrasamples=[0] * 4))


def gen_bits_and_photometric():
    rng = np.random.default_rng(7)
    for bits in (1, 2, 4):
        n = 1 << bits
        g = (test_pattern(W, H, 1, np.uint8, bits) >> (8 - bits)).astype(np.uint8)
        emit(f'gray{bits}_minisblack', dict(arr=g, bits=bits, photometric=1, rps=5), check=both(check_pillow('L'), check_magick('gray')))
        # (ImageMagick does not invert 2/4-bit MinIsWhite, Pillow and libtiff's RGBA interface do)
        emit(f'gray{bits}_miniswhite', dict(arr=g, bits=bits, photometric=0),
             check=check_pillow('L') if bits > 1 else both(check_pillow('L'), check_magick('gray')))
        emit(f'gray{bits}_lzw', dict(arr=g, bits=bits, photometric=1, compression=LZW))
        emit(f'gray{bits}_packbits_tiled', dict(arr=np.tile(g, (3, 2, 1))[:37, :41], bits=bits, photometric=1, compression=PACKBITS, tile=(16, 16)))
        cm = rng.integers(0, 65536, 3 * n).tolist()
        emit(f'palette{bits}', dict(arr=g, bits=bits, photometric=3, colormap=cm, rps=6), check=check_pillow('RGB'))
        emit(f'palette{bits}_deflate', dict(arr=g, bits=bits, photometric=3, colormap=cm, compression=DEFLATE), check=check_pillow('RGB'))
    g8 = test_pattern(W, H, 1, np.uint8, 3)
    cm8 = rng.integers(0, 65536, 768).tolist()
    emit('palette8', dict(arr=g8, photometric=3, colormap=cm8), check=check_pillow('RGB'))
    emit('palette8_lzw_tiled', dict(arr=test_pattern(40, 33, 1, np.uint8, 3), photometric=3, colormap=cm8, compression=LZW, tile=(16, 16)),
         check=check_pillow('RGB'))
    # colormap holding 8-bit values (seen in the wild): used as is, like libtiff does
    emit('palette8_8bitmap', dict(arr=g8, photometric=3, colormap=rng.integers(0, 256, 768).tolist()))
    emit('palette8_alpha', dict(arr=np.concatenate([g8, pat(1, 'uint8', seed=5)], axis=2), photometric=3, colormap=cm8, extrasamples=[2]))
    emit('rgb4bit', dict(arr=(test_pattern(W, H, 3, np.uint8, 4) >> 4).astype(np.uint8), bits=4, photometric=2))
    emit('gray1_planar_2ch', dict(arr=(test_pattern(W, H, 2, np.uint8, 4) >> 7).astype(np.uint8), bits=1, photometric=1, planar=2,
                                 extrasamples=[2]))
    b1 = (test_pattern(37, 21, 1, np.uint8, 9) >> 7).astype(np.uint8)
    emit('bilevel_fillorder2', dict(arr=b1, bits=1, photometric=0, fillorder=2), check=both(check_pillow('L'), check_magick('gray')))
    emit('bilevel_fillorder2_packbits', dict(arr=b1, bits=1, photometric=1, fillorder=2, compression=PACKBITS), check=check_magick('gray'))
    emit('bilevel_mask', dict(arr=b1, bits=1, photometric=4))

    emit('u8_miniswhite', dict(arr=pat(1, 'uint8'), photometric=0), check=check_pillow())
    emit('u16_miniswhite_lzw', dict(arr=pat(1, 'uint16'), photometric=0, compression=LZW, predictor=2), check=check_magick('gray', np.uint16))
    emit('u32_miniswhite', dict(arr=pat(1, 'uint32'), photometric=0))
    emit('f32_miniswhite', dict(arr=pat(1, 'float32'), photometric=0))   # not inverted
    emit('i16_miniswhite', dict(arr=pat(1, 'int16'), photometric=0))     # not inverted

    emit('cmyk8', dict(arr=pat(4, 'uint8'), photometric=5), check=check_pillow('RGB', tol=1))
    emit('cmyk8_lzw_planar', dict(arr=pat(4, 'uint8'), photometric=5, compression=LZW, planar=2), check=check_pillow('RGB', tol=1))
    emit('cmyk16_deflate', dict(arr=pat(4, 'uint16'), photometric=5, compression=DEFLATE, predictor=2))
    emit('cmyk8_alpha', dict(arr=pat(5, 'uint8'), photometric=5, extrasamples=[2]))


def premul(c, dt, seed=3):
    """Colour + alpha where colour <= alpha mostly, with fully transparent/opaque areas and a few invalid pixels."""
    a = pat(c, dt, seed=seed).copy()
    if np.dtype(dt).kind == 'f':
        a = np.abs(np.nan_to_num(a, nan=0.5, posinf=1.0)) / 400
        a = a.astype(dt)
        a[:, :, -1] = np.clip(a[:, :, -1] * 3, 0, 1)
        a[:3, :, -1] = 0
        a[3:6, :, -1] = 1
        a[:, :, :-1] = np.minimum(a[:, :, :-1], a[:, :, -1:])
        return a
    m = umax(a.dtype)
    a[:3, :, -1] = 0
    a[3:6, :, -1] = m
    al = a[:, :, -1:].astype(np.uint64)
    a[:, :, :-1] = (a[:, :, :-1].astype(np.uint64) * al // m).astype(dt)
    a[8, 2:5, 0] = m  # colour above alpha: must clamp
    return a


def gen_extras():
    emit('rgba_assoc_u8', dict(arr=premul(4, 'uint8'), extrasamples=[1]), check=check_pillow('RGBA', tol=2))
    emit('rgba_assoc_u16_lzw', dict(arr=premul(4, 'uint16'), extrasamples=[1], compression=LZW, predictor=2))
    emit('rgba_assoc_u32', dict(arr=premul(4, 'uint32'), extrasamples=[1]))
    emit('rgba_assoc_f32', dict(arr=premul(4, 'float32'), extrasamples=[1]))
    emit('rgba_assoc_f16', dict(arr=premul(4, 'float16'), extrasamples=[1]))
    emit('rgba_assoc_f64_planar', dict(arr=premul(4, 'float64'), extrasamples=[1], planar=2))
    emit('graya_assoc_u8', dict(arr=premul(2, 'uint8'), extrasamples=[1]))
    emit('graya_assoc_u16', dict(arr=premul(2, 'uint16'), extrasamples=[1]))
    emit('graya_unassoc_u8', dict(arr=pat(2, 'uint8'), extrasamples=[2]), check=check_pillow())
    emit('graya_unassoc_f32', dict(arr=pat(2, 'float32'), extrasamples=[2]))
    emit('rgba_unassoc_u8', dict(arr=pat(4, 'uint8'), extrasamples=[2]), check=check_pillow())
    emit('rgba_unassoc_u16', dict(arr=pat(4, 'uint16'), extrasamples=[2]))
    # everything below keeps all samples and reports ColorModel.Other
    emit('rgbx_unspecified_u8', dict(arr=pat(4, 'uint8'), extrasamples=[0]))
    emit('rgb_noextratag_4ch_u8', dict(arr=pat(4, 'uint8'), photometric=2))
    emit('rgb_2extras_u8', dict(arr=premul(5, 'uint8'), extrasamples=[1, 0]))
    emit('gray_2extras_u16', dict(arr=pat(3, 'uint16'), photometric=1, extrasamples=[0, 0]))
    emit('gray_1extra_unspecified_f32', dict(arr=pat(2, 'float32'), photometric=1, extrasamples=[0]))
    emit('multiband7_u16_deflate', dict(arr=pat(7, 'uint16'), photometric=1, extrasamples=[0] * 6, compression=DEFLATE, predictor=2))
    emit('multiband5_f32_lzw_pred3', dict(arr=pat(5, 'float32'), photometric=1, extrasamples=[0] * 4, compression=LZW, predictor=3))


def gen_structure():
    rng = np.random.default_rng(11)
    emit('1x1_u8', dict(arr=np.array([[[77]]], np.uint8)))
    emit('1x1_rgb_f32_lzw_pred3', dict(arr=np.array([[[1.5, -2.25, 1e9]]], np.float32), compression=LZW, predictor=3))
    emit('1xN_u16_deflate_pred2', dict(arr=pat(1, 'uint16', 1, 13), compression=DEFLATE, predictor=2, rps=4))
    emit('Nx1_rgb_u8_lzw_pred2', dict(arr=pat(3, 'uint8', 31, 1), compression=LZW, predictor=2))
    emit('rps1_u8_rgb_packbits', dict(arr=pat(3, 'uint8'), compression=PACKBITS, rps=1))
    # noise forces the LZW table to fill up and reset several times, long runs exercise long strings
    noise = rng.integers(0, 256, (64, 64, 3)).astype(np.uint8)
    emit('lzw_table_reset_noise', dict(arr=noise, compression=LZW))
    runs = np.zeros((90, 200, 1), np.uint8)
    runs[30:60] = 200
    runs[:, 50:52] = 17
    emit('lzw_long_runs', dict(arr=runs, compression=LZW))
    emit('lzw_oldstyle_u8_rgb', dict(arr=pat(3, 'uint8', 40, 30), compression=LZW, lzw_old=True, rps=11))
    emit('lzw_oldstyle_noise', dict(arr=noise[:, :, :1], compression=LZW, lzw_old=True))
    emit('packbits_runs', dict(arr=runs, compression=PACKBITS, rps=16))
    emit('no_bytecounts_u8_rgb', dict(arr=pat(3, 'uint8'), no_bytecounts=True))
    emit('no_bytecounts_lzw', dict(arr=pat(3, 'uint8'), compression=LZW, no_bytecounts=True))
    emit('zero_bytecounts_u16_rgb', dict(arr=pat(3, 'uint16'), zero_bytecounts=True, rps=5))
    emit('zero_bytecounts_lzw_pred2', dict(arr=pat(3, 'uint8'), compression=LZW, predictor=2, zero_bytecounts=True, rps=6))
    sp = pat(1, 'float32', 50, 37)
    sp_exp = sp.copy()
    sp_exp[0:16, 16:32] = 0
    sp_exp[32:48, 0:16] = 0
    p = dict(arr=sp, compression=DEFLATE, predictor=3, tile=(16, 16), sparse={1, 8})
    path = os.path.join(OUT, 'sparse_tiles_f32.tif')
    write_tiff(path, [p])
    write_ref(path + '.ref', sp_exp)
    SUMMARY.append(('sparse_tiles_f32', ['by construction (GDAL style sparse tiles)']))

    icc = bytes(rng.integers(0, 256, 300).astype(np.uint8))
    emit('meta_res_icc_orient', dict(arr=pat(3, 'uint8'), tags=[
        (274, 3, [6]), (282, 5, [(300, 1)]), (283, 5, [(1501, 10)]), (296, 3, [2]), (34675, 7, icc)]))
    emit('meta_res_cm', dict(arr=pat(1, 'uint8'), tags=[(282, 5, [(100, 1)]), (283, 5, [(50, 1)]), (296, 3, [3])]))
    with open(os.path.join(OUT, 'meta_res_icc_orient.icc'), 'wb') as f:
        f.write(icc)
    # GeoTIFF style DEM
    emit('geotiff_dem_f32', dict(arr=pat(1, 'float32', 40, 30), compression=DEFLATE, predictor=3, tile=(16, 16), tags=[
        (33550, 12, [0.5, 0.5, 0.0]),
        (33922, 12, [0.0, 0.0, 0.0, 600000.0, 5300000.0, 0.0]),
        (34735, 3, [1, 1, 0, 3, 1024, 0, 1, 1, 1025, 0, 1, 1, 3072, 0, 1, 32633]),
        (42113, 2, '-9999'),
        (305, 2, 'pixcodec fixture')]))
    # multi page: different size, type and compression per page
    emit('multipage', [
        dict(arr=pat(3, 'uint8'), compression=LZW, predictor=2),
        dict(arr=pat(1, 'uint16', 11, 9), compression=DEFLATE),
        dict(arr=pat(1, 'float32', 5, 7)),
        dict(arr=pat(4, 'uint8', 8, 8), extrasamples=[2], compression=PACKBITS, tile=(16, 16)),
    ])
    emit('multipage_big_be', [dict(arr=pat(1, 'int16')), dict(arr=pat(3, 'float64', 6, 4), compression=LZW, predictor=3)],
         byteorder='>', bigtiff=True)


def gen_external():
    """Files written by other libraries (libtiff via Pillow / ImageMagick, tifffile)."""
    tmp = os.path.join(OUT, '_src.tif')
    rgb = pat(3, 'uint8', 45, 31)
    for comp, cn in [('tiff_lzw', 'lzw'), ('packbits', 'packbits'), ('tiff_adobe_deflate', 'deflate')]:
        path = os.path.join(OUT, f'ext_pillow_rgb_{cn}.tif')
        Image.fromarray(rgb).save(path, compression=comp)
        a = ref_cv2(path)
        assert same(a, rgb)
        emit_external(f'ext_pillow_rgb_{cn}', path, a, ['cv2', 'pillow-written'])
    path = os.path.join(OUT, 'ext_pillow_palette_lzw.tif')
    Image.fromarray(rgb).quantize(32).save(path, compression='tiff_lzw')
    emit_external('ext_pillow_palette_lzw', path, np.asarray(Image.open(path).convert('RGB')), ['pillow'])
    path = os.path.join(OUT, 'ext_pillow_i16_lzw_pred.tif')
    g16 = pat(1, 'uint16', 45, 31)
    Image.fromarray(g16[:, :, 0]).save(path, compression='tiff_lzw', tiffinfo={317: 2})
    a = ref_cv2(path)
    assert same(a, g16)
    emit_external('ext_pillow_i16_lzw_pred', path, a, ['cv2', 'pillow-written'])

    # ImageMagick: big endian 16-bit LZW with predictor, tiles, float with the floating point predictor
    tifffile.imwrite(tmp, pat(3, 'uint16', 45, 31), photometric='rgb')
    for name, args in [
        ('ext_magick_u16_rgb_lzw_pred2_msb', ['-endian', 'MSB', '-compress', 'LZW', '-define', 'tiff:predictor=2']),
        ('ext_magick_u16_rgb_zip_tiled', ['-compress', 'Zip', '-define', 'tiff:tile-geometry=16x16', '-define', 'tiff:predictor=2']),
        ('ext_magick_u16_rgb_rle', ['-compress', 'RLE', '-define', 'tiff:rows-per-strip=4']),
        ('ext_magick_f32_rgb_lzw_pred3', ['-define', 'quantum:format=floating-point', '-depth', '32', '-compress', 'LZW',
                                           '-define', 'tiff:predictor=3']),
        ('ext_magick_f64_rgb_zip_pred3_msb', ['-define', 'quantum:format=floating-point', '-depth', '64', '-endian', 'MSB',
                                               '-compress', 'Zip', '-define', 'tiff:predictor=3']),
    ]:
        path = os.path.join(OUT, name + '.tif')
        subprocess.run(['magick', tmp, '-strip'] + args + [path], check=True)
        emit_external(name, path, ref_cv2(path), ['cv2', 'magick-written'])
    os.remove(tmp)

    # tifffile as writer
    for name, arr, kw in [
        ('ext_tifffile_u16_rgb_zlib_pred', pat(3, 'uint16', 45, 31), dict(photometric='rgb', compression='zlib', predictor=True)),
        ('ext_tifffile_f32_tiled_big', pat(1, 'float32', 70, 50)[:, :, 0], dict(tile=(32, 32), bigtiff=True, compression='zlib')),
        ('ext_tifffile_u8_planar_be', pat(3, 'uint8', 45, 31).transpose(2, 0, 1), dict(photometric='rgb', planarconfig='separate', byteorder='>')),
        ('ext_tifffile_i32_gray', pat(1, 'int32', 45, 31)[:, :, 0], dict(rowsperstrip=7)),
        ('ext_tifffile_multiband6_f64', pat(6, 'float64', 20, 10), dict(photometric='minisblack', planarconfig='contig', compression='zlib')),
    ]:
        path = os.path.join(OUT, name + '.tif')
        tifffile.imwrite(path, arr, metadata=None, **kw)
        emit_external(name, path, ref_tifffile(path), ['tifffile'])


def gen_ycbcr():
    """YCbCr without JPEG: stored as packed blocks of (SubH * SubV luma samples, Cb, Cr). Built by hand since no
    installed tool writes it; the reference is libtiff's conversion (TIFFReadRGBAImage via OpenCV)."""
    def build(name, w, h, sh, sv, comp, rps):
        rgb = test_pattern(w, h, 3, np.uint8, 21).astype(np.float64)
        y = 0.299 * rgb[:, :, 0] + 0.587 * rgb[:, :, 1] + 0.114 * rgb[:, :, 2]
        cb = (rgb[:, :, 2] - y) / 1.772 + 128
        cr = (rgb[:, :, 0] - y) / 1.402 + 128
        ph, pw = -(-h // sv) * sv, -(-w // sh) * sh
        pad = lambda a: np.pad(a, ((0, ph - h), (0, pw - w)), mode='edge')
        y, cb, cr = pad(y), pad(cb), pad(cr)
        q = lambda a: np.clip(np.round(a), 0, 255).astype(np.uint8)
        cbs = q(cb.reshape(ph // sv, sv, pw // sh, sh).mean(axis=(1, 3)))
        crs = q(cr.reshape(ph // sv, sv, pw // sh, sh).mean(axis=(1, 3)))
        yb = q(y).reshape(ph // sv, sv, pw // sh, sh).transpose(0, 2, 1, 3).reshape(ph // sv, pw // sh, sv * sh)
        blocks = np.concatenate([yb, cbs[:, :, None], crs[:, :, None]], axis=2)   # (by, bx, sh*sv+2)
        chunks = []
        for r in range(0, h, rps):
            raw = blocks[r // sv:-(-min(r + rps, h) // sv)].tobytes()
            chunks.append(lzw_encode(raw) if comp == LZW else zlib.compress(raw) if comp == DEFLATE else raw)
        d = os.path.join(OUT, 'ycbcr')
        os.makedirs(d, exist_ok=True)
        path = os.path.join(d, name + '.tif')
        write_tiff(path, [dict(arr=np.zeros((h, w, 3), np.uint8), photometric=6, compression=comp, rps=rps, raw_chunks=chunks, tags=[
            (530, 3, [sh, sv]), (532, 5, [(0, 1), (255, 1), (128, 1), (255, 1), (128, 1), (255, 1)])])])
        ref = ref_cv2(path)
        # (ImageMagick misreads subsampled non-JPEG YCbCr, so the sanity check is the distance to the RGB source)
        src = int(np.abs(ref.astype(int) - rgb[:h, :w].astype(int)).max())
        assert src < 40, (name, src)
        write_ref(path + '.ref', ref)
        SUMMARY.append(('ycbcr/' + name, ['cv2 (libtiff RGBA)', f'max error vs. the RGB source {src}']))

    build('ycbcr_2x2_none', 24, 16, 2, 2, NONE, 16)
    build('ycbcr_2x2_odd_lzw', 23, 17, 2, 2, LZW, 8)
    build('ycbcr_2x1_deflate', 23, 17, 2, 1, DEFLATE, 5)
    build('ycbcr_1x1_none', 23, 17, 1, 1, NONE, 6)
    build('ycbcr_4x2_none', 30, 13, 4, 2, NONE, 4)
    build('ycbcr_1x2_lzw', 21, 15, 1, 2, LZW, 15)


def gen_jpeg():
    """JPEG-in-TIFF (compression 7). Written by libtiff (through Pillow and ImageMagick), plus complete baseline
    4:2:0 JPEG streams wrapped into strips/tiles by hand (no JPEGTables). References from libtiff/libjpeg via
    Pillow. Kept in their own folder: PixCodec decodes them through the Tiff.JpegDecoder hook only."""
    d = os.path.join(OUT, 'jpeg')
    os.makedirs(d, exist_ok=True)
    rgb = test_pattern(70, 52, 3, np.uint8, 31)
    gray = test_pattern(70, 52, 1, np.uint8, 32)[:, :, 0]
    src = os.path.join(d, '_src.png')
    Image.fromarray(rgb).save(src)

    def jpeg_bytes(a):
        b = io.BytesIO()
        Image.fromarray(a).save(b, 'JPEG', quality=90, subsampling=2)
        return b.getvalue()

    def wrapped(path, tile):
        if tile:
            chunks = []
            for y in range(0, 52, tile):
                for x in range(0, 70, tile):
                    blk = rgb[y:y + tile, x:x + tile]
                    blk = np.pad(blk, ((0, tile - blk.shape[0]), (0, tile - blk.shape[1]), (0, 0)), mode='edge')
                    chunks.append(jpeg_bytes(blk))
            p = dict(tile=(tile, tile))
        else:
            chunks = [jpeg_bytes(rgb[y:y + 16]) for y in range(0, 52, 16)]
            p = dict(rps=16)
        write_tiff(path, [dict(arr=np.zeros((52, 70, 3), np.uint8), photometric=6, compression=7, raw_chunks=chunks,
                               tags=[(530, 3, [2, 2])], **p)])

    cases = [
        ('jpeg_rgb_strips', lambda p: Image.fromarray(rgb).save(p, compression='jpeg', quality=90)),
        ('jpeg_rgb_multistrip', lambda p: Image.fromarray(rgb).save(p, compression='jpeg', quality=90, tiffinfo={278: 16})),
        ('jpeg_gray_strips', lambda p: Image.fromarray(gray).save(p, compression='jpeg', quality=90, tiffinfo={278: 24})),
        ('jpeg_rgb_tiles', lambda p: subprocess.run(['magick', src, '-compress', 'JPEG', '-quality', '90',
                                                     '-define', 'tiff:tile-geometry=32x32', p], check=True)),
        ('jpeg_ycbcr_strips', lambda p: subprocess.run(['magick', src, '-colorspace', 'YCbCr', '-compress', 'JPEG', '-quality', '90',
                                                        '-define', 'tiff:rows-per-strip=16', p], check=True)),
        ('jpeg_ycbcr_tiles', lambda p: subprocess.run(['magick', src, '-colorspace', 'YCbCr', '-compress', 'JPEG', '-quality', '90',
                                                       '-define', 'tiff:tile-geometry=48x32', p], check=True)),
        ('jpeg_ycbcr_420_wrapped_strips', lambda p: wrapped(p, 0)),
        ('jpeg_ycbcr_420_wrapped_tiles', lambda p: wrapped(p, 32)),
    ]
    for name, make in cases:
        path = os.path.join(d, name + '.tif')
        make(path)
        with tifffile.TiffFile(path) as t:
            pg = t.pages[0]
            desc = f'photometric {int(pg.photometric)}, {"tiles" if pg.is_tiled else "strips"} x{len(pg.dataoffsets)}' \
                   f'{", JPEGTables" if 347 in pg.tags else ""}'
            want = 6 if 'ycbcr' in name else 2 if 'rgb' in name else 1
            assert int(pg.photometric) == want, (name, pg.photometric)
            assert int(pg.compression) == 7
        ref = np.asarray(Image.open(path).convert('L' if want == 1 else 'RGB'))
        orig = gray if want == 1 else rgb
        err = float(np.abs(ref.astype(int) - orig.astype(int)).mean())
        assert err < 15, (name, err)  # the reference really is the picture (4:2:0 at q90 alone costs ~12 on this pattern)
        write_ref(path + '.ref', ref)
        SUMMARY.append(('jpeg/' + name, [f'pillow (libtiff/libjpeg): {desc}, mean error vs. source {err:.2f}']))
    os.remove(src)


def gen_ccitt():
    """Bilevel fax compressions, written by libtiff (Pillow / ImageMagick), references decoded by libtiff (Pillow)."""
    rng = np.random.default_rng(5)

    def runs_image(w, h, max_run):
        # random run lengths, rows partly derived from the previous one to exercise the vertical / pass modes
        a = np.zeros((h, w), bool)
        for y in range(h):
            if y and rng.random() < 0.6:
                row = np.roll(a[y - 1], int(rng.integers(-3, 4)))
                for _ in range(int(rng.integers(0, 4))):
                    x = int(rng.integers(0, w))
                    row[x:x + int(rng.integers(1, 40))] ^= True
                a[y] = row
                continue
            x, v = 0, rng.random() < 0.3
            while x < w:
                r = int(rng.integers(0, max_run)) if rng.random() < 0.8 else int(rng.integers(0, 70))
                a[y, x:x + r] = v
                x += r
                v = not v
        return a

    text = test_pattern(203, 61, 1, np.uint8, 8)[:, :, 0] > 140
    wide = runs_image(2700, 10, 2700)        # runs beyond 2560 need several make-up codes
    busy = runs_image(331, 50, 40)
    cases = [
        ('ccitt_g4_text', text, 'group4', {}),
        ('ccitt_g4_wide_runs', wide, 'group4', {}),
        ('ccitt_g4_busy_multistrip', busy, 'group4', {278: 16}),
        ('ccitt_g4_white', np.zeros((9, 50), bool), 'group4', {}),
        ('ccitt_g4_black', np.ones((9, 50), bool), 'group4', {}),
        ('ccitt_g4_1x1', np.ones((1, 1), bool), 'group4', {}),
        ('ccitt_g4_miniswhite', text, 'group4', {262: 0}),
        ('ccitt_g3_2d_miniswhite', busy, 'group3', {262: 0, 292: 1}),
        ('ccitt_g3_1d_text', text, 'group3', {}),
        ('ccitt_g3_1d_wide_runs', wide, 'group3', {278: 5}),
        ('ccitt_g3_2d_busy', busy, 'group3', {292: 1}),
        ('ccitt_g3_2d_fill_text', text, 'group3', {292: 5, 278: 20}),
        ('ccitt_g3_1d_fill_busy', busy, 'group3', {292: 4}),
        ('ccitt_rle_text', text, 'tiff_ccitt', {}),
        ('ccitt_rle_wide_runs', wide, 'tiff_ccitt', {278: 7}),
        ('ccitt_rle_busy', busy, 'tiff_ccitt', {}),
    ]
    for name, a, comp, info in cases:
        path = os.path.join(OUT, name + '.tif')
        Image.fromarray(a).save(path, compression=comp, tiffinfo=info)
        with tifffile.TiffFile(path) as t:
            pg = t.pages[0]
            assert int(pg.compression) == {'group4': 4, 'group3': 3, 'tiff_ccitt': 2}[comp], (name, pg.compression)
            if 292 in info:
                assert pg.tags[292].value == info[292], (name, pg.tags[292].value)
            desc = f'photometric {int(pg.photometric)}, {len(pg.dataoffsets)} strip(s)'
        ref = np.asarray(Image.open(path).convert('L'))
        assert np.array_equal(ref > 0, a), name
        emit_external(name, path, ref, [f'pillow (libtiff), {desc}'])
    # ImageMagick: MinIsBlack photometric / LSB fill order variants
    src = os.path.join(OUT, '_src.png')
    Image.fromarray(busy).save(src)
    for name, args in [('ccitt_g4_magick', ['-compress', 'Group4']),
                       ('ccitt_g4_magick_lsb', ['-compress', 'Group4', '-define', 'tiff:fill-order=lsb']),
                       ('ccitt_g3_magick_lsb', ['-compress', 'Fax', '-define', 'tiff:fill-order=lsb', '-define', 'tiff:rows-per-strip=32'])]:
        path = os.path.join(OUT, name + '.tif')
        subprocess.run(['magick', src] + args + [path], check=True)
        with tifffile.TiffFile(path) as t:
            pg = t.pages[0]
            desc = f'compression {int(pg.compression)}, photometric {int(pg.photometric)}, fillorder {int(pg.fillorder)}, {len(pg.dataoffsets)} strip(s)'
        ref = np.asarray(Image.open(path).convert('L'))
        assert np.array_equal(ref > 0, busy), name
        emit_external(name, path, ref, [f'pillow (libtiff), magick-written: {desc}'])
    os.remove(src)


def main():
    if os.path.isdir(OUT):
        for f in os.listdir(OUT):
            full = os.path.join(OUT, f)
            if os.path.isfile(full):
                os.remove(full)
            elif f in ('ycbcr', 'jpeg', 'ccitt'):
                shutil.rmtree(full)
    os.makedirs(OUT, exist_ok=True)
    gen_matrix()
    gen_bits_and_photometric()
    gen_extras()
    gen_structure()
    gen_external()
    gen_ccitt()
    gen_ycbcr()
    gen_jpeg()
    unverified = [n for n, by in SUMMARY if not by]
    for n, by in SUMMARY:
        print(f'{n:45s} {", ".join(by) if by else "by construction"}')
    size = sum(os.path.getsize(os.path.join(dp, f)) for dp, _, fs in os.walk(OUT) for f in fs)
    print(f'\n{len(SUMMARY)} fixtures, {len(unverified)} without an independent reference decoder, {size / 1024:.0f} KB total')


if __name__ == '__main__':
    main()
