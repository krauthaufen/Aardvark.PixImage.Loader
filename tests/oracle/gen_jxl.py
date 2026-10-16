"""Generates tests/fixtures/jxl: name.jxl plus reference decodes by libjxl.

  * name.jxl.ref: for lossless files what Jxl.Decode returns (libjxl's integer / float decode through imagecodecs,
    exact); for lossy files libjxl's float32 decode (djxl to .npy), which the tests compare with a tolerance against
    a Float32 decode (the integer output of libjxl is dithered, so it is not a reference).
  * name.jxl.N.ref: the same for frame N of animations.
  * name.jxl.json: what the tests need to know (lossless or lossy, frame count, expected metadata).

Synthetic content only. Encoders: imagecodecs.jpegxl_encode (libjxl) and, for what imagecodecs cannot ask for
(progressive passes, LF frames, patches, noise, splines are not reachable at all, upsampling, predictors, palettes,
premultiplied alpha, animation, JPEG recompression, metadata boxes), libjxl's cjxl / djxl from PATH or from
.tmp/libjxl/tools (the static release of libjxl 0.12, run as an oracle). Brotli streams for the Brotli tests are
written with imagecodecs.brotli_encode into tests/fixtures/compression/brotli.

usage: python3 gen_jxl.py
"""
import io
import json
import os
import shlex
import shutil
import subprocess
import sys
import numpy as np
import imagecodecs
from PIL import Image, ImageDraw
from pxref import write_ref, test_pattern

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, '..', 'fixtures', 'jxl')
TOOLS = os.path.join(HERE, '..', '..', '.tmp', 'libjxl', 'tools')
TMP = os.path.join(OUT, '_tmp')


def tool(name):
    p = shutil.which(name)
    if p: return p
    p = os.path.join(TOOLS, name)
    return p if os.path.exists(p) else None


CJXL, DJXL = tool('cjxl'), tool('djxl')
if not CJXL or not DJXL:
    sys.exit('cjxl / djxl not found (PATH or .tmp/libjxl/tools)')


def save_png(path, a, **kw):
    if a.ndim == 3 and a.shape[2] == 1: a = a[..., 0]
    Image.fromarray(a).save(path, **kw)


def save_pnm16(path, a):
    h, w = a.shape[:2]
    c = 1 if a.ndim == 2 else a.shape[2]
    with open(path, 'wb') as f:
        f.write(b'P5' if c == 1 else b'P6')
        f.write(f'\n{w} {h}\n65535\n'.encode())
        f.write(a.astype('>u2').tobytes())


def cjxl(src, dst, args):
    r = subprocess.run([CJXL, src, dst, *shlex.split(args), '--quiet'], capture_output=True, text=True)
    if r.returncode: raise SystemExit(f'cjxl {dst}: {r.stderr}')


def djxl_float(path):
    out = os.path.join(TMP, 'ref.npy')
    subprocess.run([DJXL, path, out, '--quiet'], check=True, capture_output=True)
    return np.load(out)   # frames x h x w x c float32


def finish(name, lossless, frames=1, extra=None):
    path = os.path.join(OUT, name)
    data = open(path, 'rb').read()
    info = {'lossless': lossless, 'frames': frames}
    if lossless:
        ref = imagecodecs.jpegxl_decode(data)
        if frames > 1 or ref.ndim == 4:
            ref = ref if ref.ndim == 4 else ref[None]
            for k in range(ref.shape[0]): write_ref(f'{path}.{k}.ref', ref[k])
            info['frames'] = int(ref.shape[0])
            write_ref(path + '.ref', ref[0])
        else:
            write_ref(path + '.ref', ref)
    else:
        ref = djxl_float(path)
        for k in range(ref.shape[0]):
            if ref.shape[0] > 1: write_ref(f'{path}.{k}.ref', ref[k])
        write_ref(path + '.ref', ref[0])
        info['frames'] = int(ref.shape[0])
    if extra: info.update(extra)
    json.dump(info, open(path + '.json', 'w'), indent=1, sort_keys=True)


