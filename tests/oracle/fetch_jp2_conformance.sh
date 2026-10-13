#!/bin/sh
# Fetches the codestreams and reference images of the Rec. ITU-T T.803 | ISO/IEC 15444-4 conformance suite that
# tests/fixtures/jp2/conformance/limits.txt lists (about 93 MB; not redistributed here: their copyright notice
# restricts them to JPEG 2000 conformance testing). Jp2ConformanceTests picks up whatever is there.
set -e
dir="$(cd "$(dirname "$0")/../fixtures/jp2/conformance" && pwd)"
base="https://gitlab.com/wg1/htj2k-codestreams/-/raw/f6b9ede094a0bd6e1e0427e12721e3f3ee1b704b"
grep -v '^#' "$dir/limits.txt" | tr ' ' '\n' | grep -E '\.(j2k|pgx)$' | sort -u | while read -r f; do
    out="$dir/$f"
    [ -f "$out" ] && continue
    mkdir -p "$(dirname "$out")"
    curl -fsSL -o "$out.part" "$base/$f"
    mv "$out.part" "$out"
done
# every file must be the one the limits were taken for
grep -v '^#' "$dir/limits.txt" | tr ' ' '\n' | awk 'prev ~ /\.(j2k|pgx)$/ { print $0 "  " prev } { prev = $0 }' | sort -u | (cd "$dir" && sha256sum -c --quiet -)
echo "conformance files are in $dir"
