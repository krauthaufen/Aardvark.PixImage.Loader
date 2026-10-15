using Aardvark.Base;
using Half = Aardvark.Base.Half;
using System;
using System.Runtime.InteropServices;
using Aardvark.ImageCodecs;

namespace Aardvark.Data
{
    /// <summary>
    /// PixImage processor backed by Aardvark.PixImage.PixCodec's <see cref="ImageOps"/>: scaling (antialiased when downscaling), rotation and
    /// remapping for every sample type and channel count, alpha premultiplied while filtering. Registered by
    /// <see cref="PixCodec.Init"/>. ImageInterpolation maps to: Near = nearest, Linear = bilinear,
    /// Cubic = bicubic (Catmull-Rom), Lanczos = Lanczos3, SuperSample = box (area average).
    /// </summary>
    public sealed class PixCodecProcessor : IPixProcessor
    {
        public string Name => "PixCodec";
        public PixProcessorCaps Capabilities => PixProcessorCaps.All;

        public PixImage<T>? Scale<T>(PixImage<T> image, V2d scaleFactor, ImageInterpolation interpolation)
        {
            var raw = View(image, out int alpha, out bool premultiplied);
            if (raw == null) return null;
            // output size as Aardvark computes it: round(size * scale)
            int w = Math.Max(1, (int)Math.Floor(0.5 + scaleFactor.X * raw.Width));
            int h = Math.Max(1, (int)Math.Floor(0.5 + scaleFactor.Y * raw.Height));
            var r = ImageOps.Resize(raw, w, h, new ResizeOptions
            {
                Filter = Filter(interpolation),
                AlphaChannel = alpha,
                PremultiplyAlpha = !premultiplied,
            });
            return Back(image, r);
        }

        public PixImage<T>? Rotate<T>(PixImage<T> image, double angleInRadians, bool resize, ImageInterpolation interpolation,
                                     ImageBorderType borderType = ImageBorderType.Const, T border = default!)
        {
            var raw = View(image, out int alpha, out bool premultiplied);
            if (raw == null) return null;
            var r = ImageOps.Rotate(raw, angleInRadians * 180 / Math.PI, resize, WarpOptions(interpolation, borderType, border, alpha, premultiplied));
            return Back(image, r);
        }

        public PixImage<T>? Remap<T>(PixImage<T> image, Matrix<float> mapX, Matrix<float> mapY, ImageInterpolation interpolation,
                                    ImageBorderType borderType = ImageBorderType.Const, T border = default!)
        {
            if (mapX.Size != mapY.Size) throw new ArgumentException("mapX and mapY must have the same size");
            var raw = View(image, out int alpha, out bool premultiplied);
            if (raw == null) return null;
            int w = (int)mapX.SX, h = (int)mapX.SY;
            var mx = new float[(long)w * h];
            var my = new float[mx.LongLength];
            for (int y = 0, i = 0; y < h; y++)
                for (int x = 0; x < w; x++, i++) { mx[i] = mapX[x, y]; my[i] = mapY[x, y]; }
            var r = ImageOps.Remap(raw, w, h, mx, my, WarpOptions(interpolation, borderType, border, alpha, premultiplied));
            return Back(image, r);
        }

        /// <summary>The Aardvark.PixImage.PixCodec filter used for an Aardvark interpolation mode.</summary>
        public static ResampleFilter Filter(ImageInterpolation interpolation) => (interpolation & ~ImageInterpolation.SmoothEdge) switch
        {
            ImageInterpolation.Near => ResampleFilter.Nearest,
            ImageInterpolation.Linear => ResampleFilter.Bilinear,
            ImageInterpolation.Cubic => ResampleFilter.Bicubic,
            ImageInterpolation.Lanczos => ResampleFilter.Lanczos3,
            ImageInterpolation.SuperSample => ResampleFilter.Box,
            var i => throw new NotSupportedException($"interpolation {i} is not supported"),
        };

