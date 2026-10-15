"""Generates the OpenEXR fixtures in tests/fixtures/exr.

Besides the python bindings this needs the helper exr_oracle (exr_oracle.cpp next to this script, built against the
system libOpenEXR; it is compiled on first use, set EXR_ORACLE to use an existing binary) for everything the
bindings cannot write or read: luminance/chroma through the RGBA interface, all levels of mip/rip maps, deep data.


Needs the official OpenEXR python bindings (pip install OpenEXR; written with 3.5.2), which both write the files and
decode them again for the .ref sidecars, so that lossy schemes (B44, PXR24 float) are compared against what the
reference decoder produces. A few files are written by ImageMagick (linked against OpenEXR 3.1) and multi-level tiled
files are read back with OpenCV, because the python bindings only write level 0 of them and refuse to read the result.

Directories: . (flat images, bit exact), dwa/ (DWAA/DWAB, bit exact), yc/ (luminance/chroma; .ref is the RGBA
interface output as float16 RGBA), levels/ (mip/rip maps; name.exr.l<lx>_<ly>.ref per level, all channels),
deep/ (name.exr.partN.deep raw samples, .fileorder.ref the reference library's flattening, .flat.ref depth sorted
"tidy" flattening computed here), encref/ (an RGBA image and the luminance/chroma file the reference encoder makes of it).
A few files of the openexr-images collection, with SHA-256 of reference decodes, are in the test-data repository
(fixtures/exr/official of Aardvark.PixImage.PixCodec.TestData, see pxref.testdata_dir).

Sidecars:  name.exr.ref        what Exr.Decode returns with default options (float16 when all channels are HALF)
           name.exr.all.ref    all channels in file order via Exr.DecodeChannels (optional)
           name.exr.partN.ref  default decode of part N of a multi-part file

"""
import os
import struct
import subprocess
import sys
import numpy as np
import OpenEXR as X
from pxref import write_ref, test_pattern, testdata_dir

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'fixtures', 'exr')
COMP = {'none': X.NO_COMPRESSION, 'rle': X.RLE_COMPRESSION, 'zips': X.ZIPS_COMPRESSION, 'zip': X.ZIP_COMPRESSION,
        'piz': X.PIZ_COMPRESSION, 'pxr24': X.PXR24_COMPRESSION, 'b44': X.B44_COMPRESSION, 'b44a': X.B44A_COMPRESSION,
        'zstd': X.ZSTD_COMPRESSION}
CODE = {0: 'none', 1: 'rle', 2: 'zips', 3: 'zip', 4: 'piz', 5: 'pxr24', 6: 'b44', 7: 'b44a', 8: 'dwaa', 9: 'dwab',
        10: 'htj2k256', 11: 'htj2k32', 12: 'lj2k', 13: 'zstd'}


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


ORACLE = os.environ.get('EXR_ORACLE') or os.path.join(os.path.dirname(os.path.abspath(__file__)), 'exr_oracle')


def oracle(*args):
    if not os.path.exists(ORACLE):
        here = os.path.dirname(os.path.abspath(__file__))
        dev = os.path.join(here, '..', '..', '.tmp', 'dev', 'root', 'usr', 'include')      # headers unpacked from the -dev packages
        inc = [i for d in ('/usr/include', dev) for i in ('-I' + os.path.join(d, 'OpenEXR'), '-I' + os.path.join(d, 'Imath'))]
        base = ['g++', '-O1', '-std=c++17'] + inc + [os.path.join(here, 'exr_oracle.cpp'), '-o', ORACLE]
        lib = '/usr/lib/x86_64-linux-gnu/'
        if subprocess.run(base + ['-lOpenEXR', '-lImath', '-lIex', '-lIlmThread'], capture_output=True).returncode != 0:
            subprocess.run(base + [lib + 'libOpenEXR-3_1.so.30', lib + 'libImath-3_1.so.29', lib + 'libIex-3_1.so.30',
                                   lib + 'libIlmThread-3_1.so.30'], check=True)
    subprocess.run([ORACLE] + [str(a) for a in args], check=True)


def clean(sub):
    d = os.path.join(OUT, sub)
    os.makedirs(d, exist_ok=True)
    for f in os.listdir(d):
        if not f.startswith('LICENSE'):
            os.remove(os.path.join(d, f))
    return d


