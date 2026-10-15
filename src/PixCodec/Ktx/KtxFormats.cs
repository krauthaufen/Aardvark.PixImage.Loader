using System;
using System.Collections.Generic;

namespace PixCodec.KtxImpl
{
    internal enum KtxLayout
    {
        Plain,      // whole bytes per component, components in memory order
        Packed,     // components packed into a 8 / 16 / 32 bit little endian word, the first named in the top bits
        Dxgi,       // decoded like the DDS format with this DXGI number
        Etc,        // ETC1 / ETC2 / EAC blocks
        Astc,       // ASTC blocks
        Uastc,      // Basis Universal UASTC 4x4 blocks
        Etc1s,      // Basis Universal ETC1S (BasisLZ supercompression)
    }

    /// <summary>A stored texel format of a KTX file, described from its Vulkan name.</summary>
    internal sealed class KtxFormat
    {
        public string Name = "";
        public int Vk;
        public KtxLayout Layout;
        public string Components = "";   // e.g. "RGBA", "BGR", "R" in name order (Plain / Packed)
        public int[] Bits = Array.Empty<int>();
        public string Numeric = "";      // UNORM, SNORM, UINT, SINT, SFLOAT, UFLOAT, SRGB, USCALED, SSCALED
        public int PackBits;             // Packed: word size
        public int Dxgi;
        public bool Srgb;
        public int BlockW = 1, BlockH = 1, BlockD = 1, BlockBytes;   // block formats; Plain / Packed: BlockBytes = bytes per pixel
        public bool Hdr;                 // ASTC SFLOAT
        public bool Signed;              // EAC / BC signed variants
        public ColorModel Color;
        public int Channels;
        public SampleType Type;

        public bool IsBlockCompressed => BlockW > 1 || BlockH > 1 || BlockD > 1;

        public long ImageBytes(int w, int h)
            => (long)((w + BlockW - 1) / BlockW) * ((h + BlockH - 1) / BlockH) * BlockBytes;
    }

