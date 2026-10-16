"""Reference image sidecar format shared by the fixture generators and the C# tests.

  magic 'PXRF' | int32 width | int32 height | int32 channels | int32 sampleType | raw little-endian samples

sampleType matches Aardvark.ImageCodecs.SampleType: 0 u8, 1 u16, 2 u32, 3 i8, 4 i16, 5 i32, 6 f16, 7 f32, 8 f64, 9 u64, 10 i64.
Samples are row-major, top row first, channel-interleaved (R,G,B,A order).
"""
import os
import struct
import sys
import numpy as np

_TYPES = {np.dtype('uint8'): 0, np.dtype('uint16'): 1, np.dtype('uint32'): 2, np.dtype('int8'): 3,
          np.dtype('int16'): 4, np.dtype('int32'): 5, np.dtype('float16'): 6, np.dtype('float32'): 7,
          np.dtype('float64'): 8, np.dtype('uint64'): 9, np.dtype('int64'): 10}
_DTYPES = {v: k for k, v in _TYPES.items()}


def write_ref(path, arr):
    """arr: (H,W) or (H,W,C) numpy array."""
    arr = np.asarray(arr)
    if arr.ndim == 2:
        arr = arr[:, :, None]
    h, w, c = arr.shape
    t = _TYPES[arr.dtype.newbyteorder('=')]
    with open(path, 'wb') as f:
        f.write(b'PXRF')
        f.write(struct.pack('<4i', w, h, c, t))
        f.write(np.ascontiguousarray(arr).astype(arr.dtype.newbyteorder('<')).tobytes())


def missing(name, detail=''):
    """Exit status 77 because a tool or package the check needs is not installed. The C# tests then check less
    (TestUtil.CannotVerify), or fail under PIXCODEC_REQUIRE_ORACLES=1 naming `name`."""
    sys.stderr.write(f'[missing oracle: {name}] {detail}\n')
    sys.exit(77)


def unverifiable(why):
    """Exit status 77 for a file no reference decoder can verify, whatever is installed (the C# test checks less)."""
    sys.stderr.write(f'[unverifiable] {why}\n')
    sys.exit(77)


def read_ref(path):
    with open(path, 'rb') as f:
        assert f.read(4) == b'PXRF'
        w, h, c, t = struct.unpack('<4i', f.read(16))
        dt = _DTYPES[t].newbyteorder('<')
        return np.frombuffer(f.read(), dtype=dt).reshape(h, w, c)


def testdata_dir(*rel):
    """A path in the test-data repository with the third-party files ($PIXCODEC_TESTDATA or Aardvark.PixImage.PixCodec.TestData next to
    this repository, as the C# tests find it), or None when there is none."""
    d = os.environ.get('PIXCODEC_TESTDATA') or os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', '..', 'Aardvark.PixImage.PixCodec.TestData')
    d = os.path.normpath(d)
    if not os.path.isdir(os.path.join(d, 'fixtures')):
        print(f'no test-data repository at {d} (set PIXCODEC_TESTDATA): third-party files left out', file=sys.stderr)
        return None
    return os.path.join(d, *rel)


def test_pattern(w, h, c, dtype=np.uint8, seed=1):
    """Deterministic image with gradients, edges and some noise - exercises filters/predictors well."""
    rng = np.random.default_rng(seed)
    y, x = np.mgrid[0:h, 0:w].astype(np.float64)
    chans = []
    for i in range(c):
        v = 0.5 + 0.5 * np.sin(x / (7.0 + 3 * i) + i) * np.cos(y / (5.0 + 2 * i))
        v = 0.6 * v + 0.3 * (((x // 8 + y // 8 + i) % 2)) + 0.1 * rng.random((h, w))
        chans.append(v)
    a = np.clip(np.stack(chans, axis=-1), 0, 1)
    dt = np.dtype(dtype)
    if dt.kind == 'u':
        return np.round(a * np.iinfo(dt).max).astype(dt)
    if dt.kind == 'i':
        info = np.iinfo(dt)
        return np.round(a * (float(info.max) - float(info.min)) + info.min).astype(dt)
    return (a * 4.0 - 1.0).astype(dt)


def write_vref(path, arr, channels=1):
    """n-dimensional grid sidecar ('PXVR'): arr in OUR layout as a numpy array of shape (..., z, y, x[, c]) in C order, i.e.
    the slowest axis first. channels: size of the last axis if it holds interleaved channels, else 1.

      magic 'PXVR' | int32 ndims | int32 sizes[ndims] (x first) | int32 channels | int32 sampleType | raw little-endian samples
    """
    arr = np.ascontiguousarray(np.asarray(arr))
    shape = arr.shape[:-1] if channels > 1 else arr.shape
    sizes = list(reversed(shape))
    t = _TYPES[arr.dtype.newbyteorder('=')]
    with open(path, 'wb') as f:
        f.write(b'PXVR')
        f.write(struct.pack('<i', len(sizes)))
        f.write(struct.pack('<%di' % len(sizes), *sizes))
        f.write(struct.pack('<2i', channels, t))
        f.write(arr.astype(arr.dtype.newbyteorder('<')).tobytes())


def read_vref(path):
    with open(path, 'rb') as f:
        assert f.read(4) == b'PXVR'
        n, = struct.unpack('<i', f.read(4))
        sizes = struct.unpack('<%di' % n, f.read(4 * n))
        c, t = struct.unpack('<2i', f.read(8))
        dt = _DTYPES[t].newbyteorder('<')
        shape = tuple(reversed(sizes)) + ((c,) if c > 1 else ())
        return np.frombuffer(f.read(), dtype=dt).reshape(shape), c
