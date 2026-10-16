"""Volume / scientific format fixtures: NRRD, NIfTI, MetaImage, FITS.

usage: <venv>/bin/python gen_volume.py    (needs numpy, pynrrd, nibabel, SimpleITK, astropy: pip install them into
                                           .tmp/venv, see README)

Every fixture F gets F.vref (the expected samples in the decoder's default layout, see pxref.write_vref) and F.json (sizes,
channels, sample type, the expected physical metadata). Files are written by the reference libraries where they can write
the variant (pynrrd, nibabel, SimpleITK, astropy), or by hand for variants no library writes (hex / ascii NRRD, LIST and
pattern data files, byte skips, big-endian MetaImage, ...); hand-written files are read back with a reference reader here
when one can read them, as a check of the fixture itself.

Axis conventions of the expected arrays: x fastest; NRRD / NIfTI / MetaImage without a flip; FITS with the rows reversed
(the decoder puts FITS y = 1 at the bottom, top row first).
"""
import bz2
import gzip
import json
import os
import shutil
import struct
import sys
import zlib

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from pxref import write_vref

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'fixtures', 'volume')
TYPES = {np.dtype('uint8'): 'UInt8', np.dtype('uint16'): 'UInt16', np.dtype('uint32'): 'UInt32', np.dtype('int8'): 'Int8',
         np.dtype('int16'): 'Int16', np.dtype('int32'): 'Int32', np.dtype('float32'): 'Float32', np.dtype('float64'): 'Float64',
         np.dtype('uint64'): 'UInt64', np.dtype('int64'): 'Int64'}


def pattern(shape, dtype, seed=1):
    """Deterministic content with the full range of the type."""
    rng = np.random.default_rng(seed)
    dt = np.dtype(dtype)
    n = int(np.prod(shape))
    if dt.kind in 'ui':
        info = np.iinfo(dt)
        v = rng.integers(info.min, info.max, size=n, dtype=dt, endpoint=True)
        v[0], v[-1] = info.min, info.max
    else:
        v = (rng.standard_normal(n) * 1000).astype(dt)
        v[0] = 0
    return v.reshape(shape)


def our(a_f, chan_axis=None):
    """A Fortran-indexed array (x, y, z, ...) -> our C-order layout (..., z, y, x[, c])."""
    a = np.asarray(a_f)
    if chan_axis is not None:
        a = np.moveaxis(a, chan_axis, 0)
    return np.ascontiguousarray(a.transpose(tuple(reversed(range(a.ndim)))))


def save(path, data_c, channels=1, **meta):
    """data_c: expected samples in our layout. meta: sizes etc. are derived, the rest is stored as is."""
    write_vref(path + '.vref', data_c, channels)
    shape = data_c.shape[:-1] if channels > 1 else data_c.shape
    m = {'sizes': list(reversed(shape)), 'channels': channels, 'type': TYPES[data_c.dtype.newbyteorder('=')]}
    for k, v in meta.items():
        if isinstance(v, np.ndarray):
            v = v.tolist()
        m[k] = v
    with open(path + '.json', 'w') as f:
        json.dump(m, f, indent=1)


def clean(d):
    if os.path.isdir(d):
        shutil.rmtree(d)
    os.makedirs(d)


# ============================================================================ NRRD

