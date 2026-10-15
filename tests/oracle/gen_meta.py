"""Metadata fixtures: small images with EXIF / XMP / IPTC written by ExifTool, and ExifTool's reading of them.

usage: gen_meta.py [--exiftool PATH] [--samples DIR]

Own fixtures (tests/fixtures/metadata): images generated here with a fixed set of camera, GPS, DJI drone, description and
IPTC tags, in every container that can carry them. ExifTool (GPL / Artistic, only run, never linked) writes the
metadata and records its reading as <file>.exiftool.json (-j -n -G1 -a).

--samples: also copies ExifTool's own sample images of the supported formats (t/images, distributed under the terms of
Perl) into the separate test data repository (fixtures/metadata/exiftool) with their reference JSON.
"""
import argparse
import json
import os
import shutil
import subprocess
import sys

import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, '..', 'fixtures', 'metadata')
TESTDATA = os.path.join(HERE, '..', '..', '..', 'Aardvark.PixImage.IO.TestData', 'fixtures', 'metadata', 'exiftool')

TAGS = [
    '-Make=DJI', '-Model=FC6310R', '-Software=v01.20.5021', '-SerialNumber=0K8TF7R0012345', '-LensMake=DJI',
    '-LensModel=24mm F2.8', '-OwnerName=Survey Team',
    '-DateTimeOriginal=2024:06:15 14:23:45', '-SubSecTimeOriginal=123', '-OffsetTimeOriginal=+02:00',
    '-CreateDate=2024:06:15 14:23:45', '-ModifyDate=2024:06:16 09:00:00',
    '-ExposureTime=1/500', '-FNumber=5.6', '-ISO=100', '-FocalLength=8.8', '-FocalLengthIn35mmFormat=24',
    '-ExposureCompensation=-0.7', '-ExifIFD:Flash#=16', '-ExposureProgram#=2', '-MeteringMode#=2', '-WhiteBalance#=0',
    '-Orientation#=1', '-ImageDescription=Bridge inspection, pier 3', '-Artist=Jane Pilot', '-Copyright=(c) 2024 Example',
    '-UserComment=flight 17 / grid A', '-ImageUniqueID=0123456789abcdef0123456789abcdef',
    '-GPSVersionID=2.3.0.0', '-GPSLatitude=47.0707123', '-GPSLatitudeRef=N', '-GPSLongitude=-15.4395456', '-GPSLongitudeRef=W',
    '-GPSAltitude=412.35', '-GPSAltitudeRef#=0', '-GPSDateStamp=2024:06:15', '-GPSTimeStamp=12:23:45.5',
    '-GPSImgDirection=123.4', '-GPSImgDirectionRef#=T', '-GPSSpeed=36', '-GPSSpeedRef#=K', '-GPSMapDatum=WGS-84',
    '-GPSHPositioningError=0.8', '-GPSDOP=1.2',
    '-XMP-drone-dji:AbsoluteAltitude=+412.35', '-XMP-drone-dji:RelativeAltitude=+80.10',
    '-XMP-drone-dji:GimbalYawDegree=-45.30', '-XMP-drone-dji:GimbalPitchDegree=-90.00', '-XMP-drone-dji:GimbalRollDegree=+0.00',
    '-XMP-drone-dji:FlightYawDegree=-44.90', '-XMP-drone-dji:FlightPitchDegree=+2.10', '-XMP-drone-dji:FlightRollDegree=-1.30',
    '-XMP-drone-dji:FlightXSpeed=+3.2', '-XMP-drone-dji:FlightYSpeed=-0.4', '-XMP-drone-dji:FlightZSpeed=+0.0',
    '-XMP-drone-dji:RtkFlag=50', '-XMP-drone-dji:RtkStdLat=0.012', '-XMP-drone-dji:RtkStdLon=0.011', '-XMP-drone-dji:RtkStdHgt=0.025',
    '-XMP-drone-dji:CalibratedFocalLength=3666.666504', '-XMP-drone-dji:CalibratedOpticalCenterX=2736.0',
    '-XMP-drone-dji:CalibratedOpticalCenterY=1824.0',
    '-XMP-dc:Title=Pier 3 north face', '-XMP-dc:Subject=bridge', '-XMP-dc:Subject=inspection', '-XMP-dc:Subject=Grüße',
    '-XMP-xmp:Rating=4', '-XMP-photoshop:City=Graz', '-XMP-photoshop:State=Styria', '-XMP-photoshop:Country=Austria',
    '-IPTC:Keywords=bridge', '-IPTC:Keywords=concrete', '-IPTC:City=Graz', '-IPTC:Sub-location=Pier 3',
    '-IPTC:CodedCharacterSet=UTF8',
]

