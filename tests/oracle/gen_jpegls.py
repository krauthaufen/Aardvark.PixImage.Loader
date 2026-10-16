"""JPEG-LS fixtures: tests/fixtures/jpegls/*.jls + .jls.ref, and encoder expectations.

Decoder fixtures are written by CharLS (through charls.py: all interleave modes, NEAR, bit depths 2..16, preset coding
parameters, HP colour transforms, SPIFF, oversize dimensions) from our own synthetic images. References are the input
pixels for lossless files and CharLS's decode for near-lossless ones. Features CharLS does not write are built from
CharLS coded scans following T.87: restart intervals (independently coded strips joined with RSTm markers), subsampled
components and per-scan NEAR / preset parameters (separately coded components joined as the scans of one frame),
mapping tables (an LSE 2 / 3 segment and the table selector put into a coded index image), DNL, EXIF orientation.

Encoder expectations: enc_*.ref inputs with the bytes CharLS writes for the parameters listed in encode_cases.json; our
encoder has to reproduce them exactly.

Note: CharLS 2.4 does not follow T.87 for MAXVAL below 2^P - 1 (it keeps RANGE and the prediction clamp at 2^P), so
such files are not made here.

usage: python3 gen_jpegls.py
"""
import json
import os
import struct
import numpy as np
import charls
from pxref import write_ref, test_pattern

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'fixtures', 'jpegls')


def save(name, data, ref):
    os.makedirs(ROOT, exist_ok=True)
    p = os.path.join(ROOT, name)
    with open(p, 'wb') as f:
        f.write(data)
    if ref is not None:
        write_ref(p + '.ref', ref)


