"""Generates the JPEG 2000 fixtures in tests/fixtures/jp2 (name.ext + name.ext.ref).

Encoders: OpenJPEG (python `imagecodecs`, and its `opj_compress` tool for everything the binding does not expose:
code-block styles, precincts, progression order changes, region of interest, SOP / EPH, tile-parts, image and tile
offsets, component subsampling), Pillow (also OpenJPEG, other parameter paths) and OpenJPH (`imagecodecs`,
`ojph_compress`) for HTJ2K. JP2 containers with palettes, channel definitions, colour spaces and metadata boxes
are assembled here around codestreams of those encoders.

References: OpenJPEG. Plain files (no palette, no colour space conversion, no subsampling) are decoded with
imagecodecs.jpeg2k_decode; where a second decoder exists the result is cross-checked against it (OpenJPH for
reversible HT streams, Pillow for 8 bit files). Files that need colour handling are decoded by `opj_decompress`.
The .ref holds that result in PixCodec's conventions (see Jp2DecodeOptions): the image has the size of the reference
grid and unsigned samples below 8 bit are scaled to 8 bit.

The OpenJPEG / OpenJPH command line tools are not installed system-wide; point OPJ_BIN / OJPH_BIN at directories
holding opj_compress, opj_decompress / ojph_compress (the defaults are the build directories under .tmp/ described in
tests/fixtures/jp2/README.md). Fixtures whose tool is missing are left as they are.

usage: gen_jp2.py            (re)generate everything the available tools allow
"""
import glob
import io
import os
import shutil
import struct
import subprocess
import sys
import tempfile

import numpy as np

from pxref import write_ref, test_pattern

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, '..', 'fixtures', 'jp2')
ROOT = os.path.join(HERE, '..', '..')
OPJ_BIN = os.environ.get('OPJ_BIN', os.path.join(ROOT, '.tmp', 'opj-build', 'bin'))
OJPH_BIN = os.environ.get('OJPH_BIN', os.path.join(ROOT, '.tmp', 'ojph-build', 'src', 'apps'))


def tool(name):
    for d in (OPJ_BIN, os.path.join(OJPH_BIN, name), OJPH_BIN):
        p = os.path.join(d, name)
        if os.path.isfile(p):
            return p
    return shutil.which(name)


OPJ_COMPRESS, OPJ_DECOMPRESS, OJPH_COMPRESS = tool('opj_compress'), tool('opj_decompress'), tool('ojph_compress')
TMP = tempfile.mkdtemp(prefix='gen_jp2_', dir=os.environ.get('GEN_JP2_TMP'))
made, skipped, failed = [], [], []


# ---------------------------------------------------------------- references

def read_pgx(path):
    with open(path, 'rb') as f:
        hdr = f.readline().decode().split()
        big = hdr[1] == 'ML'
        rest = ' '.join(hdr[2:]).replace('+', '+ ').replace('-', '- ').split()
        signed = rest[0] == '-'
        if rest[0] in '+-':
            rest = rest[1:]
        depth, w, h = int(rest[0]), int(rest[1]), int(rest[2])
        nb = 1 if depth <= 8 else 2 if depth <= 16 else 4
        dt = np.dtype(('>' if big else '<') + ('i' if signed else 'u') + str(nb))
        return np.frombuffer(f.read(w * h * nb), dt).reshape(h, w), depth, signed


def siz(data):
    """(x0, y0, x1, y1, [(depth, signed, dx, dy)]) of the codestream inside data."""
    i = data.find(b'\xff\x4f\xff\x51')
    p = i + 6
    rsiz, x1, y1, x0, y0, tw, th, tx, ty, nc = struct.unpack('>HIIIIIIIIH', data[p:p + 36])
    comps = []
    for c in range(nc):
        s, dx, dy = struct.unpack('>BBB', data[p + 36 + 3 * c:p + 39 + 3 * c])
        comps.append(((s & 127) + 1, bool(s & 128), dx, dy))
    return x0, y0, x1, y1, comps


def out_dtype(depth, signed):
    return np.dtype(('i' if signed else 'u') + str(1 if depth <= 8 else 2 if depth <= 16 else 4))