        /// <summary>The Aardvark.PixImage.PixCodec border mode for an Aardvark border type.</summary>
        public static BorderMode Border(ImageBorderType borderType) => borderType switch
        {
            ImageBorderType.Const => BorderMode.Constant,
            ImageBorderType.Wrap => BorderMode.Wrap,
            ImageBorderType.Mirror => BorderMode.Mirror,
            ImageBorderType.MirrorR or ImageBorderType.Mirror2 => BorderMode.MirrorRepeat,
            _ => BorderMode.Clamp,
        };

        static WarpOptions WarpOptions<T>(ImageInterpolation interpolation, ImageBorderType borderType, T border, int alpha, bool premultiplied)
            => new WarpOptions
            {
                Filter = Filter(interpolation),
                Border = Border(borderType),
                BorderValue = new[] { ToDouble(border) },
                AlphaChannel = alpha,
                PremultiplyAlpha = !premultiplied,
            };

        static double ToDouble<T>(T v) => v switch
        {
            Half h => (float)h,
            IConvertible c => c.ToDouble(null),
            _ => 0,
        };

        #region PixImage <-> RawImage without colour reordering

        /// <summary>
        /// The samples of a PixImage as a RawImage in its own channel order (null if the sample type is not supported). Copies
        /// only if the volume does not have dense image layout, or for Aardvark's Half.
        /// </summary>
        internal static RawImage? View<T>(PixImage<T> image, out int alpha, out bool premultiplied)
        {
            var fmt = image.Format;
            alpha = fmt switch
            {
                Col.Format.GrayAlpha => 1,
                Col.Format.RGBA or Col.Format.BGRA or Col.Format.RGBP or Col.Format.BGRP => 3,
                _ => -1,
            };
            premultiplied = fmt is Col.Format.RGBP or Col.Format.BGRP;
            var vol = image.Volume;
            if (!vol.HasImageLayout() || vol.FirstIndex != 0 || vol.Data.LongLength != vol.SX * vol.SY * vol.SZ)
                vol = vol.CopyToImage();
            int w = (int)vol.SX, h = (int)vol.SY, c = (int)vol.SZ;
            if (alpha >= c) alpha = -1;
            Array data = vol.Data;
            if (typeof(T) == typeof(Half))
                data = MemoryMarshal.Cast<Half, System.Half>(((Half[])(object)vol.Data).AsSpan()).ToArray();
            else if (!Supported(typeof(T)))
                return null;
            var color = c <= 4 && alpha == (c == 2 || c == 4 ? c - 1 : -1) ? RawImage.DefaultColorModel(c) : ColorModel.Other;
            return new RawImage(w, h, color, data, c);
        }

        static bool Supported(Type t)
            => t == typeof(byte) || t == typeof(sbyte) || t == typeof(ushort) || t == typeof(short) || t == typeof(uint) || t == typeof(int)
            || t == typeof(ulong) || t == typeof(long) || t == typeof(float) || t == typeof(double);

        internal static PixImage<T> Back<T>(PixImage<T> like, RawImage raw)
        {
            T[] data = typeof(T) == typeof(Half)
                ? (T[])(object)MemoryMarshal.Cast<System.Half, Half>(((System.Half[])raw.Data).AsSpan()).ToArray()
                : (T[])raw.Data;
            return new PixImage<T>(like.Format, data.CreateImageVolume(new V3l(raw.Width, raw.Height, raw.Channels)));
        }

        #endregion
    }

    /// <summary>
    /// Resampling with Aardvark.PixImage.PixCodec filters on PixImages beyond the Aardvark ImageInterpolation modes (Mitchell, B-spline, Lanczos2,
    /// Hermite, Gaussian, custom), plus orientation handling.
    /// </summary>
    public static class PixCodecPixImageExtensions
    {
        /// <summary>Resizes to width x height with the given filter (antialiased when downscaling, alpha premultiplied).</summary>
        public static PixImage<T> Resized<T>(this PixImage<T> image, int width, int height, ResampleFilter filter)
            => image.Resized(width, height, new ResizeOptions { Filter = filter });

