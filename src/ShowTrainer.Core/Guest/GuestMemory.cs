using System.Buffers.Binary;
using ShowTrainer.Core.Native;

namespace ShowTrainer.Core.Guest;

/// <summary>A run of guest virtual memory that is contiguous in both guest and host address space.</summary>
public readonly record struct GuestSegment(ulong Va, ulong Host, ulong Size)
{
    public ulong End => Va + Size;
    public override string ToString() => $"VA {Va:X10}-{End:X10} -> host {Host:X12} ({Size / 1024} KB)";
}

/// <summary>Guest (Switch) virtual address space view on top of the emulator's host memory.</summary>
public abstract class GuestMemory
{
    protected readonly HostProcess Host;
    private GuestSegment[] _segments = Array.Empty<GuestSegment>();
    private readonly object _lock = new();

    protected GuestMemory(HostProcess host) { Host = host; }

    public HostProcess Process => Host;
    public abstract string Mode { get; }
    public abstract string Describe();

    public IReadOnlyList<GuestSegment> Segments { get { lock (_lock) return _segments; } }

    /// <summary>Re-reads the mapping (the guest heap grows/shrinks while the game runs).</summary>
    public void Refresh()
    {
        var segs = BuildSegments();
        segs.Sort((a, b) => a.Va.CompareTo(b.Va));
        lock (_lock) _segments = segs.ToArray();
    }

    protected abstract List<GuestSegment> BuildSegments();

    public bool TryTranslate(ulong va, out ulong host, out ulong contiguous)
    {
        GuestSegment[] segs;
        lock (_lock) segs = _segments;
        int lo = 0, hi = segs.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            var s = segs[mid];
            if (va < s.Va) hi = mid - 1;
            else if (va >= s.End) lo = mid + 1;
            else { host = s.Host + (va - s.Va); contiguous = s.End - va; return true; }
        }
        host = 0; contiguous = 0;
        return false;
    }

    public bool TryTranslate(ulong va, out ulong host) => TryTranslate(va, out host, out _);

    public bool IsMapped(ulong va) => TryTranslate(va, out _, out _);

    public bool Read(ulong va, Span<byte> dst)
    {
        int done = 0;
        while (done < dst.Length)
        {
            if (!TryTranslate(va + (ulong)done, out var host, out var contig)) return false;
            int n = (int)Math.Min((ulong)(dst.Length - done), contig);
            if (!Host.Read(host, dst.Slice(done, n))) return false;
            done += n;
        }
        return true;
    }

    public bool Write(ulong va, ReadOnlySpan<byte> src)
    {
        int done = 0;
        while (done < src.Length)
        {
            if (!TryTranslate(va + (ulong)done, out var host, out var contig)) return false;
            int n = (int)Math.Min((ulong)(src.Length - done), contig);
            if (!Host.Write(host, src.Slice(done, n))) return false;
            done += n;
        }
        return true;
    }

    public byte[]? ReadBytes(ulong va, int len)
    {
        var b = new byte[len];
        return Read(va, b) ? b : null;
    }

    public ulong? ReadU64(ulong va)
    {
        Span<byte> b = stackalloc byte[8];
        return Read(va, b) ? BinaryPrimitives.ReadUInt64LittleEndian(b) : null;
    }

    public uint? ReadU32(ulong va)
    {
        Span<byte> b = stackalloc byte[4];
        return Read(va, b) ? BinaryPrimitives.ReadUInt32LittleEndian(b) : null;
    }

    /// <summary>Contiguous guest VA ranges (ignoring host contiguity), merged.</summary>
    public List<(ulong Va, ulong Size)> MappedRuns()
    {
        var runs = new List<(ulong Va, ulong Size)>();
        foreach (var s in Segments)
        {
            if (runs.Count > 0 && runs[^1].Va + runs[^1].Size == s.Va)
                runs[^1] = (runs[^1].Va, runs[^1].Size + s.Size);
            else runs.Add((s.Va, s.Size));
        }
        return runs;
    }
}

/// <summary>
/// Fastmem: the emulator reserves a huge host arena and maps guest pages at arena + VA.
/// Translation is a simple add.
/// </summary>
public sealed class FastmemGuestMemory : GuestMemory
{
    public ulong ArenaBase { get; }
    public ulong ArenaSize { get; }

    public FastmemGuestMemory(HostProcess host, ulong arenaBase, ulong arenaSize) : base(host)
    {
        ArenaBase = arenaBase; ArenaSize = arenaSize;
        Refresh();
    }

    public override string Mode => "fastmem";
    public override string Describe() => $"fastmem arena host {ArenaBase:X} size {ArenaSize:X} (host = arena + VA)";

