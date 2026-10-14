"""Generates the LERC fixtures in tests/fixtures/lerc.

  name.lerc            the blob
  name.lerc.ref        what the reference decoder returns: (rows, cols, bands * depth), channel = band * depth + index,
                       invalid pixels set to 0
  name.lerc.mask.ref   only if not all pixels are valid: uint8 (rows, cols, 1) when all bands share the mask,
                       (rows, cols, bands) otherwise; 1 = valid
  manifest.tsv         one line per blob with what the reference reports about it (lerc_getBlobInfo) and the SHA-256
                       of the reference samples (little-endian, as in the .ref) followed by the mask bytes if there
                       is a mask. Blobs that decode to more than 12 kB have no sidecars, only this hash.

Writers / reference decoders:
  * Esri's liblerc 4.0.1 from the pip package `lerc`, called through ctypes because only the C API can write a chosen
    codec version (2..6) and no-data values. It is also the reference decoder for every blob.
  * imagecodecs (bundles liblerc 4.2) decodes every single band blob a second time; the two must agree.
  * LERC1 can not be written by liblerc. GDAL's MRF driver (via rasterio) has its own LERC1 writer: the blobs are cut
    out of the .lrc data file it writes. They are decoded by liblerc like the others.
  * esri_*.lerc are the sample blobs of the Esri/lerc repository (Apache-2.0). Being third-party files they live in
    fixtures/lerc of the test-data repository (pxref.testdata_dir), with their own manifest.tsv; fetch them with
    `--fetch-esri` (needs network), otherwise the copies there are used. Without the test-data repository they are
    left out.

liblerc 4.0.1 leaves a few bytes of its v6 lossless float coding uninitialised (the spare word after a Huffman
stream) and GDAL's LERC1 writer some tail bytes: regenerating changes those bytes (and the checksum) of a few blobs,
never what they decode to (the manifest stays the same).

usage: python3 gen_lerc.py [--fetch-esri]
"""
import ctypes as ct
import hashlib
import os
import struct
import subprocess
import sys
import tempfile
import urllib.request

import numpy as np

from pxref import write_ref, testdata_dir

MAX_REF_BYTES = 12000
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'fixtures', 'lerc')

# ---------------------------------------------------------------- liblerc through ctypes

_DT = {np.dtype('int8'): 0, np.dtype('uint8'): 1, np.dtype('int16'): 2, np.dtype('uint16'): 3,
       np.dtype('int32'): 4, np.dtype('uint32'): 5, np.dtype('float32'): 6, np.dtype('float64'): 7}
_NP = {v: k for k, v in _DT.items()}
_dll = None


def dll():
    global _dll
    if _dll is None:
        import lerc
        _dll = ct.CDLL(lerc.lercDll._name)  # a fresh handle: the package sets argtypes on its own
    return _dll


def lerc_encode(arr, max_z_error=0.0, version=-1, mask=None, nodata=None):
    """arr: (bands, rows, cols, depth). mask: None, (rows, cols) or (bands, rows, cols) of 0/1.
    nodata: None, or one (uses, value) per band - this goes through lerc_encode_4D."""
    arr = np.ascontiguousarray(arr)
    nb, nr, nc, nd = arr.shape
    nmasks, pmask = 0, None
    if mask is not None:
        mask = np.ascontiguousarray(mask, dtype=np.uint8)
        nmasks = 1 if mask.ndim == 2 else mask.shape[0]
        pmask = mask.ctypes.data_as(ct.c_char_p)
    cap = arr.nbytes * 2 + 8192 * nb + nr * nc * nb
    out = ct.create_string_buffer(cap)
    n = ct.c_uint(0)
    args = [arr.ctypes.data_as(ct.c_void_p), ct.c_int(version), ct.c_uint(_DT[arr.dtype]), nd, nc, nr, nb, nmasks, pmask,
            ct.c_double(max_z_error), out, ct.c_uint(cap), ct.byref(n)]
    if nodata is None:
        rc = dll().lerc_encodeForVersion(*args)
    else:
        uses = (ct.c_ubyte * nb)(*[1 if u else 0 for u, _ in nodata])
        vals = (ct.c_double * nb)(*[float(v) for _, v in nodata])
        assert version in (-1, 6)  # this entry point has no version argument and writes the latest
        rc = dll().lerc_encode_4D(*(args[:1] + args[2:]), ct.byref(uses), ct.byref(vals))
    if rc != 0:
        raise RuntimeError(f'lerc encode failed: {rc}')
    return out.raw[:n.value]


