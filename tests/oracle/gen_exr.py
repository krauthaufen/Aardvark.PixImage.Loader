"""Generates the OpenEXR fixtures in tests/fixtures/exr.

Needs the official OpenEXR python bindings (pip install OpenEXR; written with 3.5.2), which both write the files and
decode them again for the .ref sidecars, so that lossy schemes (B44, PXR24 float) are compared against what the
reference decoder produces. A few files are written by ImageMagick (linked against OpenEXR 3.1) and multi-level tiled
files are read back with OpenCV, because the python bindings only write level 0 of them and refuse to read the result.

Sidecars:  name.exr.ref        what Exr.Decode returns with default options (float16 when all channels are HALF)
           name.exr.all.ref    all channels in file order via Exr.DecodeChannels (optional)
           name.exr.partN.ref  default decode of part N of a multi-part file
Files in unsupported/ have no sidecar; they must throw ImageNotSupportedException.
"""
import os
import struct
import subprocess
import sys
import numpy as np
import OpenEXR as X
from pxref import write_ref, test_pattern

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'fixtures', 'exr')
COMP = {'none': X.NO_COMPRESSION, 'rle': X.RLE_COMPRESSION, 'zips': X.ZIPS_COMPRESSION, 'zip': X.ZIP_COMPRESSION,
        'piz': X.PIZ_COMPRESSION, 'pxr24': X.PXR24_COMPRESSION, 'b44': X.B44_COMPRESSION, 'b44a': X.B44A_COMPRESSION}
CODE = {0: 'none', 1: 'rle', 2: 'zips', 3: 'zip', 4: 'piz', 5: 'pxr24', 6: 'b44', 7: 'b44a', 8: 'dwaa', 9: 'dwab'}


def header_info(path):
    """Minimal independent header parse: list of (compression name, tiled?) per part."""
    d = open(path, 'rb').read()
    assert d[:4] == b'\x76\x2f\x31\x01'
    flags = struct.unpack('<I', d[4:8])[0]
    multi = bool(flags & 0x1000)
    p, parts = 8, []
    while True:
        comp, tiled = None, bool(flags & 0x200)
        while d[p] != 0:
            e = d.index(b'\0', p); name = d[p:e]; p = e + 1
            e = d.index(b'\0', p); p = e + 1
            size = struct.unpack('<i', d[p:p + 4])[0]; p += 4
            if name == b'compression':
                comp = CODE[d[p]]
            if name == b'type':
                tiled = d[p:p + size] == b'tiledimage'
            p += size
        p += 1
        parts.append((comp, tiled))
        if not multi or d[p] == 0:
            return parts


def stack(chans, names):
    """Interleave channels by the library's sample type rule."""
    arrs = [chans[n] for n in names]
    kinds = {a.dtype for a in arrs}
    if kinds == {np.dtype('float16')}:
        dt = np.float16
    elif kinds == {np.dtype('uint32')}:
        dt = np.uint32
    else:
        dt = np.float32
    return np.stack([a.astype(dt) for a in arrs], axis=-1)


def expand(ch):
    """Full resolution view of a (possibly subsampled) channel: every sample replicated over its cell."""
    a = ch.pixels
    if ch.ySampling > 1:
        a = np.repeat(a, ch.ySampling, axis=0)
    if ch.xSampling > 1:
        a = np.repeat(a, ch.xSampling, axis=1)
    return a


def read_parts(path):
    f = X.File(path, separate_channels=True)
    return [{n: expand(c) for n, c in p.channels.items()} for p in f.parts]


def finish(name, default, all_ref=False, expect=None, parts=None):
    """Write the sidecars from what the reference library decodes. default: channel names of the default decode."""
    path = os.path.join(OUT, name + '.exr')
    info = header_info(path)
    if expect is not None:
        assert [c for c, _ in info] == expect, (name, info, expect)
    decoded = read_parts(path)
    write_ref(path + '.ref', stack(decoded[0], default))
    if all_ref:
        write_ref(path + '.all.ref', stack(decoded[0], sorted(decoded[0].keys(), key=lambda s: s.encode())))
    for i, names in (parts or {}).items():
        write_ref(path + '.part%d.ref' % i, stack(decoded[i], names))


def write(name, header, chans, default, **kw):
    X.File(header, chans).write(os.path.join(OUT, name + '.exr'))
    finish(name, default, **kw)


def tiles(xs, ys, mode=X.ONE_LEVEL, rounding=X.ROUND_DOWN):
    t = X.TileDescription()
    t.xSize, t.ySize, t.mode, t.roundingMode = xs, ys, mode, rounding
    return t


def window(x0, y0, w, h):
    return (np.array([x0, y0], 'int32'), np.array([x0 + w - 1, y0 + h - 1], 'int32'))