def gen_yc(yc_source):
    """Luminance/chroma files written by the reference RGBA interface, and what it reads back from them."""
    d = clean('yc')
    for name, args in (('yc_piz', (30, 22, 'yc', 'piz', 0, 0, 1)), ('yca_zip', (40, 30, 'yca', 'zip', 0, 0, 2)),
                       ('yc_none_2x2', (2, 2, 'yc', 'none', 0, 0, 3)), ('yc_b44_window', (64, 50, 'yc', 'b44', -8, -6, 4)),
                       ('yca_piz_chromaticities', (30, 22, 'yca', 'piz', 4, 2, 5, 'chroma')), ('yca_pxr24', (64, 36, 'yca', 'pxr24', 0, 0, 6)),
                       ('yc_rle', (18, 34, 'yc', 'rle', 0, 0, 7)), ('yca_dwaa', (72, 40, 'yca', 'dwaa', 0, 0, 8)),
                       ('yc_dwab', (40, 64, 'yc', 'dwab', 2, 4, 9)), ('y_zip', (22, 14, 'y', 'zip', 0, 0, 10)),
                       ('ya_piz', (22, 14, 'ya', 'piz', 0, 0, 11))):
        path = os.path.join(d, name + '.exr')
        oracle('write-yc', path, *args)
        oracle('rgba', path, path + '.ref')
    # written with the python bindings: arbitrary (not filtered) chroma, every compression
    for cn in ('none', 'rle', 'zip', 'piz', 'pxr24', 'b44'):
        path = os.path.join(d, 'py_yc_%s.exr' % cn)
        X.File({'compression': COMP[cn]},
               {'Y': yc_source[..., 0].copy(), 'RY': X.Channel('RY', yc_source[..., 1].copy(), 2, 2),
                'BY': X.Channel('BY', yc_source[..., 2].copy(), 2, 2), 'A': yc_source[..., 3].copy()}).write(path)
        ch = read_parts(path)[0]
        write_ref(path + '.all.ref', stack(ch, ['A', 'BY', 'RY', 'Y']))
        oracle('rgba', path, path + '.ref')
    # for the encoder: RGB(A) images and what the reference makes of them in luminance/chroma form (the reference
    # writer treats images lower than 13 lines differently, hence the small sizes)
    e = clean('encref')
    for name, w, h, alpha in (('rgba_src', 40, 30, True), ('rgb_noisy', 96, 70, False), ('rgb_8x6', 8, 6, False), ('rgba_30x10', 30, 10, True),
                              ('rgb_6x12', 6, 12, False), ('rgb_4x14', 4, 14, False), ('rgb_2x2', 2, 2, False), ('rgba_14x2', 14, 2, True),
                              ('rgb_2x40', 2, 40, False), ('rgb_26x28', 26, 28, False)):
        src = os.path.join(e, name + '.exr')
        p = np.abs(test_pattern(w, h, 4, np.float32, 21 + w))
        if name == 'rgba_src':
            yy, xx = np.mgrid[0:h, 0:w]
            p[..., 0] = p[..., 0] * 0.3 + xx / float(w)
            p[..., 1] = p[..., 1] * 0.2 + yy / float(h)
            p[10:14, 5:30, :3] = 0.5
        X.File({'compression': X.ZIP_COMPRESSION}, {n: p[..., i].astype(np.float16) for i, n in enumerate('RGBA' if alpha else 'RGB')}).write(src)
        oracle('rgba-to-yc', src, os.path.join(e, name + '.yc.exr'), 'zip')


def level_list(w, h, mode, rounding):
    def count(size):
        n = 0
        while (1 << (n + 1)) <= size:
            n += 1
        if rounding == 'up' and (1 << n) < size:
            n += 1
        return n + 1
    if mode == 'mipmap':
        return [(l, l) for l in range(count(max(w, h)))]
    return [(lx, ly) for ly in range(count(h)) for lx in range(count(w))]


