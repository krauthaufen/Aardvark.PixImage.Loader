"""Generates tests/fixtures/astc: plain .astc files (astcenc's container: 16 byte header, blocks in raster order) made
by ARM astcenc from our synthetic images, with astcenc's own decodes as references.

  name.astc       the file astcenc -c? wrote
  name.astc.ref   astcenc's decode of it (-dl for LDR, -ds for sRGB, -dh for HDR), slice 0
  name.astc.<z>.ref  every slice of a volume

Covered: 2D LDR / sRGB / HDR footprints, an image whose size is not a multiple of the footprint, 3D footprints
(4x4x4, 3x3x3, 5x4x4) of a volume, a volume with a 2D footprint (block depth 1), and an HDR volume.

Oracle (only run, never read or ported): ARM astcenc (Apache-2.0, https://github.com/ARM-software/astc-encoder), built
locally; $ASTCENC points at the binary.

usage: ASTCENC=/path/to/astcenc python3 gen_astc.py
"""
import os
import struct
import subprocess
import tempfile
import numpy as np
from pxref import write_ref

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'fixtures', 'astc')
ASTCENC = os.environ.get('ASTCENC', 'astcenc')
TMP = tempfile.mkdtemp()


def write_ktx(path, arr):
    """KTX 1 input for astcenc: arr (D, H, W, 4) uint8 or float16, top row first."""
    d, h, w, _ = arr.shape
    gl = (0x1401, 1, 0x1908, 0x8058, 0x1908) if arr.dtype == np.uint8 else (0x140B, 2, 0x1908, 0x881A, 0x1908)
    hdr = b'\xabKTX 11\xbb\r\n\x1a\n' + struct.pack('<13I', 0x04030201, *gl, w, h, d if d > 1 else 0, 0, 1, 1, 0)
    data = np.ascontiguousarray(arr).tobytes()
    with open(path, 'wb') as f:
        f.write(hdr + struct.pack('<I', len(data)) + data)


def read_ktx(path):
    """astcenc's KTX output as (D, H, W, 4); RGB output (opaque) gets alpha 1."""
    b = open(path, 'rb').read()
    h = struct.unpack('<13I', b[12:64])
    gl_type, fmt, w, hh, d, kv = h[1], h[3], h[6], h[7], max(h[8], 1), h[12]
    o = 64 + kv
    n = struct.unpack('<I', b[o:o + 4])[0]
    dt = np.uint8 if gl_type == 0x1401 else np.float16
    c = {0x1908: 4, 0x1907: 3}[fmt]
    a = np.frombuffer(b[o + 4:o + 4 + n], dt).reshape(d, hh, w, c)
    if c == 3:
        a = np.concatenate([a, np.full((d, hh, w, 1), 255 if dt == np.uint8 else 1.0, dt)], axis=3)
    return a


def run(*args):
    r = subprocess.run([ASTCENC, *args, '-silent'], capture_output=True, text=True)
    if r.returncode != 0:
        raise RuntimeError(f'astcenc {args}: {r.stdout} {r.stderr}')


def volume(w, h, d, seed, hdr=False):
    rng = np.random.default_rng(seed)
    z, y, x = np.mgrid[0:d, 0:h, 0:w].astype(np.float64)
    v = np.stack([0.5 + 0.5 * np.sin(x / 4 + z / 3), 0.5 + 0.5 * np.cos(y / 5 - z / 2), ((x // 4 + y // 4 + z // 2) % 2) * 0.6 + 0.3 * rng.random((d, h, w)),
                  np.clip(1.1 - x / w + 0.1 * np.sin(z), 0, 1)], -1)
    if hdr:
        v[..., :3] *= np.exp2(6 * x / w - 2)[..., None]
        v[..., 3] = 1
        return v.astype(np.float16)
    return np.round(v * 255).astype(np.uint8)


def make(name, arr, enc, dec, block):
    src = os.path.join(TMP, name + '.ktx')
    write_ktx(src, arr)
    out = os.path.join(OUT, name + '.astc')
    run(enc, src, out, block, '-medium')
    back = os.path.join(TMP, name + '.out.ktx')
    run(dec, out, back)
    ref = read_ktx(back)
    if dec == '-ds':
        # astcenc's sRGB decode expands alpha like the colour channels; the specification (23.19) decodes it as
        # decode_float16 of the bit replicated endpoints, which is astcenc's HDR decode of the same blocks, rounded
        hb = os.path.join(TMP, name + '.h.ktx')
        run('-dH', out, hb)
        ref = ref.copy()
        ref[..., 3] = np.floor(read_ktx(hb)[..., 3].astype(np.float64) * 255 + 0.5).astype(np.uint8)
    write_ref(out + '.ref', ref[0])
    if ref.shape[0] > 1:
        for z in range(ref.shape[0]):
            write_ref(out + f'.{z}.ref', ref[z])


def main():
    os.makedirs(OUT, exist_ok=True)
    make('ldr_5x5', volume(37, 21, 1, 1), '-cl', '-dl', '5x5')
    make('ldr_8x6', volume(64, 48, 1, 2), '-cl', '-dl', '8x6')
    make('srgb_6x6', volume(40, 30, 1, 3), '-cs', '-ds', '6x6')
    make('hdr_4x4', volume(32, 20, 1, 4, hdr=True), '-ch', '-dh', '4x4')
    make('vol_4x4x4', volume(16, 12, 9, 5), '-cl', '-dl', '4x4x4')
    make('vol_3x3x3', volume(10, 7, 5, 6), '-cl', '-dl', '3x3x3')
    make('vol_5x4x4', volume(13, 9, 6, 7), '-cl', '-dl', '5x4x4')
    make('vol_hdr_4x4x4', volume(8, 8, 8, 8, hdr=True), '-ch', '-dh', '4x4x4')
    make('vol2d_6x6', volume(20, 14, 3, 9), '-cl', '-dl', '6x6')
    print('wrote', OUT)


if __name__ == '__main__':
    main()
