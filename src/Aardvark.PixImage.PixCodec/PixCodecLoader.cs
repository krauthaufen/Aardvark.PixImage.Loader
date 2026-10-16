using Aardvark.Base;
using System;
using System.Collections.Generic;
using System.IO;
using Aardvark.ImageCodecs;

namespace Aardvark.Data
{
    /// <summary>
    /// PixImage loader backed by the fully managed Aardvark.PixImage.PixCodec library (PNG, JPEG, TIFF, GIF, OpenEXR, WebP, JPEG 2000, BMP, TGA,
    /// DDS, KTX, PVR, ASTC, Radiance HDR, PNM / PFM, QOI, ICO / CUR / ANI, ICNS).
    /// Register with <see cref="PixCodec.Init"/> (done automatically by Aardvark.Init()).
    /// </summary>
    public sealed class PixCodecLoader : IPixMipmapLoader
    {
        public string Name => "PixCodec";
        public bool CanEncode => true;
        public bool CanDecode => true;

        public PixImage LoadFromFile(string filename) => ToPixImage(ImageCodec.Decode(filename));
        public PixImage LoadFromStream(Stream stream) => ToPixImage(ImageCodec.Decode(stream));

        /// <summary>The stored mip levels (DDS mip chains, OpenEXR mip maps), or just the image for files without them.</summary>
        public PixImageMipMap LoadMipmapFromFile(string filename) => ToMipMap(ImageCodec.DecodeMipmaps(filename));
        /// <inheritdoc cref="LoadMipmapFromFile"/>
        public PixImageMipMap LoadMipmapFromStream(Stream stream) => ToMipMap(ImageCodec.DecodeMipmaps(stream));

        internal static PixImageMipMap ToMipMap(RawImage[] levels) => new PixImageMipMap(Array.ConvertAll(levels, ToPixImage));

        public PixImageInfo GetInfoFromFile(string filename)
        {
            // headers of every supported format fit comfortably, except TIFF/EXR with far-away directories
            using var fs = File.OpenRead(filename);
            return GetInfoFromStream(fs);
        }

        public PixImageInfo GetInfoFromStream(Stream stream)
        {
            var info = ImageCodec.GetInfo(stream);
            var type = ElementType(info.SampleType);
            return new PixImageInfo(new PixFormat(type, ColFormat(info.Color, info.Channels)), new V2i(info.Width, info.Height));
        }

        public void SaveToFile(string filename, PixImage image, PixSaveParams saveParams)
        {
            using var fs = File.Create(filename);
            SaveToStream(fs, image, saveParams);
        }

