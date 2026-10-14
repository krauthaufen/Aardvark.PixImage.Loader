"""Reference results for PixCodec.ImageOps (resize and remap).

Resize: Pillow's Image.resize on float32 ('F', computed in double, no rounding) and on 8 bit RGB / RGBA images.
Remap: scipy.ndimage.map_coordinates (order 1 = bilinear, exact in double) with every border mode.

Output: tests/fixtures/resize/*.ref (pxref format). Names:
  in_<name>.ref                                  source image
  rs_<name>_<w>x<h>_<filter>.ref                 Pillow resize of in_<name> to w x h
  map_<name>.ref                                 float32 2-channel map (x, y) for remap
  rm_<name>_<mode>.ref                           bilinear remap of in_<name> through map_<name>
"""
import os
import sys
import numpy as np
from PIL import Image
from scipy import ndimage

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from pxref import write_ref, test_pattern

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'fixtures', 'resize')
os.makedirs(OUT, exist_ok=True)

FILTERS = {
    'nearest': Image.Resampling.NEAREST, 'box': Image.Resampling.BOX, 'bilinear': Image.Resampling.BILINEAR,
    'hamming': Image.Resampling.HAMMING, 'bicubic': Image.Resampling.BICUBIC, 'lanczos3': Image.Resampling.LANCZOS,
}
SIZES = [(16, 11), (37, 29), (80, 70), (13, 61), (1, 1), (5, 3), (111, 7)]


def put(name, arr):
    write_ref(os.path.join(OUT, name + '.ref'), arr)


def main():
    # float32 gray: Pillow 'F' resizes in double precision, the strict check of the filter conventions
    g = (test_pattern(37, 29, 1, np.float32, seed=3)[:, :, 0]).astype(np.float32)
    put('in_f32', g)
    for (w, h) in SIZES:
        for fname, f in FILTERS.items():
            r = np.asarray(Image.fromarray(g, 'F').resize((w, h), f), dtype=np.float32)
            put(f'rs_f32_{w}x{h}_{fname}', r)

    # 8 bit RGB and RGBA (Pillow rounds between its passes: compared with a tolerance)
    rgb = test_pattern(37, 29, 3, np.uint8, seed=4)
    put('in_rgb8', rgb)
    rgba = test_pattern(37, 29, 4, np.uint8, seed=5)
    rgba[:, :, 3] = np.where(rgba[:, :, 3] < 60, 0, rgba[:, :, 3])  # some fully transparent pixels
    put('in_rgba8', rgba)
    for (w, h) in [(16, 11), (80, 70), (13, 61)]:
        for fname, f in FILTERS.items():
            put(f'rs_rgb8_{w}x{h}_{fname}', np.asarray(Image.fromarray(rgb, 'RGB').resize((w, h), f)))
            put(f'rs_rgba8_{w}x{h}_{fname}', np.asarray(Image.fromarray(rgba, 'RGBA').resize((w, h), f)))

    # remap, bilinear, every border mode
    src = test_pattern(23, 17, 1, np.float32, seed=6)[:, :, 0].astype(np.float32)
    put('in_rm', src)
    rng = np.random.default_rng(7)
    dw, dh = 31, 19
    mx = rng.uniform(-30, 23 + 30, (dh, dw)).astype(np.float32)
    my = rng.uniform(-25, 17 + 25, (dh, dw)).astype(np.float32)
    put('map_rm', np.stack([mx, my], axis=-1))
    modes = {'constant': ('grid-constant', 0.25), 'clamp': ('nearest', 0), 'wrap': ('grid-wrap', 0),
             'mirror': ('mirror', 0), 'mirrorrepeat': ('reflect', 0)}
    for name, (mode, cval) in modes.items():
        r = ndimage.map_coordinates(src.astype(np.float64), [my.astype(np.float64), mx.astype(np.float64)],
                                    order=1, mode=mode, cval=cval, prefilter=False)
        put(f'rm_rm_{name}', r.astype(np.float32))


if __name__ == '__main__':
    main()