def gen_nrrd():
    import nrrd
    d = os.path.join(OUT, 'nrrd')
    clean(d)
    P = lambda n: os.path.join(d, n)
    dirs = np.array([[0.8, 0.1, 0.0], [-0.1, 0.9, 0.0], [0.0, 0.0, 2.5]])   # rows = axis vectors

    def geom(dirs, origin):
        sp = np.linalg.norm(dirs, axis=1)
        return dict(spacing=sp.tolist(), origin=list(origin), direction=(dirs / sp[:, None]).T.tolist())

    def write(name, data, header, **kw):
        nrrd.write(P(name), data, header, **kw)

    a = pattern((7, 5, 4), np.uint8)
    write('u8_raw.nrrd', a, {'space': 'left-posterior-superior', 'space directions': dirs, 'space origin': [1.0, -2.0, 3.5],
                             'encoding': 'raw', 'kinds': ['domain'] * 3}, custom_field_map=None)
    save(P('u8_raw.nrrd'), our(a), space='LPS', **geom(dirs, [1.0, -2.0, 3.5]))

    a = pattern((6, 5, 3), np.int16, 2)
    write('i16_gzip.nrrd', a, {'space': 'RAS', 'space directions': np.diag([0.5, 0.5, 1.2]), 'space origin': [0.0, 0, 0], 'encoding': 'gzip'})
    save(P('i16_gzip.nrrd'), our(a), space='RAS', **geom(np.diag([0.5, 0.5, 1.2]), [0, 0, 0]))

    a = pattern((5, 4, 3), np.float32, 3)
    write('f32_bzip2.nrrd', a, {'encoding': 'bzip2', 'spacings': [1.5, 2.0, 3.0]})
    save(P('f32_bzip2.nrrd'), our(a), spacing=[1.5, 2.0, 3.0])

    a = pattern((4, 3, 2), np.int32, 4)
    write('i32_ascii.nrrd', a, {'encoding': 'ascii'})
    save(P('i32_ascii.nrrd'), our(a))

    for t, s in ((np.uint64, 5), (np.int64, 6), (np.int8, 7), (np.uint32, 8), (np.uint16, 9), (np.float64, 10)):
        a = pattern((5, 3, 2), t, s)
        n = 'raw_%s.nrrd' % np.dtype(t).name
        write(n, a, {'encoding': 'raw'})
        save(P(n), our(a))

    # 2D RGB: channel axis first
    a = pattern((3, 6, 4), np.uint8, 11)
    write('rgb_2d.nrrd', a, {'kinds': ['RGB-color', 'domain', 'domain'], 'encoding': 'gzip'})
    save(P('rgb_2d.nrrd'), our(a, 0), channels=3, color='Rgb')

    # 4D time series: directions for x, y, z, none for time
    a = pattern((4, 3, 2, 3), np.int16, 12)
    d4 = np.array([[1.0, 0, 0], [0, 1.0, 0], [0, 0, 2.0], [np.nan, np.nan, np.nan]])
    write('time_4d.nrrd', a, {'space': 'left-posterior-superior', 'space directions': d4, 'space origin': [0.0, 0, 0],
                              'kinds': ['domain', 'domain', 'domain', 'time'], 'encoding': 'raw'})
    save(P('time_4d.nrrd'), our(a), space='LPS', spacing=[1, 1, 2, None], origin=[0, 0, 0], direction=np.eye(3).tolist())

    # detached header + gzip data (pynrrd)
    a = pattern((6, 4, 3), np.uint16, 13)
    write('detached.nhdr', a, {'encoding': 'gzip', 'space': 'RAS', 'space directions': np.diag([1.0, 1, 1]), 'space origin': [0.0, 0, 0]},
          detached_header=True)
    save(P('detached.nhdr'), our(a), space='RAS', **geom(np.diag([1.0, 1, 1]), [0, 0, 0]))

    # vector axis last (planar channels), hand written
    a = pattern((5, 4, 3, 2), np.float32, 14)
    hdr = ('NRRD0004\ntype: float\ndimension: 4\nsizes: 5 4 3 2\nkinds: domain domain domain vector\nendian: little\n'
           'encoding: raw\n\n')
    with open(P('vec_last.nrrd'), 'wb') as f:
        f.write(hdr.encode() + a.astype('<f4').tobytes(order='F'))
    save(P('vec_last.nrrd'), our(a, 3), channels=2)

    # hand written: big endian doubles, hex, comments, key/value pairs, CRLF line ends
    a = pattern((4, 3, 2), np.float64, 15)
    hdr = ('NRRD0005\r\n# a comment\r\ntype: double\r\ndimension: 3\r\nsizes: 4 3 2\r\nendian: big\r\nencoding: raw\r\n'
           'my key:=some value\r\nspacings: 1 2 nan\r\n\r\n')
    with open(P('f64_big.nrrd'), 'wb') as f:
        f.write(hdr.encode() + a.astype('>f8').tobytes(order='F'))
    save(P('f64_big.nrrd'), our(a), spacing=[1, 2, None], header={'my key': 'some value'})

    a = pattern((5, 3, 2), np.uint16, 16)
    raw = a.astype('>u2').tobytes(order='F')
    hx = raw.hex()
    lines = '\n'.join(hx[i:i + 70] for i in range(0, len(hx), 70))
    with open(P('u16_hex.nrrd'), 'wb') as f:
        f.write(('NRRD0004\ntype: ushort\ndimension: 3\nsizes: 5 3 2\nendian: big\nencoding: hex\n\n' + lines.upper() + '\n').encode())
    save(P('u16_hex.nrrd'), our(a))

    # LIST of data files (one slab each), raw
    a = pattern((4, 3, 4), np.int16, 17)
    flat = a.astype('<i2').tobytes(order='F')
    half = len(flat) // 2
    for i, part in enumerate((flat[:half], flat[half:])):
        with open(P('list_%d.raw' % i), 'wb') as f:
            f.write(part)
    with open(P('list.nhdr'), 'w') as f:
        f.write('NRRD0004\ntype: short\ndimension: 3\nsizes: 4 3 4\nendian: little\nencoding: raw\ndata file: LIST 3\nlist_0.raw\nlist_1.raw\n')
    save(P('list.nhdr'), our(a))

    # printf pattern, one gzip compressed 2D slice per file
    a = pattern((4, 3, 3), np.uint8, 18)
    for k in range(3):
        with open(P('slice%02d.raw.gz' % (k + 1)), 'wb') as f:
            f.write(gzip.compress(a[:, :, k].tobytes(order='F')))
    with open(P('pattern.nhdr'), 'w') as f:
        f.write('NRRD0004\ntype: uchar\ndimension: 3\nsizes: 4 3 3\nencoding: gzip\ndata file: slice%02d.raw.gz 1 3 1 2\n')
    save(P('pattern.nhdr'), our(a))

    # line skip and byte skip in a detached raw file
    a = pattern((3, 3, 2), np.int32, 19)
    with open(P('skip.dat'), 'wb') as f:
        f.write(b'first junk line\nsecond junk line\nXXXXX' + a.astype('<i4').tobytes(order='F') + b'trailing')
    with open(P('skip.nhdr'), 'w') as f:
        f.write('NRRD0004\ntype: int\ndimension: 3\nsizes: 3 3 2\nendian: little\nencoding: raw\nline skip: 2\nbyte skip: 5\ndata file: skip.dat\n')
    save(P('skip.nhdr'), our(a))

    # byte skip -1: the data are the last bytes of the file
    a = pattern((3, 2, 2), np.uint16, 20)
    with open(P('skipend.dat'), 'wb') as f:
        f.write(b'some binary header of unknown length\x00\x01' + a.astype('<u2').tobytes(order='F'))
    with open(P('skipend.nhdr'), 'w') as f:
        f.write('NRRD0004\ntype: ushort\ndimension: 3\nsizes: 3 2 2\nendian: little\nencoding: raw\nbyte skip: -1\ndata file: skipend.dat\n')
    save(P('skipend.nhdr'), our(a))

    # byte skip inside the decompressed gzip stream (attached)
    a = pattern((4, 2, 2), np.uint8, 21)
    with open(P('gzskip.nrrd'), 'wb') as f:
        f.write(b'NRRD0004\ntype: uint8\ndimension: 3\nsizes: 4 2 2\nencoding: gz\nbyte skip: 3\n\n' + gzip.compress(b'abc' + a.tobytes(order='F')))
    save(P('gzskip.nrrd'), our(a))

    # bzip2 with several concatenated streams (detached)
    a = pattern((8, 8, 4), np.int16, 22)
    raw = a.astype('<i2').tobytes(order='F')
    with open(P('multi.raw.bz2'), 'wb') as f:
        f.write(bz2.compress(raw[:200]) + bz2.compress(raw[200:]))
    with open(P('multibz2.nhdr'), 'w') as f:
        f.write('NRRD0004\ntype: int16\ndimension: 3\nsizes: 8 8 4\nendian: little\nencoding: bzip2\ndata file: multi.raw.bz2\n')
    save(P('multibz2.nhdr'), our(a))

    # check the hand written files with readers that can read them
    for n in ('vec_last.nrrd', 'f64_big.nrrd', 'skip.nhdr', 'gzskip.nrrd', 'multibz2.nhdr', 'list.nhdr'):
        try:
            data, _ = nrrd.read(P(n))
        except Exception as e:
            print('pynrrd cannot read', n, e)
            continue
        print('checked', n, data.shape)


