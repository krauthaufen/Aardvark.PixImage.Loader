"""Generates tests/fixtures/webp: name.webp, name.webp.ref (what Webp.Decode returns) and, for the files in the
animation groups, name.webp.N.ref (the composited RGBA canvas after frame N).

Kinds of fixtures:
  * lossy / lossless stills written by libwebp through Pillow and ImageMagick (quality, method, segments, partitions,
    filter type, sharpness, alpha compression and filtering, near-lossless ...), and VP8 key frames written by libvpx
    (ffmpeg) wrapped into a RIFF container by hand - libvpx uses loop filter deltas, which libwebp never writes;
  * lossless streams built by the small VP8L writer below, to force what no tool lets one choose: every predictor
    mode, cross-color coefficients, color cache sizes, all 120 short distance codes, code length repeats, sparse meta
    prefix codes, pixel bundling at odd widths;
  * animations: written by libwebp's animation encoder, and assembled here from single frames with every combination
    of blending, disposal and offsets;
  * container edge cases built by hand.

References are libwebp's decode (the animation decoder as exposed by Pillow, which is also what Image.open uses); for
stills it is cross checked against the plain libwebp decoder of `imagecodecs`. Pillow reports RGB whenever the VP8X
alpha flag is unset; PixCodec also returns alpha for a lossless image with the alpha hint (see output_has_alpha), so
the alpha plane is always taken from the decoder's RGBA output and the channel count from the rule in Webp.cs.

A selection of the libwebp conformance vectors is checked through fixtures/webp/vectors of the test-data repository
(see the README there and pxref.testdata_dir); their references are SHA-256 hashes of libwebp's output in
vectors/refs.txt there, written by this script from the fetched files.

usage: python3 gen_webp.py [--report]
"""
import hashlib
import io
import os
import shutil
import struct
import subprocess
import sys
import numpy as np
from PIL import Image, _webp
from pxref import write_ref, test_pattern, testdata_dir

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'fixtures', 'webp')
REPORT = '--report' in sys.argv
COUNT = [0]


# ---------------------------------------------------------------- container helpers

def chunk(tag, data):
    return tag + struct.pack('<I', len(data)) + data + (b'\0' if len(data) & 1 else b'')


def riff(*chunks):
    body = b'WEBP' + b''.join(chunks)
    return b'RIFF' + struct.pack('<I', len(body)) + body


def chunks_of(data, start=12, end=None):
    """[(tag, payload)] of a RIFF body (or an ANMF payload)."""
    res = []
    end = len(data) if end is None else end
    p = start
    while p + 8 <= end:
        tag = data[p:p + 4]
        size = struct.unpack_from('<I', data, p + 4)[0]
        res.append((tag, data[p + 8:p + 8 + size]))
        p += 8 + size + (size & 1)
    return res


def u24(v):
    return struct.pack('<I', v)[:3]


def vp8x(flags, w, h):
    return chunk(b'VP8X', bytes([flags, 0, 0, 0]) + u24(w - 1) + u24(h - 1))


ICC, ALPHA, EXIF, XMP, ANIM = 0x20, 0x10, 0x08, 0x04, 0x02


def image_chunks(data):
    """The ALPH / VP8 / VP8L chunks of a still WebP file, concatenated and padded."""
    return b''.join(chunk(t, d) for t, d in chunks_of(data) if t in (b'ALPH', b'VP8 ', b'VP8L'))


