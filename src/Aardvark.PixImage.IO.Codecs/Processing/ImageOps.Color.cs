using System;
using System.Threading.Tasks;
using Aardvark.ImageCodecs.Processing;

namespace Aardvark.ImageCodecs
{
    public static partial class ImageOps
    {
        #region Transfer functions

        /// <summary>
        /// Converts sRGB encoded colour to linear light: the result is Float32 with colour and alpha in 0..1 (integer samples are
        /// divided by their maximum, float samples are taken as normalised sRGB values). Alpha is only rescaled. Same as
        /// <see cref="ToLinear"/> with <see cref="TransferFunction.Srgb"/>.
        /// </summary>
        public static RawImage SrgbToLinear(RawImage image) => ToLinear(image, TransferFunction.Srgb);

        /// <summary>
        /// Converts colour to linear light according to <paramref name="transfer"/> (default: the image's
        /// <see cref="ImageMetadata.Transfer"/>; Unknown is taken as sRGB for 8 / 16 bit and as linear for float samples). The
        /// result is Float32 with values relative to the largest code value (1.0 = white, or 10000 cd/m² for PQ); alpha is only
        /// rescaled to 0..1. Images of colour model Other are converted in every channel.
        /// </summary>
        public static RawImage ToLinear(RawImage image, TransferFunction? transfer = null)
        {
            ArgumentNullException.ThrowIfNull(image);
            var t = transfer ?? image.Metadata?.Transfer ?? TransferFunction.Unknown;
            bool isFloat = image.SampleType is SampleType.Float16 or SampleType.Float32 or SampleType.Float64;
            if (t == TransferFunction.Unknown) t = isFloat ? TransferFunction.Linear : TransferFunction.Srgb;
            double gamma = image.Metadata?.Gamma ?? 0;
            if (t == TransferFunction.Gamma && !(gamma > 0 && gamma < 100)) throw new ArgumentException("TransferFunction.Gamma needs ImageMetadata.Gamma");
            Func<double, double> f = t switch
            {
                TransferFunction.Srgb => Samples.SrgbDecode,
                TransferFunction.Linear => v => v,
                TransferFunction.Gamma => v => v <= 0 ? 0 : Math.Pow(v, 1 / gamma),
                TransferFunction.Pq => PqEotf,
                TransferFunction.Hlg => HlgInverseOetf,
                _ => throw new ArgumentOutOfRangeException(nameof(transfer)),
            };
            var r = Map(image, f, SampleType.Float32, 1.0);
            r.Metadata!.Transfer = TransferFunction.Linear;
            r.Metadata.Gamma = 0;
            return r;
        }

        /// <summary>
        /// Encodes linear light (float 0..1, or integer samples divided by their maximum) with the sRGB curve into
        /// <paramref name="type"/> (UInt8, UInt16, Float16 or Float32). Integer results are rounded and clamped. Alpha is only
        /// rescaled.
        /// </summary>
        public static RawImage LinearToSrgb(RawImage image, SampleType type = SampleType.UInt8)
        {
            ArgumentNullException.ThrowIfNull(image);
            if (type is not (SampleType.UInt8 or SampleType.UInt16 or SampleType.Float16 or SampleType.Float32))
                throw new ArgumentException("LinearToSrgb produces UInt8, UInt16, Float16 or Float32", nameof(type));
            var r = Map(image, v => Samples.SrgbEncode(Math.Clamp(v, 0, 1)), type, Samples.MaxValue(type));
            r.Metadata!.Transfer = TransferFunction.Srgb;
            r.Metadata.Gamma = 0;
            return r;
        }

        // SMPTE ST 2084: code value 0..1 -> luminance relative to 10000 cd/m²
        static double PqEotf(double e)
        {
            const double m1 = 2610.0 / 16384, m2 = 2523.0 / 4096 * 128, c1 = 3424.0 / 4096, c2 = 2413.0 / 4096 * 32, c3 = 2392.0 / 4096 * 32;
            double p = Math.Pow(Math.Clamp(e, 0, 1), 1 / m2);
            return Math.Pow(Math.Max(p - c1, 0) / (c2 - c3 * p), 1 / m1);
        }

