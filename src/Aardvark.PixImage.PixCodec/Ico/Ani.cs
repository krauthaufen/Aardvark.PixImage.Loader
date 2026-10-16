using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Aardvark.ImageCodecs
{
    /// <summary>Structure of an animated cursor (ANI) without decoded pixels.</summary>
    public class AniAnimationInfo
    {
        /// <summary>Size of the first step's image (the largest image of its icon).</summary>
        public int Width { get; internal set; }
        public int Height { get; internal set; }
        /// <summary>Number of stored icons / cursors ("frames" of the anih header).</summary>
        public int FrameCount { get; internal set; }
        /// <summary>Number of animation steps (the sequence shown, which can repeat or reorder the frames).</summary>
        public int StepCount { get; internal set; }
        /// <summary>Default step duration in jiffies (1/60 s) from the anih header.</summary>
        public int DefaultJiffies { get; internal set; }
        /// <summary>The frames are icons / cursors (anih flag AF_ICON; always the case for files Windows reads).</summary>
        public bool FramesAreIcons { get; internal set; }
        /// <summary>The frame of each step (the "seq " chunk, or 0, 1, 2, ... without one).</summary>
        public IReadOnlyList<int> Sequence { get; internal set; } = Array.Empty<int>();
        /// <summary>Title (INFO list, INAM) and author (IART), if present.</summary>
        public string? Title { get; internal set; }
        public string? Artist { get; internal set; }
    }

    /// <summary>A decoded animated cursor: its frames and the steps of the animation.</summary>
    public sealed class AniAnimation : AniAnimationInfo
    {
        /// <summary>The steps in display order.</summary>
        public IReadOnlyList<AniStep> Steps { get; internal set; } = Array.Empty<AniStep>();

        /// <summary>The stored frames (each the largest image of its icon / cursor), in file order.</summary>
        public IReadOnlyList<RawImage> Frames { get; internal set; } = Array.Empty<RawImage>();

        /// <summary>The stored frames as ICO / CUR files (decode other sizes with <see cref="Ico"/>).</summary>
        public IReadOnlyList<byte[]> FrameData { get; internal set; } = Array.Empty<byte[]>();
    }

    /// <summary>One step of an animated cursor.</summary>
    public sealed class AniStep
    {
        public int Index { get; internal set; }
        /// <summary>Index of the stored frame shown.</summary>
        public int Frame { get; internal set; }
        /// <summary>Duration in jiffies (1/60 s): the "rate" chunk's entry, else the header's default.</summary>
        public int Jiffies { get; internal set; }
        public double DelayMs => Jiffies * 1000.0 / 60;
        /// <summary>The frame's image (shared by the steps showing the same frame).</summary>
        public RawImage Image { get; internal set; } = null!;
        /// <summary>Cursor hotspot of the image (0, 0 for icons).</summary>
        public int HotspotX { get; internal set; }
        public int HotspotY { get; internal set; }
    }

    /// <summary>One step handed to <see cref="Ani.Encode(IEnumerable{AniInputFrame}, Stream, AniEncodeOptions?)"/>.
    /// Steps that pass the same RawImage instance share one stored frame.</summary>
    public readonly record struct AniInputFrame(RawImage Image, int Jiffies = 10, int HotspotX = 0, int HotspotY = 0)
    {
        /// <summary>A step shown for about the given number of milliseconds (rounded to jiffies of 1/60 s, at least one).</summary>
        public static AniInputFrame FromMilliseconds(RawImage image, double delayMs, int hotspotX = 0, int hotspotY = 0)
            => new(image, Math.Max(1, (int)Math.Round(delayMs * 60 / 1000)), hotspotX, hotspotY);
    }

    public sealed class AniEncodeOptions
    {
        /// <summary>Store the frames as cursors (CUR, with hotspots; default) rather than icons.</summary>
        public bool Cursor { get; set; } = true;
        /// <summary>Title and author for the INFO list (INAM / IART), left out when null.</summary>
        public string? Title { get; set; }
        public string? Artist { get; set; }
        /// <summary>Frames at least this wide or high are stored as PNG inside the cursor, see <see cref="IcoEncodeOptions.PngFromSize"/>.</summary>
        public int PngFromSize { get; set; } = 256;
    }

    /// <summary>
    /// Windows animated cursors (ANI): a RIFF "ACON" file with an anih header, optional "rate" (per step durations) and
    /// "seq " (step order) chunks and a LIST "fram" of icon / cursor files, which <see cref="Ico"/> decodes.
    /// <see cref="Decode"/> returns the first step's image, <see cref="ImageCodec.DecodeAll(ReadOnlySpan{byte})"/> the image
    /// of every step, <see cref="DecodeAnimation"/> steps with durations and hotspots. Writes animated cursors and icons.
    /// </summary>
    public static class Ani
    {
        const int AfIcon = 1, AfSequence = 2;

        /// <summary>Whether the data is a RIFF file of form "ACON".</summary>
        public static bool IsMatch(ReadOnlySpan<byte> h)
            => h.Length >= 12 && h.Slice(0, 4).SequenceEqual("RIFF"u8) && h.Slice(8, 4).SequenceEqual("ACON"u8);

        sealed class Parsed
        {
            public AniAnimationInfo Info = new();
            public List<(int off, int len)> Frames = new();
            public int[]? Rates;
            public int[]? Seq;
        }

        static Parsed Parse(ReadOnlySpan<byte> d)
        {
            if (!IsMatch(d)) throw new ImageFormatException("ANI: not an animated cursor");
            var p = new Parsed();
            long end = Math.Min(d.Length, 8L + BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(4)));
            bool header = false;
            int frames = 0, steps = 0, flags = 0;
            void Walk(ReadOnlySpan<byte> d, long pos, long end, string list, int depth)
            {
                while (pos + 8 <= end)
                {
                    var id = d.Slice((int)pos, 4);
                    uint size = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice((int)pos + 4));
                    long body = pos + 8, bodyEnd = Math.Min(end, body + size);
                    int len = (int)(bodyEnd - body);
                    if (id.SequenceEqual("LIST"u8) && len >= 4 && depth < 4)
                    {
                        var form = Encoding.ASCII.GetString(d.Slice((int)body, 4));
                        Walk(d, body + 4, bodyEnd, form, depth + 1);
                    }
                    else if (list == "fram" && id.SequenceEqual("icon"u8)) p.Frames.Add(((int)body, len));
                    else if (list == "INFO" && (id.SequenceEqual("INAM"u8) || id.SequenceEqual("IART"u8)))
                    {
                        var s = d.Slice((int)body, len);
                        int z = s.IndexOf((byte)0);
                        var text = Encoding.Latin1.GetString(z >= 0 ? s.Slice(0, z) : s);
                        if (id[1] == (byte)'N') p.Info.Title = text; else p.Info.Artist = text;
                    }
                    else if (id.SequenceEqual("anih"u8))
                    {
                        if (len < 36) throw new ImageFormatException("ANI: anih header truncated");
                        var a = d.Slice((int)body, 36);
                        frames = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(a.Slice(4)), int.MaxValue);
                        steps = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(a.Slice(8)), int.MaxValue);
                        p.Info.DefaultJiffies = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(a.Slice(28)), int.MaxValue);
                        flags = (int)BinaryPrimitives.ReadUInt32LittleEndian(a.Slice(32));
                        header = true;
                    }
                    else if (id.SequenceEqual("rate"u8)) p.Rates = Words(d.Slice((int)body, len));
                    else if (id.SequenceEqual("seq "u8)) p.Seq = Words(d.Slice((int)body, len));
                    pos = body + size + (size & 1);
                }
            }
            Walk(d, 12, end, "", 0);
            if (!header) throw new ImageFormatException("ANI: no anih header");
            if (p.Frames.Count == 0) throw new ImageFormatException("ANI: no frames");
            if ((flags & AfIcon) == 0) throw new ImageNotSupportedException("ANI: raw bitmap frames (without AF_ICON) are not supported");
            // the chunks decide: headers that disagree with them are common
            int stored = p.Frames.Count;
            if (p.Seq != null && (flags & AfSequence) == 0 && p.Seq.Length == 0) p.Seq = null;
            int n = p.Seq?.Length ?? (steps > 0 && steps <= stored ? steps : stored);
            var seq = new int[n];
            for (int i = 0; i < n; i++)
            {
                int f = p.Seq != null ? p.Seq[i] : i;
                if ((uint)f >= (uint)stored) throw new ImageFormatException($"ANI: step {i} shows frame {f} of {stored}");
                seq[i] = f;
            }
            p.Info.FrameCount = stored;
            p.Info.StepCount = n;
            p.Info.Sequence = seq;
            p.Info.FramesAreIcons = true;
            _ = frames;
            return p;
        }

        static int[] Words(ReadOnlySpan<byte> s)
        {
            var r = new int[s.Length / 4];
            for (int i = 0; i < r.Length; i++) r[i] = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(i * 4)), int.MaxValue);
            return r;
        }

        static int Jiffies(Parsed p, int step) => p.Rates != null && step < p.Rates.Length ? p.Rates[step] : p.Info.DefaultJiffies;

        /// <summary>The structure (frames, steps, sequence, durations, texts) without decoding images.</summary>
        public static AniAnimationInfo GetAnimationInfo(ReadOnlySpan<byte> data)
        {
            var p = Parse(data);
            var (off, len) = p.Frames[p.Info.Sequence[0]];
            var ii = Ico.GetInfo(data.Slice(off, len));
            p.Info.Width = ii.Width; p.Info.Height = ii.Height;
            return p.Info;
        }

        /// <summary>Header information on the first step's image; FrameCount is the number of steps.</summary>
        public static ImageInfo GetInfo(ReadOnlySpan<byte> data)
        {
            var p = Parse(data);
            var (off, len) = p.Frames[p.Info.Sequence[0]];
            var ii = Ico.GetInfo(data.Slice(off, len));
            return ii with { Format = ImageFormat.Ani, FrameCount = p.Info.StepCount, IsAnimated = p.Info.StepCount > 1 };
        }

        /// <summary>Decodes the first step's image (the largest image of its icon).</summary>
        public static RawImage Decode(ReadOnlySpan<byte> data)
        {
            var p = Parse(data);
            var (off, len) = p.Frames[p.Info.Sequence[0]];
            return Ico.Decode(data.Slice(off, len));
        }

        /// <summary>Decodes every frame once and lists the steps.</summary>
        public static AniAnimation DecodeAnimation(ReadOnlySpan<byte> data)
        {
            var p = Parse(data);
            var i = p.Info;
            var frames = new RawImage[p.Frames.Count];
            var blobs = new byte[frames.Length][];
            var hot = new (int, int)[frames.Length];
            for (int f = 0; f < frames.Length; f++)
            {
                var (off, len) = p.Frames[f];
                var blob = data.Slice(off, len);
                blobs[f] = blob.ToArray();
                var es = Ico.ReadEntries(blob);
                int best = 0;
                for (int k = 1; k < es.Count; k++)
                {
                    long a = (long)es[k].Width * es[k].Height, b = (long)es[best].Width * es[best].Height;
                    if (a > b || a == b && es[k].BitCount > es[best].BitCount) best = k;
                }
                frames[f] = Ico.Decode(blob, new IcoDecodeOptions { Index = best });
                hot[f] = (es[best].HotspotX, es[best].HotspotY);
            }
            var steps = new AniStep[i.StepCount];
            for (int s = 0; s < steps.Length; s++)
            {
                int f = i.Sequence[s];
                steps[s] = new AniStep { Index = s, Frame = f, Jiffies = Jiffies(p, s), Image = frames[f], HotspotX = hot[f].Item1, HotspotY = hot[f].Item2 };
            }
            var first = frames[i.Sequence[0]];
            return new AniAnimation
            {
                Width = first.Width, Height = first.Height, FrameCount = i.FrameCount, StepCount = i.StepCount, DefaultJiffies = i.DefaultJiffies,
                FramesAreIcons = i.FramesAreIcons, Sequence = i.Sequence, Title = i.Title, Artist = i.Artist,
                Steps = steps, Frames = frames, FrameData = blobs,
            };
        }

        /// <summary>The image of every step, in display order (steps showing the same frame share the instance).</summary>
        public static RawImage[] DecodeAll(ReadOnlySpan<byte> data)
        {
            var a = DecodeAnimation(data);
            var r = new RawImage[a.Steps.Count];
            for (int i = 0; i < r.Length; i++) r[i] = a.Steps[i].Image;
            return r;
        }

        /// <summary>
        /// Writes an animated cursor (or icon): one stored frame per distinct image (steps passing the same RawImage instance
        /// share it, through a "seq " chunk), a "rate" chunk when the durations differ. Images: UInt8, at most 256 x 256.
        /// </summary>
        public static void Encode(IEnumerable<AniInputFrame> steps, Stream output, AniEncodeOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(steps);
            ArgumentNullException.ThrowIfNull(output);
            options ??= new AniEncodeOptions();
            var list = new List<AniInputFrame>(steps);
            if (list.Count == 0) throw new ArgumentException("ANI: at least one frame needed");
            var frames = new List<AniInputFrame>();
            var seq = new int[list.Count];
            bool shared = false, rates = false;
            for (int s = 0; s < list.Count; s++)
            {
                var st = list[s];
                ArgumentNullException.ThrowIfNull(st.Image);
                if (st.Jiffies <= 0) throw new ArgumentException("ANI: durations must be at least one jiffy");
                if (st.Jiffies != list[0].Jiffies) rates = true;
                int f = frames.FindIndex(x => ReferenceEquals(x.Image, st.Image) && x.HotspotX == st.HotspotX && x.HotspotY == st.HotspotY);
                if (f < 0) { f = frames.Count; frames.Add(st); } else shared = true;
                seq[s] = f;
            }
            var body = new MemoryStream();
            body.Write("ACON"u8);
            if (options.Title != null || options.Artist != null)
            {
                var info = new MemoryStream();
                info.Write("INFO"u8);
                if (options.Title != null) Chunk(info, "INAM", Zero(options.Title));
                if (options.Artist != null) Chunk(info, "IART", Zero(options.Artist));
                Chunk(body, "LIST", info.ToArray());
            }
            var anih = new byte[36];
            BinaryPrimitives.WriteUInt32LittleEndian(anih, 36);
            BinaryPrimitives.WriteUInt32LittleEndian(anih.AsSpan(4), (uint)frames.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(anih.AsSpan(8), (uint)list.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(anih.AsSpan(28), (uint)list[0].Jiffies);
            BinaryPrimitives.WriteUInt32LittleEndian(anih.AsSpan(32), (uint)(AfIcon | (shared ? AfSequence : 0)));
            Chunk(body, "anih", anih);
            if (rates)
            {
                var r = new byte[4 * list.Count];
                for (int s = 0; s < list.Count; s++) BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(4 * s), (uint)list[s].Jiffies);
                Chunk(body, "rate", r);
            }
            if (shared)
            {
                var q = new byte[4 * list.Count];
                for (int s = 0; s < list.Count; s++) BinaryPrimitives.WriteUInt32LittleEndian(q.AsSpan(4 * s), (uint)seq[s]);
                Chunk(body, "seq ", q);
            }
            var fram = new MemoryStream();
            fram.Write("fram"u8);
            foreach (var f in frames)
            {
                var ico = Ico.Encode(new[] { f.Image }, new IcoEncodeOptions
                {
                    Cursor = options.Cursor, Hotspots = new[] { (f.HotspotX, f.HotspotY) }, PngFromSize = options.PngFromSize,
                });
                Chunk(fram, "icon", ico);
            }
            Chunk(body, "LIST", fram.ToArray());
            Span<byte> head = stackalloc byte[8];
            "RIFF"u8.CopyTo(head);
            BinaryPrimitives.WriteUInt32LittleEndian(head.Slice(4), checked((uint)body.Length));
            output.Write(head);
            body.Position = 0;
            body.CopyTo(output);
        }

        public static byte[] Encode(IEnumerable<AniInputFrame> steps, AniEncodeOptions? options = null)
        {
            var ms = new MemoryStream();
            Encode(steps, ms, options);
            return ms.ToArray();
        }

        static byte[] Zero(string s)
        {
            var b = Encoding.Latin1.GetBytes(s);
            Array.Resize(ref b, b.Length + 1);
            return b;
        }

        static void Chunk(Stream o, string id, ReadOnlySpan<byte> data)
        {
            Span<byte> h = stackalloc byte[8];
            for (int i = 0; i < 4; i++) h[i] = (byte)id[i];
            BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(4), (uint)data.Length);
            o.Write(h);
            o.Write(data);
            if ((data.Length & 1) != 0) o.WriteByte(0);
        }
    }
}
