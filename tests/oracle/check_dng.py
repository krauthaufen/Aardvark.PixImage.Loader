"""Reads a DNG with tifffile and LibRaw (rawpy) and prints what they make of it, for DngTests' encoder checks.

usage: python3 check_dng.py file.dng

Prints one JSON object: stored_sha256 (tifffile's samples of the raw IFD, little endian bytes), libraw_sha256 (LibRaw's
raw_image, which is the stored values through the linearization table, uint16; absent where LibRaw stacks the planes
of LinearRaw data). Exit status 77 when tifffile or rawpy is missing.
"""
import hashlib
import json
import sys

import numpy as np

from pxref import missing

try:
    import tifffile
except ImportError:
    missing('python:tifffile')
try:
    import rawpy
except ImportError:
    missing('python:rawpy')

path = sys.argv[1]
out = {}
with tifffile.TiffFile(path) as tf:
    page = None
    for p in [tf.pages[0]] + list(tf.pages[0].pages or []):
        if (p.tags.get(254) is None or p.tags[254].value == 0) and p.photometric in (32803, 34892):
            page = p
            break
    a = page.asarray()
    out['stored_sha256'] = hashlib.sha256(np.ascontiguousarray(a).astype(a.dtype.newbyteorder('<')).tobytes()).hexdigest()
with rawpy.imread(path) as r:
    if r.raw_type == rawpy.RawType.Flat:
        out['libraw_sha256'] = hashlib.sha256(np.ascontiguousarray(r.raw_image).astype('<u2').tobytes()).hexdigest()
print(json.dumps(out))