        // ARIB STD-B67 / BT.2100 HLG: signal 0..1 -> scene linear light 0..1
        static double HlgInverseOetf(double e)
        {
            const double a = 0.17883277, b = 0.28466892, c = 0.55991073;
            e = Math.Clamp(e, 0, 1);
            return e <= 0.5 ? e * e / 3 : (Math.Exp((e - c) / a) + b) / 12;
        }

        /// <summary>Applies <paramref name="f"/> to the normalised colour samples (alpha only rescaled) and stores them as <paramref name="type"/> times <paramref name="scale"/>.</summary>
        static RawImage Map(RawImage image, Func<double, double> f, SampleType type, double scale)
        {
            int ch = image.Channels;
            int alpha = image.HasAlpha ? ch - 1 : -1;
            double max = Samples.MaxValue(image.SampleType);
            if (image.SampleType is SampleType.Int8 or SampleType.Int16 or SampleType.Int32 or SampleType.Int64 or SampleType.UInt32 or SampleType.UInt64)
                throw new ArgumentException($"{image.SampleType} samples have no colour meaning; use UInt8, UInt16 or float samples");
            var src = image.Data;
            var dst = RawImage.CreateArray(type, src.Length);
            // tables for the integer inputs
            double[]? lut = null;
            if (image.SampleType is SampleType.UInt8 or SampleType.UInt16)
            {
                lut = new double[(int)max + 1];
                for (int i = 0; i < lut.Length; i++) lut[i] = f(i / max);
            }
            int rowLen = image.Width * ch;
            Parallel.For(0, image.Height, () => new double[rowLen], (y, _, row) =>
            {
                int o = y * rowLen;
                switch (src)
                {
                    case byte[] b: for (int i = 0; i < rowLen; i++) row[i] = i % ch == alpha ? b[o + i] / max : lut![b[o + i]]; break;
                    case ushort[] u: for (int i = 0; i < rowLen; i++) row[i] = i % ch == alpha ? u[o + i] / max : lut![u[o + i]]; break;
                    case Half[] h: for (int i = 0; i < rowLen; i++) row[i] = i % ch == alpha ? (double)h[o + i] : f((double)h[o + i]); break;
                    case float[] s: for (int i = 0; i < rowLen; i++) row[i] = i % ch == alpha ? s[o + i] : f(s[o + i]); break;
                    case double[] d: for (int i = 0; i < rowLen; i++) row[i] = i % ch == alpha ? d[o + i] : f(d[o + i]); break;
                }
                switch (dst)
                {
                    case byte[] b: for (int i = 0; i < rowLen; i++) b[o + i] = (byte)Math.Clamp(Math.Floor(row[i] * scale + 0.5), 0, 255); break;
                    case ushort[] u: for (int i = 0; i < rowLen; i++) u[o + i] = (ushort)Math.Clamp(Math.Floor(row[i] * scale + 0.5), 0, 65535); break;
                    case Half[] h: for (int i = 0; i < rowLen; i++) h[o + i] = (Half)(row[i] * scale); break;
                    case float[] s: for (int i = 0; i < rowLen; i++) s[o + i] = (float)(row[i] * scale); break;
                }
                return row;
            }, _ => { });
            var meta = new ImageMetadata();
            if (image.Metadata is { } m)
            {
                meta.IccProfile = m.IccProfile; meta.Exif = m.Exif; meta.Xmp = m.Xmp; meta.DpiX = m.DpiX; meta.DpiY = m.DpiY;
                meta.Orientation = m.Orientation;
            }
            return new RawImage(image.Width, image.Height, image.Color, dst, ch, meta);
        }

        #endregion
    }
}
