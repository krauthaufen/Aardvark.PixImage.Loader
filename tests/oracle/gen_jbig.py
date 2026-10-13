"""Generates the JBIG1 fixtures: tests/fixtures/jbig/<name>.jbg + <name>.jbg.ref.

The streams are written, and the references decoded, by jbigkit 2.1 (Markus Kuhn, GPL), which is NOT part of this
repository: build it yourself (https://www.cl.cam.ac.uk/~mgk25/jbigkit/) and point PIXCODEC_JBIGKIT at the directory
holding pbmtojbg / jbgtopbm (default: <repo>/.tmp/jbigkit-2.1/pbmtools). Without the tools the existing fixtures are
kept. All pictures are synthetic and generated here; nothing shipped with jbigkit is used.

References follow the decoder's conventions: one plane -> 0 = foreground (black), 255 = background; several planes ->
the levels 0 .. 2^planes - 1.
"""
import os
import shutil
import subprocess
import sys
import tempfile
import numpy as np
from pxref import write_ref

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'fixtures', 'jbig')
TOOLS = os.environ.get('PIXCODEC_JBIGKIT', os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', '.tmp', 'jbigkit-2.1', 'pbmtools'))


def text_page(w, h, seed=1):
    """Document like bi-level content: blank areas, runs of small glyph-ish blobs, rules and a filled box."""
    rng = np.random.default_rng(seed)
    a = np.zeros((h, w), np.uint8)
    y = 2
    while y < h - 4:
        lh = int(rng.integers(3, 9))
        if rng.random() < 0.75:
            x = int(rng.integers(0, max(1, w // 6)))
            while x < w - 2:
                gw = int(rng.integers(1, 6))
                glyph = (rng.random((lh, gw)) < 0.6).astype(np.uint8)
                a[y:y + lh, x:x + gw] |= glyph[:min(lh, h - y), :min(gw, w - x)]
                x += gw + int(rng.integers(1, 4))
                if rng.random() < 0.1:
                    x += int(rng.integers(3, 12))
        y += lh + int(rng.integers(1, 6))
    if w > 20 and h > 20:
        a[h // 2, w // 8: w - w // 8] = 1
        a[h // 3: h // 3 + h // 8, w // 2: w // 2 + w // 5] = 1
    return a


def dither(w, h, period=4, seed=2):
    """Ordered dither of a gradient: strongly periodic, which is what adaptive template moves are for."""
    y, x = np.mgrid[0:h, 0:w]
    rng = np.random.default_rng(seed)
    thresh = rng.permutation(period * period).reshape(period, period)
    level = (x * period * period) // max(1, w)
    return (thresh[y % period, x % period] < level).astype(np.uint8)


def noise(w, h, p=0.5, seed=3):
    return (np.random.default_rng(seed).random((h, w)) < p).astype(np.uint8)


def gray(w, h, levels=256, seed=4):
    y, x = np.mgrid[0:h, 0:w].astype(np.float64)
    rng = np.random.default_rng(seed)
    v = 0.5 + 0.4 * np.sin(x / 9.0) * np.cos(y / 7.0) + 0.05 * rng.standard_normal((h, w))
    return np.clip(np.round(v * (levels - 1)), 0, levels - 1).astype(np.uint8)


def pnm(path, a, maxval=None):
    h, w = a.shape
    with open(path, 'wb') as f:
        if maxval is None:
            f.write(b'P4\n%d %d\n' % (w, h))
            f.write(np.packbits(a, axis=1).tobytes())
        else:
            f.write(b'P5\n%d %d\n%d\n' % (w, h, maxval))
            f.write(a.tobytes())


def read_pnm(path):
    with open(path, 'rb') as f:
        d = f.read()
    magic = d[:2]
    fields, pos = [], 2
    while len(fields) < (2 if magic == b'P4' else 3):
        while d[pos:pos + 1].isspace():
            pos += 1
        if d[pos:pos + 1] == b'#':
            pos = d.index(b'\n', pos)
            continue
        end = pos
        while not d[end:end + 1].isspace():
            end += 1
        fields.append(int(d[pos:end]))
        pos = end
    pos += 1
    w, h = fields[0], fields[1]
    if magic == b'P4':
        bits = np.unpackbits(np.frombuffer(d, np.uint8, offset=pos).reshape(h, (w + 7) // 8), axis=1)[:, :w]
        return ((1 - bits) * 255).astype(np.uint8)  # PBM 1 = black
    if fields[2] > 255:
        return np.frombuffer(d, '>u2', offset=pos).reshape(h, w).astype(np.uint16)
    return np.frombuffer(d, np.uint8, offset=pos).reshape(h, w).copy()


def encode(a, args, maxval=None):
    with tempfile.TemporaryDirectory() as t:
        src, dst = os.path.join(t, 'i.pnm'), os.path.join(t, 'o.jbg')
        pnm(src, a, maxval)
        subprocess.run([os.path.join(TOOLS, 'pbmtojbg'), *args, src, dst], check=True, stderr=subprocess.DEVNULL)
        with open(dst, 'rb') as f:
            return f.read()


def decode(data, args=()):
    with tempfile.TemporaryDirectory() as t:
        src, dst = os.path.join(t, 'i.jbg'), os.path.join(t, 'o.pnm')
        with open(src, 'wb') as f:
            f.write(data)
        subprocess.run([os.path.join(TOOLS, 'jbgtopbm'), *args, src, dst], check=True, stderr=subprocess.DEVNULL)
        return read_pnm(dst)


def emit(name, data, dec_args=(), sub=None, ref=True, like=None):
    """like: a stream of the same picture that jbgtopbm can decode, for the stripe orders it does not implement."""
    d = OUT if sub is None else os.path.join(OUT, sub)
    os.makedirs(d, exist_ok=True)
    path = os.path.join(d, name + '.jbg')
    with open(path, 'wb') as f:
        f.write(data)
    if ref:
        write_ref(path + '.ref', decode(data if like is None else like, dec_args))


def main():
    if not os.path.exists(os.path.join(TOOLS, 'pbmtojbg')):
        print('jbigkit tools not found in', TOOLS, '- keeping the existing fixtures')
        return
    if os.path.isdir(OUT):
        shutil.rmtree(OUT)
    os.makedirs(OUT)
    page, small = text_page(200, 150), text_page(61, 47, seed=5)

    # single layer: sizes, option bits, stripes
    for (w, h) in [(1, 1), (2, 3), (7, 5), (8, 8), (9, 17), (33, 17), (64, 64), (129, 66), (200, 150)]:
        emit(f'seq_{w}x{h}', encode(text_page(w, h, seed=w + h), ['-q']))
    for p in [0, 8, 64, 72]:
        emit(f'seq_p{p}', encode(page, ['-q', '-p', str(p)]))
        emit(f'seq_p{p}_noise', encode(noise(90, 70, 0.3, seed=p), ['-q', '-p', str(p)]))
    for s in [1, 2, 7, 50, 1000]:
        emit(f'seq_s{s}', encode(page, ['-q', '-s', str(s)]))
        emit(f'seq_s{s}_rst', encode(page, ['-q', '-s', str(s), '-r']))
        emit(f'seq_s{s}_rst_2line', encode(small, ['-q', '-s', str(s), '-r', '-p', '72']))
    emit('seq_blank', encode(np.zeros((120, 160), np.uint8), ['-q']))
    emit('seq_black', encode(np.ones((60, 70), np.uint8), ['-q', '-s', '9']))
    emit('seq_noise', encode(noise(150, 120), ['-q']))
    emit('seq_t85', encode(text_page(200, 260, seed=9), ['-f']))
    emit('seq_comment', encode(small, ['-q', '-C', 'made for the PixCodec tests']))

    # adaptive template moves (periodic content makes the encoder move the pixel)
    for period in [3, 4, 6, 9]:
        for extra in [[], ['-c'], ['-p', '72'], ['-s', '16'], ['-s', '16', '-r']]:
            tag = '_'.join(x.strip('-') for x in extra)
            emit(f'atmove_{period}{"_" + tag if tag else ""}', encode(dither(180, 120, period), ['-q', '-m', '127', *extra]))
    emit('atmove_diff', encode(dither(180, 120, 5), ['-d', '2', '-m', '127']))
    emit('atmove_diff_c', encode(dither(181, 121, 7), ['-d', '2', '-m', '32', '-c', '-s', '8']))

    # variable length (NEWLEN)
    emit('newlen', encode(page, ['-q', '-s', '40', '-Y', '400']))
    emit('newlen_one_stripe', encode(small, ['-q', '-s', '1000', '-Y', '90']))
    emit('newlen_layers', encode(page, ['-d', '2', '-s', '20', '-Y', '333']))

    # resolution layers with all prediction options, sizes that make odd lower layers
    for d in [1, 2, 3, 5]:
        for (w, h) in [(1, 1), (7, 5), (33, 17), (129, 66), (200, 150)]:
            emit(f'layers{d}_{w}x{h}', encode(text_page(w, h, seed=d + w), ['-d', str(d)]))
    for p in [0, 4, 8, 16, 20, 24, 28, 64, 92]:
        emit(f'layers2_p{p}', encode(page, ['-d', '2', '-p', str(p)]))
        emit(f'layers3_p{p}_noise', encode(noise(77, 59, 0.2, seed=p), ['-d', '3', '-p', str(p), '-s', '3']))
        emit(f'layers2_p{p}_rst', encode(small, ['-d', '2', '-p', str(p), '-s', '5', '-r']))
    # all 12 stripe orders; jbgtopbm only reads the ones that complete each layer before the next, so the others
    # are referenced through a twin in such an order (the coding is lossless: same picture)
    for o in [0, 2, 3, 4, 5, 6, 8, 10, 11, 12, 13, 14]:
        twin = None if o in (0, 2, 3) else encode(small, ['-d', '2', '-s', '4', '-o', '3'])
        emit(f'order{o}', encode(small, ['-d', '2', '-s', '4', '-o', str(o)]), like=twin)
        g16 = gray(40, 30, 16, seed=o)
        twin = None if o in (0, 2, 3) else encode(g16, ['-d', '1', '-s', '4', '-o', '3', '-t', '4'], maxval=15)
        emit(f'order{o}_planes', encode(g16, ['-d', '1', '-s', '4', '-o', str(o), '-t', '4'], maxval=15), like=twin)
        emit(f'order{o}_seq', encode(small, ['-q', '-s', '6', '-o', str(o)]))
    emit('layers_blank', encode(np.zeros((90, 130), np.uint8), ['-d', '3']))
    emit('layers_dither', encode(dither(120, 90, 4), ['-d', '3']))
    emit('layers_auto', encode(text_page(700, 500, seed=3), []))  # layer count chosen by the encoder

    # bit planes: Gray coded (default) and binary
    for planes in [2, 3, 4, 8]:
        g = gray(70, 50, 1 << planes, seed=planes)
        emit(f'planes{planes}_gray', encode(g, ['-q'], maxval=(1 << planes) - 1))
        emit(f'planes{planes}_binary', encode(g, ['-q', '-b'], maxval=(1 << planes) - 1), dec_args=['-b'])
        emit(f'planes{planes}_layers', encode(g, ['-d', '2'], maxval=(1 << planes) - 1))
    emit('planes_t3', encode(gray(70, 50), ['-q', '-t', '3'], maxval=255))

    # several entities for one image: layers 0..1 in the first, layer 2 in the second (decoded as one)
    multi = text_page(160, 120, seed=21)
    first, second = encode(multi, ['-d', '2', '-h', '1']), encode(multi, ['-d', '2', '-l', '2'])
    emit('concat_lower', first)
    emit('concat', first + second, dec_args=['-m'])
    emit('upper_only', second, sub='unsupported', ref=False)

    n = sum(len(fs) for _, _, fs in os.walk(OUT))
    total = sum(os.path.getsize(os.path.join(r, f)) for r, _, fs in os.walk(OUT) for f in fs)
    print(f'{n} files, {total / 1e6:.2f} MB')


if __name__ == '__main__':
    main()
