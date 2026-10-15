# PixCodec

Pure managed image loading and saving for .NET 8. No native binaries, no third-party packages, MIT licensed.
Little-endian hosts only (x64 and arm64 are tested).

| Format | Decode | Encode |
|---|---|---|
| PNG | everything in the spec, interlaced, 16 bit, APNG, all standard ancillary chunks | 8/16 bit gray, gray+alpha, RGB, RGBA, palette, 1/2/4 bit, interlaced, APNG |
| JPEG | baseline, progressive, lossless, hierarchical, arithmetic coding, 8 and 12 bit, CMYK/YCCK, scaled decode | baseline, progressive, lossless (Huffman and arithmetic), arithmetic, 8 and 12 bit |
| TIFF | classic + BigTIFF, strips + tiles, 1..64 bit int, 16/24/32/64 bit float, LZW, Deflate, PackBits, CCITT, JPEG (incl. old-style, 12 bit, lossless), ZSTD, LZMA, WebP, JPEG 2000 (incl. Aperio), JBIG, LERC (decode only), PixarLog, LogLuv, Lab, multi-page, raw tags, regions, files > 4 GB | every sample type, strips or tiles, None / LZW / Deflate / PackBits / ZSTD / LZMA / JPEG / WebP / lossless JPEG 2000 / JBIG / CCITT G3 and G4, multi-page, overviews, cloud-optimized layout, extra tags |
| GIF | 87a/89a, interlace, transparency, animations (compositing verified against Chromium) | single images and animations, quantizer + dithering |
| OpenEXR | scanline, tiled, multi-part, deep, mip/rip levels, half/float/uint, luminance/chroma, all compressions incl. PIZ, B44, DWA, ZSTD, HTJ2K, region decode from a stream | scanline, tiled, multi-part, deep, luminance/chroma, all of those compressions |
| WebP | lossy, lossless, alpha, animation | lossy, lossless, alpha, animation |
| BMP | all header versions (OS/2 1.x/2.x, V1..V5), 1/2/4/8/16/24/32 bit, palettes, bit fields of any width (>8 bit fields give 16 bit samples), alpha masks, RLE4/RLE8/RLE24, embedded JPEG/PNG, ICC; checked against the BMP Suite | 8 bit gray, RGB, RGBA (gray+alpha as RGBA), resolution, ICC |
| DDS | 1D, 2D, volume and cube map textures, arrays, mip chains, legacy and DX10 headers; BC1..BC7 (BC6H as half float, signed BC4/BC5), legacy bit mask formats, the common uncompressed DXGI formats (8/16/32 bit int and float, 10:10:10:2, 5:6:5, 5:5:5:1, 4:4:4:4, R11G11B10F, RGB9E5) | uncompressed 8/16 bit, half and float, or block compressed BC1..BC7 (incl. BC6H, signed BC4/BC5; three quality levels); mip chains (given or computed), cube maps, volume textures |
| TGA | colour-mapped, true-colour (15/16/24/32 bit), gray (+alpha), RLE, every origin, TGA 2.0 alpha type | gray, gray+alpha, RGB, RGBA, optional RLE |
| LERC | LERC1 and LERC2 v2..v6 (bands, depth, masks, no-data, lossless float) | decode only |
| JBIG1 | the whole of T.82 (layers, all stripe orders, bit planes) | single layer, 1..8 bit planes |
| JPEG 2000 | Part 1 and HTJ2K (.j2k / .jph codestreams, JP2 / JPH container), 1..38 bit, region / reduced resolution / layer limited decode | Part 1 and HTJ2K, lossless and lossy (up to 16 bit), target bitrate, tiles, progression orders |

Not supported: OS/2 Huffman BMP, 64 bit BMP, JPEG XL / JPEG XR inside TIFF, lossy HTJ2K (LJ2K) EXR, JPEG 2000 Part 2 / JPX extensions, ICC colour conversion.
LERC encoding is not offered because Esri holds patents on LERC compression.

## Core library

