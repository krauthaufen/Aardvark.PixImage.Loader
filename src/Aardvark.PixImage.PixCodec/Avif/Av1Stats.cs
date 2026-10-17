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
        static readonly ConcurrentDictionary<string, long> counts = new();

        public static void Count(string key, long n = 1)
        {
            if (!On) return;
            counts.AddOrUpdate(key, n, (_, v) => v + n);
        }

        public static void Print()
        {
            if (!On) return;
            foreach (var kv in counts.OrderBy(k => k.Key, StringComparer.Ordinal)) Console.Error.WriteLine($"  {kv.Key,-32} {kv.Value}");
            counts.Clear();
        }
    }
}
