using System.Collections.Concurrent;
using System.Diagnostics;

namespace Apfs.Mount;

/// <summary>WinFsp geri çağrılarının sayısı, toplam süresi ve okunan bayt (performans teşhisi için; `apfs mount --stats`).</summary>
public sealed class CallStats
{
    private readonly ConcurrentDictionary<string, Counter> _counters = new();

    private sealed class Counter
    {
        public long Count;
        public long Ticks;
        public long Bytes;
    }

    public bool Enabled { get; set; }

    public long Start() => Enabled ? Stopwatch.GetTimestamp() : 0;

    public void Stop(string op, long start, long bytes = 0)
    {
        if (!Enabled) return;
        var c = _counters.GetOrAdd(op, _ => new Counter());
        Interlocked.Increment(ref c.Count);
        Interlocked.Add(ref c.Ticks, Stopwatch.GetTimestamp() - start);
        Interlocked.Add(ref c.Bytes, bytes);
    }

    public IEnumerable<string> Report()
    {
        foreach (var (op, c) in _counters.OrderByDescending(kv => kv.Value.Ticks))
        {
            double ms = c.Ticks * 1000.0 / Stopwatch.Frequency;
            string extra = c.Bytes > 0 ? CoreText.T("Stats.AvgBytes", c.Bytes / Math.Max(1, c.Count) / 1024.0) : "";
            yield return CoreText.T("Stats.Line", op, c.Count, ms, ms * 1000 / Math.Max(1, c.Count), extra);
        }
    }
}
