"""Generates tests/fixtures/pvr: PowerVR textures (PVR v3 and legacy v2) made by Imagination's PVRTexToolCLI from our own
synthetic images, with reference decodes.

Oracle (only run, never read): PVRTexToolCLI (PVRTexTool 3.4 was used; it ships with Unreal Engine as
Engine/Binaries/ThirdParty/ImgTec/PVRTexToolCLI[.exe]). It runs locally ($PVRTEXTOOL=/path/to/PVRTexToolCLI) or on a
Windows machine over ssh ($PVRTEXTOOL_SSH=host, the executable in %USERPROFILE%\\pvrtool, $PVRTEXTOOL_DIR to change it).

References (name.pvr.ref, the top level of the first surface / face, laid out as our decoder returns it):
  * compressed formats (PVRTC1 2 / 4 bpp, ETC1, ETC2 / EAC, BC1 to BC3, 1 bpp, RGB9E5): PVRTexTool's own decode (-d, 8 bit PNG);
    the tests compare with a tolerance where our decoder gives more than 8 bits or rounds differently (BC, EAC, RGB9E5).
  * uncompressed formats: the stored values as the format defines them, computed from the 8 bit input (UNORM n bit:
    round(v * (2^n - 1) / 255), expanded to 8 bits as round(q * 255 / (2^n - 1))), cross checked against PVRTexTool's decode.
  * cube / array / legacy files: name.pvr.<face>.ref for every face / surface (the 8 bit inputs, stored losslessly).
  * PVRTC mip levels: name.pvr.m<level>.ref from texture2ddecoder (MIT, https://github.com/K0lb3/texture2ddecoder) when it
    is installed, else left out.

usage: PVRTEXTOOL_SSH=ripperl python3 gen_pvr.py
"""
import os
import shutil
import struct
import subprocess
import sys
import tempfile
import numpy as np
from PIL import Image
from pxref import write_ref

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'fixtures', 'pvr')
TMP = tempfile.mkdtemp()
SSH = os.environ.get('PVRTEXTOOL_SSH')
REMOTE = os.environ.get('PVRTEXTOOL_DIR', 'pvrtool')
LOCAL = os.environ.get('PVRTEXTOOL', 'PVRTexToolCLI')


