"""Generates tests/fixtures/astc_encode: test images for the ASTC encoder and ARM astcenc's encodings of them.

The images (PXRF sidecars, see pxref.py) are built from tests/fixtures/jpeg/photo_444.jpg (160 x 120, decoded by PIL):

  photo.ref        RGBA8, opaque
  photo_alpha.ref  RGBA8, alpha: a horizontal ramp, a transparent corner and a band of hard 0 / 255 stripes
  photo_hdr.ref    RGBA float16: the photo in linear light under an exposure ramp of 12 stops plus a few bright
                   highlights (up to about 400 times the base), alpha 1

For every footprint in SIZES and every astcenc preset in PRESETS (and for the other footprints ldr at -medium) the
oracle writes <variant>_<W>x<H>_<preset>.astc
(astcenc's .astc file: 16 byte header, then the blocks in raster order):

  ldr     photo.ref,       -cl (LDR linear)
  alpha   photo_alpha.ref, -cl
  srgb    photo.ref,       -cs (LDR sRGB)
  hdr     photo_hdr.ref,   -ch (HDR RGB, LDR alpha)

The C# tests (AstcEncodeTests.cs) decode these blocks with AstcBlocks and use their quality (PSNR, or the RMS of
log2 ratios for HDR) as the gates for the encoder's output on the same images: the gates come from the oracle's
encodings, never from the encoder under test.

Oracle (only run, never read or ported): ARM astcenc (Apache-2.0, https://github.com/ARM-software/astc-encoder),
built locally; $ASTCENC points at the binary. It reads KTX 1 input (uint8 or float16), written here.

usage: ASTCENC=/path/to/astcenc ~/.venvs/etcref/bin/python gen_astc_encode.py   (numpy, pillow)
"""
import os
import struct
import subprocess
import tempfile
import numpy as np
from PIL import Image
from pxref import write_ref

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, '..', 'fixtures', 'astc_encode')
ASTCENC = os.environ.get('ASTCENC', 'astcenc')
SIZES = [(4, 4), (6, 6), (8, 8)]
PRESETS = ['fast', 'medium', 'thorough']
ALL_SIZES = [(4, 4), (5, 4), (5, 5), (6, 5), (6, 6), (8, 5), (8, 6), (10, 5), (10, 6), (8, 8), (10, 8), (10, 10), (12, 10), (12, 12)]


def write_ktx(path, arr):
    """KTX 1 input for astcenc: arr (H, W, 4) uint8 or float16, top row first."""
    h, w, _ = arr.shape
    gl = (0x1401, 1, 0x1908, 0x8058, 0x1908) if arr.dtype == np.uint8 else (0x140B, 2, 0x1908, 0x881A, 0x1908)
    hdr = b'\xabKTX 11\xbb\r\n\x1a\n' + struct.pack('<13I', 0x04030201, *gl, w, h, 0, 0, 1, 1, 0)
    data = np.ascontiguousarray(arr).tobytes()
    with open(path, 'wb') as f:
        f.write(hdr + struct.pack('<I', len(data)) + data)


def run(*args):
    r = subprocess.run([ASTCENC, *args, '-silent'], capture_output=True, text=True)
    if r.returncode != 0:
        raise RuntimeError(f'astcenc {args}: {r.stdout} {r.stderr}')


def images():
    rgb = np.asarray(Image.open(os.path.join(HERE, '..', 'fixtures', 'jpeg', 'photo_444.jpg')).convert('RGB'))
    h, w, _ = rgb.shape
    photo = np.concatenate([rgb, np.full((h, w, 1), 255, np.uint8)], axis=2)

    alpha = photo.copy()
    x = np.arange(w)[None, :].repeat(h, 0)
    y = np.arange(h)[:, None].repeat(w, 1)
    a = (x * 255 // (w - 1)).astype(np.uint8)
    a[(x < 24) & (y < 24)] = 0
    band = (y >= 80) & (y < 100)
    a[band] = np.where((x[band] // 3) % 2 == 0, 0, 255)
    alpha[:, :, 3] = a

    lin = rgb / 255.0
    lin = np.where(lin <= 0.04045, lin / 12.92, ((lin + 0.055) / 1.055) ** 2.4)
    exposure = 2.0 ** (12.0 * x / w - 6)
    boost = np.zeros((h, w))
    for (cx, cy, r, s) in [(30, 20, 6, 400), (120, 40, 10, 60), (80, 100, 4, 200), (140, 90, 14, 20)]:
        boost += s * np.exp(-((x - cx) ** 2 + (y - cy) ** 2) / (r * r))
    v = lin * (exposure * (1 + boost))[:, :, None]
    hdr = np.concatenate([v, np.ones((h, w, 1))], axis=2).astype(np.float16)
    return photo, alpha, hdr


def main():
    os.makedirs(OUT, exist_ok=True)
    photo, alpha, hdr = images()
    write_ref(os.path.join(OUT, 'photo.ref'), photo)
    write_ref(os.path.join(OUT, 'photo_alpha.ref'), alpha)
    write_ref(os.path.join(OUT, 'photo_hdr.ref'), hdr)
    with tempfile.TemporaryDirectory() as tmp:
        inputs = {}
        for name, arr in (('photo', photo), ('alpha', alpha), ('hdr', hdr)):
            inputs[name] = os.path.join(tmp, name + '.ktx')
            write_ktx(inputs[name], arr)
        for variant, src, profile in (('ldr', 'photo', '-cl'), ('alpha', 'alpha', '-cl'), ('srgb', 'photo', '-cs'), ('hdr', 'hdr', '-ch')):
            for bw, bh in SIZES:
                for preset in PRESETS:
                    out = os.path.join(OUT, f'{variant}_{bw}x{bh}_{preset}.astc')
                    run(profile, inputs[src], out, f'{bw}x{bh}', '-' + preset)
                    print(out)
        for bw, bh in ALL_SIZES:
            if (bw, bh) not in SIZES:
                out = os.path.join(OUT, f'ldr_{bw}x{bh}_medium.astc')
                run('-cl', inputs['photo'], out, f'{bw}x{bh}', '-medium')
                print(out)


if __name__ == '__main__':
    main()
