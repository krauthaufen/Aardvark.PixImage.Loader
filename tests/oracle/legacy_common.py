"""Helpers shared by gen_pcx.py, gen_sgi.py, gen_sun.py, gen_xbm.py, gen_xpm.py and gen_wbmp.py.

References are what Aardvark.PixImage.PixCodec decodes; they are cross checked against Pillow, ImageMagick and (for PCX)
imagecodecs, all compared as RGBA. Disagreements are printed (with --report every comparison).
"""
import os
import subprocess
import sys
import numpy as np
from PIL import Image
from pxref import write_ref

REPORT = '--report' in sys.argv


def clear(out):
    os.makedirs(out, exist_ok=True)
    for f in os.listdir(out):
        os.remove(os.path.join(out, f))


def save(out, name, data, ref, suffix=''):
    path = os.path.join(out, name)
    if isinstance(data, str):
        data = data.encode('ascii')
    with open(path, 'wb') as f:
        f.write(data)
    write_ref(path + suffix + '.ref', ref if ref.ndim == 3 else ref[:, :, None])
    return path


def rgba(a):
    """(H,W,C) samples as RGBA of the same dtype."""
    a = a if a.ndim == 3 else a[:, :, None]
    full = np.iinfo(a.dtype).max
    c = a.shape[2]
    if c == 1:
        return np.concatenate([a, a, a, np.full_like(a, full)], -1)
    if c == 2:
        return np.concatenate([a[:, :, :1]] * 3 + [a[:, :, 1:]], -1)
    if c == 3:
        return np.concatenate([a, np.full_like(a[:, :, :1], full)], -1)
    return a[:, :, :4]


def pillow(path, frame=0):
    try:
        with Image.open(path) as im:
            if frame:
                im.seek(frame)
            im.load()
            if im.mode in ('I;16', 'I;16B', 'I;16L', 'I'):
                return rgba(np.array(im).astype(np.uint16))
            return np.array(im.convert('RGBA'))
    except Exception as e:  # noqa: BLE001 - "cannot read it"
        if REPORT:
            print(f'  pillow: {e}')
        return None


def magick(path, frame=0, depth=8):
    try:
        info = subprocess.run(['magick', 'identify', '-format', '%w %h\n', f'{path}[{frame}]'], capture_output=True, check=True).stdout.split()
        w, h = int(info[0]), int(info[1])
        out = subprocess.run(['magick', f'{path}[{frame}]', '-depth', str(depth), 'rgba:-'], capture_output=True, check=True).stdout
        return np.frombuffer(out, np.uint8 if depth == 8 else '<u2').reshape(h, w, 4)
    except Exception:  # noqa: BLE001
        return None


def compare(name, ref, other, who):
    if other is None:
        if REPORT:
            print(f'  {name}: {who} cannot read it')
        return
    r = rgba(ref)
    if other.dtype != r.dtype:
        # 16 bit reference against an 8 bit tool
        r = (r >> 8).astype(np.uint8) if r.dtype == np.uint16 else r
    if other.shape != r.shape:
        print(f'  {name}: {who} gives shape {other.shape}, reference {r.shape}')
        return
    d = np.abs(other.astype(int) - r.astype(int)).max()
    if d > 0 or REPORT:
        print(f'  {name}: {who} differs by {d}')


def check(name, path, ref, frame=0, extra=None):
    """Cross checks a fixture against Pillow and ImageMagick (and an extra decoder: function path -> RGBA or None)."""
    compare(name, ref, pillow(path, frame), 'pillow')
    compare(name, ref, magick(path, frame, 16 if ref.dtype == np.uint16 else 8), 'magick')
    if extra is not None:
        compare(name, ref, extra(path), extra.__name__)


def tool_ref(path, channels, who='pillow', frame=0):
    """A tool's decode as the reference, in the channel count the decoder gives."""
    a = pillow(path, frame) if who == 'pillow' else magick(path, frame)
    if a is None:
        raise SystemExit(f'{who} cannot read {path}')
    if channels == 1:
        assert (a[:, :, 0] == a[:, :, 1]).all() and (a[:, :, 0] == a[:, :, 2]).all()
        return a[:, :, :1].copy()
    if channels == 2:
        return a[:, :, [0, 3]].copy()
    return a[:, :, :channels].copy()


def magick_write(src, args, dst):
    subprocess.run(['magick'] + src + args + [dst], check=True)
