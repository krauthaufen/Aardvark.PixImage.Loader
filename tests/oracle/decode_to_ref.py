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
    """Default image of a PNG in the PixCodec mapping. A file named *.anim.png yields all composited animation
    frames (RGBA) stacked vertically instead."""
    import struct
    import subprocess
    path = im.filename
    if path.lower().endswith('.anim.png'):
        return decode_apng(path, im)
    with open(path, 'rb') as f:
        head = f.read(4096)
    depth, ctype = struct.unpack('>BB', head[24:26])
    if ctype in (0, 2) and b'tRNS' in head.split(b'IDAT')[0]:
        # colour key: Pillow keeps it as metadata and OpenCV drops gray keys; ImageMagick applies it
        w, h = im.size
        raw = subprocess.run(['magick', path, '-depth', '16', 'RGBA:-'], capture_output=True, check=True).stdout
        a = np.frombuffer(raw, '<u2').reshape(h, w, 4)
        if depth < 16:
            assert not np.any(a % 257)
            a = (a // 257).astype(np.uint8)
        return a[..., [0, 3]] if ctype == 0 else a
    if depth == 16 and ctype != 0:
        # Pillow reduces 16 bit color / gray+alpha to 8 bit, OpenCV (libpng) keeps it
        import cv2
        a = cv2.imread(path, cv2.IMREAD_UNCHANGED)
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


def decode_apng(path, im):
    """Composited frames, stacked vertically, on which at least two of these agree exactly (fully transparent
    pixels aside): ffmpeg (not for palette or sub-byte files, and it skips 16 bit frames that blend), Pillow
    (8 bit files) and libjxl (cjxl + djxl, if installed - see gen_png.tool). The one case with a single decoder
    is a 16 bit animation with blended frames, which only libjxl renders."""
    import subprocess
    with open(path, 'rb') as f:
        depth, ctype = f.read(26)[24:26]
    w, h = im.size
    wide = depth == 16
    n = getattr(im, 'n_frames', 1)
    results = []
    if ctype != 3 and depth >= 8:
        raw = subprocess.run(['ffmpeg', '-v', 'error', '-i', path, '-fps_mode', 'passthrough', '-f', 'rawvideo',
                              '-pix_fmt', 'rgba64le' if wide else 'rgba', '-'], capture_output=True).stdout
        size = w * h * (8 if wide else 4)
        if raw and len(raw) == n * size:
            results.append(('ffmpeg', np.frombuffer(raw, '<u2' if wide else np.uint8).reshape(n, h, w, 4)))
    if not wide:
        try:
            frames = []
            for i in range(n):
                im.seek(i)
                frames.append(np.asarray(im.convert('RGBA')).copy())
            results.append(('pillow', np.stack(frames)))
        except Exception:
            pass  # e.g. interlaced frames
    try:
        from gen_png import anim_libjxl
        a = anim_libjxl(path, w, h, np.uint16 if wide else np.uint8)
        if a is not None and a.shape == (n, h, w, 4):
            results.append(('libjxl', a))
    except Exception:
        pass
    # 16 bit frames that blend are rendered by libjxl alone (ffmpeg skips them, Pillow is 8 bit)
    alone = wide and [r[0] for r in results] == ['libjxl']
    assert len(results) >= 2 or alone, f'only {[r[0] for r in results]} can decode this animation'

    def visible(a):
        a = a.copy()
        a[a[..., 3] == 0] = 0
        return a
    first = visible(results[0][1])
    for name, a in results[1:]:
        assert np.array_equal(first, visible(a)), f'{results[0][0]} and {name} disagree'
    return first.reshape(-1, w, 4)


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
    """A part of an EXR file as float32 (uint32 if every channel is UINT), channels in R,G,B,A / Y,A order.

    Tokens before the extension select what is returned: *.all.exr gives all channels in file (byte-wise
    alphabetical) order, *.partN.exr part N instead of the first (both may be combined: *.part1.all.exr).
    Subsampled channels are expanded by replication. Uses the official OpenEXR python bindings when installed,
    otherwise OpenCV (colour channels of the first part only).
    """
    tokens = os.path.basename(path).lower().split('.')[1:-1]
    want_all = 'all' in tokens
    part = next((int(t[4:]) for t in tokens if t.startswith('part') and t[4:].isdigit()), 0)
    try:
        import OpenEXR
    except ImportError:
        OpenEXR = None
    if OpenEXR is None:
        if want_all or part:
            raise RuntimeError('reading arbitrary EXR channels / parts needs the OpenEXR python module')
        os.environ['OPENCV_IO_ENABLE_OPENEXR'] = '1'
        import cv2
        im = cv2.imread(path, cv2.IMREAD_UNCHANGED)
        if im is None:
            raise RuntimeError('OpenCV cannot read ' + path)
        if im.ndim == 3:
            im = im[..., [2, 1, 0, 3][:im.shape[2]]]
        return np.ascontiguousarray(im)
    chans = OpenEXR.File(path, separate_channels=True).parts[part].channels
    names = sorted(chans.keys(), key=lambda n: n.encode())
    if not want_all:
        for pick in (['R', 'G', 'B'], ['Y']):
            if all(n in chans for n in pick):
                names = pick + (['A'] if 'A' in chans else [])
                break
    arrs = []
    for n in names:
        a = chans[n].pixels
        if chans[n].ySampling > 1:
            a = np.repeat(a, chans[n].ySampling, axis=0)
        if chans[n].xSampling > 1:
            a = np.repeat(a, chans[n].xSampling, axis=1)
        arrs.append(a)
    dt = np.uint32 if all(a.dtype == np.uint32 for a in arrs) else np.float32
    return np.stack([a.astype(dt) for a in arrs], axis=-1)


if __name__ == '__main__':
    write_ref(sys.argv[2], decode(sys.argv[1]))
