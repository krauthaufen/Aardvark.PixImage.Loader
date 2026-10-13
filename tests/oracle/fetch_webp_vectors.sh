#!/bin/sh
# Fetches the libwebp test vectors listed in tests/fixtures/webp/vectors/refs.txt (not redistributed here).
set -e
dir="$(cd "$(dirname "$0")/../fixtures/webp/vectors" && pwd)"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
git clone --depth 1 https://chromium.googlesource.com/webm/libwebp-test-data "$tmp/src"
cut -d' ' -f1 "$dir/refs.txt" | while read -r f; do cp "$tmp/src/$f" "$dir/$f"; done