        /// <summary>Resizes to width x height with the given options. The alpha channel is taken from the PixImage format.</summary>
        public static PixImage<T> Resized<T>(this PixImage<T> image, int width, int height, ResizeOptions options)
        {
            var raw = PixCodecProcessor.View(image, out int alpha, out bool premultiplied) ?? throw Unsupported<T>();
            var o = new ResizeOptions
            {
                Filter = options.Filter,
                AlphaChannel = options.AlphaChannel ?? alpha,
                PremultiplyAlpha = options.PremultiplyAlpha && !premultiplied,
                LinearLight = options.LinearLight,
                MaxDegreeOfParallelism = options.MaxDegreeOfParallelism,
            };
            return PixCodecProcessor.Back(image, ImageOps.Resize(raw, width, height, o));
        }

        /// <summary>Scales by a factor with the given filter (size rounded to the nearest integer).</summary>
        public static PixImage<T> Scaled<T>(this PixImage<T> image, double factor, ResampleFilter filter)
            => image.Resized(Math.Max(1, (int)Math.Floor(image.Size.X * factor + 0.5)), Math.Max(1, (int)Math.Floor(image.Size.Y * factor + 0.5)), filter);

        /// <summary>
        /// Rotates counter-clockwise about the centre by an arbitrary angle (radians, as PixImage.Rotated), with any filter and border.
        /// </summary>
        public static PixImage<T> Rotated<T>(this PixImage<T> image, double angleInRadians, bool resize, WarpOptions options)
        {
            var raw = PixCodecProcessor.View(image, out int alpha, out bool premultiplied) ?? throw Unsupported<T>();
            var o = Adjust(options, alpha, premultiplied);
            return PixCodecProcessor.Back(image, ImageOps.Rotate(raw, angleInRadians * 180 / Math.PI, resize, o));
        }

        /// <summary>
        /// Affine warp; <paramref name="dstToSrc"/> maps destination pixel (x, y) to (a x + b y + c, d x + e y + f), pixel centres at
        /// integer coordinates.
        /// </summary>
        public static PixImage<T> WarpedAffine<T>(this PixImage<T> image, int width, int height, double[] dstToSrc, WarpOptions? options = null)
        {
            var raw = PixCodecProcessor.View(image, out int alpha, out bool premultiplied) ?? throw Unsupported<T>();
            return PixCodecProcessor.Back(image, ImageOps.WarpAffine(raw, width, height, dstToSrc, Adjust(options, alpha, premultiplied)));
        }

        /// <summary>Perspective warp with a row-major 3x3 homography from destination to source pixel coordinates.</summary>
        public static PixImage<T> WarpedPerspective<T>(this PixImage<T> image, int width, int height, double[] dstToSrc, WarpOptions? options = null)
        {
            var raw = PixCodecProcessor.View(image, out int alpha, out bool premultiplied) ?? throw Unsupported<T>();
            return PixCodecProcessor.Back(image, ImageOps.WarpPerspective(raw, width, height, dstToSrc, Adjust(options, alpha, premultiplied)));
        }

        /// <summary>Turns an image with EXIF/TIFF orientation 1..8 upright (lossless; 0 or 1 returns the image itself).</summary>
        public static PixImage<T> Oriented<T>(this PixImage<T> image, int orientation)
        {
            if (orientation <= 1) return image;
            var raw = PixCodecProcessor.View(image, out _, out _) ?? throw Unsupported<T>();
            return PixCodecProcessor.Back(image, ImageOps.ApplyOrientation(raw, orientation));
        }

        static WarpOptions Adjust(WarpOptions? options, int alpha, bool premultiplied)
        {
            options ??= new WarpOptions();
            return new WarpOptions
            {
                Filter = options.Filter,
                Border = options.Border,
                BorderValue = options.BorderValue,
                AlphaChannel = options.AlphaChannel ?? alpha,
                PremultiplyAlpha = options.PremultiplyAlpha && !premultiplied,
                MaxDegreeOfParallelism = options.MaxDegreeOfParallelism,
            };
        }

        static NotSupportedException Unsupported<T>() => new NotSupportedException($"PixImage<{typeof(T).Name}> is not supported by Aardvark.PixImage.PixCodec");
    }
}
