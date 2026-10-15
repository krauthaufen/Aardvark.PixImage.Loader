"""Fixtures for the small formats and KTX: tests/fixtures/{hdr,pnm,qoi,ico,ktx}/name.ext + name.ext.ref.

Every file is made from our own synthetic images. Reference encoders write most of them (ImageMagick for HDR, PFM, PAM
and ICO, netpbm for PNM, Pillow for ICO, the qoi package for QOI, KTX-Software's `ktx create` for KTX 2); hand built
files cover what the tools do not write (old RLE and the other scan orders of HDR, XYZE, PNM maxvals, KTX 1). The
references are the pixels that went in (lossless formats) or, for lossy storage (RGBE, packed and float formats), the
values the format definition gives for the stored bits, cross checked against the tools' own decoding.

usage: python3 gen_misc.py [--ktx PATH-TO-ktx]
"""
import argparse
import io
import os
import shutil
import struct
import subprocess
import sys
import tempfile
import numpy as np
from PIL import Image
from pxref import write_ref, test_pattern

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'fixtures')
TMP = tempfile.mkdtemp()


def out(sub, name):
    d = os.path.join(ROOT, sub)
    os.makedirs(d, exist_ok=True)
    return os.path.join(d, name)


def save(sub, name, data, ref):
    p = out(sub, name)
    with open(p, 'wb') as f:
        f.write(data)
    write_ref(p + '.ref', ref)
    return p


def check(name, a, b, tol=0, what=''):
    a = np.asarray(a, dtype=np.float64)
    b = np.asarray(b, dtype=np.float64)
    if a.shape != b.shape:
        print(f'  {name}: {what} shape {b.shape} vs {a.shape}')
        return
    d = np.max(np.abs(a - b)) if a.size else 0
    if d > tol:
        print(f'  {name}: {what} differs by {d}')


# ---------------------------------------------------------------- Radiance HDR

