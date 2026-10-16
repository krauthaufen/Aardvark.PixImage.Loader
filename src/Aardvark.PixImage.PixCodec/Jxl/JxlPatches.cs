using System;
using System.Collections.Generic;

namespace Aardvark.ImageCodecs.JpegXl
{
    internal enum JxlPatchBlend { None = 0, Replace = 1, Add = 2, Mul = 3, BlendAbove = 4, BlendBelow = 5, AlphaWeightedAddAbove = 6, AlphaWeightedAddBelow = 7 }

    /// <summary>Patches: rectangles copied from a reference frame and blended into the frame.</summary>
    internal sealed class JxlPatches
    {
        public struct Blending
        {
            public JxlPatchBlend Mode;
            public int Alpha;
            public bool Clamp;
        }

        public struct Patch
        {
            public int Ref, X0, Y0, W, H;
            public int X, Y;
            public Blending[] Blend;   // colour, then one per extra channel
        }

        public readonly List<Patch> Items = new();

        public static JxlPatches Read(JxlBitReader br, int numExtra, long maxPixels)
        {
            var code = JxlEntropyCode.Read(br, 10);
            var r = new JxlSymbolReader(code, br, 0);
            var p = new JxlPatches();
            uint numRef = r.ReadUint(br, 0);
            if (numRef > 1 << 20) throw new ImageFormatException("JPEG XL: too many patches");
            long total = 0;
            for (uint i = 0; i < numRef; i++)
            {
                int rf = (int)r.ReadUint(br, 1);
                if (rf > 3) throw new ImageFormatException("JPEG XL: bad patch reference");
                int x0 = (int)r.ReadUint(br, 3), y0 = (int)r.ReadUint(br, 3);
                int w = (int)r.ReadUint(br, 2) + 1, h = (int)r.ReadUint(br, 2) + 1;
                uint count = r.ReadUint(br, 7) + 1;
                if (count > 1 << 20) throw new ImageFormatException("JPEG XL: too many patch positions");
                int px = 0, py = 0;
                for (uint j = 0; j < count; j++)
                {
                    int x, y;
                    if (j == 0) { x = (int)r.ReadUint(br, 4); y = (int)r.ReadUint(br, 4); }
                    else
                    {
                        x = JxlBitReader.UnpackSigned(r.ReadUint(br, 6)) + px;
                        y = JxlBitReader.UnpackSigned(r.ReadUint(br, 6)) + py;
                    }
                    if (x < 0 || y < 0) throw new ImageFormatException("JPEG XL: patch position out of range");
                    px = x; py = y;
                    var bl = new Blending[numExtra + 1];
                    for (int k = 0; k <= numExtra; k++)
                    {
                        uint mode = r.ReadUint(br, 5);
                        if (mode > 7) throw new ImageFormatException("JPEG XL: bad patch blend mode");
                        bl[k].Mode = (JxlPatchBlend)mode;
                        if (mode >= 4 && numExtra > 1)
                        {
                            bl[k].Alpha = (int)r.ReadUint(br, 8);
                            if (bl[k].Alpha >= numExtra) throw new ImageFormatException("JPEG XL: bad patch alpha channel");
                        }
                        if (mode >= 3) bl[k].Clamp = r.ReadUint(br, 9) != 0;
                    }
                    total += (long)w * h;
                    if (total > Math.Max(maxPixels, 1 << 20) * 4) throw new ImageNotSupportedException("JPEG XL: patches cover too much");
                    p.Items.Add(new Patch { Ref = rf, X0 = x0, Y0 = y0, W = w, H = h, X = x, Y = y, Blend = bl });
                }
                if (br.Overrun) throw new ImageFormatException("JPEG XL: truncated patches");
            }
            r.CheckFinalState();
            return p;
        }

