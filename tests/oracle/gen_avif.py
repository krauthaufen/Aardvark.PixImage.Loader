"""Generates tests/fixtures/avif (AVIF files) and tests/fixtures/av1 (AV1 IVF streams) from synthetic images.

  * name.avif: written by libavif's avifenc (codecs aom and rav1e) or avifgainmaputil.
  * name.avif.json: what the tests check: size, channels, bit depth, orientation, frame count, and the SHA-256 of the
    decoded AV1 planes of every frame ("yuv": dav1d through avifdec's y4m output, as raw planes Y, U, V with 16 bit
    little-endian samples above 8 bit) - the AV1 layer has to match bit for bit.
  * name.avif.ref (name.avif.N.ref for frame N of sequences): avifdec's RGB(A) output with bilinear chroma
    upsampling (8 bit, or 16 bit for deeper streams), which the tests compare within 1 (in the stream's bit depth).
  * av1/name.ivf + name.ivf.json: aomenc streams covering the inter tools with the SHA-256 of dav1d's raw output of
    all frames.

Tools: avifenc, avifdec, avifgainmaputil, aomenc, dav1d from PATH (tests/oracle/ci/install.sh avif) or
.tmp/tools/usr/bin (the Debian trixie packages libavif-bin 1.2.1, aom-tools 3.12.1 and dav1d 1.5.1 unpacked there, with
their libraries in .tmp/tools/usr/lib/x86_64-linux-gnu); the fixtures were made with those versions.

usage: python3 gen_avif.py [avif] [gainmap] [av1]
"""
import hashlib
import json
import math
import os
import shutil
import subprocess
import sys
import numpy as np
from PIL import Image, ImageDraw, ImageFilter
from pxref import write_ref

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, '..', '..'))
OUT = os.path.join(HERE, '..', 'fixtures', 'avif')
OUT_AV1 = os.path.join(HERE, '..', 'fixtures', 'av1')
TMP = os.path.join(OUT, '_tmp')
LOCAL = os.path.join(ROOT, '.tmp', 'tools', 'usr')
ENV = dict(os.environ)
if os.path.isdir(LOCAL):
    ENV['LD_LIBRARY_PATH'] = os.path.join(LOCAL, 'lib', 'x86_64-linux-gnu') + os.pathsep + ENV.get('LD_LIBRARY_PATH', '')


def tool(name):
    p = os.path.join(LOCAL, 'bin', name)
    if os.path.exists(p): return p
    p = shutil.which(name)
    if not p: sys.exit(f'{name} not found (PATH or .tmp/tools/usr/bin)')
    return p


def run(*args):
    r = subprocess.run([str(a) for a in args], env=ENV, capture_output=True, text=True)
    if r.returncode != 0:
        sys.exit(f'failed: {" ".join(map(str, args))}\n{r.stdout[-2000:]}\n{r.stderr[-2000:]}')
    return r.stdout


# ------------------------------------------------------------------ synthetic content