    internal static class KtxFormats
    {
        static readonly Dictionary<int, string> VkNames = new()
        {
            [0] = "UNDEFINED",
            [1] = "R4G4_UNORM_PACK8",
            [2] = "R4G4B4A4_UNORM_PACK16",
            [3] = "B4G4R4A4_UNORM_PACK16",
            [4] = "R5G6B5_UNORM_PACK16",
            [5] = "B5G6R5_UNORM_PACK16",
            [6] = "R5G5B5A1_UNORM_PACK16",
            [7] = "B5G5R5A1_UNORM_PACK16",
            [8] = "A1R5G5B5_UNORM_PACK16",
            [9] = "R8_UNORM",
            [10] = "R8_SNORM",
            [11] = "R8_USCALED",
            [12] = "R8_SSCALED",
            [13] = "R8_UINT",
            [14] = "R8_SINT",
            [15] = "R8_SRGB",
            [16] = "R8G8_UNORM",
            [17] = "R8G8_SNORM",
            [18] = "R8G8_USCALED",
            [19] = "R8G8_SSCALED",
            [20] = "R8G8_UINT",
            [21] = "R8G8_SINT",
            [22] = "R8G8_SRGB",
            [23] = "R8G8B8_UNORM",
            [24] = "R8G8B8_SNORM",
            [25] = "R8G8B8_USCALED",
            [26] = "R8G8B8_SSCALED",
            [27] = "R8G8B8_UINT",
            [28] = "R8G8B8_SINT",
            [29] = "R8G8B8_SRGB",
            [30] = "B8G8R8_UNORM",
            [31] = "B8G8R8_SNORM",
            [32] = "B8G8R8_USCALED",
            [33] = "B8G8R8_SSCALED",
            [34] = "B8G8R8_UINT",
            [35] = "B8G8R8_SINT",
            [36] = "B8G8R8_SRGB",
            [37] = "R8G8B8A8_UNORM",
            [38] = "R8G8B8A8_SNORM",
            [39] = "R8G8B8A8_USCALED",
            [40] = "R8G8B8A8_SSCALED",
            [41] = "R8G8B8A8_UINT",
            [42] = "R8G8B8A8_SINT",
            [43] = "R8G8B8A8_SRGB",
            [44] = "B8G8R8A8_UNORM",
            [45] = "B8G8R8A8_SNORM",
            [46] = "B8G8R8A8_USCALED",
            [47] = "B8G8R8A8_SSCALED",
            [48] = "B8G8R8A8_UINT",
            [49] = "B8G8R8A8_SINT",
            [50] = "B8G8R8A8_SRGB",
            [51] = "A8B8G8R8_UNORM_PACK32",
            [52] = "A8B8G8R8_SNORM_PACK32",
            [53] = "A8B8G8R8_USCALED_PACK32",
            [54] = "A8B8G8R8_SSCALED_PACK32",
            [55] = "A8B8G8R8_UINT_PACK32",
            [56] = "A8B8G8R8_SINT_PACK32",
            [57] = "A8B8G8R8_SRGB_PACK32",
            [58] = "A2R10G10B10_UNORM_PACK32",
            [59] = "A2R10G10B10_SNORM_PACK32",
            [60] = "A2R10G10B10_USCALED_PACK32",
            [61] = "A2R10G10B10_SSCALED_PACK32",
            [62] = "A2R10G10B10_UINT_PACK32",
            [63] = "A2R10G10B10_SINT_PACK32",
            [64] = "A2B10G10R10_UNORM_PACK32",
            [65] = "A2B10G10R10_SNORM_PACK32",
            [66] = "A2B10G10R10_USCALED_PACK32",
            [67] = "A2B10G10R10_SSCALED_PACK32",
            [68] = "A2B10G10R10_UINT_PACK32",
            [69] = "A2B10G10R10_SINT_PACK32",
            [70] = "R16_UNORM",
            [71] = "R16_SNORM",
            [72] = "R16_USCALED",
            [73] = "R16_SSCALED",
            [74] = "R16_UINT",
            [75] = "R16_SINT",
            [76] = "R16_SFLOAT",
            [77] = "R16G16_UNORM",
            [78] = "R16G16_SNORM",
            [79] = "R16G16_USCALED",
            [80] = "R16G16_SSCALED",
            [81] = "R16G16_UINT",
            [82] = "R16G16_SINT",
            [83] = "R16G16_SFLOAT",
            [84] = "R16G16B16_UNORM",
            [85] = "R16G16B16_SNORM",
            [86] = "R16G16B16_USCALED",
            [87] = "R16G16B16_SSCALED",
            [88] = "R16G16B16_UINT",
            [89] = "R16G16B16_SINT",
            [90] = "R16G16B16_SFLOAT",
            [91] = "R16G16B16A16_UNORM",
            [92] = "R16G16B16A16_SNORM",
            [93] = "R16G16B16A16_USCALED",
            [94] = "R16G16B16A16_SSCALED",
            [95] = "R16G16B16A16_UINT",
            [96] = "R16G16B16A16_SINT",
            [97] = "R16G16B16A16_SFLOAT",
            [98] = "R32_UINT",
            [99] = "R32_SINT",
            [100] = "R32_SFLOAT",
            [101] = "R32G32_UINT",
            [102] = "R32G32_SINT",
            [103] = "R32G32_SFLOAT",
            [104] = "R32G32B32_UINT",
            [105] = "R32G32B32_SINT",
            [106] = "R32G32B32_SFLOAT",
            [107] = "R32G32B32A32_UINT",
            [108] = "R32G32B32A32_SINT",
            [109] = "R32G32B32A32_SFLOAT",
            [110] = "R64_UINT",
            [111] = "R64_SINT",
            [112] = "R64_SFLOAT",
            [113] = "R64G64_UINT",
            [114] = "R64G64_SINT",
            [115] = "R64G64_SFLOAT",
            [116] = "R64G64B64_UINT",
            [117] = "R64G64B64_SINT",
            [118] = "R64G64B64_SFLOAT",
            [119] = "R64G64B64A64_UINT",
            [120] = "R64G64B64A64_SINT",
            [121] = "R64G64B64A64_SFLOAT",
            [122] = "B10G11R11_UFLOAT_PACK32",
            [123] = "E5B9G9R9_UFLOAT_PACK32",
            [124] = "D16_UNORM",
            [125] = "X8_D24_UNORM_PACK32",
            [126] = "D32_SFLOAT",
            [127] = "S8_UINT",
            [128] = "D16_UNORM_S8_UINT",
            [129] = "D24_UNORM_S8_UINT",
            [130] = "D32_SFLOAT_S8_UINT",
            [131] = "BC1_RGB_UNORM_BLOCK",
            [132] = "BC1_RGB_SRGB_BLOCK",
            [133] = "BC1_RGBA_UNORM_BLOCK",
            [134] = "BC1_RGBA_SRGB_BLOCK",
            [135] = "BC2_UNORM_BLOCK",
            [136] = "BC2_SRGB_BLOCK",
            [137] = "BC3_UNORM_BLOCK",
            [138] = "BC3_SRGB_BLOCK",
            [139] = "BC4_UNORM_BLOCK",
            [140] = "BC4_SNORM_BLOCK",
            [141] = "BC5_UNORM_BLOCK",
            [142] = "BC5_SNORM_BLOCK",
            [143] = "BC6H_UFLOAT_BLOCK",
            [144] = "BC6H_SFLOAT_BLOCK",
            [145] = "BC7_UNORM_BLOCK",
            [146] = "BC7_SRGB_BLOCK",
            [147] = "ETC2_R8G8B8_UNORM_BLOCK",
            [148] = "ETC2_R8G8B8_SRGB_BLOCK",
            [149] = "ETC2_R8G8B8A1_UNORM_BLOCK",
            [150] = "ETC2_R8G8B8A1_SRGB_BLOCK",
            [151] = "ETC2_R8G8B8A8_UNORM_BLOCK",
            [152] = "ETC2_R8G8B8A8_SRGB_BLOCK",
            [153] = "EAC_R11_UNORM_BLOCK",
            [154] = "EAC_R11_SNORM_BLOCK",
            [155] = "EAC_R11G11_UNORM_BLOCK",
            [156] = "EAC_R11G11_SNORM_BLOCK",
            [157] = "ASTC_4x4_UNORM_BLOCK",
            [158] = "ASTC_4x4_SRGB_BLOCK",
            [159] = "ASTC_5x4_UNORM_BLOCK",
            [160] = "ASTC_5x4_SRGB_BLOCK",
            [161] = "ASTC_5x5_UNORM_BLOCK",
            [162] = "ASTC_5x5_SRGB_BLOCK",
            [163] = "ASTC_6x5_UNORM_BLOCK",
            [164] = "ASTC_6x5_SRGB_BLOCK",
            [165] = "ASTC_6x6_UNORM_BLOCK",
            [166] = "ASTC_6x6_SRGB_BLOCK",
            [167] = "ASTC_8x5_UNORM_BLOCK",
            [168] = "ASTC_8x5_SRGB_BLOCK",
            [169] = "ASTC_8x6_UNORM_BLOCK",
            [170] = "ASTC_8x6_SRGB_BLOCK",
            [171] = "ASTC_8x8_UNORM_BLOCK",
            [172] = "ASTC_8x8_SRGB_BLOCK",
            [173] = "ASTC_10x5_UNORM_BLOCK",
            [174] = "ASTC_10x5_SRGB_BLOCK",
            [175] = "ASTC_10x6_UNORM_BLOCK",
            [176] = "ASTC_10x6_SRGB_BLOCK",
            [177] = "ASTC_10x8_UNORM_BLOCK",
            [178] = "ASTC_10x8_SRGB_BLOCK",
            [179] = "ASTC_10x10_UNORM_BLOCK",
            [180] = "ASTC_10x10_SRGB_BLOCK",
            [181] = "ASTC_12x10_UNORM_BLOCK",
            [182] = "ASTC_12x10_SRGB_BLOCK",
            [183] = "ASTC_12x12_UNORM_BLOCK",
            [184] = "ASTC_12x12_SRGB_BLOCK",
            [1000066000] = "ASTC_4x4_SFLOAT_BLOCK",
            [1000066001] = "ASTC_5x4_SFLOAT_BLOCK",
            [1000066002] = "ASTC_5x5_SFLOAT_BLOCK",
            [1000066003] = "ASTC_6x5_SFLOAT_BLOCK",
            [1000066004] = "ASTC_6x6_SFLOAT_BLOCK",
            [1000066005] = "ASTC_8x5_SFLOAT_BLOCK",
            [1000066006] = "ASTC_8x6_SFLOAT_BLOCK",
            [1000066007] = "ASTC_8x8_SFLOAT_BLOCK",
            [1000066008] = "ASTC_10x5_SFLOAT_BLOCK",
            [1000066009] = "ASTC_10x6_SFLOAT_BLOCK",
            [1000066010] = "ASTC_10x8_SFLOAT_BLOCK",
            [1000066011] = "ASTC_10x10_SFLOAT_BLOCK",
            [1000066012] = "ASTC_12x10_SFLOAT_BLOCK",
            [1000066013] = "ASTC_12x12_SFLOAT_BLOCK",
            [1000340000] = "A4R4G4B4_UNORM_PACK16",
            [1000340001] = "A4B4G4R4_UNORM_PACK16",
        };

