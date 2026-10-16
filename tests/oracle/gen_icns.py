"""Generates tests/fixtures/icns: Apple icon files from our own synthetic images, with one reference per image element
(name.icns.<i>.ref, in file order, as RGBA8; PNG / JPEG 2000 elements as their own decoders give them).

  legacy.icns   hand built from Apple's element descriptions: every 1, 4 and 8 bit type (ICON, ICN#, ics#, icm#, ich#,
                icl4 / ics4 / icm4 / ich4, icl8 / ics8 / icm8 / ich8) and the 24 bit RLE types (is32, il32, ih32, it32) with
                their 8 bit masks. References: the 24 bit types as Pillow decodes them; the palette types from the Mac OS
                system colour tables ('clut' 4 and 8 as documented in Inside Macintosh: Imaging With QuickDraw; the 8 bit
                table agrees with what macOS's sips makes of an icl8 element), masks as described below.
  masks.icns    colour elements without their 8 bit mask: alpha from the 1 bit mask of the same size, or opaque.
  pillow.icns   written by Pillow from a 256 x 256 image (PNG elements); references by Pillow.
  jp2.icns      a JPEG 2000 ic08 element (made by Pillow / OpenJPEG); reference by Pillow.
  mac.icns      written by macOS iconutil (ARGB ic04 / ic05 and PNG elements) when $ICNS_MAC_HOST names a Mac to ssh to;
                references: the PNG images that went in for the PNG elements (stored losslessly), macOS's own decode
                (sips) for the ARGB elements, which iconutil stores lossily.

usage: [ICNS_MAC_HOST=host] python3 gen_icns.py
"""
import io
import os
import struct
import subprocess
import tempfile
import numpy as np
from PIL import Image
from pxref import write_ref

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'fixtures', 'icns')
MAC = os.environ.get('ICNS_MAC_HOST')
TMP = tempfile.mkdtemp()

CLUT4 = [0xFFFF, 0xFFFF, 0xFFFF, 0xFC00, 0xF37D, 0x052F, 0xFFFF, 0x648A, 0x028C, 0xDD6B, 0x08C2, 0x06A2,
         0xF2D7, 0x0856, 0x84EC, 0x46E3, 0x0000, 0xA53E, 0x0000, 0x0000, 0xD400, 0x0241, 0xAB54, 0xEAFF,
         0x1F21, 0xB793, 0x1431, 0x0000, 0x64AF, 0x11B0, 0x5600, 0x2C9D, 0x0524, 0x90D7, 0x7160, 0x3A34,
         0xC000, 0xC000, 0xC000, 0x8000, 0x8000, 0x8000, 0x4000, 0x4000, 0x4000, 0x0000, 0x0000, 0x0000]
