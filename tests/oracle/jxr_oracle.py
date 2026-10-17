"""JPEG XR reference decodes: what jxrlib (JxrDecApp, built by tests/oracle/ci/build.sh; $PIXCODEC_JXRLIB is its folder) or
imagecodecs (its bundled jxrlib, the fallback where JxrDecApp is missing or lacks the pixel format) make of a file.

The reference is the decoder's reference output of T.832 (the values after ClippingAndPackingStage( )) as a PXRF sidecar:
one channel per output array (colour channels in R, G, B / C, M, Y, K / channel order, alpha last):
  BD8 uint8, BD16 uint16, BD16S int16, BD32S int32, BD16F float16 bits, BD32F float32 bits,
  BD5 / BD565 one uint16 channel of packed words, BD10 RGB one uint32 channel, BlackWhite uint8 0 / 1 per pixel,
  RGBE four uint8 channels R, G, B, E.

usage: python3 jxr_oracle.py in.jxr out.ref        (exit 77: no reference decoder can read the file here)
"""
import os
import shutil
import struct
import subprocess
import sys
import tempfile
import numpy as np
from pxref import write_ref, missing, unverifiable

PREFIX = bytes.fromhex('24C3DD6F034EFE4BB1853D77768DC9')

# pixel format id (last GUID byte) -> (name, JxrDecApp -c code or None, output file type)
FORMATS = {
    0x0D: ('24bppRGB', 9, 'tif'), 0x0C: ('24bppBGR', 9, 'tif'), 0x0E: ('32bppBGR', 34, 'bmp32'),
    0x15: ('48bppRGB', 10, 'tif'), 0x12: ('48bppRGBFixedPoint', 11, 'tif'), 0x3B: ('48bppRGBHalf', 12, 'tif'),
    0x18: ('96bppRGBFixedPoint', 14, 'tif'), 0x40: ('64bppRGBFixedPoint', 11, 'tif'), 0x42: ('64bppRGBHalf', 12, 'tif'),
    0x41: ('128bppRGBFixedPoint', 14, 'tif'), 0x1B: ('128bppRGBFloat', 15, 'tif'),
    0x0F: ('32bppBGRA', 22, 'tif'), 0x16: ('64bppRGBA', 23, 'tif'), 0x1D: ('64bppRGBAFixedPoint', 24, 'tif'),
    0x3A: ('64bppRGBAHalf', 25, 'tif'), 0x1E: ('128bppRGBAFixedPoint', 27, 'tif'), 0x19: ('128bppRGBAFloat', 28, 'tif'),
    0x10: ('32bppPBGRA', None, None), 0x17: ('64bppPRGBA', None, None), 0x1A: ('128bppPRGBAFloat', None, None),
    0x1C: ('32bppCMYK', 17, 'tif'), 0x1F: ('64bppCMYK', 18, 'tif'), 0x2C: ('40bppCMYKAlpha', 32, 'tif'),
    0x2D: ('80bppCMYKAlpha', 33, 'tif'),
    0x08: ('8bppGray', 2, 'tif'), 0x0B: ('16bppGray', 3, 'tif'), 0x13: ('16bppGrayFixedPoint', 4, 'tif'),
    0x3E: ('16bppGrayHalf', 5, 'tif'), 0x3F: ('32bppGrayFixedPoint', 7, 'tif'), 0x11: ('32bppGrayFloat', 8, 'tif'),
    0x05: ('BlackWhite', 1, 'tif'),
    0x09: ('16bppBGR555', 29, 'bmp'), 0x0A: ('16bppBGR565', 30, 'bmp'), 0x14: ('32bppBGR101010', 31, 'bmp'),
    0x3D: ('32bppRGBE', 16, 'hdr'),
}
for n in range(3, 9):
    FORMATS[0x20 + n - 3] = (f'{8 * n}bpp{n}Channels', None, None)
    FORMATS[0x2E + n - 3] = (f'{8 * (n + 1)}bpp{n}ChannelsAlpha', None, None)
    FORMATS[0x26 + n - 3] = (f'{16 * n}bpp{n}Channels', None, None)
    FORMATS[0x34 + n - 3] = (f'{16 * (n + 1)}bpp{n}ChannelsAlpha', None, None)


def pixel_format(data):
    """The id byte of PIXEL_FORMAT (tag 0xBC01) of an Annex A file."""
    if data[:3] != b'II\xbc':
        raise ValueError('not a JPEG XR file')
    ifd, = struct.unpack_from('<I', data, 4)
    n, = struct.unpack_from('<H', data, ifd)
    for i in range(n):
        tag, typ, count, value = struct.unpack_from('<HHII', data, ifd + 2 + 12 * i)
        if tag == 0xBC01:
            g = data[value:value + 16]
            if g[:15] != PREFIX:
                raise ValueError('unknown PIXEL_FORMAT')
            return g[15]
    raise ValueError('no PIXEL_FORMAT')


def _tool(name):
    """A jxrlib program: from $PIXCODEC_JXRLIB when that is set, else from the PATH; None if missing."""
    d = os.environ.get('PIXCODEC_JXRLIB')
    if d is not None:
        for exe in (name, name + '.exe'):
            if os.path.isfile(os.path.join(d, exe)):
                return os.path.join(d, exe)
        return None
    return shutil.which(name)


