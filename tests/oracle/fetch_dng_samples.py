"""Fetches real camera DNG files for DngTests (the RealSamples tests) and writes their third-party references.

usage: python3 fetch_dng_samples.py       (from tests/oracle; needs network access once)

The files come from raw.pixls.us, all under CC0 1.0 (public domain dedication), and are downloaded - not committed - into
downloads/dng-samples of the test-data repository (git-ignored there; see pxref.testdata_dir). They are picked for
variety: Bayer patterns of every phase, X-Trans, LinearRaw (Apple ProRAW, Samsung, a monochrome Leica), lossless JPEG
in tiles and in one strip with reshaped frames, uncompressed 8 / 10 / 12 / 16-bit, lossy JPEG (Adobe DNG Converter),
linearization tables, BlackLevelDeltas, one to two colour calibrations with and without forward matrices, and opcode
lists from phones (GainMap, FixVignetteRadial, WarpRectilinear, MapPolynomial, FixBadPixelsConstant).

References per file (name.dng.json, name.dng.libraw.ref):
  stored_sha256   SHA-256 of the raw directory's samples as tifffile + imagecodecs decode them (little endian bytes,
                  row-major, channel-interleaved), with their shape and dtype
  libraw_sha256   SHA-256 of LibRaw's (rawpy) raw_image where LibRaw reads the file as a flat CFA / LinearRaw image:
                  the stored values mapped through the LinearizationTable (uint16)
  tags            make / model / EXIF exposure values / orientation / raw size / default crop as tifffile reads them
  libraw.ref      LibRaw's linear sRGB rendering (camera white balance, no auto brightness, highlights clipped, AHD
                  demosaic, its own flip), box-averaged by `box` pixels, Float32 0..1; `libraw_opcodes` says whether the
                  comparison renders with opcode lists (LibRaw applies MapPolynomial but not gain maps, vignetting or warps)
"""
import hashlib
import json
import math
import os
import sys
import urllib.request
import warnings

import numpy as np

warnings.filterwarnings('ignore')
from pxref import write_ref, testdata_dir, missing  # noqa: E402

BASE = 'https://raw.pixls.us/getfile.php/'
# (id, file name, sha256, compare the rendering with opcodes)
SAMPLES = [
    (1033, 'Adobe DNG Converter - Canon EOS 5D Mark III - Lossy JPEG compression, rgb (3:2).DNG', 'b22f1e36331f679abb8b13e433b9bbde3988723adf10fee7aa0dda6381016a98', True),
    (4264, 'Apple - iPhone 12 Pro - 8bit (4:3).DNG', 'e91e77a4533ed7cce551d83330676ea5c47dd5e55fb38adda7819366afdbdfc2', False),
    (915, 'Apple - iPhone 6s Plus - 16bit (4:3).DNG', '845fee4f09c832af3728778737f5ae9b99124debb821b6e2066b94aed1daa91c', False),
    (2433, 'Blackmagic - Pocket Cinema Camera - 12bit (16:9).dng', '0ce97c657f9f1084cc25f313854bd1d1d8579a76f6b6bde2d2f19b5bceede4c4', False),
    (1327, 'Canon - Canon PowerShot A410 - 10bit 10bit CHDK ver. 1.5.0 (4:3).DNG', 'f0d2fe47507fadf50008bddbc6bd2c5e39fddbe90cca6bca72dd360fa0e0eb38', False),
    (2204, 'Canon - EOS 5D Mark III - 14bit 14bit (2.3471882640587).dng', '1d77dcc6cdb839b10c1948f0b4471984ab48cf15e973fb77520584cee72b603e', False),
    (2649, 'Canon - EOS 5D Mark III - 16bit (16:9).dng', 'bc29ac8a37c800346749b9a5a1adc2722357f7f0e49df05a7104454881604b92', False),
    (2644, 'Fujifilm - X100S - NAN.dng', '7adec564bbf74858c70b5a01a2b744000f8057303169fc70d91bde7cb435530f', False),
    (2206, 'Google - Pixel 2 XL - 16bit (4:3).dng', '5541093a369f0487fef92bcd1f5fef1be6e4e3cec2aa4d52ad38d2211f3309d7', False),
    (1087, 'Leica - M Monochrom (Typ 246) - 12bit (3:2).DNG', '3fb74d1fc402daaa48a9c3f1d6279d7e85fb58dfdaf816f036927d49ab228977', False),
    (753, 'Leica - M9 - 8bit 8bit compressed (3:2).DNG', 'd1d162fce62210b8951c862189a6098329b5163b7f7492dc397c6e53bba04239', False),
    (1988, 'Nikon - D80 - 16bit (3:2).dng', '5095ca264e763624cd3aa62940286dd1bcff4504eefa1ce61cbb72f50f8426bb', False),
    (830, 'Pentax - K-x - 12bit (3:2).DNG', '7a594b66b54f99bb5aba4627c78abfc41213d6911ea54e4f5d2dd6c8505f6968', False),
    (6734, 'Samsung - Galaxy S23 - 16:9.dng', '873184113210f9c4f684ad1eccea587a973b2c4d4ca88036b6962a1f6ca513bb', False),
]


def local_name(name):
    """File names without characters that are awkward on some file systems (':' on Windows)."""
    return name.replace(':', '-').replace(',', '').replace(' ', '_')


def sha(b):
    return hashlib.sha256(b).hexdigest()