        static readonly Dictionary<int, KtxFormat?> Cache = new();

        public static string VkName(int vk) => VkNames.TryGetValue(vk, out var n) ? n : $"vkFormat {vk}";

        public static int VkFromName(string name)
        {
            foreach (var kv in VkNames) if (kv.Value == name) return kv.Key;
            throw new ArgumentException(name);
        }

        // 3D ASTC has no core Vulkan format: KTX 1 names it with OES enums, KTX 2 through the data format descriptor
        static readonly int[][] Astc3D =
        {
            new[] { 3, 3, 3 }, new[] { 4, 3, 3 }, new[] { 4, 4, 3 }, new[] { 4, 4, 4 }, new[] { 5, 4, 4 },
            new[] { 5, 5, 4 }, new[] { 5, 5, 5 }, new[] { 6, 5, 5 }, new[] { 6, 6, 5 }, new[] { 6, 6, 6 },
        };

        /// <summary>An ASTC format of any footprint (2D or 3D), as a data format descriptor describes it.</summary>
        public static KtxFormat Astc(int bw, int bh, int bd, bool srgb, bool hdr)
            => new()
            {
                Name = $"ASTC_{bw}x{bh}{(bd > 1 ? "x" + bd : "")}_{(hdr ? "SFLOAT" : srgb ? "SRGB" : "UNORM")}_BLOCK",
                Layout = KtxLayout.Astc, BlockW = bw, BlockH = bh, BlockD = bd, BlockBytes = 16, Srgb = srgb, Hdr = hdr,
                Color = ColorModel.Rgba, Channels = 4, Type = hdr ? SampleType.Float16 : SampleType.UInt8,
            };

