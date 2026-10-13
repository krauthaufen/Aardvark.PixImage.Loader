"""Generates tests/fixtures/gif: name.gif, name.gif.ref (what Gif.Decode returns) and name.gif.N.ref (the composited
RGBA canvas after frame N).

Three kinds of fixtures:
  * written by Pillow / ImageMagick / ffmpeg; references are Pillow's and ImageMagick's (-coalesce) decodes, which
    must agree unless the file is listed in PREFER with the reason;
  * hand built with the small writer below (things the tools cannot write); the reference is computed from the
    palette indices that went into the file with the browser compositing rules (canvas starts transparent,
    disposal 2 clears to transparent, frames are clipped to the screen) and is cross checked against the tools
    where they are able to read the file - disagreements are printed;
  * damaged files (truncated, missing end code): reference by construction only.

Transparent pixels are normalized to (0,0,0,0) everywhere.

usage: python3 gen_gif.py [--report]
"""
import io
import os
import shutil
import struct
import subprocess
import sys
import numpy as np
from PIL import Image, ImageSequence
from pxref import write_ref, test_pattern

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'fixtures', 'gif')
REPORT = '--report' in sys.argv


# ---------------------------------------------------------------- reference decoders

def norm(a):
    a = np.array(a, dtype=np.uint8)
    a[a[:, :, 3] == 0] = 0
    return a


def pillow_frames(path):
    im = Image.open(path)
    return [norm(f.convert('RGBA')) for f in ImageSequence.Iterator(im)]


def magick_frames(path):
    with Image.open(path) as im:
        w, h = im.size
    raw = subprocess.run(['magick', path, '-coalesce', '-depth', '8', 'rgba:-'], capture_output=True, check=True).stdout
    n = len(raw) // (w * h * 4)
    return [norm(np.frombuffer(raw, np.uint8, w * h * 4, i * w * h * 4).reshape(h, w, 4)) for i in range(n)]


def first_frame_header(data):
    """(screen w, screen h, frame rect, has transparent index) of the first image - decides Rgb vs Rgba."""
    w, h, packed = struct.unpack_from('<HHB', data, 6)
    p = 13 + (3 * (2 << (packed & 7)) if packed & 0x80 else 0)
    trans = False
    while True:
        b = data[p]; p += 1
        if b == 0x21:
            label = data[p]; p += 1
            if label == 0xF9:
                trans = bool(data[p + 1] & 1)
            while data[p]:
                p += data[p] + 1
            p += 1
        elif b == 0x2C:
            x, y, fw, fh = struct.unpack_from('<HHHH', data, p)
            if w == 0 or h == 0:
                w, h = x + fw, y + fh
            return w, h, (x, y, fw, fh), trans
        else:
            raise ValueError('no image')


def write_refs(name, frames):
    path = os.path.join(OUT, name)
    data = open(path, 'rb').read()
    w, h, (x, y, fw, fh), trans = first_frame_header(data)
    alpha = trans or x != 0 or y != 0 or fw < w or fh < h
    write_ref(path + '.ref', frames[0] if alpha else frames[0][:, :, :3])
    for i, f in enumerate(frames):
        write_ref(f'{path}.{i}.ref', f)


def same(a, b):
    return len(a) == len(b) and all(x.shape == y.shape and np.array_equal(x, y) for x, y in zip(a, b))


# Tool-written files where Pillow and ImageMagick disagree: which one matches browsers, and why.
PREFER = {
}


def tool_fixture(name):
    """References for a file written by a third party tool."""
    path = os.path.join(OUT, name)
    p, m = pillow_frames(path), magick_frames(path)
    if same(p, m):
        write_refs(name, p)
        return
    if name not in PREFER:
        raise SystemExit(f'{name}: Pillow and ImageMagick disagree and no preference is recorded')
    if REPORT:
        print(f'{name}: oracles disagree, using {PREFER[name][0]} ({PREFER[name][1]})')
    write_refs(name, p if PREFER[name][0] == 'pillow' else m)


# ---------------------------------------------------------------- hand written GIF writer

