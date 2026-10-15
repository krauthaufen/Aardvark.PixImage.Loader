"""Generates tests/fixtures/basis_enc: KTX2 files written by PixCodec's own ETC1S / UASTC encoders, decoded by basisu.

1. Every name.ktx2 is encoded by PixCodec (tests/oracle/basis_encode_driver) and gets name.ktx2.<level>.<layer>.<face>.ref
   for each of its images: the RGBA32 output of basisu -unpack (the reference transcoder). The tests decode the same
   files with PixCodec and require bit exact agreement, so whatever our encoder writes (Huffman tables, run lengths,
   selector history, prediction symbols, UASTC modes and hints, DFDs) is read the same way by basisu.
2. quality/: synthetic photo-like sources and oracle.txt with basisu's own PSNR (RGB, dB) and bits per pixel for them
   (-uastc at its default level 2, -etc1s -q 128, linear metrics). BasisEncodeTests gates our encoders against these
   numbers; nothing there comes from our output.

basisu (Apache-2.0, https://github.com/BinomialLLC/basis_universal) is only built and run as an oracle, never copied
(see gen_basis.py for building it).

usage: python3 gen_basis_encode.py /path/to/basisu [--keep-temp]
"""
import glob
import os
import re
import shutil
import subprocess
import sys
import tempfile
import numpy as np
from PIL import Image
from pxref import write_ref
from gen_basis import gradient, edges, noise, mixed, gray_alpha, gray

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, '..', 'fixtures', 'basis_enc')
DRIVER = os.path.join(HERE, 'basis_encode_driver')


def build_driver():
    subprocess.run(['dotnet', 'build', '-c', 'Release', DRIVER], check=True, stdout=subprocess.DEVNULL)
    exe = glob.glob(os.path.join(DRIVER, 'bin', 'Release', 'net8.0', 'basis_encode_driver.dll'))
    assert exe, 'driver not built'
    return ['dotnet', exe[0]]


def save_png(path, a):
    Image.fromarray(a, 'RGBA' if a.shape[2] == 4 else 'RGB').save(path)


