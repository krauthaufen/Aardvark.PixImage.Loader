"""CharLS (the JPEG-LS reference used by imagecodecs) through its C API with ctypes.

imagecodecs.jpegls_encode only exposes NEAR, the bit depth and the HP colour transforms; the bundled libcharls also takes the
interleave mode, preset coding parameters (T1 T2 T3 RESET MAXVAL), mapping tables and comments. The library is only RUN here
as a test oracle. Returns None from find() when imagecodecs / libcharls is missing.
"""
import ctypes
import glob
import os
import numpy as np

_lib = None


def find():
    global _lib
    if _lib is not None:
        return _lib
    env = os.environ.get('PIXCODEC_CHARLS')     # a CharLS shared library of its own (imagecodecs links it statically on Windows)
    if env:
        cands = [env]
    else:
        try:
            import imagecodecs
        except ImportError:
            return None
        base = os.path.dirname(imagecodecs.__file__)
        cands = sorted(glob.glob(os.path.join(base, '..', 'imagecodecs.libs', 'libcharls*.so*'))) + \
            sorted(glob.glob(os.path.join(base, '..', 'imagecodecs.libs', 'charls*.dll'))) + \
            sorted(glob.glob(os.path.join(base, '.dylibs', 'libcharls*.dylib')))
    if not cands:
        return None
    lib = ctypes.CDLL(cands[0])
    vp, sz = ctypes.c_void_p, ctypes.c_size_t
    lib.charls_jpegls_encoder_create.restype = vp
    lib.charls_jpegls_decoder_create.restype = vp
    lib.charls_get_error_message.restype = ctypes.c_char_p
    for n in ('charls_jpegls_encoder_destroy', 'charls_jpegls_decoder_destroy'):
        getattr(lib, n).argtypes = [vp]
    _lib = lib
    return lib


class FrameInfo(ctypes.Structure):
    _fields_ = [('width', ctypes.c_uint32), ('height', ctypes.c_uint32), ('bits_per_sample', ctypes.c_int32),
                ('component_count', ctypes.c_int32)]


class PcParameters(ctypes.Structure):
    _fields_ = [('maximum_sample_value', ctypes.c_int32), ('threshold1', ctypes.c_int32), ('threshold2', ctypes.c_int32),
                ('threshold3', ctypes.c_int32), ('reset_value', ctypes.c_int32)]


def _check(lib, err, what):
    if err != 0:
        raise RuntimeError(f'charls {what}: {lib.charls_get_error_message(err).decode()} ({err})')


ILV_NONE, ILV_LINE, ILV_SAMPLE = 0, 1, 2


