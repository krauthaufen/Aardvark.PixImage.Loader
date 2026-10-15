"""ICC colour management fixtures, computed by LittleCMS 2 (liblcms2, MIT; only run through ctypes, never ported).

usage: gen_icc.py [--compact DIR] [--icc-srgb-v4 FILE]

Writes tests/fixtures/icc:
  profiles/lcms_*.icc   profiles made by LittleCMS itself: matrix / TRC (v2 and v4, every parametric curve type,
                        tables), gray, CMYK with lut16 (v2), lut8 (v2) and lutAToB / lutBToA (v4) tables for three
                        intents with Lab and XYZ PCS, an RGB lutAToB with A curves, CLUT, M curves, matrix and B curves
  ours/*.icc            Aardvark.PixImage.IO's own profiles (written by IccTests with PIXIMAGEIO_ICC_DUMP set): checked to open in
                        LittleCMS, their profile ID is recomputed by LittleCMS, and they serve as conversion endpoints
  in_<pattern>.ref      input colours (grid + random + ramps) per channel count and sample type
  <case>.ref            LittleCMS float conversion (cmsFLAGS_NOOPTIMIZE) of the input, float32 normalised 0..1
                        (CMYK 0..1 = 0..100 %, Lab as L*, a*, b*)
  cases.json            the list of cases: profiles, intent, black point compensation, input, output
  img_*                 PNG / WebP / TIFF / JPEG files with embedded profiles (written by Pillow) and the expected
                        sRGB conversion of their pixels (img_*.srgb.ref)

With --compact (a checkout of https://github.com/saucecontrol/Compact-ICC-Profiles, CC0) and --icc-srgb-v4 (the ICC's
sRGB_v4_ICC_preference.icc) the third-party profiles are copied to the test-data repository (fixtures/icc) with cases
of their own (fixtures/icc/cases.json there).
"""
import argparse
import ctypes as C
import json
import os
import shutil
import sys

import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from pxref import write_ref, read_ref, testdata_dir  # noqa: E402

OUT = os.path.normpath(os.path.join(HERE, '..', 'fixtures', 'icc'))

lc = C.CDLL('liblcms2.so.2')


class XYZ(C.Structure):
    _fields_ = [('X', C.c_double), ('Y', C.c_double), ('Z', C.c_double)]


class XyY(C.Structure):
    _fields_ = [('x', C.c_double), ('y', C.c_double), ('Y', C.c_double)]


class XyYTriple(C.Structure):
    _fields_ = [('r', XyY), ('g', XyY), ('b', XyY)]


P = C.c_void_p
for name, res, args in [
    ('cmsCreate_sRGBProfile', P, []),
    ('cmsCreateRGBProfile', P, [C.POINTER(XyY), C.POINTER(XyYTriple), C.POINTER(P)]),
    ('cmsCreateGrayProfile', P, [C.POINTER(XyY), P]),
    ('cmsCreateLab4Profile', P, [P]),
    ('cmsCreateProfilePlaceholder', P, [P]),
    ('cmsBuildGamma', P, [P, C.c_double]),
    ('cmsBuildParametricToneCurve', P, [P, C.c_int, C.POINTER(C.c_double)]),
    ('cmsBuildTabulatedToneCurve16', P, [P, C.c_uint32, C.POINTER(C.c_uint16)]),
    ('cmsSetProfileVersion', None, [P, C.c_double]),
    ('cmsSetDeviceClass', None, [P, C.c_uint32]),
    ('cmsSetColorSpace', None, [P, C.c_uint32]),
    ('cmsSetPCS', None, [P, C.c_uint32]),
    ('cmsSaveProfileToMem', C.c_int, [P, P, C.POINTER(C.c_uint32)]),
    ('cmsOpenProfileFromMem', P, [P, C.c_uint32]),
    ('cmsCloseProfile', C.c_int, [P]),
    ('cmsCreateTransform', P, [P, C.c_uint32, P, C.c_uint32, C.c_uint32, C.c_uint32]),
    ('cmsDoTransform', None, [P, P, P, C.c_uint32]),
    ('cmsDeleteTransform', None, [P]),
    ('cmsPipelineAlloc', P, [P, C.c_uint32, C.c_uint32]),
    ('cmsPipelineInsertStage', C.c_int, [P, C.c_int, P]),
    ('cmsPipelineSetSaveAs8bitsFlag', C.c_int, [P, C.c_int]),
    ('cmsStageAllocToneCurves', P, [P, C.c_uint32, C.POINTER(P)]),
    ('cmsStageAllocCLut16bit', P, [P, C.c_uint32, C.c_uint32, C.c_uint32, C.POINTER(C.c_uint16)]),
    ('cmsStageAllocMatrix', P, [P, C.c_uint32, C.c_uint32, C.POINTER(C.c_double), C.POINTER(C.c_double)]),
    ('cmsWriteTag', C.c_int, [P, C.c_uint32, P]),
    ('cmsMLUalloc', P, [P, C.c_uint32]),
    ('cmsMLUsetASCII', C.c_int, [P, C.c_char_p, C.c_char_p, C.c_char_p]),
    ('cmsMD5computeID', C.c_int, [P]),
    ('cmsGetHeaderProfileID', None, [P, P]),
    ('cmsGetEncodedICCversion', C.c_uint32, [P]),
    ('cmsReadTag', P, [P, C.c_uint32]),
    ('cmsPipelineEvalFloat', None, [C.POINTER(C.c_float), C.POINTER(C.c_float), P]),
]:
    f = getattr(lc, name)
    f.restype = res
    f.argtypes = args