def gen_levels():
    """True multi-level files: every level written and read back by the reference library."""
    d = clean('levels')
    for name, args in (('mip_down_zip', ('mipmap', 'down', 37, 21, 8, 8, 'zip', 0, 0)), ('mip_up_piz', ('mipmap', 'up', 33, 18, 16, 16, 'piz', -5, 3)),
                       ('rip_down_zip', ('ripmap', 'down', 20, 13, 16, 8, 'zip', 0, 0)), ('rip_up_pxr24', ('ripmap', 'up', 19, 9, 8, 8, 'pxr24', 2, -4)),
                       ('mip_down_b44', ('mipmap', 'down', 40, 40, 64, 64, 'b44', 0, 0)), ('rip_down_rle', ('ripmap', 'down', 9, 6, 4, 4, 'rle', 0, 0))):
        path = os.path.join(d, name + '.exr')
        oracle('write-levels', path, *args)
        for lx, ly in level_list(args[2], args[3], args[0], args[1]):
            oracle('level', path, 0, lx, ly, '%s.l%d_%d.ref' % (path, lx, ly))


def read_deep(path):
    b = open(path, 'rb').read()
    assert b[:4] == b'PXDP'
    w, h, nc, total = [int(v) for v in np.frombuffer(b, np.int32, 4, 4)]
    counts = np.frombuffer(b, np.uint32, w * h, 20).astype(np.int64)
    p = 20 + 4 * w * h
    ch = {}
    for _ in range(nc):
        e = b.index(b'\0', p)
        name = b[p:e].decode()
        t = b[e + 1]
        ch[name] = np.frombuffer(b, np.float32 if t == 2 else np.uint32, total, e + 2)
        p = e + 2 + 4 * total
    return w, h, counts, ch


def flatten(path, names, sort, tidy):
    """Deep compositing as documented for ExrDeepImage.Flatten, written independently of the C# code."""
    w, h, counts, ch = read_deep(path)
    off = np.concatenate([[0], np.cumsum(counts)])
    out = np.zeros((h * w, len(names)), np.float32)
    f32 = np.float32
    for i in range(w * h):
        idx = list(range(off[i], off[i + 1]))
        if not idx:
            continue
        if sort and 'Z' in ch:
            idx.sort(key=lambda s: (ch['Z'][s], ch['ZBack'][s] if 'ZBack' in ch else 0, s))
        alpha = lambda s: ch['A'][s] if 'A' in ch else f32(1)
        samples = [(float(ch['Z'][s]) if 'Z' in ch else 0.0, float(ch['ZBack'][s]) if 'ZBack' in ch else None, alpha(s),
                    [f32(ch[n][s]) for n in names]) for s in idx]
        samples = [(z, z if zb is None else max(zb, z), a, v) for z, zb, a, v in samples]
        overlap = sort and tidy and 'Z' in ch and any(
            (samples[k][1] > samples[k][0] and samples[k + 1][0] < samples[k][1]) or samples[k][:2] == samples[k + 1][:2]
            for k in range(len(samples) - 1))
        if overlap:
            samples = tidy_samples(samples)
        a = f32(0)
        acc = np.zeros(len(names), np.float32)
        for z, zb, sa, vals in samples:
            if a >= 1:
                break
            t = f32(1) - a
            for k in range(len(names)):
                acc[k] += t * f32(vals[k])
            a = a + t * f32(sa)
        for k, n in enumerate(names):
            if n == 'A':
                acc[k] = a
        out[i] = acc
    return out.reshape(h, w, len(names))


def tidy_samples(samples):
    """Split volume samples at all sample boundaries inside them, then merge coinciding samples (points too)."""
    cuts = sorted({z for z, zb, a, v in samples} | {zb for z, zb, a, v in samples if zb > z})
    parts = []
    for z, zb, a, v in samples:
        a = float(a)
        v = [float(x) for x in v]
        if not zb > z:
            parts.append([z, z, a, v])
            continue
        start, length = z, zb - z
        for c in cuts + [zb]:
            if c <= start:
                continue
            end = min(c, zb)
            frac = (end - start) / length
            if a >= 1:
                na, scale = 1.0, 1.0
            elif a <= 0:
                na, scale = 0.0, frac
            else:
                na = 1 - (1 - a) ** frac
                scale = na / a
            parts.append([start, end, float(np.float32(na)), [float(np.float32(x * scale)) for x in v]])
            start = end
            if end >= zb:
                break
    parts.sort(key=lambda p: (p[0], p[1]))
    merged = []
    for p in parts:
        q = merged[-1] if merged else None
        if q is not None and q[0] == p[0] and q[1] == p[1]:
            a1, a2 = min(max(q[2], 0.0), 1.0), min(max(p[2], 0.0), 1.0)
            am = a1 + a2 - a1 * a2
            vals = []
            for c1, c2 in zip(q[3], p[3]):
                if a1 >= 1 and a2 >= 1:
                    c = (c1 + c2) / 2
                elif a1 >= 1:
                    c = c1
                elif a2 >= 1:
                    c = c2
                else:
                    u1, u2 = -np.log(1 - a1), -np.log(1 - a2)
                    v1 = u1 / a1 if a1 > 0 else 1
                    v2 = u2 / a2 if a2 > 0 else 1
                    wgt = am / (u1 + u2) if am > 0 else 1
                    c = (c1 * v1 + c2 * v2) * wgt
                vals.append(float(np.float32(c)))
            merged[-1] = [q[0], q[1], float(np.float32(am)), vals]
        else:
            merged.append(p)
    return [(z, zb, np.float32(a), v) for z, zb, a, v in merged]


