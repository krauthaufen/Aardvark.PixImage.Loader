"""Generates tests/fixtures/dpx and tests/fixtures/cineon: name.dpx / name.cin and name.ext.ref (what Dpx.Decode /
Cineon.Decode return with default options).

Hand built files from SMPTE 268M (DPX 2.0, 2003 revision) and the Kodak Cineon 4.5 draft cover every descriptor, depth,
packing, byte order, run length encoding, several image elements, orientation and padding; their references are computed
from what went into them. Files written by ImageMagick and OpenImageIO (synthetic content) have the oracle's decode as
reference. Every file is cross checked against both tools; disagreements are printed (--report prints all comparisons).

Rules the references follow (see Dpx.cs / Cineon.cs): 10 / 12 / 16 bit decode to UInt16 with the stored values, 1 bit to
0 / 255, signed to Int8 / Int16. 10 bit filled method A holds datum 0 in bits 31..22, method B in bits 29..20; packed data
is an LSB first bit stream through 32-bit words; 12 bit filled is a 16-bit unit per datum (A: value << 4). 8 bit lines are
padded to 32 bits (SMPTE, ImageMagick) unless the end-of-line padding field says otherwise or the data size shows no
padding (OpenImageIO); 16 bit lines are not padded. Y'CbCr: see ycc_to_rgb. The DPX orientation code is applied (EXIF
equivalent: 0->1, 1->2, 2->4, 3->3, 4->5, 5->6, 6->8, 7->7; Cineon 1 and 2 swapped).

Cineon: cells of 8 / 16 / 32 bits, fields from the most significant end (left justified) or packed at the low end (right
justified), one pixel per cell unless the packing's high bit is set, except 8 bit (four per 32-bit cell, as ImageMagick
writes it); lines start on a new cell; bit fields (packing 0) run MSB first through the bytes without line breaks.

Known disagreements of the tools (ImageMagick 7.1.1, OpenImageIO 3.1), for which the comparison is left out: ImageMagick
reads no filled method B, no packed one component data, no float data, does not reorder ABGR, ignores end-of-line padding
and the element data offset, converts only 4:4:4 Y'CbCr correctly (as full range BT.709) and reads 1 bit data MSB first;
its one component and 12 bit Cineon layouts differ from the specification. OpenImageIO pads no 8 bit lines, does not
restart 10 bit RGBA words at line ends, reads 1 bit data as zeros, does not convert Y'CbCr, reports orientations 4..7 with
swapped sizes but unturned pixels, and reads no Cineon line / channel interleave, padding or 12 bit data. Neither tool
reads a single 10 bit datum in the last word of a one component line (two datums: OpenImageIO takes the first from the
high bits), nor orders Cineon channels by their designators.

usage: python3 gen_dpx.py [--report]
"""
import os
import struct
import subprocess
import sys
import tempfile
import numpy as np
from pxref import write_ref

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'fixtures')
REPORT = '--report' in sys.argv
TMP = tempfile.mkdtemp()

try:
    import OpenImageIO as oiio
except ImportError:
    oiio = None


def path(sub, name):
    d = os.path.join(ROOT, sub)
    os.makedirs(d, exist_ok=True)
    return os.path.join(d, name)


# ---------------------------------------------------------------------------------------------------- DPX writer

def ascii_field(s, n):
    b = s.encode('latin1')[:n]
    return b + b'\0' * (n - len(b))


def dpx_file(w, h, elements, be=True, orient=0, data_offset=None, hdr=None, user=None, version='V2.0'):
    """elements: dicts with desc, bits, data (bytes) and optional packing, encoding, sign, lo, hi, transfer, colorimetric,
    eol, eoi, offset ('auto' = place sequentially, None = leave undefined, int)."""
    E = '>' if be else '<'
    hdr = hdr or {}
    b = bytearray(b'\xff' * 2048)
    b[0:4] = b'SDPX' if be else b'XPDS'
    user_bytes = b''
    if user is not None:
        uid, udata = user
        user_bytes = ascii_field(uid, 32) + udata
    start = data_offset if data_offset is not None else 2048 + len(user_bytes)
    start = max(start, 2048 + len(user_bytes))
    offsets = []
    pos = start
    for e in elements:
        offsets.append(pos)
        pos += len(e['data']) + e.get('eoi', 0)
    struct.pack_into(E + 'I', b, 4, start)
    b[8:16] = ascii_field(version, 8)
    struct.pack_into(E + 'IIIII', b, 16, pos, 1, 1664, 384, len(user_bytes))
    b[36:136] = ascii_field(hdr.get('file', ''), 100)
    b[136:160] = ascii_field(hdr.get('date', ''), 24)
    b[160:260] = ascii_field(hdr.get('creator', 'gen_dpx.py'), 100)
    b[260:460] = ascii_field(hdr.get('project', ''), 200)
    b[460:660] = ascii_field(hdr.get('copyright', ''), 200)
    struct.pack_into(E + 'HHII', b, 768, orient, len(elements), w, h)
    for i, e in enumerate(elements):
        o = 780 + 72 * i
        bits = e['bits']
        lo = e.get('lo', 0)
        hi = e.get('hi', (1 << bits) - 1 if bits <= 16 else 0xFFFFFFFF)
        struct.pack_into(E + 'IIIII', b, o, e.get('sign', 0), lo, 0xFFFFFFFF, hi, 0xFFFFFFFF)
        b[o + 20:o + 24] = bytes([e['desc'], e.get('transfer', 2), e.get('colorimetric', 4), bits])
        off = e.get('offset', 'auto')
        off = offsets[i] if off == 'auto' else 0xFFFFFFFF if off is None else off
        struct.pack_into(E + 'HHIII', b, o + 24, e.get('packing', 0), e.get('encoding', 0), off, e.get('eol', 0), e.get('eoi', 0))
        b[o + 40:o + 72] = ascii_field(e.get('description', ''), 32)
    # image source / film / television fields, if given
    for key, (o, fmt) in {'xoff': (1408, 'I'), 'yoff': (1412, 'I'), 'xcenter': (1416, 'f'), 'ycenter': (1420, 'f'),
                          'xorig': (1424, 'I'), 'yorig': (1428, 'I'), 'frame_pos': (1712, 'I'), 'seq_len': (1716, 'I'),
                          'held': (1720, 'I'), 'frame_rate': (1724, 'f'), 'shutter': (1728, 'f'), 'timecode': (1920, 'I'),
                          'userbits': (1924, 'I'), 'tv_rate': (1940, 'f'), 'gamma': (1948, 'f'), 'aspect_h': (1628, 'I'),
                          'aspect_v': (1632, 'I'), 'xscan': (1636, 'f'), 'yscan': (1640, 'f')}.items():
        if key in hdr:
            struct.pack_into(E + fmt, b, o, hdr[key])
    for key, (o, n) in {'src_file': (1432, 100), 'src_date': (1532, 24), 'device': (1556, 32), 'serial': (1588, 32),
                        'film_mfg': (1664, 2), 'film_type': (1666, 2), 'perfs': (1668, 2), 'prefix': (1670, 6),
                        'count': (1676, 4), 'format': (1680, 32), 'frame_id': (1732, 32), 'slate': (1764, 100)}.items():
        if key in hdr:
            b[o:o + n] = ascii_field(hdr[key], n)
    if 'border' in hdr:
        struct.pack_into(E + 'HHHH', b, 1620, *hdr['border'])
    if 'interlace' in hdr:
        b[1928:1931] = bytes([hdr['interlace'], hdr['field'], hdr['standard']])
    out = bytes(b) + user_bytes
    out += b'\0' * (start - len(out))
    for e in elements:
        out += e['data'] + b'\0' * e.get('eoi', 0)
    return out