def sig(s):
    return int.from_bytes(s.encode('ascii'), 'big')


# formats: FLOAT_SH(1) | COLORSPACE_SH(cs) | CHANNELS_SH(n) | BYTES_SH(b)
PT = {'GRAY': 3, 'RGB': 4, 'CMYK': 6, 'XYZ': 9, 'Lab': 10}


def fmt_float(space, n, double=False):
    return (1 << 22) | (PT[space] << 16) | (n << 3) | (0 if double else 4)


NOOPTIMIZE, BPC = 0x0100, 0x2000

# ---------------------------------------------------------------------------- profile helpers


def save(h):
    n = C.c_uint32(0)
    assert lc.cmsSaveProfileToMem(h, None, C.byref(n))
    buf = C.create_string_buffer(n.value)
    assert lc.cmsSaveProfileToMem(h, buf, C.byref(n))
    return buf.raw[:n.value]


def open_mem(b):
    h = lc.cmsOpenProfileFromMem(b, len(b))
    if not h:
        raise RuntimeError('LittleCMS cannot open the profile')
    return h


def para(t, *p):
    arr = (C.c_double * 10)(*p)
    return lc.cmsBuildParametricToneCurve(None, t + 1, arr)  # LittleCMS numbers ICC type n as n + 1


def table(vals):
    arr = (C.c_uint16 * len(vals))(*vals)
    return lc.cmsBuildTabulatedToneCurve16(None, len(vals), arr)


def xyy(x, y):
    return XyY(x, y, 1.0)


def rgb_profile(white, prim, curves, version=4.3, desc=None):
    tri = XyYTriple(xyy(*prim[0]), xyy(*prim[1]), xyy(*prim[2]))
    cv = (P * 3)(*curves)
    h = lc.cmsCreateRGBProfile(C.byref(xyy(*white)), C.byref(tri), cv)
    lc.cmsSetProfileVersion(h, version)
    if desc:
        set_desc(h, desc)
    return h


def set_desc(h, text):
    m = lc.cmsMLUalloc(None, 1)
    lc.cmsMLUsetASCII(m, b'en', b'US', text.encode())
    lc.cmsWriteTag(h, sig('desc'), m)


D65 = (0.3127, 0.3290)
D50 = (0.3457, 0.3585)
SRGB_P = [(0.64, 0.33), (0.30, 0.60), (0.15, 0.06)]
P3_P = [(0.680, 0.320), (0.265, 0.690), (0.150, 0.060)]
REC2020_P = [(0.708, 0.292), (0.170, 0.797), (0.131, 0.046)]
ROMM_P = [(0.7347, 0.2653), (0.1596, 0.8404), (0.0366, 0.0001)]
A98_P = [(0.64, 0.33), (0.21, 0.71), (0.15, 0.06)]
SRGB_CURVE = (2.4, 1 / 1.055, 0.055 / 1.055, 1 / 12.92, 0.04045)


def lab_to_xyz(L, a, b):
    fy = (L + 16) / 116
    fx, fz = fy + a / 500, fy - b / 200

    def finv(t):
        return np.where(t > 6 / 29, t ** 3, 3 * (6 / 29) ** 2 * (t - 4 / 29))
    return finv(fx) * 0.9642, finv(fy) * 1.0, finv(fz) * 0.8249


def xyz_to_lab(X, Y, Z):
    def f(t):
        return np.where(t > (6 / 29) ** 3, np.cbrt(t), t / (3 * (6 / 29) ** 2) + 4 / 29)
    fx, fy, fz = f(X / 0.9642), f(Y / 1.0), f(Z / 0.8249)
    return 116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz)


# linear sRGB -> XYZ D50 (Bradford adapted), for the synthetic printer model
M_SRGB_D50 = np.array([[0.4360747, 0.3850649, 0.1430804],
                       [0.2225045, 0.7168786, 0.0606169],
                       [0.0139322, 0.0971045, 0.7141733]])


def printer_forward(cmyk, variant):
    """A made-up but smooth CMYK printer: dot gain, unwanted absorptions, black not quite black. Returns XYZ (relative)."""
    c, m, y, k = [cmyk[..., i] for i in range(4)]
    gain = 0.85 + 0.05 * variant

    def g(v):
        return v ** gain
    r = (1 - 0.93 * g(c) - 0.05 * g(m)) * (1 - 0.92 * g(k))
    gg = (1 - 0.90 * g(m) - 0.08 * g(y) - 0.03 * g(c)) * (1 - 0.92 * g(k))
    b = (1 - 0.88 * g(y) - 0.06 * g(m)) * (1 - 0.92 * g(k))
    rgb = np.clip(np.stack([r, gg, b], -1), 0.0, 1.0)
    rgb = 0.012 + 0.988 * rgb  # paper / ink black floor
    if variant == 2:  # 'saturation': boost chroma a little
        mean = rgb.mean(-1, keepdims=True)
        rgb = np.clip(mean + 1.15 * (rgb - mean), 0.001, 1.0)
    xyz = rgb @ M_SRGB_D50.T
    if variant == 0:  # 'perceptual': lift black to the v4 perceptual reference black
        xyz = 0.0034731 * np.array([0.9642, 1, 0.8249]) + (1 - 0.0034731) * xyz
    return xyz