def lzw(indices, mcs, clear_when_full=True, initial_clear=True, end_code=True, extra_clears=0):
    """GIF LZW code stream (bytes, not yet split into sub-blocks)."""
    clear, eoi = 1 << mcs, (1 << mcs) + 1
    out, acc, nbits = bytearray(), 0, 0

    def emit(code, size):
        nonlocal acc, nbits
        acc |= code << nbits
        nbits += size
        while nbits >= 8:
            out.append(acc & 255)
            acc >>= 8
            nbits -= 8

    size, nxt, table = mcs + 1, eoi + 1, {}
    if initial_clear:
        for _ in range(1 + extra_clears):
            emit(clear, size)
    prefix = None
    for px in indices:
        px = int(px)
        if prefix is None:
            prefix = px
            continue
        if (prefix, px) in table:
            prefix = table[(prefix, px)]
            continue
        emit(prefix, size)
        if nxt < 4096:
            table[(prefix, px)] = nxt
            if nxt == 1 << size:
                size += 1
            nxt += 1
        elif clear_when_full:
            emit(clear, size)
            size, nxt, table = mcs + 1, eoi + 1, {}
        prefix = px
    emit(prefix, size)
    if nxt < 4096 and nxt == 1 << size:
        size += 1
    if end_code:
        emit(eoi, size)
    if nbits:
        out.append(acc & 255)
    return bytes(out)


def blocks(data, size=255):
    out = bytearray()
    for i in range(0, len(data), size):
        chunk = data[i:i + size]
        out += bytes([len(chunk)]) + chunk
    return bytes(out) + b'\0'


def ctable(pal):
    """pal: list of (r,g,b); padded to a power of two. Returns (size bits - 1, bytes)."""
    bits = 1
    while (1 << bits) < len(pal):
        bits += 1
    pal = list(pal) + [(0, 0, 0)] * ((1 << bits) - len(pal))
    return bits - 1, bytes(c for rgb in pal for c in rgb)


INTERLACE = lambda h: [*range(0, h, 8), *range(4, h, 8), *range(2, h, 4), *range(1, h, 2)]


class Frame:
    def __init__(self, idx, x=0, y=0, pal=None, trans=None, disposal=0, delay=0, interlace=False, mcs=None,
                 gce=None, block=255, mid=b'', **lzw_args):
        self.idx = np.asarray(idx, dtype=np.uint8)
        self.x, self.y, self.pal, self.trans, self.disposal, self.delay = x, y, pal, trans, disposal, delay
        self.interlace, self.mcs, self.block, self.mid, self.lzw_args = interlace, mcs, block, mid, lzw_args
        self.gce = gce if gce is not None else (trans is not None or disposal != 0 or delay != 0)
        self.decoded = None     # number of pixels (file order) a decoder gets to see, None = all


def build(w, h, gpal, frames, version=b'GIF89a', bg=0, loop=None, loop_id=b'NETSCAPE2.0', trailer=True, head=b'',
          tail=b''):
    out = bytearray(version)
    if gpal is not None:
        bits, tab = ctable(gpal)
        out += struct.pack('<HHBBB', w, h, 0xF0 | bits, bg, 0) + tab
    else:
        out += struct.pack('<HHBBB', w, h, 0x70, bg, 0)
    if loop is not None:
        out += b'\x21\xFF\x0B' + loop_id + b'\x03\x01' + struct.pack('<H', loop) + b'\0'
    out += head
    for f in frames:
        fh, fw = f.idx.shape
        if f.gce:
            flags = (f.disposal << 2) | (1 if f.trans is not None else 0)
            out += b'\x21\xF9\x04' + struct.pack('<BHB', flags, f.delay, f.trans or 0) + b'\0'
        out += f.mid
        packed = 0x40 if f.interlace else 0
        tab = b''
        ncol = len(gpal) if gpal is not None else 256
        if f.pal is not None:
            bits, tab = ctable(f.pal)
            packed |= 0x80 | bits
            ncol = len(f.pal)
        out += b'\x2C' + struct.pack('<HHHHB', f.x, f.y, fw, fh, packed) + tab
        mcs = f.mcs
        if mcs is None:
            mcs = 2
            while (1 << mcs) < ncol:
                mcs += 1
        rows = f.idx[INTERLACE(fh)] if f.interlace else f.idx
        out += bytes([mcs]) + blocks(lzw(rows.ravel(), mcs, **f.lzw_args), f.block)
    out += tail
    if trailer:
        out += b'\x3B'
    return bytes(out)


