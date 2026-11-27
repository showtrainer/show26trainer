using System.Buffers.Binary;
using System.Runtime.InteropServices;
using ShowTrainer.Core.Guest;
using ShowTrainer.Core.Infra;
using ShowTrainer.Core.Session;

namespace ShowTrainer.Core.Resolve;

public static class PointerResolver
{
    /// <summary>Re-resolves an entry for this launch. Raw heap addresses without a chain stay unresolved.</summary>
    public static bool Resolve(GuestLayout layout, AddressEntry e, bool buildMatches)
    {
        ulong? a = !buildMatches || layout.Main == null ? null
            : e.Row != null ? CheckForWrite(layout, e, out _)
            : e.Pointer != null ? Resolve(layout, e.Pointer)
            : e.MainOffset is long m ? layout.Main.Va + (ulong)m : null;
        if (a is ulong va) e.Address = va;
        return e.Resolved = a != null;
    }

    /// <summary>Checked immediately before every write. Returns the address, or null with the reason to refuse.</summary>
    public static ulong? CheckForWrite(GuestLayout layout, AddressEntry e, out string why)
    {
        why = "";
        ulong at = e.Address;
        if (e.Pointer != null)
        {
            if (Resolve(layout, e.Pointer) is not ulong a) { why = "pointer does not resolve (loading?)"; return null; }
            at = a;
            int agree = 1 + (e.AltPointers?.Count(p => Resolve(layout, p) == a) ?? 0);
            if (agree < e.MinAgree) { why = $"only {agree} of {e.MinAgree} required pointers agree (loading?)"; return null; }
        }
        if (e.Row is SeasonRow row)
        {
            ulong? found = null; int hits = 0;
            for (int i = 0; i < row.MaxRows; i++)
            {
                ulong v = at + (ulong)(i * row.Stride);
                if (layout.Memory.ReadBytes(v + (ulong)(long)row.YearOffset, 2) is not { } y) break;
                if (BinaryPrimitives.ReadUInt16LittleEndian(y) == row.Year) { found ??= v; hits++; }
            }
            if (hits != 1) { why = hits == 0 ? $"no {row.Year} row (not played yet, or loading?)" : $"{hits} rows for {row.Year}"; return null; }
            at = found!.Value;
        }
        else if (e.Pointer == null && !e.Resolved) { why = "no confirmed address for this launch"; return null; }
        foreach (var g in e.Guards ?? new())
        {
            var b = layout.Memory.ReadBytes(at + (ulong)(long)g.Offset, g.Length);
            if (b == null || b.Any(x => x < g.Min || x > g.Max)) { why = "the surrounding record does not look right (loading?)"; return null; }
        }
        return at;
    }

    /// <summary>Extra bytes past the pointer chain's target (non-zero only for season rows that have moved).</summary>
    public static long? RowShift(GuestLayout layout, AddressEntry e)
    {
        if (e.Pointer == null || CheckForWrite(layout, e, out _) is not ulong at || Resolve(layout, e.Pointer) is not ulong p) return null;
        return (long)(at - p);
    }

    /// <summary>Follows [main+Base] -> [+o] ... -> +last. Null if any hop is unmapped.</summary>
    public static ulong? Resolve(GuestLayout layout, PointerChain c)
    {
        if (layout.Main == null || c.Offsets.Count == 0) return null;
        ulong? p = layout.Memory.ReadU64(layout.Main.Va + (ulong)c.BaseOffset);
        for (int i = 0; p != null; i++)
        {
            if (p == 0) return null; // a null link (record freed during loading) never resolves
            ulong at = p.Value + (ulong)c.Offsets[i];
            if (i == c.Offsets.Count - 1) return layout.Memory.IsMapped(at) ? at : null;
            p = layout.Memory.ReadU64(at);
        }
        return null;
    }
}

/// <summary>Reverse pointer search: which chains starting in main's static data lead to a target address?</summary>
public static class PointerScanner
{
    private sealed record Node(ulong Addr, Node? Next, long Offset);

    public static List<PointerChain> Scan(GuestLayout layout, ulong target, int maxDepth, long firstMax, long nextMax, int maxResults = 2000)
    {
        var main = layout.Main ?? throw new InvalidOperationException("Main module not detected.");
        var mem = layout.Memory;
        mem.Refresh();
        var (heapVa, heapSize) = (layout.HeapBase, layout.HeapSize);
        var gate = new object();
        var vals = new List<ulong>(); var locs = new List<ulong>();
        // Only main and the heap can hold useful links; only heap addresses can be link targets.
        var items = mem.Segments.Where(s => (s.Va < main.End && s.End > main.Va) || (s.Va >= heapVa && s.Va < heapVa + heapSize))
            .SelectMany(s => Enumerable.Range(0, (int)((s.Size + 0xFFFFF) >> 20)).Select(i => (s, off: (ulong)i << 20))).ToList();
        Parallel.ForEach(items, it =>
        {
            var buf = new byte[(int)Math.Min(1UL << 20, it.s.Size - it.off)];
            if (!mem.Process.Read(it.s.Host + it.off, buf)) return;
            var v = new List<ulong>(); var l = new List<ulong>();
            var q = MemoryMarshal.Cast<byte, ulong>(buf);
            for (int i = 0; i < q.Length; i++)
                if (q[i] - heapVa < heapSize) { v.Add(q[i]); l.Add(it.s.Va + it.off + (ulong)i * 8); }
            lock (gate) { vals.AddRange(v); locs.AddRange(l); }
        });
        var va = vals.ToArray(); var la = locs.ToArray();
        vals = null!; locs = null!;
        Array.Sort(va, la);
        Log.Info($"Pointer scan: {va.Length:N0} heap pointers indexed");

        var results = new List<PointerChain>();
        var seen = new HashSet<ulong>();
        var frontier = new List<Node> { new(target, null, 0) };
        for (int depth = 1; depth <= maxDepth && frontier.Count > 0 && results.Count < maxResults; depth++)
        {
            long max = depth == 1 ? firstMax : nextMax;
            var next = new List<Node>();
            foreach (var n in frontier)
            {
                int i = Array.BinarySearch(va, n.Addr);
                if (i < 0) i = ~i - 1;
                else while (i + 1 < va.Length && va[i + 1] == n.Addr) i++;
                for (; i >= 0 && n.Addr - va[i] <= (ulong)max; i--)
                {
                    if (!seen.Add(la[i])) continue;
                    var hop = new Node(la[i], n, (long)(n.Addr - va[i]));
                    if (la[i] >= main.Va && la[i] < main.End)
                    {
                        var c = new PointerChain { BaseOffset = (long)(la[i] - main.Va) };
                        for (Node? x = hop; x?.Next != null; x = x.Next) c.Offsets.Add(x.Offset);
                        if (PointerResolver.Resolve(layout, c) == target) results.Add(c);
                    }
                    else next.Add(hop);
                }
            }
            Log.Info($"Pointer scan depth {depth}: {results.Count} chains, {next.Count:N0} links to follow");
            // Small offsets are the likeliest real struct fields.
            frontier = next.OrderBy(n => n.Offset).Take(300_000).ToList();
        }
        return results.OrderBy(c => c.Offsets.Count).ThenBy(c => c.Offsets.Sum()).Take(maxResults).ToList();
    }
}
