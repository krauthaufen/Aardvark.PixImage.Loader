"""Generates the TIFF fixtures (tests/fixtures/tiff) and their PXRF reference sidecars.

usage: python3 gen_tiff.py            (from tests/oracle; deterministic, overwrites the fixture directory)

Needs tifffile + imagecodecs, OpenCV, Pillow, ImageMagick, the system libtiff (with jbigkit and liblerc), rasterio
(GDAL) and FFmpeg. Many files come from the small TIFF writer below because no single tool produces the whole
matrix (odd bit depths with compression, old-style LZW and JPEG, sparse tiles, hand-packed YCbCr / LogL, JPEG 2000
codestreams and JBIG entities as strips ...); others are written by tifffile, libtiff (through ctypes, Pillow,
OpenCV), GDAL and ImageMagick.

Provenance rule: every .ref is what a THIRD-PARTY decoder returned for the file - tifffile + imagecodecs for the
stored samples (OpenJPEG for JPEG 2000), libtiff (OpenCV / Pillow / ImageMagick) where it interprets the data (YCbCr,
CCITT, JPEG, LogLuv, JBIG), GDAL for LERC and its masks, FFmpeg for the CCITT uncompressed mode, LittleCMS for Lab -
and all decoders that can read a file have to agree with the pixels it was built from. On top
of the decoded samples only the conversions Aardvark.PixImage.IO documents are applied (MinIsWhite inversion, 1/2/4-bit
scaling, palette expansion, naive CMYK, un-premultiplied alpha), each cross-checked against Pillow / ImageMagick
where those implement the same thing. A `.tol` sidecar gives the allowed difference where bit exactness is not
defined (float colour maths, lossy WebP).

The few variants that no installed decoder reads go to the `unverified` folder with a reference derived from the
source data; the script lists them at the end.
"""
import io
import os
import shutil
import struct
import subprocess
import sys
import warnings
import zlib

import imagecodecs
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

# ----------------------------------------------------------------------------------------------- libtiff, directly

class LibTiff:
    """The system libtiff through ctypes: the only way to write (and read back in every user format) the codecs
    that live in libtiff alone - SGI LogLuv / LogL, PixarLog."""
    SGILOGDATAFMT, PIXARLOGDATAFMT = 65560, 65549

    def __init__(self):
        import ctypes
        self.c = ctypes
        lt = self.lt = ctypes.CDLL('libtiff.so.6')
        lt.TIFFOpen.restype = ctypes.c_void_p
        lt.TIFFOpen.argtypes = [ctypes.c_char_p, ctypes.c_char_p]
        for n in ('TIFFWriteEncodedStrip', 'TIFFReadEncodedStrip'):
            getattr(lt, n).argtypes = [ctypes.c_void_p, ctypes.c_uint32, ctypes.c_void_p, ctypes.c_int64]
            getattr(lt, n).restype = ctypes.c_int64
        lt.TIFFClose.argtypes = [ctypes.c_void_p]
        lt.TIFFStripSize64.argtypes = [ctypes.c_void_p]
        lt.TIFFStripSize64.restype = ctypes.c_uint64
        lt.TIFFNumberOfStrips.argtypes = [ctypes.c_void_p]
        lt.TIFFNumberOfStrips.restype = ctypes.c_uint32

    def _set(self, t, tag, value):
        return self.lt.TIFFSetField(self.c.c_void_p(t), self.c.c_uint32(tag), self.c.c_int(value))

    def write(self, path, arr, compression, photometric, pseudo=(), rps=None, big_endian=False, tags=()):
        """arr: (H,W,C) in the user format the pseudo tag selects (floats for the log codecs)."""
        h, w, c = arr.shape
        t = self.lt.TIFFOpen(path.encode(), b'wb' if big_endian else b'wl')
        assert t
        for tag, v in ((256, w), (257, h), (277, c), (259, compression), (262, photometric), (284, 1),
                       (258, arr.dtype.itemsize * 8), (339, {'u': 1, 'i': 2, 'f': 3}[arr.dtype.kind])) + tuple(tags):
            if tag == 338:   # ExtraSamples takes a count and an array
                one = (self.c.c_uint16 * 1)(v)
                assert self.lt.TIFFSetField(self.c.c_void_p(t), self.c.c_uint32(tag), self.c.c_int(1), one)
            else:
                self._set(t, tag, v)
        for tag, v in pseudo:
            assert self._set(t, tag, v), tag
        rps = rps or h
        self._set(t, 278, rps)
        for i, y in enumerate(range(0, h, rps)):
            buf = np.ascontiguousarray(arr[y:y + rps])
            n = self.lt.TIFFWriteEncodedStrip(t, i, buf.ctypes.data, buf.nbytes)
            assert n == buf.nbytes, (path, n, buf.nbytes)
        self.lt.TIFFClose(t)

    def read(self, path, dtype, shape, pseudo=()):
        """Decoded strips in the user format the pseudo tag selects (default: what the file's tags say)."""
        t = self.lt.TIFFOpen(path.encode(), b'r')
        assert t
        for tag, v in pseudo:
            assert self._set(t, tag, v), tag
        out = np.zeros(shape, dtype)
        flat = out.reshape(-1)
        pos = 0
        for i in range(self.lt.TIFFNumberOfStrips(t)):
            n = self.lt.TIFFReadEncodedStrip(t, i, flat.ctypes.data + pos, min(flat.nbytes - pos, self.lt.TIFFStripSize64(t)))
            assert n > 0, (path, n)
            pos += n
        self.lt.TIFFClose(t)
        assert pos == flat.nbytes, (path, pos, flat.nbytes)
        return out


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


def pack_bits_rows(a, widths):
    """a: (rows, n) integers, widths: bit width of each of the n columns -> rows of MSB-first packed samples,
    each row padded to a byte. Negative values are stored in two's complement."""
    rows, n = a.shape
    cols = []
    for j in range(n):
        w = widths[j]
        v = a[:, j].astype(np.int64).astype(np.uint64) & np.uint64((1 << w) - 1)
        cols.append(((v[:, None] >> np.arange(w - 1, -1, -1, dtype=np.uint64)) & np.uint64(1)).astype(np.uint8))
    return np.packbits(np.concatenate(cols, axis=1), axis=1)


def sample_bits(p, a, c):
    """Per sample bit widths of a page description."""
    bits = p.get('bits', 24 if p.get('float24') else a.dtype.itemsize * 8)
    return list(bits) if isinstance(bits, (list, tuple)) else [bits] * c


def encode_chunk(a, p, e, plane=None):
    """a: (rows, cols, samples) samples of one strip/tile (one plane when planar) -> compressed bytes."""
    rows, cols, s = a.shape
    pred = p.get('predictor', 1)
    comp = p.get('compression', NONE)
    if comp == 50001:
        return imagecodecs.webp_encode(a, lossless=p.get('webp_lossless', True), level=p.get('webp_level', 75))
    widths = sample_bits(p, a, p['arr'].shape[2] if p['arr'].ndim == 3 else 1)
    widths = [widths[plane]] if plane is not None else widths
    if p.get('float24'):
        # three bytes per sample in file byte order; the predictor works on big endian byte planes like for all floats
        b = np.frombuffer(imagecodecs.float24_encode(np.ascontiguousarray(a, np.float32), byteorder='>'), np.uint8).reshape(rows, cols * s, 3)
        row_bytes = cols * s * 3
        if pred == 3:
            b = np.ascontiguousarray(b.transpose(0, 2, 1)).reshape(rows, -1).copy()
            b[:, s:] -= b[:, :-s].copy()
        else:
            if pred == 2:
                # horizontal differencing of the samples taken as 24-bit integers
                v = (b[:, :, 0].astype(np.int64) << 16 | b[:, :, 1].astype(np.int64) << 8 | b[:, :, 2]).reshape(rows, cols, s)
                v[:, 1:, :] = (v[:, 1:, :] - v[:, :-1, :]) & 0xFFFFFF
                v = v.reshape(rows, cols * s)
                b = np.stack([v >> 16, v >> 8 & 255, v & 255], axis=-1).astype(np.uint8)
            if e == '<':
                b = b[:, :, ::-1]
        raw = np.ascontiguousarray(b).tobytes()
    elif any(w not in (8, 16, 32, 64) for w in widths) or len(set(widths)) > 1:
        assert pred in (1, 2)
        if pred == 2:
            # differences between neighbouring pixels, modulo 2^bits per sample
            v = a.astype(np.int64).astype(np.uint64)
            m = np.array([(1 << w) - 1 for w in widths], np.uint64)
            v[:, 1:, :] = (v[:, 1:, :] - v[:, :-1, :]) & m
            a = v
        raw = pack_bits_rows(a.reshape(rows, cols * s), widths * cols)
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
    elif comp == 50000:
        raw = imagecodecs.zstd_encode(raw)
    elif comp == 34925:
        raw = imagecodecs.lzma_encode(raw)
    elif comp != NONE:
        raise ValueError(comp)
    if p.get('fillorder', 1) == 2:
        raw = raw.translate(REV)
    return raw


