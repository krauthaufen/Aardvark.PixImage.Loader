"""Generates the synthetic DNG fixtures (tests/fixtures/dng) and their reference sidecars.

usage: python3 gen_dng.py            (from tests/oracle; deterministic, overwrites the fixture directory)

The files come from the small DNG writer below (no installed tool writes the matrix of layouts: CFA patterns, odd bit
depths, both byte orders, lossless JPEG tiles with the DNG frame reshaping, Deflate with the DNG predictors X2 / X4 and
floats, lossy JPEG, JPEG XL tiles, interleaved rows / columns, opcode lists, one / two colour calibrations).

Sidecars:
  name.dng.ref      stored samples as tifffile + imagecodecs decode them (third party; interleaved rows / columns are put
                    back in order with numpy, as DNG documents it)
  name.dng.lin.ref  linear reference values (DngRawStage.Linear: opcode lists 1 and 2, linearization, black / white),
                    Float32, from the numpy model of DNG 1.7.1 chapters 5 and 7 in this file (written from the
                    specification independently of the C# code; no third-party reader applies opcode lists)
  name.dng.rawpy.ref  LibRaw's (rawpy) linear sRGB rendering, Float32 0..1, for the files whose colour pipeline LibRaw
                    implements the way the DNG specification does (one D65 calibration, as-shot neutral of D65)
  name.dng.color.ref  patch colours (linear sRGB, Float32, one pixel per patch) from the numpy model of chapter 6 below
  name.dng.json     parameters to check DngInfo against
"""
import io
import json
import math
import os
import shutil
import struct
import sys
import zlib

import imagecodecs
import numpy as np
import tifffile

from pxref import write_ref, missing

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, '..', 'fixtures', 'dng')

# Canon EOS 5D Mark III, as Adobe's converter writes it: ColorMatrix1 (Standard A), ColorMatrix2 (D65), forward matrices
CM_A = [0.7234, -0.1413, -0.0600, -0.3631, 1.1150, 0.2850, -0.0382, 0.1335, 0.6437]
CM_D65 = [0.6722, -0.0635, -0.0963, -0.4287, 1.2460, 0.2028, -0.0908, 0.2162, 0.5668]
FM_A = [0.7868, 0.0092, 0.1683, 0.2291, 0.8615, -0.0906, 0.0027, -0.4752, 1.2976]
FM_D65 = [0.7637, 0.0805, 0.1201, 0.2758, 0.9214, -0.1972, 0.0118, -0.2706, 1.0839]


# ----------------------------------------------------------------------------------------------- TIFF writer

class Tag:
    def __init__(self, tag, typ, values):
        self.tag, self.typ = tag, typ
        self.values = values if isinstance(values, (list, tuple, bytes, str, np.ndarray)) else [values]


BYTE, ASCII, SHORT, LONG, RATIONAL, UNDEF, SRATIONAL, FLOAT, DOUBLE = 1, 2, 3, 4, 5, 7, 10, 11, 12


def rat(v, signed=False):
    d = 10000
    return (int(round(v * d)), d)


def pack_tag_values(t, bo):
    if t.typ in (BYTE, UNDEF):
        return bytes(bytearray(t.values)), len(t.values)
    if t.typ == ASCII:
        b = t.values.encode() + b'\0' if isinstance(t.values, str) else bytes(t.values)
        return b, len(b)
    if t.typ == SHORT:
        return b''.join(struct.pack(bo + 'H', int(v)) for v in t.values), len(t.values)
    if t.typ == LONG:
        return b''.join(struct.pack(bo + 'I', int(v)) for v in t.values), len(t.values)
    if t.typ in (RATIONAL, SRATIONAL):
        f = 'I' if t.typ == RATIONAL else 'i'
        out = b''
        for v in t.values:
            n, d = rat(v)
            out += struct.pack(bo + f + f, n, d)
        return out, len(t.values)
    if t.typ == FLOAT:
        return b''.join(struct.pack(bo + 'f', v) for v in t.values), len(t.values)
    if t.typ == DOUBLE:
        return b''.join(struct.pack(bo + 'd', v) for v in t.values), len(t.values)
    raise ValueError(t.typ)


def write_tiff(path, ifds, big_endian=False):
    """ifds: list of (tags, chunks); chunks are the strip / tile byte strings, the offsets / byte count tags are added
    (tag 273/279 or 324/325, chosen by the presence of TileWidth). Extra directories go into IFD 0's SubIFDs when the
    first directory's tag list contains the marker Tag(330, LONG, 'sub')."""
    bo = '>' if big_endian else '<'
    data = bytearray(b'MM\0*' if big_endian else b'II*\0')
    data += b'\0\0\0\0'

    def align():
        if len(data) & 1:
            data.append(0)

    # chunk data first
    offsets = []
    for tags, chunks in ifds:
        offs = []
        for c in chunks:
            align()
            offs.append(len(data))
            data.extend(c)
        offsets.append(offs)
    ifd_pos = []

    def write_ifd(tags, offs, counts, tiled, sub_offsets):
        tags = list(tags)
        tags = [t for t in tags if t.tag != 330]
        if sub_offsets:
            tags.append(Tag(330, LONG, sub_offsets))
        tags.append(Tag(324 if tiled else 273, LONG, offs))
        tags.append(Tag(325 if tiled else 279, LONG, counts))
        tags.sort(key=lambda t: t.tag)
        align()
        start = len(data)
        n = len(tags)
        data.extend(struct.pack(bo + 'H', n))
        entries_at = len(data)
        data.extend(b'\0' * (12 * n + 4))
        for i, t in enumerate(tags):
            vb, cnt = pack_tag_values(t, bo)
            e = struct.pack(bo + 'HHI', t.tag, t.typ, cnt)
            if len(vb) <= 4:
                e += vb + b'\0' * (4 - len(vb))
            else:
                align()
                vo = len(data)
                data.extend(vb)
                e += struct.pack(bo + 'I', vo)
            data[entries_at + 12 * i: entries_at + 12 * i + 12] = e
        return start, entries_at + 12 * n

    # sub-IFDs (all but the first) first, so that IFD 0 can point at them
    sub_offs = []
    for k in range(1, len(ifds)):
        tags, chunks = ifds[k]
        tiled = any(t.tag == 322 for t in tags)
        s, _ = write_ifd(tags, offsets[k], [len(c) for c in chunks], tiled, None)
        sub_offs.append(s)
    tags, chunks = ifds[0]
    tiled = any(t.tag == 322 for t in tags)
    s, _ = write_ifd(tags, offsets[0], [len(c) for c in chunks], tiled, sub_offs)
    data[4:8] = struct.pack(bo + 'I', s)
    with open(path, 'wb') as f:
        f.write(bytes(data))