def gen_deep():
    """Deep scan line / deep tiled files written by the reference library, its samples and its flattening."""
    d = clean('deep')
    for name, args in (('scan_plain_zips', ('scan', 13, 9, 'zips', 'plain', 1)), ('scan_sorted_rle', ('scan', 17, 6, 'rle', 'sorted', 2)),
                       ('scan_zback_none', ('scan', 9, 11, 'none', 'zback', 3)), ('scan_overlap_zips', ('scan', 12, 10, 'zips', 'overlap', 4)),
                       ('tile_plain_zips', ('tile', 21, 13, 'zips', 'plain', 5)), ('tile_overlap_rle', ('tile', 13, 9, 'rle', 'overlap', 6)),
                       ('tile_sorted_none', ('tile', 10, 12, 'none', 'sorted', 7)), ('multi_zback_zips', ('multi', 13, 9, 'zips', 'zback', 8)),
                       ('scan_noalpha_zips', ('scan', 8, 5, 'zips', 'noalpha', 9)), ('scan_1x1_rle', ('scan', 1, 1, 'rle', 'plain', 10))):
        path = os.path.join(d, name + '.exr')
        oracle('write-deep', path, *args)
        parts = X.File(path, header_only=True).parts
        for i, part in enumerate(parts):
            kind = str(part.header.get('type'))
            if 'deep' not in kind:
                write_ref('%s.part%d.ref' % (path, i), stack(read_parts(path)[i], ['R']))
                continue
            dump = '%s.part%d.deep' % (path, i)
            oracle('deep', path, i, 0, 0, dump)
            names = list(read_deep(dump)[3].keys())
            file_order = flatten(dump, names, False, False)
            if 'deepscanline' in kind and 'A' in names:
                # the reference compositor (deep scan line parts with alpha only) agrees with the file order result
                tmp = dump + '.tmp'
                oracle('flatten', path, i, tmp, *names)
                ref = np.fromfile(tmp, np.float32, offset=20).reshape(file_order.shape)
                os.remove(tmp)
                assert np.array_equal(ref, file_order), name
            write_ref('%s.part%d.fileorder.ref' % (path, i), file_order)
            flat = flatten(dump, names, True, True)
            write_ref('%s.part%d.flat.ref' % (path, i), flat)
            if len(parts) == 1 and 'A' in names:
                check_with_oiio(path, names, file_order, flat)
    # ZSTD deep data (OpenEXR 3.5): written and read back with the python bindings, which handle deep images since 3.5
    for name, kind, w, h, seed in (('py_scan_zstd', 'scan', 23, 9, 31), ('py_tile_zstd', 'tile', 21, 13, 32), ('py_scan_zstd_tiny', 'scan', 3, 2, 33)):
        path = os.path.join(d, name + '.exr')
        rng = np.random.default_rng(seed)
        counts = np.where(rng.random((h, w)) < 0.2, 0, rng.integers(1, 6, (h, w)))
        if 'tiny' in name:
            counts[:] = 1
        chans = {n: np.empty((h, w), dtype=object) for n in ('A', 'Z', 'R', 'G', 'B', 'id')}
        for i in range(h):
            for j in range(w):
                n = int(counts[i, j])
                a = np.where(rng.random(n) < 0.15, 1.0, rng.random(n)).astype(np.float16)
                chans['A'][i, j] = a
                chans['Z'][i, j] = (1 + np.cumsum(rng.random(n)) * (1 if rng.random() < 0.7 else -1)).astype(np.float32)
                for c in 'RGB':
                    chans[c][i, j] = (rng.random(n) * a.astype(np.float32)).astype(np.float16)
                chans['id'][i, j] = rng.integers(0, 2**32, n, dtype=np.uint64).astype(np.uint32)
        hdr = {'compression': X.ZSTD_COMPRESSION, 'type': X.deepscanline if kind == 'scan' else X.deeptile}
        if kind == 'tile':
            hdr['tiles'] = tiles(8, 5)
        X.File(hdr, chans).write(path)
        got = X.File(path, separate_channels=True).parts[0].channels
        names = sorted(got.keys(), key=lambda n: n.encode())
        px = {n: [[got[n].pixels[i, j] if got[n].pixels[i, j] is not None else np.zeros(0, np.float32) for j in range(w)] for i in range(h)]
              for n in names}       # pixels without samples come back as None
        dump = path + '.part0.deep'
        with open(dump, 'wb') as f:
            cnt = np.array([[len(px['Z'][i][j]) for j in range(w)] for i in range(h)], np.uint32)
            assert np.array_equal(cnt, counts)
            f.write(b'PXDP' + struct.pack('<4i', w, h, len(names), int(cnt.sum())) + cnt.tobytes())
            for n in names:
                is_uint = n == 'id'
                flat = np.concatenate([px[n][i][j].astype(np.uint32 if is_uint else np.float32) for i in range(h) for j in range(w)])
                f.write(n.encode() + b'\0' + bytes([0 if is_uint else 2]) + flat.astype(np.uint32 if is_uint else np.float32).tobytes())
        write_ref(path + '.part0.fileorder.ref', flatten(dump, names, False, False))
        write_ref(path + '.part0.flat.ref', flatten(dump, names, True, True))
    # deep tiled parts with all mip / rip levels: the samples of every level
    for name, args in (('tilemip_plain_zips', ('tilemip', 21, 13, 'zips', 'plain', 11)), ('tilerip_zback_rle', ('tilerip', 11, 6, 'rle', 'zback', 12)),
                       ('tilemip_overlap_none', ('tilemip', 9, 16, 'none', 'overlap', 13))):
        path = os.path.join(d, name + '.exr')
        oracle('write-deep', path, *args)
        for lx, ly in level_list(args[1], args[2], 'mipmap' if args[0] == 'tilemip' else 'ripmap', 'up' if args[0] == 'tilerip' else 'down'):
            oracle('deep', path, 0, lx, ly, '%s.part0.l%d_%d.deep' % (path, lx, ly))
        dump = '%s.part0.l0_0.deep' % path
        names = list(read_deep(dump)[3].keys())
        write_ref('%s.part0.fileorder.ref' % path, flatten(dump, names, False, False))
        write_ref('%s.part0.flat.ref' % path, flatten(dump, names, True, True))
        os.replace(dump, '%s.part0.deep' % path)


