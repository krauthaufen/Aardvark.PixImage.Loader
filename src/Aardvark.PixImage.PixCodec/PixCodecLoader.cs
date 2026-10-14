using System;
using System.IO;
using PixCodec;

namespace Aardvark.Base
{
    /// <summary>
    /// PixImage loader backed by the fully managed PixCodec library (PNG, JPEG, TIFF, GIF, OpenEXR, WebP).
    /// Register with <see cref="PixImagePixCodec.Init"/> (done automatically by Aardvark.Init()).
    /// </summary>
    public sealed class PixCodecLoader : IPixLoader
    {
        public string Name => "PixCodec";
        public bool CanEncode => true;
        public bool CanDecode => true;

        public PixImage LoadFromFile(string filename) => ToPixImage(ImageCodec.Decode(filename));
        public PixImage LoadFromStream(Stream stream) => ToPixImage(ImageCodec.Decode(stream));

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
                case PixFileFormat.Gif:
                    Gif.Encode(ToRawImage(image, Target.Byte), stream);
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
            _ => channels switch { 1 => Col.Format.Gray, 2 => Col.Format.GrayAlpha, 3 => Col.Format.RGB, 4 => Col.Format.RGBA, _ => Col.Format.None },
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

    public static class PixImagePixCodec
    {
        public static PixCodecLoader Loader { get; } = new PixCodecLoader();

        /// <summary>The image processor (resize, rotate, remap) used by PixImage.Scaled / Resized / Rotated / Remapped.</summary>
        public static PixCodecProcessor Processor { get; } = new PixCodecProcessor();

        /// <summary>
        /// Registers the PixCodec loader and image processor with highest priority among the currently registered ones.
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

        /// <summary>Reads only the headers: format, size, channels, sample type, orientation, dpi, ... (see <see cref="ImageInfo"/>).</summary>
        public static ImageInfo GetInfo(string path) => ImageCodec.GetInfo(path);
        /// <inheritdoc cref="GetInfo(string)"/>
        public static ImageInfo GetInfo(Stream stream) => ImageCodec.GetInfo(stream);

        /// <summary>Saves in the format given by the file extension, with default or the given parameters.</summary>
        public static void Save(PixImage image, string path, PixSaveParams? saveParams = null)
        {
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
            _ => throw new ArgumentException($"cannot infer the image format from '{path}'"),
        };

        static PixImage Convert(RawImage img) => PixCodecLoader.ToPixImage(img);

        static PixImage[] ConvertAll(RawImage[] imgs) => Array.ConvertAll(imgs, Convert);

        static PixImage<T> As<T>(PixImage pi) => pi as PixImage<T> ?? pi.ToPixImage<T>();

        #endregion
    }

    /// <summary>Conversions between PixCodec's RawImage and PixImage.</summary>
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