def write_tiff(path, pages, byteorder='<', bigtiff=False):
    """pages: list of dicts with 'arr' (H,W,C) and options:
    bits, compression, predictor, planar(1|2), tile(tw,th), rps, photometric, extrasamples, colormap,
    fillorder, lzw_old, sampleformat, tags [(id, type, values)], no_bytecounts, zero_bytecounts, sparse (set of chunk indices),
    raw_chunks (pre-encoded strips, used for hand-built YCbCr / JPEG data), prefix (bytes placed right after the
    header, for tags holding absolute offsets), strips ((offsets, counts) overriding the strip tags)."""
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
        bits = sample_bits(p, a, c)
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
                        chunks.append(encode_chunk(np.ascontiguousarray(blk), p, e, plane))
        offsets, counts = [], []
        f.write(p.get('prefix', b''))   # data that tags point to by absolute offset (written right after the header)
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
        if 'strips' in p:
            offsets, counts = p['strips']   # strips that live inside the prefix

        kind = a.dtype.kind
        tags = [(256, 4, [w]), (257, 4, [h]), (258, 3, bits), (259, 3, [p.get('compression', NONE)]),
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


def unpremultiply(a, m=None):
    """c = round(c * max / alpha) clamped; alpha 0 leaves the colour as stored. m: largest sample value."""
    a = a.copy()
    al = a[:, :, -1:]
    col = a[:, :, :-1]
    if a.dtype.kind == 'u':
        m = umax(a.dtype) if m is None else m
        al_o = al.astype(object)
        safe = np.maximum(al_o, 1)
        v = np.minimum((col.astype(object) * m + al_o // 2) // safe, m)   # python ints: exact for 64 bit too
        a[:, :, :-1] = np.where(al_o == 0, col, v).astype(a.dtype)
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


BASE_SAMPLES = {0: 1, 1: 1, 3: 1, 4: 1, 2: 3, 5: 4, 6: 3, 8: 3, 9: 3, 10: 3, 32803: 1, 34892: 3, 32844: 1, 32845: 3}


def expected(p, raw, scaling='default', convert=True):
    """What Aardvark.PixImage.IO has to return, computed from the samples `raw` a third-party decoder returned for the page.
    scaling: TiffBitScaling (default / none / full); convert: TiffDecodeOptions.ConvertToRgb."""
    a = raw if raw.ndim == 3 else raw[:, :, None]
    a = a.copy()
    widths = sample_bits(p, a, a.shape[2])
    bits = max(widths)
    uniform = len(set(widths)) == 1
    pm = p.get('photometric', 2 if a.shape[2] >= 3 else 1)
    es = list(p.get('extrasamples', []))
    extra = a.shape[2] - BASE_SAMPLES[pm]
    alpha = extra == 1 and len(es) >= 1 and es[0] in (1, 2)
    if pm == 3:
        n = 1 << bits
        cm = np.array(p['colormap'], np.uint16).reshape(3, n)
        lut = (cm if cm.max() < 256 else cm >> 8).astype(np.uint8).T
        rgb = lut[a[:, :, 0]]
        if alpha:
            rgb = np.concatenate([rgb, ((a[:, :, 1:2].astype(np.uint32) * 255 + (n - 1) // 2) // (n - 1)).astype(np.uint8)], axis=2)
        return rgb
    smax = umax(a.dtype) if a.dtype.kind == 'u' else None
    if a.dtype.kind == 'u' and any(w not in (8, 16, 32, 64) for w in widths):
        if scaling == 'full' or (scaling == 'default' and uniform and bits in (1, 2, 4)):
            o = a.astype(np.uint64)
            for c, w in enumerate(widths):
                im = (1 << w) - 1
                o[:, :, c] = (o[:, :, c] * smax + im // 2) // im
            a = o.astype(a.dtype)
        elif uniform:
            smax = (1 << bits) - 1
    if pm == 0 and a.dtype.kind == 'u':
        a[:, :, 0] = smax - a[:, :, 0]
    if pm == 5 and convert:
        a = cmyk_to_rgb(a)
    if alpha and es[0] == 1 and (convert or pm not in (5, 8, 9, 10)):
        a = unpremultiply(a, smax)
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
        if a.dtype == bool:
            a = a.astype(np.uint8)
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
    return np.ascontiguousarray(a)


def same(a, b):
    return a.shape == b.shape and a.dtype == b.dtype and a.tobytes() == b.tobytes()


SUMMARY = []
UNVERIFIED = []


def third_party(name, path, p, page=0):
    """Decodes the stored samples of one page with every independent decoder that can read it. All of them have to
    agree with the fixture source. Returns (samples as decoded by a third party or None, names of the decoders)."""
    src = p.get('expect_raw', p['arr'])
    src = src if src.ndim == 3 else src[:, :, None]
    widths = sample_bits(p, src, src.shape[2])
    whole = all(w in (8, 16, 32, 64) for w in widths)
    pm = p.get('photometric', 2 if src.shape[2] >= 3 else 1)
    ok, failed, got = [], [], None

    def note(who, arr):
        nonlocal got
        if same(arr, src):
            ok.append(who)
            got = arr if got is None else got
        else:
            failed.append(who)

    # tifffile (+ imagecodecs) returns the stored samples without any photometric interpretation
    if 'raw_decode' in p:
        who, arr = p['raw_decode'](path)
        note(who, arr)
    elif pm != 6 and not p.get('zero_bytecounts'):
        # (tifffile takes zero byte counts literally where libtiff recovers; YCbCr is checked separately)
        try:
            t = ref_tifffile(path, page)
        except Exception:
            t = None
        if t is not None and t.size:
            who = 'tifffile'
            if p.get('predictor', 1) == 2 and not whole and t.dtype.kind == 'u' and len(set(widths)) == 1:
                # tifffile undoes the predictor on the unpacked integers without wrapping at the sample depth:
                # its sums are right modulo 2^bits
                t = t & t.dtype.type((1 << widths[0]) - 1)
                who = 'tifffile (masked to the sample depth)'
            note(who, t)
    # libtiff ignores the Predictor tag for PackBits (tifffile honours it, as does Aardvark.PixImage.IO), so skip that combination.
    libtiff_ok = not (p.get('compression') == PACKBITS and p.get('predictor', 1) != 1) and p.get('compression', 1) < 33003
    simple = page == 0 and whole and pm in (1, 2) and src.shape[2] in (1, 3) and 'extrasamples' not in p
    if libtiff_ok and simple and src.dtype in (np.uint8, np.uint16):
        r = subprocess.run(['magick', path, '-depth', str(widths[0]), '-endian', 'LSB', ('gray' if src.shape[2] == 1 else 'rgb') + ':-'],
                           capture_output=True)
        if r.returncode == 0 and len(r.stdout) == src.nbytes:
            note('magick', np.frombuffer(r.stdout, src.dtype).reshape(src.shape))
    if not libtiff_ok or p.get('planar', 1) == 2:
        pass  # OpenCV misreads 16-bit planar files
    elif page == 0 and whole and pm in (1, 2) and src.shape[2] <= 4 and 'extrasamples' not in p or \
            (page == 0 and pm == 2 and src.shape[2] == 4 and p.get('extrasamples') == [2] and whole and widths[0] >= 16):
        # OpenCV: gray / RGB(A) only, no palette, and it applies no inversion for 8+ bit MinIsWhite
        try:
            note('cv2', ref_cv2(path))
        except Exception:
            pass
    if failed:
        raise AssertionError(f'{name}: reference decoder(s) {failed} disagree with the fixture source')
    return got, ok


def emit(name, pages, byteorder='<', bigtiff=False, check=None, subdir=None, variants=()):
    """Writes name.tif + name.tif.ref (first page) [+ name.tif.<n>.ref for further pages]. The .ref is computed from
    what a third-party decoder returned. A page marked `unverified='reason'` is expected to be unreadable for all of
    them: it goes to the `unverified` folder with a reference derived from the source array and is listed.
    variants: extra references name.tif.<variant>.ref for decode options ('none' / 'full' bit scaling, 'raw')."""
    if isinstance(pages, dict):
        pages = [pages]
    reason = pages[0].get('unverified')
    if reason:
        subdir = 'unverified'
    d = OUT if subdir is None else os.path.join(OUT, subdir)
    os.makedirs(d, exist_ok=True)
    path = os.path.join(d, name + '.tif')
    write_tiff(path, pages, byteorder, bigtiff)
    agreed = None
    for i, p in enumerate(pages):
        raw, ok = third_party(name, path, p, i)
        if raw is None:
            if not reason:
                raise AssertionError(f'{name}: no third-party decoder can read page {i}')
            raw = p['arr']
        elif reason:
            raise AssertionError(f'{name}: marked unverified but {ok} read it')
        exp = p['ref_from_raw'](raw) if 'ref_from_raw' in p else expected(p, raw)
        if check is not None and i == 0:
            ok += check(path, exp)
        write_ref(path + ('.ref' if i == 0 else f'.{i}.ref'), exp)
        for v in variants:
            write_ref(path + f'.{v}.ref', expected(p, raw, convert=False) if v == 'raw' else expected(p, raw, scaling=v))
        agreed = ok if agreed is None else agreed
    if reason:
        UNVERIFIED.append((name, reason))
    SUMMARY.append(((subdir + '/' if subdir else '') + name, agreed if not reason else ['UNVERIFIED: ' + reason]))
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
    if np.dtype(dt).itemsize == 8 and np.dtype(dt).kind in 'ui':
        # 64-bit integers: two 32-bit patterns glued together (float based patterns cannot fill 64 bits)
        hi = test_pattern(w, h, c, np.uint32, seed).astype(np.uint64)
        lo = test_pattern(w, h, c, np.uint32, seed + 50).astype(np.uint64)
        return ((hi << np.uint64(32)) | lo).astype(dt)
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
        emit(f'gray{bits}_minisblack', dict(arr=g, bits=bits, photometric=1, rps=5), check=both(check_pillow('L'), check_magick('gray')),
             variants=('none', 'full'))
        # (ImageMagick does not invert 2/4-bit MinIsWhite, Pillow and libtiff's RGBA interface do)
        emit(f'gray{bits}_miniswhite', dict(arr=g, bits=bits, photometric=0),
             check=check_pillow('L') if bits > 1 else both(check_pillow('L'), check_magick('gray')), variants=('none',))
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
    al = a[:, :, -1:].astype(object)
    a[:, :, :-1] = (a[:, :, :-1].astype(object) * al // m).astype(dt)
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
    sp = pat(1, 'float32', 50, 37)
    sp_exp = sp.copy()
    sp_exp[0:16, 16:32] = 0
    sp_exp[32:48, 0:16] = 0
    # GDAL style sparse file: tiles with offset 0 / count 0 are not stored and read as zeros
    emit('sparse_tiles_f32', dict(arr=sp, compression=DEFLATE, predictor=3, tile=(16, 16), sparse={1, 8}, expect_raw=sp_exp))

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
        write_tol(path, 2)   # libtiff converts with fixed point tables, Aardvark.PixImage.IO with floats
        SUMMARY.append(('ycbcr/' + name, ['cv2 (libtiff RGBA)', f'max error vs. the RGB source {src}']))

    def build_planar(name, w, h, sh, sv, comp, rps, tile=None):
        # PlanarConfiguration 2: a full resolution Y plane and Cb / Cr planes reduced by the subsampling factors
        rgb = test_pattern(w, h, 3, np.uint8, 22).astype(np.float64)
        y = 0.299 * rgb[:, :, 0] + 0.587 * rgb[:, :, 1] + 0.114 * rgb[:, :, 2]
        cb = (rgb[:, :, 2] - y) / 1.772 + 128
        cr = (rgb[:, :, 0] - y) / 1.402 + 128
        ph, pw = -(-h // sv) * sv, -(-w // sh) * sh
        pad = lambda a: np.pad(a, ((0, ph - h), (0, pw - w)), mode='edge')
        q = lambda a: np.clip(np.round(a), 0, 255).astype(np.uint8)
        planes = [q(y), q(pad(cb).reshape(ph // sv, sv, pw // sh, sh).mean(axis=(1, 3))), q(pad(cr).reshape(ph // sv, sv, pw // sh, sh).mean(axis=(1, 3)))]
        chunks = []
        for i, pl in enumerate(planes):
            fh, fv = (1, 1) if i == 0 else (sh, sv)
            if tile:
                tw, th = tile[0] // fh, tile[1] // fv
                for ty in range(0, -(-h // tile[1])):
                    for tx in range(0, -(-w // tile[0])):
                        blk = pl[ty * th:(ty + 1) * th, tx * tw:(tx + 1) * tw]
                        blk = np.pad(blk, ((0, th - blk.shape[0]), (0, tw - blk.shape[1])))
                        chunks.append(blk.tobytes())
            else:
                for r in range(0, h, rps):
                    chunks.append(pl[r // fv:-(-min(r + rps, h) // fv)].tobytes())
        chunks = [lzw_encode(c) if comp == LZW else zlib.compress(c) if comp == DEFLATE else c for c in chunks]
        verified = sh == 1 and sv == 1
        d = os.path.join(OUT, 'ycbcr' if verified else 'unverified')
        os.makedirs(d, exist_ok=True)
        path = os.path.join(d, name + '.tif')
        page = dict(arr=np.zeros((h, w, 3), np.uint8), photometric=6, planar=2, compression=comp, rps=rps, raw_chunks=chunks, tags=[
            (530, 3, [sh, sv]), (532, 5, [(0, 1), (255, 1), (128, 1), (255, 1), (128, 1), (255, 1)])])
        if tile:
            page['tile'] = tile
        write_tiff(path, [page])
        if verified:
            ref = ref_cv2(path)
            src = int(np.abs(ref.astype(int) - rgb).max())
            assert src < 6, (name, src)
            write_ref(path + '.ref', ref)
            write_tol(path, 2)
            SUMMARY.append(('ycbcr/' + name, ['cv2 (libtiff RGBA)', f'max error vs. the RGB source {src}']))
        else:
            # libtiff refuses subsampled planar YCbCr and nothing else here converts it: reference = replicate the
            # chroma samples and apply the same conversion the verified contiguous / 1x1 planar fixtures check
            assert cv2.imread(path, cv2.IMREAD_UNCHANGED) is None
            up = lambda pl: np.repeat(np.repeat(pl, sv, axis=0), sh, axis=1)[:h, :w].astype(np.float64)
            yy, cbb, crr = planes[0].astype(np.float64), up(planes[1]) - 128, up(planes[2]) - 128
            ref = np.stack([yy + 1.402 * crr, yy - (0.114 * 1.772 * cbb + 0.299 * 1.402 * crr) / 0.587, yy + 1.772 * cbb], axis=-1)
            write_ref(path + '.ref', np.clip(np.floor(ref + 0.5), 0, 255).astype(np.uint8))
            write_tol(path, 1)
            reason = f'planar YCbCr with {sh}x{sv} subsampled chroma planes (libtiff: not supported)'
            UNVERIFIED.append((name, reason))
            SUMMARY.append(('unverified/' + name, ['UNVERIFIED: ' + reason]))

    build_planar('ycbcr_planar_1x1_none', 23, 17, 1, 1, NONE, 6)
    build_planar('ycbcr_planar_1x1_lzw', 23, 17, 1, 1, LZW, 17)
    build_planar('ycbcr_planar_1x1_deflate_tiled', 40, 33, 1, 1, DEFLATE, 0, tile=(16, 16))
    build_planar('ycbcr_planar_2x2_none', 23, 17, 2, 2, NONE, 8)
    build_planar('ycbcr_planar_2x1_lzw_tiled', 40, 33, 2, 1, LZW, 0, tile=(16, 16))
    build('ycbcr_2x2_none', 24, 16, 2, 2, NONE, 16)
    build('ycbcr_2x2_odd_lzw', 23, 17, 2, 2, LZW, 8)
    build('ycbcr_2x1_deflate', 23, 17, 2, 1, DEFLATE, 5)
    build('ycbcr_1x1_none', 23, 17, 1, 1, NONE, 6)
    build('ycbcr_4x2_none', 30, 13, 4, 2, NONE, 4)
    build('ycbcr_1x2_lzw', 21, 15, 1, 2, LZW, 15)


def gen_jpeg():
    """JPEG-in-TIFF (compression 7). Written by libtiff (through Pillow and ImageMagick), plus complete baseline
    4:2:0 JPEG streams wrapped into strips/tiles by hand (no JPEGTables). References from libtiff/libjpeg via
    Pillow. Kept in their own folder: Aardvark.PixImage.IO decodes them through the Tiff.JpegDecoder hook only."""
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


def write_tol(path, tol):
    """Sidecar with the largest allowed sample difference for fixtures whose reference cannot be bit exact."""
    with open(path + '.tol', 'w') as f:
        f.write(repr(float(tol)))


def gen_depths():
    """Odd bit depths (packed MSB first), 64-bit integers, 24-bit floats, wide palettes."""
    rng = np.random.default_rng(21)

    def rnd(bits, c=1, w=W, h=H, signed=False):
        dt = np.dtype(f'{"i" if signed else "u"}{1 if bits <= 8 else 2 if bits <= 16 else 4 if bits <= 32 else 8}')
        lo, hi = (-(1 << (bits - 1)), 1 << (bits - 1)) if signed else (0, 1 << bits)
        a = rng.integers(lo, hi, (h, w, c), dtype=np.int64 if bits < 64 else None).astype(dt) if bits < 64 else \
            rng.integers(0, 1 << 63, (h, w, c), dtype=np.uint64).astype(dt) * (1 if signed else 2)
        a[0, 0, :] = hi - 1 if bits < 64 else np.iinfo(dt).max
        a[0, 1, :] = lo if bits < 64 else np.iinfo(dt).min
        return a

    for bits in (3, 5, 6, 7, 9, 10, 11, 12, 13, 14, 15, 17, 20, 24, 28, 31):
        emit(f'gray{bits}bit', dict(arr=rnd(bits), bits=bits, rps=5), variants=('none', 'full'))
    emit('gray12bit_miniswhite_lzw', dict(arr=rnd(12), bits=12, photometric=0, compression=LZW), variants=('full',))
    emit('gray10bit_packbits_tiled', dict(arr=rnd(10, 1, 41, 37), bits=10, compression=PACKBITS, tile=(16, 16)))
    emit('rgb12bit_lzw', dict(arr=rnd(12, 3), bits=12, compression=LZW, rps=6))
    emit('rgb12bit_planar_deflate_tiled', dict(arr=rnd(12, 3, 41, 37), bits=12, compression=DEFLATE, planar=2, tile=(16, 16)))
    emit('be_rgb14bit', dict(arr=rnd(14, 3), bits=14), byteorder='>')
    emit('big_gray24bit_deflate', dict(arr=rnd(24), bits=24, compression=DEFLATE), bigtiff=True)
    emit('rgb6bit_planar', dict(arr=rnd(6, 3), bits=6, planar=2), variants=('full',))
    emit('rgba12bit_assoc', dict(arr=premul(4, 'uint16', seed=6) >> 4, bits=12, extrasamples=[1]), variants=('full',))
    emit('graya10bit_unassoc', dict(arr=rnd(10, 2), bits=10, extrasamples=[2]))
    emit('multiband5_12bit', dict(arr=rnd(12, 5), bits=12, photometric=1, extrasamples=[0] * 4))

    def decode565(path):
        # tifffile reads 5-6-5 words in the file byte order and rescales them; taken through its unpack_rgb here
        # without rescaling. Packed bits are MSB first whatever the byte order (TIFF 6.0), i.e. big endian words.
        with tifffile.TiffFile(path) as t:
            pg = t.pages[0]
            with open(path, 'rb') as f:
                data = b''
                for o, n in zip(pg.dataoffsets, pg.databytecounts):
                    f.seek(o)
                    data += f.read(n)
            words = np.frombuffer(data, '>u2').astype('<u2').tobytes()
            u = tifffile.tifffile.unpack_rgb(words, np.uint8, (5, 6, 5), rescale=False)
            return 'tifffile.unpack_rgb', u.reshape(pg.imagelength, pg.imagewidth, 3)

    a565 = np.dstack([rng.integers(0, 32, (H, W)), rng.integers(0, 64, (H, W)), rng.integers(0, 32, (H, W))]).astype(np.uint8)
    emit('be_rgb565', dict(arr=a565, bits=[5, 6, 5], raw_decode=decode565, rps=4), byteorder='>', variants=('full',))

    cm12 = rng.integers(0, 65536, 3 * 4096).tolist()
    emit('palette12', dict(arr=rnd(12), bits=12, photometric=3, colormap=cm12))
    # a 16-bit colormap is 393 KB whatever the image size: one fixture only, with a regular (compressible) map
    i = np.arange(65536)
    cm16 = np.concatenate([i, 65535 - i, (i * 7) & 0xFFFF]).tolist()
    emit('be_palette16_deflate', dict(arr=rnd(16), photometric=3, colormap=cm16, compression=DEFLATE, predictor=2), byteorder='>',
         check=check_magick('rgb', tol=1))

    # 64-bit integers
    emit('u64_gray_none', dict(arr=rnd(64), rps=5))
    emit('i64_gray_none', dict(arr=rnd(64, signed=True)))
    emit('u64_rgb_lzw_pred2', dict(arr=rnd(64, 3), compression=LZW, predictor=2, rps=6))
    emit('i64_rgb_deflate_pred2', dict(arr=rnd(64, 3, signed=True), compression=DEFLATE, predictor=2))
    emit('be_u64_gray_deflate_pred2', dict(arr=rnd(64), compression=DEFLATE, predictor=2), byteorder='>')
    emit('be_i64_rgb_none', dict(arr=rnd(64, 3, signed=True)), byteorder='>')
    emit('u64_rgb_planar_tiled_packbits', dict(arr=rnd(64, 3, 41, 37), compression=PACKBITS, planar=2, tile=(16, 16)))
    emit('u64_miniswhite', dict(arr=rnd(64), photometric=0))
    emit('u64_rgba_assoc', dict(arr=premul(4, 'uint64', seed=8), extrasamples=[1]))
    emit('i64_multiband4_lzw', dict(arr=rnd(64, 4, signed=True), photometric=1, extrasamples=[0] * 3, compression=LZW))

    # 24-bit floats (values representable in 24 bits: 16 bit mantissa)
    f24 = pat(3, 'float16').astype(np.float32)
    f24[0, 2, 0] = 2.0 ** -70   # subnormal as a 24-bit float
    f24[0, 3, 0] = -np.inf
    emit('f24_gray_none', dict(arr=f24[:, :, :1], float24=True, sampleformat=3, rps=5))
    emit('be_f24_rgb_none', dict(arr=f24, float24=True, sampleformat=3), byteorder='>')
    emit('f24_rgb_planar_lzw', dict(arr=f24, float24=True, sampleformat=3, planar=2, compression=LZW))
    emit('f24_gray_deflate_tiled', dict(arr=np.tile(f24[:, :, :1], (3, 2, 1))[:37, :41], float24=True, sampleformat=3,
                                       compression=DEFLATE, tile=(16, 16)))

    # Integers of 33..63 bits and the horizontal predictor on packed samples: nothing reads these. Tried: tifffile
    # (returns an empty array), imagecodecs.packints_decode ("bitspersample out of range"), GDAL ("Unsupported TIFF
    # configuration"), libtiff via OpenCV / Pillow / libvips (no unpacking of odd depths, "predictor not supported").
    why = 'no decoder unpacks integers wider than 32 bits that are not 64 bits (tifffile, imagecodecs, GDAL, libtiff, libvips tried)'
    emit('u40_gray', dict(arr=rnd(40), bits=40, rps=5, unverified=why))
    emit('be_u48_rgb_lzw', dict(arr=rnd(48, 3), bits=48, compression=LZW, unverified=why), byteorder='>')
    emit('u63_gray_planar2_deflate', dict(arr=rnd(63, 2), bits=63, compression=DEFLATE, planar=2, extrasamples=[0], photometric=1, unverified=why))
    emit('i36_gray', dict(arr=rnd(36, signed=True), bits=36, unverified=why))
    # Horizontal predictor on packed samples (differences modulo 2^bits): libtiff and GDAL refuse the combination,
    # tifffile reads the unsigned cases.
    emit('gray12bit_lzw_pred2', dict(arr=rnd(12), bits=12, compression=LZW, predictor=2, rps=6))
    emit('rgb5bit_deflate_pred2_tiled', dict(arr=rnd(5, 3, 41, 37), bits=5, compression=DEFLATE, predictor=2, tile=(16, 16)))
    emit('be_rgb10bit_planar_lzw_pred2', dict(arr=rnd(10, 3), bits=10, compression=LZW, predictor=2, planar=2), byteorder='>')
    why = 'horizontal predictor on signed / 1-bit packed samples: tifffile, GDAL and libtiff all refuse it'
    emit('i12_rgb_planar_lzw_pred2', dict(arr=rnd(12, 3, signed=True), bits=12, compression=LZW, predictor=2, planar=2, unverified=why))
    emit('gray1_deflate_pred2', dict(arr=rnd(1, 1, 37, 21), bits=1, compression=DEFLATE, predictor=2, unverified=why))

    # Signed samples of odd depths and the float predictor on 24-bit floats: tifffile and the libtiff front ends refuse
    # them, GDAL (rasterio) reads them. GDAL unpacks the bits without looking at the sign (it reports unsigned data),
    # so the sign extension Aardvark.PixImage.IO documents is applied to what it returns.
    def gdal(bits=None):
        def read(path):
            import rasterio
            with rasterio.open(path) as ds:
                a = np.moveaxis(ds.read(), 0, -1)
            if bits:
                assert a.dtype.kind == 'u', a.dtype
                v = a.astype(np.int64)
                a = np.where(v >= 1 << (bits - 1), v - (1 << bits), v).astype(a.dtype.name[1:])
            return 'GDAL (rasterio)' + (', sign extended' if bits else ''), a
        return read

    emit('i12_gray', dict(arr=rnd(12, signed=True), bits=12, raw_decode=gdal(12)))
    emit('i5_rgb_lzw', dict(arr=rnd(5, 3, signed=True), bits=5, compression=LZW, raw_decode=gdal(5)))
    emit('be_i20_gray', dict(arr=rnd(20, signed=True), bits=20, raw_decode=gdal(20)), byteorder='>')
    emit('i12_rgb_planar_deflate_tiled', dict(arr=rnd(12, 3, 41, 37, signed=True), bits=12, compression=DEFLATE, planar=2, tile=(16, 16),
                                             raw_decode=gdal(12)))
    emit('f24_gray_deflate_pred3', dict(arr=f24[:, :, :1], float24=True, sampleformat=3, compression=DEFLATE, predictor=3, raw_decode=gdal()))
    emit('be_f24_rgb_lzw_pred3', dict(arr=f24, float24=True, sampleformat=3, compression=LZW, predictor=3, rps=5, raw_decode=gdal()),
         byteorder='>')


def gen_more_codecs():
    """ZSTD, LZMA and WebP compressed files (decoded by tifffile + imagecodecs)."""
    for comp, cn in [(50000, 'zstd'), (34925, 'lzma')]:
        kw = dict(subdir=cn)
        emit(f'{cn}_u8_rgb', dict(arr=pat(3, 'uint8'), compression=comp, rps=6), **kw)
        emit(f'{cn}_u8_rgb_pred2', dict(arr=pat(3, 'uint8'), compression=comp, predictor=2), **kw)
        emit(f'{cn}_u16_rgba_pred2_tiled', dict(arr=pat(4, 'uint16', 50, 37), compression=comp, predictor=2, tile=(16, 16), extrasamples=[2]), **kw)
        emit(f'{cn}_f32_gray_pred3', dict(arr=pat(1, 'float32'), compression=comp, predictor=3, rps=5), **kw)
        emit(f'{cn}_be_f64_rgb_pred3_planar', dict(arr=pat(3, 'float64'), compression=comp, predictor=3, planar=2), byteorder='>', **kw)
        emit(f'{cn}_big_i16_dem_pred2_tiled', dict(arr=pat(1, 'int16', 70, 45), compression=comp, predictor=2, tile=(32, 32)), bigtiff=True, **kw)
        emit(f'{cn}_gray1', dict(arr=(test_pattern(37, 21, 1, np.uint8, 9) >> 7).astype(np.uint8), bits=1, compression=comp), **kw)
        emit(f'{cn}_gray12bit', dict(arr=(pat(1, 'uint16') >> 4), bits=12, compression=comp), **kw)
        emit(f'{cn}_u64_gray_pred2', dict(arr=pat(1, 'uint64'), compression=comp, predictor=2), **kw)
    # written by tifffile itself
    for cn in ('zstd', 'lzma'):
        path = os.path.join(OUT, cn, f'{cn}_ext_tifffile_u16_rgb_tiled.tif')
        tifffile.imwrite(path, pat(3, 'uint16', 70, 50), photometric='rgb', compression=cn, predictor=True, tile=(32, 32), metadata=None)
        emit_external(f'{cn}/{cn}_ext_tifffile_u16_rgb_tiled', path, ref_tifffile(path), ['tifffile'])

    # WebP: lossless files are bit exact; lossy ones are compared against libwebp with a tolerance
    kw = dict(subdir='webp')
    emit('webp_rgb_lossless_strips', dict(arr=pat(3, 'uint8', 40, 30), compression=50001, rps=8), **kw)
    emit('webp_rgba_lossless_tiled', dict(arr=pat(4, 'uint8', 50, 37), compression=50001, tile=(32, 16), extrasamples=[2]), **kw)
    emit('webp_rgb_lossless_1x1', dict(arr=pat(3, 'uint8', 1, 1), compression=50001), **kw)
    for name, arr, kwargs in [
        ('webp_ext_tifffile_rgb_lossy', pat(3, 'uint8', 70, 50), dict(photometric='rgb', compression='webp', compressionargs={'level': 80})),
        ('webp_ext_tifffile_rgb_lossy_tiled', pat(3, 'uint8', 70, 50), dict(photometric='rgb', compression='webp', tile=(32, 32), compressionargs={'level': 80})),
        ('webp_ext_tifffile_rgba_lossless', pat(4, 'uint8', 45, 31), dict(photometric='rgb', compression='webp', extrasamples=['unassalpha'],
                                                                         compressionargs={'lossless': True})),
    ]:
        path = os.path.join(OUT, 'webp', name + '.tif')
        tifffile.imwrite(path, arr, metadata=None, **kwargs)
        emit_external('webp/' + name, path, ref_tifffile(path), ['tifffile (libwebp)'])
        if 'lossy' in name:
            write_tol(path, 4)


def gen_foreign():
    """Compressions whose codecs live outside the TIFF folder: JPEG 2000 (34712, Aperio 33003 / 33005) and JBIG (34661).
    JPEG 2000 strips / tiles are raw codestreams from OpenJPEG (imagecodecs) and are read back by tifffile + OpenJPEG;
    JBIG entities are written and read back by libtiff (jbigkit)."""
    import imagecodecs
    kw = dict(subdir='jp2')

    def j2k(a, **args):
        args.setdefault('codecformat', 'j2k')
        return imagecodecs.jpeg2k_encode(np.ascontiguousarray(a), **args)

    def chunks_of(a, tile=None, rps=None, planar=False, **args):
        h, w, c = a.shape
        cw, ch = tile if tile else (w, rps or h)
        out = []
        for plane in (range(c) if planar else [None]):
            src = a if plane is None else a[:, :, plane:plane + 1]
            for y in range(0, h, ch):
                for x in range(0, w, cw):
                    blk = src[y:y + ch, x:x + cw]
                    if tile:   # tiles are stored whole: pad by repeating the edge
                        blk = np.pad(blk, ((0, ch - blk.shape[0]), (0, cw - blk.shape[1]), (0, 0)), mode='edge')
                    out.append(j2k(blk[:, :, 0] if blk.shape[2] == 1 else blk, **args))
        return out

    def jp2(name, a, comp=34712, tile=None, rps=None, planar=False, byteorder='<', bigtiff=False, extra=None, **args):
        page = dict(arr=a, compression=comp, raw_chunks=chunks_of(a, tile, rps, planar, **args))
        if tile:
            page['tile'] = tile
        if rps:
            page['rps'] = rps
        if planar:
            page['planar'] = 2
        page.update(extra or {})
        emit(name, page, byteorder=byteorder, bigtiff=bigtiff, **kw)

    jp2('jp2_u8_rgb_strip', pat(3, 'uint8'))
    jp2('jp2_u8_rgb_strips', pat(3, 'uint8'), rps=8)
    jp2('jp2_u8_rgb_nomct', pat(3, 'uint8'), mct=False)
    jp2('jp2_u8_rgb_jp2boxes', pat(3, 'uint8'), codecformat='jp2')
    jp2('jp2_u8_gray_tiled', pat(1, 'uint8', 50, 37), tile=(32, 16))
    jp2('jp2_u8_rgba_tiled', pat(4, 'uint8', 50, 37), tile=(32, 32), extra=dict(extrasamples=[2]))
    jp2('jp2_u16_gray', pat(1, 'uint16'))
    jp2('jp2_u16_rgb_planar_tiled', pat(3, 'uint16', 50, 37), tile=(32, 32), planar=True)
    jp2('jp2_be_big_i16_dem_tiled', pat(1, 'int16', 70, 45), tile=(32, 32), byteorder='>', bigtiff=True)
    jp2('jp2_i8_gray', pat(1, 'int8'))
    jp2('jp2_gray12bit', pat(1, 'uint16') >> 4, extra=dict(bits=12), bitspersample=12)
    jp2('jp2_u8_5samples', pat(5, 'uint8'), extra=dict(photometric=1, extrasamples=[0, 0, 0, 0]), mct=False)
    jp2('jp2_aperio33005_rgb_tiled', pat(3, 'uint8', 50, 37), comp=33005, tile=(32, 32))
    jp2('jp2_aperio33003_mct_tiled', pat(3, 'uint8', 50, 37), comp=33003, tile=(32, 32))
    # lossy (9/7 wavelet): decoders differ in rounding
    for name, a, args in [('jp2_u8_rgb_lossy', pat(3, 'uint8', 70, 50), dict(level=45)),
                          ('jp2_u16_gray_lossy_tiled', pat(1, 'uint16', 70, 50), dict(level=60, tile=(32, 32)))]:
        tile = args.pop('tile', None)
        path = os.path.join(OUT, 'jp2', name + '.tif')
        page = dict(arr=a, compression=34712, raw_chunks=chunks_of(a, tile, **args))
        if tile:
            page['tile'] = tile
        write_tiff(path, [page])
        emit_external('jp2/' + name, path, ref_tifffile(path), ['tifffile (OpenJPEG)'])
        write_tol(path, 2 if a.dtype == np.uint8 else 2 * 257)
    # YCbCr components in the codestream (no component transform): Aperio's 33003, and the YCbCr photometric.
    # OpenJPEG returns the components; the reference applies the JFIF conversion to them.
    rgb = pat(3, 'uint8', 50, 37).astype(np.float64)
    ycc = np.stack([0.299 * rgb[..., 0] + 0.587 * rgb[..., 1] + 0.114 * rgb[..., 2],
                    128 - 0.168736 * rgb[..., 0] - 0.331264 * rgb[..., 1] + 0.5 * rgb[..., 2],
                    128 + 0.5 * rgb[..., 0] - 0.418688 * rgb[..., 1] - 0.081312 * rgb[..., 2]], -1).round().clip(0, 255).astype(np.uint8)

    def to_rgb(c):
        c = c.astype(np.float64)
        y, cb, cr = c[..., 0], c[..., 1] - 128, c[..., 2] - 128
        return np.stack([y + 1.402 * cr, y - 0.344136 * cb - 0.714136 * cr, y + 1.772 * cb], -1).round().clip(0, 255).astype(np.uint8)

    for name, comp, pm in [('jp2_aperio33003_ycbcr_tiled', 33003, 2), ('jp2_photometric_ycbcr_tiled', 34712, 6)]:
        path = os.path.join(OUT, 'jp2', name + '.tif')
        write_tiff(path, [dict(arr=ycc, compression=comp, photometric=pm, tile=(32, 32), raw_chunks=chunks_of(ycc, (32, 32), mct=False),
                               tags=[(530, 3, [1, 1])] if pm == 6 else [])])
        with tifffile.TiffFile(path) as t:
            got = np.zeros((64, 64, 3), np.uint8)
            pg = t.pages[0]
            fh = t.filehandle
            for i, (o, n) in enumerate(zip(pg.dataoffsets, pg.databytecounts)):
                fh.seek(o)
                got[i // 2 * 32:i // 2 * 32 + 32, i % 2 * 32:i % 2 * 32 + 32] = imagecodecs.jpeg2k_decode(fh.read(n))
        assert same(got[:37, :50], ycc)
        emit_external('jp2/' + name, path, to_rgb(got[:37, :50]), ['OpenJPEG components, YCbCr to RGB by the JFIF formula in gen_tiff.py'])
        write_tol(path, 1)

    # ---- JBIG
    os.makedirs(os.path.join(OUT, 'jbig'), exist_ok=True)
    lib = LibTiff()
    rng = np.random.default_rng(77)

    def bilevel(w, h):
        yy, xx = np.mgrid[0:h, 0:w]
        a = (((xx // 5 + yy // 3) % 2 == 0) ^ (rng.random((h, w)) < 0.08)).astype(np.uint8)
        a[h // 2] = 1
        return a

    def bie(bits, fill):
        """One strip through libtiff's JBIG codec: (the bi-level image entity, libtiff's decoding of it)."""
        packed = np.packbits(bits, axis=1)
        tmp = os.path.join(OUT, 'jbig', '_strip.tif')
        lib.write(tmp, packed[:, :, None], 34661, 0, tags=((256, bits.shape[1]), (258, 1), (266, fill)))
        back = np.unpackbits(lib.read(tmp, np.uint8, packed.shape), axis=1)[:, :bits.shape[1]]
        with tifffile.TiffFile(tmp) as t:
            pg = t.pages[0]
            t.filehandle.seek(pg.dataoffsets[0])
            data = t.filehandle.read(pg.databytecounts[0])
        os.remove(tmp)
        return data, back

    # libtiff reads and writes single strip JBIG files only; files with several strips are put together from
    # entities it wrote one by one, and the reference from its reading of each
    for name, w, h, pm, rps, e, fill in [('jbig_minisblack', 61, 40, 1, None, '<', 1), ('jbig_miniswhite_strips', 131, 53, 0, 8, '<', 1),
                                         ('jbig_be_strips', 64, 33, 1, 16, '>', 1), ('jbig_1x1', 1, 1, 0, None, '<', 1),
                                         ('jbig_fillorder2', 77, 30, 0, None, '<', 2), ('jbig_fillorder2_strips', 77, 30, 1, 7, '<', 2),
                                         ('jbig_wide', 900, 70, 0, None, '<', 1)]:
        bits = bilevel(w, h)
        parts = [bie(bits[y:y + (rps or h)], fill) for y in range(0, h, rps or h)]
        back = np.concatenate([b for _, b in parts])
        assert (back == bits).all(), name
        path = os.path.join(OUT, 'jbig', name + '.tif')
        write_tiff(path, [dict(arr=bits[:, :, None], bits=1, photometric=pm, compression=34661, rps=rps or h, raw_chunks=[d for d, _ in parts],
                               tags=[(266, 3, [fill])] if fill == 2 else [])], byteorder=e)
        who = 'libtiff (jbigkit)'
        if rps is None:
            whole = np.unpackbits(lib.read(path, np.uint8, (h, (w + 7) // 8)), axis=1)[:, :w]
            assert (whole == bits).all(), name
        else:
            who += ', strip by strip'
        emit_external('jbig/' + name, path, ((back if pm == 1 else 1 - back) * 255).astype(np.uint8)[:, :, None], [who])
    # (ImageMagick cannot write JBIG in TIFF: libtiff's codec has no scanline interface)


def gen_lerc():
    """LERC (34887) as GDAL / libtiff write it, including the second compression (LERC_DEFLATE, LERC_ZSTD) and the
    validity masks: NaN samples of float data and binary alpha of 8-bit data. Written by GDAL (rasterio) and by
    tifffile + imagecodecs (liblerc); the reference is GDAL's reading, cross-checked with tifffile where a file has
    no mask (tifffile ignores LERC masks)."""
    import rasterio
    from rasterio.transform import from_origin
    d = os.path.join(OUT, 'lerc')
    os.makedirs(d, exist_ok=True)
    w, h = 70, 45
    yy, xx = np.mgrid[0:h, 0:w]
    dem = (np.sin(xx / 9) * np.cos(yy / 7) * 500 + 1000).astype(np.float32)
    hole = dem.copy()
    hole[5:12, 20:40] = np.nan
    hole[30, 3] = np.nan

    def finish(name, tifffile_too):
        path = os.path.join(d, name + '.tif')
        with rasterio.open(path) as ds:
            got = np.moveaxis(ds.read(), 0, -1)
        with tifffile.TiffFile(path) as t:
            assert t.pages[0].compression == 34887
        by = ['GDAL (libtiff + liblerc)']
        if tifffile_too:
            assert same(ref_tifffile(path), got), name
            by.append('tifffile')
        emit_external('lerc/' + name, path, got, by)
        return got

    def gdal(name, bands, tifffile_too=True, **kw):
        bands = np.asarray(bands)
        if bands.ndim == 2:
            bands = bands[None]
        path = os.path.join(d, name + '.tif')
        with rasterio.open(path, 'w', driver='GTiff', width=bands.shape[2], height=bands.shape[1], count=bands.shape[0],
                           dtype=bands.dtype, transform=from_origin(0, 0, 1, 1), **kw) as ds:
            ds.write(bands)
        aux = path + '.aux.xml'
        if os.path.exists(aux):
            os.remove(aux)
        return finish(name, tifffile_too)

    got = gdal('lerc_f32_dem_nan', hole, False, compress='LERC', nodata=np.nan)
    assert np.isnan(got).sum() == np.isnan(hole).sum()
    gdal('lerc_f32_dem', dem, compress='LERC')
    gdal('lerc_zstd_f32_dem_nan_lossy_tiled', hole, False, compress='LERC_ZSTD', MAX_Z_ERROR=0.01, tiled=True, blockxsize=32, blockysize=32)
    gdal('lerc_deflate_f64_dem_nan', hole.astype(np.float64), False, compress='LERC_DEFLATE')
    gdal('lerc_deflate_u16_rgb_tiled', np.moveaxis(pat(3, 'uint16', 70, 45), -1, 0), compress='LERC_DEFLATE', interleave='pixel',
         photometric='RGB', tiled=True, blockxsize=32, blockysize=16)
    f3 = np.stack([hole, hole * 2, hole + 1])
    gdal('lerc_f32_3samples_nan_pixels', f3, False, compress='LERC', interleave='pixel', MAX_Z_ERROR=0.05)
    f3 = f3.copy()
    f3[1, 33:36, 5:9] = np.nan
    f3[2, 0, 0] = np.nan
    got = gdal('lerc_f32_3samples_nan_mixed', f3, False, compress='LERC', interleave='pixel')
    assert np.isnan(got).sum() == np.isnan(f3).sum()
    gdal('lerc_f32_3samples_nan_planar', f3, False, compress='LERC', interleave='band', MAX_Z_ERROR=0.05)
    alpha = np.where((xx - 35) ** 2 + (yy - 22) ** 2 < 300, 255, 0)
    rgba = np.stack([(xx * 3) % 256, (yy * 5) % 256, (xx + yy) % 256, alpha]).astype(np.uint8)
    got = gdal('lerc_u8_rgba_binary_alpha', rgba, False, compress='LERC', photometric='RGB', alpha='YES', interleave='pixel')
    assert (got[..., 3] == alpha).all()
    gdal('lerc_u8_rgba_binary_alpha_tiled', rgba, False, compress='LERC', photometric='RGB', alpha='YES', interleave='pixel',
         tiled=True, blockxsize=32, blockysize=32)
    gdal('lerc_u8_rgba_opaque', np.concatenate([rgba[:3], np.full((1, h, w), 255, np.uint8)]), False, compress='LERC', photometric='RGB',
         alpha='YES', interleave='pixel')
    gdal('lerc_u8_rgba_planar', rgba, compress='LERC', photometric='RGB', alpha='YES', interleave='band')
    soft = rgba.copy()
    soft[3] = (xx * 3) % 256
    gdal('lerc_u8_rgba_soft_alpha', soft, compress='LERC', photometric='RGB', alpha='YES', interleave='pixel')
    gdal('lerc_u16_2samples_lossy', np.stack([(xx * yy) % 65536, (xx * 700 + yy) % 65536]).astype(np.uint16), compress='LERC',
         MAX_Z_ERROR=3, interleave='pixel')
    gdal('lerc_big_u8_gray_tiled', pat(1, 'uint8', 70, 45)[:, :, 0], compress='LERC', BIGTIFF='YES', tiled=True, blockxsize=32, blockysize=32)
    gdal('lerc_i16_dem_strips', pat(1, 'int16', 70, 45)[:, :, 0], compress='LERC', blockysize=8)
    # tifffile + imagecodecs
    for dt in ('int8', 'int32', 'uint32', 'float64'):
        name = f'lerc_ext_tifffile_{dt}'
        a = pat(1, dt, 50, 37)[:, :, 0]
        tifffile.imwrite(os.path.join(d, name + '.tif'), a, compression='lerc', rowsperstrip=16, metadata=None)
        # (liblerc turns the NaN among the float test values into an invalid pixel by itself; tifffile then reads 0 there)
        finish(name, not (a.dtype.kind == 'f' and np.isnan(a).any()))
    # big endian, straight from libtiff (tifffile cannot write LERC into a big endian file)
    name = 'lerc_ext_libtiff_be_f32_rgb_strips'
    a = pat(3, 'float32', 50, 37)
    LibTiff().write(os.path.join(d, name + '.tif'), a, 34887, 2, rps=10, big_endian=True)
    finish(name, False)
    name = 'lerc_ext_libtiff_be_i16_gray'
    LibTiff().write(os.path.join(d, name + '.tif'), pat(1, 'int16', 50, 37), 34887, 1, big_endian=True)
    finish(name, True)


def gen_ccitt_uncompressed():
    """CCITT "uncompressed mode" (T.4 / T.6 extension). No encoder writes it and libtiff does not read it ("uncompressed
    data (not supported)", and with it OpenCV, Pillow, GDAL, libvips; tifffile has no CCITT codec), so these files are
    packed by hand from the recommendation. FFmpeg's independent decoder reads them and provides the reference; without
    FFmpeg they are listed as unverified."""
    rng = np.random.default_rng(81)
    w, h = 61, 24
    a = (rng.random((h, w)) < 0.35).astype(np.uint8)       # 1 = black
    a[3] = 0
    a[4] = 1
    a[5, :30] = 0
    a[6, 7:] = 0

    def urow(row, t=0):
        out, whites = '', 0
        for v in row:
            if v == 0:
                whites += 1
                if whites == 5:
                    out, whites = out + '000001', 0
            else:
                out, whites = out + '0' * whites + '1', 0
        return out + '0' * (6 + whites) + '1' + str(t)       # exit code, then the colour of the next coded run

    def to_bytes(bits):
        bits += '0' * (-len(bits) % 8)
        return bytes(int(bits[i:i + 8], 2) for i in range(0, len(bits), 8))

    def changes(row):
        return [x for x in range(len(row)) if row[x] != (row[x - 1] if x else 0)]

    def finish(name, img, data, comp, tag, rps=None):
        page = dict(arr=img[:, :, None], bits=1, photometric=0, compression=comp, raw_chunks=data, rps=rps or img.shape[0], tags=[(tag, 4, [2])])
        tmp = os.path.join(OUT, name + '.tif')
        write_tiff(tmp, [page])
        for reader in (lambda: ref_cv2(tmp), lambda: np.asarray(Image.open(tmp).convert('L'))):
            try:
                got = reader()
                assert not np.array_equal(got.reshape(img.shape) > 0, img == 0), f'{name}: a libtiff based decoder reads this now - use it'
            except AssertionError:
                raise
            except Exception:
                pass
        # FFmpeg's own TIFF / fax decoder (libavcodec, not libtiff) implements the uncompressed mode
        got = None
        try:
            r = subprocess.run(['ffmpeg', '-v', 'error', '-i', tmp, '-pix_fmt', 'gray', '-f', 'rawvideo', '-'], capture_output=True)
            if r.returncode == 0 and len(r.stdout) == img.size:
                got = np.frombuffer(r.stdout, np.uint8).reshape(img.shape)
        except FileNotFoundError:
            pass
        if got is not None:
            assert np.array_equal(got, ((1 - img) * 255).astype(np.uint8)), f'{name}: FFmpeg decodes another picture'
            write_ref(tmp + '.ref', got[:, :, None])
            SUMMARY.append((name, ['ffmpeg (libavcodec)']))
            return
        d = os.path.join(OUT, 'unverified')
        os.makedirs(d, exist_ok=True)
        path = os.path.join(d, name + '.tif')
        os.replace(tmp, path)
        write_ref(path + '.ref', ((1 - img) * 255).astype(np.uint8))
        reason = 'CCITT uncompressed mode: libtiff (and so OpenCV, Pillow, GDAL, libvips) rejects it, nothing writes it; FFmpeg is not installed'
        UNVERIFIED.append((name, reason))
        SUMMARY.append(('unverified/' + name, ['UNVERIFIED: ' + reason]))

    # Group 4, every row in uncompressed mode
    bits = ''.join('0000001111' + urow(r) for r in a) + '000000000001' * 2
    finish('ccitt_g4_uncompressed', a, [to_bytes(bits)], 4, 293)

    # Group 4, mixed: uncompressed rows, rows identical to the one above coded with vertical codes only, and a row
    # that leaves uncompressed mode half way (the rest is white: one V0 against the white reference row)
    b = a.copy()
    b[0, 25:] = 0
    b[2::2] = b[1:-1:2]
    bits = '0000001111' + urow(b[0, :25]) + '1'
    for y in range(1, h):
        bits += '0000001111' + urow(b[y]) if y % 2 else '1' * (len(changes(b[y])) + 1)
    finish('ccitt_g4_uncompressed_mixed', b, [to_bytes(bits + '000000000001' * 2)], 4, 293)

    # Group 3 one-dimensional (EOL before every row) in two strips, and modified Huffman (rows byte aligned)
    strips = [to_bytes(''.join('000000000001' + '000000001111' + urow(r) for r in a[y:y + 12])) for y in range(0, h, 12)]
    finish('ccitt_g3_uncompressed', a, strips, 3, 292, rps=12)
    finish('ccitt_rle_uncompressed', a, [b''.join(to_bytes('000000001111' + urow(r)) for r in a)], 2, 292)


def gen_structures():
    """SubIFDs, complex samples, raw sensor photometrics, per-sample formats and other rarely seen layouts."""
    rng = np.random.default_rng(91)
    # SubIFDs, written by tifffile: two reduced levels below the first page and a second page with one sub-image
    # (tifffile cannot write SubIFDs inside SubIFDs). References: tifffile's reading of the sub pages.
    path = os.path.join(OUT, 'subifds_pyramid.tif')
    base = pat(3, 'uint8', 64, 48)
    lv1, lv2 = np.ascontiguousarray(base[::2, ::2]), pat(3, 'uint8', 16, 12, seed=4)
    page2, sub2 = pat(1, 'float32', 20, 10), pat(1, 'float32', 10, 5, seed=6)
    with tifffile.TiffWriter(path) as tw:
        tw.write(base, photometric='rgb', subifds=2, metadata=None)
        tw.write(lv1, photometric='rgb', subfiletype=1, compression='lzw', metadata=None)
        tw.write(lv2, photometric='rgb', subfiletype=1, tile=(16, 16), metadata=None)
        tw.write(page2[:, :, 0], subifds=1, compression='zlib', metadata=None)
        tw.write(sub2[:, :, 0], subfiletype=1, metadata=None)
    with tifffile.TiffFile(path) as t:
        p0, p1 = t.pages[0], t.pages[1]
        got = {'': p0.asarray(), '.sub0': p0.pages[0].asarray(), '.sub1': p0.pages[1].asarray(), '.1': p1.asarray(), '.1.sub0': p1.pages[0].asarray()}
    want = {'': base, '.sub0': lv1, '.sub1': lv2, '.1': page2[:, :, 0], '.1.sub0': sub2[:, :, 0]}
    for k, v in got.items():
        assert np.array_equal(v, want[k], equal_nan=v.dtype.kind == 'f'), k
        write_ref(path + k + '.ref', v)
    SUMMARY.append(('subifds_pyramid', ['tifffile (written and read)']))

    # complex samples: two parts (real, imaginary) per sample
    for name, a, kw in [('complex64_gray', (pat(1, 'float32')[:, :, 0] + 1j * pat(1, 'float32', seed=3)[:, :, 0]).astype(np.complex64), dict()),
                        ('complex128_lzw', (pat(1, 'float64')[:, :, 0] - 2j * pat(1, 'float64', seed=3)[:, :, 0]).astype(np.complex128), dict(compression='lzw')),
                        ('be_complex64_rgb_deflate_tiled', (pat(3, 'float32', 40, 33) + 1j * pat(3, 'float32', 40, 33, seed=3)).astype(np.complex64),
                         dict(compression='zlib', tile=(16, 16), byteorder='>', photometric='rgb'))]:
        path = os.path.join(OUT, name + '.tif')
        tifffile.imwrite(path, a, metadata=None, **kw)
        got = tifffile.imread(path)
        assert got.dtype == a.dtype and np.array_equal(got, a, equal_nan=True)
        part = np.dtype(f'f{a.dtype.itemsize // 2}')
        ref = np.ascontiguousarray(got).view(part).reshape(got.shape[0], got.shape[1], -1)
        emit_external(name, path, ref, ['tifffile'])
    try:
        import rasterio
        path = os.path.join(OUT, 'complex_int16_gdal.tif')
        ci = (rng.integers(-3000, 3000, (17, 23)) + 1j * rng.integers(-3000, 3000, (17, 23))).astype(np.complex64)
        with rasterio.open(path, 'w', driver='GTiff', width=23, height=17, count=1, dtype='complex_int16', compress='deflate') as ds:
            ds.write(ci, 1)
        with rasterio.open(path) as ds:
            got = ds.read(1)
        assert np.array_equal(got, ci)
        emit_external('complex_int16_gdal', path, np.stack([got.real, got.imag], axis=-1).astype(np.int16), ['GDAL (rasterio), written by GDAL'])
    except ImportError:
        pass

    # raw sensor photometrics (DNG): returned as stored
    emit('cfa_u16', dict(arr=pat(1, 'uint16'), photometric=32803, compression=LZW, predictor=2))
    emit('linearraw_u16_rgb', dict(arr=pat(3, 'uint16'), photometric=34892, tile=(16, 16)))
    # Lab with an extra sample that is not alpha: colour converted, the extra carried along (see gen_lab for the colour)

    # plain float "LogLuv": XYZ with an alpha sample, contiguous and planar. Samples: tifffile; colour: cv2 XYZ2RGB.
    xyz = cv2.cvtColor(np.abs(test_pattern(31, 23, 3, np.float32, 51)).astype(np.float32) * 0.6 + 0.01, cv2.COLOR_RGB2XYZ)
    al = np.clip(np.abs(test_pattern(31, 23, 1, np.float32, 53)), 0, 1).astype(np.float32)
    for name, kw in [('logluv_float_alpha', dict(extrasamples=[2])), ('logluv_float_alpha_planar_lzw', dict(extrasamples=[2], planar=2, compression=LZW)),
                     ('logluv_float_extra', dict(extrasamples=[0]))]:
        def from_raw(raw):
            return np.concatenate([cv2.cvtColor(np.ascontiguousarray(raw[:, :, :3]), cv2.COLOR_XYZ2RGB), raw[:, :, 3:]], axis=2)
        pth = emit(name, dict(arr=np.concatenate([xyz, al], axis=2), photometric=32845, ref_from_raw=from_raw, **kw), subdir='logluv', variants=('raw',))
        write_tol(pth, 1e-4)

    # Per-sample formats (converted to Float64), the horizontal predictor on 24-bit floats and 16-bit YCbCr without
    # JPEG: no decoder reads these the way the tags describe them (tifffile applies the first sample's format to all,
    # libtiff's RGBA interface is 8-bit only), so they are listed as unverified.
    def hand(name, page, ref, reason, byteorder='<'):
        d = os.path.join(OUT, 'unverified')
        os.makedirs(d, exist_ok=True)
        pth = os.path.join(d, name + '.tif')
        write_tiff(pth, [page], byteorder)
        write_ref(pth + '.ref', ref)
        UNVERIFIED.append((name, reason))
        SUMMARY.append(('unverified/' + name, ['UNVERIFIED: ' + reason]))
        return pth

    why = 'differing SampleFormat per sample: tifffile applies the first format to all samples, libtiff readers refuse'
    for bits, byteorder in ((32, '<'), (16, '>'), (64, '<')):
        e = byteorder
        dt = np.dtype([('u', f'{e}u{bits // 8}'), ('i', f'{e}i{bits // 8}'), ('f', f'{e}f{bits // 8}')])
        rec = np.zeros((H, W), dt)
        rec['u'] = pat(1, f'uint{bits}')[:, :, 0]
        rec['i'] = pat(1, f'int{bits}', seed=2)[:, :, 0]
        rec['f'] = np.nan_to_num(pat(1, f'float{bits}', seed=3)[:, :, 0])
        ref = np.stack([rec['u'].astype(np.float64), rec['i'].astype(np.float64), rec['f'].astype(np.float64)], axis=-1)
        chunks = [rec[y:y + 6].tobytes() for y in range(0, H, 6)]
        hand(f'mixed_formats_{bits}bit', dict(arr=np.zeros((H, W, 3), f'uint{bits}'), photometric=1, extrasamples=[0, 0], rps=6, raw_chunks=chunks,
                                             sampleformat=1, tags=[(339, 3, [1, 2, 3])]), ref, why, byteorder)

    f24 = pat(3, 'float16').astype(np.float32)
    hand('f24_rgb_lzw_pred2', dict(arr=f24, float24=True, sampleformat=3, compression=LZW, predictor=2), f24,
         'horizontal predictor on 24-bit floats: tifffile and GDAL refuse it')
    hand('be_f24_gray_deflate_pred2', dict(arr=f24[:, :, :1], float24=True, sampleformat=3, compression=DEFLATE, predictor=2, rps=5), f24[:, :, :1],
         'horizontal predictor on 24-bit floats: tifffile and GDAL refuse it', '>')

    # 16-bit YCbCr, packed 2x2 blocks and planar 2x1
    rgb16 = test_pattern(24, 16, 3, np.uint16, 23).astype(np.float64)
    y = 0.299 * rgb16[:, :, 0] + 0.587 * rgb16[:, :, 1] + 0.114 * rgb16[:, :, 2]
    cb = (rgb16[:, :, 2] - y) / 1.772 + 32768
    cr = (rgb16[:, :, 0] - y) / 1.402 + 32768
    q = lambda a: np.clip(np.round(a), 0, 65535).astype(np.uint16)

    def back(yy, cbb, crr):
        yy, cbb, crr = yy.astype(np.float64) * 65535 / 65535, (cbb.astype(np.float64) - 32768) * 32767 / 32767, (crr.astype(np.float64) - 32768) * 32767 / 32767
        r, b = yy + crr * 1.402, yy + cbb * 1.772
        gg = yy - crr * 1.402 * 0.299 / 0.587 - cbb * 1.772 * 0.114 / 0.587
        return np.clip(np.floor(np.stack([r, gg, b], axis=-1) + 0.5), 0, 65535).astype(np.uint16)

    why = '16-bit YCbCr without JPEG: libtiff converts YCbCr only at 8 bits, tifffile returns the stored samples'
    tags16 = [(532, 5, [(0, 1), (65535, 1), (32768, 1), (65535, 1), (32768, 1), (65535, 1)])]
    cbs, crs = q(cb.reshape(8, 2, 12, 2).mean(axis=(1, 3))), q(cr.reshape(8, 2, 12, 2).mean(axis=(1, 3)))
    blocks = np.concatenate([q(y).reshape(8, 2, 12, 2).transpose(0, 2, 1, 3).reshape(8, 12, 4), cbs[:, :, None], crs[:, :, None]], axis=2)
    up = lambda pl, fh, fv: np.repeat(np.repeat(pl, fv, axis=0), fh, axis=1)
    pth = hand('ycbcr16_2x2', dict(arr=np.zeros((16, 24, 3), np.uint16), photometric=6, rps=8, tags=tags16 + [(530, 3, [2, 2])],
                                   raw_chunks=[blocks[:4].astype('<u2').tobytes(), blocks[4:].astype('<u2').tobytes()]),
               back(q(y), up(cbs, 2, 2), up(crs, 2, 2)), why)
    write_tol(pth, 1)
    cbs, crs = q(cb.reshape(16, 1, 12, 2).mean(axis=(1, 3))), q(cr.reshape(16, 1, 12, 2).mean(axis=(1, 3)))
    pth = hand('be_ycbcr16_planar_2x1', dict(arr=np.zeros((16, 24, 3), np.uint16), photometric=6, planar=2, rps=16, tags=tags16 + [(530, 3, [2, 1])],
                                             raw_chunks=[q(y).astype('>u2').tobytes(), cbs.astype('>u2').tobytes(), crs.astype('>u2').tobytes()]),
               back(q(y), up(cbs, 2, 1), up(crs, 2, 1)), why, '>')
    write_tol(pth, 1)


def gen_lab():
    """CIELab / ICCLab / ITULab. Aardvark.PixImage.IO converts to sRGB with the textbook D50 Lab -> XYZ -> (Bradford) sRGB maths;
    the reference is LittleCMS (through Pillow's ImageCms: Lab D50 profile -> sRGB profile) applied to the samples
    tifffile read from the file. LittleCMS works on 8-bit Lab here, so 16-bit and ITULab samples are first rounded
    to 8-bit Lab - hence the tolerances (8-bit CIELab/ICCLab: 2 levels; ITULab and 16-bit data: the rounding to 8-bit Lab
    dominates), which are the measured distance between the two computations plus a margin."""
    from PIL import ImageCms
    srgb_p, lab_p = ImageCms.createProfile('sRGB'), ImageCms.createProfile('LAB')
    # NOOPTIMIZE: evaluate the real pipeline. By default LittleCMS interpolates 8-bit transforms in a 3D table,
    # which is off by up to 32 levels near the gamut boundary.
    to_lab = ImageCms.buildTransform(srgb_p, lab_p, 'RGB', 'LAB', flags=ImageCms.Flags.NOOPTIMIZE)
    to_rgb = ImageCms.buildTransform(lab_p, srgb_p, 'LAB', 'RGB', flags=ImageCms.Flags.NOOPTIMIZE)
    rgb = test_pattern(31, 23, 3, np.uint8, 41)
    rgb[0, :8] = [[0, 0, 0], [255, 255, 255], [255, 0, 0], [0, 255, 0], [0, 0, 255], [128, 128, 128], [255, 255, 0], [0, 255, 255]]
    # Pillow's LAB mode is the TIFF CIELab encoding: L 0..255, a and b as two's complement bytes
    pil = np.asarray(ImageCms.applyTransform(Image.fromarray(rgb), to_lab))
    L, A, B = pil[:, :, 0].astype(np.int32), pil[:, :, 1].astype(np.int8).astype(np.int32), pil[:, :, 2].astype(np.int8).astype(np.int32)

    def to_float(raw, pm):
        """Stored samples -> L*, a*, b* (the encodings Aardvark.PixImage.IO documents)."""
        r = raw[:, :, :3].astype(np.float64)
        if raw.dtype == np.uint8:
            l = r[:, :, 0] * 100 / 255
            if pm == 8:
                a, b = raw[:, :, 1].astype(np.int8).astype(np.float64), raw[:, :, 2].astype(np.int8).astype(np.float64)
            elif pm == 9:
                a, b = r[:, :, 1] - 128, r[:, :, 2] - 128
            else:
                a, b = (r[:, :, 1] - 128) * 170 / 255, (r[:, :, 2] - 96) * 200 / 255
        else:
            if pm == 8:
                l = r[:, :, 0] * 100 / 65535
                a, b = raw[:, :, 1].astype(np.int16) / 256.0, raw[:, :, 2].astype(np.int16) / 256.0
            elif pm == 9:
                l, a, b = r[:, :, 0] * 100 / 65280, r[:, :, 1] / 256 - 128, r[:, :, 2] / 256 - 128
            else:
                l, a, b = r[:, :, 0] * 100 / 65535, (r[:, :, 1] - 32768) * 170 / 65535, (r[:, :, 2] - 24576) * 200 / 65535
        return l, a, b

    def formula(raw, pm):
        """The documented conversion, in float64 (only used to report how far LittleCMS is from it)."""
        l, a, b = to_float(raw, pm)
        fy = (l + 16) / 116
        fx, fz = fy + a / 500, fy - b / 200
        g = lambda t: np.where(t > 6 / 29, t ** 3, 3 * (6 / 29) ** 2 * (t - 4 / 29))
        x, y, z = 0.96422 * g(fx), g(fy), 0.82521 * g(fz)
        lin = np.stack([3.1338561 * x - 1.6168667 * y - 0.4906146 * z, -0.9787684 * x + 1.9161415 * y + 0.0334540 * z,
                        0.0719453 * x - 0.2289914 * y + 1.4052427 * z], axis=-1).clip(0, 1)
        return np.where(lin <= 0.0031308, 12.92 * lin, 1.055 * lin ** (1 / 2.4) - 0.055)

    def make(name, raw_arr, pm, tol, **kw):
        def ref_from_raw(raw):
            l, a, b = to_float(raw, pm)
            lab8 = np.stack([np.round(l * 255 / 100).clip(0, 255), np.round(a).clip(-128, 127).astype(np.int64) & 255,
                             np.round(b).clip(-128, 127).astype(np.int64) & 255], axis=-1).astype(np.uint8)
            out = np.asarray(ImageCms.applyTransform(Image.fromarray(lab8, 'LAB'), to_rgb))
            scale = 255 if raw.dtype == np.uint8 else 65535
            mine = np.round(formula(raw, pm) * scale)
            ref = out.astype(raw.dtype) * (1 if raw.dtype == np.uint8 else 257)
            dist = float(np.abs(mine - ref).max())
            assert dist <= tol * 0.8, (name, dist, tol)
            notes.append(f'LittleCMS vs. formula: max {dist:.0f}, tolerance {tol}')
            return np.concatenate([ref, raw[:, :, 3:]], axis=2) if raw.shape[2] > 3 else ref
        notes = []
        p = dict(arr=raw_arr, photometric=pm, ref_from_raw=ref_from_raw, **kw)
        byteorder = p.pop('byteorder', '<')
        path = emit(name, p, subdir='lab', byteorder=byteorder, variants=('raw',))
        write_tol(path, tol)
        SUMMARY[-1] = (SUMMARY[-1][0], SUMMARY[-1][1] + ['LittleCMS'] + notes)

    u8 = lambda *c: np.stack(c, axis=-1).astype(np.uint8)
    u16 = lambda *c: np.stack(c, axis=-1).astype(np.uint16)
    alpha8 = pat(1, 'uint8', 31, 23, seed=9)
    make('cielab8', u8(L, A & 255, B & 255), 8, 2, rps=6)
    make('cielab8_lzw_planar', u8(L, A & 255, B & 255), 8, 2, compression=LZW, planar=2)
    make('cielab8_alpha', np.concatenate([u8(L, A & 255, B & 255), alpha8], axis=2), 8, 2, extrasamples=[2])
    make('icclab8', u8(L, A + 128, B + 128), 9, 2, compression=DEFLATE, predictor=2)
    make('cielab8_extra_unspecified', np.concatenate([u8(L, A & 255, B & 255), alpha8], axis=2), 8, 2, extrasamples=[0])
    make('itulab8', u8(L, np.round(A * 255 / 170 + 128).clip(0, 255), np.round(B * 255 / 200 + 96).clip(0, 255)), 10, 12, tile=(16, 16))
    make('cielab16', u16(L * 257, (A * 256) & 0xFFFF, (B * 256) & 0xFFFF), 8, 400)
    make('be_cielab16_deflate', u16(L * 257 + 100, (A * 256 + 77) & 0xFFFF, (B * 256 - 90) & 0xFFFF).clip(0, 65535).astype(np.uint16),
         8, 3200, compression=DEFLATE, predictor=2, byteorder='>')
    make('icclab16', u16(np.round(L * 65280 / 255), (A + 128) * 256, (B + 128) * 256), 9, 400)
    make('itulab16', u16(L * 257, np.round(A * 65535 / 170 + 32768).clip(0, 65535), np.round(B * 65535 / 200 + 24576).clip(0, 65535)), 10, 400)


def sgilog_rle(planes_rows):
    """Run length codes one row of byte planes the way SGILOG stores it (runs of 3+ equal bytes, else literals)."""
    out = bytearray()
    for row in planes_rows:
        i, n = 0, len(row)
        while i < n:
            run = 1
            while i + run < n and run < 127 and row[i + run] == row[i]:
                run += 1
            if run >= 3:
                out += bytes([126 + run, row[i]])
                i += run
                continue
            start = i
            i += 1
            while i < n and i - start < 127 and not (i + 2 < n and row[i] == row[i + 1] == row[i + 2]):
                i += 1
            out.append(i - start)
            out += row[start:i]
    return bytes(out)


def gen_logluv():
    """SGI LogLuv (32-bit) files written by libtiff through OpenCV, and a hand-packed LogL (16-bit luminance) file.
    Reference: libtiff's float decoding through OpenCV (which converts XYZ to linear RGB with its XYZ2BGR matrix)."""
    d = os.path.join(OUT, 'logluv')
    os.makedirs(d, exist_ok=True)
    rgb = np.abs(test_pattern(47, 31, 3, np.float32, 51)).astype(np.float32) * 0.6 + 0.01
    rgb[:4, :9] = 0            # black: coded as zero luminance
    rgb[4:8, :9] = 1.0         # flat area: runs
    rgb[10, 3] = [900.0, 0.5, 0.01]
    for name, extra in [('logluv32_strip', []), ('logluv32_multistrip', [cv2.IMWRITE_TIFF_ROWSPERSTRIP, 7])]:
        path = os.path.join(d, name + '.tif')
        assert cv2.imwrite(path, np.ascontiguousarray(rgb[:, :, ::-1]), [cv2.IMWRITE_TIFF_COMPRESSION, 34676] + extra)
        with tifffile.TiffFile(path) as t:
            pg = t.pages[0]
            assert int(pg.photometric) == 32845 and int(pg.compression) == 34676, (pg.photometric, pg.compression)
            desc = f'{len(pg.dataoffsets)} strip(s)'
        ref = ref_cv2(path)
        assert ref.dtype == np.float32 and float(np.abs(ref - rgb).max() / 900) < 0.05
        write_ref(path + '.ref', ref)
        write_tol(path, 2e-3)
        SUMMARY.append(('logluv/' + name, [f'cv2 (libtiff SGILOG float + XYZ2BGR), written by cv2/libtiff, {desc}']))

    # The rest is written and read back by libtiff itself (ctypes): reading with SGILOGDATAFMT_FLOAT gives CIE XYZ
    # (or luminance) floats, which is the `.raw.ref` (ConvertToRgb = false); the RGB reference is OpenCV's XYZ2RGB
    # conversion of exactly these floats.
    lib = LibTiff()
    xyz = cv2.cvtColor(rgb, cv2.COLOR_RGB2XYZ)
    fmt = [(LibTiff.SGILOGDATAFMT, 0)]

    def luv(name, compression, tol, **kw):
        path = os.path.join(d, name + '.tif')
        lib.write(path, xyz, compression, 32845, pseudo=fmt if compression in (34676, 34677) else (), **kw)
        got = lib.read(path, np.float32, xyz.shape, pseudo=fmt if compression in (34676, 34677) else ())
        with tifffile.TiffFile(path) as t:
            pg = t.pages[0]
            assert int(pg.compression) == compression and int(pg.photometric) == 32845
            desc = f'{len(pg.dataoffsets)} strip(s), {t.byteorder}'
        write_ref(path + '.ref', cv2.cvtColor(got, cv2.COLOR_XYZ2RGB))
        write_ref(path + '.raw.ref', got)
        write_tol(path, tol)
        write_tol(path + '.raw', tol)
        SUMMARY.append(('logluv/' + name, [f'libtiff float decoding (XYZ) + cv2 XYZ2RGB; written by libtiff, {desc}']))

    luv('logluv24_strips', 34677, 2e-3, rps=5)
    luv('logluv24_be', 34677, 2e-3, big_endian=True)
    luv('logluv32_libtiff_be', 34676, 2e-3, rps=9, big_endian=True)
    luv('logluv_float_uncompressed', 1, 1e-4, rps=6)        # plain XYZ floats under the LogLuv photometric
    luv('logluv_float_lzw_be', 5, 1e-4, big_endian=True)

    # LogL: 16-bit log luminance
    y = np.abs(test_pattern(37, 21, 1, np.float32, 52)) * 3 + 0.02
    y[:3, :10] = 0
    y[3:6, :10] = 0.25
    y[7, 3] = 5000.0
    # (little endian only: libtiff does not read back its own big endian LogL files correctly)
    for name, kw in [('logl16_strips', dict(rps=8)), ('logl16_strip', dict())]:
        path = os.path.join(d, name + '.tif')
        lib.write(path, y, 34676, 32844, pseudo=fmt, **kw)
        got = lib.read(path, np.float32, y.shape, pseudo=fmt)
        assert float(np.abs(got - y).max() / 5000) < 0.01
        write_ref(path + '.ref', got)
        write_tol(path, 1e-5)
        SUMMARY.append(('logluv/' + name, ['libtiff float decoding; written by libtiff']))
    path = os.path.join(d, 'logl_float_uncompressed.tif')
    lib.write(path, y, 1, 32844)
    write_ref(path + '.ref', lib.read(path, np.float32, y.shape))
    SUMMARY.append(('logluv/logl_float_uncompressed', ['libtiff; written by libtiff']))


def gen_pixarlog():
    """PixarLog (compression 32909), written by libtiff from float data. libtiff tags every such file as 8-bit, so
    the default reference is its 8-bit reading; `.u16.ref` / `.f32.ref` are its 16-bit and float readings
    (PIXARLOGDATAFMT), matching TiffDecodeOptions.PixarLogOutput."""
    lib = LibTiff()
    d = os.path.join(OUT, 'pixarlog')
    os.makedirs(d, exist_ok=True)
    P = LibTiff.PIXARLOGDATAFMT
    rgb = np.abs(test_pattern(41, 29, 3, np.float32, 71)).astype(np.float32) * 0.45
    rgb[:3, :8] = 0
    rgb[3:6, :8] = 1.0
    rgb[6, :4] = [[24.0, 0.5, 2.0]] * 4          # far above white: the 8 and 16 bit readings clamp
    rgba = np.concatenate([rgb, np.clip(np.abs(test_pattern(41, 29, 1, np.float32, 72)) * 0.4, 0, 1)], axis=2).astype(np.float32)
    for name, arr, kw in [('pixarlog_rgb', rgb, dict()), ('pixarlog_rgb_strips', rgb, dict(rps=4)),
                          # (no big endian file: libtiff on this little endian machine writes garbage for it)
                          ('pixarlog_rgba', rgba, dict(rps=7, tags=((338, 2),)))]:
        path = os.path.join(d, name + '.tif')
        lib.write(path, arr, 32909, 2, pseudo=[(P, 5)], **kw)
        with tifffile.TiffFile(path) as t:
            pg = t.pages[0]
            assert int(pg.compression) == 32909 and pg.bitspersample == 8, (pg.compression, pg.bitspersample)
            desc = f'{len(pg.dataoffsets)} strip(s), {t.byteorder}'
        f = lib.read(path, np.float32, arr.shape, pseudo=[(P, 5)])
        assert float(np.abs(f - arr).max()) < 0.06, name   # (1.004 per code step: 0.05 at the brightest pixel)
        write_ref(path + '.ref', lib.read(path, np.uint8, arr.shape))
        write_ref(path + '.u16.ref', lib.read(path, np.uint16, arr.shape, pseudo=[(P, 4)]))
        write_ref(path + '.f32.ref', f)
        SUMMARY.append(('pixarlog/' + name, [f'libtiff (8 bit, 16 bit and float readings); written by libtiff, {desc}']))

    # libtiff 4.7 cannot decode its own single-channel PixarLog files (its reading is far from what was written),
    # so the gray case has no usable reference decoder: the reference is the source rounded through the code table.
    g = np.clip(np.abs(test_pattern(41, 29, 1, np.float32, 73)) * 0.45, 0, 2).astype(np.float32)
    du = os.path.join(OUT, 'unverified')
    os.makedirs(du, exist_ok=True)
    path = os.path.join(du, 'pixarlog_gray.tif')
    lib.write(path, g, 32909, 1, pseudo=[(P, 5)], rps=8)
    bad = lib.read(path, np.float32, g.shape, pseudo=[(P, 5)])
    assert float(np.abs(bad - g).max()) > 1, 'libtiff reads gray PixarLog correctly now: use it as the reference'
    c = np.log(1.004)
    nlin = int(1 / c)
    c = 1.0 / nlin
    b = np.exp(-c * 1250)
    table = np.concatenate([np.arange(nlin) * (b * c * np.e), b * np.exp(c * np.arange(nlin, 2048))]).astype(np.float32)
    with tifffile.TiffFile(path) as t:
        pg = t.pages[0]
        rows = []
        with open(path, 'rb') as fh:
            for o, n in zip(pg.dataoffsets, pg.databytecounts):
                fh.seek(o)
                codes = np.frombuffer(zlib.decompress(fh.read(n)), '<u2').astype(np.int64)
                rows.append(np.cumsum(codes[:codes.size // 41 * 41].reshape(-1, 41), axis=1) & 0x7FF)
    codes = np.concatenate(rows)[:29]
    ref = table[codes][:, :, None]
    assert float(np.abs(ref - g).max()) < 0.01
    v = ref.astype(np.float64) * 255.0 + 0.5
    write_ref(path + '.ref', np.where(v > 255, 255, v).astype(np.uint8))
    write_ref(path + '.f32.ref', ref)
    reason = 'single-channel PixarLog: libtiff 4.7 misreads its own file (stride 1), nothing else decodes PixarLog'
    UNVERIFIED.append(('pixarlog_gray', reason))
    SUMMARY.append(('unverified/pixarlog_gray', ['UNVERIFIED: ' + reason]))


def gen_jpeg_deep():
    """JPEG-in-TIFF beyond 8-bit baseline: 12-bit DCT (written by tifffile / libjpeg-turbo) and lossless JPEG
    (SOF3, DNG style) streams from imagecodecs wrapped into strips and tiles. References: tifffile + imagecodecs."""
    rng = np.random.default_rng(61)
    d12 = os.path.join(OUT, 'jpeg12')
    os.makedirs(d12, exist_ok=True)
    g12 = (test_pattern(70, 52, 1, np.uint16, 33)[:, :, 0] >> 4)
    rgb12 = (test_pattern(70, 52, 3, np.uint16, 34) >> 4)
    for name, arr, kw in [
        ('jpeg12_gray_strips', g12, dict(rowsperstrip=16)),
        ('jpeg12_gray_tiles', g12, dict(tile=(32, 32))),
        ('jpeg12_ycbcr_strips', rgb12, dict(photometric='ycbcr', rowsperstrip=32)),
        ('jpeg12_ycbcr_tiles', rgb12, dict(photometric='ycbcr', tile=(48, 32))),
        ('jpeg12_ycbcr_420_strips', rgb12, dict(photometric='rgb')),
    ]:
        path = os.path.join(d12, name + '.tif')
        tifffile.imwrite(path, arr, compression='jpeg', bitspersample=12, metadata=None, **kw)
        with tifffile.TiffFile(path) as t:
            pg = t.pages[0]
            assert int(pg.compression) == 7 and pg.bitspersample == 12, (name, pg.compression, pg.bitspersample)
            desc = f'photometric {int(pg.photometric)}, {"tiles" if pg.is_tiled else "strips"} x{len(pg.dataoffsets)}' \
                   f'{", JPEGTables" if 347 in pg.tags else ""}'
        ref = ref_tifffile(path)
        # (tifffile's writer is quirky here: asked for 'rgb' it writes a proper 4:2:0 YCbCr file, asked for 'ycbcr' it
        # stores the given samples as Y, Cb, Cr unconverted. Both are valid YCbCr files; the reference is what
        # libjpeg-turbo makes of the streams.)
        assert ref.dtype == np.uint16 and int(pg.photometric) == (1 if arr.ndim == 2 else 6)
        write_ref(path + '.ref', ref)
        SUMMARY.append(('jpeg12/' + name, [f'tifffile (libjpeg-turbo 12 bit): {desc}']))

    g16 = rng.integers(0, 65536, (37, 50), dtype=np.uint16)
    g16[:8] = test_pattern(50, 8, 1, np.uint16, 35)[:, :, 0]
    g8 = test_pattern(50, 37, 1, np.uint8, 36)[:, :, 0]
    rgb8 = test_pattern(50, 37, 3, np.uint8, 37)
    rgb16 = test_pattern(50, 37, 3, np.uint16, 38)

    def lossless(a, bits):
        return imagecodecs.jpeg8_encode(a, lossless=True, bitspersample=bits, colorspace='rgb' if a.ndim == 3 and a.shape[2] == 3 else None,
                                        outcolorspace='rgb' if a.ndim == 3 and a.shape[2] == 3 else None)

    def wrap(name, a, bits, tile=None, rps=None, reshape=None, **extra):
        """a: (H,W[,C]). reshape: encode every strip/tile as a frame of that many components (DNG colour filter data)."""
        h, w = a.shape[:2]
        c = 1 if a.ndim == 2 else a.shape[2]
        chunks = []
        if tile:
            for y in range(0, h, tile[1]):
                for x in range(0, w, tile[0]):
                    blk = a[y:y + tile[1], x:x + tile[0]]
                    pad = [(0, tile[1] - blk.shape[0]), (0, tile[0] - blk.shape[1])] + ([(0, 0)] if a.ndim == 3 else [])
                    blk = np.ascontiguousarray(np.pad(blk, pad))
                    if reshape:
                        blk = blk.reshape(tile[1], tile[0] // reshape, reshape)
                    chunks.append(lossless(blk, bits))
            page = dict(tile=tile)
        else:
            rps = rps or h
            chunks = [lossless(np.ascontiguousarray(a[y:y + rps]), bits) for y in range(0, h, rps)]
            page = dict(rps=rps)
        page.update(arr=a if a.ndim == 3 else a[:, :, None], bits=bits if bits not in (8, 16) else a.dtype.itemsize * 8,
                    compression=7, raw_chunks=chunks, **extra)
        if bits not in (8, 16):
            page['bits'] = [bits] * c
        dd = os.path.join(OUT, 'jpegll')
        os.makedirs(dd, exist_ok=True)
        path = os.path.join(dd, name + '.tif')
        write_tiff(path, [page])
        src = a if a.ndim == 3 else a[:, :, None]
        try:
            ref = ref_tifffile(path)
            who = 'tifffile + imagecodecs'
        except Exception:
            ref = None
        if ref is None or not same(ref, src):
            # tifffile does not undo the DNG frame reshaping: decode the tile streams with imagecodecs instead
            assert reshape and tile
            ref = np.zeros_like(src)
            i = 0
            for y in range(0, h, tile[1]):
                for x in range(0, w, tile[0]):
                    blk = imagecodecs.jpeg8_decode(chunks[i]).reshape(tile[1], tile[0], c)
                    ref[y:y + tile[1], x:x + tile[0]] = blk[:h - y, :w - x]
                    i += 1
            who = 'imagecodecs (libjpeg-turbo) per tile, frames flattened to rows'
        assert same(ref, src), name
        write_ref(path + '.ref', ref)
        SUMMARY.append(('jpegll/' + name, [who]))

    wrap('jpegll_gray16_strips', g16, 16, rps=10)
    wrap('jpegll_gray16_tiles', g16, 16, tile=(32, 16))
    wrap('jpegll_gray12', g16 >> 4, 12, rps=37)
    wrap('jpegll_gray10_tiles', g16 >> 6, 10, tile=(16, 32))
    wrap('jpegll_gray8', g8, 8, rps=16)
    wrap('jpegll_rgb8_tiles', rgb8, 8, tile=(32, 32), photometric=2)
    wrap('jpegll_rgb16_strips', rgb16, 16, rps=19, photometric=2)
    wrap('jpegll_dng_cfa16_2comp_tiles', g16, 16, tile=(32, 16), reshape=2)
    wrap('jpegll_dng_cfa12_2comp_tile', g16 >> 4, 12, tile=(64, 48), reshape=2)


def parse_jpeg(b):
    """Splits a baseline JPEG into the pieces an old-style JPEG TIFF spreads over tags."""
    p = 2
    q, dc, ac, sof, dri = {}, {}, {}, None, 0
    while True:
        assert b[p] == 0xFF, p
        m = b[p + 1]
        n = struct.unpack('>H', b[p + 2:p + 4])[0]
        seg = b[p + 4:p + 2 + n]
        if m == 0xDB:
            for i in range(0, len(seg), 65):
                q[seg[i] & 15] = seg[i + 1:i + 65]
        elif m == 0xC4:
            i = 0
            while i < len(seg):
                k = sum(seg[i + 1:i + 17])
                (dc if seg[i] >> 4 == 0 else ac)[seg[i] & 15] = seg[i + 1:i + 17 + k]
                i += 17 + k
        elif m in (0xC0, 0xC3):
            h, w, k = struct.unpack('>HHB', seg[1:6])
            sof = (h, w, [(seg[6 + 3 * c], seg[7 + 3 * c], seg[8 + 3 * c]) for c in range(k)])
        elif m == 0xDD:
            dri = struct.unpack('>H', seg)[0]
        elif m == 0xDA:
            sos = {seg[1 + 2 * c]: seg[2 + 2 * c] for c in range(seg[0])}
            k = 1 + 2 * seg[0]
            return dict(q=q, dc=dc, ac=ac, sof=sof, sos=sos, dri=dri, scan_start=p + 2 + n, scan_end=b.rindex(b'\xff\xd9'),
                        ss=seg[k], al=seg[k + 2] & 15)
        p += 2 + n


def gen_ojpeg():
    """Old-style JPEG (compression 6), assembled by hand from baseline JPEG files in the three layouts of the TIFF 6.0
    spec: a complete interchange stream the strip points into, a tables-only interchange stream, and tables in the
    JPEGQTables / JPEGDCTables / JPEGACTables tags (one strip, or one strip per restart interval).

    Reference: libjpeg-turbo (Pillow) decoding the JPEG stream the file was assembled from. libtiff reads every one
    of these files too (asserted here), but its old-JPEG codec hands out subsampled YCbCr that is then box-upsampled
    and converted by libtiff's own tables: identical for 4:4:4 and gray, up to 16 levels off at chroma edges
    for 4:2:0. The distance to libtiff is reported in the summary."""
    # a smooth picture, so that libtiff's box-upsampled reading stays comparable
    rgb = np.asarray(Image.fromarray(test_pattern(14, 11, 3, np.uint8, 31)).resize((70, 52), Image.BICUBIC))
    gray = np.ascontiguousarray(rgb[:, :, 1])

    def jpg(a, **kw):
        b = io.BytesIO()
        Image.fromarray(a).save(b, 'JPEG', quality=90, **kw)
        return b.getvalue()

    def finish(name, page, j, exp):
        d = os.path.join(OUT, 'ojpeg')
        os.makedirs(d, exist_ok=True)
        path = os.path.join(d, name + '.tif')
        write_tiff(path, [page])
        lib = np.asarray(Image.open(path).convert('RGB' if exp.ndim == 3 else 'L'))
        dist = int(np.abs(lib.astype(int) - exp.astype(int)).max())
        mean = float(np.abs(lib.astype(int) - exp.astype(int)).mean())
        assert mean < 3, (name, mean)
        write_ref(path + '.ref', exp)
        SUMMARY.append(('ojpeg/' + name, [f'libjpeg-turbo on the embedded stream; libtiff (Pillow) reads the file: max {dist}, mean {mean:.2f} away']))

    def layout(j):
        info = parse_jpeg(j)
        h, w, comps = info['sof']
        hv = comps[0][1]
        tags = [(512, 3, [1])]
        if len(comps) == 3:
            tags.append((530, 3, [hv >> 4, hv & 15]))
        page = dict(arr=np.zeros((h, w, len(comps)), np.uint8), photometric=6 if len(comps) == 3 else 1, compression=6, tags=tags)
        return info, page, np.asarray(Image.open(io.BytesIO(j)))

    # 1. JPEGInterchangeFormat = the complete stream, the single strip is its scan data
    for name, j in [('ojpeg_interchange_420', jpg(rgb, subsampling=2)), ('ojpeg_interchange_444', jpg(rgb, subsampling=0)),
                    ('ojpeg_interchange_gray', jpg(gray))]:
        info, page, exp = layout(j)
        page.update(prefix=j, rps=exp.shape[0], raw_chunks=[], strips=([8 + info['scan_start']], [info['scan_end'] + 2 - info['scan_start']]))
        page['tags'] += [(513, 4, [8]), (514, 4, [len(j)])]
        finish(name, page, j, exp)

    # 2. tables in tags; raw entropy coded data in the strip(s)
    def tables_page(j, per_interval):
        info, page, exp = layout(j)
        h, w, comps = info['sof']
        blobs, pos = [], 8
        offs = {519: [], 520: [], 521: []}
        for tag, pick in ((519, lambda c: info['q'][c[2]]), (520, lambda c: info['dc'][info['sos'][c[0]] >> 4]),
                          (521, lambda c: info['ac'][info['sos'][c[0]] & 15])):
            for c in comps:
                offs[tag].append(pos)
                blobs.append(pick(c))
                pos += len(blobs[-1])
        scan = j[info['scan_start']:info['scan_end']]
        parts, rps = [scan], h
        if per_interval:
            # one restart interval (= one row of MCUs) per strip; libtiff puts the restart markers back in
            assert info['dri'] > 0
            parts, start, i = [], 0, 0
            while i < len(scan) - 1:
                if scan[i] == 0xFF and 0xD0 <= scan[i + 1] <= 0xD7:
                    parts.append(scan[start:i])
                    start = i = i + 2
                else:
                    i += 1
            parts.append(scan[start:])
            rps = 8 * (comps[0][1] & 15)
            assert len(parts) == -(-h // rps), (len(parts), h, rps)
            page['tags'].append((515, 3, [info['dri']]))
        page.update(prefix=b''.join(blobs), rps=rps, raw_chunks=parts)
        page['tags'] += [(tag, 4, o) for tag, o in offs.items()]
        return page, exp

    for name, j, per in [('ojpeg_tables_444', jpg(rgb, subsampling=0), False), ('ojpeg_tables_420', jpg(rgb, subsampling=2), False),
                         ('ojpeg_tables_gray', jpg(gray), False),
                         ('ojpeg_tables_420_restart_strips', jpg(rgb, subsampling=2, restart_marker_rows=1), True),
                         ('ojpeg_tables_422_restart_strips', jpg(rgb, subsampling=1, restart_marker_rows=1), True),
                         ('ojpeg_tables_gray_restart_strips', jpg(gray, restart_marker_rows=1), True)]:
        page, exp = tables_page(j, per)
        finish(name, page, j, exp)

    def libtiff_note(path, exp):
        try:
            lib = np.asarray(Image.open(path).convert('RGB' if exp.ndim == 3 and exp.shape[2] == 3 else 'L'))
            dist = int(np.abs(lib.astype(int) - exp.reshape(lib.shape).astype(int)).max())
            return f'libtiff (Pillow) reads the file: max {dist} away' if dist <= 40 else \
                f'libtiff (Pillow) opens the file but does not decode this layout correctly (max {dist} away)'
        except Exception as ex:
            return f'libtiff (Pillow) does not read this layout ({str(ex)[:40]})'

    def raw_file(name, page, exp, who, tol=0):
        dd = os.path.join(OUT, 'ojpeg')
        path = os.path.join(dd, name + '.tif')
        write_tiff(path, [page])
        write_ref(path + '.ref', exp)
        if tol:
            write_tol(path, tol)
        SUMMARY.append(('ojpeg/' + name, [who + '; ' + libtiff_note(path, exp)]))

    # 4. tiles: every tile is a frame of its own, sharing the tables in the tags
    for name, sub, tw in [('ojpeg_tables_tiled_444', 0, 32), ('ojpeg_tables_tiled_420', 2, 48)]:
        th = 32
        h, w = rgb.shape[:2]
        parts, infos = [], []
        exp = np.zeros_like(rgb)
        for y in range(0, h, th):
            for x in range(0, w, tw):
                blk = rgb[y:y + th, x:x + tw]
                blk = np.pad(blk, ((0, th - blk.shape[0]), (0, tw - blk.shape[1]), (0, 0)), mode='edge')
                j = jpg(np.ascontiguousarray(blk), subsampling=sub)
                info = parse_jpeg(j)
                infos.append(info)
                parts.append(j[info['scan_start']:info['scan_end']])
                dec = np.asarray(Image.open(io.BytesIO(j)))
                exp[y:y + th, x:x + tw] = dec[:h - y, :w - x]
        assert all(i['q'] == infos[0]['q'] and i['dc'] == infos[0]['dc'] and i['ac'] == infos[0]['ac'] for i in infos)
        info = infos[0]
        comps = info['sof'][2]
        blobs, pos, offs = [], 8, {519: [], 520: [], 521: []}
        for tag, pick in ((519, lambda c: info['q'][c[2]]), (520, lambda c: info['dc'][info['sos'][c[0]] >> 4]),
                          (521, lambda c: info['ac'][info['sos'][c[0]] & 15])):
            for c in comps:
                offs[tag].append(pos)
                blobs.append(pick(c))
                pos += len(blobs[-1])
        hv = comps[0][1]
        page = dict(arr=np.zeros((h, w, 3), np.uint8), photometric=6, compression=6, tile=(tw, th), prefix=b''.join(blobs), raw_chunks=parts,
                    tags=[(512, 3, [1]), (530, 3, [hv >> 4, hv & 15])] + [(t, 4, o) for t, o in offs.items()])
        raw_file(name, page, exp, 'libjpeg-turbo on the per-tile streams')

    # 5. PlanarConfiguration 2: one single-component frame per plane (Y, Cb, Cr), each with the tables of its index.
    #    Reference: Pillow decodes the three plane streams and converts YCbCr to RGB (Aardvark.PixImage.IO uses the TIFF
    #    coefficient maths in floats, hence the tolerance).
    ycc = np.asarray(Image.fromarray(rgb).convert('YCbCr'))
    for name, per in [('ojpeg_planar_444', False), ('ojpeg_planar_444_restart_strips', True)]:
        h, w = rgb.shape[:2]
        streams = [jpg(np.ascontiguousarray(ycc[:, :, c]), **(dict(restart_marker_rows=1) if per else {})) for c in range(3)]
        infos = [parse_jpeg(j) for j in streams]
        dec = np.stack([np.asarray(Image.open(io.BytesIO(j))) for j in streams], axis=-1)
        exp = np.asarray(Image.fromarray(dec, 'YCbCr').convert('RGB'))
        blobs, pos, offs = [], 8, {519: [], 520: [], 521: []}
        for tag, key in ((519, 'q'), (520, 'dc'), (521, 'ac')):
            for i in infos:
                offs[tag].append(pos)
                blobs.append(i[key][0])
                pos += len(blobs[-1])
        parts = []
        for j, i in zip(streams, infos):
            scan = j[i['scan_start']:i['scan_end']]
            if per:
                start = k = 0
                while k < len(scan) - 1:
                    if scan[k] == 0xFF and 0xD0 <= scan[k + 1] <= 0xD7:
                        parts.append(scan[start:k])
                        start = k = k + 2
                    else:
                        k += 1
                parts.append(scan[start:])
            else:
                parts.append(scan)
        tags = [(512, 3, [1]), (530, 3, [1, 1])] + [(t, 4, o) for t, o in offs.items()]
        if per:
            tags.append((515, 3, [infos[0]['dri']]))
        page = dict(arr=np.zeros((h, w, 3), np.uint8), photometric=6, compression=6, planar=2, rps=8 if per else h, prefix=b''.join(blobs),
                    raw_chunks=parts, tags=tags)
        raw_file(name, page, exp, 'libjpeg-turbo on the per-plane streams + Pillow YCbCr to RGB', tol=2)

    # 6. lossless (JPEGProc 14): predictor and point transform in tags, DC tables only
    g16 = test_pattern(70, 52, 1, np.uint16, 39)[:, :, 0]
    for name, a, bits, pm in [('ojpeg_lossless_gray8', gray, 8, 1), ('ojpeg_lossless_gray16', g16, 16, 1), ('ojpeg_lossless_gray12', g16 >> 4, 12, 1),
                              ('ojpeg_lossless_rgb8', rgb, 8, 2)]:
        c3 = a.ndim == 3
        j = imagecodecs.jpeg8_encode(np.ascontiguousarray(a), lossless=True, bitspersample=bits, colorspace='rgb' if c3 else None,
                                     outcolorspace='rgb' if c3 else None)
        info = parse_jpeg(j)
        h, w, comps = info['sof']
        exp = imagecodecs.jpeg8_decode(j)
        assert np.array_equal(exp, a)
        blobs, pos, dcs = [], 8, []
        for c in comps:
            dcs.append(pos)
            blobs.append(info['dc'][info['sos'][c[0]] >> 4])
            pos += len(blobs[-1])
        arr = a if c3 else a[:, :, None]
        page = dict(arr=np.zeros_like(arr), photometric=pm, compression=6, rps=h, prefix=b''.join(blobs),
                    raw_chunks=[j[info['scan_start']:info['scan_end']]],
                    tags=[(512, 3, [14]), (517, 3, [info['ss']] * len(comps)), (518, 3, [info['al']] * len(comps)), (520, 4, dcs)])
        if bits not in (8, 16):
            page['bits'] = [bits] * len(comps)
        raw_file(name, page, exp if c3 else exp[:, :, None], 'imagecodecs (libjpeg-turbo) on the embedded lossless stream')

    # 3. JPEGInterchangeFormat holds only the header with the tables; the strip is the scan data stored elsewhere
    j = jpg(rgb, subsampling=2)
    info, page, exp = layout(j)
    header = j[:info['scan_start']]
    page.update(prefix=header, rps=exp.shape[0], raw_chunks=[j[info['scan_start']:info['scan_end'] + 2]])
    page['tags'] += [(513, 4, [8]), (514, 4, [len(header)])]
    finish('ojpeg_header_then_strip_420', page, j, exp)


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
            else:
                shutil.rmtree(full)
    os.makedirs(OUT, exist_ok=True)
    gen_matrix()
    gen_bits_and_photometric()
    gen_extras()
    gen_structure()
    gen_external()
    gen_depths()
    gen_more_codecs()
    gen_foreign()
    gen_lerc()
    gen_lab()
    gen_logluv()
    gen_pixarlog()
    gen_structures()
    gen_ccitt()
    gen_ccitt_uncompressed()
    gen_ycbcr()
    gen_jpeg()
    gen_jpeg_deep()
    gen_ojpeg()
    for n, by in SUMMARY:
        assert by, n
        print(f'{n:45s} {", ".join(by)}')
    size = sum(os.path.getsize(os.path.join(dp, f)) for dp, _, fs in os.walk(OUT) for f in fs)
    print(f'\n{len(SUMMARY)} fixtures, {size / 1024:.0f} KB total')
    print(f'{len(UNVERIFIED)} fixtures that no installed third-party decoder can read (reference derived from the source array):')
    for n, why in UNVERIFIED:
        print(f'  unverified/{n}: {why}')


if __name__ == '__main__':
    main()