def pack_units(vals, bits, packing, E, comps=3):
    """One line of datums as stored, without line padding beyond what the unit size implies. 10 bit filled words hold the
    first datum in the high bits, except for one component elements (low bits, figure C.3)."""
    vals = [int(v) for v in vals]
    if bits == 8:
        return bytes(v & 255 for v in vals)
    if bits == 16:
        return b''.join(struct.pack(E + 'H', v & 0xFFFF) for v in vals)
    if bits == 12 and packing in (1, 2):
        return b''.join(struct.pack(E + 'H', (v << 4 if packing == 1 else v) & 0xFFFF) for v in vals)
    if bits == 10 and packing in (1, 2):
        s0 = 22 if packing == 1 else 20
        out = b''
        for i in range(0, len(vals), 3):
            word = 0
            for j, v in enumerate(vals[i:i + 3]):
                word |= (v & 1023) << (s0 - 10 * (2 - j if comps == 1 else j))
            out += struct.pack(E + 'I', word)
        return out
    # packed: LSB first bit stream through 32-bit words
    acc = n = 0
    out = b''
    for v in vals:
        acc |= (v & ((1 << bits) - 1)) << n
        n += bits
        while n >= 32:
            out += struct.pack(E + 'I', acc & 0xFFFFFFFF)
            acc >>= 32
            n -= 32
    if n:
        out += struct.pack(E + 'I', acc & 0xFFFFFFFF)
    return out


def pack_floats(vals, bits, E):
    return b''.join(struct.pack(E + ('f' if bits == 32 else 'd'), float(v)) for v in vals)


def element_data(lines, bits, packing=1, be=True, pad=True, eol=0, comps=3):
    """lines: (h, n) datums per line. pad: 8 bit lines padded to 32 bits (SMPTE / ImageMagick)."""
    E = '>' if be else '<'
    out = b''
    for line in lines:
        b = pack_floats(line, bits, E) if bits >= 32 else pack_units(line, bits, packing, E, comps)
        if pad and bits == 8 and len(b) % 4:
            b += b'\0' * (4 - len(b) % 4)
        out += b + b'\0' * eol
    return out


def rle_data(lines, comps, bits, packing=1, be=True):
    """Run length encoded lines (SMPTE 268M table 3C): flag = count << 1 | same, then one pixel or count pixels; every
    line ends on a 32-bit boundary."""
    E = '>' if be else '<'
    maxrun = ((1 << bits) - 1) >> 1
    out = b''
    for line in lines:
        px = [tuple(int(v) for v in line[i:i + comps]) for i in range(0, len(line), comps)]
        datums = []
        i = 0
        while i < len(px):
            j = i
            while j + 1 < len(px) and px[j + 1] == px[i] and j + 1 - i < maxrun:
                j += 1
            if j > i:
                datums += [(j - i + 1) << 1 | 1, *px[i]]
                i = j + 1
            else:
                k = i
                while k < len(px) and k - i < maxrun and (k + 1 >= len(px) or px[k + 1] != px[k]):
                    k += 1
                k = max(k, i + 1)
                datums.append((k - i) << 1)
                for p in px[i:k]:
                    datums += list(p)
                i = k
        b = pack_units(datums, bits, packing, E, comps)
        if len(b) % 4:
            b += b'\0' * (4 - len(b) % 4)
        out += b
    return out


# ---------------------------------------------------------------------------------------------------- references

def pattern(h, n, maxv, seed):
    rng = np.random.default_rng(seed)
    y, x = np.mgrid[0:h, 0:n]
    v = (x * 37 + y * 101 + (x * y) % 13) % (maxv + 1)
    noise = rng.integers(0, maxv + 1, (h, n))
    return np.where(rng.random((h, n)) < 0.3, noise, v).astype(np.int64)


def exif_upright(s, exif):
    """The upright image for pixels stored with EXIF orientation exif (h, w, c arrays)."""
    if exif == 1: return s
    if exif == 2: return s[:, ::-1]
    if exif == 3: return s[::-1, ::-1]
    if exif == 4: return s[::-1]
    if exif == 5: return s.transpose(1, 0, 2)
    if exif == 6: return np.rot90(s, -1)
    if exif == 7: return np.rot90(s.transpose(1, 0, 2), 2)
    if exif == 8: return np.rot90(s, 1)