# ============================================================================ NIfTI

def gen_nifti():
    import nibabel as nib
    d = os.path.join(OUT, 'nifti')
    clean(d)
    P = lambda n: os.path.join(d, n)
    rot = np.array([[0.0, -1.2, 0.0, 10.0], [0.9, 0.0, 0.0, -20.0], [0.0, 0.0, 2.0, 5.0], [0, 0, 0, 1]])

    def expect(path, raw_f, img, chan_axis=None, channels=1, dtype=None, slope=None, inter=None, **kw):
        data = raw_f
        if slope is not None:
            data = (raw_f.astype(np.float64) * slope + inter).astype(dtype)
        elif dtype is not None:
            data = raw_f.astype(dtype)
        aff = img.affine
        sp = np.linalg.norm(aff[:3, :3], axis=0)
        save(path, our(data, chan_axis), channels, space='RAS', spacing=list(sp) + kw.pop('extra_spacing', []),
             origin=aff[:3, 3].tolist(), direction=(aff[:3, :3] / sp).tolist(), tol=1e-5, **kw)

    def raw_of(path):
        im = nib.load(path)
        return np.asanyarray(im.dataobj.get_unscaled()) if hasattr(im.dataobj, 'get_unscaled') else np.asanyarray(im.dataobj)

    a = pattern((7, 5, 4), np.uint8)
    im = nib.Nifti1Image(a, rot)
    im.header.set_xyzt_units('mm', 'sec')
    nib.save(im, P('u8.nii'))
    expect(P('u8.nii'), a, nib.load(P('u8.nii')), units=['mm', 'mm', 'mm'])

    a = pattern((6, 5, 3), np.int16, 2)
    im = nib.Nifti1Image(a, rot)
    im.header.set_slope_inter(2.5, -10.0)
    nib.save(im, P('i16_scaled.nii'))
    expect(P('i16_scaled.nii'), raw_of(P('i16_scaled.nii')), nib.load(P('i16_scaled.nii')), dtype=np.float32, slope=2.5, inter=-10.0,
           slope_value=2.5, intercept=-10.0)

    a = pattern((6, 5, 3), np.int16, 3)
    im = nib.Nifti1Image(a, rot)
    im.header.set_slope_inter(1.0, -1024.0)
    nib.save(im, P('i16_intercept.nii'))
    r = raw_of(P('i16_intercept.nii'))
    save(P('i16_intercept.nii') if False else P('i16_intercept.nii'), our(r.astype(np.int32) - 1024), tol=1e-5)

    a = pattern((5, 4, 3), np.float32, 4)
    nib.save(nib.Nifti1Image(a, np.diag([1.5, 1.5, 3.0, 1])), P('f32.nii.gz'))
    expect(P('f32.nii.gz'), a, nib.load(P('f32.nii.gz')))

    a = pattern((4, 3, 2), np.float64, 5)
    hdr = nib.Nifti1Header(endianness='>')
    hdr.set_data_dtype(np.float64)
    im = nib.Nifti1Image(a, rot, hdr)
    nib.save(im, P('f64_be.nii'))
    assert nib.load(P('f64_be.nii')).header.endianness == '>'
    expect(P('f64_be.nii'), a, nib.load(P('f64_be.nii')))

    for t, s in ((np.uint16, 6), (np.int8, 7), (np.uint32, 8), (np.int64, 9), (np.uint64, 10), (np.int32, 11)):
        a = pattern((4, 3, 2), t, s)
        n = 'type_%s.nii' % np.dtype(t).name
        im = nib.Nifti1Image(a, np.eye(4), dtype=t)
        nib.save(im, P(n))
        expect(P(n), raw_of(P(n)), nib.load(P(n)))

    # RGB24 / RGBA32
    for n, c, dt in (('rgb24.nii', 3, [('R', 'u1'), ('G', 'u1'), ('B', 'u1')]), ('rgba32.nii', 4, [('R', 'u1'), ('G', 'u1'), ('B', 'u1'), ('A', 'u1')])):
        a = pattern((5, 4, 2, c), np.uint8, 12 + c)
        s = np.zeros((5, 4, 2), dtype=dt)
        for i, k in enumerate('RGBA'[:c]):
            s[k] = a[..., i]
        nib.save(nib.Nifti1Image(s, np.eye(4)), P(n))
        expect(P(n), a, nib.load(P(n)), chan_axis=3, channels=c, color='Rgb' if c == 3 else 'Rgba')

    # complex64 as two float32 channels
    a = pattern((4, 3, 2), np.float32, 14) + 1j * pattern((4, 3, 2), np.float32, 15)
    nib.save(nib.Nifti1Image(a.astype(np.complex64), np.eye(4)), P('complex64.nii'))
    ri = np.stack([a.real, a.imag], axis=-1).astype(np.float32)
    expect(P('complex64.nii'), ri, nib.load(P('complex64.nii')), chan_axis=3, channels=2)

    # .hdr / .img pair
    a = pattern((5, 4, 3), np.int16, 16)
    nib.save(nib.Nifti1Pair(a, rot), P('pair.hdr'))
    expect(P('pair.hdr'), a, nib.load(P('pair.hdr')))

    # NIfTI-2, 4D, with time spacing
    a = pattern((4, 3, 2, 3), np.int32, 17)
    im = nib.Nifti2Image(a, rot)
    im.header['pixdim'][4] = 2.5
    nib.save(im, P('nifti2_4d.nii'))
    expect(P('nifti2_4d.nii'), a, nib.load(P('nifti2_4d.nii')), extra_spacing=[2.5])

    # vector intent: dim 5 = 3 components (planar on disk)
    a = pattern((4, 3, 2, 1, 3), np.float32, 18)
    im = nib.Nifti1Image(a, np.eye(4))
    im.header.set_intent('vector')
    nib.save(im, P('vector.nii'))
    expect(P('vector.nii'), a[:, :, :, 0, :], nib.load(P('vector.nii')), chan_axis=3, channels=3, header={'intent_code': '1007'})

    # qform only, with qfac -1, and dim_info / a label intent / an extension
    a = pattern((5, 4, 3), np.uint8, 19)
    aff = np.array([[-1.0, 0, 0, 30], [0, 1.0, 0, -40], [0, 0, 1.5, 7], [0, 0, 0, 1]])
    im = nib.Nifti1Image(a, None)
    im.set_qform(aff, code=1)
    im.set_sform(None, code=0)
    im.header.set_dim_info(freq=1, phase=0, slice=2)
    im.header.set_intent('label', name='labels')
    im.header.extensions.append(nib.nifti1.Nifti1Extension('comment', b'hello volume'))
    nib.save(im, P('qform.nii'))
    q = nib.load(P('qform.nii'))
    assert q.header['sform_code'] == 0 and q.header['qform_code'] == 1
    expect(P('qform.nii'), a, q, header={'intent_code': '1002', 'intent_name': 'labels', 'dim_info': str(int(q.header['dim_info'])),
                                          'qform_code': '1', 'sform_code': '0'})

    # 2D (one slice)
    a = pattern((6, 5, 1), np.int16, 20)
    nib.save(nib.Nifti1Image(a, np.eye(4)), P('slice2d.nii'))
    r = nib.load(P('slice2d.nii'))
    save(P('slice2d.nii'), our(a[:, :, 0]), space='RAS', spacing=[1, 1], origin=[0, 0, 0], direction=[[1, 0], [0, 1], [0, 0]], tol=1e-6)