```csharp
using PixCodec;

RawImage img = ImageCodec.Decode("photo.jpg");      // format is sniffed from the content
ImageCodec.Encode(img, "out.png");                  // format from the extension
Jpeg.Encode(img, stream, new JpegEncodeOptions { Quality = 85, Progressive = true });

ImageInfo info = ImageCodec.GetInfo("scan.tif");    // headers and metadata, no pixels
// info.Format, Width, Height, Channels, SampleType, Color, FrameCount,
// BitsPerSample, Orientation (+ StoredWidth/Height), DpiX, DpiY, HasIccProfile, IsAnimated, MipLevels, Metadata
PhotoMetadata m = info.Metadata!;                   // or ImageCodec.ReadMetadata(path)
// m.Make, Model, LensModel, SerialNumber, DateTaken (+ DateTakenOffset), ExposureTime, FNumber, Iso, FocalLength, ...
// m.Gps: Latitude, Longitude, Altitude, TimeUtc, Speed, ImageDirection, HorizontalError, ...
// m.Pose: camera / gimbal and vehicle yaw, pitch, roll, relative / absolute altitude, RTK (DJI, Parrot, Pix4D camera XMP)
// m.Title, Description, Artist, Copyright, Keywords, Rating, City, ...; m.Panorama (GPano)
// raw: m.Exif (every tag), m.Xmp (every property), m.Iptc, m.Comments, m.Text (PNG), m.ExrAttributes, m.Thumbnail
if (ImageCodec.TryGetInfo(path, out var i)) { /* ... */ }
RawImage[] levels = ImageCodec.DecodeMipmaps("tex.dds");   // stored mip levels (DDS, OpenEXR), else just the image
DdsTexture tex = Dds.DecodeTexture(bytes);              // every surface: tex.GetImage(mip, item, face, slice)
Dds.Encode(img, stream, new DdsEncodeOptions { Compression = DdsCompression.BC7, Mipmaps = DdsMipmaps.Generate });
Dds.EncodeVolume(slices, stream, new DdsEncodeOptions { Compression = DdsCompression.BC4 });   // 3D texture
```

`RawImage` is a tightly packed, top-down, channel-interleaved typed array (`byte[]`, `ushort[]`, `float[]`, ...)
in R,G,B,A order with straight alpha. Images come back upright: the EXIF/TIFF orientation (JPEG, PNG, WebP, TIFF, JPEG 2000)
is applied while decoding, the metadata then says orientation 1 (also inside a kept EXIF blob, so a re-saved image is not
turned again), and `ImageInfo` reports the upright size. Every format's options have `ApplyOrientation = false` for the stored pixels. Each format has its own static class (`Png`, `Jpeg`, `Tiff`, `Gif`, `Exr`, `Webp`, `Jp2`, `Lerc`, `Jbig`)
with format specific options and extras: animations (GIF, APNG, WebP), TIFF pages / tags / regions / overviews,
EXR headers, named channels, levels and deep data.

Malformed files throw `ImageFormatException`, valid files using an unimplemented feature throw `ImageNotSupportedException`.

`GetInfo` never reads pixel data: files are memory mapped (only the pages holding headers and metadata are read),
streams are walked by seeking past image chunks (PNG, WebP, JPEG 2000) or by reading just the TIFF directories. GIF
frame counting and run length coded TGA / BMP walk all data. Metadata comes from EXIF, XMP (also JPEG extended XMP),
IPTC (JPEG APP13, TIFF), ICC descriptions, JPEG / GIF / JPEG 2000 comments, PNG text chunks (incl. ImageMagick raw
profiles) and OpenEXR attributes; where several hold a value, EXIF wins over XMP over IPTC. XMP is kept when decoding
and written back by the JPEG, PNG, TIFF, WebP and JPEG 2000 encoders (its tiff:Orientation follows the applied
orientation; JPEG splits packets beyond 64 KB into standard and extended XMP).

### Writing metadata

```csharp
var edit = new MetadataEdit
{
    Make = "DJI", Model = "FC6310R", SerialNumber = "0K8TF7R0012345",
    DateTaken = new DateTime(2024, 6, 15, 14, 23, 45, 120), DateTakenOffset = TimeSpan.FromHours(2),
    Title = "Pier 3", Artist = "Jane Pilot", Copyright = "(c) 2024", Keywords = new[] { "bridge", "pier" }, Rating = 4,
    Gps = new GpsEdit { Latitude = 47.0707123, Longitude = 15.4395456, Altitude = 412.35, TimeUtc = utc, ImageDirection = 123.4, HorizontalError = 0.02 },
};
edit.Remove(MetadataField.UserComment);                           // a field, wherever it is stored
edit.SetExif(ExifIfd.Exif, 0xA420, ExifType.Ascii, "0123...");    // any tag (not the structure ones)
edit.SetXmpNamespace("my", "http://example.com/ns/").SetXmp("my:Flight", "17").SetXmp("dc:subject", kws, XmpArrayType.Bag);
edit.RemoveXmp("crs:*");  edit.SetIptc(2, 105, "Headline");

byte[] tagged = ImageCodec.WriteMetadata(bytes, edit);                    // or (Stream, Stream, edit), (path, path, edit)
ImageCodec.WriteMetadata("scan.tif", "scan.tif", new MetadataEdit { RemoveGps = true });   // in place
new MetadataEdit { RemoveAll = true }.ApplyTo(img);                       // ImageMetadata before encoding
```