DPX_EXIF = {0: 1, 1: 2, 2: 4, 3: 3, 4: 5, 5: 6, 6: 8, 7: 7}
CIN_EXIF = {0: 1, 1: 4, 2: 2, 3: 3, 4: 5, 5: 6, 6: 8, 7: 7}


def ycc_to_rgb(lines, w, desc, bits, colorimetric=4, transfer=4, lo=None, hi=None):
    """Mirror of DpxDecoder.Ycc: 709 for 274M / 709 codes, 601 for 601 / composite, else by width (709 from 1280).
    Range: the reference codes unless they are the video defaults, else video range."""
    def pick(c):
        return 709 if c in (5, 6) else 601 if c in (7, 8, 9, 10) else 0
    std = pick(colorimetric) or pick(transfer) or (709 if w >= 1280 else 601)
    kr, kb = (0.2126, 0.0722) if std == 709 else (0.299, 0.114)
    mx = (1 << bits) - 1
    sc = 1 << (bits - 8)
    ylo, yhi, clo, chi = 16 * sc, 235 * sc, 16 * sc, 240 * sc
    if lo is not None and hi is not None and hi > lo and hi <= mx and not (lo == ylo and hi == yhi):
        ylo = clo = lo
        yhi = chi = hi
    kg = 1 - kr - kb
    crR, cbB = 2 * (1 - kr), 2 * (1 - kb)
    cbG, crG = -cbB * kb / kg, -crR * kr / kg
    cmid = (clo + chi) / 2
    alpha = desc in (101, 103)
    out = np.zeros((len(lines), w, 4 if alpha else 3), np.uint16 if bits > 8 else np.uint8)

    def q(v):
        return 0 if v <= 0 else mx if v >= 1 else int(v * mx + 0.5)

    for r, line in enumerate(lines):
        line = [float(v) for v in line]
        px = []
        if desc in (102, 103):
            nc = 3 if desc == 102 else 4
            for x in range(w):
                u = line[x * nc:x * nc + nc]
                px.append((u[1], u[0], u[2], u[3] if alpha else 0))
        else:
            nc = 4 if desc == 100 else 6
            units = (w + 1) // 2
            for k in range(units):
                u = line[k * nc:k * nc + nc]
                nu = line[(k + 1) * nc:(k + 2) * nc] if k + 1 < units else u
                if desc == 100:
                    cb, y0, cr, y1, a0, a1 = u[0], u[1], u[2], u[3], 0, 0
                    ncb, ncr = nu[0], nu[2]
                else:
                    cb, y0, a0, cr, y1, a1 = u
                    ncb, ncr = nu[0], nu[3]
                px.append((y0, cb, cr, a0))
                if 2 * k + 1 < w:
                    px.append((y1, (cb + ncb) * 0.5, (cr + ncr) * 0.5, a1))
        for x, (yv, cb, cr, a) in enumerate(px[:w]):
            Y = (yv - ylo) / (yhi - ylo)
            Cb = (cb - cmid) / (chi - clo)
            Cr = (cr - cmid) / (chi - clo)
            out[r, x, 0] = q(Y + crR * Cr)
            out[r, x, 1] = q(Y + cbG * Cb + crG * Cr)
            out[r, x, 2] = q(Y + cbB * Cb)
            if alpha:
                out[r, x, 3] = int(a)
    return out


# ---------------------------------------------------------------------------------------------------- oracle reads

def tool_magick(p, convert=False):
    """ImageMagick's RGBA decode as floats 0..1 (colour space left alone unless convert: Y'CbCr to RGB), or None."""
    try:
        info = subprocess.run(['magick', 'identify', '-format', '%w %h\n', p], capture_output=True, check=True, text=True).stdout.split()
        w, h = int(info[0]), int(info[1])
        cs = [] if convert else ['-set', 'colorspace', 'sRGB']
        raw = subprocess.run(['magick', p, *cs, '-depth', '16', 'rgba:-'], capture_output=True, check=True).stdout
        return np.frombuffer(raw, '<u2').reshape(h, w, 4).astype(np.float64) / 65535
    except Exception:
        return None


def tool_oiio(p, fmt='float'):
    """OpenImageIO's decode of the first image element as floats (integers scaled to 0..1), or None."""
    if oiio is None:
        return None
    try:
        i = oiio.ImageInput.open(p)
        if i is None:
            oiio.geterror()
            return None
        a = i.read_image(format=fmt)
        i.close()
        if a is None:
            return None
        return np.asarray(a, np.float64).reshape(a.shape[0], a.shape[1], -1)
    except Exception:
        return None


def normalized(ref, bits, is_float):
    r = ref.astype(np.float64)
    if is_float:
        return r
    if ref.dtype in (np.int8, np.int16):
        return None   # the tools do not agree on signed data; not compared
    return r / (255 if bits in (1, 8) else (1 << bits) - 1)


MAGICK_HDRI = 'HDRI' in subprocess.run(['magick', '-version'], capture_output=True, text=True).stdout


def check(name, p, ref, bits, is_float=False, tol=None, tools=('magick', 'oiio'), ycc=False):
    """Compares the reference with the tools' decodes (normalized to 0..1); for Y'CbCr only ImageMagick's own conversion
    (full range BT.709, whatever the file says), as OpenImageIO does not convert."""
    n = normalized(ref if ref.ndim == 3 else ref[:, :, None], bits, is_float)
    if n is None:
        return
    c = n.shape[2]
    # one code: the tools round 10 / 12 bit codes through 16 bit quanta
    tol = tol if tol is not None else (1e-6 if is_float else 1.01 / ((1 << min(bits, 16)) - 1) + 1.0 / 65535)
    if ycc:
        tools = ('magick',)
    for t in tools:
        a = tool_magick(p, ycc) if t == 'magick' else tool_oiio(p)
        nn = n
        if t == 'magick' and is_float:
            continue   # ImageMagick 7.1 does not read float DPX correctly
        if a is None:
            if REPORT:
                print(f'  {name}: {t} cannot read it')
            continue
        if a.shape[:2] != n.shape[:2]:
            print(f'  {name}: {t} gives size {a.shape[1]}x{a.shape[0]}, reference {n.shape[1]}x{n.shape[0]}')
            continue
        if t == 'magick' and c > 4:
            continue
        a = a[:, :, :c] if a.shape[2] >= c else a
        if a.shape[2] != c:
            print(f'  {name}: {t} gives {a.shape[2]} channels, reference {c}')
            continue
        d = np.abs(a - nn).max()
        if d > tol or REPORT:
            print(f'  {name}: {t} differs by {d:.5f} ({d * ((1 << min(bits, 16)) - 1) if not is_float else d:.2f} codes)')