def unpack_refs(basisu, tmp, out, name):
    ud = os.path.join(tmp, name + '_unpack')
    os.makedirs(ud, exist_ok=True)
    subprocess.run([basisu, '-unpack', out, '-no_ktx', '-output_path', ud],
                   check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    n = 0
    for f in sorted(glob.glob(os.path.join(ud, '*_unpacked_rgba_RGBA32_level_*.png'))):
        m = re.search(r'_level_(\d+)_face_(\d+)_layer(\d+)\.png$', f)
        level, face, layer = int(m.group(1)), int(m.group(2)), int(m.group(3))
        write_ref(f'{out}.{level}.{layer}.{face}.ref', np.asarray(Image.open(f).convert('RGBA')))
        n += 1
    assert n > 0, name
    return n


def encode(driver, basisu, tmp, name, images, args):
    pngs = []
    for i, a in enumerate(images):
        p = os.path.join(tmp, f'{name}_src{i}.png')
        save_png(p, a)
        pngs.append(p)
    out = os.path.join(OUT, name + '.ktx2')
    subprocess.run(driver + [out] + args + pngs, check=True)
    n = unpack_refs(basisu, tmp, out, name)
    print(f'{name}: {os.path.getsize(out)} bytes, {n} images')


def photo(w, h, seed, alpha=False):
    """Photo-like synthetic content: value noise octaves (smooth areas and texture), a few hard shapes, light grain."""
    rng = np.random.default_rng(seed)
    y, x = np.mgrid[0:h, 0:w].astype(np.float64)
    out = np.zeros((h, w, 3))
    for octave, amp in ((32, 0.55), (12, 0.25), (5, 0.12), (2, 0.06)):
        gh, gw = h // octave + 2, w // octave + 2
        grid = rng.random((gh, gw, 3))
        fy, fx = y / octave, x / octave
        iy, ix = fy.astype(int), fx.astype(int)
        ty, tx = fy - iy, fx - ix
        ty, tx = ty * ty * (3 - 2 * ty), tx * tx * (3 - 2 * tx)
        v = (grid[iy, ix] * ((1 - ty) * (1 - tx))[..., None] + grid[iy, ix + 1] * ((1 - ty) * tx)[..., None]
             + grid[iy + 1, ix] * (ty * (1 - tx))[..., None] + grid[iy + 1, ix + 1] * (ty * tx)[..., None])
        out += amp * v
    tint = rng.random(3) * 0.5 + 0.5
    out = out * tint * 255
    for k in range(5):
        cx, cy, r = rng.random() * w, rng.random() * h, 4 + rng.random() * w / 6
        col = rng.random(3) * 255
        m = (x - cx) ** 2 + (y - cy) ** 2 < r * r
        out[m] = out[m] * 0.3 + col * 0.7
    out += rng.normal(0, 2.0, out.shape)
    out = np.clip(out + 0.5, 0, 255).astype(np.uint8)
    if alpha:
        a = np.clip((np.sin(x / 9.0) * np.cos(y / 7.0) * 0.5 + 0.5) * 255 + rng.normal(0, 3, (h, w)), 0, 255).astype(np.uint8)
        out = np.concatenate([out, a[..., None]], -1)
    return out


def psnr_rgb(a, b):
    d = a[..., :3].astype(np.float64) - b[..., :3].astype(np.float64)
    return 10 * np.log10(255.0 ** 2 / np.mean(d * d))


def quality_refs(basisu, tmp):
    qd = os.path.join(OUT, 'quality')
    os.makedirs(qd, exist_ok=True)
    lines = []
    for name, img in (('photo_rgb_192x128', photo(192, 128, 11)), ('photo2_rgb_160x160', photo(160, 160, 12)),
                      ('photo_rgba_128x96', photo(128, 96, 13, True))):
        src = os.path.join(qd, name + '.png')
        save_png(src, img)
        ref = np.asarray(Image.open(src).convert('RGBA'))
        for fmt, args in (('uastc', ['-uastc']), ('etc1s', ['-etc1s', '-q', '128'])):
            out = os.path.join(tmp, f'{name}.{fmt}.ktx2')
            subprocess.run([basisu, '-ktx2', src, '-output_file', out, '-linear'] + args,
                           check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            ud = os.path.join(tmp, f'{name}_{fmt}_q')
            os.makedirs(ud, exist_ok=True)
            subprocess.run([basisu, '-unpack', out, '-no_ktx', '-output_path', ud],
                           check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            dec = np.asarray(Image.open(glob.glob(os.path.join(ud, '*_unpacked_rgba_RGBA32_level_0_face_0_layer0000.png'))[0]).convert('RGBA'))
            bpp = os.path.getsize(out) * 8.0 / (img.shape[0] * img.shape[1])
            p = psnr_rgb(ref, dec)
            lines.append(f'{name} {fmt} {p:.3f} {bpp:.4f}')
            print(lines[-1])
    with open(os.path.join(qd, 'oracle.txt'), 'w') as f:
        f.write('# image format basisu_psnr_rgb_db basisu_bits_per_pixel  (basisu -linear; -uastc level 2, -etc1s -q 128)\n')
        f.write('\n'.join(lines) + '\n')


def main():
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    basisu = sys.argv[1]
    driver = build_driver()
    os.makedirs(OUT, exist_ok=True)
    for f in glob.glob(os.path.join(OUT, '*.ktx2*')):
        os.remove(f)
    tmp = tempfile.mkdtemp(prefix='gen_basis_encode_')

    def alpha_ramp(x, y):
        return (x + y) * 255 / (x.max() + y.max() + 1e-9)

    flat = np.zeros((64, 64, 3), np.uint8)
    flat[...] = [90, 160, 30]
    flat[40:48, 8:24] = [250, 10, 10]                    # long selector / prediction runs around a patch

    # ETC1S / BasisLZ
    encode(driver, basisu, tmp, 'etc1s_mixed_64x48', [mixed(64, 48, False, 1)], ['etc1s'])
    encode(driver, basisu, tmp, 'etc1s_rgba_37x21', [gradient(37, 21, alpha_ramp)], ['etc1s'])
    encode(driver, basisu, tmp, 'etc1s_edges_alpha_29x30_q220', [edges(29, 30, 4)], ['etc1s', '--q', '220'])
    encode(driver, basisu, tmp, 'etc1s_noise_q1_40x24', [noise(40, 24, 3, 6)], ['etc1s', '--q', '1'])
    encode(driver, basisu, tmp, 'etc1s_noise_q255_32x32', [noise(32, 32, 3, 5)], ['etc1s', '--q', '255', '--effort', '2'])
    encode(driver, basisu, tmp, 'etc1s_gray_33x17', [gray(33, 17)], ['etc1s'])
    encode(driver, basisu, tmp, 'etc1s_flat_64x64', [flat], ['etc1s', '--effort', '0'])
    encode(driver, basisu, tmp, 'etc1s_photo_mips_96x64', [photo(96, 64, 3)], ['etc1s', '--mips'])
    encode(driver, basisu, tmp, 'etc1s_linear_mips_45x27', [mixed(45, 27, True, 3)], ['etc1s', '--mips', '--linear'])
    encode(driver, basisu, tmp, 'etc1s_array3_mips_20x12', [mixed(20, 12, True, s) for s in range(3)],
           ['etc1s', '--array', '--mips'])
    encode(driver, basisu, tmp, 'etc1s_cube_8x8', [gradient(8, 8, None, s) for s in range(6)], ['etc1s', '--cube'])
    encode(driver, basisu, tmp, 'etc1s_1x1', [np.array([[[200, 100, 50]]], np.uint8)], ['etc1s'])

    # UASTC LDR 4x4 (zstd by default)
    encode(driver, basisu, tmp, 'uastc_mixed_rgb_48x40', [mixed(48, 40, False, 1)], ['uastc'])
    encode(driver, basisu, tmp, 'uastc_mixed_rgba_48x40_high', [mixed(48, 40, True, 2)], ['uastc', '--effort', '2'])
    encode(driver, basisu, tmp, 'uastc_edges_rgba_32x32_fast', [edges(32, 32, 4)], ['uastc', '--effort', '0'])
    encode(driver, basisu, tmp, 'uastc_la_36x36', [gray_alpha(36, 36)], ['uastc'])
    encode(driver, basisu, tmp, 'uastc_noise_rgba_24x24_nozstd', [noise(24, 24, 4, 7)], ['uastc', '--nozstd'])
    encode(driver, basisu, tmp, 'uastc_photo_mips_96x64', [photo(96, 64, 4)], ['uastc', '--mips', '--linear'])
    encode(driver, basisu, tmp, 'uastc_array2_mips_13x9', [mixed(13, 9, True, s) for s in range(2)], ['uastc', '--array', '--mips'])
    encode(driver, basisu, tmp, 'uastc_cube_8x8', [gradient(8, 8, None, s) for s in range(6)], ['uastc', '--cube'])

    quality_refs(basisu, tmp)
    if '--keep-temp' in sys.argv:
        print('temp files in', tmp)
    else:
        shutil.rmtree(tmp)
    total = sum(os.path.getsize(f) for f in glob.glob(os.path.join(OUT, '**', '*'), recursive=True) if os.path.isfile(f))
    print(f'total {total} bytes')


if __name__ == '__main__':
    main()
