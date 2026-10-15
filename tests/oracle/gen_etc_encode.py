"""Quality references for the ETC1 / ETC2 / EAC encoder tests (EtcEncodeTests.cs).

Writes tests/fixtures/etc_encode: small test images (Kodak crops, a synthetic image; the alpha variants carry the alpha
pattern of alpha_pattern below) and oracle.txt, one line per image, format and oracle with the PSNR the oracle's
blocks reach, decoded and measured exactly as the C# tests measure ours:

  colour formats   PSNR of RGB (punch-through: over the opaque texels), RGBA8 also of alpha, 8 bit units
  EAC              PSNR in 16 bit units of the decoded values (11 bit values extended as the specification says)
                   against the input widened to 16 bits: unsigned v * 257, signed round((v - 128) * 32767 / 127)
                   clamped to -32767 (etc2comp reads 8 bit input that way, see check_signed_mapping)

Oracles (only run, never read or ported):
  * etcpak (pip package, ~/.venvs/bcref): ETC1, ETC2 RGB, ETC2 RGBA, EAC R11 / RG11 (no punch-through, no signed EAC)
  * etc2comp (Apache-2.0, https://github.com/google/etc2comp), built locally; $ETC2COMP points at EtcTool. Run with
    -errormetric numeric (plain squared error, what PSNR measures) at -effort 100 (its best) and 40.
Decoding: texture2ddecoder (pip) for the colour formats, the EAC formula of the specification (section 22) below.

usage: ETC2COMP=/path/to/EtcTool python3 gen_etc_encode.py            fixtures + oracle.txt
       ETC2COMP=... python3 gen_etc_encode.py --kodak DIR [--formats f,..] [--efforts 100,40]
                                                                     full-size table (stdout), with timings
   (python with etcpak, texture2ddecoder, numpy, pillow)
"""
import os
import struct
import subprocess
import sys
import tempfile
import time
import numpy as np
import etcpak
import texture2ddecoder as t2d
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, '..', 'fixtures', 'etc_encode')
ETC2COMP = os.environ.get('ETC2COMP', 'EtcTool')

FORMATS = ['Etc1', 'Etc2Rgb', 'Etc2A1', 'Etc2Rgba', 'R11', 'Rg11', 'R11s', 'Rg11s']
ETC2COMP_FMT = {'Etc1': 'ETC1', 'Etc2Rgb': 'RGB8', 'Etc2A1': 'RGB8A1', 'Etc2Rgba': 'RGBA8', 'R11': 'R11',
                'Rg11': 'RG11', 'R11s': 'SIGNED_R11', 'Rg11s': 'SIGNED_RG11'}
BPB = {'Etc2Rgba': 16, 'Rg11': 16, 'Rg11s': 16}

EAC_MOD = [
    [-3, -6, -9, -15, 2, 5, 8, 14], [-3, -7, -10, -13, 2, 6, 9, 12], [-2, -5, -8, -13, 1, 4, 7, 12],
    [-2, -4, -6, -13, 1, 3, 5, 12], [-3, -6, -8, -12, 2, 5, 7, 11], [-3, -7, -9, -11, 2, 6, 8, 10],
    [-4, -7, -8, -11, 3, 6, 7, 10], [-3, -5, -8, -11, 2, 4, 7, 10], [-2, -6, -8, -10, 1, 5, 7, 9],
    [-2, -5, -8, -10, 1, 4, 7, 9], [-2, -4, -8, -10, 1, 3, 7, 9], [-2, -5, -7, -10, 1, 4, 6, 9],
    [-3, -4, -7, -10, 2, 3, 6, 9], [-1, -2, -3, -10, 0, 1, 2, 9], [-4, -6, -8, -9, 3, 5, 7, 8],
    [-3, -5, -7, -9, 2, 4, 6, 8]]


# ---------------------------------------------------------------- input mapping (must match EtcEncodeTests)

