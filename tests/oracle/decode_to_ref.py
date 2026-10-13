"""Decode an image with an independent reference implementation and write a PXRF sidecar.

usage: decode_to_ref.py <image> <out.ref>

Used by the C# tests to verify OUR encoders against third-party decoders. Each format section may be
extended by the codec owner (keep the others untouched).
"""
import os
import sys
import numpy as np
from pxref import write_ref


def decode(path):
    ext = os.path.splitext(path)[1].lower()
    if ext in ('.tif', '.tiff'):
        import tifffile
        return tifffile.imread(path)
    if ext == '.exr':
        return decode_exr(path)
    from PIL import Image
    im = Image.open(path)
    if ext == '.png':
        return decode_png(im)
    if ext == '.gif':
        return np.asarray(im.convert('RGBA'))
    if im.mode == 'CMYK':
        im = im.convert('RGB')
    return np.asarray(im)


def decode_png(im):
    if im.mode == 'P':
        im = im.convert('RGBA' if 'transparency' in im.info else 'RGB')
    elif im.mode == '1':
        im = im.convert('L')
    elif im.mode in ('I', 'I;16', 'I;16B'):
        return np.asarray(im).astype(np.uint16)
    return np.asarray(im)


def decode_exr(path):
    """First part of an EXR file as float32 (uint32 if every channel is UINT), channels in R,G,B,A / Y,A order.

    Files named *.all.exr are returned with all channels in file (byte-wise alphabetical) order instead.
    Uses the official OpenEXR python bindings when installed, otherwise OpenCV (colour channels only).
    """
    want_all = path.lower().endswith('.all.exr')
    try:
        import OpenEXR
    except ImportError:
        OpenEXR = None
    if OpenEXR is None:
        if want_all:
            raise RuntimeError('reading arbitrary EXR channels needs the OpenEXR python module')
        os.environ['OPENCV_IO_ENABLE_OPENEXR'] = '1'
        import cv2
        im = cv2.imread(path, cv2.IMREAD_UNCHANGED)
        if im is None:
            raise RuntimeError('OpenCV cannot read ' + path)
        if im.ndim == 3:
            im = im[..., [2, 1, 0, 3][:im.shape[2]]]
        return np.ascontiguousarray(im)
    chans = OpenEXR.File(path, separate_channels=True).parts[0].channels
    names = sorted(chans.keys(), key=lambda n: n.encode())
    if not want_all:
        for pick in (['R', 'G', 'B'], ['Y']):
            if all(n in chans for n in pick):
                names = pick + (['A'] if 'A' in chans else [])
                break
    arrs = [chans[n].pixels for n in names]
    dt = np.uint32 if all(a.dtype == np.uint32 for a in arrs) else np.float32
    return np.stack([a.astype(dt) for a in arrs], axis=-1)


if __name__ == '__main__':
    write_ref(sys.argv[2], decode(sys.argv[1]))
