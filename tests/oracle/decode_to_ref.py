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
        return decode_tiff(path)
    if ext == '.exr':
        return decode_exr(path)
    from PIL import Image
    im = Image.open(path)
    if ext == '.png':
        return decode_png(im)
    if ext == '.gif':
        return decode_gif(path, im)
    if im.mode == 'CMYK':
        im = im.convert('RGB')
    return np.asarray(im)


def decode_png(im):
    import struct
    with open(im.filename, 'rb') as f:
        depth, ctype = struct.unpack('>BB', f.read(26)[24:26])
    if depth == 16 and ctype != 0:
        # Pillow reduces 16 bit color / gray+alpha to 8 bit, OpenCV (libpng) keeps it
        import cv2
        a = cv2.imread(im.filename, cv2.IMREAD_UNCHANGED)
        assert a is not None and a.dtype == np.uint16
        if ctype == 4:
            return a[..., [0, 3]]           # comes back as BGRA with B = G = R
        return a[..., ::-1] if a.shape[2] == 3 else a[..., [2, 1, 0, 3]]
    if im.mode == 'P':
        im = im.convert('RGBA' if 'transparency' in im.info else 'RGB')
    elif im.mode == '1':
        im = im.convert('L')
    elif im.mode in ('I', 'I;16', 'I;16B'):
        return np.asarray(im).astype(np.uint16)
    return np.asarray(im)


def decode_gif(path, im):
    """First frame as RGBA with transparent pixels zeroed. A file named *.anim.gif yields all composited frames
    stacked vertically; ImageMagick does that (Pillow restores disposal 2 areas to the background color where
    browsers and ImageMagick clear to transparent)."""
    if path.lower().endswith('.anim.gif'):
        import subprocess
        w, h = im.size
        raw = subprocess.run(['magick', path, '-coalesce', '-depth', '8', 'rgba:-'], capture_output=True, check=True).stdout
        a = np.frombuffer(raw, np.uint8).reshape(-1, w, 4).copy()
    else:
        a = np.array(im.convert('RGBA'))
    a[a[:, :, 3] == 0] = 0
    return a

def decode_tiff(path):
    """Samples of one page as (H,W,C), decoded by tifffile + imagecodecs (which reads every sample type, layout and
    compression PixCodec writes except CCITT). No photometric interpretation is applied, with two exceptions:
    bilevel (1-bit) pages come from libtiff through ImageMagick as 0 / 255 "as displayed", and JPEG pages come out as
    RGB. The page is the first one unless the file name ends in `.p<N>.tif`."""
    import re
    import warnings
    warnings.filterwarnings('ignore')
    import tifffile
    m = re.search(r'\.p(\d+)\.tiff?$', path)
    page = int(m.group(1)) if m else 0
    with tifffile.TiffFile(path) as t:
        pg = t.pages[page]
        if pg.bitspersample == 1:
            # libtiff, via ImageMagick (Pillow 11 cannot open big endian BigTIFF, OpenCV stumbles over other pages)
            import subprocess
            r = subprocess.run(['magick', f'{path}[{page}]', '-depth', '8', 'gray:-'], capture_output=True)
            if r.returncode != 0 or len(r.stdout) != pg.imagewidth * pg.imagelength:
                raise ValueError('ImageMagick cannot read the bilevel page: ' + r.stderr.decode(errors='replace'))
            return np.frombuffer(r.stdout, np.uint8).reshape(pg.imagelength, pg.imagewidth)
        a = pg.asarray()
        if pg.planarconfig == 2 and pg.samplesperpixel > 1:
            a = np.moveaxis(a, 0, -1)
        return a


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
