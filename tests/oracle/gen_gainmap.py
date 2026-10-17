"""Generates tests/fixtures/gainmap: Ultra HDR JPEGs that libultrahdr's ultrahdr_app writes from synthetic images
(HDR as linear half float, SDR as x / (1 + x) in sRGB, both BT.709), with ultrahdr_app's own linear decode as the
reference (.ref: half float RGBA at its default, full display boost) and the gain map metadata it reports (.json).

  * default.jpg: ultrahdr_app's defaults (multi channel gain map, full resolution, XMP and ISO 21496-1 metadata)
  * mono.jpg: -M 0 (one channel)
  * scaled4.jpg: -s 4 (gain map at a quarter of the size)
  * gamma.jpg: -s 2 -M 0 -G 2.2 (gamma applied to the gain map)
  * q70.jpg: -q 70 -Q 70

With --photos it writes block averages (32 x 32 pixels, linear RGB, float32 .blocks.ref) of ultrahdr_app's decode
for the phone photos in fixtures/gainmap/photos of the test-data repository.

usage: python3 gen_gainmap.py [--photos]   (ultrahdr_app: $PIXCODEC_ULTRAHDR, the PATH, or tests/oracle/ci/build.sh's build)
"""
import glob
import json
import os
import shutil
import subprocess
import sys
import tempfile
import numpy as np
from pxref import write_ref, testdata_dir, missing

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, '..', 'fixtures', 'gainmap')
VARIANTS = [('default', '', 0.04), ('mono', '-M 0', 0.1), ('scaled4', '-s 4', 2.0), ('gamma', '-s 2 -M 0 -G 2.2', 0.7), ('q70', '-q 70 -Q 70', 0.04)]


def ultrahdr_app():
    exe = os.environ.get('PIXCODEC_ULTRAHDR') or shutil.which('ultrahdr_app')
    if not exe:
        local = os.path.join(HERE, '..', '..', '.tmp', 'ci-tools', 'src', 'libultrahdr', 'build', 'ultrahdr_app')
        exe = local if os.path.exists(local) else None
    if not exe:
        missing('ultrahdr', 'build libultrahdr with tests/oracle/ci/build.sh or set PIXCODEC_ULTRAHDR')
    return exe


def synthetic(w, h, seed):
    rnd = np.random.default_rng(seed)
    y, x = np.mgrid[0:h, 0:w].astype(np.float64)
    u, v = x / (w - 1), y / (h - 1)
    r, g = 8 * u * u, 3 * v
    b = 0.05 + 0.6 * (((x // 8) + (y // 8)) % 2)
    dark = (x > w // 2) & (y > h // 2)
    r = np.where(dark, 0.002 * (x % 7), r); g = np.where(dark, g * 0.01, g); b = np.where(dark, 0.001, b)
    n = 1 + 0.02 * (rnd.random((h, w)) - 0.5)
    hdr = np.stack([r * n, g * n, b * n], -1)
    s = hdr / (1 + hdr)
    sdr = np.floor(np.where(s <= 0.0031308, s * 12.92, 1.055 * s ** (1 / 2.4) - 0.055) * 255 + 0.5).clip(0, 255).astype(np.uint8)
    return hdr, sdr


def run(exe, args, cwd):
    p = subprocess.run([exe] + args.split(), cwd=cwd, capture_output=True, text=True)
    if p.returncode != 0:
        sys.exit(f'ultrahdr_app {args} failed: {p.stdout}{p.stderr}')


def decode(exe, path, tmp):
    shutil.copy(path, os.path.join(tmp, 'in.jpg'))
    run(exe, '-m 1 -j in.jpg -o 0 -O 4 -z out.raw -f meta.cfg', tmp)
    raw = np.fromfile(os.path.join(tmp, 'out.raw'), dtype='<f2')
    meta = {}
    for line in open(os.path.join(tmp, 'meta.cfg')):
        k, _, v = line.strip().partition(' ')
        if k.startswith('--'):
            meta[k[2:]] = float(v)
    return raw, meta


def fixtures(exe):
    os.makedirs(OUT, exist_ok=True)
    w, h = 64, 48
    with tempfile.TemporaryDirectory() as tmp:
        for i, (name, args, tol) in enumerate(VARIANTS):
            hdr, sdr = synthetic(w, h, 11 + i)
            np.concatenate([hdr, np.ones((h, w, 1))], -1).astype('<f2').tofile(os.path.join(tmp, 'hdr.raw'))
            np.concatenate([sdr, np.full((h, w, 1), 255, np.uint8)], -1).tofile(os.path.join(tmp, 'sdr.raw'))
            run(exe, f'-m 0 -p hdr.raw -y sdr.raw -a 4 -b 3 -t 0 -C 0 -c 0 -w {w} -h {h} {args} -z out.jpg', tmp)
            path = os.path.join(OUT, name + '.jpg')
            shutil.copy(os.path.join(tmp, 'out.jpg'), path)
            raw, meta = decode(exe, path, tmp)
            write_ref(path + '.ref', raw.reshape(h, w, 4))
            meta.update(args=args, tolerance=tol, source='ultrahdr_app 1.4.0 from a synthetic image')
            json.dump(meta, open(path + '.json', 'w'), indent=1, sort_keys=True)
            print(path, os.path.getsize(path))


def photos(exe):
    d = testdata_dir('fixtures', 'gainmap', 'photos')
    if d is None:
        return
    import PIL.Image
    with tempfile.TemporaryDirectory() as tmp:
        for path in sorted(glob.glob(os.path.join(d, '*.jpg'))):
            w, h = PIL.Image.open(path).size   # (stored size: ultrahdr_app ignores the orientation)
            raw, meta = decode(exe, path, tmp)
            img = raw.reshape(h, w, 4)[:, :, :3].astype(np.float64)
            bh, bw = h // 32, w // 32
            blocks = img[:bh * 32, :bw * 32].reshape(bh, 32, bw, 32, 3).mean(axis=(1, 3)).astype(np.float32)
            write_ref(path + '.blocks.ref', blocks)
            json.dump(meta, open(path + '.json', 'w'), indent=1, sort_keys=True)
            print(path, blocks.shape)


if __name__ == '__main__':
    exe = ultrahdr_app()
    if '--photos' in sys.argv:
        photos(exe)
    else:
        fixtures(exe)
