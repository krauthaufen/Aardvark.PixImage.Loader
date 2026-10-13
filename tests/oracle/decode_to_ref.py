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
    """Stored samples of the first page as (H,W,C). tifffile reads every layout but, without the optional
    `imagecodecs` package, neither LZW nor the floating point predictor; those go to libtiff via OpenCV
    (1 and 3 channels, 4 channels except uint8 where it premultiplies) or Pillow (8-bit).
    Exits with status 77 when no installed decoder can read the file."""
    import warnings
    warnings.filterwarnings('ignore')
    import tifffile
    with tifffile.TiffFile(path) as t:
        pg = t.pages[0]
        spp, dtype = pg.samplesperpixel, pg.dtype
        try:
            a = pg.asarray()
            if pg.planarconfig == 2 and spp > 1:
                a = np.moveaxis(a, 0, -1)
            return a
        except Exception:
            pass
    if spp in (1, 3) or (spp == 4 and dtype.itemsize > 1):
        try:
            os.environ['OPENCV_LOG_LEVEL'] = 'SILENT'
            import cv2
            a = cv2.imread(path, cv2.IMREAD_UNCHANGED)
            if a is not None and a.dtype == dtype and (a.shape[2] if a.ndim == 3 else 1) == spp:
                return a if spp == 1 else a[:, :, ::-1] if spp == 3 else a[:, :, [2, 1, 0, 3]]
        except Exception:
            pass
    if dtype == np.uint8:
        try:
            from PIL import Image
            im = Image.open(path)
            if im.mode in ('L', 'LA', 'RGB', 'RGBA') and len(im.getbands()) == spp:
                return np.asarray(im)
        except Exception:
            pass
    sys.exit(77)


def decode_exr(path):
    raise NotImplementedError('exr oracle: to be provided by the EXR codec owner')


if __name__ == '__main__':
    write_ref(sys.argv[2], decode(sys.argv[1]))