def check_with_oiio(path, names, file_order, flat):
    """Third party confirmation of the flattening (optional, pip install OpenImageIO): ImageBufAlgo.flatten composites
    in file order like the reference; DeepData.sort / split / merge_overlaps followed by "over" is the tidy flattening."""
    try:
        import OpenImageIO as oiio
    except ImportError:
        return
    pick = ['R', 'G', 'B', 'A']
    buf = oiio.ImageBuf(path)
    onames = list(buf.spec().channelnames)
    oc = [onames.index(n) for n in pick]
    rc = [names.index(n) for n in pick]
    a = oiio.ImageBufAlgo.flatten(buf).get_pixels(oiio.FLOAT)[..., oc]
    assert np.abs(a - file_order[..., rc]).max() < 1e-6, path
    dd = buf.deepdata()
    zc, zbc, ac = dd.Z_channel, dd.Zback_channel, dd.A_channel
    out = np.zeros((dd.pixels, 4), np.float32)
    for p in range(dd.pixels):
        if dd.samples(p) == 0:
            continue
        dd.sort(p)
        depths = set()
        for s in range(dd.samples(p)):
            depths.add(dd.deep_value(p, zc, s))
            depths.add(dd.deep_value(p, zbc, s))
        for z in sorted(depths):
            dd.split(p, z)
        dd.sort(p)
        dd.merge_overlaps(p)
        alpha = np.float32(0)
        for s in range(dd.samples(p)):
            if alpha >= 1:
                break
            t = np.float32(1) - alpha
            for k, c in enumerate(oc):
                out[p, k] += t * np.float32(dd.deep_value(p, c, s))
            alpha = alpha + t * np.float32(dd.deep_value(p, ac, s))
    diff = np.abs(out.reshape(a.shape) - flat[..., rc]).max()
    print('  OpenImageIO agrees with the tidy flattening of %s within %.2g' % (os.path.basename(path), diff))
    assert diff < 1e-3, (path, diff)