def jxrlib():
    return _tool('JxrDecApp')


def jxrenc():
    return _tool('JxrEncApp')


def _bmp_words(path):
    d = open(path, 'rb').read()
    off, = struct.unpack_from('<I', d, 10)
    w, h, _, bpp = struct.unpack_from('<iiHH', d, 18)
    bpr = ((w * bpp // 8) + 3) & ~3
    dt = '<u2' if bpp == 16 else '<u4'
    rows = []
    for y in range(abs(h)):
        o = off + (abs(h) - 1 - y) * bpr if h > 0 else off + y * bpr
        rows.append(np.frombuffer(d, dtype=dt, count=w, offset=o))
    return np.array(rows).astype(np.uint16 if bpp == 16 else np.uint32)


def _tif(path, fid, w, h):
    import tifffile
    if fid == 0x1B:
        # JxrDecApp writes R, G, B and a padding sample into a 3 sample TIFF: read the strip as 4 samples
        with tifffile.TiffFile(path) as tf:
            off = tf.pages[0].dataoffsets[0]
        a = np.fromfile(path, dtype=np.float32, count=w * h * 4, offset=off).reshape(h, w, 4)[:, :, :3]
    else:
        a = tifffile.imread(path)
    if a.dtype == np.bool_:
        a = a.astype(np.uint8)
    return a


def _size(data):
    ifd, = struct.unpack_from('<I', data, 4)
    n, = struct.unpack_from('<H', data, ifd)
    w = h = 0
    for i in range(n):
        tag, typ, count, value = struct.unpack_from('<HHII', data, ifd + 2 + 12 * i)
        if tag == 0xBC80:
            w = value if typ == 4 else value & 0xFFFF
        if tag == 0xBC81:
            h = value if typ == 4 else value & 0xFFFF
    return w, h


def decode_jxrlib(path, data, fid):
    exe = jxrlib()
    name, code, ext = FORMATS.get(fid, (None, None, None))
    if exe is None or code is None:
        return None
    w, h = _size(data)
    with tempfile.TemporaryDirectory() as tmp:
        out = os.path.join(tmp, 'out.' + ext.replace('32', ''))
        r = subprocess.run([exe, '-i', path, '-o', out, '-c', str(code)], capture_output=True)
        if r.returncode != 0 or not os.path.exists(out) or os.path.getsize(out) == 0:
            return None
        if ext == 'bmp':
            return _bmp_words(out)
        if ext == 'bmp32':
            # JxrDecApp's conversion of 32bppBGR to 24bppRGB scrambles the samples: read its own BGRX bitmap
            wd = _bmp_words(out)
            return np.stack([(wd >> 16) & 255, (wd >> 8) & 255, wd & 255], -1).astype(np.uint8)
        if ext == 'hdr':
            d = open(out, 'rb').read()
            i = d.index(b'+X')
            i = d.index(b'\n', i) + 1
            return np.frombuffer(d[i:], np.uint8)[:w * h * 4].reshape(h, w, 4).copy()
        return _tif(out, fid, w, h)


def decode_imagecodecs(data, fid):
    try:
        import imagecodecs
    except ImportError:
        return None
    if not getattr(imagecodecs, 'JPEGXR', None) or not imagecodecs.JPEGXR.available:
        return None
    name = FORMATS.get(fid, ('?',))[0]
    if fid in (0x09, 0x0A, 0x14, 0x18, 0x41, 0x3F, 0x1E) or name is None:
        return None   # converted (packed fields) or not exactly representable (s7.24 as float32)
    try:
        a = imagecodecs.jpegxr_decode(data)
    except Exception:
        return None
    if a.dtype == np.bool_:
        a = a.astype(np.uint8)
    if 'FixedPoint' in name:
        a = np.round(a.astype(np.float64) * 8192).astype(np.int16)
    return a


def decode(path):
    data = open(path, 'rb').read()
    fid = pixel_format(data)
    a = decode_jxrlib(path, data, fid)
    if a is None:
        a = decode_imagecodecs(data, fid)
    return a, fid


def main():
    src, dst = sys.argv[1], sys.argv[2]
    data = open(src, 'rb').read()
    try:
        fid = pixel_format(data)
    except ValueError as e:
        unverifiable(str(e))
    if fid not in FORMATS:
        unverifiable(f'pixel format 0x{fid:02X} is beyond jxrlib (CMYKDIRECT, YCC)')
    a, fid = decode(src)
    if a is None:
        name, code, _ = FORMATS[fid]
        if jxrlib() is None and (code is not None or fid in (0x09, 0x0A, 0x14, 0x18, 0x41, 0x3F, 0x1E)):
            missing('jxrlib', 'JxrDecApp (tests/oracle/ci/build.sh, $PIXCODEC_JXRLIB)')
        try:
            import imagecodecs  # noqa: F401
        except ImportError:
            missing('python:imagecodecs', 'imagecodecs')
        unverifiable(f'no reference decoder reads {name} files')
    write_ref(dst, a)


if __name__ == '__main__':
    main()
