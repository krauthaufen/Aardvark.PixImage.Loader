"""Copies the BMP Suite (https://entropymine.com/jason/bmpsuite/) into the test-data repository.

  fixtures/bmp/bmpsuite/{g,q,b}/*.bmp   the generated files (public domain, except embedded ICC profiles)
  fixtures/bmp/bmpsuite/ref/*           the author's reference renderings (html/*.png, *.jpg)
  fixtures/bmp/bmpsuite/refs.tsv        file -> acceptable reference renderings, taken from html/bmpsuite.html

usage: python3 import_bmpsuite.py <unpacked bmpsuite-2.8 directory>
"""
import os
import re
import shutil
import sys
from pxref import testdata_dir


def main():
    src = sys.argv[1]
    out = testdata_dir('fixtures', 'bmp', 'bmpsuite')
    if out is None:
        sys.exit(1)
    if os.path.isdir(out):
        shutil.rmtree(out)
    for d in ('g', 'q', 'b'):
        shutil.copytree(os.path.join(src, d), os.path.join(out, d))
    os.makedirs(os.path.join(out, 'ref'))
    html = open(os.path.join(src, 'html', 'bmpsuite.html'), encoding='utf-8').read()
    refs = {}
    # every table row shows reference renderings and then the file itself
    for row in re.split(r'<tr', html):
        imgs = re.findall(r'<img src="([^"]+)"', row)
        files = [i[3:] for i in imgs if i.startswith('../') and i.endswith('.bmp')]
        pics = [i for i in imgs if not i.startswith('../')]
        for f in files:
            refs.setdefault(f, [])
            refs[f] += [p for p in pics if p not in refs[f]]
    used = set()
    with open(os.path.join(out, 'refs.tsv'), 'w') as tsv:
        tsv.write('# file\treference renderings (any of them is acceptable)\n')
        for f in sorted(refs):
            tsv.write(f + '\t' + ' '.join(refs[f]) + '\n')
            used.update(refs[f])
    for p in sorted(used):
        shutil.copy(os.path.join(src, 'html', p), os.path.join(out, 'ref', p))
    shutil.copy(os.path.join(src, 'readme.txt'), os.path.join(out, 'README.bmpsuite.txt'))
    print(f'{len(refs)} files with references, {len(used)} renderings -> {out}')


if __name__ == '__main__':
    main()
