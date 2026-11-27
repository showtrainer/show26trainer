using System.Buffers.Binary;
using ShowTrainer.Core.Guest;

namespace ShowTrainer.Core.Scan;

public sealed record GroupHit(ulong Va, int Score, string Values);

/// <summary>Finds windows of consecutive elements containing all required values, in any order; optional values raise the score.</summary>
public static class GroupScanner
{
    public static List<GroupHit> Scan(GuestLayout layout, ValueKind kind, int window, int[] required, int[] optional, int top = 25)
    {
        if (required.Concat(optional).Any(x => x is < 0 or > 255)) throw new ArgumentException("Values must be 0..255.");
        int size = ValueCodec.SizeOf(kind);
        var need = new int[256]; var opt = new int[256];
        foreach (var x in required) need[x]++;
        foreach (var x in optional) opt[x]++;
        int kinds = need.Count(x => x > 0), span = window * size;
        layout.Memory.Refresh();
        var items = layout.Memory.Segments.SelectMany(s => Enumerable.Range(0, (int)((s.Size + 0xFFFFF) >> 20)).Select(i => (s, off: (ulong)i << 20))).ToList();
        var gate = new object(); var hits = new List<GroupHit>();
        Parallel.ForEach(items, it =>
        {
            // Overlap into the next chunk so windows on the boundary are not missed.
            int len = (int)Math.Min((1UL << 20) + (ulong)span, it.s.Size - it.off) / size * size;
            var b = new byte[len];
            if (!layout.Memory.Process.Read(it.s.Host + it.off, b)) return;
            int n = len / size; var e = new short[n];
            for (int i = 0; i < n; i++)
            {
                double d = ValueCodec.ToDouble(kind, b.AsSpan(i * size, size));
                e[i] = d >= 0 && d <= 255 && d == Math.Floor(d) ? (short)d : (short)-1;
            }
            var cnt = new int[256]; int ok = 0; GroupHit? best = null; int bestAt = 0;
            void Flush() { if (best != null) { lock (gate) hits.Add(best); best = null; } }
            for (int i = 0; i < n; i++)
            {
                if (e[i] >= 0 && ++cnt[e[i]] == need[e[i]] && need[e[i]] > 0) ok++;
                if (i >= window) { int o = e[i - window]; if (o >= 0 && cnt[o]-- == need[o] && need[o] > 0) ok--; }
                if (ok != kinds) continue;
                int score = 0;
                for (int v = 0; v < 256; v++) if (opt[v] > 0) score += Math.Min(opt[v], Math.Max(0, cnt[v] - need[v]));
                int start = Math.Max(0, i - window + 1);
                if (best != null && start - bestAt > window) Flush();
                if (best == null || score > best.Score)
                { best = new(it.s.Va + it.off + (ulong)(start * size), score, string.Join(" ", e.Skip(start).Take(window).Select(x => x < 0 ? "." : x.ToString()))); bestAt = start; }
            }
            Flush();
        });
        return hits.DistinctBy(h => h.Va).OrderByDescending(h => h.Score).ThenBy(h => h.Va).Take(top).ToList();
    }
}