        public void SaveToStream(Stream stream, PixImage image, PixSaveParams saveParams)
        {
            switch (saveParams.Format)
            {
                case PixFileFormat.Png:
                    Png.Encode(ToRawImage(image, Target.ByteOrUShort), stream, new PngEncodeOptions
                    {
                        CompressionLevel = (saveParams as PixPngSaveParams)?.CompressionLevel ?? PixPngSaveParams.DefaultCompressionLevel,
                    });
                    break;
                case PixFileFormat.Jpeg:
                    Jpeg.Encode(ToRawImage(image, Target.Byte), stream, new JpegEncodeOptions
                    {
                        Quality = Math.Max(1, (saveParams as PixJpegSaveParams)?.Quality ?? PixJpegSaveParams.DefaultQuality),
                    });
                    break;
                case PixFileFormat.Webp:
                    var webp = saveParams as PixWebpSaveParams;
                    Webp.Encode(ToRawImage(image, Target.Byte), stream, new WebpEncodeOptions
                    {
                        Quality = webp?.Quality ?? PixWebpSaveParams.DefaultQuality,
                        Lossless = webp?.Lossless ?? false,
                    });
                    break;
                case PixFileFormat.Jp2:
                case PixFileFormat.J2k:
                    Jp2.Encode(ToRawImage(image, Target.ByteOrUShort), stream, new Jp2EncodeOptions { RawCodestream = saveParams.Format == PixFileFormat.J2k });
                    break;
                case PixFileFormat.Bmp:
                    Bmp.Encode(ToRawImage(image, Target.Byte), stream);
                    break;
                case PixFileFormat.Targa:
                    Tga.Encode(ToRawImage(image, Target.Byte), stream);
                    break;
                case PixFileFormat.Dds:
                    Dds.Encode(ToRawImage(image, Target.Native), stream);
                    break;
                case PixFileFormat.Gif:
                    Gif.Encode(ToRawImage(image, Target.Byte), stream);
                    break;
                case PixFileFormat.Hdr:
                    Hdr.Encode(ToRawImage(image, Target.Native), stream);
                    break;
                case PixFileFormat.Pfm:
                    Pfm.Encode(ToRawImage(image, Target.Native), stream);
                    break;
                case PixFileFormat.Pbm:
                    Pnm.Encode(ToRawImage(image.ToPixImage<byte>().ToPixImage<byte>(Col.Format.Gray), Target.Byte), stream, new PnmEncodeOptions { Bilevel = true });
                    break;
                case PixFileFormat.Pgm:
                case PixFileFormat.Ppm:
                    {
                        var raw = ToRawImage(image, Target.ByteOrUShort);
                        // PGM holds gray, PPM colour; images with alpha become PAM
                        bool gray = raw.Color is ColorModel.Gray or ColorModel.GrayAlpha;
                        if (saveParams.Format == PixFileFormat.Pgm && !gray)
                            raw = ToRawImage(raw.SampleType == SampleType.UInt16 ? image.ToPixImage<ushort>().ToPixImage<ushort>(Col.Format.Gray) : image.ToPixImage<byte>().ToPixImage<byte>(Col.Format.Gray), Target.ByteOrUShort);
                        Pnm.Encode(raw, stream);
                        break;
                    }
                case PixFileFormat.Ico:
                    Ico.Encode(new[] { ToRawImage(image, Target.Byte) }, stream);
                    break;
                case PixFileFormat.Tiff:
                    Tiff.Encode(ToRawImage(image, Target.Native), stream, new TiffEncodeOptions
                    {
                        Compression = (saveParams as PixTiffSaveParams)?.Compression switch
                        {
                            null or PixTiffCompression.Default or PixTiffCompression.Deflate => TiffCompression.Deflate,
                            PixTiffCompression.None => TiffCompression.None,
                            PixTiffCompression.Lzw => TiffCompression.Lzw,
                            PixTiffCompression.PackBits => TiffCompression.PackBits,
                            var c => throw new NotSupportedException($"TIFF compression {c} is not supported by {Name}"),
                        },
                    });
                    break;
                case PixFileFormat.Exr:
                    var exr = saveParams as PixExrSaveParams;
                    Exr.Encode(ToRawImage(image, Target.Float), stream, new ExrEncodeOptions
                    {
                        LuminanceChroma = exr?.LuminanceChroma ?? false,
                        Compression = exr?.Compression switch
                        {
                            null or PixExrCompression.Default or PixExrCompression.Zip => ExrCompression.Zip,
                            PixExrCompression.None => ExrCompression.None,
                            PixExrCompression.Piz => ExrCompression.Piz,
                            PixExrCompression.Pxr24 => ExrCompression.Pxr24,
                            PixExrCompression.B44 => ExrCompression.B44,
                            var c => throw new NotSupportedException($"EXR compression {c} is not supported by {Name}"),
                        },
                    });
                    break;
                default:
                    throw new NotSupportedException($"{Name} cannot write {saveParams.Format}");
            }
        }

        #region RawImage -> PixImage

        static Type ElementType(SampleType t) => t switch
        {
            SampleType.UInt8 => typeof(byte),
            SampleType.Int8 => typeof(sbyte),
            SampleType.UInt16 => typeof(ushort),
            SampleType.Int16 => typeof(short),
            SampleType.UInt32 => typeof(uint),
            SampleType.Int32 => typeof(int),
            SampleType.Float16 or SampleType.Float32 => typeof(float),
            SampleType.Float64 => typeof(double),
            SampleType.UInt64 => typeof(ulong),
            SampleType.Int64 => typeof(long),
            _ => throw new NotSupportedException(),
        };

        static Col.Format ColFormat(ColorModel color, int channels) => color switch
        {
            ColorModel.Gray => Col.Format.Gray,
            ColorModel.GrayAlpha => Col.Format.GrayAlpha,
            ColorModel.Rgb => Col.Format.RGB,
            ColorModel.Rgba => Col.Format.RGBA,
            // two channels without colour meaning (BC5 / RG textures, two band rasters) are RG
            _ => channels switch { 1 => Col.Format.Gray, 2 => Col.Format.RG, 3 => Col.Format.RGB, 4 => Col.Format.RGBA, _ => Col.Format.None },
        };

        static PixImage<T> Wrap<T>(RawImage img, T[] data)
            => new PixImage<T>(ColFormat(img.Color, img.Channels), data.CreateImageVolume(new V3l(img.Width, img.Height, img.Channels)));