def anmf(x, y, w, h, duration, blend, dispose, frame_chunks):
    flags = (0 if blend else 2) | (1 if dispose else 0)
    return chunk(b'ANMF', u24(x // 2) + u24(y // 2) + u24(w - 1) + u24(h - 1) + u24(duration) + bytes([flags]) + frame_chunks)


def output_has_alpha(data):
    """The rule of Webp.Decode: the VP8X alpha flag or the lossless alpha hint; for an animation also an ALPH chunk
    in the first frame or a first frame that does not cover the canvas. (libwebp ignores an ALPH chunk next to a
    still image when the VP8X alpha flag is unset, and so does PixCodec.)"""
    cs = chunks_of(data)
    flags = 0
    canvas = None
    if cs and cs[0][0] == b'VP8X':
        flags = cs[0][1][0]
        canvas = (1 + int.from_bytes(cs[0][1][4:7], 'little'), 1 + int.from_bytes(cs[0][1][7:10], 'little'))
    if flags & ALPHA:
        return True
    anim = bool(flags & ANIM)
    if anim:
        for t, d in cs:
            if t == b'ANMF':
                w, h = 1 + int.from_bytes(d[6:9], 'little'), 1 + int.from_bytes(d[9:12], 'little')
                if (w, h) != canvas or d[0:6] != bytes(6):
                    return True
                cs = chunks_of(d, 16)
                break
    alph = False
    for t, d in cs:
        if t == b'ALPH':
            alph = True
        elif t == b'VP8 ':
            return alph and anim
        elif t == b'VP8L':
            return bool(d[4] & 0x10)
    return False


# ---------------------------------------------------------------- reference decoding and writing

def libwebp_frames(data):
    """All composited RGBA frames, their end timestamps, loop count and background as libwebp's animation decoder gives them."""
    dec = _webp.WebPAnimDecoder(data)
    w, h, loop, bg, n, mode = dec.get_info()
    frames, stamps = [], []
    for _ in range(n):
        buf, t = dec.get_next()
        frames.append(np.frombuffer(buf, np.uint8).reshape(h, w, 4).copy())
        stamps.append(t)
    return frames, stamps, loop, bg


def emit(name, data, anim=False, check=True, expect=None, like=None):
    """Writes the file and its references. expect: (H, W, 3 or 4) pixels the decoder must deliver (cross check).
    like: a well formed file with the same content, used for the references when libwebp refuses the file itself
    (PixCodec is more lenient in a few documented places)."""
    path = os.path.join(OUT, name)
    try:
        frames, stamps, loop, bg = libwebp_frames(data)
        if like is not None and REPORT:
            print('  %s: libwebp decodes it too' % name)
    except Exception as e:
        if like is None:
            raise RuntimeError('%s: libwebp cannot decode it (%s)' % (name, e))
        if REPORT:
            print('  %s: libwebp refuses it (%s), reference from the well formed twin' % (name, str(e)[:50]))
        frames, stamps, loop, bg = libwebp_frames(like)
        check = False
    with open(path, 'wb') as f:
        f.write(data)
    alpha = output_has_alpha(data)
    first = frames[0] if alpha else frames[0][:, :, :3]
    write_ref(path + '.ref', first)
    if anim:
        for i, fr in enumerate(frames):
            write_ref('%s.%d.ref' % (path, i), fr)
    if expect is not None:
        e = np.asarray(expect)
        got = frames[0][:, :, :e.shape[2]]
        if not np.array_equal(got, e):
            raise RuntimeError('%s: libwebp does not decode to the pixels the fixture was built from' % name)
    if check and len(frames) == 1:
        import imagecodecs
        try:
            other = imagecodecs.webp_decode(data)
        except Exception as e:
            other = None
            if REPORT:
                print('  %s: imagecodecs cannot decode (%s)' % (name, str(e)[:60]))
        if other is not None:
            c = min(other.shape[2], first.shape[2])
            if other.shape[:2] != first.shape[:2] or not np.array_equal(other[:, :, :c], frames[0][:, :, :c]):
                raise RuntimeError('%s: Pillow and imagecodecs disagree' % name)
            if REPORT and other.shape[2] != first.shape[2]:
                print('  %s: %d channels by rule, %d from the plain decoder' % (name, first.shape[2], other.shape[2]))
    COUNT[0] += 1
    if REPORT:
        print('%-44s %6d bytes %s %d frame(s)' % (name, len(data), 'x'.join(map(str, first.shape)), len(frames)))


def pil_bytes(im, **kw):
    bio = io.BytesIO()
    im.save(bio, 'WEBP', **kw)
    return bio.getvalue()


def magick_bytes(arr, *defines, extra=()):
    h, w, c = arr.shape
    fmt = 'rgba' if c == 4 else 'rgb'
    cmd = ['magick', '-size', '%dx%d' % (w, h), '-depth', '8', fmt + ':-'] + list(extra)
    for d in defines:
        cmd += ['-define', 'webp:' + d]
    cmd += ['webp:-']
    return subprocess.run(cmd, input=arr.tobytes(), capture_output=True, check=True).stdout


def photo(w, h, c=3, seed=1):
    """Smooth, photograph like content with some texture."""
    a = test_pattern(w, h, c, np.uint8, seed).astype(np.float64)
    rng = np.random.default_rng(seed + 100)
    y, x = np.mgrid[0:h, 0:w]
    for i in range(min(c, 3)):
        a[:, :, i] = 0.6 * a[:, :, i] + 60 + 50 * np.sin(x / 9.0 + i) * np.cos(y / 11.0 - i) + rng.normal(0, 4, (h, w))
    if c == 4:
        a[:, :, 3] = np.clip(255 * (0.5 + 0.5 * np.sin(x / 6.0) * np.cos(y / 7.0)) * 1.4 - 40, 0, 255)
        a[: h // 3, : w // 3, 3] = 0
        a[h // 2:, w // 2:, 3] = 255
    return np.clip(np.round(a), 0, 255).astype(np.uint8)


def drawing(w, h, colors, seed=3, c=3):
    """Flat areas, lines and blocks in a limited number of colors."""
    rng = np.random.default_rng(seed)
    pal = rng.integers(0, 256, (colors, c)).astype(np.uint8)
    if c == 4:
        pal[:, 3] = np.where(np.arange(colors) % 3 == 0, 0, np.where(np.arange(colors) % 3 == 1, 255, pal[:, 3]))
    idx = np.zeros((h, w), np.int64)
    for k in range(colors * 3):
        x0, y0 = rng.integers(0, w), rng.integers(0, h)
        x1, y1 = min(w, x0 + rng.integers(1, max(2, w // 2))), min(h, y0 + rng.integers(1, max(2, h // 3)))
        idx[y0:y1, x0:x1] = k % colors
    idx[::7, :] = (np.arange(w) // 3) % colors
    return pal[idx]


# ---------------------------------------------------------------- lossy

def gen_lossy():
    sizes = [(1, 1), (1, 17), (17, 1), (16, 16), (15, 15), (17, 33), (33, 17), (65, 65), (2, 2), (3, 5), (100, 7), (7, 100)]
    for i, (w, h) in enumerate(sizes):
        emit('lossy_size_%dx%d.webp' % (w, h), pil_bytes(Image.fromarray(photo(w, h, 3, i)), quality=75))
    img = photo(48, 40)
    for q in (0, 1, 10, 25, 50, 80, 90, 95, 99, 100):
        emit('lossy_q%03d.webp' % q, pil_bytes(Image.fromarray(img), quality=q))
    for m in range(7):
        emit('lossy_method%d.webp' % m, pil_bytes(Image.fromarray(photo(40, 33, 3, 20 + m)), quality=60, method=m))
    emit('lossy_gray.webp', pil_bytes(Image.fromarray(photo(37, 29, 1)[:, :, 0]), quality=70))
    emit('lossy_flat.webp', pil_bytes(Image.new('RGB', (40, 24), (200, 30, 90)), quality=75))
    emit('lossy_noise.webp', pil_bytes(Image.fromarray(np.random.default_rng(5).integers(0, 256, (32, 48, 3)).astype(np.uint8)), quality=95))
    emit('lossy_saturated.webp', pil_bytes(Image.fromarray(drawing(40, 40, 6)), quality=90))
    emit('lossy_extremes.webp', pil_bytes(Image.fromarray((np.indices((32, 32)).sum(0) % 2 * 255).astype(np.uint8)).convert('RGB'), quality=100))

    # ImageMagick exposes the rest of libwebp's knobs
    big = photo(72, 136, 3, 7)
    for parts in range(4):
        emit('lossy_partitions%d.webp' % (1 << parts), magick_bytes(big, 'partitions=%d' % parts, extra=('-quality', '60')))
    for seg in (1, 2, 3, 4):
        emit('lossy_segments%d.webp' % seg, magick_bytes(photo(64, 48, 3, 30 + seg), 'segments=%d' % seg, 'sns-strength=80', extra=('-quality', '55')))
    for ft in ('simple', 'strong'):
        for strength in (0, 20, 63, 100):
            emit('lossy_filter_%s_%d.webp' % (ft, strength), magick_bytes(photo(40, 40, 3, 41), 'filter-type=' + ft, 'filter-strength=%d' % strength, extra=('-quality', '40')))
    for sharp in (0, 1, 3, 4, 5, 7):
        emit('lossy_sharpness%d.webp' % sharp, magick_bytes(photo(40, 40, 3, 42), 'filter-sharpness=%d' % sharp, 'filter-strength=60', extra=('-quality', '30')))
    emit('lossy_autofilter.webp', magick_bytes(photo(48, 48, 3, 43), 'auto-filter=true', extra=('-quality', '50')))
    emit('lossy_sns100_seg4_parts4.webp', magick_bytes(big, 'sns-strength=100', 'segments=4', 'partitions=2', 'filter-type=simple', extra=('-quality', '35')))
    emit('lossy_sharp_yuv.webp', magick_bytes(drawing(40, 40, 8), 'use-sharp-yuv=1', extra=('-quality', '80')))
    emit('lossy_pass6.webp', magick_bytes(photo(48, 32, 3, 44), 'pass=6', 'method=6', 'preprocessing=2', extra=('-quality', '45')))
    emit('lossy_target_size.webp', magick_bytes(photo(64, 64, 3, 45), 'target-size=900'))

    # alpha next to lossy data
    rgba = photo(41, 35, 4, 8)
    for aq in (100, 99, 60, 20, 0):
        emit('lossy_alpha_q%03d.webp' % aq, pil_bytes(Image.fromarray(rgba), quality=70, alpha_quality=aq))
    for comp in (0, 1):
        for filt in ('none', 'fast', 'best'):
            emit('lossy_alpha_comp%d_%s.webp' % (comp, filt), magick_bytes(rgba, 'alpha-compression=%d' % comp, 'alpha-filtering=' + {'none': '0', 'fast': '1', 'best': '2'}[filt], extra=('-quality', '70')))
    emit('lossy_alpha_opaque.webp', pil_bytes(Image.fromarray(np.dstack([photo(20, 20), np.full((20, 20), 255, np.uint8)])), quality=70))
    emit('lossy_alpha_binary.webp', pil_bytes(Image.fromarray(drawing(33, 30, 5, 9, 4)), quality=70))
    emit('lossy_alpha_1x1.webp', pil_bytes(Image.fromarray(np.array([[[10, 200, 30, 128]]], np.uint8)), quality=70))
    emit('lossy_alpha_gray.webp', pil_bytes(Image.fromarray(photo(30, 21, 4, 3)[:, :, [0, 3]], 'LA'), quality=70))
    # every ALPH filter with raw and compressed data, written here from a known plane
    plane = photo(41, 35, 4, 8)[:, :, 3]
    base = chunks_of(pil_bytes(Image.fromarray(photo(41, 35, 3, 8)), quality=70))[0][1]
    for filt in range(4):
        raw = bytes([filt << 2]) + alpha_filter(plane, filt).tobytes()
        emit('lossy_alph_raw_filter%d.webp' % filt, riff(vp8x(ALPHA, 41, 35), chunk(b'ALPH', raw), chunk(b'VP8 ', base)))
        resid = alpha_filter(plane, filt).astype(np.uint32)
        stream = vp8l_stream(0xff000000 | (resid << 8), lz='rle')
        emit('lossy_alph_lossless_filter%d.webp' % filt, riff(vp8x(ALPHA, 41, 35), chunk(b'ALPH', bytes([1 | filt << 2 | 1 << 4]) + stream), chunk(b'VP8 ', base)))

    gen_libvpx()


def alpha_filter(a, method):
    a = a.astype(np.int64)
    h, w = a.shape
    out = np.zeros_like(a)
    for y in range(h):
        for x in range(w):
            if method == 0 or (x == 0 and y == 0):
                p = 0
            elif y == 0:
                p = a[0, x - 1]
            elif x == 0:
                p = a[y - 1, 0]
            elif method == 1:
                p = a[y, x - 1]
            elif method == 2:
                p = a[y - 1, x]
            else:
                p = min(255, max(0, a[y, x - 1] + a[y - 1, x] - a[y - 1, x - 1]))
            out[y, x] = (a[y, x] - p) & 255
    return out.astype(np.uint8)


def gen_libvpx():
    """Key frames of libvpx: other loop filter settings than libwebp writes (deltas per mode, profiles 1-3), token
    partitions. Skipped when ffmpeg has no libvpx."""
    tmp = os.path.join(OUT, '_vpx')
    os.makedirs(tmp, exist_ok=True)
    try:
        variants = [
            ('base', []), ('profile1', ['-profile:v', '1']), ('profile2', ['-profile:v', '2']), ('profile3', ['-profile:v', '3']),
            ('sharp5', ['-sharpness', '5']), ('q4', ['-qmin', '4', '-qmax', '4']), ('q60', ['-qmin', '60', '-qmax', '60']),
            ('parts8', ['-slices', '8']), ('noise', ['-noise-sensitivity', '2']),
        ]
        for name, opts in variants:
            w, h = (64, 136) if name == 'parts8' else (48, 40)
            src = os.path.join(tmp, 'in.png')
            Image.fromarray(photo(w, h, 3, 60)).save(src)
            ivf = os.path.join(tmp, name + '.ivf')
            r = subprocess.run(['ffmpeg', '-y', '-loglevel', 'error', '-i', src, '-c:v', 'libvpx', '-frames:v', '1', '-b:v', '200k',
                                '-deadline', 'good', '-cpu-used', '0'] + opts + ['-f', 'ivf', ivf], capture_output=True)
            if r.returncode != 0:
                if REPORT:
                    print('  libvpx %s: ffmpeg failed (%s)' % (name, r.stderr.decode()[-80:].strip()))
                continue
            d = open(ivf, 'rb').read()
            size = struct.unpack_from('<I', d, 32)[0]
            emit('lossy_libvpx_%s.webp' % name, riff(chunk(b'VP8 ', d[44:44 + size])))
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


# ---------------------------------------------------------------- a small VP8L writer

class BitWriter:
    def __init__(self):
        self.acc, self.n, self.out = 0, 0, bytearray()

    def put(self, v, n):
        assert 0 <= v < (1 << n) or n == 0, (v, n)
        self.acc |= v << self.n
        self.n += n
        while self.n >= 8:
            self.out.append(self.acc & 255)
            self.acc >>= 8
            self.n -= 8

    def bytes(self):
        return bytes(self.out) + (bytes([self.acc]) if self.n else b'')


def huffman_lengths(freq, limit=15):
    """Code lengths of a Huffman code, limited by flattening the frequencies."""
    import heapq
    n = len(freq)
    used = [i for i in range(n) if freq[i] > 0]
    lengths = [0] * n
    if len(used) == 0:
        return lengths
    if len(used) == 1:
        lengths[used[0]] = 1
        return lengths
    m = 1
    while True:
        heap = [(max(freq[i], m), i, None, None) for i in used]
        heapq.heapify(heap)
        cnt = n
        nodes = {}
        while len(heap) > 1:
            a = heapq.heappop(heap)
            b = heapq.heappop(heap)
            nodes[cnt] = (a[1], b[1])
            heapq.heappush(heap, (a[0] + b[0], cnt, None, None))
            cnt += 1
        stack = [(heap[0][1], 0)]
        ok = True
        while stack:
            node, d = stack.pop()
            if node in nodes:
                stack.append((nodes[node][0], d + 1))
                stack.append((nodes[node][1], d + 1))
            else:
                lengths[node] = d
                ok = ok and d <= limit
        if ok:
            return lengths
        m *= 2


def canonical(lengths):
    code, codes = 0, [0] * len(lengths)
    for bits in range(1, 16):
        for i, l in enumerate(lengths):
            if l == bits:
                codes[i] = int(format(code, '0%db' % bits)[::-1], 2)     # bit reversed: written LSB first
                code += 1
        code <<= 1
    return codes


class Code:
    def __init__(self, freq, limit=15, lengths=None):
        self.lengths = lengths if lengths is not None else huffman_lengths(freq, limit)
        self.codes = canonical(self.lengths)
        self.single = sum(1 for l in self.lengths if l) <= 1

    def put(self, bw, s):
        assert self.lengths[s] > 0, s
        if not self.single:
            bw.put(self.codes[s], self.lengths[s])


CL_ORDER = [17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15]


def write_code(bw, code, style='auto'):
    """style: auto (simple when possible), normal, norle (every length as a literal), trim (with a symbol count)."""
    L = code.lengths
    used = [i for i, l in enumerate(L) if l]
    if style == 'auto' and len(used) <= 2 and all(s < 256 for s in used):
        if not used:
            used = [0]
        bw.put(1, 1)
        bw.put(len(used) - 1, 1)
        if used[0] < 2:
            bw.put(0, 1); bw.put(used[0], 1)
        else:
            bw.put(1, 1); bw.put(used[0], 8)
        if len(used) == 2:
            bw.put(used[1], 8)
        return
    if not used:                 # an empty normal code is not valid: give symbol 0 a length
        L = list(L); L[0] = 1
    tokens = []
    prev = 8
    i = 0
    while i < len(L):
        v = L[i]
        run = 1
        while i + run < len(L) and L[i + run] == v:
            run += 1
        i += run
        if style == 'norle':
            tokens += [(v, 0)] * run
            continue
        if v == 0:
            while run >= 11:
                r = min(run, 138); tokens.append((18, r - 11)); run -= r
            if run >= 3:
                tokens.append((17, run - 3)); run = 0
            tokens += [(0, 0)] * run
        else:
            if v != prev:
                tokens.append((v, 0)); run -= 1; prev = v
            while run >= 3:
                r = min(run, 6); tokens.append((16, r - 3)); run -= r
            tokens += [(v, 0)] * run
    if style == 'trim':
        while len(tokens) > 2 and tokens[-1][0] in (0, 17, 18):
            tokens.pop()
    hist = [0] * 19
    for t, _ in tokens:
        hist[t] += 1
    cl = Code(hist, 7)
    bw.put(0, 1)
    n = 19
    while n > 4 and cl.lengths[CL_ORDER[n - 1]] == 0:
        n -= 1
    bw.put(n - 4, 4)
    for k in range(n):
        bw.put(cl.lengths[CL_ORDER[k]], 3)
    if style == 'trim':
        bw.put(1, 1)
        nbits = 0 if len(tokens) == 2 else (len(tokens) - 2).bit_length() - 1
        pairs = nbits // 2 + 1
        bw.put(pairs - 1, 3)
        bw.put(len(tokens) - 2, pairs * 2)
    else:
        bw.put(0, 1)
    for t, e in tokens:
        cl.put(bw, t)
        if t == 16:
            bw.put(e, 2)
        elif t == 17:
            bw.put(e, 3)
        elif t == 18:
            bw.put(e, 7)


DISTANCE_MAP = [
    (0, 1), (1, 0), (1, 1), (-1, 1), (0, 2), (2, 0), (1, 2), (-1, 2), (2, 1), (-2, 1), (2, 2), (-2, 2), (0, 3), (3, 0),
    (1, 3), (-1, 3), (3, 1), (-3, 1), (2, 3), (-2, 3), (3, 2), (-3, 2), (0, 4), (4, 0), (1, 4), (-1, 4), (4, 1), (-4, 1),
    (3, 3), (-3, 3), (2, 4), (-2, 4), (4, 2), (-4, 2), (0, 5), (3, 4), (-3, 4), (4, 3), (-4, 3), (5, 0), (1, 5), (-1, 5),
    (5, 1), (-5, 1), (2, 5), (-2, 5), (5, 2), (-5, 2), (4, 4), (-4, 4), (3, 5), (-3, 5), (5, 3), (-5, 3), (0, 6), (6, 0),
    (1, 6), (-1, 6), (6, 1), (-6, 1), (2, 6), (-2, 6), (6, 2), (-6, 2), (4, 5), (-4, 5), (5, 4), (-5, 4), (3, 6), (-3, 6),
    (6, 3), (-6, 3), (0, 7), (7, 0), (1, 7), (-1, 7), (5, 5), (-5, 5), (7, 1), (-7, 1), (4, 6), (-4, 6), (6, 4), (-6, 4),
    (2, 7), (-2, 7), (7, 2), (-7, 2), (3, 7), (-3, 7), (7, 3), (-7, 3), (5, 6), (-5, 6), (6, 5), (-6, 5), (8, 0), (4, 7),
    (-4, 7), (7, 4), (-7, 4), (8, 1), (8, 2), (6, 6), (-6, 6), (8, 3), (5, 7), (-5, 7), (7, 5), (-7, 5), (8, 4), (6, 7),
    (-6, 7), (7, 6), (-7, 6), (8, 5), (7, 7), (-7, 7), (8, 6), (8, 7)]


def prefix(v):
    """(prefix symbol, extra bit count, extra bits) of a length or distance code >= 1."""
    if v <= 4:
        return v - 1, 0, 0
    v -= 1
    hi = v.bit_length() - 1
    second = (v >> (hi - 1)) & 1
    eb = hi - 1
    return 2 * hi + second, eb, v & ((1 << eb) - 1)


def cache_key(argb, bits):
    return ((argb * 0x1e35a7bd) & 0xffffffff) >> (32 - bits)


def parse(px, width, cache_bits=0, lz='none', min_len=3, max_len=4096):
    """Turns pixels into ops ('lit', argb) / ('copy', length, distance code) / ('cache', key).
    lz: none, rle (distance 1 and one row), plane (the 120 short distance codes), far (plain distances, exhaustive)."""
    px = [int(v) for v in px]
    n = len(px)
    cache = [0] * (1 << cache_bits) if cache_bits else None
    ops = []
    i = 0
    while i < n:
        best = (0, 0)
        if lz != 'none':
            cands = []
            if lz == 'rle':
                cands = [(2, 1), (1, width)]                      # (distance code, distance)
            elif lz == 'plane':
                for code, (dx, dy) in enumerate(DISTANCE_MAP):
                    cands.append((code + 1, max(1, dx + dy * width)))
            else:
                cands = [(d + 120, d) for d in range(1, i + 1)]
            for code, d in cands:
                if d > i:
                    continue
                l = 0
                while i + l < n and l < max_len and px[i + l] == px[i + l - d]:
                    l += 1
                if l > best[0]:
                    best = (l, code)
        if best[0] >= min_len:
            ops.append(('copy', best[0], best[1]))
            if cache is not None:
                for k in range(best[0]):
                    cache[cache_key(px[i + k], cache_bits)] = px[i + k]
            i += best[0]
            continue
        v = px[i]
        if cache is not None:
            k = cache_key(v, cache_bits)
            if cache[k] == v:
                ops.append(('cache', k))
                i += 1
                continue
            cache[k] = v
        ops.append(('lit', v))
        i += 1
    return ops


def write_pixels(bw, ops, width, cache_bits=0, group_of=None, groups=1, style='auto', coded_groups=None):
    """Writes the prefix codes of all groups and the symbols. group_of(x, y) -> group index of a pixel position."""
    green_size = 256 + 24 + ((1 << cache_bits) if cache_bits else 0)
    hists = [[[0] * green_size, [0] * 256, [0] * 256, [0] * 256, [0] * 40] for _ in range(groups)]

    def walk(fn):
        pos = 0
        for op in ops:
            g = group_of(pos % width, pos // width) if group_of else 0
            fn(g, op)
            pos += op[1] if op[0] == 'copy' else 1

    def count(g, op):
        h = hists[g]
        if op[0] == 'lit':
            v = op[1]
            h[0][(v >> 8) & 255] += 1; h[1][(v >> 16) & 255] += 1; h[2][v & 255] += 1; h[3][v >> 24] += 1
        elif op[0] == 'cache':
            h[0][280 + op[1]] += 1
        else:
            h[0][256 + prefix(op[1])[0]] += 1
            h[4][prefix(op[2])[0]] += 1
    walk(count)
    codes = [[Code(f) for f in h] for h in hists]
    for g in (coded_groups if coded_groups is not None else range(groups)):
        for c in codes[g]:
            write_code(bw, c, style)

    def put(g, op):
        c = codes[g]
        if op[0] == 'lit':
            v = op[1]
            c[0].put(bw, (v >> 8) & 255); c[1].put(bw, (v >> 16) & 255); c[2].put(bw, v & 255); c[3].put(bw, v >> 24)
        elif op[0] == 'cache':
            c[0].put(bw, 280 + op[1])
        else:
            s, eb, e = prefix(op[1])
            c[0].put(bw, 256 + s)
            bw.put(e, eb)
            s, eb, e = prefix(op[2])
            c[4].put(bw, s)
            bw.put(e, eb)
    walk(put)


def write_subimage(bw, img, style='auto', lz='rle'):
    """An entropy coded image without transforms or meta codes (transform data, color table, entropy image)."""
    img = np.asarray(img, np.uint32)
    h, w = img.shape
    bw.put(0, 1)                # no color cache
    write_pixels(bw, parse(img.reshape(-1), w, 0, lz), w, style=style)


def subsize(v, bits):
    return (v + (1 << bits) - 1) >> bits


def ch(v):
    return (v >> 24) & 255, (v >> 16) & 255, (v >> 8) & 255, v & 255


def pack(a, r, g, b):
    return (a & 255) << 24 | (r & 255) << 16 | (g & 255) << 8 | (b & 255)


def predict(mode, L, T, TL, TR):
    avg = lambda a, b: pack(*[(x + y) // 2 for x, y in zip(ch(a), ch(b))])
    clamp = lambda v: 0 if v < 0 else 255 if v > 255 else v
    if mode == 0 or mode > 13:
        return 0xff000000
    if mode == 1: return L
    if mode == 2: return T
    if mode == 3: return TR
    if mode == 4: return TL
    if mode == 5: return avg(avg(L, TR), T)
    if mode == 6: return avg(L, TL)
    if mode == 7: return avg(L, T)
    if mode == 8: return avg(TL, T)
    if mode == 9: return avg(T, TR)
    if mode == 10: return avg(avg(L, TL), avg(T, TR))
    if mode == 11:
        est = [l + t - tl for l, t, tl in zip(ch(L), ch(T), ch(TL))]
        pl = sum(abs(e - l) for e, l in zip(est, ch(L)))
        pt = sum(abs(e - t) for e, t in zip(est, ch(T)))
        return L if pl < pt else T
    if mode == 12:
        return pack(*[clamp(l + t - tl) for l, t, tl in zip(ch(L), ch(T), ch(TL))])
    a = ch(avg(L, T))
    return pack(*[clamp(x + int((x - y) / 2)) for x, y in zip(a, ch(TL))])      # C division truncates


def forward_predictor(img, bits, modes):
    h, w = img.shape
    flat = [int(v) for v in img.reshape(-1)]
    out = np.zeros((h, w), np.uint32)
    tw = subsize(w, bits)
    for y in range(h):
        for x in range(w):
            i = y * w + x
            if y == 0:
                p = 0xff000000 if x == 0 else flat[i - 1]
            elif x == 0:
                p = flat[i - w]
            else:
                # the pixel after T in memory: for the last column that is the first pixel of the current row
                p = predict(int(modes[(y >> bits) * tw + (x >> bits)]), flat[i - 1], flat[i - w], flat[i - w - 1], flat[i - w + 1])
            out[y, x] = pack(*[(a - b) & 255 for a, b in zip(ch(flat[i]), ch(p))])
    return out


def s8(v):
    return v - 256 if v > 127 else v


def forward_cross(img, bits, elems):
    h, w = img.shape
    out = img.copy()
    tw = subsize(w, bits)
    for y in range(h):
        for x in range(w):
            g2r, g2b, r2b = elems[(y >> bits) * tw + (x >> bits)]
            a, r, g, b = ch(int(img[y, x]))
            nr = r - ((s8(g2r) * s8(g)) >> 5)
            nb = b - ((s8(g2b) * s8(g)) >> 5) - ((s8(r2b) * s8(r)) >> 5)
            out[y, x] = pack(a, nr, g, nb)
    return out


def vp8l_stream(img, transforms=(), cache_bits=0, lz='rle', style='auto', meta=None, sub_style='auto', min_len=3, max_len=4096):
    """A header-less VP8L image stream. img: (H, W) uint32 ARGB.
    transforms: ('predictor', bits, modes) / ('cross', bits, [(g2r, g2b, r2b)]) / ('subgreen',) /
                ('palette', [argb], optional list of indices instead of looking the pixels up)
    meta: (bits, group index per block, number of groups) for an entropy image."""
    bw = BitWriter()
    img = np.asarray(img, np.uint32)
    h, w = img.shape
    for t in transforms:
        bw.put(1, 1)
        if t[0] == 'predictor':
            bits, modes = t[1], t[2]
            bw.put(0, 2); bw.put(bits - 2, 3)
            tw, th = subsize(w, bits), subsize(h, bits)
            write_subimage(bw, (0xff000000 | (np.asarray(modes, np.uint32) << 8)).reshape(th, tw), sub_style)
            img = forward_predictor(img, bits, modes)
        elif t[0] == 'cross':
            bits, elems = t[1], t[2]
            bw.put(1, 2); bw.put(bits - 2, 3)
            tw, th = subsize(w, bits), subsize(h, bits)
            write_subimage(bw, np.array([pack(255, e[2], e[1], e[0]) for e in elems], np.uint32).reshape(th, tw), sub_style)
            img = forward_cross(img, bits, elems)
        elif t[0] == 'subgreen':
            bw.put(2, 2)
            out = img.copy()
            for y in range(h):
                for x in range(w):
                    a, r, g, b = ch(int(img[y, x]))
                    out[y, x] = pack(a, r - g, g, b - g)
            img = out
        else:
            pal = [int(v) for v in t[1]]
            n = len(pal)
            bw.put(3, 2); bw.put(n - 1, 8)
            deltas = [pal[0]] + [pack(*[(a - b) & 255 for a, b in zip(ch(pal[i]), ch(pal[i - 1]))]) for i in range(1, n)]
            write_subimage(bw, np.array(deltas, np.uint32).reshape(1, n), sub_style)
            idx = np.asarray(t[2]).reshape(h, w) if len(t) > 2 else np.array([pal.index(int(v)) for v in img.reshape(-1)]).reshape(h, w)
            bits = 3 if n <= 2 else 2 if n <= 4 else 1 if n <= 16 else 0
            pw = subsize(w, bits)
            packed = np.zeros((h, pw), np.uint32)
            for y in range(h):
                for x in range(w):
                    packed[y, x >> bits] |= int(idx[y, x]) << ((x & ((1 << bits) - 1)) * (8 >> bits))
            img = 0xff000000 | (packed << 8)
            w = pw
    bw.put(0, 1)
    bw.put(1 if cache_bits else 0, 1)
    if cache_bits:
        bw.put(cache_bits, 4)
    ops = parse(img.reshape(-1), w, cache_bits, lz, min_len, max_len)
    if meta:
        bits, block_groups, groups = meta
        mw, mh = subsize(w, bits), subsize(h, bits)
        bw.put(1, 1); bw.put(bits - 2, 3)
        ent = np.array([pack(255, g >> 8, g & 255, 0) for g in block_groups], np.uint32).reshape(mh, mw)
        write_subimage(bw, ent, sub_style)
        write_pixels(bw, ops, w, cache_bits, lambda x, y: block_groups[(y >> bits) * mw + (x >> bits)], groups, style)
    else:
        bw.put(0, 1)
        write_pixels(bw, ops, w, cache_bits, style=style)
    return bw.bytes()


def vp8l_file(img, alpha_hint=None, **kw):
    img = np.asarray(img, np.uint32)
    h, w = img.shape
    if alpha_hint is None:
        alpha_hint = bool(np.any((img >> 24) != 255))
    hdr = (w - 1) | (h - 1) << 14 | (1 if alpha_hint else 0) << 28
    return riff(chunk(b'VP8L', b'\x2f' + struct.pack('<I', hdr) + vp8l_stream(img, **kw)))


def to_argb(a):
    a = np.asarray(a, np.uint32)
    if a.shape[2] == 3:
        return 0xff000000 | a[:, :, 0] << 16 | a[:, :, 1] << 8 | a[:, :, 2]
    return a[:, :, 3] << 24 | a[:, :, 0] << 16 | a[:, :, 1] << 8 | a[:, :, 2]


def from_argb(v, channels):
    v = np.asarray(v, np.uint32)
    planes = [(v >> 16) & 255, (v >> 8) & 255, v & 255] + ([v >> 24] if channels == 4 else [])
    return np.stack(planes, axis=-1).astype(np.uint8)


# ---------------------------------------------------------------- lossless

def emit_hand(name, pixels, **kw):
    """A hand written lossless file; libwebp must give back exactly the pixels it was built from."""
    argb = to_argb(pixels)
    emit(name, vp8l_file(argb, **kw), expect=pixels)


def gen_lossless():
    sizes = [(1, 1), (1, 17), (17, 1), (16, 16), (17, 33), (65, 65), (2, 3), (130, 5), (5, 130)]
    for i, (w, h) in enumerate(sizes):
        emit('lossless_size_%dx%d.webp' % (w, h), pil_bytes(Image.fromarray(photo(w, h, 3, i)), lossless=True))
    for m in range(7):
        emit('lossless_method%d.webp' % m, pil_bytes(Image.fromarray(photo(45, 37, 3, 50 + m)), lossless=True, method=m, quality=60))
    for q in (0, 30, 100):
        emit('lossless_effort%03d.webp' % q, pil_bytes(Image.fromarray(photo(52, 40, 3, 61)), lossless=True, quality=q, method=6))
    emit('lossless_rgba.webp', pil_bytes(Image.fromarray(photo(40, 33, 4, 2)), lossless=True))
    hidden = photo(40, 33, 4, 4)
    emit('lossless_rgba_exact.webp', pil_bytes(Image.fromarray(hidden), lossless=True, exact=True))
    emit('lossless_rgba_not_exact.webp', pil_bytes(Image.fromarray(hidden), lossless=True, exact=False))
    emit('lossless_rgba_opaque.webp', pil_bytes(Image.fromarray(np.dstack([photo(20, 20), np.full((20, 20), 255, np.uint8)])), lossless=True))
    emit('lossless_gray.webp', pil_bytes(Image.fromarray(photo(45, 31, 1)[:, :, 0]), lossless=True))
    emit('lossless_noise.webp', pil_bytes(Image.fromarray(np.random.default_rng(6).integers(0, 256, (24, 40, 3)).astype(np.uint8)), lossless=True))
    emit('lossless_flat.webp', pil_bytes(Image.new('RGB', (37, 21), (1, 2, 3)), lossless=True))
    for colors in (2, 3, 4, 5, 16, 17, 64, 256):
        emit('lossless_palette%d.webp' % colors, pil_bytes(Image.fromarray(drawing(43, 31, colors, colors)), lossless=True, method=4))
    emit('lossless_palette16_alpha.webp', pil_bytes(Image.fromarray(drawing(33, 30, 16, 9, 4)), lossless=True, exact=True))
    emit('lossless_palette2_wide.webp', pil_bytes(Image.fromarray(drawing(131, 9, 2, 5)), lossless=True))
    emit('lossless_drawing_many.webp', pil_bytes(Image.fromarray(drawing(64, 64, 300, 12)), lossless=True, method=6, quality=100))
    for nl in (60, 20, 0):
        emit('lossless_near%d.webp' % nl, magick_bytes(photo(48, 40, 3, 70), 'lossless=true', 'near-lossless=%d' % nl))
    emit('lossless_magick_m0.webp', magick_bytes(photo(48, 40, 3, 71), 'lossless=true', 'method=0'))
    emit('lossless_big_cache.webp', pil_bytes(Image.fromarray(drawing(96, 96, 200, 13)), lossless=True, method=5, quality=90))

    # ---- hand written streams
    rgb = photo(19, 21, 3, 80)
    rgba = photo(19, 21, 4, 81)
    emit_hand('hand_plain.webp', rgb, lz='none')
    emit_hand('hand_plain_alpha.webp', rgba, lz='none')
    emit_hand('hand_norle_codes.webp', rgb, lz='none', style='norle')
    emit_hand('hand_trimmed_codes.webp', drawing(19, 21, 9, 82), lz='rle', style='trim')
    emit_hand('hand_subgreen.webp', rgb, transforms=[('subgreen',)], lz='none')
    # every predictor mode in a block of its own, 4x4 blocks: 19x21 pixels -> 5x6 blocks
    emit_hand('hand_predictor_all_modes.webp', rgba, transforms=[('predictor', 2, [(i * 7 + 3) % 14 for i in range(30)])], lz='none')
    for mode in range(14):
        emit_hand('hand_predictor_mode%02d.webp' % mode, rgba if mode % 2 else rgb, transforms=[('predictor', 5, [mode])], lz='none')
    emit_hand('hand_predictor_modes_14_15.webp', rgb, transforms=[('predictor', 3, [14, 15, 1, 15, 14, 2, 15, 14, 11])], lz='none')
    emit_hand('hand_predictor_width1.webp', photo(1, 9, 4, 83), transforms=[('predictor', 2, [5, 9, 13])], lz='none')
    emit_hand('hand_predictor_width2.webp', photo(2, 9, 4, 84), transforms=[('predictor', 2, [3, 10, 5])], lz='none')
    emit_hand('hand_predictor_height1.webp', photo(9, 1, 4, 85), transforms=[('predictor', 2, [3, 10, 5])], lz='none')
    cross = [(((i * 37) % 256), ((i * 91 + 5) % 256), ((i * 53 + 200) % 256)) for i in range(9)]
    emit_hand('hand_cross_color.webp', rgb, transforms=[('cross', 3, cross)], lz='none')
    emit_hand('hand_cross_color_extremes.webp', rgb, transforms=[('cross', 5, [(128, 127, 255)])], lz='none')
    emit_hand('hand_all_transforms.webp', rgba, transforms=[('subgreen',), ('predictor', 3, [(i * 5) % 14 for i in range(9)]), ('cross', 2, [((i * 29) % 256, (i * 3) % 256, (i * 111) % 256) for i in range(30)])], cache_bits=4)
    emit_hand('hand_transforms_reordered.webp', rgba, transforms=[('cross', 3, cross), ('predictor', 2, [(i * 3 + 1) % 14 for i in range(30)]), ('subgreen',)], lz='rle')
    for bits in (1, 2, 5, 10, 11):
        emit_hand('hand_cache%02d.webp' % bits, drawing(23, 17, 40, 86 + bits), cache_bits=bits, lz='none' if bits % 2 else 'rle')
    emit_hand('hand_cache_predictor.webp', rgb, transforms=[('predictor', 4, [11, 12, 13, 5])], cache_bits=6, lz='rle')
    # all short distance codes: a wide image with repeating content, matches searched in the 2-D neighborhood only
    tile = drawing(8, 8, 23, 90)
    emit_hand('hand_plane_codes.webp', np.tile(tile, (4, 5, 1))[:30, :37], lz='plane', min_len=2)
    emit_hand('hand_plane_codes_narrow.webp', np.tile(tile[:, :3], (4, 1, 1)), lz='plane', min_len=2)      # distances clamp to 1
    emit_hand('hand_plane_codes_width1.webp', np.tile(tile[:, :1], (5, 1, 1)), lz='plane', min_len=2)
    emit_hand('hand_far_distances.webp', np.tile(drawing(31, 3, 50, 91), (9, 1, 1)), lz='far', min_len=4)
    emit_hand('hand_long_runs.webp', np.full((70, 70, 3), 77, np.uint8), lz='rle')                       # copies of 4096 pixels
    emit_hand('hand_overlapping_copies.webp', np.tile(drawing(5, 1, 5, 92), (3, 30, 1)), lz='far', min_len=3)
    emit_hand('hand_copy_cache.webp', np.tile(drawing(9, 4, 12, 93), (4, 3, 1)), lz='far', cache_bits=3, min_len=5)
    for n, w in ((1, 9), (2, 17), (3, 13), (4, 16), (5, 15), (16, 33), (17, 12), (255, 20), (256, 20)):
        img = drawing(w, 11, min(n, 256), 100 + n)
        if n >= 255:
            img = np.random.default_rng(n).integers(0, 256, (11, w, 3)).astype(np.uint8)
        pal = sorted(set(int(v) for v in to_argb(img).reshape(-1)))
        while len(pal) < n:
            pal.append((pal[-1] + 0x010203) & 0xffffff | 0xff000000)
        emit_hand('hand_palette%03d_w%d.webp' % (n, w), img, transforms=[('palette', pal[:n])], lz='rle')
    pal = [0xff102030, 0x80405060, 0x00708090]
    idx = (np.indices((6, 13)).sum(0) % 3)
    emit_hand('hand_palette_alpha.webp', from_argb(np.array(pal, np.uint32)[idx], 4), transforms=[('palette', pal)], lz='none')
    emit_hand('hand_palette_predictor.webp', drawing(21, 14, 30, 110), transforms=[('palette', sorted(set(int(v) for v in to_argb(drawing(21, 14, 30, 110)).reshape(-1)))), ('predictor', 2, [(i * 3) % 14 for i in range(24)])], lz='rle')
    # indices beyond the color table decode as transparent black
    pal = [0xff0000ff, 0xff00ff00, 0xffff0000]
    idx = np.indices((5, 9)).sum(0) % 4
    emit('hand_palette_index_out_of_range.webp', vp8l_file(np.zeros((5, 9), np.uint32), alpha_hint=True, transforms=[('palette', pal, idx)], lz='none'),
         expect=from_argb(np.array(pal + [0], np.uint32)[idx], 4))
    pal = [(0xff000000 | i * 0x030201) for i in range(20)]
    idx = (np.indices((6, 11)).sum(0) * 17) % 256
    emit('hand_palette_index_out_of_range_8bit.webp', vp8l_file(np.zeros((6, 11), np.uint32), alpha_hint=True, transforms=[('palette', pal, idx)], lz='none'),
         expect=from_argb(np.array(pal + [0] * 236, np.uint32)[idx], 4))
    # entropy image: groups per block, with unused and very large group numbers
    img = photo(21, 18, 3, 120)
    emit_hand('hand_meta_2groups.webp', img, meta=(2, [(i // 3) % 2 for i in range(6 * 5)], 2), lz='none')
    emit_hand('hand_meta_9groups.webp', img, meta=(3, list(range(9)), 9), lz='rle', cache_bits=3)
    emit_hand('hand_meta_sparse.webp', img, meta=(4, [0, 700, 5, 299], 701), lz='none')
    emit_hand('hand_meta_single_block.webp', img, meta=(5, [0], 1), lz='none')
    emit_hand('hand_meta_transforms.webp', photo(21, 18, 4, 121), transforms=[('subgreen',), ('predictor', 2, [(i * 9) % 14 for i in range(30)])],
              meta=(2, [(i * 7) % 5 for i in range(30)], 5), cache_bits=2, lz='rle')
    # codes: long ones need the second level of the lookup tables; alpha hint contradicting the data
    skew = np.zeros((24, 40, 3), np.uint8)
    vals = np.repeat(np.arange(40), np.maximum(1, (960 * 0.6 ** np.arange(40)).astype(int)))[:960]
    skew[:, :, 1] = np.resize(vals, 960).reshape(24, 40)
    skew[:, :, 0] = skew[::-1, :, 1] * 3
    emit_hand('hand_long_codes.webp', skew, lz='none')
    emit('hand_alpha_hint_unset.webp', vp8l_file(to_argb(rgba), alpha_hint=False, lz='none'))
    emit('hand_alpha_hint_set_opaque.webp', vp8l_file(to_argb(rgb), alpha_hint=True, lz='none'))
    emit_hand('hand_single_color.webp', np.full((9, 9, 4), (5, 6, 7, 8), np.uint8), lz='none')           # all codes without bits
    emit_hand('hand_two_colors.webp', np.where((np.indices((9, 14)).sum(0) % 3 == 0)[:, :, None], np.uint8(200), np.uint8(17)).repeat(3, 2), lz='none')


# ---------------------------------------------------------------- animation

def still_chunks(arr, lossless=True, **kw):
    return image_chunks(pil_bytes(Image.fromarray(arr), lossless=lossless, exact=True, **kw))


def gen_animation():
    w, h = 32, 24
    frames = []
    for i in range(5):
        a = photo(w, h, 3, 200)
        a[4 + 2 * i:12 + 2 * i, 3 + 4 * i:13 + 4 * i] = (250 - 40 * i, 20 + 30 * i, 128)
        frames.append(Image.fromarray(a))
    def save_all(name, fr, **kw):
        bio = io.BytesIO()
        fr[0].save(bio, 'WEBP', save_all=True, append_images=fr[1:], **kw)
        emit(name, bio.getvalue(), anim=True)
    save_all('anim_lossless.webp', frames, lossless=True, duration=[30, 70, 110, 200, 40], loop=0)
    save_all('anim_lossy.webp', frames, quality=70, duration=100, loop=3)
    save_all('anim_lossless_minsize.webp', frames, lossless=True, duration=50, loop=1, minimize_size=True)
    save_all('anim_mixed.webp', frames, duration=20, loop=0, allow_mixed=True, quality=60)
    save_all('anim_kmin.webp', frames + frames, lossless=True, duration=10, kmin=2, kmax=3)
    save_all('anim_background.webp', frames[:2], lossless=True, duration=65535, loop=65535, background=(10, 20, 30, 40))
    # translucent frames: the encoder picks blending and disposal
    rgba = []
    for i in range(4):
        a = photo(w, h, 4, 210 + i)
        a[:, :, 3] = np.where((np.indices((h, w)).sum(0) + 5 * i) % 16 < 8, a[:, :, 3], 0)
        rgba.append(Image.fromarray(a))
    save_all('anim_alpha_lossless.webp', rgba, lossless=True, duration=40, loop=2, exact=True)
    save_all('anim_alpha_lossy.webp', rgba, quality=75, duration=40, loop=2)
    save_all('anim_alpha_minsize.webp', rgba, lossless=True, duration=40, minimize_size=True, exact=True)
    save_all('anim_single_frame.webp', frames[:1], lossless=True, duration=77)
    pal = [Image.fromarray(drawing(w, h, 6, 220 + i)) for i in range(3)]
    save_all('anim_palette.webp', pal, lossless=True, duration=[0, 1, 16777215 // 2])

    # assembled by hand: every combination of blending and disposal, offsets, frame types
    cw, ch_ = 24, 18
    def frame_px(i, fw, fh, alpha):
        a = photo(fw, fh, 4, 300 + i)
        if alpha == 'none':
            a[:, :, 3] = 255
        elif alpha == 'binary':
            a[:, :, 3] = np.where((np.indices((fh, fw)).sum(0) + i) % 5 < 3, 255, 0)
        elif alpha == 'low':
            a[:, :, 3] = 1 + (np.indices((fh, fw)).sum(0) * 3 + i) % 12       # tiny alpha: the blend arithmetic shows
        return a
    rects = [(0, 0, cw, ch_), (4, 2, 12, 10), (10, 6, 14, 12), (0, 8, 12, 10), (2, 2, 20, 14), (0, 0, cw, ch_), (8, 10, 9, 7)]
    for blend0 in (True, False):
        for dispose0 in (False, True):
            for alpha in ('soft', 'binary', 'low', 'none'):
                for lossless in (True, False):
                    if not lossless and alpha in ('binary', 'low'):
                        continue
                    parts = []
                    for i, (x, y, fw, fh) in enumerate(rects):
                        blend = blend0 if i % 3 else not blend0
                        dispose = dispose0 if i % 2 == 0 else not dispose0
                        px = frame_px(i, fw, fh, alpha if i != 5 else 'soft')
                        fc = still_chunks(px if alpha != 'none' or i == 5 else px[:, :, :3], lossless, quality=80)
                        parts.append(anmf(x, y, fw, fh, 10 * (i + 1), blend, dispose, fc))
                    name = 'hand_anim_%s_%s_%s_%s.webp' % ('blend' if blend0 else 'noblend', 'dispose' if dispose0 else 'keep', alpha, 'lossless' if lossless else 'lossy')
                    emit(name, riff(vp8x(ANIM | ALPHA, cw, ch_), chunk(b'ANIM', struct.pack('<IH', 0x80402010, 7)), *parts), anim=True)
    # uniform settings for all frames
    for blend in (True, False):
        for dispose in (False, True):
            parts = [anmf(x, y, fw, fh, 5, blend, dispose, still_chunks(frame_px(i, fw, fh, 'soft'))) for i, (x, y, fw, fh) in enumerate(rects[1:5])]
            emit('hand_anim_all_%s_%s.webp' % ('blend' if blend else 'noblend', 'dispose' if dispose else 'keep'),
                 riff(vp8x(ANIM | ALPHA, cw, ch_), chunk(b'ANIM', struct.pack('<IH', 0, 0)), *parts), anim=True)
    # no alpha flag although the frames are translucent and do not cover the canvas
    parts = [anmf(x, y, fw, fh, 5, True, False, still_chunks(frame_px(i, fw, fh, 'soft'))) for i, (x, y, fw, fh) in enumerate(rects[1:4])]
    emit('hand_anim_no_alpha_flag.webp', riff(vp8x(ANIM, cw, ch_), chunk(b'ANIM', struct.pack('<IH', 0xffffffff, 1)), *parts), anim=True)
    full = [anmf(0, 0, cw, ch_, 5, True, False, still_chunks(frame_px(i, cw, ch_, 'none')[:, :, :3], False, quality=60)) for i in range(2)]
    emit('hand_anim_opaque_no_alpha_flag.webp', riff(vp8x(ANIM, cw, ch_), chunk(b'ANIM', struct.pack('<IH', 0xff000000, 0)), *full), anim=True)
    # unknown chunks inside and between frames, metadata around the animation, odd payloads
    f0 = still_chunks(frame_px(0, cw, ch_, 'soft'))
    f1 = still_chunks(frame_px(1, 9, 7, 'soft'))
    head = [vp8x(ANIM | ALPHA | ICC | EXIF | XMP, cw, ch_), chunk(b'ICCP', b'fake icc profile')]
    tail = [chunk(b'EXIF', exif_blob(6)), chunk(b'XMP ', b'<x:xmpmeta/>')]
    good = riff(*head, chunk(b'ANIM', struct.pack('<IH', 0x01020304, 9)), anmf(0, 0, cw, ch_, 1, True, False, f0 + chunk(b'XTRA', b'12345')),
                anmf(14, 10, 9, 7, 2, True, True, f1 + chunk(b'FOO ', b'x')), *tail, chunk(b'UNKN', b''))
    emit('hand_anim_unknown_chunks.webp', good, anim=True)
    # unknown chunks where the format does not foresee them
    emit('hand_anim_unknown_chunks_lenient.webp', riff(*head, chunk(b'JUNK', b'abc'), chunk(b'ANIM', struct.pack('<IH', 0x01020304, 9)),
         anmf(0, 0, cw, ch_, 1, True, False, f0 + chunk(b'XTRA', b'12345')), chunk(b'UNKN', b''),
         anmf(14, 10, 9, 7, 2, True, True, chunk(b'FOO ', b'x') + f1), *tail), anim=True, like=good)
    emit('hand_anim_1x1_canvas.webp', riff(vp8x(ANIM | ALPHA, 1, 1), chunk(b'ANIM', struct.pack('<IH', 0, 0)),
         anmf(0, 0, 1, 1, 3, True, False, still_chunks(frame_px(0, 1, 1, 'soft'))), anmf(0, 0, 1, 1, 4, True, True, still_chunks(frame_px(1, 1, 1, 'low')))), anim=True)


def exif_blob(orientation, big_endian=False):
    e = '>' if big_endian else '<'
    return (b'MM\0*' if big_endian else b'II*\0') + struct.pack(e + 'IH', 8, 2) + struct.pack(e + 'HHIHH', 0x0112, 3, 1, orientation, 0) \
        + struct.pack(e + 'HHII', 0x011a, 5, 1, 38) + struct.pack(e + 'I', 0) + struct.pack(e + 'II', 72, 1)


# ---------------------------------------------------------------- container edge cases

def gen_container():
    rgb = photo(20, 14, 3, 400)
    rgba = photo(20, 14, 4, 401)
    lossy = pil_bytes(Image.fromarray(rgb), quality=80)
    lossy_a = pil_bytes(Image.fromarray(rgba), quality=80)
    lossless = pil_bytes(Image.fromarray(rgb), lossless=True)
    lossless_a = pil_bytes(Image.fromarray(rgba), lossless=True, exact=True)
    vp8 = dict(chunks_of(lossy))[b'VP8 ']
    vp8l = dict(chunks_of(lossless))[b'VP8L']
    vp8l_a = dict(chunks_of(lossless_a))[b'VP8L']
    alph = dict(chunks_of(lossy_a))[b'ALPH']
    vp8_a = dict(chunks_of(lossy_a))[b'VP8 ']

    emit('edge_simple_lossy.webp', riff(chunk(b'VP8 ', vp8)))
    emit('edge_simple_lossless.webp', riff(chunk(b'VP8L', vp8l)))
    emit('edge_vp8x_lossy.webp', riff(vp8x(0, 20, 14), chunk(b'VP8 ', vp8)))
    emit('edge_vp8x_lossless.webp', riff(vp8x(0, 20, 14), chunk(b'VP8L', vp8l)))
    emit('edge_vp8x_alpha_flag_without_alph.webp', riff(vp8x(ALPHA, 20, 14), chunk(b'VP8 ', vp8)))
    emit('edge_vp8x_alph_without_alpha_flag.webp', riff(vp8x(0, 20, 14), chunk(b'ALPH', alph), chunk(b'VP8 ', vp8_a)))
    emit('edge_vp8x_lossless_alpha_without_flag.webp', riff(vp8x(0, 20, 14), chunk(b'VP8L', vp8l_a)))
    emit('edge_vp8x_flag_lossless_opaque.webp', riff(vp8x(ALPHA, 20, 14), chunk(b'VP8L', vp8l)))
    emit('edge_alph_before_vp8l.webp', riff(vp8x(ALPHA, 20, 14), chunk(b'ALPH', alph), chunk(b'VP8L', vp8l_a)), like=riff(vp8x(ALPHA, 20, 14), chunk(b'VP8L', vp8l_a)))
    emit('edge_unknown_chunks.webp', riff(vp8x(ALPHA, 20, 14), chunk(b'abcd', b'123'), chunk(b'ALPH', alph), chunk(b'ZZZZ', b''), chunk(b'VP8 ', vp8_a), chunk(b'TAIL', b'tail data')), like=lossy_a)
    emit('edge_metadata.webp', riff(vp8x(ICC | EXIF | XMP, 20, 14), chunk(b'ICCP', bytes(range(200)) * 3), chunk(b'VP8 ', vp8), chunk(b'EXIF', exif_blob(6)), chunk(b'XMP ', b'<?xpacket begin=""?><x:xmpmeta xmlns:x="adobe:ns:meta/"/>')))
    emit('edge_metadata_before_image.webp', riff(vp8x(EXIF | XMP, 20, 14), chunk(b'XMP ', b'<x/>'), chunk(b'EXIF', b'Exif\0\0' + exif_blob(3, True)), chunk(b'VP8L', vp8l)))
    emit('edge_metadata_without_flags.webp', riff(vp8x(0, 20, 14), chunk(b'ICCP', b'icc'), chunk(b'VP8L', vp8l), chunk(b'EXIF', exif_blob(8)), chunk(b'XMP ', b'xmp')))
    emit('edge_duplicate_metadata.webp', riff(vp8x(ICC | EXIF, 20, 14), chunk(b'ICCP', b'first'), chunk(b'ICCP', b'second'), chunk(b'VP8L', vp8l), chunk(b'EXIF', exif_blob(2)), chunk(b'EXIF', exif_blob(4))))
    emit('edge_trailing_data.webp', riff(chunk(b'VP8L', vp8l)) + b'trailing bytes after the RIFF chunk')
    # an odd sized last chunk whose padding byte is missing, with the RIFF size counting the bytes that are there
    odd = vp8l if len(vp8l) & 1 else vp8l + b'\0'
    body = b'WEBP' + b'VP8L' + struct.pack('<I', len(odd)) + odd
    emit('edge_missing_padding.webp', b'RIFF' + struct.pack('<I', len(body)) + body, check=False, like=riff(chunk(b'VP8L', vp8l)))
    body = b'WEBP' + chunk(b'VP8L', odd)
    emit('edge_odd_riff_size.webp', b'RIFF' + struct.pack('<I', len(body) - 1) + body, check=False, like=riff(chunk(b'VP8L', vp8l)))
    emit('edge_anim_chunks_without_flag.webp', riff(vp8x(0, 20, 14), chunk(b'ANIM', struct.pack('<IH', 0, 0)), chunk(b'VP8L', vp8l)), like=riff(chunk(b'VP8L', vp8l)))
    emit('edge_vp8x_longer.webp', riff(chunk(b'VP8X', bytes([0, 0, 0, 0]) + u24(19) + u24(13) + b'future fields'), chunk(b'VP8L', vp8l)), like=riff(chunk(b'VP8L', vp8l)))
    emit('edge_vp8x_reserved_bits.webp', riff(chunk(b'VP8X', bytes([0xc1, 0xff, 0xff, 0xff]) + u24(19) + u24(13)), chunk(b'VP8L', vp8l)), like=riff(chunk(b'VP8L', vp8l)))
    emit('edge_two_images.webp', riff(vp8x(0, 20, 14), chunk(b'VP8L', vp8l), chunk(b'VP8 ', dict(chunks_of(pil_bytes(Image.fromarray(photo(20, 14, 3, 5)), quality=30)))[b'VP8 '])), check=False, like=riff(chunk(b'VP8L', vp8l)))
    emit('edge_lossy_trailing_garbage_in_chunk.webp', riff(chunk(b'VP8 ', vp8 + b'\x55' * 9)))
    emit('edge_lossless_trailing_garbage_in_chunk.webp', riff(chunk(b'VP8L', vp8l + b'\xaa' * 9)))
    emit('edge_alph_longer_raw.webp', riff(vp8x(ALPHA, 20, 14), chunk(b'ALPH', b'\0' + bytes(rgba[:, :, 3].tobytes()) + b'extra'), chunk(b'VP8 ', vp8_a)))
    raw_alpha = riff(vp8x(ALPHA, 20, 14), chunk(b'ALPH', b'\0' + rgba[:, :, 3].tobytes()), chunk(b'VP8 ', vp8_a))
    emit('edge_alph_preprocessing_bit.webp', riff(vp8x(ALPHA, 20, 14), chunk(b'ALPH', bytes([0x10]) + rgba[:, :, 3].tobytes()), chunk(b'VP8 ', vp8_a)))
    # reserved bits and unknown pre-processing values: to be ignored by the format's rules, libwebp rejects them
    emit('edge_alph_reserved_bits.webp', riff(vp8x(ALPHA, 20, 14), chunk(b'ALPH', bytes([0xf0]) + rgba[:, :, 3].tobytes()), chunk(b'VP8 ', vp8_a)), like=raw_alpha)


# ---------------------------------------------------------------- conformance vectors

def gen_vectors():
    d = testdata_dir('fixtures', 'webp', 'vectors')
    if d is None:
        return
    lines = []
    for name in sorted(os.listdir(d)):
        if not name.endswith('.webp'):
            continue
        data = open(os.path.join(d, name), 'rb').read()
        frames = libwebp_frames(data)[0]
        px = frames[0] if output_has_alpha(data) else frames[0][:, :, :3]
        import imagecodecs
        other = imagecodecs.webp_decode(data)
        c = min(other.shape[2], px.shape[2])
        if not np.array_equal(other[:, :, :c], px[:, :, :c]):
            raise RuntimeError('vector %s: Pillow and imagecodecs disagree' % name)
        lines.append('%s %d %d %d %s' % (name, px.shape[1], px.shape[0], px.shape[2], hashlib.sha256(px.tobytes()).hexdigest()))
    # the vector files are not committed (see vectors/README): keep the list when they are not fetched
    if not lines:
        return
    with open(os.path.join(d, 'refs.txt'), 'w') as f:
        f.write('\n'.join(lines) + '\n')
    if REPORT:
        print('%d conformance vectors hashed' % len(lines))


def main():
    os.makedirs(OUT, exist_ok=True)
    for f in os.listdir(OUT):
        p = os.path.join(OUT, f)
        if os.path.isfile(p):
            os.remove(p)
    gen_lossy()
    gen_lossless()
    gen_animation()
    gen_container()
    gen_vectors()
    total = sum(os.path.getsize(os.path.join(r, f)) for r, _, fs in os.walk(OUT) for f in fs)
    print('%d fixtures, %.2f MB with references' % (COUNT[0], total / 1e6))


if __name__ == '__main__':
    main()