# ============================================================================ MetaImage

def gen_meta():
    import SimpleITK as sitk
    d = os.path.join(OUT, 'metaimage')
    clean(d)
    P = lambda n: os.path.join(d, n)
    D = [0.0, 1.0, 0.0, -1.0, 0.0, 0.0, 0.0, 0.0, 1.0]

    def write(name, arr, vector=False, compress=False, direction=None, spacing=None, origin=None):
        im = sitk.GetImageFromArray(arr, isVector=vector)
        if direction is not None:
            im.SetDirection(direction)
        if spacing is not None:
            im.SetSpacing(spacing)
        if origin is not None:
            im.SetOrigin(origin)
        sitk.WriteImage(im, P(name), compress)
        r = sitk.ReadImage(P(name))
        n = r.GetDimension()
        dm = np.array(r.GetDirection()).reshape(n, n)
        save(P(name), sitk.GetArrayFromImage(r), r.GetNumberOfComponentsPerPixel(), space='LPS', spacing=list(r.GetSpacing()),
             origin=list(r.GetOrigin()), direction=dm.tolist(), tol=1e-9)

    write('u8.mha', pattern((4, 5, 7), np.uint8), direction=D, spacing=[0.5, 0.6, 0.7], origin=[1, 2, 3])
    write('i16_z.mha', pattern((3, 5, 6), np.int16, 2), compress=True, spacing=[1, 1, 2.5])
    write('f32.mhd', pattern((3, 4, 5), np.float32, 3), origin=[-5, 5, 0])
    write('f32_z.mhd', pattern((3, 4, 5), np.float32, 4), compress=True)
    write('vec3.mha', pattern((2, 4, 5, 3), np.uint8, 5), vector=True, compress=True)
    for t, s in ((np.int8, 6), (np.uint16, 7), (np.int32, 8), (np.uint32, 9), (np.int64, 10), (np.uint64, 11), (np.float64, 12)):
        write('type_%s.mha' % np.dtype(t).name, pattern((2, 3, 4), t, s))
    write('slice2d.mha', pattern((5, 6), np.int16, 13), direction=[0.0, 1.0, 1.0, 0.0])
    write('vol4d.mha', pattern((2, 2, 3, 4), np.float32, 14))

    # hand written: big endian, HeaderSize, LIST and pattern data files
    a = pattern((2, 3, 4), np.int16, 15)  # (z, y, x)
    with open(P('be.raw'), 'wb') as f:
        f.write(b'JUNKJUNK' + a.astype('>i2').tobytes())
    with open(P('be.mhd'), 'w') as f:
        f.write('ObjectType = Image\nNDims = 3\nBinaryData = True\nBinaryDataByteOrderMSB = True\nCompressedData = False\n'
                'ElementSpacing = 1 2 3\nDimSize = 4 3 2\nHeaderSize = 8\nElementType = MET_SHORT\nElementDataFile = be.raw\n')
    save(P('be.mhd'), a, spacing=[1, 2, 3], space='LPS', origin=None, direction=None)
    a = pattern((3, 3, 4), np.uint16, 16)
    for k in range(3):
        with open(P('sl_%d.raw' % k), 'wb') as f:
            f.write(a[k].astype('<u2').tobytes())
    with open(P('list.mhd'), 'w') as f:
        f.write('ObjectType = Image\nNDims = 3\nBinaryData = True\nBinaryDataByteOrderMSB = False\nDimSize = 4 3 3\n'
                'ElementType = MET_USHORT\nElementDataFile = LIST 2D\nsl_0.raw\nsl_1.raw\nsl_2.raw\n')
    save(P('list.mhd'), a, space='LPS')
    with open(P('pattern.mhd'), 'w') as f:
        f.write('ObjectType = Image\nNDims = 3\nBinaryData = True\nBinaryDataByteOrderMSB = False\nDimSize = 4 3 3\n'
                'ElementType = MET_USHORT\nElementDataFile = sl_%d.raw 0 2 1\n')
    save(P('pattern.mhd'), a, space='LPS')
    a = pattern((2, 3, 3), np.float32, 17)
    with open(P('end.raw'), 'wb') as f:
        f.write(b'whatever header' + a.astype('<f4').tobytes())
    with open(P('headerend.mhd'), 'w') as f:
        f.write('ObjectType = Image\nNDims = 3\nDimSize = 3 3 2\nHeaderSize = -1\nElementType = MET_FLOAT\nElementDataFile = end.raw\n')
    save(P('headerend.mhd'), a, space='LPS')
    for n in ('be.mhd', 'list.mhd', 'pattern.mhd', 'headerend.mhd'):
        r = sitk.GetArrayFromImage(sitk.ReadImage(P(n)))
        exp = np.fromfile(P(n) + '.vref', dtype=np.uint8)
        print('checked', n, r.shape)


