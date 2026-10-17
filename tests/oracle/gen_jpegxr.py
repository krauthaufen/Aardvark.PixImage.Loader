"""JPEG XR fixtures: tests/fixtures/jpegxr/*.jxr written by jxrlib's JxrEncApp (and imagecodecs, for CMYK, BlackWhite and
n-channel images, which JxrEncApp does not read) from our synthetic images, each with a .jxr.ref of jxrlib's decode in
the reference output form of jxr_oracle.py.

Covered: every pixel format JxrEncApp writes, lossless and lossy (-q quality and -q QP), overlap 0 / 1 / 2, spatial and
frequency order, 4:2:0 / 4:2:2 / 4:4:4 / Y only coding of RGB, tiles (uniform and given sizes), flexbits trimming, dropped
subbands, planar and interleaved alpha with its own quantization, RGBE, and an image wide enough for the adaptive scan
reset every 16 macroblocks.

usage: python3 gen_jpegxr.py          (needs JxrEncApp / JxrDecApp: $PIXCODEC_JXRLIB or the PATH, and imagecodecs)
"""
import os
import subprocess
import sys
import tempfile
import numpy as np
import tifffile
from PIL import Image
from pxref import write_ref
from jxr_oracle import decode, jxrenc

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'fixtures', 'jpegxr')
TMP = tempfile.mkdtemp()