def gen_htj2k():
    """HTJ2K (OpenEXR 3.4) files in htj2k/: lossless, so .ref / .all.ref as for the other flat fixtures."""
    d = clean('htj2k')
    global OUT
    root, OUT = OUT, d
    try:
        for cn, c in (('htj2k256', X.HTJ2K256_COMPRESSION), ('htj2k32', X.HTJ2K32_COMPRESSION)):
            write('rgb_half_' + cn, {'compression': c}, rgb(35, 37), ['R', 'G', 'B'], expect=[cn])
            write('mixed_' + cn, {'compression': c}, mixed(19, 34), ['R', 'G', 'B', 'A'], all_ref=True, expect=[cn])
            write('one_' + cn, {'compression': c}, rgb(1, 1, np.float16, True), ['R', 'G', 'B', 'A'], expect=[cn])
            write('uint_' + cn, {'compression': c}, {'id': ids(9, 33)}, ['id'], expect=[cn])
            write('float_rgb_' + cn, {'compression': c}, rgb(13, 18, np.float32), ['R', 'G', 'B'], expect=[cn])
            write('tall_' + cn, {'compression': c}, rgb(21, 300, np.float16, True, 5), ['R', 'G', 'B', 'A'], expect=[cn])
            write('tiled_' + cn, {'compression': c, 'type': X.tiledimage, 'tiles': tiles(16, 8)}, rgb(37, 21, np.float16, True), ['R', 'G', 'B', 'A'], expect=[cn])
        sub = test_pattern(24, 36, 3, np.float32, 8)
        name = os.path.join(d, 'subsampled_htj2k256.exr')
        X.File({'compression': X.HTJ2K256_COMPRESSION},
               {'a': X.Channel('a', sub[..., 0].copy(), 2, 1), 'b': X.Channel('b', sub[..., 1].copy(), 1, 2),
                'c': X.Channel('c', sub[..., 2].astype(np.float16), 2, 2), 'u': X.Channel('u', ids(24, 36), 2, 2)}).write(name)
        write_ref(name + '.ref', stack(read_parts(name)[0], ['a', 'b', 'c', 'u']))
        wide = rgb(300, 40, np.float16, False, 9)
        write('wide_htj2k32', {'compression': X.HTJ2K32_COMPRESSION}, wide, ['R', 'G', 'B'])
    finally:
        OUT = root


def gen_attributes():
    """One file with the standard attribute types the bindings can write (attributes/attributes.exr)."""
    d = clean('attributes')
    hdr = {'compression': X.ZIP_COMPRESSION,
           'timeCode': X.TimeCode(12, 34, 56, 23, 1, 0, 0, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8),
           'keyCode': X.KeyCode(1, 2, 3, 4, 5, 4, 64),
           'envmap': X.Envmap(1),
           'idmanifest': X.IDManifest(),
           'multiView': ['left', 'right'],
           'worldToCamera': np.arange(16, dtype=np.float32).reshape(4, 4),
           'm33': np.arange(9, dtype=np.float32).reshape(3, 3),
           'v3i': np.array([1, 2, 3], np.int32),
           'v2d': np.array([1.5, 2.5], np.float64),
           'v3d': np.array([1.5, 2.5, 3.5], np.float64),
           'm44d': np.arange(16, dtype=np.float64).reshape(4, 4),
           'box2f': (np.array([0.5, 1.5], np.float32), np.array([2.5, 3.5], np.float32)),
           'fv': [1.0, 2.0, 3.5],
           'whiteLuminance': 100.0,
           'adoptedNeutral': np.array([0.3127, 0.329], np.float32)}
    p = X.PreviewImage(5, 3)
    try:
        px = p.pixels
        for y in range(3):
            for x in range(5):
                px[y, x] = (10 * y + x, 20, 30 + x, 255 - y)
        p = X.PreviewImage(px)
    except Exception:
        pass
    hdr['preview'] = p
    X.File(hdr, {'R': np.zeros((4, 4), 'float16')}).write(os.path.join(d, 'attributes.exr'))


