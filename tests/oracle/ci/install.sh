#!/usr/bin/env bash
# Installs the command line oracles the tests run (CI, GitHub Actions runners: Linux x64, macOS arm64, Windows x64 under
# Git bash). Downloads go to $1 (default .tmp/ci-tools); they are kept when present, so that directory can be cached.
# Tells later steps where things are through $GITHUB_ENV / $GITHUB_PATH.
#   install.sh [tool ...]    tools: magick astcenc ktx libjxl exiftool basisu charls zstd 7z ffmpeg netpbm (default: all)
#                            and avif (libavif, libaom, dav1d: only to regenerate tests/fixtures/avif with gen_avif.py)
set -euo pipefail

TOOLS="${CI_TOOLS:-$(pwd)/.tmp/ci-tools}"
mkdir -p "$TOOLS"
TOOLS="$(cd "$TOOLS" && pwd)"
if [ $# -eq 0 ]; then want=(magick astcenc ktx libjxl exiftool basisu charls zstd 7z ffmpeg netpbm); else want=("$@"); fi

OS="${RUNNER_OS:-$(uname -s)}"
case "$OS" in Linux) OS=Linux ;; Darwin|macOS) OS=macOS ;; *) OS=Windows ;; esac

native() { if [ "$OS" = Windows ]; then cygpath -w "$1"; else echo "$1"; fi; }
add_path() { native "$1" >> "${GITHUB_PATH:-/dev/null}"; echo "PATH += $1"; }
set_env() { echo "$1=$(native "$2")" >> "${GITHUB_ENV:-/dev/null}"; echo "$1=$2"; }
fetch() { [ -f "$2" ] || curl -fsSL --retry 3 -o "$2" "$1"; }

MAGICK=7.1.2-34
ASTCENC=5.7.0
KTX=4.4.2
JXL=0.12.0
EXIFTOOL=13.59
BASISU=v2_50
ZSTD=1.5.7
CHARLS=2.4.4

APT_UPDATED=0
apt_get() {
  [ $APT_UPDATED = 1 ] || { sudo apt-get update -qq; APT_UPDATED=1; }
  sudo apt-get install -y --no-install-recommends "$@" > /dev/null
}
brew_get() { HOMEBREW_NO_AUTO_UPDATE=1 HOMEBREW_NO_INSTALL_CLEANUP=1 brew install --quiet "$@" > /dev/null; }