def natural(w, h, seed=1, bits=8):
    """Smooth multi scale noise with soft stripes: looks like natural texture (no flat areas, no screen content)."""
    rng = np.random.default_rng(seed)
    acc = np.zeros((h, w, 3))
    for s, amp in [(64, 0.5), (16, 0.25), (4, 0.12), (1, 0.05)]:
        n = rng.random((max(2, h // s + 2), max(2, w // s + 2), 3))
        im = Image.fromarray((n * 255).astype(np.uint8)).resize((w, h), Image.BICUBIC)
        acc += amp * np.asarray(im, dtype=np.float64) / 255
    y, x = np.mgrid[0:h, 0:w]
    acc += 0.15 * ((x + 2 * y) % 97 < 48)[..., None]
    acc = (acc - acc.min()) / (acc.max() - acc.min() + 1e-9)
    if bits == 8:
        return np.asarray(Image.fromarray(np.round(acc * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(0.7)))
    return np.round(acc * 65535).astype(np.uint16)   # 16 bit PNG; avifenc -d reduces it


def screen(w, h, seed=3):
    """Few colours, glyph like boxes and a repeated tile: triggers palette and intra block copy in aom."""
    img = Image.new('RGB', (w, h), (240, 240, 245))
    d = ImageDraw.Draw(img)
    rng = np.random.default_rng(seed)
    cols = [(20, 20, 20), (200, 30, 30), (30, 90, 200), (250, 200, 0), (0, 150, 70)]
    for i in range(0, h, 12):
        for j in range(0, w, 7):
            if rng.random() < 0.6:
                c = cols[rng.integers(0, len(cols))]
                d.rectangle([j, i, j + 4, i + 8], outline=c)
                d.line([j, i + 4, j + 4, i + 4], fill=c)
    tile = img.crop((0, 0, 32, 24))
    for k in range(4):
        img.paste(tile, (w // 2 + (k * 32) % (w // 2), h // 2))
    return np.asarray(img)


def with_alpha(rgb):
    h, w = rgb.shape[:2]
    y, x = np.mgrid[0:h, 0:w]
    mx = 255 if rgb.dtype == np.uint8 else 65535
    a = np.clip((x / max(1, w - 1)) * 0.8 + ((y // 8) % 2) * 0.2, 0, 1)
    a[: h // 4, : w // 4] = 0
    return np.concatenate([rgb, np.round(a * mx).astype(rgb.dtype)[..., None]], -1)


def save_png(path, a):
    if a.dtype == np.uint16:
        # PIL cannot write 16 bit RGB: go through numpy -> PPM-like path via imagecodecs if needed
        import imagecodecs
        with open(path, 'wb') as f: f.write(imagecodecs.png_encode(a))
    else:
        Image.fromarray(a).save(path)


# ------------------------------------------------------------------ y4m / references

def read_y4m(path):
    d = open(path, 'rb').read()
    hdr_end = d.index(b'\n')
    toks = d[:hdr_end].decode().split()
    w = int(next(t for t in toks if t[0] == 'W')[1:])
    h = int(next(t for t in toks if t[0] == 'H')[1:])
    c = next((t[1:] for t in toks if t[0] == 'C'), '420jpeg')
    bd = 12 if c.endswith('12') else 10 if c.endswith('10') else 8
    sub = None if c.startswith('mono') else (0, 0) if c.startswith('444') else (1, 0) if c.startswith('422') else (1, 1)
    bps = 2 if bd > 8 else 1
    dims = [(w, h)] + ([] if sub is None else [((w + sub[0]) >> sub[0], (h + sub[1]) >> sub[1])] * 2)
    frames, pos = [], hdr_end + 1
    while pos < len(d):
        assert d[pos:pos + 5] == b'FRAME'
        pos = d.index(b'\n', pos) + 1
        n = sum(pw * ph for pw, ph in dims) * bps
        frames.append(d[pos:pos + n])
        pos += n
    return w, h, bd, sub, frames


def avif_yuv(path, index=0):
    y4m = os.path.join(TMP, 'out.y4m')
    run(tool('avifdec'), '-c', 'dav1d', '--index', index, path, y4m)
    return read_y4m(y4m)


def avif_rgb(path, deep, index=0):
    png = os.path.join(TMP, 'out.png')
    run(tool('avifdec'), '-c', 'dav1d', '-u', 'bilinear', '-d', 16 if deep else 8, '--index', index, path, png)
    if deep:
        import imagecodecs
        return imagecodecs.png_decode(open(png, 'rb').read())
    return np.asarray(Image.open(png))


def avif_frames(path):
    out = run(tool('avifdec'), '--info', path)
    for line in out.splitlines():
        if 'Frames' in line or 'frames' in line:
            try: return int(line.split(':')[-1].split()[0])
            except ValueError: pass
    return 1


# ------------------------------------------------------------------ cases

def still(name, img, args, expect=None, frames_from=None):
    os.makedirs(TMP, exist_ok=True)
    src = os.path.join(TMP, name + '.png')
    if isinstance(img, list):
        srcs = []
        for i, im in enumerate(img):
            p = os.path.join(TMP, f'{name}.{i}.png')
            save_png(p, im)
            srcs.append(p)
    else:
        save_png(src, img)
        srcs = [src]
    out = os.path.join(OUT, name + '.avif')
    run(tool('avifenc'), *args, *srcs, '-o', out)
    finish(name, out, expect, len(srcs) if isinstance(img, list) and '-g' not in args and '--grid' not in args else 1)


def finish(name, out, expect, nframes):
    info = {'frames': nframes}
    info.update(expect or {})
    yuv = []
    for i in range(nframes):
        w, h, bd, sub, fr = avif_yuv(out, i)
        yuv.append(hashlib.sha256(fr[0]).hexdigest())
        ref = avif_rgb(out, bd > 8, i)
        write_ref(out + ('.ref' if nframes == 1 else f'.{i}.ref'), ref)
    info.update({'width': int(ref.shape[1]), 'height': int(ref.shape[0]), 'channels': int(ref.shape[2]) if ref.ndim == 3 else 1,
                 'bitDepth': bd, 'subsampling': 'mono' if sub is None else f'{sub[0]}{sub[1]}', 'yuv': yuv})
    with open(out + '.json', 'w') as f: json.dump(info, f, indent=1)
    print(name, info['width'], info['height'], bd, info['subsampling'], os.path.getsize(out))


def gen_stills():
    nat = natural(160, 112)
    nat_odd = natural(117, 75, 2)
    still('nat_420_s6', nat, ['-y', '420', '-c', 'aom', '-s', '6', '-q', '50'])
    still('nat_420_s0', natural(96, 64, 4), ['-y', '420', '-c', 'aom', '-s', '0', '-q', '45'])
    still('nat_420_10', natural(160, 112, 5, 10), ['-y', '420', '-c', 'aom', '-s', '5', '-q', '50', '-d', '10'])
    still('nat_444_12', natural(96, 80, 6, 12), ['-c', 'aom', '-s', '5', '-q', '60', '-d', '12', '-y', '444'])
    still('nat_422_10', natural(96, 80, 7, 10), ['-c', 'aom', '-s', '5', '-q', '60', '-d', '10', '-y', '422'])
    still('nat_422_12', nat_odd, ['-c', 'aom', '-s', '5', '-q', '60', '-d', '12', '-y', '422'])
    still('nat_444_8', nat, ['-c', 'aom', '-s', '5', '-q', '55', '-y', '444'])
    still('gray_400', natural(96, 64, 8)[..., :1].repeat(3, -1), ['-c', 'aom', '-s', '5', '-q', '50', '-y', '400'])
    still('gray_400_10', natural(80, 64, 9, 10)[..., :1].repeat(3, -1), ['-c', 'aom', '-s', '5', '-q', '50', '-y', '400', '-d', '10'])
    still('lossless_444', natural(64, 48, 10), ['-c', 'aom', '-s', '6', '-l'])
    still('lossless_420', natural(64, 48, 11), ['-c', 'aom', '-s', '6', '-q', '100', '-y', '420'])
    still('odd_size', nat_odd, ['-y', '420', '-c', 'aom', '-s', '6', '-q', '40'])
    still('tiny', natural(7, 5, 12), ['-y', '420', '-c', 'aom', '-s', '6', '-q', '50'])
    still('rav1e_cdef', nat, ['-y', '420', '-c', 'rav1e', '-s', '5', '-q', '50'])
    still('rav1e_444_10', natural(96, 64, 13, 10), ['-c', 'rav1e', '-s', '6', '-q', '50', '-y', '444', '-d', '10'])
    still('tiles_2x2', natural(320, 256, 14), ['-y', '420', '-c', 'aom', '-s', '8', '-q', '40', '--tilerowslog2', '1', '--tilecolslog2', '1'])
    still('screen_444', screen(128, 96), ['-c', 'aom', '-s', '4', '-q', '70', '-y', '444', '-a', 'tune-content=screen'])
    still('screen_420_10', screen(128, 96, 4), ['-y', '420', '-c', 'aom', '-s', '6', '-q', '40', '-d', '10', '-a', 'tune-content=screen'])
    still('filmgrain', nat, ['-y', '420', '-c', 'aom', '-s', '6', '-q', '40', '-a', 'film-grain-test=1'])
    still('sb128_lr', natural(160, 112, 15), ['-y', '420', '-c', 'aom', '-s', '3', '-q', '30', '-a', 'sb-size=128', '-a', 'enable-restoration=1'])
    still('qm', nat, ['-y', '420', '-c', 'aom', '-s', '5', '-q', '40', '-a', 'enable-qm=1', '-a', 'qm-min=0'])
    still('limited_range', nat, ['-y', '420', '-c', 'aom', '-s', '6', '-q', '60', '-r', 'limited'])
    still('bt709', nat, ['-y', '420', '-c', 'aom', '-s', '6', '-q', '60', '--cicp', '1/13/1'])
    still('bt2020_pq_10', natural(96, 64, 16, 10), ['-y', '420', '-c', 'aom', '-s', '6', '-q', '60', '-d', '10', '--cicp', '9/16/9'])
    still('identity_444', nat, ['-c', 'aom', '-s', '6', '-q', '70', '-y', '444', '--cicp', '1/13/0'])
    still('ycgco_444', natural(64, 48, 17), ['-c', 'aom', '-s', '6', '-q', '70', '-y', '444', '--cicp', '2/13/8'])
    # alpha and premultiplied alpha
    still('alpha', with_alpha(nat), ['-y', '420', '-c', 'aom', '-s', '6', '-q', '50', '--qalpha', '60'])
    still('alpha_lossless_10', with_alpha(natural(96, 64, 18, 10)), ['-y', '420', '-c', 'aom', '-s', '6', '-q', '50', '--qalpha', '100', '-d', '10'])
    still('alpha_premultiplied', with_alpha(nat), ['-y', '420', '-c', 'aom', '-s', '6', '-q', '70', '--qalpha', '100', '-p'])
    # grids: one image split into 2x2 tiles
    still('grid_2x2', natural(192, 128, 19), ['-y', '420', '-c', 'aom', '-s', '7', '-q', '50', '-g', '2x2'])
    still('grid_alpha_3x1', with_alpha(natural(192, 64, 20)), ['-y', '420', '-c', 'aom', '-s', '7', '-q', '50', '-g', '3x1'])
    # transforms: avifdec ignores them, so the refs are the stored pixels and expect says what to apply
    still('irot1', nat, ['-y', '420', '-c', 'aom', '-s', '7', '-q', '50', '--irot', '1'], {'irot': 1})
    still('irot3_imir0', nat, ['-y', '420', '-c', 'aom', '-s', '7', '-q', '50', '--irot', '3', '--imir', '0'], {'irot': 3, 'imir': 0})
    still('imir1', nat, ['-y', '420', '-c', 'aom', '-s', '7', '-q', '50', '--imir', '1'], {'imir': 1})
    still('clap', nat, ['-y', '420', '-c', 'aom', '-s', '7', '-q', '50', '--crop', '10,20,100,60'], {'crop': [10, 20, 100, 60]})
    # metadata items
    exif = os.path.join(TMP, 'exif.bin')
    xmp = os.path.join(TMP, 'xmp.xml')
    os.makedirs(TMP, exist_ok=True)
    with open(exif, 'wb') as f: f.write(exif_blob())
    with open(xmp, 'w') as f: f.write(XMP)
    still('exif_xmp', nat, ['-y', '420', '-c', 'aom', '-s', '7', '-q', '50', '--exif', exif, '--xmp', xmp], {'make': 'PixCodecTest', 'xmpTitle': 'AVIF fixture'})
    icc = os.path.join(TMP, 'p3.icc')
    with open(icc, 'wb') as f: f.write(p3_icc())
    still('icc', nat, ['-y', '420', '-c', 'aom', '-s', '7', '-q', '50', '--icc', icc], {'icc': True})
    # image sequences
    seq = sequence_frames(128, 80, 8)
    still('seq', seq, ['-y', '420', '-c', 'aom', '-s', '8', '-q', '50', '--fps', '25', '-k', '4'], {'animated': True, 'durationMs': 40})
    still('seq_alpha', [with_alpha(f) for f in sequence_frames(96, 64, 5)], ['-y', '420', '-c', 'aom', '-s', '8', '-q', '50', '--fps', '10'], {'animated': True, 'durationMs': 100})
    still('seq_10', [f.astype(np.uint16) * 257 for f in sequence_frames(96, 64, 4)], ['-y', '420', '-c', 'aom', '-s', '8', '-q', '60', '-d', '10'], {'animated': True})
    # layered (progressive) image: two layers, the image is the highest one
    still('progressive', nat, ['-y', '420', '-c', 'aom', '-s', '7', '-q', '60', '--progressive'], {'layered': True})


def sequence_frames(w, h, n):
    big = natural(w * 2, h * 2, 21)
    im = Image.fromarray(big)
    frames = []
    for i in range(n):
        t = i / max(1, n - 1)
        a = math.radians(5 * t)
        sc = 1 + 0.2 * t
        cx, cy = w + 20 * t, h + 10 * t
        ca, sa = math.cos(a) / sc, math.sin(a) / sc
        coeffs = (ca, sa, cx - ca * w / 2 - sa * h / 2, -sa, ca, cy + sa * w / 2 - ca * h / 2)
        frames.append(np.asarray(im.transform((w, h), Image.AFFINE, coeffs, Image.BICUBIC)))
    return frames


XMP = '''<?xpacket begin="" id="W5M0MpCehiHzreSzNTczkc9d"?>
<x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
<rdf:Description rdf:about="" xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title><rdf:Alt><rdf:li xml:lang="x-default">AVIF fixture</rdf:li></rdf:Alt></dc:title></rdf:Description>
</rdf:RDF></x:xmpmeta><?xpacket end="w"?>'''


def exif_blob():
    """A minimal big endian TIFF with Make = PixCodecTest (avifenc stores it after a 4 byte offset)."""
    import struct
    make = b'PixCodecTest\0'
    ifd = struct.pack('>H', 1) + struct.pack('>HHII', 0x010F, 2, len(make), 26) + struct.pack('>I', 0)
    return b'MM\0*' + struct.pack('>I', 8) + ifd + make


def p3_icc():
    sys.path.insert(0, HERE)
    # a Display P3 profile written by LittleCMS through ctypes is overkill: use the tiny one ImageCms makes
    from PIL import ImageCms
    p = ImageCms.createProfile('sRGB')
    return ImageCms.ImageCmsProfile(p).tobytes()


# ------------------------------------------------------------------ AV1 streams (inter tools)

def write_y4m(path, frames, bd=8):
    h, w = frames[0].shape[:2]
    with open(path, 'wb') as f:
        cs = 'C420jpeg' if bd == 8 else f'C420p{bd}'
        f.write(f'YUV4MPEG2 W{w} H{h} F25:1 Ip A1:1 {cs}\n'.encode())
        mx = (1 << bd) - 1
        for fr in frames:
            a = fr.astype(np.float64) / 255
            y = 0.299 * a[..., 0] + 0.587 * a[..., 1] + 0.114 * a[..., 2]
            u = (a[..., 2] - y) / 1.772 + 0.5
            v = (a[..., 0] - y) / 1.402 + 0.5
            u = u.reshape(h // 2, 2, w // 2, 2).mean((1, 3))
            v = v.reshape(h // 2, 2, w // 2, 2).mean((1, 3))
            f.write(b'FRAME\n')
            for p in (y, u, v):
                f.write(np.clip(np.round(p * mx), 0, mx).astype(np.uint16 if bd > 8 else np.uint8).tobytes())


def ivf_case(name, src, args, frames):
    out = os.path.join(OUT_AV1, name + '.ivf')
    run(tool('aomenc'), '-t', '1', '--end-usage=q', f'--limit={frames}', *args, '--ivf', '-o', out, src)
    raw = os.path.join(TMP, 'out.yuv')
    run(tool('dav1d'), '-q', '-i', out, '-o', raw, '--muxer', 'yuv')
    with open(out + '.json', 'w') as f:
        json.dump({'sha256': hashlib.sha256(open(raw, 'rb').read()).hexdigest(), 'bytes': os.path.getsize(raw), 'frames': frames}, f, indent=1)
    print(name, os.path.getsize(out))


def gen_av1():
    os.makedirs(OUT_AV1, exist_ok=True)
    os.makedirs(TMP, exist_ok=True)
    frames = sequence_frames(128, 80, 16)
    y4m = os.path.join(TMP, 'seq.y4m')
    write_y4m(y4m, frames)
    n = 16
    ivf_case('rt', y4m, ['--cpu-used=8', '--rt', '--cq-level=40'], n)
    ivf_case('good', y4m, ['--cpu-used=3', '--cq-level=30'], n)
    ivf_case('altref', y4m, ['--cpu-used=2', '--cq-level=30', '--lag-in-frames=15', '--auto-alt-ref=1'], n)
    ivf_case('warp_global', y4m, ['--cpu-used=1', '--cq-level=25', '--enable-warped-motion=1', '--enable-global-motion=1'], n)
    ivf_case('obmc', y4m, ['--cpu-used=1', '--cq-level=25', '--enable-obmc=1', '--enable-warped-motion=0'], n)
    ivf_case('interintra_masked', y4m, ['--cpu-used=1', '--cq-level=25', '--enable-interintra-comp=1', '--enable-masked-comp=1',
                                        '--enable-smooth-interintra=1', '--enable-interinter-wedge=1', '--enable-interintra-wedge=1', '--lag-in-frames=15'], n)
    ivf_case('dual_filter', y4m, ['--cpu-used=2', '--cq-level=30', '--enable-dual-filter=1'], n)
    ivf_case('dist_wtd', y4m, ['--cpu-used=2', '--cq-level=30', '--enable-dist-wtd-comp=1', '--lag-in-frames=15'], n)
    ivf_case('error_resilient', y4m, ['--cpu-used=4', '--cq-level=30', '--error-resilient=1'], n)
    ivf_case('superres', y4m, ['--cpu-used=4', '--cq-level=30', '--superres-mode=1', '--superres-denominator=12'], n)
    ivf_case('resize', y4m, ['--cpu-used=4', '--cq-level=30', '--resize-mode=1', '--resize-denominator=12'], n)
    ivf_case('filmgrain', y4m, ['--cpu-used=4', '--cq-level=30', '--film-grain-test=1'], n)
    ivf_case('tiles', y4m, ['--cpu-used=4', '--cq-level=30', '--tile-columns=1', '--tile-rows=1'], n)
    ivf_case('sb128', y4m, ['--cpu-used=3', '--cq-level=30', '--sb-size=128'], n)
    y4m10 = os.path.join(TMP, 'seq10.y4m')
    write_y4m(y4m10, frames, 10)
    ivf_case('good_10', y4m10, ['--cpu-used=4', '--cq-level=30', '--bit-depth=10', '--input-bit-depth=10'], n)


def gen_gainmap():
    """An SDR base image with a gain map to a brighter BT.2100 PQ alternate (ISO 21496-1 tone map item)."""
    os.makedirs(TMP, exist_ok=True)
    base = natural(128, 96, 30)
    alt = (np.clip(base.astype(np.float64) / 255 * 1.6, 0, 1) ** 1.2 * 65535).astype(np.uint16)
    bp, ap = os.path.join(TMP, 'gm_base.png'), os.path.join(TMP, 'gm_alt.png')
    save_png(bp, base)
    save_png(ap, alt)
    out = os.path.join(OUT, 'gainmap.avif')
    run(tool('avifgainmaputil'), 'combine', bp, ap, out, '--cicp-alternate', '9/16/9', '--downscaling', '2', '-y', '420', '-q', '60')
    y4m = os.path.join(TMP, 'gm.y4m')
    run(tool('avifgainmaputil'), 'extractgainmap', out, y4m)
    w, h, bd, sub, fr = read_y4m(y4m)
    meta = run(tool('avifgainmaputil'), 'printmetadata', out)
    frac = lambda key: [int(x) for x in meta.split(key)[1].split('fraction:')[1].split(')')[0].split('/')]
    finish('gainmap', out, {'gainMap': {'width': w, 'height': h, 'bitDepth': bd, 'yuv': hashlib.sha256(fr[0]).hexdigest(),
                                        'baseHeadroom': frac('Base headroom'), 'alternateHeadroom': frac('Alternate headroom'),
                                        'alternatePrimaries': 9, 'alternateTransfer': 16}}, 1)


if __name__ == '__main__':
    os.makedirs(OUT, exist_ok=True)
    what = sys.argv[1:] or ['avif', 'av1']
    if 'avif' in what: gen_stills()
    if 'gainmap' in what or 'avif' in what: gen_gainmap()
    if 'av1' in what: gen_av1()
    shutil.rmtree(TMP, ignore_errors=True)