def gen_selfcheck():
    """References for files written by OUR encoder (tests/Aardvark.PixImage.PixCodec.Codecs.Tests/ExrSelfCheckTests.cs with
    EXR_WRITE_SELFCHECK=1 writes them): every level and every deep sample as the reference library reads it."""
    d = os.path.join(OUT, 'selfcheck')
    if not os.path.isdir(d):
        return
    for f in os.listdir(d):
        if not f.endswith('.exr'):
            os.remove(os.path.join(d, f))
    for f in sorted(os.listdir(d)):
        path = os.path.join(d, f)
        for i, part in enumerate(X.File(path, header_only=True).parts):
            hd = part.header
            kind = str(hd.get('type'))
            if 'deep' in kind:
                oracle('deep', path, i, 0, 0, '%s.part%d.deep' % (path, i))
                t = hd.get('tiles')
                if t is not None and t.mode != X.ONE_LEVEL:
                    dw = hd['dataWindow']
                    w, h = int(dw[1][0] - dw[0][0] + 1), int(dw[1][1] - dw[0][1] + 1)
                    for lx, ly in level_list(w, h, 'mipmap' if t.mode == X.MIPMAP_LEVELS else 'ripmap', 'up' if t.roundingMode == X.ROUND_UP else 'down'):
                        if (lx, ly) != (0, 0):
                            oracle('deep', path, i, lx, ly, '%s.part%d.l%d_%d.deep' % (path, i, lx, ly))
            elif 'tiles' in hd:
                t = hd['tiles']
                dw = hd['dataWindow']
                w, h = int(dw[1][0] - dw[0][0] + 1), int(dw[1][1] - dw[0][1] + 1)
                mode = 'mipmap' if t.mode == X.MIPMAP_LEVELS else 'ripmap' if t.mode == X.RIPMAP_LEVELS else None
                levels = level_list(w, h, mode, 'up' if t.roundingMode == X.ROUND_UP else 'down') if mode else [(0, 0)]
                for lx, ly in levels:
                    oracle('level', path, i, lx, ly, '%s.l%d_%d.ref' % (path, lx, ly))
            else:
                ch = read_parts(path)[i]
                write_ref('%s.part%d.ref' % (path, i), stack(ch, sorted(ch.keys(), key=lambda n: n.encode())))


OFFICIAL = 'https://raw.githubusercontent.com/AcademySoftwareFoundation/openexr-images/main/'


def gen_official():
    """A few small files of the openexr-images collection (BSD licence, see LICENSE there); hashes of reference decodes.
    They live in the test-data repository (fixtures/exr/official there)."""
    import hashlib
    import urllib.request
    d = testdata_dir('fixtures', 'exr', 'official')
    if d is None:
        return
    os.makedirs(d, exist_ok=True)
    for f in os.listdir(d):
        if not f.startswith('LICENSE') and not f.endswith('.exr'):
            os.remove(os.path.join(d, f))

    def fetch(rel, name):
        path = os.path.join(d, name)
        if not os.path.exists(path):
            urllib.request.urlretrieve(OFFICIAL + rel, path)
        return path
    fetch('LICENSE', 'LICENSE.openexr-images')
    tmp = os.path.join(d, '_tmp.bin')
    for rel in ('MultiResolution/ColorCodedLevels.exr', 'MultiResolution/MirrorPattern.exr'):
        path = fetch(rel, os.path.basename(rel))
        lines = []
        for l in range(10):
            oracle('level', path, 0, l, l, tmp)
            lines.append('level %d %d %s' % (l, l, hashlib.sha256(open(tmp, 'rb').read()[20:]).hexdigest()))
        open(path + '.sha256', 'w').write('\n'.join(lines) + '\n')
    path = fetch('v2/LowResLeftView/Trunks.exr', 'Trunks.exr')
    oracle('deep', path, 0, 0, 0, tmp)
    w, h, counts, ch = read_deep(tmp)
    hsh = hashlib.sha256(counts.astype(np.int32).tobytes())
    for n in ch:
        hsh.update(ch[n].tobytes())
    lines = ['deep %s' % hsh.hexdigest()]
    oracle('flatten', path, 0, tmp, 'R', 'G', 'B', 'A')
    lines.append('flat %s' % hashlib.sha256(open(tmp, 'rb').read()[20:]).hexdigest())
    open(path + '.sha256', 'w').write('\n'.join(lines) + '\n')
    os.remove(tmp)