def rgb(w, h, dtype=np.float16, alpha=False, seed=1):
    p = test_pattern(w, h, 4 if alpha else 3, dtype, seed)
    d = {'R': p[..., 0].copy(), 'G': p[..., 1].copy(), 'B': p[..., 2].copy()}
    if alpha:
        d['A'] = np.clip(p[..., 3], 0, 1).astype(dtype)
    return d


def ids(w, h, seed=5):
    rng = np.random.default_rng(seed)
    a = rng.integers(0, 12, (h, w)).astype(np.uint32) * np.uint32(0x01010101) + np.uint32(0xfffffff0)
    a[::3, ::4] = rng.integers(0, 2**32, a[::3, ::4].shape, dtype=np.uint64).astype(np.uint32)
    return a


def mixed(w, h, seed=2):
    d = rgb(w, h, np.float16, True, seed)
    d['Z'] = (test_pattern(w, h, 1, np.float32, seed + 1)[..., 0] * 1000.25).astype(np.float32)
    d['id'] = ids(w, h, seed)
    dif = test_pattern(w, h, 3, np.float16, seed + 2)
    d['diffuse.R'], d['diffuse.G'], d['diffuse.B'] = dif[..., 0].copy(), dif[..., 1].copy(), dif[..., 2].copy()
    return d