def composite(w, h, gpal, frames):
    """Browser rules, straight from the indices."""
    canvas = np.zeros((h, w, 4), np.uint8)
    result, prev = [], None
    for k, f in enumerate(frames):
        if prev is not None:
            pf, backup = prev
            ph, pw = pf.idx.shape
            if pf.disposal == 2:
                canvas[pf.y:pf.y + ph, pf.x:pf.x + pw] = 0
            elif pf.disposal in (3, 4):
                canvas = backup
        backup = canvas.copy()
        pal = f.pal if f.pal is not None else gpal
        if pal is None:
            pal = [(i, i, i) for i in range(256)]
        fh, fw = f.idx.shape
        order = INTERLACE(fh) if f.interlace else list(range(fh))
        n = 0
        for row in order:
            for col in range(fw):
                n += 1
                if f.decoded is not None and n > f.decoded:
                    continue
                i = int(f.idx[row, col])
                yy, xx = f.y + row, f.x + col
                if i == f.trans or yy >= h or xx >= w:
                    continue
                canvas[yy, xx] = (*pal[i], 255) if i < len(pal) else (0, 0, 0, 255)
        result.append(canvas.copy())
        prev = (f, backup)
    return result


def hand_fixture(name, w, h, gpal, frames, data=None, check=True, expected=None, **kw):
    """Writes a hand built file. check=False for files the tools are not expected to read the same way."""
    if data is None:
        data = build(w, h, gpal, frames, **kw)
    path = os.path.join(OUT, name)
    open(path, 'wb').write(data)
    exp = expected if expected is not None else composite(w, h, gpal, frames)
    write_refs(name, exp)
    if REPORT:
        for tool, fn in (('pillow', pillow_frames), ('magick', magick_frames)):
            try:
                got = fn(path)
                verdict = 'same' if same(got, exp) else 'DIFFERENT'
            except Exception as e:
                verdict = 'cannot read (' + type(e).__name__ + ')'
            if check or verdict == 'same':
                print(f'{name:34s} {tool}: {verdict}')
            else:
                print(f'{name:34s} {tool}: {verdict} [expected to differ]')


# ---------------------------------------------------------------- fixtures

def quantized(w, h, colors, seed=1):
    return Image.fromarray(test_pattern(w, h, 3, seed=seed)).quantize(colors, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE)


def save(im, name, **kw):
    im.save(os.path.join(OUT, name), **kw)
    tool_fixture(name)


