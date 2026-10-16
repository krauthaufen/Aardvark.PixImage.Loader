"""Generates tests/fixtures/xpm: name.xpm and name.xpm.ref (what Xpm.Decode returns).

Files written by ImageMagick (Pillow does not write XPM) with its decode as the reference, and hand built XPM 3 and
XPM 2 files with references computed from what went into them: 1 to 3 characters per pixel (spaces and punctuation
among them), #RGB / #RRGGBB / #RRRGGGBBB / #RRRRGGGGBBBB colours, X11 colour names with spaces and odd case, None,
visuals other than c (m, g, g4, s; c missing), hotspot and XPMEXT extensions, comments between the strings. All are
cross checked against Pillow and ImageMagick (disagreements are printed, --report prints all comparisons).

Decoding rules the references follow: c, else g, g4, m; hex components scaled to 8 bits rounding to nearest (as
ImageMagick does; X11's own parser takes the high bits instead, which differs for #RGB); colour names from X.Org's
rgb.txt (ImageMagick has its own table, which differs for a few names); images with None are RGBA (transparent
pixels 0, 0, 0, 0); images whose colours are all gray are Gray / GrayAlpha.

usage: python3 gen_xpm.py [--report]
"""
import os
import numpy as np
from PIL import Image
from pxref import test_pattern
from legacy_common import clear, save, check, tool_ref, magick_write

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'fixtures', 'xpm')
RGB_TXT = '/usr/share/X11/rgb.txt'


def x11_names():
    d = {}
    with open(RGB_TXT) as f:
        for line in f:
            if line.startswith('!') or not line.strip():
                continue
            r, g, b, *n = line.split()
            d[''.join(n).lower()] = (int(r), int(g), int(b))
    return d


NAMES = x11_names()


def hand(name, txt, ref):
    check(name, save(OUT, name + '.xpm', txt, ref), ref)


def xpm3(name, w, h, cpp, colors, rows, values_extra='', between='', after=''):
    s = f'/* XPM */\nstatic char *{name}[] = {{\n/* values */\n"{w} {h} {len(colors)} {cpp}{values_extra}",\n'
    s += ''.join(f'"{k} {v}",{between}\n' for k, v in colors)
    s += ',\n'.join(f'"{r}"' for r in rows) + (after or '') + '\n};\n'
    return s


def build(keys, idx):
    return [''.join(keys[i] for i in row) for row in idx]


def expect(cols, idx, gray=False, alpha=False):
    """cols: list of (r, g, b) or None; result in the colour model the decoder picks."""
    lut = np.array([(0, 0, 0, 0) if c is None else (*c, 255) for c in cols], np.uint8)
    a = lut[idx]
    if gray:
        return a[:, :, [0, 3]] if alpha else a[:, :, :1]
    return a if alpha else a[:, :, :3]


def tool_files():
    rgb = test_pattern(37, 23, 3)
    gray = test_pattern(37, 23, 1)[:, :, 0]
    rgba = test_pattern(37, 23, 4, seed=2)
    rgba[:, :, 3] = np.where(np.arange(37) % 3 == 0, 0, 255)[None, :]

    def from_magick(name, mode, src, args, channels):
        tmp = os.path.join(OUT, '_src.png')
        Image.fromarray(src, mode).save(tmp)
        path = os.path.join(OUT, name + '.xpm')
        magick_write([tmp], args, path)
        os.remove(tmp)
        ref = tool_ref(path, channels, 'magick')
        if channels == 4:
            ref[ref[:, :, 3] == 0] = 0
        with open(path, 'rb') as f:
            save(OUT, name + '.xpm', f.read(), ref)
        check(name, path, ref)

    from_magick('im_rgb', 'RGB', rgb, [], 3)               # many colours: two characters per pixel
    from_magick('im_pal', 'RGB', rgb, ['-colors', '40'], 3)
    from_magick('im_gray', 'L', gray, [], 1)
    from_magick('im_rgba', 'RGBA', rgba, [], 4)