        /// <summary>KTX 1: the 3D ASTC formats of OES_texture_compression_astc (0x93C0.. RGBA, 0x93E0.. sRGB).</summary>
        public static KtxFormat? FromGl3D(uint internalFormat)
        {
            int i = internalFormat >= 0x93C0 && internalFormat <= 0x93C9 ? (int)(internalFormat - 0x93C0)
                  : internalFormat >= 0x93E0 && internalFormat <= 0x93E9 ? (int)(internalFormat - 0x93E0) : -1;
            if (i < 0) return null;
            var d = Astc3D[i];
            return Astc(d[0], d[1], d[2], internalFormat >= 0x93E0, false);
        }

        public static KtxFormat? FromVk(int vk)
        {
            lock (Cache)
            {
                if (!Cache.TryGetValue(vk, out var f)) Cache[vk] = f = VkNames.TryGetValue(vk, out var n) ? Parse(vk, n) : null;
                return f;
            }
        }

        static readonly int[] AstcW = { 4, 5, 5, 6, 6, 8, 8, 8, 10, 10, 10, 10, 12, 12 };
        static readonly int[] AstcH = { 4, 4, 5, 5, 6, 5, 6, 8, 5, 6, 8, 10, 10, 12 };

        static KtxFormat? Parse(int vk, string name)
        {
            var f = new KtxFormat { Vk = vk, Name = name };
            if (name.StartsWith("BC"))
            {
                f.BlockW = f.BlockH = 4;
                f.Srgb = name.Contains("SRGB");
                f.Layout = KtxLayout.Dxgi;
                f.Signed = name.Contains("SNORM") || name.Contains("SFLOAT");
                (f.Dxgi, f.BlockBytes) = name switch
                {
                    _ when name.StartsWith("BC1_") => (f.Srgb ? 72 : 71, 8),
                    _ when name.StartsWith("BC2_") => (f.Srgb ? 75 : 74, 16),
                    _ when name.StartsWith("BC3_") => (f.Srgb ? 78 : 77, 16),
                    _ when name.StartsWith("BC4_") => (f.Signed ? 81 : 80, 8),
                    _ when name.StartsWith("BC5_") => (f.Signed ? 84 : 83, 16),
                    _ when name.StartsWith("BC6H_") => (f.Signed ? 96 : 95, 16),
                    _ when name.StartsWith("BC7_") => (f.Srgb ? 99 : 98, 16),
                    _ => (0, 0),
                };
                if (f.Dxgi == 0) return null;
                var d = DdsImpl.DdsReader.FromDxgi(f.Dxgi)!;
                (f.Color, f.Channels, f.Type) = (d.Color, d.Channels, d.Type);
                if (name.StartsWith("BC1_RGB_")) (f.Color, f.Channels) = (ColorModel.Rgb, 3);
                return f;
            }
            if (name.StartsWith("ETC2_") || name.StartsWith("EAC_"))
            {
                f.Layout = KtxLayout.Etc;
                f.BlockW = f.BlockH = 4;
                f.Srgb = name.Contains("SRGB");
                f.Signed = name.Contains("SNORM");
                (f.BlockBytes, f.Color, f.Channels, f.Type) = name switch
                {
                    _ when name.StartsWith("ETC2_R8G8B8_") => (8, ColorModel.Rgb, 3, SampleType.UInt8),
                    _ when name.StartsWith("ETC2_R8G8B8A1_") => (8, ColorModel.Rgba, 4, SampleType.UInt8),
                    _ when name.StartsWith("ETC2_R8G8B8A8_") => (16, ColorModel.Rgba, 4, SampleType.UInt8),
                    _ when name.StartsWith("EAC_R11G11_") => (16, ColorModel.Other, 2, f.Signed ? SampleType.Int16 : SampleType.UInt16),
                    _ when name.StartsWith("EAC_R11_") => (8, ColorModel.Gray, 1, f.Signed ? SampleType.Int16 : SampleType.UInt16),
                    _ => (0, ColorModel.Other, 0, SampleType.UInt8),
                };
                return f.BlockBytes == 0 ? null : f;
            }
            if (name.StartsWith("ASTC_"))
            {
                // ASTC_<w>x<h>_UNORM / SRGB / SFLOAT _BLOCK
                var parts = name.Split('_');
                var dims = parts[1].Split('x');
                f.Layout = KtxLayout.Astc;
                f.BlockW = int.Parse(dims[0]); f.BlockH = int.Parse(dims[1]);
                f.BlockBytes = 16;
                f.Srgb = parts[2] == "SRGB";
                f.Hdr = parts[2] == "SFLOAT";
                (f.Color, f.Channels, f.Type) = (ColorModel.Rgba, 4, f.Hdr ? SampleType.Float16 : SampleType.UInt8);
                return f;
            }
            if (name.Contains("PVRTC") || name.Contains("_422_") || name.Contains("PLANE") || name.Contains("X6") || name.Contains("X4")
                || name.Contains("S8_UINT") || name == "UNDEFINED")
                return null;
            if (name == "B10G11R11_UFLOAT_PACK32") return Dx(f, 26);
            if (name == "E5B9G9R9_UFLOAT_PACK32") return Dx(f, 67);
            // plain and packed: <components with bits>_<numeric>[_PACKnn]
            var p = name.Split('_');
            string comps = p[0], numeric = p.Length > 1 ? p[1] : "";
            var letters = new List<char>();
            var bits = new List<int>();
            for (int i = 0; i < comps.Length;)
            {
                char c = comps[i++];
                int b = 0;
                while (i < comps.Length && char.IsDigit(comps[i])) b = b * 10 + (comps[i++] - '0');
                if (b == 0) return null;
                letters.Add(c); bits.Add(b);
            }
            f.Components = new string(letters.ToArray());
            f.Bits = bits.ToArray();
            f.Numeric = numeric;
            f.Srgb = numeric == "SRGB";
            int total = 0;
            foreach (var b in bits) total += b;
            if (total % 8 != 0) return null;
            f.BlockBytes = total / 8;
            bool packed = p.Length > 2 && p[2].StartsWith("PACK");
            bool whole = true;
            foreach (var b in bits) whole &= b is 8 or 16 or 32 or 64;
            if (packed && !whole) { f.Layout = KtxLayout.Packed; f.PackBits = int.Parse(p[2].Substring(4)); }
            else if (!whole) return null;
            else if (packed)
            {
                // a packed word of whole bytes (A8B8G8R8_..._PACK32): little endian puts the last named component first
                f.Layout = KtxLayout.Plain;
                var arr = f.Components.ToCharArray(); Array.Reverse(arr); f.Components = new string(arr);
                Array.Reverse(f.Bits);
            }
            else f.Layout = KtxLayout.Plain;
            // what decoding gives
            int maxBits = 0;
            foreach (var b in f.Bits) maxBits = Math.Max(maxBits, b);
            bool signed = numeric is "SNORM" or "SINT" or "SSCALED";
            bool flt = numeric is "SFLOAT" or "UFLOAT";
            f.Type = flt ? maxBits switch { 16 => SampleType.Float16, 32 => SampleType.Float32, 64 => SampleType.Float64, _ => SampleType.Float32 }
                : maxBits switch
                {
                    <= 8 => signed ? SampleType.Int8 : SampleType.UInt8,
                    <= 16 => signed ? SampleType.Int16 : SampleType.UInt16,
                    <= 32 => signed ? SampleType.Int32 : SampleType.UInt32,
                    _ => signed ? SampleType.Int64 : SampleType.UInt64,
                };
            bool hasA = f.Components.Contains('A');
            int colours = 0;
            foreach (var c in f.Components) if (c is 'R' or 'G' or 'B' or 'D') colours++;
            f.Channels = colours + (hasA ? 1 : 0);
            f.Color = (colours, hasA) switch
            {
                (1, false) => ColorModel.Gray,
                (0, true) => ColorModel.Gray,        // alpha only (A8_UNORM): as gray, like DDS
                (1, true) => ColorModel.GrayAlpha,
                (3, false) => ColorModel.Rgb,
                (3, true) => ColorModel.Rgba,
                _ => ColorModel.Other,
            };
            if (f.Components.Contains('X')) return null;
            return f;
        }

