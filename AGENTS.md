# AI Agent Guide

Repository rules for coding agents working on Aardvark.PixImage.PixCodec: pure managed (no native code) image codecs for
.NET 8 and .NET 10, MIT licensed, with an Aardvark `PixImage` loader on top.

## Licence rule (most important)

- Everything in this repository is MIT. Write codecs **from the specifications only**. Never port, copy or read the
  code of other implementations (libjpeg, libpng, libtiff, libwebp, OpenEXR, astcenc, ...), whatever their licence.
- Third-party **data** tables only from permissive sources, each with an entry in `THIRD-PARTY-NOTICES.md`.
- Other tools (GPL ones included) may only be **run** as test oracles, never linked or shipped.
- Third-party test files never go into this repository: they live in the separate test-data repository
  (see Tests).
- LERC is decode only (Esri patents claim the compression). Never add a LERC encoder or encoder helpers.

## Commands

| Task | Command |
|------|---------|
| Restore + build | `./build.sh` or `build.cmd` (tool restore, `dotnet paket restore`, Release build of the solution) |
| Test all | `dotnet test Aardvark.PixImage.PixCodec.slnx -c Release` (about 7 minutes, 15k tests per framework) |
| Test some | `dotnet test tests/Aardvark.PixImage.PixCodec.Codecs.Tests -c Release -f net10.0 --filter "FullyQualifiedName~Png"` |
| Pack locally | `dotnet aardpack Aardvark.PixImage.PixCodec.slnx --notag` (writes `bin/pack`; does not publish without a token) |

The solution file is `Aardvark.PixImage.PixCodec.slnx`. `global.json` needs a .NET 10 SDK; the .NET 8 runtime is needed
to run the net8.0 tests.

## Dependencies (Paket)

- Paket, not `dotnet add package`. Change `paket.dependencies`, then `dotnet paket install`. Never edit `paket.lock`
  by hand.
- The library depends only on `Aardvark.Base.Tensors`. Do not add further runtime dependencies.
- Test packages are in the `Test` group.

## Layout

| Path | Contents |
|------|----------|
| `src/Aardvark.PixImage.PixCodec/Core` | `RawImage`, `ImageCodec` (format detection, `Decode`, `GetInfo`), managed zlib / zstd / xz / Brotli (decode) |
| `src/Aardvark.PixImage.PixCodec/<Format>` | one folder per format: `Png`, `Jpeg`, `Tiff`, `Gif`, `Exr`, `Webp`, `Jp2`, `Jxl` (JPEG XL, decode only), `Jbig`, `Bmp`, `Tga`, `Dds`, `Ktx`, `Hdr`, `Pnm`, `Qoi`, `Ico`, `Lerc` |
| `src/Aardvark.PixImage.PixCodec/Metadata` | EXIF / XMP / IPTC / GPS reading without decoding pixels, lossless metadata writing |
| `src/Aardvark.PixImage.PixCodec/Color` | ICC profiles and colour management |
| `src/Aardvark.PixImage.PixCodec/Processing` | `ImageOps`: resize filters, warps, orientation, transfer functions |
| `src/Aardvark.PixImage.PixCodec/PixCodecLoader.cs` | Aardvark adapter: `PixCodec`, `PixCodecLoader` (`IPixLoader`) |
| `src/Aardvark.PixImage.PixCodec/PixCodecProcessor.cs` | `PixCodecProcessor` (`IPixProcessor`) |
| `tests/Aardvark.PixImage.PixCodec.Codecs.Tests` | codec tests (namespace `Aardvark.ImageCodecs.Tests`) |
| `tests/Aardvark.PixImage.PixCodec.Tests` | loader tests against Aardvark `PixImage` |
| `tests/fixtures` | synthetic fixtures with reference decodes (`.ref` and other sidecars) |
| `tests/oracle` | `gen_*.py` scripts that create fixtures and run the reference tools |

## Names and conventions

- Package and repository: `Aardvark.PixImage.PixCodec`. Loader classes are in namespace `Aardvark.Data` (`PixCodec`,
  `PixCodecLoader`, `PixCodecProcessor`), like the other Aardvark loaders; codec code is in `Aardvark.ImageCodecs`.
- Never create a namespace `Aardvark.PixImage.*`: it hides the `PixImage` class for code in any `Aardvark.*` namespace
  (CS0118).
- Decoders always return upright images: EXIF / TIFF orientation is applied by default (`ApplyOrientation = false` opts
  out), and `ImageInfo` reports the upright size. Users must never have to apply orientation themselves.
- PixImages from the loader always have the dense layout (channel fastest, then x, then y; top row first, no y flip).
- JPEG trellis quantization stays off by default.
- Little-endian hosts only.
- Environment variables use the prefix `PIXCODEC_`.

## Code pitfalls seen here

- `switch` patterns on `byte[]` / `ushort[]` / `uint[]` also match `sbyte[]` / `short[]` / `int[]` (CLR array
  covariance): switch on `SampleType` instead.
- `Vector<T>.Count` is 8 floats with AVX2 but 4 on ARM (NEON) and on x64 without AVX2. Size buffers from the actual
  count, never from the AVX2 width. Several classes use `[SkipLocalsInit]`, so `stackalloc` memory is not zeroed.
- .NET 10 ARM64 JIT bug: in optimized code, `v[k] = x` on a `Vector4` local that started as `Vector4.Zero` / `One`
  writes element 0 whatever `k` is. Write such elements through a ref (see `AstcEncoder.SetElement`).
- Test code must not assume `/` as path separator, or a particular build of an oracle tool (ImageMagick Q16 versus
  HDRI).

## Tests

- Decoders are checked against reference decoders through the `.ref` sidecars that `tests/oracle/gen_*.py` writes;
  encoders by decoding their output with the reference tools. Tests whose tool or python package is missing skip
  themselves.
- Third-party test files are in https://github.com/krauthaufen/Aardvark.PixImage.PixCodec.TestData. Tests find it via
  `PIXCODEC_TESTDATA` or as `../Aardvark.PixImage.PixCodec.TestData`, and skip without it.
- Every change gets a regression test. Run the suite on both frameworks; CI also runs Windows and macOS (ARM64), which
  have caught bugs that x64 Linux does not show.

## Releases

- Version and release notes come from `RELEASE_NOTES.md` (Aardvark.Build). A release is a new section on top of that
  file pushed to `main`; `.github/workflows/publish.yml` then tests, packs with aardpack and pushes to nuget.org and
  GitHub Packages. Never remove or rewrite older release notes entries.
- Package metadata lives in `src/Aardvark.PixImage.PixCodec/paket.template`.
- Commit messages: one short line.