PAL4 = np.array([(v + 128) // 257 for v in CLUT4], np.uint8).reshape(16, 3)


def pal8():
    p = []
    for r in range(6):
        for g in range(6):
            for b in range(6):
                if (r, g, b) != (5, 5, 5):
                    p.append((255 - 51 * r, 255 - 51 * g, 255 - 51 * b))
    ramp = [0xEE, 0xDD, 0xBB, 0xAA, 0x88, 0x77, 0x55, 0x44, 0x22, 0x11]
    for c in range(4):
        for v in ramp:
            p.append((v if c in (0, 3) else 0, v if c in (1, 3) else 0, v if c in (2, 3) else 0))
    p.append((0, 0, 0))
    return np.array(p, np.uint8)


PAL8 = pal8()


def element(t, data):
    return t + struct.pack('>I', len(data) + 8) + data


def icns(elements):
    body = b''.join(elements)
    return b'icns' + struct.pack('>I', len(body) + 8) + body


def pack(plane):
    """The ICNS run length code: literal runs (n - 1, then n bytes) and repeats (n + 125, then the byte), n >= 3."""
    o = bytearray()
    i, n = 0, len(plane)
    while i < n:
        run = 1
        while i + run < n and run < 130 and plane[i + run] == plane[i]:
            run += 1
        if run >= 3:
            o += bytes([run + 125, plane[i]])
            i += run
            continue
        start = i
        while i < n and i - start < 128 and not (i + 2 < n and plane[i] == plane[i + 1] == plane[i + 2]):
            i += 1
        o += bytes([i - start - 1]) + bytes(plane[start:i])
    return bytes(o)


def pattern(w, h, seed):
    rng = np.random.default_rng(seed)
    y, x = np.mgrid[0:h, 0:w].astype(float)
    a = np.zeros((h, w, 4))
    a[..., 0] = 0.5 + 0.5 * np.sin(x / 3 + seed)
    a[..., 1] = 0.5 + 0.5 * np.cos(y / 4)
    a[..., 2] = ((x // 4 + y // 4) % 2) * 0.8
    a[..., 2][rng.random((h, w)) < 0.1] = 1
    a[..., 3] = np.clip(1.3 - (x + y) / (w + h) * 1.5, 0, 1)
    v = (a * 255 + 0.5).astype(np.uint8)
    v[: h // 4, : w // 4] = v[0, 0]   # a flat corner: runs for the RLE
    return v


def bits(mask):
    """Rows of 1 bit pixels, most significant bit first."""
    h, w = mask.shape
    return np.packbits(mask.astype(np.uint8), axis=1).tobytes()


def mono(w, h, seed, with_mask=True):
    rng = np.random.default_rng(seed)
    y, x = np.mgrid[0:h, 0:w]
    black = ((x + y) % 5 == 0) | (rng.random((h, w)) < 0.2)
    m = ((x - w / 2) ** 2 + (y - h / 2) ** 2) < (w / 2) ** 2
    data = bits(black) + (bits(m) if with_mask else b'')
    ref = np.zeros((h, w, 4), np.uint8)
    ref[..., :3] = np.where(black, 0, 255)[..., None]
    ref[..., 3] = np.where(m, 255, 0) if with_mask else 255
    return data, ref, m


def indexed(w, h, seed, depth):
    rng = np.random.default_rng(seed)
    idx = rng.integers(0, 1 << depth, (h, w)).astype(np.uint8)
    idx[: h // 2, : w // 3] = (seed * 7) % (1 << depth)
    if depth == 4:
        data = (idx[:, 0::2] << 4 | idx[:, 1::2]).tobytes()
        rgb = PAL4[idx]
    else:
        data = idx.tobytes()
        rgb = PAL8[idx]
    return data, rgb


def rle24(img, t):
    planes = b''.join(pack(img[..., c].ravel().tobytes()) for c in range(3))
    return (b'\0\0\0\0' if t == b'it32' else b'') + planes


def pillow_rgba(data, size):
    im = Image.open(io.BytesIO(data))
    im.size = size[:2]
    im.load(scale=size[2])
    return np.asarray(im.convert('RGBA'))


def legacy():
    els, refs = [], []

    def add(t, data, ref):
        els.append(element(t, data))
        if ref is not None:
            refs.append(ref)

    # 1 bit icons with masks (their own alpha)
    for t, (w, h), seed in [(b'ICN#', (32, 32), 1), (b'ics#', (16, 16), 2), (b'icm#', (16, 12), 3), (b'ich#', (48, 48), 4)]:
        d, r, _ = mono(w, h, seed)
        add(t, d, r)
    d, r, _ = mono(32, 32, 5, with_mask=False)
    add(b'ICON', d, r)
    # 8 bit masks (alpha of the palette and RLE elements of that size)
    masks = {}
    for t, s in [(b's8mk', 16), (b'l8mk', 32), (b'h8mk', 48), (b't8mk', 128)]:
        m = pattern(s, s, s)[..., 3]
        masks[s] = m
        add(t, m.tobytes(), None)
    one_bit = {(32, 32): mono(32, 32, 1)[2], (16, 16): mono(16, 16, 2)[2], (16, 12): mono(16, 12, 3)[2], (48, 48): mono(48, 48, 4)[2]}

    def alpha(w, h):
        if w == h and w in masks:
            return masks[w]
        return np.where(one_bit[(w, h)], 255, 0).astype(np.uint8)

    for t, (w, h), depth in [(b'icl4', (32, 32), 4), (b'ics4', (16, 16), 4), (b'icm4', (16, 12), 4), (b'ich4', (48, 48), 4),
                             (b'icl8', (32, 32), 8), (b'ics8', (16, 16), 8), (b'icm8', (16, 12), 8), (b'ich8', (48, 48), 8)]:
        d, rgb = indexed(w, h, w + depth, depth)
        add(t, d, np.dstack([rgb, alpha(w, h)]))
    rle = []
    for t, s in [(b'is32', 16), (b'il32', 32), (b'ih32', 48), (b'it32', 128)]:
        img = pattern(s, s, s)
        add(t, rle24(img, t), None)
        refs.append(None)
        rle.append((len(refs) - 1, s, img))
    data = icns(els)
    # the RLE elements as Pillow decodes them (with their 8 bit masks)
    for k, s, img in rle:
        ref = pillow_rgba(data, (s, s, 1))
        assert np.array_equal(ref[..., :3], img[..., :3]) and np.array_equal(ref[..., 3], masks[s])
        refs[k] = ref
    return data, refs


def masks_file():
    """il32 with only the 1 bit mask of ICN#, icl8 with neither (opaque)."""
    els, refs = [], []
    d, r, m = mono(32, 32, 9)
    els.append(element(b'ICN#', d)); refs.append(r)
    img = pattern(32, 32, 11)
    els.append(element(b'il32', rle24(img, b'il32'))); refs.append(np.dstack([img[..., :3], np.where(m, 255, 0).astype(np.uint8)]))
    i8, rgb = indexed(16, 16, 12, 8)
    els.append(element(b'ics8', i8)); refs.append(np.dstack([rgb, np.full((16, 16), 255, np.uint8)]))
    els.append(element(b'TOC ', b'il32' + struct.pack('>I', 8)))
    els.append(element(b'icnV', struct.pack('>f', 1.0)))
    return icns(els), refs


def write(name, data, refs):
    p = os.path.join(OUT, name)
    with open(p, 'wb') as f:
        f.write(data)
    for i, r in enumerate(refs):
        write_ref(p + f'.{i}.ref', r)


def main():
    os.makedirs(OUT, exist_ok=True)
    write('legacy.icns', *legacy())
    write('masks.icns', *masks_file())
    # Pillow: PNG elements from one 1024 x 1024 image
    big = pattern(256, 256, 21)
    bio = io.BytesIO()
    Image.fromarray(big).save(bio, format='ICNS')
    data = bio.getvalue()
    sizes = {b'ic07': (128, 128, 1), b'ic08': (256, 256, 1), b'ic09': (512, 512, 1), b'ic10': (512, 512, 2), b'ic11': (16, 16, 2),
             b'ic12': (32, 32, 2), b'ic13': (128, 128, 2), b'ic14': (256, 256, 2)}
    # Pillow writes every size (scaling up); the elements of up to 256 pixels are kept (its table of contents is dropped)
    kept, order = [], []
    p = 8
    while p < len(data):
        t, l = struct.unpack('>4sI', data[p:p + 8])
        if t in sizes and sizes[t][0] * sizes[t][2] <= 256:
            kept.append(data[p:p + l])
            order.append(t)
        p += l
    data = b'icns' + struct.pack('>I', 8 + sum(len(k) for k in kept)) + b''.join(kept)
    refs = [pillow_rgba(data, sizes[t]) for t in order if t in sizes]
    assert max(r.shape[0] for r in refs) <= 256
    write('pillow.icns', data, refs)
    # JPEG 2000 element
    j = pattern(256, 256, 22)
    bio = io.BytesIO()
    Image.fromarray(j).save(bio, format='JPEG2000', irreversible=False)
    jp = bio.getvalue()
    data = icns([element(b'ic08', jp)])
    write('jp2.icns', data, [np.asarray(Image.open(io.BytesIO(jp)).convert('RGBA'))])
    if MAC:
        mac()
    print('wrote', OUT)


def mac():
    names = [(16, 'icon_16x16'), (32, 'icon_16x16@2x'), (32, 'icon_32x32'), (64, 'icon_32x32@2x'), (128, 'icon_128x128'),
             (256, 'icon_128x128@2x'), (256, 'icon_256x256')]
    d = os.path.join(TMP, 't.iconset')
    os.makedirs(d)
    imgs = {}
    for k, (s, n) in enumerate(names):
        imgs[n] = pattern(s, s, 30 + k)
        Image.fromarray(imgs[n]).save(os.path.join(d, n + '.png'))
    subprocess.run(['ssh', MAC, 'rm -rf /tmp/pixcodec-icns && mkdir /tmp/pixcodec-icns'], check=True)
    subprocess.run(['scp', '-qr', d, f'{MAC}:/tmp/pixcodec-icns/'], check=True)
    subprocess.run(['ssh', MAC, 'cd /tmp/pixcodec-icns && iconutil -c icns t.iconset -o t.icns'], check=True)
    out = os.path.join(TMP, 'mac.icns')
    subprocess.run(['scp', '-q', f'{MAC}:/tmp/pixcodec-icns/t.icns', out], check=True)
    data = open(out, 'rb').read()
    by_type = {b'ic04': 'icon_16x16', b'ic11': 'icon_16x16@2x', b'ic05': 'icon_32x32', b'ic12': 'icon_32x32@2x',
               b'ic07': 'icon_128x128', b'ic13': 'icon_128x128@2x', b'ic08': 'icon_256x256', b'ic14': 'icon_256x256@2x',
               b'ic09': 'icon_512x512', b'ic10': 'icon_512x512@2x'}
    refs = []
    p = 8
    while p < len(data):
        t, l = struct.unpack('>4sI', data[p:p + 8])
        if t in (b'ic04', b'ic05'):
            # iconutil stores ARGB elements lossily (premultiplied on the way, transparent pixels black): the reference is
            # macOS's own decode of the element alone (sips)
            one = os.path.join(TMP, t.decode() + '.icns')
            with open(one, 'wb') as f:
                f.write(icns([data[p:p + l]]))
            subprocess.run(['scp', '-q', one, f'{MAC}:/tmp/pixcodec-icns/one.icns'], check=True)
            subprocess.run(['ssh', MAC, 'cd /tmp/pixcodec-icns && sips -s format png one.icns --out one.png >/dev/null'], check=True)
            png = os.path.join(TMP, 'one.png')
            subprocess.run(['scp', '-q', f'{MAC}:/tmp/pixcodec-icns/one.png', png], check=True)
            refs.append(np.asarray(Image.open(png).convert('RGBA')))
        elif t in by_type:
            refs.append(imgs[by_type[t]])
        p += l
    write('mac.icns', data, refs)


if __name__ == '__main__':
    main()
