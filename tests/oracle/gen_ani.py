"""Generates tests/fixtures/ani: animated cursors (RIFF ACON) built by hand from the format description (anih header,
optional "rate" and "seq " chunks, LIST "fram" of icon chunks, LIST "INFO"), with frames that ImageMagick (bitmaps, turned
into cursors by setting the directory's type and hotspot) and Pillow (ICO) wrote from our own synthetic images.

  name.ani            the file
  name.ani.<s>.ref    the image of animation step s (RGBA8, the largest image of the frame's icon as Pillow decodes it)
  name.ani.txt        one line per step: frame index, jiffies, hotspot x, hotspot y

  seq.ani     4 CUR frames (32 x 32, hotspots set in the directory), a "seq " chunk that repeats and reorders them, "rate"
              durations, INFO title / artist
  plain.ani   3 ICO frames of two sizes each (16 and 32; the 32 is the image), no "seq " and no "rate" (header rate),
              odd sized INFO strings (chunk padding)

usage: python3 gen_ani.py
"""
import io
import os
import struct
import subprocess
import tempfile
import numpy as np
from PIL import Image
from pxref import write_ref

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'fixtures', 'ani')
TMP = tempfile.mkdtemp()


def chunk(cid, data):
    return cid + struct.pack('<I', len(data)) + data + (b'\0' if len(data) & 1 else b'')


def riff(chunks):
    body = b'ACON' + b''.join(chunks)
    return b'RIFF' + struct.pack('<I', len(body)) + body


def lst(form, chunks):
    return chunk(b'LIST', form + b''.join(chunks))


def anih(frames, steps, jiffies, flags):
    return chunk(b'anih', struct.pack('<9I', 36, frames, steps, 0, 0, 0, 0, jiffies, flags))


def frame_image(s, k):
    y, x = np.mgrid[0:s, 0:s]
    a = np.zeros((s, s, 4), np.uint8)
    a[..., 0] = (x * 255 // s + 60 * k) % 256
    a[..., 1] = (y * 255 // s) % 256
    a[..., 2] = 40 * k
    a[..., 3] = np.where(((x - s / 2) ** 2 + (y - s / 2) ** 2) < (s / 2.2) ** 2, 255, 0)
    return a


def pillow_largest(blob):
    im = Image.open(io.BytesIO(blob))
    if 'sizes' in im.info:
        im.size = max(im.info['sizes'])
    im.load()
    return np.asarray(im.convert('RGBA'))


def cur(img, hot):
    p = os.path.join(TMP, 'f.png')
    Image.fromarray(img).save(p)
    o = os.path.join(TMP, 'f.cur')
    subprocess.run(['magick', p, 'CUR:' + o], check=True)
    d = bytearray(open(o, 'rb').read())
    # ImageMagick writes the icon type and planes / bit count: made a cursor with the hotspot in the directory entry
    d[2] = 2
    struct.pack_into('<HH', d, 10, *hot)
    return bytes(d)


def write(name, data, steps):
    os.makedirs(OUT, exist_ok=True)
    p = os.path.join(OUT, name)
    with open(p, 'wb') as f:
        f.write(data)
    with open(p + '.txt', 'w') as f:
        for i, (ref, frame, jiffies, hx, hy) in enumerate(steps):
            write_ref(p + f'.{i}.ref', ref)
            f.write(f'{frame} {jiffies} {hx} {hy}\n')


def main():
    # seq.ani
    hots = [(3, 4), (16, 16), (31, 0), (0, 31)]
    frames = [cur(frame_image(32, k), hots[k]) for k in range(4)]
    refs = [pillow_largest(f) for f in frames]
    for k in range(4):
        assert np.array_equal(refs[k], frame_image(32, k)), 'CUR frames are lossless'
    seq = [0, 1, 2, 1, 3, 3, 0]
    rate = [5, 10, 3, 10, 20, 1, 7]
    data = riff([lst(b'INFO', [chunk(b'INAM', b'Spinner\0'), chunk(b'IART', b'gen_ani.py\0')]),
                 anih(4, len(seq), 6, 3), chunk(b'rate', struct.pack(f'<{len(rate)}I', *rate)),
                 chunk(b'seq ', struct.pack(f'<{len(seq)}I', *seq)), lst(b'fram', [chunk(b'icon', f) for f in frames])])
    write('seq.ani', data, [(refs[f], f, rate[i], *hots[f]) for i, f in enumerate(seq)])
    # plain.ani
    icos, irefs = [], []
    for k in range(3):
        bio = io.BytesIO()
        Image.fromarray(frame_image(32, k + 4)).save(bio, format='ICO', sizes=[(16, 16), (32, 32)])
        icos.append(bio.getvalue())
        irefs.append(pillow_largest(icos[-1]))
    data = riff([anih(3, 3, 12, 1), lst(b'fram', [chunk(b'icon', f) for f in icos]),
                 lst(b'INFO', [chunk(b'INAM', b'Odd\0'), chunk(b'IART', b'abc')])])
    write('plain.ani', data, [(irefs[k], k, 12, 0, 0) for k in range(3)])
    print('wrote', OUT)


if __name__ == '__main__':
    main()
