using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace ExileMaps.Classes;

// Rolling 60-frame average per named bucket. Thread-safe (background cache refresh writes too).
public static class PerfMonitor
{
    private const int Samples = 60;
    private static readonly object Lock = new();
    private static readonly Dictionary<string, long[]> Buffers = new();
    private static readonly Dictionary<string, int> Heads = new();

    public static void Record(string key, long elapsedTicks)
    {
        lock (Lock)
        {
            if (!Buffers.TryGetValue(key, out var buf))
            {
                buf = new long[Samples];
                Buffers[key] = buf;
                Heads[key] = 0;
            }
            int h = Heads[key];
            buf[h] = elapsedTicks;
            Heads[key] = (h + 1) % Samples;
        }
    }

    private static double AvgMs(long[] buf)
    {
        long sum = 0;
        foreach (var t in buf) sum += t;
        return sum / (double)Samples / Stopwatch.Frequency * 1000.0;
    }

    public static List<(string Key, double Ms)> Snapshot()
    {
        lock (Lock)
            return Buffers.OrderBy(kv => kv.Key).Select(kv => (kv.Key, AvgMs(kv.Value))).ToList();
    }
}
