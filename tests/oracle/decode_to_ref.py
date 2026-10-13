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
    if ext in ('.jpg', '.jpeg') and jpeg_needs_turbo3(path):
        import imagecodecs
        with open(path, 'rb') as f:
            return imagecodecs.jpeg8_decode(f.read())
    from PIL import Image
    im = Image.open(path)
    if ext == '.png':
        return decode_png(im)
    if ext == '.gif':
        return decode_gif(path, im)
    if im.mode == 'CMYK':
        im = im.convert('RGB')
    return np.asarray(im)


def jpeg_needs_turbo3(path):
    """True for JPEG beyond Pillow's reach: more than 8 bit per sample or lossless (SOF3)."""
    with open(path, 'rb') as f:
        d = f.read()
    i = 2
    while i + 4 <= len(d):
        if d[i] != 0xFF:
            i += 1
            continue
        m = d[i + 1]
        if m in (0xC0, 0xC1, 0xC2, 0xC3, 0xC9, 0xCA):
            return m == 0xC3 or d[i + 4] > 8
        if m == 0xDA:
            break
        i += 2 + ((d[i + 2] << 8) | d[i + 3]) if m not in (0xD8, 0x01, 0xFF) and not 0xD0 <= m <= 0xD7 else 2 if m != 0xFF else 1
    return False


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
