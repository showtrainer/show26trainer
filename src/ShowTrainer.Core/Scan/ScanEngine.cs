using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ShowTrainer.Core.Guest;
using ShowTrainer.Core.Infra;

namespace ShowTrainer.Core.Scan;

public enum ScanScope { AllGuest, HeapOnly, MainModule }

public sealed record SnapshotSegment(ulong Va, ulong Size, long FileOffset);

/// <summary>Result set of a scan. Either an explicit address list, or (after an unknown-value first scan) a memory snapshot on disk.</summary>
public sealed class ScanResults
{
    public ValueKind Kind { get; init; }
    public int Alignment { get; init; }
    public ulong[] Addresses { get; init; } = Array.Empty<ulong>();
    /// <summary>Values at the time of this scan, SizeOf(Kind) bytes per address.</summary>
    public byte[] Values { get; init; } = Array.Empty<byte>();
    public string? SnapshotPath { get; init; }
    public List<SnapshotSegment>? SnapshotSegments { get; init; }
    public int ScanNumber { get; init; }
    public string Description { get; init; } = "";
    public bool Truncated { get; init; }

    public bool IsSnapshot => SnapshotPath != null;
    public long Count => IsSnapshot ? SnapshotSegments!.Sum(s => (long)s.Size / Alignment) : Addresses.LongLength;
    public int ValueSize => ValueCodec.SizeOf(Kind);
    public ReadOnlySpan<byte> ValueAt(int i) => Values.AsSpan(i * ValueSize, ValueSize);
}

public sealed record ScanRequest(ScanCompare Compare, string? Value1 = null, string? Value2 = null, double? FloatTolerance = null);

public sealed class ScanEngine
{
    public const int MaxResults = 30_000_000;
    private const int Chunk = 1 << 20;
    private readonly GuestLayout _layout;
    public string SnapshotDir { get; set; } = Path.Combine(Path.GetTempPath(), "ShowTrainer");

    public ScanEngine(GuestLayout layout) { _layout = layout; }
    private GuestMemory Mem => _layout.Memory;

    public List<GuestSegment> SegmentsFor(ScanScope scope)
    {
        Mem.Refresh();
        (ulong lo, ulong hi) = scope switch
        {
            ScanScope.HeapOnly when _layout.HeapSize != 0 => (_layout.HeapBase, _layout.HeapBase + _layout.HeapSize),
            ScanScope.MainModule when _layout.Main != null => (_layout.Main.Va, _layout.Main.End),
            _ => (0UL, ulong.MaxValue),
        };
        var list = new List<GuestSegment>();
        foreach (var s in Mem.Segments)
        {
            ulong a = Math.Max(s.Va, lo), b = Math.Min(s.End, hi);
            if (a < b) list.Add(new GuestSegment(a, s.Host + (a - s.Va), b - a));
        }
        return list;
    }

    // ---------------------------------------------------------------- first scan

    public Task<ScanResults> FirstScanAsync(ValueKind kind, ScanRequest req, int alignment, ScanScope scope, IProgress<double>? progress = null, CancellationToken ct = default)
        => Task.Run(() => FirstScan(kind, req, alignment, scope, progress, ct), ct);