def float_pattern(w, h, seed=3):
    rng = np.random.default_rng(seed)
    y, x = np.mgrid[0:h, 0:w].astype(np.float64)
    r = np.exp2((x / w) * 12 - 6) * (1 + 0.2 * np.sin(y / 3))
    g = np.exp2((y / h) * 10 - 4) * (0.5 + 0.5 * ((x // 4 + y // 4) % 2))
    b = rng.random((h, w)) * 3
    a = np.stack([r, g, b], -1)
    a[0, 0] = 0
    a[1, 1] = [1e-30, 0, 0]
    a[2, 2] = [70000, 1, 0.5]
    return a.astype(np.float32)


def to_rgbe(f):
    f = np.maximum(f.astype(np.float64), 0)
    m = f.max(-1)
    out = np.zeros(f.shape[:2] + (4,), np.uint8)
    ok = m >= 1e-32
    mant, e = np.frexp(np.where(ok, m, 1))
    scale = np.where(ok, mant * 256 / np.where(ok, m, 1), 0)
    carry = np.round(m * scale) >= 256
    e = e + carry
    scale = np.where(carry, scale / 2, scale)
    for c in range(3):
        out[..., c] = np.round(f[..., c] * scale).astype(np.uint8)
    out[..., 3] = np.where(ok, e + 128, 0).astype(np.uint8)
    return out


def from_rgbe(q):
    e = q[..., 3].astype(np.int32)
    f = np.where(e == 0, 0.0, np.ldexp(1.0, e - 136))
    return (q[..., :3].astype(np.float64) * f[..., None]).astype(np.float32)


def rle_component(v):
    o = bytearray()
    i, n = 0, len(v)
    while i < n:
        j = i
        while j < n and j - i < 127 and v[j] == v[i]:
            j += 1
        if j - i >= 4:
            o += bytes([128 + j - i, v[i]])
            i = j
            continue
        k = i
        while k < n and k - i < 128:
            r = k
            while r < n and r - k < 4 and v[r] == v[k]:
                r += 1
            if r - k >= 4:
                break
            k += 1
        o += bytes([k - i]) + bytes(v[i:k])
        i = k
    return bytes(o)


def hdr_file(q, res='-Y {h} +X {w}', header=b'', mode='rle', fmt=b'32-bit_rle_rgbe'):
    """q: rgbe (H, W, 4) top-down image; written in the scan order of the resolution string."""
    h, w = q.shape[:2]
    t = res.format(h=h, w=w).split()
    major, minor = t[0], t[2]
    img = q
    # bring the image into scan order: rows of the first axis, pixels along the second
    if major[1] == 'X':
        img = np.transpose(img, (1, 0, 2))           # rows = columns (x), pixels along y top to bottom
        if major[0] == '-':
            img = img[::-1]
        if minor[0] == '+':                           # +Y: bottom to top
            img = img[:, ::-1]
    else:
        if major[0] == '+':
            img = img[::-1]
        if minor[0] == '-':
            img = img[:, ::-1]
    o = bytearray(b'#?RADIANCE\n' + header + b'FORMAT=' + fmt + b'\n\n' + res.format(h=h, w=w).encode() + b'\n')
    for line in img:
        if mode == 'rle':
            o += bytes([2, 2, len(line) >> 8, len(line) & 255])
            for c in range(4):
                o += rle_component(line[:, c].tobytes())
        elif mode == 'flat':
            o += line.tobytes()
        else:   # old RLE: repeats of the previous pixel as 1,1,1,count
            prev = None
            k = 0
            while k < len(line):
                px = line[k]
                o += px.tobytes()
                j = k + 1
                while j < len(line) and (line[j] == px).all() and j - k <= 255:
                    j += 1
                run = j - k - 1
                if run > 0:
                    o += bytes([1, 1, 1, run])
                k = j
    return bytes(o)


def gen_hdr():
    f = float_pattern(41, 23)
    q = to_rgbe(f)
    ref = from_rgbe(q)
    # ImageMagick writes RLE RGBE from a float image
    pfm = os.path.join(TMP, 'f.pfm')
    with open(pfm, 'wb') as fh:
        fh.write(b'PF\n41 23\n-1.0\n' + f[::-1].astype('<f4').tobytes())
    p = out('hdr', 'magick.hdr')
    subprocess.run(['magick', pfm, p], check=True)
    qm = read_hdr_rgbe(open(p, 'rb').read())
    write_ref(p + '.ref', from_rgbe(qm))
    # (this ImageMagick build is not HDRI: it clamps to [0, 1], so only the clamped values are compared)
    m = magick_float(p)
    small = (from_rgbe(qm).max(-1) <= 1)[..., None]
    check('magick.hdr', np.where(small, from_rgbe(qm), 0), np.where(small, m, 0), tol=1e-3, what='magick decode vs ours')
    for name, kw in [('rle.hdr', {}), ('flat.hdr', {'mode': 'flat'}), ('oldrle.hdr', {'mode': 'old'}),
                     ('flip_y.hdr', {'res': '+Y {h} +X {w}'}), ('flip_x.hdr', {'res': '-Y {h} -X {w}'}),
                     ('xmajor.hdr', {'res': '+X {w} -Y {h}'}), ('xmajor2.hdr', {'res': '-X {w} +Y {h}'}),
                     ('header.hdr', {'header': b'# comment\nEXPOSURE=2.0\nEXPOSURE=0.5\nGAMMA=2.2\nPRIMARIES=0.64 0.33 0.29 0.6 0.15 0.06 0.3333 0.3333\nSOFTWARE=gen_misc\n'})]:
        d = hdr_file(q, **kw)
        save('hdr', name, d, ref)
        # ImageMagick reads it back the same way
        m = magick_float(out('hdr', name))
        if m is not None:
            r = ref[::-1] if name == 'flip_y.hdr' else ref   # ImageMagick ignores the +Y (bottom-up) order
            small = (r.max(-1) <= 1)[..., None]
            check(name, np.where(small, r, 0), np.where(small, m, 0), tol=1 / 64, what='magick decode')
    # runs: a flat image with long runs in every component, old RLE with a count above 255 (shifted repeat)
    flat = np.zeros((4, 600, 4), np.uint8)
    flat[..., :3] = 100
    flat[..., 3] = 129
    flat[1, 300:] = [10, 20, 30, 130]
    save('hdr', 'runs.hdr', hdr_file(flat), from_rgbe(flat))
    line = np.tile(np.array([50, 60, 70, 128], np.uint8), (1, 700, 1))
    o = bytearray(b'#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y 1 +X 700\n')
    o += bytes([50, 60, 70, 128])                  # first pixel
    o += bytes([1, 1, 1, 699 & 255])               # 699 = 2 * 256 + 187: repeat 187
    o += bytes([1, 1, 1, 699 >> 8])                # then 2 << 8 = 512
    save('hdr', 'oldrle_shift.hdr', bytes(o), from_rgbe(line))
    # XYZE: X, Y, Z are converted to RGB with Radiance's standard primaries
    xyz = to_rgbe(np.abs(float_pattern(9, 7, seed=5)))
    d = hdr_file(xyz, fmt=b'32-bit_rle_xyze')
    m = rgb_to_xyz(np.array([0.640, 0.330, 0.290, 0.600, 0.150, 0.060, 1 / 3, 1 / 3]))
    rgb = from_rgbe(xyz).astype(np.float64) @ np.linalg.inv(m).T
    save('hdr', 'xyze.hdr', d, rgb.astype(np.float32))


def rgb_to_xyz(p):
    c = np.zeros((3, 3))
    for k in range(3):
        x, y = p[2 * k], p[2 * k + 1]
        c[:, k] = [x / y, 1, (1 - x - y) / y]
    wx, wy = p[6], p[7]
    s = np.linalg.solve(c, [wx / wy, 1, (1 - wx - wy) / wy])
    return c * s


def read_hdr_rgbe(d):
    pos = d.index(b'\n\n') + 2
    e = d.index(b'\n', pos)
    t = d[pos:e].split()
    h, w = int(t[1]), int(t[3])
    pos = e + 1
    img = np.zeros((h, w, 4), np.uint8)
    for y in range(h):
        if d[pos] == 2 and d[pos + 1] == 2:
            pos += 4
            for c in range(4):
                i = 0
                while i < w:
                    n = d[pos]; pos += 1
                    if n > 128:
                        img[y, i:i + n - 128, c] = d[pos]; pos += 1; i += n - 128
                    else:
                        img[y, i:i + n, c] = np.frombuffer(d[pos:pos + n], np.uint8); pos += n; i += n
        else:
            img[y] = np.frombuffer(d[pos:pos + 4 * w], np.uint8).reshape(w, 4); pos += 4 * w
    return img


def magick_float(path):
    try:
        r = subprocess.run(['magick', path, '-depth', '32', '-define', 'quantum:format=floating-point', 'pfm:-'], capture_output=True, check=True)
    except subprocess.CalledProcessError:
        return None
    return read_pfm(r.stdout)


def read_pfm(d):
    toks = []
    pos = 0
    while len(toks) < 4:
        while d[pos:pos + 1].isspace() or d[pos:pos + 1] == b'#':
            if d[pos:pos + 1] == b'#':
                pos = d.index(b'\n', pos)
            pos += 1
        s = pos
        while not d[pos:pos + 1].isspace():
            pos += 1
        toks.append(d[s:pos].decode())
    pos += 1
    ch = 3 if toks[0] == 'PF' else 1
    w, h, scale = int(toks[1]), int(toks[2]), float(toks[3])
    a = np.frombuffer(d[pos:pos + w * h * ch * 4], '<f4' if scale < 0 else '>f4').reshape(h, w, ch)
    return a[::-1].astype(np.float32)


# ---------------------------------------------------------------- Netpbm

def gen_pnm():
    rgb = test_pattern(29, 17, 3)
    gray = test_pattern(29, 17, 1)[..., 0]
    rgba = test_pattern(29, 17, 4)
    for name, im, fmt in [('p5.pgm', gray, 'L'), ('p6.ppm', rgb, 'RGB')]:
        b = io.BytesIO()
        Image.fromarray(im, fmt).save(b, 'PPM')
        save('pnm', name, b.getvalue(), im)
    # netpbm: plain variants, 16 bit, PAM with alpha, bilevel
    def netpbm(cmd, data):
        return subprocess.run(cmd, input=data, capture_output=True, check=True).stdout
    p6 = open(out('pnm', 'p6.ppm'), 'rb').read()
    save('pnm', 'p3.ppm', netpbm(['pnmtoplainpnm'], p6), rgb)
    p5 = open(out('pnm', 'p5.pgm'), 'rb').read()
    save('pnm', 'p2.pgm', netpbm(['pnmtoplainpnm'], p5), gray)
    bits = (gray >= 128)
    pbm = netpbm(['pamthreshold', '-simple', '-threshold=0.5'], p5)   # 1 = black, scaled to 0 / 255
    save('pnm', 'p4.pbm', pbm, np.where(bits, 255, 0).astype(np.uint8))
    save('pnm', 'p1.pbm', netpbm(['pnmtoplainpnm'], pbm), np.where(bits, 255, 0).astype(np.uint8))
    rgb16 = test_pattern(29, 17, 3, np.uint16)
    save('pnm', 'p6_16.ppm', b'P6\n29 17\n65535\n' + rgb16.astype('>u2').tobytes(), rgb16)
    # other maxvals: values scaled to the full range, rounding to nearest
    for mv in (1000, 4095, 100, 1):
        v = np.round(gray.astype(np.float64) / 255 * mv).astype(np.int64)
        if mv > 255:
            data = b'P5\n29 17\n%d\n' % mv + v.astype('>u2').tobytes()
            ref = ((v * 65535 + mv // 2) // mv).astype(np.uint16)
        else:
            data = b'P5\n# a comment\n29 17 %d\n' % mv + v.astype(np.uint8).tobytes()
            ref = ((v * 255 + mv // 2) // mv).astype(np.uint8)
        p = save('pnm', f'maxval{mv}.pgm', data, ref)
        im = np.array(Image.open(p))
        check(f'maxval{mv}.pgm', ref.astype(np.float64) / (65535 if mv > 255 else 255), im.astype(np.float64) / (65535 if im.dtype != np.uint8 else 255),
              tol=1.5 / 255, what='pillow')
    # PAM written by ImageMagick, and hand built ones
    src = os.path.join(TMP, 'rgba.png')
    Image.fromarray(rgba, 'RGBA').save(src)
    p = out('pnm', 'magick_rgba.pam')
    subprocess.run(['magick', src, p], check=True)
    write_ref(p + '.ref', rgba)
    ga = rgba[..., [0, 3]]
    save('pnm', 'graya.pam', b'P7\nWIDTH 29\nHEIGHT 17\nDEPTH 2\nMAXVAL 255\nTUPLTYPE GRAYSCALE_ALPHA\nENDHDR\n' + ga.tobytes(), ga)
    bw = bits.astype(np.uint8)
    save('pnm', 'bw.pam', b'P7\nWIDTH 29\nHEIGHT 17\nDEPTH 1\nMAXVAL 1\nTUPLTYPE BLACKANDWHITE\nENDHDR\n' + bw.tobytes(), (bw * 255).astype(np.uint8))
    five = test_pattern(29, 17, 5)
    save('pnm', 'depth5.pam', b'P7\nWIDTH 29\nHEIGHT 17\nDEPTH 5\nMAXVAL 255\nTUPLTYPE MULTISPECTRAL\nENDHDR\n' + five.tobytes(), five)
    # PFM: ImageMagick (little endian) and big endian by hand; rows bottom to top
    f = float_pattern(29, 17)
    pf = os.path.join(TMP, 'f.exr')
    p = out('pnm', 'magick.pfm')
    raw = b'PF\n29 17\n-1.0\n' + f[::-1].astype('<f4').tobytes()
    open(os.path.join(TMP, 'in.pfm'), 'wb').write(raw)
    subprocess.run(['magick', os.path.join(TMP, 'in.pfm'), p], check=True)
    # (ImageMagick without HDRI clamps to [0, 1]; the file holds what it wrote)
    write_ref(p + '.ref', read_pfm(open(p, 'rb').read()))
    check('magick.pfm', np.clip(f, 0, 1), read_pfm(open(p, 'rb').read()), tol=1e-4, what='magick round trip')
    save('pnm', 'be.pfm', b'PF\n29 17\n1.0\n' + f[::-1].astype('>f4').tobytes(), f)
    g = f[..., 1:2]
    save('pnm', 'gray.pfm', b'Pf\n29 17\n-1.0\n' + g[::-1].astype('<f4').tobytes(), g)


# ---------------------------------------------------------------- QOI

def gen_qoi():
    import qoi
    for name, im in [('rgb.qoi', test_pattern(37, 19, 3)), ('rgba.qoi', test_pattern(37, 19, 4)),
                     ('runs.qoi', np.repeat(np.repeat(test_pattern(5, 3, 4), 16, 0), 30, 1))]:
        d = qoi.encode(np.ascontiguousarray(im))
        p = save('qoi', name, d, im)
        check(name, im, qoi.decode(d), what='qoi round trip')
    # a linear colour space header
    d = bytearray(qoi.encode(np.ascontiguousarray(test_pattern(8, 8, 3))))
    d[13] = 1
    save('qoi', 'linear.qoi', bytes(d), test_pattern(8, 8, 3))


# ---------------------------------------------------------------- ICO / CUR

def gen_ico():
    base = test_pattern(64, 64, 4)
    base[..., 3] = np.where((np.mgrid[0:64, 0:64][0] + np.mgrid[0:64, 0:64][1]) % 9 < 3, 0, 255)
    src = os.path.join(TMP, 'icon.png')
    Image.fromarray(base, 'RGBA').save(src)
    # ImageMagick: BMP entries (32 bit with mask), PNG for 256
    p = out('ico', 'magick.ico')
    subprocess.run(['magick', src, '-define', 'icon:auto-resize=64,48,32,16', p], check=True)
    ref_entries('magick.ico', p)
    # Pillow
    p = out('ico', 'pillow.ico')
    Image.fromarray(base, 'RGBA').save(p, sizes=[(16, 16), (32, 32), (64, 64)])
    ref_entries('pillow.ico', p)
    # low colour depths with the AND mask doing the transparency: ImageMagick with a palette image
    p = out('ico', 'palette.ico')
    subprocess.run(['magick', src, '-colors', '16', '-define', 'icon:auto-resize=32', p], check=True)
    ref_entries('palette.ico', p)
    p = out('ico', 'magick.cur')
    subprocess.run(['magick', src, '-resize', '32x32', 'cur:' + p], check=True)
    ref_entries('magick.cur', p)


def ref_entries(name, path):
    """One reference per directory entry, decoded by Pillow (which reads every entry of the directory)."""
    from PIL import IcoImagePlugin, CurImagePlugin
    with open(path, 'rb') as f:
        data = f.read()
    n = struct.unpack('<H', data[4:6])[0]
    for i in range(n):
        e = 6 + 16 * i
        size, off = struct.unpack('<II', data[e + 8:e + 16])
        blob = data[off:off + size]
        # a single entry icon around this blob, which Pillow decodes on its own
        one = bytearray(data[:6]); one[4:6] = struct.pack('<H', 1)
        ent = bytearray(data[e:e + 16]); ent[12:16] = struct.pack('<I', 22)
        im = Image.open(io.BytesIO(bytes(one + ent + blob)))
        im.load()
        write_ref(path + f'.{i}.ref', np.array(im.convert('RGBA')))


# ---------------------------------------------------------------- KTX

def gen_ktx(ktx):
    def create(name, fmt, raw, w, h, extra=(), ref=None):
        # raw: the bytes of one image, or a list (one input file per image: level, layer, face, depth slice)
        rps = []
        for i, r in enumerate(raw if isinstance(raw, list) else [raw]):
            rp = os.path.join(TMP, f'{name}.{i}.raw')
            open(rp, 'wb').write(r)
            rps.append(rp)
        p = out('ktx', name)
        cmd = [ktx, 'create', '--raw', '--width', str(w), '--height', str(h), '--format', fmt, '--testrun', *extra, *rps, p]
        r = subprocess.run(cmd, capture_output=True, text=True)
        if r.returncode != 0:
            print(name, r.stderr.strip()[:300])
            return None
        if ref is not None:
            write_ref(p + '.ref', ref)
        return p

    rgba = test_pattern(23, 13, 4)
    rgb = rgba[..., :3]
    create('rgba8.ktx2', 'R8G8B8A8_UNORM', rgba.tobytes(), 23, 13, ref=rgba)
    create('rgba8_srgb_zstd.ktx2', 'R8G8B8A8_SRGB', rgba.tobytes(), 23, 13, ['--zstd', '5'], ref=rgba)
    create('rgba8_zlib.ktx2', 'R8G8B8A8_UNORM', rgba.tobytes(), 23, 13, ['--zlib', '5'], ref=rgba)
    create('rgb8.ktx2', 'R8G8B8_UNORM', rgb.tobytes(), 23, 13, ref=rgb)
    create('bgra8.ktx2', 'B8G8R8A8_UNORM', rgba[..., [2, 1, 0, 3]].tobytes(), 23, 13, ref=rgba)
    create('bgr8_srgb.ktx2', 'B8G8R8_SRGB', rgb[..., ::-1].tobytes(), 23, 13, ref=rgb)
    create('r8.ktx2', 'R8_UNORM', rgba[..., 0].tobytes(), 23, 13, ref=rgba[..., :1])
    create('rg8.ktx2', 'R8G8_UNORM', rgba[..., :2].tobytes(), 23, 13, ref=rgba[..., :2])
    s8 = test_pattern(23, 13, 4, np.int8)
    create('rgba8_snorm.ktx2', 'R8G8B8A8_SNORM', s8.tobytes(), 23, 13, ref=s8)
    u16 = test_pattern(23, 13, 4, np.uint16)
    create('rgba16.ktx2', 'R16G16B16A16_UNORM', u16.astype('<u2').tobytes(), 23, 13, ref=u16)
    create('r16.ktx2', 'R16_UNORM', u16[..., 0].astype('<u2').tobytes(), 23, 13, ref=u16[..., :1])
    h16 = test_pattern(23, 13, 4, np.float16)
    create('rgba16f.ktx2', 'R16G16B16A16_SFLOAT', h16.astype('<f2').tobytes(), 23, 13, ref=h16)
    create('rg16f.ktx2', 'R16G16_SFLOAT', h16[..., :2].astype('<f2').tobytes(), 23, 13, ref=h16[..., :2])
    f32 = test_pattern(23, 13, 4, np.float32)
    create('rgba32f.ktx2', 'R32G32B32A32_SFLOAT', f32.astype('<f4').tobytes(), 23, 13, ref=f32)
    create('rgb32f.ktx2', 'R32G32B32_SFLOAT', f32[..., :3].astype('<f4').tobytes(), 23, 13, ref=f32[..., :3])
    create('r32f.ktx2', 'R32_SFLOAT', f32[..., 0].astype('<f4').tobytes(), 23, 13, ref=f32[..., :1])
    u32 = test_pattern(23, 13, 1, np.uint32)
    create('r32ui.ktx2', 'R32_UINT', u32.astype('<u4').tobytes(), 23, 13, ref=u32)
    # packed formats: references follow the format definitions (UNORM fields scaled to 8 / 16 bits rounding to nearest)
    def scale(v, bits, out_bits):
        mx = (1 << bits) - 1
        return (v.astype(np.int64) * ((1 << out_bits) - 1) + mx // 2) // mx
    r5 = (rgb[..., 0] >> 3).astype(np.uint16); g6 = (rgb[..., 1] >> 2).astype(np.uint16); b5 = (rgb[..., 2] >> 3).astype(np.uint16)
    create('r5g6b5.ktx2', 'R5G6B5_UNORM_PACK16', ((r5 << 11) | (g6 << 5) | b5).astype('<u2').tobytes(), 23, 13,
           ref=np.stack([scale(r5, 5, 8), scale(g6, 6, 8), scale(b5, 5, 8)], -1).astype(np.uint8))
    c4 = (rgba >> 4).astype(np.uint16)
    create('rgba4.ktx2', 'R4G4B4A4_UNORM_PACK16', ((c4[..., 0] << 12) | (c4[..., 1] << 8) | (c4[..., 2] << 4) | c4[..., 3]).astype('<u2').tobytes(), 23, 13,
           ref=scale(c4, 4, 8).astype(np.uint8))
    a1 = (rgba[..., 3] >> 7).astype(np.uint16)
    c5 = (rgb >> 3).astype(np.uint16)
    create('a1rgb5.ktx2', 'A1R5G5B5_UNORM_PACK16', ((a1 << 15) | (c5[..., 0] << 10) | (c5[..., 1] << 5) | c5[..., 2]).astype('<u2').tobytes(), 23, 13,
           ref=np.concatenate([scale(c5, 5, 8), scale(a1, 1, 8)[..., None]], -1).astype(np.uint8))
    c10 = (u16 >> 6).astype(np.uint32)
    word = (c10[..., 3] >> 8 << 30) | (c10[..., 2] << 20) | (c10[..., 1] << 10) | c10[..., 0]
    create('a2bgr10.ktx2', 'A2B10G10R10_UNORM_PACK32', word.astype('<u4').tobytes(), 23, 13,
           ref=np.concatenate([scale(c10[..., :3], 10, 16), scale(c10[..., 3] >> 8, 2, 16)[..., None]], -1).astype(np.uint16))
    # structure: mip levels (raw data holds every level, largest first), cube map, array, 3D, 1D
    chain = [test_pattern(max(1, 16 >> m), max(1, 8 >> m), 4, seed=m + 1) for m in range(5)]
    p = create('mips.ktx2', 'R8G8B8A8_UNORM', [c.tobytes() for c in chain], 16, 8, ['--levels', '5'])
    for m, c in enumerate(chain):
        write_ref(p + f'.mip{m}.ref', c)
    faces = [test_pattern(8, 8, 4, seed=10 + i) for i in range(6)]
    p = create('cube.ktx2', 'R8G8B8A8_SRGB', [f.tobytes() for f in faces], 8, 8, ['--cubemap'])
    for i, f in enumerate(faces):
        write_ref(p + f'.face{i}.ref', f)
    layers = [test_pattern(9, 5, 3, seed=20 + i) for i in range(3)]
    p = create('array.ktx2', 'R8G8B8_UNORM', [l.tobytes() for l in layers], 9, 5, ['--layers', '3'])
    for i, l in enumerate(layers):
        write_ref(p + f'.layer{i}.ref', l)
    slices = [test_pattern(7, 6, 1, np.uint16, seed=30 + i) for i in range(4)]
    p = create('volume.ktx2', 'R16_UNORM', [s.astype('<u2').tobytes() for s in slices], 7, 6, ['--depth', '4', '--zstd', '3'])
    for i, s in enumerate(slices):
        write_ref(p + f'.slice{i}.ref', s)
    line = test_pattern(31, 1, 4)
    create('line.ktx2', 'R8G8B8A8_UNORM', line.tobytes(), 31, 1, ['--1d'], ref=line)
    # ASTC (encoded by ktx's built in encoder); the reference is ktx extract's decoding
    for fmt, name in [('ASTC_4x4_UNORM_BLOCK', 'astc4x4.ktx2'), ('ASTC_6x5_SRGB_BLOCK', 'astc6x5_srgb.ktx2'), ('ASTC_12x12_UNORM_BLOCK', 'astc12x12.ktx2')]:
        png_in = os.path.join(TMP, name + '.in.png')
        Image.fromarray(rgba, 'RGBA').save(png_in)
        p = out('ktx', name)
        r = subprocess.run([ktx, 'create', '--format', fmt, '--testrun', '--assign-tf', 'srgb' if 'SRGB' in fmt else 'linear', png_in, p], capture_output=True, text=True)
        if r.returncode != 0:
            print(name, r.stderr.strip()[:300])
        else:
            png = os.path.join(TMP, name + '.png')
            r = subprocess.run([ktx, 'extract', p, png], capture_output=True, text=True)
            if r.returncode == 0:
                write_ref(p + '.ref', np.array(Image.open(png).convert('RGBA')))
            else:
                print(name, 'extract:', r.stderr.strip()[:200])
    # ETC2 / EAC: random blocks are valid blocks of every mode; the test decodes the .blocks file block by block
    rng = np.random.default_rng(7)
    for fmt, bb in [('ETC2_R8G8B8_UNORM_BLOCK', 8), ('ETC2_R8G8B8A1_SRGB_BLOCK', 8), ('ETC2_R8G8B8A8_UNORM_BLOCK', 16),
                    ('EAC_R11_UNORM_BLOCK', 8), ('EAC_R11_SNORM_BLOCK', 8), ('EAC_R11G11_UNORM_BLOCK', 16), ('EAC_R11G11_SNORM_BLOCK', 16)]:
        blocks = rng.integers(0, 256, (4 * 3 * bb,), dtype=np.uint8).tobytes()   # 13 x 10 pixels: 4 x 3 blocks
        name = fmt.lower().replace('_block', '') + '.ktx2'
        p = create(name, fmt, blocks, 13, 10)
        if p:
            open(p + '.blocks', 'wb').write(blocks)
    gen_ktx1(ktx)


def ktx1(gl_type, type_size, gl_format, internal, base, w, h, depth, elems, faces, levels, kv, images, big=False, pad_rows=True):
    """images[level][elem][face][slice]: (rows) arrays of bytes per row; writes the KTX 1.1 layout."""
    e = '>' if big else '<'
    head = bytes([0xAB, 0x4B, 0x54, 0x58, 0x20, 0x31, 0x31, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A])
    kvd = bytearray()
    for k, v in kv:
        pair = k.encode() + b'\0' + v
        kvd += struct.pack(e + 'I', len(pair)) + pair
        while len(kvd) % 4:
            kvd += b'\0'
    o = bytearray(head + struct.pack(e + '13I', 0x04030201, gl_type, type_size, gl_format, internal, base, w, h, depth, elems, faces, levels, len(kvd)) + kvd)
    for lv in images:
        body = bytearray()
        face_size = None
        for el in lv:
            for fc in el:
                start = len(body)
                for sl in fc:
                    for row in sl:
                        body += row
                        while pad_rows and len(body) % 4:
                            body += b'\0'
                face_size = len(body) - start
                while len(body) % 4:
                    body += b'\0'
        size = face_size if (faces == 6 and elems == 0) else len(body)
        o += struct.pack(e + 'I', size) + body
        while len(o) % 4:
            o += b'\0'
    return bytes(o)


def gen_ktx1(ktx):
    GL_UBYTE, GL_HALF, GL_RGBA, GL_RGB = 0x1401, 0x140B, 0x1908, 0x1907
    rgb = test_pattern(23, 13, 3)        # odd width: rows of 69 bytes padded to 72
    rows = lambda a: [r.tobytes() for r in a]
    def emit(name, data, refs, convert=True):
        p = out('ktx', name)
        open(p, 'wb').write(data)
        for suffix, r in refs:
            write_ref(p + suffix + '.ref', r)
        if not convert:
            return
        # KTX-Software converts it to KTX 2: the file is well formed
        r = subprocess.run([ktx, 'convert', '--input-type', 'ktx', p, os.path.join(TMP, name + '.ktx2')], capture_output=True, text=True)
        if r.returncode != 0:
            print(name, 'ktx convert:', r.stderr.strip()[:300])
    emit('k1_rgb8.ktx', ktx1(GL_UBYTE, 1, GL_RGB, 0x8051, GL_RGB, 23, 13, 0, 0, 1, 1, [('KTXorientation', b'S=r,T=d\0')], [[[[rows(rgb)]]]]), [('', rgb)])
    emit('k1_rgb8_be.ktx', ktx1(GL_UBYTE, 1, GL_RGB, 0x8051, GL_RGB, 23, 13, 0, 0, 1, 1, [], [[[[rows(rgb)]]]], big=True), [('', rgb)])
    up = ktx1(GL_UBYTE, 1, GL_RGB, 0x8051, GL_RGB, 23, 13, 0, 0, 1, 1, [('KTXorientation', b'S=r,T=u\0')], [[[[rows(rgb[::-1])]]]])
    emit('k1_bottomup.ktx', up, [('', rgb)])
    h = test_pattern(6, 5, 4, np.float16)
    emit('k1_rgba16f_be.ktx', ktx1(GL_HALF, 2, GL_RGBA, 0x881A, GL_RGBA, 6, 5, 0, 0, 1, 1, [],
                                   [[[[[r.astype('>f2').tobytes() for r in h]]]]], big=True), [('', h)])
    chain = [test_pattern(max(1, 8 >> m), max(1, 4 >> m), 4, seed=m) for m in range(4)]
    emit('k1_mips.ktx', ktx1(GL_UBYTE, 1, GL_RGBA, 0x8058, GL_RGBA, 8, 4, 0, 0, 1, 4, [], [[[[rows(c)]]] for c in chain]),
         [(f'.mip{m}', c) for m, c in enumerate(chain)])
    faces = [test_pattern(5, 5, 3, seed=40 + i) for i in range(6)]
    emit('k1_cube.ktx', ktx1(GL_UBYTE, 1, GL_RGB, 0x8051, GL_RGB, 5, 5, 0, 0, 6, 1, [], [[[[rows(f)] for f in faces]]]),
         [(f'.face{i}', f) for i, f in enumerate(faces)])
    layers = [test_pattern(5, 3, 4, seed=50 + i) for i in range(2)]
    emit('k1_array.ktx', ktx1(GL_UBYTE, 1, GL_RGBA, 0x8058, GL_RGBA, 5, 3, 0, 2, 1, 1, [], [[[[rows(l)]] for l in layers]]),
         [(f'.layer{i}', l) for i, l in enumerate(layers)])
    # unsized internal format (GL_RGBA + GL_UNSIGNED_BYTE), as older writers produced; libktx rejects these, we read them
    rgba = test_pattern(7, 3, 4)
    emit('k1_unsized.ktx', ktx1(GL_UBYTE, 1, GL_RGBA, GL_RGBA, GL_RGBA, 7, 3, 0, 0, 1, 1, [], [[[[rows(rgba)]]]]), [('', rgba)], convert=False)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--ktx', default=shutil.which('ktx'))
    ap.add_argument('--only')
    a = ap.parse_args()
    steps = {'hdr': gen_hdr, 'pnm': gen_pnm, 'qoi': gen_qoi, 'ico': gen_ico}
    for k, f in steps.items():
        if not a.only or a.only == k:
            print('==', k); f()
    if a.ktx and (not a.only or a.only == 'ktx'):
        print('== ktx'); gen_ktx(a.ktx)
    elif not a.ktx:
        print('ktx tool not found (--ktx): KTX fixtures skipped')


if __name__ == '__main__':
    main()
