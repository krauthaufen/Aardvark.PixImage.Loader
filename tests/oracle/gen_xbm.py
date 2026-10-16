"""Generates tests/fixtures/xbm: name.xbm and name.xbm.ref (what Xbm.Decode returns).

Files written by Pillow and ImageMagick with a tool's decode as the reference, and hand built files in the X10 and X11
C source forms (16 bit shorts / chars, comments, hotspot and other defines, decimal and octal values, upper case hex,
CRLF lines, no trailing comma) with references computed from what went into them, cross checked against Pillow and
ImageMagick (disagreements are printed, --report prints all comparisons). Bits are least significant first, 1 black.

usage: python3 gen_xbm.py [--report]
"""
import os
import numpy as np
from PIL import Image
from pxref import test_pattern
from legacy_common import clear, save, check, tool_ref, magick_write

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'fixtures', 'xbm')


def values(bits, unit):
    """Row-major 0/1 image (1 black) to the list of per row padded words, least significant bit leftmost."""
    h, w = bits.shape
    out = []
    for row in bits:
        for x0 in range(0, w, unit):
            v = 0
            for i, b in enumerate(row[x0:x0 + unit]):
                v |= int(b) << i
            out.append(v)
    return out


def text(name, bits, unit=8, decl='static unsigned char', fmt='0x{:02x}', head='', extra='', per_line=12, sep=', ',
         trailing=False, nl='\n'):
    h, w = bits.shape
    vals = [fmt.format(v) for v in values(bits, unit)]
    body = ''
    for i in range(0, len(vals), per_line):
        body += '   ' + sep.join(vals[i:i + per_line]) + (',' if i + per_line < len(vals) or trailing else '') + nl
    return (f'{head}#define {name}_width {w}{nl}#define {name}_height {h}{nl}{extra}'
            f'{decl} {name}_bits[] = {{{nl}{body}}};{nl}').replace('\n', nl)


def hand(name, txt, bits):
    ref = np.where(bits == 1, 0, 255).astype(np.uint8)[:, :, None]
    check(name, save(OUT, name + '.xbm', txt, ref), ref)


def tool_files():
    gray = test_pattern(37, 23, 1)[:, :, 0]
    path = os.path.join(OUT, 'pil.xbm')
    Image.fromarray(gray > 128).save(path)
    ref = tool_ref(path, 1, 'magick')
    with open(path, 'rb') as f:
        save(OUT, 'pil.xbm', f.read(), ref)
    check('pil', path, ref)
    tmp = os.path.join(OUT, '_src.png')
    Image.fromarray(gray).save(tmp)
    path = os.path.join(OUT, 'im.xbm')
    magick_write([tmp], [], path)
    os.remove(tmp)
    ref = tool_ref(path, 1, 'magick')
    with open(path, 'rb') as f:
        save(OUT, 'im.xbm', f.read(), ref)
    check('im', path, ref)


def hand_files():
    rng = np.random.default_rng(2)
    b = rng.integers(0, 2, (9, 21))
    hand('x11', text('x11', b), b)
    hand('x11_signed_char', text('sc', b, decl='static char'), b)
    b16 = rng.integers(0, 2, (5, 16))
    hand('x11_width16', text('w16', b16), b16)
    hand('x11_hotspot_comments', text('hs', b, head='/* a bitmap */\n// with a comment\n',
                                       extra='#define hs_x_hot 3\n#define hs_y_hot 4\n#define other_thing 7\n'), b)
    hand('x11_decimal_upper_crlf', text('d', b, fmt='{}', sep=',', per_line=7, nl='\r\n'), b)
    hand('x11_upper_hex_trailing_comma', text('u', b, fmt='0X{:02X}', trailing=True), b)
    hand('x11_octal', text('o', b, fmt='0{:o}', per_line=30), b)
    hand('x10', text('x10', b, unit=16, decl='static short', fmt='0x{:04x}', per_line=8), b)
    b33 = rng.integers(0, 2, (4, 33))
    hand('x10_width33', text('x', b33, unit=16, decl='static unsigned short', fmt='0x{:04x}', per_line=8), b33)
    hand('one_pixel', text('p', np.ones((1, 1), int)), np.ones((1, 1), int))


def main():
    clear(OUT)
    tool_files()
    hand_files()
    print(f'{len([f for f in os.listdir(OUT) if not f.endswith(".ref")])} files in {OUT}')


if __name__ == '__main__':
    main()