def printer_inverse(lab):
    """A rough inverse of printer_forward (variant 1) with grey component replacement; only needs to be smooth."""
    X, Y, Z = lab_to_xyz(lab[..., 0], lab[..., 1], lab[..., 2])
    rgb = np.stack([X, Y, Z], -1) @ np.linalg.inv(M_SRGB_D50).T
    rgb = np.clip((rgb - 0.012) / 0.988, 0, 1)
    cmy = 1 - rgb
    k = np.clip(np.min(cmy, -1) - 0.2, 0, 1) * 0.9
    cmy = np.clip((cmy - k[..., None]) / np.maximum(1 - k[..., None], 1e-6), 0, 1)
    return np.concatenate([cmy, k[..., None]], -1) ** (1 / 0.88)


def grid_points(n, dims):
    g = np.linspace(0, 1, n)
    mesh = np.meshgrid(*([g] * dims), indexing='ij')  # first input varies slowest, as ICC CLUTs are ordered
    return np.stack([m.reshape(-1) for m in mesh], -1)


def encode_pcs(v, pcs):
    """Real PCS values -> LittleCMS's internal 16 bit encoding (v4 Lab, u1Fixed15 XYZ)."""
    if pcs == 'Lab':
        e = np.stack([v[..., 0] / 100, (v[..., 1] + 128) / 255, (v[..., 2] + 128) / 255], -1)
    else:
        e = v * 32768 / 65535
    return np.clip(np.round(e * 65535), 0, 65535).astype(np.uint16)


def decode_pcs(e, pcs):
    e = e.astype(np.float64) / 65535
    if pcs == 'Lab':
        return np.stack([e[..., 0] * 100, e[..., 1] * 255 - 128, e[..., 2] * 255 - 128], -1)
    return e * 65535 / 32768


def clut_stage(nin, nout, grid, values16):
    arr = (C.c_uint16 * values16.size)(*values16.reshape(-1).tolist())
    return lc.cmsStageAllocCLut16bit(None, grid, nin, nout, arr)


def curves_stage(curves):
    arr = (P * len(curves))(*curves)
    return lc.cmsStageAllocToneCurves(None, len(curves), arr)


def ident_curves(n):
    return curves_stage([lc.cmsBuildGamma(None, 1.0) for _ in range(n)])


def pipeline(nin, nout, stages, eight=False):
    p = lc.cmsPipelineAlloc(None, nin, nout)
    for s in stages:
        assert s, 'stage allocation failed'
        assert lc.cmsPipelineInsertStage(p, 1, s)  # cmsAT_END
    if eight:
        lc.cmsPipelineSetSaveAs8bitsFlag(p, 1)
    return p


def gamma_curve(g, tabulated):
    # lut8 needs tables of 256 entries; lut16 takes the size of the first curve for all (keep them alike)
    if not tabulated:
        return para(0, g)
    return table([int(round(65535 * (i / 255) ** g)) for i in range(256)])


def cmyk_profile(version, pcs='Lab', eight=False, desc='CMYK'):
    h = lc.cmsCreateProfilePlaceholder(None)
    lc.cmsSetProfileVersion(h, version)
    lc.cmsSetDeviceClass(h, sig('prtr'))
    lc.cmsSetColorSpace(h, sig('CMYK'))
    lc.cmsSetPCS(h, sig(pcs.ljust(4)))
    set_desc(h, desc)
    paper = lab_to_xyz(94.0, 0.6, -2.5)
    lc.cmsWriteTag(h, sig('wtpt'), C.byref(XYZ(*[float(v) for v in paper])))
    gin = 9 if not eight else 7
    pts = grid_points(gin, 4)
    for intent in range(3):
        xyz = printer_forward(pts, intent)
        v = np.stack(xyz_to_lab(*[xyz[:, i] for i in range(3)]), -1) if pcs == 'Lab' else xyz
        a2b = pipeline(4, 3, [curves_stage([gamma_curve(1.0 + 0.1 * i, version < 4) for i in range(4)]),
                              clut_stage(4, 3, gin, encode_pcs(v, pcs)),
                              curves_stage([gamma_curve(1.0, version < 4) for i in range(3)])], eight)
        assert lc.cmsWriteTag(h, sig(f'A2B{intent}'), a2b)
        gout = 13 if not eight else 9
        q = grid_points(gout, 3)
        real = decode_pcs(np.round(q * 65535), pcs)
        lab = real if pcs == 'Lab' else np.stack(xyz_to_lab(real[:, 0], real[:, 1], real[:, 2]), -1)
        if intent == 0:
            lab = lab.copy()
            lab[:, 0] = np.clip((lab[:, 0] - 3) / 0.97, 0, 100)
        dev = printer_inverse(lab)
        b2a = pipeline(3, 4, [curves_stage([gamma_curve(1.0, version < 4) for i in range(3)]),
                              clut_stage(3, 4, gout, np.clip(np.round(dev * 65535), 0, 65535).astype(np.uint16)),
                              curves_stage([gamma_curve(1.0 / (1.0 + 0.1 * i), version < 4) for i in range(4)])], eight)
        assert lc.cmsWriteTag(h, sig(f'B2A{intent}'), b2a)
    return save(h)