        static KtxFormat Dx(KtxFormat f, int dxgi)
        {
            var d = DdsImpl.DdsReader.FromDxgi(dxgi)!;
            f.Layout = KtxLayout.Dxgi; f.Dxgi = dxgi; f.BlockBytes = d.PixelBits / 8;
            (f.Color, f.Channels, f.Type) = (d.Color, d.Channels, d.Type);
            return f;
        }

        /// <summary>The Vulkan format matching a KTX 1 OpenGL format description (0 if none).</summary>
        public static int VkFromGl(uint internalFormat, uint format, uint type)
        {
            switch (internalFormat)
            {
                case 0x8229: return 9;      // R8
                case 0x822B: return 16;     // RG8
                case 0x8051: return 23;     // RGB8
                case 0x8058: return 37;     // RGBA8
                case 0x8C41: return 29;     // SRGB8
                case 0x8C43: return 43;     // SRGB8_ALPHA8
                case 0x8F94: return 10;     // R8_SNORM
                case 0x8F95: return 17;     // RG8_SNORM
                case 0x8F96: return 24;     // RGB8_SNORM
                case 0x8F97: return 38;     // RGBA8_SNORM
                case 0x822A: return 70;     // R16
                case 0x822C: return 77;     // RG16
                case 0x8054: return 84;     // RGB16
                case 0x805B: return 91;     // RGBA16
                case 0x8F98: return 71;     // R16_SNORM
                case 0x8F99: return 78;     // RG16_SNORM
                case 0x8F9A: return 85;     // RGB16_SNORM
                case 0x8F9B: return 92;     // RGBA16_SNORM
                case 0x822D: return 76;     // R16F
                case 0x822F: return 83;     // RG16F
                case 0x881B: return 90;     // RGB16F
                case 0x881A: return 97;     // RGBA16F
                case 0x822E: return 100;    // R32F
                case 0x8230: return 103;    // RG32F
                case 0x8815: return 106;    // RGB32F
                case 0x8814: return 109;    // RGBA32F
                case 0x8232: return 13;     // R8UI
                case 0x8231: return 14;     // R8I
                case 0x8234: return 74;     // R16UI
                case 0x8233: return 75;     // R16I
                case 0x8236: return 98;     // R32UI
                case 0x8235: return 99;     // R32I
                case 0x8238: return 20;     // RG8UI
                case 0x8237: return 21;     // RG8I
                case 0x823A: return 81;     // RG16UI
                case 0x8239: return 82;     // RG16I
                case 0x823C: return 101;    // RG32UI
                case 0x823B: return 102;    // RG32I
                case 0x8D7C: return 41;     // RGBA8UI
                case 0x8D8E: return 42;     // RGBA8I
                case 0x8D76: return 95;     // RGBA16UI
                case 0x8D88: return 96;     // RGBA16I
                case 0x8D70: return 107;    // RGBA32UI
                case 0x8D82: return 108;    // RGBA32I
                case 0x8D7D: return 27;     // RGB8UI
                case 0x8D8F: return 28;     // RGB8I
                case 0x8D77: return 88;     // RGB16UI
                case 0x8D89: return 89;     // RGB16I
                case 0x8D71: return 104;    // RGB32UI
                case 0x8D83: return 105;    // RGB32I
                case 0x8C3A: return 122;    // R11F_G11F_B10F
                case 0x8C3D: return 123;    // RGB9_E5
                case 0x8059: return 64;     // RGB10_A2
                case 0x906F: return 68;     // RGB10_A2UI
                case 0x8D62: return 4;      // RGB565
                case 0x8056: return 2;      // RGBA4
                case 0x8057: return 6;      // RGB5_A1
                case 0x8040: return 9;      // LUMINANCE8
                case 0x803C: return 9;      // ALPHA8 (as gray)
                case 0x8045: return 16;     // LUMINANCE8_ALPHA8 (as two channels)
                case 0x81A5: return 124;    // DEPTH_COMPONENT16
                case 0x8CAC: return 126;    // DEPTH_COMPONENT32F
                // S3TC, RGTC, BPTC
                case 0x83F0: return 131;
                case 0x83F1: return 133;
                case 0x83F2: return 135;
                case 0x83F3: return 137;
                case 0x8C4C: return 132;
                case 0x8C4D: return 134;
                case 0x8C4E: return 136;
                case 0x8C4F: return 138;
                case 0x8DBB: return 139;
                case 0x8DBC: return 140;
                case 0x8DBD: return 141;
                case 0x8DBE: return 142;
                case 0x8E8C: return 145;
                case 0x8E8D: return 146;
                case 0x8E8E: return 144;
                case 0x8E8F: return 143;
                // ETC1 decodes as ETC2 RGB (ETC2 is a superset), ETC2 / EAC
                case 0x8D64: return 147;
                case 0x9274: return 147;
                case 0x9275: return 148;
                case 0x9276: return 149;
                case 0x9277: return 150;
                case 0x9278: return 151;
                case 0x9279: return 152;
                case 0x9270: return 153;
                case 0x9271: return 154;
                case 0x9272: return 155;
                case 0x9273: return 156;
            }
            // ASTC: 0x93B0.. (RGBA) and 0x93D0.. (SRGB8_ALPHA8), 14 footprints
            if (internalFormat >= 0x93B0 && internalFormat <= 0x93BD) return 157 + 2 * (int)(internalFormat - 0x93B0);
            if (internalFormat >= 0x93D0 && internalFormat <= 0x93DD) return 158 + 2 * (int)(internalFormat - 0x93D0);
            // unsized internal formats: the format / type pair decides
            const uint UByte = 0x1401, Byte = 0x1400, UShort = 0x1403, Short = 0x1402, Half = 0x140B, HalfOes = 0x8D61, Float = 0x1406, UInt = 0x1405, Int = 0x1404;
            int comps = format switch
            {
                0x1903 or 0x1909 or 0x1906 => 1,   // RED, LUMINANCE, ALPHA
                0x8227 or 0x190A => 2,             // RG, LUMINANCE_ALPHA
                0x1907 => 3,                       // RGB
                0x1908 => 4,                       // RGBA
                0x80E0 => -3,                      // BGR
                0x80E1 => -4,                      // BGRA
                _ => 0,
            };
            if (comps == 0) return 0;
            int[] byComps(int r, int rg, int rgb, int rgba, int bgr, int bgra) => new[] { 0, r, rg, rgb, rgba, bgr, bgra };
            int idx = comps > 0 ? comps : 2 - comps;   // -3 -> 5, -4 -> 6
            int[] t = type switch
            {
                UByte => byComps(9, 16, 23, 37, 30, 44),
                Byte => byComps(10, 17, 24, 38, 31, 45),
                UShort => byComps(70, 77, 84, 91, 0, 0),
                Short => byComps(71, 78, 85, 92, 0, 0),
                Half or HalfOes => byComps(76, 83, 90, 97, 0, 0),
                Float => byComps(100, 103, 106, 109, 0, 0),
                UInt => byComps(98, 101, 104, 107, 0, 0),
                Int => byComps(99, 102, 105, 108, 0, 0),
                0x8363 => byComps(0, 0, 4, 0, 0, 0),      // UNSIGNED_SHORT_5_6_5
                0x8033 => byComps(0, 0, 0, 2, 0, 0),      // UNSIGNED_SHORT_4_4_4_4
                0x8034 => byComps(0, 0, 0, 6, 0, 0),      // UNSIGNED_SHORT_5_5_5_1
                _ => byComps(0, 0, 0, 0, 0, 0),
            };
            return t[idx];
        }
    }
}