def to_convention(a, depth, signed):
    """Sample type and scaling PixCodec returns for components of this depth."""
    a = np.asarray(a)
    if not signed and depth < 8:
        m = (1 << depth) - 1
        return ((a.astype(np.int64) * 255 + m // 2) // m).astype(np.uint8)
    return a.astype(out_dtype(depth, signed))


def opj_components(path, extra=()):
    """Every component as opj_decompress writes it to PGX: list of (array, depth, signed)."""
    d = tempfile.mkdtemp(dir=TMP)
    r = subprocess.run([OPJ_DECOMPRESS, '-i', path, '-o', os.path.join(d, 'o.pgx'), *extra], capture_output=True, text=True)
    fs = sorted(glob.glob(os.path.join(d, 'o_*.pgx')), key=lambda s: int(s.rsplit('_', 1)[1][:-4])) or glob.glob(os.path.join(d, 'o.pgx'))
    if r.returncode != 0 or not fs:
        raise RuntimeError('opj_decompress failed: ' + r.stdout[-300:] + r.stderr[-300:])
    return [read_pgx(f) for f in fs]


def ref_plain(data, ht=False):
    """Reference of a file whose components map one to one to channels: OpenJPEG through imagecodecs."""
    import imagecodecs
    x0, y0, x1, y1, comps = siz(data)
    a = imagecodecs.jpeg2k_decode(data)
    if a.ndim == 2:
        a = a[:, :, None]
    elif a.shape[2] != len(comps) and a.shape[0] == len(comps):
        a = np.moveaxis(a, 0, -1)
    assert a.shape == (y1 - y0, x1 - x0, len(comps)), (a.shape, comps)
    depth, signed = max(c[0] for c in comps), comps[0][1]
    # second opinions
    if ht and all(c[0] <= 16 for c in comps):
        try:
            b = imagecodecs.htj2k_decode(data)
            if b.ndim == 2:
                b = b[:, :, None]
            elif b.shape[2] != len(comps) and b.shape[0] == len(comps):
                b = np.moveaxis(b, 0, -1)
            d = np.abs(a.astype(np.int64) - b.astype(np.int64))
            # OpenJPH wraps where a lossy sample leaves the range; OpenJPEG clamps
            wrapped = d >= (1 << depth) - 2
            assert d[~wrapped].max(initial=0) <= (1 if depth <= 8 else 1 << (depth - 10)), 'OpenJPEG and OpenJPH disagree'
        except (ValueError, RuntimeError):
            pass
    if depth == 8 and not signed and len(comps) in (1, 3, 4) and all(c[0] == 8 for c in comps):
        try:
            from PIL import Image
            b = np.asarray(Image.open(io.BytesIO(data)))
            if b.ndim == 2:
                b = b[:, :, None]
            if b.shape == a.shape:
                assert np.abs(a.astype(int) - b.astype(int)).max() <= 1, 'OpenJPEG (imagecodecs) and Pillow disagree'
        except (OSError, SyntaxError, ValueError):
            pass
    return to_convention(a, depth, signed)


def ref_image(path):
    """Reference of a file that needs colour handling: what opj_decompress writes as an image."""
    import tifffile
    from PIL import Image
    # PNG: OpenJPEG converts CMYK and applies palettes / channel definitions; TIFF keeps more than 8 bit
    x0, y0, x1, y1, comps = siz(open(path, 'rb').read())
    deep = max(c[0] for c in comps) > 8
    out = os.path.join(tempfile.mkdtemp(dir=TMP), 'o.tif' if deep else 'o.png')
    r = subprocess.run([OPJ_DECOMPRESS, '-i', path, '-o', out], capture_output=True, text=True)
    if r.returncode != 0 or not os.path.exists(out):
        raise RuntimeError('opj_decompress failed: ' + r.stdout[-300:] + r.stderr[-300:])
    a = tifffile.imread(out) if deep else np.asarray(Image.open(out))
    return a if a.ndim == 3 else a[:, :, None]


def sycc_to_rgb(a, depth=8):
    """OpenJPEG's conversion: products truncated toward zero, result clamped."""
    a = a.astype(np.int64)
    off, top = 1 << (depth - 1), (1 << depth) - 1
    y, cb, cr = a[..., 0], a[..., 1] - off, a[..., 2] - off
    r = y + np.trunc(1.402 * cr).astype(np.int64)
    g = y - np.trunc(0.344 * cb + 0.714 * cr).astype(np.int64)
    b = y + np.trunc(1.772 * cb).astype(np.int64)
    return np.clip(np.stack([r, g, b], axis=-1), 0, top)


def ffmpeg_planes(path, comps, x0, y0, x1, y1):
    """Component planes of an 8 bit luma / chroma codestream from FFmpeg's own JPEG 2000 decoder (not OpenJPEG)."""
    r = subprocess.run(['ffmpeg', '-v', 'error', '-c:v', 'jpeg2000', '-i', path, '-f', 'rawvideo', '-'], capture_output=True)
    raw = np.frombuffer(r.stdout, np.uint8)
    planes, o = [], 0
    for depth, signed, dx, dy in comps:
        cw, ch = -(-x1 // dx) - -(-x0 // dx), -(-y1 // dy) - -(-y0 // dy)
        planes.append((raw[o:o + cw * ch].reshape(ch, cw), depth, signed))
        o += cw * ch
    if r.returncode != 0 or o != raw.size:
        raise RuntimeError('ffmpeg cannot decode ' + path)
    return planes


def upsample(planes, x0, y0, x1, y1, comps):
    """Component planes on the reference grid by sample replication (what PixCodec and OpenJPEG's tools do)."""
    w, h = x1 - x0, y1 - y0
    out = []
    for (a, depth, signed), (_, _, dx, dy) in zip(planes, comps):
        cx0, cy0 = -(-x0 // dx), -(-y0 // dy)
        xs = np.clip((x0 + np.arange(w)) // dx - cx0, 0, a.shape[1] - 1)
        ys = np.clip((y0 + np.arange(h)) // dy - cy0, 0, a.shape[0] - 1)
        out.append(a[np.ix_(ys, xs)])
    return np.stack(out, axis=-1)


def attempt(f):
    """Keeps going after a fixture that cannot be made; main reports them and fails."""
    def g(name, *args, **kw):
        try:
            return f(name, *args, **kw)
        except Exception as e:   # noqa: BLE001 - every failure is reported with its fixture
            failed.append(f'{name}: {str(e).strip()[-300:]}')
    return g


def save(name, data, ref):
    path = os.path.join(OUT, name)
    with open(path, 'wb') as f:
        f.write(data)
    write_ref(path + '.ref', ref)
    made.append(name)


# ---------------------------------------------------------------- encoders

def pattern(w, h, c, dtype=np.uint8, seed=1, bits=None):
    a = test_pattern(w, h, c, dtype, seed)
    if bits is not None:
        info = np.iinfo(a.dtype)
        a = (a.astype(np.int64) - info.min) >> (info.bits - bits)
        if info.min < 0:
            a -= 1 << (bits - 1)
        a = a.astype(dtype)
    return a[:, :, 0] if c == 1 else a


def write_pnm(path, a):
    a = np.asarray(a)
    deep = a.dtype.itemsize > 1
    with open(path, 'wb') as f:
        if a.ndim == 2 or a.shape[2] == 1:
            f.write(b'P5\n%d %d\n%d\n' % (a.shape[1], a.shape[0], 65535 if deep else 255))
        else:
            f.write(b'P6\n%d %d\n%d\n' % (a.shape[1], a.shape[0], 65535 if deep else 255))
        f.write(a.astype('>u2' if deep else np.uint8).tobytes())


@attempt
def opj(name, img, args, raw=None, how='plain'):
    """opj_compress with the given arguments. raw = (-F string, bytes) for raw input."""
    if not OPJ_COMPRESS or not OPJ_DECOMPRESS or how == 'skip':
        skipped.append(name)
        return
    d = tempfile.mkdtemp(dir=TMP)
    out = os.path.join(d, 'o' + os.path.splitext(name)[1])
    if raw is not None:
        src = os.path.join(d, 'in.raw')
        open(src, 'wb').write(raw[1])
        cmd = [OPJ_COMPRESS, '-i', src, '-o', out, '-F', raw[0], *args]
    else:
        a = np.asarray(img)
        if a.ndim == 3 and a.shape[2] in (2, 4):
            from PIL import Image
            src = os.path.join(d, 'in.png')
            Image.fromarray(a).save(src)
        else:
            src = os.path.join(d, 'in.pgm' if a.ndim == 2 or a.shape[2] == 1 else 'in.ppm')
            write_pnm(src, a)
        cmd = [OPJ_COMPRESS, '-i', src, '-o', out, *args]
    r = subprocess.run(cmd, capture_output=True, text=True)
    if r.returncode != 0 or not os.path.exists(out):
        raise RuntimeError(name + ': opj_compress failed: ' + r.stdout[-400:] + r.stderr[-400:])
    data = open(out, 'rb').read()
    if how == 'plain':
        save(name, data, ref_plain(data))
    elif how == 'components':
        # subsampled: every component from opj_decompress, replicated to the reference grid
        x0, y0, x1, y1, comps = siz(data)
        planes = opj_components(out)
        if planes[0][0].shape != (-(-y1 // comps[0][3]) - -(-y0 // comps[0][3]), -(-x1 // comps[0][2]) - -(-x0 // comps[0][2])):
            # the tool already replicated subsampled components to the size of the first one
            planes = [(p[::c[3], ::c[2]] if p.shape == planes[0][0].shape and (c[2], c[3]) != (comps[0][2], comps[0][3]) else p, dp, sg)
                      for (p, dp, sg), c in zip(planes, comps)]
        a = upsample(planes, x0, y0, x1, y1, comps)
        save(name, data, to_convention(a, max(c[0] for c in comps), comps[0][1]))
    elif how == 'ffmpeg':
        # Luma / chroma at an odd image offset: OpenJPEG's tool paints the first row and column without chroma
        # there. The planes come from FFmpeg's decoder and are converted as OpenJPEG does everywhere else; that this
        # pairing reproduces opj_decompress exactly is asserted on the fixtures without an odd offset.
        x0, y0, x1, y1, comps = siz(data)
        save(name, data, sycc_to_rgb(upsample(ffmpeg_planes(out, comps, x0, y0, x1, y1), x0, y0, x1, y1, comps)).astype(np.uint8))
    else:
        ref = ref_image(out)
        x0, y0, x1, y1, comps = siz(data)
        if raw is not None and shutil.which('ffmpeg') and comps[1][2:] in ((2, 1), (2, 2)) and (x0 | y0) & 1 == 0:
            mine = sycc_to_rgb(upsample(ffmpeg_planes(out, comps, x0, y0, x1, y1), x0, y0, x1, y1, comps))
            if 'irr' not in name:
                assert np.array_equal(mine, ref), 'FFmpeg planes + conversion differ from opj_decompress'
        save(name, data, ref)


@attempt
def lib(name, img, how=None, **kw):
    import imagecodecs
    data = imagecodecs.jpeg2k_encode(img, codecformat='jp2' if name.endswith('.jp2') else 'j2k', **kw)
    save(name, data, ref_plain(data))
    return data


@attempt
def ht(name, img, **kw):
    import imagecodecs
    data = imagecodecs.htj2k_encode(img, **kw)
    save(name, data, ref_plain(data, ht=True))
    return data


@attempt
def ojph(name, img, args, yuv=None):
    """ojph_compress. yuv = (bytes, extra arguments) for raw planar input with per component depth / subsampling."""
    if not OJPH_COMPRESS or (yuv is not None and not OPJ_DECOMPRESS):
        skipped.append(name)
        return
    d = tempfile.mkdtemp(dir=TMP)
    out = os.path.join(d, 'o' + os.path.splitext(name)[1])
    if yuv is not None:
        src = os.path.join(d, 'in.yuv')
        open(src, 'wb').write(yuv[0])
        cmd = [OJPH_COMPRESS, '-i', src, '-o', out, *yuv[1], *args]
    else:
        a = np.asarray(img)
        src = os.path.join(d, 'in.pgm' if a.ndim == 2 else 'in.ppm')
        write_pnm(src, a)
        cmd = [OJPH_COMPRESS, '-i', src, '-o', out, *args]
    r = subprocess.run(cmd, capture_output=True, text=True)
    if r.returncode != 0 or not os.path.exists(out):
        raise RuntimeError(name + ': ojph_compress failed: ' + r.stdout[-400:] + r.stderr[-400:])
    data = open(out, 'rb').read()
    if yuv is None:
        save(name, data, ref_plain(data, ht=True))
    elif '420' in name:
        save(name, data, ref_image(out))
    else:
        x0, y0, x1, y1, comps = siz(data)
        a = upsample(opj_components(out), x0, y0, x1, y1, comps)
        depth = max(c[0] for c in comps)
        if len(set(c[0] for c in comps)) > 1:
            # channels of different depth are scaled to the deepest
            cols = []
            for i, c in enumerate(comps):
                m = (1 << c[0]) - 1
                cols.append((a[:, :, i].astype(np.int64) * ((1 << depth) - 1) + m // 2) // m)
            a = np.stack(cols, axis=-1)
        save(name, data, to_convention(a, depth, comps[0][1]))


# ---------------------------------------------------------------- JP2 boxes

def box(kind, body):
    return struct.pack('>I4s', 8 + len(body), kind) + body


def jp2(codestream, ncomp, depth, w, h, colr=(1, 16), extra_header=b'', before=b'', after=b'', brand=b'jp2 ', compat=(b'jp2 ',),
        bpc=None, jp2c_len=None):
    ihdr = struct.pack('>IIHBBBB', h, w, ncomp, 255 if bpc is not None else depth - 1, 7, 0, 0)
    head = box(b'ihdr', ihdr)
    if bpc is not None:
        head += box(b'bpcc', bytes(bpc))
    if colr[0] == 1:
        head += box(b'colr', struct.pack('>BBBI', 1, 0, 0, colr[1]))
    elif colr[0] == 2:
        head += box(b'colr', struct.pack('>BBB', 2, 0, 0) + colr[1])
    head += extra_header
    cs = box(b'jp2c', codestream)
    if jp2c_len == 0:
        cs = struct.pack('>I4s', 0, b'jp2c') + codestream
    elif jp2c_len == 1:
        cs = struct.pack('>I4sQ', 1, b'jp2c', 16 + len(codestream)) + codestream
    return (box(b'jP  ', b'\r\n\x87\n') + box(b'ftyp', brand + struct.pack('>I', 0) + b''.join(compat)) + box(b'jp2h', head)
            + before + cs + after)


def cdef(entries):
    return box(b'cdef', struct.pack('>H', len(entries)) + b''.join(struct.pack('>HHH', *e) for e in entries))


def res_box(kind, vn, vd, hn, hd, ve, he):
    return box(kind, struct.pack('>HHHHbb', vn, vd, hn, hd, ve, he))


EXIF_UUID = bytes.fromhex('4A7067546966664578 69662D3E4A5032'.replace(' ', ''))
XMP_UUID = bytes.fromhex('BE7ACFCB97A942E89C71999491E3AFAC')


def exif(orientation, big=False):
    e = '>' if big else '<'
    return (b'MM\x00*' if big else b'II*\x00') + struct.pack(e + 'I', 8) + struct.pack(e + 'H', 1) + struct.pack(e + 'HHIHH', 0x112, 3, 1, orientation, 0) + struct.pack(e + 'I', 0)


@attempt
def container(name, data, how='image', ref=None):
    path = os.path.join(TMP, name)
    open(path, 'wb').write(data)
    if ref is None:
        if not OPJ_DECOMPRESS:
            skipped.append(name)
            return
        ref = ref_image(path) if how == 'image' else ref_plain(data)
    save(name, data, ref)


# ---------------------------------------------------------------- the fixture list

def generate():
    import imagecodecs
    os.makedirs(OUT, exist_ok=True)
    g8, rgb8, rgba8 = pattern(45, 37, 1), pattern(45, 37, 3), pattern(31, 29, 4)
    g16, rgb16 = pattern(40, 33, 1, np.uint16), pattern(40, 33, 3, np.uint16)

    # --- OpenJPEG through imagecodecs: sample types, sizes, both wavelets, both containers
    for nm, img in (('gray8', g8), ('rgb8', rgb8), ('rgba8', rgba8), ('gray16', g16), ('rgb16', rgb16)):
        lib(f'{nm}_rev.j2k', img, reversible=True, level=0)
        lib(f'{nm}_rev.jp2', img, reversible=True, level=0)
        lib(f'{nm}_irr.j2k', img, reversible=False, level=38)
    lib('rgb8_irr.jp2', rgb8, reversible=False, level=32)
    lib('graya8_rev.jp2', pattern(33, 20, 2), reversible=True, level=0)
    lib('gray12_rev.j2k', pattern(40, 33, 1, np.uint16, bits=12), reversible=True, level=0, bitspersample=12)
    lib('rgb12_irr.j2k', pattern(40, 33, 3, np.uint16, bits=12), reversible=False, level=40, bitspersample=12)
    lib('gray_s16_rev.j2k', pattern(40, 33, 1, np.int16, seed=3), reversible=True, level=0)
    lib('gray_s8_rev.j2k', pattern(40, 33, 1, np.int8, seed=3), reversible=True, level=0)
    lib('rgb_s16_irr.j2k', pattern(32, 30, 3, np.int16, seed=4), reversible=False, level=45)
    lib('gray_1x1.j2k', np.array([[77]], np.uint8), reversible=True, level=0)
    lib('gray_1x9.j2k', pattern(1, 9, 1), reversible=True, level=0)
    lib('gray_9x1.jp2', pattern(9, 1, 1), reversible=True, level=0)
    lib('rgb_2x3.j2k', pattern(2, 3, 3), reversible=True, level=0)
    lib('rgb_3x2_irr.j2k', pattern(3, 2, 3), reversible=False, level=50)
    lib('gray_flat.j2k', np.full((40, 40), 200, np.uint8), reversible=True, level=0)
    lib('gray_res1.j2k', g8, reversible=True, level=0, resolutions=1)
    lib('rgb_res2_irr.j2k', rgb8, reversible=False, level=40, resolutions=2)
    lib('rgb_res7.j2k', pattern(130, 70, 3), reversible=True, level=0, resolutions=7)
    lib('rgb_nomct_rev.j2k', rgb8, reversible=True, level=0, mct=False)
    lib('rgb_nomct_irr.j2k', rgb8, reversible=False, level=40, mct=False)
    opj('rgb_tiles16.j2k', rgb8, ['-t', '16,16', '-n', '3'])
    opj('rgb_tiles20x12_irr.jp2', rgb8, ['-t', '20,12', '-I', '-r', '6', '-n', '3'])
    opj('gray16_tiles_irr.j2k', g16, ['-t', '24,24', '-I', '-r', '8', '-n', '4'])
    lib('comps5_planar.j2k', np.moveaxis(pattern(30, 22, 5), -1, 0).copy(), reversible=True, level=0, planar=True)
    lib('noise_rev.j2k', np.random.default_rng(7).integers(0, 256, (48, 48), np.uint8), reversible=True, level=0)
    lib('noise16_rev.j2k', np.random.default_rng(8).integers(0, 65536, (32, 32, 3), np.uint16), reversible=True, level=0)

    # --- opj_compress: code-block styles, structure, progression, markers
    # (Tile sizes are chosen so that no tile has a resolution level without samples: for those opj_compress -TP
    # writes packets that do not exist, and then cannot decode its own file.)
    big = pattern(80, 62, 3)
    for m in (1, 2, 4, 8, 16, 32, 5, 9, 20, 38, 63):
        opj(f'style{m:02d}_rev.j2k', pattern(70, 61, 1, seed=m), ['-M', str(m)])
    opj('style01_irr.j2k', big, ['-M', '1', '-I', '-r', '6'])
    opj('style13_irr.j2k', big, ['-M', '13', '-I', '-r', '8'])
    opj('style63_layers_irr.j2k', big, ['-M', '63', '-I', '-r', '30,12,5'])
    opj('style05_layers_rev.j2k', pattern(70, 61, 1), ['-M', '5', '-r', '20,8,1'])
    opj('cblk4x4.j2k', g8, ['-b', '4,4'])
    opj('cblk8x64.j2k', big, ['-b', '8,64'])
    opj('cblk64x4.j2k', big, ['-b', '64,4', '-I', '-r', '5'])
    opj('cblk16_irr.j2k', big, ['-b', '16,16', '-I', '-r', '4'])
    opj('cblk1024x4.j2k', pattern(300, 9, 1), ['-b', '1024,4', '-n', '1'])
    opj('precincts.j2k', big, ['-c', '[32,32],[16,16],[8,8]', '-b', '8,8'])
    opj('precincts_irr.j2k', big, ['-c', '[64,64],[32,32]', '-b', '16,16', '-I', '-r', '6'])
    opj('precincts_tiny.j2k', g8, ['-c', '[8,8],[4,4],[2,2]', '-b', '4,4', '-n', '3'])
    for p in ('LRCP', 'RLCP', 'RPCL', 'PCRL', 'CPRL'):
        opj(f'prog_{p.lower()}.j2k', big, ['-p', p, '-r', '20,6,1', '-c', '[32,32],[16,16]', '-b', '16,16', '-t', '64,48'])
    opj('prog_rpcl_irr.j2k', big, ['-p', 'RPCL', '-I', '-r', '30,10', '-c', '[32,32],[16,16],[16,16],[16,16],[16,16],[16,16]', '-b', '8,8'])
    opj('layers_rev.j2k', big, ['-r', '40,20,10,4,1'])
    opj('layers_irr.j2k', big, ['-I', '-r', '80,40,20,10,5'])
    opj('layers_psnr.j2k', big, ['-I', '-q', '25,30,38,45'])
    opj('poc.j2k', big, ['-POC', 'T1=0,0,1,3,3,CPRL/T1=3,0,1,6,3,RPCL', '-r', '1', '-n', '6', '-c', '[32,32]', '-b', '16,16'])
    opj('poc_layers.j2k', big, ['-POC', 'T1=0,0,2,6,1,LRCP/T1=0,1,3,6,3,RLCP/T1=0,0,3,6,1,PCRL', '-r', '20,8,1', '-n', '6'])
    opj('poc_tiles.j2k', big, ['-POC', 'T1=0,0,1,2,3,RLCP/T1=2,0,1,6,3,CPRL/T2=0,0,1,6,2,PCRL/T2=0,2,1,6,3,LRCP', '-t', '48,80', '-r', '1', '-n', '6'])
    opj('roi.j2k', big, ['-ROI', 'c=0,U=12', '-I', '-r', '10'])
    opj('roi_rev.j2k', g8, ['-ROI', 'c=0,U=9'])
    opj('roi_layers.j2k', big, ['-ROI', 'c=1,U=20', '-I', '-r', '40,15,6'])
    opj('sop_eph.j2k', big, ['-SOP', '-EPH', '-r', '10,1', '-t', '64,64'])
    opj('sop.j2k', g8, ['-SOP'])
    opj('eph_irr.j2k', big, ['-EPH', '-I', '-r', '8', '-p', 'PCRL', '-c', '[32,32]'])
    opj('plt_tlm.j2k', big, ['-PLT', '-TLM', '-t', '40,40'])
    opj('tileparts_r.j2k', big, ['-TP', 'R', '-t', '48,32', '-r', '10,1'])
    opj('tileparts_l.j2k', big, ['-TP', 'L', '-t', '64,64', '-r', '20,5,1'])
    opj('tileparts_c.j2k', big, ['-TP', 'C', '-t', '48,32', '-p', 'CPRL'])
    opj('tileparts_r_irr.jp2', big, ['-TP', 'R', '-t', '32,32', '-I', '-r', '6', '-p', 'RPCL'])
    opj('offset.j2k', big, ['-d', '7,5'])
    opj('offset_tiles.j2k', big, ['-d', '13,9', '-t', '40,32', '-T', '5,3'])
    opj('offset_tiles_irr.j2k', big, ['-d', '21,17', '-t', '33,29', '-T', '4,10', '-I', '-r', '5', '-n', '4'])
    opj('offset_odd_1level.j2k', g8, ['-d', '1,1', '-n', '2'])
    opj('tiles_1px.j2k', pattern(9, 7, 3), ['-t', '1,1', '-n', '1'])
    opj('tiles_3x5.j2k', pattern(23, 19, 3), ['-t', '3,5', '-n', '2'])
    opj('tiles_odd_irr.j2k', big, ['-t', '37,23', '-I', '-r', '5', '-n', '4'])
    opj('guard5.j2k', g8, ['-GuardBits', '5'])
    opj('comment.j2k', g8, ['-C', 'a comment in the main header'])
    opj('mct_off_irr.j2k', big, ['-mct', '0', '-I', '-r', '5'])
    opj('res1_irr.j2k', big, ['-n', '1', '-I', '-r', '5'])
    opj('rgba16.jp2', None, ['-n', '3'], raw=('21,17,4,16,u', pattern(21, 17, 4, np.uint16).transpose(2, 0, 1).astype('>u2').tobytes()))
    # all components subsampled alike, and luma / chroma layouts with image offsets
    opj('sub2x2_all.j2k', big, ['-s', '2,2'], how='components')
    opj('sub3x1_all.j2k', g8, ['-s', '3,1'], how='components')
    w, h = 46, 38
    yuv = pattern(w, h, 3, seed=9)
    def planar(dxy):
        return b''.join(np.ascontiguousarray(yuv[::dy, ::dx, c]).tobytes() for c, (dx, dy) in enumerate(dxy))
    for nm, dxy in (('420', ((1, 1), (2, 2), (2, 2))), ('422', ((1, 1), (2, 1), (2, 1))), ('440', ((1, 1), (1, 2), (1, 2))), ('411', ((1, 1), (4, 1), (4, 1))),
                    ('odd', ((1, 1), (3, 2), (2, 3)))):
        fmt = f'{w},{h},3,8,u@' + ':'.join(f'{dx}x{dy}' for dx, dy in dxy)
        opj(f'sub_{nm}.j2k', None, ['-mct', '0'], raw=(fmt, planar(dxy)), how='image' if nm in ('420', '422') else 'components')
        if nm in ('420', '422'):
            opj(f'sub_{nm}_irr.jp2', None, ['-mct', '0', '-I', '-r', '4', '-t', '32,32'], raw=(fmt, planar(dxy)), how='image')
    fmt = f'{w},{h},3,8,u@1x1:2x2:2x2'
    opj('sub_420_offset.j2k', None, ['-mct', '0', '-d', '3,5'], raw=(fmt, planar(((1, 1), (2, 2), (2, 2)))), how='ffmpeg' if shutil.which('ffmpeg') else 'skip')
    opj('sub_420_offset_even.j2k', None, ['-mct', '0', '-d', '6,2', '-t', '24,20', '-n', '3'], raw=(fmt, planar(((1, 1), (2, 2), (2, 2)))), how='image')

    # --- Pillow (OpenJPEG with other parameter paths)
    try:
        from PIL import Image
        for nm, kw in (('pil_layers_irr.jp2', dict(irreversible=True, quality_mode='rates', quality_layers=[40, 15, 5], progression='RPCL')),
                       ('pil_psnr_cprl.j2k', dict(irreversible=True, quality_mode='dB', quality_layers=[28, 36, 44], progression='CPRL', codeblock_size=(16, 16))),
                       ('pil_precincts.jp2', dict(precinct_size=(32, 32), codeblock_size=(8, 8), num_resolutions=4, tile_size=(48, 48))),
                       ('pil_offset.jp2', dict(offset=(5, 9), tile_offset=(2, 3), tile_size=(40, 40))),
                       ('pil_la.jp2', dict()), ('pil_rgba_irr.jp2', dict(irreversible=True, quality_layers=[8]))):
            src = pattern(60, 44, 2 if 'la' in nm else 4 if 'rgba' in nm else 3, seed=11)
            b = io.BytesIO()
            Image.fromarray(src).save(b, 'JPEG2000' , **kw) if nm.endswith('.jp2') else Image.fromarray(src).save(b, 'JPEG2000', no_jp2=True, **kw)
            save(nm, b.getvalue(), ref_plain(b.getvalue()))
    except ImportError:
        skipped.append('pil_*')

    # --- HTJ2K: OpenJPH through imagecodecs and its tool
    ht('ht_gray8_rev.jhc', g8, reversible=True)
    ht('ht_rgb8_rev.jhc', rgb8, reversible=True)
    ht('ht_rgb8_irr.jhc', rgb8, reversible=False, level=0.02)
    ht('ht_gray16_rev.jhc', g16, reversible=True)
    ht('ht_rgb16_irr.jhc', rgb16, reversible=False, level=0.001)
    ht('ht_gray12_rev.jhc', pattern(40, 33, 1, np.uint16, bits=12), reversible=True, bitspersample=12)
    ht('ht_gray_s16_rev.jhc', pattern(40, 33, 1, np.int16, seed=3), reversible=True)
    ht('ht_1x1.jhc', np.array([[9]], np.uint8), reversible=True)
    ht('ht_1x7.jhc', pattern(1, 7, 1), reversible=True)
    ht('ht_7x1.jhc', pattern(7, 1, 1), reversible=True)
    ht('ht_3x3_irr.jhc', pattern(3, 3, 3), reversible=False, level=0.01)
    ht('ht_res1.jhc', g8, reversible=True, resolutions=1)
    ht('ht_res3_irr.jhc', rgb8, reversible=False, level=0.05, resolutions=3)
    ht('ht_noise_rev.jhc', np.random.default_rng(5).integers(0, 256, (67, 65), np.uint8), reversible=True)
    ht('ht_noise16_rev.jhc', np.random.default_rng(6).integers(0, 65536, (33, 34, 3), np.uint16), reversible=True)
    ht('ht_flat.jhc', np.full((33, 33), 100, np.uint8), reversible=True)
    ojph('ht_tiles.jhc', big, ['-reversible', 'true', '-tile_size', '{40,48}'])
    ojph('ht_tiles_irr.jhc', big, ['-qstep', '0.02', '-tile_size', '{33,31}', '-num_decomps', '3'])
    ojph('ht_blocks16.jhc', big, ['-reversible', 'true', '-block_size', '{16,16}'])
    ojph('ht_blocks4x128.jhc', big, ['-reversible', 'true', '-block_size', '{4,128}'])
    ojph('ht_blocks256x4_irr.jhc', big, ['-qstep', '0.01', '-block_size', '{256,4}'])
    ojph('ht_precincts.jhc', big, ['-reversible', 'true', '-precincts', '{16,16},{32,32}', '-block_size', '{8,8}', '-prog_order', 'PCRL'])
    for p in ('LRCP', 'RLCP', 'RPCL', 'PCRL', 'CPRL'):
        ojph(f'ht_prog_{p.lower()}.jhc', big, ['-reversible', 'true', '-prog_order', p, '-precincts', '{32,32}', '-tile_size', '{64,48}'])
    ojph('ht_tileparts_rc.jhc', big, ['-reversible', 'true', '-tileparts', 'RC', '-tile_size', '{48,48}', '-tlm_marker', 'true'])
    ojph('ht_offset.jhc', big, ['-reversible', 'true', '-image_offset', '{5,7}', '-tile_offset', '{2,4}', '-tile_size', '{40,36}'])
    ojph('ht_nocolour_irr.jhc', big, ['-qstep', '0.03', '-colour_trans', 'false'])
    ojph('ht_qfactor.jhc', big, ['-qfactor', '70'])
    ojph('ht_container_rev.jph', big, ['-reversible', 'true'])
    ojph('ht_container_irr.jph', g8, ['-qstep', '0.01'])
    ojph('ht_levels0.jhc', g8, ['-reversible', 'true', '-num_decomps', '0'])
    ojph('ht_levels7.jhc', pattern(150, 140, 1), ['-reversible', 'true', '-num_decomps', '7'])
    # raw planar input: subsampling and components of different depth
    y = pattern(w, h, 3, seed=9)
    ojph('ht_sub420.jhc', None, ['-reversible', 'true'], yuv=(planar(((1, 1), (2, 2), (2, 2))),
         ['-dims', f'{{{w},{h}}}', '-num_comps', '3', '-signed', 'false', '-bit_depth', '8', '-downsamp', '{1,1},{2,2},{2,2}']))
    mixed = (pattern(w, h, 1, np.uint16, bits=10).astype('<u2').tobytes() + (pattern(w, h, 1, seed=2)).tobytes() + (pattern(w, h, 1, seed=3) >> 4).tobytes())
    ojph('ht_depths_10_8_4.jhc', None, ['-reversible', 'true'], yuv=(mixed,
         ['-dims', f'{{{w},{h}}}', '-num_comps', '3', '-signed', 'false', '-bit_depth', '10,8,4', '-downsamp', '{1,1}']))

    # --- JP2 containers around OpenJPEG codestreams
    cs_g = imagecodecs.jpeg2k_encode(g8, codecformat='j2k', reversible=True, level=0)
    cs_rgb = imagecodecs.jpeg2k_encode(rgb8, codecformat='j2k', reversible=True, level=0)
    cs_rgb_plain = imagecodecs.jpeg2k_encode(rgb8, codecformat='j2k', reversible=True, level=0, mct=False)
    cs_rgba = imagecodecs.jpeg2k_encode(rgba8, codecformat='j2k', reversible=True, level=0)
    cs_ga = imagecodecs.jpeg2k_encode(pattern(33, 20, 2), codecformat='j2k', reversible=True, level=0)
    hg, wg = g8.shape
    # palettes
    idx = (pattern(40, 30, 1, seed=13) >> 4).astype(np.uint8)
    cs_idx = imagecodecs.jpeg2k_encode(idx, codecformat='j2k', reversible=True, level=0, bitspersample=4)
    pal = np.random.default_rng(1).integers(0, 256, (16, 3), np.uint8)
    pclr = box(b'pclr', struct.pack('>HB', 16, 3) + bytes([7, 7, 7]) + pal.tobytes())
    cmap3 = box(b'cmap', b''.join(struct.pack('>HBB', 0, 1, c) for c in range(3)))
    container('palette_rgb.jp2', jp2(cs_idx, 1, 4, 40, 30, extra_header=pclr + cmap3), ref=pal[idx])
    pal4 = np.random.default_rng(2).integers(0, 256, (16, 4), np.uint8)
    pclr4 = box(b'pclr', struct.pack('>HB', 16, 4) + bytes([7, 7, 7, 7]) + pal4.tobytes())
    cmap4 = box(b'cmap', b''.join(struct.pack('>HBB', 0, 1, c) for c in range(4)))
    container('palette_rgba.jp2', jp2(cs_idx, 1, 4, 40, 30, extra_header=pclr4 + cmap4 + cdef([(0, 0, 1), (1, 0, 2), (2, 0, 3), (3, 1, 0)])), ref=pal4[idx])
    palg = np.arange(16, dtype=np.uint8)[::-1] * 17
    pclrg = box(b'pclr', struct.pack('>HB', 16, 1) + bytes([7]) + palg.tobytes())
    container('palette_gray.jp2', jp2(cs_idx, 1, 4, 40, 30, colr=(1, 17), extra_header=pclrg + box(b'cmap', struct.pack('>HBB', 0, 1, 0))), ref=palg[idx][:, :, None])
    pal16 = np.random.default_rng(3).integers(0, 4096, (16, 3)).astype(np.uint16)
    pclr16 = box(b'pclr', struct.pack('>HB', 16, 3) + bytes([11, 11, 11]) + pal16.astype('>u2').tobytes())
    container('palette_12bit.jp2', jp2(cs_idx, 1, 4, 40, 30, extra_header=pclr16 + cmap3), ref=pal16[idx])
    # 8 bit indices with a direct (unmapped) alpha component next to the palette
    ia = np.stack([idx, pattern(40, 30, 1, seed=14)], axis=-1)
    cs_ia = imagecodecs.jpeg2k_encode(ia, codecformat='j2k', reversible=True, level=0)
    cmap_ia = box(b'cmap', b''.join(struct.pack('>HBB', 0, 1, c) for c in range(3)) + struct.pack('>HBB', 1, 0, 0))
    container('palette_plus_alpha.jp2', jp2(cs_ia, 2, 8, 40, 30, extra_header=pclr + cmap_ia + cdef([(0, 0, 1), (1, 0, 2), (2, 0, 3), (3, 1, 0)])),
              ref=np.concatenate([pal[np.minimum(idx, 15)], ia[:, :, 1:]], axis=-1))
    # channel definitions
    hr, wr = rgb8.shape[:2]
    container('cdef_bgr.jp2', jp2(cs_rgb_plain, 3, 8, wr, hr, extra_header=cdef([(0, 0, 3), (1, 0, 2), (2, 0, 1)])), ref=rgb8[:, :, ::-1])
    container('cdef_argb.jp2', jp2(cs_rgba, 4, 8, 31, 29, extra_header=cdef([(0, 1, 0), (1, 0, 1), (2, 0, 2), (3, 0, 3)])), ref=rgba8[:, :, [1, 2, 3, 0]])
    container('cdef_alpha_gray.jp2', jp2(cs_ga, 2, 8, 33, 20, colr=(1, 17), extra_header=cdef([(0, 1, 0), (1, 0, 1)])), ref=pattern(33, 20, 2)[:, :, ::-1])
    pm = rgba8.astype(np.int64)
    a = pm[:, :, 3:4]
    unpre = np.where((a > 0) & (a < 255), np.minimum((pm[:, :, :3] * 255 + a // 2) // np.maximum(a, 1), 255), pm[:, :, :3])
    container('cdef_premultiplied.jp2', jp2(cs_rgba, 4, 8, 31, 29, extra_header=cdef([(0, 0, 1), (1, 0, 2), (2, 0, 3), (3, 2, 0)])),
              ref=np.concatenate([unpre, a], axis=-1).astype(np.uint8))
    container('cdef_unassociated.jp2', jp2(cs_rgba, 4, 8, 31, 29, extra_header=cdef([(0, 0, 1), (1, 0, 2), (2, 0, 3), (3, 65535, 65535)])), ref=rgba8)
    container('rgba_no_cdef.jp2', jp2(cs_rgba, 4, 8, 31, 29), ref=rgba8)
    # colour spaces
    container('sycc.jp2', jp2(cs_rgb_plain, 3, 8, wr, hr, colr=(1, 18)))
    cs420 = None
    p420 = os.path.join(OUT, 'sub_420.j2k')
    if os.path.exists(p420):
        cs420 = open(p420, 'rb').read()
        container('sycc_420.jp2', jp2(cs420, 3, 8, w, h, colr=(1, 18)))
        container('srgb_420.jp2', jp2(cs420, 3, 8, w, h, colr=(1, 16)))
    cmyk = pattern(30, 26, 4, seed=21)
    cs_cmyk = imagecodecs.jpeg2k_encode(cmyk, codecformat='j2k', reversible=True, level=0)
    container('cmyk.jp2', jp2(cs_cmyk, 4, 8, 30, 26, colr=(1, 12), brand=b'jpx ', compat=(b'jpx ', b'jp2 ')))
    try:
        from PIL import ImageCms
        icc = ImageCms.ImageCmsProfile(ImageCms.createProfile('sRGB')).tobytes()
        container('icc.jp2', jp2(cs_rgb, 3, 8, wr, hr, colr=(2, icc)), ref=rgb8)
        container('icc_gray.jp2', jp2(cs_g, 1, 8, wg, hg, colr=(2, icc[:16] + b'GRAY' + icc[20:])), ref=g8[:, :, None])
    except ImportError:
        skipped.append('icc*.jp2')
    # resolution, metadata, box syntax
    res = box(b'res ', res_box(b'resc', 11811, 1, 11811, 1, 0, 0))                                   # 300 dpi
    container('res_300dpi.jp2', jp2(cs_rgb, 3, 8, wr, hr, extra_header=res), ref=rgb8)
    res2 = box(b'res ', res_box(b'resd', 2835, 1, 5669, 2, 0, 0) + res_box(b'resc', 5669, 10, 2835, 1, 1, 0))   # capture 72 x 144 dpi wins
    container('res_capture_and_display.jp2', jp2(cs_g, 1, 8, wg, hg, colr=(1, 17), extra_header=res2), ref=g8[:, :, None])
    container('res_display_only.jp2', jp2(cs_g, 1, 8, wg, hg, colr=(1, 17), extra_header=box(b'res ', res_box(b'resd', 3937, 100, 3937, 100, 2, 2))), ref=g8[:, :, None])
    container('exif_before.jp2', jp2(cs_rgb, 3, 8, wr, hr, before=box(b'uuid', EXIF_UUID + exif(6))), ref=rgb8)
    container('exif_after.jp2', jp2(cs_rgb, 3, 8, wr, hr, after=box(b'uuid', EXIF_UUID + exif(8, big=True))), ref=rgb8)
    container('exif_prefixed.jp2', jp2(cs_g, 1, 8, wg, hg, colr=(1, 17), before=box(b'uuid', EXIF_UUID + b'Exif\0\0' + exif(3))), ref=g8[:, :, None])
    xmp = b'<?xpacket begin="" id="W5M0MpCehiHzreSzNTczkc9d"?><x:xmpmeta xmlns:x="adobe:ns:meta/"></x:xmpmeta><?xpacket end="w"?>'
    container('xmp_uuid.jp2', jp2(cs_g, 1, 8, wg, hg, colr=(1, 17), before=box(b'uuid', XMP_UUID + xmp) + box(b'xml ', b'<a/>')), ref=g8[:, :, None])
    container('box_xl.jp2', jp2(cs_g, 1, 8, wg, hg, colr=(1, 17), jp2c_len=1, before=box(b'free', b'\0' * 9)), ref=g8[:, :, None])
    container('box_to_eof.jp2', jp2(cs_g, 1, 8, wg, hg, colr=(1, 17), jp2c_len=0, before=box(b'abcd', b'unknown box')), ref=g8[:, :, None])
    container('jpx_compatible.jp2', jp2(cs_rgb, 3, 8, wr, hr, brand=b'jpx ', compat=(b'jpx ', b'jp2 ', b'jpxb'),
                                        before=box(b'rreq', bytes([1, 0xC0, 0x40, 0, 2, 0, 5, 0x80, 0, 45, 0x40, 0, 0])), extra_header=b''), ref=rgb8)
    container('colr_twice.jp2', jp2(cs_rgb, 3, 8, wr, hr, colr=(1, 16), extra_header=box(b'colr', struct.pack('>BBBI', 1, 0, 0, 17))), ref=rgb8)
    # bits per component box: 8 bit colour (declared 8) - the box repeats what SIZ says
    container('bpcc.jp2', jp2(cs_rgba, 4, 8, 31, 29, bpc=[7, 7, 7, 7], extra_header=cdef([(0, 0, 1), (1, 0, 2), (2, 0, 3), (3, 1, 0)])), ref=rgba8)
    # sub byte depths (scaled to 8 bit by PixCodec)
    for bits in (1, 2, 4, 7):
        src = (pattern(36, 28, 1, seed=bits) >> (8 - bits)).astype(np.uint8)
        d = imagecodecs.jpeg2k_encode(src, codecformat='j2k', reversible=True, level=0, bitspersample=bits)
        save(f'gray{bits}bit.j2k', d, ref_plain(d))
    src = (pattern(36, 28, 3, seed=5) >> 3).astype(np.uint8)
    d = imagecodecs.jpeg2k_encode(src, codecformat='jp2', reversible=True, level=0, bitspersample=5)
    save('rgb5bit.jp2', d, ref_plain(d))


def option_refs():
    """References for the decoder options: name.ext.<option>.ref next to the fixture, by opj_decompress
    (-r reduce, -l layers, -d window). The window is the one Jp2Tests.OptionWindow computes: a quarter in from the
    left, a third down, half the width and height. OpenJPEG's window decoding of HT streams is not usable (it
    disagrees with its own full decode), so HT fixtures get the reduce references only."""
    if not OPJ_DECOMPRESS:
        skipped.append('option references')
        return
    names = ['layers_irr.j2k', 'layers_rev.j2k', 'prog_lrcp.j2k', 'prog_rpcl.j2k', 'prog_pcrl.j2k', 'offset_tiles.j2k', 'tileparts_r.j2k', 'rgb_res7.j2k', 'precincts.j2k',
             'poc_layers.j2k', 'style63_layers_irr.j2k', 'roi_layers.j2k', 'offset_tiles_irr.j2k', 'ht_tiles.jhc', 'ht_precincts.jhc', 'ht_tiles_irr.jhc']
    for name in names:
        path = os.path.join(OUT, name)
        if not os.path.exists(path):
            continue
        x0, y0, x1, y1, comps = siz(open(path, 'rb').read())
        w, h = x1 - x0, y1 - y0
        win = (x0 + w // 4, y0 + h // 3, x0 + w // 4 + w // 2, y0 + h // 3 + h // 2)
        d = ['-d', ','.join(map(str, win))]
        options = [('reduce1', ['-r', '1']), ('reduce2', ['-r', '2'])]
        if not name.startswith('ht_'):
            options += [('window', d), ('window_reduce1', d + ['-r', '1'])]
            # opj_decompress -l on a position progression with tiles drops layers of the wrong tiles (the same
            # image in LRCP order shows what the first layers hold), so those files get no layer references
            if not name.startswith('prog_'):
                options += [('layers1', ['-l', '1']), ('layers2', ['-l', '2']), ('window_layers1', d + ['-l', '1'])]
        for tag, args in options:
            try:
                planes = opj_components(path, args)
            except RuntimeError as e:
                failed.append(f'{name} {tag}: {str(e)[-200:]}')
                continue
            a = np.stack([p[0] for p in planes], axis=-1)
            write_ref(f'{path}.{tag}.ref', to_convention(a, max(c[0] for c in comps), comps[0][1]))


if __name__ == '__main__':
    try:
        generate()
        option_refs()
    finally:
        shutil.rmtree(TMP, ignore_errors=True)
    size = sum(os.path.getsize(os.path.join(OUT, f)) for f in os.listdir(OUT))
    print(f'{len(made)} fixtures written, {size / 1e6:.2f} MB in {OUT}')
    if skipped:
        print('skipped (tool not found):', ', '.join(skipped))
    if failed:
        print('FAILED:\n  ' + '\n  '.join(failed))
        sys.exit(1)