        /// <summary>
        /// Wraps the decoded samples in a PixImage without copying (half floats are widened to float). The volume always has the
        /// canonical dense image layout: channel fastest, then x, then y (DZ = 1, DX = channels, DY = width * channels, first index 0).
        /// </summary>
        public static PixImage ToPixImage(RawImage img) => img.SampleType switch
        {
            // by SampleType, not by array pattern: sbyte[] would also match `byte[]` (CLR array covariance)
            SampleType.UInt8 => Wrap(img, (byte[])img.Data),
            SampleType.UInt16 => Wrap(img, (ushort[])img.Data),
            SampleType.UInt32 => Wrap(img, (uint[])img.Data),
            SampleType.Float32 => Wrap(img, (float[])img.Data),
            SampleType.Float64 => Wrap(img, (double[])img.Data),
            SampleType.Int8 => Wrap(img, (sbyte[])img.Data),
            SampleType.Int16 => Wrap(img, (short[])img.Data),
            SampleType.Int32 => Wrap(img, (int[])img.Data),
            SampleType.UInt64 => Wrap(img, (ulong[])img.Data),
            SampleType.Int64 => Wrap(img, (long[])img.Data),
            SampleType.Float16 => Wrap(img, Array.ConvertAll((System.Half[])img.Data, h => (float)h)), // (plain Half is Aardvark's here)
            _ => throw new NotSupportedException($"unsupported sample type {img.SampleType}"),
        };

        #endregion

        #region PixImage -> RawImage

        enum Target { Native, Byte, ByteOrUShort, Float }

        static RawImage Raw<T>(PixImage<T> pi)
        {
            var fmt = pi.Format;
            Col.Format wanted = fmt switch
            {
                Col.Format.BGR => Col.Format.RGB,
                Col.Format.BGRA => Col.Format.RGBA,
                Col.Format.BW or Col.Format.Alpha => Col.Format.Gray,
                _ => fmt,
            };
            if (wanted != fmt && !(fmt is Col.Format.BW or Col.Format.Alpha)) pi = pi.ToFormat(wanted);
            pi = pi.ToImageLayout();
            var vol = pi.Volume;
            int w = (int)vol.SX, h = (int)vol.SY, c = (int)vol.SZ;
            var data = vol.Data;
            if (vol.FirstIndex != 0 || data.LongLength != (long)w * h * c)
                data = vol.CopyToImage().Data;
            var color = wanted switch
            {
                Col.Format.Gray => ColorModel.Gray,
                Col.Format.GrayAlpha => ColorModel.GrayAlpha,
                Col.Format.RGB => ColorModel.Rgb,
                Col.Format.RGBA => ColorModel.Rgba,
                Col.Format.RG => ColorModel.Other,
                _ => RawImage.DefaultColorModel(c) is var m && c <= 4 ? m : ColorModel.Other,
            };
            return new RawImage(w, h, color, data, c);
        }

        /// <summary>Views a PixImage as a RawImage (no copy when it already has RGB(A)/gray image layout).</summary>
        public static RawImage ToRawImage(PixImage image) => ToRawImage(image, Target.Native);

        static RawImage ToRawImage(PixImage image, Target target)
        {
            var t = image.PixFormat.Type;
            switch (target)
            {
                case Target.Byte:
                    if (t != typeof(byte)) image = image.ToPixImage<byte>();
                    break;
                case Target.ByteOrUShort:
                    if (t != typeof(byte) && t != typeof(ushort)) image = image.ToPixImage<ushort>();
                    break;
                case Target.Float:
                    if (t != typeof(float)) image = image.ToPixImage<float>();
                    break;
            }
            return image switch
            {
                PixImage<byte> p => Raw(p),
                PixImage<ushort> p => Raw(p),
                PixImage<uint> p => Raw(p),
                PixImage<float> p => Raw(p),
                PixImage<double> p => Raw(p),
                PixImage<sbyte> p => Raw(p),
                PixImage<short> p => Raw(p),
                PixImage<int> p => Raw(p),
                PixImage<ulong> p => Raw(p),
                PixImage<long> p => Raw(p),
                _ => throw new NotSupportedException($"unsupported PixImage type {image.PixFormat}"),
            };
        }

        #endregion
    }

    public static class PixCodec
    {
        public static PixCodecLoader Loader { get; } = new PixCodecLoader();

        /// <summary>The image processor (resize, rotate, remap) used by PixImage.Scaled / Resized / Rotated / Remapped.</summary>
        public static PixCodecProcessor Processor { get; } = new PixCodecProcessor();

        /// <summary>
        /// Registers the Aardvark.PixImage.PixCodec loader and image processor with highest priority among the currently registered ones.
        /// </summary>
        [OnAardvarkInit]
        public static void Init()
        {
            PixImage.AddLoader(Loader);
            PixImage.AddProcessor(Processor);
        }

        #region Direct loading (no loader registration needed)