def image(w, h, c, bits, kind='pattern', seed=1):
    dt = np.uint8 if bits <= 8 else np.uint16
    if kind == 'pattern':
        a = test_pattern(w, h, c, np.uint16, seed=seed).astype(np.uint32) >> (16 - bits)
    elif kind == 'noise':
        a = np.random.default_rng(seed).integers(0, 1 << bits, (h, w, c))
    elif kind == 'flat':      # long runs, run interruptions, line ends inside runs
        a = np.zeros((h, w, c), np.uint32)
        a[h // 3:, w // 4:w // 2] = (1 << bits) - 1
        a[:, -3:] = 1
        a[h // 2, ::7] = (1 << bits) // 2
    else:                     # 'mixed': flat areas with noisy patches
        a = test_pattern(w, h, c, np.uint16, seed=seed).astype(np.uint32) >> (16 - bits)
        a[:, : w // 2] = a[:, :1]
        a[h // 2:, :] = (1 << bits) // 3
    return a.astype(dt).reshape(h, w, c)


def segments(e):
    """(head up to the first SOS, list of (sos header, scan data)) of a CharLS file; scan data ends before the next marker
    that is not RSTm."""
    i = e.index(b'\xff\xda')
    head, scans = e[:i], []
    while i < len(e) and e[i:i + 2] == b'\xff\xda':
        ls = e[i + 2] << 8 | e[i + 3]
        sos = e[i:i + 2 + ls]
        j = i + 2 + ls
        k = j
        while True:
            k = e.index(b'\xff', k)
            if e[k + 1] >= 0x80 and not 0xD0 <= e[k + 1] <= 0xD7:
                break
            k += 1
        scans.append((sos, e[j:k]))
        i = k
        while e[i:i + 2] == b'\xff\xf8':   # LSE between scans
            i += 2 + (e[i + 2] << 8 | e[i + 3])
    return head, scans


def frame(e):
    i = e.index(b'\xff\xf7')
    return i, e[i:i + 2 + (e[i + 2] << 8 | e[i + 3])]


def sof(bits, w, h, comps):
    """comps: [(id, H, V)]"""
    b = bytes([bits]) + struct.pack('>HHB', h, w, len(comps))
    for cid, hh, vv in comps:
        b += bytes([cid, hh << 4 | vv, 0])
    return b'\xff\xf7' + struct.pack('>H', 2 + len(b)) + b


def sos(comps, near, ilv, tables=None):
    tables = tables or [0] * len(comps)
    b = bytes([len(comps)]) + b''.join(bytes([c, t]) for c, t in zip(comps, tables)) + bytes([near, ilv, 0])
    return b'\xff\xda' + struct.pack('>H', 2 + len(b)) + b


def replicate(plane, fx, fy, w, h):
    return np.repeat(np.repeat(plane, fy, 0), fx, 1)[:h, :w]


def main():
    cases = []
    # ---------------------------------------------------------------- CharLS written
    for bits in (2, 3, 4, 5, 8, 10, 12, 13, 16):
        for kind in ('pattern', 'noise', 'flat'):
            a = image(41, 23, 1, bits, kind)
            save(f'gray{bits}_{kind}.jls', charls.encode(a, bits=bits), a)
    for bits in (8, 12, 16):
        for ilv in (0, 1, 2):
            for kind in ('pattern', 'mixed', 'flat'):
                a = image(37, 21, 3, bits, kind, seed=bits)
                save(f'rgb{bits}_ilv{ilv}_{kind}.jls', charls.encode(a, bits=bits, ilv=ilv), a)
    for ilv in (0, 1, 2):
        a = image(29, 17, 4, 8, 'mixed', seed=4)
        save(f'four8_ilv{ilv}.jls', charls.encode(a, ilv=ilv), a)
    for n in (2, 5):   # more components (CharLS interleaves only 3 or 4)
        a = image(19, 11, n, 8, 'pattern', seed=n)
        save(f'comp{n}_ilv0.jls', charls.encode(a, ilv=0), a)
    for bits, nears in ((8, (1, 3, 10)), (12, (2, 7)), (16, (1, 60)), (4, (1,))):
        for near in nears:
            for c, ilv in ((1, 0), (3, 1), (3, 2)):
                a = image(33, 19, c, bits, 'mixed', seed=near)
                e = charls.encode(a, bits=bits, near=near, ilv=ilv)
                save(f'near{near}_b{bits}_c{c}_ilv{ilv}.jls', e, charls.decode(e))
    for pc in ((0, 4, 9, 30, 10), (0, 0, 0, 0, 255), (0, 2, 3, 4, 64), (0, 0, 40, 0, 0)):
        for near in (0, 2):
            if pc[1] and pc[1] <= near:
                continue
            a = image(31, 19, 1, 8, 'mixed', seed=7)
            tag = '_'.join(map(str, pc[1:]))
            try:
                e = charls.encode(a, near=near, pc=pc)
                d = charls.decode(e)
            except RuntimeError:
                continue
            save(f'preset_{tag}_n{near}.jls', e, d)
            a3 = image(31, 19, 3, 12, 'pattern', seed=8)
            e = charls.encode(a3, bits=12, near=near, ilv=1, pc=pc)
            save(f'preset_{tag}_n{near}_rgb12.jls', e, charls.decode(e))
    for tf in (1, 2, 3):
        for bits in (8, 16):
            for ilv in (1, 2):
                a = image(35, 18, 3, bits, 'noise' if tf == 3 else 'pattern', seed=tf)
                save(f'hp{tf}_b{bits}_ilv{ilv}.jls', charls.encode(a, bits=bits, ilv=ilv, transform=tf), a)
    a = image(27, 15, 1, 8)
    save('spiff_gray.jls', charls.encode(a, spiff=1), a)
    a = image(27, 15, 3, 8)
    save('spiff_rgb_dpi.jls', charls.encode(a, spiff=2), a)   # 96 x 72 dpi
    a = image(25, 13, 1, 8)
    save('comment_padded.jls', charls.encode(a, comment=b'synthetic', options=1), a)   # COM, even size (FF fill)
    for (w, h) in ((1, 1), (1, 17), (17, 1), (2, 2)):
        a = image(w, h, 3, 8, 'noise')
        save(f'tiny_{w}x{h}.jls', charls.encode(a, ilv=2), a)
    a = image(4100, 5, 1, 8, 'flat')            # runs long enough for RUNindex 31
    save('longruns.jls', charls.encode(a), a)
    a = np.zeros((2, 70000, 1), np.uint8)
    a[1, 30000:30010] = 9
    save('oversize_70000x2.jls', charls.encode(a), a)   # LSE id 4

    # ---------------------------------------------------------------- built from CharLS scans
    # restart intervals: strips coded independently, joined with RSTm (CharLS decodes this too)
    for c, ilv, ri in ((1, 0, 3), (3, 2, 4), (3, 1, 5), (1, 0, 1)):
        a = image(30, 21, c, 8, 'mixed', seed=ri)
        head, scans = segments(charls.encode(a, ilv=ilv))
        out = bytearray(head) + b'\xff\xdd\x00\x04' + struct.pack('>H', ri) + scans[0][0]
        for k, y in enumerate(range(0, a.shape[0], ri)):
            if k:
                out += bytes([0xFF, 0xD0 + (k - 1) % 8])
            out += segments(charls.encode(np.ascontiguousarray(a[y:y + ri]), ilv=ilv))[1][0][1]
        out += b'\xff\xd9'
        assert (charls.decode(bytes(out)) == a).all()
        save(f'restart_c{c}_ilv{ilv}_ri{ri}.jls', bytes(out), a)
    # separate scans: per component NEAR and preset parameters (LSE before each scan)
    w, h = 26, 17
    a = image(w, h, 3, 8, 'mixed', seed=11)
    out = bytearray(b'\xff\xd8') + sof(8, w, h, [(1, 1, 1), (2, 1, 1), (3, 1, 1)])
    ref = np.zeros_like(a)
    for ci, (near, pc) in enumerate(((0, None), (3, (0, 0, 0, 0, 20)), (1, (0, 5, 11, 30, 0)))):
        e = charls.encode(np.ascontiguousarray(a[:, :, ci]), near=near, pc=pc)
        if pc:
            i = e.index(b'\xff\xf8')
            out += e[i:i + 15]
        out += sos([ci + 1], near, 0) + segments(e)[1][0][1]
        ref[:, :, ci] = charls.decode(e)[:, :, 0]
    out += b'\xff\xd9'
    save('scans_per_component.jls', bytes(out), ref)
    # subsampled components (non interleaved): Y full size, Cb / Cr 2x2 (and 2x1); upsampled by replication
    for (hh, vv) in ((2, 2), (2, 1), (1, 2)):
        w, h = 33, 19
        y = image(w, h, 1, 8, 'pattern', seed=1)[:, :, 0]
        cw, ch = -(-w // hh), -(-h // vv)
        cb = image(cw, ch, 1, 8, 'pattern', seed=2)[:, :, 0]
        cr = image(cw, ch, 1, 8, 'noise', seed=3)[:, :, 0]
        out = bytearray(b'\xff\xd8') + sof(8, w, h, [(1, hh, vv), (2, 1, 1), (3, 1, 1)])
        for cid, p in ((1, y), (2, cb), (3, cr)):
            out += sos([cid], 0, 0) + segments(charls.encode(p))[1][0][1]
        out += b'\xff\xd9'
        ref = np.stack([y, replicate(cb, hh, vv, w, h), replicate(cr, hh, vv, w, h)], -1)
        save(f'subsampled_{hh}x{vv}.jls', bytes(out), ref)
    # mapping tables: an index image with a palette (3 byte entries -> RGB), 1 and 2 byte tables, LSE 3 continuation
    w, h = 23, 14
    idx = (image(w, h, 1, 4, 'pattern')[:, :, 0]).astype(np.uint8)   # 0..15
    e = charls.encode(idx, bits=4)
    i, f = frame(e)
    head, scans = segments(e)
    pal = np.array([[(17 * k) % 256, (91 * k + 5) % 256, (200 - 13 * k) % 256] for k in range(16)], np.uint8)

    def with_table(lse, ent):
        s = scans[0][0]
        s = s[:6] + bytes([1]) + s[7:]     # Tm of the single component = table 1
        return head[:i] + f + lse + s + scans[0][1] + b'\xff\xd9', ent

    def lse_table(tid, wt, data, cont=False):
        body = bytes([3 if cont else 2, tid, wt]) + data
        return b'\xff\xf8' + struct.pack('>H', 2 + len(body)) + body

    data, ref = with_table(lse_table(1, 3, pal.tobytes()), pal[idx])
    save('palette_rgb.jls', data, ref)
    data, ref = with_table(lse_table(1, 3, pal[:7].tobytes()) + lse_table(1, 3, pal[7:].tobytes(), cont=True), pal[idx])
    save('palette_rgb_continued.jls', data, ref)
    g1 = (255 - 16 * np.arange(16)).astype(np.uint8)
    data, ref = with_table(lse_table(1, 1, g1.tobytes()), g1[idx])
    save('palette_gray8.jls', data, ref)
    g2 = (1000 + 3000 * np.arange(16)).astype(np.uint16)
    data, ref = with_table(lse_table(1, 2, g2.astype('>u2').tobytes()), g2[idx])
    save('palette_gray16.jls', data, ref)
    # DNL: the frame header leaves the height open
    a = image(21, 13, 1, 8, 'mixed')
    e = charls.encode(a)
    i, f = frame(e)
    f0 = f[:5] + b'\x00\x00' + f[7:]
    data = e[:i] + f0 + e[i + len(f):-2] + b'\xff\xdc\x00\x04' + struct.pack('>H', 13) + b'\xff\xd9'
    save('dnl.jls', data, a)
    # EXIF orientation 6 (rotate 90 clockwise to view)
    a = image(20, 11, 3, 8, 'pattern')
    e = charls.encode(a, ilv=2)
    tiff = b'II*\x00\x08\x00\x00\x00' + struct.pack('<H', 1) + struct.pack('<HHIHH', 0x0112, 3, 1, 6, 0) + b'\x00\x00\x00\x00'
    app1 = b'\xff\xe1' + struct.pack('>H', 2 + 6 + len(tiff)) + b'Exif\x00\x00' + tiff
    save('exif_orientation6.jls', e[:2] + app1 + e[2:], np.ascontiguousarray(np.rot90(a, -1)))

    # ---------------------------------------------------------------- encoder expectations
    def enc_case(name, img, **kw):
        bits = kw.get('bits', 8 if img.dtype == np.uint8 else 16)
        ilv = kw.get('ilv', 0 if img.shape[2] == 1 else 2)
        e = charls.encode(img, bits=bits, near=kw.get('near', 0), ilv=ilv, transform=kw.get('tf', 0), pc=kw.get('pc'))
        save(f'enc_{name}.jls', e, None)
        write_ref(os.path.join(ROOT, f'enc_{name}.ref'), img)
        cases.append({'name': name, 'bits': bits, 'near': kw.get('near', 0), 'ilv': ilv, 'tf': kw.get('tf', 0),
                      'preset': list(kw['pc']) if kw.get('pc') else None})

    for kind in ('pattern', 'noise', 'flat', 'mixed'):
        enc_case(f'gray8_{kind}', image(45, 27, 1, 8, kind))
        enc_case(f'gray16_{kind}', image(45, 27, 1, 16, kind))
        for ilv in (0, 1, 2):
            enc_case(f'rgb8_{kind}_ilv{ilv}', image(39, 22, 3, 8, kind, seed=5), ilv=ilv)
    for bits in (2, 5, 10, 12, 14):
        enc_case(f'gray{bits}', image(30, 20, 1, bits, 'mixed'), bits=bits)
    for near in (1, 3, 9):
        enc_case(f'gray8_near{near}', image(45, 27, 1, 8, 'mixed'), near=near)
        enc_case(f'rgb8_near{near}_ilv2', image(39, 22, 3, 8, 'mixed'), near=near)
        enc_case(f'rgb12_near{near}_ilv1', image(39, 22, 3, 12, 'pattern'), near=near, bits=12, ilv=1)
    enc_case('gray16_near40', image(45, 27, 1, 16, 'pattern'), near=40)
    for tf in (1, 2, 3):
        enc_case(f'rgb8_hp{tf}', image(39, 22, 3, 8, 'pattern', seed=tf), tf=tf)
        enc_case(f'rgb16_hp{tf}_ilv1', image(39, 22, 3, 16, 'noise', seed=tf), tf=tf, ilv=1)
    enc_case('rgba8_ilv2', image(31, 17, 4, 8, 'mixed'), ilv=2)
    enc_case('rgba8_ilv1', image(31, 17, 4, 8, 'mixed'), ilv=1)
    enc_case('preset_gray8', image(45, 27, 1, 8, 'mixed'), pc=(0, 4, 9, 30, 10))
    enc_case('preset_gray8_near2', image(45, 27, 1, 8, 'mixed'), pc=(0, 5, 11, 30, 32), near=2)
    enc_case('wide_flat', image(3000, 4, 1, 8, 'flat'))
    enc_case('tiny_rgb', image(1, 1, 3, 8, 'noise'))
    with open(os.path.join(ROOT, 'encode_cases.json'), 'w') as fo:
        json.dump(cases, fo, indent=1)


if __name__ == '__main__':
    if charls.find() is None:
        raise SystemExit('imagecodecs with libcharls is needed')
    main()