def photo(w, h, c=3, bits=8, seed=3):
    """Smooth gradients with texture and a few edges, like a small photograph."""
    rng = np.random.default_rng(seed)
    y, x = np.mgrid[0:h, 0:w].astype(np.float64)
    base = [0.5 + 0.35 * np.sin(x / 13.0 + y / 29.0), 0.5 + 0.3 * np.cos(x / 9.0 - y / 17.0), 0.45 + 0.4 * np.sin((x + 2 * y) / 31.0),
            np.clip(0.2 + (x / w) * 0.9, 0, 1)]
    a = np.stack(base[:c], -1) + rng.normal(0, 0.02, (h, w, c))
    a[h // 3:h // 3 + h // 5, w // 4:w // 2, :min(c, 3)] = 0.9
    a = np.clip(a, 0, 1)
    if bits == 16: return np.round(a * 65535).astype(np.uint16)
    if bits == 32: return a.astype(np.float32)
    return np.round(a * 255).astype(np.uint8)


def text_image(w, h, alpha=False):
    im = Image.new('RGBA' if alpha else 'RGB', (w, h), (0, 0, 0, 0) if alpha else (250, 248, 240))
    d = ImageDraw.Draw(im)
    for row in range(h // 14):
        d.text((4, 2 + row * 14), 'patches repeat: abc abc abc' if row % 2 else 'jxl jxl jxl jxl jxl jxl', fill=(30, 30, 90, 255))
    return np.array(im)


def main():
    os.makedirs(OUT, exist_ok=True)
    os.makedirs(TMP, exist_ok=True)
    for f in os.listdir(OUT):
        if f.endswith(('.jxl', '.ref', '.json')): os.remove(os.path.join(OUT, f))
    t = lambda n: os.path.join(TMP, n)
    o = lambda n: os.path.join(OUT, n)

    rgb = photo(96, 64); save_png(t('rgb.png'), rgb)
    rgba = photo(96, 64, 4); save_png(t('rgba.png'), rgba)
    gray = photo(80, 56, 1)[..., 0]; save_png(t('gray.png'), gray)
    odd = photo(131, 47); save_png(t('odd.png'), odd)
    wide = photo(264, 36); save_png(t('wide.png'), wide)
    small = photo(20, 16); save_png(t('small.png'), small)
    rgb16 = photo(72, 48, 3, 16); save_pnm16(t('rgb16.ppm'), rgb16)
    gray16 = photo(64, 40, 1, 16)[..., 0]; save_pnm16(t('gray16.pgm'), gray16)
    pal = np.array([[200, 30, 40], [20, 180, 60], [240, 240, 240], [10, 10, 10], [90, 60, 200]], np.uint8)
    yy, xx = np.mgrid[0:60, 0:90]
    palimg = pal[((xx // 7) + (yy // 5)) % 5]; save_png(t('pal.png'), palimg)
    txt = text_image(128, 84); save_png(t('text.png'), txt)
    txta = text_image(120, 84, True); save_png(t('texta.png'), txta)

    # ---- lossless, through imagecodecs
    for e in (1, 2, 3, 7, 9):
        open(o(f'll_rgb_e{e}.jxl'), 'wb').write(imagecodecs.jpegxl_encode(rgb, lossless=True, effort=e)); finish(f'll_rgb_e{e}.jxl', True)
    open(o('ll_rgba.jxl'), 'wb').write(imagecodecs.jpegxl_encode(rgba, lossless=True)); finish('ll_rgba.jxl', True)
    open(o('ll_gray.jxl'), 'wb').write(imagecodecs.jpegxl_encode(gray, lossless=True)); finish('ll_gray.jxl', True)
    open(o('ll_gray_alpha.jxl'), 'wb').write(imagecodecs.jpegxl_encode(np.stack([gray, gray[::-1]], -1), lossless=True)); finish('ll_gray_alpha.jxl', True)
    open(o('ll_rgb16.jxl'), 'wb').write(imagecodecs.jpegxl_encode(rgb16, lossless=True)); finish('ll_rgb16.jxl', True)
    open(o('ll_gray16.jxl'), 'wb').write(imagecodecs.jpegxl_encode(gray16, lossless=True)); finish('ll_gray16.jxl', True)
    open(o('ll_10bit.jxl'), 'wb').write(imagecodecs.jpegxl_encode((rgb16 >> 6).astype(np.uint16), lossless=True, bitspersample=10)); finish('ll_10bit.jxl', True)
    f32 = (photo(48, 40, 3, 32) * 2.5 - 0.5).astype(np.float32)
    open(o('ll_float32.jxl'), 'wb').write(imagecodecs.jpegxl_encode(f32, lossless=True)); finish('ll_float32.jxl', True)
    open(o('ll_float16.jxl'), 'wb').write(imagecodecs.jpegxl_encode(f32.astype(np.float16), lossless=True)); finish('ll_float16.jxl', True)
    open(o('ll_odd.jxl'), 'wb').write(imagecodecs.jpegxl_encode(odd, lossless=True, effort=5)); finish('ll_odd.jxl', True)
    open(o('ll_container.jxl'), 'wb').write(imagecodecs.jpegxl_encode(rgb, lossless=True, usecontainer=True)); finish('ll_container.jxl', True)

    # ---- lossless, through cjxl: tools the API does not reach
    for name, src, args in [
        ('ll_groups128.jxl', 'wide.png', '-d 0 -e 2 -g 0'), ('ll_groups1024.jxl', 'wide.png', '-d 0 -e 2 -g 3'),
        ('ll_rct_0.jxl', 'rgb.png', '-d 0 -e 3 -C 0'), ('ll_rct_6.jxl', 'rgb.png', '-d 0 -e 3 -C 6'),
        ('ll_rct_19.jxl', 'rgb.png', '-d 0 -e 3 -C 19'), ('ll_rct_41.jxl', 'rgb.png', '-d 0 -e 3 -C 41'),
        ('ll_pred_avg.jxl', 'rgb.png', '-d 0 -e 5 -P 3 -I 0'), ('ll_pred_select.jxl', 'rgb.png', '-d 0 -e 5 -P 4'),
        ('ll_pred_gradient.jxl', 'rgb.png', '-d 0 -e 5 -P 5'), ('ll_pred_weighted.jxl', 'rgb.png', '-d 0 -e 5 -P 6 -I 0'),
        ('ll_pred_toptop.jxl', 'rgb.png', '-d 0 -e 5 -P 13'), ('ll_pred_mix.jxl', 'rgb.png', '-d 0 -e 7 -P 15'),
        ('ll_prevchannels.jxl', 'rgb.png', '-d 0 -e 9 -E 3'), ('ll_effort10.jxl', 'odd.png', '-d 0 -e 10 --allow_expert_options'),
        ('ll_palette.jxl', 'pal.png', '-d 0 -e 7'), ('ll_channel_palette.jxl', 'rgb.png', '-d 0 -e 7 -X 90 -Y 90'),
        ('ll_delta_palette.jxl', 'rgb.png', '-m 1 -d 0 -e 7 --modular_lossy_palette --modular_palette_colors=0'),
        ('ll_squeeze.jxl', 'odd.png', '-d 0 -e 7 -R 1'), ('ll_progressive.jxl', 'rgb.png', '-d 0 -p'),
        ('ll_patches.jxl', 'text.png', '-d 0 -e 7 --patches=1'), ('ll_patches_alpha.jxl', 'texta.png', '-d 0 -e 7 --patches=1'),
        ('ll_premultiplied.jxl', 'rgba.png', '-d 0 --premultiply=1'),
        ('ll_upsampled2.jxl', 'small.png', '-d 0 --resampling=2 --already_downsampled'),
        ('ll_upsampled8.jxl', 'small.png', '-d 0 --resampling=8 --already_downsampled'),
        ('ll_epf.jxl', 'odd.png', '-d 0 -m 1 --epf=3'), ('ll_gaborish.jxl', 'odd.png', '-d 0 -m 1 --gaborish=1'),
    ]:
        cjxl(t(src), o(name), args)
        if name == 'll_premultiplied.jxl':
            # libjxl returns premultiplied colour; Jxl.Decode returns straight alpha: store the float decode
            finish(name, False, extra={'premultiplied': True})
        elif name in ('ll_upsampled2.jxl', 'll_upsampled8.jxl', 'll_epf.jxl', 'll_gaborish.jxl'):
            finish(name, False)   # filters / upsampling: compare floats
        else:
            finish(name, True)

    # ---- ICC profile and metadata boxes
    icc_a98 = open(os.path.join(HERE, '..', 'fixtures', 'icc', 'ours', 'a98.icc'), 'rb').read()
    Image.fromarray(rgb).save(t('a98.png'), icc_profile=icc_a98)
    cjxl(t('a98.png'), o('ll_icc.jxl'), '-d 0'); finish('ll_icc.jxl', True, extra={'icc': 'a98.icc'})
    ex = Image.Exif()
    ex[0x010F] = 'JxlMake'; ex[0x0110] = 'JxlModel'; ex[0x0132] = '2024:05:06 07:08:09'
    ex[0x8825] = {1: 'N', 2: (48.0, 12.0, 30.5), 3: 'E', 4: (16.0, 22.0, 10.25)}
    exif = ex.tobytes()
    if exif.startswith(b'Exif\0\0'): exif = exif[6:]
    open(t('exif.bin'), 'wb').write(exif)
    xmp = (b'<?xpacket begin="\xef\xbb\xbf" id="W5M0MpCehiHzreSzNTczkc9d"?><x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF '
           b'xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"><rdf:Description rdf:about="" xmlns:dc="http://purl.org/dc/elements/1.1/">'
           b'<dc:title><rdf:Alt><rdf:li xml:lang="x-default">JXL fixture</rdf:li></rdf:Alt></dc:title></rdf:Description></rdf:RDF></x:xmpmeta>'
           b'<?xpacket end="w"?>')
    open(t('xmp.bin'), 'wb').write(xmp)
    meta = {'make': 'JxlMake', 'model': 'JxlModel', 'lat': 48 + 12 / 60 + 30.5 / 3600, 'lon': 16 + 22 / 60 + 10.25 / 3600, 'title': 'JXL fixture'}
    exif_args = f'-x exif={t("exif.bin")} -x xmp={t("xmp.bin")}'
    cjxl(t('rgb.png'), o('meta_brob.jxl'), f'-d 1 {exif_args} --compress_boxes=1'); finish('meta_brob.jxl', False, extra={'meta': meta})
    cjxl(t('rgb.png'), o('meta_plain.jxl'), f'-d 0 {exif_args} --compress_boxes=0'); finish('meta_plain.jxl', True, extra={'meta': meta})
    # orientation from a JPEG's EXIF, kept in the codestream header
    for orient in (3, 6, 8):
        ex2 = Image.Exif(); ex2[0x0112] = orient
        Image.fromarray(odd).save(t(f'o{orient}.jpg'), exif=ex2.tobytes(), quality=95)
        cjxl(t(f'o{orient}.jpg'), o(f'orient{orient}.jxl'), '-j 0 -d 0'); finish(f'orient{orient}.jxl', True, extra={'orientation': orient})

    # ---- lossy
    for name, src, args in [
        ('vd_e1.jxl', 'rgb.png', '-d 1 -e 1'), ('vd_e3.jxl', 'rgb.png', '-d 1 -e 3'), ('vd_e5.jxl', 'odd.png', '-d 1 -e 5'),
        ('vd_e7.jxl', 'wide.png', '-d 1 -e 7'), ('vd_e9_d05.jxl', 'rgb.png', '-d 0.5 -e 9'), ('vd_d3.jxl', 'wide.png', '-d 3'),
        ('vd_d6.jxl', 'wide.png', '-d 6 -e 7'), ('vd_alpha.jxl', 'rgba.png', '-d 1'), ('vd_alpha_lossy.jxl', 'rgba.png', '-d 2 -a 1'),
        ('vd_premultiplied.jxl', 'rgba.png', '-d 1 --premultiply=1'), ('vd_gray.jxl', 'gray.png', '-d 1'),
        ('vd_16bit.jxl', 'rgb16.ppm', '-d 1'), ('vd_noepf_nogab.jxl', 'rgb.png', '-d 2 --epf=0 --gaborish=0'),
        ('vd_epf1.jxl', 'rgb.png', '-d 2 --epf=1 --gaborish=0'), ('vd_epf3.jxl', 'rgb.png', '-d 3 --epf=3'),
        ('vd_progressive.jxl', 'wide.png', '-d 1 -p'), ('vd_progressive_dc2.jxl', 'wide.png', '-d 1 --progressive_dc=2'),
        ('vd_qprogressive_ac.jxl', 'rgb.png', '-d 1 --qprogressive_ac'), ('vd_group_order.jxl', 'wide.png', '-d 1 --group_order=1'),
        ('vd_resampling2.jxl', 'wide.png', '-d 2 --resampling=2'), ('vd_resampling4.jxl', 'wide.png', '-d 2 --resampling=4'),
        ('vd_resampling8.jxl', 'wide.png', '-d 2 --resampling=8'), ('vd_ec_resampling.jxl', 'rgba.png', '-d 1 --ec_resampling=2'),
        ('vd_patches.jxl', 'text.png', '-d 1 -e 7 --patches=1'), ('vd_patches_alpha.jxl', 'texta.png', '-d 1 -e 7 --patches=1'),
        ('vd_identity.jxl', 'text.png', '-d 1 -e 8 --patches=0'),
        ('mod_lossy.jxl', 'rgb.png', '-m 1 -d 1'), ('mod_lossy_d3.jxl', 'odd.png', '-m 1 -d 3'), ('mod_lossy_epf.jxl', 'rgb.png', '-m 1 -d 2 --epf=2'),
        ('vd_icc.jxl', 'a98.png', '-d 1'),
    ]:
        cjxl(t(src), o(name), args)
        finish(name, False, extra={'premultiplied': True} if 'premultiplied' in name else None)
    f01 = np.clip(f32, 0, 1)
    open(o('vd_float.jxl'), 'wb').write(imagecodecs.jpegxl_encode(f01, distance=1)); finish('vd_float.jxl', False)
    # noise: the generated grain is not libjxl's pseudo random sequence; the tests check its amplitude only
    cjxl(t('rgb.png'), o('vd_noise.jxl'), '-d 1 --photon_noise_iso=3200'); finish('vd_noise.jxl', False, extra={'noise': True})

    # ---- JPEG recompression (VarDCT in YCbCr, chroma subsampling)
    for name, kw in [('jpeg420', {'subsampling': 2}), ('jpeg422', {'subsampling': 1}), ('jpeg444', {'subsampling': 0}), ('jpeg_progressive', {'subsampling': 2, 'progressive': True})]:
        Image.fromarray(odd).save(t(name + '.jpg'), quality=85, **kw)
        cjxl(t(name + '.jpg'), o(name + '.jxl'), ''); finish(name + '.jxl', False)
    Image.fromarray(gray).save(t('jpeg_gray.jpg'), quality=85)
    cjxl(t('jpeg_gray.jpg'), o('jpeg_gray.jxl'), ''); finish('jpeg_gray.jxl', False)

    # ---- animation
    W, H = 64, 48
    frames = []
    for k in range(5):
        f = photo(W, H, 4, seed=10 + k)
        f[..., 3] = 255
        f[8 + 4 * k:20 + 4 * k, 6 + 9 * k:20 + 9 * k, :3] = [250, 40 * k, 200]
        if k % 2: f[30:44, 20:50, 3] = 100
        frames.append(Image.fromarray(f))
    frames[0].save(t('anim.png'), save_all=True, append_images=frames[1:], duration=[80, 120, 40, 200, 60], loop=0)
    cjxl(t('anim.png'), o('anim_lossless.jxl'), '-d 0'); finish('anim_lossless.jxl', True, frames=5, extra={'durations_ms': [80, 120, 40, 200, 60]})
    cjxl(t('anim.png'), o('anim_lossy.jxl'), '-d 1'); finish('anim_lossy.jxl', False, frames=5)

    # ---- Brotli streams
    bout = os.path.join(HERE, '..', 'fixtures', 'compression', 'brotli')
    os.makedirs(bout, exist_ok=True)
    rng = np.random.default_rng(5)
    words = b' '.join(rng.choice([b'the', b'image', b'decoder', b'brotli', b'JPEG', b'metadata', b'<rdf:li>', b'</rdf:li>', b'xmlns'], 4000))
    samples = {'text': words, 'binary': rng.integers(0, 256, 20000, dtype=np.uint8).tobytes(), 'runs': b'ab' * 30000 + b'c' * 9000,
               'empty': b'', 'xmp': xmp * 20}
    for name, data in samples.items():
        for q, lg in [(0, 16), (5, 18), (11, 22), (11, 10)]:
            enc = imagecodecs.brotli_encode(data, level=q, lgwin=lg, mode=1 if name in ('text', 'xmp') else 0)
            open(os.path.join(bout, f'{name}_q{q}_w{lg}.br'), 'wb').write(enc)
        open(os.path.join(bout, f'{name}.bin'), 'wb').write(data)

    shutil.rmtree(TMP)
    n = len([f for f in os.listdir(OUT) if f.endswith('.jxl')])
    print(f'{n} JPEG XL fixtures in {OUT}')


if __name__ == '__main__':
    main()