def rgb_lut_profile():
    """RGB -> XYZ lutAToB with A curves, a 3D CLUT, M curves, a matrix with offsets and B curves; and the inverse."""
    h = lc.cmsCreateProfilePlaceholder(None)
    lc.cmsSetProfileVersion(h, 4.3)
    lc.cmsSetDeviceClass(h, sig('mntr'))
    lc.cmsSetColorSpace(h, sig('RGB '))
    lc.cmsSetPCS(h, sig('XYZ '))
    set_desc(h, 'RGB LUT')
    lc.cmsWriteTag(h, sig('wtpt'), C.byref(XYZ(0.9642, 1.0, 0.8249)))
    g = 7
    pts = grid_points(g, 3)
    # CLUT: a mild hue twist in linear light
    tw = np.stack([pts[:, 0] * 0.92 + pts[:, 1] * 0.08, pts[:, 1] * 0.95 + pts[:, 2] * 0.05, pts[:, 2] * 0.9 + pts[:, 0] * 0.1], -1)
    m = M_SRGB_D50 * 32768 / 65535 / 1.02  # into the u1Fixed15 encoding, leaving room for the offset
    off = np.array([0.002, 0.002, 0.0015])
    a2b = pipeline(3, 3, [curves_stage([para(0, 2.2), para(3, *SRGB_CURVE), para(1, 2.0, 0.95, 0.05)]),
                          clut_stage(3, 3, g, np.round(tw * 65535).astype(np.uint16)),
                          curves_stage([para(0, 1.1), para(0, 1.0), para(0, 0.9)]),
                          lc.cmsStageAllocMatrix(None, 3, 3, (C.c_double * 9)(*m.reshape(-1)), (C.c_double * 3)(*off)),
                          curves_stage([para(0, 1.0)] * 3)])
    assert lc.cmsWriteTag(h, sig('A2B0'), a2b)
    mi = np.linalg.inv(m)
    offi = -mi @ off
    q = grid_points(g, 3)
    inv_tw = np.clip(q @ np.linalg.inv(np.array([[0.92, 0.08, 0], [0, 0.95, 0.05], [0.1, 0, 0.9]])).T, 0, 1)
    b2a = pipeline(3, 3, [curves_stage([para(0, 1.0)] * 3),
                          lc.cmsStageAllocMatrix(None, 3, 3, (C.c_double * 9)(*mi.reshape(-1)), (C.c_double * 3)(*offi)),
                          curves_stage([para(0, 1 / 1.1), para(0, 1.0), para(0, 1 / 0.9)]),
                          clut_stage(3, 3, g, np.round(inv_tw * 65535).astype(np.uint16)),
                          curves_stage([para(0, 1 / 2.2), para(0, 1 / 2.4), para(0, 0.5)])])
    assert lc.cmsWriteTag(h, sig('B2A0'), b2a)
    return save(h)


def lcms_profiles():
    pr = {}
    pr['lcms_srgb_v4'] = save(lc.cmsCreate_sRGBProfile())
    h = lc.cmsCreate_sRGBProfile()
    lc.cmsSetProfileVersion(h, 2.1)
    pr['lcms_srgb_v2'] = save(h)
    pr['lcms_p3_v4'] = save(rgb_profile(D65, P3_P, [para(3, *SRGB_CURVE)] * 3, desc='P3'))
    pr['lcms_rec2020_v4'] = save(rgb_profile(D65, REC2020_P, [para(3, 1 / 0.45, 1 / 1.099, 0.099 / 1.099, 1 / 4.5, 0.081)] * 3, desc='2020'))
    pr['lcms_a98_v2'] = save(rgb_profile(D65, A98_P, [lc.cmsBuildGamma(None, 563 / 256)] * 3, version=2.1, desc='A98'))
    pr['lcms_romm_v4'] = save(rgb_profile(D50, ROMM_P, [para(3, 1.8, 1, 0, 1 / 16, 1 / 32)] * 3, desc='ROMM'))
    # every parametric type: type 1 (CIE 122), type 2 (IEC 61966-3), type 4 (with offsets)
    pr['lcms_paratypes_v4'] = save(rgb_profile(D65, SRGB_P, [para(1, 2.2, 1.05, -0.05), para(2, 2.0, 0.97, 0.01, 0.01),
                                                             para(4, 2.4, 0.95, 0.05, 1 / 10.0, 0.06, 0.002, 0.001)], desc='para'))
    # tables: an S-shaped and a plain one, written as curv
    # (with a linear share, so that the inverse is unambiguous: flat stretches have no single inverse)
    t1 = [int(round(65535 * (0.85 * (0.5 - 0.5 * np.cos(np.pi * (i / 1023) ** 1.3)) + 0.15 * i / 1023))) for i in range(1024)]
    t2 = [int(round(65535 * (i / 255) ** 2.0)) for i in range(256)]
    pr['lcms_tables_v4'] = save(rgb_profile(D65, P3_P, [table(t1), table(t2), lc.cmsBuildGamma(None, 1.8)], desc='tables'))
    pr['lcms_tables_v2'] = save(rgb_profile(D65, P3_P, [table(t1), table(t2), lc.cmsBuildGamma(None, 1.8)], version=2.1, desc='tables'))
    pr['lcms_gray22_v4'] = save_gray(lc.cmsBuildGamma(None, 2.2), 4.3)
    pr['lcms_graytable_v2'] = save_gray(table(t1), 2.1)
    pr['lcms_cmyk_v4'] = cmyk_profile(4.3, desc='CMYK v4')
    pr['lcms_cmyk_v2'] = cmyk_profile(2.1, desc='CMYK v2')
    pr['lcms_cmyk8_v2'] = cmyk_profile(2.1, eight=True, desc='CMYK v2 lut8')
    pr['lcms_cmykxyz_v4'] = cmyk_profile(4.3, pcs='XYZ', desc='CMYK XYZ v4')
    pr['lcms_rgblut_v4'] = rgb_lut_profile()
    return pr


