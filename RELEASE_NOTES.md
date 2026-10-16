### 1.0.0-prerelease0002
* JPEG XL: decoder (all conformance files pass, within 2x of libjxl) and encoder (lossless, lossy VarDCT, metadata, animation).
* JPEG-LS decode and encode.
* New formats: PCX/DCX, SGI, Sun Raster, XBM, XPM, WBMP, ICNS, ANI, .astc, PVR, DPX, Cineon.
* Volume formats NRRD, NIfTI, MetaImage and FITS with physical metadata, via `DecodeVolume` / `LoadVolume`.
* `GetInfo` on streams of unknown format no longer reads the whole stream.
* CI runs every test with all reference tools on Windows, macOS and Linux.

### 1.0.0-prerelease0001
* First release: pure managed decoders and encoders for PNG, JPEG, TIFF, GIF, OpenEXR, WebP, JPEG 2000, JBIG, BMP, TGA, DDS, KTX, Radiance HDR, PNM, QOI and ICO, LERC decode only.
* Aardvark loader `Aardvark.Data.PixCodec` with PixImage, mip map, cube map and volume loading and saving.
* Metadata (EXIF, XMP, GPS, ICC) readable without decoding the image, lossless metadata writing, ICC colour management, resizing and other image operations.
