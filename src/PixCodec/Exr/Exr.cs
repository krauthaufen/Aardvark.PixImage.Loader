using System;
using System.Collections.Generic;
using System.IO;

namespace PixCodec
{
    public sealed class ExrDecodeOptions
    {
        /// <summary>Convert half float channels to float32 (default). If false, all-half images are returned as Float16.</summary>
        public bool HalfAsFloat { get; set; } = true;

        /// <summary>Which part of a multi-part file to decode (default: the first).</summary>
        public int PartIndex { get; set; }

        /// <summary>
        /// Layer to decode with <see cref="Exr.Decode"/>, e.g. "diffuse" for the channels "diffuse.R", "diffuse.G", ...
        /// ("" is the root layer). Null picks a default, see <see cref="Exr.Decode"/>.
        /// </summary>
        public string? Layer { get; set; }

        /// <summary>Decode chunks on multiple threads when the image is large enough (default true).</summary>
        public bool Parallel { get; set; } = true;
    }

    public sealed class ExrEncodeOptions
    {
        /// <summary>Compression (default Zip). All members except Dwaa / Dwab can be written.</summary>
        public ExrCompression Compression { get; set; } = ExrCompression.Zip;

        /// <summary>
        /// Pixel type to store. Null (default): Float32 and Float16 images are stored as HALF (the OpenEXR convention),
        /// UInt32 images as UINT. Float32 / Float16 images can be stored as Half or Float; UInt32 only as UInt.
        /// When float32 samples are stored as HALF they are rounded to nearest even; finite values beyond the half
        /// range are clamped to +-65504, infinities and NaNs are kept.
        /// </summary>
        public ExrPixelType? PixelType { get; set; }

        /// <summary>
        /// Channel names, one per image channel in image order. Required for <see cref="ColorModel.Other"/>; for the
        /// other color models it overrides the default names (Y / Y,A / R,G,B / R,G,B,A).
        /// </summary>
        public IReadOnlyList<string>? ChannelNames { get; set; }

        /// <summary>Order in which the scan line blocks are written (default increasing Y). RandomY is written as increasing.</summary>
        public ExrLineOrder LineOrder { get; set; } = ExrLineOrder.IncreasingY;

        /// <summary>zlib level 0..9 for Zip, Zips and Pxr24.</summary>
        public int ZipLevel { get; set; } = 6;

        /// <summary>Position of the top left pixel in the data window (default 0,0).</summary>
        public int DataWindowX { get; set; }
        public int DataWindowY { get; set; }

        /// <summary>Display window; null (default) makes it (0,0)-(width-1,height-1).</summary>
        public ExrBox2i? DisplayWindow { get; set; }

        /// <summary>
        /// Additional header attributes. "displayWindow", "pixelAspectRatio", "screenWindowCenter" and
        /// "screenWindowWidth" replace the defaults; the structural attributes (channels, compression, dataWindow,
        /// lineOrder, tiles, type, chunkCount, version) cannot be supplied.
        /// </summary>
        public IList<ExrAttribute> Attributes { get; } = new List<ExrAttribute>();

        /// <summary>Compress chunks on multiple threads when the image is large enough (default true).</summary>
        public bool Parallel { get; set; } = true;
    }

    /// <summary>A named channel for <see cref="Exr.EncodeChannels"/>: samples are read from Data[Offset + i * Stride].</summary>
    public sealed class ExrChannelData
    {
        public string Name { get; }
        /// <summary>float[], Half[] or uint[] with at least Offset + (width * height - 1) * Stride + 1 elements, row major, top row first.</summary>
        public Array Data { get; }
        public int Offset { get; }
        public int Stride { get; }
        /// <summary>Stored type: float[] / Half[] may be stored as Half or Float, uint[] only as UInt. Null: Half for float data, UInt for uint.</summary>
        public ExrPixelType? PixelType { get; }
        public bool PLinear { get; set; }

