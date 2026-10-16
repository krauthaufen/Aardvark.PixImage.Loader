"""Fetches the JPEG-LS conformance test images for the optional JpegLsTests.ConformanceImages test.

usage: python3 fetch_jpegls_conformance.py [target-dir]      (from tests/oracle; needs network access once)

The files are the data set of ITU-T T.87 | ISO/IEC 14495-1 Annex E (test8.ppm and its components, test16.pgm, and their
JPEG-LS codings t8c?e?, t8nde?, t8sse?, t16e?: all interleave modes, NEAR 3, non-default T1..T3 / RESET, components
subsampled 4x vertically and 2x2 in a line interleaved scan). They come without a licence of their own, so they are not
committed anywhere: this script downloads them from the CharLS repository (tag 2.4.2, test/conformance; data files
only), checks their SHA-256 and puts them into downloads/jpegls-conformance of the test-data repository (git-ignored
there; see pxref.testdata_dir), or into target-dir.
"""
import hashlib
import os
import sys
import urllib.request

from pxref import testdata_dir

BASE = 'https://raw.githubusercontent.com/team-charls/charls/2.4.2/test/conformance/'
FILES = {
    't16e0.jls': '0169aab6eb839925cc781016e3c3ed19d323fadee99d9747375e787b88e4d23f',
    't16e3.jls': 'e3b7327d232247949bd6aa4520d3a2627bb60c952ff23d700c92900a70863813',
    't8c0e0.jls': '8c564fbd3a8667bd071cc8d994952fdfae3d62db5c359be4b6d6734e89acea6d',
    't8c0e3.jls': '6356737dbf5168000cebc5e4056e04eb687664cd15797de324fa0845eb407dc3',
    't8c1e0.jls': 'fdd6fa22f94135f7c3db7932da2154aefc79085fec3b3f65da8a62d6964b8078',
    't8c1e3.jls': 'be41c9c2687542d452171ae629c76905b7af7073d9db56f9a549b6323df6ed1e',
    't8c2e0.jls': '2cbf1d38b9d186a06ea7b19cc74df6259d238c789f49ed7329a8e34afd6ba5ae',
    't8c2e3.jls': 'df1fa8e1ac3256a2ea226996d27c8bd504a7ca08385674aedf77b6edd42be8de',
    't8nde0.jls': 'c3e1244dfc035626cbdea7a89a8120fde3ae4deb22847695928cfbd5f36884ae',
    't8nde3.jls': '0597c16d6d60d89f0aa9e71a8fd6bbf982ef1ae22d4b8afc897dafa68efd90e8',
    't8sse0.jls': 'bee806d92fb11dcdaf4ad1dbcc695622f95fdd4d8a7d6d77f72fc1a0d98f6d26',
    't8sse3.jls': '215e3e176124749917ba9676c459709725721cc26e0851b4f97c1aa5f677fb0f',
    'test16.pgm': '1eb2001a0fe66c9d44776b40a35aaa3b68a4fe74cb749e6271d96523378149d2',
    'test8.ppm': 'a7ecaa841b8a7dc131a73007f0d6c07732e901029810e45ca3cc788fdf9e9593',
    'test8b.pgm': '3c4a4d24d57d26afc86ab6bc4486dc813e83a8f595b0c0ece737a1ad7402a060',
    'test8bs2.pgm': '6cf4289f0afd89d0622ff0bfc04a830770b104b969ba69e8e952b1834faf69a4',
    'test8g.pgm': 'ae607ad1b61efa340831a9b0f0c228f752908ec59b20deaa54b4c0419d0e00fd',
    'test8gr4.pgm': '9cb5b43912836fce8b1f40eb2ad67fed9d1c303421641e67d6759909d10e37d3',
    'test8r.pgm': '9474fbec2fe54221b0943f4f43014f70469a2478654d1f4ac1de05bed3ceb182',
}


def main():
    target = sys.argv[1] if len(sys.argv) > 1 else testdata_dir('downloads', 'jpegls-conformance')
    if target is None:
        raise SystemExit('no test-data repository: pass a target directory')
    os.makedirs(target, exist_ok=True)
    for name, sha in FILES.items():
        with urllib.request.urlopen(BASE + name) as r:
            data = r.read()
        h = hashlib.sha256(data).hexdigest()
        if h != sha:
            raise SystemExit(f'{name}: SHA-256 {h} differs from {sha}')
        with open(os.path.join(target, name), 'wb') as f:
            f.write(data)
        print(f'{h}  {name}')


if __name__ == '__main__':
    main()