for t in "${want[@]}"; do
  echo "== $t"
  case "$t:$OS" in
  magick:Linux)
    d="$TOOLS/magick"
    if [ ! -x "$d/squashfs-root/AppRun" ]; then
      mkdir -p "$d" && fetch "https://github.com/ImageMagick/ImageMagick/releases/download/$MAGICK/ImageMagick-$MAGICK-gcc-x86_64.AppImage" "$d/magick.AppImage"
      chmod +x "$d/magick.AppImage" && (cd "$d" && ./magick.AppImage --appimage-extract > /dev/null)
      mkdir -p "$d/bin" && printf '#!/bin/sh\nexec "%s/squashfs-root/AppRun" "$@"\n' "$d" > "$d/bin/magick" && chmod +x "$d/bin/magick"
    fi
    add_path "$d/bin" ;;
  magick:macOS) brew_get imagemagick ;;
  magick:Windows)
    d="$TOOLS/magick"
    if [ ! -f "$d/magick.exe" ]; then
      mkdir -p "$d" && fetch "https://github.com/ImageMagick/ImageMagick/releases/download/$MAGICK/ImageMagick-$MAGICK-portable-Q16-HDRI-x64.7z" "$TOOLS/magick.7z"
      7z x -y -o"$d" "$TOOLS/magick.7z" > /dev/null
      [ -f "$d/magick.exe" ] || { inner="$(dirname "$(find "$d" -name magick.exe | head -1)")"; mv "$inner"/* "$d"/; }
    fi
    add_path "$d" ;;

  astcenc:*)
    d="$TOOLS/astcenc"
    case "$OS" in Linux) a=linux-x64 ;; macOS) a=macos-universal ;; Windows) a=windows-x64 ;; esac
    if [ ! -d "$d" ]; then
      fetch "https://github.com/ARM-software/astc-encoder/releases/download/$ASTCENC/astcenc-$ASTCENC-$a.zip" "$TOOLS/astcenc.zip"
      mkdir -p "$d" && unzip -q -o "$TOOLS/astcenc.zip" -d "$d"
    fi
    exe="$(find "$d" -type f \( -name 'astcenc-sse4.1*' -o -name 'astcenc' -o -name 'astcenc-neon' \) | head -1)"
    chmod +x "$exe" || true
    set_env ASTCENC "$exe" ;;

  ktx:Linux)
    d="$TOOLS/ktx"
    if [ ! -d "$d" ]; then
      fetch "https://github.com/KhronosGroup/KTX-Software/releases/download/v$KTX/KTX-Software-$KTX-Linux-x86_64.tar.bz2" "$TOOLS/ktx.tar.bz2"
      mkdir -p "$d" && tar -xjf "$TOOLS/ktx.tar.bz2" -C "$d" --strip-components=1
    fi
    set_env PIXCODEC_KTX "$d/bin/ktx" ;;
  ktx:macOS)
    fetch "https://github.com/KhronosGroup/KTX-Software/releases/download/v$KTX/KTX-Software-$KTX-Darwin-arm64.pkg" "$TOOLS/ktx.pkg"
    sudo installer -pkg "$TOOLS/ktx.pkg" -target / > /dev/null
    set_env PIXCODEC_KTX /usr/local/bin/ktx ;;
  ktx:Windows)
    fetch "https://github.com/KhronosGroup/KTX-Software/releases/download/v$KTX/KTX-Software-$KTX-Windows-x64.exe" "$TOOLS/ktx-setup.exe"
    d="$TOOLS/ktx"
    [ -f "$d/bin/ktx.exe" ] || MSYS2_ARG_CONV_EXCL='*' "$TOOLS/ktx-setup.exe" /S "/D=$(native "$d")"
    exe="$d/bin/ktx.exe"
    [ -f "$exe" ] || exe="$(find "$d" "/c/Program Files/KTX-Software" -name ktx.exe 2>/dev/null | head -1)"
    set_env PIXCODEC_KTX "$exe" ;;

  libjxl:Linux)
    d="$TOOLS/jxl"
    if [ ! -d "$d" ]; then
      apt_get lzip
      fetch "https://github.com/libjxl/libjxl/releases/download/v$JXL/jxl-linux-x86_64-static.tar.lz" "$TOOLS/jxl.tar.lz"
      mkdir -p "$d" && tar --lzip -xf "$TOOLS/jxl.tar.lz" -C "$d"
    fi
    add_path "$(dirname "$(find "$d" -type f -name cjxl | head -1)")" ;;
  libjxl:macOS) brew_get jpeg-xl ;;
  libjxl:Windows)
    d="$TOOLS/jxl"
    if [ ! -d "$d" ]; then
      fetch "https://github.com/libjxl/libjxl/releases/download/v$JXL/jxl-x64-windows-static.zip" "$TOOLS/jxl.zip"
      mkdir -p "$d" && unzip -q -o "$TOOLS/jxl.zip" -d "$d"
    fi
    add_path "$(dirname "$(find "$d" -type f -name cjxl.exe | head -1)")" ;;

  exiftool:*)
    d="$TOOLS/exiftool"
    if [ ! -f "$d/exiftool" ]; then
      fetch "https://github.com/exiftool/exiftool/archive/refs/tags/$EXIFTOOL.tar.gz" "$TOOLS/exiftool.tar.gz"
      mkdir -p "$d" && tar -xzf "$TOOLS/exiftool.tar.gz" -C "$d" --strip-components=1
    fi
    set_env EXIFTOOL "$d/exiftool" ;;

  basisu:*)
    d="$TOOLS/basisu"
    exe="$(find "$d" -type f \( -name basisu -o -name basisu.exe \) -path '*bin*' 2>/dev/null | head -1 || true)"
    if [ -z "$exe" ]; then
      fetch "https://github.com/BinomialLLC/basis_universal/archive/refs/tags/$BASISU.tar.gz" "$TOOLS/basisu.tar.gz"
      mkdir -p "$d" && tar -xzf "$TOOLS/basisu.tar.gz" -C "$d" --strip-components=1
      cmake -S "$d" -B "$d/build" -DCMAKE_BUILD_TYPE=Release -DBASISU_EXAMPLES=OFF > /dev/null
      cmake --build "$d/build" --config Release --target basisu --parallel 4 > /dev/null
      exe="$(find "$d" -type f \( -name basisu -o -name basisu.exe \) -path '*bin*' | head -1)"
    fi
    set_env PIXCODEC_BASISU "$exe" ;;

  charls:Linux|charls:macOS) ;;   # (imagecodecs' wheels carry libcharls; tests/oracle/charls.py finds it)
  charls:Windows)
    # imagecodecs links CharLS statically on Windows: the same version as a DLL of its own for tests/oracle/charls.py
    d="$TOOLS/charls"
    dll="$(find "$d" -type f -iname 'charls*.dll' 2>/dev/null | head -1 || true)"
    if [ -z "$dll" ]; then
      fetch "https://github.com/team-charls/charls/archive/refs/tags/$CHARLS.tar.gz" "$TOOLS/charls.tar.gz"
      mkdir -p "$d" && tar -xzf "$TOOLS/charls.tar.gz" -C "$d" --strip-components=1
      cmake -S "$d" -B "$d/build" -DBUILD_SHARED_LIBS=ON -DCHARLS_BUILD_TESTS=OFF -DCHARLS_BUILD_SAMPLES=OFF -DCHARLS_BUILD_FUZZ_TEST=OFF > /dev/null
      cmake --build "$d/build" --config Release --parallel 4 > /dev/null
      dll="$(find "$d" -type f -iname 'charls*.dll' | head -1)"
    fi
    set_env PIXCODEC_CHARLS "$dll" ;;

  zstd:Linux) command -v zstd > /dev/null || apt_get zstd ;;
  zstd:macOS) command -v zstd > /dev/null || brew_get zstd ;;
  zstd:Windows)
    d="$TOOLS/zstd"
    if [ ! -d "$d" ]; then
      fetch "https://github.com/facebook/zstd/releases/download/v$ZSTD/zstd-v$ZSTD-win64.zip" "$TOOLS/zstd.zip"
      mkdir -p "$d" && unzip -q -o "$TOOLS/zstd.zip" -d "$d"
    fi
    add_path "$(dirname "$(find "$d" -type f -name zstd.exe | head -1)")" ;;

  7z:Linux) command -v 7z > /dev/null || apt_get p7zip-full ;;
  7z:macOS)
    command -v 7zz > /dev/null || brew_get sevenzip
    mkdir -p "$TOOLS/bin" && ln -sf "$(command -v 7zz)" "$TOOLS/bin/7z" && add_path "$TOOLS/bin" ;;
  7z:Windows) command -v 7z > /dev/null || add_path "C:/Program Files/7-Zip" ;;

  ffmpeg:Linux) apt_get ffmpeg ;;
  ffmpeg:macOS) brew_get ffmpeg ;;
  ffmpeg:Windows) command -v ffmpeg > /dev/null || choco install ffmpeg -y --no-progress > /dev/null ;;

  netpbm:Linux) apt_get netpbm ;;
  netpbm:macOS) brew_get netpbm ;;
  netpbm:Windows) echo "netpbm comes from Cygwin (see build.yml)" ;;

  avif:Linux) apt_get libavif-bin aom-tools dav1d ;;
  avif:macOS) brew_get libavif aom dav1d ;;
  avif:Windows) echo "libavif / aom / dav1d: pacman -S mingw-w64-x86_64-libavif mingw-w64-x86_64-aom mingw-w64-x86_64-dav1d in MSYS2" ;;

  *) echo "unknown tool $t" >&2; exit 1 ;;
  esac
done
