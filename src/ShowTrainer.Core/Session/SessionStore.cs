using System.Text.Json;
using System.Text.Json.Serialization;
using ShowTrainer.Core.Guest;
using ShowTrainer.Core.Infra;
using ShowTrainer.Core.Scan;

namespace ShowTrainer.Core.Session;

public sealed class ScanState
{
    public ValueKind Kind { get; set; }
    public int Alignment { get; set; }
    public int ScanNumber { get; set; }
    public string Description { get; set; } = "";
    public bool Truncated { get; set; }
    public long Count { get; set; }
    public string? AddressesB64 { get; set; }
    public string? ValuesB64 { get; set; }
    public string? SnapshotPath { get; set; }
    public List<SnapshotSegment>? SnapshotSegments { get; set; }

    /// <summary>Large result sets go to a binary sidecar file (JSON strings are limited to ~166 MB).</summary>
    public string? DataFile { get; set; }

    public static ScanState From(ScanResults r, string? sidecarPath = null)
    {
        var st = new ScanState
        {
            Kind = r.Kind, Alignment = r.Alignment, ScanNumber = r.ScanNumber, Description = r.Description, Truncated = r.Truncated, Count = r.Count,
            SnapshotPath = r.SnapshotPath, SnapshotSegments = r.SnapshotSegments,
        };
        if (r.IsSnapshot) return st;
        if (r.Addresses.Length <= 20_000 || sidecarPath == null)
        {
            st.AddressesB64 = Convert.ToBase64String(System.Runtime.InteropServices.MemoryMarshal.AsBytes(r.Addresses.AsSpan()));
            st.ValuesB64 = Convert.ToBase64String(r.Values);
            if (sidecarPath != null && File.Exists(sidecarPath)) File.Delete(sidecarPath); // stale from a bigger earlier scan
        }
        else
        {
            using var fs = new FileStream(sidecarPath, FileMode.Create, FileAccess.Write);
            using var bw = new BinaryWriter(fs);
            bw.Write((long)r.Addresses.Length);
            fs.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(r.Addresses.AsSpan()));
            fs.Write(r.Values);
            st.DataFile = Path.GetFileName(sidecarPath);
        }
        return st;
    }

    [System.Text.Json.Serialization.JsonIgnore] public string? BaseDir { get; set; }

    public ScanResults ToResults()
    {
        if (SnapshotPath != null)
            return new ScanResults { Kind = Kind, Alignment = Alignment, ScanNumber = ScanNumber, Description = Description, SnapshotPath = SnapshotPath, SnapshotSegments = SnapshotSegments };
        ulong[] addrs; byte[] vals;
        if (DataFile != null)
        {
            var path = Path.IsPathRooted(DataFile) ? DataFile : Path.Combine(BaseDir ?? "", DataFile);
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            long n = br.ReadInt64();
            addrs = new ulong[n];
            fs.ReadExactly(System.Runtime.InteropServices.MemoryMarshal.AsBytes(addrs.AsSpan()));
            vals = new byte[n * ValueCodec.SizeOf(Kind)];
            fs.ReadExactly(vals);
        }
        else
        {
            addrs = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(Convert.FromBase64String(AddressesB64 ?? "")).ToArray();
            vals = Convert.FromBase64String(ValuesB64 ?? "");
        }
        return new ScanResults { Kind = Kind, Alignment = Alignment, ScanNumber = ScanNumber, Description = Description, Truncated = Truncated, Addresses = addrs, Values = vals };
    }
}

/// <summary>Everything persisted between runs: the build the addresses belong to, saved entries, and the last scan.</summary>
public sealed class TrainerSession
{
    public int Version { get; set; } = 1;
    public DateTime SavedUtc { get; set; }
    public string? TitleId { get; set; }
    public string? BuildId { get; set; }
    public string? GameTitle { get; set; }
    public ulong MainVa { get; set; }
    public ulong HeapBase { get; set; }
    public List<AddressEntry> Entries { get; set; } = new();
    public ScanState? Scan { get; set; }

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public void Save(string path, GuestLayout? layout)
    {
        if (layout != null)
        {
            BuildId = layout.BuildId ?? BuildId; TitleId = layout.TitleId ?? TitleId; GameTitle = layout.GameTitle ?? GameTitle;
            MainVa = layout.Main?.Va ?? MainVa; HeapBase = layout.HeapBase;
        }
        SavedUtc = DateTime.UtcNow;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, true);
        Log.Info($"Session saved to {path} ({Entries.Count} entries, scan={(Scan == null ? "none" : Scan.Count.ToString("N0"))})");
    }

    /// <summary>Stores scan results; big sets are written to "&lt;session&gt;.scan.bin".</summary>
    public void SetScan(ScanResults? r, string sessionPath)
    {
        if (r == null) { Scan = null; return; }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(sessionPath))!);
        Scan = ScanState.From(r, Path.GetFullPath(sessionPath) + ".scan.bin");
        Scan.BaseDir = Path.GetDirectoryName(Path.GetFullPath(sessionPath));
    }

    public static TrainerSession Load(string path)
    {
        var s = JsonSerializer.Deserialize<TrainerSession>(File.ReadAllText(path), Json) ?? new TrainerSession();
        if (s.Scan != null) s.Scan.BaseDir = Path.GetDirectoryName(Path.GetFullPath(path));
        Log.Info($"Session loaded from {path}: build {s.BuildId ?? "?"}, {s.Entries.Count} entries");
        return s;
    }

    /// <summary>Returns a warning if the session was recorded for a different game build.</summary>
    public string? CheckBuild(GuestLayout layout)
    {
        if (BuildId != null && layout.BuildId != null && !string.Equals(BuildId, layout.BuildId, StringComparison.OrdinalIgnoreCase))
            return $"Session was recorded for build {BuildId[..16]} but the running game is {layout.BuildId[..16]}. Addresses will not match; re-scan or re-resolve.";
        return null;
    }
}