# ----------------------------------------------------------------------------------------------- DNG helpers

def base_tags(w, h, bits, spp=1, photometric=32803, compression=1, sample_format=1, orientation=1, cfa=None,
              black=None, black_dim=(1, 1), white=None, active=None, crop=None, colour='d65', neutral=None,
              baseline_exposure=0.0, lin=None, extra=()):
    t = [
        Tag(254, LONG, 0), Tag(256, LONG, w), Tag(257, LONG, h), Tag(258, SHORT, [bits] * spp), Tag(259, SHORT, compression),
        Tag(262, SHORT, photometric), Tag(271, ASCII, 'PixCodec'), Tag(272, ASCII, 'Synthetic'), Tag(274, SHORT, orientation),
        Tag(277, SHORT, spp), Tag(284, SHORT, 1), Tag(339, SHORT, [sample_format] * spp),
        Tag(50706, BYTE, [1, 7, 1, 0]), Tag(50707, BYTE, [1, 4, 0, 0]), Tag(50708, ASCII, 'PixCodec Synthetic'),
    ]
    if cfa is not None:
        rows, cols, pattern = cfa
        t += [Tag(33421, SHORT, [rows, cols]), Tag(33422, BYTE, pattern), Tag(50710, BYTE, [0, 1, 2]), Tag(50711, SHORT, 1)]
    if black is not None:
        t += [Tag(50713, SHORT, list(black_dim)), Tag(50714, RATIONAL, black)]
    if white is not None:
        t.append(Tag(50717, LONG, white))
    if active is not None:
        t.append(Tag(50829, LONG, list(active)))
    if crop is not None:
        t += [Tag(50719, RATIONAL, crop[:2]), Tag(50720, RATIONAL, crop[2:])]
    if lin is not None:
        t.append(Tag(50712, SHORT, list(lin)))
    if colour == 'd65':
        t += [Tag(50778, SHORT, 21), Tag(50721, SRATIONAL, CM_D65)]
    elif colour == 'dual':
        t += [Tag(50778, SHORT, 17), Tag(50779, SHORT, 21), Tag(50721, SRATIONAL, CM_A), Tag(50722, SRATIONAL, CM_D65),
              Tag(50964, SRATIONAL, FM_A), Tag(50965, SRATIONAL, FM_D65)]
    if neutral is not None:
        t.append(Tag(50728, RATIONAL, list(neutral)))
    t.append(Tag(50730, SRATIONAL, baseline_exposure))
    return t + list(extra)


