"""Writes fixtures/avif/libavif/refs.txt of the test-data repository: for each AVIF file of libavif 1.2.1 tests/data
(fetched by its fetch/fetch_libavif_testdata.sh; the files are not redistributed), its SHA-256, avifdec's (dav1d)
result for the primary image (or the first frame) - width, height, bit depth, chroma format and the SHA-256 of the
decoded planes as in gen_avif.py - or "fail" when avifdec rejects the file.

usage: python3 gen_avif_libavif.py <test-data repository> [file list]
  The file list (one path relative to tests/data per line) is only needed for the first run, before refs.txt exists.
"""
import hashlib
import os
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from gen_avif import ENV, TMP, read_y4m, tool  # noqa: E402

BASE = 'https://raw.githubusercontent.com/AOMediaCodec/libavif/v1.2.1/tests/data'


def main(testdata, listfile=None):
    d = os.path.join(testdata, 'fixtures', 'avif', 'libavif')
    os.makedirs(d, exist_ok=True)
    refs = os.path.join(d, 'refs.txt')
    if listfile: names = [l.strip() for l in open(listfile) if l.strip()]
    else: names = [l.split()[0] for l in open(refs) if not l.startswith('#')]
    os.makedirs(TMP, exist_ok=True)
    lines = ['# file sha256 width height bitdepth format yuv-sha256 (avifdec 1.2.1 with dav1d 1.5.1; "fail": avifdec rejects it)']
    for n in names:
        p = os.path.join(d, n)
        if not os.path.exists(p):
            os.makedirs(os.path.dirname(p), exist_ok=True)
            subprocess.run(['curl', '-sSfL', '-o', p, f'{BASE}/{n}'], check=True)
        sha = hashlib.sha256(open(p, 'rb').read()).hexdigest()
        y4m = os.path.join(TMP, 'lib.y4m')
        r = subprocess.run([tool('avifdec'), '-c', 'dav1d', '--index', '0', p, y4m], env=ENV, capture_output=True, text=True)
        if r.returncode != 0:
            lines.append(f'{n} {sha} fail')
        else:
            w, h, bd, sub, fr = read_y4m(y4m)
            fmt = 'mono' if sub is None else f'{sub[0]}{sub[1]}'
            lines.append(f'{n} {sha} {w} {h} {bd} {fmt} {hashlib.sha256(fr[0]).hexdigest()}')
        print(lines[-1])
    with open(refs, 'w') as f: f.write('\n'.join(lines) + '\n')
    import shutil
    shutil.rmtree(TMP, ignore_errors=True)


if __name__ == '__main__':
    main(*sys.argv[1:])