`WriteMetadata` rewrites JPEG (APP1 EXIF / XMP / extended XMP, APP13 IPTC), PNG (eXIf, iTXt XMP, existing text chunks,
CRCs), TIFF and BigTIFF (the changed directories are appended, the header points to the new IFD0, unchanged entries keep
their values where they are), WebP (EXIF / XMP chunks, VP8X flags, a VP8X header added to simple files) and JPEG 2000
JP2 / JPX (uuid boxes) without touching the compressed image data, which stays byte for byte as it is. EXIF values go to
EXIF (IFD0, Exif, GPS directories; required tags are added to directories made new, ExifVersion / GPSVersionID are raised
when a newer tag such as OffsetTimeOriginal is added, EXIF 3.0 ImageTitle is only written into 3.0 directories); title,
description, creator, rights, keywords, rating and location always go to XMP too; XMP properties that mirror EXIF values
are updated where they exist; IPTC datasets only where the file already has IPTC (switched to UTF-8 when needed, the
Photoshop IPTC digest kept in step). Removing values zeroes their bytes (old directories, values, thumbnails) unless
something else still refers to them.

EXIF blocks with a maker note, sub directories or data no known field refers to (previews that maker notes point to)
keep their layout: everything stays at its offset, the directories are written in their old place when they fit,
otherwise after the block. Others are laid out anew. Limits: TIFF edits concern the first page (IFD0 and its EXIF / GPS
directories); EXIF in JPEG must fit into one 64 KB segment (drop the thumbnail otherwise); multi-picture (MPF) offsets
are corrected, AFCP trailers are not; JPEG 2000 codestream comments and raw codestreams (.j2k) are not touched, JPX
files with fragment tables are refused; BMP and TGA have no standard place for EXIF / XMP, GIF, OpenEXR and DDS are not
supported (`ImageNotSupportedException`). `RemoveAll` keeps the ICC profile, resolution and orientation and, in TIFF,
every structural and GeoTIFF tag.

## Compression

`PixCodec.Compression` holds fully managed deflate/inflate (zlib, levels 0..9, streaming `Inflater`), Zstandard
(decode and encode, both with dictionaries), XZ / LZMA (decode incl. BCJ filters, encode up to an optimal parser, multi-threaded) and vectorised CRC-32 / Adler-32.
The managed deflate is the default; `Zlib.Backend = ZlibBackend.Bcl` (or `PIXCODEC_ZLIB=bcl`) switches to
`System.IO.Compression`.

## Image operations

`ImageOps` works on any `RawImage` (every sample type, any channel count) and returns new images:

```csharp
var thumb = ImageOps.Resize(img, 320, 240);                          // Lanczos3
var small = ImageOps.Resize(img, 800, 600, ResampleFilter.Mitchell);
var fit   = ImageOps.ResizeToFit(img, 1024, 1024, new ResizeOptions { LinearLight = true });
var turn  = ImageOps.Rotate90(img);                                  // lossless, also Flip*, Transpose, ApplyOrientation
var rot   = ImageOps.Rotate(img, 12.5, expand: true, new WarpOptions { Filter = ResampleFilter.Bicubic });
var w     = ImageOps.WarpPerspective(img, 800, 600, homography, new WarpOptions { Border = BorderMode.Mirror });
```

- Filters: `Nearest`, `Box`, `Bilinear`, `Hermite`, `Bicubic` (Catmull-Rom), `Mitchell`, `BSpline`, `Hamming`, `Lanczos2`,
  `Lanczos3`, `Gaussian`, plus `ResampleFilter.Cubic(b, c)`, `Lanczos(n)` and custom kernels.
- Resizing is separable and widens the filter when downscaling (no aliasing), with pixel areas aligned
  (the same conventions as Pillow, whose float results it reproduces). Alpha is premultiplied while filtering;
  `LinearLight` filters 8 and 16 bit sRGB in linear light. Integer results are rounded and clamped, float results are not.
- Lossless: `Crop`, `FlipHorizontal`, `FlipVertical`, `Rotate90`, `Rotate180`, `Rotate270`, `Transpose`, `Transverse`, `ApplyOrientation`.
- Warps (`Rotate`, `WarpAffine`, `WarpPerspective`, `Remap`) interpolate with any filter and border mode
  (`Constant`, `Clamp`, `Wrap`, `Mirror`, `MirrorRepeat`).
- Multi-threaded and vectorised: 4000x3000 RGB to 1000x750 with Lanczos3 takes about 30 ms on 8 cores (Pillow: 125 ms, one thread).