def strips(arr_bytes_rows, rows_per_strip, row_bytes):
    """Splits raw row bytes into strips."""
    out = []
    for i in range(0, len(arr_bytes_rows) // row_bytes, rows_per_strip):
        out.append(arr_bytes_rows[i * row_bytes:(i + rows_per_strip) * row_bytes])
    return out


def pack_bits(a, bits):
    """Packs unsigned samples MSB first into rows padded to a byte (TIFF / DNG rule, any byte order)."""
    h = a.shape[0]
    rows = []
    for y in range(h):
        bitsarr = np.unpackbits(a[y].reshape(-1).astype('>u4').view(np.uint8).reshape(-1, 4), axis=1)[:, 32 - bits:]
        rows.append(np.packbits(bitsarr.reshape(-1)).tobytes())
    return b''.join(rows), len(rows[0])


def neutral_for(cm, xy):
    m = np.array(cm).reshape(3, 3)
    xyz = np.array([xy[0] / xy[1], 1.0, (1 - xy[0] - xy[1]) / xy[1]])
    n = m @ xyz
    return n / n.max()


D65 = (0.31271, 0.32902)
D50 = (0.34567, 0.35850)

# camera space patch colours (linear, 0..1 of the white level minus black), 6 x 4 patches
PATCHES = np.array([
    [0.08, 0.06, 0.05], [0.30, 0.22, 0.18], [0.10, 0.15, 0.25], [0.08, 0.12, 0.05], [0.18, 0.18, 0.30], [0.20, 0.40, 0.35],
    [0.45, 0.25, 0.06], [0.07, 0.10, 0.30], [0.35, 0.12, 0.12], [0.06, 0.04, 0.10], [0.30, 0.45, 0.08], [0.50, 0.38, 0.06],
    [0.03, 0.05, 0.20], [0.08, 0.25, 0.08], [0.30, 0.06, 0.05], [0.60, 0.55, 0.06], [0.35, 0.10, 0.25], [0.04, 0.20, 0.30],
    [0.80, 0.80, 0.80], [0.50, 0.50, 0.50], [0.30, 0.30, 0.30], [0.15, 0.15, 0.15], [0.07, 0.07, 0.07], [0.02, 0.02, 0.02],
])


def patch_scene(w, h, neutral, cols=6, rows=4):
    """A linear camera image of flat patches (patch values are white balanced colours times the neutral, so that the grey
    row is neutral)."""
    img = np.zeros((h, w, 3))
    pw, ph = w // cols, h // rows
    for i, c in enumerate(PATCHES):
        r, k = divmod(i, cols)
        img[r * ph:(r + 1) * ph, k * pw:(k + 1) * pw] = np.array(c) * neutral
    return img, pw, ph


def mosaic(img, pattern, rr, rc):
    h, w, _ = img.shape
    out = np.zeros((h, w))
    for y in range(rr):
        for x in range(rc):
            out[y::rr, x::rc] = img[y::rr, x::rc, pattern[y * rc + x]]
    return out


# ----------------------------------------------------------------------------------------------- the numpy model

def linearize(stored, black_pat, black_dim, white, lin=None, dh=None, dv=None):
    """Chapter 5 on the active area: table, black (pattern + deltas), scaling by 1 / (white - max black), values > 1 clipped."""
    v = stored.astype(np.float64)
    if lin is not None:
        v = np.asarray(lin, np.float64)[np.minimum(stored, len(lin) - 1)]
    h, w = v.shape[:2]
    br, bc = black_dim
    b = np.zeros((h, w))
    bp = np.asarray(black_pat, np.float64).reshape(br, bc)
    for y in range(br):
        for x in range(bc):
            b[y::br, x::bc] = bp[y, x]
    mx = bp.max()
    if dh is not None:
        b += np.asarray(dh)[None, :]
        mx += max(dh)
    if dv is not None:
        b += np.asarray(dv)[:, None]
        mx += max(dv)
    r = (v - b) / (white - mx)
    return np.minimum(r, 1.0)


def cct(x, y):
    """Correlated colour temperature: minimum distance to the Planckian locus (Krystek's approximation) in CIE 1960 uv,
    by a dense search in reciprocal temperature."""
    d = -2 * x + 12 * y + 3
    u, v = 4 * x / d, 6 * y / d
    mired = np.linspace(1e6 / 15000, 1e6 / 1000, 200001)
    t = 1e6 / mired
    pu = (0.860117757 + 1.54118254e-4 * t + 1.28641212e-7 * t * t) / (1 + 8.42420235e-4 * t + 7.08145163e-7 * t * t)
    pv = (0.317398726 + 4.22806245e-5 * t + 4.20481691e-8 * t * t) / (1 - 2.89741816e-5 * t + 1.61456053e-7 * t * t)
    return t[np.argmin((pu - u) ** 2 + (pv - v) ** 2)]


BRADFORD = np.array([[0.8951, 0.2664, -0.1614], [-0.7502, 1.7135, 0.0367], [0.0389, -0.0685, 1.0296]])


def xyz(xy):
    return np.array([xy[0] / xy[1], 1.0, (1 - xy[0] - xy[1]) / xy[1]])


def bradford(src, dst):
    s, d = BRADFORD @ xyz(src), BRADFORD @ xyz(dst)
    return np.linalg.inv(BRADFORD) @ np.diag(d / s) @ BRADFORD


def srgb_from_xyz_d50():
    prim = np.array([[0.64, 0.33], [0.30, 0.60], [0.15, 0.06]])
    p = np.array([xyz(c) for c in prim]).T
    s = np.linalg.solve(p, xyz(D65))
    rgb2xyz65 = p @ np.diag(s)
    return np.linalg.inv(bradford(D65, D50) @ rgb2xyz65)


def norm_cm(cm):
    """Colour matrices are used scaled so that D50 maps to a camera neutral with largest component 1 (the scale of a
    matrix carries no meaning; the reader documents this convention)."""
    m = np.array(cm).reshape(3, 3)
    return m / (m @ xyz(D50)).max()


def dual_weight(t, t1=2856, t2=6504):
    if t <= t1:
        return 1.0
    if t >= t2:
        return 0.0
    return (1 / t - 1 / t2) / (1 / t1 - 1 / t2)


def neutral_to_xy(neutral):
    xy = D50
    for _ in range(100):
        w = dual_weight(cct(*xy))
        cm = w * norm_cm(CM_A) + (1 - w) * norm_cm(CM_D65)
        X = np.linalg.solve(cm, neutral)
        nxy = (X[0] / X.sum(), X[1] / X.sum())
        if abs(nxy[0] - xy[0]) < 1e-9 and abs(nxy[1] - xy[1]) < 1e-9:
            break
        xy = nxy
    return xy


def camera_to_srgb_dual(neutral):
    """Chapter 6 with two calibrations and forward matrices: CameraToXYZ_D50 = FM * D (AB = CC = identity)."""
    xy = neutral_to_xy(neutral)
    w = dual_weight(cct(*xy))
    fm = w * np.array(FM_A).reshape(3, 3) + (1 - w) * np.array(FM_D65).reshape(3, 3)
    c2x = fm @ np.diag(1 / np.asarray(neutral))
    return srgb_from_xyz_d50() @ c2x, xy, w


def camera_to_srgb_single(neutral):
    """One calibration, no forward matrix: CA * inverse(ColorMatrix1), Bradford from the white to D50."""
    cm = norm_cm(CM_D65)
    X = np.linalg.solve(cm, neutral)
    xy = (X[0] / X.sum(), X[1] / X.sum())
    return srgb_from_xyz_d50() @ bradford(xy, D50) @ np.linalg.inv(cm), xy


# opcodes ---------------------------------------------------------------------------------------

def opcode(oid, params, optional=False, version=(1, 3, 0, 0)):
    v = (version[0] << 24) | (version[1] << 16) | (version[2] << 8) | version[3]
    return struct.pack('>IIII', oid, v, 1 if optional else 0, len(params)) + params


def opcode_list(ops):
    return struct.pack('>I', len(ops)) + b''.join(ops)


def area(top, left, bottom, right, plane, planes, rp, cp):
    return struct.pack('>8I', top, left, bottom, right, plane, planes, rp, cp)


def apply_area(img, top, left, bottom, right, plane, planes, rp, cp, f):
    """img: (planes, h, w)."""
    for p in range(plane, plane + planes):
        if p >= img.shape[0]:
            break
        ys = np.arange(top, bottom, rp)
        xs = np.arange(left, right, cp)
        sub = img[p][np.ix_(ys, xs)]
        img[p][np.ix_(ys, xs)] = f(sub, ys, xs, p - plane)


# ----------------------------------------------------------------------------------------------- fixtures

def write_json(path, d):
    with open(path, 'w') as f:
        json.dump(d, f, indent=1, sort_keys=True)


UNVERIFIED = []


def ref_stored(path, dng, source=None):
    """Third-party decode of the raw IFD's samples (the IFD with NewSubFileType 0). Where tifffile cannot decode the file
    (imagecodecs lacks the DNG X2 / X4 predictors for some sample types), the source samples the file was written from
    are the reference and the file is listed as unverified."""
    try:
        with tifffile.TiffFile(dng) as tf:
            a = tf.pages[0].asarray()
    except NotImplementedError:
        if source is None:
            raise
        a = source
        UNVERIFIED.append(os.path.basename(dng))
    if a.ndim == 2:
        a = a[:, :, None]
    write_ref(path, a)
    return a


def rawpy_render(dng, out):
    try:
        import rawpy
    except ImportError:
        missing('python:rawpy')
    with rawpy.imread(dng) as r:
        rgb = r.postprocess(use_camera_wb=True, no_auto_bright=True, output_bps=16, gamma=(1, 1), user_flip=0,
                            output_color=rawpy.ColorSpace.sRGB, demosaic_algorithm=rawpy.DemosaicAlgorithm.LINEAR,
                            highlight_mode=rawpy.HighlightMode.Clip)
    write_ref(out, (rgb.astype(np.float32) / 65535).astype(np.float32))


def main():
    shutil.rmtree(OUT, ignore_errors=True)
    os.makedirs(OUT)
    rng = np.random.default_rng(7)

    # 1. Bayer RGGB, 16-bit uncompressed strips, masked border, 2 x 2 black pattern, default crop, one D65 calibration ----
    W, H, aT, aL = 96, 64, 2, 4
    RW, RH = W + 8, H + 4
    n65 = neutral_for(CM_D65, D65)
    scene, pw, ph = patch_scene(W, H, n65)
    black = [256, 260, 258, 262]
    white = 16000
    pat = [0, 1, 1, 2]
    m = mosaic(scene, pat, 2, 2)
    bp = np.array(black, float).reshape(2, 2)
    bl = np.tile(bp, (H // 2, W // 2))
    stored = np.full((RH, RW), 256, np.uint16)
    stored[aT:aT + H, aL:aL + W] = np.round(bl + m * (white - bp.max())).astype(np.uint16)
    tags = base_tags(RW, RH, 16, cfa=(2, 2, pat), black=black, black_dim=(2, 2), white=white,
                     active=(aT, aL, aT + H, aL + W), crop=(2, 2, W - 4, H - 4), neutral=list(n65)) + [Tag(278, LONG, 16)]
    p = os.path.join(OUT, 'bayer16_rggb.dng')
    rb = stored.astype('<u2').tobytes()
    write_tiff(p, [(tags, strips(rb, 16, RW * 2))])
    ref_stored(p + '.ref', p)
    lin = linearize(stored[aT:aT + H, aL:aL + W], black, (2, 2), white)
    write_ref(p + '.lin.ref', lin.astype(np.float32))
    rawpy_render(p, p + '.rawpy.ref')
    mat, xy = camera_to_srgb_single(n65)
    cols = np.array([mat @ (c * n65) for c in PATCHES], np.float32)   # white balanced patch c is the camera value c * n
    write_ref(p + '.color.ref', cols.reshape(4, 6, 3))
    write_json(p + '.json', dict(width=RW, height=RH, active=[aT, aL, aT + H, aL + W], final=[W - 4, H - 4], cfa=pat, bits=16,
                                 white=white, black=black, patch=[pw, ph], crop=[2, 2], layout='cfa', planes=3))

    # 2. Big endian, 12-bit packed, GRBG, linearization table, BlackLevelDeltaH / V, two calibrations + forward matrices -
    W, H = 96, 96
    nd = np.array([0.55, 1.0, 0.72])
    scene, pw, ph = patch_scene(W, H, nd, 4, 6)
    pat = [1, 0, 2, 1]
    m = mosaic(scene, pat, 2, 2)
    # stored = sqrt-like encoding through a table of 4096 entries mapping to 0..16383
    table = np.round(np.linspace(0, 1, 4096) ** 2 * 16383).astype(np.uint16)
    dh = list(np.round(np.linspace(0, 6, W), 3))
    dv = list(np.round(np.linspace(3, 0, H), 3))
    black0, white = 64.0, 16383
    target = black0 + np.array(dh)[None, :] + np.array(dv)[:, None] + m * (white - (black0 + max(dh) + max(dv)))
    stored = np.searchsorted(table, np.clip(target, 0, 16383)).astype(np.uint16)
    stored = np.minimum(stored, 4095)
    tags = base_tags(W, H, 12, cfa=(2, 2, pat), black=[black0], white=white, colour='dual', neutral=list(nd),
                     lin=table, baseline_exposure=0.5,
                     extra=[Tag(50715, SRATIONAL, dh), Tag(50716, SRATIONAL, dv), Tag(278, LONG, 20)])
    p = os.path.join(OUT, 'bayer12_be_lin_dual.dng')
    packed, rowb = pack_bits(stored, 12)
    write_tiff(p, [(tags, strips(packed, 20, rowb))], big_endian=True)
    ref_stored(p + '.ref', p)
    write_ref(p + '.lin.ref', linearize(stored, [black0], (1, 1), white, lin=table, dh=dh, dv=dv).astype(np.float32))
    mat, xy, wgt = camera_to_srgb_dual(nd)
    # the patches are stored through a quantizing table: the model takes the linear values the file actually holds
    cols = np.array([mat @ (c * nd) for c in PATCHES[:24]], np.float32) * (2 ** 0.5)
    write_ref(p + '.color.ref', cols.reshape(6, 4, 3))
    write_json(p + '.json', dict(width=W, height=H, active=[0, 0, H, W], final=[W, H], cfa=pat, bits=12, white=[white],
                                 patch=[pw, ph], crop=[0, 0], layout='cfa', planes=3, white_xy=list(xy), weight=wgt,
                                 temperatures=[2856, 6504], baseline_exposure=0.5))

    # 3. Lossless JPEG tiles, 14-bit, BGGR, each 32 x 32 tile coded as a 64 x 16 frame (DNG's reshaping) --------------
    W, H, T = 96, 64, 32
    pat = [2, 1, 1, 0]
    data = (rng.random((H, W)) * 4000 + 600).astype(np.uint16)
    data[::7] += 3000
    chunks = []
    for ty in range(0, H, T):
        for tx in range(0, W, T):
            tile = data[ty:ty + T, tx:tx + T]
            chunks.append(imagecodecs.ljpeg_encode(tile.reshape(T // 2, T * 2), bitspersample=14))
    tags = base_tags(W, H, 14, compression=7, cfa=(2, 2, pat), black=[512], white=16383, neutral=[0.5, 1, 0.7]) + \
        [Tag(322, LONG, T), Tag(323, LONG, T)]
    p = os.path.join(OUT, 'ljpeg14_tiles.dng')
    write_tiff(p, [(tags, chunks)])
    a = ref_stored(p + '.ref', p)
    assert (a[:, :, 0] == data).all()
    write_json(p + '.json', dict(width=W, height=H, cfa=pat, bits=14, layout='cfa', planes=3))

    # 4. LinearRaw, 3 x 16-bit, Deflate with horizontal differencing X2 (predictor 34892), little endian ------------------
    W, H = 64, 40
    lr = (rng.random((H, W, 3)) * 30000 + 1000).astype(np.uint16)
    lr[:, ::5] = 40000
    raw = lr.astype(np.int64).reshape(H, W * 3)
    diff = raw.copy()
    diff[:, 6:] = raw[:, 6:] - raw[:, :-6]   # same sample of the pixel two to the left
    rows = (diff & 0xFFFF).astype('<u2').tobytes()
    tags = base_tags(W, H, 16, spp=3, photometric=34892, compression=8, white=[65535] * 3, neutral=list(n65),
                     extra=[Tag(317, SHORT, 34892), Tag(278, LONG, 16)])
    chunks = [zlib.compress(c) for c in strips(rows, 16, W * 6)]
    p = os.path.join(OUT, 'linearraw16_deflate_x2.dng')
    write_tiff(p, [(tags, chunks)])
    a = ref_stored(p + '.ref', p, lr)
    assert (a == lr).all()
    write_json(p + '.json', dict(width=W, height=H, layout='linear', planes=3, bits=16))

    # 5. CFA float16 / float32, Deflate with the floating point predictors X4 / X2 -------------------------------------
    for name, dt, pred, factor in (('cfa_f16_deflate_fpx4.dng', np.float16, 34895, 4), ('cfa_f32_deflate_fpx2.dng', np.float32, 34894, 2)):
        W, H = 64, 32
        v = (rng.random((H, W)) * 1.2).astype(dt)
        bps = np.dtype(dt).itemsize
        out = []
        for y in range(H):
            be = v[y].astype(np.dtype(dt).newbyteorder('>')).view(np.uint8).reshape(W, bps)
            planes = be.T.reshape(-1).astype(np.int64)                  # byte planes, most significant first
            d = planes.copy()
            d[factor:] = planes[factor:] - planes[:-factor]
            out.append((d & 0xFF).astype(np.uint8).tobytes())
        rows = b''.join(out)
        tags = base_tags(W, H, bps * 8, compression=8, sample_format=3, cfa=(2, 2, [0, 1, 1, 2]), white=1,
                         neutral=list(n65), extra=[Tag(317, SHORT, pred), Tag(278, LONG, 8)])
        p = os.path.join(OUT, name)
        write_tiff(p, [(tags, [zlib.compress(c) for c in strips(rows, 8, W * bps)])])
        a = ref_stored(p + '.ref', p, v)
        assert (a[:, :, 0].astype(np.float32) == v.astype(np.float32)).all(), name
        write_json(p + '.json', dict(width=W, height=H, layout='cfa', planes=3, bits=bps * 8, float=True))

    # 6. JPEG XL tiles (DNG 1.7): 12-bit CFA, lossless, bare codestreams ------------------------------------------------
    W, H, T = 96, 64, 48
    data = (rng.random((H, W)) * 3500 + 200).astype(np.uint16)
    padded = np.zeros((2 * T, 2 * T), np.uint16)
    padded[:H, :W] = data
    chunks = [imagecodecs.jpegxl_encode(np.ascontiguousarray(padded[ty:ty + T, tx:tx + T]), lossless=True, bitspersample=12)
              for ty in range(0, H, T) for tx in range(0, W, T)]
    tags = base_tags(W, H, 12, compression=52546, cfa=(2, 2, [0, 1, 1, 2]), black=[200], white=4095, neutral=list(n65)) + \
        [Tag(322, LONG, T), Tag(323, LONG, T)]
    p = os.path.join(OUT, 'jxl12_tiles.dng')
    write_tiff(p, [(tags, chunks)])
    a = ref_stored(p + '.ref', p)
    assert (a[:, :, 0] == data).all()
    write_json(p + '.json', dict(width=W, height=H, layout='cfa', planes=3, bits=12))

    # 7. Lossy JPEG (34892), LinearRaw 8-bit with a linearization table, in a SubIFD, with a JPEG preview in IFD 0 ------
    W, H = 80, 64
    scene, _, _ = patch_scene(W, H, n65)
    enc = np.round(np.sqrt(np.clip(scene, 0, 1)) * 255).astype(np.uint8)
    table = np.round((np.arange(256) / 255.0) ** 2 * 65535).astype(np.uint16)
    jp = imagecodecs.jpeg8_encode(enc, level=95)
    rtags = base_tags(W, H, 8, spp=3, photometric=34892, compression=34892, white=[65535] * 3, lin=table, neutral=list(n65),
                      extra=[Tag(278, LONG, H)])
    preview = imagecodecs.jpeg8_encode(np.full((24, 30, 3), 128, np.uint8), level=80)
    ptags = [Tag(254, LONG, 1), Tag(256, LONG, 30), Tag(257, LONG, 24), Tag(258, SHORT, [8, 8, 8]), Tag(259, SHORT, 7),
             Tag(262, SHORT, 6), Tag(271, ASCII, 'PixCodec'), Tag(272, ASCII, 'Synthetic'), Tag(274, SHORT, 1),
             Tag(277, SHORT, 3), Tag(278, LONG, 24), Tag(50706, BYTE, [1, 4, 0, 0]),
             Tag(50778, SHORT, 21), Tag(50721, SRATIONAL, CM_D65), Tag(50728, RATIONAL, list(n65))]
    rtags = [t for t in rtags if t.tag not in (50706, 50707, 50708, 271, 272, 274, 50778, 50721, 50728, 50730)]
    p = os.path.join(OUT, 'lossy_linearraw_subifd.dng')
    write_tiff(p, [(ptags, [preview]), (rtags, [jp])])
    with tifffile.TiffFile(p) as tf:
        a = tf.pages[0].pages[0].asarray()
    write_ref(p + '.ref', a)
    with open(p + '.preview.jpg', 'wb') as f:
        f.write(preview)
    write_json(p + '.json', dict(width=W, height=H, layout='linear', planes=3, bits=8, previews=[[30, 24]], jpeg_preview=len(preview)))

    # 8. Row / column interleave 2 x 2 (Bayer as four sub-images), uncompressed 16-bit ----------------------------------
    W, H = 48, 32
    data = (np.arange(W * H).reshape(H, W) * 37 % 60000).astype(np.uint16)
    rows_order = np.concatenate([np.arange(0, H, 2), np.arange(1, H, 2)])
    cols_order = np.concatenate([np.arange(0, W, 2), np.arange(1, W, 2)])
    stored_il = data[rows_order][:, cols_order]
    tags = base_tags(W, H, 16, cfa=(2, 2, [0, 1, 1, 2]), white=65535, neutral=list(n65),
                     extra=[Tag(50975, SHORT, 2), Tag(52547, SHORT, 2), Tag(278, LONG, H)])
    p = os.path.join(OUT, 'interleaved_2x2.dng')
    write_tiff(p, [(tags, [stored_il.astype('<u2').tobytes()])])
    write_ref(p + '.ref', data)   # tifffile returns the stored order; DNG puts the fields back as above
    write_json(p + '.json', dict(width=W, height=H, layout='cfa', planes=3, bits=16))

    # 9. Opcode lists: list 1 bad pixels, list 2 gain maps / per row and column ops / polynomial / table --------------
    W, H = 64, 48
    base = (rng.random((H, W)) * 30000 + 2000).astype(np.uint16)
    bad_const = 65535
    base[5, 7] = bad_const
    base[20, 30] = bad_const
    bad_points = [(11, 13), (40, 50)]
    bad_rect = (30, 2, 33, 6)
    l1 = opcode_list([
        opcode(4, struct.pack('>II', bad_const, 0)),
        opcode(5, struct.pack('>III', 0, len(bad_points), 1) + b''.join(struct.pack('>II', r, c) for r, c in bad_points)
               + struct.pack('>4I', *bad_rect)),
    ])
    gm_v, gm_h = 3, 4
    gains = (1 + 0.3 * rng.random((gm_v, gm_h, 1))).astype(np.float32)
    gm = area(0, 0, H, W, 0, 1, 2, 2) + struct.pack('>II', gm_v, gm_h) + struct.pack('>4d', 1 / (gm_v - 1), 1 / (gm_h - 1), 0, 0) \
        + struct.pack('>I', 1) + gains.astype('>f4').tobytes()
    rows_scale = (0.9 + 0.2 * rng.random(H // 2)).astype(np.float32)
    col_delta = (0.02 * rng.random(W)).astype(np.float32) - 0.01
    poly = [0.0, 1.1, -0.1]
    tab = np.round(np.sqrt(np.linspace(0, 1, 65536)) * 65535).astype(np.uint16)
    l2 = opcode_list([
        opcode(9, gm),
        opcode(12, area(1, 0, H, W, 0, 1, 2, 1) + struct.pack('>I', H // 2) + rows_scale.astype('>f4').tobytes()),
        opcode(11, area(0, 0, H, W, 0, 1, 1, 1) + struct.pack('>I', W) + col_delta.astype('>f4').tobytes()),
        opcode(8, area(0, 1, H, W, 0, 1, 1, 2) + struct.pack('>I', 2) + struct.pack('>3d', *poly)),
        opcode(7, area(8, 8, 16, 24, 0, 1, 1, 1) + struct.pack('>I', len(tab)) + tab.astype('>u2').tobytes()),
        opcode(1000, b'\x00\x01', optional=True),   # unknown, optional: skipped
    ])
    tags = base_tags(W, H, 16, cfa=(2, 2, [0, 1, 1, 2]), black=[1000], white=40000, neutral=list(n65),
                     extra=[Tag(51008, UNDEF, l1), Tag(51009, UNDEF, l2), Tag(278, LONG, H)])
    p = os.path.join(OUT, 'opcodes_list1_list2.dng')
    write_tiff(p, [(tags, [base.astype('<u2').tobytes()])])
    ref_stored(p + '.ref', p)
    # model: list 1 (bad pixels: mean of the nearest good pixels of the same Bayer colour), then chapter 5, then list 2
    v = base.astype(np.float64)
    badmask = np.zeros((H, W), bool)
    badmask[base == bad_const] = True
    for r, c in bad_points:
        badmask[r, c] = True
    badmask[bad_rect[0]:bad_rect[2], bad_rect[1]:bad_rect[3]] = True
    colour = np.zeros((H, W), int)
    colour[0::2, 0::2] = 0
    colour[0::2, 1::2] = 1
    colour[1::2, 0::2] = 1
    colour[1::2, 1::2] = 2
    src = v.copy()
    for y, x in zip(*np.nonzero(badmask)):
        col = colour[y, x]
        for rad in range(1 if col == 1 else 2, 17):
            acc = []
            for dy in range(-rad, rad + 1):
                for dx in range(-rad, rad + 1):
                    if max(abs(dx), abs(dy)) != rad:
                        continue
                    yy, xx = y + dy, x + dx
                    if 0 <= yy < H and 0 <= xx < W and colour[yy, xx] == col and not badmask[yy, xx]:
                        acc.append(src[yy, xx])
            if acc:
                v[y, x] = np.mean(acc)
                break
    v = np.clip(v, 0, 65535)
    lin = np.minimum((v - 1000) / (40000 - 1000), 1.0)
    img = np.clip(lin, 0, 1)[None].copy()

    def gainmap(sub, ys, xs, _):
        fy = np.clip(((ys + 0.5) / H - 0) / (1 / (gm_v - 1)), 0, gm_v - 1)
        fx = np.clip(((xs + 0.5) / W - 0) / (1 / (gm_h - 1)), 0, gm_h - 1)
        y0 = np.floor(fy).astype(int); x0 = np.floor(fx).astype(int)
        y1 = np.minimum(y0 + 1, gm_v - 1); x1 = np.minimum(x0 + 1, gm_h - 1)
        ty = (fy - y0)[:, None]; tx = (fx - x0)[None, :]
        g = gains[:, :, 0]
        top = g[y0][:, x0] + (g[y0][:, x1] - g[y0][:, x0]) * tx
        bot = g[y1][:, x0] + (g[y1][:, x1] - g[y1][:, x0]) * tx
        return sub * (top * (1 - ty) + bot * ty)
    apply_area(img, 0, 0, H, W, 0, 1, 2, 2, gainmap)
    img = np.clip(img, 0, 1)
    apply_area(img, 1, 0, H, W, 0, 1, 2, 1, lambda s, ys, xs, _: s * rows_scale[(ys - 1) // 2][:, None])
    img = np.clip(img, 0, 1)
    apply_area(img, 0, 0, H, W, 0, 1, 1, 1, lambda s, ys, xs, _: s + col_delta[xs][None, :])
    img = np.clip(img, 0, 1)
    apply_area(img, 0, 1, H, W, 0, 1, 1, 2, lambda s, ys, xs, _: poly[0] + poly[1] * s + poly[2] * s * s)
    img = np.clip(img, 0, 1)

    def table_map(s, ys, xs, _):
        x = np.clip(s * 65535, 0, len(tab) - 1)
        i0 = np.floor(x).astype(int); i1 = np.minimum(i0 + 1, len(tab) - 1)
        return (tab[i0] + (tab[i1].astype(float) - tab[i0]) * (x - i0)) / 65535
    apply_area(img, 8, 8, 16, 24, 0, 1, 1, 1, table_map)
    img = np.clip(img, 0, 1)
    write_ref(p + '.lin.ref', img[0].astype(np.float32))
    write_json(p + '.json', dict(width=W, height=H, layout='cfa', planes=3, bits=16, opcodes=[2, 6, 0]))

    # 10. List 3 on LinearRaw: vignette, warp (rectilinear), TrimBounds; orientation 6; also an unknown required opcode -
    W, H = 48, 32
    lr = np.zeros((H, W, 3))
    yy, xx = np.mgrid[0:H, 0:W]
    lr[..., 0] = 0.2 + 0.3 * (xx / W)
    lr[..., 1] = 0.25 + 0.2 * (yy / H)
    lr[..., 2] = 0.3 + 0.1 * np.sin(xx / 5.0) * np.cos(yy / 4.0)
    stored = np.round(lr * 65535).astype(np.uint16)
    vig = (0.15, 0.05, 0.0, 0.0, 0.0, 0.45, 0.55)
    warp = (1, 1.0, -0.04, 0.01, 0.0, 0.002, -0.001, 0.5, 0.5)
    l3 = opcode_list([
        opcode(3, struct.pack('>7d', *vig)),
        opcode(1, struct.pack('>I', 1) + struct.pack('>8d', *warp[1:])),
        opcode(6, struct.pack('>4I', 2, 3, H - 1, W - 2)),
    ])
    tags = base_tags(W, H, 16, spp=3, photometric=34892, white=[65535] * 3, neutral=[1, 1, 1], colour=None, orientation=6,
                     extra=[Tag(51022, UNDEF, l3), Tag(278, LONG, H)])
    p = os.path.join(OUT, 'linearraw_list3_orient6.dng')
    write_tiff(p, [(tags, [stored.astype('<u2').tobytes()])])
    ref_stored(p + '.ref', p)
    planes = (stored.astype(np.float64) / 65535).transpose(2, 0, 1)
    x1, y1 = W - 1, H - 1
    cx, cy = vig[5] * x1, vig[6] * y1
    mm = math.hypot(max(abs(cx), abs(x1 - cx)), max(abs(cy), abs(y1 - cy)))
    r2 = ((xx - cx) ** 2 + (yy - cy) ** 2) / mm ** 2
    g = 1 + vig[0] * r2 + vig[1] * r2 ** 2
    planes = np.clip(planes * g[None], 0, 1)
    k = warp[1:]
    cx, cy = k[6] * x1, k[7] * y1
    mm = math.hypot(max(abs(cx), abs(x1 - cx)), max(abs(cy), abs(y1 - cy)))
    dx, dy = (xx - cx) / mm, (yy - cy) / mm
    rr = dx * dx + dy * dy
    f = k[0] + k[1] * rr + k[2] * rr ** 2 + k[3] * rr ** 3
    sx = cx + mm * (f * dx + k[4] * 2 * dx * dy + k[5] * (rr + 2 * dx * dx))
    sy = cy + mm * (f * dy + k[5] * 2 * dx * dy + k[4] * (rr + 2 * dy * dy))

    def cubic(t):
        t = np.abs(t)
        return np.where(t < 1, (1.5 * t - 2.5) * t * t + 1, np.where(t < 2, ((-0.5 * t + 2.5) * t - 4) * t + 2, 0))
    warped = np.zeros_like(planes)
    ix, iy = np.floor(sx).astype(int), np.floor(sy).astype(int)
    fx, fy = sx - ix, sy - iy
    for pl in range(3):
        acc = np.zeros((H, W))
        for j in range(-1, 3):
            for i in range(-1, 3):
                acc += planes[pl][np.clip(iy + j, 0, H - 1), np.clip(ix + i, 0, W - 1)] * cubic(i - fx) * cubic(j - fy)
        warped[pl] = acc
    warped = np.clip(warped, 0, 1)[:, 2:H - 1, 3:W - 2]
    out = warped.transpose(1, 2, 0)
    out = np.rot90(out, -1)          # orientation 6: rotate 90 degrees clockwise to view
    write_ref(p + '.render.ref', np.ascontiguousarray(out).astype(np.float32))
    write_json(p + '.json', dict(width=W, height=H, layout='linear', planes=3, bits=16, orientation=6,
                                 final_upright=[H - 3, W - 5], opcodes=[0, 0, 3]))

    p2 = os.path.join(OUT, 'unknown_required_opcode.dng')
    l2 = opcode_list([opcode(4711, b'', optional=False, version=(1, 9, 0, 0))])
    tags = base_tags(16, 8, 16, cfa=(2, 2, [0, 1, 1, 2]), white=65535, neutral=list(n65), extra=[Tag(51009, UNDEF, l2), Tag(278, LONG, 8)])
    write_tiff(p2, [(tags, [np.zeros((8, 16), '<u2').tobytes()])])

    # 11. X-Trans 6 x 6 flat patches (non-Bayer demosaic), monochrome LinearRaw ------------------------------------------
    xt = [1, 1, 0, 1, 1, 2, 1, 1, 2, 1, 1, 0, 2, 0, 1, 0, 2, 1, 1, 1, 2, 1, 1, 0, 1, 1, 0, 1, 1, 2, 0, 2, 1, 2, 0, 1]
    W, H = 96, 72
    scene, pw, ph = patch_scene(W, H, n65)
    m = mosaic(scene, xt, 6, 6)
    stored = np.round(m * 65535).astype(np.uint16)
    tags = base_tags(W, H, 16, cfa=(6, 6, xt), white=65535, neutral=list(n65), extra=[Tag(278, LONG, H)])
    p = os.path.join(OUT, 'xtrans16.dng')
    write_tiff(p, [(tags, [stored.astype('<u2').tobytes()])])
    ref_stored(p + '.ref', p)
    mat, xy = camera_to_srgb_single(n65)
    write_ref(p + '.color.ref', np.array([mat @ (c * n65) for c in PATCHES], np.float32).reshape(4, 6, 3))
    write_json(p + '.json', dict(width=W, height=H, layout='cfa', planes=3, bits=16, patch=[pw, ph], crop=[0, 0], cfa=xt))

    W, H = 40, 30
    mono = (np.linspace(0, 1, W * H).reshape(H, W) ** 1.0 * 4000).astype(np.uint16)
    tags = base_tags(W, H, 12, photometric=34892, white=4000, black=[100], colour=None, baseline_exposure=-1.0,
                     extra=[Tag(278, LONG, H)])
    p = os.path.join(OUT, 'mono12.dng')
    packed, rowb = pack_bits(mono, 12)
    write_tiff(p, [(tags, [packed])])
    ref_stored(p + '.ref', p)
    lin = np.minimum((mono.astype(np.float64) - 100) / (4000 - 100), 1)
    out = lin * 0.5   # Float32 output keeps values below black
    write_ref(p + '.render.ref', out.astype(np.float32))
    write_json(p + '.json', dict(width=W, height=H, layout='linear', planes=1, bits=12, baseline_exposure=-1.0))

    # 12. Demosaic quality: a known full colour image (edges at several angles, fine stripes, smooth gradients), mosaiced
    W, H = 128, 96
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float64)
    # natural images have strongly correlated channels: detail in the luminance, slowly varying chroma
    lum = 0.45 + 0.3 * np.sin(np.hypot(xx - 40, yy - 50) ** 2 / 180.0) + 0.15 * ((xx + 2 * yy) % 24 < 12) \
        + 0.1 * ((xx * 0.7 - yy) % 20 < 10)
    chroma = np.stack([0.8 + 0.3 * xx / W, 0.9 - 0.2 * yy / H, 0.6 + 0.4 * (xx + yy) / (W + H)], -1)
    truth = np.clip(lum[..., None] * chroma, 0, 1)
    m = mosaic(truth, [0, 1, 1, 2], 2, 2)
    stored = np.round(m * 65535).astype(np.uint16)
    tags = base_tags(W, H, 16, cfa=(2, 2, [0, 1, 1, 2]), white=65535, neutral=[1, 1, 1], colour=None, extra=[Tag(278, LONG, H)])
    p = os.path.join(OUT, 'demosaic_truth.dng')
    write_tiff(p, [(tags, [stored.astype('<u2').tobytes()])])
    write_ref(p + '.truth.ref', (np.round(truth * 65535) / 65535).astype(np.float32))

    print('wrote', len([f for f in os.listdir(OUT) if f.endswith('.dng')]), 'DNG fixtures to', os.path.normpath(OUT))
    if UNVERIFIED:
        print('stored samples not verified by a third-party decoder (reference = source data):', ', '.join(UNVERIFIED))


if __name__ == '__main__':
    main()
