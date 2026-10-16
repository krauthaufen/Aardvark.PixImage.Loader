"""Generates tests/fixtures/jxl-spec: JPEG XL files (libjxl's cjxl) for points of ISO/IEC 18181-1:2024 that the
conformance files and gen_jxl.py's fixtures do not reach, with libjxl's decodes as references (see gen_jxl.py).

  * icc_xyz_shared.jxl: an ICC profile whose tag list has an rXYZ / gXYZ / bXYZ run followed by a tag sharing gXYZ's
    data, so the tag start after the run is predicted (18181-1:2024 E.4.4); the profile is icc_xyz_shared.icc.
  * lf_frame_e*.jxl: VarDCT frames whose LF comes from an LF frame (cjxl --progressive_dc), which also use LF
    thresholds in the block context (G.2.2).

usage: python3 gen_jxl_spec.py (cjxl / djxl from PATH or .tmp/libjxl/tools)
"""
import json
import os
import struct
import numpy as np
from PIL import Image, ImageCms
from gen_jxl import cjxl, djxl_float, photo, save_png, TMP
from pxref import write_ref

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, '..', 'fixtures', 'jxl-spec')


def finish(path, info):
    ref = djxl_float(path)
    write_ref(path + '.ref', ref[0])
    json.dump(info, open(path + '.json', 'w'), indent=1, sort_keys=True)


def icc_xyz_shared():
    """lcms's sRGB profile with the red primary nudged (so libjxl keeps the ICC), its tags reordered so rXYZ, gXYZ,
    bXYZ are consecutive and followed by wtpt pointing at gXYZ's data."""
    b = bytearray(ImageCms.ImageCmsProfile(ImageCms.createProfile('sRGB')).tobytes())
    n = struct.unpack('>I', b[128:132])[0]
    tags = {}
    for i in range(n):
        t, o, s = struct.unpack('>4sII', b[132 + 12 * i:144 + 12 * i])
        tags[t] = bytes(b[o:o + s])
    r = bytearray(tags[b'rXYZ']); r[11] ^= 0x40; tags[b'rXYZ'] = bytes(r)
    order = [b'rXYZ', b'gXYZ', b'bXYZ', b'wtpt', b'rTRC', b'gTRC', b'bTRC', b'chad', b'desc', b'cprt', b'chrm']
    stored = [t for t in order if t not in (b'wtpt', b'gTRC', b'bTRC')]
    table, body, at = bytearray(struct.pack('>I', len(order))), bytearray(), {}
    start = 128 + 4 + 12 * len(order)
    for t in stored:
        at[t] = start + len(body)
        body += tags[t]
        while len(body) % 4: body += b'\0'
    for t in order:
        o = at[b'gXYZ'] if t == b'wtpt' else at[b'rTRC'] if t in (b'gTRC', b'bTRC') else at[t]
        table += struct.pack('>4sII', t, o, len(tags[t]))
    p = bytearray(b[:128]) + table + body
    p[0:4] = struct.pack('>I', len(p))
    p[84:100] = bytes(16)
    return bytes(p)


def main():
    os.makedirs(OUT, exist_ok=True)
    os.makedirs(TMP, exist_ok=True)
    t = lambda n: os.path.join(TMP, n)
    o = lambda n: os.path.join(OUT, n)

    icc = icc_xyz_shared()
    open(o('icc_xyz_shared.icc'), 'wb').write(icc)
    Image.fromarray(photo(32, 24)).save(t('icc.png'), icc_profile=icc)
    cjxl(t('icc.png'), o('icc_xyz_shared.jxl'), '-d 0')
    finish(o('icc_xyz_shared.jxl'), {'lossless': True, 'icc': 'icc_xyz_shared.icc'})

    save_png(t('lf.png'), photo(160, 96))
    for e in (7, 9):
        cjxl(t('lf.png'), o(f'lf_frame_e{e}.jxl'), f'-d 2 -e {e} --progressive_dc=1')
        finish(o(f'lf_frame_e{e}.jxl'), {'lossless': False})
    print(f'JPEG XL spec fixtures in {OUT}')


if __name__ == '__main__':
    main()
