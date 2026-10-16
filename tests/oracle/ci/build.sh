#!/usr/bin/env bash
# Builds the oracles that come as source only (CI; Linux and macOS in bash, Windows in an MSYS2 MINGW64 shell):
#   jbgtopbm   jbigkit 2.1 (JBIG1 decoder)
#   pngcheck   4.0.1 (PNG chunk reader)
#   jpeg       the ISO/IEC 10918 reference software (libjpeg by Thomas Richter)
#   exr_oracle tests/oracle/exr_oracle.cpp against the system's OpenEXR
# into $CI_TOOLS/src (default .tmp/ci-tools/src); kept when present, so that directory can be cached. Needs a C/C++
# compiler, make, zlib and OpenEXR development files (with pkg-config). Tells later steps where things are through
# $GITHUB_ENV / $GITHUB_PATH.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
TOOLS="${CI_TOOLS:-$(pwd)/.tmp/ci-tools}"
mkdir -p "$TOOLS/src/bin"
TOOLS="$(cd "$TOOLS" && pwd)"
D="$TOOLS/src"

case "$(uname -s)" in MINGW*|MSYS*|CYGWIN*) WIN=1; X=.exe ;; *) WIN=0; X= ;; esac
native() { if [ $WIN = 1 ]; then cygpath -w "$1"; else echo "$1"; fi; }
add_path() { native "$1" >> "${GITHUB_PATH:-/dev/null}"; echo "PATH += $1"; }
set_env() { echo "$1=$(native "$2")" >> "${GITHUB_ENV:-/dev/null}"; echo "$1=$2"; }
fetch() { [ -f "$2" ] || curl -fsSL --retry 3 -o "$2" "$1"; }
unpack() { mkdir -p "$2" && tar -xzf "$1" -C "$2" --strip-components=1; }
# Windows: the MinGW runtime and library DLLs an executable needs, copied next to it
deps() { [ $WIN = 1 ] || return 0; ldd "$1" | awk '/mingw64/ { print $3 }' | while read -r f; do cp -n "$f" "$(dirname "$1")/"; done; }
STATIC=; [ $WIN = 1 ] && STATIC=-static

JPEG_COMMIT=c719010a26ce0c666e98b2acf924ad5fc24b4f5d

# jbigkit
if [ ! -f "$D/jbigkit/pbmtools/jbgtopbm$X" ]; then
  fetch https://www.cl.cam.ac.uk/~mgk25/jbigkit/download/jbigkit-2.1.tar.gz "$D/jbigkit.tar.gz"
  unpack "$D/jbigkit.tar.gz" "$D/jbigkit"
  (cd "$D/jbigkit" && cc -O2 $STATIC -Ilibjbig -o pbmtools/jbgtopbm$X pbmtools/jbgtopbm.c libjbig/jbig.c libjbig/jbig_ar.c)
fi
set_env PIXCODEC_JBIGKIT "$D/jbigkit/pbmtools"

# pngcheck
if [ ! -f "$D/bin/pngcheck$X" ]; then
  fetch https://github.com/pnggroup/pngcheck/archive/refs/tags/v4.0.1.tar.gz "$D/pngcheck.tar.gz"
  unpack "$D/pngcheck.tar.gz" "$D/pngcheck"
  cc -O2 $STATIC -DUSE_ZLIB -o "$D/bin/pngcheck$X" "$D/pngcheck/pngcheck.c" -lz
fi
add_path "$D/bin"

# ISO/IEC 10918 reference software
if [ ! -f "$D/libjpeg/jpeg$X" ]; then
  fetch "https://github.com/thorfdbg/libjpeg/archive/$JPEG_COMMIT.tar.gz" "$D/libjpeg.tar.gz"
  unpack "$D/libjpeg.tar.gz" "$D/libjpeg"
  # (MinGW has no libc to link explicitly: -lc goes, the runtime is linked statically)
  if [ $WIN = 1 ]; then libs=(LDLIBS="-lm -static"); else libs=(); fi
  (cd "$D/libjpeg" && ./configure > /dev/null && make -j4 final ${libs[@]+"${libs[@]}"} > "$D/libjpeg.log" 2>&1) || { tail -40 "$D/libjpeg.log"; exit 1; }
  deps "$D/libjpeg/jpeg$X"
fi
set_env PIXCODEC_ISO_JPEG "$D/libjpeg/jpeg$X"

# exr_oracle
if [ ! -f "$D/exr/exr_oracle$X" ]; then   # (the CI cache key covers exr_oracle.cpp)
  mkdir -p "$D/exr"
  c++ -O1 -std=c++17 "$HERE/../exr_oracle.cpp" -o "$D/exr/exr_oracle$X" $(pkg-config --cflags --libs OpenEXR)
  deps "$D/exr/exr_oracle$X"
fi
set_env EXR_ORACLE "$D/exr/exr_oracle$X"