def raw_page(tf):
    """The raw IFD: NewSubFileType 0 with CFA or LinearRaw photometric, IFD 0 or one of its SubIFDs."""
    for p in [tf.pages[0]] + list(tf.pages[0].pages or []):
        if p.tags.get(254) is not None and p.tags[254].value != 0:
            continue
        if p.photometric in (32803, 34892):
            return p
    raise ValueError('no raw IFD')


def tag(page, code, default=None):
    t = page.tags.get(code)
    return default if t is None else t.value


def pairs(v):
    """Two values that tifffile returns as numerator / denominator pairs when the tag is RATIONAL."""
    if v is None:
        return []
    v = list(v)
    if len(v) == 4:
        return [v[0] / v[1], v[2] / v[3]]
    return [float(x) for x in v]


def box(a, k):
    h, w = a.shape[0] // k * k, a.shape[1] // k * k
    a = a[:h, :w]
    return a.reshape(h // k, k, w // k, k, -1).mean(axis=(1, 3))


def jsonable(v):
    if isinstance(v, (bytes, bytearray)):
        return v.decode('latin-1').strip('\0 ')
    if isinstance(v, tuple) and len(v) == 2 and all(isinstance(x, int) for x in v):
        return v[0] / v[1] if v[1] else None
    if isinstance(v, (np.integer,)):
        return int(v)
    if isinstance(v, (np.floating,)):
        return float(v)
    if isinstance(v, str):
        return v.strip('\0 ')
    return v


def main():
    out = testdata_dir('downloads', 'dng-samples')
    if out is None:
        sys.exit(1)
    try:
        import tifffile
        import rawpy
    except ImportError as e:
        missing('python:' + e.name)
    os.makedirs(out, exist_ok=True)
    for sid, name, want, with_ops in SAMPLES:
        path = os.path.join(out, local_name(name))
        if not os.path.exists(path):
            url = BASE + str(sid) + '/nice/' + urllib.parse.quote(name)
            print('fetching', name)
            data = urllib.request.urlopen(url, timeout=300).read()
            with open(path + '.part', 'wb') as f:
                f.write(data)
            os.replace(path + '.part', path)
        data = open(path, 'rb').read()
        if sha(data) != want:
            raise SystemExit(f'{name}: SHA-256 mismatch')
        info = {'source': f'https://raw.pixls.us ({sid}), CC0 1.0', 'sha256': sha(data), 'libraw_opcodes': with_ops}

        with tifffile.TiffFile(path) as tf:
            p = raw_page(tf)
            a = p.asarray()
            if a.ndim == 2:
                a = a[:, :, None]
            info['stored_shape'] = list(a.shape)
            info['stored_dtype'] = str(a.dtype)
            info['stored_sha256'] = sha(np.ascontiguousarray(a).astype(a.dtype.newbyteorder('<')).tobytes())
            ifd0 = tf.pages[0]
            exif = tag(ifd0, 34665) or {}
            t = {
                'make': jsonable(tag(ifd0, 271)), 'model': jsonable(tag(ifd0, 272)), 'orientation': tag(ifd0, 274, 1),
                'raw_width': p.imagewidth, 'raw_height': p.imagelength, 'bits': p.bitspersample,
                'photometric': int(p.photometric), 'compression': int(p.compression),
                'cfa_dim': list(tag(p, 33421) or []), 'cfa_pattern': list(tag(p, 33422) or []),
                'active_area': list(tag(p, 50829) or []),
                'crop_origin': pairs(tag(p, 50719)),
                'crop_size': pairs(tag(p, 50720)),
            }
            for k, code in (('iso', 34855), ('exposure', 33434), ('fnumber', 33437), ('focal', 37386), ('date', 36867)):
                v = exif.get(tifffile.TIFF.TAGS.get(code)) if isinstance(exif, dict) else None
                if v is None:
                    v = tag(ifd0, code)
                if isinstance(v, (tuple, list)) and len(v) == 2 and k != 'iso':
                    v = v[0] / v[1] if v[1] else None
                elif isinstance(v, (tuple, list)):
                    v = v[0]
                t[k] = jsonable(v)
            info['tags'] = t

        try:
            with rawpy.imread(path) as r:
                if r.raw_type == rawpy.RawType.Flat or a.shape[2] == 1:
                    ri = r.raw_image.copy()
                    info['libraw_shape'] = list(ri.shape)
                    info['libraw_sha256'] = sha(np.ascontiguousarray(ri).astype('<u2').tobytes())
                rgb = r.postprocess(use_camera_wb=True, no_auto_bright=True, output_bps=16, gamma=(1, 1),
                                    output_color=rawpy.ColorSpace.sRGB, highlight_mode=rawpy.HighlightMode.Clip)
                info['libraw_flip'] = int(r.sizes.flip)
                info['libraw_margins'] = [int(r.sizes.top_margin), int(r.sizes.left_margin)]
            if rgb.ndim == 2:
                rgb = rgb[:, :, None]
            k = max(8, math.ceil(max(rgb.shape[:2]) / 512))
            info['box'] = k
            info['libraw_size'] = [int(rgb.shape[1]), int(rgb.shape[0])]
            write_ref(path + '.libraw.ref', box(rgb.astype(np.float64) / 65535, k).astype(np.float32))
        except (rawpy.LibRawError, rawpy.LibRawFileUnsupportedError) as e:
            info['libraw_error'] = str(e)
        with open(path + '.json', 'w') as f:
            json.dump(info, f, indent=1, sort_keys=True)
        print('ok', name)


if __name__ == '__main__':
    main()
