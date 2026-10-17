using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;

namespace Aardvark.ImageCodecs.Av1
{
    /// <summary>
    /// Development aid: counts of the coding tools a stream uses, printed to stderr when PIXCODEC_AV1_STATS=1 (tool
    /// coverage of test streams).
    /// </summary>
    internal static class Av1Stats
    {
        public static readonly bool On = Environment.GetEnvironmentVariable("PIXCODEC_AV1_STATS") == "1";
        /// <summary>PIXCODEC_AV1_STATS=1 or =time: the time spent in the decoding stages.</summary>
        public static readonly bool TimeOn = On || Environment.GetEnvironmentVariable("PIXCODEC_AV1_STATS") == "time";
        static readonly ConcurrentDictionary<string, long> counts = new();

        public static void Count(string key, long n = 1)
        {
            if (!TimeOn) return;
            counts.AddOrUpdate(key, n, (_, v) => v + n);
        }

        /// <summary>Adds the microseconds since <paramref name="start"/> (a Stopwatch timestamp) to <paramref name="key"/>; returns now.</summary>
        public static long Time(string key, long start)
        {
            if (!TimeOn) return 0;
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            Count(key + "_us", (now - start) * 1000000 / System.Diagnostics.Stopwatch.Frequency);
            return now;
        }

        public static void Print()
        {
            if (!TimeOn) return;
            foreach (var kv in counts.OrderBy(k => k.Key, StringComparer.Ordinal)) Console.Error.WriteLine($"  {kv.Key,-32} {kv.Value}");
            counts.Clear();
        }
    }
}
