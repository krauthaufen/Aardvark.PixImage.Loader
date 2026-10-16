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
    if ext == '.jbg':
        return decode_jbig(path)
    if ext in ('.jpg', '.jpeg') and jpeg_needs_turbo3(path):
        import imagecodecs
        with open(path, 'rb') as f:
            return imagecodecs.jpeg8_decode(f.read())
    if ext == '.webp':
        return decode_webp(path)
    if ext == '.jxl':
        return decode_jxl(path)
    if ext in ('.jp2', '.j2k', '.j2c', '.jph', '.jhc'):
        return decode_jp2(path)
    if ext == '.lerc':
        return decode_lerc(path)
    if ext in ('.dpx', '.cin'):
        return decode_oiio(path)
    if ext == '.jls':
        # JPEG-LS through CharLS (charls.py: any interleave mode, interleaved output); exit 77 without imagecodecs
        import charls
        if charls.find() is None:
            sys.exit(77)
        with open(path, 'rb') as f:
            return charls.decode(f.read())
    if ext == '.dds' and dds_block_compressed(path):
        return decode_dds(path)
    if ext in ('.pcx', '.dcx', '.sgi', '.rgb', '.rgba', '.bw', '.ras', '.xbm', '.xpm', '.wbmp'):
        return decode_legacy(path, ext)
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


def decode_jbig(path):
    """JBIG1 through jbigkit's jbgtopbm (optional tool, see gen_jbig.py); exit status 77 when it is not available."""
    import gen_jbig
    tool = os.path.join(gen_jbig.TOOLS, 'jbgtopbm')
    if not os.path.exists(tool):
        sys.exit(77)
    with open(path, 'rb') as f:
        return gen_jbig.decode(f.read())