`ImageCodec.DecodeAll` returns every page of a TIFF or every frame of an animated GIF, PNG or WebP.

## Aardvark

`Aardvark.PixImage.PixCodec` provides an `IPixLoader`. It registers itself on `Aardvark.Init()`,
or call `PixImagePixCodec.Init()`.

```csharp
PixImagePixCodec.Init();
var pi = PixImage.Load("dem.tif");                  // PixImage<float>, wraps the decoded array without copying
pi.Save("dem.exr");
```

Without registering anything, `PixImagePixCodec` loads straight into PixImages. Like the registered loader it always returns
upright images (orientation applied) whose volume has the dense image layout: channels innermost, then x, then y, top row first,
starting at index 0 (no y-flip; the same convention as the other Aardvark loaders).

```csharp
PixImage img       = PixImagePixCodec.Load("photo.jpg");                         // PixImage<byte>, upright
PixImage<float> f  = PixImagePixCodec.Load<float>("scan.tif");                    // converted if the file is not float
PixImage[] pages   = PixImagePixCodec.LoadAll("stack.tif");                       // pages or animation frames
ImageInfo info     = PixImagePixCodec.GetInfo("dem.tif");
PixImagePixCodec.Save(img, "out.webp");
PixImage p = rawImage.ToPixImage();   RawImage r = pixImage.ToRawImage();
PixImageMipMap mm  = PixImagePixCodec.LoadMipmap("tex.dds");                     // also PixImageMipMap.Load (IPixMipmapLoader)
PixCube cube       = PixImagePixCodec.LoadCube("sky.dds");                        // DDS cube map, every side with its mip chain
PixImagePixCodec.SaveDds(cube, stream);
PixImagePixCodec.SaveDds(volume, stream, new DdsEncodeOptions { Compression = DdsCompression.BC1 });   // PixVolume
```

`Init()` also registers `PixCodecProcessor`, so `PixImage.Scaled`, `Resized`, `Rotated`, `Remapped` and `PixImageMipMap.Create`
use PixCodec for every sample type (ImageInterpolation `Near`, `Linear`, `Cubic`, `Lanczos`, `SuperSample` map to nearest,
bilinear, bicubic, Lanczos3 and box). Extension methods add the other filters and options:
`pi.Resized(w, h, ResampleFilter.Mitchell)`, `pi.Resized(w, h, new ResizeOptions { LinearLight = true })`,
`pi.Rotated(angle, true, new WarpOptions { ... })`, `pi.WarpedAffine(...)`, `pi.WarpedPerspective(...)`, `pi.Oriented(orientation)`.
The alpha channel is taken from the PixImage format (BGRA works as is, premultiplied RGBP/BGRP are not premultiplied again). Two channel images without colour meaning (BC5 and RG textures, two band rasters) load as `Col.Format.RG`.

Half float EXR channels load as `PixImage<float>`. BGR(A) images are saved as RGB(A). The existing `PixSaveParams`
classes (JPEG quality, PNG level, TIFF / EXR compression, WebP quality / lossless, JPEG 2000) are honoured.

## Tests

`dotnet test`. Decoders are checked against reference decoders (libjpeg-turbo, libpng, libtiff, libwebp, the OpenEXR
library, Chromium, ...) through `.ref` sidecars generated by `tests/oracle/gen_*.py`; encoders are checked by decoding
their output with the same reference tools. Running the encoder cross-checks needs `python3` with Pillow, numpy,
tifffile, imagecodecs, opencv and OpenEXR; without python they are skipped. Some extra checks use tools fetched by
`tests/oracle/gen_png.py --fetch-tools`.

Metadata is checked against ExifTool (run, never linked): `tests/oracle/gen_meta.py` writes the reading fixtures,
`tests/oracle/gen_meta_write.py` stores golden `WriteMetadata` outputs with ExifTool's reading and validation of them
(`tests/fixtures/metadata-write`). With ExifTool installed (`$EXIFTOOL` or `~/.local/share/exiftool/exiftool`) the
writing tests also run it live, including a tag by tag comparison of rewritten camera files with maker notes.

Everything in `tests/fixtures` is generated by `tests/oracle/gen_*.py` from synthetic images. Third-party test files
(the PngSuite, openexr-images samples, the lists of the libwebp vectors, the JPEG 2000 conformance suite and the
libtiff / GDAL / OpenSlide samples) are in a separate test-data repository (`pixcodec-testdata`) with their licences. The tests look for it in `PIXCODEC_TESTDATA` or next to this repository
(`../pixcodec-testdata`); without it the tests that need it are skipped.

Third-party data in the library: see `THIRD-PARTY-NOTICES.md`.