        /// <summary>
        /// Decodes an image file into a PixImage of the file's sample type (PixImage&lt;byte&gt;, &lt;ushort&gt;, &lt;float&gt;, ...)
        /// without copying the samples. Half floats become float. The EXIF/TIFF orientation is already applied (the image is
        /// upright as viewers show it) and the volume has the dense image layout (channel, x, y; see <see cref="PixCodecLoader.ToPixImage"/>).
        /// </summary>
        public static PixImage Load(string path) => Convert(ImageCodec.Decode(path));
        /// <inheritdoc cref="Load(string)"/>
        public static PixImage Load(Stream stream) => Convert(ImageCodec.Decode(stream));
        /// <inheritdoc cref="Load(string)"/>
        public static PixImage Load(ReadOnlySpan<byte> data) => Convert(ImageCodec.Decode(data));

        /// <summary>Decodes into a PixImage&lt;T&gt;, converting the samples if the file has another type (e.g. 16 bit to byte).</summary>
        public static PixImage<T> Load<T>(string path) => As<T>(Load(path));
        /// <inheritdoc cref="Load{T}(string)"/>
        public static PixImage<T> Load<T>(Stream stream) => As<T>(Load(stream));
        /// <inheritdoc cref="Load{T}(string)"/>
        public static PixImage<T> Load<T>(ReadOnlySpan<byte> data) => As<T>(Load(data));

        /// <summary>
        /// Decodes every image in the file: TIFF pages, the frames of animated GIF / PNG / WebP (each the whole canvas as shown), or
        /// the single image.
        /// </summary>
        public static PixImage[] LoadAll(string path) => ConvertAll(ImageCodec.DecodeAll(path));
        /// <inheritdoc cref="LoadAll(string)"/>
        public static PixImage[] LoadAll(Stream stream) => ConvertAll(ImageCodec.DecodeAll(stream));
        /// <inheritdoc cref="LoadAll(string)"/>
        public static PixImage[] LoadAll(ReadOnlySpan<byte> data) => ConvertAll(ImageCodec.DecodeAll(data));

        /// <summary>
        /// The image with its stored mip levels (DDS mip chains, OpenEXR mip maps), largest first; files without levels give a
        /// single level. <see cref="PixImageMipMap.Create"/> computes levels.
        /// </summary>
        public static PixImageMipMap LoadMipmap(string path) => PixCodecLoader.ToMipMap(ImageCodec.DecodeMipmaps(path));
        /// <inheritdoc cref="LoadMipmap(string)"/>
        public static PixImageMipMap LoadMipmap(Stream stream) => PixCodecLoader.ToMipMap(ImageCodec.DecodeMipmaps(stream));

        /// <summary>
        /// A DDS cube map as a PixCube: each side with its mip chain (Aardvark's CubeSide order is the order DDS stores the
        /// faces in: +X, -X, +Y, -Y, +Z, -Z). Faces a legacy file leaves out are null. <paramref name="item"/> selects the cube
        /// of a cube map array.
        /// </summary>
        public static PixCube LoadCube(string path, int item = 0) => LoadCube(File.ReadAllBytes(path), item);
        /// <inheritdoc cref="LoadCube(string, int)"/>
        public static PixCube LoadCube(Stream stream, int item = 0) => LoadCube(ImageCodec.ReadAll(stream), item);

        /// <summary>A DDS, KTX or PVR cube map as a PixCube (see <see cref="LoadCube(string, int)"/>); <paramref name="item"/> is the cube (array layer).</summary>
        public static PixCube LoadCube(byte[] data, int item = 0)
        {
            if (Pvr.IsMatch(data))
            {
                var p = Pvr.DecodeTexture(data);
                if (p.Faces != 6) throw new ImageLoadException("not a cube map");
                var ps = new PixImageMipMap[6];
                for (int i = 0; i < 6; i++) ps[i] = PixCodecLoader.ToMipMap(p.GetMipChain(item, i));
                return new PixCube(ps);
            }
            if (!Ktx.IsMatch(data)) return ToCube(Dds.DecodeTexture(data), item);
            var t = Ktx.DecodeTexture(data);
            if (t.Kind != KtxTextureKind.CubeMap) throw new ImageLoadException($"not a cube map but a {t.Kind}");
            var sides = new PixImageMipMap[6];
            for (int i = 0; i < 6; i++) sides[i] = PixCodecLoader.ToMipMap(t.GetMipChain(item, i));
            return new PixCube(sides);
        }