def decode_png(im):
    """Default image of a PNG in the Aardvark.PixImage.PixCodec mapping. A file named *.anim.png yields all composited animation
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
    is a 16 bit animation with blended frames, which only libjxl renders. Exits with status 77 when the decoders
    installed here are not enough to verify the file."""
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
    if len(results) < 2 and not alone:
        # not verifiable with what is installed here (libjxl is optional): the caller skips the check
        sys.stderr.write(f'only {[r[0] for r in results]} can decode this animation\n')
        sys.exit(77)

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
    compression Aardvark.PixImage.PixCodec writes except CCITT and JBIG). No photometric interpretation is applied, with two exceptions:
    bilevel (1-bit) pages come from libtiff through ImageMagick as 0 / 255 "as displayed", and JPEG pages come out as
    RGB. The page is the first one unless the file name ends in `.p<N>.tif`. Exits with status 77 when a decoder
    this needs (tifffile, imagecodecs, ImageMagick) is not installed: the file cannot be verified here."""
    import re
    import warnings
    warnings.filterwarnings('ignore')
    try:
        import tifffile
    except ImportError:
        sys.exit(77)
    m = re.search(r'\.p(\d+)\.tiff?$', path)
    page = int(m.group(1)) if m else 0
    with tifffile.TiffFile(path) as t:
        pg = t.pages[page]
        if pg.bitspersample == 1:
            # libtiff, via ImageMagick (Pillow 11 cannot open big endian BigTIFF, OpenCV stumbles over other pages)
            import subprocess
            try:
                r = subprocess.run(['magick', f'{path}[{page}]', '-depth', '8', 'gray:-'], capture_output=True)
            except FileNotFoundError:
                sys.exit(77)
            if r.returncode == 127:
                sys.exit(77)
            if r.returncode != 0 or len(r.stdout) != pg.imagewidth * pg.imagelength:
                raise ValueError('ImageMagick cannot read the bilevel page: ' + r.stderr.decode(errors='replace'))
            return np.frombuffer(r.stdout, np.uint8).reshape(pg.imagelength, pg.imagewidth)
        try:
            a = pg.asarray()
        except Exception:
            try:
                import imagecodecs  # noqa: F401
            except ImportError:
                sys.exit(77)    # tifffile without imagecodecs reads little more than uncompressed whole-byte data
            raise
        if pg.planarconfig == 2 and pg.samplesperpixel > 1:
            a = np.moveaxis(a, 0, -1)
        return a


def decode_jxl(path):
    """libjxl through imagecodecs (samples as stored: uint8 / uint16 / float16 / float32, orientation not applied);
    the frames of an animation stacked vertically. Exit status 77 when imagecodecs has no JPEG XL."""
    try:
        import imagecodecs
        if not imagecodecs.JPEGXL.available:
            sys.exit(77)
    except (ImportError, AttributeError):
        sys.exit(77)
    with open(path, 'rb') as f:
        a = imagecodecs.jpegxl_decode(f.read(), keeporientation=True)
    if a.ndim == 4:
        a = a.reshape(a.shape[0] * a.shape[1], a.shape[2], a.shape[3])
    return a


def decode_webp(path):
    """What libwebp shows (through Pillow's binding of its animation decoder): the first frame, RGBA when the file
    signals alpha by the rule documented in Webp.cs, else RGB. A file named *.anim.webp yields all composited RGBA
    frames stacked vertically."""
    try:
        from gen_webp import libwebp_frames, output_has_alpha
        from PIL import _webp
        _webp.WebPAnimDecoder
    except (ImportError, AttributeError):
        sys.exit(77)        # Pillow without (animated) WebP support: cannot be verified here
    with open(path, 'rb') as f:
        data = f.read()
    frames = libwebp_frames(data)[0]
    if path.lower().endswith('.anim.webp'):
        return np.concatenate(frames, axis=0)
    return frames[0] if output_has_alpha(data) else frames[0][:, :, :3]


def decode_jp2(path):
    """JPEG 2000 / HTJ2K: the components as stored, one channel each (no palette, channel definition or colour space
    is applied), by OpenJPEG through imagecodecs. HT codestreams OpenJPEG rejects (more than 31 bit) go to OpenJPH.
    As Aardvark.PixImage.PixCodec does, unsigned samples of less than 8 bit are scaled to 8 bit. Exits with status 77 when no decoder
    here can read the file (imagecodecs missing, subsampled components, 32 bit Part 1 streams)."""
    import struct
    try:
        import imagecodecs
    except ImportError:
        sys.exit(77)
    with open(path, 'rb') as f:
        data = f.read()
    i = data.find(b'\xff\x4f\xff\x51')
    nc = struct.unpack('>H', data[i + 40:i + 42])[0]
    comps = [struct.unpack('>BBB', data[i + 42 + 3 * c:i + 45 + 3 * c]) for c in range(nc)]
    depth, signed = max((c[0] & 127) + 1 for c in comps), bool(comps[0][0] & 128)
    a = None
    for dec in (imagecodecs.jpeg2k_decode, imagecodecs.htj2k_decode):
        try:
            a = dec(data)
            break
        except Exception as e:   # noqa: BLE001 - whatever the binding raises means "cannot read this one"
            err = e
    if a is None:
        sys.stderr.write(f'no JPEG 2000 decoder here reads this file: {err}\n')
        sys.exit(77)
    if a.ndim == 2:
        a = a[:, :, None]
    elif a.shape[2] != nc and a.shape[0] == nc:
        a = np.moveaxis(a, 0, -1)
    if a.dtype.kind == 'f':
        return a   # OpenJPH undoes the float mapping of an NLT marker segment
    if not signed and depth < 8:
        m = (1 << depth) - 1
        return ((a.astype(np.int64) * 255 + m // 2) // m).astype(np.uint8)
    kind = 'i' if signed else 'u'
    return a.astype(np.dtype(kind + str(1 if depth <= 8 else 2 if depth <= 16 else 4)))


def decode_exr(path):
    """A part of an EXR file as float32 (uint32 if every channel is UINT), channels in R,G,B,A / Y,A order.

    Tokens before the extension select what is returned: *.all.exr gives all channels in file (byte-wise
    alphabetical) order, *.partN.exr part N instead of the first (both may be combined: *.part1.all.exr).
    Subsampled channels are expanded by replication. Needs the official OpenEXR python bindings. *.deep.exr describes a deep part as float64: per pixel
    the sample count, then for every channel (file order) the sum of sample[k] * (k + 1).
    Exits with status 77 when the installed tools cannot read the file (no bindings, or bindings too old for it).
    """
    tokens = os.path.basename(path).lower().split('.')[1:-1]
    want_all = 'all' in tokens
    part = next((int(t[4:]) for t in tokens if t.startswith('part') and t[4:].isdigit()), 0)
    try:
        import OpenEXR
    except ImportError:
        OpenEXR = None
    if OpenEXR is None:
        # (OpenCV can read some EXR files, but converts and orders channels its own way: not a usable reference)
        sys.exit(77)
    newer = any(t in tokens for t in ('zstd', 'htj2k', 'deep'))       # need OpenEXR 3.4 / 3.5
    try:
        chans = OpenEXR.File(path, separate_channels=True).parts[part].channels
    except Exception:
        if newer and tuple(int(v) for v in OpenEXR.__version__.split('.')[:2]) < (3, 5):
            sys.exit(77)
        raise
    names = sorted(chans.keys(), key=lambda n: n.encode())
    if 'deep' in tokens:
        first = chans[names[0]].pixels
        h, w = first.shape
        out = np.zeros((h, w, 1 + len(names)), np.float64)
        for i in range(h):
            for j in range(w):
                if first[i, j] is None:         # no samples
                    continue
                out[i, j, 0] = len(first[i, j])
                for k, n in enumerate(names):
                    v = chans[n].pixels[i, j].astype(np.float64)
                    out[i, j, 1 + k] = float((v * np.arange(1, len(v) + 1)).sum())
        return out
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


def dds_block_compressed(path):
    with open(path, 'rb') as f:
        d = f.read(148)
    cc = d[84:88]
    if cc == b'DX10':
        return 70 <= int.from_bytes(d[128:132], 'little') <= 99 and int.from_bytes(d[128:132], 'little') not in range(85, 94)
    return cc in (b'DXT1', b'DXT3', b'DXT5', b'ATI1', b'ATI2')


def decode_dds(path):
    """Top surface of a block compressed DDS with bcdec (imagecodecs.bcn_decode); signed BC4 / BC5 are not covered."""
    import struct
    import imagecodecs
    with open(path, 'rb') as f:
        d = f.read()
    h, w = struct.unpack_from('<II', d, 12)
    cc = d[84:88]
    legacy = {b'DXT1': 71, b'DXT3': 74, b'DXT5': 77, b'ATI1': 80, b'ATI2': 83}
    if cc == b'DX10':
        dxgi, off = struct.unpack_from('<I', d, 128)[0], 148
    else:
        dxgi, off = legacy[cc], 128
    dxgi = {72: 71, 75: 74, 78: 77, 99: 98}.get(dxgi, dxgi)
    fmt, bb, ch = {71: (1, 8, 4), 74: (2, 16, 4), 77: (3, 16, 4), 80: (4, 8, 1), 83: (5, 16, 2),
                   95: (6, 16, 3), 96: (-6, 16, 3), 98: (7, 16, 4)}[dxgi]
    bw, bh = max(1, (w + 3) // 4), max(1, (h + 3) // 4)
    shape = (bh * 4, bw * 4) if ch == 1 else (bh * 4, bw * 4, ch)
    a = imagecodecs.bcn_decode(d[off:off + bw * bh * bb], fmt, shape=shape)
    return a[:h, :w]


def decode_legacy(path, ext):
    """PCX / DCX (first page), SGI, Sun Raster, XBM, XPM, WBMP as RGBA (uint16 for 16 bit SGI), by ImageMagick, except
    where it disagrees with the format description (see gen_pcx.py, gen_sun.py): 1 bit PCX through Pillow, RLE coded
    Sun Raster through Pillow when its rows need no padding. Exits with status 77 when no tool here reads the file
    right: 2 channel SGI (ImageMagick drops the alpha), RLE coded 32 bit Sun Raster or with padded rows, or when
    ImageMagick is missing."""
    import shutil
    import struct
    from legacy_common import magick, pillow
    with open(path, 'rb') as f:
        d = f.read(1024)
    depth = 8
    use_pillow = False
    if ext in ('.pcx', '.dcx'):
        h = d if ext == '.pcx' else None
        if h is None:
            with open(path, 'rb') as f:
                full = f.read()
            h = full[struct.unpack_from('<I', full, 4)[0]:]
        use_pillow = h[3] == 1 and h[65] == 1
    elif ext in ('.sgi', '.rgb', '.rgba', '.bw'):
        dim, zs = struct.unpack_from('>H', d, 4)[0], struct.unpack_from('>H', d, 10)[0]
        if dim == 3 and zs == 2:
            sys.exit(77)
        depth = 16 if d[3] == 2 else 8
    elif ext == '.ras':
        w, _, bits, _, typ = struct.unpack_from('>5I', d, 4)
        if typ == 2 and (bits == 32 or (w * bits + 15) // 16 * 16 != w * bits):
            sys.exit(77)
        use_pillow = typ == 2
    if use_pillow:
        a = pillow(path)
    else:
        if shutil.which('magick') is None:
            sys.exit(77)
        a = magick(path, 0, depth)
    if a is None:
        raise ValueError('the reference tool cannot read the file')
    return a


def decode_lerc(path):
    """LERC blob decoded by Esri's liblerc (pip package `lerc`, through gen_lerc.py): (rows, cols, bands * depth),
    invalid pixels 0. The validity mask goes to <out>.mask as uint8 (rows, cols, 1 or bands) if any pixel is invalid,
    the per band no-data values (only for blobs that keep no-data values) to <out>.nodata as text. Exit status 77
    when the `lerc` package is not installed."""
    try:
        import lerc  # noqa: F401
    except ImportError:
        sys.exit(77)
    from gen_lerc import lerc_decode
    with open(path, 'rb') as f:
        blob = f.read()
    data, masks, nodata = lerc_decode(blob)
    nb, nr, nc, nd = data.shape
    valid = np.ones((nb, nr, nc), np.uint8) if masks is None else (masks != 0).astype(np.uint8)
    if valid.shape[0] != nb:
        valid = np.repeat(valid, nb, axis=0)
    data = data * valid[..., None].astype(data.dtype)
    if np.issubdtype(data.dtype, np.floating):
        data[valid == 0] = 0
    out = sys.argv[2]
    if not valid.all():
        same = all(np.array_equal(valid[b], valid[0]) for b in range(nb))
        write_ref(out + '.mask', np.ascontiguousarray((valid[:1] if same else valid).transpose(1, 2, 0)))
    if nodata is not None:
        with open(out + '.nodata', 'w') as f:
            f.write(' '.join(repr(v) if u else 'x' for u, v in nodata))
    return np.ascontiguousarray(data.transpose(1, 2, 0, 3).reshape(nr, nc, nb * nd))


def decode_oiio(path):
    """DPX / Cineon through OpenImageIO (optional python package; exit status 77 when it is missing): integers scaled to
    16 bit, floats as float32, alpha as stored (unassociated)."""
    try:
        import OpenImageIO as oiio
    except ImportError:
        sys.exit(77)
    i = oiio.ImageInput.open(path)
    if i is None:
        raise RuntimeError(oiio.geterror())
    spec = i.spec()
    flt = spec.format.basetype in (oiio.FLOAT, oiio.HALF, oiio.DOUBLE)
    a = i.read_image(format='float' if flt else 'uint16')
    i.close()
    return np.ascontiguousarray(a.reshape(spec.height, spec.width, spec.nchannels))


if __name__ == '__main__':
    write_ref(sys.argv[2], decode(sys.argv[1]))
