using System;

namespace Aardvark.ImageCodecs.Av1
{
    /// <summary>Sample planes of a frame (all bit depths as ushort).</summary>
    internal sealed class Av1Frame
    {
        public readonly ushort[][] Planes = new ushort[3][];
        public readonly int[] Stride = new int[3];
        public readonly int[] AllocW = new int[3], AllocH = new int[3];
        public int NumPlanes, SubX, SubY, BitDepth;
        /// <summary>The visible size (luma): UpscaledWidth x FrameHeight.</summary>
        public int Width, Height;

        public Av1Frame(int numPlanes, int allocW, int allocH, int subX, int subY, int bitDepth)
        {
            NumPlanes = numPlanes; SubX = subX; SubY = subY; BitDepth = bitDepth;
            for (int p = 0; p < numPlanes; p++)
            {
                int w = p == 0 ? allocW : (allocW + subX) >> subX;
                int h = p == 0 ? allocH : (allocH + subY) >> subY;
                AllocW[p] = w; AllocH[p] = h; Stride[p] = w;
                Planes[p] = new ushort[(long)w * h];
            }
        }

        public int PlaneWidth(int p) => p == 0 ? Width : (Width + SubX) >> SubX;
        public int PlaneHeight(int p) => p == 0 ? Height : (Height + SubY) >> SubY;

        public Av1Frame CloneLayout()
        {
            var f = new Av1Frame(NumPlanes, AllocW[0], AllocH[0], SubX, SubY, BitDepth) { Width = Width, Height = Height };
            return f;
        }

        public Av1Frame Clone()
        {
            var f = CloneLayout();
            for (int p = 0; p < NumPlanes; p++) Array.Copy(Planes[p], f.Planes[p], Planes[p].Length);
            return f;
        }
    }

    /// <summary>A decoded frame as output by the decoder (after film grain).</summary>
    internal sealed class Av1Picture
    {
        public Av1Frame Frame = null!;
        public int Width, Height, BitDepth, SubX, SubY;
        public bool Monochrome;
        public int ColorPrimaries, TransferCharacteristics, MatrixCoefficients;
        public bool FullRange;
        public int ChromaSamplePosition;
        public int SpatialId, TemporalId;
        public bool ShowExisting;
        public int FrameType;
    }
}