def gen_pillow():
    for colors, (w, h) in ((2, (33, 21)), (4, (17, 9)), (16, (40, 31)), (256, (61, 47))):
        save(quantized(w, h, colors), f'pil_pal{colors}.gif')
        save(quantized(w, h, colors), f'pil_pal{colors}_interlaced.gif', interlace=True)
    for h in (1, 2, 3, 4, 5, 7, 8, 9):
        save(quantized(6, h, 8, seed=h), f'pil_interlaced_h{h}.gif', interlace=True)
    save(Image.fromarray(test_pattern(23, 19, 1)[:, :, 0]), 'pil_gray.gif')
    save(Image.new('1', (9, 5), 1), 'pil_bilevel.gif')
    save(quantized(1, 1, 2), 'pil_1x1.gif')
    save(quantized(300, 3, 64), 'pil_wide.gif')
    save(quantized(3, 300, 64), 'pil_tall.gif', interlace=True)
    im = quantized(31, 23, 16)
    save(im, 'pil_transparent.gif', transparency=3)
    save(im, 'pil_transparent_interlaced.gif', transparency=0, interlace=True)
    save(im, 'pil_comment.gif', comment=b'hello gif ' * 40)
    rgba = test_pattern(29, 22, 4, seed=5)
    rgba[:, :, 3] = np.where(rgba[:, :, 3] > 128, 255, 0)
    save(Image.fromarray(rgba), 'pil_rgba.gif')

    # Animations: a square moving over a pattern, full frames in different modes.
    def moving(n, w=32, h=24, mode='RGB'):
        frames = []
        for i in range(n):
            a = test_pattern(w, h, 3, seed=2) // 2
            a[4 + i * 3:10 + i * 3, 3 + i * 5:11 + i * 5] = (250, 40 * i, 20)
            frames.append(Image.fromarray(a).quantize(32, dither=Image.Dither.NONE))
        return frames
    fr = moving(4)
    # Not with disposal 3: Pillow writes "restore previous" on the first frame too and then deltas against it;
    # Pillow, ImageMagick and browsers each show such a file differently (see hand_anim_disposal3*.gif instead).
    for d in (0, 1, 2):
        fr[0].save(os.path.join(OUT, f'pil_anim_disposal{d}.gif'), save_all=True, append_images=fr[1:], duration=[30, 70, 110, 200],
                   loop=0, disposal=d)
        tool_fixture(f'pil_anim_disposal{d}.gif')
    fr[0].save(os.path.join(OUT, 'pil_anim_loop3.gif'), save_all=True, append_images=fr[1:], duration=50, loop=3, optimize=False)
    tool_fixture('pil_anim_loop3.gif')
    fr[0].save(os.path.join(OUT, 'pil_anim_noloop.gif'), save_all=True, append_images=fr[1:], duration=50)
    tool_fixture('pil_anim_noloop.gif')
    # different palettes per frame -> local color tables
    loc = [Image.fromarray(test_pattern(27, 18, 3, seed=s)).quantize(64, dither=Image.Dither.NONE) for s in (3, 4, 5)]
    loc[0].save(os.path.join(OUT, 'pil_anim_local_palettes.gif'), save_all=True, append_images=loc[1:], duration=40, loop=0)
    tool_fixture('pil_anim_local_palettes.gif')
    # transparent animation with a growing transparent hole
    tr = []
    for i in range(4):
        a = test_pattern(30, 20, 4, seed=7)
        a[:, :, 3] = 255
        a[2:4 + 4 * i, 5:8 + 5 * i, 3] = 0
        tr.append(Image.fromarray(a))
    for d in (1, 2):
        tr[0].save(os.path.join(OUT, f'pil_anim_transparent_d{d}.gif'), save_all=True, append_images=tr[1:], duration=60, loop=0, disposal=d)
        tool_fixture(f'pil_anim_transparent_d{d}.gif')


def run(*cmd):
    subprocess.run(cmd, check=True, capture_output=True)