    protected override List<GuestSegment> BuildSegments()
    {
        var list = new List<GuestSegment>();
        ulong a = ArenaBase, end = ArenaBase + ArenaSize;
        while (a < end)
        {
            var r = Host.Query(a);
            if (r is null || r.Value.Size == 0) break;
            var reg = r.Value;
            if (reg.Type == Native.Win32.MEM_MAPPED && reg.IsReadable)
            {
                ulong s = Math.Max(reg.Base, ArenaBase), e = Math.Min(reg.End, end);
                list.Add(new GuestSegment(s - ArenaBase, s, e - s));
            }
            a = reg.End;
        }
        return Coalesce(list);
    }

    internal static List<GuestSegment> Coalesce(List<GuestSegment> list)
    {
        var o = new List<GuestSegment>(list.Count);
        foreach (var s in list)
        {
            if (o.Count > 0 && o[^1].End == s.Va && o[^1].Host + o[^1].Size == s.Host)
                o[^1] = o[^1] with { Size = o[^1].Size + s.Size };
            else o.Add(s);
        }
        return o;
    }
}

public enum PageTableEncoding
{
    /// <summary>entry = (hostPtr - VA) | typeBits  (Yuzu Common::PageTable::pointers).</summary>
    HostPointerMinusVa,
    /// <summary>entry = physAddr - VA (Yuzu Common::PageTable::backing_addr); host = backing + phys - DRAM base.</summary>
    PhysMinusVa,
}

/// <summary>
/// Walks the emulator's software page table (one 8-byte entry per 4 KB guest page) to translate VA to host.
/// Works whether or not fastmem is enabled.
/// </summary>
public sealed class PageTableGuestMemory : GuestMemory
{
    public const ulong DramBase = 0x8000_0000;
    public ulong TableHost { get; }
    public ulong TableSize { get; }
    public PageTableEncoding Encoding { get; }
    public ulong BackingHost { get; }
    public ulong BackingSize { get; }

    public PageTableGuestMemory(HostProcess host, ulong tableHost, ulong tableSize, PageTableEncoding enc, ulong backingHost, ulong backingSize) : base(host)
    {
        TableHost = tableHost; TableSize = tableSize; Encoding = enc; BackingHost = backingHost; BackingSize = backingSize;
        Refresh();
    }

    public override string Mode => "pagetable";
    public override string Describe() =>
        $"page table host {TableHost:X} ({TableSize / 8} entries, {Encoding}), DRAM backing host {BackingHost:X} size {BackingSize:X}";

    /// <summary>Decodes one entry; returns false if the page is unmapped.</summary>
    public static bool Decode(PageTableEncoding enc, ulong entry, ulong va, ulong backingHost, ulong backingSize, out ulong host)
    {
        host = 0;
        if (entry == 0) return false;
        if (enc == PageTableEncoding.HostPointerMinusVa)
        {
            host = (entry & ~0xFFFUL) + va;
        }
        else
        {
            ulong phys = entry + va;
            if (phys < DramBase) return false;
            host = backingHost + (phys - DramBase);
        }
        return host >= backingHost && host + 0x1000 <= backingHost + backingSize;
    }

    protected override List<GuestSegment> BuildSegments()
    {
        var list = new List<GuestSegment>();
        ulong a = TableHost, end = TableHost + TableSize;
        var buf = new byte[1 << 20];
        while (a < end)
        {
            var r = Host.Query(a);
            if (r is null || r.Value.Size == 0) break;
            var reg = r.Value;
            ulong rs = Math.Max(reg.Base, TableHost), re = Math.Min(reg.End, end);
            if (reg.IsReadable)
            {
                for (ulong q = rs; q < re; q += (ulong)buf.Length)
                {
                    int n = (int)Math.Min((ulong)buf.Length, re - q);
                    var span = buf.AsSpan(0, n);
                    if (!Host.Read(q, span)) continue;
                    ulong firstIdx = (q - TableHost) / 8;
                    for (int i = 0; i + 8 <= n; i += 8)
                    {
                        ulong e = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(i));
                        if (e == 0) continue;
                        ulong va = (firstIdx + (ulong)(i / 8)) << 12;
                        if (!Decode(Encoding, e, va, BackingHost, BackingSize, out var host)) continue;
                        if (list.Count > 0 && list[^1].End == va && list[^1].Host + list[^1].Size == host)
                            list[^1] = list[^1] with { Size = list[^1].Size + 0x1000 };
                        else list.Add(new GuestSegment(va, host, 0x1000));
                    }
                }
            }
            a = reg.End;
        }
        return list;
    }
}