    public ScanResults FirstScan(ValueKind kind, ScanRequest req, int alignment, ScanScope scope, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (alignment <= 0) alignment = ValueCodec.SizeOf(kind);
        var segs = SegmentsFor(scope);
        long totalBytes = segs.Sum(s => (long)s.Size);
        Log.Info($"First scan {kind} {req.Compare} {req.Value1} {req.Value2} align={alignment} scope={scope}: {segs.Count} segments, {totalBytes >> 20} MB");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        if (req.Compare == ScanCompare.Unknown)
            return Snapshot(kind, alignment, segs, totalBytes, progress, ct);
        if (req.Compare is not (ScanCompare.Exact or ScanCompare.Between))
            throw new ArgumentException("First scan supports Exact, Between, or Unknown initial value.");

        var p = Params.From(kind, req);
        // Split into chunk-sized work items.
        var items = new List<(GuestSegment Seg, ulong Off, int Len)>();
        foreach (var s in segs)
            for (ulong off = 0; off < s.Size; off += Chunk)
                items.Add((s, off, (int)Math.Min(Chunk, s.Size - off)));

        var partial = new (List<ulong> A, List<byte> V)[items.Count];
        long doneBytes = 0; int truncated = 0; long found = 0;
        Parallel.For(0, items.Count, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount }, () => new byte[Chunk], (i, _, buf) =>
        {
            var (seg, off, len) = items[i];
            var span = buf.AsSpan(0, len);
            var a = new List<ulong>(); var v = new List<byte>();
            if (Volatile.Read(ref found) < MaxResults && Mem.Process.Read(seg.Host + off, span))
                Matcher.FirstPass(kind, p, span, seg.Va + off, alignment, a, v);
            partial[i] = (a, v);
            if (Interlocked.Add(ref found, a.Count) > MaxResults) Interlocked.Exchange(ref truncated, 1);
            progress?.Report((double)Interlocked.Add(ref doneBytes, len) / Math.Max(1, totalBytes));
            return buf;
        }, _ => { });

