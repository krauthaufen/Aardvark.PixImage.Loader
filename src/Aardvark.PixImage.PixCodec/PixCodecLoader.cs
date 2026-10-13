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

        /// <summary>Wraps the decoded samples in a PixImage without copying (half floats are widened to float).</summary>
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
            SampleType.Float16 => Wrap(img, Array.ConvertAll((Half[])img.Data, h => (float)h)),
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

        /// <summary>Registers the PixCodec loader with highest priority among the currently registered loaders.</summary>
        [OnAardvarkInit]
        public static void Init() => PixImage.AddLoader(Loader);
    }
}
