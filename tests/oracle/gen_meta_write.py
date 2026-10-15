"""Metadata writing fixtures: golden outputs of ImageCodec.WriteMetadata and ExifTool's reading of them.

usage: gen_meta_write.py [--exiftool PATH] [--no-build]

1. Runs the MetadataWriteTests.Golden cases with PIXCODEC_WRITE_GOLDEN=1: they store their plain inputs (made by our
   encoders, once) in tests/fixtures/metadata-write/in and every output in tests/fixtures/metadata-write. The other
   inputs are the ExifTool-written fixtures of tests/fixtures/metadata (gen_meta.py).
2. Records ExifTool's reading of each output as <file>.exiftool.json (-j -n -G1 -a -struct) and ExifTool's validation of
   every input and output as <file>.validate.txt (-validate -warning -a -s3; inputs in metadata-write/in).

ExifTool (GPL / Artistic) is only run, never linked. The tests compare their outputs with the golden ones byte for byte
and check the stored ExifTool readings, so they work without ExifTool; with ExifTool installed they also check live.
"""
import argparse
import json
import os
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, '..', '..'))
OUT = os.path.join(ROOT, 'tests', 'fixtures', 'metadata-write')
IN = os.path.join(OUT, 'in')
META = os.path.join(ROOT, 'tests', 'fixtures', 'metadata')
META_INPUTS = ['meta.jpg', 'meta.png', 'meta.tif', 'meta.webp', 'meta.jp2']
EXT = ('.jpg', '.png', '.tif', '.btf', '.webp', '.jp2')


def exiftool_path(arg):
    for p in (arg, os.environ.get('EXIFTOOL'), os.path.expanduser('~/.local/share/exiftool/exiftool'), shutil.which('exiftool')):
        if p and os.path.exists(p):
            return p
    sys.exit('ExifTool not found (--exiftool, $EXIFTOOL or ~/.local/share/exiftool/exiftool)')


def run_golden(build):
    env = dict(os.environ, PIXCODEC_WRITE_GOLDEN='1')
    cmd = ['dotnet', 'test', os.path.join(ROOT, 'tests', 'PixCodec.Tests'), '-c', 'Release',
           '--filter', 'FullyQualifiedName~MetadataWriteTests.Golden']
    if not build:
        cmd.append('--no-build')
    r = subprocess.run(cmd, env=env, capture_output=True, text=True)
    print(r.stdout.strip().splitlines()[-1] if r.stdout.strip() else r.stderr[-2000:])
    # (a case may fail on a changed expectation; the outputs are written before the checks)


def reading(et, path, out):
    data = json.loads(subprocess.run([et, '-j', '-n', '-G1', '-a', '-struct', '-charset', 'utf8', path],
                                     capture_output=True, check=True).stdout)[0]
    for k in ('SourceFile', 'System:Directory', 'System:FileName', 'System:FileModifyDate', 'System:FileAccessDate',
              'System:FileInodeChangeDate', 'System:FilePermissions', 'ExifTool:ExifToolVersion'):
        data.pop(k, None)
    with open(out, 'w', encoding='utf-8') as f:
        json.dump(data, f, indent=1, ensure_ascii=False, sort_keys=True)


def validate(et, path, out):
    r = subprocess.run([et, '-validate', '-warning', '-a', '-s3', path], capture_output=True, text=True)
    with open(out, 'w', encoding='utf-8') as f:
        f.write(r.stdout)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--exiftool')
    ap.add_argument('--no-build', action='store_true')
    a = ap.parse_args()
    et = exiftool_path(a.exiftool)
    run_golden(not a.no_build)
    n = 0
    for f in sorted(os.listdir(OUT)):
        p = os.path.join(OUT, f)
        if os.path.isfile(p) and f.lower().endswith(EXT):
            reading(et, p, p + '.exiftool.json')
            validate(et, p, p + '.validate.txt')
            n += 1
    for f in sorted(os.listdir(IN)):
        p = os.path.join(IN, f)
        if f.lower().endswith(EXT):
            validate(et, p, p + '.validate.txt')
    for f in META_INPUTS:
        validate(et, os.path.join(META, f), os.path.join(IN, f + '.validate.txt'))
    print('recorded', n, 'outputs')


if __name__ == '__main__':
    main()