        var res = Merge(kind, alignment, partial, 1, $"{req.Compare} {req.Value1}{(req.Value2 != null ? ".." + req.Value2 : "")}", truncated == 1);
        Log.Info($"First scan done: {res.Count:N0} results in {sw.ElapsedMilliseconds} ms{(res.Truncated ? " (TRUNCATED)" : "")}");
        return res;
    }

    private ScanResults Snapshot(ValueKind kind, int alignment, List<GuestSegment> segs, long totalBytes, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(SnapshotDir);
        var path = Path.Combine(SnapshotDir, $"snap_{DateTime.Now:yyyyMMdd_HHmmss_fff}.bin");
        var list = new List<SnapshotSegment>();
        var buf = new byte[Chunk];
        long pos = 0, done = 0;
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20))
        {
            foreach (var s in segs)
            {
                ct.ThrowIfCancellationRequested();
                list.Add(new SnapshotSegment(s.Va, s.Size, pos));
                for (ulong off = 0; off < s.Size; off += Chunk)
                {
                    int len = (int)Math.Min(Chunk, s.Size - off);
                    var span = buf.AsSpan(0, len);
                    if (!Mem.Process.Read(s.Host + off, span)) span.Clear();
                    fs.Write(span);
                    pos += len; done += len;
                    progress?.Report((double)done / Math.Max(1, totalBytes));
                }
            }
        }
        Log.Info($"Unknown-value snapshot: {done >> 20} MB written to {path}");
        return new ScanResults { Kind = kind, Alignment = alignment, SnapshotPath = path, SnapshotSegments = list, ScanNumber = 1, Description = "Unknown initial value" };
    }

    // ---------------------------------------------------------------- next scan

    public Task<ScanResults> NextScanAsync(ScanResults prev, ScanRequest req, IProgress<double>? progress = null, CancellationToken ct = default)
        => Task.Run(() => NextScan(prev, req, progress, ct), ct);

    public ScanResults NextScan(ScanResults prev, ScanRequest req, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (req.Compare == ScanCompare.Unknown) throw new ArgumentException("Unknown is only valid for the first scan.");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Mem.Refresh();
        var kind = prev.Kind;
        var p = Params.From(kind, req);
        ScanResults res = prev.IsSnapshot ? NextFromSnapshot(prev, req, p, progress, ct) : NextFromList(prev, req, p, progress, ct);
        Log.Info($"Scan #{res.ScanNumber} {req.Compare} {req.Value1}: {prev.Count:N0} -> {res.Count:N0} in {sw.ElapsedMilliseconds} ms");
        if (prev.IsSnapshot) TryDelete(prev.SnapshotPath);
        return res;
    }

    private ScanResults NextFromList(ScanResults prev, ScanRequest req, Params p, IProgress<double>? progress, CancellationToken ct)
    {
        var kind = prev.Kind; int vs = prev.ValueSize;
        const int Block = 1 << 16;
        int blocks = (prev.Addresses.Length + Block - 1) / Block;
        var partial = new (List<ulong> A, List<byte> V)[blocks];
        int done = 0;
        Parallel.For(0, blocks, new ParallelOptions { CancellationToken = ct }, () => new byte[0x10000 + 16], (bi, _, win) =>
        {
            var a = new List<ulong>(); var v = new List<byte>();
            int start = bi * Block, end = Math.Min(prev.Addresses.Length, start + Block);
            int i = start;
            while (i < end)
            {
                // Read a window covering as many consecutive addresses as fit.
                ulong w0 = prev.Addresses[i];
                int j = i;
                while (j + 1 < end && prev.Addresses[j + 1] + (ulong)vs - w0 <= 0x10000) j++;
                int wlen = (int)(prev.Addresses[j] + (ulong)vs - w0);
                var span = win.AsSpan(0, wlen);
                if (Mem.Read(w0, span))
                {
                    for (int k = i; k <= j; k++)
                    {
                        var cur = span.Slice((int)(prev.Addresses[k] - w0), vs);
                        if (Matcher.Match(kind, req.Compare, p, cur, prev.ValueAt(k)))
                        { a.Add(prev.Addresses[k]); foreach (var b in cur) v.Add(b); }
                    }
                }
                i = j + 1;
            }
            partial[bi] = (a, v);
            progress?.Report((double)Interlocked.Increment(ref done) / blocks);
            return win;
        }, _ => { });
        return Merge(kind, prev.Alignment, partial, prev.ScanNumber + 1, $"{req.Compare} {req.Value1}", false);
    }

    private ScanResults NextFromSnapshot(ScanResults prev, ScanRequest req, Params p, IProgress<double>? progress, CancellationToken ct)
    {
        var kind = prev.Kind; int align = prev.Alignment;
        var items = new List<(SnapshotSegment Seg, ulong Off, int Len)>();
        foreach (var s in prev.SnapshotSegments!)
            for (ulong off = 0; off < s.Size; off += Chunk)
                items.Add((s, off, (int)Math.Min(Chunk, s.Size - off)));
        var partial = new (List<ulong> A, List<byte> V)[items.Count];
        long found = 0; int truncated = 0, done = 0;
        using var fh = File.OpenHandle(prev.SnapshotPath!, FileMode.Open, FileAccess.Read, FileShare.Read);
        Parallel.For(0, items.Count, new ParallelOptions { CancellationToken = ct }, () => (new byte[Chunk], new byte[Chunk]), (i, _, bufs) =>
        {
            var (seg, off, len) = items[i];
            var a = new List<ulong>(); var v = new List<byte>();
            var cur = bufs.Item1.AsSpan(0, len); var old = bufs.Item2.AsSpan(0, len);
            if (Volatile.Read(ref found) < MaxResults && RandomAccess.Read(fh, old, seg.FileOffset + (long)off) == len && Mem.Read(seg.Va + off, cur))
                Matcher.SnapshotPass(kind, req.Compare, p, cur, old, seg.Va + off, align, a, v);
            partial[i] = (a, v);
            if (Interlocked.Add(ref found, a.Count) > MaxResults) Interlocked.Exchange(ref truncated, 1);
            progress?.Report((double)Interlocked.Increment(ref done) / items.Count);
            return bufs;
        }, _ => { });
        return Merge(kind, align, partial, prev.ScanNumber + 1, $"{req.Compare} {req.Value1}", truncated == 1);
    }

    private static ScanResults Merge(ValueKind kind, int align, (List<ulong> A, List<byte> V)[] partial, int scanNo, string desc, bool truncated)
    {
        long total = partial.Sum(x => (long)(x.A?.Count ?? 0));
        int n = (int)Math.Min(total, MaxResults);
        var addrs = new ulong[n];
        var vals = new byte[n * ValueCodec.SizeOf(kind)];
        int ai = 0, vi = 0;
        foreach (var (a, v) in partial)
        {
            if (a == null) continue;
            int take = Math.Min(a.Count, n - ai);
            if (take <= 0) { if (ai >= n) break; continue; }
            a.CopyTo(0, addrs, ai, take);
            v.CopyTo(0, vals, vi, take * ValueCodec.SizeOf(kind));
            ai += take; vi += take * ValueCodec.SizeOf(kind);
        }
        return new ScanResults { Kind = kind, Alignment = align, Addresses = addrs, Values = vals, ScanNumber = scanNo, Description = desc, Truncated = truncated || total > MaxResults };
    }

    private static void TryDelete(string? path) { try { if (path != null) File.Delete(path); } catch { } }

    // ---------------------------------------------------------------- comparison core

    internal readonly struct Params
    {
        public readonly long I1, I2; public readonly double D1, D2, Tol; public readonly bool Between;
        public Params(long i1, long i2, double d1, double d2, double tol, bool between) { I1 = i1; I2 = i2; D1 = d1; D2 = d2; Tol = tol; Between = between; }
        public static Params From(ValueKind kind, ScanRequest r)
        {
            double d1 = r.Value1 != null ? ValueCodec.ParseDouble(r.Value1) : 0, d2 = r.Value2 != null ? ValueCodec.ParseDouble(r.Value2) : 0;
            double tol = r.FloatTolerance ?? (r.Value1 != null && ValueCodec.IsFloat(kind) ? ValueCodec.DefaultTolerance(r.Value1) : 0);
            return new Params((long)d1, (long)d2, d1, d2, tol, r.Compare == ScanCompare.Between);
        }
    }

    internal static class Matcher
    {
        public static void FirstPass(ValueKind k, in Params p, ReadOnlySpan<byte> buf, ulong va, int align, List<ulong> a, List<byte> v)
        {
            switch (k)
            {
                case ValueKind.Byte: FirstInt<byte>(p, buf, va, align, a, v); break;
                case ValueKind.Int16: FirstInt<short>(p, buf, va, align, a, v); break;
                case ValueKind.Int32: FirstInt<int>(p, buf, va, align, a, v); break;
                case ValueKind.Int64: FirstInt<long>(p, buf, va, align, a, v); break;
                case ValueKind.Float: FirstFloat<float>(p, buf, va, align, a, v); break;
                case ValueKind.Double: FirstFloat<double>(p, buf, va, align, a, v); break;
            }
        }

        private static void FirstInt<T>(in Params p, ReadOnlySpan<byte> buf, ulong va, int align, List<ulong> a, List<byte> v) where T : unmanaged, IBinaryInteger<T>
        {
            int size = Unsafe.SizeOf<T>();
            T lo = T.CreateTruncating(p.I1), hi = T.CreateTruncating(p.I2);
            bool between = p.Between;
            if (!between && align == size && (buf.Length % size) == 0)
            {
                // Vectorized IndexOf on aligned data.
                var typed = MemoryMarshal.Cast<byte, T>(buf);
                int idx = 0;
                while (true)
                {
                    int k = typed.Slice(idx).IndexOf(lo);
                    if (k < 0) break;
                    idx += k;
                    Add(a, v, va + (ulong)(idx * size), buf.Slice(idx * size, size));
                    idx++;
                }
                return;
            }
            for (int i = 0; i + size <= buf.Length; i += align)
            {
                T x = Unsafe.ReadUnaligned<T>(ref MemoryMarshal.GetReference(buf.Slice(i)));
                if (between ? (x >= lo && x <= hi) : x == lo) Add(a, v, va + (ulong)i, buf.Slice(i, size));
            }
        }

        private static void FirstFloat<T>(in Params p, ReadOnlySpan<byte> buf, ulong va, int align, List<ulong> a, List<byte> v) where T : unmanaged, IFloatingPointIeee754<T>
        {
            int size = Unsafe.SizeOf<T>();
            bool between = p.Between;
            T lo = T.CreateTruncating(p.D1), hi = T.CreateTruncating(p.D2), tol = T.CreateTruncating(p.Tol);
            for (int i = 0; i + size <= buf.Length; i += align)
            {
                T x = Unsafe.ReadUnaligned<T>(ref MemoryMarshal.GetReference(buf.Slice(i)));
                bool ok = between ? (x >= lo && x <= hi) : T.Abs(x - lo) <= tol;
                if (ok && T.IsFinite(x)) Add(a, v, va + (ulong)i, buf.Slice(i, size));
            }
        }

        public static void SnapshotPass(ValueKind k, ScanCompare c, in Params p, ReadOnlySpan<byte> cur, ReadOnlySpan<byte> old, ulong va, int align, List<ulong> a, List<byte> v)
        {
            int size = ValueCodec.SizeOf(k);
            // Fast path for "changed"/"unchanged": skip identical 64-byte blocks.
            for (int i = 0; i + size <= cur.Length; i += align)
            {
                if (c == ScanCompare.Unchanged || c == ScanCompare.Changed)
                {
                    if ((i & 63) == 0 && i + 64 <= cur.Length && cur.Slice(i, 64).SequenceEqual(old.Slice(i, 64)))
                    {
                        if (c == ScanCompare.Changed) { i += 64 - align; continue; }
                    }
                }
                if (Match(k, c, p, cur.Slice(i, size), old.Slice(i, size))) Add(a, v, va + (ulong)i, cur.Slice(i, size));
            }
        }

        public static bool Match(ValueKind k, ScanCompare c, in Params p, ReadOnlySpan<byte> cur, ReadOnlySpan<byte> old) => k switch
        {
            ValueKind.Byte => CmpI(c, p, cur[0], old[0]),
            ValueKind.Int16 => CmpI(c, p, Rd<short>(cur), Rd<short>(old)),
            ValueKind.Int32 => CmpI(c, p, Rd<int>(cur), Rd<int>(old)),
            ValueKind.Int64 => CmpI(c, p, Rd<long>(cur), Rd<long>(old)),
            ValueKind.Float => CmpF(c, p, Rd<float>(cur), Rd<float>(old)),
            ValueKind.Double => CmpF(c, p, Rd<double>(cur), Rd<double>(old)),
            _ => false,
        };

        private static T Rd<T>(ReadOnlySpan<byte> b) where T : unmanaged => Unsafe.ReadUnaligned<T>(ref MemoryMarshal.GetReference(b));

        private static bool CmpI(ScanCompare c, in Params p, long cur, long old) => c switch
        {
            ScanCompare.Exact => cur == p.I1,
            ScanCompare.Changed => cur != old,
            ScanCompare.Unchanged => cur == old,
            ScanCompare.Increased => cur > old,
            ScanCompare.Decreased => cur < old,
            ScanCompare.IncreasedBy => cur - old == p.I1,
            ScanCompare.DecreasedBy => old - cur == p.I1,
            ScanCompare.Between => cur >= p.I1 && cur <= p.I2,
            _ => false,
        };

        private static bool CmpF(ScanCompare c, in Params p, double cur, double old)
        {
            if (!double.IsFinite(cur)) return false;
            return c switch
            {
                ScanCompare.Exact => Math.Abs(cur - p.D1) <= p.Tol,
                ScanCompare.Changed => cur != old,
                ScanCompare.Unchanged => cur == old,
                ScanCompare.Increased => cur > old,
                ScanCompare.Decreased => cur < old,
                ScanCompare.IncreasedBy => Math.Abs(cur - old - p.D1) <= Math.Max(p.Tol, 1e-6),
                ScanCompare.DecreasedBy => Math.Abs(old - cur - p.D1) <= Math.Max(p.Tol, 1e-6),
                ScanCompare.Between => cur >= p.D1 && cur <= p.D2,
                _ => false,
            };
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Add(List<ulong> a, List<byte> v, ulong addr, ReadOnlySpan<byte> val)
        {
            a.Add(addr);
            foreach (var b in val) v.Add(b);
        }
    }
}