        public ExrChannelData(string name, Array data, ExrPixelType? pixelType = null, int offset = 0, int stride = 1)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Data = data ?? throw new ArgumentNullException(nameof(data));
            if (offset < 0 || stride < 1) throw new ArgumentException("invalid offset / stride");
            PixelType = pixelType; Offset = offset; Stride = stride;
        }
    }

    /// <summary>
    /// OpenEXR codec.
    /// <para>
    /// Decoding supports version 2 scan line and tiled files (level 0 of mip/rip maps), multi-part files, HALF / FLOAT /
    /// UINT channels and the compressions None, RLE, ZIPS, ZIP, PIZ, PXR24, B44 and B44A. Deep data and DWAA / DWAB
    /// throw <see cref="ImageNotSupportedException"/>. The decoded image always covers the data window.
    /// </para>
    /// </summary>
    public static class Exr
    {
        /// <summary>Returns whether the data starts with a OpenEXR signature (needs at most 16 bytes).</summary>
        public static bool IsMatch(ReadOnlySpan<byte> header) => ExrFileStructure.IsMatch(header);

        /// <summary>Reads header level information without decoding pixel data (first part, default channel selection).</summary>
        public static ImageInfo GetInfo(ReadOnlySpan<byte> data)
        {
            var file = ExrFileStructure.Parse(data);
            var part = file.Parts[0];
            int[] idx; ColorModel color;
            try { (idx, color) = SelectDefault(part, null); }
            catch (ImageNotSupportedException)
            {
                idx = new int[part.Channels.Count];
                for (int i = 0; i < idx.Length; i++) idx[i] = i;
                color = ColorModel.Other;
            }
            return new ImageInfo(ImageFormat.Exr, part.Width, part.Height, idx.Length, OutputType(part, idx, true), color);
        }

        /// <summary>Parses the headers of all parts (one for ordinary files). Does not touch pixel data.</summary>
        public static ExrHeader[] ReadHeaders(ReadOnlySpan<byte> data) => ExrFileStructure.Parse(data).Parts;

        /// <summary>
        /// Decodes a complete OpenEXR file held in memory.
        /// <para>
        /// Channel mapping (channels are stored alphabetically in the file, the result is always in R,G,B,A order):
        /// within the selected layer, R,G,B → Rgb (Rgba with A); otherwise Y (unless the layer also has an X, as in
        /// normals or positions) → Gray (GrayAlpha with A); otherwise a
        /// single channel → Gray; otherwise all channels of the layer in file order as <see cref="ColorModel.Other"/>.
        /// With <see cref="ExrDecodeOptions.Layer"/> == null the root layer (names without '.') is used if it has
        /// R,G,B or Y, else the first layer that has them, else all channels of the part are taken (Gray / Other).
        /// Luminance/chroma files (Y with RY / BY) throw <see cref="ImageNotSupportedException"/>; their channels can
        /// still be read with <see cref="DecodeChannels"/>.
        /// </para>
        /// <para>
        /// Sample type: all selected channels HALF → Float32 (or Float16 if HalfAsFloat is false); any FLOAT → Float32;
        /// all UINT → UInt32; UINT mixed with HALF / FLOAT → Float32 (UINT values are converted by value, which is
        /// inexact above 2^24).
        /// </para>
        /// </summary>
        public static RawImage Decode(ReadOnlySpan<byte> data, ExrDecodeOptions? options = null)
        {
            options ??= new ExrDecodeOptions();
            var file = ExrFileStructure.Parse(data);
            var part = GetPart(file, options.PartIndex);
            var (idx, color) = SelectDefault(part, options.Layer);
            return ExrDecoder.Decode(data, file, options.PartIndex, idx, color, options);
        }

        /// <summary>
        /// Decodes an explicit list of channels (full names such as "Z" or "diffuse.R") of the part selected by
        /// <see cref="ExrDecodeOptions.PartIndex"/> into one interleaved image, in the given order. The color model is
        /// Rgb / Rgba / Gray / GrayAlpha if the names are exactly R,G,B(,A) or Y(,A) of one layer in that order, Gray
        /// for a single channel, otherwise <see cref="ColorModel.Other"/>. Sample type rules are those of <see cref="Decode"/>.
        /// Subsampled channels are expanded to full resolution by sample replication.
        /// </summary>
        public static RawImage DecodeChannels(ReadOnlySpan<byte> data, IReadOnlyList<string> channelNames, ExrDecodeOptions? options = null)
        {
            if (channelNames == null || channelNames.Count == 0) throw new ArgumentException("no channels given", nameof(channelNames));
            options ??= new ExrDecodeOptions();
            var file = ExrFileStructure.Parse(data);
            var part = GetPart(file, options.PartIndex);
            var idx = new int[channelNames.Count];
            var used = new HashSet<int>();
            for (int i = 0; i < idx.Length; i++)
            {
                idx[i] = part.ChannelIndex(channelNames[i]);
                if (idx[i] < 0) throw new ArgumentException($"EXR part has no channel '{channelNames[i]}'", nameof(channelNames));
                if (!used.Add(idx[i])) throw new ArgumentException($"channel '{channelNames[i]}' is listed twice", nameof(channelNames));
            }
            return ExrDecoder.Decode(data, file, options.PartIndex, idx, NaturalModel(part, idx), options);
        }

        /// <summary>
        /// Encodes an image as a scan line OpenEXR file to the stream (which only needs to be writable, not seekable).
        /// Float32, Float16 and UInt32 images are accepted; integer 8/16 bit images throw
        /// <see cref="ImageNotSupportedException"/> (convert them to float first). Channels are named Y / Y,A / R,G,B /
        /// R,G,B,A by color model; <see cref="ColorModel.Other"/> needs <see cref="ExrEncodeOptions.ChannelNames"/>.
        /// </summary>
        public static void Encode(RawImage image, Stream output, ExrEncodeOptions? options = null)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));
            options ??= new ExrEncodeOptions();
            if (image.SampleType != SampleType.Float32 && image.SampleType != SampleType.Float16 && image.SampleType != SampleType.UInt32)
                throw new ImageNotSupportedException($"OpenEXR cannot store {image.SampleType} samples; convert to Float32, Float16 or UInt32");
            IReadOnlyList<string>? names = options.ChannelNames;
            if (names == null)
                names = image.Color switch
                {
                    ColorModel.Gray => new[] { "Y" },
                    ColorModel.GrayAlpha => new[] { "Y", "A" },
                    ColorModel.Rgb => new[] { "R", "G", "B" },
                    ColorModel.Rgba => new[] { "R", "G", "B", "A" },
                    _ => throw new ArgumentException("ExrEncodeOptions.ChannelNames is required for ColorModel.Other"),
                };
            if (names.Count != image.Channels)
                throw new ArgumentException($"{names.Count} channel names given for an image with {image.Channels} channels");
            var chans = new ExrChannelData[image.Channels];
            for (int i = 0; i < chans.Length; i++)
                chans[i] = new ExrChannelData(names[i], image.Data, options.PixelType, i, image.Channels);
            ExrEncoder.Encode(image.Width, image.Height, chans, output, options);
        }

        /// <summary>
        /// Encodes arbitrary named channels (AOVs, depth, ids, layers such as "diffuse.R") as one scan line part.
        /// Every channel has width * height samples; types may be mixed. Custom header attributes go into
        /// <see cref="ExrEncodeOptions.Attributes"/>. <see cref="ExrEncodeOptions.PixelType"/> and ChannelNames are ignored here.
        /// </summary>
        public static void EncodeChannels(int width, int height, IReadOnlyList<ExrChannelData> channels, Stream output, ExrEncodeOptions? options = null)
            => ExrEncoder.Encode(width, height, channels, output, options ?? new ExrEncodeOptions());

        static ExrHeader GetPart(ExrFileStructure file, int index)
        {
            if (index < 0 || index >= file.Parts.Length)
                throw new ArgumentException($"EXR file has {file.Parts.Length} part(s), part index {index} is out of range");
            return file.Parts[index];
        }

        internal static SampleType OutputType(ExrHeader part, int[] idx, bool halfAsFloat)
        {
            bool anyHalf = false, anyFloat = false, anyUInt = false;
            foreach (int i in idx)
                switch (part.Channels[i].PixelType)
                {
                    case ExrPixelType.Half: anyHalf = true; break;
                    case ExrPixelType.Float: anyFloat = true; break;
                    default: anyUInt = true; break;
                }
            if (anyUInt && !anyHalf && !anyFloat) return SampleType.UInt32;
            if (anyHalf && !anyFloat && !anyUInt && !halfAsFloat) return SampleType.Float16;
            return SampleType.Float32;
        }

        /// <summary>Tries R,G,B(,A) then Y(,A) among the channels of one layer.</summary>
        static bool TryColor(ExrHeader part, string layer, out int[] idx, out ColorModel color)
        {
            int Find(string baseName) => part.ChannelIndex(layer.Length == 0 ? baseName : layer + "." + baseName);
            int r = Find("R"), g = Find("G"), b = Find("B"), a = Find("A"), y = Find("Y");
            if (r >= 0 && g >= 0 && b >= 0)
            {
                idx = a >= 0 ? new[] { r, g, b, a } : new[] { r, g, b };
                color = a >= 0 ? ColorModel.Rgba : ColorModel.Rgb;
                return true;
            }
            // a Y next to an X is a vector component (normals, positions), not luminance
            if (y >= 0 && Find("X") < 0)
            {
                if (Find("RY") >= 0 || Find("BY") >= 0)
                    throw new ImageNotSupportedException("EXR luminance/chroma (Y, RY, BY) images are not converted to RGB; use DecodeChannels");
                idx = a >= 0 ? new[] { y, a } : new[] { y };
                color = a >= 0 ? ColorModel.GrayAlpha : ColorModel.Gray;
                return true;
            }
            idx = Array.Empty<int>(); color = ColorModel.Other;
            return false;
        }

        internal static (int[] idx, ColorModel color) SelectDefault(ExrHeader part, string? layer)
        {
            int[] idx; ColorModel color;
            var all = new List<int>();
            if (layer != null)
            {
                if (TryColor(part, layer, out idx, out color)) return (idx, color);
                for (int i = 0; i < part.Channels.Count; i++) if (part.Channels[i].Layer == layer) all.Add(i);
                if (all.Count == 0) throw new ArgumentException($"EXR part has no layer '{layer}'");
            }
            else
            {
                if (TryColor(part, "", out idx, out color)) return (idx, color);
                foreach (var l in part.Layers)
                    if (l.Length > 0 && TryColor(part, l, out idx, out color)) return (idx, color);
                for (int i = 0; i < part.Channels.Count; i++) all.Add(i);
            }
            return (all.ToArray(), all.Count == 1 ? ColorModel.Gray : ColorModel.Other);
        }

        static ColorModel NaturalModel(ExrHeader part, int[] idx)
        {
            if (idx.Length == 1) return ColorModel.Gray;
            string layer = part.Channels[idx[0]].Layer;
            var names = new string[idx.Length];
            for (int i = 0; i < idx.Length; i++)
            {
                if (part.Channels[idx[i]].Layer != layer) return ColorModel.Other;
                names[i] = part.Channels[idx[i]].BaseName;
            }
            return string.Join(",", names) switch
            {
                "R,G,B" => ColorModel.Rgb,
                "R,G,B,A" => ColorModel.Rgba,
                "Y,A" => ColorModel.GrayAlpha,
                _ => ColorModel.Other,
            };
        }
    }
}

namespace PixCodec
{
    /// <summary>OpenEXR compression schemes (values as stored in the file).</summary>
    public enum ExrCompression { None, Rle, Zips, Zip, Piz, Pxr24, B44, B44A, Dwaa, Dwab }
}
