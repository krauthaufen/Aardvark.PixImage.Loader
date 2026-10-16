using System;
using System.Diagnostics;

namespace Aardvark.ImageCodecs.JpegXl
{
    /// <summary>Development aid: stage timings printed to stderr when PIXCODEC_JXL_PROFILE=1.</summary>
    internal static class JxlProfile
    {
        public static readonly bool On = Environment.GetEnvironmentVariable("PIXCODEC_JXL_PROFILE") == "1";
        static readonly Stopwatch sw = Stopwatch.StartNew();
        static double last;

        public static void Mark(string stage)
        {
            if (!On) return;
            double t = sw.Elapsed.TotalMilliseconds;
            Console.Error.WriteLine($"  {stage,-28} {t - last,8:F1} ms");
            last = t;
        }

        public static void Reset()
        {
            if (On) last = sw.Elapsed.TotalMilliseconds;
        }
    }
}