def main():
    os.makedirs(os.path.join(OUT, 'unsupported'), exist_ok=True)
    for f in os.listdir(OUT):
        if f.endswith('.exr') or f.endswith('.ref'):
            os.remove(os.path.join(OUT, f))
    RGB, RGBA = ['R', 'G', 'B'], ['R', 'G', 'B', 'A']

    # --- compression matrix: half RGB over several chunks, mixed HALF/FLOAT/UINT with layers, 1x1
    for cn, c in COMP.items():
        write('rgb_half_' + cn, {'compression': c}, rgb(35, 37), RGB, expect=[cn])
        write('mixed_' + cn, {'compression': c, 'comment': 'mixed types'}, mixed(19, 34), RGBA, all_ref=True, expect=[cn])
        write('one_' + cn, {'compression': c}, rgb(1, 1, np.float16, True), RGBA, expect=[cn])
        write('uint_' + cn, {'compression': c}, {'id': ids(9, 33)}, ['id'], expect=[cn])
        write('float_rgb_' + cn, {'compression': c}, rgb(13, 18, np.float32), RGB, expect=[cn])

    # --- channel sets
    y = test_pattern(21, 17, 2, np.float16, 7)
    write('y_half_zip', {'compression': X.ZIP_COMPRESSION}, {'Y': y[..., 0].copy()}, ['Y'])
    write('ya_half_piz', {'compression': X.PIZ_COMPRESSION}, {'Y': y[..., 0].copy(), 'A': y[..., 1].copy()}, ['Y', 'A'])
    write('z_float_zips', {'compression': X.ZIPS_COMPRESSION}, {'Z': test_pattern(7, 5, 1, np.float32)[..., 0].copy()}, ['Z'])
    write('aov_only_zip', {'compression': X.ZIP_COMPRESSION},
          {'N.X': y[..., 0].copy(), 'N.Y': y[..., 1].copy(), 'N.Z': y[..., 0].copy(), 'depth': y[..., 1].astype(np.float32)},
          ['N.X', 'N.Y', 'N.Z', 'depth'])
    lay = rgb(11, 9, np.float16, False, 9)
    write('layer_only_piz', {'compression': X.PIZ_COMPRESSION},
          {'spec.R': lay['R'], 'spec.G': lay['G'], 'spec.B': lay['B'], 'Z': lay['R'].astype(np.float32)},
          ['spec.R', 'spec.G', 'spec.B'], all_ref=True)
    write('long_names_zip', {'compression': X.ZIP_COMPRESSION},
          {'a_rather_long_channel_name_that_needs_the_long_names_flag.R': lay['R'], 'Y': lay['G']}, ['Y'], all_ref=True)

    # --- special values
    sp = np.array([0, 0x8000, 1, 0x3ff, 0x400, 0x7bff, 0x7c00, 0xfc00, 0x7e00, 0x7c01, 0xfe01, 0x3c00, 0xbc00, 0x8001, 0x83ff, 0x0200],
                  np.uint16).view(np.float16)
    grid = np.tile(sp, 6)[:90].reshape(9, 10).copy()
    for cn in ('none', 'zip', 'piz', 'pxr24', 'rle'):
        write('special_half_' + cn, {'compression': COMP[cn]}, {'Y': grid}, ['Y'])
    fsp = np.array([0, 0x80000000, 1, 0x007fffff, 0x00800000, 0x7f7fffff, 0x7f800000, 0xff800000, 0x7fc00000, 0x7f800001,
                    0x3f800000, 0x3f8000ff, 0x3f800080, 0x3f800180, 0x7f7fff80, 0x12345678], np.uint32).view(np.float32)
    fgrid = np.tile(fsp, 6)[:90].reshape(9, 10).copy()
    for cn in ('zip', 'piz', 'pxr24'):
        write('special_float_' + cn, {'compression': COMP[cn]}, {'Z': fgrid}, ['Z'])

    # --- PIZ with more than 2^14 distinct values in a chunk (16 bit wavelet mode), flat and tiny data
    rng = np.random.default_rng(11)
    noise = rng.integers(0, 0x7c00, (33, 150, 4)).astype(np.uint16).view(np.float16)
    write('piz_wide_range', {'compression': X.PIZ_COMPRESSION},
          {'R': noise[..., 0].copy(), 'G': noise[..., 1].copy(), 'B': noise[..., 2].copy(), 'A': noise[..., 3].copy()}, RGBA)
    flat = np.full((40, 33), 0.5, np.float16)
    for cn in ('piz', 'zip', 'rle', 'b44', 'b44a', 'pxr24'):
        write('flat_' + cn, {'compression': COMP[cn]}, {'R': flat, 'G': flat * 2, 'B': np.zeros_like(flat)}, RGB)
    write('piz_uint_float', {'compression': X.PIZ_COMPRESSION},
          {'id': ids(70, 40, 3), 'Z': (test_pattern(70, 40, 1, np.float32, 4)[..., 0] * 77).astype(np.float32)}, ['Z', 'id'], all_ref=True)

    # --- geometry: thin images, data / display windows, line order
    for cn in ('zip', 'piz', 'b44', 'pxr24', 'rle'):
        write('row_' + cn, {'compression': COMP[cn]}, rgb(41, 1), RGB)
        write('col_' + cn, {'compression': COMP[cn]}, rgb(1, 41), RGB)
        write('odd_' + cn, {'compression': COMP[cn]}, rgb(5, 3, np.float16, True), RGBA)
    for cn in ('zip', 'piz', 'b44a'):
        write('window_' + cn, {'compression': COMP[cn], 'dataWindow': window(-7, -40, 23, 35), 'displayWindow': window(0, 0, 64, 48),
                               'pixelAspectRatio': 2.0}, rgb(23, 35, np.float16, True), RGBA)
        write('decreasing_' + cn, {'compression': COMP[cn], 'lineOrder': X.DECREASING_Y}, rgb(17, 70), RGB)
    b44p = rgb(30, 21, np.float16, False, 3)
    write('b44_plinear', {'compression': X.B44_COMPRESSION},
          {k: X.Channel(k, np.abs(v), 1, 1, True) for k, v in b44p.items()}, RGB)
    write('b44_mixed_tiny', {'compression': X.B44_COMPRESSION}, mixed(3, 2), RGBA, all_ref=True)

    # --- tiled
    for cn in ('none', 'zip', 'piz', 'b44', 'pxr24', 'rle'):
        write('tiled_' + cn, {'compression': COMP[cn], 'type': X.tiledimage, 'tiles': tiles(16, 8)}, rgb(37, 21, np.float16, True), RGBA, expect=[cn])
    write('tiled_big_tile', {'compression': X.PIZ_COMPRESSION, 'type': X.tiledimage, 'tiles': tiles(64, 64)}, rgb(17, 13), RGB)
    write('tiled_mixed', {'compression': X.ZIP_COMPRESSION, 'type': X.tiledimage, 'tiles': tiles(5, 7)}, mixed(19, 23), RGBA, all_ref=True)
    write('tiled_window_decreasing', {'compression': X.ZIP_COMPRESSION, 'type': X.tiledimage, 'tiles': tiles(8, 8),
                                      'dataWindow': window(5, -9, 20, 19), 'displayWindow': window(0, 0, 30, 30),
                                      'lineOrder': X.DECREASING_Y}, rgb(20, 19), RGB)
    write('tiled_random_y', {'compression': X.PIZ_COMPRESSION, 'type': X.tiledimage, 'tiles': tiles(8, 8), 'lineOrder': X.RANDOM_Y},
          rgb(20, 19), RGB)
    # multi-level files: the bindings write level 0 only; references come from OpenCV's OpenEXR
    os.environ['OPENCV_IO_ENABLE_OPENEXR'] = '1'
    import cv2
    for name, mode, rnd in (('tiled_mipmap_down', X.MIPMAP_LEVELS, X.ROUND_DOWN), ('tiled_mipmap_up', X.MIPMAP_LEVELS, X.ROUND_UP),
                            ('tiled_ripmap_down', X.RIPMAP_LEVELS, X.ROUND_DOWN), ('tiled_ripmap_up', X.RIPMAP_LEVELS, X.ROUND_UP)):
        path = os.path.join(OUT, name + '.exr')
        src = rgb(37, 21)
        exp = np.stack([src['R'], src['G'], src['B']], -1)
        X.File({'compression': X.ZIP_COMPRESSION, 'type': X.tiledimage, 'tiles': tiles(8, 8, mode, rnd)}, src).write(path)
        im = cv2.imread(path, cv2.IMREAD_UNCHANGED)
        assert im is not None and im.shape == (21, 37, 3)
        ref = im[..., ::-1].astype(np.float16)
        assert np.array_equal(ref, exp)
        write_ref(path + '.ref', ref)

    # --- multi-part
    disp = window(0, 0, 20, 50)
    p0 = X.Part({'compression': X.ZIP_COMPRESSION, 'dataWindow': window(0, 0, 17, 13), 'displayWindow': disp}, rgb(17, 13, np.float16, True), 'beauty')
    p1 = X.Part({'compression': X.PIZ_COMPRESSION, 'type': X.tiledimage, 'tiles': tiles(8, 8),
                 'dataWindow': window(2, 1, 9, 20), 'displayWindow': disp},
                {'Z': test_pattern(9, 20, 1, np.float32, 3)[..., 0].copy()}, 'depth')
    p2 = X.Part({'compression': X.B44_COMPRESSION, 'dataWindow': window(3, 4, 12, 40), 'displayWindow': disp},
                {**rgb(12, 40, np.float16, False, 4), 'id': ids(12, 40)}, 'third')
    X.File([p0, p1, p2]).write(os.path.join(OUT, 'multipart.exr'))
    finish('multipart', RGBA, expect=['zip', 'piz', 'b44'], parts={1: ['Z'], 2: RGB})

    # --- subsampled channels (luminance/chroma style and odd factors)
    yc = test_pattern(16, 36, 4, np.float16, 6)
    for cn in ('none', 'rle', 'zip', 'piz', 'pxr24', 'b44'):
        X.File({'compression': COMP[cn]},
               {'Y': yc[..., 0].copy(), 'RY': X.Channel('RY', yc[..., 1].copy(), 2, 2), 'BY': X.Channel('BY', yc[..., 2].copy(), 2, 2),
                'A': yc[..., 3].copy()}).write(os.path.join(OUT, 'unsupported', 'yc_%s.exr' % cn))
        path = os.path.join(OUT, 'unsupported', 'yc_%s.exr' % cn)
        d = read_parts(path)[0]
        assert d['RY'].shape == (36, 16)
        write_ref(path + '.all.ref', stack(d, ['A', 'BY', 'RY', 'Y']))
    sub = test_pattern(24, 36, 3, np.float32, 8)
    X.File({'compression': X.ZIP_COMPRESSION, 'dataWindow': window(-12, 36, 24, 36), 'displayWindow': window(0, 0, 24, 36)},
           {'a': X.Channel('a', sub[..., 0].copy(), 3, 1), 'b': X.Channel('b', sub[..., 1].copy(), 1, 4),
            'c': X.Channel('c', sub[..., 2].astype(np.float16), 4, 3), 'u': X.Channel('u', ids(24, 36), 2, 2)}
           ).write(os.path.join(OUT, 'subsampled_zip.exr'))
    d = read_parts(os.path.join(OUT, 'subsampled_zip.exr'))[0]
    write_ref(os.path.join(OUT, 'subsampled_zip.exr.ref'), stack(d, ['a', 'b', 'c', 'u']))

    # --- files written by ImageMagick (a second writer on top of OpenEXR 3.1)
    src = os.path.join(OUT, '_src.png')
    from PIL import Image
    Image.fromarray(test_pattern(31, 22, 4, np.uint8, 12), 'RGBA').save(src)
    for cn, flag in (('zip', 'Zip'), ('piz', 'Piz'), ('b44', 'B44'), ('pxr24', 'Pxr24'), ('rle', 'RLE')):
        path = os.path.join(OUT, 'magick_%s.exr' % cn)
        subprocess.run(['magick', src, '-define', 'exr:color-type=RGBA', '-compress', flag, path], check=True)
        finish('magick_' + cn, RGBA, expect=[cn])
    os.remove(src)

    # --- unsupported
    for cn, c in (('dwaa', X.DWAA_COMPRESSION), ('dwab', X.DWAB_COMPRESSION)):
        X.File({'compression': c}, rgb(20, 20)).write(os.path.join(OUT, 'unsupported', cn + '.exr'))

    total = sum(os.path.getsize(os.path.join(dp, f)) for dp, _, fs in os.walk(OUT) for f in fs)
    print('fixtures: %d files, %.1f kB' % (sum(len(fs) for _, _, fs in os.walk(OUT)), total / 1024))


if __name__ == '__main__':
    sys.exit(main())