def lerc_info(blob):
    ia = (ct.c_uint * 11)()
    da = (ct.c_double * 3)()
    rc = dll().lerc_getBlobInfo(ct.c_char_p(blob), ct.c_uint(len(blob)), ct.byref(ia), ct.byref(da), 11, 3)
    if rc != 0:
        raise RuntimeError(f'lerc getBlobInfo failed: {rc}')
    keys = ['version', 'dataType', 'nDepth', 'nCols', 'nRows', 'nBands', 'nValidPixels', 'blobSize', 'nMasks',
            'nDepth2', 'nUsesNoData']
    r = dict(zip(keys, list(ia)))
    r.update(zMin=da[0], zMax=da[1], maxZError=da[2])
    return r


def lerc_decode(blob):
    """-> data (bands, rows, cols, depth), masks (nMasks, rows, cols) or None, [(uses, value)] per band or None"""
    i = lerc_info(blob)
    nb, nr, nc, nd = i['nBands'], i['nRows'], i['nCols'], i['nDepth']
    arr = np.zeros((nb, nr, nc, nd), dtype=_NP[i['dataType']])
    nm = i['nMasks']
    mask = np.zeros((nm, nr, nc), dtype=np.uint8) if nm else None
    pmask = mask.ctypes.data_as(ct.c_char_p) if nm else None
    uses = (ct.c_ubyte * nb)()
    vals = (ct.c_double * nb)()
    rc = dll().lerc_decode_4D(ct.c_char_p(blob), ct.c_uint(len(blob)), nm, pmask, nd, nc, nr, nb,
                              ct.c_uint(i['dataType']), arr.ctypes.data_as(ct.c_void_p), ct.byref(uses), ct.byref(vals))
    if rc != 0:
        raise RuntimeError(f'lerc decode failed: {rc}')
    nodata = [(bool(uses[k]), vals[k]) for k in range(nb)] if i['nUsesNoData'] else None
    return arr, mask, nodata


# ---------------------------------------------------------------- writing fixtures

manifest = []
written = set()


def emit(name, blob, out=OUT, lines=manifest):
    """Writes the blob and the reference decoder's view of it into out, its manifest line into lines."""
    assert name not in written, name
    written.add(name)
    info = lerc_info(blob)
    data, masks, nodata = lerc_decode(blob)
    nb, nr, nc, nd = data.shape
    if masks is None:
        valid = np.ones((nb, nr, nc), dtype=np.uint8)
    elif masks.shape[0] == 1:
        valid = np.repeat(masks, nb, axis=0)
    else:
        valid = masks
    valid = (valid != 0).astype(np.uint8)
    data = data * valid[..., None].astype(data.dtype)
    if np.issubdtype(data.dtype, np.floating):
        data[valid == 0] = 0  # also where the stored value was inf / nan

    # second opinion (a newer liblerc); it handles one band at a time and no no-data values
    if nb == 1 and nodata is None and info['version'] != 0:
        import imagecodecs
        other = imagecodecs.lerc_decode(blob, masks=True)
        o_data, o_mask = other
        o_data = np.asarray(o_data).reshape(nr, nc, nd)
        o_valid = np.ones((nr, nc), np.uint8) if o_mask is None else (np.asarray(o_mask).reshape(nr, nc) != 0)
        assert np.array_equal(o_valid != 0, valid[0] != 0), name
        sel = valid[0] != 0
        assert np.array_equal(o_data[sel], data[0][sel], equal_nan=True), name

    with open(os.path.join(out, name + '.lerc'), 'wb') as f:
        f.write(blob)
    ref = np.ascontiguousarray(data.transpose(1, 2, 0, 3).reshape(nr, nc, nb * nd))
    sha = hashlib.sha256(ref.astype(ref.dtype.newbyteorder('<')).tobytes())
    small = ref.nbytes <= MAX_REF_BYTES  # larger images are checked through the hash only
    if small:
        write_ref(os.path.join(out, name + '.lerc.ref'), ref)
    if not valid.all():
        same = all(np.array_equal(valid[b], valid[0]) for b in range(nb))
        m = np.ascontiguousarray((valid[:1] if same else valid).transpose(1, 2, 0))
        sha.update(m.tobytes())
        if small:
            write_ref(os.path.join(out, name + '.lerc.mask.ref'), m)
    nd_txt = '-' if nodata is None else ','.join((repr(v) if u else 'x') for u, v in nodata)
    lines.append('\t'.join(str(x) for x in [
        name + '.lerc', max(info['version'], 1), info['dataType'], info['nCols'], info['nRows'], info['nDepth'],
        info['nBands'], info['nValidPixels'], info['nMasks'], info['nUsesNoData'], repr(info['zMin']),
        repr(info['zMax']), repr(info['maxZError']), info['blobSize'], nd_txt, sha.hexdigest()]))
    return info