        /// <summary>
        /// The top level of a DDS, KTX or PVR volume (3D) texture or of an .astc volume as a PixVolume of the stored sample type
        /// (half floats as float), with the dense layout channel, x, y, z. <paramref name="mip"/> picks a smaller level,
        /// <paramref name="item"/> a KTX array layer / PVR surface.
        /// </summary>
        public static PixVolume LoadVolume(string path, int mip = 0, int item = 0) => LoadVolume(File.ReadAllBytes(path), mip, item);
        /// <inheritdoc cref="LoadVolume(string, int, int)"/>
        public static PixVolume LoadVolume(Stream stream, int mip = 0, int item = 0) => LoadVolume(ImageCodec.ReadAll(stream), mip, item);
        /// <inheritdoc cref="LoadVolume(string, int, int)"/>
        public static PixVolume LoadVolume(byte[] data, int mip = 0, int item = 0)
        {
            RawImage[] slices;
            if (Astc.IsMatch(data))
            {
                if (mip != 0 || item != 0) throw new ImageLoadException(".astc files hold a single level");
                slices = Astc.DecodeVolume(data);
            }
            else if (Pvr.IsMatch(data))
            {
                var t = Pvr.DecodeTexture(data);
                if (t.Depth == 1) throw new ImageLoadException("not a volume texture");
                slices = t.GetSlices(mip, item);
            }
            else if (Ktx.IsMatch(data))
            {
                var t = Ktx.DecodeTexture(data);
                if (t.Kind != KtxTextureKind.Texture3D) throw new ImageLoadException($"not a volume texture but a {t.Kind}");
                slices = t.GetSlices(mip, item);
            }
            else
            {
                var t = Dds.DecodeTexture(data);
                if (t.Kind != DdsTextureKind.Texture3D) throw new ImageLoadException($"not a volume texture but a {t.Kind}");
                slices = t.GetSlices(mip, item);
            }
            return ToVolume(slices);
        }

        static PixVolume ToVolume(RawImage[] slices) => slices[0].SampleType switch
        {
            SampleType.UInt8 => Volume<byte>(slices, s => (byte[])s.Data),
            SampleType.UInt16 => Volume<ushort>(slices, s => (ushort[])s.Data),
            SampleType.UInt32 => Volume<uint>(slices, s => (uint[])s.Data),
            SampleType.Int8 => Volume<sbyte>(slices, s => (sbyte[])s.Data),
            SampleType.Int16 => Volume<short>(slices, s => (short[])s.Data),
            SampleType.Int32 => Volume<int>(slices, s => (int[])s.Data),
            SampleType.Float16 => Volume<float>(slices, s => Array.ConvertAll((System.Half[])s.Data, h => (float)h)),
            SampleType.Float32 => Volume<float>(slices, s => (float[])s.Data),
            SampleType.Float64 => Volume<double>(slices, s => (double[])s.Data),
            var t => throw new NotSupportedException($"unsupported sample type {t}"),
        };

        static PixVolume<T> Volume<T>(RawImage[] slices, Func<RawImage, T[]> data)
        {
            var f = slices[0];
            long w = f.Width, h = f.Height, c = f.Channels, d = slices.Length;
            var fmt = f.Color switch
            {
                ColorModel.Gray => Col.Format.Gray,
                ColorModel.GrayAlpha => Col.Format.GrayAlpha,
                ColorModel.Rgb => Col.Format.RGB,
                ColorModel.Rgba => Col.Format.RGBA,
                _ => c switch { 1 => Col.Format.Gray, 2 => Col.Format.RG, 3 => Col.Format.RGB, 4 => Col.Format.RGBA, _ => Col.Format.None },
            };
            var all = new T[w * h * c * d];
            for (int z = 0; z < d; z++) data(slices[z]).CopyTo(all, z * w * h * c);
            // channel fastest, then x, y, z
            var tensor = new Tensor4<T>(all, 0, new V4l(w, h, d, c), new V4l(c, w * c, w * h * c, 1));
            return new PixVolume<T>(fmt, tensor);
        }

        /// <summary>
        /// Saves an image as KTX 2: uncompressed or BC1 to BC7 (<see cref="KtxEncodeOptions.Compression"/>), optionally with a
        /// computed mip chain, Zstandard supercompressed by default.
        /// </summary>
        public static void SaveKtx(PixImage image, Stream stream, KtxEncodeOptions? options = null)
            => Ktx.Encode(PixCodecLoader.ToRawImage(image), stream, options);

        /// <summary>Saves a mip map as KTX 2 (every level half the size of the previous one, rounded down).</summary>
        public static void SaveKtx(PixImageMipMap mipmap, Stream stream, KtxEncodeOptions? options = null)
            => Ktx.EncodeMipmaps(Array.ConvertAll(mipmap.ImageArray, PixCodecLoader.ToRawImage), stream, options);

        /// <summary>Saves a PixCube as a KTX 2 cube map, each side with its mip chain.</summary>
        public static void SaveKtx(PixCube cube, Stream stream, KtxEncodeOptions? options = null)
            => Ktx.EncodeCube(Array.ConvertAll(cube.MipMapArray, m => (IReadOnlyList<RawImage>)Array.ConvertAll(m.ImageArray, PixCodecLoader.ToRawImage)), stream, options);