        /// <summary>Blends the patches into the frame (all channels at frame resolution, before the colour transform).</summary>
        public void Apply(JxlFrameImage frame, JxlDecoderState state, JxlImageHeader img)
        {
            int nc = frame.NumColour;
            int ne = img.ExtraChannels.Length;
            foreach (var p in Items)
            {
                var src = state.References[p.Ref] ?? throw new ImageFormatException("JPEG XL: patch from an empty reference frame");
                if ((long)p.X0 + p.W > src.Width || (long)p.Y0 + p.H > src.Height)
                    throw new ImageFormatException("JPEG XL: patch outside of its reference frame");
                if ((long)p.X + p.W > frame.Width || (long)p.Y + p.H > frame.Height)
                    throw new ImageFormatException("JPEG XL: patch outside of the frame");
                // colour channels share blending 0, extra channel e uses blending e + 1
                for (int c = 0; c < frame.Planes.Length; c++)
                {
                    var bl = c < nc ? p.Blend[0] : p.Blend[c - nc + 1];
                    if (bl.Mode == JxlPatchBlend.None) continue;
                    if (c >= src.Planes.Length) continue;
                    var dst = frame.Planes[c];
                    var sp = src.Planes[c];
                    int ai = nc + bl.Alpha;
                    bool premul = ne > bl.Alpha && img.ExtraChannels[bl.Alpha].AlphaAssociated;
                    float[]? fa = null, ba = null;   // alpha of the patch (src) and of the frame (before blending)
                    if (bl.Mode >= JxlPatchBlend.BlendAbove && ai < frame.Planes.Length && ai < src.Planes.Length)
                    {
                        fa = src.Planes[ai];
                        ba = frame.Planes[ai];
                    }
                    bool isAlpha = c == ai;
                    for (int y = 0; y < p.H; y++)
                    {
                        int so = (p.Y0 + y) * src.Width + p.X0, d0 = (p.Y + y) * frame.Width + p.X;
                        for (int x = 0; x < p.W; x++)
                        {
                            int si = so + x, di = d0 + x;
                            float s = sp[si], d = dst[di];
                            float r;
                            switch (bl.Mode)
                            {
                                case JxlPatchBlend.Replace: r = s; break;
                                case JxlPatchBlend.Add: r = d + s; break;
                                case JxlPatchBlend.Mul: r = d * (bl.Clamp ? Math.Clamp(s, 0, 1) : s); break;
                                case JxlPatchBlend.BlendAbove:
                                case JxlPatchBlend.BlendBelow:
                                    {
                                        bool above = bl.Mode == JxlPatchBlend.BlendAbove;
                                        float fg = above ? s : d, bg = above ? d : s;
                                        float a = fa != null ? (above ? fa[si] : ba![di]) : 1;
                                        float b = fa != null ? (above ? ba![di] : fa[si]) : 0;
                                        if (bl.Clamp) { a = Math.Clamp(a, 0, 1); b = Math.Clamp(b, 0, 1); }
                                        if (isAlpha) r = a + b * (1 - a);
                                        else if (premul) r = fg + bg * (1 - a);
                                        else
                                        {
                                            float na = a + b * (1 - a);
                                            r = na > 0 ? (fg * a + bg * b * (1 - a)) / na : 0;
                                        }
                                        break;
                                    }
                                case JxlPatchBlend.AlphaWeightedAddAbove:
                                    {
                                        if (isAlpha) { r = d; break; }
                                        float a = fa != null ? fa[si] : 1;
                                        if (bl.Clamp) a = Math.Clamp(a, 0, 1);
                                        r = d + s * a;
                                        break;
                                    }
                                default:
                                    {
                                        if (isAlpha) { r = s; break; }
                                        float a = ba != null ? ba[di] : 1;
                                        if (bl.Clamp) a = Math.Clamp(a, 0, 1);
                                        r = s + d * a;
                                        break;
                                    }
                            }
                            dst[di] = r;
                        }
                    }
                }
            }
        }
    }
}