def hand_files():
    rng = np.random.default_rng(4)
    w, h = 13, 6
    # cpp 1 with a space and punctuation as keys, every hex form, names, None
    cols = [('#F00', (255, 0, 0)), ('#00ff80', (0, 255, 128)), ('#123456789', (round(0x123 * 255 / 4095), round(0x456 * 255 / 4095), round(0x789 * 255 / 4095))),
            ('#1234ABCDffff', (round(0x1234 * 255 / 65535), round(0xABCD * 255 / 65535), 255)),
            ('light blue', NAMES['lightblue']), ('DarkSlateGray', NAMES['darkslategray']), ('None', None)]
    keys = [' ', '.', '+', '@', '#', '$', '%']
    idx = rng.integers(0, len(cols), (h, w))
    colors = [(k, 'c ' + c) for k, (c, _) in zip(keys, cols)]
    hand('cpp1_forms', xpm3('a', w, h, 1, colors, build(keys, idx)), expect([c for _, c in cols], idx, alpha=True))

    # cpp 2, visuals in other orders, s names, c missing (g used), comments between strings
    cols2 = [('m white s bg c #102030', (16, 32, 48)), ('s fg g #808080 m black', (128, 128, 128)),
             ('g4 #ffffff m black', (255, 255, 255)), ('m red', NAMES['red']), ('c navy blue m black g gray50', NAMES['navyblue'])]
    keys2 = ['ab', 'a ', ' a', '..', '#!']
    idx = rng.integers(0, len(cols2), (h, w))
    colors = [(k, c) for k, (c, _) in zip(keys2, cols2)]
    hand('cpp2_visuals', xpm3('b', w, h, 2, colors, build(keys2, idx), between=' /* c */'), expect([c for _, c in cols2], idx))

    # cpp 3, gray only -> Gray, with a hotspot and an extension after the pixels
    grays = [(f'gray{v}', NAMES[f'gray{v}']) for v in (0, 13, 50, 87, 100)] + [('#777', (0x77, 0x77, 0x77))]
    keys3 = ['aaa', 'aab', 'xyz', '   ', '.,;', 'q-q']
    idx = rng.integers(0, len(grays), (h, w))
    colors = [(k, 'c ' + c) for k, (c, _) in zip(keys3, grays)]
    hand('cpp3_gray_ext', xpm3('c', w, h, 3, colors, build(keys3, idx), values_extra=' 2 3 XPMEXT',
                               after=',\n"XPMEXT ext1 data",\n"XPMENDEXT"'), expect([c for _, c in grays], idx, gray=True))
    # gray with None -> GrayAlpha
    cols4 = [('#000000', (0, 0, 0)), ('#ffffff', (255, 255, 255)), ('none', None)]
    keys4 = ['a', 'b', 'c']
    idx = rng.integers(0, 3, (h, w))
    hand('gray_alpha', xpm3('d', w, h, 1, [(k, 'c ' + c) for k, (c, _) in zip(keys4, cols4)], build(keys4, idx)),
         expect([c for _, c in cols4], idx, gray=True, alpha=True))

    # XPM 2
    cols5 = [('#ff0000', (255, 0, 0)), ('#0000ff', (0, 0, 255)), ('green', NAMES['green'])]
    idx = rng.integers(0, 3, (h, w))
    txt = f'! XPM2\n{w} {h} 3 1\n' + ''.join(f'{k} c {c}\n' for k, (c, _) in zip('xyz', cols5)) + '\n'.join(build('xyz', idx)) + '\n'
    hand('xpm2', txt, expect([c for _, c in cols5], idx))


def main():
    clear(OUT)
    tool_files()
    hand_files()
    print(f'{len([f for f in os.listdir(OUT) if not f.endswith(".ref")])} files in {OUT}')


if __name__ == '__main__':
    main()
