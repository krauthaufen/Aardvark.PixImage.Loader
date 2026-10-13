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