def alpha_pattern(w, h, binary):
    a = np.zeros((h, w), np.int64)
    for y in range(h):
        for x in range(w):
            dx, dy = x - w * 0.3, y - h * 0.6
            v = 0 if dx * dx + dy * dy < (h * 0.2) * (h * 0.2) else (x * 255 // (w // 2) if x < w // 2 else 255 - (y * 7 % 64))
            a[y, x] = (255 if v >= 128 else 0) if binary else v
    return a.astype(np.uint8)


def snorm(v):
    return np.maximum(-32767, np.round((v.astype(np.float64) - 128) * 32767 / 127)).astype(np.int64)


def ext11(x):
    return (x << 5) | (x >> 6)


def ext11s(x):
    m = np.abs(x)
    return np.sign(x) * ((m << 5) | (m >> 5))


# ---------------------------------------------------------------- decoding

def eac11(blocks, w, h, signed):
    """One EAC channel per 8 byte block (raster order) -> 11 bit values (h, w)."""
    out = np.zeros((h, w), np.int64)
    bw = w // 4
    for k in range(len(blocks) // 8):
        v = int.from_bytes(blocks[k * 8:k * 8 + 8], 'big')
        mul, tab = v >> 52 & 15, v >> 48 & 15
        if signed:
            b = v >> 56
            b = max(b - 256 if b >= 128 else b, -127) * 8
        else:
            b = (v >> 56) * 8 + 4
        by, bx = divmod(k, bw)
        for i in range(16):   # pixels a..p down the columns
            x, y = i >> 2, i & 3
            m = EAC_MOD[tab][v >> (45 - 3 * i) & 7]
            val = b + (m * mul * 8 if mul else m)
            out[by * 4 + y, bx * 4 + x] = min(max(val, -1023), 1023) if signed else min(max(val, 0), 2047)
    return out


def split_channels(data, nblocks, two):
    """RG11: 16 byte blocks, R then G."""
    if not two:
        return [data[:nblocks * 8]]
    r = b''.join(data[k * 16:k * 16 + 8] for k in range(nblocks))
    g = b''.join(data[k * 16 + 8:k * 16 + 16] for k in range(nblocks))
    return [r, g]


def psnr(img, fmt, data):
    """img: (h, w, 4) uint8 input; data: the blocks. Returns (colour PSNR, alpha PSNR or None)."""
    h, w = img.shape[:2]
    nb = (w // 4) * (h // 4)
    if fmt in ('Etc1', 'Etc2Rgb', 'Etc2A1', 'Etc2Rgba'):
        dec = {'Etc1': t2d.decode_etc1, 'Etc2Rgb': t2d.decode_etc2, 'Etc2A1': t2d.decode_etc2a1, 'Etc2Rgba': t2d.decode_etc2a8}[fmt]
        out = np.frombuffer(dec(bytes(data[:nb * BPB.get(fmt, 8)]), w, h), np.uint8).reshape(h, w, 4)[:, :, [2, 1, 0, 3]]
        x = img.astype(np.float64); y = out.astype(np.float64)
        mask = img[:, :, 3] >= 128 if fmt == 'Etc2A1' else np.ones((h, w), bool)
        if fmt == 'Etc2A1':
            assert np.all((out[:, :, 3] == 255) == mask), 'punch-through alpha'
        se = ((x[:, :, :3] - y[:, :, :3]) ** 2)[mask].sum()
        pc = 99 if se == 0 else 10 * np.log10(255.0 ** 2 * 3 * mask.sum() / se)
        pa = None
        if fmt == 'Etc2Rgba':
            sa = ((x[:, :, 3] - y[:, :, 3]) ** 2).sum()
            pa = 99 if sa == 0 else 10 * np.log10(255.0 ** 2 * w * h / sa)
        return pc, pa
    signed = fmt.endswith('s')
    two = fmt.startswith('Rg')
    chans = split_channels(data, nb, two)
    se, n = 0.0, 0
    for c, blocks in enumerate(chans):
        v = eac11(blocks, w, h, signed)
        dec = ext11s(v) if signed else ext11(v)
        t = snorm(img[:, :, c]) if signed else img[:, :, c].astype(np.int64) * 257
        se += float(((dec - t).astype(np.float64) ** 2).sum()); n += w * h
    peak = 65534.0 if signed else 65535.0
    return (99 if se == 0 else 10 * np.log10(peak * peak * n / se)), None


# ---------------------------------------------------------------- oracles

def run_etcpak(img, fmt):
    h, w = img.shape[:2]
    raw = np.ascontiguousarray(img).tobytes()
    nb = (w // 4) * (h // 4)
    f = {'Etc1': etcpak.compress_etc1_rgb, 'Etc2Rgb': etcpak.compress_etc2_rgb, 'Etc2Rgba': etcpak.compress_etc2_rgba,
         'R11': etcpak.compress_eac_r, 'Rg11': etcpak.compress_eac_rg}.get(fmt)
    if f is None:
        return None, 0
    t = time.perf_counter()
    data = f(raw, w, h)
    dt = time.perf_counter() - t
    return bytes(data[:nb * BPB.get(fmt, 8)]), dt


def read_ktx(path):
    d = open(path, 'rb').read()
    assert d[:12] == b'\xabKTX 11\xbb\r\n\x1a\n'
    kv = struct.unpack('<I', d[60:64])[0]
    off = 64 + kv
    size = struct.unpack('<I', d[off:off + 4])[0]
    return d[off + 4:off + 4 + size]


def run_etc2comp(img, fmt, effort, threads=8):
    with tempfile.TemporaryDirectory() as td:
        src, dst = os.path.join(td, 'in.png'), os.path.join(td, 'out.ktx')
        Image.fromarray(img, 'RGBA').save(src)
        t = time.perf_counter()
        subprocess.run([ETC2COMP, src, '-format', ETC2COMP_FMT[fmt], '-effort', str(effort), '-errormetric', 'numeric',
                        '-j', str(threads), '-output', dst], check=True, capture_output=True)
        dt = time.perf_counter() - t
        return read_ktx(dst), dt


def prepare(img, fmt):
    """The input of a format: alpha pattern for the alpha formats, opaque otherwise."""
    h, w = img.shape[:2]
    o = img.copy()
    if fmt == 'Etc2A1':
        o[:, :, 3] = alpha_pattern(w, h, True)
    elif fmt == 'Etc2Rgba':
        o[:, :, 3] = alpha_pattern(w, h, False)
    else:
        o[:, :, 3] = 255
    return o


def load(path):
    return np.array(Image.open(path).convert('RGBA'))


def check_signed_mapping():
    """etc2comp reads 8 bit input as signed (v - 128) / 127: constant images decode to snorm(v) within a few 11 bit steps."""
    for v in (0, 1, 64, 127, 128, 129, 200, 255):
        img = np.full((4, 4, 4), v, np.uint8)
        data, _ = run_etc2comp(img, 'R11s', 100, 1)
        got = ext11s(eac11(data, 4, 4, True))[0, 0]
        want = snorm(np.array([v]))[0]
        assert abs(int(got) - int(want)) <= 128, (v, got, want)   # etc2comp is a few 11 bit steps off on constants


# ---------------------------------------------------------------- fixtures

def synthetic(w=64, h=64):
    """Gradients, hard edges between saturated colours, thin lines and a noise patch."""
    rng = np.random.default_rng(2026)
    img = np.zeros((h, w, 4), np.uint8)
    img[:, :, 3] = 255
    yy, xx = np.mgrid[0:h, 0:w]
    img[:, :, 0] = (xx * 4) % 256
    img[:, :, 1] = (yy * 4) % 256
    img[:, :, 2] = ((xx + yy) * 2) % 256
    img[h // 2:, :w // 2, :3] = [[220, 30, 40]]
    img[h // 2:, :w // 2, :3][((xx[h // 2:, :w // 2] // 5 + yy[h // 2:, :w // 2] // 5) % 2) == 1] = [20, 60, 200]
    img[::7, w // 2:, :3] = 255
    img[h * 3 // 4:, w * 3 // 4:, :3] = rng.integers(0, 256, (h - h * 3 // 4, w - w * 3 // 4, 3))
    return img


def make_fixtures(kodak):
    os.makedirs(OUT, exist_ok=True)
    imgs = {}
    for name, x, y in (('kodim23', 304, 128), ('kodim13', 256, 192)):
        crop = load(os.path.join(kodak, name + '.png'))[y:y + 128, x:x + 128]
        imgs[name + '_crop'] = crop
    imgs['synthetic'] = synthetic()
    for name, img in imgs.items():
        Image.fromarray(img[:, :, :3], 'RGB').save(os.path.join(OUT, name + '.png'), optimize=True)
    lines = []
    for name, img in imgs.items():
        for fmt in FORMATS:
            inp = prepare(img, fmt)
            res = []
            data, _ = run_etcpak(inp, fmt)
            if data is not None:
                res.append(('etcpak', psnr(inp, fmt, data)))
            for effort in (40, 100):
                data, _ = run_etc2comp(inp, fmt, effort)
                res.append((f'etc2comp{effort}', psnr(inp, fmt, data)))
            for oracle, (pc, pa) in res:
                lines.append(f'{name} {fmt} {oracle} {pc:.3f}' + (f' {pa:.3f}' if pa is not None else ''))
                print(lines[-1], flush=True)
    with open(os.path.join(OUT, 'oracle.txt'), 'w') as f:
        f.write('# image format oracle colour-PSNR [alpha-PSNR]; written by tests/oracle/gen_etc_encode.py\n')
        f.write('\n'.join(lines) + '\n')


def kodak_table(kodak, formats, efforts):
    files = sorted(p for p in os.listdir(kodak) if p.endswith('.png'))
    for fmt in formats:
        acc = {}
        for p in files:
            inp = prepare(load(os.path.join(kodak, p)), fmt)
            mp = inp.shape[0] * inp.shape[1] / 1e6
            runs = []
            data, dt = run_etcpak(inp, fmt)
            if data is not None:
                runs.append(('etcpak', data, dt))
            for e in efforts:
                data, dt = run_etc2comp(inp, fmt, e)
                runs.append((f'etc2comp{e}', data, dt))
            for oracle, data, dt in runs:
                pc, pa = psnr(inp, fmt, data)
                a = acc.setdefault(oracle, [0.0, 0.0, 0.0, 0.0])
                a[0] += pc; a[1] += pa or 0; a[2] += dt; a[3] += mp
        for oracle, (pc, pa, dt, mp) in acc.items():
            n = len(files)
            print(f'{fmt} {oracle}: mean {pc / n:.3f} dB' + (f' alpha {pa / n:.3f}' if fmt == 'Etc2Rgba' else '')
                  + f', {mp / dt:.2f} MP/s', flush=True)


if __name__ == '__main__':
    if '--kodak' in sys.argv:
        kodak = sys.argv[sys.argv.index('--kodak') + 1]
        formats = sys.argv[sys.argv.index('--formats') + 1].split(',') if '--formats' in sys.argv else FORMATS
        efforts = [int(e) for e in sys.argv[sys.argv.index('--efforts') + 1].split(',')] if '--efforts' in sys.argv else [100, 40]
        kodak_table(kodak, formats, efforts)
    else:
        check_signed_mapping()
        make_fixtures(os.environ.get('KODAK', os.path.join(HERE, 'kodak')))