        /// <summary>Saves a PixVolume as a KTX 2 volume texture, with a 3D mip chain if <see cref="KtxEncodeOptions.GenerateMipmaps"/> is set.</summary>
        public static void SaveKtx(PixVolume volume, Stream stream, KtxEncodeOptions? options = null)
            => Ktx.EncodeVolume(VolumeSlices(volume), stream, options);

        static PixCube ToCube(DdsTexture t, int item)
        {
            if (t.Kind != DdsTextureKind.CubeMap) throw new ImageLoadException($"not a cube map but a {t.Kind}");
            var sides = new PixImageMipMap[6];
            for (int i = 0; i < t.Faces.Count; i++) sides[t.Faces[i]] = PixCodecLoader.ToMipMap(t.GetMipChain(item, i));
            return new PixCube(sides);
        }

        /// <summary>
        /// Saves an image as PVR v3: uncompressed (the image's sample type) or block compressed (BC1 to BC7, ETC2 / EAC, ASTC; see
        /// <see cref="PvrEncodeOptions.Compression"/>), optionally with a computed mip chain.
        /// </summary>
        public static void SavePvr(PixImage image, Stream stream, PvrEncodeOptions? options = null)
            => Pvr.Encode(PixCodecLoader.ToRawImage(image), stream, options);

        /// <summary>Saves a mip map as PVR (every level half the size of the previous one, rounded down).</summary>
        public static void SavePvr(PixImageMipMap mipmap, Stream stream, PvrEncodeOptions? options = null)
            => Pvr.EncodeMipmaps(Array.ConvertAll(mipmap.ImageArray, PixCodecLoader.ToRawImage), stream, options);

        /// <summary>Saves a PixCube as a PVR cube map, each side with its mip chain.</summary>
        public static void SavePvr(PixCube cube, Stream stream, PvrEncodeOptions? options = null)
            => Pvr.EncodeCube(Array.ConvertAll(cube.MipMapArray, m => (IReadOnlyList<RawImage>)Array.ConvertAll(m.ImageArray, PixCodecLoader.ToRawImage)), stream, options);

        /// <summary>Saves a PixVolume as a PVR volume texture, with a 3D mip chain if <see cref="PvrEncodeOptions.GenerateMipmaps"/> is set.</summary>
        public static void SavePvr(PixVolume volume, Stream stream, PvrEncodeOptions? options = null)
            => Pvr.EncodeVolume(VolumeSlices(volume), stream, options);

        /// <summary>Saves an image as a plain .astc file (LDR from integer images, HDR from float images).</summary>
        public static void SaveAstc(PixImage image, Stream stream, AstcEncodeOptions? options = null)
            => Astc.Encode(PixCodecLoader.ToRawImage(image), stream, options);

        /// <summary>Saves a PixVolume as an .astc volume (2D footprint, slice by slice).</summary>
        public static void SaveAstc(PixVolume volume, Stream stream, AstcEncodeOptions? options = null)
            => Astc.EncodeVolume(VolumeSlices(volume), stream, options);

        /// <summary>Saves square images (16 to 1024 pixels) as an Apple icon file, see <see cref="Icns.Encode(IReadOnlyList{RawImage}, Stream, IcnsEncodeOptions?)"/>.</summary>
        public static void SaveIcns(IEnumerable<PixImage> images, Stream stream, IcnsEncodeOptions? options = null)
            => Icns.Encode(System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(images, i => PixCodecLoader.ToRawImage(i.PixFormat.Type == typeof(byte) ? i : i.ToPixImage<byte>()))), stream, options);

        /// <summary>Saves an animated cursor (ANI): one step per image, each shown for <paramref name="jiffies"/> (1/60 s).</summary>
        public static void SaveAni(IEnumerable<PixImage> steps, Stream stream, int jiffies = 10, AniEncodeOptions? options = null)
            => Ani.Encode(System.Linq.Enumerable.Select(steps, i => new AniInputFrame(PixCodecLoader.ToRawImage(i.PixFormat.Type == typeof(byte) ? i : i.ToPixImage<byte>()), jiffies)), stream, options);

        /// <summary>
        /// Saves an image as DDS: uncompressed, or block compressed (BC1 to BC7) per <see cref="DdsEncodeOptions.Compression"/>,
        /// optionally with a computed mip chain (<see cref="DdsEncodeOptions.Mipmaps"/>).
        /// </summary>
        public static void SaveDds(PixImage image, Stream stream, DdsEncodeOptions? options = null)
            => Dds.Encode(PixCodecLoader.ToRawImage(image), stream, options);

        /// <summary>Saves a mip map as DDS (every level half the size of the previous one, rounded down).</summary>
        public static void SaveDds(PixImageMipMap mipmap, Stream stream, DdsEncodeOptions? options = null)
            => Dds.EncodeMipmaps(Array.ConvertAll(mipmap.ImageArray, PixCodecLoader.ToRawImage), stream, options);

