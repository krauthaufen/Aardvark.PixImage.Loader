"""Generates tests/fixtures/basis_hdr: UASTC HDR 4x4 KTX2 files written by basisu, with Float16 RGBA references.

UASTC HDR 4x4 is standard (constrained) ASTC HDR 4x4 data: KTX2 vkFormat ASTC_4x4_SFLOAT_BLOCK with data format
descriptor colour model 167 (KHR_DF_MODEL_UASTC_HDR_4X4), optionally zstd supercompressed. Every name.ktx2 gets
name.ktx2.<level>.<layer>.<face>.ref per image: basisu -unpack's RGBA_HALF output (its ASTC HDR decoder), read from the
EXR it writes.

The sources are synthetic HDR images made here (written as OpenEXR half float). basisu (Apache-2.0) is only run as
an oracle.

usage: python3 gen_basis_hdr.py /path/to/basisu
"""
import glob
import os
import re
import subprocess
import sys
import tempfile
import shutil
import numpy as np
import OpenEXR
import Imath
from pxref import write_ref

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'fixtures', 'basis_hdr')


def write_exr(path, rgb):
    h, w, _ = rgb.shape
    hdr = OpenEXR.Header(w, h)
    half = Imath.Channel(Imath.PixelType(Imath.PixelType.HALF))
    hdr['channels'] = {'R': half, 'G': half, 'B': half}
    f = OpenEXR.OutputFile(path, hdr)
    f.writePixels({c: rgb[..., i].astype(np.float16).tobytes() for i, c in enumerate('RGB')})
    f.close()


def read_exr_rgba(path):
    f = OpenEXR.InputFile(path)
    dw = f.header()['dataWindow']
    w, h = dw.max.x - dw.min.x + 1, dw.max.y - dw.min.y + 1
    chans = f.header()['channels']
    out = np.zeros((h, w, 4), np.float16)
    for i, c in enumerate('RGBA'):
        if c in chans:
            out[..., i] = np.frombuffer(f.channel(c, Imath.PixelType(Imath.PixelType.HALF)), np.float16).reshape(h, w)
        else:
            out[..., i] = 1.0
    return out


def hdr_image(w, h, seed):
    rng = np.random.default_rng(seed)
    y, x = np.mgrid[0:h, 0:w].astype(np.float64)
    base = np.stack([x / w, y / h, 0.5 + 0.5 * np.sin(x / 5.0) * np.cos(y / 7.0)], -1)
    sky = np.exp2((x / w) * 12 - 4)[..., None] * np.array([1.0, 0.9, 0.7])   # 1/16 .. 256
    img = base * 0.8 + 0.2 * sky
    img[(x - w * 0.7) ** 2 + (y - h * 0.3) ** 2 < (h * 0.12) ** 2] = [2000.0, 1800.0, 1500.0]   # a sun
    img += rng.random(img.shape) * 0.02
    return img.astype(np.float32)


def encode(basisu, tmp, name, img, args):
    src = os.path.join(tmp, name + '.exr')
    write_exr(src, img)
    out = os.path.join(OUT, name + '.ktx2')
    subprocess.run([basisu, '-ktx2', '-hdr', src, '-output_file', out] + args,
                   check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    ud = os.path.join(tmp, name + '_unpack')
    os.makedirs(ud, exist_ok=True)
    subprocess.run([basisu, '-unpack', out, '-no_ktx', '-output_path', ud],
                   check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    n = 0
    for f in sorted(glob.glob(os.path.join(ud, '*_unpacked_rgba_RGBA_HALF_level_*.exr'))):
        m = re.search(r'_level_(\d+)_face_(\d+)_layer_(\d+)\.exr$', f)
        level, face, layer = int(m.group(1)), int(m.group(2)), int(m.group(3))
        write_ref(f'{out}.{level}.{layer}.{face}.ref', read_exr_rgba(f))
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
    tmp = tempfile.mkdtemp(prefix='gen_basis_hdr_')
    encode(basisu, tmp, 'uastc_hdr_41x23', hdr_image(41, 23, 1), [])
    encode(basisu, tmp, 'uastc_hdr_mips_32x24', hdr_image(32, 24, 2), ['-mipmap'])
    encode(basisu, tmp, 'uastc_hdr_l3_nozstd_20x20', hdr_image(20, 20, 3), ['-uastc_level', '3', '-ktx2_no_zstandard'])
    shutil.rmtree(tmp)


if __name__ == '__main__':
    main()
