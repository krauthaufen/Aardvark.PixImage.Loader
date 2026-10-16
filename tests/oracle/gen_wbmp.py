"""Generates tests/fixtures/wbmp: name.wbmp and name.wbmp.ref (what Wbmp.Decode returns).

A file written by ImageMagick with its decode as the reference, and hand built type 0 files (WAP-237 WAE
specification, section 6: multi-byte width / height, rows padded to bytes, most significant bit first, 1 white) with
references computed from what went into them, cross checked against ImageMagick (Pillow does not read WBMP;
disagreements are printed, --report prints all comparisons).

usage: python3 gen_wbmp.py [--report]
"""
import os
import numpy as np
from PIL import Image
from pxref import test_pattern
from legacy_common import clear, save, check, tool_ref, magick_write

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'fixtures', 'wbmp')


def mb(v):
    groups = [v & 0x7F]
    v >>= 7
    while v:
        groups.append(0x80 | (v & 0x7F))
        v >>= 7
    return bytes(reversed(groups))


def wbmp(white):
    h, w = white.shape
    rb = (w + 7) // 8
    body = bytearray(rb * h)
    for y in range(h):
        for x in range(w):
            if white[y, x]:
                body[y * rb + x // 8] |= 0x80 >> (x % 8)
    return b'\0\0' + mb(w) + mb(h) + bytes(body)


def hand(name, white):
    ref = np.where(white, 255, 0).astype(np.uint8)[:, :, None]
    check(name, save(OUT, name + '.wbmp', wbmp(white), ref), ref)


def main():
    clear(OUT)
    gray = test_pattern(37, 23, 1)[:, :, 0]
    tmp = os.path.join(OUT, '_src.png')
    Image.fromarray(gray).save(tmp)
    path = os.path.join(OUT, 'im.wbmp')
    magick_write([tmp], [], path)
    os.remove(tmp)
    ref = tool_ref(path, 1, 'magick')
    with open(path, 'rb') as f:
        save(OUT, 'im.wbmp', f.read(), ref)
    check('im', path, ref)

    rng = np.random.default_rng(8)
    hand('odd', rng.integers(0, 2, (7, 13)).astype(bool))
    hand('wide_multibyte', rng.integers(0, 2, (3, 300)).astype(bool))       # width 300: two byte integer
    hand('tall_multibyte', rng.integers(0, 2, (130, 8)).astype(bool))
    hand('one', np.ones((1, 1), bool))
    # truncated: the last two rows are black (complete files only are recognized by content; Wbmp.Decode reads it)
    white = rng.integers(0, 2, (6, 16)).astype(bool)
    data = wbmp(white)
    exp = np.where(white, 255, 0).astype(np.uint8)[:, :, None]
    exp[4:] = 0
    exp[3, 8:] = 0
    save(OUT, 'truncated.wbmp', data[:len(data) - 5], exp)
    print(f'{len([f for f in os.listdir(OUT) if not f.endswith(".ref")])} files in {OUT}')


if __name__ == '__main__':
    main()