def encode(img, bits=None, near=0, ilv=ILV_LINE, transform=0, pc=None, spiff=False, comment=None,
           mapping_tables=(), table_ids=(), options=0):
    """img: (H,W) or (H,W,C) uint8/uint16, interleaved. ilv NONE: CharLS expects planar input, handled here.
    pc: (MAXVAL, T1, T2, T3, RESET), zeros for defaults. mapping_tables: [(id, entry_size, bytes)];
    table_ids: [(component, id)]."""
    lib = find()
    vp = ctypes.c_void_p
    img = np.asarray(img)
    if img.ndim == 2:
        img = img[:, :, None]
    h, w, c = img.shape
    if bits is None:
        bits = 8 if img.dtype == np.uint8 else 16
    enc = vp(lib.charls_jpegls_encoder_create())
    try:
        fi = FrameInfo(w, h, bits, c)
        _check(lib, lib.charls_jpegls_encoder_set_frame_info(enc, ctypes.byref(fi)), 'frame info')
        _check(lib, lib.charls_jpegls_encoder_set_near_lossless(enc, ctypes.c_int32(near)), 'near')
        if c > 1:
            _check(lib, lib.charls_jpegls_encoder_set_interleave_mode(enc, ctypes.c_int32(ilv)), 'interleave')
        if transform:
            _check(lib, lib.charls_jpegls_encoder_set_color_transformation(enc, ctypes.c_int32(transform)), 'transform')
        if options:
            _check(lib, lib.charls_jpegls_encoder_set_encoding_options(enc, ctypes.c_int32(options)), 'options')
        if pc is not None:
            p = PcParameters(*pc)
            _check(lib, lib.charls_jpegls_encoder_set_preset_coding_parameters(enc, ctypes.byref(p)), 'preset')
        size = ctypes.c_size_t()
        _check(lib, lib.charls_jpegls_encoder_get_estimated_destination_size(enc, ctypes.byref(size)), 'size')
        n = size.value + 65536 + sum(len(t[2]) for t in mapping_tables) * 2
        buf = ctypes.create_string_buffer(n)
        _check(lib, lib.charls_jpegls_encoder_set_destination_buffer(enc, buf, ctypes.c_size_t(n)), 'dest')
        if spiff:
            # SPIFF colour space (T.84 F.2.2): 8 gray, 10 RGB, 2 none; resolution units 0 aspect ratio (spiff=1) or
            # 1 dots per inch (spiff=2); vertical 72, horizontal 96
            cs = {1: 8, 3: 10}.get(c, 2)
            _check(lib, lib.charls_jpegls_encoder_write_standard_spiff_header(enc, ctypes.c_int32(cs), ctypes.c_int32(1 if spiff == 2 else 0),
                                                                              ctypes.c_uint32(72), ctypes.c_uint32(96)), 'spiff')
        if comment is not None:
            _check(lib, lib.charls_jpegls_encoder_write_comment(enc, comment, ctypes.c_size_t(len(comment))), 'comment')
        for tid, esize, data in mapping_tables:
            _check(lib, lib.charls_jpegls_encoder_write_mapping_table(enc, ctypes.c_int32(tid), ctypes.c_int32(esize), data,
                                                                       ctypes.c_size_t(len(data))), 'mapping table')
        for comp, tid in table_ids:
            _check(lib, lib.charls_jpegls_encoder_set_mapping_table_id(enc, ctypes.c_int32(comp), ctypes.c_int32(tid)), 'table id')
        src = img if (c == 1 or ilv != ILV_NONE) else np.moveaxis(img, 2, 0)
        src = np.ascontiguousarray(src)
        _check(lib, lib.charls_jpegls_encoder_encode_from_buffer(enc, src.ctypes.data_as(vp), ctypes.c_size_t(src.nbytes),
                                                                  ctypes.c_uint32(0)), 'encode')
        _check(lib, lib.charls_jpegls_encoder_get_bytes_written(enc, ctypes.byref(size)), 'written')
        return buf.raw[:size.value]
    finally:
        lib.charls_jpegls_encoder_destroy(enc)


def decode(data):
    """Returns (H,W,C) uint8/uint16 interleaved (whatever interleave mode the file has)."""
    lib = find()
    vp = ctypes.c_void_p
    dec = vp(lib.charls_jpegls_decoder_create())
    try:
        src = ctypes.create_string_buffer(bytes(data), len(data))
        _check(lib, lib.charls_jpegls_decoder_set_source_buffer(dec, src, ctypes.c_size_t(len(data))), 'source')
        _check(lib, lib.charls_jpegls_decoder_read_header(dec), 'header')
        fi = FrameInfo()
        _check(lib, lib.charls_jpegls_decoder_get_frame_info(dec, ctypes.byref(fi)), 'frame info')
        ilv = ctypes.c_int32()
        _check(lib, lib.charls_jpegls_decoder_get_interleave_mode(dec, ctypes.byref(ilv)), 'interleave')
        size = ctypes.c_size_t()
        _check(lib, lib.charls_jpegls_decoder_get_destination_size(dec, ctypes.c_uint32(0), ctypes.byref(size)), 'size')
        out = np.zeros(size.value, np.uint8)
        _check(lib, lib.charls_jpegls_decoder_decode_to_buffer(dec, out.ctypes.data_as(vp), ctypes.c_size_t(size.value),
                                                               ctypes.c_uint32(0)), 'decode')
        dt = np.uint8 if fi.bits_per_sample <= 8 else np.uint16
        a = out.view(dt)
        h, w, c = fi.height, fi.width, fi.component_count
        if c > 1 and ilv.value == ILV_NONE:
            return np.ascontiguousarray(np.moveaxis(a.reshape(c, h, w), 0, 2))
        return a.reshape(h, w, c)
    finally:
        lib.charls_jpegls_decoder_destroy(dec)