# ---------------------------------------------------------------- content

TYPES = ['int8', 'uint8', 'int16', 'uint16', 'int32', 'uint32', 'float32', 'float64']
SHORT = {'int8': 'i8', 'uint8': 'u8', 'int16': 'i16', 'uint16': 'u16', 'int32': 'i32', 'uint32': 'u32',
         'float32': 'f32', 'float64': 'f64'}
# value range used for "natural" content, (low, high)
RANGE = {'int8': (-100, 120), 'uint8': (0, 255), 'int16': (-9000, 21000), 'uint16': (0, 60000),
         'int32': (-100000, 3000000), 'uint32': (0, 70000), 'float32': (-50.0, 2500.0), 'float64': (-50.0, 2500.0)}


def unit(kind, h, w, d, rng):
    """(h, w, d) array in [0, 1]."""
    y, x = np.mgrid[0:h, 0:w].astype(np.float64)
    chans = []
    for m in range(d):
        if kind == 'smooth':  # elevation like; the depth values are strongly correlated
            v = 0.5 + 0.25 * np.sin(x / 9.0 + 0.3) * np.cos(y / 7.0) + 0.2 * np.sin((x + 2 * y) / 23.0) + 0.02 * m
            v = v + 0.004 * rng.random((h, w))
        elif kind == 'noise':
            v = rng.random((h, w))
        elif kind == 'steps':  # constant blocks, some of them 0
            b = rng.integers(0, 6, ((h + 7) // 8, (w + 7) // 8)) / 5.0
            v = np.kron(b, np.ones((8, 8)))[:h, :w]
        elif kind == 'sparse':  # few distinct values: lookup table blocks
            v = rng.choice(np.array([0.0, 0.11, 0.5, 0.52, 0.93, 1.0]), (h, w), p=[0.4, 0.2, 0.15, 0.1, 0.1, 0.05])
        elif kind == 'mixed':
            v = 0.5 + 0.3 * np.sin(x / 5.0) * np.cos(y / 4.0)
            v[: h // 2, w // 2:] = rng.random((h // 2, w - w // 2))
            v[h // 2:, : w // 3] = 0.25
            v[h - h // 4:, w // 2:] = 0.0
        elif kind == 'ramp':
            v = (x + 3 * y + m) / (w + 3 * h + d)
        elif kind == 'photo':  # smooth with fine noise: 8 bit data that likes difference coding
            v = 0.5 + 0.3 * np.sin(x / 11.0 + m) * np.cos(y / 8.0) + 0.03 * rng.standard_normal((h, w))
        elif kind == 'skew':  # independent pixels with a skewed histogram: plain Huffman beats differences
            v = np.minimum(rng.geometric(0.35, (h, w)) - 1, 30) / 30.0
            v = np.where(rng.random((h, w)) < 0.2, 1.0 - v, v)
        else:
            raise ValueError(kind)
        chans.append(np.clip(v, 0, 1))
    return np.stack(chans, axis=-1)


def content(kind, h, w, d, dtype, seed, full=False):
    """(h, w, d) array of dtype."""
    rng = np.random.default_rng(seed)
    dt = np.dtype(dtype)
    if kind == 'zero':
        return np.zeros((h, w, d), dt)
    if kind == 'const':
        return np.full((h, w, d), -7 if dt.kind != 'u' else 7, dt)
    u = unit(kind, h, w, d, rng)
    lo, hi = RANGE[dtype]
    if full or (kind == 'noise' and dt.kind in 'iu'):
        info = np.iinfo(dt) if dt.kind in 'iu' else None
        if info is not None:
            lo, hi = info.min, info.max
        else:
            lo, hi = -1e6, 1e6
    a = lo + u * (float(hi) - float(lo))
    if dt.kind in 'iu':
        a = np.round(a)
        if kind in ('steps', 'mixed'):
            a[u == 0] = 0  # keep true zeros: all-zero blocks
    elif kind in ('steps', 'mixed'):
        a[u == 0] = 0
    return a.astype(dt)


def mask_of(kind, h, w, seed):
    rng = np.random.default_rng(seed + 1000)
    m = np.ones((h, w), np.uint8)
    if kind == 'random':
        m = (rng.random((h, w)) < 0.5).astype(np.uint8)
    elif kind == 'hole':
        m[h // 4: h // 4 + max(1, h // 3), w // 5: w // 5 + max(1, w // 2)] = 0
    elif kind == 'none':
        m[:] = 0
    elif kind == 'single':
        m[:] = 0
        m[h // 2, w // 3] = 1
    elif kind == 'corner':  # few valid pixels: the encoder writes them raw in one sweep
        m[:] = 0
        m[h - 3:, w - 4:] = 1
    elif kind == 'rows':
        m[1::3, :] = 0
    elif kind == 'checker':
        y, x = np.mgrid[0:h, 0:w]
        m = ((x + y) % 2).astype(np.uint8)
    elif kind == 'blocks':  # whole 8x8 blocks invalid
        b = rng.integers(0, 2, ((h + 7) // 8, (w + 7) // 8))
        m = np.kron(b, np.ones((8, 8), np.uint8))[:h, :w].astype(np.uint8)
        m[0, 0] = 1
    elif kind != 'all':
        raise ValueError(kind)
    return m


def gen_liblerc():
    seed = 0

    # A: every type x content, lossless, two versions each so that every (type, version) pair occurs
    for ti, t in enumerate(TYPES):
        for ci, kind in enumerate(['smooth', 'noise', 'steps', 'sparse', 'mixed']):
            for k in range(2):
                v = 2 + (ti + ci * 2 + k * 3) % 5
                seed += 1
                a = content(kind, 19, 21, 1, t, seed)
                emit(f'a_{SHORT[t]}_{kind}_v{v}', lerc_encode(a[None], 0, v))

    # B: lossy
    for ti, t in enumerate(TYPES):
        errs = [1, 2.5, 10] if np.dtype(t).kind in 'iu' else [0.01, 0.5, 3.7]
        if t in ('int8', 'uint8'):
            errs = [1, 2.5, 6]
        for ei, e in enumerate(errs):
            for ci, kind in enumerate(['smooth', 'mixed']):
                v = 2 + (ti + ei + ci * 2) % 5
                seed += 1
                a = content(kind, 22, 27, 1, t, seed)
                emit(f'b_{SHORT[t]}_{kind}_e{e}_v{v}', lerc_encode(a[None], e, v))

    # C: sizes
    sizes = [(1, 1), (1, 7), (9, 1), (8, 8), (16, 16), (17, 13), (33, 21), (8, 65), (70, 5), (2, 2), (15, 17)]
    for si, (h, w) in enumerate(sizes):
        for ti, t in enumerate(['uint8', 'int16', 'float32']):
            v = 2 + (si + ti) % 5
            e = [0, 1, 0.05][(si + ti) % 3]
            seed += 1
            a = content('smooth' if (si + ti) % 2 else 'noise', h, w, 1, t, seed)
            emit(f'c_{h}x{w}_{SHORT[t]}_e{e}_v{v}', lerc_encode(a[None], e, v))

    # D: masks
    for mi, mk in enumerate(['random', 'hole', 'none', 'single', 'corner', 'rows', 'checker', 'blocks']):
        for ti, t in enumerate(['uint8', 'uint16', 'float32', 'float64', 'int32']):
            if (mi + ti) % 5 >= 3:
                continue
            v = 2 + (mi + ti * 2) % 5
            e = [0, 2, 0.1, 0][(mi + ti) % 4]
            seed += 1
            a = content('smooth' if ti % 2 else 'mixed', 23, 30, 1, t, seed)
            emit(f'd_{mk}_{SHORT[t]}_e{e}_v{v}', lerc_encode(a[None], e, v, mask=mask_of(mk, 23, 30, seed)))

    # E: several values per pixel (v4+); v5+ may code a depth value relative to the previous one
    for di, d in enumerate([2, 3, 5]):
        for ti, t in enumerate(TYPES):
            for k, kind in enumerate(['smooth', 'noise', 'mixed']):
                if (di + ti + k) % 3 == 2:
                    continue
                v = 4 + (di + ti + k) % 3
                lossy = (di + ti + k) % 2 == 1
                e = 0 if not lossy else (2 if np.dtype(t).kind in 'iu' else 0.05)
                mk = ['all', 'hole', 'random'][(di * 2 + ti + k) % 3]
                seed += 1
                a = content(kind, 14, 13, d, t, seed)
                m = None if mk == 'all' else mask_of(mk, 14, 13, seed)
                emit(f'e_d{d}_{SHORT[t]}_{kind}_e{e}_{mk}_v{v}', lerc_encode(a[None], e, v, mask=m))

    # F: bands (concatenated blobs); masks shared, per band, or only on some bands
    k = 0
    for nb in (2, 3):
        for d in (1, 2):
            for mk in ('all', 'shared', 'perband', 'firstonly', 'lastonly', 'empty-band'):
                for t in [TYPES[(k * 3) % 8]]:
                    v = 2 + k % 5 if d == 1 else 4 + k % 3
                    e = 0 if k % 3 else (1 if np.dtype(t).kind in 'iu' else 0.02)
                    seed += 1
                    k += 1
                    a = np.stack([content(['smooth', 'mixed', 'noise'][b % 3], 12, 17, d, t, seed * 10 + b) for b in range(nb)])
                    if mk == 'all':
                        m = None
                    elif mk == 'shared':
                        m = mask_of('hole', 12, 17, seed)
                    else:
                        m = np.stack([mask_of(['random', 'hole', 'rows'][b % 3], 12, 17, seed + b) for b in range(nb)])
                        if mk == 'firstonly':
                            m[1:] = 1
                        elif mk == 'lastonly':
                            m[:-1] = 1
                        elif mk == 'empty-band':
                            m[1] = 0
                            m[2:] = m[0]
                    emit(f'f_b{nb}_d{d}_{mk}_{SHORT[t]}_e{e}_v{v}', lerc_encode(a, e, v, mask=m))

    # G: 8 bit lossless, Huffman modes
    for ti, t in enumerate(['uint8', 'int8']):
        for kind in ('photo', 'skew'):
            for v in range(2, 7):
                seed += 1
                a = content(kind, 30, 33, 1, t, seed, full=True)
                emit(f'g_{SHORT[t]}_{kind}_v{v}', lerc_encode(a[None], 0, v))
            for d, mk, v in ((3, 'all', 4), (3, 'hole', 6), (1, 'random', 3), (2, 'rows', 5), (1, 'blocks', 2)):
                seed += 1
                a = content(kind, 30, 33, d, t, seed, full=True)
                m = None if mk == 'all' else mask_of(mk, 30, 33, seed)
                emit(f'g_{SHORT[t]}_{kind}_d{d}_{mk}_v{v}', lerc_encode(a[None], 0, v, mask=m))
    # long Huffman codes: a few very rare values among one dominant one
    rng = np.random.default_rng(77)
    a = np.zeros((90, 90, 1), np.uint8)
    p = 0.5 ** np.arange(1, 25)
    vals = rng.choice(np.arange(24), (90, 90), p=p / p.sum())
    a[..., 0] = (vals * 9 + 3).astype(np.uint8)
    emit('g_u8_longcodes_v4', lerc_encode(a[None], 0, 4))

    # H: v6 lossless floating point
    for ti, t in enumerate(['float32', 'float64']):
        for kind in ('smooth', 'ramp', 'noise', 'mixed', 'steps'):
            seed += 1
            a = content(kind, 30, 34, 1, t, seed)
            emit(f'h_{SHORT[t]}_{kind}_v6', lerc_encode(a[None], 0, 6))
        for d, mk in ((3, 'all'), (2, 'hole'), (1, 'random'), (1, 'corner')):
            seed += 1
            a = content('smooth', 30, 34, d, t, seed)
            m = None if mk == 'all' else mask_of(mk, 30, 34, seed)
            emit(f'h_{SHORT[t]}_smooth_d{d}_{mk}_v6', lerc_encode(a[None], 0, 6, mask=m))
        # whole numbers stored as floating point: the encoder notices and codes them like integers
        seed += 1
        a = np.round(content('smooth', 30, 34, 1, t, seed))
        emit(f'h_{SHORT[t]}_wholenumbers_v6', lerc_encode(a[None], 0, 6))
        # special values
        seed += 1
        a = content('smooth', 12, 14, 1, t, seed)
        a[3, 4, 0] = np.inf
        a[5, 6, 0] = -np.inf
        a[7, 1, 0] = -0.0
        a[8, 2, 0] = np.finfo(t).tiny / 4
        try:
            emit(f'h_{SHORT[t]}_special_v6', lerc_encode(a[None], 0, 6))
        except RuntimeError as ex:
            print('  (liblerc refuses inf input:', ex, ')')
    # larger and smoother: the byte planes get extra delta passes
    y, x = np.mgrid[0:150, 0:150].astype(np.float64)
    big = np.sin(x / 90.0) * np.cos(y / 70.0) * 1000
    emit('h_f32_big_v6', lerc_encode(big.astype(np.float32)[None, :, :, None], 0, 6))
    emit('h_f64_big_v6', lerc_encode(np.exp(x / 50.0 + y / 70.0)[None, :110, :110, None], 0, 6))

    # I: v6 no-data values in pixels that are only partly valid (lerc_encode_4D)
    k = 0
    for t in ('uint8', 'int16', 'float32', 'float64', 'uint16', 'int32'):
        for e in (0, 1 if np.dtype(t).kind in 'iu' else 0.25):
            for where in ('below', 'inside', 'above'):
                k += 1
                if k % 3 == 0:
                    continue
                seed += 1
                d = 2 + k % 3
                a = content('smooth', 16, 18, d, t, seed)
                lo, hi = float(a.min()), float(a.max())
                if where == 'below':
                    nd = 0 if np.dtype(t).kind == 'u' else lo - 1000
                    if np.dtype(t).kind == 'u':
                        a = np.maximum(a, 5)
                elif where == 'above':
                    nd = hi + 37
                    if t == 'uint8':
                        a = np.minimum(a, 200)
                        nd = 255
                else:
                    nd = np.round((lo + hi) / 2)
                    a[a == nd] += 1
                nd = np.dtype(t).type(nd)
                rng = np.random.default_rng(seed)
                holes = rng.random(a.shape) < 0.15
                a[holes] = nd
                a[2:5, 3:9, :] = nd  # these pixels are invalid as a whole
                emit(f'i_nodata_{where}_{SHORT[t]}_d{d}_e{e}', lerc_encode(a[None], e, 6, nodata=[(True, float(nd))]))
    # two bands, only one with a no-data value; and NaN input
    seed += 1
    a = np.stack([content('smooth', 16, 18, 3, 'float32', seed), content('mixed', 16, 18, 3, 'float32', seed + 1)])
    a[0][np.random.default_rng(seed).random(a[0].shape) < 0.2] = -9999
    emit('i_nodata_band0only_f32_d3', lerc_encode(a, 0.01, 6, nodata=[(True, -9999.0), (False, 0.0)]))
    seed += 1
    a = content('smooth', 16, 18, 2, 'float64', seed)
    a[np.random.default_rng(seed).random(a.shape) < 0.2] = np.nan
    emit('i_nan_f64_d2', lerc_encode(a[None], 0.01, 6, nodata=[(True, -1e30)]))
    a = content('smooth', 16, 18, 1, 'float32', seed)
    a[np.random.default_rng(seed).random(a.shape) < 0.2] = np.nan
    emit('i_nan_f32_d1', lerc_encode(a[None], 0, 6))

    # J: low entropy at a larger size: 16x16 blocks
    for t in ('uint16', 'float32', 'int32'):
        seed += 1
        a = content('steps', 56, 64, 1, t, seed)
        a[20:40, 20:60, 0] += (np.arange(40) % 3).astype(a.dtype)
        emit(f'j_blocks16_{SHORT[t]}', lerc_encode(a[None], 0 if t != 'float32' else 0.5, 4))


# ---------------------------------------------------------------- LERC1 through GDAL's MRF driver

LERC1_CASES = [('smooth', 64, 0.01, 'all'), ('smooth', 37, 0.5, 'hole'), ('noise', 32, 0.0, 'all'), ('steps', 40, 0.1, 'random'),
               ('mixed', 50, 5.0, 'all'), ('const', 16, 0.01, 'all'), ('zero', 16, 0.01, 'all'),
               ('smooth', 5, 0.01, 'all'), ('sparse', 45, 0.001, 'rows'), ('ramp', 100, 0.25, 'corner'), ('smooth', 7, 1.0, 'single')]


def mrf_multiband_worker(tmp):
    """Three bands in one pixel interleaved MRF tile: GDAL writes three complete LERC1 blobs one after the other.
    GDAL reads them back as the reference (liblerc expects later bands without their mask part and crashes)."""
    import logging
    import rasterio
    logging.getLogger('rasterio').setLevel(logging.ERROR)
    for name, holes in (('k_lerc1_gdal_b3', 'perband'), ('k_lerc1_gdal_b3_shared', 'shared')):
        n = 24
        a = np.stack([content(['smooth', 'mixed', 'steps'][b], n, n, 1, 'float32', 7100 + b)[..., 0] for b in range(3)])
        for b in range(3):
            a[b][mask_of('hole' if holes == 'shared' else ['hole', 'random', 'rows'][b], n, n, 7200 if holes == 'shared' else 7200 + b) == 0] = -32000.0
        path = os.path.join(tmp, name + '.mrf')
        with rasterio.open(path, 'w', driver='MRF', width=n, height=n, count=3, dtype='float32', nodata=-32000.0,
                           COMPRESS='LERC', BLOCKSIZE=n, INTERLEAVE='PIXEL', OPTIONS='V1=ON LERC_PREC=0.05') as ds:
            ds.write(a)
        with open(path[:-4] + '.lrc', 'rb') as f:
            blob = f.read()
        assert blob.count(b'CntZImage ') == 3
        with rasterio.open(path) as ds:
            back = ds.read()
        valid = (back != -32000.0).astype(np.uint8)
        with open(os.path.join(tmp, name + '.gdal'), 'wb') as f:
            f.write(blob)
        np.save(os.path.join(tmp, name + '.data.npy'), back)
        np.save(os.path.join(tmp, name + '.valid.npy'), valid)


def esri_style(blob):
    """GDAL's multi-band LERC1 (complete blobs) as Esri's writer lays it out: later bands without the count part."""
    out, p = bytearray(), 0
    while blob[p:p + 10] == b'CntZImage ':  # (the MRF data file may continue with padding)
        cnt = struct.unpack('<iiif', blob[p + 34:p + 50])
        zp = p + 50 + cnt[2]
        z = struct.unpack('<iiif', blob[zp:zp + 16])
        end = zp + 16 + z[2]
        out += blob[p:end] if p == 0 else blob[p:p + 34] + blob[zp:end]
        p = end
    return bytes(out)


def emit_gdal(name, blob, data, valid):
    """A blob liblerc can not read: GDAL's reading of it is the reference."""
    assert name not in written, name
    written.add(name)
    nb, nr, nc = data.shape
    data = np.where(valid != 0, data, np.float32(0))
    with open(os.path.join(OUT, name + '.lerc'), 'wb') as f:
        f.write(blob)
    ref = np.ascontiguousarray(data.transpose(1, 2, 0).astype(np.float32))
    sha = hashlib.sha256(ref.tobytes())
    write_ref(os.path.join(OUT, name + '.lerc.ref'), ref)
    if not valid.all():
        same = all(np.array_equal(valid[b], valid[0]) for b in range(nb))
        m = np.ascontiguousarray((valid[:1] if same else valid).transpose(1, 2, 0))
        sha.update(m.tobytes())
        write_ref(os.path.join(OUT, name + '.lerc.mask.ref'), m)
    v = data[valid != 0]
    mze = struct.unpack('<d', blob[26:34])[0]
    size = len(esri_style(blob)) + sum(16 + struct.unpack('<i', blob[p + 42:p + 46])[0]  # the mask parts left out there
                                       for p in [i for i in range(1, len(blob)) if blob.startswith(b'CntZImage ', i)])
    manifest.append('\t'.join(str(x) for x in [
        name + '.lerc', 1, 6, nc, nr, 1, nb, int(valid[0].sum()), 1 if not valid.all() else 0, 0,
        repr(float(v.min())), repr(float(v.max())), repr(mze), size, '-', sha.hexdigest()]))


def mrf_worker(tmp, case):
    """Runs in its own process, one per blob: GDAL bundles another liblerc, which must not share a process with
    the pip one, and writing several MRF datasets in one process corrupted the heap here (GDAL 3.12.1)."""
    import logging
    import rasterio
    logging.getLogger('rasterio').setLevel(logging.ERROR)
    kind, n, e, mk = LERC1_CASES[case]
    seed = 5001 + case
    if True:
        a = content(kind, n, n, 1, 'float32', seed)[..., 0].copy()
        a[mask_of(mk, n, n, seed) == 0] = -32000.0
        path = os.path.join(tmp, f'{kind}{n}.mrf')
        with rasterio.open(path, 'w', driver='MRF', width=n, height=n, count=1, dtype='float32', nodata=-32000.0,
                           COMPRESS='LERC', BLOCKSIZE=n, OPTIONS=f'V1=ON LERC_PREC={e}') as ds:
            ds.write(a, 1)
        idx = open(path[:-4] + '.idx', 'rb').read()
        off, size = struct.unpack('>QQ', idx[:16])  # (a tile without any valid pixel is not stored at all)
        with open(path[:-4] + '.lrc', 'rb') as f:
            f.seek(off)
            blob = f.read(size)
        assert blob[:10] == b'CntZImage ', (kind, n)
        with open(os.path.join(tmp, f'k_lerc1_{kind}{n}_e{e}_{mk}.blob'), 'wb') as f:
            f.write(blob)


def gen_lerc1():
    with tempfile.TemporaryDirectory(prefix='_lerc1_', dir=os.path.dirname(OUT)) as tmp:
        for case in range(len(LERC1_CASES)):
            r = subprocess.run([sys.executable, os.path.abspath(__file__), '--mrf-worker', tmp, str(case)],
                               capture_output=True, text=True)
            if r.returncode != 0:
                raise RuntimeError(f'LERC1 generation through GDAL MRF failed (rasterio needed), exit {r.returncode}:\n' + r.stderr)
        for f in sorted(os.listdir(tmp)):
            if f.endswith('.blob'):
                with open(os.path.join(tmp, f), 'rb') as fh:
                    emit(f[:-5], fh.read())
        r = subprocess.run([sys.executable, os.path.abspath(__file__), '--mrf-multiband-worker', tmp], capture_output=True, text=True)
        if r.returncode != 0:
            raise RuntimeError(f'multi-band LERC1 generation through GDAL MRF failed, exit {r.returncode}:\n' + r.stderr)
        for name in ('k_lerc1_gdal_b3', 'k_lerc1_gdal_b3_shared'):
            with open(os.path.join(tmp, name + '.gdal'), 'rb') as fh:
                blob = fh.read()
            data = np.load(os.path.join(tmp, name + '.data.npy'))
            valid = np.load(os.path.join(tmp, name + '.valid.npy'))
            emit_gdal(name, blob, data, valid)
            if name.endswith('shared'):
                # the same bands as Esri's writer would store them, which liblerc reads
                emit('k_lerc1_esri_b3', esri_style(blob))


# ---------------------------------------------------------------- Esri's sample blobs

ESRI = ['bluemarble_256_256_3_byte.lerc2', 'california_400_400_1_float.lerc2', 'world.lerc1']
MANIFEST_HEADER = '# file\tversion\tdataType\tcols\trows\tdepth\tbands\tvalidPixels\tmasks\tusesNoData\tzMin\tzMax\tmaxZError\tblobSize\tnoData\tsha256\n'


def write_manifest(out, lines):
    with open(os.path.join(out, 'manifest.tsv'), 'w') as f:
        f.write(MANIFEST_HEADER)
        f.write('\n'.join(lines) + '\n')


def gen_esri(fetch):
    out = testdata_dir('fixtures', 'lerc')
    if out is None:
        return
    os.makedirs(out, exist_ok=True)
    for f in os.listdir(out):
        if f.endswith('.ref'):
            os.remove(os.path.join(out, f))
    lines = []
    for f in ESRI:
        name = 'esri_' + f.replace('.', '_')
        local = os.path.join(out, name + '.lerc')
        if fetch:
            urllib.request.urlretrieve('https://github.com/Esri/lerc/raw/master/testData/' + f, local)
        if not os.path.exists(local):
            print('missing', local, '(run with --fetch-esri)')
            continue
        with open(local, 'rb') as fh:
            blob = fh.read()
        emit(name, blob, out, lines)
    write_manifest(out, lines)
    print(len(lines), 'Esri sample blobs in', out)


def main():
    if len(sys.argv) == 3 and sys.argv[1] == '--mrf-multiband-worker':
        mrf_multiband_worker(sys.argv[2])
        return
    if len(sys.argv) == 4 and sys.argv[1] == '--mrf-worker':
        mrf_worker(sys.argv[2], int(sys.argv[3]))
        return
    os.makedirs(OUT, exist_ok=True)
    for f in os.listdir(OUT):
        if f.endswith(('.lerc', '.ref')) or f == 'manifest.tsv':
            os.remove(os.path.join(OUT, f))
    gen_liblerc()
    gen_lerc1()
    write_manifest(OUT, manifest)
    total = sum(os.path.getsize(os.path.join(OUT, f)) for f in os.listdir(OUT))
    print(len(manifest), 'blobs,', total // 1024, 'KiB in', OUT)
    gen_esri('--fetch-esri' in sys.argv)


if __name__ == '__main__':
    main()