def save_gray(curve, version):
    h = lc.cmsCreateGrayProfile(C.byref(xyy(*D50)), curve)
    lc.cmsSetProfileVersion(h, version)
    return save(h)


# ---------------------------------------------------------------------------- inputs and conversions

def pattern(n, kind):
    """Grid + random + ramps for n channels, as uint8 / uint16 / float32 (normalised)."""
    rng = np.random.default_rng(100 + n)
    levels = {1: 0, 3: 9, 4: 5}[n]
    parts = []
    if levels:
        parts.append(grid_points(levels, n))
    parts.append(rng.random((200, n)))
    ramp = np.linspace(0, 1, 256)
    parts.append(np.repeat(ramp[:, None], n, 1))
    if n == 4:
        z = np.zeros((256, 4))
        z[:, 3] = ramp
        parts.append(z)
    if n == 1:
        parts.append(np.linspace(0, 1, 1024)[:, None])
    v = np.concatenate(parts, 0)
    if kind == 'u8':
        return np.round(v * 255).astype(np.uint8)
    if kind == 'u16':
        return np.round(v * 65535).astype(np.uint16)
    return (np.round(v * 65535) / 65535).astype(np.float32)


def normalised(a):
    if a.dtype == np.uint8:
        return a.astype(np.float64) / 255
    if a.dtype == np.uint16:
        return a.astype(np.float64) / 65535
    return a.astype(np.float64)


SPACE = {1: 'GRAY', 3: 'RGB', 4: 'CMYK'}


def lcms_convert(src, dst, x, intent, flags, nin, nout, out_space=None):
    """x: normalised float64 (pixels, nin). Returns float64 (pixels, nout); CMYK in 0..1, Lab real."""
    hs, hd = open_mem(src), open_mem(dst)
    ins, outs = SPACE[nin], out_space or SPACE[nout]
    t = lc.cmsCreateTransform(hs, fmt_float(ins, nin, True), hd, fmt_float(outs, nout, True), intent, flags | NOOPTIMIZE)
    if not t:
        raise RuntimeError('cmsCreateTransform failed')
    xin = np.ascontiguousarray(x * (100 if ins == 'CMYK' else 1), dtype=np.float64)
    out = np.zeros((x.shape[0], nout), np.float64)
    lc.cmsDoTransform(t, xin.ctypes.data, out.ctypes.data, x.shape[0])
    lc.cmsDeleteTransform(t)
    lc.cmsCloseProfile(hs)
    lc.cmsCloseProfile(hd)
    if outs == 'CMYK':
        out /= 100
    return out


def lab4():
    return save(lc.cmsCreateLab4Profile(None))


INTENT = {'perceptual': 0, 'relative': 1, 'saturation': 2, 'absolute': 3}


class Writer:
    def __init__(self, root, profiles):
        self.root = root
        self.profiles = profiles  # name -> (bytes, relative path)
        self.cases = []
        self.inputs = set()

    def inp(self, n, kind):
        name = f'in_{n}_{kind}'
        if name not in self.inputs:
            a = pattern(n, kind)
            write_ref(os.path.join(self.root, name + '.ref'), a[None, :, :])
            self.inputs.add(name)
        return name, pattern(n, kind)

    def raw(self, src, tag, nin):
        """The tag's LUT alone (cmsReadTag + cmsPipelineEvalFloat): normalised outputs in the tag's own encoding."""
        iname, a = self.inp(nin, 'f32')
        h = open_mem(self.profiles[src][0])
        p = lc.cmsReadTag(h, sig(tag))
        assert p, tag
        x = normalised(a).astype(np.float32)
        y = np.zeros((x.shape[0], 16), np.float32)
        for i in range(x.shape[0]):
            o = (C.c_float * 16)()
            lc.cmsPipelineEvalFloat(x[i].ctypes.data_as(C.POINTER(C.c_float)), o, p)
            y[i] = np.frombuffer(o, np.float32)
        lc.cmsCloseProfile(h)
        nout = 4 if tag.startswith('B2A') and self.profiles[src][0][16:20] == b'CMYK' else 3
        name = f'{src}__raw_{tag}'
        write_ref(os.path.join(self.root, name + '.ref'), y[None, :, :nout])
        self.cases.append({'name': name, 'src': self.profiles[src][1], 'dst': None, 'intent': tag, 'bpc': False,
                           'input': iname, 'out': 'f32', 'lab': True})

    def case(self, src, dst, intent='relative', bpc=False, kind='u8', out='u8', nin=3, nout=3, lab=False):
        iname, a = self.inp(nin, kind)
        x = normalised(a)
        sb = self.profiles[src][0]
        db = lab4() if lab else self.profiles[dst][0]
        y = lcms_convert(sb, db, x, INTENT[intent], BPC if bpc else 0, nin, 3 if lab else nout, 'Lab' if lab else None)
        name = f'{src}__{"lab" if lab else dst}__{intent}{"_bpc" if bpc else ""}__{kind}_{out}'
        write_ref(os.path.join(self.root, name + '.ref'), y.astype(np.float32)[None, :, :])
        self.cases.append({'name': name, 'src': self.profiles[src][1], 'dst': None if lab else self.profiles[dst][1],
                           'intent': intent, 'bpc': bpc, 'input': iname, 'out': out, 'lab': lab})

    def finish(self):
        with open(os.path.join(self.root, 'cases.json'), 'w') as f:
            json.dump(self.cases, f, indent=1)