def save(sub, name, data, ref):
    p = path(sub, name)
    with open(p, 'wb') as f:
        f.write(data)
    write_ref(p + '.ref', ref)
    return p


# ---------------------------------------------------------------------------------------------------- DPX fixtures

def direct_ref(lines, w, c, bits, signed=False, abgr=False):
    a = np.asarray(lines).reshape(len(lines), w, c)
    if abgr:
        a = a[:, :, ::-1]
    if bits == 1:
        return (a * 255).astype(np.uint8)
    if bits >= 32:
        return a.astype(np.float32 if bits == 32 else np.float64)
    if signed:
        sh = 64 - bits
        s = (a.astype(np.int64) << sh) >> sh
        return s.astype(np.int8 if bits == 8 else np.int16)
    return a.astype(np.uint8 if bits == 8 else np.uint16)


def plain(name, w, h, c, desc, bits, packing=1, be=True, seed=1, pad=True, eol=0, signed=False, transfer=2, colorimetric=4,
          tools=('magick', 'oiio'), **kw):
    if bits >= 32:
        rng = np.random.default_rng(seed)
        lines = (rng.random((h, w * c)) * 4 - 1).astype(np.float32 if bits == 32 else np.float64)
    else:
        lines = pattern(h, w * c, (1 << bits) - 1, seed)
    data = element_data(lines, bits, packing, be, pad, eol, c)
    e = dict(desc=desc, bits=bits, packing=packing, data=data, eol=eol, sign=1 if signed else 0, transfer=transfer, colorimetric=colorimetric)
    ref = direct_ref(lines, w, c, bits, signed, abgr=desc == 52)
    p = save('dpx', name + '.dpx', dpx_file(w, h, [e], be, **kw), ref)
    check(name, p, ref, bits, bits >= 32, tools=tools)