# ============================================================================ FITS

def gen_fits():
    from astropy.io import fits
    import warnings
    warnings.simplefilter('ignore')
    d = os.path.join(OUT, 'fits')
    clean(d)
    P = lambda n: os.path.join(d, n)

    def flip(a):
        return np.ascontiguousarray(a[..., ::-1, :]) if a.ndim >= 2 else a

    def expect(name, data, hdu=None, **kw):
        save(P(name), flip(data), **kw)

    for t, s in ((np.uint8, 1), (np.int16, 2), (np.int32, 3), (np.int64, 4), (np.float32, 5), (np.float64, 6),
                 (np.uint16, 7), (np.uint32, 8), (np.int8, 9), (np.uint64, 10)):
        a = pattern((5, 7), t, s)       # (y, x)
        n = 'bitpix_%s.fits' % np.dtype(t).name
        fits.PrimaryHDU(a).writeto(P(n))
        r = fits.getdata(P(n))
        assert r.dtype.newbyteorder('=') == np.dtype(t), (n, r.dtype)
        expect(n, r)

    a = pattern((3, 4, 5), np.int32, 11)   # cube (z, y, x)
    fits.PrimaryHDU(a).writeto(P('cube.fits'))
    expect('cube.fits', a)

    # BSCALE / BZERO / BLANK -> float with NaN
    raw = pattern((4, 6), np.int16, 12)
    raw[1, 2] = -32768
    h = fits.PrimaryHDU(raw)
    h.header['BSCALE'] = 0.5
    h.header['BZERO'] = 100.0
    h.header['BLANK'] = -32768
    h.header['BUNIT'] = 'Jy/beam'
    h.writeto(P('scaled.fits'))
    r = fits.getdata(P('scaled.fits'))
    assert r.dtype.newbyteorder('=') == np.float32 and np.isnan(r[1, 2])
    expect('scaled.fits', r, slope_value=0.5, intercept=100.0, value_unit='Jy/beam')

    # integer BZERO (not an unsigned convention) -> Int32 exactly
    raw = pattern((4, 5), np.int16, 13)
    h = fits.PrimaryHDU(raw)
    h.header['BZERO'] = 1000
    h.header['BSCALE'] = 1
    h.writeto(P('intzero.fits'))
    expect('intzero.fits', raw.astype(np.int32) + 1000)

    # integer BLANK without scaling: stays Int16, metadata reports it
    raw = pattern((3, 5), np.int16, 14)
    raw[0, 0] = -999
    h = fits.PrimaryHDU(raw)
    h.header['BLANK'] = -999
    h.writeto(P('blank_int.fits'))
    expect('blank_int.fits', raw, blank=-999)

    # several HDUs: primary image, binary table, IMAGE extension (cube)
    a1 = pattern((4, 6), np.float32, 15)
    a2 = pattern((2, 3, 4), np.int16, 16)
    tab = fits.BinTableHDU.from_columns([fits.Column(name='x', format='J', array=np.arange(3))])
    fits.HDUList([fits.PrimaryHDU(a1), tab, fits.ImageHDU(a2, name='SECOND')]).writeto(P('multi.fits'))
    expect('multi.fits', a1, hdus=2)
    save(P('multi.fits') + '.hdu2', flip(a2))

    # empty primary, image in the first extension
    a = pattern((5, 6), np.uint16, 17)
    fits.HDUList([fits.PrimaryHDU(), fits.ImageHDU(a)]).writeto(P('ext_only.fits'))
    expect('ext_only.fits', fits.getdata(P('ext_only.fits'), 1))

    # WCS
    a = pattern((6, 8), np.float32, 18)
    h = fits.PrimaryHDU(a)
    for k, v in dict(CTYPE1='RA---TAN', CTYPE2='DEC--TAN', CRPIX1=4.5, CRPIX2=3.0, CRVAL1=150.0, CRVAL2=2.0, CDELT1=-0.001,
                     CDELT2=0.002, PC1_1=0.8, PC1_2=-0.6, PC2_1=0.6, PC2_2=0.8, CUNIT1='deg', CUNIT2='deg').items():
        h.header[k] = v
    h.header['LONGSTR'] = 'x' * 100   # CONTINUE cards
    h.writeto(P('wcs.fits'))
    # expected linear WCS for our (flipped) grid: world = CRVAL + M (p - CRPIX), p1 = i + 1, p2 = H - j
    M = np.array([[-0.001 * 0.8, -0.001 * -0.6], [0.002 * 0.6, 0.002 * 0.8]])
    Hh = 6
    o = np.array([150.0, 2.0]) + M @ np.array([1 - 4.5, Hh - 3.0])
    cols = np.stack([M[:, 0], -M[:, 1]], axis=1)
    sp = np.linalg.norm(cols, axis=0)
    expect('wcs.fits', a, space='FITS-WCS', origin=o.tolist(), spacing=sp.tolist(), direction=(cols / sp).tolist(), tol=1e-9,
           units=['deg', 'deg'], header={'CTYPE1': 'RA---TAN', 'LONGSTR': 'x' * 100})

    # gzipped file
    a = pattern((4, 5), np.int16, 19)
    fits.PrimaryHDU(a).writeto(P('_tmp.fits'))
    with open(P('_tmp.fits'), 'rb') as f, open(P('plain.fits.gz'), 'wb') as g:
        g.write(gzip.compress(f.read()))
    os.remove(P('_tmp.fits'))
    expect('plain.fits.gz', a)

    # tile compression
    def comp(name, a, read_hdu=1, **kw):
        fits.CompImageHDU(a, **kw).writeto(P(name))
        with fits.open(P(name)) as f:
            r = f[read_hdu].data
        expect(name, r, tol=kw.pop('tol', 0))

    comp('rice_i16.fits', pattern((9, 13), np.int16, 20), compression_type='RICE_1')
    comp('rice_i32.fits', pattern((9, 13), np.int32, 21), compression_type='RICE_1')
    comp('rice_u8.fits', pattern((9, 13), np.uint8, 22), compression_type='RICE_1')
    comp('rice_u16.fits', pattern((9, 13), np.uint16, 23), compression_type='RICE_1')
    smooth = (np.add.outer(np.arange(40), np.arange(50)) * 3).astype(np.int32)
    comp('rice_smooth.fits', smooth, compression_type='RICE_1', tile_shape=(7, 11))
    comp('rice_zeros.fits', np.zeros((5, 70), np.int16), compression_type='RICE_1')
    comp('rice_cube.fits', pattern((3, 5, 6), np.int16, 24), compression_type='RICE_1', tile_shape=(1, 5, 6))
    comp('gzip1_i16.fits', pattern((7, 9), np.int16, 25), compression_type='GZIP_1')
    comp('gzip1_i64.fits', pattern((4, 5), np.int64, 26), compression_type='GZIP_1')
    comp('gzip2_f32.fits', pattern((7, 9), np.float32, 27), compression_type='GZIP_2', quantize_level=0.0)
    comp('gzip2_f64.fits', pattern((7, 9), np.float64, 28), compression_type='GZIP_2', quantize_level=0.0)
    comp('nocomp_i32.fits', pattern((4, 6), np.int32, 29), compression_type='NOCOMPRESS')
    noisy = (np.random.default_rng(30).standard_normal((20, 30)) * 10 + 100).astype(np.float32)
    noisy[3, 4] = np.nan
    comp('rice_f32_dither1.fits', noisy, compression_type='RICE_1', quantize_level=16, quantize_method=1, dither_seed=1234)
    z = noisy.copy()
    z[5, :7] = 0
    comp('rice_f32_dither2.fits', z, compression_type='RICE_1', quantize_level=16, quantize_method=2, dither_seed=77)
    comp('rice_f32_nodither.fits', noisy, compression_type='RICE_1', quantize_level=16, quantize_method=-1)
    comp('gzip1_f32_quant.fits', noisy, compression_type='GZIP_1', quantize_level=8, quantize_method=1, dither_seed=9000)
    mask = np.zeros((6, 40), np.int32)
    mask[1, 3:9] = 1
    mask[2, :] = 5
    mask[3, 10:12] = 70000
    mask[4, ::3] = 2
    comp('plio.fits', mask, compression_type='PLIO_1')
    fits.CompImageHDU(pattern((8, 8), np.int16, 31), compression_type='HCOMPRESS_1').writeto(P('hcompress.fits'))


if __name__ == '__main__':
    which = sys.argv[1:] or ['nrrd', 'nifti', 'meta', 'fits']
    if 'nrrd' in which:
        gen_nrrd()
    if 'nifti' in which:
        gen_nifti()
    if 'meta' in which:
        gen_meta()
    if 'fits' in which:
        gen_fits()