def own_profiles():
    d = os.path.join(OUT, 'ours')
    r = {}
    if not os.path.isdir(d):
        print('no tests/fixtures/icc/ours: run the IccTests with PIXIMAGEIO_ICC_DUMP=1 first', file=sys.stderr)
        return r
    for f in sorted(os.listdir(d)):
        if f.endswith('.icc'):
            b = open(os.path.join(d, f), 'rb').read()
            h = open_mem(b)  # LittleCMS must accept it
            idb = (C.c_ubyte * 16)()
            lc.cmsGetHeaderProfileID(h, idb)
            stored = bytes(idb)
            if b[8] >= 4:
                lc.cmsMD5computeID(h)
                lc.cmsGetHeaderProfileID(h, idb)
                assert bytes(idb) == stored, f'{f}: profile ID differs from LittleCMS\'s MD5'
            lc.cmsCloseProfile(h)
            r['ours_' + f[:-4]] = (b, 'ours/' + f)
    return r


def own_cases(w):
    for k, kind in [('u8', 'u8'), ('u16', 'u16'), ('f32', 'f32')]:
        # matrix / TRC both ways, all sample types
        w.case('ours_srgb', 'lcms_p3_v4', kind=k, out=kind)
        w.case('lcms_p3_v4', 'ours_srgb', kind=k, out=kind)
        w.case('lcms_paratypes_v4', 'ours_srgb', kind=k, out=kind)
        w.case('ours_srgb', 'lcms_tables_v2', kind=k, out=kind)
    w.case('lcms_srgb_v4', 'ours_srgb')
    w.case('lcms_srgb_v2', 'ours_srgb', kind='u16', out='u16')
    w.case('lcms_a98_v2', 'ours_srgb', intent='perceptual')
    w.case('lcms_romm_v4', 'ours_srgb', kind='u16', out='u16')
    w.case('lcms_tables_v4', 'lcms_tables_v2')
    w.case('lcms_tables_v4', 'lcms_rec2020_v4', kind='u16', out='u16')
    w.case('lcms_rec2020_v4', 'lcms_gray22_v4', nout=1)
    w.case('lcms_gray22_v4', 'ours_srgb', nin=1)
    w.case('lcms_graytable_v2', 'lcms_gray22_v4', nin=1, nout=1, kind='u16', out='u16')
    w.case('lcms_graytable_v2', 'ours_gray_srgb', nin=1, nout=1)
    w.case('lcms_p3_v4', 'ours_linear_srgb', kind='u8', out='f32')
    w.case('ours_p3', 'lcms_srgb_v4')
    w.case('ours_rec2020', 'lcms_srgb_v4', kind='u16', out='u16')
    w.case('ours_prophoto', 'lcms_srgb_v4', kind='f32', out='f32')
    w.case('ours_a98', 'lcms_srgb_v4')
    w.case('ours_srgb_v2', 'lcms_srgb_v4', kind='u16', out='u16')
    w.case('ours_gray22', 'lcms_srgb_v4', nin=1)
    w.case('lcms_srgb_v4', 'ours_prophoto', kind='u16', out='u16')
    # CMYK tables of each type and intent
    for prof in ['lcms_cmyk_v4', 'lcms_cmyk_v2', 'lcms_cmyk8_v2', 'lcms_cmykxyz_v4']:
        for intent in ['perceptual', 'relative', 'saturation', 'absolute']:
            w.case(prof, 'ours_srgb', intent=intent, nin=4)
        for tag in ['A2B0', 'A2B1', 'A2B2']:
            w.raw(prof, tag, 4)
            w.raw(prof, 'B2A' + tag[3], 3)
        if prof != 'lcms_cmykxyz_v4':  # LittleCMS finds another black for XYZ based CMYK tables
            w.case(prof, 'ours_srgb', intent='relative', bpc=True, nin=4)
        w.case(prof, 'ours_srgb', intent='relative', nin=4, kind='u16', out='u16')
        for intent in ['perceptual', 'relative', 'absolute']:
            w.case('ours_srgb', prof, intent=intent, nout=4)
        if prof != 'lcms_cmykxyz_v4':
            w.case('ours_srgb', prof, intent='relative', bpc=True, nout=4)
        w.case('ours_srgb', prof, intent='relative', nout=4, kind='f32', out='f32')
    w.case('lcms_cmyk_v4', 'lcms_cmyk_v2', intent='relative', nin=4, nout=4)
    w.case('lcms_cmyk_v2', 'lcms_cmykxyz_v4', intent='perceptual', nin=4, nout=4)
    w.case('lcms_cmyk_v2', 'lcms_cmyk_v4', intent='perceptual', nin=4, nout=4)
    w.case('lcms_rgblut_v4', 'ours_srgb')
    w.case('lcms_rgblut_v4', 'ours_srgb', kind='f32', out='f32')
    w.raw('lcms_rgblut_v4', 'A2B0', 3)
    w.raw('lcms_rgblut_v4', 'B2A0', 3)
    w.case('ours_srgb', 'lcms_rgblut_v4')
    w.case('lcms_rgblut_v4', 'lcms_cmyk_v4', intent='perceptual', nout=4)
    # v4 perceptual tables into v2 / v4 profiles (black point scaling only towards v4)
    w.case('lcms_cmyk_v4', 'lcms_srgb_v2', intent='perceptual', nin=4)
    w.case('lcms_cmyk_v4', 'lcms_srgb_v4', intent='saturation', nin=4)
    w.case('lcms_cmyk8_v2', 'lcms_cmyk_v2', intent='perceptual', nin=4, nout=4)