def ycc(name, w, h, desc, bits, packing=1, be=True, colorimetric=6, transfer=6, lo=None, hi=None, seed=3, rle=False):
    comps, unit = {100: (4, 2), 101: (6, 2), 102: (3, 1), 103: (4, 1)}[desc]
    n = (w + unit - 1) // unit * comps
    mx = (1 << bits) - 1
    sc = 1 << (bits - 8)
    rng = np.random.default_rng(seed)
    lines = rng.integers(16 * sc, 236 * sc, (h, n)).astype(np.int64)
    # a smooth part so that the chroma interpolation shows
    lines[:, :n // 2] = (np.arange(n // 2)[None, :] * 7 * sc + 40 * sc) % (200 * sc) + 20 * sc
    e = dict(desc=desc, bits=bits, packing=packing, transfer=transfer, colorimetric=colorimetric,
             lo=lo if lo is not None else 0xFFFFFFFF, hi=hi if hi is not None else 0xFFFFFFFF)
    e['data'] = rle_data(lines, comps, bits, packing, be) if rle else element_data(lines, bits, packing, be, comps=comps)
    if rle:
        e['encoding'] = 1
    ref = ycc_to_rgb(lines, w, desc, bits, colorimetric, transfer, lo, hi)
    p = save('dpx', name + '.dpx', dpx_file(w, h, [e], be), ref)
    # ImageMagick converts as full range BT.709 with its own chroma upsampling: compared where that is what the file says
    # (its 4:2:2 and alpha variants come out wrong, so only 4:4:4 is compared)
    full709 = desc == 102 and lo == 0 and hi == (1 << bits) - 1 and (colorimetric in (5, 6) or transfer in (5, 6))
    if full709 or REPORT:
        check(name, p, ref, bits, tol=1.01 / ((1 << bits) - 1) + 1 / 65535 if desc in (102, 103) else 0.05, ycc=True)


def rle(name, w, h, c, desc, bits, packing=1, be=True, seed=5):
    base = pattern(h, w * c, (1 << bits) - 1, seed).reshape(h, w, c)
    base[:, 2:9] = base[:, 2:3]          # a run
    base[1:, :] = np.where(np.arange(w)[None, :, None] % 5 == 0, base[1:, :], base[1:, 0:1])  # long runs on later lines
    lines = base.reshape(h, w * c)
    e = dict(desc=desc, bits=bits, packing=packing, encoding=1, data=rle_data(lines, c, bits, packing, be))
    ref = direct_ref(lines, w, c, bits)
    p = save('dpx', name + '.dpx', dpx_file(w, h, [e], be), ref)
    check(name, p, ref, bits)


def gen_dpx_hand():
    print('DPX hand built')
    plain('rgb10a_be', 7, 5, 3, 50, 10, 1, True)
    plain('rgb10a_le', 7, 5, 3, 50, 10, 1, False)
    plain('rgb10b_be', 7, 5, 3, 50, 10, 2, True, tools=('oiio',))
    plain('rgb10p_le', 7, 5, 3, 50, 10, 0, False)
    plain('rgb10p_be', 7, 5, 3, 50, 10, 0, True)
    plain('rgba10a_be', 7, 5, 4, 51, 10, 1, True, tools=('magick',))
    plain('luma10a_be', 6, 5, 1, 6, 10, 1, True)
    # the last word of a line holds two datums / one datum: OpenImageIO reads the first from the high bits, and neither
    # tool reads a single one
    plain('luma10a_w8', 8, 5, 1, 6, 10, 1, True, tools=('magick',))
    plain('luma10a_w7_le', 7, 5, 1, 6, 10, 1, False, tools=())
    plain('luma10p_le', 7, 5, 1, 6, 10, 0, False, tools=('oiio',))
    plain('rgb12a_be', 7, 5, 3, 50, 12, 1, True)
    plain('rgb12b_le', 7, 5, 3, 50, 12, 2, False, tools=('oiio',))
    plain('rgb12p_be', 7, 5, 3, 50, 12, 0, True)
    plain('luma12p_le', 7, 5, 1, 6, 12, 0, False, tools=('oiio',))
    plain('rgb16_le', 7, 5, 3, 50, 16, 0, False)
    plain('rgb16_be', 7, 5, 3, 50, 16, 0, True)
    plain('rgba16_be', 6, 4, 4, 51, 16, 0, True)
    plain('rgb8_be_pad', 5, 4, 3, 50, 8, 0, True, tools=('magick',))
    plain('rgb8_le_eol', 5, 4, 3, 50, 8, 0, False, pad=False, eol=3, tools=('oiio',))
    plain('luma8_nopad', 5, 4, 1, 6, 8, 0, True, pad=False)
    plain('luma8_pad', 5, 4, 1, 6, 8, 0, False, tools=('magick',))
    plain('rgba8_be', 5, 4, 4, 51, 8, 0, True)
    plain('luma1_le', 37, 3, 1, 6, 1, 0, False)
    plain('luma1_be', 37, 3, 1, 6, 1, 0, True)
    plain('red8', 6, 4, 1, 1, 8, tools=('magick',))
    plain('green16', 6, 4, 1, 2, 16, be=False)
    plain('blue10', 6, 4, 1, 3, 10)
    plain('alpha8', 6, 4, 1, 4, 8, tools=('magick',))
    plain('depth_f32', 6, 4, 1, 8, 32, transfer=11, colorimetric=11)
    plain('user1_16', 6, 4, 1, 0, 16, transfer=0, colorimetric=0)
    plain('abgr8', 5, 4, 4, 52, 8, tools=('oiio',))
    plain('abgr16_le', 5, 4, 4, 52, 16, be=False, tools=('oiio',))
    plain('abgr10a', 5, 4, 4, 52, 10, tools=('oiio',))
    plain('rgbf32_be', 6, 4, 3, 50, 32)
    plain('rgbaf32_le', 6, 4, 4, 51, 32, be=False)
    plain('rgbf64_be', 6, 4, 3, 50, 64)
    plain('lumaf64_le', 6, 4, 1, 6, 64, be=False)
    plain('user2_8', 6, 4, 2, 150, 8, tools=())
    plain('user5_10', 6, 4, 5, 153, 10, tools=())
    plain('signed16', 6, 4, 1, 6, 16, signed=True)
    plain('signed8', 6, 4, 3, 50, 8, signed=True)
    plain('signed10', 6, 4, 3, 50, 10, signed=True)
    plain('eol10a', 7, 4, 3, 50, 10, 1, eol=8, tools=('oiio',))
    plain('offset_user', 7, 4, 3, 50, 10, 1, data_offset=8192, user=('gen_dpx user', bytes(range(200))))
    # Y'CbCr
    ycc('cbycry8_709', 10, 4, 100, 8)
    ycc('cbycry10_601', 10, 4, 100, 10, colorimetric=7, transfer=7)
    ycc('cbycry10_odd', 9, 3, 100, 10, colorimetric=8, transfer=8)
    ycc('cbyacrya10', 8, 3, 101, 10)
    ycc('cbycr8', 7, 3, 102, 8, colorimetric=7, transfer=7)
    ycc('cbycra16_le', 7, 3, 103, 16, be=False)
    ycc('cbycr10_full', 7, 3, 102, 10, lo=0, hi=1023)
    ycc('cbycra8_full_le', 7, 3, 103, 8, be=False, lo=0, hi=255)
    ycc('cbycry10_full', 10, 3, 100, 10, lo=0, hi=1023)
    ycc('cbyacrya8_full', 8, 3, 101, 8, lo=0, hi=255)
    ycc('cbycr8_unspec', 7, 3, 102, 8, colorimetric=4, transfer=4)
    ycc('cbycry8_rle', 12, 3, 100, 8, rle=True)
    # run length encoding
    rle('rle8_rgb', 13, 4, 3, 50, 8)
    rle('rle10a_rgb', 13, 4, 3, 50, 10, 1)
    rle('rle10a_luma_le', 13, 4, 1, 6, 10, 1, False)
    rle('rle16_luma', 13, 4, 1, 6, 16, 0, False)
    rle('rle12p_rgba', 13, 4, 4, 51, 12, 0)
    rle('rle10b_rgb_le', 13, 4, 3, 50, 10, 2, False)
    multi()
    orientations()


def multi():
    w, h = 6, 4
    # separate R, G, B elements: Decode combines them, DecodeAll returns each
    planes = [pattern(h, w, 255, s) for s in (11, 12, 13)]
    els = [dict(desc=d, bits=8, data=element_data(p, 8, comps=1)) for d, p in zip((1, 2, 3), planes)]
    ref = np.stack([p.reshape(h, w) for p in planes], -1).astype(np.uint8)
    p = save('dpx', 'multi_rgb_planes.dpx', dpx_file(w, h, els), ref)
    for i, pl in enumerate(planes):
        write_ref(p + f'.{i}.ref', pl.reshape(h, w, 1).astype(np.uint8))
    check('multi_rgb_planes', p, ref[:, :, :1], 8, tools=('magick',))
    # an RGB element and an alpha element, the first with end-of-image padding
    rgb = pattern(h, w * 3, 1023, 14)
    a = pattern(h, w, 255, 15)
    for name, off in (('multi_rgb_alpha', 'auto'), ('multi_no_offsets', None)):
        # undefined offsets (a non-conforming file: the offset is a core field): the first element starts at the image
        # data offset, the next one after the first and its end-of-image padding
        els = [dict(desc=50, bits=10, packing=1, data=element_data(rgb, 10), eoi=12, offset=off),
               dict(desc=4, bits=8, data=element_data(a, 8, comps=1), offset=off)]
        ref = direct_ref(rgb, w, 3, 10)
        p = save('dpx', name + '.dpx', dpx_file(w, h, els), ref)
        write_ref(p + '.0.ref', ref)
        write_ref(p + '.1.ref', direct_ref(a, w, 1, 8))
        if off is not None:
            check(name, p, ref, 10)
    # luma then depth (float), offsets given in reverse file order
    y = pattern(h, w, 65535, 16)
    rng = np.random.default_rng(17)
    z = rng.random((h, w)).astype(np.float32) * 100
    dy, dz = element_data(y, 16, comps=1), element_data(z, 32, comps=1)
    els = [dict(desc=6, bits=16, data=dy, offset=2048 + len(dz)), dict(desc=8, bits=32, data=dz, offset=2048, transfer=11)]
    raw = bytearray(dpx_file(w, h, [dict(desc=6, bits=16, data=b'', offset=2048 + len(dz)), dict(desc=8, bits=32, data=b'', offset=2048, transfer=11)]))
    raw[2048:] = dz + dy
    struct.pack_into('>I', raw, 16, len(raw))
    ref = direct_ref(y, w, 1, 16)
    p = save('dpx', 'multi_luma_depth.dpx', bytes(raw), ref)
    write_ref(p + '.0.ref', ref)
    write_ref(p + '.1.ref', z.reshape(h, w, 1))
    check('multi_luma_depth', p, ref, 16, tools=('oiio',))   # ImageMagick reads from the header's image data offset


def orientations():
    w, h = 5, 3
    s = pattern(h, w * 3, 255, 21)
    for code in range(8):
        stored = direct_ref(s, w, 3, 8)
        ref = np.ascontiguousarray(exif_upright(stored, DPX_EXIF[code]))
        e = dict(desc=50, bits=8, data=element_data(s, 8))
        p = save('dpx', f'orient{code}.dpx', dpx_file(w, h, [e], orient=code), ref)
        # the tools leave the pixels as stored; OpenImageIO reports the EXIF orientation it sees
        if oiio is not None:
            i = oiio.ImageInput.open(p)
            o = i.spec().get_int_attribute('Orientation', 0)
            i.close()
            if o != DPX_EXIF[code]:
                print(f'  orient{code}: OpenImageIO says EXIF orientation {o}, we use {DPX_EXIF[code]}')
        check(f'orient{code}', p, stored, 8, tools=('magick',))


def header_fixture():
    w, h = 4, 2
    s = pattern(h, w * 3, 1023, 31)
    hdr = dict(file='frame.0001.dpx', date='2024:03:05:14:30:15+0100', creator='gen_dpx.py creator', project='Test Project',
               copyright='(c) nobody', xoff=10, yoff=20, xcenter=2.5, ycenter=1.5, xorig=1920, yorig=1080,
               src_file='source.dpx', src_date='2024:03:04:10:00:00Z', device='Scanner X', serial='SN123',
               border=(1, 2, 3, 4), aspect_h=1, aspect_v=1, xscan=24.89, yscan=18.67,
               film_mfg='12', film_type='34', perfs='01', prefix='123456', count='7890', format='Academy',
               frame_pos=86400, seq_len=1000, held=1, frame_rate=24.0, shutter=180.0, frame_id='keyframe', slate='Scene 1 Take 2',
               timecode=0x01020304, userbits=0x11223344, tv_rate=23.976, gamma=2.2, interlace=0, field=1, standard=203)
    e = dict(desc=50, bits=10, packing=1, data=element_data(s, 10), transfer=6, colorimetric=6, description='main plate')
    ref = direct_ref(s, w, 3, 10)
    p = save('dpx', 'header_full.dpx', dpx_file(w, h, [e], hdr=hdr, user=('USERID', b'user data!')), ref)
    if oiio is not None:
        i = oiio.ImageInput.open(p)
        sp = i.spec()
        i.close()
        if REPORT:
            print('  header_full:', {a.name: a.value for a in sp.extra_attribs})


# ---------------------------------------------------------------------------------------------------- tool written DPX

def tool_written():
    print('DPX written by the tools')
    rng = np.random.default_rng(41)
    w, h = 9, 5
    src = rng.integers(0, 65536, (h, w, 4)).astype(np.uint16)
    src[:, :3] = np.arange(3)[None, :, None] * 20000
    ppm = os.path.join(TMP, 'src.ppm')
    pam = os.path.join(TMP, 'src.pam')
    with open(ppm, 'wb') as f:
        f.write(b'P6 %d %d 65535\n' % (w, h) + src[:, :, :3].astype('>u2').tobytes())
    with open(pam, 'wb') as f:
        f.write(b'P7\nWIDTH %d\nHEIGHT %d\nDEPTH 4\nMAXVAL 65535\nTUPLTYPE RGB_ALPHA\nENDHDR\n' % (w, h) + src.astype('>u2').tobytes())

    def from_oracle(name, p, bits, c, use='oiio'):
        """Reference: the oracle's decode in our sample type."""
        a = tool_oiio(p) if use == 'oiio' else tool_magick(p)
        if a is None:
            print(f'  {name}: {use} cannot read its own file')
            return None
        a = a[:, :, :c]
        if bits >= 32:
            return a.astype(np.float32)
        mx = (1 << bits) - 1
        return np.round(a * mx).astype(np.uint8 if bits == 8 else np.uint16)

    def finish(name, p, ref, bits, tools=('magick', 'oiio')):
        if ref is None:
            os.remove(p)
            return
        write_ref(p + '.ref', ref)
        check(name, p, ref, bits, bits >= 32, tools=tools)

    for bits in (8, 10, 12, 16):
        p = path('dpx', f'im_rgb{bits}.dpx')
        subprocess.run(['magick', ppm, '-depth', str(bits), p], check=True)
        finish(f'im_rgb{bits}', p, from_oracle(f'im_rgb{bits}', p, bits, 3, 'magick'), bits, ('magick',) if bits == 8 else ('magick', 'oiio'))
    for bits in (8, 16):
        p = path('dpx', f'im_rgba{bits}.dpx')
        subprocess.run(['magick', pam, '-depth', str(bits), p], check=True)
        finish(f'im_rgba{bits}', p, from_oracle(f'im_rgba{bits}', p, bits, 4, 'magick'), bits)
    if oiio is None:
        print('  OpenImageIO missing: its files are left out')
        return
    def write_oiio(name, a, attrs, fmt='uint16'):
        p = path('dpx', name + '.dpx')
        hh, ww, c = a.shape
        spec = oiio.ImageSpec(ww, hh, c, fmt)
        for k, v in attrs.items():
            spec.attribute(k, v)
        o = oiio.ImageOutput.create(p)
        o.open(p, spec)
        o.write_image(a)
        o.close()
        return p
    for name, c, attrs, bits in [
            ('oiio_rgb10a_le', 3, {'oiio:BitsPerSample': 10}, 10),
            ('oiio_rgb10a_be', 3, {'oiio:BitsPerSample': 10, 'oiio:Endian': 'big'}, 10),
            ('oiio_rgb10b', 3, {'oiio:BitsPerSample': 10, 'dpx:Packing': 'Filled, method B'}, 10),   # (ImageMagick cannot read method B)
            ('oiio_rgb10p', 3, {'oiio:BitsPerSample': 10, 'dpx:Packing': 'Packed'}, 10),
            ('oiio_rgba10', 4, {'oiio:BitsPerSample': 10}, 10),
            ('oiio_rgb12', 3, {'oiio:BitsPerSample': 12}, 12),
            ('oiio_rgb16_be', 3, {'oiio:Endian': 'big'}, 16),
            ('oiio_rgba16', 4, {}, 16)]:
        p = write_oiio(name, np.ascontiguousarray(src[:, :, :c]), attrs)
        finish(name, p, from_oracle(name, p, bits, c), bits, ('oiio',) if name == 'oiio_rgb10b' else ('magick', 'oiio'))
    g = np.ascontiguousarray(src[:, :, :1])
    p = write_oiio('oiio_luma10', g, {'oiio:BitsPerSample': 10})
    finish('oiio_luma10', p, from_oracle('oiio_luma10', p, 10, 1), 10, ('oiio',))   # ImageMagick cannot read packed luma
    p = write_oiio('oiio_luma12', g, {'oiio:BitsPerSample': 12})
    finish('oiio_luma12', p, from_oracle('oiio_luma12', p, 12, 1), 12, ('oiio',))
    p = write_oiio('oiio_rgb8', np.ascontiguousarray((src[:, :, :3] >> 8).astype(np.uint8)), {}, 'uint8')
    finish('oiio_rgb8', p, from_oracle('oiio_rgb8', p, 8, 3), 8)   # (width 9: unpadded lines fill the data exactly)
    f = (src[:, :, :3].astype(np.float32) / 65535 * 3 - 1)
    p = write_oiio('oiio_rgbf32', np.ascontiguousarray(f), {}, 'float')
    finish('oiio_rgbf32', p, from_oracle('oiio_rgbf32', p, 32, 3), 32)


# ---------------------------------------------------------------------------------------------------- Cineon

def cin_file(w, h, chans, data, be=True, orient=0, interleave=0, packing=5, sign=0, eol=0, eoc=0, hdr=None):
    """chans: list of (metric, designator, bits)."""
    E = '>' if be else '<'
    hdr = hdr or {}
    b = bytearray(b'\xff' * 2048)
    struct.pack_into(E + 'IIIIII', b, 0, 0x802A5FD7, 2048, 1024, 1024, 0, 2048 + len(data))
    b[24:32] = ascii_field('V4.5', 8)
    b[32:132] = ascii_field(hdr.get('file', 'gen.cin'), 100)
    b[132:144] = ascii_field(hdr.get('date', '1999:12:31'), 12)
    b[144:156] = ascii_field(hdr.get('time', '23:59:58UTC'), 12)
    b[192] = orient
    b[193] = len(chans)
    b[194:196] = b'\0\0'
    for i, (m, d, bits) in enumerate(chans):
        o = 196 + 28 * i
        b[o:o + 4] = bytes([m, d, bits, 0])
        struct.pack_into(E + 'II4f', b, o + 4, w, h, 0, 0, (1 << bits) - 1, 2.048)
    b[452:652] = ascii_field(hdr.get('label', ''), 200)
    b[680:684] = bytes([interleave, packing, sign, 0])
    struct.pack_into(E + 'II', b, 684, eol, eoc)
    struct.pack_into(E + 'ii', b, 712, hdr.get('xoff', 0), hdr.get('yoff', 0))
    b[844:908] = ascii_field(hdr.get('device', ''), 64)
    struct.pack_into(E + 'f', b, 980, hdr.get('gamma', 1.0))
    struct.pack_into(E + 'If', b, 1068, hdr.get('frame_pos', 7), hdr.get('frame_rate', 24.0))
    b[1036:1068] = ascii_field(hdr.get('format', ''), 32)
    return bytes(b) + data


def cin_cells(fields_lines, bits, packing, be, per_cell=None):
    """Packs lines of fields into cells (see the module notes). per_cell overrides the fields per cell."""
    E = '>' if be else '<'
    p = packing & 0x7F
    if p == 0:
        bitstr = ''.join(format(int(v), f'0{bits}b') for line in fields_lines for v in line)
        bitstr += '0' * (-len(bitstr) % 8)
        return bytes(int(bitstr[i:i + 8], 2) for i in range(0, len(bitstr), 8))
    cell = {1: 8, 2: 8, 3: 16, 4: 16, 5: 32, 6: 32}[p]
    k = per_cell or cell // bits
    fmt = {8: 'B', 16: 'H', 32: 'I'}[cell]
    left = p in (1, 3, 5)
    out = b''
    for line in fields_lines:
        for i in range(0, len(line), k):
            v = 0
            for j, f in enumerate(line[i:i + k]):
                sh = cell - (j + 1) * bits if left else (k - 1 - j) * bits
                v |= int(f) << sh
            out += struct.pack(E + fmt, v)
    return out


def cin(name, w, h, chans, bits, packing=5, be=True, orient=0, interleave=0, per_cell=None, seed=51, order=None, sign=0, eol=0,
        tools=('magick', 'oiio')):
    n = len(chans)
    fields = pattern(h, w * n, (1 << bits) - 1, seed)
    stored = fields.reshape(h, w, n)
    if interleave == 0:
        lines = list(fields)
    elif interleave == 1:
        lines = [stored[y, :, c] for y in range(h) for c in range(n)]
    else:
        lines = [stored[y, :, c] for c in range(n) for y in range(h)]
    if per_cell is None and interleave == 0 and n > 1 and (packing & 0x80) == 0 and bits != 8 and packing & 0x7F:
        cell = {1: 8, 2: 8, 3: 16, 4: 16, 5: 32, 6: 32}[packing & 0x7F]
        per_cell = min(cell // bits, n)
    if interleave == 0 and bits == 16:
        lines = [fields.ravel()]   # 16 bit samples run on across line ends
    data = cin_cells(lines, bits, packing, be, per_cell)
    if eol:
        # end-of-line padding: rebuild line by line
        data = b''.join(cin_cells([l], bits, packing, be, per_cell) + b'\0' * eol for l in lines)
    ref = stored[:, :, order] if order is not None else stored
    if sign:
        sh = 64 - bits
        ref = ((ref.astype(np.int64) << sh) >> sh).astype(np.int8 if bits <= 8 else np.int16)
    elif bits in (1, 2, 4):
        ref = (ref * 255 // ((1 << bits) - 1)).astype(np.uint8)
    else:
        ref = ref.astype(np.uint8 if bits <= 8 else np.uint16)
    ref = np.ascontiguousarray(exif_upright(ref, CIN_EXIF[orient]))
    p = save('cineon', name + '.cin', cin_file(w, h, [(m, d, bits) for m, d in chans], data, be, orient, interleave, packing, sign, eol), ref)
    if orient == 0 and not sign and bits in (8, 10, 12, 16) and n in (1, 3):
        check(name, p, ref, bits, tools=tools)


def gen_cineon():
    print('Cineon hand built')
    rgb = [(0, 1), (0, 2), (0, 3)]
    cin('rgb10_be', 7, 5, rgb, 10)
    cin('rgb10_le', 7, 5, rgb, 10, be=False)
    cin('gray10', 6, 5, [(0, 0)], 10, tools=('oiio',))   # ImageMagick takes one channel cells from the low end
    cin('gray10_w7', 7, 5, [(0, 0)], 10, tools=())
    cin('rgb8', 5, 4, rgb, 8, tools=('magick',))
    cin('gray8', 7, 3, [(0, 0)], 8, tools=('magick',))
    cin('rgb16', 5, 4, rgb, 16)
    cin('rgb12', 5, 4, rgb, 12, tools=())
    cin('bgr10_designators', 6, 4, [(0, 3), (0, 2), (0, 1)], 10, order=[2, 1, 0], tools=())
    cin('rgb10_video', 6, 4, [(0, 4), (0, 5), (0, 6)], 10)
    cin('rgb10_bitfields', 7, 3, rgb, 10, packing=0, tools=('magick',))
    cin('rgb10_word_left', 6, 3, rgb, 10, packing=3, tools=('oiio',))
    cin('rgb10_word_right', 6, 3, rgb, 10, packing=4, tools=('oiio',))
    cin('rgb10_long_right', 6, 3, rgb, 10, packing=6, tools=('oiio',))
    cin('rgb8_byte', 5, 3, rgb, 8, packing=1)
    cin('rgba6_any', 6, 3, [(0, 1), (0, 2), (0, 3), (1, 7)], 6, packing=0x85)
    cin('rgba6_one', 6, 3, [(0, 1), (0, 2), (0, 3), (1, 7)], 6, packing=5)
    cin('gray4', 9, 3, [(0, 0)], 4)
    cin('gray1', 37, 3, [(0, 0)], 1)
    cin('rgb10_line', 6, 4, rgb, 10, interleave=1, tools=())
    cin('rgb10_channel', 6, 4, rgb, 10, interleave=2, tools=())
    cin('gray12_signed', 6, 3, [(0, 0)], 12, sign=1)
    cin('rgb10_eol', 7, 3, rgb, 10, eol=4, tools=())
    for code in range(8):
        cin(f'orient{code}', 5, 3, rgb, 10, orient=code)
    # written by ImageMagick (its 10 bit output holds log encoded codes: references from OpenImageIO, which reads them raw)
    print('Cineon written by ImageMagick')
    rng = np.random.default_rng(61)
    w, h = 9, 5
    src = rng.integers(0, 65536, (h, w, 3)).astype(np.uint16)
    ppm = os.path.join(TMP, 'c.ppm')
    with open(ppm, 'wb') as f:
        f.write(b'P6 %d %d 65535\n' % (w, h) + src.astype('>u2').tobytes())
    for bits in (8, 10, 16):
        p = path('cineon', f'im_rgb{bits}.cin')
        subprocess.run(['magick', ppm, '-depth', str(bits), p], check=True)
        # 8 bit: ImageMagick starts every line on a new cell, OpenImageIO does not (width 9: the lines end inside a cell)
        a = tool_oiio(p) if bits != 8 else None
        if a is None:
            # OpenImageIO missing: decode with ImageMagick, colour space left alone
            a = tool_magick(p)[:, :, :3]
        mx = (1 << bits) - 1
        ref = np.round(a * mx).astype(np.uint8 if bits == 8 else np.uint16)
        write_ref(p + '.ref', ref)
        check(f'im_rgb{bits}', p, ref, bits, tools=('magick',) if bits == 8 else ('magick', 'oiio'))


if __name__ == '__main__':
    gen_dpx_hand()
    header_fixture()
    tool_written()
    gen_cineon()
