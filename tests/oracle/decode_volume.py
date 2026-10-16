"""Decode a volume / scientific image with a reference library, for the encoder tests.

usage: decode_volume.py <nrrd|nibabel|sitk|astropy> <file> <out>

Writes <out>.vref (samples in the Aardvark.PixImage.PixCodec layout: x fastest, channels interleaved; FITS rows reversed so
that the top row comes first) and <out>.json (sizes, channels, type, space, spacing, origin, direction as rows of the
matrix whose columns are the axis directions).
"""
import json
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from pxref import write_vref


def our(a_f, chan_axis=None):
    a = np.asarray(a_f)
    if chan_axis is not None:
        a = np.moveaxis(a, chan_axis, 0)
    return np.ascontiguousarray(a.transpose(tuple(reversed(range(a.ndim)))))


def dump(out, data, channels, meta):
    write_vref(out + '.vref', data, channels)
    with open(out + '.json', 'w') as f:
        json.dump(meta, f)


def geom(cols, origin):
    cols = np.asarray(cols, dtype=np.float64)
    sp = np.linalg.norm(cols, axis=0)
    return dict(spacing=sp.tolist(), origin=[float(x) for x in origin], direction=(cols / np.where(sp == 0, 1, sp)).tolist())


def main():
    tool, path, out = sys.argv[1:4]
    if tool == 'nrrd':
        import nrrd
        data, h = nrrd.read(path)
        kinds = h.get('kinds', [])
        chan = 0 if kinds and kinds[0] not in ('domain', 'space', 'time') and data.ndim > 1 else None
        c = data.shape[0] if chan is not None else 1
        meta = {}
        if 'space directions' in h:
            sd = np.array(h['space directions'], dtype=np.float64)
            rows = [r for r in sd if not np.isnan(r).any()]
            meta.update(geom(np.array(rows).T, h.get('space origin', np.zeros(len(rows[0])))))
            meta['space'] = h.get('space')
        dump(out, our(data, chan), c, meta)
    elif tool == 'nibabel':
        import nibabel as nib
        im = nib.load(path)
        a = np.asanyarray(im.dataobj)
        c, chan = 1, None
        if a.dtype.names:
            a = np.stack([a[n] for n in a.dtype.names], axis=-1)
            c, chan = a.shape[-1], a.ndim - 1
        elif np.iscomplexobj(a):
            a = np.stack([a.real, a.imag], axis=-1)
            c, chan = 2, a.ndim - 1
        elif a.ndim == 5:
            a = a[:, :, :, 0, :]
            c, chan = a.shape[-1], 3
        while a.ndim > (3 if chan is None else 4) and a.shape[-1 if chan is None else -2] == 1:
            a = a[..., 0] if chan is None else a[..., 0, :]
        aff = im.affine
        meta = geom(aff[:3, :3], aff[:3, 3])
        meta['space'] = 'RAS'
        meta['intent'] = int(im.header['intent_code'])
        dump(out, our(a, chan), c, meta)
    elif tool == 'sitk':
        import SimpleITK as sitk
        im = sitk.ReadImage(path)
        n = im.GetDimension()
        a = sitk.GetArrayFromImage(im)
        c = im.GetNumberOfComponentsPerPixel()
        meta = dict(spacing=list(im.GetSpacing()), origin=list(im.GetOrigin()),
                    direction=np.array(im.GetDirection()).reshape(n, n).tolist(), space='LPS')
        dump(out, np.ascontiguousarray(a), c, meta)
    elif tool == 'astropy':
        from astropy.io import fits
        with fits.open(path) as f:
            hdu = next(h for h in f if h.data is not None and h.is_image)
            a = np.array(hdu.data)
            hdr = hdu.header
        if a.ndim >= 2:
            a = np.ascontiguousarray(a[..., ::-1, :])
        meta = {k: hdr[k] for k in ('CRPIX1', 'CRPIX2', 'CRVAL1', 'CRVAL2', 'BUNIT') if k in hdr}
        dump(out, a, 1, meta)
    else:
        sys.exit('unknown tool ' + tool)


if __name__ == '__main__':
    main()
