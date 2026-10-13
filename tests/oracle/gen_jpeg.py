"""Generates the JPEG fixtures: tests/fixtures/jpeg/<name>.jpg + <name>.jpg.ref (reference = Pillow / libjpeg-turbo decode).

Extra directories (no .ref sidecars):
  jpeg/unsupported/  valid files the decoder must reject with ImageNotSupportedException
  jpeg/misc/         files for dedicated tests (damaged data, odd marker layouts), paired with .ref where one exists
"""
import io
import os
import shutil
import struct
import subprocess
import tempfile
import numpy as np
import imagecodecs as ic
from PIL import Image
from pxref import write_ref, test_pattern

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'fixtures', 'jpeg')
SS = {'444': 0, '422': 1, '420': 2, '411': '4:1:1'}


def photo(w, h, c=3, seed=5):
    """Smooth, photo like content: low frequency blobs plus mild noise (keeps JPEG ringing realistic)."""
    rng = np.random.default_rng(seed)
    y, x = np.mgrid[0:h, 0:w].astype(np.float64)
    chans = []
    for i in range(c):
        v = np.zeros((h, w))
        for _ in range(6):
            fx, fy, ph = rng.uniform(0.02, 0.4), rng.uniform(0.02, 0.4), rng.uniform(0, 6.28)
            v += rng.uniform(0.2, 1) * np.sin(x * fx + y * fy + ph)
        v = (v - v.min()) / max(1e-9, v.max() - v.min())
        v = 0.9 * v + 0.1 * rng.random((h, w))
        chans.append(v)
    a = np.clip(np.stack(chans, -1), 0, 1)
    a = np.round(a * 255).astype(np.uint8)
    return a[:, :, 0] if c == 1 else a