def gen_tools():
    tmp = os.path.join(OUT, '_src')
    os.makedirs(tmp, exist_ok=True)
    try:
        src = os.path.join(tmp, 'a.png')
        Image.fromarray(test_pattern(45, 34, 3, seed=9)).save(src)
        o = lambda n: os.path.join(OUT, n)
        run('magick', src, o('im_256.gif')); tool_fixture('im_256.gif')
        run('magick', src, '-colors', '7', o('im_7colors.gif')); tool_fixture('im_7colors.gif')
        run('magick', src, '-interlace', 'GIF', o('im_interlaced.gif')); tool_fixture('im_interlaced.gif')
        run('magick', src, '-colorspace', 'Gray', '-colors', '5', o('im_gray5.gif')); tool_fixture('im_gray5.gif')
        run('magick', '-size', '37x25', 'xc:none', '-fill', 'red', '-draw', 'circle 18,12 18,3', '-fill', 'blue', '-draw',
            'rectangle 2,2 9,20', o('im_transparent.gif')); tool_fixture('im_transparent.gif')
        frames = []
        for i in range(5):
            f = os.path.join(tmp, f'f{i}.png')
            a = test_pattern(36, 26, 3, seed=11) // 3 * 2
            a[3 + 4 * i:9 + 4 * i, 20 - 3 * i:27 - 3 * i] = (255, 255, 40 * i)
            Image.fromarray(a).save(f)
            frames.append(f)
        run('magick', '-delay', '7', '-loop', '2', *frames, '-colors', '48', o('im_anim.gif')); tool_fixture('im_anim.gif')
        run('magick', '-delay', '7', '-loop', '0', *frames, '-colors', '48', '-layers', 'OptimizeFrame', o('im_anim_optframe.gif'))
        tool_fixture('im_anim_optframe.gif')
        run('magick', '-delay', '12', *frames, '-colors', '48', '-layers', 'OptimizeTransparency', o('im_anim_opttrans.gif'))
        tool_fixture('im_anim_opttrans.gif')
        run('magick', '-delay', '5', '-dispose', 'Background', *frames, '-colors', '48', o('im_anim_dispose_bg.gif'))
        tool_fixture('im_anim_dispose_bg.gif')
        run('magick', '-delay', '5', '-dispose', 'Previous', *frames, '-colors', '48', o('im_anim_dispose_prev.gif'))
        tool_fixture('im_anim_dispose_prev.gif')
        if shutil.which('ffmpeg'):
            ff = ['ffmpeg', '-y', '-v', 'error', '-fflags', '+bitexact', '-flags', '+bitexact']
            run(*ff, '-f', 'lavfi', '-i', 'testsrc=size=48x36:rate=5', '-frames:v', '6', o('ff_testsrc.gif'))
            tool_fixture('ff_testsrc.gif')
            run(*ff, '-f', 'lavfi', '-t', '1', '-i', 'mandelbrot=size=40x30:rate=4', '-vf',
                'split[a][b];[a]palettegen=max_colors=32[p];[b][p]paletteuse=dither=none', o('ff_mandelbrot_palette.gif'))
            tool_fixture('ff_mandelbrot_palette.gif')
            run(*ff, '-i', src, o('ff_still.gif'))
            tool_fixture('ff_still.gif')
    finally:
        shutil.rmtree(tmp)


PAL4 = [(255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 255, 0)]
PAL8 = PAL4 + [(0, 255, 255), (255, 0, 255), (30, 30, 30), (240, 240, 240)]
GRAY256 = [(i, 255 - i, (i * 7) & 255) for i in range(256)]


