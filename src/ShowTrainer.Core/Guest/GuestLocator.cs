using System.Buffers.Binary;
using ShowTrainer.Core.Infra;
using ShowTrainer.Core.Native;

namespace ShowTrainer.Core.Guest;

public sealed record GuestModule(int Index, ulong Va, ulong Size, ulong Mod0Va, string? Name)
{
    public ulong End => Va + Size;
    public string? BuildId { get; set; }
    public override string ToString() => $"#{Index} {Name ?? "?"} VA {Va:X10}-{End:X10} size {Size:X} MOD0@+{Mod0Va - Va:X}{(BuildId != null ? " build " + BuildId : "")}";
}

/// <summary>Everything we learned about the running game's address space.</summary>
public sealed class GuestLayout
{
    public required GuestMemory Memory { get; init; }
    public List<GuestModule> Modules { get; init; } = new();
    public GuestModule? Main { get; set; }
    public ulong HeapBase { get; set; }
    public ulong HeapSize { get; set; }
    public string? BuildId => Main?.BuildId;
    public string? TitleId { get; set; }
    public string? GameTitle { get; set; }
    public List<string> DetectionLog { get; } = new();

    public bool GameDetected => Main != null;

    public string FormatAddress(ulong va)
    {
        var parts = new List<string> { $"{va:X10}" };
        if (Main != null && va >= Main.Va && va < Main.Va + 0x1_0000_0000) parts.Add($"main+{va - Main.Va:X}");
        var mod = Modules.FirstOrDefault(m => va >= m.Va && va < m.End);
        if (mod != null && mod != Main) parts.Add($"{mod.Name ?? "mod" + mod.Index}+{va - mod.Va:X}");
        if (HeapSize != 0 && va >= HeapBase && va < HeapBase + HeapSize) parts.Add($"heap+{va - HeapBase:X}");
        return string.Join("  ", parts);
    }

    public long? MainOffset(ulong va) => Main == null ? null : (long)(va - Main.Va);
    public long? HeapOffset(ulong va) => HeapSize == 0 ? null : (long)(va - HeapBase);
}

/// <summary>
/// Finds the Switch guest address space inside the emulator process.
///
/// Strategy (each step is logged so failures can be debugged):
///  1. Find the DRAM backing: the largest committed MEM_MAPPED read/write view (4/6/8 GB).
///  2. Find the software page table: a private VirtualAlloc of 2^(AS bits - 12) * 8 bytes whose
///     non-zero entries decode to host pointers inside the DRAM backing.
///  3. Fallback: the fastmem arena (a huge reserved run with MEM_MAPPED views; host = arena + VA).
///  4. Verify by locating NSO modules (MOD0 headers), pick main, read its GNU build-id, and pick the heap.
/// </summary>
public static class GuestLocator
{
    public static GuestLayout? Locate(HostProcess proc, Action<string>? progress = null)
    {
        var log = new List<string>();
        void L(string s) { log.Add(s); Log.Info("[detect] " + s); progress?.Invoke(s); }

        var regions = proc.EnumerateRegions();
        L($"{regions.Count} host regions enumerated");

        // 1. DRAM backing
        var backingCands = regions.Where(r => r.Type == Win32.MEM_MAPPED && r.IsCommitted && (r.Protect & Win32.PAGE_READWRITE) != 0 && r.Size >= 0xF000_0000)
                                  .OrderByDescending(r => r.Size).ToList();
        foreach (var b in backingCands) L($"DRAM backing candidate: {b}");
        if (backingCands.Count == 0) { L("No DRAM backing (>=3.75 GB RW mapped view) found. Is a game emulator running?"); }
        var backing = backingCands.FirstOrDefault();

        GuestMemory? mem = null;

        // 2. Page table
        if (backingCands.Count > 0)
        {
            var best = FindPageTable(proc, regions, backing, L);
            if (best != null) mem = best;
        }

        // 3. Fastmem arena
        FastmemGuestMemory? fast = FindFastmem(proc, regions, L);
        if (mem == null && fast != null) mem = fast;
        else if (mem != null && fast != null)
        {
            L($"Fastmem arena also present ({fast.Segments.Count} segments); using page table as primary.");
            // Cross-check: the same guest page must read identically through both paths.
            var probe = mem.Segments.FirstOrDefault(s => s.Size >= 0x1000 && fast.IsMapped(s.Va));
            if (probe.Size != 0)
            {
                var a = mem.ReadBytes(probe.Va, 256); var b = fast.ReadBytes(probe.Va, 256);
                L($"Cross-check VA {probe.Va:X}: page table vs fastmem {(a != null && b != null && a.AsSpan().SequenceEqual(b) ? "MATCH" : "MISMATCH")}");
            }
        }

        if (mem == null)
        {
            L("FAILED: could not find guest memory. Make sure the game is booted (not just the game list).");
            return null;
        }
        L($"Guest memory: {mem.Describe()}; {mem.Segments.Count} segments, {mem.Segments.Aggregate(0UL, (a, s) => a + s.Size) >> 20} MB mapped");

        var layout = new GuestLayout { Memory = mem };
        layout.Modules.AddRange(FindModules(mem, L));
        PickMain(layout, L);
        PickHeap(layout, L);
        TitleInfo.Fill(layout, proc, L);
        layout.DetectionLog.AddRange(log);
        return layout;
    }