def main():
    os.makedirs(os.path.join(OUT, 'dwa'), exist_ok=True)
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
    for cn in ('none', 'zip', 'piz', 'b44', 'pxr24', 'rle', 'zstd'):
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

    # --- subsampled channels with odd factors (luminance/chroma files are in yc/)
    sub = test_pattern(24, 36, 3, np.float32, 8)
    for cn in ('zip', 'zstd'):
        name = os.path.join(OUT, 'subsampled_%s.exr' % cn)
        X.File({'compression': COMP[cn], 'dataWindow': window(-12, 36, 24, 36), 'displayWindow': window(0, 0, 24, 36)},
               {'a': X.Channel('a', sub[..., 0].copy(), 3, 1), 'b': X.Channel('b', sub[..., 1].copy(), 1, 4),
                'c': X.Channel('c', sub[..., 2].astype(np.float16), 4, 3), 'u': X.Channel('u', ids(24, 36), 2, 2)}).write(name)
        write_ref(name + '.ref', stack(read_parts(name)[0], ['a', 'b', 'c', 'u']))

    # --- files written by ImageMagick (a second writer on top of OpenEXR 3.1)
    src = os.path.join(OUT, '_src.png')
    from PIL import Image
    Image.fromarray(test_pattern(31, 22, 4, np.uint8, 12), 'RGBA').save(src)
    for cn, flag in (('zip', 'Zip'), ('piz', 'Piz'), ('b44', 'B44'), ('pxr24', 'Pxr24'), ('rle', 'RLE')):
        path = os.path.join(OUT, 'magick_%s.exr' % cn)
        subprocess.run(['magick', src, '-define', 'exr:color-type=RGBA', '-compress', flag, path], check=True)
        finish('magick_' + cn, RGBA, expect=[cn])
    os.remove(src)

    # --- DWAA / DWAB in dwa/ (lossy, but decoding is deterministic: bit exact like everything else)
    def dwa(name, header, chans, default):
        path = os.path.join(OUT, 'dwa', name + '.exr')
        X.File(header, chans).write(path)
        d = read_parts(path)[0]
        write_ref(path + '.ref', stack(d, default))
        if len(d) > 4:
            write_ref(path + '.all.ref', stack(d, sorted(d.keys(), key=lambda s: s.encode())))
    clean('dwa')
    dwa('dwaa_rgba', {'compression': X.DWAA_COMPRESSION}, rgb(67, 45, np.float16, True), RGBA)
    dwa('dwab_rgba', {'compression': X.DWAB_COMPRESSION}, rgb(53, 270, np.float16, True, 3), RGBA)
    dwa('dwaa_mixed', {'compression': X.DWAA_COMPRESSION}, mixed(70, 41), RGBA)
    dwa('dwaa_float_rgb', {'compression': X.DWAA_COMPRESSION}, rgb(64, 40, np.float32), RGB)
    dwa('dwaa_y', {'compression': X.DWAA_COMPRESSION}, {'Y': test_pattern(90, 33, 1, np.float16, 5)[..., 0].copy()}, ['Y'])
    dwa('dwaa_tiled', {'compression': X.DWAA_COMPRESSION, 'type': X.tiledimage, 'tiles': tiles(48, 40)}, rgb(100, 60, np.float16, True, 4), RGBA)
    dwa('dwab_tiny_raw', {'compression': X.DWAB_COMPRESSION}, rgb(5, 3), RGB)      # too small to compress: stored raw
    wide = test_pattern(80, 64, 3, np.float32, 6) * np.array([1, 30, 400], np.float32)
    dwa('dwaa_bright', {'compression': X.DWAA_COMPRESSION},
        {'R': wide[..., 0].astype(np.float16), 'G': wide[..., 1].astype(np.float16), 'B': wide[..., 2].astype(np.float16)}, RGB)

    gen_yc(yc_source=test_pattern(16, 36, 4, np.float16, 6))
    gen_levels()
    gen_deep()
    gen_official()
    gen_selfcheck()
    gen_htj2k()
    gen_attributes()

    total = sum(os.path.getsize(os.path.join(dp, f)) for dp, _, fs in os.walk(OUT) for f in fs)
    print('fixtures: %d files, %.1f kB' % (sum(len(fs) for _, _, fs in os.walk(OUT)), total / 1024))


if __name__ == '__main__':
    sys.exit(main())