        /// <summary>Saves a PixCube as a DDS cube map, each side with its mip chain.</summary>
        public static void SaveDds(PixCube cube, Stream stream, DdsEncodeOptions? options = null)
            => Dds.EncodeCube(Array.ConvertAll(cube.MipMapArray, m => (IReadOnlyList<RawImage>)Array.ConvertAll(m.ImageArray, PixCodecLoader.ToRawImage)), stream, options);

        /// <summary>
        /// Saves a PixVolume as a DDS volume texture (no mip levels); block compression, if chosen, applies to each slice.
        /// </summary>
        public static void SaveDds(PixVolume volume, Stream stream, DdsEncodeOptions? options = null)
            => Dds.EncodeVolume(VolumeSlices(volume), stream, options);

        /// <summary>The z slices of a PixVolume as RawImages (BGR(A) reordered to RGB(A)).</summary>
        public static RawImage[] VolumeSlices(PixVolume volume) => volume switch
        {
            PixVolume<byte> v => Slices(v),
            PixVolume<ushort> v => Slices(v),
            PixVolume<uint> v => Slices(v),
            PixVolume<float> v => Slices(v),
            PixVolume<double> v => Slices(v),
            PixVolume<sbyte> v => Slices(v),
            PixVolume<short> v => Slices(v),
            PixVolume<int> v => Slices(v),
            PixVolume<ulong> v => Slices(v),
            PixVolume<long> v => Slices(v),
            _ => throw new NotSupportedException($"unsupported PixVolume type {volume.GetType()}"),
        };

        static RawImage[] Slices<T>(PixVolume<T> v)
        {
            var t = v.Tensor4;
            long w = t.Size.X, h = t.Size.Y, d = t.Size.Z, c = t.Size.W;
            var dl = t.Delta;
            var src = t.Data;
            long f0 = t.FirstIndex;
            bool bgr = v.Format is Col.Format.BGR or Col.Format.BGRA && c >= 3;
            var color = v.Format switch
            {
                Col.Format.Gray or Col.Format.BW or Col.Format.Alpha => ColorModel.Gray,
                Col.Format.GrayAlpha => ColorModel.GrayAlpha,
                Col.Format.RGB or Col.Format.BGR => ColorModel.Rgb,
                Col.Format.RGBA or Col.Format.BGRA => ColorModel.Rgba,
                Col.Format.RG => ColorModel.Other,
                _ => c <= 4 ? RawImage.DefaultColorModel((int)c) : ColorModel.Other,
            };
            var r = new RawImage[d];
            for (long z = 0; z < d; z++)
            {
                var data = new T[w * h * c];
                long o = 0;
                for (long y = 0; y < h; y++)
                    for (long x = 0; x < w; x++)
                        for (long k = 0; k < c; k++)
                        {
                            long sk = bgr && k < 3 ? 2 - k : k;
                            data[o++] = src[f0 + x * dl.X + y * dl.Y + z * dl.Z + sk * dl.W];
                        }
                r[z] = new RawImage((int)w, (int)h, color, data, (int)c);
            }
            return r;
        }

        /// <summary>
        /// Reads the descriptive metadata without decoding pixels: EXIF, XMP, IPTC, comments, and the values commonly needed
        /// from them (camera, exposure, dates, GPS, drone gimbal / flight attitude, description). See <see cref="PhotoMetadata"/>.
        /// </summary>
        public static PhotoMetadata ReadMetadata(string path) => ImageCodec.ReadMetadata(path);
        /// <inheritdoc cref="ReadMetadata(string)"/>
        public static PhotoMetadata ReadMetadata(Stream stream) => ImageCodec.ReadMetadata(stream);

        /// <summary>
        /// Sets, changes or removes metadata (GPS, dates, camera, description, any EXIF tag / XMP property / IPTC dataset) of an
        /// image file without re-encoding its pixels (JPEG, PNG, TIFF, WebP, JPEG 2000). <paramref name="outputPath"/> may be
        /// the input path.
        /// </summary>
        public static void WriteMetadata(string inputPath, string outputPath, MetadataEdit edit) => ImageCodec.WriteMetadata(inputPath, outputPath, edit);

        /// <summary>
        /// Loads an image converted to sRGB through its embedded ICC profile (Adobe RGB, Display P3, ProPhoto, CMYK, ...); images
        /// without a profile are returned as they are. 8 and 16 bit images keep their sample type.
        /// </summary>
        public static PixImage LoadSrgb(string path) => Convert(ImageOps.ToSrgb(ImageCodec.Decode(path)));
        /// <inheritdoc cref="LoadSrgb(string)"/>
        public static PixImage LoadSrgb(Stream stream) => Convert(ImageOps.ToSrgb(ImageCodec.Decode(stream)));