# what each container can hold (ExifTool writes EXIF / XMP / IPTC where the format has a place for it)
FORMATS = {
    'meta.jpg': ('JPEG', {}),
    'meta.png': ('PNG', {}),
    'meta.tif': ('TIFF', {'compression': 'tiff_deflate'}),
    'meta.webp': ('WEBP', {'lossless': True}),
    'meta.gif': ('GIF', {}),
    'meta.jp2': ('JPEG2000', {}),
}


def exiftool_path(arg):
    for p in (arg, os.environ.get('EXIFTOOL'), os.path.expanduser('~/.local/share/exiftool/exiftool'), shutil.which('exiftool')):
        if p and os.path.exists(p):
            return p
    sys.exit('ExifTool not found (--exiftool, $EXIFTOOL or ~/.local/share/exiftool/exiftool)')


def reference(et, path):
    out = subprocess.run([et, '-j', '-n', '-G1', '-a', '-struct', '-charset', 'utf8', path], capture_output=True, check=True).stdout
    data = json.loads(out)[0]
    for k in ('SourceFile', 'System:Directory', 'System:FileName', 'System:FileModifyDate', 'System:FileAccessDate',
              'System:FileInodeChangeDate', 'System:FilePermissions', 'ExifTool:ExifToolVersion'):
        data.pop(k, None)
    with open(path + '.exiftool.json', 'w', encoding='utf-8') as f:
        json.dump(data, f, indent=1, ensure_ascii=False, sort_keys=True)


def own(et):
    os.makedirs(OUT, exist_ok=True)
    y, x = np.mgrid[0:24, 0:32]
    img = np.stack([x * 8, y * 10, (x + y) * 4], -1).astype(np.uint8)
    for name, (fmt, kw) in FORMATS.items():
        path = os.path.join(OUT, name)
        im = Image.fromarray(img)
        if fmt == 'GIF':
            im = im.convert('P')
        im.save(path, fmt, **kw)
        r = subprocess.run([et, '-overwrite_original', '-q', '-q', *TAGS, path], capture_output=True, text=True)
        if r.returncode != 0:
            print(name, r.stderr.strip()[:300])
        reference(et, path)
        print('wrote', name)


SAMPLE_EXT = ('.jpg', '.tif', '.png', '.webp', '.gif', '.jp2', '.exr', '.dng', '.btf')


def samples(et, src):
    os.makedirs(TESTDATA, exist_ok=True)
    n = 0
    for f in sorted(os.listdir(src)):
        if not f.lower().endswith(SAMPLE_EXT):
            continue
        dst = os.path.join(TESTDATA, f)
        shutil.copyfile(os.path.join(src, f), dst)
        reference(et, dst)
        n += 1
    with open(os.path.join(TESTDATA, 'README.md'), 'w') as fh:
        fh.write('Sample images of ExifTool 13.59 (t/images, https://exiftool.org), copyright Phil Harvey, distributed under\n'
                 'the same terms as Perl itself (GPL or Artistic License). Used only as test input; *.exiftool.json is\n'
                 'ExifTool\'s reading of each file (-j -n -G1 -a -struct).\n')
    print('copied', n, 'samples')


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--exiftool')
    ap.add_argument('--samples')
    a = ap.parse_args()
    et = exiftool_path(a.exiftool)
    own(et)
    if a.samples:
        samples(et, a.samples)


if __name__ == '__main__':
    main()