def pattern(w, h, c, seed=1):
    """Gradients, a checker board and some noise in [0, 1], one pattern per channel."""
    rng = np.random.default_rng(seed)
    y, x = np.mgrid[0:h, 0:w].astype(np.float64)
    ch = []
    for i in range(c):
        v = 0.5 + 0.5 * np.sin(x / (7.0 + 3 * i) + i) * np.cos(y / (5.0 + 2 * i))
        v = 0.6 * v + 0.3 * (((x // 8 + y // 8 + i) % 2)) + 0.1 * rng.random((h, w))
        ch.append(v)
    return np.clip(np.stack(ch, -1), 0, 1)


# JxrEncApp -c code -> (input kind, channels)
INPUTS = {
    0: ('bmp', 3), 2: ('u8', 1), 3: ('u16', 1), 4: ('fix16', 1), 5: ('half', 1), 7: ('fix32', 1), 8: ('float', 1),
    9: ('u8', 3), 10: ('u16', 3), 11: ('fix16', 3), 12: ('half', 3), 14: ('fix32', 3), 15: ('float', 3),
    22: ('u8', 4), 23: ('u16', 4), 24: ('fix16', 4), 25: ('half', 4), 27: ('fix32', 4), 28: ('float', 4),
    32: ('u8', 5), 34: ('bmp', 3), 16: ('hdr', 3),
}


def write_input(code, w, h, seed):
    kind, c = INPUTS[code]
    p = pattern(w, h, c, seed)
    if kind == 'bmp':
        path = os.path.join(TMP, 'in.bmp')
        Image.fromarray((p * 255).round().astype(np.uint8)).save(path)
        return path
    if kind == 'hdr':
        path = os.path.join(TMP, 'in.hdr')
        rgbe = np.zeros((h, w, 4), np.uint8)
        rgbe[:, :, :3] = (p * 127 + 128).astype(np.uint8)
        rgbe[:, :, 3] = 118 + (np.random.default_rng(seed).integers(0, 20, (h, w)))
        rgbe[0, 0] = 0
        with open(path, 'wb') as f:
            f.write(f'#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y {h} +X {w}\n'.encode() + rgbe.tobytes())
        return path
    if kind == 'u8':
        a = (p * 255).round().astype(np.uint8)
    elif kind == 'u16':
        a = (p * 65535).round().astype(np.uint16)
    elif kind == 'fix16':
        a = ((p * 3 - 1) * 8192).round().astype(np.int16)
    elif kind == 'fix32':
        a = ((p * 3 - 1) * 16777216).round().astype(np.int32)
    elif kind == 'half':
        a = (p * 3 - 1).astype(np.float16)
    else:
        a = (np.exp(p * 6 - 3) - 0.5).astype(np.float32)
    if c == 1:
        a = a[:, :, 0]
    kw = dict(metadata=None, photometric='minisblack' if c == 1 else 'separated' if c == 5 else 'rgb')
    if c in (4, 5):
        kw['extrasamples'] = [2]
    path = os.path.join(TMP, 'in.tif')
    tifffile.imwrite(path, a, **kw)
    return path


def save(name, path):
    os.makedirs(ROOT, exist_ok=True)
    dst = os.path.join(ROOT, name + '.jxr')
    with open(path, 'rb') as f:
        data = f.read()
    with open(dst, 'wb') as f:
        f.write(data)
    a, _ = decode(dst)
    if a is None:
        os.remove(dst)
        print(f'{name}: jxrlib cannot decode it, left out')
        return
    write_ref(dst + '.ref', a)
    print(f'{name}: {len(data)} bytes')


def jxr(name, code, opts, w=70, h=45, seed=1):
    src = write_input(code, w, h, seed)
    out = os.path.join(TMP, 'out.jxr')
    if os.path.exists(out):
        os.remove(out)
    r = subprocess.run([jxrenc(), '-i', src, '-o', out, '-c', str(code)] + opts, capture_output=True, text=True)
    if r.returncode != 0 or not os.path.exists(out) or os.path.getsize(out) == 0:
        print(f'{name}: JxrEncApp failed ({r.returncode})')
        return
    save(name, out)


def ic(name, a, **kw):
    import imagecodecs
    out = os.path.join(TMP, 'out.jxr')
    with open(out, 'wb') as f:
        f.write(imagecodecs.jpegxr_encode(a, **kw))
    save(name, out)


def main():
    if jxrenc() is None:
        sys.exit('JxrEncApp not found ($PIXCODEC_JXRLIB)')
    # every format JxrEncApp writes, lossless and lossy
    names = {0: 'bgr24', 2: 'gray8', 3: 'gray16', 4: 'grayfix16', 5: 'grayhalf', 7: 'grayfix32', 8: 'grayfloat',
             9: 'rgb24', 10: 'rgb48', 11: 'rgbfix48', 12: 'rgbhalf48', 14: 'rgbfix96', 15: 'rgbfloat128',
             22: 'bgra32', 23: 'rgba64', 24: 'rgbafix64', 25: 'rgbahalf64', 27: 'rgbafix128', 28: 'rgbafloat128',
             32: 'cmyka40', 34: 'bgr32', 16: 'rgbe'}
    for code, n in names.items():
        jxr(f'{n}_lossless', code, [])
        jxr(f'{n}_q60', code, ['-q', '0.6'], seed=2)
    # coding modes of 24 bit RGB
    for q in ('0.9', '0.75', '0.5', '0.25', '0.05', '20', '90'):
        for l in ('0', '1', '2'):
            jxr(f'rgb24_q{q}_l{l}', 9, ['-q', q, '-l', l], seed=3)
            jxr(f'rgb24_q{q}_l{l}_spatial', 9, ['-q', q, '-l', l, '-f'], seed=3)
    for d, dn in (('0', 'y'), ('1', '420'), ('2', '422'), ('3', '444')):
        for l in ('0', '1', '2'):
            jxr(f'rgb24_{dn}_l{l}', 9, ['-q', '0.7', '-d', d, '-l', l], 82, 50, seed=4)
            jxr(f'rgb24_{dn}_l{l}_spatial', 9, ['-q', '0.7', '-d', d, '-l', l, '-f'], 82, 50, seed=4)
        jxr(f'rgb24_{dn}_lossless', 9, ['-d', d], 82, 50, seed=4)
    # tiles
    jxr('rgb24_tiles_u23', 9, ['-q', '0.7', '-U', '2', '3'], 150, 100, seed=5)
    jxr('rgb24_tiles_u23_spatial', 9, ['-q', '0.7', '-U', '2', '3', '-f'], 150, 100, seed=5)
    jxr('rgb24_tiles_vh', 9, ['-q', '0.6', '-V', '2', '3', '-H', '1', '2'], 150, 100, seed=5)
    jxr('rgb24_tiles_vh_spatial_l2', 9, ['-q', '0.6', '-V', '2', '3', '-H', '1', '2', '-l', '2', '-f'], 150, 100, seed=5)
    jxr('rgb24_tiles_420_l2', 9, ['-q', '0.5', '-U', '3', '2', '-l', '2', '-d', '1'], 150, 100, seed=5)
    jxr('rgb24_tiles_422_l2', 9, ['-q', '0.5', '-U', '3', '2', '-l', '2', '-d', '2'], 150, 100, seed=5)
    jxr('rgb24_tiles_lossless', 9, ['-U', '2', '2'], 150, 100, seed=5)
    jxr('rgba64_tiles_interleaved', 23, ['-q', '0.7', '-U', '2', '2', '-a', '3', '-Q', '40'], 150, 100, seed=5)
    # flexbits trimming, dropped subbands
    for t in ('2', '6', '15'):
        jxr(f'rgb24_trim{t}', 9, ['-q', '0.85', '-F', t], seed=6)
        jxr(f'rgb24_trim{t}_spatial', 9, ['-q', '0.85', '-F', t, '-f'], seed=6)
    for s, sn in (('1', 'noflexbits'), ('2', 'nohighpass'), ('3', 'dconly')):
        jxr(f'rgb24_{sn}', 9, ['-q', '0.85', '-s', s], seed=7)
        jxr(f'rgb24_{sn}_spatial', 9, ['-q', '0.85', '-s', s, '-f'], seed=7)
        jxr(f'gray16_{sn}', 3, ['-q', '0.85', '-s', s], seed=7)
    # alpha: planar / interleaved, quantized
    for code, n in ((22, 'bgra32'), (23, 'rgba64'), (24, 'rgbafix64'), (25, 'rgbahalf64'), (27, 'rgbafix128'), (28, 'rgbafloat128')):
        jxr(f'{n}_planar_q', code, ['-a', '2', '-q', '0.6', '-Q', '30'], seed=8)
        jxr(f'{n}_interleaved', code, ['-a', '3'], seed=8)
        jxr(f'{n}_interleaved_q', code, ['-a', '3', '-q', '0.6', '-Q', '30', '-f'], seed=8)
        jxr(f'{n}_interleaved_q_l2', code, ['-a', '3', '-q', '0.5', '-Q', '60', '-l', '2'], seed=8)
    jxr('cmyka40_interleaved', 32, ['-a', '3'], seed=8)
    # wider than 16 macroblocks: adaptive context and scan totals reset per 16 macroblock columns
    jxr('rgb24_wide', 9, ['-q', '0.7'], 300, 40, seed=9)
    jxr('rgb24_wide_lossless_spatial', 9, ['-f'], 300, 40, seed=9)
    jxr('gray8_tall_odd', 2, ['-q', '0.8', '-l', '2'], 21, 133, seed=9)
    # imagecodecs (its jxrlib): CMYK, BlackWhite, n channels
    p = pattern(70, 45, 8, 10)
    ic('cmyk32_lossless', (p[:, :, :4] * 255).round().astype(np.uint8), photometric=5)
    ic('cmyk32_q70', (p[:, :, :4] * 255).round().astype(np.uint8), photometric=5, level=0.7)
    ic('cmyk64_lossless', (p[:, :, :4] * 65535).round().astype(np.uint16), photometric=5)
    ic('cmyk64_q70', (p[:, :, :4] * 65535).round().astype(np.uint16), photometric=5, level=0.7)
    ic('blackwhite_lossless', p[:, :, 0] > 0.5)
    ic('blackwhite_q70', p[:, :, 0] > 0.5, level=0.7)
    for n in (5, 6, 8):
        ic(f'channels{n}_8_lossless', (p[:, :, :n] * 255).round().astype(np.uint8))
        ic(f'channels{n}_8_q70', (p[:, :, :n] * 255).round().astype(np.uint8), level=0.7)
    ic('channels5_16_lossless', (p[:, :, :5] * 65535).round().astype(np.uint16))
    ic('channels6_16_q70', (p[:, :, :6] * 65535).round().astype(np.uint16), level=0.7)


if __name__ == '__main__':
    main()