    private static PageTableGuestMemory? FindPageTable(HostProcess proc, List<HostRegion> regions, HostRegion backing, Action<string> L)
    {
        PageTableGuestMemory? best = null;
        int bestPages = 0;
        // Page table: one u64 per page. 36-bit AS => 128 MB, 39-bit => 1 GB.
        foreach (var g in regions.Where(r => r.Type == Win32.MEM_PRIVATE && r.AllocationBase != 0).GroupBy(r => r.AllocationBase))
        {
            ulong total = g.Aggregate(0UL, (a, r) => a + r.Size);
            if (total < 0x0800_0000 || total > 0x4020_0000) continue;
            var committed = g.Where(r => r.IsReadable).ToList();
            if (committed.Count == 0) continue;

            foreach (var enc in new[] { PageTableEncoding.HostPointerMinusVa, PageTableEncoding.PhysMinusVa })
            {
                int nonzero = 0, hits = 0;
                var buf = new byte[1 << 20];
                ulong scanned = 0;
                foreach (var r in committed)
                {
                    for (ulong q = r.Base; q < r.End && scanned < (256UL << 20); q += (ulong)buf.Length)
                    {
                        // Early out: a real page table has ~100% valid entries; bail once it is clearly not one.
                        if (nonzero >= 20000 && hits < nonzero * 0.5) break;
                        int n = (int)Math.Min((ulong)buf.Length, r.End - q);
                        if (!proc.Read(q, buf.AsSpan(0, n))) continue;
                        scanned += (ulong)n;
                        ulong idx0 = (q - g.Key) / 8;
                        for (int i = 0; i + 8 <= n; i += 8)
                        {
                            ulong e = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(i));
                            if (e == 0) continue;
                            nonzero++;
                            if (PageTableGuestMemory.Decode(enc, e, (idx0 + (ulong)i / 8) << 12, backing.Base, backing.Size, out _)) hits++;
                        }
                    }
                }
                if (nonzero == 0) continue;
                double ratio = (double)hits / nonzero;
                if (hits > 0)
                    L($"page table candidate alloc {g.Key:X} size {total:X} enc={enc}: {hits}/{nonzero} entries valid ({ratio:P1})");
                if (ratio >= 0.95 && hits >= 256 && hits > bestPages)
                {
                    bestPages = hits;
                    best = new PageTableGuestMemory(proc, g.Key, total, enc, backing.Base, backing.Size);
                }
            }
        }
        if (best != null) L($"Selected page table {best.TableHost:X} ({best.Encoding}), {bestPages} mapped pages ({bestPages * 4L / 1024} MB)");
        else L("No software page table found");
        return best;
    }

    private static FastmemGuestMemory? FindFastmem(HostProcess proc, List<HostRegion> regions, Action<string> L)
    {
        // Contiguous non-free runs spanning >= 64 GB.
        for (int i = 0; i < regions.Count; i++)
        {
            if (regions[i].State == Win32.MEM_FREE) continue;
            int j = i; ulong end = regions[i].End; int mapped = 0;
            while (j + 1 < regions.Count && regions[j + 1].State != Win32.MEM_FREE && regions[j + 1].Base == end)
            {
                j++; end = regions[j].End;
                if (regions[j].Type == Win32.MEM_MAPPED && regions[j].IsCommitted) mapped++;
            }
            ulong span = end - regions[i].Base;
            if (span >= 0x10_0000_0000UL)
            {
                // The arena's own reservation is the first region in the run that is >= 1 GB or MEM_MAPPED.
                int k = i;
                // Skip the DRAM backing view (adjacent to the arena in Yuzu-family builds) and small neighbours.
                while (k <= j && (regions[k].Size < 0x4000_0000 || (regions[k].Type == Win32.MEM_MAPPED && regions[k].Size <= 0x3_0000_0000))) k++;
                ulong arena = k <= j ? regions[k].Base : regions[i].Base;
                // Yuzu-family HostMemory aligns the virtual base up to the 2 MB huge-page size.
                arena = (arena + 0x1F_FFFFUL) & ~0x1F_FFFFUL;
                L($"Fastmem arena candidate host {arena:X}-{end:X} ({span >> 30} GB), {mapped} mapped views");
                if (mapped > 0)
                {
                    var fm = new FastmemGuestMemory(proc, arena, end - arena);
                    if (fm.Segments.Count > 0) return fm;
                }
                else L("  arena has no mapped views (fastmem disabled or game not booted)");
            }
            i = j;
        }
        return null;
    }

    /// <summary>Scans mapped guest pages for NSO module headers: u32 at page+4 = offset of "MOD0".</summary>
    private static List<GuestModule> FindModules(GuestMemory mem, Action<string> L)
    {
        var found = new List<(ulong Va, ulong Mod0)>();
        var buf = new byte[1 << 20];
        // Modules live in the code region, never in the big heap run: skip the largest run to keep detection fast.
        var largest = mem.MappedRuns().OrderByDescending(r => r.Size).FirstOrDefault();
        var segs = mem.Segments.Where(s => largest.Size < 0x2000_0000 || s.Va < largest.Va || s.Va >= largest.Va + largest.Size).ToList();
        L($"Module scan over {segs.Count} segments ({segs.Aggregate(0UL, (a, s) => a + s.Size) >> 20} MB), skipping heap run {largest.Va:X}");
        foreach (var s in segs)
        {
            for (ulong off = 0; off < s.Size; off += (ulong)buf.Length)
            {
                int n = (int)Math.Min((ulong)buf.Length, s.Size - off);
                if (!mem.Process.Read(s.Host + off, buf.AsSpan(0, n))) continue;
                for (int p = 0; p + 0x1000 <= n; p += 0x1000)
                {
                    // Module start: word0 is a branch or flags word; word1 = offset of the MOD0 header (can be tens of MB into the image).
                    uint mo = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(p + 4));
                    if (mo < 8 || mo > 0x2000_0000 || (mo & 3) != 0) continue;
                    ulong mod0 = s.Va + off + (ulong)p + mo;
                    bool isMod0 = p + mo + 4 <= n
                        ? buf.AsSpan(p + (int)mo, 4).SequenceEqual("MOD0"u8)
                        : mem.ReadU32(mod0) == 0x30444F4D; // "MOD0"
                    if (isMod0) found.Add((s.Va + off + (ulong)p, mod0));
                }
            }
        }
        var mods = new List<GuestModule>();
        int idx = 0;
        foreach (var (va, mod0) in found.OrderBy(f => f.Va))
        {
            // MOD0: magic, dynamic_off, bss_start_off, bss_end_off (all relative to MOD0)
            var hdr = mem.ReadBytes(mod0, 0x1C);
            ulong size = 0x1000;
            if (hdr != null)
            {
                int bssEnd = BinaryPrimitives.ReadInt32LittleEndian(hdr.AsSpan(12));
                if (bssEnd > 0) size = (mod0 - va) + (ulong)bssEnd;
                size = (size + 0xFFF) & ~0xFFFUL;
            }
            var m = new GuestModule(idx++, va, size, mod0, null);
            m.BuildId = ReadBuildId(mem, m);
            mods.Add(m);
        }
        NameModules(mods);
        foreach (var m in mods) L($"Module {m}");
        if (mods.Count == 0) L("No MOD0 module headers found in guest memory");
        return mods;
    }

    /// <summary>Find the GNU build-id ELF note (namesz=4, type=3, "GNU\0") in a module image.</summary>
    private static string? ReadBuildId(GuestMemory mem, GuestModule m)
    {
        ulong limit = Math.Min(m.Size, 0x1000_0000);
        var buf = new byte[1 << 20];
        ReadOnlySpan<byte> gnu = "GNU\0"u8;
        for (ulong off = 0; off < limit; off += (ulong)buf.Length - 64)
        {
            int n = (int)Math.Min((ulong)buf.Length, limit - off);
            if (!mem.Read(m.Va + off, buf.AsSpan(0, n))) continue;
            var span = buf.AsSpan(0, n);
            int start = 0;
            while (true)
            {
                int k = span.Slice(start).IndexOf(gnu);
                if (k < 0) break;
                int pos = start + k;
                start = pos + 1;
                if (pos < 12) continue;
                uint namesz = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(pos - 12));
                uint descsz = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(pos - 8));
                uint type = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(pos - 4));
                if (namesz != 4 || type != 3 || descsz < 8 || descsz > 0x20 || pos + 4 + descsz > n) continue;
                var id = span.Slice(pos + 4, (int)descsz).ToArray();
                var full = new byte[0x20];
                id.CopyTo(full, 0);
                return Convert.ToHexString(full);
            }
            if (n < buf.Length) break;
        }
        return null;
    }

    private static void NameModules(List<GuestModule> mods)
    {
        // Typical official title order: rtld, main, subsdk0..N, sdk. rtld is tiny.
        if (mods.Count == 0) return;
        int i = 0;
        var named = new List<GuestModule>();
        if (mods.Count >= 2 && mods[0].Size <= 0x10_0000) named.Add(mods[i++] with { Name = "rtld" });
        if (i < mods.Count) named.Add(mods[i++] with { Name = "main" });
        int sub = 0;
        for (; i < mods.Count; i++)
            named.Add(mods[i] with { Name = i == mods.Count - 1 && mods.Count >= 3 ? "sdk" : $"subsdk{sub++}" });
        for (int k = 0; k < mods.Count; k++) { var bid = mods[k].BuildId; mods[k] = named[k]; mods[k].BuildId = bid; }
    }

    private static void PickMain(GuestLayout layout, Action<string> L)
    {
        layout.Main = layout.Modules.FirstOrDefault(m => m.Name == "main");
        if (layout.Main != null) L($"Main module at VA {layout.Main.Va:X} (size {layout.Main.Size:X}), build id {layout.Main.BuildId ?? "NOT FOUND"}");
        else L("Main module not identified");
    }

    private static void PickHeap(GuestLayout layout, Action<string> L)
    {
        var runs = layout.Memory.MappedRuns()
            .Where(r => !layout.Modules.Any(m => m.Va < r.Va + r.Size && m.End > r.Va))
            .OrderByDescending(r => r.Size).ToList();
        foreach (var r in runs.Take(5)) L($"Mapped run VA {r.Va:X}-{r.Va + r.Size:X} ({r.Size >> 20} MB)");
        if (runs.Count > 0)
        {
            layout.HeapBase = runs[0].Va; layout.HeapSize = runs[0].Size;
            L($"Heap = largest non-module run: VA {layout.HeapBase:X} size {layout.HeapSize >> 20} MB");
        }
    }
}