        /// <summary>Reads only the headers: format, size, channels, sample type, orientation, dpi, ... (see <see cref="ImageInfo"/>).</summary>
        public static ImageInfo GetInfo(string path) => ImageCodec.GetInfo(path);
        /// <inheritdoc cref="GetInfo(string)"/>
        public static ImageInfo GetInfo(Stream stream) => ImageCodec.GetInfo(stream);

        /// <summary>Saves in the format given by the file extension, with default or the given parameters.</summary>
        public static void Save(PixImage image, string path, PixSaveParams? saveParams = null)
        {
            // formats without a PixFileFormat
            var codec = ImageCodec.FormatFromExtension(path);
            if (saveParams == null && codec is ImageFormat.Qoi or ImageFormat.Ktx or ImageFormat.Pvr or ImageFormat.Astc or ImageFormat.Icns or ImageFormat.Ani)
            {
                using var fs = File.Create(path);
                var bytes = image.PixFormat.Type == typeof(byte) ? image : image.ToPixImage<byte>();
                switch (codec)
                {
                    case ImageFormat.Qoi: Qoi.Encode(PixCodecLoader.ToRawImage(bytes), fs); break;
                    case ImageFormat.Ktx: Ktx.Encode(PixCodecLoader.ToRawImage(image), fs); break;
                    case ImageFormat.Pvr: Pvr.Encode(PixCodecLoader.ToRawImage(image), fs); break;
                    case ImageFormat.Astc: Astc.Encode(PixCodecLoader.ToRawImage(image), fs); break;
                    case ImageFormat.Icns: Icns.Encode(new[] { PixCodecLoader.ToRawImage(bytes) }, fs); break;
                    default: Ani.Encode(new[] { new AniInputFrame(PixCodecLoader.ToRawImage(bytes)) }, fs); break;
                }
                return;
            }
            var p = saveParams ?? new PixSaveParams(FileFormat(path));
            Loader.SaveToFile(path, image, p);
        }

        /// <summary>Saves to a stream in the given format.</summary>
        public static void Save(PixImage image, Stream stream, PixFileFormat format)
            => Loader.SaveToStream(stream, image, new PixSaveParams(format));

        static PixFileFormat FileFormat(string path) => ImageCodec.FormatFromExtension(path) switch
        {
            ImageFormat.Png => PixFileFormat.Png,
            ImageFormat.Jpeg => PixFileFormat.Jpeg,
            ImageFormat.Tiff => PixFileFormat.Tiff,
            ImageFormat.Gif => PixFileFormat.Gif,
            ImageFormat.Exr => PixFileFormat.Exr,
            ImageFormat.Webp => PixFileFormat.Webp,
            ImageFormat.Jp2 => Path.GetExtension(path).ToLowerInvariant() is ".j2k" or ".j2c" or ".jph" ? PixFileFormat.J2k : PixFileFormat.Jp2,
            ImageFormat.Bmp => PixFileFormat.Bmp,
            ImageFormat.Tga => PixFileFormat.Targa,
            ImageFormat.Dds => PixFileFormat.Dds,
            ImageFormat.Hdr => PixFileFormat.Hdr,
            ImageFormat.Pfm => PixFileFormat.Pfm,
            ImageFormat.Pnm => Path.GetExtension(path).ToLowerInvariant() switch { ".pbm" => PixFileFormat.Pbm, ".pgm" => PixFileFormat.Pgm, _ => PixFileFormat.Ppm },
            ImageFormat.Ico => PixFileFormat.Ico,
            _ => throw new ArgumentException($"cannot infer the image format from '{path}'"),
        };

        static PixImage Convert(RawImage img) => PixCodecLoader.ToPixImage(img);

        static PixImage[] ConvertAll(RawImage[] imgs) => Array.ConvertAll(imgs, Convert);

        static PixImage<T> As<T>(PixImage pi) => pi as PixImage<T> ?? pi.ToPixImage<T>();

        #endregion
    }

    /// <summary>Conversions between Aardvark.PixImage.PixCodec's RawImage and PixImage.</summary>
    public static class PixCodecRawImageExtensions
    {
        /// <summary>
        /// Wraps the decoded samples in a PixImage without copying (half floats are widened to float). The volume always has the
        /// canonical dense image layout: channel fastest, then x, then y (DZ = 1, DX = channels, DY = width * channels, first index 0).
        /// </summary>
        public static PixImage ToPixImage(this RawImage image) => PixCodecLoader.ToPixImage(image);

        /// <summary>Views a PixImage as a RawImage (no copy when it already has RGB(A)/gray image layout; BGR(A) is reordered).</summary>
        public static RawImage ToRawImage(this PixImage image) => PixCodecLoader.ToRawImage(image);
    }
}