# ---------------------------------------------------------------------------- images with embedded profiles

def image_files(w, profiles):
    rng = np.random.default_rng(7)
    h, wd = 24, 32
    y, x = np.mgrid[0:h, 0:wd]
    rgb = np.stack([x * 255 // (wd - 1), y * 255 // (h - 1), (x + y) * 255 // (wd + h - 2)], -1).astype(np.uint8)
    rgb[::5, ::3] = rng.integers(0, 256, rgb[::5, ::3].shape, dtype=np.uint8)
    cmyk = np.concatenate([rgb, (255 - rgb.max(-1, keepdims=True)) // 2], -1).astype(np.uint8)
    srgb = profiles['ours_srgb'][0]
    out = []

    def ref(name, pix, src, nin, intent=0):
        y2 = lcms_convert(src, srgb, normalised(pix.reshape(-1, nin)), intent, 0, nin, 3)
        write_ref(os.path.join(OUT, name + '.srgb.ref'), y2.reshape(pix.shape[0], pix.shape[1], 3).astype(np.float32))

    a98 = profiles['lcms_a98_v2'][0]
    Image.fromarray(rgb, 'RGB').save(os.path.join(OUT, 'img_a98.png'), icc_profile=a98)
    ref('img_a98.png', rgb, a98, 3)
    p3 = profiles['lcms_p3_v4'][0]
    Image.fromarray(rgb, 'RGB').save(os.path.join(OUT, 'img_p3.webp'), lossless=True, icc_profile=p3)
    ref('img_p3.webp', rgb, p3, 3)
    cm = profiles['lcms_cmyk_v4'][0]
    Image.fromarray(cmyk, 'CMYK').save(os.path.join(OUT, 'img_cmyk.tif'), icc_profile=cm)
    ref('img_cmyk.tif', cmyk, cm, 4)
    Image.fromarray(cmyk, 'CMYK').save(os.path.join(OUT, 'img_cmyk.jpg'), quality=95, icc_profile=cm)
    dec = np.asarray(Image.open(os.path.join(OUT, 'img_cmyk.jpg')))  # libjpeg's decode, as plain CMYK
    ref('img_cmyk.jpg', dec, cm, 4)
    Image.fromarray(rgb, 'RGB').save(os.path.join(OUT, 'img_a98.jpg'), quality=95, icc_profile=a98)
    dec = np.asarray(Image.open(os.path.join(OUT, 'img_a98.jpg')).convert('RGB'))
    ref('img_a98.jpg', dec, a98, 3)
    return out


# ---------------------------------------------------------------------------- third-party profiles

ICC_TERMS = """sRGB_v4_ICC_preference.icc - terms of use as published by the International Color Consortium
(https://www.color.org/srgbprofiles.xalter):

To anyone who acknowledges that the file "sRGB_v4_ICC_preference.icc" is provided "AS IS" WITH NO EXPRESS OR IMPLIED
WARRANTY, permission to use, copy and distribute this file for any purpose is hereby granted without fee, provided that
the file is not changed including the ICC copyright notice tag, and that the name of ICC shall not be used in advertising
or publicity pertaining to distribution of the software without specific, written prior permission. ICC makes no
representations about the suitability of this software for any purpose.

The file here is unchanged (SHA-256 83174717332326ddc198d9df188a4daec27b8979ba152cebbfc470c793d0bb11).
"""

TESTDATA_README = """# ICC profiles

Third-party ICC profiles for Aardvark.PixImage.IO's colour management tests (`IccTests.ThirdPartyMatchesLittleCms`), copied here
unchanged by Aardvark.PixImage.IO's `tests/oracle/gen_icc.py`, with LittleCMS 2 reference conversions of a test pattern through them
(`in_*.ref` inputs, `<case>.ref` outputs, `cases.json`).

| Files | Source | Licence |
| --- | --- | --- |
| `AdobeCompat-*.icc`, `CGATS001Compat-v2-micro.icc`, `DisplayP3*.icc`, `ProPhoto-v2-micro.icc`, `Rec2020*.icc`, `scRGB-v2.icc`, `sGrey-*.icc`, `sRGB-v*.icc`, `WideGamutCompat-v4.icc` | Compact-ICC-Profiles by Clinton Ingram, https://github.com/saucecontrol/Compact-ICC-Profiles | CC0 1.0, see `LICENSE.Compact-ICC-Profiles` |
| `sRGB_v4_ICC_preference.icc` | International Color Consortium, https://www.color.org/srgbprofiles.xalter (also in https://github.com/InternationalColorConsortium/DemoIccMAX, `Testing/`) | ICC terms in `LICENSE.ICC-profiles.txt`: copy and distribute unchanged |
"""

COMPACT = ['AdobeCompat-v2.icc', 'AdobeCompat-v4.icc', 'CGATS001Compat-v2-micro.icc', 'DisplayP3Compat-v2-micro.icc',
           'DisplayP3-v4.icc', 'ProPhoto-v2-micro.icc', 'Rec2020-v4.icc', 'Rec2020-g24-v4.icc', 'scRGB-v2.icc',
           'sGrey-v2-nano.icc', 'sGrey-v4.icc', 'sRGB-v2-nano.icc', 'sRGB-v2-magic.icc', 'sRGB-v4.icc', 'WideGamutCompat-v4.icc']


def third_party(args, ours):
    root = testdata_dir('fixtures', 'icc')
    if root is None:
        return
    os.makedirs(root, exist_ok=True)
    prof = dict(ours)
    if args.compact:
        for f in COMPACT:
            shutil.copy(os.path.join(args.compact, 'profiles', f), os.path.join(root, f))
        shutil.copy(os.path.join(args.compact, 'license'), os.path.join(root, 'LICENSE.Compact-ICC-Profiles'))
    if args.icc_srgb_v4:
        shutil.copy(args.icc_srgb_v4, os.path.join(root, 'sRGB_v4_ICC_preference.icc'))
        with open(os.path.join(root, 'LICENSE.ICC-profiles.txt'), 'w') as f:
            f.write(ICC_TERMS)
    if args.compact or args.icc_srgb_v4:
        with open(os.path.join(root, 'README.md'), 'w') as f:
            f.write(TESTDATA_README)
    for f in sorted(os.listdir(root)):
        if f.endswith('.icc'):
            prof['tp_' + f[:-4]] = (open(os.path.join(root, f), 'rb').read(), 'testdata:' + f)
    # 'ours/..' paths stay relative to tests/fixtures/icc
    w = Writer(root, {k: (v[0], v[1] if v[1].startswith('testdata:') else 'pixcodec:' + v[1]) for k, v in prof.items()})
    for k in sorted(prof):
        if not k.startswith('tp_'):
            continue
        b = prof[k][0]
        space = b[16:20].decode('latin1')
        n = {'GRAY': 1, 'RGB ': 3, 'CMYK': 4}[space]
        dst = 'ours_gray_srgb' if n == 1 else 'ours_srgb'
        w.case(k, dst, intent='perceptual', nin=n, nout=1 if n == 1 else 3)
        w.case(k, dst, intent='relative', nin=n, nout=1 if n == 1 else 3, kind='u16', out='u16')
        if n != 4:
            w.case(dst, k, intent='perceptual', nin=1 if n == 1 else 3, nout=n)
    for tag in ['A2B0', 'A2B1', 'B2A0', 'B2A1']:
        w.raw('tp_sRGB_v4_ICC_preference', tag, 3)
    w.case('tp_sRGB_v4_ICC_preference', 'ours_srgb', intent='perceptual', kind='f32', out='f32')
    w.case('tp_sRGB_v4_ICC_preference', 'ours_srgb', intent='saturation')
    w.raw('tp_CGATS001Compat-v2-micro', 'A2B0', 4)
    w.case('tp_CGATS001Compat-v2-micro', 'ours_srgb', intent='relative', bpc=True, nin=4)
    w.case('tp_CGATS001Compat-v2-micro', 'tp_sRGB_v4_ICC_preference', intent='perceptual', nin=4)
    w.case('tp_sRGB_v4_ICC_preference', 'tp_sRGB-v2-nano', intent='perceptual')
    w.case('tp_sRGB_v4_ICC_preference', 'tp_AdobeCompat-v4', intent='perceptual', kind='u16', out='u16')
    w.finish()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--compact', help='Compact-ICC-Profiles checkout')
    ap.add_argument('--icc-srgb-v4', help="ICC's sRGB_v4_ICC_preference.icc")
    args = ap.parse_args()
    os.makedirs(os.path.join(OUT, 'profiles'), exist_ok=True)
    profiles = {}
    for k, b in lcms_profiles().items():
        with open(os.path.join(OUT, 'profiles', k + '.icc'), 'wb') as f:
            f.write(b)
        profiles[k] = (b, 'profiles/' + k + '.icc')
    ours = own_profiles()
    profiles.update(ours)
    if 'ours_srgb' not in profiles:
        sys.exit('own profiles missing')
    w = Writer(OUT, profiles)
    own_cases(w)
    w.finish()
    image_files(w, profiles)
    third_party(args, ours)


if __name__ == '__main__':
    main()