def gen_hand():
    rng = np.random.default_rng(42)
    R = lambda h, w, n: rng.integers(0, n, (h, w), dtype=np.uint8)

    hand_fixture('hand_1x1.gif', 1, 1, PAL4, [Frame([[2]])])
    hand_fixture('hand_1x1_gif87a.gif', 1, 1, PAL4, [Frame([[1]])], version=b'GIF87a')
    hand_fixture('hand_gif87a.gif', 13, 7, PAL8, [Frame(R(7, 13, 8))], version=b'GIF87a')

    # LZW corner cases
    noise = R(80, 100, 256)
    hand_fixture('hand_deferred_clear.gif', 100, 80, GRAY256, [Frame(noise, clear_when_full=False)])
    hand_fixture('hand_clear_when_full.gif', 100, 80, GRAY256, [Frame(noise)])
    hand_fixture('hand_no_initial_clear.gif', 20, 11, PAL8, [Frame(R(11, 20, 8), initial_clear=False)])
    hand_fixture('hand_repeated_clears.gif', 20, 11, PAL8, [Frame(R(11, 20, 8), extra_clears=3)])
    two = R(9, 14, 2)
    hand_fixture('hand_mincode1.gif', 14, 9, PAL4[:2], [Frame(two, mcs=1)], check=False)
    hand_fixture('hand_mincode2_2colors.gif', 14, 9, PAL4[:2], [Frame(two, mcs=2)])
    hand_fixture('hand_mincode8_2colors.gif', 14, 9, PAL4[:2], [Frame(two, mcs=8)])
    hand_fixture('hand_mincode3_4colors.gif', 14, 9, PAL4, [Frame(R(9, 14, 4), mcs=3)])
    for mcs in (2, 3, 4, 5, 6, 7, 8):
        n = 1 << mcs
        hand_fixture(f'hand_mincode{mcs}.gif', 37, 29, GRAY256[:n], [Frame(R(29, 37, n), mcs=mcs)])
    runs = np.zeros((40, 60), np.uint8)
    runs[10:30, 5:55] = 1
    hand_fixture('hand_long_runs.gif', 60, 40, PAL4, [Frame(runs)])      # KwKwK codes, long strings
    hand_fixture('hand_solid_big.gif', 200, 150, PAL4, [Frame(np.full((150, 200), 3, np.uint8), clear_when_full=False)])
    hand_fixture('hand_block_size_1.gif', 20, 11, PAL8, [Frame(R(11, 20, 8), block=1)])
    hand_fixture('hand_block_size_7.gif', 20, 11, PAL8, [Frame(R(11, 20, 8), block=7)])

    # Missing end code / trailer, data after the end code, surplus pixels
    img = R(12, 16, 8)
    hand_fixture('hand_no_end_code.gif', 16, 12, PAL8, [Frame(img, end_code=False)], check=False)
    hand_fixture('hand_no_trailer.gif', 16, 12, PAL8, [Frame(img)], trailer=False, check=False)
    full = build(16, 12, PAL8, [Frame(img)])
    # cut inside the pixel data: keep whole codes for the first rows only
    cut = Frame(img)
    stream = lzw(img.ravel()[:100], 3, end_code=False)
    head = full[:full.index(b'\x2C') + 10]
    data = head + bytes([3]) + blocks(stream)[:-1]      # no terminator, no trailer: the file just stops
    cut.decoded = 100
    hand_fixture('hand_truncated_data.gif', 16, 12, PAL8, [cut], data=data, check=False)
    # a sub-block that announces more bytes than the file has
    data2 = head + bytes([3, 200]) + stream
    hand_fixture('hand_truncated_subblock.gif', 16, 12, PAL8, [cut], data=data2, check=False)
    # the same damage in an interlaced frame leaves holes between the rows
    icut = Frame(img, interlace=True)
    istream = lzw(img[INTERLACE(12)].ravel()[:70], 3, end_code=False)
    ihead = bytearray(head); ihead[-1] |= 0x40
    icut.decoded = 70
    hand_fixture('hand_truncated_interlaced.gif', 16, 12, PAL8, [icut], data=bytes(ihead) + bytes([3]) + blocks(istream), check=False)
    # transparent frame, truncated: the rest stays transparent
    tcut = Frame(img, trans=5)
    tcut.decoded = 100
    tfull = build(16, 12, PAL8, [Frame(img, trans=5)])
    thead = tfull[:tfull.index(b'\x2C') + 10]
    hand_fixture('hand_truncated_transparent.gif', 16, 12, PAL8, [tcut], data=thead + bytes([3]) + blocks(stream)[:-1], check=False)
    # second frame cut off in its header: only the first one counts
    two_frames = build(16, 12, PAL8, [Frame(img, delay=5), Frame(R(12, 16, 8), delay=5)])
    second = two_frames.index(b'\x2C', two_frames.index(b'\x2C') + 1)
    hand_fixture('hand_truncated_second_header.gif', 16, 12, PAL8, [Frame(img, delay=5)], data=two_frames[:second + 5], check=False)
    # more pixels than the frame holds, then garbage after the end code inside the data blocks
    big = np.vstack([img, R(3, 16, 8)])
    over = head + bytes([3]) + blocks(lzw(big.ravel(), 3) + b'\x55\xAA\x13')+ b'\x3B'
    hand_fixture('hand_surplus_pixels.gif', 16, 12, PAL8, [Frame(img)], data=over, check=False)
    hand_fixture('hand_junk_after_trailer.gif', 16, 12, PAL8, [Frame(img)], tail=b'\x3Bjunk junk\x00\xFF', check=False)

    # Palettes
    hand_fixture('hand_local_palette_only.gif', 11, 6, None, [Frame(R(6, 11, 4), pal=PAL4)])
    hand_fixture('hand_local_overrides_global.gif', 11, 6, PAL8, [Frame(R(6, 11, 4), pal=PAL4[::-1])])
    hand_fixture('hand_no_palette.gif', 11, 6, None, [Frame(R(6, 11, 200), mcs=8)], check=False)
    hand_fixture('hand_index_beyond_palette.gif', 11, 6, PAL4, [Frame(R(6, 11, 8), mcs=3)], check=False)
    hand_fixture('hand_transparent_beyond_palette.gif', 11, 6, PAL4, [Frame(R(6, 11, 8), mcs=3, trans=6)], check=False)
    hand_fixture('hand_palette_256_local.gif', 32, 16, PAL4, [Frame(R(16, 32, 256), pal=GRAY256)])
    hand_fixture('hand_palette_3_entries.gif', 9, 9, PAL4[:3], [Frame(R(9, 9, 3))])
    hand_fixture('hand_background_index.gif', 9, 9, PAL8, [Frame(R(5, 5, 8), x=2, y=2)], bg=5, check=False)

    # Transparency and frame geometry
    t = R(10, 14, 4)
    hand_fixture('hand_transparent.gif', 14, 10, PAL4, [Frame(t, trans=2)])
    hand_fixture('hand_gce_no_transparency.gif', 14, 10, PAL4, [Frame(t, gce=True, delay=9)])
    hand_fixture('hand_all_transparent.gif', 5, 4, PAL4, [Frame(np.full((4, 5), 1, np.uint8), trans=1)])
    hand_fixture('hand_partial_frame.gif', 14, 10, PAL4, [Frame(R(4, 6, 4), x=5, y=3)], check=False)
    hand_fixture('hand_partial_interlaced.gif', 14, 12, PAL4, [Frame(R(9, 6, 4), x=5, y=2, interlace=True)], check=False)
    hand_fixture('hand_frame_beyond_screen.gif', 10, 8, PAL4, [Frame(R(7, 9, 4), x=6, y=4)], check=False)
    hand_fixture('hand_frame_beyond_interlaced.gif', 10, 8, PAL4, [Frame(R(11, 9, 4), x=6, y=2, interlace=True)], check=False)
    hand_fixture('hand_frame_outside_screen.gif', 10, 8, PAL4, [Frame(R(3, 3, 4), x=20, y=30)], check=False)
    hand_fixture('hand_frame_larger_than_screen.gif', 6, 5, PAL4, [Frame(R(9, 11, 4))], check=False)
    f0 = Frame(R(5, 7, 4))
    hand_fixture('hand_screen_0x0.gif', 0, 0, PAL4, [f0], check=False, expected=composite(7, 5, PAL4, [f0]))

    # Extensions
    comment = b'\x21\xFE' + blocks(b'a comment that is long enough to need more than one tiny block', 20)
    plain = b'\x21\x01' + blocks(bytes([0, 0, 0, 0, 5, 0, 5, 0, 8, 8, 1, 0]) + b'plain text', 12)
    unknown = b'\x21\x77' + blocks(b'who knows what this is', 9)
    app = b'\x21\xFF\x0BXMP DataXMP' + blocks(b'<x:xmpmeta/>')
    hand_fixture('hand_extensions.gif', 14, 10, PAL4, [Frame(t)], head=comment + plain + unknown + app, check=False)
    hand_fixture('hand_extension_between_gce_and_image.gif', 14, 10, PAL4, [Frame(t, trans=1, mid=comment + unknown)])
    hand_fixture('hand_comment_after_image.gif', 14, 10, PAL4, [Frame(t)], tail=comment)
    hand_fixture('hand_loop_animexts.gif', 14, 10, PAL4, [Frame(t, delay=3), Frame(R(10, 14, 4), delay=4)], loop=7,
                 loop_id=b'ANIMEXTS1.0', check=False)
    hand_fixture('hand_loop_65535.gif', 14, 10, PAL4, [Frame(t, delay=3), Frame(R(10, 14, 4), delay=4)], loop=65535)

    # Animations: every disposal method with partial rectangles
    base = R(16, 24, 8)

    def anim(disposals, trans=None, interlace=False):
        fr = [Frame(base, delay=10, disposal=disposals[0])]
        spots = [(2, 3, 9, 6), (8, 5, 12, 7), (0, 9, 24, 4), (15, 0, 9, 16)]
        for k, d in enumerate(disposals[1:]):
            x, y, w, h = spots[k % len(spots)]
            fr.append(Frame(R(h, w, 8), x=x, y=y, delay=10 + k, disposal=d, trans=trans, interlace=interlace))
        return fr
    for d in (0, 1, 2, 3):
        hand_fixture(f'hand_anim_disposal{d}.gif', 24, 16, PAL8, anim([d] * 5), loop=0, check=False)
        hand_fixture(f'hand_anim_disposal{d}_transparent.gif', 24, 16, PAL8, anim([d] * 5, trans=6), loop=0, check=False)
    hand_fixture('hand_anim_disposal_mixed.gif', 24, 16, PAL8, anim([1, 2, 3, 0, 3, 3, 2, 1], trans=1), loop=1, check=False)
    hand_fixture('hand_anim_disposal_mixed_interlaced.gif', 24, 16, PAL8, anim([2, 3, 1, 3, 2], trans=0, interlace=True), check=False)
    hand_fixture('hand_anim_disposal4.gif', 24, 16, PAL8, anim([1, 4, 4, 1]), check=False)
    # 5..7 are reserved; like Chrome they are treated as "unspecified" (the tools each do something else)
    odd = anim([1, 7, 5, 6])
    hand_fixture('hand_anim_disposal7.gif', 24, 16, PAL8, odd, check=False,
                 expected=composite(24, 16, PAL8, [Frame(f.idx, x=f.x, y=f.y) for f in odd]))
    # first frame small with disposal 2 / 3: the canvas around it is transparent
    small = [Frame(R(6, 8, 8), x=3, y=4, disposal=3, delay=5), Frame(R(5, 5, 8), x=10, y=2, disposal=2, delay=5),
             Frame(R(7, 20, 8), x=1, y=8, disposal=1, delay=5, trans=3)]
    hand_fixture('hand_anim_small_first.gif', 24, 16, PAL8, small, check=False)
    # local palettes mixed with the global one, frames reaching beyond the screen
    mixed = [Frame(base, delay=2), Frame(R(6, 10, 4), x=18, y=12, pal=PAL4[::-1], delay=2, disposal=2),
             Frame(R(5, 7, 16), x=4, y=4, pal=GRAY256[:16], delay=2, trans=9, disposal=3), Frame(R(3, 3, 8), x=0, y=0, delay=2)]
    hand_fixture('hand_anim_local_palettes_clipped.gif', 24, 16, PAL8, mixed, loop=0, check=False)
    # zero delays and 87a without any extension: frames still count
    hand_fixture('hand_anim_gif87a.gif', 24, 16, PAL8, [Frame(base), Frame(R(4, 4, 8), x=1, y=1), Frame(R(4, 4, 8), x=9, y=9)],
                 version=b'GIF87a', check=False)
    # many tiny frames
    hand_fixture('hand_anim_40_frames.gif', 12, 10, PAL8,
                 [Frame(R(10, 12, 8), delay=1)] + [Frame(R(2, 3, 8), x=(k * 5) % 10, y=(k * 3) % 9, delay=k, disposal=k % 4) for k in range(39)],
                 loop=0, check=False)


def main():
    if os.path.isdir(OUT):
        shutil.rmtree(OUT)
    os.makedirs(OUT)
    gen_pillow()
    gen_tools()
    gen_hand()
    files = os.listdir(OUT)
    size = sum(os.path.getsize(os.path.join(OUT, f)) for f in files)
    print(f'{sum(f.endswith(".gif") for f in files)} gif fixtures, {len(files)} files, {size / 1024:.0f} KiB')


if __name__ == '__main__':
    main()