def to_rgb_ref(im):
    """The decoder's documented 4 component handling: RGB = (255 - C) * (255 - K) / 255 on Pillow's ink values."""
    if im.mode == 'CMYK':
        a = np.asarray(im).astype(np.int32)
        k = 255 - a[:, :, 3:4]
        return (((255 - a[:, :, :3]) * k + 127) // 255).astype(np.uint8)
    return np.asarray(im)


def emit(name, data, sub=None, ref=True):
    d = OUT if sub is None else os.path.join(OUT, sub)
    os.makedirs(d, exist_ok=True)
    path = os.path.join(d, name + '.jpg')
    with open(path, 'wb') as f:
        f.write(data)
    if ref is not False:
        # ref may be the bytes of an equivalent file when the reference decoder refuses this one
        im = Image.open(io.BytesIO(data if ref is True else ref))
        im.load()
        write_ref(path + '.ref', to_rgb_ref(im))


def pil(arr, **kw):
    b = io.BytesIO()
    im = arr if isinstance(arr, Image.Image) else Image.fromarray(arr)
    im.save(b, 'JPEG', **kw)
    return b.getvalue()


def magick(arr, *args):
    with tempfile.TemporaryDirectory() as t:
        src, dst = os.path.join(t, 's.png'), os.path.join(t, 'd.jpg')
        Image.fromarray(arr).save(src)
        subprocess.run(['magick', src, *args, dst], check=True)
        with open(dst, 'rb') as f:
            return f.read()


def segments(data):
    """Yields (marker, start, end) for the header segments up to and including SOS."""
    i = 2
    while i < len(data):
        m = data[i + 1]
        ln = struct.unpack('>H', data[i + 2:i + 4])[0]
        yield m, i, i + 2 + ln
        if m == 0xDA:
            return
        i += 2 + ln


def emit_ic(name, data, sub=None):
    """Fixture whose reference is the imagecodecs (libjpeg-turbo 3) decode: needed beyond 8 bit and for lossless."""
    d = OUT if sub is None else os.path.join(OUT, sub)
    os.makedirs(d, exist_ok=True)
    path = os.path.join(d, name + '.jpg')
    with open(path, 'wb') as f:
        f.write(data)
    write_ref(path + '.ref', ic.jpeg8_decode(data))


def photo16(w, h, c, bits, seed=5):
    """photo() spread over the given bit depth, with detail in the low bits."""
    rng = np.random.default_rng(seed + bits)
    a = photo(w, h, c, seed).astype(np.float64) / 255
    a = np.clip(a + (rng.random(a.shape) - 0.5) * 0.02, 0, 1)
    return np.round(a * ((1 << bits) - 1)).astype(np.uint16 if bits > 8 else np.uint8)


def with_dnl(data):
    """Moves the image height from the frame header into a DNL segment after the first scan."""
    d = bytearray(data)
    sos = None
    for m, s, e in segments(data):
        if m in (0xC0, 0xC1, 0xC2, 0xC9, 0xCA):
            height = bytes(d[s + 5:s + 7])
            d[s + 5:s + 7] = b'\0\0'
        if m == 0xDA:
            sos = e
    i = sos
    while not (d[i] == 0xFF and d[i + 1] != 0 and not 0xD0 <= d[i + 1] <= 0xD7):
        i += 1
    return bytes(d[:i]) + b'\xff\xdc\x00\x04' + height + bytes(d[i:])


def round2(cmyk):
    # ---- arithmetic coding (ImageMagick writes it; reference = Pillow)
    ar = ['-define', 'jpeg:arithmetic-coding=on']
    for sf in ['1x1', '2x1', '2x2']:
        for (w, h) in [(1, 1), (7, 5), (17, 33), (65, 65), (52, 37)]:
            emit(f'arith_{sf}_{w}x{h}', magick(photo(w, h, seed=w + 3), *ar, '-quality', '85', '-sampling-factor', sf))
            emit(f'arith_prog_{sf}_{w}x{h}', magick(photo(w, h, seed=w + 4), *ar, '-quality', '85', '-sampling-factor', sf, '-interlace', 'JPEG'))
    for q in [3, 30, 60, 98, 100]:
        emit(f'arith_q{q}', magick(test_pattern(64, 48, 3, seed=q), *ar, '-quality', str(q), '-sampling-factor', '2x2'))
        emit(f'arith_prog_q{q}', magick(test_pattern(61, 47, 3, seed=q), *ar, '-quality', str(q), '-sampling-factor', '1x1', '-interlace', 'JPEG'))
        emit(f'arith_gray_q{q}', magick(test_pattern(40, 40, 1, seed=q)[:, :, 0], *ar, '-quality', str(q)))
    emit('arith_noise', magick(np.random.default_rng(3).integers(0, 256, (48, 56, 3), dtype=np.uint8), *ar, '-quality', '99'))
    emit('arith_prog_noise', magick(np.random.default_rng(3).integers(0, 256, (48, 56, 3), dtype=np.uint8), *ar, '-quality', '99', '-interlace', 'JPEG'))
    emit('arith_flat', magick(np.full((40, 40, 3), (200, 30, 90), np.uint8), *ar))
    # long stretches of identical decisions with rare exceptions drive the probability estimator to its extreme states
    # (no .ref: it would be large; the test asks the oracle directly)
    sparse = np.full((640, 640), 120, np.uint8)
    rng = np.random.default_rng(12)
    for _ in range(40):
        y, x = rng.integers(0, 640, 2)
        sparse[y, x] = rng.integers(0, 256)
    emit('arith_sparse', magick(sparse, *ar, '-quality', '90'), 'misc', ref=False)
    emit('arith_prog_sparse', magick(sparse, *ar, '-quality', '90', '-interlace', 'JPEG'), 'misc', ref=False)
    emit('arith_prog_gray', magick(photo(100, 75, 1), *ar, '-quality', '50', '-interlace', 'JPEG'))
    for ri in [1, 3, 20]:
        emit(f'arith_rst{ri}', magick(photo(70, 50), *ar, '-quality', '80', '-sampling-factor', '2x2', '-define', f'jpeg:restart-interval={ri}'))
        emit(f'arith_prog_rst{ri}', magick(photo(70, 50), *ar, '-quality', '80', '-interlace', 'JPEG', '-define', f'jpeg:restart-interval={ri}'))
    emit('arith_cmyk', magick(photo(40, 30), *ar, '-colorspace', 'CMYK'))

    # ---- 12 bit DCT (libjpeg-turbo 3 via imagecodecs writes and reads it)
    for ss in ['444', '422', '420']:
        for (w, h) in [(1, 1), (7, 5), (17, 33), (41, 43)]:
            emit_ic(f'p12_{ss}_{w}x{h}', ic.jpeg8_encode(photo16(w, h, 3, 12, seed=w), bitspersample=12, level=85, subsampling=ss))
    for q in [5, 50, 95, 100]:
        emit_ic(f'p12_q{q}', ic.jpeg8_encode(photo16(64, 48, 3, 12), bitspersample=12, level=q, subsampling='420'))
        emit_ic(f'p12_gray_q{q}', ic.jpeg8_encode(photo16(40, 37, 1, 12), bitspersample=12, level=q))
    emit_ic('p12_opt', ic.jpeg8_encode(photo16(64, 48, 3, 12), bitspersample=12, level=80, optimize=True))
    emit_ic('p12_noise', ic.jpeg8_encode(np.random.default_rng(2).integers(0, 4096, (40, 44, 3), dtype=np.uint16), bitspersample=12, level=100, subsampling='444'))
    emit_ic('p12_extreme', ic.jpeg8_encode(np.kron(np.random.default_rng(8).integers(0, 2, (9, 9, 3), dtype=np.uint16) * 4095, np.ones((7, 7, 1), np.uint16)), bitspersample=12, level=60, subsampling='420'))

    # ---- lossless: every precision and predictor, 1 to 4 components
    for bits in range(2, 17):
        for psv in ([1 + bits % 7] if bits not in (8, 12, 16) else range(1, 8)):
            emit_ic(f'll_p{bits}_psv{psv}_gray', ic.jpeg8_encode(photo16(33, 21, 1, bits, seed=psv), lossless=True, bitspersample=bits, predictor=psv))
            emit_ic(f'll_p{bits}_psv{psv}_rgb', ic.jpeg8_encode(photo16(23, 31, 3, bits, seed=psv), lossless=True, bitspersample=bits, predictor=psv))
    for (w, h) in [(1, 1), (1, 7), (9, 1), (2, 2)]:
        emit_ic(f'll_{w}x{h}', ic.jpeg8_encode(photo16(w, h, 3, 16), lossless=True, bitspersample=16, predictor=4))
    emit_ic('ll_noise16', ic.jpeg8_encode(np.random.default_rng(1).integers(0, 65536, (30, 41), dtype=np.uint16), lossless=True, bitspersample=16, predictor=1))
    emit_ic('ll_extremes16', ic.jpeg8_encode(np.random.default_rng(1).choice(np.array([0, 65535, 32768, 1], np.uint16), (30, 41, 3)), lossless=True, bitspersample=16, predictor=7))
    emit_ic('ll_flat', ic.jpeg8_encode(np.full((20, 20, 3), 77, np.uint8), lossless=True))
    emit_ic('ll_cmyk', ic.jpeg8_encode(np.asarray(cmyk), lossless=True, colorspace='cmyk'))
    emit_ic('ll_2comp', ic.jpeg8_encode(photo(31, 17)[:, :, :2].copy(), lossless=True))
    emit_ic('ll_ljpeg', ic.ljpeg_encode(photo16(40, 30, 1, 16)))
    emit_ic('ll_ljpeg_12', ic.ljpeg_encode(photo16(40, 30, 1, 12), bitspersample=12))
    # point transform: declare the low bits dropped (the decoder scales the samples back up)
    for pt, bits in [(1, 8), (3, 12), (4, 16)]:
        d = bytearray(ic.jpeg8_encode(photo16(33, 21, 3, bits - pt), lossless=True, bitspersample=bits, predictor=4))
        for m, s, e in segments(bytes(d)):
            if m == 0xDA:
                d[e - 1] = pt
        emit_ic(f'll_pt{pt}_p{bits}', bytes(d))

    # ---- DNL: the height arrives after the first scan
    emit('dnl_baseline', with_dnl(pil(photo(40, 30), quality=80, subsampling=2)), ref=pil(photo(40, 30), quality=80, subsampling=2))
    emit('dnl_progressive', with_dnl(pil(photo(40, 30), quality=80, progressive=True)), ref=pil(photo(40, 30), quality=80, progressive=True))
    emit('dnl_gray_rst', with_dnl(pil(photo(33, 41, 1), quality=80, restart_marker_blocks=2)), ref=pil(photo(33, 41, 1), quality=80, restart_marker_blocks=2))

    # ---- a real YCCK file (luma and K at full, chroma at half resolution)
    emit('ycck_420', ic.jpeg8_encode(np.asarray(cmyk), colorspace='cmyk', outcolorspace='ycck', level=90))

    # ---- decode options: references for selected fixtures in jpeg/options/<fixture>.<variant>.ref
    opt = os.path.join(OUT, 'options')
    os.makedirs(opt, exist_ok=True)
    scaled = ['rgb_444_65x65', 'rgb_422_65x65', 'rgb_420_65x65', 'rgb_411_65x65', 'rgb_420_17x33', 'rgb_422_33x17', 'rgb_420_16x16', 'rgb_420_9x9',
              'rgb_444_7x5', 'gray_65x65', 'gray_17x33', 'photo_420', 'photo_422', 'photo_444', 'prog_420_100x75', 'prog_422_65x65', 'q100_444',
              'q10_420', 'im_1x2_65x65', 'im_4x1_65x65', 'im_2x2_2x1_2x1_65x65', 'im_2x1_1x2_1x2_50x37', 'im_4x2_65x65', 'im_2x4_33x17', 'im_3x1_50x37',
              'im_2x2_1x2_1x2_65x65', 'im_1x1_2x1_2x1_65x65', 'cmyk_420', 'arith_2x2_65x65', 'arith_prog_2x1_52x37', 'noise_420', 'extreme', 'rst_blocks1_420']
    for name in scaled:
        src = os.path.join(OUT, name + '.jpg')
        w, h = Image.open(src).size
        for s in (2, 4, 8):
            im = Image.open(src)
            if w // s == 0 or h // s == 0:
                continue
            im.draft(None, (w // s, h // s))
            if im.size != (-(-w // s), -(-h // s)) or getattr(im, 'decoderconfig', (1,))[0] != s:
                continue
            im.load()
            write_ref(os.path.join(opt, f'{name}.s{s}.ref'), to_rgb_ref(im))
    for name in ['rgb_420_65x65', 'rgb_422_33x17', 'rgb_444_7x5', 'prog_420_100x75', 'im_1x2_65x65', 'arith_2x2_65x65']:
        im = Image.open(os.path.join(OUT, name + '.jpg'))
        im.draft('YCbCr', im.size)
        im.load()
        assert im.mode == 'YCbCr'
        write_ref(os.path.join(opt, name + '.ycc.ref'), np.asarray(im))
    for name in ['cmyk', 'cmyk_420', 'ycck', 'ycck_420', 'cmyk_prog']:
        im = Image.open(os.path.join(OUT, name + '.jpg'))
        im.load()
        write_ref(os.path.join(opt, name + '.cmyk.ref'), 255 - np.asarray(im))  # undo Pillow's inversion: samples as libjpeg delivers them

    # ---- EXIF orientation: reference = Pillow's exif_transpose
    from PIL import ImageOps
    for o in range(1, 9):
        exif = Image.Exif()
        exif[0x0112] = o
        for tag, arr in (('rgb', photo(23, 14)), ('gray', photo(9, 20, 1))):
            data = pil(arr, quality=85, exif=exif.tobytes(), dpi=(300, 100))
            path = os.path.join(opt, f'orient{o}_{tag}.jpg')
            with open(path, 'wb') as f:
                f.write(data)
            write_ref(path + '.ref', np.asarray(ImageOps.exif_transpose(Image.open(io.BytesIO(data)))))


ISO_TOOL = os.environ.get('PIXCODEC_ISO_JPEG', os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', '.tmp', 'jpeg170'))


def round3():
    """Fixtures in jpeg/iso/ written by the ISO/IEC 10918 reference software ("libjpeg" by Thomas Richter, version 1.70,
    https://github.com/thorfdbg/libjpeg, built outside the repository and only RUN here): the corners of the standard
    no other available writer produces - hierarchical mode, lossless with arithmetic coding, 12 bit with arithmetic
    coding or progressive, subsampled lossless, DNL. The pictures are our own synthetic ones.

    File names: <name>.t<N>[.raw].jpg - N is the tolerated sample difference, 'raw' means the reference holds the
    components as stored (compare with KeepYCbCr). References come from libjpeg-turbo 3 (imagecodecs) wherever it
    decodes the file (N = 0), otherwise from the reference software's own decoder: exact for lossless coding, within a
    few levels for DCT based coding because inverse DCT and chroma upsampling are not standardized bit for bit.
    <name>.plain.jpg next to a DNL file is the same encode without DNL (the test demands identical pixels).
    """
    iso = os.path.join(OUT, 'iso')
    if not os.path.exists(ISO_TOOL):
        print('reference JPEG software not found at', ISO_TOOL, '- keeping the existing jpeg/iso fixtures')
        return
    if os.path.isdir(iso):
        shutil.rmtree(iso)
    os.makedirs(iso)

    def pnm(path, a, bits):
        h, w = a.shape[:2]
        c = a.shape[2] if a.ndim == 3 else 1
        with open(path, 'wb') as f:
            f.write(b'%s\n%d %d\n%d\n' % (b'P6' if c == 3 else b'P5', w, h, (1 << bits) - 1))
            f.write(a.astype('>u2').tobytes() if bits > 8 else a.tobytes())

    def rd(path):
        with open(path, 'rb') as f:
            d = f.read()
        parts = d.split(None, 4)
        w, h, mx = int(parts[1]), int(parts[2]), int(parts[3])
        a = np.frombuffer(parts[4], dtype='>u2' if mx > 255 else np.uint8).reshape(h, w, 3 if parts[0] == b'P6' else 1)
        return a.astype(np.uint16 if mx > 255 else np.uint8)

    def run(args):
        r = subprocess.run([ISO_TOOL, *args], capture_output=True, text=True)
        return r

    def make(name, args, tol, c=3, bits=8, w=47, h=33, raw=False, seed=5, ref='turbo'):
        """ref: 'turbo' = libjpeg-turbo decode (must work, exact), 'tool' = the reference software's decode."""
        with tempfile.TemporaryDirectory() as t:
            src, out, dec = os.path.join(t, 'in.pnm'), os.path.join(t, 'o.jpg'), os.path.join(t, 'o.pnm')
            a = photo16(w, h, c, bits, seed)
            pnm(src, a, bits)
            run([*args, src, out])
            assert os.path.exists(out) and os.path.getsize(out) > 0, name
            with open(out, 'rb') as f:
                data = f.read()
            if ref == 'turbo':
                expected = ic.jpeg8_decode(data)
            else:
                run([*(['-c'] if raw else []), out, dec])
                expected = rd(dec)
            path = os.path.join(iso, f"{name}.t{tol}{'.raw' if raw else ''}.jpg")
            with open(path, 'wb') as f:
                f.write(data)
            write_ref(path + '.ref', expected)
            return data

    # 12 bit with arithmetic coding / progressive, from a third party writer; libjpeg-turbo reads them
    make('p12_arith', ['-q', '85', '-a'], 0, bits=12)
    make('p12_arith_420', ['-q', '85', '-a', '-s', '1x1,2x2,2x2'], 0, bits=12)
    make('p12_arith_gray', ['-q', '70', '-a'], 0, bits=12, c=1)
    make('p12_arith_rst', ['-q', '85', '-a', '-z', '3'], 0, bits=12)
    make('p12_arith_prog', ['-q', '85', '-a', '-v'], 0, bits=12)
    make('p12_arith_prog_420', ['-q', '85', '-a', '-v', '-s', '1x1,2x2,2x2'], 0, bits=12)
    make('p12_prog', ['-q', '85', '-v', '-h'], 0, bits=12)
    make('p12_prog_420', ['-q', '85', '-v', '-h', '-s', '1x1,2x2,2x2'], 0, bits=12)
    make('p12_prog_gray_rst', ['-q', '60', '-v', '-h', '-z', '5'], 0, bits=12, c=1)
    make('p8_arith_422', ['-q', '85', '-a', '-s', '1x1,2x1,2x1'], 0)
    make('p8_arith_prog_420_rst', ['-q', '85', '-a', '-v', '-s', '1x1,2x2,2x2', '-z', '2'], 0)

    # lossless with subsampled components; libjpeg-turbo reads them
    make('ll_sub420', ['-p', '-c', '-s', '1x1,2x2,2x2'], 0)
    make('ll_sub422', ['-p', '-c', '-s', '1x1,2x1,2x1'], 0)
    make('ll_sub420_p12', ['-p', '-c', '-s', '1x1,2x2,2x2'], 0, bits=12)
    make('ll_sub411_rst', ['-p', '-c', '-s', '1x1,4x1,4x1', '-z', '12'], 0)

    # lossless with arithmetic coding (SOF11): only the reference software reads these; lossless, so exact
    make('ll_arith', ['-p', '-c', '-a'], 0, ref='tool', raw=True)
    make('ll_arith_gray', ['-p', '-a'], 0, ref='tool', c=1)
    make('ll_arith_p12', ['-p', '-c', '-a'], 0, ref='tool', raw=True, bits=12)
    make('ll_arith_p16_gray', ['-p', '-a'], 0, ref='tool', c=1, bits=16)
    make('ll_arith_rst', ['-p', '-c', '-a', '-z', '47'], 0, ref='tool', raw=True)
    make('ll_arith_noise', ['-p', '-a'], 0, ref='tool', c=1, bits=16, seed=77, w=64, h=40)

    # DNL from a third party writer, each with its twin without DNL
    for name, args, tol, kw in [('dnl_seq', ['-q', '85'], 0, {}), ('dnl_prog', ['-q', '85', '-v', '-h'], 0, {}), ('dnl_arith', ['-q', '85', '-a'], 0, {}),
                                ('dnl_420_rst', ['-q', '85', '-s', '1x1,2x2,2x2', '-z', '2'], 0, {}), ('dnl_ll', ['-p', '-c'], 0, {'raw': True})]:
        plain = make(name + '.plain', args, 0, **kw)
        # reference of the DNL file: the libjpeg-turbo decode of its twin
        d = make(name, [*args, '-n'], tol, ref='tool', **kw)
        shutil.copyfile(os.path.join(iso, f"{name}.plain.t0{'.raw' if kw.get('raw') else ''}.jpg.ref"), os.path.join(iso, f"{name}.t{tol}{'.raw' if kw.get('raw') else ''}.jpg.ref"))
        assert b'\xff\xdc\x00\x04' in d and b'\xff\xdc\x00\x04' not in plain

    # hierarchical mode; only the reference software reads these
    make('hier_ll', ['-p', '-c', '-h', '-y', '2'], 0, ref='tool', raw=True)
    make('hier_ll_gray', ['-p', '-h', '-y', '2'], 0, ref='tool', c=1, w=40, h=30)
    make('hier_ll_p12', ['-p', '-c', '-h', '-y', '2'], 0, ref='tool', raw=True, bits=12)
    make('hier_ll_arith', ['-p', '-c', '-a', '-y', '2'], 0, ref='tool', raw=True)
    make('hier_dct_lossless_residual', ['-q', '85', '-h', '-y', '0', '-c'], 6, ref='tool', raw=True)
    make('hier_dct1', ['-q', '85', '-h', '-y', '1'], 6, ref='tool', raw=True)
    make('hier_dct2', ['-q', '85', '-h', '-y', '2'], 6, ref='tool', raw=True)
    make('hier_dct3', ['-q', '85', '-h', '-y', '3'], 8, ref='tool', raw=True, w=90, h=61)
    make('hier_dct2_gray', ['-q', '85', '-h', '-y', '2'], 6, ref='tool', c=1)
    make('hier_dct2_arith', ['-q', '85', '-a', '-y', '2'], 6, ref='tool', raw=True)
    make('hier_dct2_prog', ['-q', '85', '-v', '-h', '-y', '2'], 6, ref='tool', raw=True)
    make('hier_dct2_arith_prog', ['-q', '85', '-v', '-a', '-y', '2'], 6, ref='tool', raw=True)
    make('hier_dct2_p12', ['-q', '85', '-h', '-y', '2'], 60, ref='tool', raw=True, bits=12)
    make('hier_dct2_rst', ['-q', '85', '-h', '-y', '2', '-z', '2'], 6, ref='tool', raw=True)


def main():
    keep = None
    if os.path.isdir(OUT):
        # the jpeg/iso fixtures need a tool that may not be installed: carry them over
        if os.path.isdir(os.path.join(OUT, 'iso')):
            keep = tempfile.mkdtemp()
            shutil.move(os.path.join(OUT, 'iso'), os.path.join(keep, 'iso'))
        shutil.rmtree(OUT)
    os.makedirs(OUT)
    if keep:
        shutil.move(os.path.join(keep, 'iso'), os.path.join(OUT, 'iso'))
        os.rmdir(keep)
    round3()
    sizes = [(1, 1), (2, 2), (3, 3), (5, 4), (4, 5), (7, 5), (8, 8), (9, 9), (15, 17), (16, 16), (17, 33), (33, 17), (65, 65), (64, 48)]

    # every subsampling mode Pillow writes, across sizes that hit partial MCUs and the upsampling edge cases
    for ss, v in SS.items():
        for (w, h) in sizes:
            emit(f'rgb_{ss}_{w}x{h}', pil(test_pattern(w, h, 3, seed=w * 31 + h), quality=80, subsampling=v))
    for (w, h) in sizes:
        emit(f'gray_{w}x{h}', pil(test_pattern(w, h, 1, seed=w + h)[:, :, 0], quality=80))

    # sampling layouts only ImageMagick writes (4:4:0, wide/tall luma, mixed directions)
    for sf in ['1x2', '4x1', '3x1', '4x2', '2x4', '1x4', '3x2', '2x2,2x1,2x1', '2x1,1x2,1x2', '1x1,2x1,2x1', '2x2,1x2,1x2', '2x2,2x2,1x1']:
        tag = sf.replace(',', '_')
        for (w, h) in [(1, 1), (3, 3), (5, 4), (7, 5), (17, 33), (33, 17), (65, 65), (50, 37)]:
            emit(f'im_{tag}_{w}x{h}', magick(test_pattern(w, h, 3, seed=w * 7 + h), '-quality', '85', '-sampling-factor', sf))

    # quality range (coarse and fine quantization, 16 bit tables never appear at 8 bit precision)
    for q in [1, 10, 30, 50, 95, 100]:
        emit(f'q{q}_420', pil(photo(64, 48), quality=q, subsampling=2))
        emit(f'q{q}_444', pil(photo(61, 47, seed=9), quality=q, subsampling=0))
        emit(f'q{q}_gray', pil(photo(40, 40, 1), quality=q))
    emit('photo_420', pil(photo(200, 152), quality=90, subsampling=2))
    emit('photo_422', pil(photo(131, 98), quality=85, subsampling=1))
    emit('photo_444', pil(photo(160, 120), quality=92, subsampling=0))
    emit('noise_420', pil(np.random.default_rng(3).integers(0, 256, (48, 56, 3), dtype=np.uint8), quality=97, subsampling=2))
    emit('noise_gray', pil(np.random.default_rng(4).integers(0, 256, (40, 33), dtype=np.uint8), quality=100))
    emit('flat', pil(np.full((40, 40, 3), (200, 30, 90), np.uint8), quality=75))
    emit('extreme', pil(np.kron(np.random.default_rng(8).integers(0, 2, (9, 9, 3), dtype=np.uint8) * 255, np.ones((7, 7, 1), np.uint8)), quality=60, subsampling=2))

    # progressive (spectral selection + successive approximation), optimized tables, restart markers
    for ss, v in SS.items():
        for (w, h) in [(1, 1), (7, 5), (17, 33), (65, 65), (100, 75)]:
            emit(f'prog_{ss}_{w}x{h}', pil(photo(w, h, seed=w), quality=85, subsampling=v, progressive=True))
    emit('prog_gray_33x17', pil(photo(33, 17, 1), quality=85, progressive=True))
    emit('prog_gray_100x75', pil(photo(100, 75, 1), quality=40, progressive=True))
    emit('prog_q100', pil(photo(64, 64, seed=2), quality=100, subsampling=0, progressive=True))
    emit('prog_q5', pil(photo(64, 64, seed=2), quality=5, subsampling=2, progressive=True))
    emit('prog_noise', pil(np.random.default_rng(6).integers(0, 256, (40, 40, 3), dtype=np.uint8), quality=95, subsampling=2, progressive=True))
    emit('prog_im_1x2', magick(photo(50, 37), '-quality', '80', '-sampling-factor', '1x2', '-interlace', 'JPEG'))
    emit('prog_im_4x1', magick(photo(50, 37), '-quality', '80', '-sampling-factor', '4x1', '-interlace', 'JPEG'))
    emit('opt_420', pil(photo(64, 48), quality=75, subsampling=2, optimize=True))
    emit('opt_gray', pil(photo(40, 40, 1), quality=75, optimize=True))
    emit('rst_blocks1_420', pil(photo(70, 50), quality=80, subsampling=2, restart_marker_blocks=1))
    emit('rst_blocks3_444', pil(photo(70, 50), quality=80, subsampling=0, restart_marker_blocks=3))
    emit('rst_rows1_422', pil(photo(70, 50), quality=80, subsampling=1, restart_marker_rows=1))
    emit('rst_rows2_gray', pil(photo(70, 50, 1), quality=80, restart_marker_rows=2))
    emit('rst_prog_420', pil(photo(70, 50), quality=80, subsampling=2, progressive=True, restart_marker_blocks=2))
    emit('rst_prog_gray', pil(photo(70, 50, 1), quality=80, progressive=True, restart_marker_rows=1))
    emit('rst_many', pil(photo(160, 16), quality=80, subsampling=0, restart_marker_blocks=1))  # RST numbering wraps past 7

    # color spaces
    cmyk = Image.fromarray(photo(50, 40, 4, seed=11), 'CMYK')
    emit('cmyk', pil(cmyk, quality=90))
    emit('cmyk_420', pil(cmyk, quality=90, subsampling=2))
    emit('cmyk_prog', pil(cmyk, quality=90, progressive=True))
    d = bytearray(pil(cmyk, quality=90))
    a = d.find(b'Adobe')
    d[a + 11] = 2  # color transform flag: declare the same data to be YCCK
    emit('ycck', bytes(d))
    emit('rgb_adobe', pil(photo(40, 30), quality=90, keep_rgb=True))
    emit('rgb_adobe_prog', pil(photo(40, 30), quality=90, keep_rgb=True, progressive=True))
    # RGB signalled only by component ids: drop the Adobe segment
    d = pil(photo(40, 30), quality=90, keep_rgb=True)
    a = d.find(b'\xff\xee')
    emit('rgb_ids', d[:a] + d[a + 16:])
    # YCbCr without a JFIF segment
    d = pil(photo(40, 30), quality=90, subsampling=2)
    assert d[2:4] == b'\xff\xe0'
    emit('ycc_nojfif', d[:2] + d[20:])

    # metadata
    icc = bytes(np.random.default_rng(1).integers(0, 256, 70000, dtype=np.uint8))  # > 64K: split over two APP2 segments
    exif = Image.Exif()
    exif[0x0112] = 6
    exif[0x010F] = 'PixCodec'
    emit('meta_exif_icc', pil(photo(24, 16), quality=80, exif=exif.tobytes(), icc_profile=icc, dpi=(300, 150)))
    with open(os.path.join(OUT, 'meta_exif_icc.icc'), 'wb') as f:
        f.write(icc)
    emit('meta_dpi', pil(photo(24, 16, 1), quality=80, dpi=(72, 96)))
    emit('meta_comment', pil(photo(24, 16), quality=80, comment=b'hello'))

    # marker layout oddities that decoders must tolerate
    d = pil(photo(40, 30), quality=80, subsampling=2)
    emit('fill_bytes', d[:2] + b'\xff\xff\xff' + d[2:20] + b'\x00\x11\x22' + d[20:], 'misc')  # fill FFs and junk between segments
    emit('no_eoi', d[:-2], 'misc', ref=d)
    emit('trailing_junk', d + b'trailing garbage \xff\xd8\xff', 'misc')
    d = pil(photo(40, 30), quality=80, subsampling=2, progressive=True)
    emit('prog_no_eoi', d[:-2], 'misc', ref=d)

    # damaged data: no reference (decoders differ in how they conceal), tests only check the intact part
    d = pil(photo(64, 64), quality=80, subsampling=0)
    emit('truncated_444', d[:len(d) * 2 // 3], 'misc', ref=False)
    emit('truncated_444_full', d, 'misc')
    d = pil(photo(64, 64), quality=80, subsampling=0, restart_marker_blocks=4)
    cut = bytearray(d)
    r = cut.find(b'\xff\xd3')
    del cut[r:r + 2]  # lose one restart marker
    emit('rst_lost', bytes(cut), 'misc', ref=False)
    emit('rst_lost_full', d, 'misc')

    # JPEG-in-TIFF style streams: <name>.tables.bin (tables only), <name>.data.bin (abbreviated), <name>.ref = the
    # components as the TIFF reader expects them (see the name: rgb = YCbCr converted, raw = as stored)
    def tiff(name, im, raw, **kw):
        d = os.path.join(OUT, 'tiff')
        os.makedirs(d, exist_ok=True)
        for ext, st in (('tables', 1), ('data', 2)):
            with open(os.path.join(d, f'{name}.{ext}.bin'), 'wb') as f:
                f.write(pil(im, streamtype=st, **kw))
        full = Image.open(io.BytesIO(pil(im, **kw)))
        if raw and full.mode == 'RGB' and not kw.get('keep_rgb'):
            full.draft('YCbCr', full.size)  # libjpeg output without the color conversion
        full.load()
        a = np.asarray(full)
        if full.mode == 'CMYK':
            a = 255 - a  # Pillow inverts 4 component data; the stored samples are wanted
        write_ref(os.path.join(d, name + '.ref'), a)

    tiff('ycc420_rgb', photo(70, 52), False, quality=85, subsampling=2)
    tiff('ycc420_raw', photo(70, 52), True, quality=85, subsampling=2)
    tiff('ycc422_rgb', photo(41, 33), False, quality=85, subsampling=1)
    tiff('ycc444_rgb', photo(64, 64), False, quality=90, subsampling=0)
    tiff('rgb_raw', photo(48, 40), True, quality=90, keep_rgb=True)
    tiff('gray_raw', photo(50, 30, 1), True, quality=85)
    tiff('cmyk_raw', cmyk, True, quality=90)

    round2(cmyk)

    # JPEG-in-TIFF beyond 8 bit baseline: a full stream split into its tables and the rest (name suffixes as above)
    def tiff_split(name, data, ref):
        d = os.path.join(OUT, 'tiff')
        tables, rest, i = b'\xff\xd8', b'\xff\xd8', 2
        for m, s, e in segments(data):
            if m in (0xDB, 0xC4):
                tables += data[s:e]
            else:
                rest += data[s:e]
            i = e
        with open(os.path.join(d, name + '.tables.bin'), 'wb') as f:
            f.write(tables + b'\xff\xd9')
        with open(os.path.join(d, name + '.data.bin'), 'wb') as f:
            f.write(rest + data[i:])
        write_ref(os.path.join(d, name + '.ref'), ref)

    for name, arr, kw in [('p12_ycc420_rgb', photo16(70, 52, 3, 12), dict(bitspersample=12, level=90, subsampling='420')),
                          ('p12_gray_raw', photo16(50, 30, 1, 12), dict(bitspersample=12, level=90)),
                          ('ll16_gray_raw', photo16(50, 30, 1, 16), dict(bitspersample=16, lossless=True, predictor=6)),
                          ('ll16_rgb_raw', photo16(41, 33, 3, 16), dict(bitspersample=16, lossless=True, predictor=1)),
                          ('ll10_2comp_raw', photo16(41, 33, 3, 10)[:, :, :2].copy(), dict(bitspersample=10, lossless=True, predictor=4)),
                          ('ll8_rgb_raw', photo(41, 33), dict(lossless=True, predictor=7))]:
        data = ic.jpeg8_encode(arr, **kw)
        tiff_split(name, data, ic.jpeg8_decode(data))
    data = magick(photo(70, 52), '-define', 'jpeg:arithmetic-coding=on', '-sampling-factor', '2x2', '-quality', '85')
    tiff_split('arith_ycc420_rgb', data, np.asarray(Image.open(io.BytesIO(data))))

    # old-style TIFF JPEG (compression 6): tables as raw bytes, strips as bare entropy coded data.
    # Container: u8 component count, then per component q table (64 bytes), DC table and AC table (u16 length + bytes),
    # then u16 frame width, u16 frame height, u8 hSub, u8 vSub, u16 restart interval, and the entropy coded data.
    def old_style(name, data):
        q, huff, sof, sos_end, ri = {}, {}, None, None, 0
        for m, s, e in segments(data):
            seg = data[s + 4:e]
            if m == 0xDB:
                while seg:
                    q[seg[0]] = seg[1:65]
                    seg = seg[65:]
            elif m == 0xC4:
                while seg:
                    n = sum(seg[1:17])
                    huff[seg[0]] = seg[1:17 + n]
                    seg = seg[17 + n:]
            elif m == 0xC0:
                sof = seg
            elif m == 0xDD:
                ri = struct.unpack('>H', seg)[0]
            elif m == 0xDA:
                sos, sos_end = seg, e
        nc = sof[5]
        out = bytes([nc])
        for i in range(nc):
            tq = sof[8 + 3 * i]
            td, ta = sos[2 + 2 * i] >> 4, sos[2 + 2 * i] & 15
            out += q[tq] + b''.join(struct.pack('<H', len(t)) + t for t in (huff[td], huff[0x10 | ta]))
        h, w = struct.unpack('>HH', sof[1:5])
        out += struct.pack('<HHBBH', w, h, sof[7] >> 4, sof[7] & 15, ri) + data[sos_end:-2]
        d = os.path.join(OUT, 'tiff')
        with open(os.path.join(d, name + '.old.bin'), 'wb') as f:
            f.write(out)
        write_ref(os.path.join(d, name + '.old.ref'), np.asarray(Image.open(io.BytesIO(data))))

    old_style('ycc420', pil(photo(70, 52), quality=85, subsampling=2))
    old_style('ycc422_rst', pil(photo(41, 33), quality=85, subsampling=1, restart_marker_blocks=3))
    old_style('ycc444', pil(photo(40, 40), quality=85, subsampling=0))
    old_style('gray_rst', pil(photo(50, 30, 1), quality=85, restart_marker_rows=1))

    # valid but unsupported: more than 4 components (a frame header is all it takes)
    d = pil(photo(16, 16, 1), quality=80)
    sof = d.find(b'\xff\xc0')
    frame = b'\xff\xc0' + struct.pack('>HBHHB', 8 + 3 * 5, 8, 16, 16, 5) + b''.join(bytes([i + 1, 0x11, 0]) for i in range(5))
    emit('five_components', d[:sof] + frame + d[sof + 2 + 11:], 'unsupported', ref=False)

    total = sum(os.path.getsize(os.path.join(r, f)) for r, _, fs in os.walk(OUT) for f in fs)
    n = sum(len(fs) for _, _, fs in os.walk(OUT))
    print(f'{n} files, {total / 1e6:.2f} MB')


if __name__ == '__main__':
    main()
