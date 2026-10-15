"""Generates tests/fixtures/basis: Basis Universal KTX2 files (BasisLZ/ETC1S and UASTC LDR 4x4) with RGBA8 references.

Every name.ktx2 gets name.ktx2.<level>.<layer>.<face>.ref for each of its images: the RGBA32 output of
basisu -unpack (the reference transcoder; ETC1S and UASTC to RGBA are deterministic, the tests are bit exact).

The images are synthetic (made here): gradients, noise, sharp edges, alpha variants, grayscale, odd sizes, mipmaps,
texture arrays, cube maps and an ETC1S video (P-frames).

basisu (Apache-2.0, https://github.com/BinomialLLC/basis_universal) is only built and run as an oracle, never copied:

  git clone --depth 1 https://github.com/BinomialLLC/basis_universal.git && cd basis_universal
  mkdir build && cd build && cmake .. -DCMAKE_BUILD_TYPE=Release && make -j8 basisu

usage: python3 gen_basis.py /path/to/basisu [--keep-temp]
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

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'fixtures', 'basis')
rng = np.random.default_rng(2026)


# ---------------------------------------------------------------- images

def gradient(w, h, alpha=None, seed=0):
    y, x = np.mgrid[0:h, 0:w].astype(np.float64)
    r = x / max(w - 1, 1) * 255
    g = y / max(h - 1, 1) * 255
    b = (np.sin(x / 3.0 + seed) * np.cos(y / 4.0) * 0.5 + 0.5) * 255
    c = [r, g, b]
    if alpha is not None:
        c.append(alpha(x, y))
    return np.clip(np.stack(c, -1) + 0.5, 0, 255).astype(np.uint8)


def edges(w, h, channels=3):
    """Sharp coloured shapes on a checkerboard: multi subset blocks."""
    y, x = np.mgrid[0:h, 0:w]
    a = np.zeros((h, w, channels), np.uint8)
    a[..., 0] = np.where((x // 3 + y // 5) % 2 == 0, 230, 20)
    a[..., 1] = np.where(x * 2 + y < w, 200, 40)
    a[..., 2] = np.where((x - w / 2) ** 2 + (y - h / 2) ** 2 < (min(w, h) / 3) ** 2, 250, 10)
    if channels == 4:
        a[..., 3] = np.where((x + y) % 7 < 3, 255, np.where(x % 5 == 0, 0, 128))
    return a


def noise(w, h, channels, seed):
    return np.random.default_rng(seed).integers(0, 256, (h, w, channels), dtype=np.uint8)


def mixed(w, h, alpha, seed):
    """Quadrants of gradient, noise, edges and solid colour: a spread of UASTC modes."""
    c = 4 if alpha else 3
    a = np.zeros((h, w, c), np.uint8)
    hw, hh = w // 2, h // 2
    a[:hh, :hw] = gradient(hw, hh, (lambda x, y: x * 255 / max(hw - 1, 1)) if alpha else None, seed)
    a[:hh, hw:] = noise(w - hw, hh, c, seed)
    a[hh:, :hw] = edges(hw, h - hh, c)
    a[hh:, hw:] = [90, 160, 30, 200][:c]
    return a


def gray_alpha(w, h):
    """Luminance + alpha: the UASTC LA modes."""
    y, x = np.mgrid[0:h, 0:w]
    l = ((x * 7 + y * 3) % 256).astype(np.uint8)
    l[(x // 4 + y // 4) % 3 == 0] = 240
    al = np.where((x // 2 + y) % 4 == 0, 30, 255 - (y * 255 // max(h - 1, 1))).astype(np.uint8)
    return np.stack([l, l, l, al], -1)


def gray(w, h):
    y, x = np.mgrid[0:h, 0:w]
    l = np.clip((x * 255 // max(w - 1, 1)) ^ (y * 8 % 256), 0, 255).astype(np.uint8)
    return np.stack([l, l, l], -1)


# ---------------------------------------------------------------- encoding

def save_png(path, a):
    Image.fromarray(a, 'RGBA' if a.shape[2] == 4 else 'RGB').save(path)


def encode(basisu, tmp, name, images, args):
    pngs = []
    for i, a in enumerate(images):
        p = os.path.join(tmp, f'{name}_src{i}.png')
        save_png(p, a)
        pngs.append(p)
    out = os.path.join(OUT, name + '.ktx2')
    cmd = [basisu, '-ktx2'] + args + pngs + ['-output_file', out]
    subprocess.run(cmd, check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    # reference: RGBA32 (transcoder format 13) of every image
    ud = os.path.join(tmp, name + '_unpack')
    os.makedirs(ud, exist_ok=True)
    subprocess.run([basisu, '-unpack', out, '-no_ktx', '-output_path', ud],
                   check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    n = 0
    for f in sorted(glob.glob(os.path.join(ud, '*_unpacked_rgba_RGBA32_level_*.png'))):
        m = re.search(r'_level_(\d+)_face_(\d+)_layer(\d+)\.png$', f)
        level, face, layer = int(m.group(1)), int(m.group(2)), int(m.group(3))
        img = np.asarray(Image.open(f).convert('RGBA'))
        write_ref(f'{out}.{level}.{layer}.{face}.ref', img)
        n += 1
    assert n > 0, name
    print(f'{name}: {os.path.getsize(out)} bytes, {n} images')


def main():
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    basisu = sys.argv[1]
    os.makedirs(OUT, exist_ok=True)
    for f in glob.glob(os.path.join(OUT, '*')):
        os.remove(f)
    tmp = tempfile.mkdtemp(prefix='gen_basis_')

    def alpha_ramp(x, y):
        return (x + y) * 255 / (x.max() + y.max() + 1e-9)

    # ETC1S (BasisLZ)
    encode(basisu, tmp, 'etc1s_rgb_37x21', [gradient(37, 21)], ['-etc1s'])
    encode(basisu, tmp, 'etc1s_rgba_37x21', [gradient(37, 21, alpha_ramp)], ['-etc1s'])
    encode(basisu, tmp, 'etc1s_edges_alpha_29x30', [edges(29, 30, 4)], ['-etc1s', '-q', '200'])
    encode(basisu, tmp, 'etc1s_noise_hq_32x32', [noise(32, 32, 3, 5)], ['-etc1s', '-q', '255'])
    encode(basisu, tmp, 'etc1s_noise_lq_40x24', [noise(40, 24, 4, 6)], ['-etc1s', '-q', '1'])
    encode(basisu, tmp, 'etc1s_gray_33x17', [gray(33, 17)], ['-etc1s'])
    encode(basisu, tmp, 'etc1s_linear_mips_45x27', [mixed(45, 27, True, 3)], ['-etc1s', '-mipmap', '-linear'])
    encode(basisu, tmp, 'etc1s_mips_64x48', [mixed(64, 48, False, 4)], ['-etc1s', '-mipmap'])
    encode(basisu, tmp, 'etc1s_array3_mips_20x12', [mixed(20, 12, True, s) for s in range(3)],
           ['-etc1s', '-tex_type', '2darray', '-mipmap'])
    encode(basisu, tmp, 'etc1s_cube_8x8', [gradient(8, 8, None, s) for s in range(6)], ['-etc1s', '-tex_type', 'cubemap'])
    frames = []
    base = mixed(32, 24, True, 9)
    for k in range(4):
        f = base.copy()
        f[4 + 2 * k:12 + 2 * k, 8:20] = [255, 40 * k, 0, 255]
        frames.append(f)
    encode(basisu, tmp, 'etc1s_video_32x24', frames, ['-etc1s', '-tex_type', 'video', '-mipmap'])

    # UASTC LDR 4x4 (zstd by default)
    encode(basisu, tmp, 'uastc_rgb_37x21', [gradient(37, 21)], ['-uastc'])
    encode(basisu, tmp, 'uastc_rgba_37x21_nozstd', [gradient(37, 21, alpha_ramp)], ['-uastc', '-ktx2_no_zstandard'])
    encode(basisu, tmp, 'uastc_mixed_rgb_48x40', [mixed(48, 40, False, 1)], ['-uastc', '-uastc_level', '3'])
    encode(basisu, tmp, 'uastc_mixed_rgba_48x40', [mixed(48, 40, True, 2)], ['-uastc', '-uastc_level', '3'])
    encode(basisu, tmp, 'uastc_edges_rgba_32x32_l0', [edges(32, 32, 4)], ['-uastc', '-uastc_level', '0'])
    encode(basisu, tmp, 'uastc_edges_rgb_32x32_l4', [edges(32, 32, 3)], ['-uastc', '-uastc_level', '4'])
    encode(basisu, tmp, 'uastc_la_36x36', [gray_alpha(36, 36)], ['-uastc', '-uastc_level', '3'])
    encode(basisu, tmp, 'uastc_noise_rgba_24x24', [noise(24, 24, 4, 7)], ['-uastc', '-uastc_level', '3'])
    y, x = np.mgrid[0:32, 0:32]
    smooth = np.stack([x * 3 + 40, y * 5 + 20, (x * y) % 200], -1).astype(np.uint8)   # mode 18 (5 bit weights)
    encode(basisu, tmp, 'uastc_smooth_rgb_32x32', [smooth], ['-uastc', '-uastc_level', '3'])
    line = np.stack([x * 8, x * 8 + 3, 255 - x * 8, x * 8], -1).astype(np.uint8)       # mode 14 (RGBA, 2 bit weights)
    encode(basisu, tmp, 'uastc_line_rgba_32x32', [line], ['-uastc', '-uastc_level', '3'])
    encode(basisu, tmp, 'uastc_linear_mips_45x27', [mixed(45, 27, True, 3)], ['-uastc', '-mipmap', '-linear'])
    encode(basisu, tmp, 'uastc_array2_mips_13x9', [mixed(13, 9, s == 1, s) for s in range(2)],
           ['-uastc', '-tex_type', '2darray', '-mipmap', '-ktx2_no_zstandard'])

    if '--keep-temp' in sys.argv:
        print('temp files in', tmp)
    else:
        shutil.rmtree(tmp)
    total = sum(os.path.getsize(f) for f in glob.glob(os.path.join(OUT, '*')))
    print(f'total {total} bytes')


if __name__ == '__main__':
    main()