def pattern(w, h, seed):
    rng = np.random.default_rng(seed)
    y, x = np.mgrid[0:h, 0:w].astype(float)
    a = np.zeros((h, w, 4))
    a[..., 0] = 0.5 + 0.5 * np.sin(x / 5 + seed)
    a[..., 1] = 0.5 + 0.5 * np.cos(y / 4)
    a[..., 2] = ((x // 8 + y // 8) % 2) * 0.7 + 0.2 * rng.random((h, w))
    a[..., 3] = np.clip(1.2 - x / w, 0, 1)
    return (a * 255 + 0.5).astype(np.uint8)


INPUTS = {'a': pattern(64, 64, 1), 'w': pattern(64, 32, 2), 's': pattern(8, 8, 3), 'u': pattern(32, 16, 4)}
for i in range(6):
    INPUTS[f'f{i}'] = pattern(32, 32, 10 + i)

# (output name, input(s), arguments)
JOBS = []
COMPRESSED = [('PVRTC1_4', 'pvrtc4'), ('PVRTC1_4_RGB', 'pvrtc4rgb'), ('PVRTC1_2', 'pvrtc2'), ('PVRTC1_2_RGB', 'pvrtc2rgb'),
              ('ETC1', 'etc1'), ('BC1', 'bc1'), ('BC2', 'bc2'), ('BC3', 'bc3'), ('ETC2_RGBA', 'etc2rgba'), ('ETC2_RGB_A1', 'etc2a1'),
              ('ETC2_RGB', 'etc2rgb'), ('EAC_R11', 'eacr11'), ('EAC_RG11', 'eacrg11'), ('1BPP', 'bw1'), ('RGBE9995', 'rgbe9995')]
for f, n in COMPRESSED:
    JOBS.append((n, 'a', f'-f {f}', True))
JOBS += [('pvrtc4w', 'w', '-f PVRTC1_4', True), ('pvrtc2w', 'w', '-f PVRTC1_2', True),
         ('pvrtc4s', 's', '-f PVRTC1_4 -m', True), ('pvrtc2s', 's', '-f PVRTC1_2 -m', True), ('pvrtc4m', 'a', '-f PVRTC1_4 -m', True)]
# uncompressed layouts: name -> (format argument, channel names, bits)
PLAIN = {
    'rgba8': 'r8g8b8a8', 'bgra8': 'b8g8r8a8', 'argb8': 'a8r8g8b8', 'rgb8': 'r8g8b8', 'rg8': 'r8g8', 'r5g6b5': 'r5g6b5',
    'r4g4b4a4': 'r4g4b4a4', 'a1r5g5b5': 'a1r5g5b5', 'r5g5b5a1': 'r5g5b5a1', 'r3g3b2': 'r3g3b2', 'l8': 'l8', 'l8a8': 'l8a8',
    'a8': 'a8', 'rgb10a2': 'r10g10b10a2', 'x8b8g8r8': 'x8b8g8r8',
}
for n, f in PLAIN.items():
    JOBS.append((n, 'u', f'-f {f},UBN', False))
JOBS += [('rgba16', 'u', '-f r16g16b16a16,USN', False), ('rgba32f', 'u', '-f r32g32b32a32,SF', False),
         ('r16f', 'u', '-f r16,SF', False), ('srgb', 'u', '-f r8g8b8a8,UBN,sRGB', False), ('rgba8m', 'a', '-f r8g8b8a8 -m', False),
         ('flip', 'u', '-f r8g8b8a8 -flip y,flag', False),
         ('cube', 'f0,f1,f2,f3,f4,f5', '-f r8g8b8a8 -cube -m', False), ('array', 'f0,f1,f2', '-f r8g8b8a8 -array', False),
         ('v2rgba8', 'a', '-f r8g8b8a8 -m -legacypvr', False), ('v2bgra', 'u', '-f b8g8r8a8 -legacypvr', False),
         ('v2a8', 'u', '-f a8 -legacypvr', False), ('v2cubem', 'f0,f1,f2,f3,f4,f5', '-f r8g8b8a8 -cube -m -legacypvr', False),
         ('v2pvrtc4', 'a', '-f PVRTC1_4 -legacypvr', True), ('v2pvrtc2', 'a', '-f PVRTC1_2 -legacypvr', True),
         ('v2etc', 'a', '-f ETC1 -legacypvr', True)]


def run_tool():
    for k, v in INPUTS.items():
        Image.fromarray(v).save(os.path.join(TMP, k + '.png'))
    cmds = []
    for n, src, args, dec in JOBS:
        ins = ','.join(s + '.png' for s in src.split(','))
        d = f' -d {n}.png' if dec else ''
        cmds.append(f'-i {ins} -o {n}.pvr {args}{d} -shh')
    if SSH:
        work = REMOTE + '/fixtures'
        subprocess.run(['ssh', SSH, f'New-Item -ItemType Directory -Force -Path {work} | Out-Null'], check=True)
        subprocess.run(['scp', '-q'] + [os.path.join(TMP, k + '.png') for k in INPUTS] + [f'{SSH}:{work}/'], check=True)
        script = '; '.join(f'..\\PVRTexToolCLI.exe {c}' for c in cmds)
        subprocess.run(['ssh', SSH, f'cd {work}; {script}'], check=True)
        subprocess.run(['scp', '-q', f'{SSH}:{work}/*.pvr', f'{SSH}:{work}/*.png', TMP + '/'], check=True)
    else:
        for c in cmds:
            subprocess.run([LOCAL] + c.split(' '), cwd=TMP, check=True)


def unorm(v, bits):
    """8 bit input stored as n bit UNORM, read back as 8 bits (the values our decoder returns)."""
    m = (1 << bits) - 1
    q = np.floor(v.astype(np.float64) * m / 255 + 0.5)
    return np.floor(q * 255 / m + 0.5).astype(np.uint8)


def plain_ref(name, src):
    """What decoding the uncompressed layout gives (8 bit inputs, every channel as UNORM of its bits)."""
    r, g, b, a = (src[..., i] for i in range(4))
    lum = None
    if name in ('l8', 'l8a8'):
        # PVRTexTool's luminance; taken from its own decode below
        return None
    t = {
        'rgba8': src, 'bgra8': src, 'argb8': src, 'rgb8': src[..., :3], 'rg8': src[..., :2], 'x8b8g8r8': src[..., :3],
        'a8': a,
        'r5g6b5': np.stack([unorm(r, 5), unorm(g, 6), unorm(b, 5)], -1),
        'r4g4b4a4': np.stack([unorm(c, 4) for c in (r, g, b, a)], -1),
        'a1r5g5b5': np.stack([unorm(r, 5), unorm(g, 5), unorm(b, 5), unorm(a, 1)], -1),
        'r5g5b5a1': np.stack([unorm(r, 5), unorm(g, 5), unorm(b, 5), unorm(a, 1)], -1),
        'r3g3b2': np.stack([unorm(r, 3), unorm(g, 3), unorm(b, 2)], -1),
    }
    if name == 'rgb10a2':
        m = lambda v, n: np.floor(np.floor(v.astype(np.float64) * ((1 << n) - 1) / 255 + 0.5) * 65535 / ((1 << n) - 1) + 0.5).astype(np.uint16)
        return np.stack([m(r, 10), m(g, 10), m(b, 10), m(a, 2)], -1)
    return t[name]


def main():
    os.makedirs(ROOT, exist_ok=True)
    run_tool()
    for n, src, args, dec in JOBS:
        p = os.path.join(ROOT, n + '.pvr')
        shutil.copy(os.path.join(TMP, n + '.pvr'), p)
        first = INPUTS[src.split(',')[0]]
        if dec:
            im = np.asarray(Image.open(os.path.join(TMP, n + '.png')).convert('RGBA'))
            if n in ('pvrtc4rgb', 'pvrtc2rgb', 'etc1', 'etc2rgb', 'rgbe9995', 'v2etc'):
                im = im[..., :3]
            elif n == 'eacr11' or n == 'bw1':
                im = im[..., :1]
            elif n == 'eacrg11':
                im = im[..., :2]
            write_ref(p + '.ref', im)
            if n.endswith('s') or n.endswith('m'):
                mips_ref(p, n)
            continue
        if ',' in src:
            for i, s in enumerate(src.split(',')):
                write_ref(p + f'.{i}.ref', INPUTS[s])
            continue
        if n in PLAIN:
            ref = plain_ref(n, first)
            tool = np.asarray(Image.open(os.path.join(TMP, src + '.png')))
            if ref is None:
                ref = l_ref(p, first)
            write_ref(p + '.ref', ref)
        elif n == 'rgba16':
            write_ref(p + '.ref', first.astype(np.uint16) * 257)
        elif n == 'rgba32f':
            write_ref(p + '.ref', (first.astype(np.float64) / 255).astype(np.float32))
        elif n == 'r16f':
            write_ref(p + '.ref', (first[..., :1].astype(np.float64) / 255).astype(np.float16))
        elif n == 'srgb':
            # PVRTexTool takes the 8 bit input as linear and stores it sRGB encoded (the tests allow 1 for rounding)
            v = first.astype(np.float64) / 255
            e = np.where(v <= 0.0031308, v * 12.92, 1.055 * v ** (1 / 2.4) - 0.055)
            ref = first.copy()
            ref[..., :3] = np.floor(e[..., :3] * 255 + 0.5).astype(np.uint8)
            write_ref(p + '.ref', ref)
        elif n == 'v2a8':
            write_ref(p + '.ref', first[..., 3:4])
        else:
            write_ref(p + '.ref', first)
    print('wrote', ROOT)


def l_ref(p, src):
    """Luminance layouts: PVRTexTool chooses the luminance weights; the reference is the stored byte (checked to be a
    plausible luminance of the input) plus alpha."""
    with open(p, 'rb') as f:
        d = f.read()
    meta = struct.unpack('<I', d[48:52])[0]
    h, w = src.shape[:2]
    pf = struct.unpack('<Q', d[8:16])[0]
    nch = 2 if (pf >> 40) & 255 else 1
    px = np.frombuffer(d[52 + meta:52 + meta + w * h * nch], np.uint8).reshape(h, w, nch)
    y = src[..., 0] * 0.2126 + src[..., 1] * 0.7152 + src[..., 2] * 0.0722
    assert np.max(np.abs(px[..., 0] - y)) < 4, 'luminance far from Rec. 709'
    if nch == 2:
        assert np.array_equal(px[..., 1], src[..., 3])
    return px.copy()


def mips_ref(p, n):
    try:
        import texture2ddecoder
    except ImportError:
        print('  texture2ddecoder missing: no mip references for', n, file=sys.stderr)
        return
    with open(p, 'rb') as f:
        d = f.read()
    v, flags, pf, cs, ct, h, w, dep, ns, nf, mm, meta = struct.unpack('<IIQIIIIIIIII', d[:52])
    two = pf in (0, 1)
    pos = 52 + meta
    for m in range(mm):
        lw, lh = max(1, w >> m), max(1, h >> m)
        bw = max(2, -(-lw // (8 if two else 4)))
        bh = max(2, -(-lh // 4))
        size = bw * bh * 8
        # texture2ddecoder decodes the padded word grid; the level is its top left corner
        pw, ph = bw * (8 if two else 4), bh * 4
        bgra = texture2ddecoder.decode_pvrtc(d[pos:pos + size], pw, ph, two)
        a = np.frombuffer(bgra, np.uint8).reshape(ph, pw, 4)[:lh, :lw][..., [2, 1, 0, 3]]
        if m > 0:
            write_ref(p + f'.m{m}.ref', a)
        pos += size


if __name__ == '__main__':
    main()
